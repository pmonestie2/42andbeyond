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
    /// <summary>Capture one server-launched missile in flight and its resolved impact.</summary>
    [InitializeOnLoad]
    public static class ProjectileVisualSmoke
    {
        private static int stage;
        private static uint mobId;
        private static double deadline, captureAt;
        private static bool connected;

        /// <summary>Register only when a disposable visual run opts in.</summary>
        static ProjectileVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                    "--42andbeyond-projectile-smoke") < 0) return;
            deadline = EditorApplication.timeSinceStartup + 100;
            EditorApplication.update += Tick;
        }

        /// <summary>Follow native launch, travel and damage reports in that order.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Projectile smoke timed out at stage " + stage); return; }
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
                ShotTravelKind kind = ShotPresentation.Classify(shot, snapshot.PlayerId,
                    snapshot.Mounted, snapshot.Ammo);
                if (kind != ShotTravelKind.Missile || shot.Duration < 900)
                { Fail($"Expected native traveling missile, got {kind} / {shot.Duration} ms."); return; }
                client.StopWeapons();
                captureAt = EditorApplication.timeSinceStartup + shot.Duration / 2500.0;
                stage = 2; return;
            }
            if (stage == 2)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/projectile-travel.png");
                stage = 3; return;
            }
            if (stage == 3)
            {
                DamageEvent result = snapshot.Damages.FirstOrDefault(item =>
                    item.Source == snapshot.PlayerId && item.Target == mobId);
                if (result == null) return;
                ScreenCapture.CaptureScreenshot("Builds/verification/projectile-result.png");
                Debug.Log($"PASS: server missile launch traveled before {result.Outcome} impact.");
                captureAt = EditorApplication.timeSinceStartup + .6;
                stage = 4; return;
            }
            if (stage != 4 || EditorApplication.timeSinceStartup < captureAt) return;
            if (!File.Exists("Builds/verification/projectile-travel.png") ||
                !File.Exists("Builds/verification/projectile-result.png"))
            { Fail("Travel or result capture missing."); return; }
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Exit this owned Editor with a concrete fixture failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
