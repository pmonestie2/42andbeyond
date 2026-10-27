using System;
using System.IO;
using System.Linq;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Accept a real peer invitation through the retained popup on a disposable server.</summary>
    [InitializeOnLoad]
    public static class InviteVisualSmoke
    {
        private static ClientWorker peer;
        private static int stage;
        private static bool connected;
        private static double deadline, captureAt;

        /// <summary>Register only for the explicit disposable invitation check.</summary>
        static InviteVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-invite-smoke") < 0) return;
            deadline = EditorApplication.timeSinceStartup + 100;
            EditorApplication.update += Tick;
        }

        /// <summary>Invite the displayed pilot and click the same Accept button used during play.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Finish(false, "Invite check timed out at stage " + stage); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")).maximized = true;
                EditorApplication.EnterPlaymode(); return;
            }
            if (!EditorApplication.isPlaying) return;
            SpaceClient client = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            if (!connected) { connected = true; client.StartLocalSession("127.0.0.1", "local", "local"); }
            if (client.ChoosingCharacter) client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Finish(false, client.ConnectionError); return; }
            Snapshot s = client.CurrentSnapshot;
            if (s?.Ready != true) return;
            if (peer == null) peer = new ClientWorker("127.0.0.1", "peer", "pass");
            if (peer.Error != null) { Finish(false, peer.Error); return; }
            VisualElement root = GameObject.Find("Flight deck UI")?.GetComponent<UIDocument>().rootVisualElement;
            if (stage == 0)
            {
                if (peer.Latest?.Ready != true || !peer.Latest.Players.Any(p => p.Id == s.PlayerId)) return;
                client.OpenChatEntry();
                captureAt = EditorApplication.timeSinceStartup + .5;
                stage = 10; return;
            }
            if (stage == 10)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                TextField entry = root?.Q<TextField>("chat-entry");
                if (entry == null || entry.worldBound.width <= 0 || entry.parent.resolvedStyle.display == DisplayStyle.None)
                { Finish(false, "Chat composer was not visible."); return; }
                entry.value = "Hello from the Comms panel";
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/chat-readable.png");
                captureAt = EditorApplication.timeSinceStartup + .5;
                stage = 12; return;
            }
            if (stage == 12)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                TextField entry = root.Q<TextField>("chat-entry");
                var input = new Event { type = EventType.KeyDown, keyCode = KeyCode.Return };
                using (KeyDownEvent evt = KeyDownEvent.GetPooled(input)) entry.SendEvent(evt);
                stage = 11; return;
            }
            if (stage == 11)
            {
                if (!peer.Latest.Messages.Any(message => message.Contains("Hello from the Comms panel"))) return;
                Debug.Log("PASS: retained chat input sent a sector message with Enter to the other pilot.");
                client.OpenChatEntry();
                captureAt = EditorApplication.timeSinceStartup + .4;
                stage = 13; return;
            }
            if (stage == 13)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                TextField entry = root.Q<TextField>("chat-entry");
                entry.value = "unfinished draft";
                var older = new Event { type = EventType.KeyDown, keyCode = KeyCode.UpArrow };
                using (KeyDownEvent evt = KeyDownEvent.GetPooled(older)) entry.SendEvent(evt);
                if (entry.value != "Hello from the Comms panel")
                { Finish(false, "Up did not recall the previous sent line."); return; }
                var newer = new Event { type = EventType.KeyDown, keyCode = KeyCode.DownArrow };
                using (KeyDownEvent evt = KeyDownEvent.GetPooled(newer)) entry.SendEvent(evt);
                if (entry.value != "unfinished draft")
                { Finish(false, "Down did not restore the unsent draft."); return; }
                client.CloseChatEntry();
                Debug.Log("PASS: chat Up/Down recall preserves the unsent draft.");
                peer.Submit(new ClientCommand(CommandKind.InvitePlayer, s.PlayerId));
                stage = 1; return;
            }
            if (stage == 1)
            {
                if (s.Confirmation?.Kind != ConfirmationKind.Group) return;
                Button accept = root?.Q<Button>("accept-group-invite");
                if (accept == null || accept.worldBound.width <= 0 ||
                    accept.parent.parent.parent.resolvedStyle.display == DisplayStyle.None) return;
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/group-invite.png");
                captureAt = EditorApplication.timeSinceStartup + .4; stage = 2; return;
            }
            if (stage == 2)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                Button accept = root.Q<Button>("accept-group-invite");
                var input = new Event { type = EventType.MouseDown, button = 0, mousePosition = accept.worldBound.center };
                using (PointerDownEvent evt = PointerDownEvent.GetPooled(input)) accept.SendEvent(evt);
                input.type = EventType.MouseUp;
                using (PointerUpEvent evt = PointerUpEvent.GetPooled(input)) accept.SendEvent(evt);
                stage = 3; return;
            }
            if (stage == 3 && s.Group.Joined && s.Group.Members.Any(m => m.Id == peer.Latest.PlayerId))
                Finish(true, "PASS: native peer invitation accepted through retained popup.");
        }

        /// <summary>Join the owned peer worker and close only this check's editor.</summary>
        private static void Finish(bool passed, string message)
        {
            peer?.Dispose(); peer = null;
            if (passed) Debug.Log(message); else Debug.LogError(message);
            EditorApplication.update -= Tick; EditorApplication.Exit(passed ? 0 : 1);
        }
    }
}
