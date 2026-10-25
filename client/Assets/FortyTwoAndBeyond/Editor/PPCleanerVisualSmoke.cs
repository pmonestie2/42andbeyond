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
    /// <summary>Capture the server-owned Cleaner reward and docked skill rank.</summary>
    [InitializeOnLoad]
    public static class PPCleanerVisualSmoke
    {
        private static bool connected, chosen, roomRequested, opened, captured;
        private static int stage;
        private static ulong initialCredits;
        private static double deadline, captureAt;

        /// <summary>Register only for an explicit disposable Cleaner capture.</summary>
        static PPCleanerVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-pp-cleaner-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Turn in the authored mission and display its trained skill at station.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("PP Cleaner capture timed out."); return; }
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
            if (!connected)
            { connected = true; client.StartLocalSession("127.0.0.1", "rookiepp", "rookiepp"); }
            if (!chosen && client.ChoosingCharacter)
            { chosen = true; client.ChooseCharacter(client.Characters[0].Slot); }
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || snapshot.Station?.Id != 10371) return;
            if (stage == 0)
            {
                MissionState mission = snapshot.Missions.FirstOrDefault(item =>
                    item?.Name.StartsWith("The Cleaner") == true);
                if (mission?.Stage != 2) return;
                StationContact contact = snapshot.Station.Contacts.FirstOrDefault(item => item.Id == 372);
                if (contact == null) { Fail("Sub Commander Brook is absent from Arx Bursa."); return; }
                if (snapshot.Station.CurrentRoom != contact.Room)
                { if (!roomRequested) { roomRequested = true; client.EnterStationRoom(contact.Room); }
                  return; }
                if (!opened) { opened = true; initialCredits = snapshot.Credits; client.OpenNpc(372); }
                if (snapshot.Dialogue?.Choices.Any(choice => choice.Node == 2) != true) return;
                client.ChooseNpc(2); stage = 1; return;
            }
            if (stage == 1)
            {
                if (snapshot.Missions.Any(item => item?.Name.StartsWith("The Cleaner") == true) ||
                    snapshot.Credits < initialCredits + 2500 ||
                    !snapshot.Skills.Any(skill => skill.Id == 40 && skill.Rank >= 1) ||
                    !snapshot.Cargo.Any(item => item?.Template == 3023) ||
                    !snapshot.Cargo.Any(item => item?.Template == 515)) return;
                if (!Capture("pp-cleaner-reward.png")) return;
                client.ChooseNpc(230);
                FieldInfo hudField = typeof(SpaceClient).GetField("abilityHud",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                AbilityHud hud = hudField?.GetValue(client) as AbilityHud;
                if (hud == null) { Fail("Skills panel is unavailable after Cleaner turn-in."); return; }
                typeof(AbilityHud).GetField("open", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(hud, true);
                typeof(AbilityHud).GetField("scroll", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(hud, new Vector2(0, 1250));
                captured = false; stage = 2; return;
            }
            if (snapshot.Dialogue != null) return;
            if (!Capture("pp-cleaner-skills.png")) return;
            Debug.Log("PASS: Cleaner reward and Projectile Weapon rank visible while docked.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <returns>bool: Whether Unity finished saving a fresh screenshot.</returns>
        private static bool Capture(string name)
        {
            string path = "Builds/verification/" + name;
            if (!captured)
            { Directory.CreateDirectory("Builds/verification");
              if (File.Exists(path)) File.Delete(path);
              ScreenCapture.CaptureScreenshot(path); captured = true;
              captureAt = EditorApplication.timeSinceStartup + .6; return false; }
            return File.Exists(path) && EditorApplication.timeSinceStartup >= captureAt;
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
