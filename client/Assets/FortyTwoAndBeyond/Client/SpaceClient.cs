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
        private RecoveryHud recoveryHud;
        private VaultHud vaultHud;
        private ProductionHud productionHud;
        private MissionHud missionHud;
        private TurboHud turboHud;
        private readonly FlightThrottle flightThrottle = new FlightThrottle();
        private readonly StationWalkState stationWalk = new StationWalkState();
        public bool AbilitiesOpen => abilityHud != null && abilityHud.IsOpen;
        public bool Incapacitated => snapshot != null && snapshot.Station == null &&
            snapshot.MaxHull > 0 && snapshot.Hull <= 0;
        private Transform ship, worldRoot, environment, hangar, concourse, ownStationAvatar;
        private readonly Dictionary<uint, Transform> bodies = new Dictionary<uint, Transform>();
        private readonly Dictionary<uint, Transform> stationBodies = new Dictionary<uint, Transform>();
        private string host = "192.168.47.1", username = "", password = "", status = "Ready to connect", sector = "";
        private readonly NavigationSelection navigationSelection = new NavigationSelection();
        private uint pendingNavigationTarget;
        private bool chart, positioned;
        private bool chatOpen;
        private int chatOpenedFrame = -1;
        private int lastFrameRate = 120;
        private int sampledFrames, displayFps;
        private float sampledSeconds, slowestFrameMs, displayFrameMs, nextPerformanceLog, lastSnapshotAt;
        private float previousYaw, previousPitch, orbitYaw = 22, orbitPitch = 8, zoom = 2.8f, mapZoom = 1400;
        private Vector3 renderedPosition;
        private float authoritativePoseAt;
        private float nextStationPoseAt;
        public Snapshot CurrentSnapshot => snapshot;
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
        { if (FindFirstObjectByType<SpaceClient>() == null) new GameObject("42 and Beyond").AddComponent<SpaceClient>(); }

        /// <summary>Create lighting, reusable models, the camera and the flight HUD.</summary>
        private void Awake()
        {
            bindings = new KeyBindings();
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 120;
            art = new SpaceArt(); hud = new FlightHud(this); miningHud = new MiningHud(this, hud);
            abilityHud = new AbilityHud(this, hud);
            recoveryHud = new RecoveryHud(this, hud);
            vaultHud = new VaultHud(this, hud);
            productionHud = new ProductionHud(this, hud);
            missionHud = new MissionHud(this, hud);
            turboHud = new TurboHud(this, hud);
            worldRoot = new GameObject("Server world").transform; worldRoot.SetParent(transform, false);
            remoteShips = new RemoteShips(art, worldRoot);
            ship = art.Ship(transform); combatEffects = new CombatEffects(transform, ship, remoteShips); ship.localScale = Vector3.one * .25f;
            environment = art.Environment(transform);
            hangar = art.Hangar(transform); hangar.gameObject.SetActive(false);
            concourse = art.StationConcourse(transform); concourse.gameObject.SetActive(false);
            ownStationAvatar = art.StationAvatar(transform);
            ownStationAvatar.name = "Your station avatar";
            ownStationAvatar.gameObject.SetActive(false);
            RenderSettings.skybox = art.Sky; RenderSettings.ambientLight = new Color(.18f, .23f, .32f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            var sun = new GameObject("Sunlight", typeof(Light)); sun.transform.SetParent(transform, false);
            sun.transform.rotation = Quaternion.Euler(30, -35, 0);
            Light light = sun.GetComponent<Light>(); light.type = LightType.Directional; light.color = new Color(1, .83f, .65f); light.intensity = 1.7f;
            light.shadows = LightShadows.Soft; QualitySettings.shadowDistance = 150; QualitySettings.antiAliasing = 4;
            view = Camera.main;
            if (view == null) view = new GameObject("Flight camera", typeof(Camera)).GetComponent<Camera>();
            view.clearFlags = CameraClearFlags.Skybox; view.nearClipPlane = .015f; view.farClipPlane = 35000; view.fieldOfView = 60;
            view.transform.position = new Vector3(0, 1, -zoom); view.transform.LookAt(Vector3.zero);
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
            picker.Dispose(); picker = null;
            worker = new ClientWorker(host, username, password, slot); password = "";
            status = "Entering " + name + "…";
        }

        /// <summary>Return to the account form after a failed lookup or session.</summary>
        public void ReturnToLogin() { Disconnect(); }

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
            int frameRate = !Application.isFocused ? 60 : 120;
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
                bool poseChanged = next.HasPosition && (snapshot == null || !snapshot.HasPosition ||
                    next.Position.Distance(snapshot.Position) > .001f || next.Speed != snapshot.Speed ||
                    next.AngularY != snapshot.AngularY || next.AngularZ != snapshot.AngularZ ||
                    !next.Orientation.SequenceEqual(snapshot.Orientation));
                snapshot = next; if (worker.Error == null) status = next.Status;
                lastSnapshotAt = Time.unscaledTime;
                if (sector != next.Sector) { ClearWorld(); sector = next.Sector; positioned = false; }
                hud?.ObserveAuthoritativeTarget(next);
                ApplyWorld(next);
                RefreshNavigationSelection(next);
                if (Ready && pendingNavigationTarget != 0 &&
                    navigationSelection.TargetId == pendingNavigationTarget)
                { SelectWorldObject(pendingNavigationTarget); pendingNavigationTarget = 0; }
                ApplyStationAvatars(next);
                remoteShips.Apply(next.Players.Concat(next.Mobs).ToArray(), next.Group, next.SelectedTargetId);
                combatEffects.Apply(next);
                if (poseChanged) authoritativePoseAt = Time.unscaledTime;
                if (!positioned && next.HasPosition) { renderedPosition = Convert(next.Position); positioned = true; }
            }
            if (!Ready || !Application.isFocused)
            { flightThrottle.Reset(); StopStationWalk(); return; }
            if (bindings.Pressed(ControlAction.OpenChat) && !chatOpen && !hud.InputFocused && !hud.SettingsOpen)
            {
                chatOpen = true;
                chatOpenedFrame = Time.frameCount;
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            { chatOpen = false; hud.ReleaseInputFocus(); }
            if (hud.InputFocused && Input.GetMouseButtonDown(0) && !hud.ContainsPointer())
                hud.ReleaseInputFocus();
            if (chatOpen || hud.InputFocused || hud.SettingsOpen)
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
            for (int abilitySlot = 0; abilitySlot < 6; abilitySlot++)
                if (bindings.Pressed((ControlAction)((int)ControlAction.AbilityOne + abilitySlot)))
                    abilityHud.ActivateSlot(abilitySlot, snapshot);
            if (bindings.Pressed(ControlAction.Warp)) WarpSelected();
            if (bindings.Pressed(ControlAction.Gate)) GateSelected();
            if (bindings.Pressed(ControlAction.Dock)) DockSelected();
            if (bindings.Pressed(ControlAction.ResetCamera)) { orbitYaw = 22; orbitPitch = 8; }
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
                if (bindings.Pressed(ControlAction.Follow)) FollowSelected();
            }
            else { previousYaw = previousPitch = 0; flightThrottle.Reset(); }
            if (!hud.ContainsPointer() && !miningHud.ContainsPointer() &&
                !abilityHud.ContainsPointer() && !missionHud.ContainsPointer())
            {
                if (Input.GetMouseButton(1) && !chart)
                { orbitYaw += Input.GetAxis("Mouse X") * 3; orbitPitch = Mathf.Clamp(orbitPitch - Input.GetAxis("Mouse Y") * 3, -65, 65); }
                float wheel = Input.mouseScrollDelta.y;
                if (chart) mapZoom = Mathf.Clamp(mapZoom * Mathf.Exp(-wheel * .12f), 100, 4000);
                else zoom = Mathf.Clamp(zoom * Mathf.Exp(-wheel * .1f), 1.5f, 14);
                if (Input.GetMouseButtonDown(0)) SelectAtPointer();
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
            displayFps = Mathf.RoundToInt(sampledFrames / sampledSeconds);
            displayFrameMs = slowestFrameMs;
            sampledFrames = 0; sampledSeconds = slowestFrameMs = 0;
            if (Time.unscaledTime >= nextPerformanceLog)
            {
                float snapshotAge = snapshot == null ? -1 : Time.unscaledTime - lastSnapshotAt;
                string currentStatus = (status ?? "").Replace('\r', ' ').Replace('\n', ' ');
                if (currentStatus.Length > 96) currentStatus = currentStatus.Substring(0, 96);
                Debug.Log($"42 and Beyond frame pacing: {displayFps} FPS, slowest {displayFrameMs:0.0} ms, " +
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

        /// <summary>Show only server-advertised pilots in the current station room.</summary>
        private void ApplyStationAvatars(Snapshot next)
        {
            uint room = next.Station?.CurrentRoom ?? uint.MaxValue;
            var visible = new HashSet<uint>();
            foreach (StationAvatar avatar in next.StationAvatars.Where(item =>
                room != uint.MaxValue && item.Room == room))
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
            float delta = Mathf.Min(Time.unscaledDeltaTime, .1f);
            float blend = 1 - Mathf.Exp(-14 * delta);
            bool docked = snapshot?.Station != null;
            if (snapshot != null && !docked)
            {
                bool formed = snapshot.Formation != null && snapshot.Group.Formed;
                float age = Mathf.Clamp(Time.unscaledTime - authoritativePoseAt, 0, .25f);
                Quaternion projectedRotation = formed ? ConvertRotation(snapshot.Orientation) :
                    ProjectRotation(snapshot, age);
                ship.rotation = Quaternion.Slerp(ship.rotation, projectedRotation, blend);
                float ramp = snapshot.Acceleration > .001f
                    ? Mathf.Min(age, Mathf.Abs(snapshot.TargetSpeed - snapshot.Speed) / snapshot.Acceleration) : 0;
                float direction = Mathf.Sign(snapshot.TargetSpeed - snapshot.Speed);
                float currentSpeed = snapshot.Speed + direction * snapshot.Acceleration * ramp;
                float distance = snapshot.Speed * ramp +
                    .5f * direction * snapshot.Acceleration * ramp * ramp + currentSpeed * (age - ramp);
                Vector3 velocity = ship.forward * (currentSpeed * MetersToWorld);
                Vector3 predicted = formed ? Convert(snapshot.Position) : Convert(snapshot.Position) +
                    projectedRotation * Vector3.forward * (distance * MetersToWorld);
                if (!formed) renderedPosition += velocity * delta;
                renderedPosition = Vector3.Distance(renderedPosition, predicted) > 50
                    ? predicted : Vector3.Lerp(renderedPosition, predicted, 1 - Mathf.Exp(-10 * delta));
            }
            worldRoot.position = -renderedPosition;
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
            }
            ship.gameObject.SetActive(!docked || dockingBay);
            environment.gameObject.SetActive(!chart && !docked);
            if (docked)
            {
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
                view.orthographic = true; view.orthographicSize = mapZoom;
                view.clearFlags = CameraClearFlags.SolidColor; view.backgroundColor = new Color(.008f, .018f, .032f);
                view.transform.position = new Vector3(0, 5000, 0); view.transform.rotation = Quaternion.Euler(90, 0, 0);
                ship.localScale = Vector3.one * mapZoom * .008f;
            }
            else
            {
                view.orthographic = false; view.clearFlags = CameraClearFlags.Skybox; ship.localScale = Vector3.one * .25f;
                Quaternion orbit = ship.rotation * Quaternion.Euler(orbitPitch, orbitYaw, 0);
                view.transform.position = orbit * new Vector3(0, zoom * .24f, -zoom);
                view.transform.LookAt(ship.position + ship.forward * .3f, orbit * Vector3.up);
            }
        }

        /// <summary>Route the actual mouse click through world target selection.</summary>
        private void SelectAtPointer()
        { SelectAtPointer(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y)); }

        /// <summary>Select the closest visible marker across ships and world objects.</summary>
        public void SelectAtPointer(Vector2 pointer)
        {
            if (snapshot == null) return;
            float best = 36, bestDepth = float.PositiveInfinity;
            uint selected = 0; byte kind = 0;
            foreach (ShipActor actor in snapshot.Players.Concat(snapshot.Mobs).Where(item => item.HasPosition))
            {
                Vector3 point = ProjectPlayer(actor.Id); if (point.z <= 0) continue;
                float distance = Vector2.Distance(new Vector2(point.x, point.y), pointer);
                if (distance <= 36 && (distance < best - 2 ||
                    Mathf.Abs(distance - best) <= 2 && point.z < bestDepth))
                { best = distance; bestDepth = point.z; selected = actor.Id; kind = 1; }
            }
            foreach (MapObject item in snapshot.Objects.Where(WarpTargets.WorldObject))
            {
                Vector3 point = Project(item.Position); if (point.z <= 0) continue;
                float distance = Vector2.Distance(new Vector2(point.x, point.y), pointer);
                if (distance <= 36 && (distance < best - 2 ||
                    Mathf.Abs(distance - best) <= 2 && point.z < bestDepth))
                { best = distance; bestDepth = point.z; selected = item.Id; kind = item.Kind == 25 ? (byte)2 :
                    item.Kind == 38 ? (byte)3 : (byte)4; }
            }
            if (selected == 0) return;
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

        /// <summary>Select an existing navigable server object.</summary>
        public void Select(uint id)
        {
            if (snapshot == null || !snapshot.Objects.Any(item => item.Id == id && item.Navigable)) return;
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
                contact.Id == id && contact.Room == snapshot.Station.CurrentRoom))
                worker.Submit(new ClientCommand(CommandKind.OpenNpc, id)); }

        /// <summary>Move the station avatar to one room offered by the server layout.</summary>
        public void EnterStationRoom(uint room)
        {
            if (Ready && snapshot.Station != null && snapshot.Dialogue == null &&
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

        /// <summary>Activate a server-supported effect from an installed device mount.</summary>
        public void ActivateDevice(int slot)
        {
            if (Ready && !Incapacitated && snapshot.Station == null &&
                snapshot.WarpPhase != WarpPhase.Charging && snapshot.WarpPhase != WarpPhase.Traveling &&
                slot >= 9 && slot < 9 + snapshot.DeviceMounts && slot < snapshot.Mounted.Length &&
                snapshot.Mounted[slot]?.ActivationLabel.Length > 0 &&
                snapshot.ActiveDeviceLabel.Length == 0)
                worker.Submit(new ClientCommand(CommandKind.ActivateDevice, (uint)slot));
        }

        /// <summary>Request activation of the equipped Turbo Weapons device.</summary>
        public void ActivateTurboWeapons()
        {
            if (Ready && !Incapacitated && snapshot.ActiveDeviceLabel.Length == 0 && snapshot.Station == null &&
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
            if (Ready && snapshot.OpenLootId == id)
                worker.Submit(new ClientCommand(CommandKind.CloseLoot, id));
        }

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

        /// <summary>Follow the currently selected visible ship through native flight control.</summary>
        public void FollowSelected()
        {
            uint id = hud.SelectedContactId;
            if (Ready && snapshot.Players.Concat(snapshot.Mobs).Any(actor => actor.Id == id && actor.HasPosition))
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
        { if (Ready) worker.Submit(new ClientCommand(CommandKind.Chat, target: group ? 1u : 4u, text: text)); }

        /// <summary>Whether the local chat entry is open.</summary>
        public bool ChatOpen => chatOpen;
        public int ChatOpenedFrame => chatOpenedFrame;
        public void CloseChatEntry() { chatOpen = false; }

        /// <summary>Switch between the flight camera and sector chart.</summary>
        public void SetChart(bool enabled) { chart = enabled; }

        /// <summary>Draw the HUD on the main thread.</summary>
        private void OnGUI()
        {
            Matrix4x4 previous = GUI.matrix;
            float scale = hud.Scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            try
            {
                hud.Draw(ref host, ref username, ref password);
                if (!hud.SettingsOpen && !hud.EquipmentOpen)
                {
                    if (productionHud.IsOpen) vaultHud.Close();
                    vaultHud.Draw(snapshot);
                    if (vaultHud.IsOpen && productionHud.IsOpen)
                    { productionHud.Close(); CloseProduction(); }
                    productionHud.Draw(snapshot);
                }
                if (!hud.SettingsOpen && !hud.EquipmentOpen && !AbilitiesOpen && !Incapacitated) miningHud.Draw(snapshot);
                if (!hud.SettingsOpen && !hud.EquipmentOpen && !Incapacitated) abilityHud.Draw(snapshot);
                if (!hud.SettingsOpen && !hud.EquipmentOpen && !AbilitiesOpen) turboHud.Draw(snapshot);
                if (!hud.SettingsOpen && !hud.EquipmentOpen && !AbilitiesOpen &&
                    !productionHud.IsOpen && !vaultHud.IsOpen) missionHud.Draw(snapshot);
                else missionHud.Close();
                if (!hud.SettingsOpen && Incapacitated) recoveryHud.Draw(snapshot);
            }
            finally { GUI.matrix = previous; }
        }

        /// <summary>Join the worker before removing the connected world's resources.</summary>
        public void Disconnect()
        {
            worker?.Dispose(); worker = null; snapshot = null; sector = ""; navigationSelection.Reset(); pendingNavigationTarget = 0; positioned = false;
            authoritativePoseAt = lastSnapshotAt = 0;
            picker?.Dispose(); picker = null; characters = null;
            previousYaw = previousPitch = 0; flightThrottle.Reset(); ClearWorld(); status = "Disconnected";
        }

        /// <summary>Release scene instances belonging to the departed sector.</summary>
        private void ClearWorld()
        {
            navigationSelection.Reset();
            pendingNavigationTarget = 0;
            hud?.ResetTargetSelection();
            stationWalk.EnterRoom(uint.MaxValue); nextStationPoseAt = 0;
            combatEffects?.Clear(); remoteShips?.Clear(); miningHud?.Clear();
            foreach (Transform body in bodies.Values) Destroy(body.gameObject);
            foreach (Transform body in stationBodies.Values) Destroy(body.gameObject);
            bodies.Clear(); stationBodies.Clear(); renderedObjects = System.Array.Empty<MapObject>();
        }

        /// <summary>Release held steering on focus loss while preserving authoritative travel.</summary>
        private void OnApplicationFocus(bool focused)
        {
            Application.targetFrameRate = focused ? 120 : 60;
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
