using System;
using System.IO;
using System.Reflection;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Capture the server-sent equipped effect page on a disposable pilot.</summary>
    [InitializeOnLoad]
    public static class GearEffectsVisualSmoke
    {
        private static bool connected, chosen, displayed;
        private static double deadline, captureAt;

        /// <summary>Register only for the explicit gear-effect smoke flag.</summary>
        static GearEffectsVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-gear-effects-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Open the selected system's catalog effects and capture one fresh frame.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Gear-effect capture timed out."); return; }
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
            if (snapshot?.Ready != true || snapshot.Station == null ||
                snapshot.Mounted[9]?.Template != 2089 || snapshot.Mounted[9].EquippedEffects.Count != 4)
                return;
            if (!displayed)
            {
                FlightHud hud = typeof(SpaceClient).GetField("hud",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as FlightHud;
                if (hud == null) { Fail("Equipment HUD unavailable."); return; }
                Set(hud, "equipmentOpen", true);
                Set(hud, "selectedMount", 9);
                Set(hud, "inspectMount", true);
                Set(hud, "inspectEffects", true);
                Set(hud, "effectScroll", Vector2.zero);
                Directory.CreateDirectory("Builds/verification");
                displayed = true; captureAt = EditorApplication.timeSinceStartup + 1;
                return;
            }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            string path = "Builds/verification/gear-equipped-effects.png";
            if (!File.Exists(path))
            { ScreenCapture.CaptureScreenshot(path); captureAt = EditorApplication.timeSinceStartup + .5; return; }
            Debug.Log("PASS: native four-effect item metadata visible in the loadout inspector.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Set one owned test-only HUD field for the disposable capture.</summary>
        private static void Set(FlightHud hud, string field, object value)
        {
            FieldInfo target = typeof(FlightHud).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (target == null) throw new MissingFieldException(typeof(FlightHud).Name, field);
            target.SetValue(hud, value);
        }

        /// <summary>Stop this owned Editor on a concrete capture failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
