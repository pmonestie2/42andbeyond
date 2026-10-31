using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Verify equipped-item shortcuts through the actual retained pointer path on an owned server.</summary>
    [InitializeOnLoad]
    public static class ShortcutVisualSmoke
    {
        private static int stage;
        private static bool connected;
        private static double deadline, at;
        private static uint target;
        private static string inventory;
        private static int choiceId;
        private static DynamicSpacePreview dynamicSpace;
        private static UiLayoutRegression uiRegression;
        private static UiViewportDriver uiDriver;
        private static readonly Dictionary<string, int?> saved = new Dictionary<string, int?>();
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>Start only for an explicitly requested disposable shortcut check.</summary>
        static ShortcutVisualSmoke()
        {
            if (!Environment.GetCommandLineArgs().Contains("--42andbeyond-shortcuts-smoke")) return;
            deadline = EditorApplication.timeSinceStartup + 160;
            EditorApplication.update += Tick;
        }

        /// <summary>Assign from both fitting views, extend/persist the bar and fire the exact bound mount.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline) { Finish(false, "timeout at stage " + stage); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                EditorWindow window = EditorWindow.GetWindow(gameView); window.maximized = true; window.Focus();
                EditorApplication.EnterPlaymode(); return;
            }
            if (!EditorApplication.isPlaying) return;
            var client = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            if (!connected) { connected = true; client.StartLocalSession("127.0.0.1", "local", "local"); }
            if (client.ChoosingCharacter) client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Finish(false, client.ConnectionError); return; }
            if(uiRegression!=null) return;
            Snapshot state = client.CurrentSnapshot;
            if (state?.Ready != true || state.Mounted[3]?.Type != 15 || state.Ammo[3] == null) return;
            var root = GameObject.Find("Flight deck UI").GetComponent<UIDocument>().rootVisualElement;
            var hud = (FlightHud)typeof(SpaceClient).GetField("hud", Private).GetValue(client);
            var abilities = (AbilityHud)typeof(SpaceClient).GetField("abilityHud", Private).GetValue(client);
            double now = EditorApplication.timeSinceStartup;
            try
            {
                if (stage == 0)
                {
                    var keys = Enumerable.Range(0, 24).Select(index => $"42andBeyond.ability.{state.PlayerId}.{index}")
                        .Append($"42andBeyond.ability.count.{state.PlayerId}")
                        .Concat(new[]{"overview","destinations","target","integrity","propulsion","communications","fitting"}.Select(key=>"42andbeyond.collapse."+key));
                    foreach (string key in keys) { saved[key] = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetInt(key) : (int?)null; PlayerPrefs.DeleteKey(key); }
                    typeof(AbilityHud).GetField("slotOwner", Private).SetValue(abilities, 0u);
                    inventory = Fingerprint(state); CheckSystemPresentation(client, hud); ResourceDialChecks.Run(); HeadingPresentationChecks.Run(); FlightPoseBufferChecks.Run();
                    at = now + .4; stage = 1; return;
                }
                if (now < at) return;
                if (stage == 1)
                {
                    Require(root.Q("weapon-row") == null && root.Q("mini-device-9") == null,
                        "Obsolete Ship Loadout widget survived Systems consolidation");
                    Click(root.Q<Button>("expand-systems")); at = now + .3; stage = 2; return;
                }
                if (stage == 2)
                {
                    Require(hud.EquipmentOpen && root.Q("content-fitting").resolvedStyle.display != DisplayStyle.None,
                        "Systems expansion did not show ship, mounts and cargo");
                    Drag(root.Q("mount-3"), root, root.Q("shortcut-0"));
                    Drag(root.Q("mount-9"), root, root.Q("shortcut-1"));
                    Require(Bindings(abilities)[0] == int.MinValue + 4 && Bindings(abilities)[1] == int.MinValue + 10,
                        "Fitted weapon/device did not bind to their exact systems");
                    Click(root.Q<Button>("add-shortcut")); at = now + .3; stage = 3; return;
                }
                if (stage == 3)
                {
                    Require(Count(abilities) == 7 && root.Q("shortcut-6").resolvedStyle.display == DisplayStyle.Flex,
                        "Add system did not expose a seventh slot");
                    at = now + .2; stage = 30; return;
                }
                if (stage == 30)
                {
                    Drag(root.Q("mount-0"), root, root.Q("shortcut-2"));
                    Require(Bindings(abilities)[2] == int.MinValue + 1, "Passive shield could not be dragged from full fitting");
                    Require(Fingerprint(state) == inventory, "Binding a shortcut moved owned inventory");
                    var loaded = new AbilityHud(client, hud);
                    typeof(AbilityHud).GetMethod("ShortcutCount", Private).Invoke(loaded, new object[] { state });
                    Require(Count(loaded) == 7 && Bindings(loaded).Take(3).SequenceEqual(Bindings(abilities).Take(3)),
                        "Shortcut count or equipped assignments were not persisted");
                    Click(root.Q("shortcut-2"));
                    Require(hud.EquipmentOpen && (int)typeof(FlightHud).GetField("selectedMount", Private).GetValue(hud) == 0,
                        "Passive shortcut did not inspect its fitted item");
                    ScreenCapture.CaptureScreenshot("Builds/verification/systems-expanded.png");
                    at = now + .3; stage = 31; return;
                }
                if (stage == 31)
                {
                    Pointer(root.Q("shortcut-0"), EventType.MouseDown, root.Q("shortcut-0").worldBound.center, 1);
                    Require(root.Q("system-selector").resolvedStyle.display != DisplayStyle.None,
                        "Right click did not open the runtime system selector");
                    var choices = (SystemChoice[])typeof(AbilityHud).GetMethod("SystemChoices", Private)
                        .Invoke(abilities, new object[] { 0, state });
                    Require(choices.Length > 0, "Selector omitted the server-advertised skills");
                    SystemChoice choice = choices[0]; choiceId = choice.Id;
                    root.Q<TextField>("systems-search").value = choice.Name;
                    at = now + .3; stage = 32; return;
                }
                if (stage == 32)
                { ScreenCapture.CaptureScreenshot("Builds/verification/systems-selector.png"); at = now + .3; stage = 33; return; }
                if (stage == 33)
                {
                    Click(root.Q<Button>("system-choice-" + choiceId));
                    Require(Bindings(abilities)[0] == choiceId, "Selecting an effect/sublevel did not change the system assignment");
                    at = now + .2; stage = 34; return;
                }
                if (stage == 34)
                {
                    Drag(root.Q("mount-3"), root, root.Q("shortcut-0"));
                    at = now + .3; stage = 4; return;
                }
                if (stage == 4)
                {
                    for (int i = 0; i < 3; i++) Require(root.Q("shortcut-" + i).Q<Image>()?.image != null, "Fitted shortcut is missing its item icon");
                    Directory.CreateDirectory("Builds/verification"); ScreenCapture.CaptureScreenshot("Builds/verification/equipment-shortcuts.png");
                    hud.ToggleEquipment(); target = state.Mobs.First(actor => actor.HasPosition).Id;
                    client.SelectContact(target); stage = 5; return;
                }
                if (stage == 5 && state.SelectedTargetId == target)
                { Click(root.Q("shortcut-0")); stage = 6; return; }
                if (stage == 6 && state.Ammo[3].Quantity < 20)
                {
                    Click(root.Q("shortcut-0"));
                    typeof(AbilityHud).GetMethod("ClearShortcut", Private).Invoke(abilities, new object[] { 1, state });
                    Require(Bindings(abilities)[1] == -1, "Clear shortcut did not preserve an empty slot");
                    Click(root.Q<Button>("remove-shortcut"));
                    Require(Count(abilities) == 6 && Bindings(abilities)[0] == int.MinValue + 4,
                        "Removing an empty extra slot destroyed an existing assignment");
                    Require(Enumerable.Range(0,24).Select(index => client.Bindings.Get(KeyBindings.ShortcutAction(index))).Distinct().Count() == 24,
                        "Extended shortcut keyboard bindings conflict");
                    at = now + .5; stage = 7; return;
                }
                if (stage == 7 && File.Exists("Builds/verification/equipment-shortcuts.png"))
                {
                    var art = (SpaceArt)typeof(SpaceClient).GetField("art", Private).GetValue(client);
                    dynamicSpace = new DynamicSpacePreview(art); dynamicSpace.Capture(true);
                    at = now + 2; stage = 8; return;
                }
                if (stage == 8)
                {
                    dynamicSpace.Capture(false); dynamicSpace.Dispose(); dynamicSpace = null;
                    uiRegression=new UiLayoutRegression(client,hud,root);
                    uiDriver=new GameObject("Owned UI viewport test").AddComponent<UiViewportDriver>();
                    uiDriver.Tick=StepUi;
                }
            }
            catch (Exception error) { Finish(false, error.ToString()); }
        }

        /// <summary>Check Screen and IMGUI measurements from the actual player-loop context.</summary>
        private static void StepUi()
        {
            try
            {
                if(uiRegression.Step(EditorApplication.timeSinceStartup))
                    Finish(true,"equipped weapon/device/passive drag, Systems expansion, grouped selector, WH continuity, cooldown/active states, animated space, persistence, exact-mount firing, clear/remove; UI viewport matrix, fitting persistence, collapse/drag, rendered login accessibility");
            }
            catch(Exception error){Finish(false,error.ToString());}
        }

        /// <summary>Keep every WH destination selectable across loading and verify active effects never receive recovery masks.</summary>
        private static void CheckSystemPresentation(SpaceClient client, FlightHud hud)
        {
            var world = new World(1077);
            world.Apply(new Frame(0xffff, Array.Empty<byte>()), 0x7efeed42);
            uint[] wormholes = { 19,26,37,66,67,123,131,28 };
            typeof(World).GetProperty("AvailableAbilities").SetValue(world, wormholes);
            var view = new AbilityHud(client, hud);
            typeof(AbilityHud).GetMethod("ShortcutCount", Private).Invoke(view, new object[] { new Snapshot(world,"test",true) });
            Bindings(view)[0] = 19;
            var choicesMethod = typeof(AbilityHud).GetMethod("SystemChoices", Private);
            var choices = (SystemChoice[])choicesMethod.Invoke(view, new object[] { 0, new Snapshot(world,"test",true) });
            Require(choices.Count(choice => choice.Skill == 15) == 7, "Wormhole destinations were lost or merged into one rank");
            typeof(World).GetProperty("AvailableAbilities").SetValue(world, Array.Empty<uint>());
            choices = (SystemChoice[])choicesMethod.Invoke(view, new object[] { 0, new Snapshot(world,"loading",false) });
            Require(choices.Count(choice => choice.Skill == 15) == 7, "Transient loading removed previously advertised wormholes");
            Bindings(view)[0] = 28;
            world.AbilityCooldownEnds[12] = System.Diagnostics.Stopwatch.GetTimestamp() + 8 * System.Diagnostics.Stopwatch.Frequency;
            var method = typeof(AbilityHud).GetMethod("SystemState", Private);
            var cooling = (SystemActivity)method.Invoke(view, new object[] { 0, new Snapshot(world,"test",true) });
            Require(cooling.CoveredFraction > .95f && cooling.Remaining > 7, "Recovery did not cover the assigned system");
            world.AbilityCooldownEnds[12] = System.Diagnostics.Stopwatch.GetTimestamp() + 4 * System.Diagnostics.Stopwatch.Frequency;
            var recovering = (SystemActivity)method.Invoke(view, new object[] { 0, new Snapshot(world,"test",true) });
            Require(recovering.CoveredFraction > .35f && recovering.CoveredFraction < .7f, "Recovery mask did not drain proportionally");
            var buffs = (TimedDeviceBuff[])typeof(World).GetField("buffs", Private).GetValue(world);
            buffs[6] = new TimedDeviceBuff("Cloak", 0, 1, 180, permanent: true);
            var active = (SystemActivity)method.Invoke(view, new object[] { 0, new Snapshot(world,"test",true) });
            Require(active.Active && active.CoveredFraction == 0, "An active effect was incorrectly grayed by recovery");
        }

        /// <returns>int[]: Saved shortcut binding encodings on the exercised instance.</returns>
        private static int[] Bindings(AbilityHud hud) { return (int[])typeof(AbilityHud).GetField("slots", Private).GetValue(hud); }
        /// <returns>int: Number of visible shortcut slots.</returns>
        private static int Count(AbilityHud hud) { return (int)typeof(AbilityHud).GetField("slotCount", Private).GetValue(hud); }
        /// <returns>string: Exact inventory identities and quantities before activation.</returns>
        private static string Fingerprint(Snapshot state) { return string.Join("|", state.Mounted.Concat(state.Cargo).Concat(state.Ammo).Select(item => item == null ? "-" : item.Template + ":" + item.Quantity)); }
        /// <summary>Assert one user-visible behavior with a concrete error.</summary>
        private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
        /// <summary>Dispatch an actual retained button click.</summary>
        private static void Click(VisualElement target) { Pointer(target, EventType.MouseDown, target.worldBound.center); Pointer(target, EventType.MouseUp, target.worldBound.center); }
        /// <summary>Route a fitted item's drag through the shared ghost and pointer-capture handlers.</summary>
        private static void Drag(VisualElement source, VisualElement root, VisualElement target)
        { Pointer(source, EventType.MouseDown, source.worldBound.center); Pointer(root, EventType.MouseDrag, target.worldBound.center); Pointer(root, EventType.MouseUp, target.worldBound.center); }
        /// <summary>Send one native retained pointer phase.</summary>
        private static void Pointer(VisualElement target, EventType phase, Vector2 position, int button = 0)
        {
            var input = new Event { type = phase, button = button, mousePosition = position };
            if (phase == EventType.MouseDown) { using (var evt = PointerDownEvent.GetPooled(input)) target.SendEvent(evt); }
            else if (phase == EventType.MouseDrag) { using (var evt = PointerMoveEvent.GetPooled(input)) target.SendEvent(evt); }
            else { using (var evt = PointerUpEvent.GetPooled(input)) target.SendEvent(evt); }
        }
        /// <summary>Restore the user's preferences before either successful or failed test exit.</summary>
        private static void Finish(bool success, string message)
        {
            if(uiDriver!=null)UnityEngine.Object.Destroy(uiDriver.gameObject);uiDriver=null;
            uiRegression?.Dispose();uiRegression=null;
            dynamicSpace?.Dispose(); dynamicSpace = null;
            foreach (var pair in saved) { if (pair.Value.HasValue) PlayerPrefs.SetInt(pair.Key, pair.Value.Value); else PlayerPrefs.DeleteKey(pair.Key); }
            PlayerPrefs.Save(); EditorApplication.update -= Tick;
            if (success) Debug.Log("PASS: " + message); else Debug.LogError("Shortcut smoke: " + message);
            EditorApplication.Exit(success ? 0 : 1);
        }
    }
}
