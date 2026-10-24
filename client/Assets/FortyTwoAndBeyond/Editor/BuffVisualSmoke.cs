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
    /// <summary>Capture one server-confirmed timed device buff in the disposable editor.</summary>
    [InitializeOnLoad]
    public static class BuffVisualSmoke
    {
        private static int stage;
        private static double deadline, captureAt;
        private static bool connected;

        /// <summary>Register only for the explicit local timed-buff smoke.</summary>
        static BuffVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-buff-visual-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Activate the equipped Turbo Weapons device and capture its native visual.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Timed buff smoke timed out at stage " + stage); return; }
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
            if (!connected) { connected = true; client.StartLocalSession("127.0.0.1", "pje", "pje"); }
            if (client.ChoosingCharacter) client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || !snapshot.HasPosition) return;
            if (stage == 0)
            {
                if (!snapshot.Mounted.Any(item => item?.ActivationLabel == "Turbo Weapons"))
                { Fail("Disposable pje pilot has no mounted Turbo Weapons device."); return; }
                client.ActivateTurboWeapons();
                stage = 1;
                return;
            }
            if (stage == 1)
            {
                DeviceBuffView buff = snapshot.DeviceBuff;
                if (buff == null || buff.Name != "Turbo Weapons" || !buff.VisualActive ||
                    buff.VisualDescriptor != 604 || buff.RemainingSeconds <= 0) return;
                captureAt = EditorApplication.timeSinceStartup + .5;
                stage = 2;
                return;
            }
            if (stage == 2)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                if (GameObject.Find("Active device field") == null)
                { Fail("Native Turbo buff did not create the ship-local visual."); return; }
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/turbo-buff.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 3;
                return;
            }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            if (!File.Exists("Builds/verification/turbo-buff.png"))
            { Fail("Timed buff screenshot was not written."); return; }
            Debug.Log("PASS: native Turbo Weapons buff icon, timer, and ship field captured.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Exit the disposable editor with a concrete failure reason.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
