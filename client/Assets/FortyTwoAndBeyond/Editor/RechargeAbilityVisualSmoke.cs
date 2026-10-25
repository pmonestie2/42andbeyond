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
        private static bool connected, chosen, activated, captured;
        private static double deadline, nextCapture;

        /// <summary>Register this disposable visual check only for its opt-in flag.</summary>
        static RechargeAbilityVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-recharge-smoke") < 0)
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
                !snapshot.AvailableAbilities.Contains(89u)) return;
            AbilityHud hud = typeof(SpaceClient).GetField("abilityHud",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as AbilityHud;
            FieldInfo open = typeof(AbilityHud).GetField("open",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (hud == null || open == null) { Fail("Ability panel unavailable."); return; }
            open.SetValue(hud, true);
            if (!activated)
            {
                activated = true; client.ActivateAbilityFor(89, snapshot.PlayerId);
                nextCapture = EditorApplication.timeSinceStartup + 1;
                return;
            }
            if (!client.Status.Contains("Recharge Shields completed.") ||
                EditorApplication.timeSinceStartup < nextCapture) return;
            string path = "Builds/verification/recharge-ability.png";
            if (!captured)
            { Directory.CreateDirectory("Builds/verification");
              ScreenCapture.CaptureScreenshot(path); captured = true;
              nextCapture = EditorApplication.timeSinceStartup + .6; return; }
            if (!File.Exists(path) || EditorApplication.timeSinceStartup < nextCapture) return;
            Debug.Log("PASS: native Recharge Shields visible and self cast completed.");
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
