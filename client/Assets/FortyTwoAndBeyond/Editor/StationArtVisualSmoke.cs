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
        private static bool compact;
        private static bool motion;
        private static uint contactId;

        /// <returns>string: The screenshot path for the active Game View size.</returns>
        private static string CaptureName(string scene)
        { return "Builds/verification/station-" + scene + (compact ? "-compact" : "") + ".png"; }

        /// <summary>Attach the opt-in driver before the Editor enters Play mode.</summary>
        static StationArtVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-station-art-smoke") < 0)
                return;
            compact = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-station-compact") >= 0;
            motion = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-station-motion") >= 0;
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
                    EditorWindow window = compact ?
                        ScriptableObject.CreateInstance(gameView) as EditorWindow :
                        EditorWindow.GetWindow(gameView);
                    if (compact)
                    { window.Show(); window.position = new Rect(80, 75, 1180, 700); }
                    else window.maximized = true;
                    window.Focus();
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
                ScreenCapture.CaptureScreenshot(CaptureName("hangar"));
                stage = 2; captureAt = EditorApplication.timeSinceStartup + 1; return;
            }
            if (stage == 2)
            {
                if (!File.Exists(CaptureName("hangar")))
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
                ScreenCapture.CaptureScreenshot(CaptureName("concourse"));
                stage = 5; captureAt = EditorApplication.timeSinceStartup + 1; return;
            }
            if (stage == 5)
            {
                if (!File.Exists(CaptureName("concourse")))
                { Fail("Missing concourse capture."); return; }
                if (motion)
                {
                    var contact = client.CurrentSnapshot.Station.Contacts.FirstOrDefault(entry =>
                        entry.Room == client.CurrentSnapshot.Station.CurrentRoom);
                    if (contact == null) { Fail("No contact in the captured room."); return; }
                    contactId = contact.Id;
                    client.OpenNpc(contactId);
                    stage = 9; return;
                }
                client.EnterStationRoom(0);
                stage = 6; return;
            }
            if (stage == 9)
            {
                if (client.CurrentSnapshot.Dialogue?.NpcId != contactId) return;
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 10; return;
            }
            if (stage == 10)
            {
                ScreenCapture.CaptureScreenshot(CaptureName("dialogue"));
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 11; return;
            }
            if (stage == 11)
            {
                if (!File.Exists(CaptureName("dialogue")))
                { Fail("Missing contact dialogue capture."); return; }
                client.ChooseNpc(230);
                stage = 12; return;
            }
            if (stage == 12)
            {
                if (client.CurrentSnapshot.Dialogue != null) return;
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
                ScreenCapture.CaptureScreenshot(CaptureName("loadout"));
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 8; return;
            }
            if (!File.Exists(CaptureName("loadout")))
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
