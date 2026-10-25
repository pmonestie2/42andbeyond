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
    /// <summary>Capture a PW's server-published typed deflects in disposable space.</summary>
    [InitializeOnLoad]
    public static class DefenseVisualSmoke
    {
        private static bool connected, chosen, displayed;
        private static double deadline, captureAt;

        /// <summary>Register only for the explicit defense graphical smoke flag.</summary>
        static DefenseVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-defense-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Show all seven authoritative Combat Trance deflect values.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Defense capture timed out."); return; }
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
            if (!chosen && client.ChoosingCharacter)
            { chosen = true; client.ChooseCharacter(client.Characters[0].Slot); }
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || snapshot.Station != null ||
                !snapshot.Defense.Known || snapshot.Defense.Deflects.Count != 7 ||
                snapshot.Defense.Deflects.Any(value => value < 37)) return;
            if (!displayed)
            {
                DefenseHud hud = typeof(SpaceClient).GetField("defenseHud",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as DefenseHud;
                FieldInfo open = typeof(DefenseHud).GetField("open",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (hud == null || open == null) { Fail("Defense HUD unavailable."); return; }
                open.SetValue(hud, true);
                Directory.CreateDirectory("Builds/verification");
                displayed = true; captureAt = EditorApplication.timeSinceStartup + .8;
                return;
            }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            string path = "Builds/verification/defense-typed.png";
            if (!File.Exists(path))
            { ScreenCapture.CaptureScreenshot(path); captureAt = EditorApplication.timeSinceStartup + .5; return; }
            Debug.Log("PASS: native PW Combat Trance deflects visible in defense panel.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Stop this owned Editor on a concrete defense fixture failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
