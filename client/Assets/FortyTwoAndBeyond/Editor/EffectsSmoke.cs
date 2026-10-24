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
    /// <summary>Opt-in graphical check for authoritative combat, mining, and warp cues.</summary>
    [InitializeOnLoad]
    public static class EffectsSmoke
    {
        private static int stage;
        private static uint mobId, resourceId;
        private static double deadline, nextAt;
        private static bool connected;

        /// <summary>Register only for a disposable visual server run.</summary>
        static EffectsSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-effects-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 130;
            EditorApplication.update += Tick;
        }

        /// <summary>Drive one server-owned action at a time and capture the rendered result.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline) { Fail("Effects smoke timed out at stage " + stage); return; }
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
                ShipActor mob = snapshot.Mobs.Where(actor => actor.HasPosition && actor.Hostile)
                    .OrderBy(actor => actor.Position.Distance(snapshot.Position)).FirstOrDefault();
                MapObject resource = snapshot.Objects.FirstOrDefault(item => item.Kind == 38);
                if (mob == null || resource == null) return;
                mobId = mob.Id; resourceId = resource.Id;
                client.SelectContact(mobId); client.ToggleWeapons(mobId);
                stage = 1; return;
            }
            if (stage == 1)
            {
                if (EditorApplication.timeSinceStartup >= nextAt)
                {
                    ShipActor mob = snapshot.Mobs.FirstOrDefault(actor => actor.Id == mobId);
                    ItemView weapon = snapshot.Mounted.Length > 3 ? snapshot.Mounted[3] : null;
                    Debug.Log($"EFFECTS WAIT: shots={snapshot.Shots.Length} damage={snapshot.Damages.Length} " +
                        $"mobRange={mob?.Position.Distance(snapshot.Position):0} " +
                        $"weapon={weapon?.Name} range={weapon?.Range:0} firing={snapshot.WeaponsFiring} " +
                        $"status={client.Status} messages={string.Join(" | ", snapshot.Messages.TakeLast(3))}");
                    nextAt = EditorApplication.timeSinceStartup + 4;
                }
                if (!snapshot.Shots.Any(shot => shot.Source == snapshot.PlayerId && shot.Target == mobId)) return;
                ScreenCapture.CaptureScreenshot("Builds/verification/combat-effects.png");
                nextAt = EditorApplication.timeSinceStartup + .8; stage = 2; return;
            }
            if (stage == 2 && EditorApplication.timeSinceStartup >= nextAt)
            {
                client.StopWeapons(); client.OpenResource(resourceId);
                stage = 3; return;
            }
            if (stage == 3)
            {
                ResourceView resource = snapshot.Resources.FirstOrDefault(item => item.Id == resourceId);
                if (!snapshot.ResourceWindowOpen || resource == null) return;
                ItemView item = resource.Items.FirstOrDefault(entry => entry != null);
                if (item == null) { Fail("Nearby ore has no prospectable item."); return; }
                client.ProspectResource(item.Slot);
                stage = 4; return;
            }
            if (stage == 4)
            {
                if (snapshot.MiningActivity?.TargetId != resourceId) return;
                ScreenCapture.CaptureScreenshot("Builds/verification/mining-effects.png");
                nextAt = EditorApplication.timeSinceStartup + 1; stage = 5; return;
            }
            if (stage == 5 && EditorApplication.timeSinceStartup >= nextAt)
            {
                client.CloseResource(); client.WarpSelected();
                stage = 6; return;
            }
            if (stage == 6)
            {
                if (snapshot.WarpPhase != WarpPhase.Traveling) return;
                nextAt = EditorApplication.timeSinceStartup + .45;
                stage = 8; return;
            }
            if (stage == 8)
            {
                if (snapshot.WarpPhase != WarpPhase.Traveling ||
                    EditorApplication.timeSinceStartup < nextAt) return;
                ScreenCapture.CaptureScreenshot("Builds/verification/warp-effects.png");
                nextAt = EditorApplication.timeSinceStartup + 1; stage = 7; return;
            }
            if (stage != 7 || EditorApplication.timeSinceStartup < nextAt) return;
            foreach (string name in new[] { "combat-effects", "mining-effects", "warp-effects" })
                if (!File.Exists("Builds/verification/" + name + ".png"))
                { Fail("Missing capture: " + name); return; }
            Debug.Log("PASS: authoritative combat, mining, and warp effects captured.");
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
