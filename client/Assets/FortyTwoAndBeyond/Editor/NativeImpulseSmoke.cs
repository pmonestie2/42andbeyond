using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Exercise the actual Unity flight client against an owned local server.</summary>
    [InitializeOnLoad]
    public static class NativeImpulseSmoke
    {
        private static readonly FieldInfo ThrottleField = typeof(SpaceClient).GetField(
            "flightThrottle", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SpeedField = typeof(SpaceClient).GetField(
            "motionReportSpeed", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RenderField = typeof(SpaceClient).GetField(
            "renderedPosition", BindingFlags.Instance | BindingFlags.NonPublic);
        private static int stage;
        private static double deadline, stageAt;
        private static Vector3 origin;
        private static float maximum, minimum;
        private static readonly bool[] HadRecent = new bool[ServerHistory.Capacity];
        private static readonly string[] Recent = new string[ServerHistory.Capacity];
        private static bool experienceUi, floatingMap;
        private static UiViewportScope viewport;

        /// <summary>Register only for an explicit disposable impulse smoke.</summary>
        static NativeImpulseSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-native-impulse-smoke") < 0)
                return;
            experienceUi = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-experience-ui-smoke") >= 0;
            floatingMap = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-floating-map-smoke") >= 0;
            for (int index = 0; index < ServerHistory.Capacity; index++)
            {
                string key = "42andbeyond.recent-server." + index;
                HadRecent[index] = PlayerPrefs.HasKey(key);
                Recent[index] = PlayerPrefs.GetString(key, "");
            }
            deadline = EditorApplication.timeSinceStartup + 75;
            EditorApplication.update += Tick;
        }

        /// <summary>Drive W, opposing S, fresh S, and the stop action through SpaceClient.Thrust.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            double now = EditorApplication.timeSinceStartup;
            if (now > deadline) { Fail("Native impulse smoke timed out at stage " + stage); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                EditorApplication.EnterPlaymode();
                return;
            }
            if (!EditorApplication.isPlaying) return;
            SpaceClient client = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            if ((experienceUi || floatingMap) && viewport == null)
            { viewport = new UiViewportScope(); viewport.Set(1440, 900); return; }
            if (client.ChoosingCharacter && client.Characters.Length > 0)
                client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || !snapshot.HasPosition || snapshot.Station != null ||
                !snapshot.MaxImpulseSpeed.HasValue || !snapshot.MinImpulseSpeed.HasValue ||
                !snapshot.ImpulseAcceleration.HasValue || !client.Ready) return;
            FlightThrottle throttle = (FlightThrottle)ThrottleField.GetValue(client);
            maximum = snapshot.MaxImpulseSpeed.Value;
            minimum = snapshot.MinImpulseSpeed.Value;
            if (maximum <= 0 || minimum >= 0 || snapshot.ImpulseAcceleration.Value <= 0)
            { Fail("Owned server did not publish usable impulse limits"); return; }
            if (stage == 0)
            {
                if (experienceUi)
                {
                    Button self = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None)
                        .Select(document => document.rootVisualElement.Q<Button>("flight-target-self"))
                        .FirstOrDefault(button => button != null);
                    if (self == null) return;
                    using (var click = ClickEvent.GetPooled())
                    { click.target = self.parent.Q("flight-pilot-name"); self.parent.Q("flight-pilot-name").SendEvent(click); }
                }
                if (floatingMap) client.SetChart(true);
                byte? command = throttle.Sample(true, true, false, false);
                if (command != 2) { Fail("W did not request forward thrust"); return; }
                origin = Render(client);
                client.Thrust(command.Value);
                stageAt = now; stage = 1;
                return;
            }
            if (stage == 1 && now - stageAt >= 1.3)
            {
                if (experienceUi && !CheckExperienceHud(client)) return;
                if (floatingMap && !CheckFloatingMap(client)) return;
                float speed = Speed(client);
                float travel = Vector3.Distance(Render(client), origin);
                if (speed <= 5 || speed > maximum + .01f || travel < .05f ||
                    Mathf.Abs(client.DisplayedSpeed - speed) > .01f ||
                    snapshot.TargetSpeed <= 0)
                { Fail($"W had no bounded rendered/server movement: predicted {speed:0.0}, server target {snapshot.TargetSpeed:0.0}, render {travel:0.000}"); return; }
                byte? command = throttle.Sample(true, false, true, false,
                    currentSpeed: speed, targetSpeed: maximum);
                if (command != 4) { Fail("Opposing S did not brake W"); return; }
                client.Thrust(command.Value);
                stageAt = now; stage = 2;
                return;
            }
            if (stage == 2 && Speed(client) <= .5f)
            {
                if (now - stageAt > 3.5) { Fail("S brake took too long"); return; }
                throttle.Sample(true, false, false, false);
                byte? command = throttle.Sample(true, false, true, false,
                    currentSpeed: Speed(client), targetSpeed: 0);
                if (command != 3) { Fail("Fresh S did not request reverse"); return; }
                origin = Render(client);
                client.Thrust(command.Value);
                stageAt = now; stage = 3;
                return;
            }
            if (stage == 2 && now - stageAt > 3.5)
            { Fail("Opposing S failed to stop forward motion"); return; }
            if (stage == 3 && now - stageAt >= 1.3)
            {
                float speed = Speed(client);
                float travel = Vector3.Distance(Render(client), origin);
                if (speed >= -5 || speed < minimum - .01f || travel < .05f ||
                    Mathf.Abs(client.DisplayedSpeed - speed) > .01f ||
                    snapshot.TargetSpeed >= 0)
                { Fail($"Reverse had no bounded rendered/server movement: predicted {speed:0.0}, server target {snapshot.TargetSpeed:0.0}, render {travel:0.000}"); return; }
                byte? command = throttle.Sample(true, false, true, true,
                    currentSpeed: speed, targetSpeed: minimum);
                if (command != 4) { Fail("Stop action did not request brake"); return; }
                client.Thrust(command.Value);
                stageAt = now; stage = 4;
                return;
            }
            if (stage == 4 && Speed(client) >= -.5f)
            {
                Debug.Log($"PASS: native impulse W/S/brake with server limits {maximum:0.0}/{minimum:0.0}; rendered and authoritative movement changed direction");
                if (floatingMap)
                {
                    TacticalSectorMap map = Map(client);
                    typeof(TacticalSectorMap).GetField("windowSize", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(map, new Vector2(720, 460));
                    stageAt = now; stage = 5; return;
                }
                Finish();
                return;
            }
            if (stage == 5 && now - stageAt > .4)
            {
                Rect size = Map(client).WindowBounds;
                if (Mathf.Abs(size.width - 720) > 1 || Mathf.Abs(size.height - 460) > 1)
                { Fail("Floating map did not apply its independent window size"); return; }
                typeof(SpaceClient).GetMethod("HandleChartShortcut", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(client, new object[] { true, false });
                if (client.Chart) { Fail("Tab did not close map"); return; }
                typeof(SpaceClient).GetMethod("HandleChartShortcut", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(client, new object[] { true, false });
                stageAt = now; stage = 6; return;
            }
            if (stage == 6 && now - stageAt > .4)
            {
                if (!CheckFloatingMap(client)) return;
                if (Mathf.Abs(Map(client).WindowBounds.width - 720) > 1)
                { Fail("Tab reopening lost map size"); return; }
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/floating-map.png");
                stageAt = now; stage = 7; return;
            }
            if (stage == 7 && now - stageAt > .5)
            {
                Debug.Log("PASS: floating map preserves flight camera, HUD, movement, independent sizing and Tab reopen");
                Finish(); return;
            }
            if (stage == 4 && now - stageAt > 3.5)
                Fail("Stop action failed to stop reverse motion");
        }

        /// <summary>Close only the owned smoke Editor and restore saved preferences.</summary>
        private static void Finish()
        {
            EditorApplication.update -= Tick; RestoreRecentServers(); EditorApplication.Exit(0);
        }

        /// Returns:
        ///     TacticalSectorMap: The actual floating map attached to the running client.
        private static TacticalSectorMap Map(SpaceClient client)
        {
            var hud = (FlightHud)typeof(SpaceClient).GetField("hud", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
            return (TacticalSectorMap)typeof(FlightHud).GetField("tacticalMap", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(hud);
        }

        /// Returns:
        ///     bool: Whether an open floating map preserves the scene and ordinary flight cards.
        private static bool CheckFloatingMap(SpaceClient client)
        {
            var camera = (Camera)typeof(SpaceClient).GetField("view", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
            VisualElement root = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None)
                .Select(document => document.rootVisualElement).FirstOrDefault(item => item.Q("panel-browse") != null);
            if (!client.Chart || Map(client) == null || camera.cullingMask == 0 || camera.orthographic ||
                camera.clearFlags != CameraClearFlags.Skybox || root == null ||
                root.Q("panel-browse").resolvedStyle.visibility != Visibility.Visible)
            { Fail("Floating map replaced flight camera or hid ordinary flight cards"); return false; }
            return true;
        }

        /// <summary>Returns:
        ///     float: Current main-thread flight prediction speed in metres per second.
        /// </summary>
        private static float Speed(SpaceClient client) => (float)SpeedField.GetValue(client);

        /// Returns:
        ///     bool: Whether the live experience rows fit below the compact resource dials.
        private static bool CheckExperienceHud(SpaceClient client)
        {
            VisualElement root = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None)
                .Select(document => document.rootVisualElement)
                .FirstOrDefault(candidate => candidate.Q("experience-progress") != null);
            if (client.CurrentSnapshot.SelectedTargetId != client.CurrentSnapshot.PlayerId)
            { Fail("Flight-deck self-target control did not select the own ship"); return false; }
            ExperienceState progress = client.CurrentSnapshot.Experience;
            if (root == null || progress == null) { Fail("Missing native XP HUD telemetry"); return false; }
            VisualElement assembly = root.Q("resource-dial-assembly");
            VisualElement tracks = root.Q("experience-progress");
            VisualElement dial = assembly.Q("gauge-defense");
            if (dial.worldBound.width > 81 || tracks.worldBound.yMin < dial.worldBound.yMax + 2 ||
                tracks.worldBound.yMax > root.Q("flight-strip").worldBound.yMax || tracks.worldBound.width < 190)
            { Fail("Experience tracks overlap the dials or extend beyond the flight panel"); return false; }
            string[] keys = { "combat", "explore", "trade" };
            uint[] levels = { progress.CombatLevel, progress.ExploreLevel, progress.TradeLevel };
            float[] fractions = { progress.CombatFraction, progress.ExploreFraction, progress.TradeFraction };
            for (int index = 0; index < keys.Length; index++)
            {
                VisualElement row = root.Q("experience-" + keys[index]);
                VisualElement rail = row.Q("experience-" + keys[index] + "-rail");
                VisualElement fill = row.Q("experience-" + keys[index] + "-fill");
                if (row.worldBound.height < 14 || !row.tooltip.Contains("Level " + levels[index]) ||
                    row.Q<Label>(className: "experience-percent").text != $"{fractions[index] * 100:0.0}%" ||
                    Mathf.Abs(fill.resolvedStyle.width / rail.resolvedStyle.width - fractions[index]) > .01f)
                { Fail("Experience HUD differs from native telemetry: " + keys[index]); return false; }
            }
            Directory.CreateDirectory("Builds/verification");
            ScreenCapture.CaptureScreenshot("Builds/verification/experience-gauges.png");
            Debug.Log("PASS: three native XP gauges below 80px resource dials; levels, progress, tooltips and bounds");
            return true;
        }

        /// <summary>Returns:
        ///     Vector3: The displayed ship position in Unity world units.
        /// </summary>
        private static Vector3 Render(SpaceClient client) => (Vector3)RenderField.GetValue(client);

        /// <summary>Exit the disposable Editor with a specific failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            RestoreRecentServers();
            EditorApplication.Exit(1);
        }

        /// <summary>Restore only the five recent-server values changed by the smoke login.</summary>
        private static void RestoreRecentServers()
        {
            viewport?.Dispose(); viewport = null;
            for (int index = 0; index < ServerHistory.Capacity; index++)
            {
                string key = "42andbeyond.recent-server." + index;
                if (HadRecent[index]) PlayerPrefs.SetString(key, Recent[index]);
                else PlayerPrefs.DeleteKey(key);
            }
            PlayerPrefs.Save();
        }
    }
}
