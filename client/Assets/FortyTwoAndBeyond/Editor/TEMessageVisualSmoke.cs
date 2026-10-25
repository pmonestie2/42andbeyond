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
    /// <summary>Capture the native TE mission105 space dialogue and completion.</summary>
    [InitializeOnLoad]
    public static class TEMessageVisualSmoke
    {
        private static bool connected, chosen, selected, captured;
        private static int stage;
        private static double deadline, captureAt;

        /// <summary>Register only for this explicit disposable mission capture.</summary>
        static TEMessageVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-te105-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Follow only server-advertised space dialogue branches.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("TE mission105 space dialogue timed out."); return; }
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
            { connected = true; client.StartLocalSession("127.0.0.1", "rookiete", "rookiete"); }
            if (!chosen && client.ChoosingCharacter)
            { chosen = true; client.ChooseCharacter(client.Characters[0].Slot); }
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || snapshot.Station != null ||
                !snapshot.Objects.Any(item => item.Id == 113451)) return;
            if (stage == 0)
            {
                if (!snapshot.Missions.Any(item => item?.Name == "Message For The First Sergeant" &&
                    item.Stage == 1) || !snapshot.Cargo.Any(item => item?.Template == 5226)) return;
                if (!selected)
                {
                    selected = true;
                    MapObject contact = snapshot.Objects.First(item => item.Id == 113451);
                    Debug.Log($"TE105 contact kind {contact.Kind}, navigable {contact.Navigable}, " +
                        $"range {contact.Position.Distance(snapshot.Position):0} m");
                    client.SelectWorldObject(113451);
                    Debug.Log($"TE105 selected target after request {client.Target?.Id}");
                }
                if (snapshot.Dialogue?.Choices.Any(choice => choice.Node == 2) != true) return;
                if (!Capture("te105-space-offer.png")) return;
                client.ChooseNpc(2); stage = 1; captured = false; return;
            }
            if (stage == 1)
            {
                if (snapshot.Dialogue?.Choices.Any(choice => choice.Node == 3) != true) return;
                if (!Capture("te105-space-reply.png")) return;
                client.ChooseNpc(3); stage = 2; captured = false; return;
            }
            if (snapshot.Missions.Any(item => item?.Name == "Message For The First Sergeant") ||
                snapshot.Cargo.Any(item => item?.Template == 5226) ||
                snapshot.Experience?.TradeLevel == 0 || snapshot.Experience.ExploreLevel == 0) return;
            if (!Capture("te105-space-complete.png")) return;
            Debug.Log("PASS: TE mission105 consumed message and granted trade/explore XP in space.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <returns>bool: Whether Unity finished saving the fresh screenshot.</returns>
        private static bool Capture(string name)
        {
            string path = "Builds/verification/" + name;
            if (!captured)
            { Directory.CreateDirectory("Builds/verification");
              if (File.Exists(path)) File.Delete(path);
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
