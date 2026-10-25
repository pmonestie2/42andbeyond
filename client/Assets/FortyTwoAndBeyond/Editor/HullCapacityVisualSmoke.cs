using System;
using System.IO;
using System.Reflection;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Capture a low-stage scout's native hull and cargo capacity in loadout.</summary>
    [InitializeOnLoad]
    public static class HullCapacityVisualSmoke
    {
        private static int stage;
        private static bool connected;
        private static double deadline, captureAt;

        /// <summary>Register only for the owned hull-progression fixture.</summary>
        static HullCapacityVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                    "--42andbeyond-hull-capacity-smoke") < 0) return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Wait for authoritative AUX values before opening the loadout overlay.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Hull capacity smoke timed out at stage " + stage); return; }
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
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || snapshot.Experience == null ||
                snapshot.CargoCapacity == 0) return;
            if (stage == 0)
            {
                if (snapshot.Experience.HullStage != 0 || snapshot.CargoCapacity != 25)
                { Fail($"Expected stage-zero scout 25 cargo slots, got stage " +
                    $"{snapshot.Experience.HullStage} and {snapshot.CargoCapacity} slots."); return; }
                FieldInfo field = typeof(SpaceClient).GetField("hud",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                FlightHud hud = field?.GetValue(client) as FlightHud;
                if (hud == null) { Fail("Flight HUD unavailable."); return; }
                hud.ToggleEquipment();
                Directory.CreateDirectory("Builds/verification");
                captureAt = EditorApplication.timeSinceStartup + .35;
                stage = 1; return;
            }
            if (stage == 1 && EditorApplication.timeSinceStartup >= captureAt)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/hull-capacity.png");
                captureAt = EditorApplication.timeSinceStartup + .6;
                stage = 2; return;
            }
            if (stage != 2 || EditorApplication.timeSinceStartup < captureAt) return;
            if (!File.Exists("Builds/verification/hull-capacity.png"))
            { Fail("Hull capacity capture missing."); return; }
            Debug.Log("PASS: stage-zero scout native 25-slot cargo capacity captured.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Exit only the owned Editor with a concrete fixture failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
