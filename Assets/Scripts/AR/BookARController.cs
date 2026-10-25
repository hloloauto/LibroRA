using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace LibroRA.AR
{
    /// <summary>
    /// Controla el libro AR: relaciona cada imagen de la <c>XRReferenceImageLibrary</c> (Pagina_01..Pagina_06)
    /// con su prefab de personaje y su audio.
    ///
    /// - Basado en eventos: reacciona a <see cref="ARTrackedImageManager.trackablesChanged"/>, sin polling.
    /// - Cada prefab se instancia UNA sola vez (al arrancar, desactivado) y despues solo se activa/desactiva.
    /// - Solo se muestra contenido mientras la imagen esta en <see cref="TrackingState.Tracking"/>;
    ///   con Limited/None se oculta y se detiene el audio.
    /// - Un unico AudioSource: nunca suenan dos narraciones a la vez al hojear rapido.
    /// - Si el prefab tiene <see cref="SleepWakeController"/>, su secuencia arranca sola al activarse.
    ///
    /// Se cuelga en un GameObject vacio de la escena (p. ej. "BookController").
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class BookARController : MonoBehaviour
    {
        /// <summary>Contenido asociado a una pagina del libro.</summary>
        [Serializable]
        public sealed class PageContent
        {
            [Tooltip("Nombre EXACTO de la imagen en la XRReferenceImageLibrary (p. ej. Pagina_01).")]
            public string imageName = "Pagina_01";

            [Tooltip("Prefab del personaje/escena de esta pagina.")]
            public GameObject prefab;

            [Tooltip("Narracion o efecto de sonido de la pagina (opcional).")]
            public AudioClip narration;

            [Tooltip("Multiplicador de escala sobre la escala original del prefab.")]
            [Min(0.001f)] public float contentScale = 0.1f;

            [Tooltip("Giro en Y respecto a la pagina. 180 = el personaje mira hacia quien lee el libro.")]
            public float yawOffset = 180f;

            [Tooltip("Desplazamiento a la DERECHA del centro de la pagina, en centimetros (negativo = izquierda).")]
            public float offsetRightCm;

            [Tooltip("Desplazamiento hacia ARRIBA del centro de la pagina, en centimetros (negativo = hacia abajo). " +
                     "Sirve para colocar al personaje en una zona sin texto.")]
            public float offsetUpCm;
        }

        /// <summary>Estado en runtime de una pagina (todo cacheado).</summary>
        private sealed class PageRuntime
        {
            public PageContent Content;
            public GameObject Instance;
            public Transform InstanceTransform;
            public Vector3 BaseScale;
            public bool IsShown;
        }

        private const float CentimetersToMeters = 0.01f;

        [Header("Referencias")]
        [SerializeField] private ARTrackedImageManager trackedImageManager;

        [Header("Orientacion de la imagen")]
        [Tooltip("Marcado: el borde SUPERIOR de la pagina es el eje +Z de la imagen detectada. " +
                 "Si en la tablet el personaje se desplaza hacia el lado contrario al esperado, desmarcalo.")]
        [SerializeField] private bool imageTopIsPositiveZ = true;

        [Header("Paginas")]
        [SerializeField] private List<PageContent> pages = new List<PageContent>();

        /// <summary>Se dispara cuando el contenido de una pagina pasa a mostrarse (ya activo y colocado).</summary>
        public event Action<GameObject> PageShown;

        /// <summary>Se dispara cuando el contenido de una pagina se oculta.</summary>
        public event Action<GameObject> PageHidden;

        private readonly Dictionary<string, PageRuntime> pagesByImageName = new Dictionary<string, PageRuntime>();
        private AudioSource audioSource;
        private PageRuntime currentAudioPage;

        private void Awake()
        {
            audioSource = GetComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = false;

            if (trackedImageManager == null)
            {
                Debug.LogError($"[{nameof(BookARController)}] Falta asignar 'trackedImageManager'.", this);
                enabled = false;
                return;
            }

            BuildPages();
        }

        private void OnEnable()
        {
            if (trackedImageManager != null)
            {
                trackedImageManager.trackablesChanged.AddListener(OnTrackablesChanged);
            }
        }

        private void OnDisable()
        {
            if (trackedImageManager != null)
            {
                trackedImageManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
            }

            HideAll();
        }

        /// <summary>Instancia cada prefab una sola vez y lo deja oculto.</summary>
        private void BuildPages()
        {
            for (int i = 0; i < pages.Count; i++)
            {
                PageContent content = pages[i];

                if (content == null || content.prefab == null || string.IsNullOrEmpty(content.imageName))
                {
                    Debug.LogWarning($"[{nameof(BookARController)}] La pagina #{i} esta incompleta y se ignora.", this);
                    continue;
                }

                if (pagesByImageName.ContainsKey(content.imageName))
                {
                    Debug.LogWarning($"[{nameof(BookARController)}] Imagen duplicada '{content.imageName}'; se ignora la #{i}.", this);
                    continue;
                }

                GameObject instance = Instantiate(content.prefab, transform);
                Transform instanceTransform = instance.transform;

                PageRuntime runtime = new PageRuntime
                {
                    Content = content,
                    Instance = instance,
                    InstanceTransform = instanceTransform,
                    BaseScale = instanceTransform.localScale
                };

                instance.SetActive(false);
                pagesByImageName.Add(content.imageName, runtime);
            }
        }

        private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> args)
        {
            for (int i = 0; i < args.added.Count; i++)
            {
                RefreshPage(args.added[i]);
            }

            for (int i = 0; i < args.updated.Count; i++)
            {
                RefreshPage(args.updated[i]);
            }

            for (int i = 0; i < args.removed.Count; i++)
            {
                ARTrackedImage removedImage = args.removed[i].Value;
                if (removedImage != null && pagesByImageName.TryGetValue(removedImage.referenceImage.name, out PageRuntime page))
                {
                    Hide(page);
                }
            }
        }

        /// <summary>Muestra u oculta la pagina segun el estado de tracking de su imagen.</summary>
        private void RefreshPage(ARTrackedImage trackedImage)
        {
            if (!pagesByImageName.TryGetValue(trackedImage.referenceImage.name, out PageRuntime page))
            {
                return;
            }

            bool shouldShow = trackedImage.trackingState == TrackingState.Tracking;

            if (shouldShow && !page.IsShown)
            {
                Show(page, trackedImage.transform);
            }
            else if (!shouldShow && page.IsShown)
            {
                Hide(page);
            }
        }

        private void Show(PageRuntime page, Transform imageTransform)
        {
            // Si el eje Z apunta hacia abajo en la pagina, se invierten el desplazamiento vertical y el giro.
            float upSign = imageTopIsPositiveZ ? 1f : -1f;
            float yaw = page.Content.yawOffset + (imageTopIsPositiveZ ? 0f : 180f);

            Transform t = page.InstanceTransform;
            t.SetParent(imageTransform, false);
            t.localPosition = new Vector3(
                page.Content.offsetRightCm * CentimetersToMeters,
                0f,
                page.Content.offsetUpCm * CentimetersToMeters * upSign);
            t.localRotation = Quaternion.Euler(0f, yaw, 0f);
            t.localScale = page.BaseScale * page.Content.contentScale;

            page.Instance.SetActive(true);
            page.IsShown = true;

            PlayNarration(page);
            PageShown?.Invoke(page.Instance);
        }

        private void Hide(PageRuntime page)
        {
            if (!page.IsShown)
            {
                return;
            }

            page.IsShown = false;
            page.Instance.SetActive(false);

            // Se devuelve al controlador para que no dependa de un ARTrackedImage que puede destruirse.
            page.InstanceTransform.SetParent(transform, false);

            if (currentAudioPage == page)
            {
                audioSource.Stop();
                currentAudioPage = null;
            }

            PageHidden?.Invoke(page.Instance);
        }

        /// <summary>Detiene la narracion de la pagina (p. ej. cuando el personaje va a hablar).</summary>
        public void StopNarration()
        {
            audioSource.Stop();
            currentAudioPage = null;
        }

        private void HideAll()
        {
            foreach (KeyValuePair<string, PageRuntime> entry in pagesByImageName)
            {
                Hide(entry.Value);
            }
        }

        /// <summary>Reproduce la narracion de la pagina deteniendo cualquier otra que estuviera sonando.</summary>
        private void PlayNarration(PageRuntime page)
        {
            audioSource.Stop();
            currentAudioPage = null;

            if (page.Content.narration == null)
            {
                return;
            }

            audioSource.clip = page.Content.narration;
            audioSource.Play();
            currentAudioPage = page;
        }
    }
}
