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
    /// <summary>Capture a recipient's native Scan Boost buff from two disposable pilots.</summary>
    [InitializeOnLoad]
    public static class PeerScanVisualSmoke
    {
        private static int stage;
        private static double deadline;
        private static bool connected;
        private static ClientWorker sender;

        /// <summary>Register only for the explicit owned Scan Boost visual check.</summary>
        static PeerScanVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-peer-scan-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 100;
            EditorApplication.update += Tick;
        }

        /// <summary>Invite, target, activate, and capture the recipient's server buff.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Peer Scan Boost smoke timed out at stage " + stage); return; }
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
            Snapshot own = client.CurrentSnapshot;
            if (own?.Ready != true || !own.HasPosition) return;
            if (sender == null) sender = new ClientWorker("127.0.0.1", "peer", "pass");
            if (sender.Error != null) { Fail(sender.Error); return; }
            Snapshot other = sender.Latest;
            if (other?.Ready != true || !other.HasPosition ||
                !own.Players.Any(actor => actor.Id == other.PlayerId && actor.HasPosition)) return;
            if (other.Mounted[12]?.ActivationLabel != "Scan Boost" ||
                other.Mounted[12]?.TargetsPeer != true)
            { Fail("Disposable sender has no peer-targeted Scan Boost device in slot 12."); return; }
            if (stage == 0) { client.InvitePlayer(other.PlayerId); stage = 1; return; }
            if (stage == 1 && other.Confirmation?.Kind == ConfirmationKind.Group)
            { sender.Submit(new ClientCommand(CommandKind.AnswerConfirmation, 1)); stage = 2; return; }
            if (stage == 2 && own.Group.Members.Any(member => member.Id == other.PlayerId))
            { sender.Submit(new ClientCommand(CommandKind.SelectTarget, own.PlayerId)); stage = 3; return; }
            if (stage == 3 && other.SelectedTargetId == own.PlayerId)
            { sender.Submit(new ClientCommand(CommandKind.ActivateDevice, 12)); stage = 4; return; }
            if (stage == 4 && own.PeerBuff?.Name == "Increase Scan Range" &&
                own.PeerBuff.VisualActive && own.PeerBuff.VisualDescriptor == 462 &&
                own.PeerBuff.RemainingSeconds > 0)
            {
                Directory.CreateDirectory("Builds/verification");
                if (File.Exists("Builds/verification/peer-scan-buff.png"))
                    File.Delete("Builds/verification/peer-scan-buff.png");
                ScreenCapture.CaptureScreenshot("Builds/verification/peer-scan-buff.png");
                Debug.Log("PASS: recipient Scan Boost slot-one icon, timer, and ObjectEffect462 captured.");
                stage = 5;
            }
            if (stage == 5 && File.Exists("Builds/verification/peer-scan-buff.png")) Finish(0);
        }

        /// <summary>Join the owned sender worker and exit after the graphical check.</summary>
        private static void Finish(int code)
        { EditorApplication.update -= Tick; sender?.Dispose(); sender = null; EditorApplication.Exit(code); }

        /// <summary>Report a concrete failure before owned cleanup.</summary>
        private static void Fail(string reason)
        { Debug.LogError(reason); Finish(1); }
    }
}
