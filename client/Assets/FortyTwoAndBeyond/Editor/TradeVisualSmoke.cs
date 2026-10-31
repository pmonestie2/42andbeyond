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
    /// <summary>Capture the native trade offer overlay from server-encoded disposable fixtures.</summary>
    [InitializeOnLoad]
    internal static class TradeVisualSmoke
    {
        private static double deadline, captureAt;
        private static bool entered, captured;

        /// <summary>Register only when the opt-in graphical fixture is requested.</summary>
        static TradeVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-trade-smoke") < 0) return;
            deadline = EditorApplication.timeSinceStartup + 50;
            EditorApplication.update += Tick;
        }

        /// <summary>Show an immutable trade snapshot, capture it, and exit the owned Editor.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.isPlaying) entered = true;
            if (EditorApplication.timeSinceStartup > deadline) { Finish(false, "Trade visual smoke timed out."); return; }
            if (!entered)
            {
                entered = true;
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                EditorWindow window = EditorWindow.GetWindow(gameView);
                window.position = new Rect(60, 60, 1280, 760);
                window.Focus(); EditorApplication.EnterPlaymode(); return;
            }
            if (!EditorApplication.isPlaying) return;
            if (UnityEngine.Object.FindFirstObjectByType<TradeVisualProbe>() == null)
                new GameObject("Disposable trade visual probe").AddComponent<TradeVisualProbe>();
            if (TradeVisualProbe.Ready && captureAt == 0)
            { captureAt = EditorApplication.timeSinceStartup + 1.5; return; }
            if (captureAt == 0 || EditorApplication.timeSinceStartup < captureAt) return;
            string path = Path.GetFullPath("Builds/verification/trade-offer.png");
            if (!captured)
            { Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path)) File.Delete(path);
                ScreenCapture.CaptureScreenshot(path); captured = true; return; }
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                Finish(true, "Trade visual smoke captured " + path);
        }

        /// <summary>Exit the opt-in Editor process after reporting the fixture result.</summary>
        private static void Finish(bool passed, string message)
        {
            EditorApplication.update -= Tick;
            if (passed) Debug.Log(message); else Debug.LogError(message);
            EditorApplication.Exit(passed ? 0 : 1);
        }
    }

    /// <summary>Render an isolated native trade fixture over the actual client Game View.</summary>
    internal sealed class TradeVisualProbe : MonoBehaviour
    {
        private Snapshot snapshot;
        private TradeHud panel;
        private FlightHud hud;
        internal static bool Ready { get; private set; }

        /// <summary>Load two Rust-encoded offer frames without connecting to a live server.</summary>
        private void Start()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-trade-smoke") < 0) return;
            SpaceClient client = FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            hud = (FlightHud)typeof(SpaceClient).GetField("hud",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
            panel = new TradeHud(client, hud);
            var world = new World();
            world.ItemCatalog[2505] = new ItemTemplateInfo(2505, 11,
                "Prototype Terran Experimental Long-Range Reactor Array", "");
            world.Equipment.CargoCapacity = 25;
            for (int slot = 0; slot < 12; slot++)
                world.Equipment.Cargo[slot] = new ItemState { Template = 2505, Quantity = (uint)(slot + 1), Quality = 1.25f,
                    Structure = .5f, BuilderName = "Trader" };
            world.Apply(new Frame(0x1f, new byte[] { 2, 0, 0, 64, 0 }), 0x40000001);
            world.Apply(new Frame(0x1b, Fixture("trade_offer_own.hex")), 0x40000001);
            world.Apply(new Frame(0x1b, Fixture("trade_offer_peer.hex")), 0x40000001);
            world.Apply(new Frame(0x1f, new byte[] { 2, 0, 0, 64, 3 }), 0x40000001);
            snapshot = new Snapshot(world, "Ready", true);
            Ready = true;
        }

        /// <returns>
        ///     byte[]: One Rust-encoded native 0x1b trade offer body.
        /// </returns>
        private static byte[] Fixture(string name)
        {
            string path = Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                "Tests", "Fixtures", name);
            string hex = File.ReadAllText(path).Trim();
            var bytes = new byte[hex.Length / 2];
            for (int index = 0; index < bytes.Length; index++)
                bytes[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
            return bytes;
        }

        /// <summary>Draw the trade panel at the same scale as the live HUD.</summary>
        private void OnGUI()
        {
            if (panel == null || snapshot == null) return;
            Matrix4x4 prior = GUI.matrix;
            int depth = GUI.depth; GUI.depth = -100;
            GUI.matrix = Matrix4x4.Scale(new Vector3(hud.Scale, hud.Scale, 1));
            panel.Draw(snapshot);
            GUI.matrix = prior; GUI.depth = depth;
        }
    }
}
