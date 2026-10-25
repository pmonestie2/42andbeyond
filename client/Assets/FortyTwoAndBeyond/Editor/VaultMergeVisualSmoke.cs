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
    /// <summary>Capture cargo and vault partial merges with every usable cargo slot occupied.</summary>
    [InitializeOnLoad]
    public static class VaultMergeVisualSmoke
    {
        private static int stage;
        private static bool connected, chosen;
        private static double deadline, captureAt;

        /// <summary>Register only for the owned disposable vault fixture.</summary>
        static VaultMergeVisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-vault-merge-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        /// <summary>Observe two server-confirmed 0x27 partial stack transfers.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Vault merge timed out at stage " + stage); return; }
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
            if (snapshot?.Ready != true || snapshot.Station == null || snapshot.CargoCapacity != 20 ||
                snapshot.Cargo[0] == null || snapshot.Vault[0] == null) return;
            if (stage == 0)
            {
                if (snapshot.Cargo.Take(20).Count(item => item != null) != 20 ||
                    snapshot.Cargo[0].Quantity != 35 || snapshot.Vault[0].Quantity != 20)
                { Fail("Disposable full-cargo merge state did not load."); return; }
                VaultHud hud = typeof(SpaceClient).GetField("vaultHud",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as VaultHud;
                if (hud == null) { Fail("Vault HUD unavailable."); return; }
                typeof(VaultHud).GetField("open", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(hud, true);
                Directory.CreateDirectory("Builds/verification");
                client.RetrieveVault(0);
                stage = 1; return;
            }
            if (stage == 1)
            {
                if (snapshot.Cargo[0].Quantity != 50 || snapshot.Vault[0].Quantity != 5) return;
                captureAt = EditorApplication.timeSinceStartup + .5;
                stage = 2; return;
            }
            if (stage == 2 && EditorApplication.timeSinceStartup >= captureAt)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/vault-merge-retrieve.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 3; return;
            }
            if (stage == 3 && EditorApplication.timeSinceStartup >= captureAt)
            {
                if (!File.Exists("Builds/verification/vault-merge-retrieve.png")) return;
                client.StoreCargo(0);
                stage = 4; return;
            }
            if (stage == 4)
            {
                if (snapshot.Cargo[0]?.Quantity != 5 || snapshot.Vault[0]?.Quantity != 50) return;
                captureAt = EditorApplication.timeSinceStartup + .5;
                stage = 5; return;
            }
            if (stage == 5 && EditorApplication.timeSinceStartup >= captureAt)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/vault-merge-store.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 6; return;
            }
            if (stage != 6 || EditorApplication.timeSinceStartup < captureAt) return;
            if (!File.Exists("Builds/verification/vault-merge-store.png")) return;
            Debug.Log("PASS: native partial vault-to-cargo and cargo-to-vault merges with full cargo.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Stop this owned Editor with a concrete fixture failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
