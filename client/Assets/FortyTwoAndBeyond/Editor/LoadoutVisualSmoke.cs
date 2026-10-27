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
        private static Vector2 fittingBefore;

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
                Drag(root.Q("cargo-0"), root, root.Q("mount-3").worldBound.center);
                stage = 1; return;
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
            if (snapshot.Mounted[3]?.Template != 2656 || snapshot.Cargo[1] != null) return;
            Debug.Log("PASS: cargo reordered, magazine loaded, and missile rounds consumed in server snapshots. Pointer drags unloaded ammunition and equipment over occupied cargo, then equipped the weapon again.");
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
            var input = new Event { type = EventType.MouseDown, button = 0, mousePosition = source.worldBound.center };
            using (PointerDownEvent evt = PointerDownEvent.GetPooled(input)) source.SendEvent(evt);
            input.type = EventType.MouseDrag; input.mousePosition = destination;
            using (PointerMoveEvent evt = PointerMoveEvent.GetPooled(input)) capture.SendEvent(evt);
            input.type = EventType.MouseUp;
            using (PointerUpEvent evt = PointerUpEvent.GetPooled(input)) capture.SendEvent(evt);
        }

        /// <summary>Exit the owned Editor with the first concrete failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
