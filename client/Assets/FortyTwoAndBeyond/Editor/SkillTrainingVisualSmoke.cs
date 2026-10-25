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
    /// <summary>Capture a durable native skill purchase in a disposable progression account.</summary>
    [InitializeOnLoad]
    public static class SkillTrainingVisualSmoke
    {
        private static bool connected, chosen, trained, captured;
        private static double deadline, captureAt;

        /// <summary>Register only when the explicit skill-training smoke flag is present.</summary>
        static SkillTrainingVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-skill-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Train Beam Weapon once and capture the server-refreshed rank and points.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Skill training graphical purchase timed out."); return; }
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
            if (!connected) { connected = true; client.StartLocalSession("127.0.0.1", "cadet", "cadet"); }
            if (!chosen && client.ChoosingCharacter)
            { chosen = true; client.ChooseCharacter(client.Characters[0].Slot); }
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || snapshot.Station != null || snapshot.Experience == null)
                return;
            SkillRank beam = snapshot.Skills.FirstOrDefault(skill => skill.Id == 1);
            if (beam == null) return;
            AbilityHud hud = typeof(SpaceClient).GetField("abilityHud",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as AbilityHud;
            FieldInfo open = typeof(AbilityHud).GetField("open",
                BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo tab = typeof(AbilityHud).GetField("trainingTab",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (hud == null || open == null || tab == null)
            { Fail("Training panel unavailable."); return; }
            open.SetValue(hud, true); tab.SetValue(hud, true);
            if (!trained)
            {
                if (!beam.CanTrain || snapshot.Experience.AvailableSkillPoints != 1) return;
                trained = true; client.TrainSkill(1);
                return;
            }
            if (beam.Rank != 2 || snapshot.Experience.AvailableSkillPoints != 0) return;
            string path = "Builds/verification/skill-training.png";
            if (!captured)
            { Directory.CreateDirectory("Builds/verification");
              ScreenCapture.CaptureScreenshot(path); captured = true;
              captureAt = EditorApplication.timeSinceStartup + .6; return; }
            if (!File.Exists(path) || EditorApplication.timeSinceStartup < captureAt) return;
            Debug.Log("PASS: native Beam rank 2 and remaining points 0 visible after 0x57.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Exit the owned Editor on a concrete disposable failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
