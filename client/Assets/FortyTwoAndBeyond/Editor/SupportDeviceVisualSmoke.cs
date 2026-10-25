using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Exercise the loadout recipient control with two disposable pilots.</summary>
    [InitializeOnLoad]
    public static class SupportDeviceVisualSmoke
    {
        private static double deadline;
        private static int stage;
        private static bool connected;
        private static ClientWorker peer;

        /// <summary>Register only when the owned support-device smoke flag is present.</summary>
        static SupportDeviceVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-support-device-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 100;
            EditorApplication.update += Tick;
        }

        /// <summary>Capture selection, then verify the native two-pilot transfer result.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline) { Fail("Support device smoke timed out at stage " + stage); return; }
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
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || !snapshot.HasPosition) return;
            if (peer == null) peer = new ClientWorker("127.0.0.1", "peer", "pass");
            if (peer.Error != null) { Fail(peer.Error); return; }
            Snapshot other = peer.Latest;
            if (other?.Ready != true || !other.HasPosition ||
                !snapshot.Players.Any(actor => actor.Id == other.PlayerId && actor.HasPosition)) return;
            if (snapshot.Mounted[14]?.ActivationLabel != "Transfer Reactor Energy" ||
                other.Mounted[13]?.ActivationLabel != "Turbo Weapons")
            { Fail("Disposable support loadout did not publish equipped item metadata."); return; }
            if (stage == 0)
            {
                peer.Submit(new ClientCommand(CommandKind.ActivateDevice, 13));
                stage = 1; return;
            }
            if (stage == 1)
            {
                if (other.ActiveDeviceLabel != "Turbo Weapons" || other.Energy >= .99f) return;
                FieldInfo field = typeof(SpaceClient).GetField("hud", BindingFlags.Instance | BindingFlags.NonPublic);
                FlightHud hud = field?.GetValue(client) as FlightHud;
                if (hud == null) { Fail("Flight HUD was unavailable."); return; }
                hud.ToggleEquipment();
                typeof(FlightHud).GetField("selectedMount", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(hud, 14);
                typeof(FlightHud).GetField("inspectMount", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(hud, true);
                client.SelectContact(other.PlayerId);
                stage = 2; return;
            }
            if (stage == 2)
            {
                if (snapshot.SelectedTargetId != other.PlayerId) return;
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/support-recipient.png");
                client.ActivateDevice(14);
                stage = 3; return;
            }
            if (stage == 3 && snapshot.Messages.Contains("Transfer Reactor Energy completed.") &&
                other.Messages.Contains("Reactor energy received from a nearby pilot."))
            {
                Debug.Log("PASS: loadout-selected peer reactor transfer from native 0x17/0x5d.");
                stage = 4;
            }
            if (stage == 4 && File.Exists("Builds/verification/support-recipient.png")) Finish(0);
        }

        /// <summary>Exit and join the owned peer worker after a successful check.</summary>
        private static void Finish(int code)
        {
            EditorApplication.update -= Tick;
            peer?.Dispose(); peer = null;
            EditorApplication.Exit(code);
        }

        /// <summary>Stop the disposable check with its concrete failure reason.</summary>
        private static void Fail(string reason)
        { Debug.LogError(reason); Finish(1); }
    }
}
