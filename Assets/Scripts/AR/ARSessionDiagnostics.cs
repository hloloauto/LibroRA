using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace LibroRA.AR
{
    /// <summary>
    /// Muestra en pantalla el estado de la sesion AR (solo en Development Build / Editor).
    /// Util para distinguir: dispositivo no soportado, ARCore sin instalar, sin permiso de camara, etc.
    /// Basado en el evento <see cref="ARSession.stateChanged"/>; no hace polling.
    /// Se cuelga en cualquier GameObject de la escena (por ejemplo "AR Session").
    /// </summary>
    public sealed class ARSessionDiagnostics : MonoBehaviour
    {
        [SerializeField, Min(10)] private int fontSize = 36;

        private string message = "AR: esperando estado...";
        private GUIStyle style;

        private void OnEnable()
        {
            if (!Debug.isDebugBuild)
            {
                enabled = false;
                return;
            }

            ARSession.stateChanged += OnStateChanged;
            UpdateMessage(ARSession.state);
        }

        private void OnDisable()
        {
            ARSession.stateChanged -= OnStateChanged;
        }

        private void OnStateChanged(ARSessionStateChangedEventArgs args)
        {
            UpdateMessage(args.state);
        }

        private void UpdateMessage(ARSessionState state)
        {
            message = $"AR Session: {state}\n{Describe(state)}";
            Debug.Log($"[{nameof(ARSessionDiagnostics)}] {message}");
        }

        private static string Describe(ARSessionState state)
        {
            switch (state)
            {
                case ARSessionState.Unsupported:
                    return "Dispositivo NO compatible con ARCore.";
                case ARSessionState.NeedsInstall:
                    return "Falta instalar/actualizar Google Play Services for AR.";
                case ARSessionState.Installing:
                    return "Instalando ARCore...";
                case ARSessionState.Ready:
                    return "ARCore listo; iniciando sesion.";
                case ARSessionState.SessionInitializing:
                    return "Inicializando. Mueve el dispositivo despacio.";
                case ARSessionState.SessionTracking:
                    return "Tracking OK. Si la pantalla sigue negra, es un problema de render.";
                case ARSessionState.CheckingAvailability:
                    return "Comprobando disponibilidad...";
                default:
                    return string.Empty;
            }
        }

        private void OnGUI()
        {
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = fontSize,
                    normal = { textColor = Color.yellow }
                };
            }

            GUI.Label(new Rect(20f, 20f, Screen.width - 40f, 300f), message, style);
        }
    }
}
