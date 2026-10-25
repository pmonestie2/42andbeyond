using System;
using System.IO;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using CharacterRecord = FortyTwoAndBeyond.Protocol.CharacterInfo;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Capture a fresh pilot's server-owned class and track levels in the picker.</summary>
    [InitializeOnLoad]
    public static class CharacterRosterVisualSmoke
    {
        private static bool connected, captured;
        private static int stage;
        private static double deadline, captureAt;

        /// <summary>Register only for the explicit disposable roster smoke flag.</summary>
        static CharacterRosterVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-roster-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 60;
            EditorApplication.update += Tick;
        }

        /// <summary>Wait for the native character list and save its visible picker card.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Native-progression roster capture timed out."); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameView != null)
                { EditorWindow window = EditorWindow.GetWindow(gameView);
                  window.maximized = true; window.Focus(); }
                EditorApplication.EnterPlaymode(); return;
            }
            if (!EditorApplication.isPlaying) return;
            SpaceClient client = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            if (!connected) { connected = true; client.StartLocalSession("127.0.0.1", "rookie", "rookie"); }
            if (client.LoginError != null) { Fail(client.LoginError); return; }
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            if (stage == 0)
            {
                if (!client.ChoosingCharacter) return;
                CharacterRecord[] pilots = client.Characters;
                if (pilots.Length != 1 || pilots[0].ClassName != "Terran Scout" ||
                    pilots[0].CombatLevel != 0 || pilots[0].TradeLevel != 0 ||
                    pilots[0].ExploreLevel != 0 || pilots[0].SectorId != 10201 ||
                    pilots[0].SectorName != "Loki Station")
                { Fail("Native roster did not expose the level-zero Scout at Loki Station."); return; }
                if (!Capture("new-pilot-roster.png")) return;
                client.ChooseCharacter(pilots[0].Slot); stage = 1; captured = false; return;
            }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true) return;
            if (stage == 1)
            {
                if (snapshot.Station == null) return;
                if (snapshot.Station.Id != 10201)
                { Fail("Scout did not enter Loki Station."); return; }
                if (!Capture("new-pilot-station.png")) return;
                client.Launch(); stage = 2; captured = false; return;
            }
            if (stage == 2 && snapshot.Station == null && snapshot.HasPosition)
            {
                if (!Capture("new-pilot-space.png")) return;
                Debug.Log("PASS: native level-zero Scout picker, station, and launch captured.");
                EditorApplication.update -= Tick;
                EditorApplication.Exit(0);
            }
        }

        /// <returns>bool: Whether Unity finished saving a fresh screenshot for this stage.</returns>
        private static bool Capture(string name)
        {
            string path = "Builds/verification/" + name;
            if (!captured)
            {
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot(path); captured = true;
                captureAt = EditorApplication.timeSinceStartup + .6;
                return false;
            }
            return File.Exists(path) && EditorApplication.timeSinceStartup >= captureAt;
        }

        /// <summary>Exit the owned Editor after a concrete disposable failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
