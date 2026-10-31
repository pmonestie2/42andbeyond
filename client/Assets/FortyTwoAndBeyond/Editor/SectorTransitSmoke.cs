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
    /// <summary>Exercise both real native transfers against an owned loopback server.</summary>
    [InitializeOnLoad]
    public static class SectorTransitSmoke
    {
        private static int stage;
        private static bool connected;
        private static uint source, destination, gateId;
        private static int repeatedEntry, repeatCount;
        private static double deadline, at;
        private static readonly FieldInfo Presentation = typeof(SpaceClient).GetField(
            "sectorTravelVisual", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>Register only for the isolated transit verification flag.</summary>
        static SectorTransitSmoke()
        {
            if (!Environment.GetCommandLineArgs().Contains("--42andbeyond-transit-smoke")) return;
            deadline = EditorApplication.timeSinceStartup + 210;
            EditorApplication.update += Tick;
        }

        /// <summary>Cast a wormhole, then pass through a real return gate and capture both arrivals.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline) { Fail("timeout at stage " + stage); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                EditorWindow window = EditorWindow.GetWindow(gameView); window.maximized = true; window.Focus();
                EditorApplication.EnterPlaymode(); return;
            }
            if (!EditorApplication.isPlaying) return;
            SpaceClient client = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            if (!connected) { connected = true; client.StartLocalSession("127.0.0.1", "pje", "pje"); }
            if (client.ChoosingCharacter) client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot state = client.CurrentSnapshot;
            if (state?.Ready != true || !state.HasPosition) return;
            var background=client.GetComponentsInChildren<SpaceEnvironmentBackground>(true).Single();
            var expected=SectorSceneryProfile.Generate(state.SectorId,state.Sector);
            if(background.SectorFingerprint!=expected.Fingerprint || background.SectorBodyCount!=expected.Bodies.Count)
            { Fail("authoritative sector entry did not apply its own celestial recipe");return; }
            double now = EditorApplication.timeSinceStartup;
            var visual = (SectorTravelVisual)Presentation.GetValue(client);
            if (stage == 0)
            {
                if (!state.AvailableAbilities.Contains(19u)) { Fail("JE wormhole not advertised"); return; }
                client.SelectFreeFlight(); source = state.SectorId; client.ActivateAbility(19); at = now; stage = 1; return;
            }
            if (stage == 1 && now - at > 8)
            { Fail("wormhole cast missing: " + state.Status + "; energy=" + state.Energy + "; " + string.Join("; ", state.Messages)); return; }
            if (stage == 1 && state.ObjectEffects.Any(effect => effect.Descriptor == 689 && effect.OwnerId == state.PlayerId))
            { at = now; stage = 2; }
            if (stage == 2 && now - at > .07)
            {
                MeshRenderer[] surfaces = client.GetComponentsInChildren<MeshRenderer>().Where(renderer =>
                    renderer.transform.parent?.name == "Wormhole plasma aperture").ToArray();
                if (!visual.Active || surfaces.Length != 2 || surfaces.Any(renderer => !renderer.sharedMaterial.shader.isSupported))
                { Fail("native cast did not create supported plasma surfaces"); return; }
                Capture("wormhole-departure"); stage = 3;
            }
            if (stage == 3 && state.SectorId != source)
            { destination = state.SectorId; at = now; stage = 4; }
            if (stage == 4 && now - at > 1.8)
            {
                if (state.ArrivalKind != 2 || !visual.Active) { Fail("wormhole arrival lost during handoff"); return; }
                if (!state.AbilityCooldownSeconds.TryGetValue(15, out float recovery) || recovery <= 0)
                { Fail("handoff discarded authoritative wormhole recovery"); return; }
                Capture("wormhole-arrival"); stage = 5;
            }
            if (stage == 5 && now - at > 12)
            {
                if (state.AbilityCooldownSeconds.TryGetValue(15, out float cooldown) && cooldown > 0) return;
                repeatedEntry = state.EntryGeneration;
                client.ActivateAbility(19); at = now; stage = 12;
                // Deliberately miss every rendered frame of the quarter-second cast.
                System.Threading.Thread.Sleep(600);
                return;
            }
            // A busy debug server advances simulation recovery more slowly than wall time.
            // Retry only explicit authoritative rejection; an accepted but missing animation still fails.
            if (stage == 12 && state.EntryGeneration == repeatedEntry && now - at > 3 &&
                state.Status == "This skill is recovering.")
            { client.ActivateAbility(19); at = now; System.Threading.Thread.Sleep(600); return; }
            if (stage == 12 && state.EntryGeneration != repeatedEntry)
            {
                if (state.SectorId != destination || state.ArrivalKind != 2 || !visual.Active)
                { Fail("coalesced same-sector wormhole skipped arrival"); return; }
                at = now; stage = 13;
            }
            if (stage == 13 && now - at > 1)
            {
                if (!visual.Active) { Fail("repeated arrival expired prematurely"); return; }
                Capture("wormhole-repeat-"+repeatCount); repeatCount++; client.SetChart(true); stage = 14;
            }
            if (stage == 14 && now - at > 4)
            {
                if (visual.Active) { Fail("chart retained an expired wormhole or input lock"); return; }
                client.SetChart(false); at = now; stage = repeatCount<3?5:15;
            }
            if (stage == 15 && now - at > .2)
            {
                MapObject gate = state.Objects.FirstOrDefault(item => item.Kind == 11);
                if (gate == null) { Fail("destination contains no return gate"); return; }
                gateId = gate.Id; client.SendChat("/d_warp " + gateId); client.SelectWorldObject(gateId);
                stage = 6; return;
            }
            if (stage == 6 && state.Verbs.Available(gateId, 10) == true)
            { client.GateSelected(); stage = 7; return; }
            if (stage == 7 && state.GateCameraTarget == gateId) { at = now; stage = 8; }
            if (stage == 8 && now - at > 4)
            {
                GateTransitVisual gate = client.GetComponentsInChildren<GateTransitVisual>().FirstOrDefault(item => item.Contraction < .8f);
                if (!visual.Active || gate == null) { Fail("approved gate did not contract or move the rendered ship"); return; }
                Capture("gate-departure"); stage = 9;
            }
            if (stage == 9 && state.SectorId != destination) { at = now; stage = 10; }
            if (stage == 10 && now - at > 1.8)
            {
                if (state.ArrivalKind != 1 || !visual.Active) { Fail("gate arrival lost during handoff"); return; }
                Capture("gate-arrival"); stage = 11;
            }
            if (stage == 11 && now - at > 4)
            {
                if (visual.Active || client.ShipForPreview.localPosition.sqrMagnitude > .001f)
                { Fail("travel pose did not return to authoritative ship origin"); return; }
                Debug.Log("PASS: four native wormholes, repeated same-sector handoffs with skipped cast frames, chart cleanup, gate transit");
                EditorApplication.update -= Tick; EditorApplication.Exit(0);
            }
        }

        /// <summary>Save the actual game view after a rendered transition frame.</summary>
        private static void Capture(string name)
        { ScreenCapture.CaptureScreenshot(Path.Combine("Builds/verification", name + ".png")); }
        /// <summary>Report an isolated verification failure.</summary>
        private static void Fail(string message)
        { Debug.LogError("Sector transit smoke: " + message); EditorApplication.update -= Tick; EditorApplication.Exit(1); }
    }
}
