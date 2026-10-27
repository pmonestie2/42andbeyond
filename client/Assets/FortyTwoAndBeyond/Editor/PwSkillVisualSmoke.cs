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
    /// <summary>Exercise Shield Sap and Nova against one disposable large target.</summary>
    [InitializeOnLoad]
    public static class PwSkillVisualSmoke
    {
        private static int stage;
        private static bool connected, chosen, capturedSap, capturedNova;
        private static uint target;
        private static float initialShield;
        private static double deadline, nextAt;

        /// <summary>Register only for the explicit PW skill smoke flag.</summary>
        static PwSkillVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-pw-skill-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 95;
            EditorApplication.update += Tick;
        }

        /// <summary>Cast through the normal client and confirm target effects in Unity.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("PW skills timed out at stage " + stage); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameView != null)
                { EditorWindow window = EditorWindow.GetWindow(gameView);
                  window.maximized = true; window.Focus(); }
                EditorApplication.EnterPlaymode(); return;
            }
            if (!EditorApplication.isPlaying) return;
            SpaceClient client = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            if (!connected)
            { connected = true; client.StartLocalSession("127.0.0.1", "ppw", "ppw"); }
            if (!chosen && client.ChoosingCharacter)
            { chosen = true; client.ChooseCharacter(client.Characters[0].Slot); }
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || snapshot.Station != null ||
                !snapshot.AvailableAbilities.Contains(55u) ||
                !snapshot.AvailableAbilities.Contains(110u)) return;
            if (stage == 0)
            {
                ShipActor mob = snapshot.Mobs.FirstOrDefault(actor =>
                    actor.HasPosition && actor.Shield > .5f);
                if (mob == null) return;
                target = mob.Id; initialShield = mob.Shield;
                client.SelectContact(target);
                client.ActivateAbility(55);
                stage = 1;
                nextAt = EditorApplication.timeSinceStartup + 5;
                return;
            }
            if (stage == 1)
            {
                ShipActor mob = snapshot.Mobs.FirstOrDefault(actor => actor.Id == target);
                if (!capturedSap && GameObject.Find("Shield Sap target orb") != null)
                {
                    Directory.CreateDirectory("Builds/verification");
                    ScreenCapture.CaptureScreenshot("Builds/verification/pw-sapping-sphere.png");
                    capturedSap = true;
                }
                if (mob != null && mob.Shield < initialShield &&
                    snapshot.Shots.Any(shot => shot.Asset == 139 && shot.Target == target) &&
                    capturedSap)
                {
                    nextAt = EditorApplication.timeSinceStartup + .8;
                    stage = 2;
                }
                else if (EditorApplication.timeSinceStartup > nextAt)
                    Fail("Group Sapping Sphere did not drain the selected MOB: " + client.Status);
                return;
            }
            if (stage == 2)
            {
                if (EditorApplication.timeSinceStartup < nextAt) return;
                client.ActivateAbility(110);
                stage = 3;
                nextAt = EditorApplication.timeSinceStartup + 6;
                return;
            }
            if (stage == 3)
            {
                bool confirmed = snapshot.ActiveBuffs.Any(buff =>
                    buff.Slot == 15 && buff.Name == "Shield_Inversion") &&
                    snapshot.Shots.Any(shot => shot.Asset == 98 && shot.Target == target);
                GameObject visual = GameObject.Find("Shield Nova target stream");
                if (confirmed && visual != null &&
                    visual.GetComponentsInChildren<LineRenderer>().Length >= 3)
                {
                    ScreenCapture.CaptureScreenshot("Builds/verification/pw-shield-nova.png");
                    capturedNova = true;
                    nextAt = EditorApplication.timeSinceStartup + .8;
                    stage = 4;
                }
                else if (EditorApplication.timeSinceStartup > nextAt)
                    Fail("Shield Nova did not reach its target: " + client.Status);
                return;
            }
            if (stage == 4 && EditorApplication.timeSinceStartup >= nextAt)
            {
                if (!capturedSap || !capturedNova ||
                    !File.Exists("Builds/verification/pw-sapping-sphere.png") ||
                    !File.Exists("Builds/verification/pw-shield-nova.png"))
                { Fail("PW skill screenshots were not written."); return; }
                Debug.Log("PASS: PW Group Sapping Sphere drained the selected MOB and " +
                    "Shield Nova produced a server-targeted beam and active Unity visual.");
                EditorApplication.update -= Tick;
                EditorApplication.Exit(0);
            }
        }

        /// <summary>Stop this owned Editor after a concrete skill failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
