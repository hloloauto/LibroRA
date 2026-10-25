using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Finger = UnityEngine.InputSystem.EnhancedTouch.Finger;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace LibroRA.AR
{
    /// <summary>
    /// Coloca (o reposiciona) un prefab sobre el plano real detectado al tocar la pantalla.
    /// - Basado en eventos: reacciona a <see cref="Touch.onFingerDown"/>, sin polling en Update().
    /// - El prefab se instancia UNA sola vez; los toques siguientes solo lo mueven (sin GC).
    /// - Requiere el nuevo Input System (Project Settings > Player > Active Input Handling).
    /// Se cuelga en el GameObject "XR Origin" junto a ARRaycastManager y ARPlaneManager.
    /// </summary>
    [RequireComponent(typeof(ARRaycastManager))]
    public sealed class ARTapToPlace : MonoBehaviour
    {
        [Header("Contenido")]
        [Tooltip("Prefab de POLYGON a colocar sobre el suelo/mesa detectado.")]
        [SerializeField] private GameObject prefabToPlace;

        [Tooltip("Multiplicador de escala. Los modelos Synty miden ~1 unidad = 1 m; en mesa suele usarse 0.05 - 0.3.")]
        [SerializeField, Min(0.001f)] private float placementScale = 0.15f;

        [Tooltip("Si es true, el objeto gira en el eje Y para mirar a la camara al colocarse por primera vez.")]
        [SerializeField] private bool faceCameraOnFirstPlacement = true;

        [Header("Referencias AR")]
        [Tooltip("Camara AR (XR Origin > Camera Offset > Main Camera). Solo se usa para orientar el objeto.")]
        [SerializeField] private Camera arCamera;

        [Tooltip("Que tipos de superficie aceptan el toque.")]
        [SerializeField] private TrackableType trackableTypes = TrackableType.PlaneWithinPolygon;

        // Lista reutilizada en cada raycast para no generar basura.
        private static readonly List<ARRaycastHit> Hits = new List<ARRaycastHit>(8);

        private ARRaycastManager raycastManager;
        private Transform arCameraTransform;
        private GameObject spawnedInstance;
        private Transform spawnedTransform;

        /// <summary>Instancia actual colocada en la escena (null hasta el primer toque valido).</summary>
        public GameObject SpawnedInstance => spawnedInstance;

        private void Awake()
        {
            // Cache de referencias (regla del proyecto: nada de Find en runtime).
            raycastManager = GetComponent<ARRaycastManager>();

            if (arCamera == null)
            {
                Debug.LogError($"[{nameof(ARTapToPlace)}] Falta asignar 'arCamera' en el Inspector.", this);
                enabled = false;
                return;
            }

            if (prefabToPlace == null)
            {
                Debug.LogError($"[{nameof(ARTapToPlace)}] Falta asignar 'prefabToPlace' en el Inspector.", this);
                enabled = false;
                return;
            }

            arCameraTransform = arCamera.transform;
        }

        private void OnEnable()
        {
            EnhancedTouchSupport.Enable();
            Touch.onFingerDown += OnFingerDown;
        }

        private void OnDisable()
        {
            Touch.onFingerDown -= OnFingerDown;
            EnhancedTouchSupport.Disable();
        }

        private void OnFingerDown(Finger finger)
        {
            // Solo reaccionamos al primer dedo para evitar colocar con gestos multitouch.
            if (finger.index != 0)
            {
                return;
            }

            if (!raycastManager.Raycast(finger.screenPosition, Hits, trackableTypes))
            {
                return;
            }

            // Los hits vienen ordenados por distancia: el primero es el mas cercano.
            Pose hitPose = Hits[0].pose;
            PlaceOrMove(hitPose);
        }

        private void PlaceOrMove(Pose hitPose)
        {
            if (spawnedInstance == null)
            {
                spawnedInstance = Instantiate(prefabToPlace, hitPose.position, hitPose.rotation);
                spawnedTransform = spawnedInstance.transform;
                spawnedTransform.localScale *= placementScale;

                if (faceCameraOnFirstPlacement)
                {
                    FaceCameraYawOnly();
                }
                return;
            }

            // Reposicionar: se conserva la rotacion y escala actuales.
            spawnedTransform.position = hitPose.position;
        }

        /// <summary>Orienta el objeto hacia la camara ignorando la inclinacion (solo giro en Y).</summary>
        private void FaceCameraYawOnly()
        {
            Vector3 toCamera = arCameraTransform.position - spawnedTransform.position;
            toCamera.y = 0f;

            if (toCamera.sqrMagnitude > 0.0001f)
            {
                spawnedTransform.rotation = Quaternion.LookRotation(toCamera.normalized, Vector3.up);
            }
        }
    }
}
