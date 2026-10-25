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
    /// <summary>Capture the native Recharge Shields ability and its acknowledged self cast.</summary>
    [InitializeOnLoad]
    public static class RechargeAbilityVisualSmoke
    {
        private static bool connected, chosen, activated, captured, hull, ranks;
        private static double deadline, nextCapture;

        /// <summary>Register this disposable visual check only for its opt-in flag.</summary>
        static RechargeAbilityVisualSmoke()
        {
            string[] args = Environment.GetCommandLineArgs();
            hull = Array.IndexOf(args, "--42andbeyond-hull-smoke") >= 0;
            ranks = Array.IndexOf(args, "--42andbeyond-support-ranks-smoke") >= 0;
            if (!hull && !ranks && Array.IndexOf(args, "--42andbeyond-recharge-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Wait for server ability state, cast once, and capture the open panel.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Recharge Shields visual capture timed out."); return; }
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
            if (!connected) { connected = true; client.StartLocalSession("127.0.0.1", "ptt", "ptt"); }
            if (!chosen && client.ChoosingCharacter)
            { chosen = true; client.ChooseCharacter(client.Characters[0].Slot); }
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || snapshot.Station != null ||
                !snapshot.AvailableAbilities.Contains(ranks ? 32u : hull ? 95u : 89u) ||
                ranks && (!snapshot.AvailableAbilities.Contains(31u) ||
                    !snapshot.AvailableAbilities.Contains(91u)) ||
                hull && !snapshot.AvailableAbilities.Contains(77u)) return;
            AbilityHud hud = typeof(SpaceClient).GetField("abilityHud",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as AbilityHud;
            FieldInfo open = typeof(AbilityHud).GetField("open",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (hud == null || open == null) { Fail("Ability panel unavailable."); return; }
            open.SetValue(hud, true);
            if (!activated)
            {
                activated = true;
                client.ActivateAbilityFor(ranks ? 32u : hull ? 95u : 89u, snapshot.PlayerId);
                nextCapture = EditorApplication.timeSinceStartup + 1;
                return;
            }
            if (!client.Status.Contains(hull ? "Hull repair completed." :
                "Recharge Shields completed.") ||
                EditorApplication.timeSinceStartup < nextCapture) return;
            string path = ranks ? "Builds/verification/support-ranks.png" :
                hull ? "Builds/verification/hull-ability.png" :
                "Builds/verification/recharge-ability.png";
            if (!captured)
            { Directory.CreateDirectory("Builds/verification");
              ScreenCapture.CaptureScreenshot(path); captured = true;
              nextCapture = EditorApplication.timeSinceStartup + .6; return; }
            if (!File.Exists(path) || EditorApplication.timeSinceStartup < nextCapture) return;
            Debug.Log(ranks ? "PASS: rank 1/5 support abilities visible and self cast completed." :
                hull ? "PASS: native Patch/Repair Hull visible and self cast completed." :
                "PASS: native Recharge Shields visible and self cast completed.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Exit the owned editor when the disposable check fails.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
