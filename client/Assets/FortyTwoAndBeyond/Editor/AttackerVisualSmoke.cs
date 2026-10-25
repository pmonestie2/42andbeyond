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
    /// <summary>Capture native MOB aggro followed by the Unity target-attacker action.</summary>
    [InitializeOnLoad]
    public static class AttackerVisualSmoke
    {
        private static int stage;
        private static bool connected, chosen;
        private static uint attackerId;
        private static double deadline, captureAt;

        /// <summary>Attach only to an explicitly owned disposable combat fixture.</summary>
        static AttackerVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-attacker-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Wait for the server's 0x8b add and then target that visible MOB.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Attacker smoke timed out at stage " + stage); return; }
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
            if (snapshot?.Ready != true || !snapshot.HasPosition) return;
            if (stage == 0)
            {
                ShipActor attacker = snapshot.Mobs.FirstOrDefault(mob =>
                    snapshot.Attackers.Contains(mob.Id) && mob.HasPosition);
                if (attacker == null) return;
                attackerId = attacker.Id;
                client.TargetNextAttacker();
                stage = 1; return;
            }
            if (stage == 1)
            {
                if (snapshot.SelectedTargetId != attackerId) return;
                Directory.CreateDirectory("Builds/verification");
                captureAt = EditorApplication.timeSinceStartup + .7;
                stage = 2; return;
            }
            if (stage == 2 && EditorApplication.timeSinceStartup >= captureAt)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/attacker-target.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 3; return;
            }
            if (stage != 3 || EditorApplication.timeSinceStartup < captureAt) return;
            if (!File.Exists("Builds/verification/attacker-target.png"))
            { Fail("Attacker target screenshot missing."); return; }
            Debug.Log("PASS: native 0x8b attacker selected through Unity and acknowledged as target.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Stop only this owned Editor with the fixture's failure reason.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
