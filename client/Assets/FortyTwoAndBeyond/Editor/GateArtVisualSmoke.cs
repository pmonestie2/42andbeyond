using System;
using System.IO;
using System.Linq;
using FortyTwoAndBeyond.Client;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Capture the jump gate against an owned disposable sector.</summary>
    [InitializeOnLoad]
    public static class GateArtVisualSmoke
    {
        private static double deadline, captureAt;
        private static int stage;
        private static bool connected;

        /// <summary>Enable the opt-in visual driver after Editor domain reloads.</summary>
        static GateArtVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-gate-art-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Connect to the local fixture and capture the rendered gate approach.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline) { Fail("Gate art smoke timed out."); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameView != null)
                {
                    EditorWindow window = EditorWindow.GetWindow(gameView);
                    window.maximized = true; window.Focus();
                }
                EditorApplication.EnterPlaymode(); return;
            }
            if (!EditorApplication.isPlaying) return;
            SpaceClient client = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            if (!connected) { connected = true; client.StartLocalSession("127.0.0.1", "local", "local"); }
            if (client.ChoosingCharacter) client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            if (client.CurrentSnapshot?.Ready != true || !client.CurrentSnapshot.HasPosition) return;
            var gate = client.CurrentSnapshot.Objects.FirstOrDefault(item => item.Kind == 11);
            if (gate == null) return;
            if (stage == 0)
            {
                client.Select(gate.Id);
                captureAt = EditorApplication.timeSinceStartup + 2;
                stage = 1; return;
            }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            Directory.CreateDirectory("Builds/verification");
            if (stage == 1)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/gate-art.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 2; return;
            }
            if (!File.Exists("Builds/verification/gate-art.png"))
            { Fail("Missing gate art capture."); return; }
            Debug.Log("PASS: server gate selected and graphical approach captured");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Exit the opt-in Editor with the failing stage.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
