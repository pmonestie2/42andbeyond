using System;
using System.IO;
using System.Linq;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Capture a real scene pick and 4–5 km non-nav warp on disposable state.</summary>
    [InitializeOnLoad]
    public static class WarpTargetVisualSmoke
    {
        private static int stage;
        private static uint target;
        private static double deadline;
        private static bool connected;
        private static string lastObservation = "waiting for snapshot";

        /// <summary>Register only for the owned object-target graphical check.</summary>
        static WarpTargetVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-warp-target-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 100;
            EditorApplication.update += Tick;
        }

        /// <summary>Select a projected ore marker and record the server warp result.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Warp target graphical smoke timed out at stage " + stage + ": " + lastObservation); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameView != null)
                {
                    EditorWindow window = EditorWindow.GetWindow(gameView);
                    window.maximized = true; window.Focus();
                }
                EditorApplication.EnterPlaymode();
                return;
            }
            if (!EditorApplication.isPlaying) return;
            SpaceClient client = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            if (!connected)
            { connected = true; client.StartLocalSession("127.0.0.1", "local", "local"); }
            if (client.ChoosingCharacter) client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || !snapshot.HasPosition) return;
            MapObject ore = snapshot.Objects.FirstOrDefault(item => item.Id == target) ??
                snapshot.Objects.FirstOrDefault(item => item.Kind == 38 &&
                    item.Position.Distance(snapshot.Position) >= 4000 &&
                    item.Position.Distance(snapshot.Position) <= 5000);
            if (ore == null)
            {
                lastObservation = $"position {snapshot.Position}, resources " +
                    string.Join(", ", snapshot.Objects.Where(item => item.Kind == 38)
                        .Select(item => $"{item.Id}:{item.Position.Distance(snapshot.Position):0}"));
                return;
            }
            Directory.CreateDirectory("Builds/verification");
            if (stage == 0)
            {
                Vector3 marker = client.Project(ore.Position);
                lastObservation = $"ore {ore.Id} at {ore.Position.Distance(snapshot.Position):0} m, " +
                    $"marker {marker}, screen {Screen.width}x{Screen.height}";
                if (marker.z <= 0 || marker.x < 360 || marker.x > Screen.width - 340 ||
                    marker.y < 100 || marker.y > Screen.height - 150) return;
                target = ore.Id;
                client.SelectAtPointer(new Vector2(marker.x, marker.y));
                stage = 1;
            }
            else if (stage == 1 && client.WarpTargetId == target &&
                snapshot.SelectedTargetId == target)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/warp-object-selected.png");
                client.WarpSelected();
                stage = 2;
            }
            else if (stage == 2 && (snapshot.WarpPhase == WarpPhase.Charging ||
                snapshot.WarpPhase == WarpPhase.Traveling))
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/warp-object-travel.png");
                stage = 3;
            }
            else if (stage == 3 && ore.Position.Distance(snapshot.Position) < 1300 &&
                snapshot.WarpPhase != WarpPhase.Charging &&
                snapshot.WarpPhase != WarpPhase.Traveling)
            {
                Debug.Log($"PASS: scene-picked resource {target} at 4–5 km and warped to " +
                    $"{ore.Position.Distance(snapshot.Position):0} m center range.");
                stage = 4;
            }
            else if (stage == 4 &&
                File.Exists("Builds/verification/warp-object-selected.png") &&
                File.Exists("Builds/verification/warp-object-travel.png"))
            {
                EditorApplication.update -= Tick;
                EditorApplication.Exit(0);
            }
        }

        /// <summary>Exit the disposable Editor with its failing stage.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
