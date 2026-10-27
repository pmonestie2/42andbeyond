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
    /// <summary>Verify native cargo movement, launcher loading, and spent rounds in Unity.</summary>
    [InitializeOnLoad]
    public static class LoadoutVisualSmoke
    {
        private static int stage;
        private static bool connected, captured;
        private static uint mobId;
        private static double deadline, captureAt;

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
                  window.maximized = true; window.Focus(); }
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
                snapshot.Mounted[3]?.Type != 15) return;
            if (stage == 0)
            {
                if (snapshot.Cargo[0]?.Template != 507 || snapshot.Cargo[1]?.Template != 5931)
                { Fail("Disposable cargo was not published by the server."); return; }
                client.MoveInventory(new InventoryMove(1, 0, 2, 3, 20));
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
                client.SelectContact(mobId); client.ToggleWeapons(mobId);
                stage = 3; return;
            }
            if (stage == 3)
            {
                if (snapshot.Ammo[3] == null || snapshot.Ammo[3].Quantity >= 20) return;
                FlightHud hud = typeof(SpaceClient).GetField("hud",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as FlightHud;
                if (hud == null) { Fail("Loadout HUD was not initialized."); return; }
                typeof(FlightHud).GetField("equipmentOpen",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(hud, true);
                client.StopWeapons();
                stage = 4; return;
            }
            string path = "Builds/verification/loadout-ammo.png";
            if (!captured)
            { Directory.CreateDirectory("Builds/verification");
              if (File.Exists(path)) File.Delete(path);
              ScreenCapture.CaptureScreenshot(path); captured = true;
              captureAt = EditorApplication.timeSinceStartup + .6; return; }
            if (!File.Exists(path) || EditorApplication.timeSinceStartup < captureAt) return;
            Debug.Log("PASS: cargo reordered, magazine loaded, and missile rounds consumed in server snapshots.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
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
