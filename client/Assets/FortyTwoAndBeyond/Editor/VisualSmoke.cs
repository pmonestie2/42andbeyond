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
    /// <summary>Opt-in graphical smoke check against a separately owned disposable server.</summary>
    [InitializeOnLoad]
    public static class VisualSmoke
    {
        private static double deadline, captureAt;
        private static bool connected;
        private static int stage;
        private static bool uiSmoke, cloakSmoke, itemsSmoke;
        private static ulong inspectionCredits;
        private static uint inspectedTemplate;
        private static long inspectionOwned;
        private static bool smallUiSmoke;
        private static bool productionSmoke;
        private static bool artOnlySmoke;
        private static bool isolatedArtSmoke;
        private static bool followSmoke;
        private static bool rallySmoke;
        private static uint rallyAbility;
        private static bool followTurnVisible;
        private static ClientWorker peer;

        /// <summary>Resume the smoke driver after the Play-mode domain reload.</summary>
        static VisualSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-visual-smoke") < 0 &&
                Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-art-smoke") < 0 &&
                Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-follow-smoke") < 0 &&
                Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-rally-art-smoke") < 0 &&
                Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-production-smoke") < 0) return;
            itemsSmoke = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-items-smoke") >= 0;
            cloakSmoke = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-cloak-smoke") >= 0;
            uiSmoke = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-ui-smoke") >= 0;
            smallUiSmoke = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-small-ui-smoke") >= 0;
            productionSmoke = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-production-smoke") >= 0;
            artOnlySmoke = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-art-smoke") >= 0;
            isolatedArtSmoke = Array.IndexOf(Environment.GetCommandLineArgs(),
                "--42andbeyond-isolated-art") >= 0;
            followSmoke = Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-follow-smoke") >= 0;
            rallySmoke = Array.IndexOf(Environment.GetCommandLineArgs(),
                "--42andbeyond-rally-art-smoke") >= 0;
            int rallyArgument = Array.IndexOf(Environment.GetCommandLineArgs(),
                "--42andbeyond-rally-ability");
            rallyAbility = rallyArgument >= 0 && rallyArgument + 1 < Environment.GetCommandLineArgs().Length &&
                uint.TryParse(Environment.GetCommandLineArgs()[rallyArgument + 1], out uint parsedAbility) ?
                parsedAbility : 46u;
            deadline = EditorApplication.timeSinceStartup + (uiSmoke || productionSmoke ? 90 : 300);
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
                    EditorWindow window = smallUiSmoke ?
                        ScriptableObject.CreateInstance(gameView) as EditorWindow :
                        EditorWindow.GetWindow(gameView);
                    if (smallUiSmoke)
                    {
                        window.Show();
                        window.position = new Rect(80, 75, 1180, 700);
                    }
                    else window.maximized = true;
                    window.Focus();
                }
                EditorApplication.EnterPlaymode(); return;
            }
            if (!EditorApplication.isPlaying) return;
            SpaceClient client = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            if (!connected)
            {
                connected = true;
                client.StartLocalSession("127.0.0.1", rallySmoke ? "pte" : "local",
                    rallySmoke ? "pte" : "local");
            }
            if (client.ChoosingCharacter) client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            if (productionSmoke) { TickProduction(client); return; }
            if (itemsSmoke) { TickItems(client); return; }
            if (client.CurrentSnapshot == null || !client.CurrentSnapshot.Ready || !client.CurrentSnapshot.HasPosition || client.CurrentSnapshot.Objects.Length == 0) return;
            if (cloakSmoke) { TickCloak(client); return; }
            if (uiSmoke) { TickUi(client); return; }
            if (followSmoke) { TickFollow(client); return; }
            if (rallySmoke) { TickRallyArt(client); return; }
            if (stage == 0)
            {
                if (artOnlySmoke && client.Characters?.Length > 0)
                {
                    string[] models = { "TVF01A", "TVT01A", "TVE01A", "JVF01A", "JVT01A",
                        "JVE01A", "PVF01A", "PVT01A", "PVE01A" };
                    string[] upgraded = { "OriginalTE", "OriginalTT", "OriginalTS",
                        "OriginalJD", "OriginalJS", "OriginalJEBase", "OriginalPW",
                        "OriginalPP", "OriginalPS" };
                    int classIndex = client.Characters[0].ClassIndex;
                    string model = client.CurrentSnapshot.Experience?.HullStage >= 5 ?
                        upgraded[classIndex] : models[classIndex];
                    if (GameObject.Find("Original Earth and Beyond hull " + model) == null)
                    { Fail("Expected original " + model + " hull for selected character."); return; }
                    if (model == "OriginalPW")
                    {
                        PwWarpArms arms = UnityEngine.Object.FindFirstObjectByType<PwWarpArms>();
                        if (arms == null || arms.GetComponentsInChildren<MeshFilter>().Length < 9)
                        { Fail("PW arm rig or a moving mesh is missing."); return; }
                        Transform firstPivot = arms.GetComponentsInChildren<Transform>()
                            .FirstOrDefault(part => part.name == "L_1");
                        if (firstPivot == null) { Fail("PW shoulder bone is missing."); return; }
                        Quaternion restingRotation = firstPivot.localRotation;
                        Vector3 restingWeapon = arms.WeaponSocket(0).position;
                        arms.Tick(true, 3f);
                        if (Quaternion.Angle(firstPivot.localRotation,
                            restingRotation) < 8f ||
                            Vector3.Distance(restingWeapon, arms.WeaponSocket(0).position) < .2f)
                        { Fail("Original PW warp animation did not rotate its arm."); return; }
                        arms.Tick(false, 3f);
                    }
                }
                if (!isolatedArtSmoke)
                {
                    if (peer == null) peer = new ClientWorker("127.0.0.1", "peer", "pass");
                    if (peer.Error != null) { Fail("Peer client: " + peer.Error); return; }
                    if (peer.Latest?.Ready != true ||
                        !client.CurrentSnapshot.Players.Any(player => player.HasPosition)) return;
                }
                if (!client.CurrentSnapshot.Mobs.Any(mob => mob.HasPosition &&
                    mob.Position.Distance(client.CurrentSnapshot.Position) < 5000)) return;
                if (client.RenderedBodyCount < 200) { Fail("Static scenery was not instantiated."); return; }
                Directory.CreateDirectory("Builds/verification");
                captureAt = EditorApplication.timeSinceStartup + 3; stage = 1;
            }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            if (stage == 1)
            {
                Camera captureCamera = typeof(SpaceClient).GetField("view",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as Camera;
                if (captureCamera != null)
                    Debug.Log("Flight capture camera forward=" + captureCamera.transform.forward);
                ScreenCapture.CaptureScreenshot("Builds/verification/chase.png");
                captureAt = EditorApplication.timeSinceStartup + 2; stage = 2; return;
            }
            if (artOnlySmoke && stage == 2)
            {
                if (!File.Exists("Builds/verification/chase.png")) return;
                Debug.Log("PASS: flight art captured against disposable server.");
                ClosePeer(); EditorApplication.update -= Tick; EditorApplication.Exit(0); return;
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
                VisualElement root = SelectionRoot();
                int expected = client.CurrentSnapshot.Players.Count(actor => actor.HasPosition);
                int actual = root.Query<VisualElement>(className: "contact").ToList()
                    .Count(row => row.name.StartsWith("actor-", StringComparison.Ordinal));
                if (actual != Math.Min(24, expected))
                { Fail("Players filter retained unrelated contacts, including an empty result."); return; }
                root.Q<DropdownField>("contact-selection-filter").value = "All contacts";
                captureAt = EditorApplication.timeSinceStartup + .3; stage = 13;
                return;
            }
            if (stage == 13)
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

        /// <summary>Inspect actual station offers without buying, then verify the local galaxy catalog.</summary>
        private static void TickItems(SpaceClient client)
        {
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || snapshot.Station == null) return;
            var browser = (ItemBrowserHud)typeof(SpaceClient).GetField("itemBrowserHud", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
            if (stage == 0)
            {
                if (snapshot.Station.CurrentRoom == uint.MaxValue) return;
                client.OpenNpc(16); stage = 1; return;
            }
            if (stage == 1)
            {
                if (snapshot.Dialogue == null) return;
                client.ChooseNpc(2); stage = 2; return;
            }
            if (stage == 2)
            {
                ItemView offer = snapshot.Vendor.FirstOrDefault(item => item != null && item.Price > 0);
                if (snapshot.Dialogue?.VendorOpen != true || offer == null) return;
                inspectedTemplate = offer.Template; inspectionCredits = snapshot.Credits;
                inspectionOwned = ItemBrowserHud.CargoCount(snapshot, inspectedTemplate);
                client.InspectVendorItem(offer, false);
                captureAt = EditorApplication.timeSinceStartup + .7; stage = 3; return;
            }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            if (stage == 3)
            {
                if (!browser.IsOpen || snapshot.Credits != inspectionCredits || ItemBrowserHud.CargoCount(snapshot, inspectedTemplate) != inspectionOwned)
                { Fail("Vendor inspection unexpectedly bought an item."); return; }
                ScreenCapture.CaptureScreenshot("Builds/verification/vendor-inspect.png");
                captureAt = EditorApplication.timeSinceStartup + .6; stage = 4; return;
            }
            if (stage == 4)
            {
                browser.Close(); client.OpenItemBrowser();
                typeof(ItemBrowserHud).GetField("search", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(browser, "Sting");
                captureAt = EditorApplication.timeSinceStartup + .7; stage = 5; return;
            }
            if (stage == 5)
            {
                Array matches = (Array)typeof(ItemBrowserHud).GetField("matches", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(browser);
                if (browser.CatalogCount < 4000 || matches == null || matches.Length == 0 || matches.Length >= browser.CatalogCount)
                { Fail("Client-only item database search failed to filter the bundled catalog."); return; }
                ScreenCapture.CaptureScreenshot("Builds/verification/item-search.png");
                captureAt = EditorApplication.timeSinceStartup + .7; stage = 6; return;
            }
            if (stage == 6)
            {
                Debug.Log("PASS: explicit vendor item inspection preserves credits and cargo; local galaxy item search filters the bundled catalog.");
                EditorApplication.update -= Tick; EditorApplication.Exit(0);
            }
        }

        /// <summary>Exercise real JE cloak activation, its nonzero ghost floor, and material restoration.</summary>
        private static void TickCloak(SpaceClient client)
        {
            ShipCloakVisual visual = typeof(SpaceClient).GetField("cloakVisual",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as ShipCloakVisual;
            if (visual == null) { Fail("Cloak visual is missing."); return; }
            if (stage == 0)
            {
                if (!client.CurrentSnapshot.AvailableAbilities.Contains(0u)) return;
                Transform hull = client.ShipForPreview.Cast<Transform>().FirstOrDefault(child =>
                    child.name.StartsWith("Original Earth and Beyond hull ", StringComparison.Ordinal));
                if (hull == null || Quaternion.Angle(hull.localRotation, Quaternion.Euler(0, 90, 0)) > .1f)
                { Fail("JE hull is facing backwards."); return; }
                Directory.CreateDirectory("Builds/verification");
                captureAt = EditorApplication.timeSinceStartup + 2; stage = 1; return;
            }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            if (stage == 1)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/cloak-before.png");
                client.ActivateAbility(0u); stage = 2; return;
            }
            if (stage == 2)
            {
                if (!client.CurrentSnapshot.ActiveBuffs.Any(buff => buff.Slot == 6) || visual.Amount < 1) return;
                Renderer[] surfaces = client.ShipForPreview.GetComponentsInChildren<Renderer>();
                if (!surfaces.Any(renderer => renderer.sharedMaterials.Any(material => material != null &&
                    material.shader.name == "42 and Beyond/ShipCloak")) || ShipCloakVisual.GhostOpacity < .15f ||
                    surfaces.Any(renderer => !renderer.enabled))
                { Fail("Cloak lost its visible ghost or did not shade the hull."); return; }
                ScreenCapture.CaptureScreenshot("Builds/verification/cloak-active.png");
                captureAt = EditorApplication.timeSinceStartup + 1; stage = 3; return;
            }
            if (stage == 3)
            {
                client.ActivateAbility(0u); stage = 4; return;
            }
            if (stage == 4)
            {
                if (client.CurrentSnapshot.ActiveBuffs.Any(buff => buff.Slot == 6) || visual.Amount > 0) return;
                if (client.ShipForPreview.GetComponentsInChildren<Renderer>().Any(renderer =>
                    renderer.sharedMaterials.Any(material => material != null && material.shader.name == "42 and Beyond/ShipCloak")))
                { Fail("Uncloaking did not restore the original hull materials."); return; }
                ScreenCapture.CaptureScreenshot("Builds/verification/cloak-restored.png");
                captureAt = EditorApplication.timeSinceStartup + 1; stage = 5; return;
            }
            if (stage == 5)
            {
                Debug.Log("PASS: authoritative JE cloak, visible ghost floor and restored opaque hull.");
                EditorApplication.update -= Tick; EditorApplication.Exit(0);
            }
        }

        /// <summary>Capture the imported Rally rigs against a disposable flight scene.</summary>
        private static void TickRallyArt(SpaceClient client)
        {
            if (stage == 0)
            {
                if (client.CurrentSnapshot.AvailableAbilities.Length == 0) return;
                if (!client.CurrentSnapshot.AvailableAbilities.Contains(rallyAbility))
                { Fail("Disposable TE pilot lacks selected Rally tactic."); return; }
                client.ActivateAbility(rallyAbility);
                captureAt = EditorApplication.timeSinceStartup + 6;
                stage = 1;
            }
            ushort descriptor = rallyAbility == 35 ? (ushort)675 :
                rallyAbility == 116 ? (ushort)327 : (ushort)676;
            if (stage == 1 && client.CurrentSnapshot.RallyBuff?.VisualDescriptor == descriptor &&
                EditorApplication.timeSinceStartup >= captureAt)
            {
                Transform ship = typeof(SpaceClient).GetField("ship",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as Transform;
                MeshRenderer wheel = ship?.GetComponentsInChildren<MeshRenderer>(true)
                    .FirstOrDefault(mesh => mesh.name.StartsWith("WHEEL"));
                MeshRenderer ribbon = ship?.GetComponentsInChildren<MeshRenderer>(true)
                    .FirstOrDefault(mesh => mesh.name.StartsWith("GRAPH") && mesh.enabled);
                if (ship == null || wheel == null || ribbon == null ||
                    GameObject.Find("Original RallyDamageAnimated") != null)
                { Fail("Restored Rally wheel or band did not instantiate."); return; }
                RallyHullFrame hull = RallyHullFrame.Measure(ship);
                float wheelHeight = Vector3.Dot(wheel.bounds.center - ship.position, ship.up);
                if (wheelHeight >= hull.BottomY)
                { Fail("Rally wheel is above the visible hull."); return; }
                Debug.Log("RALLY_FRAME center=" + hull.CenterX + "," + hull.CenterY + "," +
                    hull.CenterZ + " width=" + hull.HalfWidth * 2f + " nose=" + hull.NoseZ +
                    " wheel_y=" + wheelHeight);
                ScreenCapture.CaptureScreenshot("Builds/verification/rally-native.png");
                stage = 2;
                return;
            }
            if (stage == 2 && File.Exists("Builds/verification/rally-native.png"))
            {
                typeof(SpaceClient).GetField("orbitYaw", BindingFlags.Instance |
                    BindingFlags.NonPublic)?.SetValue(client, 45f);
                captureAt = EditorApplication.timeSinceStartup + .5;
                stage = 3;
                return;
            }
            if (stage == 3 && EditorApplication.timeSinceStartup >= captureAt)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/rally-oblique.png");
                stage = 4;
                return;
            }
            if (stage != 4 || !File.Exists("Builds/verification/rally-oblique.png")) return;
            Debug.Log("PASS: server-confirmed Rally field rendered without imported mesh clutter.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Verify that Follow turns the rendered ship toward a server-visible MOB.</summary>
        private static void TickFollow(SpaceClient client)
        {
            Snapshot snapshot = client.CurrentSnapshot;
            ShipActor target = snapshot.Mobs.FirstOrDefault(mob => mob.HasPosition &&
                mob.Position.Distance(snapshot.Position) > 500 &&
                mob.Position.Distance(snapshot.Position) < 5000);
            if (stage < 2 && target == null) return;
            if (stage == 0)
            {
                client.Follow(target.Id);
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 1;
                return;
            }
            Transform ship = typeof(SpaceClient).GetField("ship",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as Transform;
            if (ship == null) { Fail("Follow smoke could not find the rendered ship."); return; }
            Quaternion chaseHeading = (Quaternion)typeof(SpaceClient).GetField("chaseHeading",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
            if (stage == 1 && !followTurnVisible &&
                Quaternion.Angle(ship.rotation, chaseHeading) > 15)
            {
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/follow-turn.png");
                followTurnVisible = true;
            }
            if (stage == 2)
            {
                if (EditorApplication.timeSinceStartup < captureAt ||
                    !File.Exists("Builds/verification/follow.png") ||
                    !File.Exists("Builds/verification/follow-turn.png")) return;
                MapObject beacon = snapshot.Objects.FirstOrDefault(item =>
                    item.Name == "Follow Test Beacon");
                if (beacon == null) { Fail("Follow smoke has no world beacon."); return; }
                client.Select(beacon.Id);
                client.FollowSelected();
                stage = 3;
                return;
            }
            if (stage == 4)
            {
                if (EditorApplication.timeSinceStartup < captureAt ||
                    !File.Exists("Builds/verification/follow-object.png")) return;
                typeof(SpaceClient).GetField("orbitYaw", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, 137f);
                typeof(SpaceClient).GetField("orbitPitch", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, -22f);
                client.FollowSelected();
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 5; return;
            }
            if (stage == 5)
            {
                if (EditorApplication.timeSinceStartup < captureAt) return;
                MapObject beacon = snapshot.Objects.First(item => item.Name == "Follow Test Beacon");
                Vector3 heading = SpaceClient.Convert(beacon.Position) - SpaceClient.Convert(snapshot.Position);
                float orbit = (float)typeof(SpaceClient).GetField("orbitYaw", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
                Vector3 projected = client.Project(beacon.Position);
                if (Mathf.Abs(orbit) > .01f || Vector3.Angle(ship.forward, heading) > 8 ||
                    projected.z <= 0 || projected.x < 0 || projected.x > Screen.width)
                { Fail("Close-range Follow did not face and reveal the target after an arbitrary camera orbit."); return; }
                ScreenCapture.CaptureScreenshot("Builds/verification/follow-close-target.png");
                Debug.Log("PASS: Space turns toward ships and world objects, closes to under 1 km, and faces close targets from an arbitrary camera orbit.");
                EditorApplication.update -= Tick;
                EditorApplication.Exit(0);
                return;
            }
            if (stage == 3)
            {
                MapObject beacon = snapshot.Objects.FirstOrDefault(item =>
                    item.Name == "Follow Test Beacon");
                if (beacon == null || beacon.Position.Distance(snapshot.Position) >= 950 ||
                    snapshot.Speed > .5f) return;
                Vector3 towardBeacon = SpaceClient.Convert(beacon.Position) -
                    SpaceClient.Convert(snapshot.Position);
                if (Vector3.Angle(SpaceClient.ConvertRotation(snapshot.Orientation) *
                    Vector3.forward, towardBeacon) > 8f) return;
                ScreenCapture.CaptureScreenshot("Builds/verification/follow-object.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 4;
                return;
            }
            if (EditorApplication.timeSinceStartup < captureAt) return;
            if (!followTurnVisible) { Fail("Follow did not visibly turn in the chase view."); return; }
            Vector3 toward = SpaceClient.Convert(target.Position) -
                SpaceClient.Convert(snapshot.Position);
            float angle = Vector3.Angle(ship.forward, toward);
            float authority = Vector3.Angle(
                SpaceClient.ConvertRotation(snapshot.Orientation) * Vector3.forward, toward);
            if (snapshot.Speed < 1 || angle > 12 || authority > 8) return;
            Directory.CreateDirectory("Builds/verification");
            ScreenCapture.CaptureScreenshot("Builds/verification/follow.png");
            Debug.Log($"Follow heading aligned: render {angle:0.0}°, server {authority:0.0}°.");
            captureAt = EditorApplication.timeSinceStartup + 1;
            stage = 2;
        }

        /// <summary>Capture the native refinery overlay after a disposable station login.</summary>
        private static void TickProduction(SpaceClient client)
        {
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || snapshot.Station == null || snapshot.Production.LabId == 0) return;
            if (stage == 0)
            {
                client.OpenProduction(4);
                stage = 1;
                return;
            }
            if (stage == 1)
            {
                if (snapshot.Production.Mode != 4 ||
                    !snapshot.Production.Formulas.Any(formula => formula.Template == 3381)) return;
                client.SelectProductionRecipe(3381);
                stage = 2;
                return;
            }
            if (stage == 2)
            {
                if (snapshot.Production.Target?.Template != 3381) return;
                client.ShowStationService(4);
                Directory.CreateDirectory("Builds/verification");
                captureAt = EditorApplication.timeSinceStartup + 2;
                stage = 3;
                return;
            }
            if (stage == 3 && EditorApplication.timeSinceStartup >= captureAt)
            {
                ScreenCapture.CaptureScreenshot("Builds/verification/production.png");
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 4;
                return;
            }
            if (stage != 4 || EditorApplication.timeSinceStartup < captureAt) return;
            if (!File.Exists("Builds/verification/production.png"))
            { Fail("Missing workbench capture."); return; }
            Debug.Log("PASS: disposable station workbench formula and graphical refinery overlay");
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
                SelectionRoot().Q<DropdownField>("object-selection-filter").value = "Gates";
                captureAt = EditorApplication.timeSinceStartup + .3;
                stage = 10;
                return;
            }
            if (stage == 10 || stage == 11)
            {
                VisualElement root = SelectionRoot();
                byte kind = stage == 10 ? (byte)11 : (byte)12;
                MapObject[] expected = client.CurrentSnapshot.Objects.Where(item => item.Kind == kind).ToArray();
                VisualElement[] rows = root.Query<VisualElement>(className: "contact").ToList()
                    .Where(row => row.name.StartsWith("destination-", StringComparison.Ordinal)).ToArray();
                if (rows.Length != expected.Length || expected.Any(item => root.Q("destination-" + item.Id) == null))
                { Fail("Object selection filter omitted known destinations or included other types."); return; }
                if (stage == 10 && !expected.Any(item => item.Name.Contains("Saturn")))
                { Fail("ABA selection did not receive the Saturn gate."); return; }
                root.Q<DropdownField>("object-selection-filter").value = stage == 10 ? "Stations" : "All navigation";
                captureAt = EditorApplication.timeSinceStartup + .3;
                if (stage == 11) root.Q<DropdownField>("contact-selection-filter").value = "Players";
                stage = stage == 10 ? 11 : 12;
                return;
            }
            if (stage == 12)
            {
                VisualElement root = SelectionRoot();
                int expected = client.CurrentSnapshot.Players.Count(actor => actor.HasPosition);
                int actual = root.Query<VisualElement>(className: "contact").ToList()
                    .Count(row => row.name.StartsWith("actor-", StringComparison.Ordinal));
                if (actual != Math.Min(24, expected))
                { Fail("Players filter retained unrelated contacts, including an empty result."); return; }
                root.Q<DropdownField>("contact-selection-filter").value = "All contacts";
                captureAt = EditorApplication.timeSinceStartup + .3; stage = 13;
                return;
            }
            if (stage == 13)
            {
                ShipActor mob = client.TargetActor;
                if (mob?.CombatLevel == null || !SelectionRoot().Q("panel-target").Query<Label>().ToList()
                    .Any(label => label.text.Contains("CL " + mob.CombatLevel.Value)))
                { Fail("Selected mob HUD does not show its server-provided combat level."); return; }
                ScreenCapture.CaptureScreenshot(UiCaptureName("mob-target"));
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 2;
                return;
            }
            if (stage == 2)
            {
                if (!File.Exists(UiCaptureName("mob-target"))) { Fail("Missing mob target capture."); return; }
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
                ScreenCapture.CaptureScreenshot(UiCaptureName("equipment"));
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 4;
                return;
            }
            if (stage == 4)
            {
                if (!File.Exists(UiCaptureName("equipment"))) { Fail("Missing equipment capture."); return; }
                FieldInfo field = typeof(SpaceClient).GetField("hud", BindingFlags.Instance | BindingFlags.NonPublic);
                (field?.GetValue(client) as FlightHud)?.ToggleEquipment();
                client.SetChart(true);
                captureAt = EditorApplication.timeSinceStartup + 2;
                stage = 5;
                return;
            }
            if (stage == 5)
            {
                FlightHud mapHud = typeof(SpaceClient).GetField("hud", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as FlightHud;
                TacticalSectorMap map = typeof(FlightHud).GetField("tacticalMap", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(mapHud) as TacticalSectorMap;
                if (map == null) { Fail("Dedicated tactical map was not created."); return; }
                Snapshot mapSnapshot = client.CurrentSnapshot;
                MapObject[] navigables = mapSnapshot.Objects.Where(TacticalSectorMap.IsNavigationTarget).ToArray();
                if (map.NavigationCount != navigables.Length || navigables.Any(item => !map.ContainsNavigationTarget(item.Id)))
                { Fail("Sector map discarded valid known navigation points."); return; }
                MapObject[] scenery = mapSnapshot.Objects.Where(item => item.Kind == 25 || item.Kind == 38 ||
                    item.Kind != 11 && item.Kind != 12 && item.Name.StartsWith("Asteroid", StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Replace(" ", "").IndexOf("BorderDemarcation", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                if (scenery.Any(item => map.ContainsNavigationTarget(item.Id)) ||
                    mapSnapshot.Players.Concat(mapSnapshot.Mobs).Any(actor => map.ContainsNavigationTarget(actor.Id)))
                { Fail("Navigation-only map displayed asteroid, border, resource, wreck or actor markers."); return; }
                if (mapSnapshot.Objects.Any(item => item.Name == "Dutch Flat") &&
                    !mapSnapshot.Objects.Where(item => item.Name == "Dutch Flat").All(item => map.ContainsNavigationTarget(item.Id)))
                { Fail("Navigation map incorrectly filtered a legitimate asteroid-asset navigation point."); return; }
                MapObject marker = client.CurrentSnapshot.Objects.First(item => item.Navigable && item.Name == "Bedrock Cluster");
                Vector2 point = map.ProjectPosition(marker.Position);
                if (!map.PlotBounds.Contains(point) || !map.SelectAtPoint(point))
                { Fail("Fit sector did not expose a distant navigation marker."); return; }
                if (client.Target?.Id != marker.Id) { Fail("Chart marker did not select its destination."); return; }
                ScreenCapture.CaptureScreenshot(UiCaptureName("ui-chart"));
                captureAt = EditorApplication.timeSinceStartup + 1;
                stage = 6;
                return;
            }
            if (!File.Exists(UiCaptureName("ui-chart"))) { Fail("Missing chart capture."); return; }
            FlightHud finalHud = typeof(SpaceClient).GetField("hud", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client) as FlightHud;
            TacticalSectorMap finalMap = typeof(FlightHud).GetField("tacticalMap", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(finalHud) as TacticalSectorMap;
            var labelHits = (System.Collections.IList)typeof(TacticalSectorMap).GetField("labelHits", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(finalMap);
            if (labelHits.Count > 2 || SelectionRoot().Q("panel-browse").resolvedStyle.visibility != Visibility.Visible)
            { Fail("Default floating map has excessive labels or hides the flight cards."); return; }
            Debug.Log("PASS: tactical target, equipment and uncluttered legacy-style chart panels captured.");
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// Returns:
        ///     VisualElement: The retained flight deck used by the actual selection controls.
        private static VisualElement SelectionRoot()
        {
            return UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None)
                .Select(document => document.rootVisualElement)
                .First(root => root.Q<DropdownField>("object-selection-filter") != null);
        }

        /// <returns>string: The standard or small Game View capture path for one HUD view.</returns>
        private static string UiCaptureName(string stem)
        { return "Builds/verification/" + stem + (smallUiSmoke ? "-small" : "") + ".png"; }

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
