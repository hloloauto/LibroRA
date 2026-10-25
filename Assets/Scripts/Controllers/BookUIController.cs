using System.Collections;
using System.Collections.Generic;
using LibroRA.AR;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LibroRA.Controllers
{
    /// <summary>
    /// Interfaz en pantalla del libro: boton "Hablar" y globo de dialogo.
    ///
    /// - Escucha <see cref="BookARController.PageShown"/>/<see cref="BookARController.PageHidden"/>
    ///   para saber que personajes tiene la pagina activa (sin Find ni polling).
    /// - Una pagina puede tener varios personajes; el globo muestra el nombre de quien habla.
    /// - El boton "Hablar" habla con el primer personaje de la pagina (el protagonista).
    /// - El boton y el globo solo se ven mientras hay una pagina en tracking.
    /// - La voz usa un unico AudioSource y detiene la narracion para que no se superpongan.
    ///
    /// Se cuelga en el Canvas (no en el boton ni en el globo, que se activan y desactivan).
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class BookUIController : MonoBehaviour
    {
        [Header("Referencias de escena")]
        [SerializeField] private BookARController bookController;
        [SerializeField] private Button talkButton;
        [SerializeField] private GameObject dialogueBubble;
        [SerializeField] private TMP_Text dialogueLabel;

        private readonly Dictionary<GameObject, CharacterInteraction[]> actorsByPage =
            new Dictionary<GameObject, CharacterInteraction[]>();

        private AudioSource voiceSource;
        private GameObject currentPage;
        private CharacterInteraction[] currentActors;
        private Coroutine hideBubbleRoutine;

        private void Awake()
        {
            voiceSource = GetComponent<AudioSource>();
            voiceSource.playOnAwake = false;
            voiceSource.loop = false;

            if (bookController == null || talkButton == null || dialogueBubble == null || dialogueLabel == null)
            {
                Debug.LogError($"[{nameof(BookUIController)}] Faltan referencias en el Inspector.", this);
                enabled = false;
                return;
            }

            talkButton.onClick.AddListener(OnTalkPressed);
            SetUiVisible(false);
        }

        private void OnEnable()
        {
            if (bookController == null)
            {
                return;
            }

            bookController.PageShown += OnPageShown;
            bookController.PageHidden += OnPageHidden;
        }

        private void OnDisable()
        {
            if (bookController != null)
            {
                bookController.PageShown -= OnPageShown;
                bookController.PageHidden -= OnPageHidden;
            }

            Detach();
        }

        private void OnDestroy()
        {
            if (talkButton != null)
            {
                talkButton.onClick.RemoveListener(OnTalkPressed);
            }
        }

        // ------------------------------------------------------------------ Eventos del libro

        private void OnPageShown(GameObject page)
        {
            if (!actorsByPage.TryGetValue(page, out CharacterInteraction[] actors))
            {
                // Se cachean los componentes: la busqueda solo ocurre la primera vez por pagina.
                actors = page.GetComponentsInChildren<CharacterInteraction>(true);
                actorsByPage.Add(page, actors);
            }

            if (actors.Length == 0)
            {
                return;
            }

            Detach();
            currentPage = page;
            currentActors = actors;

            for (int i = 0; i < currentActors.Length; i++)
            {
                currentActors[i].LineRequested += OnLineRequested;
            }

            talkButton.gameObject.SetActive(true);
        }

        private void OnPageHidden(GameObject page)
        {
            if (currentPage == page)
            {
                Detach();
            }
        }

        /// <summary>Deja de escuchar a los personajes actuales y oculta la interfaz.</summary>
        private void Detach()
        {
            if (currentActors != null)
            {
                for (int i = 0; i < currentActors.Length; i++)
                {
                    if (currentActors[i] != null)
                    {
                        currentActors[i].LineRequested -= OnLineRequested;
                    }
                }
            }

            currentActors = null;
            currentPage = null;

            if (voiceSource != null)
            {
                voiceSource.Stop();
            }

            if (hideBubbleRoutine != null)
            {
                StopCoroutine(hideBubbleRoutine);
                hideBubbleRoutine = null;
            }

            if (talkButton != null)
            {
                SetUiVisible(false);
            }
        }

        // ------------------------------------------------------------------ SI: boton "Hablar"

        private void OnTalkPressed()
        {
            if (currentActors != null && currentActors.Length > 0)
            {
                currentActors[0].Talk();
            }
        }

        // ------------------------------------------------------------------ FEEDBACK: globo + voz

        private void OnLineRequested(CharacterInteraction speaker, CharacterInteraction.Line line)
        {
            // Una sola voz a la vez: se corta la narracion de la pagina y cualquier frase anterior.
            bookController.StopNarration();
            voiceSource.Stop();

            if (line.clip != null)
            {
                voiceSource.clip = line.clip;
                voiceSource.Play();
            }

            dialogueLabel.text = string.IsNullOrEmpty(speaker.SpeakerName)
                ? line.text
                : speaker.SpeakerName + ": " + line.text;
            dialogueBubble.SetActive(true);

            if (hideBubbleRoutine != null)
            {
                StopCoroutine(hideBubbleRoutine);
            }

            hideBubbleRoutine = StartCoroutine(HideBubbleAfter(line.seconds));
        }

        private IEnumerator HideBubbleAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            dialogueBubble.SetActive(false);
            hideBubbleRoutine = null;
        }

        private void SetUiVisible(bool visible)
        {
            talkButton.gameObject.SetActive(visible);
            dialogueBubble.SetActive(visible);
        }
    }
}
