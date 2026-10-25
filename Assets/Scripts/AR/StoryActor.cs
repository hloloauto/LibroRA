using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace LibroRA.AR
{
    /// <summary>Posturas de los brazos disponibles para los personajes del cuento.</summary>
    public enum StoryPose
    {
        Relaxed,
        ArmsUp,
        ArmsOut,
        HandsOnFace,
        PointForward,
        HandsToMouth
    }

    /// <summary>Movimientos del cuerpo que se pueden combinar con una postura.</summary>
    public enum StoryMotion
    {
        None,
        Hop,
        Run,
        Trip,
        ShakeHead,
        Chew
    }

    /// <summary>
    /// Actor del cuento: ejecuta una lista de "beats" (postura + movimiento + frase) cuando se activa.
    /// Anima por codigo el rig Humanoid de POLYGON (no necesita clips de animacion).
    ///
    /// - Cada beat dura N segundos: mueve los brazos a una postura, aplica un movimiento del cuerpo
    ///   y, si tiene texto, el personaje lo "dice" a traves de <see cref="CharacterInteraction"/>.
    /// - Las direcciones de los brazos se calculan en espacio mundo desde la posicion de los huesos,
    ///   asi que no dependen de los ejes locales de cada hueso.
    /// - Todo se cachea en Awake; la animacion usa corrutinas (sin Update ni Find).
    /// - Se activa/desactiva junto con el prefab de la pagina: al reactivarse, la secuencia reinicia.
    ///
    /// Va en la raiz de cada personaje dentro del prefab de pagina.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StoryActor : MonoBehaviour
    {
        /// <summary>Un paso de la secuencia del personaje.</summary>
        [Serializable]
        public sealed class Beat
        {
            public StoryPose pose = StoryPose.Relaxed;
            public StoryMotion motion = StoryMotion.None;
            [Min(0.2f)] public float seconds = 2.5f;

            [Tooltip("Frase que dice al empezar este paso. Dejar el texto vacio para que no hable.")]
            public CharacterInteraction.Line line = new CharacterInteraction.Line();
        }

        /// <summary>Estado del cuerpo en un instante (todo en unidades locales del personaje o grados).</summary>
        private struct BodyState
        {
            public float Height;   // elevacion de la cadera
            public float Pitch;    // inclinacion hacia adelante
            public float Legs;     // balanceo de piernas
            public float Arms;     // balanceo de brazos
            public float Nod;      // asentir con la cabeza
            public float Shake;    // negar con la cabeza
        }

        private struct ArmPose
        {
            public Quaternion ShoulderL, ElbowL, ShoulderR, ElbowR;
        }

        [Header("Secuencia")]
        [SerializeField] private List<Beat> beats = new List<Beat>();
        [Tooltip("Espera antes de empezar (sirve para que los personajes de una pagina hablen por turnos).")]
        [SerializeField, Min(0f)] private float startDelay;
        [Tooltip("Repetir la secuencia en bucle.")]
        [SerializeField] private bool repeat;
        [SerializeField, Min(0f)] private float repeatDelay = 2f;

        [Header("Movimiento")]
        [SerializeField, Min(0.05f)] private float poseBlendSeconds = 0.35f;
        [SerializeField, Min(0f)] private float hopHeight = 0.22f;
        [SerializeField, Min(0.5f)] private float runStepsPerSecond = 3f;
        [SerializeField, Min(0f)] private float breathAmplitude = 0.008f;
        [SerializeField, Min(0.1f)] private float breathsPerSecond = 0.35f;

        [Header("Sentado")]
        [Tooltip("El personaje hace toda la secuencia sentado (piernas dobladas, cadera baja).")]
        [SerializeField] private bool seated;
        [Tooltip("Altura de la cadera al estar sentado, en unidades locales (la del asiento + ~0.15).")]
        [SerializeField, Min(0.2f)] private float seatedHipsHeight = 0.62f;

        [Header("Referencia (opcional)")]
        [Tooltip("Raiz del personaje (define arriba/adelante/derecha). Si esta vacio se usa este GameObject.")]
        [SerializeField] private Transform characterRoot;

        private Transform hips, spine, head;
        private Transform shoulderL, elbowL, handL, shoulderR, elbowR, handR;
        private Transform upperLegL, upperLegR, lowerLegL, lowerLegR;

        private Quaternion bindShoulderL, bindElbowL, bindShoulderR, bindElbowR;
        private Quaternion bindUpperLegL, bindUpperLegR, bindLowerLegL, bindLowerLegR, bindHead;
        private Quaternion hipsBindRelativeRotation;
        private Vector3 hipsBindLocalPosition;
        private Vector3 spineBindScale;

        private ArmPose[] poses;
        private ArmPose currentArms;
        private BodyState lastBody;

        private CharacterInteraction interaction;
        private bool initialized;

        /// <summary>Lista editable de pasos (la usa la herramienta de editor que crea las paginas).</summary>
        public List<Beat> Beats => beats;

        /// <summary>Si es true, el personaje hace la secuencia sentado.</summary>
        public bool Seated
        {
            get => seated;
            set => seated = value;
        }

        /// <summary>Segundos de espera inicial.</summary>
        public float StartDelay
        {
            get => startDelay;
            set => startDelay = Mathf.Max(0f, value);
        }

        private void Awake()
        {
            if (characterRoot == null)
            {
                characterRoot = transform;
            }

            Animator animator = GetComponentInChildren<Animator>(true);
            if (animator != null)
            {
                animator.enabled = false;
            }

            interaction = GetComponent<CharacterInteraction>();

            initialized = CacheBones();
            if (!initialized)
            {
                enabled = false;
                return;
            }

            BuildPoses();
            currentArms = poses[(int)StoryPose.Relaxed];
        }

        private void OnEnable()
        {
            if (initialized)
            {
                StartCoroutine(Play());
            }
        }

        private void OnDisable()
        {
            StopAllCoroutines();
        }

        // ------------------------------------------------------------------ Secuencia

        private IEnumerator Play()
        {
            // Siempre se arranca de pie y relajado.
            currentArms = poses[(int)StoryPose.Relaxed];
            lastBody = default;
            ApplyFrame(in currentArms, in currentArms, 1f, default);

            // Un frame de espera: BookARController avisa a la interfaz DESPUES de activar la pagina,
            // y asi la primera frase no se dice antes de que alguien la escuche.
            yield return null;

            if (startDelay > 0f)
            {
                yield return Idle(startDelay);
            }

            do
            {
                for (int i = 0; i < beats.Count; i++)
                {
                    yield return RunBeat(beats[i]);
                }

                if (repeat)
                {
                    yield return Idle(repeatDelay);
                }
            }
            while (repeat);

            // Termina la secuencia: se queda vivo, respirando.
            while (true)
            {
                ApplyFrame(in currentArms, in currentArms, 1f, default);
                yield return null;
            }
        }

        private IEnumerator Idle(float seconds)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                ApplyFrame(in currentArms, in currentArms, 1f, default);
                elapsed += Time.deltaTime;
                yield return null;
            }
        }

        private IEnumerator RunBeat(Beat beat)
        {
            ArmPose from = currentArms;
            ArmPose to = poses[(int)beat.pose];

            if (interaction != null && beat.line != null && !string.IsNullOrEmpty(beat.line.text))
            {
                interaction.Say(beat.line);
            }

            float elapsed = 0f;
            while (elapsed < beat.seconds)
            {
                float blend = Smooth(elapsed / poseBlendSeconds);
                BodyState state = Evaluate(beat.motion, elapsed / beat.seconds, Time.time);
                lastBody = state;
                ApplyFrame(in from, in to, blend, state);
                elapsed += Time.deltaTime;
                yield return null;
            }

            currentArms = to;

            // Pequeno "asentamiento" para que el cuerpo vuelva a neutro sin saltos.
            const float settleSeconds = 0.2f;
            BodyState start = lastBody;
            elapsed = 0f;
            while (elapsed < settleSeconds)
            {
                float k = Smooth(elapsed / settleSeconds);
                ApplyFrame(in currentArms, in currentArms, 1f, Lerp(start, default, k));
                elapsed += Time.deltaTime;
                yield return null;
            }

            lastBody = default;
        }

        // ------------------------------------------------------------------ Movimientos

        private BodyState Evaluate(StoryMotion motion, float u, float time)
        {
            BodyState s = default;

            switch (motion)
            {
                case StoryMotion.Hop:
                    s.Height = Mathf.Abs(Mathf.Sin(Mathf.PI * 2f * u)) * hopHeight;
                    break;

                case StoryMotion.Run:
                    RunCycle(ref s, time, 14f);
                    break;

                case StoryMotion.Trip:
                    if (u < 0.45f)
                    {
                        RunCycle(ref s, time, 14f);
                    }
                    else if (u < 0.75f)
                    {
                        // Tropieza: se inclina hacia adelante y baja.
                        float k = Smooth((u - 0.45f) / 0.3f);
                        s.Pitch = Mathf.Lerp(14f, 55f, k);
                        s.Height = -0.25f * k;
                    }
                    else
                    {
                        // Se recupera con un saltito.
                        float k = (u - 0.75f) / 0.25f;
                        s.Pitch = Mathf.Lerp(55f, 0f, Smooth(k));
                        s.Height = Mathf.Lerp(-0.25f, 0f, Smooth(k)) + Mathf.Sin(Mathf.PI * k) * hopHeight;
                    }
                    break;

                case StoryMotion.ShakeHead:
                    s.Shake = Mathf.Sin(time * 9f) * 25f;
                    break;

                case StoryMotion.Chew:
                    s.Nod = Mathf.Sin(time * 7f) * 9f;
                    break;
            }

            return s;
        }

        /// <summary>Carrera en el sitio: rebote, inclinacion y balanceo opuesto de brazos y piernas.</summary>
        private void RunCycle(ref BodyState s, float time, float lean)
        {
            float phase = time * runStepsPerSecond * Mathf.PI * 2f;
            s.Height = Mathf.Abs(Mathf.Sin(phase)) * 0.06f;
            s.Pitch = lean;
            s.Legs = Mathf.Sin(phase) * 38f;
            s.Arms = -Mathf.Sin(phase) * 35f;
        }

        private static BodyState Lerp(BodyState a, BodyState b, float t)
        {
            return new BodyState
            {
                Height = Mathf.Lerp(a.Height, b.Height, t),
                Pitch = Mathf.Lerp(a.Pitch, b.Pitch, t),
                Legs = Mathf.Lerp(a.Legs, b.Legs, t),
                Arms = Mathf.Lerp(a.Arms, b.Arms, t),
                Nod = Mathf.Lerp(a.Nod, b.Nod, t),
                Shake = Mathf.Lerp(a.Shake, b.Shake, t)
            };
        }

        private static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        // ------------------------------------------------------------------ Aplicar al esqueleto

        private void ApplyFrame(in ArmPose from, in ArmPose to, float blend, BodyState s)
        {
            // Sentado: la cadera baja hasta el asiento (el resto de movimientos se suma encima).
            float seatDrop = seated ? seatedHipsHeight - hipsBindLocalPosition.y : 0f;

            // 1) Cadera: define la posicion y la inclinacion de todo el cuerpo.
            hips.rotation = characterRoot.rotation * Quaternion.Euler(s.Pitch, 0f, 0f) * hipsBindRelativeRotation;
            hips.position = characterRoot.TransformPoint(new Vector3(
                hipsBindLocalPosition.x,
                hipsBindLocalPosition.y + s.Height + seatDrop,
                hipsBindLocalPosition.z));

            // 2) Brazos: mezcla de posturas + balanceo al correr.
            Quaternion armSwing = Quaternion.AngleAxis(s.Arms, characterRoot.right);
            Quaternion armSwingInverse = Quaternion.AngleAxis(-s.Arms, characterRoot.right);
            shoulderL.localRotation = WithWorldDelta(shoulderL, armSwing, Quaternion.Slerp(from.ShoulderL, to.ShoulderL, blend));
            elbowL.localRotation = Quaternion.Slerp(from.ElbowL, to.ElbowL, blend);
            shoulderR.localRotation = WithWorldDelta(shoulderR, armSwingInverse, Quaternion.Slerp(from.ShoulderR, to.ShoulderR, blend));
            elbowR.localRotation = Quaternion.Slerp(from.ElbowR, to.ElbowR, blend);

            // 3) Piernas. Sentado: los muslos apuntan hacia adelante (-90 grados) y las rodillas se doblan (+90).
            float thighBase = seated ? -90f : 0f;
            upperLegL.localRotation = WithWorldDelta(upperLegL, Quaternion.AngleAxis(thighBase + s.Legs, characterRoot.right), bindUpperLegL);
            upperLegR.localRotation = WithWorldDelta(upperLegR, Quaternion.AngleAxis(thighBase - s.Legs, characterRoot.right), bindUpperLegR);

            // Las pantorrillas se calculan despues de los muslos porque dependen de su rotacion actual.
            if (seated)
            {
                Quaternion kneeBend = Quaternion.AngleAxis(90f, characterRoot.right);
                lowerLegL.localRotation = WithWorldDelta(lowerLegL, kneeBend, bindLowerLegL);
                lowerLegR.localRotation = WithWorldDelta(lowerLegR, kneeBend, bindLowerLegR);
            }
            else
            {
                lowerLegL.localRotation = bindLowerLegL;
                lowerLegR.localRotation = bindLowerLegR;
            }

            // 4) Cabeza: asentir (eje derecho) y negar (eje vertical).
            Quaternion headDelta = Quaternion.AngleAxis(s.Shake, characterRoot.up) * Quaternion.AngleAxis(s.Nod, characterRoot.right);
            head.localRotation = WithWorldDelta(head, headDelta, bindHead);

            // 5) Respiracion.
            float wave = Mathf.Sin(Time.time * breathsPerSecond * Mathf.PI * 2f);
            spine.localScale = spineBindScale * (1f + breathAmplitude * wave);
        }

        /// <summary>Aplica una rotacion definida en espacio mundo a un hueso, partiendo de su rotacion local base.</summary>
        private static Quaternion WithWorldDelta(Transform bone, Quaternion worldDelta, Quaternion baseLocal)
        {
            Quaternion parent = bone.parent.rotation;
            return Quaternion.Inverse(parent) * worldDelta * parent * baseLocal;
        }

        // ------------------------------------------------------------------ Inicializacion

        private bool CacheBones()
        {
            Transform[] all = characterRoot.GetComponentsInChildren<Transform>(true);

            hips = FindBone(all, "Hips");
            spine = FindBone(all, "Spine_02");
            head = FindBone(all, "Head");
            shoulderL = FindBone(all, "Shoulder_L");
            elbowL = FindBone(all, "Elbow_L");
            handL = FindBone(all, "Hand_L");
            shoulderR = FindBone(all, "Shoulder_R");
            elbowR = FindBone(all, "Elbow_R");
            handR = FindBone(all, "Hand_R");
            upperLegL = FindBone(all, "UpperLeg_L");
            upperLegR = FindBone(all, "UpperLeg_R");
            lowerLegL = FindBone(all, "LowerLeg_L");
            lowerLegR = FindBone(all, "LowerLeg_R");

            if (hips == null || spine == null || head == null
                || shoulderL == null || elbowL == null || handL == null
                || shoulderR == null || elbowR == null || handR == null
                || upperLegL == null || upperLegR == null || lowerLegL == null || lowerLegR == null)
            {
                Debug.LogError($"[{nameof(StoryActor)}] Faltan huesos del rig POLYGON " +
                               "(Hips, Spine_02, Head, Shoulder/Elbow/Hand _L/_R, UpperLeg/LowerLeg _L/_R).", this);
                return false;
            }

            bindShoulderL = shoulderL.localRotation;
            bindElbowL = elbowL.localRotation;
            bindShoulderR = shoulderR.localRotation;
            bindElbowR = elbowR.localRotation;
            bindUpperLegL = upperLegL.localRotation;
            bindUpperLegR = upperLegR.localRotation;
            bindLowerLegL = lowerLegL.localRotation;
            bindLowerLegR = lowerLegR.localRotation;
            bindHead = head.localRotation;

            hipsBindRelativeRotation = Quaternion.Inverse(characterRoot.rotation) * hips.rotation;
            hipsBindLocalPosition = characterRoot.InverseTransformPoint(hips.position);
            spineBindScale = spine.localScale;
            return true;
        }

        private static Transform FindBone(Transform[] all, string boneName)
        {
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name == boneName)
                {
                    return all[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Calcula una sola vez cada postura. Direcciones en espacio del personaje:
        /// x = hacia afuera del cuerpo, y = arriba, z = adelante.
        /// </summary>
        private void BuildPoses()
        {
            poses = new ArmPose[Enum.GetValues(typeof(StoryPose)).Length];

            Vector3 relaxedUpper = new Vector3(0.15f, -1f, 0f);
            Vector3 relaxedFore = new Vector3(0.05f, -0.9f, 0.35f);

            poses[(int)StoryPose.Relaxed] = BuildArmPose(relaxedUpper, relaxedFore, relaxedUpper, relaxedFore);

            Vector3 upUpper = new Vector3(0.35f, 1f, 0.1f);
            Vector3 upFore = new Vector3(0.15f, 1f, 0f);
            poses[(int)StoryPose.ArmsUp] = BuildArmPose(upUpper, upFore, upUpper, upFore);

            Vector3 outUpper = new Vector3(1f, 0.3f, 0f);
            Vector3 outFore = new Vector3(1f, 0.35f, 0f);
            poses[(int)StoryPose.ArmsOut] = BuildArmPose(outUpper, outFore, outUpper, outFore);

            Vector3 faceUpper = new Vector3(0.35f, -0.6f, 0.6f);
            Vector3 faceFore = new Vector3(-0.2f, 0.9f, 0.5f);
            poses[(int)StoryPose.HandsOnFace] = BuildArmPose(faceUpper, faceFore, faceUpper, faceFore);

            // Un brazo relajado y el otro senalando hacia adelante.
            Vector3 pointDir = new Vector3(0.3f, 0.05f, 1f);
            poses[(int)StoryPose.PointForward] = BuildArmPose(relaxedUpper, relaxedFore, pointDir, pointDir);

            Vector3 mouthUpper = new Vector3(0.2f, -0.8f, 0.5f);
            Vector3 mouthFore = new Vector3(-0.5f, 0.85f, 0.5f);
            poses[(int)StoryPose.HandsToMouth] = BuildArmPose(mouthUpper, mouthFore, mouthUpper, mouthFore);
        }

        private ArmPose BuildArmPose(Vector3 upperL, Vector3 foreL, Vector3 upperR, Vector3 foreR)
        {
            RestoreBindArms();

            Aim(shoulderL, elbowL, ToWorld(OutwardOf(shoulderL), upperL));
            Aim(elbowL, handL, ToWorld(OutwardOf(shoulderL), foreL));
            Aim(shoulderR, elbowR, ToWorld(OutwardOf(shoulderR), upperR));
            Aim(elbowR, handR, ToWorld(OutwardOf(shoulderR), foreR));

            ArmPose pose = new ArmPose
            {
                ShoulderL = shoulderL.localRotation,
                ElbowL = elbowL.localRotation,
                ShoulderR = shoulderR.localRotation,
                ElbowR = elbowR.localRotation
            };

            RestoreBindArms();
            return pose;
        }

        private void RestoreBindArms()
        {
            shoulderL.localRotation = bindShoulderL;
            elbowL.localRotation = bindElbowL;
            shoulderR.localRotation = bindShoulderR;
            elbowR.localRotation = bindElbowR;
        }

        private Vector3 OutwardOf(Transform shoulder)
        {
            float side = Mathf.Sign(Vector3.Dot(shoulder.position - hips.position, characterRoot.right));
            return characterRoot.right * side;
        }

        private Vector3 ToWorld(Vector3 outward, Vector3 characterSpaceDirection)
        {
            return (outward * characterSpaceDirection.x
                    + characterRoot.up * characterSpaceDirection.y
                    + characterRoot.forward * characterSpaceDirection.z).normalized;
        }

        private static void Aim(Transform bone, Transform child, Vector3 worldDirection)
        {
            Vector3 current = (child.position - bone.position).normalized;
            bone.rotation = Quaternion.FromToRotation(current, worldDirection) * bone.rotation;
        }
    }
}
