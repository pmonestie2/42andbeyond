using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FortyTwoAndBeyond.Client;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Capture the station art against an owned disposable station session.</summary>
    [InitializeOnLoad]
    public static class StationArtVisualSmoke
    {
        private static double deadline, captureAt;
        private static int stage;
        private static bool connected;

        /// <summary>Attach the opt-in driver before the Editor enters Play mode.</summary>
        static StationArtVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-station-art-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Capture the docking bay, then walk into the next station room.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline) { Fail("Station art smoke timed out."); return; }
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
            if (client.CurrentSnapshot?.Station == null || !client.CurrentSnapshot.Ready) return;
            Directory.CreateDirectory("Builds/verification");
            if (stage == 0)
            {
                captureAt = EditorApplication.timeSinceStartup + 2;
                stage = 1; return;
            }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            if (stage == 1)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/station-hangar.png");
                stage = 2; captureAt = EditorApplication.timeSinceStartup + 1; return;
            }
            if (stage == 2)
            {
                if (!File.Exists("Builds/verification/station-hangar.png"))
                { Fail("Missing hangar capture."); return; }
                if (!client.CurrentSnapshot.Station.RoomList.Any(room => room.Index == 1))
                { Fail("Disposable station has no concourse room."); return; }
                client.EnterStationRoom(1);
                stage = 3; return;
            }
            if (stage == 3)
            {
                if (client.CurrentSnapshot.Station.CurrentRoom != 1) return;
                captureAt = EditorApplication.timeSinceStartup + 2;
                stage = 4; return;
            }
            if (stage == 4)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/station-concourse.png");
                stage = 5; captureAt = EditorApplication.timeSinceStartup + 1; return;
            }
            if (stage == 5)
            {
                if (!File.Exists("Builds/verification/station-concourse.png"))
                { Fail("Missing concourse capture."); return; }
                client.EnterStationRoom(0);
                stage = 6; return;
            }
            if (stage == 6)
            {
                if (client.CurrentSnapshot.Station.CurrentRoom != 0) return;
                FieldInfo field = typeof(SpaceClient).GetField("hud",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                FlightHud hud = field?.GetValue(client) as FlightHud;
                if (hud == null) { Fail("Missing loadout HUD."); return; }
                hud.ToggleEquipment();
                captureAt = EditorApplication.timeSinceStartup + 2;
                stage = 7; return;
            }
            if (stage == 7)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/station-loadout.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 8; return;
            }
            if (!File.Exists("Builds/verification/station-loadout.png"))
            { Fail("Missing loadout capture."); return; }
            Debug.Log("PASS: disposable station hangar, concourse, and loadout art captured");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Exit the opt-in Editor run with a meaningful failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
