using System;
using System.IO;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using CharacterRecord = FortyTwoAndBeyond.Protocol.CharacterInfo;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Capture a fresh pilot's server-owned class and track levels in the picker.</summary>
    [InitializeOnLoad]
    public static class CharacterRosterVisualSmoke
    {
        private static bool connected, captured;
        private static double deadline, captureAt;

        /// <summary>Register only for the explicit disposable roster smoke flag.</summary>
        static CharacterRosterVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-roster-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 60;
            EditorApplication.update += Tick;
        }

        /// <summary>Wait for the native character list and save its visible picker card.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Native-progression roster capture timed out."); return; }
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
            if (!connected) { connected = true; client.StartLocalSession("127.0.0.1", "rookie", "rookie"); }
            if (client.LoginError != null) { Fail(client.LoginError); return; }
            if (!client.ChoosingCharacter) return;
            CharacterRecord[] pilots = client.Characters;
            if (pilots.Length != 1 || pilots[0].ClassName != "Terran Scout" ||
                pilots[0].CombatLevel != 0 || pilots[0].TradeLevel != 0 ||
                pilots[0].ExploreLevel != 0)
            { Fail("Native roster did not expose the level-zero Scout."); return; }
            string path = "Builds/verification/new-pilot-roster.png";
            if (!captured)
            { Directory.CreateDirectory("Builds/verification");
              ScreenCapture.CaptureScreenshot(path); captured = true;
              captureAt = EditorApplication.timeSinceStartup + .6; return; }
            if (!File.Exists(path) || EditorApplication.timeSinceStartup < captureAt) return;
            Debug.Log("PASS: native level-zero Scout class and track levels visible in picker.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Exit the owned Editor after a concrete disposable failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
