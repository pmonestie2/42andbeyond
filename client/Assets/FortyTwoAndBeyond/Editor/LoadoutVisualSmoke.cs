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
        private static bool dragCaptured, invalidCaptured;
        private static string inventoryBefore;

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
                Hud(client).ToggleEquipment();
                captureAt = EditorApplication.timeSinceStartup + .6;
                stage = -1; return;
            }
            if (stage == -1)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                VisualElement root = DeckRoot();
                if (root.Q("panel-destinations").resolvedStyle.display == DisplayStyle.None ||
                    root.Q("panel-target").resolvedStyle.display == DisplayStyle.None ||
                    root.Q("panel-target").parent.resolvedStyle.display == DisplayStyle.None ||
                    root.Q("collapse-destinations") == null || root.Q("collapse-target") == null)
                { Fail("Opening inventory hid the flight panels or lost their collapse controls."); return; }
                dragStart = root.Q("cargo-0").worldBound.center;
                dragEnd = root.Q("mount-3").worldBound.center;
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
                captureAt = EditorApplication.timeSinceStartup + .15; stage = -3; return;
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
                    !root.Q("mount-3").ClassListContains("drop-valid") ||
                    !root.Q("mount-3").ClassListContains("drop-hover"))
                { Fail("Held inventory drag lost its native icon, scaled pointer alignment, source dimming or valid target feedback."); return; }
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
        private static void Pointer(VisualElement target, EventType phase, Vector2 position)
        {
            var input = new Event { type = phase, button = 0, mousePosition = position };
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
