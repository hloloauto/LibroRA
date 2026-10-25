using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace LibroRA.Controllers
{
    /// <summary>
    /// Medidor de rendimiento en pantalla para evidenciar FPS, draw calls y memoria en la tableta.
    ///
    /// - Solo existe en Development Build (y en el editor en Play): se crea solo, sin tocar la escena,
    ///   y NO forma parte de la version de produccion.
    /// - Muestra FPS promedio y minimo, tiempo de cuadro, draw calls, SetPass, batches, triangulos y memoria.
    /// - Cada 60 segundos escribe un promedio en logcat con la etiqueta [RENDIMIENTO], para citarlo en el informe.
    /// - Los contadores se leen con ProfilerRecorder (sin asignaciones por cuadro); el texto se reconstruye una vez por segundo.
    /// </summary>
    public sealed class PerformanceOverlay : MonoBehaviour
    {
        private const float RefreshSeconds = 1f;
        private const float ReportSeconds = 60f;
        private const float BytesPerMegabyte = 1048576f;

        private ProfilerRecorder drawCalls;
        private ProfilerRecorder setPassCalls;
        private ProfilerRecorder batches;
        private ProfilerRecorder triangles;
        private ProfilerRecorder usedMemory;
        private ProfilerRecorder gcMemory;

        private readonly StringBuilder builder = new StringBuilder(320);
        private GUIStyle style;
        private string liveText = "Midiendo...";
        private string reportText = string.Empty;

        // Ventana de 1 segundo (lo que se ve en pantalla).
        private int windowFrames;
        private float windowTime;
        private float windowWorstFrame;

        // Ventana de 60 segundos (informe).
        private int reportFrames;
        private float reportTime;
        private float reportWorstFrame;
        private double reportDrawCalls;
        private double reportBatches;
        private double reportMemory;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (!Debug.isDebugBuild)
            {
                return;
            }

            GameObject host = new GameObject(nameof(PerformanceOverlay));
            DontDestroyOnLoad(host);
            host.AddComponent<PerformanceOverlay>();
        }

        private void OnEnable()
        {
            drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            setPassCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            usedMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Used Memory");
            gcMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Used Memory");
        }

        private void OnDisable()
        {
            drawCalls.Dispose();
            setPassCalls.Dispose();
            batches.Dispose();
            triangles.Dispose();
            usedMemory.Dispose();
            gcMemory.Dispose();
        }

        // Update solo en herramienta de depuracion: acumula el tiempo de cada cuadro (no contiene logica del juego).
        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f)
            {
                return;
            }

            windowFrames++;
            windowTime += dt;
            windowWorstFrame = Mathf.Max(windowWorstFrame, dt);

            reportFrames++;
            reportTime += dt;
            reportWorstFrame = Mathf.Max(reportWorstFrame, dt);
            reportDrawCalls += drawCalls.LastValue;
            reportBatches += batches.LastValue;
            reportMemory += usedMemory.LastValue;

            if (windowTime >= RefreshSeconds)
            {
                RefreshLiveText();
            }

            if (reportTime >= ReportSeconds)
            {
                PublishReport();
            }
        }

        private void RefreshLiveText()
        {
            float fps = windowFrames / windowTime;
            float minFps = 1f / windowWorstFrame;

            builder.Clear();
            builder.Append("FPS ").Append(fps.ToString("0")).Append("  (min ").Append(minFps.ToString("0")).Append(")  ")
                   .Append((1000f / fps).ToString("0.0")).Append(" ms\n");
            builder.Append("Draw calls ").Append(drawCalls.LastValue)
                   .Append("  SetPass ").Append(setPassCalls.LastValue)
                   .Append("  Batches ").Append(batches.LastValue).Append('\n');
            builder.Append("Triangulos ").Append((triangles.LastValue / 1000f).ToString("0")).Append(" k\n");
            builder.Append("Memoria ").Append((usedMemory.LastValue / BytesPerMegabyte).ToString("0")).Append(" MB")
                   .Append("  (GC ").Append((gcMemory.LastValue / BytesPerMegabyte).ToString("0")).Append(" MB)");
            liveText = builder.ToString();

            windowFrames = 0;
            windowTime = 0f;
            windowWorstFrame = 0f;
        }

        private void PublishReport()
        {
            float fps = reportFrames / reportTime;
            float minFps = 1f / reportWorstFrame;
            double drawAverage = reportDrawCalls / reportFrames;
            double batchAverage = reportBatches / reportFrames;
            double memoryAverage = reportMemory / reportFrames / BytesPerMegabyte;

            reportText = $"Promedio {ReportSeconds:0} s: {fps:0} FPS (min {minFps:0}) | draw calls {drawAverage:0} | " +
                         $"batches {batchAverage:0} | memoria {memoryAverage:0} MB";
            Debug.Log($"[RENDIMIENTO] {reportText}");

            reportFrames = 0;
            reportTime = 0f;
            reportWorstFrame = 0f;
            reportDrawCalls = 0d;
            reportBatches = 0d;
            reportMemory = 0d;
        }

        private void OnGUI()
        {
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.Max(18, Mathf.RoundToInt(Screen.height * 0.022f)),
                    normal = { textColor = Color.white },
                    wordWrap = true
                };
            }

            float width = Screen.width * 0.42f;
            float height = style.fontSize * 8.5f;
            Rect box = new Rect(12f, Screen.height - height - 12f, width, height);

            GUI.Box(box, GUIContent.none);
            GUI.Label(new Rect(box.x + 8f, box.y + 4f, box.width - 16f, box.height - 8f),
                string.IsNullOrEmpty(reportText) ? liveText : liveText + "\n" + reportText, style);
        }
    }
}
