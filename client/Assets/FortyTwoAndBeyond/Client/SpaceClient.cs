using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;
using CharacterRecord = FortyTwoAndBeyond.Protocol.CharacterInfo;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Own the rendered world and foreground input; networking has a separate owner.</summary>
    public sealed class SpaceClient : MonoBehaviour
    {
        private const int ForegroundFrameRate = 60;
        private const int BackgroundFrameRate = 30;
        public const float MetersToWorld = .01f;
        private ClientWorker worker;
        private DemoRosterWorker demoRoster;
        private KeyBindings bindings;
        private CharacterPickerWorker picker;
        private CharacterRecord[] characters;
        private Snapshot snapshot;
        private MapObject[] renderedObjects = System.Array.Empty<MapObject>();
        private Camera view;
        private SpaceArt art;
        private RemoteShips remoteShips;
        private CombatEffects combatEffects;
        private FlightHud hud;
        private MiningHud miningHud;
        private AbilityHud abilityHud;
        internal AbilityHud AbilitiesUi => abilityHud;
        internal MissionHud MissionsUi => missionHud;
        internal DefenseHud DefenseUi => defenseHud;
        private RecoveryHud recoveryHud;
        private VaultHud vaultHud;
        private ProductionHud productionHud;
        private ComponentMarketHud componentMarketHud;
        private GalaxyTravelHud galaxyTravelHud;
        private MissionHud missionHud;
        private DefenseHud defenseHud;
        private readonly FlightThrottle flightThrottle = new FlightThrottle();
        private readonly StationWalkState stationWalk = new StationWalkState();
        private readonly LootSweep lootSweep = new LootSweep();
        public bool AbilitiesOpen => abilityHud != null && abilityHud.IsOpen;
        public bool Incapacitated => snapshot != null && snapshot.Station == null &&
            snapshot.MaxHull > 0 && snapshot.Hull <= 0;
        private Transform ship, worldRoot, environment, systemSun, hangar, concourse, ownStationAvatar;
        private Light sunLight;
        private readonly Dictionary<uint, Transform> bodies = new Dictionary<uint, Transform>();
        private readonly Dictionary<uint, Transform> stationBodies = new Dictionary<uint, Transform>();
        private StationCharacters stationCharacters;
        private string host = "192.168.47.1", username = "", password = "", status = "Ready to connect", sector = "";
        private readonly NavigationSelection navigationSelection = new NavigationSelection();
        private uint pendingNavigationTarget;
        private bool chart, positioned;
        private int flightCameraMask;
        private bool chatOpen;
        private int chatOpenedFrame = -1;
        private int lastFrameRate = ForegroundFrameRate;
        private int sampledFrames, displayFps;
        private int guiPasses;
        private long guiTicks;
        private float sampledSeconds, slowestFrameMs, displayFrameMs, nextPerformanceLog, lastSnapshotAt;
        private float previousYaw, previousPitch, orbitYaw, orbitPitch = 8, zoom = 2.8f, mapZoom = 1400;
        private int selectedShipClass = -1;
        private uint selectedHullStage = uint.MaxValue;
        private Vector3 renderedPosition;
        private float authoritativePoseAt;
        private uint visualFollowTarget;
        private float visualFollowUntil;
        private Quaternion chaseHeading;
        private bool chaseHeadingReady;
        private float chaseHoldUntil;
        private float skyWarp;
        private float nextStationPoseAt;
        public Snapshot CurrentSnapshot => snapshot;
        public Transform ShipForPreview => ship;
        public string ConnectionError => worker?.Error;
        public string Status => status;
        public bool Connected => worker != null || picker != null;
        public bool ChoosingCharacter => picker != null && Characters != null;
        public bool PickingCharacter => picker != null;
        public CharacterRecord[] Characters => picker?.Characters ?? characters;
        public string LoginError => picker?.Error ?? worker?.Error;
        public DemoAccount[] DemoAccounts => demoRoster?.Accounts;
        public string DemoRosterError => demoRoster?.Error;
        public string DemoRosterHost { get; private set; }
        public bool Ready => worker != null && worker.Running && snapshot != null && snapshot.Ready;
        public bool LootAllActive => lootSweep.Active;
        public string LootAllStatus => lootSweep.Status;
        public int LootAllCollectedStacks => lootSweep.CollectedStacks;
        public bool Chart => chart;
        public float ChartRadiusKm => mapZoom / 10f;
        public MapObject Target => snapshot?.Objects.FirstOrDefault(item => item.Id == navigationSelection.TargetId);
        public ShipActor TargetActor => snapshot?.Players.Concat(snapshot.Mobs)
            .FirstOrDefault(actor => actor.Id == navigationSelection.TargetId);
        public uint WarpTargetId => navigationSelection.TargetId;
        public bool FreeFlight => navigationSelection.FreeFlight;
        public int RenderedBodyCount => bodies.Count;
        public int Fps => displayFps;
        public float FrameMs => displayFrameMs;
        public KeyBindings Bindings => bindings;

        /// <summary>Create the space client when the bootstrap scene starts.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(),
                "--42andbeyond-animation-probe") >= 0) return;
            if (FindFirstObjectByType<SpaceClient>() == null)
                new GameObject("42 and Beyond").AddComponent<SpaceClient>();
        }

        /// <summary>Create lighting, reusable models, the camera and the flight HUD.</summary>
        private void Awake()
        {
            bindings = new KeyBindings();
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = ForegroundFrameRate;
            art = new SpaceArt(); stationCharacters = new StationCharacters(art, transform);
            hud = new FlightHud(this); miningHud = new MiningHud(this, hud);
            abilityHud = new AbilityHud(this, hud);
            recoveryHud = new RecoveryHud(this, hud);
            vaultHud = new VaultHud(this, hud);
            productionHud = new ProductionHud(this, hud);
            componentMarketHud = new ComponentMarketHud(this, hud);
            galaxyTravelHud = new GalaxyTravelHud(this, hud);
            missionHud = new MissionHud(this, hud);
            defenseHud = new DefenseHud(hud);
            worldRoot = new GameObject("Server world").transform; worldRoot.SetParent(transform, false);
            remoteShips = new RemoteShips(art, worldRoot);
            ship = art.Ship(transform);
            combatEffects = new CombatEffects(transform, ship, worldRoot, remoteShips);
            ship.localScale = Vector3.one * .25f;
            environment = art.Environment(transform);
            systemSun = art.Sun(environment);
            hangar = art.Hangar(transform); hangar.gameObject.SetActive(false);
            concourse = art.StationConcourse(transform); concourse.gameObject.SetActive(false);
            ownStationAvatar = art.StationAvatar(transform);
            ownStationAvatar.name = "Your station avatar";
            ownStationAvatar.gameObject.SetActive(false);
            RenderSettings.skybox = art.Sky; RenderSettings.ambientLight = new Color(.2f, .24f, .34f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            var sunLamp = new GameObject("System sunlight", typeof(Light)); sunLamp.transform.SetParent(transform, false);
            sunLight = sunLamp.GetComponent<Light>(); sunLight.type = LightType.Directional;
            sunLight.intensity = 1.6f;
            sunLight.shadows = LightShadows.Soft; QualitySettings.shadowDistance = 150; QualitySettings.antiAliasing = 4;
            var rim = new GameObject("Blue rim light", typeof(Light)); rim.transform.SetParent(transform, false);
            rim.transform.rotation = Quaternion.Euler(15, 155, 0);
            Light rimLight = rim.GetComponent<Light>(); rimLight.type = LightType.Directional;
            rimLight.color = new Color(.48f, .68f, 1f); rimLight.intensity = .55f;
            view = Camera.main;
            if (view == null) view = new GameObject("Flight camera", typeof(Camera)).GetComponent<Camera>();
            flightCameraMask = view.cullingMask;
            view.clearFlags = CameraClearFlags.Skybox; view.nearClipPlane = .015f; view.farClipPlane = 35000; view.fieldOfView = 60;
            view.transform.position = new Vector3(0, 1, -zoom); view.transform.LookAt(Vector3.zero);
            ConfigureSectorSun("Asteroid Belt Alpha");
        }

        /// <summary>Give each sector a stable visible sun and matching light direction.</summary>
        private void ConfigureSectorSun(string sectorName)
        {
            uint hash = 2166136261;
            foreach (char letter in sectorName)
                hash = (hash ^ letter) * 16777619;
            art.Sky.SetFloat("_Yaw", ((hash >> 12) & 255) / 255f);
            float east = 13200 + ((hash & 15) - 7.5f) * 140;
            float altitude = 1700 + (((hash >> 4) & 15) - 7.5f) * 90;
            Vector3 position = new Vector3(east, altitude, 22000);
            systemSun.localPosition = position;
            sunLight.transform.rotation = Quaternion.LookRotation(-position.normalized, Vector3.up);
            Color color = Color.Lerp(new Color(1f, .75f, .48f),
                new Color(.8f, .89f, 1f), ((hash >> 8) & 15) / 15f);
            sunLight.color = color;
            art.TintSun(color);
        }

        /// <summary>Connect the owned local demo when invoked by the project launcher.</summary>
        private void Start()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            if (System.Array.IndexOf(args, "--42andbeyond-local") >= 0)
                StartLocalSession("127.0.0.1", "local", "local");
            if (System.Array.IndexOf(args, "--42andbeyond-vm") >= 0)
                StartLocalSession("192.168.47.128", "toon", "pass");
            if (System.Array.IndexOf(args, "--42andbeyond-local") < 0 &&
                System.Array.IndexOf(args, "--42andbeyond-vm") < 0)
                DiscoverDemoAccounts(host);
        }

        /// <summary>Query only the server's published sandbox class roster.</summary>
        public void DiscoverDemoAccounts(string address)
        {
            demoRoster?.Dispose(); demoRoster = null;
            DemoRosterHost = null;
            try { demoRoster = new DemoRosterWorker(address); DemoRosterHost = address; }
            catch (System.ArgumentException error) { status = error.Message; }
        }

        /// <summary>Join the previous worker before replacing the connection.</summary>
        public void StartLocalSession(string address, string account, string secret)
        {
            Disconnect(); host = address; username = account; password = secret;
            try { LocalSession.LocalAddress(host); picker = new CharacterPickerWorker(host, username, password); status = "Checking account…"; }
            catch (System.ArgumentException error) { status = error.Message; }
        }

        /// <summary>Enter the selected server character after account lookup.</summary>
        public void ChooseCharacter(int slot)
        {
            CharacterRecord character = Characters?.FirstOrDefault(item => item.Slot == slot);
            if (!ChoosingCharacter || character == null) return;
            string name = character.Name;
            selectedShipClass = character.ClassIndex;
            selectedHullStage = 0;
            art.SetShipClass(ship, character.ClassIndex);
            picker.Dispose(); picker = null;
            worker = new ClientWorker(host, username, password, slot,
                navigationData: Resources.Load<TextAsset>("GalaxyNavigation")?.bytes); password = "";
            status = "Entering " + name + "…";
        }

        /// <summary>Return to the account form after a failed lookup or session.</summary>
        public void ReturnToLogin() { Disconnect(); }

        /// <summary>Open a separate login window from this installed game build.</summary>
        public void OpenAnotherClient()
        {
            if (Application.isEditor)
            { status = "Open another client from the installed game."; return; }
            try
            {
                string executable = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                if (string.IsNullOrEmpty(executable) || !System.IO.File.Exists(executable))
                    throw new System.IO.FileNotFoundException("Game executable is unavailable.");
                string root = Application.platform == RuntimePlatform.OSXPlayer ?
                    System.IO.Path.Combine(System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.UserProfile), "Library", "Logs", "42 and Beyond") :
                    System.IO.Path.Combine(System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.LocalApplicationData), "42 and Beyond", "Logs");
                System.IO.Directory.CreateDirectory(root);
                string log = System.IO.Path.Combine(root, "player-" +
                    System.DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" +
                    System.Guid.NewGuid().ToString("N").Substring(0, 6) + ".log");
                string quotedLog = "\"" + log.Replace("\"", "\\\"") + "\"";
                System.Diagnostics.ProcessStartInfo start;
                if (Application.platform == RuntimePlatform.OSXPlayer)
                {
                    string bundle = System.IO.Directory.GetParent(executable).Parent.Parent.FullName;
                    string quotedBundle = "\"" + bundle.Replace("\"", "\\\"") + "\"";
                    start = new System.Diagnostics.ProcessStartInfo("/usr/bin/open",
                        "-n -a " + quotedBundle + " --args -logFile " + quotedLog);
                }
                else
                    start = new System.Diagnostics.ProcessStartInfo(executable,
                        "-logFile " + quotedLog);
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                System.Diagnostics.Process launched = System.Diagnostics.Process.Start(start);
                if (launched == null) throw new System.InvalidOperationException("No client process started.");
                launched.Dispose();
                status = "Opened another client. Choose a different pilot in its login window.";
            }
            catch (System.Exception error)
            {
                status = "Could not open another client: " + error.Message;
                UnityEngine.Debug.LogError(status);
            }
        }

        /// <returns>Unity-space coordinates in hundred-meter units.</returns>
        public static Vector3 Convert(Position position)
        { return new Vector3(position.Y, position.Z, position.X) * MetersToWorld; }

        /// <returns>A rotation preserving the server's forward and up axes.</returns>
        public static Quaternion ConvertRotation(float[] q)
        {
            var source = new Quaternion(q[0], q[1], q[2], q[3]);
            if (Quaternion.Dot(source, source) < .00001f) return Quaternion.identity;
            source = source.normalized; Vector3 forward = source * Vector3.right, up = source * Vector3.forward;
            return Quaternion.LookRotation(new Vector3(forward.y, forward.z, forward.x), new Vector3(up.y, up.z, up.x));
        }

        /// <returns>The current visual heading projected from native steering rates.</returns>
        private static Quaternion ProjectRotation(Snapshot state, float age)
        {
            Quaternion source = new Quaternion(state.Orientation[0], state.Orientation[1],
                state.Orientation[2], state.Orientation[3]).normalized;
            float yaw = state.AngularZ * 420f * age;
            float pitch = state.AngularY * 420f * age;
            source = (source * new Quaternion(0, 0, yaw, 1).normalized *
                new Quaternion(0, pitch, 0, 1).normalized).normalized;
            Vector3 forward = source * Vector3.right, up = source * Vector3.forward;
            return Quaternion.LookRotation(new Vector3(forward.y, forward.z, forward.x),
                new Vector3(up.y, up.z, up.x));
        }

        /// <summary>Consume snapshots and route keyboard and mouse actions.</summary>
        private void Update()
        {
            MeasureFrame();
            int frameRate = Application.isFocused ? ForegroundFrameRate : BackgroundFrameRate;
            if (frameRate != lastFrameRate)
            {
                Application.targetFrameRate = frameRate;
                lastFrameRate = frameRate;
            }
            if (picker?.Characters != null)
            {
                characters = picker.Characters;
                status = "Choose a character";
            }
            if (picker?.Error != null) status = picker.Error;
            if (worker?.Error != null) status = worker.Error;
            Snapshot next = worker?.Latest;
            if (next != null && !ReferenceEquals(next, snapshot))
            {
                bool travelEnded = snapshot != null &&
                    ((snapshot.WarpPhase == WarpPhase.Charging ||
                      snapshot.WarpPhase == WarpPhase.Traveling) &&
                     (next.WarpPhase == WarpPhase.Recovering || next.WarpPhase == WarpPhase.Idle) ||
                     snapshot.Speed > 1000f && next.Speed < 100f);
                bool positionChanged = next.HasPosition && snapshot?.HasPosition == true &&
                    next.Position.Distance(snapshot.Position) > 1f;
                bool poseChanged = next.HasPosition && (snapshot == null || !snapshot.HasPosition ||
                    next.Position.Distance(snapshot.Position) > .001f || next.Speed != snapshot.Speed ||
                    next.AngularY != snapshot.AngularY || next.AngularZ != snapshot.AngularZ ||
                    !next.Orientation.SequenceEqual(snapshot.Orientation));
                snapshot = next; if (worker.Error == null) status = next.Status;
                if (selectedShipClass >= 0 && next.Experience != null &&
                    selectedHullStage != next.Experience.HullStage)
                {
                    selectedHullStage = next.Experience.HullStage;
                    art.SetShipClass(ship, selectedShipClass, selectedHullStage);
                }
                if (lootSweep.Active)
                {
                    CorpseLoot contents = next.Loot.FirstOrDefault(item => item.Id == lootSweep.CorpseId);
                    int? slot = lootSweep.Observe(next.OpenLootId, next.SelectedTargetId,
                        contents, next.LootActivity,
                        next.Station != null || next.WarpPhase != WarpPhase.Idle,
                        System.Diagnostics.Stopwatch.GetTimestamp());
                    if (slot.HasValue) TakeLoot(lootSweep.CorpseId, slot.Value);
                }
                lastSnapshotAt = Time.unscaledTime;
                if (sector != next.Sector)
                { ClearWorld(); sector = next.Sector; positioned = false; ConfigureSectorSun(sector); }
                hud?.ObserveAuthoritativeTarget(next);
                ApplyWorld(next);
                RefreshNavigationSelection(next);
                if (Ready && pendingNavigationTarget != 0 &&
                    navigationSelection.TargetId == pendingNavigationTarget)
                { SelectWorldObject(pendingNavigationTarget); pendingNavigationTarget = 0; }
                ApplyStationAvatars(next);
                stationCharacters.Apply(next.Station);
                remoteShips.Apply(next.Players.Concat(next.Mobs).ToArray(), next.Group,
                    next.SelectedTargetId, next.Position);
                combatEffects.Apply(next);
                if (poseChanged) authoritativePoseAt = Time.unscaledTime;
                if (!positioned && next.HasPosition) { renderedPosition = Convert(next.Position); positioned = true; }
                else if (travelEnded && positionChanged) renderedPosition = Convert(next.Position);
            }
            if (!Ready || !Application.isFocused)
            { flightThrottle.Reset(); StopStationWalk(); return; }
            if (bindings.Pressed(ControlAction.Galaxy) && !chatOpen && !hud.InputFocused && !hud.SettingsOpen &&
                !hud.GroupInviteOpen && !GalaxyTravelOpen) OpenGalaxyTravel();
            if (bindings.Pressed(ControlAction.OpenChat) && !chatOpen && !hud.InputFocused &&
                !hud.SettingsOpen && !hud.GroupInviteOpen && !ComponentMarketOpen && !GalaxyTravelOpen)
            {
                OpenChatEntry();
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (hud.GroupInviteOpen) AnswerConfirmation(false);
                else if (GalaxyTravelOpen) galaxyTravelHud.Close();
                else if (ComponentMarketOpen) componentMarketHud.Close();
                else { chatOpen = false; hud.ReleaseInputFocus(); }
            }
            if (hud.InputFocused && Input.GetMouseButtonDown(0) && !hud.ContainsPointer())
                hud.ReleaseInputFocus();
            if (chatOpen || hud.InputFocused || hud.SettingsOpen || hud.GroupInviteOpen || ComponentMarketOpen || GalaxyTravelOpen)
            {
                flightThrottle.Reset();
                StopStationWalk();
                if ((previousYaw != 0 || previousPitch != 0) && !FollowingFormation)
                    worker.Submit(new ClientCommand(CommandKind.Steer));
                previousYaw = previousPitch = 0;
                return;
            }
            if (snapshot.Station != null)
            {
                flightThrottle.Reset();
                if (bindings.Pressed(ControlAction.Equipment)) hud.ToggleEquipment();
                if (bindings.Pressed(ControlAction.Launch)) Launch();
                if (snapshot.Dialogue != null || hud.EquipmentOpen) StopStationWalk();
                else UpdateStationWalk(snapshot.Station.CurrentRoom);
                return;
            }
            if (Incapacitated)
            {
                flightThrottle.Reset();
                if (previousYaw != 0 || previousPitch != 0)
                    worker.Submit(new ClientCommand(CommandKind.Steer));
                previousYaw = previousPitch = 0;
                return;
            }
            if (bindings.Pressed(ControlAction.Chart)) SetChart(!chart);
            if (bindings.Pressed(ControlAction.Equipment)) hud.ToggleEquipment();
            if (bindings.Pressed(ControlAction.Fire))
            {
                if (snapshot.WeaponsFiring) StopWeapons();
                else ToggleWeapons(hud.SelectedContactId);
            }
            if (bindings.Pressed(ControlAction.TurboWeapons)) ActivateTurboWeapons();
            if (bindings.Pressed(ControlAction.NextHostile)) TargetNextHostile();
            if (bindings.Pressed(ControlAction.NextAttacker)) TargetNextAttacker();
            for (int abilitySlot = 0; abilitySlot < 6; abilitySlot++)
                if (bindings.Pressed((ControlAction)((int)ControlAction.AbilityOne + abilitySlot)))
                    abilityHud.ActivateSlot(abilitySlot, snapshot);
            if (bindings.Pressed(ControlAction.Warp)) WarpSelected();
            if (bindings.Pressed(ControlAction.Gate)) GateSelected();
            if (bindings.Pressed(ControlAction.Dock)) DockSelected();
            if (bindings.Pressed(ControlAction.Follow)) FollowSelected();
            if (bindings.Pressed(ControlAction.ResetCamera)) { orbitYaw = 0; orbitPitch = 8; }
            if (!FollowingFormation)
            {
                float yaw = (bindings.Held(ControlAction.TurnLeft) ? 1 : 0) -
                    (bindings.Held(ControlAction.TurnRight) ? 1 : 0);
                float pitch = (bindings.Held(ControlAction.PitchUp) ? 1 : 0) -
                    (bindings.Held(ControlAction.PitchDown) ? 1 : 0);
                if (yaw != previousYaw || pitch != previousPitch)
                {
                    worker.Submit(new ClientCommand(CommandKind.Steer, yaw: yaw, pitch: pitch));
                    previousYaw = yaw; previousPitch = pitch;
                    flightThrottle.Reset();
                }
                byte? thrust = flightThrottle.Sample(true, bindings.Held(ControlAction.Forward),
                    bindings.Held(ControlAction.Reverse), bindings.Held(ControlAction.Brake));
                if (thrust.HasValue) Thrust(thrust.Value);
            }
            else { previousYaw = previousPitch = 0; flightThrottle.Reset(); }
            if (!hud.ContainsPointer() && !defenseHud.ContainsPointer(snapshot) && !miningHud.ContainsPointer() &&
                !abilityHud.ContainsPointer() && !missionHud.ContainsPointer())
            {
                if (Input.GetMouseButton(1) && !chart)
                { orbitYaw += Input.GetAxis("Mouse X") * 3; orbitPitch = Mathf.Clamp(orbitPitch - Input.GetAxis("Mouse Y") * 3, -65, 65); }
                float wheel = Input.mouseScrollDelta.y;
                if (!chart) zoom = Mathf.Clamp(zoom * Mathf.Exp(-wheel * .1f), 1.5f, 14);
                if (!chart && Input.GetMouseButtonDown(0)) SelectAtPointer();
            }
        }

        /// <summary>Publish one-second frame pacing measurements for the HUD.</summary>
        private void MeasureFrame()
        {
            float delta = Time.unscaledDeltaTime;
            if (delta <= 0 || delta > 1) return;
            sampledSeconds += delta; sampledFrames++;
            slowestFrameMs = Mathf.Max(slowestFrameMs, delta * 1000);
            if (sampledSeconds < 1) return;
            float guiMsPerFrame = guiTicks * 1000f /
                System.Diagnostics.Stopwatch.Frequency / Mathf.Max(1, sampledFrames);
            float guiPassesPerFrame = guiPasses / (float)Mathf.Max(1, sampledFrames);
            displayFps = Mathf.RoundToInt(sampledFrames / sampledSeconds);
            displayFrameMs = slowestFrameMs;
            sampledFrames = 0; sampledSeconds = slowestFrameMs = 0;
            guiTicks = 0; guiPasses = 0;
            if (Time.unscaledTime >= nextPerformanceLog)
            {
                float snapshotAge = snapshot == null ? -1 : Time.unscaledTime - lastSnapshotAt;
                string currentStatus = (status ?? "").Replace('\r', ' ').Replace('\n', ' ');
                if (currentStatus.Length > 96) currentStatus = currentStatus.Substring(0, 96);
                Debug.Log($"42 and Beyond frame pacing: {displayFps} FPS, slowest {displayFrameMs:0.0} ms, " +
                    $"HUD {guiMsPerFrame:0.00} ms/frame in {guiPassesPerFrame:0.0} passes, " +
                    $"focused {Application.isFocused}, contacts {remoteShips.Count}, worker {worker?.Running == true}, " +
                    $"ready {Ready}, snapshot age {snapshotAge:0.0} s, chat {chatOpen}, " +
                    $"input focus {hud?.InputFocused == true}, settings {hud?.SettingsOpen == true}, " +
                    $"sector {sector}, status {currentStatus}");
                nextPerformanceLog = Time.unscaledTime + 30;
            }
        }

        /// <summary>Create visible scenery using the server's positions, rotations and scale.</summary>
        private void ApplyWorld(Snapshot next)
        {
            if (renderedObjects.SequenceEqual(next.Objects)) return;
            renderedObjects = next.Objects;
            var ids = new HashSet<uint>();
            foreach (MapObject item in next.Objects)
            {
                ids.Add(item.Id);
                if (!bodies.TryGetValue(item.Id, out Transform body))
                { body = art.WorldObject(worldRoot, item); if (body != null) bodies[item.Id] = body; }
                if (body != null) { body.localPosition = Convert(item.Position); body.localRotation = ConvertRotation(item.Orientation); }
            }
            foreach (uint id in bodies.Keys.Where(id => !ids.Contains(id)).ToArray())
            { Destroy(bodies[id].gameObject); bodies.Remove(id); }
        }

        /// <summary>Keep explicit object and actor targets while retaining a default nav route.</summary>
        private void RefreshNavigationSelection(Snapshot next)
        {
            uint nearest = next.Objects.Where(item => item.Navigable)
                .OrderBy(item => item.Position.Distance(next.Position)).FirstOrDefault()?.Id ?? 0;
            uint previous = navigationSelection.TargetId;
            var visible = new HashSet<uint>(next.Objects.Where(WarpTargets.WorldObject).Select(item => item.Id));
            foreach (ShipActor actor in next.Players.Concat(next.Mobs).Where(actor => actor.HasPosition))
                visible.Add(actor.Id);
            uint selected = navigationSelection.Resolve(visible, nearest);
            if (selected != previous) pendingNavigationTarget = selected;
        }

        /// <summary>Combine server-advertised station pilots into the Unity concourse.</summary>
        private void ApplyStationAvatars(Snapshot next)
        {
            uint room = next.Station?.CurrentRoom ?? uint.MaxValue;
            var visible = new HashSet<uint>();
            foreach (StationAvatar avatar in next.StationAvatars.Where(item =>
                room != uint.MaxValue && (next.AggregateStation || item.Room == room)))
            {
                visible.Add(avatar.Id);
                if (!stationBodies.TryGetValue(avatar.Id, out Transform body))
                {
                    body = art.StationAvatar(transform);
                    body.name = avatar.Name + " station avatar";
                    stationBodies[avatar.Id] = body;
                }
                float x = float.IsNaN(avatar.Position.X) || float.IsInfinity(avatar.Position.X)
                    ? 0 : avatar.Position.X;
                float z = float.IsNaN(avatar.Position.Z) || float.IsInfinity(avatar.Position.Z)
                    ? 0 : avatar.Position.Z;
                body.localPosition = new Vector3(Mathf.Clamp(x * .25f, -6, 6), 0,
                    Mathf.Clamp(z * .25f, -9, 9));
                body.GetComponent<StationSuitMotion>()?.ReportPose(body.localPosition);
                float orientation = float.IsNaN(avatar.Orientation) || float.IsInfinity(avatar.Orientation)
                    ? 0 : avatar.Orientation;
                body.localRotation = Quaternion.Euler(0, orientation * Mathf.Rad2Deg, 0);
            }
            foreach (uint id in stationBodies.Keys.Where(id => !visible.Contains(id)).ToArray())
            { Destroy(stationBodies[id].gameObject); stationBodies.Remove(id); }
        }

        /// <summary>Walk the local station avatar and publish bounded poses at five hertz.</summary>
        private void UpdateStationWalk(uint room)
        {
            if (room == uint.MaxValue) return;
            if (stationWalk.Room != room)
            { stationWalk.EnterRoom(room); nextStationPoseAt = Time.unscaledTime + .2f; }
            bool wasMoving = stationWalk.Moving;
            stationWalk.Advance(Time.unscaledDeltaTime,
                bindings.Held(ControlAction.Forward), bindings.Held(ControlAction.Reverse),
                bindings.Held(ControlAction.TurnLeft), bindings.Held(ControlAction.TurnRight));
            if (stationWalk.Moving && Time.unscaledTime >= nextStationPoseAt)
            {
                PublishStationPose(1);
                nextStationPoseAt = Time.unscaledTime + .2f;
            }
            else if (wasMoving && !stationWalk.Moving) PublishStationPose(0);
        }

        /// <summary>Publish the avatar's latest room pose without moving the ship.</summary>
        private void PublishStationPose(uint action)
        {
            if (stationWalk.Room == uint.MaxValue || worker == null) return;
            worker.Submit(new ClientCommand(CommandKind.StationPose, stationRoom: stationWalk.Room,
                stationAction: action, stationOrientation: stationWalk.Orientation,
                stationX: stationWalk.X, stationZ: stationWalk.Z));
        }

        /// <summary>Tell room peers when station walking ends or input is suspended.</summary>
        private void StopStationWalk()
        { if (stationWalk.Stop()) PublishStationPose(0); }

        /// <summary>Smooth authoritative poses and position the camera without changing gameplay state.</summary>
        private void LateUpdate()
        {
            hud.UpdateRetained();
            float delta = Mathf.Min(Time.unscaledDeltaTime, .1f);
            float blend = 1 - Mathf.Exp(-14 * delta);
            bool docked = snapshot?.Station != null;
            if (snapshot != null && !docked)
            {
                bool formed = snapshot.Formation != null && snapshot.Group.Formed;
                bool fastTravel = snapshot.WarpPhase == WarpPhase.Charging ||
                    snapshot.WarpPhase == WarpPhase.Traveling || snapshot.Speed > 1000f;
                float horizon = fastTravel ? .7f : .35f;
                float poseAge = Mathf.Max(0, Time.unscaledTime - authoritativePoseAt);
                float age = Mathf.Min(poseAge, horizon);
                Quaternion projectedRotation = formed ? ConvertRotation(snapshot.Orientation) :
                    ProjectRotation(snapshot, age);
                Quaternion displayedRotation = projectedRotation;
                if (visualFollowTarget != 0)
                {
                    if (Time.unscaledTime >= visualFollowUntil || formed)
                        visualFollowTarget = 0;
                    else
                    {
                        ShipActor followed = snapshot.Players.Concat(snapshot.Mobs)
                            .FirstOrDefault(actor => actor.Id == visualFollowTarget && actor.HasPosition);
                        MapObject objectTarget = followed == null ? snapshot.Objects
                            .FirstOrDefault(item => item.Id == visualFollowTarget &&
                                WarpTargets.WorldObject(item)) : null;
                        Vector3 toward = followed != null ?
                            Convert(followed.Position) - Convert(snapshot.Position) :
                            objectTarget != null ? Convert(objectTarget.Position) -
                            Convert(snapshot.Position) : Vector3.zero;
                        if (toward.sqrMagnitude > .0001f)
                        {
                            Quaternion facing = Quaternion.LookRotation(toward, projectedRotation * Vector3.up);
                            if (Quaternion.Angle(projectedRotation, facing) < 8f) visualFollowTarget = 0;
                            else displayedRotation = facing;
                        }
                        else visualFollowTarget = 0;
                    }
                }
                ship.rotation = Quaternion.Slerp(ship.rotation, displayedRotation, blend);
                float ramp = snapshot.Acceleration > .001f
                    ? Mathf.Min(age, Mathf.Abs(snapshot.TargetSpeed - snapshot.Speed) / snapshot.Acceleration) : 0;
                float direction = Mathf.Sign(snapshot.TargetSpeed - snapshot.Speed);
                float currentSpeed = snapshot.Speed + direction * snapshot.Acceleration * ramp;
                float distance = snapshot.Speed * ramp +
                    .5f * direction * snapshot.Acceleration * ramp * ramp + currentSpeed * (age - ramp);
                Vector3 velocity = projectedRotation * Vector3.forward *
                    (currentSpeed * MetersToWorld);
                Vector3 predicted = Convert(snapshot.Position) +
                    projectedRotation * Vector3.forward * (distance * MetersToWorld);
                if (poseAge < horizon) renderedPosition += velocity * delta;
                renderedPosition = Vector3.Distance(renderedPosition, predicted) > 50
                    ? predicted : Vector3.MoveTowards(renderedPosition,
                        Vector3.Lerp(renderedPosition, predicted,
                            1 - Mathf.Exp(-(fastTravel ? 6f : 10f) * delta)),
                        (fastTravel ? 14f : 30f) * delta);
            }
            worldRoot.position = -renderedPosition;
            float warpTarget = snapshot?.WarpPhase == WarpPhase.Traveling ? 1f :
                snapshot?.WarpPhase == WarpPhase.Charging ? .28f :
                snapshot?.WarpPhase == WarpPhase.Recovering ? .36f : 0f;
            if (ship != null)
            {
                PwWarpArms arms = ship.GetComponentInChildren<PwWarpArms>();
                if (arms != null) arms.Tick(snapshot?.WarpPhase == WarpPhase.Charging ||
                    snapshot?.WarpPhase == WarpPhase.Traveling, delta);
                JeWarpRig jenquai = ship.GetComponentInChildren<JeWarpRig>();
                if (jenquai != null) jenquai.Tick(snapshot?.WarpPhase == WarpPhase.Charging ||
                    snapshot?.WarpPhase == WarpPhase.Traveling, delta);
            }
            skyWarp = Mathf.Lerp(skyWarp, warpTarget, 1 - Mathf.Exp(-4f * delta));
            art.Sky.SetFloat("_WarpStrength", skyWarp);
            art.Sky.SetVector("_Parallax", new Vector4(
                renderedPosition.x * .000012f,
                renderedPosition.z * .000008f, 0, 0));
            worldRoot.gameObject.SetActive(!docked);
            remoteShips.Tick(Time.unscaledDeltaTime); combatEffects.Tick();
            bool dockingBay = docked && (snapshot.Station.CurrentRoom == 0 ||
                snapshot.Station.CurrentRoom == uint.MaxValue);
            hangar.gameObject.SetActive(dockingBay);
            concourse.gameObject.SetActive(docked && !dockingBay);
            bool avatarVisible = docked && snapshot.Station.CurrentRoom != uint.MaxValue;
            ownStationAvatar.gameObject.SetActive(avatarVisible);
            Vector3 avatarPosition = new Vector3(stationWalk.X * .25f, 0, stationWalk.Z * .25f);
            if (avatarVisible)
            {
                ownStationAvatar.position = avatarPosition;
                ownStationAvatar.rotation = Quaternion.Euler(0, stationWalk.Orientation * Mathf.Rad2Deg, 0);
                ownStationAvatar.GetComponent<StationSuitMotion>()?.SetWalking(stationWalk.Translating);
            }
            if (docked) stationCharacters?.Tick(avatarPosition,
                snapshot.Dialogue?.NpcId ?? 0, delta);
            ship.gameObject.SetActive(!docked || dockingBay);
            environment.gameObject.SetActive(!chart && !docked);
            if (docked)
            {
                chaseHeadingReady = false;
                view.cullingMask = flightCameraMask;
                view.orthographic = false; view.clearFlags = CameraClearFlags.SolidColor;
                view.backgroundColor = new Color(.025f, .055f, .075f);
                if (dockingBay)
                {
                    ship.position = new Vector3(0, 1.4f, 0); ship.rotation = Quaternion.identity;
                    ship.localScale = Vector3.one * 1.3f;
                    view.transform.position = new Vector3(
                        Mathf.Clamp(avatarPosition.x + 4, -4, 6), 3.5f,
                        Mathf.Clamp(avatarPosition.z - 7, -9, 3));
                    view.transform.LookAt(avatarPosition + new Vector3(0, 1.2f, 1));
                }
                else
                {
                    view.transform.position = new Vector3(
                        Mathf.Clamp(avatarPosition.x * .35f, -3, 3), 3.8f,
                        Mathf.Clamp(avatarPosition.z - 7, -9, 3));
                    view.transform.LookAt(avatarPosition + new Vector3(0, 1.5f, 2));
                }
                return;
            }
            ship.localPosition = Vector3.zero;
            if (chart)
            {
                chaseHeadingReady = false;
                view.cullingMask = 0;
                view.orthographic = true; view.orthographicSize = mapZoom;
                view.clearFlags = CameraClearFlags.SolidColor; view.backgroundColor = new Color(.008f, .018f, .032f);
                view.transform.position = new Vector3(0, 5000, 0); view.transform.rotation = Quaternion.Euler(90, 0, 0);
                ship.localScale = Vector3.one * .55f;
            }
            else
            {
                view.cullingMask = flightCameraMask;
                view.orthographic = false; view.clearFlags = CameraClearFlags.Skybox; ship.localScale = Vector3.one * .55f;
                if (!chaseHeadingReady) { chaseHeading = ship.rotation; chaseHeadingReady = true; }
                else if (Time.unscaledTime >= chaseHoldUntil)
                    chaseHeading = Quaternion.Slerp(chaseHeading, ship.rotation,
                        1 - Mathf.Exp(-2.4f * delta));
                Quaternion orbit = chaseHeading * Quaternion.Euler(orbitPitch, orbitYaw, 0);
                view.transform.position = orbit * new Vector3(0, zoom * .24f, -zoom);
                view.transform.LookAt(ship.position + chaseHeading * Vector3.forward * .3f,
                    orbit * Vector3.up);
            }
            systemSun.rotation = view.transform.rotation;
        }

        /// <summary>Route the actual mouse click through world target selection.</summary>
        private void SelectAtPointer()
        { SelectAtPointer(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y)); }

        /// <summary>Select the closest visible marker across ships and world objects.</summary>
        public void SelectAtPointer(Vector2 pointer)
        {
            if (snapshot == null) return;
            float best = 58, bestDepth = float.PositiveInfinity;
            uint selected = 0; byte kind = 0;
            foreach (ShipActor actor in snapshot.Players.Concat(snapshot.Mobs).Where(item => item.HasPosition))
            {
                Vector3 point = ProjectPlayer(actor.Id); if (point.z <= 0) continue;
                float distance = Vector2.Distance(new Vector2(point.x, point.y), pointer);
                if (distance <= 58 && (distance < best - 2 ||
                    Mathf.Abs(distance - best) <= 2 && point.z < bestDepth))
                { best = distance; bestDepth = point.z; selected = actor.Id; kind = 1; }
            }
            foreach (MapObject item in snapshot.Objects.Where(WarpTargets.WorldObject))
            {
                Vector3 point = Project(item.Position); if (point.z <= 0) continue;
                float distance = Vector2.Distance(new Vector2(point.x, point.y), pointer);
                if (distance <= 58 && (distance < best - 2 ||
                    Mathf.Abs(distance - best) <= 2 && point.z < bestDepth))
                { best = distance; bestDepth = point.z; selected = item.Id; kind = item.Kind == 25 ? (byte)2 :
                    item.Kind == 38 ? (byte)3 : (byte)4; }
            }
            if (selected == 0)
            { hud?.SelectWorldLabelAtPointer(pointer); return; }
            if (kind == 1) hud.SelectContact(selected);
            else if (kind == 2) hud.SelectWreck(selected);
            else if (kind == 3) miningHud.SelectResource(selected);
            else Select(selected);
        }

        /// <returns>A projected position in GUI coordinates with signed camera depth.</returns>
        public Vector3 Project(Position position)
        { Vector3 point = view.WorldToScreenPoint(Convert(position) - renderedPosition); point.y = Screen.height - point.y; return point; }

        /// <returns>A remote ship's interpolated GUI position, or a negative depth when absent.</returns>
        public Vector3 ProjectPlayer(uint id)
        {
            if (!remoteShips.TryPosition(id, out Vector3 position)) return new Vector3(0, 0, -1);
            Vector3 point = view.WorldToScreenPoint(position);
            point.y = Screen.height - point.y;
            return point;
        }

        /// <summary>Select a server-listed destination or nearby space contact.</summary>
        public void Select(uint id)
        {
            if (snapshot == null || !snapshot.Objects.Any(item => item.Id == id && WarpTargets.WorldObject(item))) return;
            hud?.ClearSelection();
            navigationSelection.ChooseTarget(id);
            pendingNavigationTarget = 0;
            SelectWorldObject(id);
        }

        /// <summary>Select a visible navigation, resource, or wreck object for server verbs.</summary>
        public void SelectWorldObject(uint id)
        {
            if (snapshot == null || !snapshot.Objects.Any(item => item.Id == id && WarpTargets.WorldObject(item))) return;
            navigationSelection.ChooseTarget(id);
            pendingNavigationTarget = 0;
            if (Ready && snapshot.Station == null)
                worker.Submit(new ClientCommand(CommandKind.SelectTarget, id));
        }

        /// <summary>Clear the destination so the next warp travels ahead.</summary>
        public void SelectFreeFlight()
        { if (snapshot != null && snapshot.Station == null)
            { navigationSelection.ChooseFreeFlight(); pendingNavigationTarget = 0; } }

        /// <summary>Warp to the selected target, fly ahead, or cancel active warp.</summary>
        public void WarpSelected()
        {
            if (FollowingFormation)
            { status = "Leader controls formation warp. Leave formation to warp alone."; return; }
            uint target = navigationSelection.TargetId;
            if (snapshot != null && target != 0 && snapshot.Players.Any(actor => actor.Id == target))
            { status = "Direct warp to pilots is unavailable. Use Follow instead."; return; }
            if (Ready && snapshot.Station == null && snapshot.WarpPhase != WarpPhase.Recovering)
                worker.Submit(new ClientCommand(CommandKind.Warp, target: target));
        }

        /// <summary>
        /// Returns:
        ///     bool: Whether this ship is formed under the leader's travel control.
        /// </summary>
        private bool FollowingFormation => snapshot?.Group.Joined == true &&
            !snapshot.Group.IsLeader && snapshot.Group.Formed;

        /// <summary>Request travel through the selected gate.</summary>
        public void GateSelected()
        { if (Ready && Target?.Kind == 11 && snapshot.Verbs.Available(Target.Id, 10) == true)
                worker.Submit(new ClientCommand(CommandKind.Gate, Target.Id)); }

        /// <summary>Request docking at the selected nearby station.</summary>
        public void DockSelected()
        { if (Ready && Target?.Kind == 12 && snapshot.Verbs.Available(Target.Id, 8) == true)
                worker.Submit(new ClientCommand(CommandKind.Dock, Target.Id)); }

        /// <summary>Leave the station after its server-owned launch transition.</summary>
        public void Launch()
        { if (Ready && snapshot.Station != null) worker.Submit(new ClientCommand(CommandKind.Launch)); }

        /// <summary>Ask the server to open a contact placed in this station.</summary>
        public void OpenNpc(uint id)
        { if (Ready && snapshot.Station != null && snapshot.Station.Contacts.Any(contact =>
                contact.Id == id))
                worker.Submit(new ClientCommand(CommandKind.OpenNpc, id)); }

        /// <summary>Move the station avatar to one room offered by the server layout.</summary>
        public void EnterStationRoom(uint room)
        {
            if (Ready && snapshot.Station != null &&
                (snapshot.Dialogue == null || snapshot.Station.RoomList.Count == 1) &&
                snapshot.Station.RoomList.Any(entry => entry.Index == room) &&
                snapshot.Station.CurrentRoom != room)
                worker.Submit(new ClientCommand(CommandKind.EnterStationRoom, room));
        }

        /// <summary>Send one server-offered dialogue branch or close command.</summary>
        public void ChooseNpc(byte node)
        { if (Ready && snapshot.Dialogue != null)
                worker.Submit(new ClientCommand(CommandKind.ChooseNpc, node)); }

        /// <summary>Buy one item from a server-listed station vendor slot.</summary>
        public void BuyVendor(int slot)
        { if (Ready && snapshot.Dialogue?.VendorOpen == true)
                worker.Submit(new ClientCommand(CommandKind.BuyVendor, slot: slot)); }

        /// <summary>Sell one item from a cargo slot through the active vendor.</summary>
        public void SellVendor(int slot)
        { if (Ready && snapshot.Dialogue?.VendorOpen == true)
                worker.Submit(new ClientCommand(CommandKind.SellVendor, slot: slot)); }

        /// <summary>Install a cargo item in the selected ship hardpoint.</summary>
        public void EquipCargo(int cargoSlot, int mountSlot)
        {
            if (Ready && cargoSlot >= 0 && cargoSlot < snapshot.Cargo.Length &&
                mountSlot >= 0 && mountSlot < snapshot.Mounted.Length)
                worker.Submit(new ClientCommand(CommandKind.EquipCargo, (uint)cargoSlot, slot: mountSlot));
        }

        /// <summary>Remove an installed item into an empty cargo slot.</summary>
        public void UnequipMount(int mountSlot)
        {
            if (Ready && mountSlot >= 0 && mountSlot < snapshot.Mounted.Length)
                worker.Submit(new ClientCommand(CommandKind.UnequipMount, (uint)mountSlot));
        }

        /// <summary>Send an owned inventory drag to the worker for server validation.</summary>
        public void MoveInventory(InventoryMove move)
        {
            if (Ready && move.SourceSlot >= 0 && move.DestinationSlot >= 0)
                worker.Submit(new ClientCommand(CommandKind.MoveInventory,
                    inventoryMove: move));
        }

        /// <summary>Store one cargo stack in the server-owned station vault.</summary>
        public void StoreCargo(int slot)
        {
            if (Ready && snapshot.Station != null && slot >= 0 && slot < snapshot.Cargo.Length &&
                snapshot.Cargo[slot] != null)
                worker.Submit(new ClientCommand(CommandKind.StoreCargo, (uint)slot));
        }

        /// <summary>Retrieve one vault stack into available cargo space.</summary>
        public void RetrieveVault(int slot)
        {
            if (Ready && snapshot.Station != null && slot >= 0 && slot < snapshot.Vault.Length &&
                snapshot.Vault[slot] != null)
                worker.Submit(new ClientCommand(CommandKind.RetrieveVault, (uint)slot));
        }

        /// <summary>
        /// Returns:
        ///     bool: Whether a station workbench is selected in the concourse directory.
        /// </summary>
        public bool StationServiceOpen => productionHud?.IsOpen == true;

        /// <summary>
        /// Returns:
        ///     uint: The selected manufacturing, analyze, dismantle, or refining mode.
        /// </summary>
        public uint StationServiceMode => productionHud?.SelectedMode ?? 0;

        /// <summary>
        /// Returns:
        ///     bool: Whether the component trading overlay currently owns input.
        /// </summary>
        public bool ComponentMarketOpen => componentMarketHud?.IsOpen == true;

        /// <summary>Show suppliers and buyers for an owned item or manufacturing requirement.</summary>
        public void OpenComponentMarket(ItemView item = null)
        {
            if (!Ready) return;
            chatOpen = false; hud.ReleaseInputFocus();
            galaxyTravelHud.Close(); componentMarketHud.Open(item);
        }

        /// <summary>Returns:
        ///     bool: Whether the galaxy destination browser owns foreground input.
        /// </summary>
        public bool GalaxyTravelOpen => galaxyTravelHud?.IsOpen == true;

        /// <summary>Open a galaxy-wide directory of gates and stations.</summary>
        public void OpenGalaxyTravel()
        {
            if (!Ready) return;
            chatOpen = false; hud.ReleaseInputFocus(); hud.EndChartInteraction();
            componentMarketHud.Close(); galaxyTravelHud.Open();
        }

        /// <summary>Search the client's bundled known gate and station map.</summary>
        public void QueryGalaxyDestinations(string query, uint page, uint flags)
        {
            if (Ready) worker.Submit(new ClientCommand(CommandKind.QueryGalaxyDestinations, page, text: query, slot: (int)flags));
        }

        /// <summary>Plan a local itinerary using known gates and station locations.</summary>
        public void PreviewGalaxyRoute(uint sector, uint objectId)
        {
            if (Ready) worker.Submit(new ClientCommand(CommandKind.PreviewGalaxyRoute, sector, stationRoom: objectId));
        }

        /// <summary>Begin following the locally previewed route with normal flight commands.</summary>
        public void StartGalaxyTravel()
        {
            if (!Ready) return;
            previousYaw = previousPitch = 0; flightThrottle.Reset();
            worker.Submit(new ClientCommand(CommandKind.StartGalaxyTravel));
        }

        /// <summary>Stop scheduling journey legs and brake or cancel warp when possible.</summary>
        public void CancelGalaxyTravel()
        { if (worker != null) worker.Submit(new ClientCommand(CommandKind.CancelGalaxyTravel)); }

        /// <summary>Load the current station's purchasable components into the market browser.</summary>
        public void QueryComponentStock()
        {
            if (Ready) worker.Submit(new ClientCommand(CommandKind.QueryComponentStock));
        }

        /// <summary>Request authoritative component vendor locations and negotiated quotes.</summary>
        public void QueryComponentMarket(uint template)
        {
            if (Ready && template != 0)
                worker.Submit(new ClientCommand(CommandKind.QueryComponentMarket, template));
        }

        /// <summary>Buy a component quantity from a quoted vendor in the current station.</summary>
        public void BuyMarketComponent(uint npc, uint template, uint quantity)
        {
            if (Ready && snapshot.Station != null && quantity > 0)
                worker.Submit(new ClientCommand(CommandKind.TradeComponentMarket, npc,
                    marketTemplate: template, marketQuantity: quantity));
        }

        /// <summary>Sell part or all of an owned cargo stack to a quoted local buyer.</summary>
        public void SellMarketComponent(uint npc, int cargoSlot, uint quantity)
        {
            if (Ready && snapshot.Station != null && cargoSlot >= 0 && cargoSlot < snapshot.Cargo.Length &&
                snapshot.Cargo[cargoSlot] != null && quantity > 0)
                worker.Submit(new ClientCommand(CommandKind.TradeComponentMarket, npc, slot: cargoSlot,
                    marketTemplate: snapshot.Cargo[cargoSlot].Template, marketQuantity: quantity, marketSell: true));
        }

        /// <summary>Request the developer transport offered by a component supplier quote.</summary>
        public void TravelToMarketStation(uint station)
        {
            if (Ready && station != 0)
                worker.Submit(new ClientCommand(CommandKind.TravelComponentMarket, station));
        }

        /// <summary>Open a workbench from the shared station directory.</summary>
        public void ShowStationService(uint mode)
        {
            if (!Ready || snapshot.Station == null || mode < 1 || mode > 4) return;
            if (snapshot.Dialogue != null) ChooseNpc(230);
            productionHud.Open(mode);
        }

        /// <summary>Dismiss the selected workbench before talking to a station contact.</summary>
        public void HideStationService()
        {
            productionHud.Close();
            if (snapshot?.Production.Open == true) CloseProduction();
        }

        /// <summary>Open one of the server's four station production modes.</summary>
        public void OpenProduction(uint mode)
        {
            if (Ready && snapshot.Station != null && mode >= 1 && mode <= 4)
                worker.Submit(new ClientCommand(CommandKind.OpenProduction, mode));
        }

        /// <summary>Close the production terminal and cancel its pending job.</summary>
        public void CloseProduction()
        { if (Ready && snapshot.Station != null) worker.Submit(new ClientCommand(CommandKind.CloseProduction)); }

        /// <summary>Select a formula from the server's current workbench list.</summary>
        public void SelectProductionRecipe(uint template)
        { if (Ready && snapshot.Station != null)
                worker.Submit(new ClientCommand(CommandKind.SelectProductionRecipe, template)); }

        /// <summary>Filter server-owned production formulas by one native category.</summary>
        public void SelectProductionCategory(uint category)
        { if (Ready && snapshot.Station != null)
                worker.Submit(new ClientCommand(CommandKind.SelectProductionCategory, category)); }

        /// <summary>Show another server-owned recipe page in the station workbench.</summary>
        public void SelectProductionPage(uint page)
        { if (Ready && snapshot.Station != null) worker.Submit(new ClientCommand(CommandKind.SelectProductionPage, page)); }

        /// <summary>Enable or disable one server-side recipe level mask.</summary>
        public void SetProductionLevels(uint mask, bool enabled)
        { if (Ready && snapshot.Station != null)
                worker.Submit(new ClientCommand(CommandKind.SetProductionLevels, mask, slot: enabled ? 1 : 0)); }

        /// <summary>Start one production job or the refinery's available stack.</summary>
        public void StartProduction(bool stack)
        { if (Ready && snapshot.Station != null)
                worker.Submit(new ClientCommand(CommandKind.StartProduction, stack ? 1u : 0u)); }

        /// <summary>Move one owned cargo item into the station analyzer.</summary>
        public void InsertAnalysisSample(int cargoSlot)
        { if (Ready && snapshot.Station != null && cargoSlot >= 0 && cargoSlot < snapshot.Cargo.Length)
                worker.Submit(new ClientCommand(CommandKind.InsertAnalysisSample, (uint)cargoSlot)); }

        /// <summary>Return the analyzer's sample to an empty cargo slot.</summary>
        public void ReturnAnalysisSample()
        { if (Ready && snapshot.Station != null)
                worker.Submit(new ClientCommand(CommandKind.ReturnAnalysisSample)); }

        /// <summary>Toggle one mounted weapon in space while preserving the other hardpoints.</summary>
        public void ActivateWeapon(int slot)
        {
            if (Ready && !Incapacitated && snapshot.Station == null && slot >= 3 &&
                slot < 3 + snapshot.WeaponMounts && slot < snapshot.Mounted.Length &&
                snapshot.Mounted[slot]?.Type >= 14 && snapshot.Mounted[slot]?.Type <= 16)
                worker.Submit(new ClientCommand(CommandKind.ActivateWeapon, (uint)slot));
        }

        /// <summary>Activate a server-supported effect from an installed core or device mount.</summary>
        public void ActivateDevice(int slot)
        {
            ItemView item = snapshot != null && slot >= 0 && slot < snapshot.Mounted.Length ?
                snapshot.Mounted[slot] : null;
            if (Ready && !Incapacitated && snapshot.Station == null &&
                snapshot.WarpPhase != WarpPhase.Charging && snapshot.WarpPhase != WarpPhase.Traveling &&
                item != null && DeviceEffects.MountedSlot(item.ActivationLabel, item.Type, slot,
                    snapshot.DeviceMounts))
                worker.Submit(new ClientCommand(CommandKind.ActivateDevice, (uint)slot));
        }

        /// <summary>Queue a device cast for self or a visible crew member after native target acknowledgment.</summary>
        public void ActivateDeviceFor(int slot, uint recipient)
        {
            ItemView item = snapshot != null && slot >= 0 && slot < snapshot.Mounted.Length ?
                snapshot.Mounted[slot] : null;
            bool requiresPeer = item != null && DeviceEffects.RequiresPeer(item.ActivationLabel);
            bool validRecipient = snapshot != null && (recipient == snapshot.PlayerId ?
                !requiresPeer : (item?.TargetsPeer == true || requiresPeer) &&
                snapshot.Players.Any(actor => actor.Id == recipient && actor.HasPosition &&
                    (requiresPeer || snapshot.Group.Members.Any(member => member.Id == recipient))));
            if (!Ready || Incapacitated || snapshot.Station != null || item == null ||
                snapshot.WarpPhase != WarpPhase.Idle ||
                !DeviceEffects.MountedSlot(item.ActivationLabel, item.Type, slot,
                    snapshot.DeviceMounts) || !validRecipient)
                return;
            worker.Submit(new ClientCommand(CommandKind.ActivateDeviceFor, recipient, slot: slot));
        }

        /// <summary>Request activation of the equipped Turbo Weapons device.</summary>
        public void ActivateTurboWeapons()
        {
            if (Ready && !Incapacitated && snapshot.Station == null &&
                snapshot.WarpPhase != WarpPhase.Charging && snapshot.WarpPhase != WarpPhase.Traveling &&
                snapshot.Mounted.Skip(9).Take((int)snapshot.DeviceMounts).Any(item =>
                    item?.Template == 2363 || item?.Template == 2412 || item?.Template == 2265))
                worker.Submit(new ClientCommand(CommandKind.ActivateTurboWeapons));
        }

        /// <summary>Request a server-authoritative formation change or follow action.</summary>
        public void Formation(uint action)
        { if (Ready) worker.Submit(new ClientCommand(CommandKind.Formation, action)); }

        /// <summary>Open a visible wreck through the server's loot protocol.</summary>
        public void OpenLoot(uint id)
        {
            if (Ready && snapshot.Verbs.Available(id, 3) == true &&
                snapshot.Objects.Any(item => item.Id == id && item.Kind == 25))
                worker.Submit(new ClientCommand(CommandKind.OpenLoot, id));
        }

        /// <summary>Close the current wreck window through its native server action.</summary>
        public void CloseLoot(uint id)
        {
            if (lootSweep.CorpseId == id) lootSweep.Cancel("Collect All stopped.");
            if (Ready && snapshot.OpenLootId == id)
                worker.Submit(new ClientCommand(CommandKind.CloseLoot, id));
        }

        /// <summary>Begin one-at-a-time wreck pickup after each native stock update.</summary>
        public void CollectAllLoot(uint id)
        {
            if (!Ready) return;
            CorpseLoot contents = snapshot.Loot.FirstOrDefault(item => item.Id == id);
            int? slot = lootSweep.Begin(id, snapshot.OpenLootId, snapshot.SelectedTargetId,
                contents, snapshot.LootActivity, System.Diagnostics.Stopwatch.GetTimestamp());
            if (slot.HasValue) TakeLoot(id, slot.Value);
        }

        /// <summary>Stop future wreck requests while any current tractor finishes normally.</summary>
        public void StopCollectAllLoot()
        { lootSweep.Cancel("Collect All stopped after the current tractor."); }

        /// <summary>Tractor one server-owned wreck slot into cargo.</summary>
        public void TakeLoot(uint id, int slot)
        {
            if (Ready && snapshot.OpenLootId == id)
                worker.Submit(new ClientCommand(CommandKind.TakeLoot, id, slot: slot));
        }

        /// <summary>Open a visible asteroid's native prospecting window.</summary>
        public void OpenResource(uint id)
        {
            if (Ready && snapshot.Verbs.Available(id, 9) == true &&
                snapshot.Objects.Any(item => item.Id == id && item.Kind == 38))
                worker.Submit(new ClientCommand(CommandKind.OpenResource, id));
        }

        /// <summary>Close the current prospecting window.</summary>
        public void CloseResource()
        { if (Ready && snapshot.ResourceWindowOpen) worker.Submit(new ClientCommand(CommandKind.CloseResource)); }

        /// <summary>Prospect one item slot advertised by the selected asteroid.</summary>
        public void ProspectResource(int slot)
        {
            if (Ready && snapshot.ResourceWindowOpen && snapshot.Resources.Any(resource =>
                resource.Id == snapshot.ResourceTargetId && slot >= 0 && slot < resource.Items.Length &&
                resource.Items[slot] != null))
                worker.Submit(new ClientCommand(CommandKind.ProspectResource, slot: slot));
        }

        /// <summary>Use a class ability advertised by the connected server.</summary>
        public void ActivateAbility(uint id)
        {
            if (Ready && snapshot.Station == null && snapshot.AvailableAbilities.Contains(id))
                worker.Submit(new ClientCommand(CommandKind.ActivateAbility, id));
        }

        /// <summary>Queue a directed support ability for a visible pilot or self.</summary>
        public void ActivateAbilityFor(uint id, uint recipient)
        {
            if (!Ready || snapshot.Station != null || !AbilityTargets.Directed(id) ||
                !snapshot.AvailableAbilities.Contains(id) ||
                recipient != snapshot.PlayerId && !snapshot.Players.Any(actor =>
                    actor.Id == recipient && actor.HasPosition)) return;
            worker.Submit(new ClientCommand(CommandKind.ActivateAbilityFor, recipient, slot: (int)id));
        }

        /// <summary>Ask the server to spend earned points on one advertised skill rank.</summary>
        public void TrainSkill(uint id)
        {
            if (!Ready || snapshot.Experience == null || snapshot.Experience.AvailableSkillPoints == 0 ||
                !snapshot.Skills.Any(skill => skill.Id == id && skill.CanTrain &&
                    skill.Cost <= snapshot.Experience.AvailableSkillPoints)) return;
            worker.Submit(new ClientCommand(CommandKind.TrainSkill, id));
        }

        /// <summary>Ask the server for choices after the ship is incapacitated.</summary>
        public void RequestRescue()
        { if (Ready && Incapacitated) worker.Submit(new ClientCommand(CommandKind.RequestRescue)); }

        /// <summary>Send one rescue choice listed by the server.</summary>
        public void ChooseRescue(byte node)
        {
            if (Ready && Incapacitated && snapshot.Dialogue != null &&
                snapshot.Dialogue.Choices.Any(choice => choice.Node == node))
                worker.Submit(new ClientCommand(CommandKind.ChooseRescue, node));
        }

        /// <summary>Use the character's Jumpstart ability on its own incapacitated ship.</summary>
        public void SelfJumpstart()
        {
            if (Ready && Incapacitated && snapshot.AvailableAbilities.Contains(65u))
                worker.Submit(new ClientCommand(CommandKind.SelfJumpstart));
        }

        /// <summary>Answer the server's pending native group or Jumpstart confirmation.</summary>
        public void AnswerConfirmation(bool accept)
        {
            if (Ready && snapshot.Confirmation != null)
                worker.Submit(new ClientCommand(CommandKind.AnswerConfirmation, accept ? 1u : 0u));
        }

        /// <summary>Set the authoritative target of a visible contact.</summary>
        public void SelectContact(uint id)
        {
            if (Ready && snapshot.Players.Concat(snapshot.Mobs).Any(actor => actor.Id == id))
            {
                navigationSelection.ChooseTarget(id);
                pendingNavigationTarget = 0;
                worker.Submit(new ClientCommand(CommandKind.SelectTarget, id));
            }
        }

        /// <summary>Select the own ship for a self-directed class support ability.</summary>
        public void SelectAbilitySelf()
        {
            if (Ready && snapshot.Station == null)
                worker.Submit(new ClientCommand(CommandKind.SelectTarget, snapshot.PlayerId));
        }

        /// <summary>Follow the currently selected visible ship through native flight control.</summary>
        public void FollowSelected()
        {
            uint id = hud.SelectedContactId != 0 ? hud.SelectedContactId :
                navigationSelection.TargetId != 0 ? navigationSelection.TargetId :
                snapshot?.SelectedTargetId ?? 0;
            Follow(id);
        }

        /// <summary>Ask the server to follow a visible ship or world object.</summary>
        public void Follow(uint id)
        {
            if (!Ready || snapshot.Station != null) return;
            bool shipTarget = snapshot.Players.Concat(snapshot.Mobs)
                .Any(actor => actor.Id == id && actor.HasPosition);
            bool objectTarget = snapshot.Objects.Any(item => item.Id == id &&
                WarpTargets.WorldObject(item));
            if (!shipTarget && !objectTarget) return;
            visualFollowTarget = id;
            visualFollowUntil = Time.unscaledTime + 1.2f;
            chaseHoldUntil = Time.unscaledTime + .65f;
            navigationSelection.ChooseTarget(id);
            pendingNavigationTarget = 0;
            worker.Submit(new ClientCommand(CommandKind.FollowTarget, id));
        }

        /// <summary>Invite one currently visible player by server identity.</summary>
        public void InvitePlayer(uint id)
        {
            if (Ready && snapshot.Players.Any(actor => actor.Id == id))
                worker.Submit(new ClientCommand(CommandKind.InvitePlayer, id));
        }

        /// <summary>Cycle through server-visible hostile ships in distance order.</summary>
        private void TargetNextHostile()
        {
            ShipActor[] hostiles = snapshot.Mobs.Where(actor => actor.Hostile && actor.HasPosition)
                .OrderBy(actor => actor.Position.Distance(snapshot.Position)).ToArray();
            if (hostiles.Length == 0) { status = "No hostile ships in sensor range"; return; }
            int current = System.Array.FindIndex(hostiles, actor => actor.Id == hud.SelectedContactId);
            hud.SelectContact(hostiles[(current + 1) % hostiles.Length].Id);
        }

        /// <summary>Select the next visible MOB confirmed by the server as attacking this pilot.</summary>
        public void TargetNextAttacker()
        {
            if (!Ready || snapshot.Station != null) return;
            uint next = ThreatSelection.Next(snapshot, hud.SelectedContactId);
            if (next == 0) { status = "No confirmed attackers in sensor range"; return; }
            hud.SelectContact(next);
        }

        /// <summary>Stop weapons without requiring a surviving target.</summary>
        public void StopWeapons()
        { if (Ready && snapshot.WeaponsFiring) worker.Submit(new ClientCommand(CommandKind.StopWeapons)); }

        /// <summary>Request server-selected combat against one visible MOB.</summary>
        public void ToggleWeapons(uint id)
        {
            if (Ready && snapshot.Mobs.Any(mob => mob.Id == id && mob.HasPosition))
                worker.Submit(new ClientCommand(CommandKind.ToggleWeapons, id));
        }

        /// <summary>Submit one explicit throttle or brake command.</summary>
        public void Thrust(byte command) { if (Ready && snapshot.Station == null) worker.Submit(new ClientCommand(CommandKind.Thrust, command)); }

        /// <summary>Send one chat line or local developer command to the server.</summary>
        public void SendChat(string text, bool group = false)
        {
            if (!Ready) return;
            hud.ChatHistory.Remember(text);
            worker.Submit(new ClientCommand(CommandKind.Chat, target: group ? 1u : 4u, text: text));
        }

        /// <summary>Whether the local chat entry is open.</summary>
        public bool ChatOpen => chatOpen;
        public int ChatOpenedFrame => chatOpenedFrame;
        /// <summary>Open the visible chat composer from Enter or its Comms button.</summary>
        public void OpenChatEntry()
        {
            if (!Ready) return;
            if (!chatOpen) hud.ChatHistory.Begin();
            chatOpen = true; chatOpenedFrame = Time.frameCount;
        }
        public void CloseChatEntry() { chatOpen = false; }

        /// <summary>Switch between the flight camera and sector chart.</summary>
        public void SetChart(bool enabled)
        { chart = enabled; if (!enabled) hud?.EndChartInteraction(); }

        /// <summary>Draw the HUD on the main thread.</summary>
        private void OnGUI()
        {
            long guiStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            Matrix4x4 previous = GUI.matrix;
            bool previousEnabled = GUI.enabled;
            float scale = hud.Scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            try
            {
                bool groupInvite = hud.GroupInviteOpen;
                if (GalaxyTravelOpen && !groupInvite)
                { galaxyTravelHud.Draw(snapshot); return; }
                if (ComponentMarketOpen && !groupInvite)
                {
                    componentMarketHud.Draw(snapshot);
                    return;
                }
                if (groupInvite && !hud.RetainedVisible) hud.HandleGroupInviteInput(Event.current);
                if (groupInvite || ComponentMarketOpen) GUI.enabled = false;
                hud.Draw(ref host, ref username, ref password);
                if (!groupInvite && !GalaxyTravelOpen && !ComponentMarketOpen) DrawGalaxyTravelProgress();
                if (!hud.SettingsOpen && !hud.EquipmentOpen && !AbilitiesOpen && !Incapacitated)
                    defenseHud.Draw(snapshot);
                else defenseHud.Close();
                if (!hud.SettingsOpen && (!hud.EquipmentOpen || snapshot?.Station != null))
                {
                    if (productionHud.IsOpen) vaultHud.Close();
                    vaultHud.Draw(snapshot);
                    if (vaultHud.IsOpen && productionHud.IsOpen)
                    { productionHud.Close(); CloseProduction(); }
                    productionHud.Draw(snapshot);
                }
                if (!hud.SettingsOpen && !hud.EquipmentOpen && !AbilitiesOpen && !Incapacitated) miningHud.Draw(snapshot);
                if (!hud.SettingsOpen && (!hud.EquipmentOpen || snapshot?.Station != null) && !Incapacitated) abilityHud.Draw(snapshot);
                if (!hud.SettingsOpen && !hud.EquipmentOpen && !AbilitiesOpen &&
                    !productionHud.IsOpen && !vaultHud.IsOpen) missionHud.Draw(snapshot);
                else missionHud.Close();
                if (!hud.SettingsOpen && Incapacitated) recoveryHud.Draw(snapshot);
                GUI.enabled = previousEnabled && !groupInvite;
                componentMarketHud.Draw(snapshot);
                GUI.enabled = previousEnabled;
                if (groupInvite && !hud.RetainedVisible) hud.DrawGroupInvitePopup(snapshot);
            }
            finally
            {
                GUI.enabled = previousEnabled; GUI.matrix = previous;
                guiTicks += System.Diagnostics.Stopwatch.GetTimestamp() - guiStartedAt;
                guiPasses++;
            }
        }

        /// <summary>Keep active journey status and cancellation available with the browser closed.</summary>
        private void DrawGalaxyTravelProgress()
        {
            GalaxyTravelView travel = snapshot?.GalaxyTravel;
            if (travel?.Active != true) return;
            float width = Screen.width / hud.Scale;
            Rect strip = new Rect(365, 71, Mathf.Max(350, width - 730), 28);
            hud.Panel(strip);
            GUI.Label(new Rect(strip.x + 8, strip.y + 4, strip.width - 161, 22), travel.Status, hud.SmallStyle);
            if (GUI.Button(new Rect(strip.xMax - 148, strip.y + 2, 78, 24), "Route / " + bindings.Display(ControlAction.Galaxy), hud.ButtonStyle)) OpenGalaxyTravel();
            if (GUI.Button(new Rect(strip.xMax - 66, strip.y + 2, 62, 24), "Cancel", hud.ButtonStyle)) CancelGalaxyTravel();
        }

        /// <summary>Join the worker before removing the connected world's resources.</summary>
        public void Disconnect()
        {
            worker?.Dispose(); worker = null; snapshot = null; sector = ""; navigationSelection.Reset(); pendingNavigationTarget = 0; positioned = false;
            authoritativePoseAt = lastSnapshotAt = 0;
            visualFollowTarget = 0;
            chaseHeadingReady = false;
            chaseHoldUntil = 0;
            skyWarp = 0;
            art.Sky.SetFloat("_WarpStrength", 0);
            picker?.Dispose(); picker = null; characters = null;
            selectedShipClass = -1; selectedHullStage = uint.MaxValue;
            defenseHud?.Close();
            componentMarketHud?.Close();
            galaxyTravelHud?.Close();
            previousYaw = previousPitch = 0; flightThrottle.Reset(); ClearWorld(); status = "Disconnected";
        }

        /// <summary>Release scene instances belonging to the departed sector.</summary>
        private void ClearWorld()
        {
            lootSweep.Cancel("");
            navigationSelection.Reset();
            pendingNavigationTarget = 0;
            hud?.ResetTargetSelection();
            stationWalk.EnterRoom(uint.MaxValue); nextStationPoseAt = 0;
            combatEffects?.Clear(); remoteShips?.Clear(); miningHud?.Clear();
            stationCharacters?.Clear();
            foreach (Transform body in bodies.Values) Destroy(body.gameObject);
            foreach (Transform body in stationBodies.Values) Destroy(body.gameObject);
            bodies.Clear(); stationBodies.Clear(); renderedObjects = System.Array.Empty<MapObject>();
        }

        /// <summary>Release held steering on focus loss while preserving authoritative travel.</summary>
        private void OnApplicationFocus(bool focused)
        {
            Application.targetFrameRate = focused ? ForegroundFrameRate : BackgroundFrameRate;
            if (!focused) flightThrottle.Reset();
            if (!focused) StopStationWalk();
            if (focused || worker == null || snapshot?.Station != null) return;
            bool warping = snapshot?.WarpPhase == WarpPhase.Charging ||
                snapshot?.WarpPhase == WarpPhase.Traveling;
            bool formationTravel = snapshot?.Group.Joined == true &&
                snapshot.Group.Formation.Length > 0;
            if (!warping && !formationTravel)
                worker.Submit(new ClientCommand(CommandKind.Thrust, 4));
            if (!FollowingFormation && !warping &&
                (previousYaw != 0 || previousPitch != 0))
                worker.Submit(new ClientCommand(CommandKind.Steer));
            previousYaw = previousPitch = 0;
        }

        /// <summary>Stop networking before releasing shared rendering assets.</summary>
        private void OnDestroy() { worker?.Dispose(); worker = null; picker?.Dispose(); picker = null; demoRoster?.Dispose(); demoRoster = null; combatEffects?.Dispose(); art?.Dispose(); hud?.Dispose(); }
    }
}
