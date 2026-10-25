using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using Finger = UnityEngine.InputSystem.EnhancedTouch.Finger;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace LibroRA.AR
{
    /// <summary>
    /// Reglas de interaccion del personaje: SI (accion del usuario + estado del personaje)
    /// ENTONCES (reaccion) FEEDBACK (texto, voz y movimiento).
    ///
    ///   SI toca al personaje     Y puede interactuar -> ENTONCES se sacude -> FEEDBACK frase de "toque" (+ audio)
    ///   SI pulsa "Hablar"        Y puede interactuar -> ENTONCES responde  -> FEEDBACK frase de "hablar" (+ audio)
    ///   SI pulsa "Hablar"        Y esta dormido      -> ENTONCES ronca     -> FEEDBACK "Zzz..."
    ///   SI termina de despertar                      -> ENTONCES saluda    -> FEEDBACK frase al despertar
    ///
    /// - Funciona con o sin <see cref="SleepWakeController"/>: sin el, el personaje siempre puede interactuar.
    /// - Tambien lo usa <see cref="StoryActor"/> para decir las frases del cuento (<see cref="Say"/>).
    /// - Basado en eventos: toque via <see cref="Touch.onFingerDown"/> (nuevo Input System), sin Update().
    /// - No muestra UI directamente: emite <see cref="LineRequested"/> y la escucha <c>BookUIController</c>.
    /// - Va en la raiz de cada personaje.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CharacterInteraction : MonoBehaviour
    {
        /// <summary>Una frase del personaje: texto en pantalla, voz opcional y tiempo visible.</summary>
        [Serializable]
        public sealed class Line
        {
            [TextArea(1, 3)] public string text;
            public AudioClip clip;
            [Min(0.5f)] public float seconds = 3f;
        }

        private const float MaxRayDistance = 10f;

        [Header("Identidad")]
        [Tooltip("Nombre que aparece antes de la frase en el globo de dialogo. Vacio = sin nombre.")]
        [SerializeField] private string speakerName = "";

        [Header("Frases (SI -> ENTONCES -> FEEDBACK)")]
        [SerializeField] private Line touchedAwakeLine = new Line { text = "¡No me tocas! Comienza a leer." };
        [SerializeField] private Line talkAwakeLine = new Line { text = "Tengo mucho sueño... quiero seguir durmiendo." };
        [SerializeField] private Line talkAsleepLine = new Line { text = "Zzz... zzz... zzz...", seconds = 2.5f };
        [Tooltip("Frase al terminar de despertar. Dejar el texto vacio para que no diga nada.")]
        [SerializeField] private Line wokeUpLine = new Line { text = "¡Ahh... qué sueño!", seconds = 2.5f };

        [Header("Despues de hablarle (solo con SleepWakeController)")]
        [Tooltip("Tras responder, el personaje vuelve a acostarse.")]
        [SerializeField] private bool returnToSleepAfterTalk;
        [SerializeField, Min(0f)] private float returnToSleepDelay = 3f;
        [Tooltip("Segundos dormido hasta despertar otra vez. 0 = se queda dormido.")]
        [SerializeField, Min(0f)] private float wakeAgainDelay = 6f;

        [Header("Sacudida al tocarlo")]
        [SerializeField, Min(0.1f)] private float shakeSeconds = 0.6f;
        [SerializeField, Min(0f)] private float shakeDegrees = 14f;
        [SerializeField, Min(1f)] private float shakeSpeed = 40f;

        [Header("Collider de toque (se crea solo si el prefab no tiene ninguno)")]
        [SerializeField, Min(0.1f)] private float colliderHeight = 1.8f;
        [SerializeField, Min(0.05f)] private float colliderRadius = 0.35f;

        private SleepWakeController sleeper;
        private Collider touchCollider;
        private Camera arCamera;
        private Coroutine shakeRoutine;
        private Coroutine sleepCycleRoutine;
        private Quaternion shakeBaseRotation;

        /// <summary>
        /// Se dispara cuando el personaje quiere decir una frase. El primer argumento es quien habla
        /// (permite mostrar su nombre); el segundo, la frase.
        /// </summary>
        public event Action<CharacterInteraction, Line> LineRequested;

        /// <summary>Nombre del personaje para el globo de dialogo.</summary>
        public string SpeakerName => speakerName;

        /// <summary>Un personaje sin SleepWakeController siempre puede interactuar.</summary>
        private bool CanInteract => sleeper == null || sleeper.IsAwake;

        private void Awake()
        {
            sleeper = GetComponent<SleepWakeController>();

            // Se evita modificar mallas/prefabs originales: el collider solo se agrega si falta.
            touchCollider = GetComponentInChildren<Collider>(true);
            if (touchCollider == null)
            {
                CapsuleCollider capsule = gameObject.AddComponent<CapsuleCollider>();
                capsule.height = colliderHeight;
                capsule.radius = colliderRadius;
                capsule.center = new Vector3(0f, colliderHeight * 0.5f, 0f);
                touchCollider = capsule;
            }
        }

        private void OnEnable()
        {
            if (arCamera == null)
            {
                arCamera = Camera.main;
            }

            EnhancedTouchSupport.Enable();
            Touch.onFingerDown += OnFingerDown;

            if (sleeper != null)
            {
                sleeper.Awakened += OnAwakened;
                sleeper.FellAsleep += OnFellAsleep;
            }

            // Solo se puede tocar al personaje cuando esta despierto.
            touchCollider.enabled = CanInteract;
        }

        private void OnDisable()
        {
            Touch.onFingerDown -= OnFingerDown;

            if (sleeper != null)
            {
                sleeper.Awakened -= OnAwakened;
                sleeper.FellAsleep -= OnFellAsleep;
            }

            StopShake();
            sleepCycleRoutine = null;
        }

        /// <summary>Asigna nombre y frases de interaccion por codigo (lo usa la herramienta de editor).</summary>
        public void Configure(string speaker, Line touched, Line talk)
        {
            speakerName = speaker;
            touchedAwakeLine = touched;
            talkAwakeLine = talk;
        }

        // ------------------------------------------------------------------ SI (entradas)

        /// <summary>SI: pulsan el boton "Hablar". Lo invoca <c>BookUIController</c>.</summary>
        public void Talk()
        {
            if (!CanInteract)
            {
                Say(talkAsleepLine);
                return;
            }

            Say(talkAwakeLine);

            if (returnToSleepAfterTalk && sleeper != null)
            {
                RestartSleepCycle();
            }
        }

        private void OnFingerDown(Finger finger)
        {
            if (finger.index != 0 || !CanInteract || arCamera == null)
            {
                return;
            }

            Ray ray = arCamera.ScreenPointToRay(finger.screenPosition);
            if (Physics.Raycast(ray, out RaycastHit hit, MaxRayDistance, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore) && hit.transform.IsChildOf(transform))
            {
                OnTouched();
            }
        }

        // ------------------------------------------------------------------ ENTONCES + FEEDBACK

        private void OnTouched()
        {
            Say(touchedAwakeLine);
            StartShake();
        }

        private void OnAwakened()
        {
            touchCollider.enabled = true;

            if (!string.IsNullOrEmpty(wokeUpLine.text))
            {
                Say(wokeUpLine);
            }
        }

        private void OnFellAsleep()
        {
            touchCollider.enabled = false;
        }

        /// <summary>El personaje dice una frase (texto + voz opcional) a traves de la interfaz.</summary>
        public void Say(Line line)
        {
            LineRequested?.Invoke(this, line);
        }

        // ------------------------------------------------------------------ Ciclo "vuelve a dormir"

        private void RestartSleepCycle()
        {
            if (sleepCycleRoutine != null)
            {
                StopCoroutine(sleepCycleRoutine);
            }

            sleepCycleRoutine = StartCoroutine(SleepCycle());
        }

        private IEnumerator SleepCycle()
        {
            yield return new WaitForSeconds(returnToSleepDelay);
            sleeper.GoBackToSleep();

            if (wakeAgainDelay > 0f)
            {
                yield return new WaitForSeconds(wakeAgainDelay);
                sleeper.WakeUp();
            }

            sleepCycleRoutine = null;
        }

        // ------------------------------------------------------------------ Sacudida

        private void StartShake()
        {
            StopShake();
            shakeBaseRotation = transform.localRotation;
            shakeRoutine = StartCoroutine(Shake());
        }

        private void StopShake()
        {
            if (shakeRoutine == null)
            {
                return;
            }

            StopCoroutine(shakeRoutine);
            shakeRoutine = null;
            transform.localRotation = shakeBaseRotation;
        }

        private IEnumerator Shake()
        {
            float elapsed = 0f;
            while (elapsed < shakeSeconds)
            {
                float damping = 1f - elapsed / shakeSeconds;
                float angle = Mathf.Sin(elapsed * shakeSpeed) * shakeDegrees * damping;
                transform.localRotation = shakeBaseRotation * Quaternion.Euler(0f, angle, 0f);
                elapsed += Time.deltaTime;
                yield return null;
            }

            transform.localRotation = shakeBaseRotation;
            shakeRoutine = null;
        }
    }
}
