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
    /// <summary>Capture one native missile flight through shield, hull, and wreck creation.</summary>
    [InitializeOnLoad]
    public static class CombatMomentVisualSmoke
    {
        private static int stage;
        private static uint mobId;
        private static long lastDamage;
        private static double deadline, captureAt, ownShieldBendAt;
        private static bool connected, hullReady, ownShieldCaptured;

        /// <summary>Register only for an explicitly disposable combat visual run.</summary>
        static CombatMomentVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                    "--42andbeyond-combat-moment-smoke") < 0) return;
            deadline = EditorApplication.timeSinceStartup + 180;
            EditorApplication.update += Tick;
        }

        /// <summary>Observe server shot and damage stages before each screenshot.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Combat moment timed out at stage " + stage); return; }
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
            Directory.CreateDirectory("Builds/verification");
            if (!ownShieldCaptured && snapshot.Shield > 0 && snapshot.Damages.Any(item =>
                item.Target == snapshot.PlayerId && item.Amount > 0))
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/combat-own-shield.png");
                ownShieldCaptured = true;
                ownShieldBendAt = EditorApplication.timeSinceStartup + .45;
            }
            if (ownShieldBendAt > 0 && EditorApplication.timeSinceStartup >= ownShieldBendAt)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/combat-own-shield-bend.png");
                ownShieldBendAt = 0;
            }
            if (stage == 0)
            {
                ShipActor mob = snapshot.Mobs.FirstOrDefault(actor => actor.Hostile && actor.HasPosition);
                if (mob == null) return;
                mobId = mob.Id;
                client.SelectContact(mobId); client.ToggleWeapons(mobId);
                stage = 1; return;
            }
            if (stage == 1)
            {
                ShotEvent shot = snapshot.Shots.FirstOrDefault(item =>
                    item.Source == snapshot.PlayerId && item.Target == mobId);
                if (shot == null) return;
                if (ShotPresentation.Classify(shot, snapshot.PlayerId, snapshot.Mounted,
                        snapshot.Ammo) != ShotTravelKind.Missile)
                { Fail("Expected a native missile launch."); return; }
                captureAt = EditorApplication.timeSinceStartup + shot.Duration / 2500.0;
                stage = 2; return;
            }
            if (stage == 2)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                ScreenCapture.CaptureScreenshot("Builds/verification/combat-missile.png");
                stage = 3; return;
            }
            DamageEvent result = snapshot.Damages.FirstOrDefault(item =>
                item.Source == snapshot.PlayerId && item.Target == mobId &&
                item.Sequence > lastDamage && item.Amount > 0);
            if (result != null) lastDamage = result.Sequence;
            ShipActor target = snapshot.Mobs.FirstOrDefault(actor => actor.Id == mobId);
            if (stage == 3 && result != null && target != null && target.Shield > .001f)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/combat-shield.png");
                stage = 4; return;
            }
            if (stage == 4 && !hullReady && target != null && target.Shield <= .001f)
            { hullReady = true; return; }
            if (stage == 4 && hullReady && result != null && target != null &&
                target.Shield <= .001f && target.Hull > 0 && target.Hull < target.MaxHull)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/combat-hull.png");
                stage = 5; return;
            }
            if (stage == 4 && target == null && snapshot.Objects.Any(item => item.Kind == 25))
            {
                // A lethal hit can cross the entire hull between two snapshots.
                ScreenCapture.CaptureScreenshot("Builds/verification/combat-hull.png");
                stage = 5; return;
            }
            if (stage == 5 && target == null && snapshot.Objects.Any(item => item.Kind == 25))
            {
                client.StopWeapons();
                captureAt = EditorApplication.timeSinceStartup + .2;
                stage = 6; return;
            }
            if (stage == 6)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                if (GameObject.Find("Ship explosion fireball") == null)
                { Fail("Confirmed ship death did not create a fireball."); return; }
                ScreenCapture.CaptureScreenshot("Builds/verification/combat-breakup.png");
                captureAt = EditorApplication.timeSinceStartup + 1.1;
                stage = 7; return;
            }
            if (stage == 7)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                if (GameObject.Find("Ship explosion smoke") == null)
                { Fail("Confirmed ship death did not leave smoke."); return; }
                ScreenCapture.CaptureScreenshot("Builds/verification/combat-aftermath.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 8; return;
            }
            if (stage != 8 || EditorApplication.timeSinceStartup < captureAt) return;
            foreach (string name in new[] { "combat-missile", "combat-shield", "combat-own-shield",
                "combat-own-shield-bend",
                "combat-hull",
                "combat-breakup", "combat-aftermath" })
                if (!File.Exists("Builds/verification/" + name + ".png"))
                { Fail("Missing " + name + " screenshot."); return; }
            Debug.Log("PASS: native missile, shield, hull, and confirmed ship explosion captured.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>End this owned Editor when the native visual sequence fails.</summary>
        private static void Fail(string message)
        {
            Debug.LogError(message);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
