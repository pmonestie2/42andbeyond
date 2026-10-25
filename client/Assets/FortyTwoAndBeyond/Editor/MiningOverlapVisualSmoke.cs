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
    /// <summary>Capture two native mining phases overlapping on an owned ore field.</summary>
    [InitializeOnLoad]
    public static class MiningOverlapVisualSmoke
    {
        private static int stage;
        private static uint resourceId, firstOre, secondOre;
        private static int secondSlot;
        private static bool connected;
        private static double deadline, captureAt;

        /// <summary>Register only for the disposable mining overlap check.</summary>
        static MiningOverlapVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-mining-overlap-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 100;
            EditorApplication.update += Tick;
        }

        /// <summary>Open one ore field, start successive slots, and require both cargo awards.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Mining overlap smoke timed out at stage " + stage); return; }
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
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || !snapshot.HasPosition) return;
            if (stage == 0)
            {
                MapObject resource = snapshot.Objects.FirstOrDefault(item => item.Kind == 38);
                if (resource == null) return;
                resourceId = resource.Id;
                FieldInfo field = typeof(SpaceClient).GetField("miningHud", BindingFlags.Instance | BindingFlags.NonPublic);
                MiningHud hud = field?.GetValue(client) as MiningHud;
                if (hud == null) { Fail("Mining HUD was unavailable."); return; }
                hud.SelectResource(resourceId);
                stage = 1; return;
            }
            if (stage == 1 && snapshot.Verbs.Available(resourceId, 9) == true)
            { client.OpenResource(resourceId); stage = 2; return; }
            if (stage == 2)
            {
                ResourceView stock = snapshot.Resources.FirstOrDefault(item => item.Id == resourceId);
                if (!snapshot.ResourceWindowOpen || stock == null) return;
                ItemView[] items = stock.Items.Where(item => item != null).Take(2).ToArray();
                if (items.Length != 2) { Fail("Disposable ore field has fewer than two slots."); return; }
                firstOre = items[0].Template; secondOre = items[1].Template;
                secondSlot = items[1].Slot;
                client.ProspectResource(items[0].Slot);
                stage = 3; return;
            }
            if (stage == 3 && snapshot.MiningTractor != null)
            { client.ProspectResource(secondSlot); stage = 4; return; }
            if (stage == 4 && snapshot.MiningTractor != null && snapshot.MiningProspect != null)
            {
                Directory.CreateDirectory("Builds/verification");
                if (File.Exists("Builds/verification/mining-overlap.png"))
                    File.Delete("Builds/verification/mining-overlap.png");
                ScreenCapture.CaptureScreenshot("Builds/verification/mining-overlap.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 5; return;
            }
            if (stage == 5 && EditorApplication.timeSinceStartup >= captureAt)
            {
                if (!File.Exists("Builds/verification/mining-overlap.png"))
                { Fail("Mining overlap screenshot was not written."); return; }
                stage = 6;
            }
            if (stage == 6 && snapshot.Cargo.Any(item => item?.Template == firstOre) &&
                snapshot.Cargo.Any(item => item?.Template == secondOre))
            {
                Debug.Log("PASS: concurrent native prospect/ore tractor and two cargo awards captured.");
                Finish(0);
            }
        }

        /// <summary>Exit only this owned disposable Editor check.</summary>
        private static void Finish(int code)
        { EditorApplication.update -= Tick; EditorApplication.Exit(code); }

        /// <summary>Report the failed stage before stopping the owned check.</summary>
        private static void Fail(string reason)
        { Debug.LogError(reason); Finish(1); }
    }
}
