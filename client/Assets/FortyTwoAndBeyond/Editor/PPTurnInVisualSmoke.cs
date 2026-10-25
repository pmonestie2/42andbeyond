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
    /// <summary>Capture the native PP mission return and durable station reward.</summary>
    [InitializeOnLoad]
    public static class PPTurnInVisualSmoke
    {
        private static bool connected, chosen, roomRequested, opened, captured;
        private static int stage;
        private static ulong initialCredits;
        private static double deadline, captureAt;

        /// <summary>Register only for the explicit disposable station turn-in flag.</summary>
        static PPTurnInVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-pp-turnin-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Open Crones Christopher's offered branch and observe the server reward.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("PP mission return capture timed out."); return; }
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
            if (!connected)
            { connected = true; client.StartLocalSession("127.0.0.1", "rookiepp", "rookiepp"); }
            if (!chosen && client.ChoosingCharacter)
            { chosen = true; client.ChooseCharacter(client.Characters[0].Slot); }
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || snapshot.Station?.Id != 10371) return;
            if (stage == 0)
            {
                MissionState mission = snapshot.Missions.FirstOrDefault(item =>
                    item?.Name == "Waste Disposal");
                if (mission?.Stage != 2) return;
                StationContact contact = snapshot.Station.Contacts.FirstOrDefault(item => item.Id == 371);
                if (contact == null) { Fail("Crones Christopher is absent from Arx Bursa."); return; }
                if (snapshot.Station.CurrentRoom != contact.Room)
                { if (!roomRequested) { roomRequested = true; client.EnterStationRoom(contact.Room); }
                  return; }
                if (!opened) { opened = true; initialCredits = snapshot.Credits; client.OpenNpc(371); }
                if (snapshot.Dialogue?.Choices.Any(choice => choice.Node == 2) != true) return;
                if (!Capture("pp-turnin-before.png")) return;
                client.ChooseNpc(2); stage = 1; captured = false; return;
            }
            if (snapshot.Missions.Any(item => item?.Name == "Waste Disposal") ||
                snapshot.Credits < initialCredits + 200) return;
            if (!Capture("pp-turnin-after.png")) return;
            Debug.Log("PASS: native PP mission turn-in cleared journal and added 200 credits.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <returns>bool: Whether Unity finished saving this turn-in screenshot.</returns>
        private static bool Capture(string name)
        {
            string path = "Builds/verification/" + name;
            if (!captured)
            { Directory.CreateDirectory("Builds/verification");
              ScreenCapture.CaptureScreenshot(path); captured = true;
              captureAt = EditorApplication.timeSinceStartup + .6; return false; }
            return File.Exists(path) && EditorApplication.timeSinceStartup >= captureAt;
        }

        /// <summary>Exit the owned Editor on a concrete disposable failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
