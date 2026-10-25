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
    /// <summary>Exercise the native Sharim offer and capture its accepted objective and faction PDA.</summary>
    [InitializeOnLoad]
    public static class Mission81VisualSmoke
    {
        private static int stage;
        private static bool connected, chosen, roomRequested, compact, offerCaptured;
        private static double deadline, captureAt;
        private static DialogueState priorDialogue;

        /// <returns>string: The screenshot path for the chosen Game View size.</returns>
        private static string CaptureName(string scene)
        { return "Builds/verification/mission81-" + scene + (compact ? "-compact" : "") + ".png"; }

        /// <summary>Start only for the owned disposable mission fixture.</summary>
        static Mission81VisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-mission81-smoke") < 0)
                return;
            compact = Array.IndexOf(Environment.GetCommandLineArgs(),
                "--42andbeyond-mission81-compact") >= 0;
            deadline = EditorApplication.timeSinceStartup + 110;
            EditorApplication.update += Tick;
        }

        /// <summary>Accept the advertised mission branch and inspect the worker snapshot.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Mission81 timed out at stage " + stage); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameView != null)
                {
                    EditorWindow window = compact ?
                        ScriptableObject.CreateInstance(gameView) as EditorWindow :
                        EditorWindow.GetWindow(gameView);
                    if (compact)
                    { window.Show(); window.position = new Rect(80, 75, 1180, 700); }
                    else window.maximized = true;
                    window.Focus();
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
            if (snapshot?.Ready != true || snapshot.Station == null || snapshot.Reputation == null) return;
            if (stage == 0)
            {
                if (snapshot.Missions.Any(mission => mission?.Name == "Defense of the Sharim"))
                { stage = 5; return; }
                if (snapshot.Station.CurrentRoom != 1)
                {
                    if (!roomRequested && snapshot.Station.RoomList.Any(room => room.Index == 1))
                    { roomRequested = true; client.EnterStationRoom(1); }
                }
                else stage = 1;
                return;
            }
            if (stage == 1)
            { client.OpenNpc(489); stage = 2; return; }
            if (stage >= 2 && stage <= 3)
            {
                DialogueState dialogue = snapshot.Dialogue;
                byte node = (byte)stage; // NPC open already displays source node 1.
                if (dialogue == null || dialogue == priorDialogue ||
                    !dialogue.Choices.Any(choice => choice.Node == node)) return;
                if (stage == 2 && dialogue.MissionKind != MissionDialogueKind.Offer)
                {
                    Fail("NPC489 did not offer mission81; kind=" + dialogue.MissionKind +
                        ", text=" + dialogue.Text + ", choices=" +
                        string.Join(" | ", dialogue.Choices.Select(choice => choice.Text)));
                    return;
                }
                if (stage == 2 && !offerCaptured)
                {
                    Directory.CreateDirectory("Builds/verification");
                    ScreenCapture.CaptureScreenshot(CaptureName("offer"));
                    offerCaptured = true; return;
                }
                if (stage == 2 && !File.Exists(CaptureName("offer"))) return;
                priorDialogue = dialogue;
                client.ChooseNpc(node);
                stage++; return;
            }
            if (stage == 4)
            {
                if (!snapshot.Missions.Any(mission => mission?.Name == "Defense of the Sharim")) return;
                if (snapshot.Dialogue != null) client.ChooseNpc(230);
                stage = 5; return;
            }
            if (stage == 5)
            {
                if (snapshot.Dialogue != null) return;
                FieldInfo field = typeof(SpaceClient).GetField("missionHud",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MissionHud hud = field?.GetValue(client) as MissionHud;
                if (hud == null) { Fail("Mission journal unavailable."); return; }
                typeof(MissionHud).GetField("open", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(hud, true);
                Directory.CreateDirectory("Builds/verification");
                captureAt = EditorApplication.timeSinceStartup + .5;
                stage = 6; return;
            }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            if (stage == 6)
            {
                ScreenCapture.CaptureScreenshot(CaptureName("objective"));
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 7; return;
            }
            if (stage == 7)
            {
                if (!File.Exists(CaptureName("objective"))) return;
                MissionHud hud = typeof(SpaceClient).GetField("missionHud",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as MissionHud;
                typeof(MissionHud).GetField("factionsTab", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(hud, true);
                captureAt = EditorApplication.timeSinceStartup + .5;
                stage = 8; return;
            }
            if (stage == 8)
            {
                ScreenCapture.CaptureScreenshot(CaptureName("factions"));
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 9; return;
            }
            if (!File.Exists(CaptureName("factions"))) return;
            Debug.Log("PASS: mission81 native accept, journal objective, and faction PDA captured.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Stop the owned Editor with a concrete scenario failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
