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
    /// <summary>Verify two server-owned wreck stacks are tractored in sequence.</summary>
    [InitializeOnLoad]
    public static class CollectAllVisualSmoke
    {
        private static int stage;
        private static uint corpseId;
        private static bool connected;
        private static double deadline, captureAt;

        /// <summary>Register only for an owned two-stack wreck fixture.</summary>
        static CollectAllVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                    "--42andbeyond-collect-all-smoke") < 0) return;
            deadline = EditorApplication.timeSinceStartup + 100;
            EditorApplication.update += Tick;
        }

        /// <summary>Require each next request to follow an authoritative depletion event.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Collect All timed out at stage " + stage); return; }
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
            if (stage == 0)
            {
                MapObject corpse = snapshot.Objects.FirstOrDefault(item => item.Kind == 25);
                if (corpse == null) return;
                corpseId = corpse.Id;
                FieldInfo hudField = typeof(SpaceClient).GetField("hud", BindingFlags.Instance |
                    BindingFlags.NonPublic);
                FlightHud hud = hudField?.GetValue(client) as FlightHud;
                if (hud == null) { Fail("Flight HUD is unavailable."); return; }
                hud.SelectWreck(corpseId);
                stage = 1; return;
            }
            if (stage == 1)
            {
                if (snapshot.SelectedTargetId != corpseId ||
                    snapshot.Verbs.Available(corpseId, 3) != true) return;
                client.OpenLoot(corpseId);
                stage = 2; return;
            }
            if (stage == 2)
            {
                CorpseLoot contents = snapshot.Loot.FirstOrDefault(item => item.Id == corpseId);
                if (snapshot.OpenLootId != corpseId ||
                    contents?.Items.Count(item => item != null) != 2) return;
                client.CollectAllLoot(corpseId);
                if (!client.LootAllActive) { Fail("Collect All did not begin."); return; }
                stage = 3; return;
            }
            if (stage == 3)
            {
                if (snapshot.LootActivity?.CorpseId != corpseId) return;
                stage = 4; return;
            }
            if (stage == 4)
            {
                if (client.LootAllCollectedStacks != 1 ||
                    snapshot.LootActivity?.CorpseId != corpseId) return;
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/loot-all-second.png");
                stage = 5; return;
            }
            if (stage == 5)
            {
                if (client.LootAllActive ||
                    !snapshot.Cargo.Any(item => item?.Template == 5931 && item.Quantity == 2) ||
                    !snapshot.Cargo.Any(item => item?.Template == 5932 && item.Quantity == 3)) return;
                Debug.Log("PASS: two full wreck stacks collected by sequential native tractors.");
                captureAt = EditorApplication.timeSinceStartup + .6;
                stage = 6; return;
            }
            if (stage != 6 || EditorApplication.timeSinceStartup < captureAt) return;
            if (!File.Exists("Builds/verification/loot-all-second.png"))
            { Fail("Second-stack capture missing."); return; }
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Exit the disposable Editor with a concrete failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
