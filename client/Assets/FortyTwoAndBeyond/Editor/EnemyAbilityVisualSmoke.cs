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
    /// <summary>Capture an acknowledged Shield Drain cast against a disposable MOB.</summary>
    [InitializeOnLoad]
    public static class EnemyAbilityVisualSmoke
    {
        private static bool connected, chosen, cast, captured;
        private static uint target;
        private static float startingShield;
        private static double deadline, captureAt;

        /// <summary>Register only for the explicit enemy ability visual flag.</summary>
        static EnemyAbilityVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-enemy-ability-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Cast immediately after target input and capture the server-confirmed hit.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Shield Drain graphical cast timed out."); return; }
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
            if (!connected) { connected = true; client.StartLocalSession("127.0.0.1", "pje", "pje"); }
            if (!chosen && client.ChoosingCharacter)
            { chosen = true; client.ChooseCharacter(client.Characters[0].Slot); }
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || snapshot.Station != null ||
                !snapshot.AvailableAbilities.Contains(106u)) return;
            if (!cast)
            {
                ShipActor mob = snapshot.Mobs.FirstOrDefault(actor => actor.HasPosition && actor.Shield > 0);
                if (mob == null) return;
                target = mob.Id; startingShield = mob.Shield;
                AbilityHud hud = typeof(SpaceClient).GetField("abilityHud",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as AbilityHud;
                FieldInfo open = typeof(AbilityHud).GetField("open",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (hud == null || open == null) { Fail("Ability panel unavailable."); return; }
                open.SetValue(hud, true);
                cast = true; client.SelectContact(target); client.ActivateAbility(106);
                return;
            }
            if (!snapshot.Mobs.Any(actor => actor.Id == target && actor.Shield < startingShield)) return;
            string path = "Builds/verification/shield-drain.png";
            if (!captured)
            { Directory.CreateDirectory("Builds/verification");
              ScreenCapture.CaptureScreenshot(path); captured = true;
              captureAt = EditorApplication.timeSinceStartup + .6; return; }
            if (!File.Exists(path) || EditorApplication.timeSinceStartup < captureAt) return;
            Debug.Log("PASS: Shield Drain followed native target acknowledgment and damaged MOB shields.");
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
