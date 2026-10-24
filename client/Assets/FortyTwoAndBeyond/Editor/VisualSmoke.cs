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
    /// <summary>Opt-in graphical smoke check against a separately owned disposable server.</summary>
    [InitializeOnLoad]
    public static class VisualSmoke
    {
        private static double deadline, captureAt;
        private static bool connected;
        private static int stage;
        private static bool uiSmoke;
        private static ClientWorker peer;

        /// <summary>Resume the smoke driver after the Play-mode domain reload.</summary>
        static VisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-visual-smoke") < 0) return;
            uiSmoke = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-ui-smoke") >= 0;
            deadline = EditorApplication.timeSinceStartup + (uiSmoke ? 90 : 300);
            EditorApplication.update += Tick;
        }

        /// <summary>Enter Play mode and capture both camera views after receiving a map.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline) { Fail("Visual smoke timed out."); return; }
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
            if (client.ChoosingCharacter) client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            if (client.CurrentSnapshot == null || !client.CurrentSnapshot.Ready || !client.CurrentSnapshot.HasPosition || client.CurrentSnapshot.Objects.Length == 0) return;
            if (uiSmoke) { TickUi(client); return; }
            if (stage == 0)
            {
                if (peer == null) peer = new ClientWorker("127.0.0.1", "peer", "pass");
                if (peer.Error != null) { Fail("Peer client: " + peer.Error); return; }
                if (peer.Latest?.Ready != true ||
                    !client.CurrentSnapshot.Players.Any(player => player.HasPosition) ||
                    !client.CurrentSnapshot.Mobs.Any(mob => mob.HasPosition &&
                        mob.Position.Distance(client.CurrentSnapshot.Position) < 5000)) return;
                if (client.RenderedBodyCount < 200) { Fail("Static scenery was not instantiated."); return; }
                Directory.CreateDirectory("Builds/verification");
                captureAt = EditorApplication.timeSinceStartup + 3; stage = 1;
            }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            if (stage == 1)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/chase.png");
                captureAt = EditorApplication.timeSinceStartup + 2; stage = 2; return;
            }
            if (stage == 2)
            {
                client.SetChart(true); captureAt = EditorApplication.timeSinceStartup + 2; stage = 3; return;
            }
            if (stage == 3)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/chart.png");
                captureAt = EditorApplication.timeSinceStartup + 2; stage = 4; return;
            }
            if (stage == 4)
            {
                client.SetChart(false);
                var asteroid = client.CurrentSnapshot.Objects.Where(item => item.Navigable && item.Name.Contains("Asteroid")).OrderBy(item => item.Position.Distance(client.CurrentSnapshot.Position)).First();
                client.Select(asteroid.Id); client.WarpSelected(); stage = 5; return;
            }
            if (stage == 5)
            {
                if (client.Target.Position.Distance(client.CurrentSnapshot.Position) > 1800) return;
                captureAt = EditorApplication.timeSinceStartup + 3; stage = 6; return;
            }
            if (stage == 6)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/asteroid.png");
                captureAt = EditorApplication.timeSinceStartup + 2; stage = 7; return;
            }
            if (stage == 7)
            {
                var station = client.CurrentSnapshot.Objects.First(item => item.Kind == 12);
                client.Select(station.Id); client.WarpSelected(); stage = 8; return;
            }
            if (stage == 8)
            {
                if (client.Target.Position.Distance(client.CurrentSnapshot.Position) > 4700) return;
                captureAt = EditorApplication.timeSinceStartup + 3; stage = 9; return;
            }
            if (stage == 9)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/station.png");
                captureAt = EditorApplication.timeSinceStartup + 2; stage = 10; return;
            }
            if (stage == 10)
            {
                var gate = client.CurrentSnapshot.Objects.Where(item => item.Kind == 11).OrderBy(item => item.Position.Distance(client.CurrentSnapshot.Position)).First();
                client.Select(gate.Id); client.WarpSelected(); stage = 11; return;
            }
            if (stage == 11)
            {
                if (client.Target.Position.Distance(client.CurrentSnapshot.Position) > 1800) return;
                captureAt = EditorApplication.timeSinceStartup + 3; stage = 12; return;
            }
            if (stage == 12)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/gate.png");
                captureAt = EditorApplication.timeSinceStartup + 2; stage = 13; return;
            }
            if (stage == 13) { client.GateSelected(); stage = 14; return; }
            if (stage == 14 && client.CurrentSnapshot.Sector == "Asteroid Belt Alpha") return;
            foreach (string name in new[] { "chase", "chart", "asteroid", "station", "gate" })
                if (!File.Exists("Builds/verification/" + name + ".png")) { Fail("Missing capture: " + name); return; }
            Debug.Log("PASS: 3D scenery, flight/chart rendering, asteroid/station/gate approaches and actual sector travel to " + client.CurrentSnapshot.Sector);
            ClosePeer();
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Capture the target and equipment panels in an isolated graphical run.</summary>
        private static void TickUi(SpaceClient client)
        {
            if (stage == 0)
            {
                ShipActor mob = client.CurrentSnapshot.Mobs.FirstOrDefault(actor => actor.HasPosition);
                if (mob == null) return;
                client.SelectContact(mob.Id);
                captureAt = EditorApplication.timeSinceStartup + 2;
                stage = 1;
                return;
            }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            Directory.CreateDirectory("Builds/verification");
            if (stage == 1)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/mob-target.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 2;
                return;
            }
            if (stage == 2)
            {
                if (!File.Exists("Builds/verification/mob-target.png")) { Fail("Missing mob target capture."); return; }
                FieldInfo field = typeof(SpaceClient).GetField("hud", BindingFlags.Instance | BindingFlags.NonPublic);
                FlightHud hud = field?.GetValue(client) as FlightHud;
                if (hud == null) { Fail("UI smoke could not find flight HUD."); return; }
                hud.ToggleEquipment();
                typeof(FlightHud).GetField("selectedMount", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(hud, 3);
                typeof(FlightHud).GetField("inspectMount", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(hud, true);
                captureAt = EditorApplication.timeSinceStartup + 2;
                stage = 3;
                return;
            }
            if (stage == 3)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/equipment.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 4;
                return;
            }
            if (stage == 4)
            {
                if (!File.Exists("Builds/verification/equipment.png")) { Fail("Missing equipment capture."); return; }
                FieldInfo field = typeof(SpaceClient).GetField("hud", BindingFlags.Instance | BindingFlags.NonPublic);
                (field?.GetValue(client) as FlightHud)?.ToggleEquipment();
                client.SetChart(true);
                captureAt = EditorApplication.timeSinceStartup + 2;
                stage = 5;
                return;
            }
            if (stage == 5)
            {
                MapObject marker = client.CurrentSnapshot.Objects.First(item => item.Navigable && item.Name == "Bedrock Cluster");
                Vector3 point = client.Project(marker.Position);
                client.SelectAtPointer(new Vector2(point.x, point.y));
                if (client.Target?.Id != marker.Id) { Fail("Chart marker did not select its destination."); return; }
                ScreenCapture.CaptureScreenshot("Builds/verification/ui-chart.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 6;
                return;
            }
            if (!File.Exists("Builds/verification/ui-chart.png")) { Fail("Missing chart capture."); return; }
            Debug.Log("PASS: tactical target, equipment and chart panels captured.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Close the disposable second client before leaving Play mode.</summary>
        private static void ClosePeer()
        {
            peer?.Dispose();
            peer = null;
        }

        /// <summary>Make failed graphical checks visible to the calling harness.</summary>
        private static void Fail(string message)
        { Debug.LogError(message); ClosePeer(); EditorApplication.update -= Tick; EditorApplication.Exit(1); }
    }
}
