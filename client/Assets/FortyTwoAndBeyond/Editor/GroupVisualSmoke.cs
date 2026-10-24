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
    /// <summary>Capture a real two-pilot group marker and server-driven motion wake.</summary>
    [InitializeOnLoad]
    public static class GroupVisualSmoke
    {
        private static int stage;
        private static uint wingId;
        private static double deadline, captureAt;
        private static bool connected;

        /// <summary>Register only for the owned multiplayer graphical smoke.</summary>
        static GroupVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-group-visual-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 100;
            EditorApplication.update += Tick;
        }

        /// <summary>Invite a visible peer, wait for native group state, and capture its marker.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Group visual smoke timed out at stage " + stage); return; }
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
            if (!connected) { connected = true; client.StartLocalSession("127.0.0.1", "alice", "example"); }
            if (client.ChoosingCharacter) client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || !snapshot.HasPosition) return;
            Directory.CreateDirectory("Builds/verification");
            if (stage == 0)
            {
                ShipActor wing = snapshot.Players.FirstOrDefault(actor => actor.Name == "Bob" && actor.HasPosition);
                if (wing == null) return;
                wingId = wing.Id;
                client.InvitePlayer(wingId);
                stage = 1;
                return;
            }
            if (stage == 1)
            {
                if (!snapshot.Group.Members.Any(member => member.Id == wingId)) return;
                captureAt = EditorApplication.timeSinceStartup + 1.2;
                stage = 2;
                return;
            }
            if (stage == 2)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                ScreenCapture.CaptureScreenshot("Builds/verification/group-crew.png");
                client.SelectContact(wingId);
                captureAt = EditorApplication.timeSinceStartup + .8;
                stage = 3;
                return;
            }
            if (stage == 3)
            {
                if (snapshot.SelectedTargetId != wingId ||
                    EditorApplication.timeSinceStartup < captureAt) return;
                ScreenCapture.CaptureScreenshot("Builds/verification/group-target.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 4;
                return;
            }
            if (stage != 4 || EditorApplication.timeSinceStartup < captureAt) return;
            if (!File.Exists("Builds/verification/group-crew.png") ||
                !File.Exists("Builds/verification/group-target.png"))
            { Fail("Missing group graphical capture"); return; }
            Debug.Log("PASS: real grouped peer marker and movement captured.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Exit the disposable Editor with a concrete check failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
