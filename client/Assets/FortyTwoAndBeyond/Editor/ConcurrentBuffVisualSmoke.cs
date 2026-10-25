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
    /// <summary>Capture three independent server-owned recipient buffs on one ship.</summary>
    [InitializeOnLoad]
    public static class ConcurrentBuffVisualSmoke
    {
        private static int stage;
        private static double deadline;
        private static bool connected;
        private static ClientWorker explorer, sentinel;

        /// <summary>Register only for the owned three-pilot buff fixture.</summary>
        static ConcurrentBuffVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                    "--42andbeyond-concurrent-buff-smoke") < 0) return;
            deadline = EditorApplication.timeSinceStartup + 150;
            EditorApplication.update += Tick;
        }

        /// <summary>Group pilots, activate distinct ally devices, and capture authoritative rows.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Concurrent buffs timed out at stage " + stage); return; }
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
            if (stage == 11)
            {
                if (File.Exists("Builds/verification/concurrent-buffs.png")) Finish(0);
                return;
            }
            Snapshot own = client.CurrentSnapshot;
            if (own?.Ready != true || !own.HasPosition) return;
            if (explorer == null) explorer = new ClientWorker("127.0.0.1", "peer", "peer");
            if (sentinel == null) sentinel = new ClientWorker("127.0.0.1", "shield", "shield");
            if (explorer.Error != null || sentinel.Error != null)
            { Fail(explorer.Error ?? sentinel.Error); return; }
            Snapshot first = explorer.Latest, second = sentinel.Latest;
            if (first?.Ready != true || second?.Ready != true ||
                !own.Players.Any(actor => actor.Id == first.PlayerId) ||
                !own.Players.Any(actor => actor.Id == second.PlayerId)) return;
            if (stage == 0)
            {
                if (first.Mounted[12]?.ActivationLabel != "Scan Boost" ||
                    first.Mounted[13]?.ActivationLabel != "Recharge Reactor" ||
                    first.Mounted[14]?.ActivationLabel != "Reduce Ship Signature" ||
                    second.Mounted[12]?.ActivationLabel != "Recharge Shields")
                { Fail("Disposable ally devices are missing or not legal in their mounts."); return; }
                client.InvitePlayer(first.PlayerId); stage = 1; return;
            }
            if (stage == 1 && first.Confirmation?.Kind == ConfirmationKind.Group)
            { explorer.Submit(new ClientCommand(CommandKind.AnswerConfirmation, 1)); stage = 2; return; }
            if (stage == 2 && own.Group.Members.Any(member => member.Id == first.PlayerId))
            { client.InvitePlayer(second.PlayerId); stage = 3; return; }
            if (stage == 3 && second.Confirmation?.Kind == ConfirmationKind.Group)
            { sentinel.Submit(new ClientCommand(CommandKind.AnswerConfirmation, 1)); stage = 4; return; }
            if (stage == 4 && own.Group.Members.Any(member => member.Id == second.PlayerId))
            { explorer.Submit(new ClientCommand(CommandKind.SelectTarget, own.PlayerId)); stage = 5; return; }
            if (stage == 5 && first.SelectedTargetId == own.PlayerId)
            { explorer.Submit(new ClientCommand(CommandKind.ActivateDevice, 12)); stage = 6; return; }
            if (stage == 6 && own.PeerBuff?.VisualActive == true)
            { sentinel.Submit(new ClientCommand(CommandKind.SelectTarget, own.PlayerId)); stage = 7; return; }
            if (stage == 7 && second.SelectedTargetId == own.PlayerId)
            { sentinel.Submit(new ClientCommand(CommandKind.ActivateDevice, 12)); stage = 8; return; }
            if (stage == 8 && own.ShieldBuff?.VisualActive == true)
            { explorer.Submit(new ClientCommand(CommandKind.ActivateDevice, 13)); stage = 9; return; }
            if (stage == 9 && own.ReactorBuff?.VisualActive == true)
            { explorer.Submit(new ClientCommand(CommandKind.ActivateDevice, 14)); stage = 10; return; }
            if (stage != 10 || own.SignatureBuff == null) return;
            if (own.PeerBuff?.VisualDescriptor != 462 ||
                own.ShieldBuff.VisualDescriptor != 553 ||
                own.ReactorBuff.VisualDescriptor != 553 ||
                own.ShieldBuff.VisualEffectId == own.ReactorBuff.VisualEffectId ||
                own.PeerBuff.RemainingSeconds <= 0 || own.ShieldBuff.RemainingSeconds <= 0 ||
                own.ReactorBuff.RemainingSeconds <= 0 ||
                own.SignatureBuff.Name != "Reduce Signature" ||
                own.SignatureBuff.VisualActive || own.SignatureBuff.RemainingSeconds <= 0)
            { Fail("Recipient visual IDs or authoritative buff timers are invalid."); return; }
            Directory.CreateDirectory("Builds/verification");
            string capture = "Builds/verification/concurrent-buffs.png";
            if (File.Exists(capture)) File.Delete(capture);
            ScreenCapture.CaptureScreenshot(capture);
            Debug.Log("PASS: four independent recipient buff slots and three visuals captured.");
            stage = 11;
        }

        /// <summary>Join owned network workers before Editor exit.</summary>
        private static void Finish(int code)
        {
            EditorApplication.update -= Tick;
            explorer?.Dispose(); sentinel?.Dispose();
            explorer = sentinel = null;
            EditorApplication.Exit(code);
        }

        /// <summary>Exit with a concrete disposable-fixture failure.</summary>
        private static void Fail(string reason)
        { Debug.LogError(reason); Finish(1); }
    }
}
