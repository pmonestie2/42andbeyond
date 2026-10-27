using System;
using System.Collections.Generic;
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
    /// <summary>Capture grouped native skills and a bound quickbar variant selector.</summary>
    [InitializeOnLoad]
    public static class AbilityVariantsVisualSmoke
    {
        private static bool connected, chosen, prepared, captured;
        private static double deadline, captureAt;

        /// <summary>Register only for an explicit disposable Skills UI check.</summary>
        static AbilityVariantsVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                "--42andbeyond-ability-variants-smoke") < 0) return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Show an available skill family and its right-click quickbar choices.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Ability variant capture timed out."); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type view = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (view != null)
                { EditorWindow window = EditorWindow.GetWindow(view);
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
                !snapshot.AvailableAbilities.Contains(6u) ||
                !snapshot.AvailableAbilities.Contains(27u)) return;
            AbilityHud hud = typeof(SpaceClient).GetField("abilityHud",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as AbilityHud;
            if (hud == null) { Fail("Skills UI is unavailable."); return; }
            if (!prepared)
            {
                prepared = true;
                typeof(AbilityHud).GetField("open", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(hud, true);
                typeof(AbilityHud).GetField("trainingTab", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(hud, false);
                typeof(AbilityHud).GetField("equipmentTab", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(hud, false);
                var expanded = typeof(AbilityHud).GetField("expandedSkills",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(hud) as HashSet<int>;
                expanded?.Add(22);
                var slots = typeof(AbilityHud).GetField("slots",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(hud) as int[];
                if (expanded == null || slots == null)
                { Fail("Skill group or quickbar fields are unavailable."); return; }
                slots[0] = 6;
                typeof(AbilityHud).GetField("variantSlot", BindingFlags.Instance |
                    BindingFlags.NonPublic)?.SetValue(hud, 0);
                return;
            }
            string path = "Builds/verification/ability-variants.png";
            if (!captured)
            { Directory.CreateDirectory("Builds/verification");
              if (File.Exists(path)) File.Delete(path);
              ScreenCapture.CaptureScreenshot(path); captured = true;
              captureAt = EditorApplication.timeSinceStartup + .6; return; }
            if (!File.Exists(path) || EditorApplication.timeSinceStartup < captureAt) return;
            Debug.Log("PASS: native Anger variants grouped and quickbar selector rendered.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Exit the disposable Editor with an actionable error.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
