using System;
using System.Collections;
using UnityEngine;

namespace LibroRA.AR
{
    /// <summary>
    /// Animacion procedural de "dormido -> despertar" para un personaje POLYGON (rig Humanoid de Synty).
    /// No requiere clips de animacion: posa los huesos por codigo.
    ///
    /// Secuencia: durmiendo acostado boca arriba (respirando) -> se estira -> se levanta -> de pie relajado.
    ///
    /// - Las direcciones de los brazos se calculan en espacio mundo a partir de la posicion de los huesos,
    ///   por lo que NO dependen de los ejes locales de cada hueso.
    /// - Todas las referencias se cachean en Awake; las animaciones usan corrutinas (sin Update ni Find).
    /// - Desactiva el Animator del personaje para que no sobrescriba la pose (el prefab no tiene controlador).
    ///
    /// Se cuelga en la raiz del prefab del personaje (p. ej. "Character_Warrior_White Variant").
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SleepWakeController : MonoBehaviour
    {
        private const string HipsName = "Hips";
        private const string SpineName = "Spine_02";

        [Header("Referencias (opcional)")]
        [Tooltip("Raiz del personaje (define arriba/adelante/derecha). Si esta vacio se usa este GameObject.")]
        [SerializeField] private Transform characterRoot;

        [Header("Comportamiento")]
        [Tooltip("Al activarse el objeto, empieza dormido de inmediato.")]
        [SerializeField] private bool playOnEnable = true;

        [Tooltip("Si es true se despierta solo tras 'Sleep Seconds'. Si es false, llama a WakeUp() desde otro script.")]
        [SerializeField] private bool autoWake = true;

        [SerializeField, Min(0f)] private float sleepSeconds = 3f;
        [SerializeField, Min(0.05f)] private float stretchSeconds = 1.2f;
        [SerializeField, Min(0.05f)] private float riseSeconds = 1.1f;

        [Header("Ajuste de pose")]
        [Tooltip("Altura de la cadera cuando esta acostado, en unidades locales del personaje (~metros sin escalar).")]
        [SerializeField, Min(0f)] private float lyingHipsHeight = 0.15f;

        [Tooltip("Salto extra al levantarse (unidades locales).")]
        [SerializeField, Min(0f)] private float riseHopHeight = 0.12f;

        [Header("Respiracion")]
        [SerializeField, Min(0f)] private float sleepBreathAmplitude = 0.025f;
        [SerializeField, Min(0f)] private float awakeBreathAmplitude = 0.008f;
        [SerializeField, Min(0.1f)] private float breathsPerSecond = 0.35f;

        /// <summary>Pose de los cuatro huesos del brazo (rotaciones locales).</summary>
        private struct ArmPose
        {
            public Quaternion ShoulderL, ElbowL, ShoulderR, ElbowR;
        }

        // Huesos cacheados.
        private Transform hips, spine, shoulderL, elbowL, handL, shoulderR, elbowR, handR;

        // Pose original (T-pose) para restaurar y como referencia.
        private Quaternion bindShoulderL, bindElbowL, bindShoulderR, bindElbowR;
        private Quaternion hipsBindRelativeRotation;
        private Vector3 hipsBindLocalPosition;
        private Vector3 spineBindScale;

        private ArmPose sleepArms, stretchArms, relaxedArms;

        private bool initialized;

        /// <summary>True una vez terminada la secuencia de despertar.</summary>
        public bool IsAwake { get; private set; }

        /// <summary>Se dispara cuando el personaje termina de levantarse.</summary>
        public event Action Awakened;

        /// <summary>Se dispara cuando el personaje pasa a estar dormido (o empieza a acostarse).</summary>
        public event Action FellAsleep;

        private void Awake()
        {
            if (characterRoot == null)
            {
                characterRoot = transform;
            }

            // Sin controlador asignado, el Animator no aporta nada; se apaga para no pisar la pose.
            Animator animator = GetComponentInChildren<Animator>(true);
            if (animator != null)
            {
                animator.enabled = false;
            }

            initialized = CacheBones();
            if (!initialized)
            {
                enabled = false;
                return;
            }

            BuildPoses();
        }

        private void OnEnable()
        {
            if (!initialized || !playOnEnable)
            {
                return;
            }

            StartCoroutine(autoWake ? FullSequence() : SleepOnly());
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            IsAwake = false;
        }

        // ------------------------------------------------------------------ API publica

        /// <summary>Acuesta al personaje dormido y lo mantiene respirando hasta llamar a <see cref="WakeUp"/>.</summary>
        public void BeginSleep()
        {
            if (!initialized) return;
            StopAllCoroutines();
            StartCoroutine(SleepOnly());
        }

        /// <summary>Inicia la secuencia de despertar (estirarse y levantarse) desde la pose actual.</summary>
        public void WakeUp()
        {
            if (!initialized) return;
            StopAllCoroutines();
            StartCoroutine(WakeSequence());
        }

        /// <summary>Vuelve a acostarse con una animacion suave y se queda dormido hasta llamar a <see cref="WakeUp"/>.</summary>
        public void GoBackToSleep()
        {
            if (!initialized) return;
            StopAllCoroutines();
            StartCoroutine(SleepDownSequence());
        }

        /// <summary>Detiene todo y deja al personaje dormido (util al perder el tracking de la pagina).</summary>
        public void ResetToSleep()
        {
            if (!initialized) return;
            StopAllCoroutines();
            EnterSleepPose();
        }

        // ------------------------------------------------------------------ Secuencias

        private IEnumerator SleepOnly()
        {
            EnterSleepPose();
            while (true)
            {
                Breathe(sleepBreathAmplitude);
                yield return null;
            }
        }

        private IEnumerator FullSequence()
        {
            EnterSleepPose();

            float elapsed = 0f;
            while (elapsed < sleepSeconds)
            {
                Breathe(sleepBreathAmplitude);
                elapsed += Time.deltaTime;
                yield return null;
            }

            yield return WakeSequence();
        }

        private IEnumerator WakeSequence()
        {
            IsAwake = false;

            // 1) Estirarse: los brazos pasan de la pose de dormir a arriba.
            float elapsed = 0f;
            while (elapsed < stretchSeconds)
            {
                float t = Smooth(elapsed / stretchSeconds);
                ApplyArms(in sleepArms, in stretchArms, t);
                Breathe(Mathf.Lerp(sleepBreathAmplitude, awakeBreathAmplitude, t));
                elapsed += Time.deltaTime;
                yield return null;
            }

            // 2) Levantarse: gira de acostado a de pie con un pequeno salto; brazos a posicion relajada.
            elapsed = 0f;
            while (elapsed < riseSeconds)
            {
                float t = Smooth(elapsed / riseSeconds);
                float height = Mathf.Lerp(lyingHipsHeight, hipsBindLocalPosition.y, t)
                               + Mathf.Sin(t * Mathf.PI) * riseHopHeight;
                ApplyBody(Mathf.Lerp(-90f, 0f, t), height);
                ApplyArms(in stretchArms, in relaxedArms, t);
                Breathe(awakeBreathAmplitude);
                elapsed += Time.deltaTime;
                yield return null;
            }

            ApplyBody(0f, hipsBindLocalPosition.y);
            ApplyArms(in relaxedArms, in relaxedArms, 1f);
            IsAwake = true;
            Awakened?.Invoke();

            // 3) De pie: solo respira hasta que se desactive el objeto.
            while (true)
            {
                Breathe(awakeBreathAmplitude);
                yield return null;
            }
        }

        /// <summary>Inverso de levantarse: de pie a acostado, brazos de relajados a dormidos.</summary>
        private IEnumerator SleepDownSequence()
        {
            IsAwake = false;
            FellAsleep?.Invoke();

            float elapsed = 0f;
            while (elapsed < riseSeconds)
            {
                float t = Smooth(elapsed / riseSeconds);
                float height = Mathf.Lerp(hipsBindLocalPosition.y, lyingHipsHeight, t);
                ApplyBody(Mathf.Lerp(0f, -90f, t), height);
                ApplyArms(in relaxedArms, in sleepArms, t);
                Breathe(Mathf.Lerp(awakeBreathAmplitude, sleepBreathAmplitude, t));
                elapsed += Time.deltaTime;
                yield return null;
            }

            yield return SleepOnly();
        }

        // ------------------------------------------------------------------ Poses

        private void EnterSleepPose()
        {
            IsAwake = false;
            ApplyBody(-90f, lyingHipsHeight);
            ApplyArms(in sleepArms, in sleepArms, 1f);
            Breathe(sleepBreathAmplitude);
            FellAsleep?.Invoke();
        }

        /// <summary>Gira la cadera (y con ella todo el cuerpo) sobre el eje derecho del personaje.</summary>
        private void ApplyBody(float pitchDegrees, float height)
        {
            hips.rotation = characterRoot.rotation * Quaternion.Euler(pitchDegrees, 0f, 0f) * hipsBindRelativeRotation;
            hips.position = characterRoot.TransformPoint(
                new Vector3(hipsBindLocalPosition.x, height, hipsBindLocalPosition.z));
        }

        private void ApplyArms(in ArmPose from, in ArmPose to, float t)
        {
            shoulderL.localRotation = Quaternion.Slerp(from.ShoulderL, to.ShoulderL, t);
            elbowL.localRotation = Quaternion.Slerp(from.ElbowL, to.ElbowL, t);
            shoulderR.localRotation = Quaternion.Slerp(from.ShoulderR, to.ShoulderR, t);
            elbowR.localRotation = Quaternion.Slerp(from.ElbowR, to.ElbowR, t);
        }

        /// <summary>Hincha y deshincha ligeramente el torso.</summary>
        private void Breathe(float amplitude)
        {
            float wave = Mathf.Sin(Time.time * breathsPerSecond * Mathf.PI * 2f);
            spine.localScale = spineBindScale * (1f + amplitude * wave);
        }

        private static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        // ------------------------------------------------------------------ Inicializacion

        private bool CacheBones()
        {
            Transform[] all = characterRoot.GetComponentsInChildren<Transform>(true);

            hips = FindBone(all, HipsName);
            spine = FindBone(all, SpineName);
            shoulderL = FindBone(all, "Shoulder_L");
            elbowL = FindBone(all, "Elbow_L");
            handL = FindBone(all, "Hand_L");
            shoulderR = FindBone(all, "Shoulder_R");
            elbowR = FindBone(all, "Elbow_R");
            handR = FindBone(all, "Hand_R");

            if (hips == null || spine == null || shoulderL == null || elbowL == null || handL == null
                || shoulderR == null || elbowR == null || handR == null)
            {
                Debug.LogError($"[{nameof(SleepWakeController)}] No se encontraron todos los huesos " +
                               "(Hips, Spine_02, Shoulder/Elbow/Hand _L/_R). Revisa que el prefab sea un personaje POLYGON.", this);
                return false;
            }

            bindShoulderL = shoulderL.localRotation;
            bindElbowL = elbowL.localRotation;
            bindShoulderR = shoulderR.localRotation;
            bindElbowR = elbowR.localRotation;

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
        /// Calcula una vez las tres poses de brazos. Direcciones en espacio del personaje:
        /// x = hacia afuera del cuerpo, y = arriba, z = adelante.
        /// </summary>
        private void BuildPoses()
        {
            // Dormido: brazos pegados al cuerpo, antebrazos cruzados sobre el abdomen.
            sleepArms = BuildArmPose(new Vector3(0.20f, -1.00f, 0.10f), new Vector3(-0.45f, -0.25f, 0.60f));
            // Estirado: brazos hacia arriba.
            stretchArms = BuildArmPose(new Vector3(0.35f, 1.00f, 0.10f), new Vector3(0.15f, 1.00f, 0.00f));
            // Relajado: brazos caidos a los lados con codo ligeramente doblado.
            relaxedArms = BuildArmPose(new Vector3(0.15f, -1.00f, 0.00f), new Vector3(0.05f, -0.90f, 0.35f));
        }

        private ArmPose BuildArmPose(Vector3 upperDirection, Vector3 foreDirection)
        {
            RestoreBindArms();

            Vector3 outwardL = OutwardOf(shoulderL);
            Vector3 outwardR = OutwardOf(shoulderR);

            AimLimb(shoulderL, elbowL, handL, outwardL, upperDirection, foreDirection);
            AimLimb(shoulderR, elbowR, handR, outwardR, upperDirection, foreDirection);

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

        /// <summary>Vector lateral (hacia afuera) del lado al que pertenece el hombro.</summary>
        private Vector3 OutwardOf(Transform shoulder)
        {
            float side = Mathf.Sign(Vector3.Dot(shoulder.position - hips.position, characterRoot.right));
            return characterRoot.right * side;
        }

        private void AimLimb(Transform shoulder, Transform elbow, Transform hand, Vector3 outward,
            Vector3 upperDirection, Vector3 foreDirection)
        {
            // Primero el brazo superior; despues el antebrazo (su posicion ya refleja la rotacion del hombro).
            Aim(shoulder, elbow, ToWorld(outward, upperDirection));
            Aim(elbow, hand, ToWorld(outward, foreDirection));
        }

        private Vector3 ToWorld(Vector3 outward, Vector3 characterSpaceDirection)
        {
            return (outward * characterSpaceDirection.x
                    + characterRoot.up * characterSpaceDirection.y
                    + characterRoot.forward * characterSpaceDirection.z).normalized;
        }

        /// <summary>Rota 'bone' para que el segmento hacia 'child' apunte en 'worldDirection'.</summary>
        private static void Aim(Transform bone, Transform child, Vector3 worldDirection)
        {
            Vector3 current = (child.position - bone.position).normalized;
            bone.rotation = Quaternion.FromToRotation(current, worldDirection) * bone.rotation;
        }
    }
}
