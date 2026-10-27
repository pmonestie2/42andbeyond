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
        private static bool kinetic;

        /// <summary>Register only when a disposable visual run opts in.</summary>
        static ProjectileVisualSmoke()
        {
            kinetic = Array.IndexOf(Environment.GetCommandLineArgs(),
                "--42andbeyond-kinetic-smoke") >= 0;
            if (!kinetic && Array.IndexOf(Environment.GetCommandLineArgs(),
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
                ShotTravelKind expected = kinetic ? ShotTravelKind.Projectile : ShotTravelKind.Missile;
                if (kind != expected || shot.Duration < (kinetic ? 120 : 900))
                { Fail($"Expected native traveling {expected}, got {kind} / {shot.Duration} ms."); return; }
                captureAt = EditorApplication.timeSinceStartup + shot.Duration / 2500.0;
                stage = 2; return;
            }
            if (stage == 2)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                GameObject round = GameObject.Find(kinetic ? "Kinetic projectile" : "Guided missile");
                if (round == null || round.GetComponentsInChildren<MeshRenderer>().Length <
                    (kinetic ? 2 : 4) || !kinetic &&
                    round.GetComponentInChildren<ParticleSystem>() == null)
                { Fail("Projectile body, fins, or missile smoke missing during server-published flight."); return; }
                if (!kinetic && round.GetComponentInChildren<ParticleSystem>().particleCount == 0)
                { Fail("Missile exhaust emitted no smoke during flight."); return; }
                Camera camera = UnityEngine.Object.FindObjectsByType<Camera>(
                    FindObjectsSortMode.None).FirstOrDefault(item => item.enabled);
                if (camera != null)
                {
                    Vector3 screen = camera.WorldToScreenPoint(round.transform.position);
                    Debug.Log($"{(kinetic ? "Projectile" : "Missile")} screenshot position: {screen}; world {round.transform.position}");
                }
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/" +
                    (kinetic ? "kinetic" : "projectile") + "-travel.png");
                stage = 3; return;
            }
            if (stage == 3)
            {
                DamageEvent result = snapshot.Damages.FirstOrDefault(item =>
                    item.Source == snapshot.PlayerId && item.Target == mobId && item.Amount > 0);
                if (result == null) return;
                client.StopWeapons();
                captureAt = EditorApplication.timeSinceStartup + .12;
                stage = 4; return;
            }
            if (stage == 4)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                if (!kinetic && GameObject.Find("Missile detonation") == null)
                { Fail("Server-confirmed missile detonation sprite was not rendered."); return; }
                ScreenCapture.CaptureScreenshot("Builds/verification/" +
                    (kinetic ? "kinetic" : "projectile") + "-result.png");
                Debug.Log($"PASS: server {(kinetic ? "projectile" : "missile")} launch traveled before confirmed impact.");
                captureAt = EditorApplication.timeSinceStartup + .6;
                stage = 5; return;
            }
            if (stage != 5 || EditorApplication.timeSinceStartup < captureAt) return;
            string prefix = "Builds/verification/" + (kinetic ? "kinetic" : "projectile");
            if (!File.Exists(prefix + "-travel.png") || !File.Exists(prefix + "-result.png"))
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
