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
    /// <summary>Capture a server-selected crew support cast on disposable pilots.</summary>
    [InitializeOnLoad]
    public static class SupportAbilityVisualSmoke
    {
        private static int stage;
        private static double deadline;
        private static bool connected;
        private static ClientWorker peer;

        /// <summary>Register only for the owned two-pilot ability smoke flag.</summary>
        static SupportAbilityVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-support-ability-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 100;
            EditorApplication.update += Tick;
        }

        /// <summary>Invite, select, cast, and capture authoritative support state.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline) { Fail("Support ability smoke timed out at stage " + stage); return; }
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
            if (peer == null) peer = new ClientWorker("127.0.0.1", "peer", "pass");
            if (peer.Error != null) { Fail(peer.Error); return; }
            Snapshot other = peer.Latest;
            if (other?.Ready != true || !other.HasPosition ||
                !own.Players.Any(actor => actor.Id == other.PlayerId && actor.HasPosition)) return;
            if (stage == 0) { client.InvitePlayer(other.PlayerId); stage = 1; return; }
            if (stage == 1 && other.Confirmation?.Kind == ConfirmationKind.Group)
            { peer.Submit(new ClientCommand(CommandKind.AnswerConfirmation, 1)); stage = 2; return; }
            if (stage == 2 && own.Group.Members.Any(member => member.Id == other.PlayerId))
            { client.SelectContact(other.PlayerId); stage = 3; return; }
            if (stage == 3 && own.SelectedTargetId == other.PlayerId)
            {
                FieldInfo field = typeof(SpaceClient).GetField("abilityHud", BindingFlags.Instance | BindingFlags.NonPublic);
                AbilityHud hud = field?.GetValue(client) as AbilityHud;
                if (hud == null) { Fail("Ability HUD was unavailable."); return; }
                typeof(AbilityHud).GetField("open", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(hud, true);
                client.ActivateAbility(44);
                stage = 4; return;
            }
            if (stage == 4 && other.ObjectEffects.Any(effect =>
                effect.OwnerId == other.PlayerId && effect.Descriptor == 216))
            {
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/support-ability.png");
                Debug.Log("PASS: selected crew Environment Barrier and recipient native effect.");
                stage = 5;
            }
            if (stage == 5 && File.Exists("Builds/verification/support-ability.png")) Finish(0);
        }

        /// <summary>Stop the owned peer worker and Editor after this smoke.</summary>
        private static void Finish(int code)
        { EditorApplication.update -= Tick; peer?.Dispose(); peer = null; EditorApplication.Exit(code); }

        /// <summary>Report the failing stage before owned cleanup.</summary>
        private static void Fail(string reason)
        { Debug.LogError(reason); Finish(1); }
    }
}
