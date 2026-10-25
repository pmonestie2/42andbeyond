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
    /// <summary>Capture a native mission conversation on a selected space navpoint.</summary>
    [InitializeOnLoad]
    public static class SpaceMissionVisualSmoke
    {
        private static bool connected, chosen, selected, captured;
        private static int stage;
        private static double deadline, captureAt;

        /// <summary>Register only for an explicit disposable PP mission smoke.</summary>
        static SpaceMissionVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-space-mission-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Select Waste Disposal Station, answer its branch, and capture both states.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Native space mission capture timed out."); return; }
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
            if (snapshot?.Ready != true || snapshot.Station != null) return;
            MissionState mission = snapshot.Missions.FirstOrDefault(item => item?.Name == "Waste Disposal");
            if (mission == null) return;
            if (!selected)
            {
                if (mission.Stage != 1 || !snapshot.Objects.Any(item => item.Id == 103059)) return;
                selected = true; client.Select(103059); return;
            }
            if (stage == 0)
            {
                if (snapshot.Dialogue?.Choices.Any(choice => choice.Node == 2) != true) return;
                if (!Capture("space-mission-dialogue.png")) return;
                client.ChooseNpc(2); stage = 1; captured = false; return;
            }
            if (stage == 1 && mission.Stage == 2)
            {
                if (!Capture("space-mission-objective.png")) return;
                Debug.Log("PASS: native PP space dialogue and durable mission stage two captured.");
                EditorApplication.update -= Tick;
                EditorApplication.Exit(0);
            }
        }

        /// <returns>bool: Whether Unity finished saving this stage's screenshot.</returns>
        private static bool Capture(string name)
        {
            string path = "Builds/verification/" + name;
            if (!captured)
            { Directory.CreateDirectory("Builds/verification");
              ScreenCapture.CaptureScreenshot(path); captured = true;
              captureAt = EditorApplication.timeSinceStartup + .6; return false; }
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
