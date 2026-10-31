using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Exercise the retained group Assist controls against two disposable pilots.</summary>
    [InitializeOnLoad]
    public static class GroupAssistVisualSmoke
    {
        private static int stage;
        private static double deadline, nextAt;
        private static bool connected, waitingForEffects;
        private static ClientWorker peer;
        private static UiViewportScope viewport;
        private static uint firstTarget, secondTarget;
        private static int previousCollapse, previousShortcut, originalShortcutCount;
        private static bool addShortcutFirst;
        private static readonly Dictionary<string, int?> savedShortcutPrefs = new Dictionary<string, int?>();
        private static int[] shortcutFixture;
        private static float previousStatus, previousSpeed, beforeStatus, beforeSpeed, beforeSystems;

        /// <summary>Register only for the explicit group Assist graphical smoke.</summary>
        static GroupAssistVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-group-assist-smoke") < 0) return;
            previousStatus = PlayerPrefs.GetFloat("42andbeyond.strip.status", -1);
            previousSpeed = PlayerPrefs.GetFloat("42andbeyond.strip.speed", -1);
            PlayerPrefs.DeleteKey("42andbeyond.strip.status"); PlayerPrefs.DeleteKey("42andbeyond.strip.speed");
            previousCollapse = PlayerPrefs.GetInt("42andbeyond.collapse.overview", 0);
            PlayerPrefs.SetInt("42andbeyond.collapse.overview", 1);
            deadline = EditorApplication.timeSinceStartup + 110;
            EditorApplication.update += Tick;
        }

        /// <summary>Check group layout, click both Assist controls, and compare authoritative targets.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline) { Fail("Group Assist timed out at stage " + stage); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameView != null)
                { EditorWindow window = EditorWindow.GetWindow(gameView); window.maximized = true; window.Focus(); }
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
            if (stage == 0)
            {
                for (int index = -1; index < 24; index++)
                {
                    string key = index < 0 ? $"42andBeyond.ability.count.{own.PlayerId}" : $"42andBeyond.ability.{own.PlayerId}.{index}";
                    savedShortcutPrefs[key] = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetInt(key) : (int?)null;
                }
                client.ActivateAbility(0); client.InvitePlayer(other.PlayerId); stage = 1; return;
            }
            if (stage == 1 && other.Confirmation?.Kind == ConfirmationKind.Group)
            { peer.Submit(new ClientCommand(CommandKind.AnswerConfirmation, 1)); stage = 2; return; }
            if (stage == 2 && own.Group.Members.Any(member => member.Id == other.PlayerId))
            {
                client.SendChat("group-color-probe", true);
                client.SendChat("sector-color-probe");
                client.SendChat("/tell Alice private-color-probe <color=red>literal</color>");
                viewport = new UiViewportScope(); viewport.Set(1280, 720);
                uint[] destinations = other.Objects.Where(item => item.Navigable).Select(item => item.Id).Distinct().Take(2).ToArray();
                if (destinations.Length < 2) { Fail("Two test destinations are required."); return; }
                firstTarget = destinations[0]; secondTarget = destinations[1];
                peer.Submit(new ClientCommand(CommandKind.SelectTarget, firstTarget));
                nextAt = EditorApplication.timeSinceStartup + 1;
                stage = 3; return;
            }
            if (stage == 3 && other.SelectedTargetId == firstTarget &&
                EditorApplication.timeSinceStartup >= nextAt)
            {
                VisualElement root = DeckRoot();
                VisualElement browse = root.Q("panel-browse"), comms = root.Q("panel-communications");
                VisualElement target = root.Q("panel-target"), strip = root.Q("flight-strip");
                if (browse == null || comms == null || target == null || strip == null ||
                    browse.worldBound.Overlaps(comms.worldBound) || comms.worldBound.Overlaps(target.worldBound) ||
                    strip.worldBound.yMin < browse.worldBound.yMax + 30 ||
                    root.Q("panel-crew").resolvedStyle.display == DisplayStyle.None ||
                    root.Q("content-overview").resolvedStyle.display == DisplayStyle.None)
                { Fail("1280 flight surfaces overlap or GROUP tab is missing: " + browse?.worldBound +
                    " / " + comms?.worldBound + " / " + target?.worldBound + " / " + strip?.worldBound); return; }
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/streamlined-flight-1280.png");
                Click(root.Q<Button>("browse-tab-1"));
                nextAt = EditorApplication.timeSinceStartup + .2; stage = 31; return;
            }
            if (stage == 31 && EditorApplication.timeSinceStartup >= nextAt)
            {
                VisualElement root = DeckRoot();
                if (root.Q("panel-destinations").resolvedStyle.display == DisplayStyle.None)
                { Fail("Destination tab did not reveal server navigation choices."); return; }
                Click(root.Q<Button>("browse-tab-2"));
                viewport.Set(1693, 713);
                nextAt = EditorApplication.timeSinceStartup + .35; stage = 32; return;
            }
            if (stage == 32 && EditorApplication.timeSinceStartup >= nextAt)
            {
                VisualElement grip = DeckRoot().Q("communications-resize-grip");
                if (grip == null) { Fail("Communications resize grip missing."); return; }
                Resize(grip, new Vector2(35, 65));
                VisualElement root = DeckRoot();
                beforeStatus = root.Q("panel-integrity").resolvedStyle.width;
                beforeSpeed = root.Q("panel-propulsion").resolvedStyle.width;
                Resize(root.Q("flight-divider-0"), new Vector2(-20, 0));
                nextAt = EditorApplication.timeSinceStartup + .45; stage = 33; return;
            }
            if (stage == 33 && EditorApplication.timeSinceStartup >= nextAt)
            {
                VisualElement root = DeckRoot();
                float status = root.Q("panel-integrity").resolvedStyle.width;
                float speed = root.Q("panel-propulsion").resolvedStyle.width;
                if (Mathf.Abs(status - (beforeStatus - 20)) > 3 || Mathf.Abs(speed - (beforeSpeed + 20)) > 3)
                { Fail("First flight divider did not transfer width between status and propulsion: " + status + "/" + speed); return; }
                beforeSpeed = speed; beforeSystems = root.Q("shortcut-viewport").resolvedStyle.width;
                Resize(root.Q("flight-divider-1"), new Vector2(30, 0));
                nextAt = EditorApplication.timeSinceStartup + .45; stage = 34; return;
            }
            if (stage == 34 && EditorApplication.timeSinceStartup >= nextAt)
            {
                VisualElement root = DeckRoot();
                if (Mathf.Abs(root.Q("panel-propulsion").resolvedStyle.width - beforeSpeed - 30) > 3 ||
                    Mathf.Abs(root.Q("shortcut-viewport").resolvedStyle.width - beforeSystems + 30) > 3)
                { Fail("Second flight divider did not transfer width between propulsion and Systems."); return; }
                if (!own.ActiveBuffs.Any()) return; // Wait for the server's three-second cloak cast.
                if (!waitingForEffects)
                { waitingForEffects = true; nextAt = EditorApplication.timeSinceStartup + .4; return; }
                if (root.Q("flight-effects").resolvedStyle.display != DisplayStyle.None ||
                    !root.Q<Button>("flight-effects-badge").text.Contains("1"))
                { Fail("Active effects should be grouped in their count badge until hovered."); return; }
                using (MouseEnterEvent evt = MouseEnterEvent.GetPooled(new Event { type = EventType.MouseMove, mousePosition = root.Q("flight-effects-badge").worldBound.center })) { evt.target = root.Q("flight-effects-badge"); root.Q("flight-effects-badge").SendEvent(evt); }
                nextAt = EditorApplication.timeSinceStartup + .2; stage = 35; return;
            }
            if (stage == 35 && EditorApplication.timeSinceStartup >= nextAt)
            {
                VisualElement root = DeckRoot(), panel = root.Q("flight-effects");
                if (panel.resolvedStyle.display == DisplayStyle.None ||
                    !panel.Query<Label>().ToList().Any(label => label.text.Contains("Cloak")))
                { Fail("Hover did not reveal the authoritative cloak effect."); return; }
                ScreenCapture.CaptureScreenshot("Builds/verification/flight-effects-hover.png");
                using (MouseLeaveEvent evt = MouseLeaveEvent.GetPooled(new Event { type = EventType.MouseMove, mousePosition = Vector2.zero })) { evt.target = root.Q("flight-effects-badge"); root.Q("flight-effects-badge").SendEvent(evt); }
                using (MouseEnterEvent evt = MouseEnterEvent.GetPooled(new Event { type = EventType.MouseMove, mousePosition = root.Q("flight-effects-badge").worldBound.center })) { evt.target = panel; panel.SendEvent(evt); }
                nextAt = EditorApplication.timeSinceStartup + .3; stage = 36; return;
            }
            if (stage == 36 && EditorApplication.timeSinceStartup >= nextAt)
            {
                VisualElement root = DeckRoot();
                VisualElement panel = root.Q("flight-effects");
                if (panel.resolvedStyle.display == DisplayStyle.None)
                { Fail("Effects closed while pointer entered its scrollable popover."); return; }
                using (MouseLeaveEvent evt = MouseLeaveEvent.GetPooled(new Event { type = EventType.MouseMove, mousePosition = Vector2.zero })) { evt.target = panel; panel.SendEvent(evt); }
                nextAt = EditorApplication.timeSinceStartup + .35; stage = 37; return;
            }
            if (stage == 37 && EditorApplication.timeSinceStartup >= nextAt)
            {
                if (DeckRoot().Q("flight-effects").resolvedStyle.display != DisplayStyle.None)
                { Fail("Effects popover did not close after leaving."); return; }
                Click(DeckRoot().Q<Button>("flight-effects-badge"));
                nextAt = EditorApplication.timeSinceStartup + .2; stage = 38; return;
            }
            if (stage == 38 && EditorApplication.timeSinceStartup >= nextAt)
            {
                if (DeckRoot().Q("flight-effects").resolvedStyle.display == DisplayStyle.None)
                { Fail("Clicking Effects did not pin the list."); return; }
                Click(DeckRoot().Q<Button>("flight-effects-badge"));
                Debug.Log("PASS: both flight dividers resize; effects badge opens on hover, stays interactive, hides on leave and pins on click.");
                shortcutFixture = (int[])typeof(AbilityHud).GetField("slots", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(typeof(SpaceClient).GetField("abilityHud", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client));
                previousShortcut = shortcutFixture[0]; shortcutFixture[0] = 0;
                VisualElement slot = DeckRoot().Q("shortcut-0");
                using (MouseEnterEvent evt = MouseEnterEvent.GetPooled(new Event { type = EventType.MouseMove, mousePosition = slot.worldBound.center }))
                { evt.target = slot; slot.SendEvent(evt); }
                nextAt = EditorApplication.timeSinceStartup + .3; stage = 39; return;
            }
            if (stage == 39 && EditorApplication.timeSinceStartup >= nextAt)
            {
                VisualElement root = DeckRoot(), card = root.Q("systems-hover-overlay");
                string copy = string.Join(" ", card.Query<Label>().ToList().Select(label => label.text));
                if (card.resolvedStyle.display == DisplayStyle.None || !copy.Contains("Cloak") ||
                    !copy.Contains("Level") || !copy.Contains("Energy") || !copy.Contains("Key:"))
                { Fail("Systems shortcut runtime hover lacks skill description, rank, cost or binding: " + copy); return; }
                Label[] chat = root.Query<Label>(className: "chat-entry").ToList().ToArray();
                Label groupLine = chat.FirstOrDefault(line => line.text.Contains("group-color-probe"));
                Label sectorLine = chat.FirstOrDefault(line => line.text.Contains("sector-color-probe"));
                Label privateLine = chat.FirstOrDefault(line => line.text.Contains("private-color-probe"));
                if (groupLine == null || sectorLine == null || privateLine == null ||
                    groupLine.resolvedStyle.color == sectorLine.resolvedStyle.color ||
                    privateLine.resolvedStyle.color == groupLine.resolvedStyle.color || privateLine.enableRichText)
                { Fail("Chat channel colors did not distinguish actual group, sector and private messages safely."); return; }
                ScreenCapture.CaptureScreenshot("Builds/verification/systems-hover.png");
                nextAt = EditorApplication.timeSinceStartup + .5; stage = 391; return;
            }
            if (stage == 391 && EditorApplication.timeSinceStartup >= nextAt)
            {
                VisualElement root = DeckRoot();
                VisualElement slot = root.Q("shortcut-0");
                using (MouseLeaveEvent evt = MouseLeaveEvent.GetPooled(new Event { type = EventType.MouseMove, mousePosition = Vector2.zero }))
                { evt.target = slot; slot.SendEvent(evt); }
                shortcutFixture[0] = previousShortcut; shortcutFixture = null;
                Button add = root.Q<Button>("add-shortcut-compact"), remove = root.Q<Button>("remove-shortcut-compact");
                if (add == null || remove == null || add.resolvedStyle.display == DisplayStyle.None ||
                    !root.Q("flight-strip").worldBound.Contains(add.worldBound.center) ||
                    root.Q<ScrollView>("shortcut-viewport").Contains(add))
                { Fail("Compact Systems add/remove controls are missing, clipped or scroll away with the slots."); return; }
                originalShortcutCount = ReadShortcutCount(client); addShortcutFirst = originalShortcutCount < 24;
                Click(addShortcutFirst ? add : remove);
                nextAt = EditorApplication.timeSinceStartup + .25; stage = 392; return;
            }
            if (stage == 392 && EditorApplication.timeSinceStartup >= nextAt)
            {
                if (ReadShortcutCount(client) != originalShortcutCount + (addShortcutFirst ? 1 : -1))
                { Fail("Compact Systems control did not change the number of shortcuts."); return; }
                Click(DeckRoot().Q<Button>(addShortcutFirst ? "remove-shortcut-compact" : "add-shortcut-compact"));
                nextAt = EditorApplication.timeSinceStartup + .25; stage = 393; return;
            }
            if (stage == 393 && EditorApplication.timeSinceStartup >= nextAt)
            {
                if (ReadShortcutCount(client) != originalShortcutCount)
                { Fail("Compact Systems add/remove round trip did not restore slot count."); return; }
                Debug.Log("PASS: fixed compact Systems +/- controls add and remove actual shortcut slots.");
                nextAt = EditorApplication.timeSinceStartup + .2; stage = 4; return;
            }
            if (stage == 4 && EditorApplication.timeSinceStartup >= nextAt)
            {
                VisualElement root = DeckRoot();
                VisualElement group = root.Q("panel-crew"), comms = root.Q("panel-communications");
                if (comms.worldBound.height < 190 ||
                    group.resolvedStyle.display == DisplayStyle.None ||
                    !root.Q("panel-browse").worldBound.Contains(group.worldBound.center))
                { Fail("Joined GROUP tab hidden after resize: group=" + group.worldBound +
                    " browse=" + root.Q("panel-browse").worldBound + " comms=" + comms.worldBound); return; }
                int memberIndex = Array.FindIndex(own.Group.Members, member => member.Id == other.PlayerId);
                Button assist = root.Q<Button>("crew-assist-" + memberIndex);
                if (assist == null || !assist.enabledInHierarchy) { Fail("Member Assist is unavailable."); return; }
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/group-assist.png");
                Click(assist); stage = 5; return;
            }
            if (stage == 5 && own.SelectedTargetId == firstTarget && client.Target?.Id == firstTarget &&
                client.WarpTargetId == firstTarget)
            {
                client.SelectContact(other.PlayerId);
                peer.Submit(new ClientCommand(CommandKind.SelectTarget, secondTarget));
                nextAt = EditorApplication.timeSinceStartup + .4;
                stage = 6; return;
            }
            if (stage == 6 && EditorApplication.timeSinceStartup >= nextAt &&
                own.SelectedTargetId == other.PlayerId && other.SelectedTargetId == secondTarget)
            {
                Button assist = DeckRoot().Q<Button>("target-assist");
                VisualElement target = DeckRoot().Q("panel-target");
                if (assist == null || target == null || assist.resolvedStyle.display == DisplayStyle.None ||
                    !assist.enabledInHierarchy || target.resolvedStyle.visibility != Visibility.Visible ||
                    assist.worldBound.width < 100 || !DeckRoot().worldBound.Contains(assist.worldBound.center))
                { Fail("Selected pilot Assist is unavailable: target=" + target?.worldBound +
                    " assist=" + assist?.worldBound + " visible=" + target?.resolvedStyle.visibility); return; }
                ScreenCapture.CaptureScreenshot("Builds/verification/group-target-assist.png");
                Click(assist); stage = 7; return;
            }
            if (stage == 7 && own.SelectedTargetId == secondTarget && client.Target?.Id == secondTarget &&
                client.WarpTargetId == secondTarget)
            {
                if (!File.Exists("Builds/verification/group-assist.png") ||
                    !File.Exists("Builds/verification/group-target-assist.png")) return;
                Debug.Log("PASS: joined GROUP is visible; both Assist buttons acquire server-selected navigation targets.");
                Finish(0);
            }
        }

        /// Returns:
        ///     VisualElement: The active retained flight document root.
        private static VisualElement DeckRoot() =>
            GameObject.Find("Flight deck UI").GetComponent<UIDocument>().rootVisualElement;

        /// <summary>Dispatch actual pointer phases through a retained button.</summary>
        private static void Click(Button button)
        {
            Vector2 position = button.worldBound.center;
            var down = new Event { type = EventType.MouseDown, button = 0, mousePosition = position };
            var up = new Event { type = EventType.MouseUp, button = 0, mousePosition = position };
            using (PointerDownEvent evt = PointerDownEvent.GetPooled(down)) button.SendEvent(evt);
            using (PointerUpEvent evt = PointerUpEvent.GetPooled(up)) button.SendEvent(evt);
        }

        /// <summary>Drag the visible communications resize grip in both axes.</summary>
        private static void Resize(VisualElement grip, Vector2 delta)
        {
            Vector2 start = grip.worldBound.center;
            var down = new Event { type = EventType.MouseDown, button = 0, mousePosition = start };
            var move = new Event { type = EventType.MouseDrag, button = 0, mousePosition = start + delta };
            var up = new Event { type = EventType.MouseUp, button = 0, mousePosition = start + delta };
            using (PointerDownEvent evt = PointerDownEvent.GetPooled(down)) grip.SendEvent(evt);
            using (PointerMoveEvent evt = PointerMoveEvent.GetPooled(move)) grip.SendEvent(evt);
            using (PointerUpEvent evt = PointerUpEvent.GetPooled(up)) grip.SendEvent(evt);
        }

        /// Returns:
        ///     int: The actual shortcut count maintained by this owned test client's ability HUD.
        private static int ReadShortcutCount(SpaceClient client)
        {
            object ability = typeof(SpaceClient).GetField("abilityHud", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
            return (int)typeof(AbilityHud).GetField("slotCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ability);
        }

        /// <summary>Restore the user's collapse preference and stop only owned test workers.</summary>
        private static void Finish(int code)
        {
            EditorApplication.update -= Tick;
            if (shortcutFixture != null) shortcutFixture[0] = previousShortcut;
            foreach (var preference in savedShortcutPrefs)
                if (preference.Value.HasValue) PlayerPrefs.SetInt(preference.Key, preference.Value.Value);
                else PlayerPrefs.DeleteKey(preference.Key);
            PlayerPrefs.SetInt("42andbeyond.collapse.overview", previousCollapse);
            if (previousStatus < 0) PlayerPrefs.DeleteKey("42andbeyond.strip.status");
            else PlayerPrefs.SetFloat("42andbeyond.strip.status", previousStatus);
            if (previousSpeed < 0) PlayerPrefs.DeleteKey("42andbeyond.strip.speed");
            else PlayerPrefs.SetFloat("42andbeyond.strip.speed", previousSpeed);
            PlayerPrefs.Save();
            viewport?.Dispose(); viewport = null;
            peer?.Dispose(); peer = null;
            EditorApplication.Exit(code);
        }

        /// <summary>Report a concrete disposable graphical or protocol failure.</summary>
        private static void Fail(string reason) { Debug.LogError(reason); Finish(1); }
    }
}
