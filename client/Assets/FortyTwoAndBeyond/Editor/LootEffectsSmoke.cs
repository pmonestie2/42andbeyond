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
    /// <summary>Capture a real server-created wreck and its native tractor transfer.</summary>
    [InitializeOnLoad]
    public static class LootEffectsSmoke
    {
        private static int stage;
        private static uint corpseId;
        private static double deadline, nextAt;
        private static bool connected;

        /// <summary>Register only for the disposable loot graphical run.</summary>
        static LootEffectsSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-loot-effects-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 120;
            EditorApplication.update += Tick;
        }

        /// <summary>Kill the fixture MOB, open its wreck, and capture its tractor packet effect.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline) { Fail("Loot effect timed out at stage " + stage); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameView != null)
                {
                    EditorWindow window = EditorWindow.GetWindow(gameView);
                    window.maximized = true; window.Focus();
                }
                EditorApplication.EnterPlaymode();
                return;
            }
            if (!EditorApplication.isPlaying) return;
            SpaceClient client = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            if (!connected) { connected = true; client.StartLocalSession("127.0.0.1", "local", "local"); }
            if (client.ChoosingCharacter) client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || !snapshot.HasPosition) return;
            Directory.CreateDirectory("Builds/verification");
            if (stage == 0)
            {
                ShipActor mob = snapshot.Mobs.FirstOrDefault(actor => actor.HasPosition);
                if (mob == null) return;
                client.SelectContact(mob.Id);
                client.ToggleWeapons(mob.Id);
                stage = 1;
                return;
            }
            if (stage == 1)
            {
                MapObject corpse = snapshot.Objects.FirstOrDefault(item => item.Kind == 25);
                if (corpse == null) return;
                corpseId = corpse.Id;
                client.StopWeapons();
                Vector3 projected = client.Project(corpse.Position);
                if (projected.z > 0) client.SelectAtPointer(new Vector2(projected.x, projected.y));
                client.OpenLoot(corpseId);
                stage = 2;
                return;
            }
            if (stage == 2)
            {
                CorpseLoot contents = snapshot.Loot.FirstOrDefault(item => item.Id == corpseId);
                if (snapshot.OpenLootId != corpseId || contents == null) return;
                ItemView item = contents.Items.FirstOrDefault(entry => entry != null);
                if (item == null) { Fail("Wreck has no tractorable stock"); return; }
                client.TakeLoot(corpseId, item.Slot);
                stage = 3;
                return;
            }
            if (stage == 3)
            {
                if (snapshot.LootActivity?.CorpseId != corpseId) return;
                nextAt = EditorApplication.timeSinceStartup + .2;
                stage = 4;
                return;
            }
            if (stage == 4)
            {
                if (snapshot.LootActivity?.CorpseId != corpseId ||
                    EditorApplication.timeSinceStartup < nextAt) return;
                ScreenCapture.CaptureScreenshot("Builds/verification/loot-effects.png");
                nextAt = EditorApplication.timeSinceStartup + 1;
                stage = 5;
                return;
            }
            if (stage != 5 || EditorApplication.timeSinceStartup < nextAt) return;
            if (!File.Exists("Builds/verification/loot-effects.png"))
            { Fail("Missing loot graphical capture"); return; }
            Debug.Log("PASS: native wreck tractor effect captured from disposable server.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Exit the disposable Editor with a concrete check failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
