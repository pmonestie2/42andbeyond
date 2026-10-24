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
    /// <summary>Exercise a Unity wing ship under a disposable leader's impulse and warp.</summary>
    [InitializeOnLoad]
    public static class FormationVisualSmoke
    {
        private static ClientWorker remote;
        private static int stage;
        private static double deadline;
        private static Position wingStart, wingWarpStart, leaderStart;
        private static bool connected, unityLeads;

        /// <summary>Register only when the disposable formation smoke is requested.</summary>
        static FormationVisualSmoke()
        {
            string[] args = Environment.GetCommandLineArgs();
            unityLeads = Array.IndexOf(args, "--42andbeyond-formation-leader-visual-smoke") >= 0;
            if (!unityLeads && Array.IndexOf(args, "--42andbeyond-formation-visual-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 120;
            EditorApplication.update += Tick;
        }

        /// <summary>Advance two worker-owned clients and capture the follower's travel.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Formation visual smoke timed out at stage " + stage); return; }
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
            SpaceClient local = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (local == null) return;
            if (!connected)
            {
                connected = true;
                remote = new ClientWorker("127.0.0.1", unityLeads ? "bob" : "alice", "example");
                local.StartLocalSession("127.0.0.1", unityLeads ? "alice" : "bob", "example");
            }
            if (local.ChoosingCharacter) local.ChooseCharacter(local.Characters[0].Slot);
            if (remote.Error != null || local.ConnectionError != null)
            { Fail(remote.Error ?? local.ConnectionError); return; }
            Snapshot localSnapshot = local.CurrentSnapshot, remoteSnapshot = remote.Latest;
            Snapshot captain = unityLeads ? localSnapshot : remoteSnapshot;
            Snapshot follower = unityLeads ? remoteSnapshot : localSnapshot;
            if (captain?.Ready != true || follower?.Ready != true ||
                !captain.HasPosition || !follower.HasPosition) return;
            if (stage == 0)
            {
                if (!captain.Players.Any(actor => actor.Id == follower.PlayerId) ||
                    !follower.Players.Any(actor => actor.Id == captain.PlayerId)) return;
                if (unityLeads) local.InvitePlayer(follower.PlayerId);
                else remote.Submit(new ClientCommand(CommandKind.InvitePlayer, follower.PlayerId));
                stage = 1;
            }
            else if (stage == 1 && follower.Confirmation != null)
            {
                if (unityLeads) remote.Submit(new ClientCommand(CommandKind.AnswerConfirmation, 1));
                else local.AnswerConfirmation(true);
                stage = 2;
            }
            else if (stage == 2 && captain.Group.Members.Any(member => member.Id == follower.PlayerId))
            {
                if (unityLeads) local.Formation(5);
                else remote.Submit(new ClientCommand(CommandKind.Formation, 5));
                stage = 3;
            }
            else if (stage == 3 && follower.Group.Formation == "Block")
            {
                if (unityLeads) remote.Submit(new ClientCommand(CommandKind.Formation, 7));
                else local.Formation(7);
                stage = 4;
            }
            else if (stage == 4 && follower.Group.Formed && follower.Formation?.LeaderId == captain.PlayerId)
            {
                Directory.CreateDirectory("Builds/verification");
                leaderStart = captain.Position; wingStart = follower.Position;
                if (unityLeads) local.Thrust(2);
                else remote.Submit(new ClientCommand(CommandKind.Thrust, 2));
                stage = 5;
            }
            else if (stage == 5 && captain.Position.Distance(leaderStart) > 50 &&
                follower.Position.Distance(wingStart) > 50 && follower.Group.Formed)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/formation-" +
                    (unityLeads ? "leader" : "wing") + "-impulse.png");
                if (unityLeads) local.Thrust(4);
                else remote.Submit(new ClientCommand(CommandKind.Thrust, 4));
                wingWarpStart = follower.Position;
                if (unityLeads) { local.SelectFreeFlight(); local.WarpSelected(); }
                else remote.Submit(new ClientCommand(CommandKind.Warp, 0));
                stage = 6;
            }
            else if (stage == 6 && captain.WarpPhase == WarpPhase.Traveling &&
                follower.Position.Distance(wingWarpStart) > 1000 && follower.Group.Formed &&
                follower.Formation?.LeaderId == captain.PlayerId &&
                localSnapshot.Players.Any(actor => actor.Id == remoteSnapshot.PlayerId &&
                    actor.HasPosition && actor.Position.Distance(localSnapshot.Position) < 500))
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/formation-" +
                    (unityLeads ? "leader" : "wing") + "-warp.png");
                Debug.Log($"PASS: Unity {(unityLeads ? "leader" : "wing")} formed and traveled; leader x={captain.Position.X:0}, " +
                    $"wing x={follower.Position.X:0}, anchor={follower.Formation.LeaderId}.");
                stage = 7;
            }
            else if (stage == 7 &&
                File.Exists("Builds/verification/formation-" + (unityLeads ? "leader" : "wing") + "-impulse.png") &&
                File.Exists("Builds/verification/formation-" + (unityLeads ? "leader" : "wing") + "-warp.png"))
                Finish(0);
        }

        /// <summary>Stop the test-owned captain worker and exit the disposable Editor.</summary>
        private static void Finish(int code)
        {
            EditorApplication.update -= Tick;
            remote?.Dispose(); remote = null;
            EditorApplication.Exit(code);
        }

        /// <summary>Exit with the failing stage while cleaning up the owned worker.</summary>
        private static void Fail(string reason)
        { Debug.LogError(reason); Finish(1); }
    }
}
