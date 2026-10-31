using System;
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
    /// <summary>Verify native cargo movement, launcher loading, and spent rounds in Unity.</summary>
    [InitializeOnLoad]
    public static class LoadoutVisualSmoke
    {
        private static int stage;
        private static bool connected, captured, flightCaptured;
        private static uint mobId;
        private static double deadline, captureAt;
        private static Vector2 fittingBefore, dragStart, dragEnd, dragPoint;
        private static double dragStarted;
        private static double dragReadyDeadline;
        private static bool dragCaptured, invalidCaptured, hoverCaptured;
        private static Vector2 resizedComms;
        private static string inventoryBefore;
        private static int[] compactBindings;
        private static int originalCompactBinding;

        /// <summary>Register only for an explicit disposable inventory check.</summary>
        static LoadoutVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                "--42andbeyond-loadout-smoke") < 0) return;
            deadline = EditorApplication.timeSinceStartup + 100;
            EditorApplication.update += Tick;
        }

        /// <summary>Wait for each server snapshot before moving, firing, and capturing.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Loadout visual smoke timed out at stage " + stage); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type view = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (view != null)
                { EditorWindow window = EditorWindow.GetWindow(view);
                  window.maximized = true;
                  window.Focus(); }
                EditorApplication.EnterPlaymode(); return;
            }
            if (!EditorApplication.isPlaying) return;
            SpaceClient client = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            if (!connected)
            { connected = true; client.StartLocalSession("127.0.0.1", "local", "local"); }
            if (client.ChoosingCharacter) client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || !snapshot.HasPosition ||
                stage < 8 && snapshot.Mounted[3]?.Type != 15) return;
            if (stage == 0)
            {
                if (snapshot.Cargo[0]?.Template != 507 || snapshot.Cargo[1]?.Template != 5931)
                { Fail("Disposable cargo was not published by the server."); return; }
                object abilities = typeof(SpaceClient).GetField("abilityHud", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
                compactBindings = (int[])typeof(AbilityHud).GetField("slots", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(abilities);
                originalCompactBinding = compactBindings[0]; compactBindings[0] = int.MinValue + 4;
                captureAt = EditorApplication.timeSinceStartup + .4;
                stage = -210; return;
            }
            if (stage == -210)
            {
                if (EditorApplication.timeSinceStartup < captureAt || snapshot.LauncherAmmo?.Known != true) return;
                VisualElement slot = DeckRoot().Q("shortcut-0");
                Pointer(slot, EventType.MouseDown, slot.worldBound.center, 1);
                captureAt = EditorApplication.timeSinceStartup + .5; stage = -211; return;
            }
            if (stage == -211)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                VisualElement root = DeckRoot();
                if (root.Q("fitting-window").resolvedStyle.display != DisplayStyle.None ||
                    root.Q("launcher-ammo-picker").resolvedStyle.display == DisplayStyle.None ||
                    root.Q<Button>("ammo-choice-0") == null || root.Q<Button>("ammo-choice-1") != null)
                { Fail("Compact launcher shortcut did not retain a compatible-only ammo menu without opening inventory."); return; }
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/compact-shortcut-ammo.png");
                captureAt = EditorApplication.timeSinceStartup + .3; stage = -212; return;
            }
            if (stage == -212)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                Button choice = DeckRoot().Q<Button>("ammo-choice-0");
                Pointer(choice, EventType.MouseDown, choice.worldBound.center);
                Pointer(choice, EventType.MouseUp, choice.worldBound.center);
                stage = -213; return;
            }
            if (stage == -213)
            {
                if (snapshot.Ammo[3]?.Quantity != 20 || snapshot.Cargo[0] != null) return;
                client.MoveInventory(new InventoryMove(8, 3, 1, 0, 20)); stage = -214; return;
            }
            if (stage == -214)
            {
                if (snapshot.Ammo[3] != null || snapshot.Cargo[0]?.Quantity != 20) return;
                compactBindings[0] = originalCompactBinding; compactBindings = null;
                Debug.Log("PASS: compact launcher shortcut kept compatible ammo menu open and loaded selected cargo ammo.");
                captureAt = EditorApplication.timeSinceStartup + .3; stage = -20; return;
            }
            if (stage == -20)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                VisualElement root = DeckRoot();
                if (root.Q("weapon-row") != null) { Fail("Obsolete Ship Loadout survived Systems consolidation."); return; }
                if (root.Q<Label>("flight-pilot-name")?.text != client.PilotName ||
                    root.Q<Label>("flight-sector")?.text != snapshot.Sector ||
                    root.Q("panel-communications")?.resolvedStyle.height < 75)
                { Fail("Merged flight deck identity or default communications preview is missing."); return; }
                VisualElement comms = root.Q("panel-communications");
                VisualElement grip = root.Q("communications-resize-grip");
                TextField composer = root.Q<TextField>("chat-entry");
                if (composer == null || composer.resolvedStyle.display == DisplayStyle.None || grip == null)
                { Fail("Always-visible composer or communications resize grip is missing."); return; }
                Vector2 start = grip.worldBound.center;
                resizedComms = new Vector2(comms.resolvedStyle.width + 75, comms.resolvedStyle.height + 40);
                Pointer(grip, EventType.MouseDown, start);
                Pointer(grip, EventType.MouseDrag, start + new Vector2(75, 40));
                Pointer(grip, EventType.MouseUp, start + new Vector2(75, 40));
                if (comms.style.width.value.value < resizedComms.x - 8 ||
                    comms.style.height.value.value < resizedComms.y - 8)
                { Fail("Communications resize grip did not resize both dimensions."); return; }
                composer.Focus(); composer.value = "Typing stays visible";
                if (!client.ChatOpen) { Fail("Focused always-visible composer did not block flight input."); return; }
                Button menu = root.Q<Button>("flight-menu");
                if (menu == null) { Fail("Merged flight deck lacks the command menu."); return; }
                Pointer(menu, EventType.MouseDown, menu.worldBound.center);
                Pointer(menu, EventType.MouseUp, menu.worldBound.center);
                VisualElement options = root.Q("flight-menu-options");
                if (options == null || options.resolvedStyle.display == DisplayStyle.None || options.childCount < 10)
                { Fail("Flight deck menu did not preserve the former toolbar commands."); return; }
                string menuPath = "Builds/verification/flight-deck-menu.png";
                if (File.Exists(menuPath)) File.Delete(menuPath);
                ScreenCapture.CaptureScreenshot(menuPath);
                captureAt = EditorApplication.timeSinceStartup + .3;
                stage = -21; return;
            }
            if (stage == -21)
            {
                if (EditorApplication.timeSinceStartup < captureAt ||
                    !File.Exists("Builds/verification/flight-deck-menu.png")) return;
                VisualElement root = DeckRoot();
                VisualElement comms = root.Q("panel-communications");
                if (comms.resolvedStyle.width < resizedComms.x - 8 ||
                    comms.resolvedStyle.height < resizedComms.y - 8)
                { Fail("Communications dimensions reverted during normal refresh."); return; }
                TextField composer = root.Q<TextField>("chat-entry");
                if (composer.value != "Typing stays visible")
                { Fail("Always-visible composer lost its typed content during refresh."); return; }
                composer.value = ""; composer.Blur();
                Button menu = root.Q<Button>("flight-menu");
                Pointer(menu, EventType.MouseDown, menu.worldBound.center);
                Pointer(menu, EventType.MouseUp, menu.worldBound.center);
                Pointer(menu, EventType.MouseDown, menu.worldBound.center);
                Pointer(menu, EventType.MouseUp, menu.worldBound.center);
                Button expand = root.Query<Button>().ToList().FirstOrDefault(button => button.text == "Systems / I");
                if (expand == null) { Fail("Flight menu lacks full ship fitting expansion."); return; }
                Pointer(expand, EventType.MouseDown, expand.worldBound.center);
                Pointer(expand, EventType.MouseUp, expand.worldBound.center);
                captureAt = EditorApplication.timeSinceStartup + .6;
                stage = -1; return;
            }
            if (stage == -1)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                VisualElement root = DeckRoot();
                if (root.Q("fitting-window").resolvedStyle.display == DisplayStyle.None ||
                    root.Q("content-fitting").resolvedStyle.display == DisplayStyle.None)
                { Fail("Compact Expand did not open the full ship fitting view."); return; }
                if (root.Q("panel-browse") == null || root.Q("panel-destinations") == null ||
                    root.Q("panel-target") == null || root.Q("flight-strip") == null)
                { Fail("Opening inventory removed the shared flight browser or strip."); return; }
                VisualElement cargoFirst = root.Q("cargo-0"), cargoSecond = root.Q("cargo-1");
                if (cargoFirst == null || cargoSecond == null ||
                    cargoSecond.worldBound.x <= cargoFirst.worldBound.x + 50 ||
                    Mathf.Abs(cargoSecond.worldBound.y - cargoFirst.worldBound.y) > 10)
                { Fail("Expanded cargo did not lay out two readable columns."); return; }
                if (snapshot.LauncherAmmo?.Known != true) return;
                VisualElement launcher = root.Q("mount-3");
                Pointer(launcher, EventType.MouseDown, launcher.worldBound.center, 1);
                VisualElement ammoMenu = root.Q("launcher-ammo-picker");
                if (ammoMenu == null || ammoMenu.resolvedStyle.display == DisplayStyle.None ||
                    root.Q("ammo-choice-0") == null || root.Q("ammo-choice-1") != null)
                { Fail("Launcher menu did not show only server-compatible Dragonclaw ammunition."); return; }
                string ammoPath = "Builds/verification/launcher-ammo-menu.png";
                if (File.Exists(ammoPath)) File.Delete(ammoPath);
                ScreenCapture.CaptureScreenshot(ammoPath);
                captureAt = EditorApplication.timeSinceStartup + .3;
                stage = -12; return;
            }
            if (stage == -12)
            {
                if (EditorApplication.timeSinceStartup < captureAt ||
                    !File.Exists("Builds/verification/launcher-ammo-menu.png")) return;
                VisualElement root = DeckRoot();
                Button close = root.Q<Button>("close-ammo-picker");
                Pointer(close, EventType.MouseDown, close.worldBound.center);
                Pointer(close, EventType.MouseUp, close.worldBound.center);
                VisualElement cargoFirst = root.Q("cargo-0");
                Pointer(cargoFirst, EventType.MouseDrag, cargoFirst.worldBound.center);
                VisualElement hover = root.Q("inventory-item-hover");
                if (hover == null || hover.resolvedStyle.display == DisplayStyle.None ||
                    hover.Q<Label>(className: "title")?.text != snapshot.Cargo[0].Name)
                { Fail("Runtime cargo hover did not show the full server item name and stats."); return; }
                if (!hoverCaptured)
                {
                    string hoverPath = "Builds/verification/loadout-hover.png";
                    if (File.Exists(hoverPath)) File.Delete(hoverPath);
                    ScreenCapture.CaptureScreenshot(hoverPath); hoverCaptured = true;
                    captureAt = EditorApplication.timeSinceStartup + .3;
                    stage = -11; return;
                }
            }
            if (stage == -11)
            {
                if (EditorApplication.timeSinceStartup < captureAt ||
                    !File.Exists("Builds/verification/loadout-hover.png")) return;
                VisualElement root = DeckRoot();
                dragStart = root.Q("cargo-0").worldBound.center;
                dragEnd = root.Q("magazine-3").worldBound.center;
                Pointer(root.Q("cargo-0"), EventType.MouseDown, dragStart);
                dragStarted = EditorApplication.timeSinceStartup;
                stage = -2; return;
            }
            if (stage == -2)
            {
                VisualElement root = DeckRoot();
                float t = Mathf.Clamp01((float)((EditorApplication.timeSinceStartup - dragStarted) / .3));
                dragPoint = Vector2.Lerp(dragStart, dragEnd, t);
                Pointer(root, EventType.MouseDrag, dragPoint);
                if (t < 1) return;
                captureAt = EditorApplication.timeSinceStartup + .05;
                dragReadyDeadline = EditorApplication.timeSinceStartup + 2;
                stage = -3; return;
            }
            if (stage == -3)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                VisualElement root = DeckRoot();
                VisualElement ghost = root.Q("inventory-drag-ghost");
                Image icon = root.Q<Image>("inventory-drag-icon");
                if (ghost == null || ghost.resolvedStyle.display == DisplayStyle.None || icon?.image == null ||
                    Vector2.Distance(icon.worldBound.center, dragPoint) > 90 ||
                    !root.Q("cargo-0").ClassListContains("drag-source") ||
                    !root.Q("magazine-3").ClassListContains("drop-valid") ||
                    !root.Q("magazine-3").ClassListContains("drop-hover"))
                { if (EditorApplication.timeSinceStartup < dragReadyDeadline) return;
                    Fail("Held drag state: ghost=" + (ghost?.resolvedStyle.display.ToString() ?? "missing") +
                    " icon=" + (icon?.image != null) + " distance=" + (icon == null ? -1 : Vector2.Distance(icon.worldBound.center, dragPoint)) +
                    " point=" + dragPoint + " iconCenter=" + (icon == null ? Vector2.zero : icon.worldBound.center) +
                    " root=" + root.worldBound +
                    " source=" + root.Q("cargo-0").ClassListContains("drag-source") +
                    " valid=" + root.Q("magazine-3").ClassListContains("drop-valid") +
                    " hovered=" + root.Q("magazine-3").ClassListContains("drop-hover")); return; }
                if (snapshot.Cargo[0]?.Template != 507 || snapshot.Cargo[0].Quantity != 20 || snapshot.Ammo[3] != null)
                { Fail("Held pointer drag changed server inventory before pointer-up."); return; }
                string dragPath = "Builds/verification/loadout-drag.png";
                if (!dragCaptured)
                {
                    Directory.CreateDirectory("Builds/verification");
                    if (File.Exists(dragPath)) File.Delete(dragPath);
                    ScreenCapture.CaptureScreenshot(dragPath); dragCaptured = true;
                    captureAt = EditorApplication.timeSinceStartup + .25; return;
                }
                if (!File.Exists(dragPath)) return;
                Pointer(root, EventType.MouseUp, dragEnd); stage = 1; return;
            }
            if (stage == 1)
            {
                if (snapshot.Ammo[3]?.Quantity != 20 || snapshot.Cargo[0] != null) return;
                LauncherAmmoOption launcher = snapshot.LauncherAmmo?.Launchers.FirstOrDefault(option => option.Mount == 3);
                if (launcher == null || launcher.PreviousTemplate != 507 || !launcher.AutoReload) return;
                VisualElement root = DeckRoot();
                Pointer(root.Q("mount-3"), EventType.MouseDown, root.Q("mount-3").worldBound.center, 1);
                Toggle toggle = root.Q<Toggle>("launcher-auto-reload");
                if (toggle == null || !toggle.enabledSelf) { Fail("Auto Reload remained unavailable after choosing ammunition."); return; }
                toggle.value = false;
                stage = 11; return;
            }
            if (stage == 11)
            {
                LauncherAmmoOption launcher = snapshot.LauncherAmmo?.Launchers.FirstOrDefault(option => option.Mount == 3);
                if (launcher?.AutoReload != false) return;
                DeckRoot().Q<Toggle>("launcher-auto-reload").value = true;
                stage = 12; return;
            }
            if (stage == 12)
            {
                LauncherAmmoOption launcher = snapshot.LauncherAmmo?.Launchers.FirstOrDefault(option => option.Mount == 3);
                if (launcher?.AutoReload != true) return;
                VisualElement root = DeckRoot();
                Button close = root.Q<Button>("close-ammo-picker");
                Pointer(close, EventType.MouseDown, close.worldBound.center);
                Pointer(close, EventType.MouseUp, close.worldBound.center);
                client.MoveInventory(new InventoryMove(1, 1, 1, 5, 3));
                stage = 2; return;
            }
            if (stage == 2)
            {
                if (snapshot.Cargo[1] != null || snapshot.Cargo[5]?.Template != 5931) return;
                VisualElement root = DeckRoot();
                if (root.Q("cargo-5") == null) return;
                inventoryBefore = InventoryFingerprint(snapshot);
                Drag(root.Q("cargo-5"), root, root.Q("mount-0").worldBound.center);
                captureAt = EditorApplication.timeSinceStartup + .12;
                stage = 21; return;
            }
            if (stage == 21)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                VisualElement ghost = DeckRoot().Q("inventory-drag-ghost");
                if (ghost.resolvedStyle.display == DisplayStyle.None || !ghost.ClassListContains("drop-rejected"))
                { Fail("Incompatible slot did not visibly return its rejected item."); return; }
                if (InventoryFingerprint(snapshot) != inventoryBefore)
                { Fail("Rejected inventory drop mutated server inventory."); return; }
                string invalidPath = "Builds/verification/loadout-drag-invalid.png";
                if (!invalidCaptured)
                {
                    if (File.Exists(invalidPath)) File.Delete(invalidPath);
                    ScreenCapture.CaptureScreenshot(invalidPath); invalidCaptured = true;
                }
                captureAt = EditorApplication.timeSinceStartup + .95; stage = 22; return;
            }
            if (stage == 22)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                VisualElement root = DeckRoot();
                if (InventoryFingerprint(snapshot) != inventoryBefore ||
                    root.Q("inventory-drag-ghost").resolvedStyle.display != DisplayStyle.None ||
                    root.Q("cargo-5").ClassListContains("drag-source"))
                { Fail("Rejected drag did not restore source visuals and preserve inventory."); return; }
                Pointer(root.Q("cargo-5"), EventType.MouseDown, root.Q("cargo-5").worldBound.center);
                Pointer(root, EventType.MouseDrag, root.Q("mount-3").worldBound.center);
                captureAt = EditorApplication.timeSinceStartup + .15; stage = 23; return;
            }
            if (stage == 23)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                VisualElement root = DeckRoot();
                if (root.Q("inventory-drag-ghost").resolvedStyle.display == DisplayStyle.None)
                { Fail("Cross-frame Escape test lost its held drag."); return; }
                var key = new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape };
                using (KeyDownEvent evt = KeyDownEvent.GetPooled(key)) root.SendEvent(evt);
                captureAt = EditorApplication.timeSinceStartup + .95; stage = 24; return;
            }
            if (stage == 24)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                VisualElement root = DeckRoot();
                if (InventoryFingerprint(snapshot) != inventoryBefore ||
                    root.Q("inventory-drag-ghost").resolvedStyle.display != DisplayStyle.None ||
                    root.Q("cargo-5").ClassListContains("drag-source"))
                { Fail("Escape cancellation left a ghost, dim source or changed inventory."); return; }
                if (!File.Exists("Builds/verification/loadout-drag-invalid.png")) return;
                ShipActor mob = snapshot.Mobs.FirstOrDefault(actor => actor.Hostile && actor.HasPosition);
                if (mob == null) return;
                mobId = mob.Id;
                Hud(client).SelectContact(mobId); client.ToggleWeapons(mobId);
                Hud(client).ToggleEquipment();
                captureAt = EditorApplication.timeSinceStartup + .5;
                stage = 3; return;
            }
            if (stage == 3)
            {
                if (snapshot.Ammo[3] == null || snapshot.Ammo[3].Quantity >= 20 ||
                    EditorApplication.timeSinceStartup < captureAt) return;
                string flightPath = "Builds/verification/loadout-flight.png";
                if (!flightCaptured)
                {
                    Directory.CreateDirectory("Builds/verification");
                    if (File.Exists(flightPath)) File.Delete(flightPath);
                    ScreenCapture.CaptureScreenshot(flightPath);
                    flightCaptured = true;
                    captureAt = EditorApplication.timeSinceStartup + .6;
                    return;
                }
                if (!File.Exists(flightPath) ||
                    EditorApplication.timeSinceStartup < captureAt) return;
                stage = 4; return;
            }
            if (stage == 4)
            {
                FlightHud hud = typeof(SpaceClient).GetField("hud",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as FlightHud;
                if (hud == null) { Fail("Loadout HUD was not initialized."); return; }
                typeof(FlightHud).GetField("equipmentOpen",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(hud, true);
                client.StopWeapons();
                captureAt = EditorApplication.timeSinceStartup + .6;
                stage = 5; return;
            }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            if (stage == 5)
            {
                VisualElement mount = DeckRoot().Q("mount-3");
                Drag(mount, DeckRoot(), mount.worldBound.center);
                captureAt = EditorApplication.timeSinceStartup + .25;
                stage = 6; return;
            }
            string path = "Builds/verification/loadout-ammo.png";
            if (!captured)
            { Directory.CreateDirectory("Builds/verification");
              if (File.Exists(path)) File.Delete(path);
              ScreenCapture.CaptureScreenshot(path); captured = true;
              captureAt = EditorApplication.timeSinceStartup + .6; return; }
            if (!File.Exists(path) || EditorApplication.timeSinceStartup < captureAt) return;
            if (stage == 6)
            {
                VisualElement root = DeckRoot();
                fittingBefore = root.Q("fitting-window").worldBound.position;
                VisualElement handle = root.Q("fitting-handle");
                Drag(handle, handle, handle.worldBound.center + new Vector2(24, 12));
                captureAt = EditorApplication.timeSinceStartup + .2;
                stage = 7; return;
            }
            if (stage == 7)
            {
                Vector2 moved = DeckRoot().Q("fitting-window").worldBound.position;
                if (Vector2.Distance(moved, fittingBefore + new Vector2(24, 12)) > 2)
                { Fail("Retained fitting header did not move its window."); return; }
                VisualElement root = DeckRoot();
                Drag(root.Q("magazine-3"), root, root.Q("cargo-5").worldBound.center);
                stage = 8; return;
            }
            if (stage == 8)
            {
                if (snapshot.Ammo[3] != null || snapshot.Cargo[0]?.Template != 507) return;
                VisualElement root = DeckRoot();
                Drag(root.Q("mount-3"), root, root.Q("cargo-5").worldBound.center);
                stage = 9; return;
            }
            if (stage == 9)
            {
                if (snapshot.Mounted[3] != null || snapshot.Cargo[1]?.Template != 2656) return;
                VisualElement root = DeckRoot();
                if (root.Q("cargo-1") == null) return;
                Drag(root.Q("cargo-1"), root, root.Q("mount-3").worldBound.center);
                stage = 10; return;
            }
            if (stage == 10)
            {
                if (snapshot.Mounted[3]?.Template != 2656 || snapshot.Cargo[1] != null) return;
                client.OpenGalaxyTravel();
                var browser = (GalaxyTravelHud)typeof(SpaceClient).GetField("galaxyTravelHud", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
                typeof(GalaxyTravelHud).GetField("search", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(browser, "Earth Station");
                typeof(GalaxyTravelHud).GetField("flags", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(browser, 2u);
                client.QueryGalaxyDestinations("Earth Station", 0, 2); stage = 30; return;
            }
            if (stage == 30)
            {
                var destination = snapshot.GalaxyCatalog?.Entries.FirstOrDefault(item => item.Sector == 1060 && item.Id == 103066);
                if (destination == null) return;
                var browser = (GalaxyTravelHud)typeof(SpaceClient).GetField("galaxyTravelHud", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
                typeof(GalaxyTravelHud).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(browser, destination);
                client.PreviewGalaxyRoute(destination.Sector, destination.Id); stage = 31; return;
            }
            if (stage == 31)
            {
                if (snapshot.GalaxyRoute == null) return;
                if (!snapshot.GalaxyRoute.Success || snapshot.GalaxyRoute.ArrivalSector != 10601 ||
                    snapshot.GalaxyRoute.Legs.Count != 2 || snapshot.GalaxyRoute.Legs[0].Kind != 0 || snapshot.GalaxyRoute.Legs[1].Kind != 2)
                { Fail("Known-map route did not plan Alpha to Earth Station through a gate and docking."); return; }
                captureAt = EditorApplication.timeSinceStartup + .7; stage = 32; return;
            }
            if (stage == 32)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/galaxy-travel.png");
                captureAt = EditorApplication.timeSinceStartup + .5; stage = 33; return;
            }
            if (!File.Exists("Builds/verification/galaxy-travel.png")) return;
            Debug.Log("PASS: client-only known-map route preview and galaxy destination overlay.");
            Debug.Log("PASS: cargo reordered, magazine loaded, and missile rounds consumed in server snapshots. Native icon drag held before commit, invalid drop and Escape cancellation preserved inventory. Pointer drags unloaded ammunition and equipment over occupied cargo, then equipped the weapon again.");
            EditorApplication.update -= Tick;
            if (compactBindings != null) compactBindings[0] = originalCompactBinding;
            EditorApplication.Exit(0);
        }

        /// Returns:
        ///     FlightHud: The foreground controller owned by this disposable client.
        private static FlightHud Hud(SpaceClient client)
        { return (FlightHud)typeof(SpaceClient).GetField("hud", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client); }

        /// Returns:
        ///     VisualElement: The actual retained runtime document used by this disposable client.
        private static VisualElement DeckRoot()
        { return GameObject.Find("Flight deck UI").GetComponent<UIDocument>().rootVisualElement; }

        /// <summary>Route native pointer events through the actual retained controls.</summary>
        private static void Drag(VisualElement source, VisualElement capture, Vector2 destination)
        {
            if (source == null) throw new InvalidOperationException("Missing retained inventory control.");
            Pointer(source, EventType.MouseDown, source.worldBound.center);
            Pointer(capture, EventType.MouseDrag, destination);
            Pointer(capture, EventType.MouseUp, destination);
        }

        /// <summary>Dispatch one native pointer phase through the actual retained panel.</summary>
        private static void Pointer(VisualElement target, EventType phase, Vector2 position, int button = 0)
        {
            var input = new Event { type = phase, button = button, mousePosition = position };
            if (phase == EventType.MouseDown)
            { using (PointerDownEvent evt = PointerDownEvent.GetPooled(input)) target.SendEvent(evt); }
            else if (phase == EventType.MouseDrag)
            { using (PointerMoveEvent evt = PointerMoveEvent.GetPooled(input)) target.SendEvent(evt); }
            else
            { using (PointerUpEvent evt = PointerUpEvent.GetPooled(input)) target.SendEvent(evt); }
        }

        /// Returns:
        ///     string: Exact server-owned cargo, mounted equipment and magazine identities and quantities.
        private static string InventoryFingerprint(Snapshot snapshot) => string.Join("|",
            snapshot.Cargo.Concat(snapshot.Mounted).Concat(snapshot.Ammo).Select(item =>
                item == null ? "-" : item.Template + ":" + item.Quantity + ":" + item.Structure));

        /// <summary>Exit the owned Editor with the first concrete failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
