using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Render server-published weapon fire and confirmed damage in the flight scene.</summary>
    public sealed class CombatEffects
    {
        private sealed class Beam
        {
            public readonly uint Source, Target;
            public readonly LineRenderer Halo, Core;
            public readonly Color Color;
            public readonly ShotTravelKind Kind;
            public readonly Vector3 LaunchPoint;
            public readonly float Started, Lifetime;
            public readonly float ShieldRadius;
            public readonly Transform Body;
            public readonly ParticleSystem Exhaust;
            public readonly Vector3 CurveOffset;
            public bool VisualExpired;

            /// <summary>Keep a two-layer shot and its server-derived expiry together.</summary>
            public Beam(uint source, uint target, LineRenderer halo, LineRenderer core, Color color,
                ShotTravelKind kind, Vector3 launchPoint, float lifetime, Transform body,
                ParticleSystem exhaust, Vector3 curveOffset, float shieldRadius)
            {
                Source = source; Target = target; Halo = halo; Core = core; Color = color;
                Kind = kind; LaunchPoint = launchPoint; Started = Time.unscaledTime;
                Lifetime = lifetime; Body = body; Exhaust = exhaust; CurveOffset = curveOffset;
                ShieldRadius = shieldRadius;
            }
        }

        private sealed class Detonation
        {
            public readonly Transform Fire, Smoke;
            public readonly Renderer FireRenderer, SmokeRenderer;
            public readonly Light Flash;
            public readonly Vector3 Position;
            public readonly float Started;

            /// <summary>Track a missile detonation triggered by a server damage report.</summary>
            public Detonation(Transform fire, Renderer fireRenderer, Transform smoke,
                Renderer smokeRenderer, Light flash, Vector3 position)
            {
                Fire = fire; FireRenderer = fireRenderer; Smoke = smoke;
                SmokeRenderer = smokeRenderer; Flash = flash; Position = position;
                Started = Time.unscaledTime;
            }
        }

        private sealed class Impact
        {
            public readonly uint Target;
            public readonly LineRenderer Ring;
            public readonly LineRenderer ShieldArc;
            public readonly LineRenderer[] Sparks;
            public readonly Light Flash;
            public readonly ShieldHitVisual ShieldVisual;
            public readonly Color Color;
            public readonly bool Critical;
            public readonly bool? Shielded;
            public readonly float Started;

            /// <summary>Track a ring spawned only by a confirmed server damage event.</summary>
            public Impact(uint target, LineRenderer ring, LineRenderer shieldArc, LineRenderer[] sparks,
                Light flash, ShieldHitVisual shieldVisual, Color color, bool critical, bool? shielded)
            {
                Target = target; Ring = ring; ShieldArc = shieldArc; Sparks = sparks;
                Flash = flash; ShieldVisual = shieldVisual; Color = color;
                Critical = critical; Shielded = shielded;
                Started = Time.unscaledTime;
            }
        }

        private sealed class ShieldPreview
        {
            public readonly uint Source, Target;
            public readonly ShieldHitVisual Visual;
            public readonly float Started;

            /// <summary>Track a visual shield response from a server-published beam launch.</summary>
            public ShieldPreview(uint source, uint target, ShieldHitVisual visual)
            {
                Source = source; Target = target; Visual = visual;
                Started = Time.unscaledTime - .045f;
            }
        }

        private sealed class Destruction
        {
            public readonly Vector3 AbsolutePosition;
            public readonly LineRenderer Ring, InnerRing;
            public readonly LineRenderer[] Fragments;
            public readonly Transform[] Fire, Smoke;
            public readonly Renderer[] FireRenderers, SmokeRenderers;
            public readonly Light Flash;
            public readonly float Started;

            /// <summary>Keep one authoritative hull death and its scene effects together.</summary>
            public Destruction(Vector3 absolutePosition, LineRenderer ring, LineRenderer innerRing,
                LineRenderer[] fragments, Transform[] fire, Renderer[] fireRenderers,
                Transform[] smoke, Renderer[] smokeRenderers, Light flash)
            {
                AbsolutePosition = absolutePosition; Ring = ring; InnerRing = innerRing;
                Fragments = fragments; Fire = fire; FireRenderers = fireRenderers;
                Smoke = smoke; SmokeRenderers = smokeRenderers; Flash = flash;
                Started = Time.unscaledTime;
            }
        }

        private readonly List<Beam> beams = new List<Beam>();
        private readonly List<ParticleSystem> lingeringExhaust = new List<ParticleSystem>();
        private readonly List<Impact> impacts = new List<Impact>();
        private readonly List<ShieldPreview> shieldPreviews = new List<ShieldPreview>();
        private readonly List<Detonation> detonations = new List<Detonation>();
        private readonly List<Destruction> destructions = new List<Destruction>();
        private readonly Dictionary<uint, Vector3> mobPositions = new Dictionary<uint, Vector3>();
        private readonly Dictionary<uint, float> mobSeenAt = new Dictionary<uint, float>();
        private readonly Dictionary<uint, float> recentMobDamage = new Dictionary<uint, float>();
        private readonly Dictionary<uint, float> actorHulls = new Dictionary<uint, float>();
        private readonly HashSet<uint> knownWrecks = new HashSet<uint>();
        private bool wreckBaseline;
        private bool ownHullKnown;
        private float lastOwnHull;
        private readonly Dictionary<uint, float> reportedShields = new Dictionary<uint, float>();
        private readonly Transform parent, player, worldRoot;
        private readonly RemoteShips ships;
        private readonly ShipLoadoutVisuals loadout;
        private readonly Material material, impactMaterial, fireMaterial, smokeMaterial,
            exhaustMaterial;
        private readonly Material missileHullMaterial, missileFinMaterial, projectileMaterial,
            nozzleMaterial;
        private readonly MaterialPropertyBlock spriteProperties = new MaterialPropertyBlock();
        private Camera effectCamera;
        private float normalFieldOfView = 60f;
        private sealed class MiningVisual
        {
            public LineRenderer Halo, Core, Contact, Orbit;
            public readonly LineRenderer[] Packets = new LineRenderer[4];
            public MiningActivity Activity;
            public Vector3 Point;
        }
        private sealed class SapVisualEvent
        {
            public readonly uint Source, Target;
            public readonly NativeShieldSapVisual Visual;
            /// <summary>Keep one confirmed siphon descriptor with its ship endpoints.</summary>
            public SapVisualEvent(uint source, uint target, NativeShieldSapVisual visual)
            { Source = source; Target = target; Visual = visual; }
        }
        private readonly List<SapVisualEvent> sapVisuals = new List<SapVisualEvent>();
        private readonly MiningVisual prospectVisual = new MiningVisual();
        private readonly MiningVisual tractorVisual = new MiningVisual();
        private LineRenderer lootHalo, lootCore, lootFilament, lootRing;
        private LootActivity lootActivity;
        private Vector3 lootPoint;
        private readonly LineRenderer[] warpStreaks = new LineRenderer[28];
        private readonly LineRenderer[] driveHalos = new LineRenderer[2];
        private readonly LineRenderer[] driveCores = new LineRenderer[2];
        private readonly LineRenderer[] driveFins = new LineRenderer[2];
        private float driveSpeed;
        private LineRenderer deviceHalo, deviceTrace;
        private TurboEnergyVisual turboEnergy;
        private ShieldBurnEnergyVisual shieldBurnEnergy;
        private bool shieldBurnNova;
        private uint shieldBurnTargetId;
        private uint deviceEffectId;
        private ushort deviceDescriptor;
        private Color deviceTint;
        private LineRenderer peerHalo, peerSweep;
        private uint peerEffectId;
        private ushort peerDescriptor;
        private readonly LineRenderer[] recipientRings = new LineRenderer[2];
        private readonly uint[] recipientEffectIds = new uint[2];
        private Light rallyLight;
        private NativeRallyVisual nativeRally;
        private GameObject rallyAura;
        private Mesh rallyAuraMesh;
        private Material rallyAuraMaterial;
        private NativeTranceVisual nativeTrance;
        private uint tranceEffectId;
        private ushort tranceDescriptor;
        private NativeSkillShieldVisual nativeEnvironmentShield;
        private uint environmentShieldEffectId;
        private NativeSkillShieldVisual nativePsionicShield;
        private uint psionicShieldEffectId;
        private ShieldChargingVisual shieldCharging;
        private readonly Dictionary<uint, ShieldChargingVisual> peerShieldCharging =
            new Dictionary<uint, ShieldChargingVisual>();
        private readonly Dictionary<ulong, NativeHackingBeamVisual> nativeHackingBeams =
            new Dictionary<ulong, NativeHackingBeamVisual>();
        private readonly Dictionary<ulong, uint> hackingTargets = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, NativeMenaceVisual> nativeMenace =
            new Dictionary<ulong, NativeMenaceVisual>();
        private readonly Dictionary<ulong, uint> menaceTargets = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, NativeBefriendVisual> nativeBefriend =
            new Dictionary<ulong, NativeBefriendVisual>();
        private readonly Dictionary<ulong, uint> befriendTargets = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, NativeSkillShieldVisual> nativeRecharge =
            new Dictionary<ulong, NativeSkillShieldVisual>();
        private readonly Dictionary<ulong, uint> rechargeTargets = new Dictionary<ulong, uint>();
        private NativeInversionVisual nativeInversion;
        private uint inversionEffectId, inversionTargetId;
        private NativeSelfDestructVisual nativeSelfDestruct;
        private uint selfDestructEffectId;
        private readonly Dictionary<ulong, NativeFoldVisual> nativeFold =
            new Dictionary<ulong, NativeFoldVisual>();
        private readonly Dictionary<ulong, uint> foldTargets = new Dictionary<ulong, uint>();
        private NativeWormholeVisual nativeWormhole;
        private uint wormholeEffectId;
        private uint rallyEffectId;
        private ushort rallyDescriptor;
        private RallyHullFrame rallyHull;
        private WarpPhase warpPhase;
        private bool warpArrived;
        private float warpChangedAt;
        private long lastShotSequence, lastDamageSequence;
        private uint playerId;

        /// <summary>Load an explicitly packaged beam shader for the standalone player.</summary>
        public CombatEffects(Transform parent, Transform player, Transform worldRoot, RemoteShips ships)
        {
            this.parent = parent; this.player = player; this.worldRoot = worldRoot; this.ships = ships;
            loadout = new ShipLoadoutVisuals(player);
            Shader spriteShader = Resources.Load<Shader>("ExplosionSprite");
            Texture2D fireTexture = Resources.Load<Texture2D>("LegacyEffects/GLOW_1") ??
                Resources.Load<Texture2D>("ExplosionFireball");
            Texture2D smokeTexture = Resources.Load<Texture2D>("LegacyEffects/EXHAST3") ??
                Resources.Load<Texture2D>("ExplosionSmoke");
            if (spriteShader != null && fireTexture != null && smokeTexture != null)
            {
                fireMaterial = new Material(spriteShader); fireMaterial.SetTexture("_MainTex", fireTexture);
                smokeMaterial = new Material(spriteShader); smokeMaterial.SetTexture("_MainTex", smokeTexture);
            }
            Shader particleShader = Resources.Load<Shader>("ExplosionParticle");
            if (particleShader != null && smokeTexture != null)
            {
                exhaustMaterial = new Material(particleShader);
                exhaustMaterial.SetTexture("_MainTex", smokeTexture);
            }
            Shader hullShader = Resources.Load<Shader>("Surface");
            if (hullShader != null)
            {
                missileHullMaterial = ShotMaterial(hullShader, new Color(.47f, .52f, .54f), .78f,
                    .58f, new Color(.055f, .065f, .07f));
                missileFinMaterial = ShotMaterial(hullShader, new Color(.09f, .12f, .15f), .67f,
                    .38f, Color.black);
                projectileMaterial = ShotMaterial(hullShader, new Color(.66f, .55f, .38f), .7f,
                    .55f, new Color(.11f, .045f, .01f));
                nozzleMaterial = ShotMaterial(hullShader, new Color(.24f, .11f, .04f), .45f,
                    .5f, new Color(1f, .32f, .045f));
            }
            Shader shader = Resources.Load<Shader>("BeamAdditive");
            if (shader != null)
            {
                material = new Material(shader);
                impactMaterial = new Material(shader);
                impactMaterial.SetInt("_ZTest", 8);
                for (int side = 0; side < 2; side++)
                {
                    driveHalos[side] = Line("Ion drive plasma", .18f,
                        new Color(.24f, .72f, 1f, .3f), 4);
                    driveCores[side] = Line("Ion drive core", .045f,
                        new Color(.67f, .94f, 1f, .8f), 4);
                    driveFins[side] = Line("Warp drive shear", .03f,
                        new Color(.48f, .7f, 1f, .38f), 3);
                    foreach (LineRenderer line in new[] { driveHalos[side], driveCores[side], driveFins[side] })
                    {
                        if (line == null) continue;
                        line.widthCurve = new AnimationCurve(new Keyframe(0, .2f),
                            new Keyframe(.28f, 1f), new Keyframe(1, 0f));
                        line.enabled = false;
                    }
                }
            }
        }

        /// <returns>Material: An owned physical surface for a traveling round.</returns>
        private static Material ShotMaterial(Shader shader, Color color, float metallic,
            float smoothness, Color emission)
        {
            var surface = new Material(shader);
            surface.SetColor("_Color", color); surface.SetFloat("_Metallic", metallic);
            surface.SetFloat("_Glossiness", smoothness);
            surface.SetColor("_EmissionColor", emission);
            return surface;
        }

        /// <returns>LineRenderer: A scene-owned luminous line, or null if the shader is unavailable.</returns>
        private LineRenderer Line(string name, float width, Color color, int vertices, bool foreground = false)
        {
            if (material == null) return null;
            var model = new GameObject(name, typeof(LineRenderer));
            model.transform.SetParent(parent, false);
            LineRenderer line = model.GetComponent<LineRenderer>();
            line.sharedMaterial = foreground ? impactMaterial : material; line.positionCount = vertices;
            line.useWorldSpace = true; line.alignment = LineAlignment.View;
            line.numCapVertices = 4; line.numCornerVertices = 4;
            line.widthMultiplier = width;
            line.startColor = color; line.endColor = color;
            return line;
        }

        /// <returns>Transform: A solid round or finned missile oriented along local forward.</returns>
        private Transform CreateShotBody(ShotTravelKind kind)
        {
            var root = new GameObject(kind == ShotTravelKind.Missile ? "Guided missile" :
                "Kinetic projectile").transform;
            root.SetParent(parent, false);
            if (kind == ShotTravelKind.Projectile)
            {
                ShotPart(root, PrimitiveType.Capsule, "Machined projectile", Vector3.zero,
                    new Vector3(.055f, .12f, .055f), projectileMaterial,
                    Quaternion.Euler(90, 0, 0));
                ShotPart(root, PrimitiveType.Sphere, "Projectile tip", new Vector3(0, 0, .13f),
                    new Vector3(.085f, .085f, .11f), projectileMaterial, Quaternion.identity);
                return root;
            }
            root.localScale = Vector3.one * 1.35f;
            ShotPart(root, PrimitiveType.Cylinder, "Missile casing", Vector3.zero,
                new Vector3(.095f, .24f, .095f), missileHullMaterial,
                Quaternion.Euler(90, 0, 0));
            ShotPart(root, PrimitiveType.Sphere, "Missile seeker", new Vector3(0, 0, .24f),
                new Vector3(.12f, .12f, .16f), missileHullMaterial, Quaternion.identity);
            ShotPart(root, PrimitiveType.Cylinder, "Missile recognition band",
                new Vector3(0, 0, .11f), new Vector3(.101f, .023f, .101f),
                projectileMaterial, Quaternion.Euler(90, 0, 0));
            ShotPart(root, PrimitiveType.Cylinder, "Missile rocket motor", new Vector3(0, 0, -.25f),
                new Vector3(.055f, .055f, .055f), nozzleMaterial,
                Quaternion.Euler(90, 0, 0));
            for (int fin = 0; fin < 4; fin++)
            {
                Transform vane = ShotPart(root, PrimitiveType.Cube, "Missile stabilizer",
                    new Vector3(0, .12f, -.18f), new Vector3(.025f, .19f, .22f),
                    missileFinMaterial, Quaternion.identity);
                vane.localRotation = Quaternion.AngleAxis(fin * 90f, Vector3.forward);
                vane.localPosition = vane.localRotation * new Vector3(0, .12f, -.18f);
            }
            return root;
        }

        /// <returns>Transform: A child mesh that shares an owned shot material.</returns>
        private static Transform ShotPart(Transform root, PrimitiveType shape, string name,
            Vector3 position, Vector3 scale, Material surface, Quaternion rotation)
        {
            GameObject part = GameObject.CreatePrimitive(shape); part.name = name;
            part.transform.SetParent(root, false);
            part.transform.localPosition = position; part.transform.localScale = scale;
            part.transform.localRotation = rotation;
            part.GetComponent<Renderer>().sharedMaterial = surface;
            Object.Destroy(part.GetComponent<Collider>());
            return part.transform;
        }

        /// <returns>ParticleSystem: A short world-space smoke trail from the rocket motor.</returns>
        private ParticleSystem CreateMissileExhaust(Transform body)
        {
            if (exhaustMaterial == null) return null;
            var source = new GameObject("Missile exhaust", typeof(ParticleSystem));
            source.transform.SetParent(body, false);
            source.transform.localPosition = new Vector3(0, 0, -.3f);
            source.transform.localRotation = Quaternion.Euler(0, 180, 0);
            ParticleSystem particles = source.GetComponent<ParticleSystem>();
            var main = particles.main;
            main.playOnAwake = false; main.loop = true; main.duration = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(.45f, .8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(.12f, .4f);
            main.startSize = new ParticleSystem.MinMaxCurve(.19f, .29f);
            main.startColor = new Color(.78f, .8f, .8f, .62f);
            main.maxParticles = 110;
            var emission = particles.emission; emission.rateOverTime = 55f;
            var shape = particles.shape; shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 12f; shape.radius = .025f;
            var size = particles.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0, .5f), new Keyframe(1, 2.8f)));
            var color = particles.colorOverLifetime; color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(new Color(.91f, .9f, .86f), 0),
                new GradientColorKey(new Color(.4f, .45f, .48f), 1) },
                new[] { new GradientAlphaKey(.75f, 0), new GradientAlphaKey(.4f, .45f),
                    new GradientAlphaKey(0, 1) });
            color.color = gradient;
            var renderer = source.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = exhaustMaterial;
            particles.Play();
            return particles;
        }

        /// <summary>Show a compact fireball and smoke puff at a confirmed missile hit.</summary>
        private void SpawnDetonation(Vector3 position)
        {
            Transform fire = ExplosionSprite("Missile detonation", fireMaterial, out Renderer fireRenderer);
            Transform smoke = ExplosionSprite("Missile detonation smoke", smokeMaterial,
                out Renderer smokeRenderer);
            Light flash = null;
            if (detonations.Count < 6)
            {
                var source = new GameObject("Missile detonation flash", typeof(Light));
                source.transform.SetParent(parent, false);
                flash = source.GetComponent<Light>();
                flash.type = LightType.Point; flash.color = new Color(1f, .57f, .24f);
                flash.range = 4f; flash.intensity = 4f;
                flash.shadows = LightShadows.None;
            }
            detonations.Add(new Detonation(fire, fireRenderer, smoke, smokeRenderer,
                flash, position));
            if (detonations.Count > 8) RemoveDetonation(0);
        }

        /// <summary>Spawn visuals only for new authoritative shot and damage events.</summary>
        public void Apply(Snapshot snapshot)
        {
            playerId = snapshot.PlayerId;
            driveSpeed = Mathf.Abs(snapshot.Speed);
            loadout.Apply(snapshot);
            foreach (ShipActor mob in snapshot.Mobs.Where(actor => actor.HasPosition))
            {
                mobPositions[mob.Id] = SpaceClient.Convert(mob.Position);
                mobSeenAt[mob.Id] = Time.unscaledTime;
            }
            SetDeviceBuff(snapshot.DeviceBuff, snapshot.Station != null);
            SetAbilityEnergy(snapshot);
            SetPeerBuff(snapshot.PeerBuff, snapshot.Station != null);
            SetResourceBuff(0, snapshot.ShieldBuff, snapshot.Station != null);
            SetResourceBuff(1, snapshot.ReactorBuff, snapshot.Station != null);
            SetRallyBuff(snapshot.RallyBuff, snapshot.Station != null);
            SetCombatTrance(snapshot);
            SetEnvironmentShield(snapshot);
            SetPsionicShield(snapshot);
            SetShieldCharging(snapshot);
            SetHackingBeams(snapshot);
            SetMenaceVisuals(snapshot);
            SetBefriendVisuals(snapshot);
            SetRechargeVisuals(snapshot);
            SetInversionVisual(snapshot);
            SetSelfDestructVisual(snapshot);
            SetFoldVisuals(snapshot);
            SetWormholeVisual(snapshot);
            foreach (ShotEvent shot in snapshot.Shots)
            {
                if (shot.Sequence <= lastShotSequence) continue;
                lastShotSequence = shot.Sequence;
                if (snapshot.WarpPhase != WarpPhase.Idle ||
                    !TryPoint(shot.Target, out Vector3 shotTarget)) continue;
                bool hasLaunch = TryPoint(shot.Source, out Vector3 launch);
                if (!hasLaunch && shot.Asset != 146) continue;
                if (!hasLaunch) launch = shotTarget;
                if (shot.Asset == 139 || shot.Asset == 146)
                {
                    var sap = new NativeShieldSapVisual(parent, shot.Asset,
                        shot.Duration / 1000f, shot.Target == playerId);
                    if (TryPoint(shot.Target, out Vector3 sapTarget))
                        sap.Tick(launch, sapTarget);
                    sapVisuals.Add(new SapVisualEvent(shot.Source, shot.Target, sap));
                    if (sapVisuals.Count > 32)
                    { sapVisuals[0].Visual.Dispose(); sapVisuals.RemoveAt(0); }
                    continue;
                }
                if (shot.Asset == 98 && shot.Source == playerId)
                {
                    shieldBurnTargetId = shot.Target;
                    inversionTargetId = shot.Target;
                    if (shieldBurnEnergy != null && !shieldBurnNova)
                    {
                        shieldBurnEnergy.Dispose();
                        shieldBurnEnergy = null;
                    }
                    if (shieldBurnEnergy == null)
                        shieldBurnEnergy = new ShieldBurnEnergyVisual(parent, true);
                    shieldBurnNova = true;
                    continue;
                }
                if (shot.Source == playerId && loadout.TryMuzzle(shot.Sequence, out Vector3 muzzle))
                    launch = muzzle;
                ShotTravelKind kind = ShotPresentation.Classify(shot, snapshot.PlayerId,
                    snapshot.Mounted, snapshot.Ammo);
                Color tint = shot.Source == playerId ? new Color(.3f, .86f, 1f) :
                    new Color(1f, .37f, .18f);
                if (kind == ShotTravelKind.Missile && shot.Source == playerId)
                    tint = new Color(1f, .72f, .33f);
                LineRenderer halo = Line(kind == ShotTravelKind.Beam ? "Weapon corona" :
                    "Ordnance wake", .14f, tint, 2);
                LineRenderer core = Line(kind == ShotTravelKind.Beam ? "Weapon core" :
                    "Ordnance tracer", .025f, Color.white, 2);
                if (halo == null || core == null) continue;
                Transform body = kind == ShotTravelKind.Beam ? null : CreateShotBody(kind);
                ParticleSystem exhaust = kind == ShotTravelKind.Missile && body != null ?
                    CreateMissileExhaust(body) : null;
                Vector3 heading = (SpaceClient.Convert(snapshot.Position) - launch).normalized;
                if (TryPoint(shot.Target, out Vector3 destination))
                    heading = (destination - launch).normalized;
                Vector3 lateral = Vector3.Cross(heading, Vector3.up).normalized;
                if (lateral.sqrMagnitude < .01f) lateral = Vector3.right;
                Vector3 curve = kind == ShotTravelKind.Missile ?
                    (lateral * (shot.Sequence % 2 == 0 ? 1f : -1f) + Vector3.up * .45f) *
                    Mathf.Clamp(Vector3.Distance(launch, destination) * .12f, .3f, 1.5f) :
                    Vector3.zero;
                float shieldRadius = ShieldAt(snapshot, shot.Target) == true ?
                    ShieldEnvelopeRadius(snapshot, shot.Target) : 0f;
                beams.Add(new Beam(shot.Source, shot.Target, halo, core, tint, kind, launch,
                    kind == ShotTravelKind.Beam ? Mathf.Clamp(shot.Duration / 1000f + .25f, .32f, 1.3f) :
                    Mathf.Clamp(shot.Duration / 1000f, .12f, 65.5f), body, exhaust, curve,
                    shieldRadius));
                if (kind == ShotTravelKind.Beam && shieldRadius > 0f)
                    BeginShieldPreview(shot.Source, shot.Target, launch, shotTarget,
                        shieldRadius);
                if (beams.Count > 32) RemoveBeam(0);
            }
            foreach (DamageEvent damage in snapshot.Damages)
            {
                if (damage.Sequence <= lastDamageSequence) continue;
                lastDamageSequence = damage.Sequence;
                if (damage.Amount <= 0 && damage.Outcome == DamageOutcome.Miss)
                    CancelShieldPreview(damage.Source, damage.Target);
                ShotTravelKind? retired = RetireTravelingShot(damage.Source, damage.Target);
                if (damage.Amount > 0 && mobPositions.ContainsKey(damage.Target))
                    recentMobDamage[damage.Target] = Time.unscaledTime;
                if (snapshot.WarpPhase != WarpPhase.Idle ||
                    !TryPoint(damage.Target, out Vector3 targetPoint) ||
                    damage.Amount <= 0) continue;
                if (retired == ShotTravelKind.Missile &&
                    TryPoint(damage.Target, out Vector3 detonationPoint))
                    SpawnDetonation(detonationPoint);
                bool critical = damage.Outcome == DamageOutcome.Critical;
                bool? shielded = ShieldAt(snapshot, damage.Target);
                if (shielded == true && HasShieldPreview(damage.Source, damage.Target))
                    continue;
                ShieldHitVisual shieldVisual = null;
                if (shielded == true)
                {
                    Camera camera = ViewCamera();
                    Vector3 sourcePoint = TryPoint(damage.Source, out Vector3 attacker) ?
                        attacker : camera != null ? camera.transform.position :
                        targetPoint + player.forward;
                    float radius = ShieldEnvelopeRadius(snapshot, damage.Target);
                    ShieldChargingVisual charging = ChargedShield(damage.Target);
                    if (charging != null && charging.Active)
                        charging.Impact(sourcePoint - targetPoint);
                    else
                    {
                        shieldVisual = new ShieldHitVisual(radius, sourcePoint - targetPoint);
                        shieldVisual.Tick(targetPoint, 0);
                    }
                }
                Color tint = shielded == true ? new Color(.32f, .86f, 1f) : DamageColor(damage);
                LineRenderer ring = Line(shielded == true ? "Confirmed shield impact" :
                    "Confirmed hull or system impact", .035f, tint, 25, true);
                if (ring == null) { shieldVisual?.Dispose(); continue; }
                ring.useWorldSpace = false;
                if (shieldVisual != null && shieldVisual.Active) ring.enabled = false;
                LineRenderer shieldArc = shielded == true && (shieldVisual == null || !shieldVisual.Active) ?
                    Line("Shield envelope ripple", .045f, tint, 25, true) : null;
                if (shieldArc != null)
                {
                    shieldArc.transform.SetParent(ring.transform, false);
                    shieldArc.transform.localRotation = Quaternion.Euler(0, 58, 0);
                    shieldArc.useWorldSpace = false;
                }
                LineRenderer[] sparks = shielded == false ?
                    new LineRenderer[critical ? 6 : 3] : System.Array.Empty<LineRenderer>();
                for (int index = 0; index < sparks.Length; index++)
                {
                    sparks[index] = Line("Critical impact spark", .055f,
                        new Color(1f, .87f, .44f), 2, true);
                    if (sparks[index] == null) continue;
                    sparks[index].transform.SetParent(ring.transform, false);
                    sparks[index].useWorldSpace = false;
                    sparks[index].enabled = false;
                }
                Light flash = null;
                if (impacts.Count(impact => impact.Flash != null) < 4)
                {
                    var source = new GameObject("Confirmed impact light", typeof(Light));
                    source.transform.SetParent(parent, false);
                    flash = source.GetComponent<Light>();
                    flash.type = LightType.Point; flash.color = tint;
                    flash.range = critical ? 4f : 2.8f;
                    flash.intensity = critical ? 3.5f : 2.4f;
                    flash.shadows = LightShadows.None;
                    flash.renderMode = LightRenderMode.ForceVertex;
                }
                impacts.Add(new Impact(damage.Target, ring, shieldArc, sparks,
                    flash, shieldVisual, tint, critical, shielded));
                if (impacts.Count > 24) RemoveImpact(0);
            }
            UpdateDestructions(snapshot);
            reportedShields.Clear();
            reportedShields[snapshot.PlayerId] = snapshot.Shield;
            foreach (ShipActor actor in snapshot.Players.Concat(snapshot.Mobs))
                if (actor.Shield >= 0) reportedShields[actor.Id] = actor.Shield;
            SetWarpPhase(snapshot.WarpPhase, snapshot.WarpArrived);
            SetLootActivity(snapshot);
            SetMiningVisual(prospectVisual, snapshot.MiningProspect, snapshot);
            SetMiningVisual(tractorVisual, snapshot.MiningTractor, snapshot);
        }

        /// <returns>ShotTravelKind?: The retired round type, if a matching round exists.</returns>
        private ShotTravelKind? RetireTravelingShot(uint source, uint target)
        {
            for (int index = 0; index < beams.Count; index++)
                if (beams[index].Kind != ShotTravelKind.Beam && beams[index].Source == source &&
                    beams[index].Target == target)
                { ShotTravelKind kind = beams[index].Kind; RemoveBeam(index); return kind; }
            return null;
        }

        /// <summary>Start the shield response in the same frame as a visible beam.</summary>
        private void BeginShieldPreview(uint source, uint target, Vector3 launch,
            Vector3 targetPoint, float radius)
        {
            ShieldChargingVisual charging = ChargedShield(target);
            if (charging != null && charging.Active)
            {
                charging.Impact(launch - targetPoint);
                return;
            }
            var visual = new ShieldHitVisual(radius, launch - targetPoint);
            if (!visual.Active) { visual.Dispose(); return; }
            visual.Tick(targetPoint, .045f);
            shieldPreviews.Add(new ShieldPreview(source, target, visual));
            if (shieldPreviews.Count > 24) RemoveShieldPreview(0);
        }

        /// <returns>bool: Whether this beam already owns a visible shield response.</returns>
        private bool HasShieldPreview(uint source, uint target)
        {
            return shieldPreviews.Any(preview => preview.Source == source &&
                preview.Target == target && Time.unscaledTime - preview.Started <
                ShieldHitVisual.Duration);
        }

        /// <summary>Withdraw the earliest provisional shield response on a reported miss.</summary>
        private void CancelShieldPreview(uint source, uint target)
        {
            for (int index = 0; index < shieldPreviews.Count; index++)
                if (shieldPreviews[index].Source == source &&
                    shieldPreviews[index].Target == target)
                { RemoveShieldPreview(index); return; }
        }

        /// <summary>Start death effects from authoritative hull or damaged-ship removal.</summary>
        private void UpdateDestructions(Snapshot snapshot)
        {
            if (snapshot.Station != null || snapshot.WarpPhase != WarpPhase.Idle)
            {
                mobPositions.Clear(); mobSeenAt.Clear(); recentMobDamage.Clear();
                actorHulls.Clear(); ownHullKnown = false;
                knownWrecks.Clear(); wreckBaseline = false;
                return;
            }
            if (ownHullKnown && lastOwnHull > 0 && snapshot.MaxHull > 0 && snapshot.Hull <= 0)
                SpawnDestruction(SpaceClient.Convert(snapshot.Position));
            lastOwnHull = snapshot.Hull;
            ownHullKnown = true;
            var currentActors = new HashSet<uint>();
            foreach (ShipActor actor in snapshot.Players.Concat(snapshot.Mobs).Where(actor => actor.HasPosition))
            {
                currentActors.Add(actor.Id);
                if (actorHulls.TryGetValue(actor.Id, out float previousHull) &&
                    previousHull > 0 && actor.MaxHull > 0 && actor.Hull <= 0)
                {
                    SpawnDestruction(SpaceClient.Convert(actor.Position));
                    recentMobDamage.Remove(actor.Id);
                }
                actorHulls[actor.Id] = actor.Hull;
            }
            foreach (uint id in actorHulls.Keys.ToArray())
                if (!currentActors.Contains(id)) actorHulls.Remove(id);
            var currentMobs = new HashSet<uint>(snapshot.Mobs.Select(mob => mob.Id));
            foreach (uint id in mobPositions.Keys.ToArray())
            {
                if (currentMobs.Contains(id)) continue;
                if (recentMobDamage.TryGetValue(id, out float damagedAt) &&
                    Time.unscaledTime - damagedAt <= 3f)
                {
                    SpawnDestruction(mobPositions[id]);
                    mobPositions.Remove(id); mobSeenAt.Remove(id); recentMobDamage.Remove(id);
                }
            }
            var wrecks = snapshot.Objects.Where(item => item.Kind == 25).ToArray();
            var currentWrecks = new HashSet<uint>(wrecks.Select(item => item.Id));
            if (!wreckBaseline) { knownWrecks.UnionWith(currentWrecks); wreckBaseline = true; }
            knownWrecks.IntersectWith(currentWrecks);
            foreach (MapObject wreck in wrecks)
            {
                if (knownWrecks.Contains(wreck.Id)) continue;
                Vector3 wreckPoint = SpaceClient.Convert(wreck.Position);
                uint killed = 0;
                float closest = 5f;
                foreach (var mob in mobPositions)
                {
                    if (currentMobs.Contains(mob.Key) ||
                        !recentMobDamage.TryGetValue(mob.Key, out float damagedAt) ||
                        Time.unscaledTime - damagedAt > 3f) continue;
                    float distance = Vector3.Distance(mob.Value, wreckPoint);
                    if (distance >= closest) continue;
                    closest = distance; killed = mob.Key;
                }
                if (killed == 0) continue;
                SpawnDestruction(wreckPoint);
                mobPositions.Remove(killed); recentMobDamage.Remove(killed);
            }
            knownWrecks.UnionWith(currentWrecks);
            foreach (uint id in recentMobDamage.Keys.ToArray())
                if (Time.unscaledTime - recentMobDamage[id] > 3f)
                    recentMobDamage.Remove(id);
            foreach (uint id in mobPositions.Keys.ToArray())
                if (!currentMobs.Contains(id) &&
                    (!mobSeenAt.TryGetValue(id, out float seenAt) || Time.unscaledTime - seenAt > 3f))
                { mobPositions.Remove(id); mobSeenAt.Remove(id); }
        }

        /// <returns>Transform: A camera-facing explosion plane, or null without its material.</returns>
        private Transform ExplosionSprite(string name, Material surface, out Renderer renderer)
        {
            renderer = null;
            if (surface == null) return null;
            GameObject sprite = GameObject.CreatePrimitive(PrimitiveType.Quad);
            sprite.name = name;
            sprite.transform.SetParent(parent, false);
            Object.Destroy(sprite.GetComponent<Collider>());
            renderer = sprite.GetComponent<Renderer>();
            renderer.sharedMaterial = surface;
            return sprite.transform;
        }

        /// <summary>Start a bounded multi-stage explosion at an authoritative ship death.</summary>
        private void SpawnDestruction(Vector3 absolutePosition)
        {
            LineRenderer ring = Line("Confirmed hull shockwave", .13f,
                new Color(1f, .72f, .36f, .95f), 49, true);
            LineRenderer innerRing = Line("Confirmed inner blast wave", .07f,
                new Color(1f, .94f, .7f, .8f), 49, true);
            if (ring != null) ring.useWorldSpace = false;
            if (innerRing != null) innerRing.useWorldSpace = false;
            var fragments = new LineRenderer[14];
            for (int index = 0; index < fragments.Length; index++)
                fragments[index] = Line("Confirmed hull fragment", .11f,
                    new Color(1f, .68f, .35f, .8f), 2);
            var fire = new Transform[2];
            var fireRenderers = new Renderer[2];
            var smoke = new Transform[2];
            var smokeRenderers = new Renderer[2];
            for (int index = 0; index < 2; index++)
            {
                fire[index] = ExplosionSprite("Ship explosion fireball", fireMaterial,
                    out fireRenderers[index]);
                smoke[index] = ExplosionSprite("Ship explosion smoke", smokeMaterial,
                    out smokeRenderers[index]);
            }
            if (ring == null && fire[0] == null) return;
            Light flash = null;
            if (destructions.Count < 4)
            {
                var source = new GameObject("Confirmed explosion light", typeof(Light));
                source.transform.SetParent(parent, false);
                flash = source.GetComponent<Light>();
                flash.type = LightType.Point; flash.color = new Color(1f, .62f, .3f);
                flash.range = 10f; flash.intensity = 8f;
                flash.shadows = LightShadows.None;
                flash.renderMode = LightRenderMode.ForceVertex;
            }
            destructions.Add(new Destruction(absolutePosition, ring, innerRing, fragments,
                fire, fireRenderers, smoke, smokeRenderers, flash));
            if (destructions.Count > 4) RemoveDestruction(0);
        }

        /// <summary>Keep each server-owned prospect or ore tractor visual independent.</summary>
        private void SetMiningVisual(MiningVisual visual, MiningActivity activity, Snapshot snapshot)
        {
            MapObject resource = activity == null ? null : snapshot.Objects.FirstOrDefault(item =>
                item.Id == activity.TargetId && item.Kind == 38);
            if (activity == null || snapshot.Station != null || snapshot.WarpPhase != WarpPhase.Idle ||
                activity.Phase == MiningPhase.Prospect && resource == null)
            { ClearMiningVisual(visual); return; }
            if (visual.Activity == null || visual.Activity.EffectId != activity.EffectId ||
                visual.Activity.ArticleId != activity.ArticleId)
            {
                ClearMiningVisual(visual);
                Color tint = activity.Phase == MiningPhase.Prospect ? new Color(.35f, .95f, .9f) :
                    new Color(1f, .72f, .32f);
                string phase = activity.Phase == MiningPhase.Prospect ? "Prospect" : "Ore tractor";
                visual.Halo = Line(phase + " field", .09f, tint, 2);
                visual.Core = Line(phase + " focus", .025f, Color.white, 2);
                visual.Contact = Line(phase + " contact", .035f, tint, 5);
                visual.Orbit = Line(phase + " orbit", .025f, tint, 33);
                for (int index = 0; index < visual.Packets.Length; index++)
                    visual.Packets[index] = Line(phase + " transfer pulse", .035f, tint, 2);
                foreach (LineRenderer line in new[] { visual.Halo, visual.Core, visual.Contact, visual.Orbit })
                    if (line != null) line.enabled = false;
                foreach (LineRenderer line in visual.Packets)
                    if (line != null) line.enabled = false;
            }
            visual.Activity = activity;
            Position source = activity.Phase == MiningPhase.Tractor ? activity.Position : resource.Position;
            visual.Point = player.position + SpaceClient.Convert(source) -
                SpaceClient.Convert(snapshot.Position);
        }

        /// <summary>Anchor a wreck tractor effect to the server's transfer position and identity.</summary>
        private void SetLootActivity(Snapshot snapshot)
        {
            LootActivity activity = snapshot.LootActivity;
            if (activity == null || snapshot.Station != null || snapshot.WarpPhase != WarpPhase.Idle)
            { ClearLoot(); return; }
            if (lootActivity == null || lootActivity.EffectId != activity.EffectId ||
                lootActivity.ArticleId != activity.ArticleId)
            {
                ClearLoot();
                Color tint = new Color(1f, .64f, .28f);
                lootHalo = Line("Wreck tractor field", .17f, tint, 2);
                lootCore = Line("Wreck tractor core", .035f, Color.white, 2);
                lootFilament = Line("Wreck tractor flow", .025f, tint, 16);
                lootRing = Line("Wreck pickup lock", .04f, tint, 33);
                if (lootRing != null) lootRing.useWorldSpace = false;
                foreach (LineRenderer line in new[] { lootHalo, lootCore, lootFilament, lootRing })
                    if (line != null) line.enabled = false;
            }
            lootActivity = activity;
            lootPoint = player.position + SpaceClient.Convert(activity.Position) -
                SpaceClient.Convert(snapshot.Position);
        }

        /// <returns>bool: Whether a visible actor's rendered world position was found.</returns>
        private bool TryPoint(uint id, out Vector3 position)
        {
            if (id == playerId) { position = player.position; return true; }
            return ships.TryPosition(id, out position);
        }

        /// <returns>Camera: The active flight camera, including scenes without a MainCamera tag.</returns>
        private Camera ViewCamera()
        {
            if (effectCamera == null)
            {
                effectCamera = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
                if (effectCamera != null) normalFieldOfView = effectCamera.fieldOfView;
            }
            return effectCamera;
        }

        /// <returns>Color: A damage-type tint derived from the server's confirmed result.</returns>
        private Color DamageColor(DamageEvent damage)
        {
            switch (damage.Kind)
            {
                case DamageKind.Explosive: return new Color(1f, .7f, .28f);
                case DamageKind.Plasma: return new Color(1f, .4f, .18f);
                case DamageKind.Emp: return new Color(.38f, .64f, 1f);
                case DamageKind.Chemical: return new Color(.45f, 1f, .43f);
                case DamageKind.Psionic: return new Color(.83f, .47f, 1f);
                case DamageKind.System: return new Color(.72f, .6f, 1f);
                default: return new Color(1f, .47f, .23f);
            }
        }

        /// <returns>bool?: Whether the latest authoritative target state reports a live shield.</returns>
        private bool? ShieldAt(Snapshot snapshot, uint targetId)
        {
            float current = targetId == snapshot.PlayerId ? snapshot.Shield :
                snapshot.Players.Concat(snapshot.Mobs).FirstOrDefault(item => item.Id == targetId)?.Shield ?? -1;
            if (current < 0) return null;
            return current > .001f || reportedShields.TryGetValue(targetId, out float prior) && prior > .001f;
        }

        /// <returns>float: The same visible shield radius used by shots and impacts.</returns>
        private float ShieldEnvelopeRadius(Snapshot snapshot, uint targetId)
        {
            if (targetId == snapshot.PlayerId)
            {
                RallyHullFrame hull = RallyHullFrame.Measure(player);
                return Mathf.Clamp(Mathf.Max(hull.HalfWidth, hull.HalfLength) * 1.2f,
                    1.5f, 2.4f);
            }
            float scale = snapshot.Players.Concat(snapshot.Mobs)
                .FirstOrDefault(actor => actor.Id == targetId)?.Scale ?? 1f;
            return Mathf.Clamp(scale * 1.7f, 1.3f, 3.2f);
        }

        /// <summary>Track moving endpoints and animate the server-confirmed effects.</summary>
        public void Tick()
        {
            lingeringExhaust.RemoveAll(exhaust => exhaust == null);
            for (int index = beams.Count - 1; index >= 0; index--)
            {
                Beam beam = beams[index];
                float age = Time.unscaledTime - beam.Started;
                if (age >= beam.Lifetime + (beam.Kind == ShotTravelKind.Beam ? 0f : .8f) ||
                    !TryPoint(beam.Target, out Vector3 to) ||
                    beam.Kind == ShotTravelKind.Beam && !TryPoint(beam.Source, out _))
                { RemoveBeam(index); continue; }
                if (beam.Kind != ShotTravelKind.Beam && age >= beam.Lifetime)
                {
                    if (!beam.VisualExpired)
                    {
                        beam.Halo.enabled = false; beam.Core.enabled = false;
                        if (beam.Body != null)
                            foreach (MeshRenderer renderer in
                                beam.Body.GetComponentsInChildren<MeshRenderer>())
                                renderer.enabled = false;
                        if (beam.Exhaust != null)
                            beam.Exhaust.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                        beam.VisualExpired = true;
                    }
                    continue;
                }
                float fade = beam.Kind == ShotTravelKind.Beam ?
                    Mathf.Clamp01((beam.Lifetime - age) / .25f) :
                    1f - age / beam.Lifetime;
                Vector3 start, end;
                if (beam.Kind == ShotTravelKind.Beam)
                {
                    TryPoint(beam.Source, out Vector3 from);
                    Vector3 surface = TargetSurfacePoint(beam, to);
                    Vector3 offset = (surface - from).normalized * .18f;
                    start = from + offset; end = surface - offset;
                }
                else
                {
                    float progress = Mathf.Clamp01(age / beam.Lifetime);
                    float tail = Mathf.Min(beam.Kind == ShotTravelKind.Missile ? .035f : .06f,
                        .42f / Mathf.Max(.01f, Vector3.Distance(beam.LaunchPoint, to)));
                    end = ShotPosition(beam, to, progress);
                    Vector3 prior = ShotPosition(beam, to, Mathf.Max(0, progress - .012f));
                    Vector3 direction = (end - prior).normalized;
                    if (direction.sqrMagnitude < .01f) direction = (to - beam.LaunchPoint).normalized;
                    if (beam.Body != null)
                    {
                        beam.Body.position = end;
                        if (direction.sqrMagnitude > .01f)
                            beam.Body.rotation = Quaternion.LookRotation(direction, Vector3.up);
                    }
                    start = ShotPosition(beam, to, Mathf.Max(0, progress - tail)) -
                        direction * (beam.Kind == ShotTravelKind.Missile ? .4f : .1f);
                    end -= direction * (beam.Kind == ShotTravelKind.Missile ? .4f : .1f);
                }
                beam.Halo.SetPosition(0, start); beam.Halo.SetPosition(1, end);
                beam.Core.SetPosition(0, start); beam.Core.SetPosition(1, end);
                beam.Halo.widthMultiplier = (beam.Kind == ShotTravelKind.Beam ? .14f :
                    beam.Kind == ShotTravelKind.Missile ? .12f : .065f) * fade;
                beam.Core.widthMultiplier = (beam.Kind == ShotTravelKind.Beam ? .025f :
                    beam.Kind == ShotTravelKind.Missile ? .035f : .014f) * fade;
                Color corona = beam.Kind == ShotTravelKind.Beam ? beam.Color :
                    new Color(1f, .38f, .1f);
                corona.a = (beam.Kind == ShotTravelKind.Beam ? .28f : .34f) * fade;
                Color core = beam.Kind == ShotTravelKind.Beam ? Color.white :
                    new Color(1f, .79f, .4f);
                core.a = (beam.Kind == ShotTravelKind.Beam ? .9f : .65f) * fade;
                beam.Halo.startColor = beam.Halo.endColor = corona;
                beam.Core.startColor = beam.Core.endColor = core;
            }
            for (int index = shieldPreviews.Count - 1; index >= 0; index--)
            {
                ShieldPreview preview = shieldPreviews[index];
                float age = Time.unscaledTime - preview.Started;
                if (age >= ShieldHitVisual.Duration ||
                    !TryPoint(preview.Target, out Vector3 position))
                { RemoveShieldPreview(index); continue; }
                preview.Visual.Tick(position, age);
            }
            TickDetonations();
            for (int index = impacts.Count - 1; index >= 0; index--)
            {
                Impact impact = impacts[index];
                float age = Time.unscaledTime - impact.Started;
                float duration = impact.ShieldVisual != null && impact.ShieldVisual.Active ?
                    ShieldHitVisual.Duration : .48f;
                if (age >= duration || !TryPoint(impact.Target, out Vector3 position))
                { RemoveImpact(index); continue; }
                float markerAge = Mathf.Min(age, .48f);
                float markerFade = 1f - markerAge / .48f;
                Camera camera = ViewCamera();
                Vector3 facing = camera != null ? (camera.transform.position - position).normalized :
                    (player.position - position).normalized;
                bool ownImpact = impact.Target == playerId;
                impact.ShieldVisual?.Tick(position, age);
                impact.Ring.transform.position = impact.ShieldVisual != null &&
                    impact.ShieldVisual.Active ? impact.ShieldVisual.ContactPoint(position, age) :
                    ownImpact ? position : position + facing *
                    (impact.Shielded == true ? 1.05f : impact.Shielded == false ? .85f : .7f);
                if (impact.Flash != null)
                {
                    impact.Flash.transform.position = impact.Ring.transform.position;
                    impact.Flash.intensity = (impact.Critical ? 3.5f : 2.4f) *
                        markerFade * markerFade;
                }
                if (camera != null) impact.Ring.transform.rotation = camera.transform.rotation;
                float radius = ownImpact && impact.ShieldVisual == null ? .1f + markerAge * .3f :
                    impact.Shielded == true ? .32f + markerAge * 1.05f :
                    .12f + markerAge * (impact.Critical ? 1.5f : 1.1f);
                for (int point = 0; point < 25; point++)
                {
                    float angle = point * Mathf.PI * 2 / 24;
                    float contour = impact.Shielded == true ?
                        radius * (1 + .055f * Mathf.Cos(angle * 6)) : radius;
                    impact.Ring.SetPosition(point, new Vector3(Mathf.Cos(angle) * contour,
                        Mathf.Sin(angle) * contour, 0));
                }
                Color color = impact.Color; color.a = (impact.Shielded == true ? .95f :
                    impact.Critical ? 1f : .85f) * markerFade;
                impact.Ring.startColor = impact.Ring.endColor = color;
                impact.Ring.widthMultiplier = (impact.Shielded == true ? .055f :
                    impact.Critical ? .07f : .04f) * markerFade;
                if (impact.ShieldArc != null)
                {
                    for (int point = 0; point < 25; point++)
                    {
                        float angle = point * Mathf.PI * 2 / 24;
                        float contour = radius * (1.12f + .055f * Mathf.Cos(angle * 6));
                        impact.ShieldArc.SetPosition(point, new Vector3(Mathf.Cos(angle) * contour,
                            Mathf.Sin(angle) * contour, 0));
                    }
                    Color arc = impact.Color; arc.a = .58f * markerFade;
                    impact.ShieldArc.startColor = impact.ShieldArc.endColor = arc;
                    impact.ShieldArc.widthMultiplier = .045f * markerFade;
                }
                for (int spark = 0; spark < impact.Sparks.Length; spark++)
                {
                    LineRenderer ray = impact.Sparks[spark];
                    if (ray == null) continue;
                    float angle = (spark + .5f) * Mathf.PI * 2f / impact.Sparks.Length;
                    Vector3 direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
                    ray.SetPosition(0, direction * (radius + .08f));
                    ray.SetPosition(1, direction * (radius + .42f * markerFade));
                    Color flash = impact.Critical ? new Color(1f, .87f, .44f,
                        .9f * markerFade) : impact.Color;
                    if (!impact.Critical) flash.a = .72f * markerFade;
                    ray.startColor = ray.endColor = flash;
                    ray.widthMultiplier = .055f * markerFade;
                    ray.enabled = true;
                }
            }
            TickDestructions();
            TickShieldSap();
            TickMiningVisual(prospectVisual);
            TickMiningVisual(tractorVisual);
            TickLoot();
            TickWarp();
            TickDrive();
            TickDeviceBuff();
            turboEnergy?.Tick();
            TickShieldBurnEnergy();
            TickPeerBuff();
            TickResourceBuffs();
            TickRallyBuff();
            nativeTrance?.Tick();
            nativeEnvironmentShield?.Tick();
            nativePsionicShield?.Tick();
            shieldCharging?.Tick(player.position);
            foreach (var charged in peerShieldCharging)
                if (TryPoint(charged.Key, out Vector3 peerPoint))
                    charged.Value.Tick(peerPoint);
            TickHackingBeams();
            TickMenaceVisuals();
            TickBefriendVisuals();
            TickRechargeVisuals();
            TickInversionVisual();
            nativeSelfDestruct?.Tick(player.position, player.forward, ViewCamera());
            TickFoldVisuals();
            nativeWormhole?.Tick(player.position, player.rotation);
        }

        /// <summary>Track each confirmed Shield Sap strand for its one-second native duration.</summary>
        private void TickShieldSap()
        {
            for (int index = sapVisuals.Count - 1; index >= 0; index--)
            {
                SapVisualEvent effect = sapVisuals[index];
                bool hasSource = TryPoint(effect.Source, out Vector3 source);
                if (TryPoint(effect.Target, out Vector3 target) &&
                    (hasSource || effect.Visual.OrbOnly) &&
                    effect.Visual.Tick(hasSource ? source : target, target))
                    continue;
                effect.Visual.Dispose();
                sapVisuals.RemoveAt(index);
            }
        }

        /// <summary>Use server-published device and skill status to own energy visuals.</summary>
        private void SetAbilityEnergy(Snapshot snapshot)
        {
            bool turboActive = snapshot.Station == null && snapshot.TurboWeaponsActive;
            if (turboActive && turboEnergy == null)
                turboEnergy = new TurboEnergyVisual(player);
            if (!turboActive && turboEnergy != null)
            {
                turboEnergy.Dispose();
                turboEnergy = null;
            }
            bool burnActive = snapshot.Station == null && snapshot.ActiveBuffs.Any(buff =>
                buff.Slot == 15 && buff.Name == "Shield_Inversion");
            bool novaActive = snapshot.ObjectEffects.Any(effect =>
                effect.OwnerId == snapshot.PlayerId && effect.Descriptor == 98);
            if (burnActive && shieldBurnEnergy != null && shieldBurnNova != novaActive)
            {
                shieldBurnEnergy.Dispose();
                shieldBurnEnergy = null;
            }
            if (burnActive && shieldBurnEnergy == null)
            {
                shieldBurnEnergy = new ShieldBurnEnergyVisual(parent, novaActive);
                shieldBurnNova = novaActive;
                shieldBurnTargetId = snapshot.SelectedTargetId;
            }
            if (!burnActive && shieldBurnEnergy != null)
            {
                shieldBurnEnergy.Dispose();
                shieldBurnEnergy = null;
                shieldBurnTargetId = 0;
                shieldBurnNova = false;
            }
        }

        /// <summary>Follow the cast target for the active server-timed shield burn.</summary>
        private void TickShieldBurnEnergy()
        {
            if (shieldBurnEnergy == null) return;
            Vector3 target = TryPoint(shieldBurnTargetId, out Vector3 point) ? point :
                player.position + player.forward * 2f;
            shieldBurnEnergy.Tick(player.position, target);
        }

        /// <returns>Vector3: The visible round position along its target-tracking flight path.</returns>
        private static Vector3 ShotPosition(Beam shot, Vector3 target, float progress)
        {
            Vector3 point = Vector3.Lerp(shot.LaunchPoint,
                TargetSurfacePoint(shot, target), progress);
            return point + shot.CurveOffset * (4f * progress * (1f - progress));
        }

        /// <returns>Vector3: The visible shield boundary, or the ship center without a shield.</returns>
        private static Vector3 TargetSurfacePoint(Beam shot, Vector3 target)
        {
            Vector3 approach = target - shot.LaunchPoint;
            float distance = approach.magnitude;
            if (distance < .001f || shot.ShieldRadius <= 0) return target;
            return target - approach / distance * Mathf.Min(shot.ShieldRadius, distance * .8f);
        }

        /// <summary>Expand and fade missile blast sprites after the authoritative hit.</summary>
        private void TickDetonations()
        {
            Camera camera = ViewCamera();
            for (int index = detonations.Count - 1; index >= 0; index--)
            {
                Detonation burst = detonations[index];
                float age = Time.unscaledTime - burst.Started;
                if (age >= .85f) { RemoveDetonation(index); continue; }
                float fireFade = Mathf.Clamp01(1f - age / .42f);
                float smokeFade = Mathf.Clamp01(1f - age / .85f);
                Vector3 facing = camera != null ?
                    (camera.transform.position - burst.Position).normalized : Vector3.forward;
                Vector3 surface = burst.Position + facing * .85f;
                if (burst.Fire != null)
                {
                    burst.Fire.position = surface;
                    if (camera != null) burst.Fire.rotation = camera.transform.rotation;
                    burst.Fire.localScale = Vector3.one * (.65f + age * 2.4f);
                    TintSprite(burst.FireRenderer, new Color(1f, .95f, .88f, fireFade));
                }
                if (burst.Smoke != null)
                {
                    burst.Smoke.position = surface + Vector3.up * age * .24f;
                    if (camera != null) burst.Smoke.rotation = camera.transform.rotation;
                    burst.Smoke.localScale = Vector3.one * (.48f + age * 1.8f);
                    TintSprite(burst.SmokeRenderer, new Color(.55f, .59f, .63f,
                        smokeFade * .62f));
                }
                if (burst.Flash != null)
                {
                    burst.Flash.transform.position = burst.Position;
                    burst.Flash.intensity = 4f * fireFade * fireFade;
                }
            }
        }

        /// <returns>Color: The distinct luminous color of one server-published Rally tier.</returns>
        private static Color RallyTint(ushort descriptor)
        {
            switch (descriptor)
            {
                case 333: return new Color(1f, .36f, .15f);
                case 675: return new Color(.25f, .76f, 1f);
                case 676: return new Color(1f, .72f, .17f);
                default: return new Color(.7f, .46f, 1f);
            }
        }

        /// <summary>Track the server's active Combat Trance object effect.</summary>
        private void SetCombatTrance(Snapshot snapshot)
        {
            NativeObjectEffect effect = snapshot.Station == null ?
                snapshot.ObjectEffects.FirstOrDefault(candidate =>
                    candidate.OwnerId == snapshot.PlayerId &&
                    candidate.Descriptor >= 669 && candidate.Descriptor <= 673) : null;
            if (effect == null)
            {
                ClearCombatTrance();
                return;
            }
            if (nativeTrance != null && tranceEffectId == effect.EffectId &&
                tranceDescriptor == effect.Descriptor) return;
            ClearCombatTrance();
            nativeTrance = new NativeTranceVisual(player, effect.Descriptor);
            tranceEffectId = effect.EffectId;
            tranceDescriptor = effect.Descriptor;
        }

        /// <summary>Remove the Combat Trance rail and particles when the server effect ends.</summary>
        private void ClearCombatTrance()
        {
            nativeTrance?.Dispose();
            nativeTrance = null;
            tranceEffectId = 0;
            tranceDescriptor = 0;
        }

        /// <summary>Track the persistent native Environment Shield descriptor.</summary>
        private void SetEnvironmentShield(Snapshot snapshot)
        {
            NativeObjectEffect effect = snapshot.Station == null ?
                snapshot.ObjectEffects.FirstOrDefault(candidate =>
                    candidate.OwnerId == snapshot.PlayerId && candidate.Descriptor == 216) : null;
            if (effect == null)
            {
                ClearEnvironmentShield();
                return;
            }
            if (nativeEnvironmentShield != null &&
                environmentShieldEffectId == effect.EffectId) return;
            ClearEnvironmentShield();
            nativeEnvironmentShield = new NativeSkillShieldVisual(player, 216);
            environmentShieldEffectId = effect.EffectId;
        }

        /// <summary>Remove the imported shield when its server effect ends.</summary>
        private void ClearEnvironmentShield()
        {
            nativeEnvironmentShield?.Dispose();
            nativeEnvironmentShield = null;
            environmentShieldEffectId = 0;
        }

        /// <summary>Track the server's active native Psionic Shield descriptor.</summary>
        private void SetPsionicShield(Snapshot snapshot)
        {
            NativeObjectEffect effect = snapshot.Station == null ?
                snapshot.ObjectEffects.FirstOrDefault(candidate =>
                    candidate.OwnerId == snapshot.PlayerId && candidate.Descriptor == 214) : null;
            if (effect == null)
            {
                ClearPsionicShield();
                return;
            }
            if (nativePsionicShield != null && psionicShieldEffectId == effect.EffectId) return;
            ClearPsionicShield();
            nativePsionicShield = new NativeSkillShieldVisual(player, 214);
            psionicShieldEffectId = effect.EffectId;
        }

        /// <summary>Remove the imported Psionic Shield when its server effect ends.</summary>
        private void ClearPsionicShield()
        {
            nativePsionicShield?.Dispose();
            nativePsionicShield = null;
            psionicShieldEffectId = 0;
        }

        /// <summary>Show the TT field only while the server's recipient buff is active.</summary>
        private void SetShieldCharging(Snapshot snapshot)
        {
            bool active = snapshot.Station == null && snapshot.ActiveBuffs.Any(buff =>
                buff.Slot == 13);
            if (!active)
                ClearOwnShieldCharging();
            else
            {
                float radius = ShieldEnvelopeRadius(snapshot, snapshot.PlayerId);
                if (shieldCharging == null) shieldCharging = new ShieldChargingVisual(radius);
                else shieldCharging.SetRadius(radius);
            }
            var peers = snapshot.Station == null ?
                new HashSet<uint>(snapshot.ShieldChargingPeers) : new HashSet<uint>();
            foreach (uint id in peerShieldCharging.Keys.Where(id => !peers.Contains(id)).ToArray())
            {
                peerShieldCharging[id].Dispose();
                peerShieldCharging.Remove(id);
            }
            foreach (uint id in peers)
            {
                float radius = ShieldEnvelopeRadius(snapshot, id);
                if (!peerShieldCharging.TryGetValue(id, out ShieldChargingVisual visual))
                    peerShieldCharging[id] = new ShieldChargingVisual(radius);
                else visual.SetRadius(radius);
            }
            for (int index = shieldPreviews.Count - 1; index >= 0; index--)
                if ((shieldPreviews[index].Target == snapshot.PlayerId && active) ||
                    peers.Contains(shieldPreviews[index].Target))
                    RemoveShieldPreview(index);
        }

        /// <returns>ShieldChargingVisual: The server-owned field on this visible ship, if any.</returns>
        private ShieldChargingVisual ChargedShield(uint id)
        {
            if (id == playerId) return shieldCharging;
            return peerShieldCharging.TryGetValue(id, out ShieldChargingVisual visual) ?
                visual : null;
        }

        /// <summary>Remove the local field while preserving fields on visible peers.</summary>
        private void ClearOwnShieldCharging()
        {
            shieldCharging?.Dispose();
            shieldCharging = null;
        }

        /// <summary>Remove the charging membrane when its authoritative buff ends.</summary>
        private void ClearShieldCharging()
        {
            ClearOwnShieldCharging();
            foreach (ShieldChargingVisual visual in peerShieldCharging.Values)
                visual.Dispose();
            peerShieldCharging.Clear();
        }

        /// <summary>Match archived Hacking beams to server-published target effects.</summary>
        private void SetHackingBeams(Snapshot snapshot)
        {
            var current = new HashSet<ulong>();
            if (snapshot.Station == null)
                foreach (NativeObjectEffect effect in snapshot.ObjectEffects)
                {
                    if (effect.Descriptor != 193 || effect.OwnerId == snapshot.PlayerId) continue;
                    ulong key = ((ulong)effect.OwnerId << 32) | effect.EffectId;
                    current.Add(key);
                    if (nativeHackingBeams.ContainsKey(key)) continue;
                    nativeHackingBeams[key] = new NativeHackingBeamVisual(parent);
                    hackingTargets[key] = effect.OwnerId;
                }
            foreach (ulong key in nativeHackingBeams.Keys.Where(key => !current.Contains(key)).ToArray())
            {
                nativeHackingBeams[key].Dispose();
                nativeHackingBeams.Remove(key);
                hackingTargets.Remove(key);
            }
        }

        /// <summary>Follow each native Hacking target as both ships move.</summary>
        private void TickHackingBeams()
        {
            foreach (var beam in nativeHackingBeams)
            {
                if (TryPoint(hackingTargets[beam.Key], out Vector3 target))
                    beam.Value.Tick(player.position, target);
                else
                    beam.Value.Hide();
            }
        }

        /// <summary>Release Hacking beams on sector change or logout.</summary>
        private void ClearHackingBeams()
        {
            foreach (NativeHackingBeamVisual beam in nativeHackingBeams.Values)
                beam.Dispose();
            nativeHackingBeams.Clear();
            hackingTargets.Clear();
        }

        /// <summary>Match Menace holograms to server-published mob effects.</summary>
        private void SetMenaceVisuals(Snapshot snapshot)
        {
            var current = new HashSet<ulong>();
            if (snapshot.Station == null)
                foreach (NativeObjectEffect effect in snapshot.ObjectEffects)
                {
                    if (effect.Descriptor != 198 && effect.Descriptor != 199) continue;
                    ulong key = ((ulong)effect.OwnerId << 32) | effect.EffectId;
                    current.Add(key);
                    if (nativeMenace.ContainsKey(key)) continue;
                    nativeMenace[key] = new NativeMenaceVisual(parent, effect.Descriptor);
                    menaceTargets[key] = effect.OwnerId;
                }
            foreach (ulong key in nativeMenace.Keys.Where(key => !current.Contains(key)).ToArray())
            {
                nativeMenace[key].Dispose();
                nativeMenace.Remove(key);
                menaceTargets.Remove(key);
            }
        }

        /// <summary>Keep each original skull over its affected mob.</summary>
        private void TickMenaceVisuals()
        {
            Camera camera = ViewCamera();
            foreach (var visual in nativeMenace)
            {
                if (TryPoint(menaceTargets[visual.Key], out Vector3 target))
                {
                    visual.Value.Show();
                    visual.Value.Tick(target, camera);
                }
                else visual.Value.Hide();
            }
        }

        /// <summary>Release Menace holograms on sector change or logout.</summary>
        private void ClearMenaceVisuals()
        {
            foreach (NativeMenaceVisual visual in nativeMenace.Values)
                visual.Dispose();
            nativeMenace.Clear();
            menaceTargets.Clear();
        }

        /// <summary>Match Befriend spark emitters to server-published mob effects.</summary>
        private void SetBefriendVisuals(Snapshot snapshot)
        {
            var current = new HashSet<ulong>();
            if (snapshot.Station == null)
                foreach (NativeObjectEffect effect in snapshot.ObjectEffects)
                {
                    if (effect.Descriptor != 221) continue;
                    ulong key = ((ulong)effect.OwnerId << 32) | effect.EffectId;
                    current.Add(key);
                    if (nativeBefriend.ContainsKey(key)) continue;
                    nativeBefriend[key] = new NativeBefriendVisual(parent);
                    befriendTargets[key] = effect.OwnerId;
                }
            foreach (ulong key in nativeBefriend.Keys.Where(key => !current.Contains(key)).ToArray())
            {
                nativeBefriend[key].Dispose();
                nativeBefriend.Remove(key);
                befriendTargets.Remove(key);
            }
        }

        /// <summary>Keep the original Befriend sparks on affected mobs.</summary>
        private void TickBefriendVisuals()
        {
            foreach (var visual in nativeBefriend)
            {
                if (TryPoint(befriendTargets[visual.Key], out Vector3 target))
                {
                    visual.Value.Show();
                    visual.Value.Tick(target);
                }
                else visual.Value.Hide();
            }
        }

        /// <summary>Release Befriend emitters on sector change or logout.</summary>
        private void ClearBefriendVisuals()
        {
            foreach (NativeBefriendVisual visual in nativeBefriend.Values)
                visual.Dispose();
            nativeBefriend.Clear();
            befriendTargets.Clear();
        }

        /// <summary>Match original recharge flashes to confirmed repair effects.</summary>
        private void SetRechargeVisuals(Snapshot snapshot)
        {
            var current = new HashSet<ulong>();
            if (snapshot.Station == null)
                foreach (NativeObjectEffect effect in snapshot.ObjectEffects)
                {
                    if (effect.Descriptor != 136) continue;
                    ulong key = ((ulong)effect.OwnerId << 32) | effect.EffectId;
                    current.Add(key);
                    if (nativeRecharge.ContainsKey(key)) continue;
                    nativeRecharge[key] = new NativeSkillShieldVisual(parent, 136);
                    rechargeTargets[key] = effect.OwnerId;
                }
            foreach (ulong key in nativeRecharge.Keys.Where(key => !current.Contains(key)).ToArray())
            {
                nativeRecharge[key].Dispose();
                nativeRecharge.Remove(key);
                rechargeTargets.Remove(key);
            }
        }

        /// <summary>Animate each recharge flash at the server-owned ship.</summary>
        private void TickRechargeVisuals()
        {
            foreach (var visual in nativeRecharge)
                if (TryPoint(rechargeTargets[visual.Key], out Vector3 target))
                    visual.Value.TickAt(target);
        }

        /// <summary>Release recharge flashes on sector change or logout.</summary>
        private void ClearRechargeVisuals()
        {
            foreach (NativeSkillShieldVisual visual in nativeRecharge.Values)
                visual.Dispose();
            nativeRecharge.Clear();
            rechargeTargets.Clear();
        }

        /// <summary>Attach native Shield Inversion lightning to its confirmed cast.</summary>
        private void SetInversionVisual(Snapshot snapshot)
        {
            NativeObjectEffect effect = snapshot.Station == null ?
                snapshot.ObjectEffects.FirstOrDefault(candidate =>
                    candidate.OwnerId == snapshot.PlayerId && candidate.Descriptor == 98) : null;
            if (effect == null)
            {
                ClearInversionVisual();
                return;
            }
            if (nativeInversion != null && inversionEffectId == effect.EffectId) return;
            ClearInversionVisual();
            nativeInversion = new NativeInversionVisual(parent);
            inversionEffectId = effect.EffectId;
            inversionTargetId = snapshot.SelectedTargetId;
        }

        /// <summary>Keep sustained lightning aimed at the cast's selected mob.</summary>
        private void TickInversionVisual()
        {
            if (nativeInversion == null) return;
            Vector3 target = TryPoint(inversionTargetId, out Vector3 point) ? point :
                player.position + player.forward * 2f;
            nativeInversion.Tick(player.position, target);
        }

        /// <summary>Release Shield Inversion lightning when its native effect ends.</summary>
        private void ClearInversionVisual()
        {
            nativeInversion?.Dispose();
            nativeInversion = null;
            inversionEffectId = inversionTargetId = 0;
        }

        /// <summary>Start the original numbered visual on a confirmed Self Destruct.</summary>
        private void SetSelfDestructVisual(Snapshot snapshot)
        {
            NativeObjectEffect effect = snapshot.Station == null ?
                snapshot.ObjectEffects.FirstOrDefault(candidate =>
                    candidate.OwnerId == snapshot.PlayerId && candidate.Descriptor == 206) : null;
            if (effect == null)
            {
                ClearSelfDestructVisual();
                return;
            }
            if (nativeSelfDestruct != null && selfDestructEffectId == effect.EffectId) return;
            ClearSelfDestructVisual();
            nativeSelfDestruct = new NativeSelfDestructVisual(parent);
            selfDestructEffectId = effect.EffectId;
        }

        /// <summary>Release the countdown when the native effect ends.</summary>
        private void ClearSelfDestructVisual()
        {
            nativeSelfDestruct?.Dispose();
            nativeSelfDestruct = null;
            selfDestructEffectId = 0;
        }

        /// <summary>Match original Fold Space emitters to active native effects.</summary>
        private void SetFoldVisuals(Snapshot snapshot)
        {
            var current = new HashSet<ulong>();
            if (snapshot.Station == null)
                foreach (NativeObjectEffect effect in snapshot.ObjectEffects)
                {
                    if (effect.Descriptor != 202 && effect.Descriptor != 267) continue;
                    ulong key = ((ulong)effect.OwnerId << 32) | effect.EffectId;
                    current.Add(key);
                    if (nativeFold.ContainsKey(key)) continue;
                    nativeFold[key] = new NativeFoldVisual(parent, effect.Descriptor);
                    foldTargets[key] = effect.OwnerId;
                }
            foreach (ulong key in nativeFold.Keys.Where(key => !current.Contains(key)).ToArray())
            {
                nativeFold[key].Dispose();
                nativeFold.Remove(key);
                foldTargets.Remove(key);
            }
        }

        /// <summary>Keep Fold Space particles on the server-owned ship.</summary>
        private void TickFoldVisuals()
        {
            foreach (var visual in nativeFold)
            {
                if (TryPoint(foldTargets[visual.Key], out Vector3 target))
                {
                    visual.Value.Show();
                    visual.Value.Tick(target);
                }
                else visual.Value.Hide();
            }
        }

        /// <summary>Release Fold Space particles on sector change or logout.</summary>
        private void ClearFoldVisuals()
        {
            foreach (NativeFoldVisual visual in nativeFold.Values)
                visual.Dispose();
            nativeFold.Clear();
            foldTargets.Clear();
        }

        /// <summary>Attach original Create Wormhole cast emitters to the pilot.</summary>
        private void SetWormholeVisual(Snapshot snapshot)
        {
            NativeObjectEffect effect = snapshot.Station == null ?
                snapshot.ObjectEffects.FirstOrDefault(candidate =>
                    candidate.OwnerId == snapshot.PlayerId && candidate.Descriptor == 689) : null;
            if (effect == null)
            {
                ClearWormholeVisual();
                return;
            }
            if (nativeWormhole != null && wormholeEffectId == effect.EffectId) return;
            ClearWormholeVisual();
            nativeWormhole = new NativeWormholeVisual(parent);
            wormholeEffectId = effect.EffectId;
        }

        /// <summary>Release the original portal when its cast effect ends.</summary>
        private void ClearWormholeVisual()
        {
            nativeWormhole?.Dispose();
            nativeWormhole = null;
            wormholeEffectId = 0;
        }

        /// <summary>Create a ship-local tactical field only for a confirmed Rally visual.</summary>
        private void SetRallyBuff(DeviceBuffView buff, bool docked)
        {
            if (docked || buff == null || !buff.VisualActive ||
                buff.VisualDescriptor != 333 && buff.VisualDescriptor != 675 &&
                buff.VisualDescriptor != 676 && buff.VisualDescriptor != 327)
            { ClearRallyBuff(); return; }
            if (nativeRally != null && rallyEffectId == buff.VisualEffectId &&
                rallyDescriptor == buff.VisualDescriptor) return;
            ClearRallyBuff();
            rallyEffectId = buff.VisualEffectId;
            rallyDescriptor = buff.VisualDescriptor;
            UnityEngine.Debug.Log("Rally visual attached: descriptor=" + rallyDescriptor +
                ", effect=" + rallyEffectId + ", buff=" + buff.Name);
            rallyHull = RallyHullFrame.Measure(player);
            nativeRally = new NativeRallyVisual(player, rallyDescriptor, rallyHull);
            Color tint = RallyTint(rallyDescriptor);
            CreateRallyAura();
            var source = new GameObject("Rally ship glow", typeof(Light));
            source.transform.SetParent(player, false);
            source.transform.localPosition = new Vector3(rallyHull.CenterX,
                rallyHull.BottomY - .15f, rallyHull.CenterZ) /
                Mathf.Max(.001f, Mathf.Abs(player.lossyScale.x));
            rallyLight = source.GetComponent<Light>();
            rallyLight.type = LightType.Point;
            rallyLight.color = tint;
            rallyLight.range = Mathf.Max(2.7f, rallyHull.HalfWidth * 2.8f);
            rallyLight.shadows = LightShadows.None;
            rallyLight.renderMode = LightRenderMode.ForceVertex;
        }

        /// <summary>Place one soft, untextured gold glow beneath the visible hull.</summary>
        private void CreateRallyAura()
        {
            Shader shader = Resources.Load<Shader>("LegacyEffect");
            if (shader == null) return;
            rallyAura = new GameObject("Rally hull glow", typeof(MeshFilter), typeof(MeshRenderer));
            rallyAura.transform.SetParent(player, false);
            rallyAuraMesh = new Mesh { name = "Rally soft disc" };
            const int segments = 48;
            var vertices = new Vector3[segments + 2];
            var colors = new Color[segments + 2];
            var triangles = new int[segments * 3];
            vertices[0] = Vector3.zero;
            colors[0] = new Color(1f, 1f, 1f, .8f);
            for (int index = 0; index <= segments; index++)
            {
                float angle = index * Mathf.PI * 2f / segments;
                vertices[index + 1] = new Vector3(
                    Mathf.Cos(angle) * rallyHull.HalfWidth * 2f, 0,
                    Mathf.Sin(angle) * rallyHull.HalfLength * 1.5f);
                colors[index + 1] = new Color(1f, 1f, 1f, 0f);
                if (index == segments) continue;
                triangles[index * 3] = 0;
                triangles[index * 3 + 1] = index + 1;
                triangles[index * 3 + 2] = index + 2;
            }
            rallyAuraMesh.vertices = vertices;
            rallyAuraMesh.colors = colors;
            rallyAuraMesh.triangles = triangles;
            rallyAuraMesh.RecalculateBounds();
            rallyAura.GetComponent<MeshFilter>().sharedMesh = rallyAuraMesh;
            rallyAuraMaterial = new Material(shader);
            rallyAuraMaterial.SetTexture("_MainTex", Texture2D.whiteTexture);
            rallyAuraMaterial.SetColor("_Tint", new Color(1f, .6f, .15f, .95f));
            rallyAura.GetComponent<MeshRenderer>().sharedMaterial = rallyAuraMaterial;
        }

        /// <summary>Animate the original Rally rig and its soft under-hull light.</summary>
        private void TickRallyBuff()
        {
            if (nativeRally == null || !nativeRally.Active) return;
            float time = Time.unscaledTime;
            float pulse = .78f + .22f * Mathf.Sin(time *
                (rallyDescriptor == 676 ? 7f : 3.5f));
            nativeRally.Tick();
            if (rallyLight != null)
                rallyLight.intensity = (rallyDescriptor == 327 ? .45f : .8f) * pulse;
            if (rallyAura == null) return;
            float scale = Mathf.Max(.001f, Mathf.Abs(player.lossyScale.x));
            rallyAura.transform.localScale = Vector3.one / scale;
            rallyAura.transform.localPosition = new Vector3(rallyHull.CenterX,
                rallyHull.BottomY - .12f, rallyHull.CenterZ) / scale;
            rallyAuraMaterial.SetColor("_Tint", new Color(1f, .62f, .2f, .95f * pulse));
        }

        /// <summary>Release the original Rally rig and its ship glow.</summary>
        private void ClearRallyBuff()
        {
            nativeRally?.Dispose();
            nativeRally = null;
            if (rallyLight != null) Object.Destroy(rallyLight.gameObject);
            if (rallyAura != null) Object.Destroy(rallyAura);
            if (rallyAuraMesh != null) Object.Destroy(rallyAuraMesh);
            if (rallyAuraMaterial != null) Object.Destroy(rallyAuraMaterial);
            rallyLight = null;
            rallyAura = null;
            rallyAuraMesh = null;
            rallyAuraMaterial = null;
            rallyEffectId = 0;
            rallyDescriptor = 0;
        }

        /// <summary>Animate a fireball, smoke, shockwave and ejecta at a confirmed death.</summary>
        private void TickDestructions()
        {
            Camera camera = ViewCamera();
            Vector3 right = camera != null ? camera.transform.right : Vector3.right;
            Vector3 up = camera != null ? camera.transform.up : Vector3.up;
            Vector3 forward = camera != null ? camera.transform.forward : Vector3.forward;
            for (int index = destructions.Count - 1; index >= 0; index--)
            {
                Destruction burst = destructions[index];
                float age = Time.unscaledTime - burst.Started;
                if (age >= 2.8f) { RemoveDestruction(index); continue; }
                Vector3 position = worldRoot.TransformPoint(burst.AbsolutePosition);
                float waveFade = Mathf.Clamp01((1.35f - age) / 1.1f);
                for (int waveIndex = 0; waveIndex < 2; waveIndex++)
                {
                    LineRenderer wave = waveIndex == 0 ? burst.Ring : burst.InnerRing;
                    if (wave == null) continue;
                    wave.transform.position = position;
                    if (camera != null) wave.transform.rotation = camera.transform.rotation;
                    bool outer = wave == burst.Ring;
                    float radius = (outer ? .25f : .13f) + age * (outer ? 4.2f : 2.4f);
                    for (int point = 0; point < 49; point++)
                    {
                        float angle = point * Mathf.PI * 2 / 48;
                        float contour = radius * (1 + .04f * Mathf.Cos(angle * 9));
                        wave.SetPosition(point, new Vector3(Mathf.Cos(angle) * contour,
                            Mathf.Sin(angle) * contour, 0));
                    }
                    Color tint = outer ? new Color(1f, .56f, .23f, waveFade * .8f) :
                        new Color(1f, .93f, .62f, waveFade * .9f);
                    wave.startColor = wave.endColor = tint;
                    wave.widthMultiplier = (outer ? .09f : .05f) * waveFade;
                }
                if (burst.Flash != null)
                {
                    burst.Flash.transform.position = position;
                    float flashFade = Mathf.Clamp01(1 - age / .85f);
                    burst.Flash.intensity = 8f * flashFade * flashFade;
                }
                float fireFade = Mathf.Clamp01((1.5f - age) / .85f);
                float smokeFade = Mathf.Clamp01((age - .2f) / .35f) *
                    Mathf.Clamp01((2.8f - age) / 1.25f);
                for (int layer = 0; layer < burst.Fire.Length; layer++)
                {
                    Transform flame = burst.Fire[layer];
                    if (flame != null)
                    {
                        flame.position = position - forward * (.18f + layer * .1f);
                        flame.rotation = (camera != null ? camera.transform.rotation :
                            Quaternion.identity) * Quaternion.Euler(0, 0,
                            layer * 78f + age * (layer == 0 ? 13f : -18f));
                        flame.localScale = Vector3.one *
                            ((layer == 0 ? 2.6f : 2.0f) + age * (layer == 0 ? 2.2f : 1.6f));
                        TintSprite(burst.FireRenderers[layer], new Color(1f, 1f, 1f,
                            fireFade * (layer == 0 ? .95f : .62f)));
                    }
                    Transform smoke = burst.Smoke[layer];
                    if (smoke == null) continue;
                    smoke.position = position + right * (layer == 0 ? -.35f : .38f) +
                        up * (age * (.28f + layer * .08f)) - forward * (.22f + layer * .1f);
                    smoke.rotation = (camera != null ? camera.transform.rotation :
                        Quaternion.identity) * Quaternion.Euler(0, 0,
                        layer * 110f + age * (layer == 0 ? -9f : 11f));
                    smoke.localScale = Vector3.one * (2.0f + age * (1.6f + layer * .3f));
                    TintSprite(burst.SmokeRenderers[layer], new Color(.77f, .82f, .9f,
                        smokeFade * (layer == 0 ? .5f : .34f)));
                }
                for (int fragment = 0; fragment < burst.Fragments.Length; fragment++)
                {
                    LineRenderer shard = burst.Fragments[fragment];
                    if (shard == null) continue;
                    float angle = fragment * Mathf.PI * 2 / burst.Fragments.Length + .17f;
                    Vector3 direction = (right * Mathf.Cos(angle) + up * Mathf.Sin(angle) +
                        forward * (fragment % 3 - 1) * .24f).normalized;
                    float travel = .2f + age * (1.8f + fragment % 4 * .38f);
                    float sparkFade = Mathf.Clamp01((2.1f - age) / 1.6f);
                    shard.SetPosition(0, position + direction * travel);
                    shard.SetPosition(1, position + direction *
                        (travel + (.25f + fragment % 3 * .12f) * sparkFade));
                    Color shardColor = new Color(1f, .69f, .32f, sparkFade * .9f);
                    shard.startColor = shardColor; shardColor.a = 0;
                    shard.endColor = shardColor;
                    shard.widthMultiplier = .11f * sparkFade;
                }
            }
        }

        /// <summary>Set one shared explosion sprite's tint without cloning its material.</summary>
        private void TintSprite(Renderer renderer, Color tint)
        {
            if (renderer == null) return;
            spriteProperties.Clear();
            spriteProperties.SetColor("_Tint", tint);
            renderer.SetPropertyBlock(spriteProperties);
        }

        /// <summary>Animate one authoritative mining beam without changing the other phase.</summary>
        private void TickMiningVisual(MiningVisual visual)
        {
            MiningActivity activity = visual.Activity;
            if (activity != null && visual.Halo != null && visual.Core != null && visual.Contact != null)
            {
                float pulse = .65f + .35f * Mathf.Sin(Time.unscaledTime * 18);
                float progress = activity.Progress(Stopwatch.GetTimestamp());
                Vector3 origin = player.position + player.forward * .3f;
                Vector3 oreSurface = visual.Point + (origin - visual.Point).normalized * .65f;
                visual.Halo.SetPosition(0, origin); visual.Halo.SetPosition(1, oreSurface);
                visual.Core.SetPosition(0, origin); visual.Core.SetPosition(1, oreSurface);
                visual.Halo.enabled = visual.Core.enabled = true;
                Color tint = activity.Phase == MiningPhase.Prospect ?
                    new Color(.35f, .95f, .9f) : new Color(1f, .72f, .32f);
                tint.a = .3f + .2f * pulse;
                visual.Halo.startColor = visual.Halo.endColor = tint;
                visual.Halo.widthMultiplier = .08f + .025f * pulse;
                Color core = Color.white; core.a = .65f + .25f * pulse;
                visual.Core.startColor = visual.Core.endColor = core;
                Camera camera = ViewCamera();
                Vector3 right = camera != null ? camera.transform.right : Vector3.right;
                Vector3 up = camera != null ? camera.transform.up : Vector3.up;
                float contactRadius = activity.Phase == MiningPhase.Prospect ? .35f : .27f;
                visual.Contact.SetPosition(0, oreSurface + right * contactRadius);
                visual.Contact.SetPosition(1, oreSurface + up * contactRadius);
                visual.Contact.SetPosition(2, oreSurface - right * contactRadius);
                visual.Contact.SetPosition(3, oreSurface - up * contactRadius);
                visual.Contact.SetPosition(4, oreSurface + right * contactRadius);
                visual.Contact.enabled = true;
                tint.a = .8f * pulse;
                visual.Contact.startColor = visual.Contact.endColor = tint;
                visual.Contact.widthMultiplier = .035f + .015f * pulse;
                if (visual.Orbit != null)
                {
                    visual.Orbit.enabled = activity.Phase == MiningPhase.Prospect;
                    if (visual.Orbit.enabled)
                    {
                        float radius = .42f + progress * .45f;
                        for (int point = 0; point < 33; point++)
                        {
                            float angle = point * Mathf.PI * 2 / 32 + Time.unscaledTime * 1.7f;
                            visual.Orbit.SetPosition(point, oreSurface + right * Mathf.Cos(angle) * radius +
                                up * Mathf.Sin(angle) * radius * .55f);
                        }
                        Color scan = tint; scan.a = .32f * (1f - progress * .5f);
                        visual.Orbit.startColor = visual.Orbit.endColor = scan;
                    }
                }
                for (int index = 0; index < visual.Packets.Length; index++)
                {
                    LineRenderer packet = visual.Packets[index];
                    if (packet == null) continue;
                    float along = Mathf.Repeat(Time.unscaledTime * .65f + index * .25f, 1f);
                    if (activity.Phase == MiningPhase.Tractor) along = 1f - along;
                    Vector3 center = Vector3.Lerp(origin, oreSurface, along);
                    Vector3 travel = (oreSurface - origin).normalized * .045f;
                    packet.SetPosition(0, center - travel);
                    packet.SetPosition(1, center + travel);
                    Color signal = tint; signal.a = .38f + .32f * pulse;
                    packet.startColor = packet.endColor = signal;
                    packet.enabled = true;
                }
            }
        }

        /// <summary>Show only the server-active recipient Scan Boost field on this ship.</summary>
        private void SetPeerBuff(DeviceBuffView buff, bool docked)
        {
            if (docked || buff == null || !buff.VisualActive)
            { ClearPeerBuff(); return; }
            if (peerHalo != null && peerEffectId == buff.VisualEffectId &&
                peerDescriptor == buff.VisualDescriptor) return;
            ClearPeerBuff();
            peerEffectId = buff.VisualEffectId;
            peerDescriptor = buff.VisualDescriptor;
            Color tint = new Color(.3f, .92f, 1f);
            peerHalo = Line("Recipient scan field", .024f, tint, 65);
            peerSweep = Line("Recipient scan sweep", .028f, tint, 2);
            foreach (LineRenderer line in new[] { peerHalo, peerSweep })
                if (line != null) { line.useWorldSpace = false; line.enabled = false; }
        }

        /// <summary>Rotate a low-opacity scan plane while the native recipient effect exists.</summary>
        private void TickPeerBuff()
        {
            if (peerHalo == null || peerSweep == null) return;
            float time = Time.unscaledTime;
            float pulse = .72f + .28f * Mathf.Sin(time * 5f);
            peerHalo.transform.position = peerSweep.transform.position = player.position;
            peerHalo.transform.rotation = peerSweep.transform.rotation = player.rotation;
            for (int index = 0; index < 65; index++)
            {
                float angle = index * Mathf.PI * 2f / 64f;
                peerHalo.SetPosition(index, new Vector3(Mathf.Cos(angle) * .68f,
                    Mathf.Sin(time * 2.1f + angle * 3f) * .025f, Mathf.Sin(angle) * .5f));
            }
            float sweepAngle = time * 1.9f;
            peerSweep.SetPosition(0, Vector3.zero);
            peerSweep.SetPosition(1, new Vector3(Mathf.Cos(sweepAngle) * .68f,
                .025f, Mathf.Sin(sweepAngle) * .5f));
            Color halo = new Color(.32f, .92f, 1f, .17f * pulse);
            Color sweep = new Color(.6f, 1f, 1f, .27f * pulse);
            peerHalo.startColor = peerHalo.endColor = halo;
            peerSweep.startColor = new Color(sweep.r, sweep.g, sweep.b, .08f);
            peerSweep.endColor = sweep;
            peerHalo.enabled = peerSweep.enabled = true;
        }

        /// <summary>Remove the recipient field on native expiry, docking or scene reset.</summary>
        private void ClearPeerBuff()
        {
            if (peerHalo != null) Object.Destroy(peerHalo.gameObject);
            if (peerSweep != null) Object.Destroy(peerSweep.gameObject);
            peerHalo = peerSweep = null;
            peerEffectId = 0;
            peerDescriptor = 0;
        }

        /// <summary>Bind each native recipient recharge effect to a separate compact ship ring.</summary>
        private void SetResourceBuff(int slot, DeviceBuffView buff, bool docked)
        {
            if (docked || buff == null || !buff.VisualActive)
            { ClearResourceBuff(slot); return; }
            if (recipientRings[slot] != null && recipientEffectIds[slot] == buff.VisualEffectId)
                return;
            ClearResourceBuff(slot);
            recipientEffectIds[slot] = buff.VisualEffectId;
            Color tint = slot == 0 ? new Color(.4f, .77f, 1f) : new Color(.4f, 1f, .69f);
            recipientRings[slot] = Line(slot == 0 ? "Recipient shield recharge" :
                "Recipient reactor recharge", .023f, tint, 49);
            recipientRings[slot].useWorldSpace = false;
            recipientRings[slot].enabled = false;
        }

        /// <summary>Keep the two server-owned recharge visuals distinct and inside the ship silhouette.</summary>
        private void TickResourceBuffs()
        {
            for (int slot = 0; slot < recipientRings.Length; slot++)
            {
                LineRenderer ring = recipientRings[slot];
                if (ring == null) continue;
                float time = Time.unscaledTime;
                ring.transform.position = player.position;
                ring.transform.rotation = player.rotation * Quaternion.Euler(
                    slot == 0 ? 25f : -25f, time * (slot == 0 ? 24f : -21f), 0);
                float radius = slot == 0 ? .63f : .79f;
                for (int point = 0; point < 49; point++)
                {
                    float angle = point * Mathf.PI * 2f / 48f;
                    ring.SetPosition(point, new Vector3(Mathf.Cos(angle) * radius,
                        Mathf.Sin(angle) * radius * .48f, 0));
                }
                Color tint = slot == 0 ? new Color(.4f, .77f, 1f) : new Color(.4f, 1f, .69f);
                tint.a = .18f + .06f * Mathf.Sin(time * 4f + slot);
                ring.startColor = ring.endColor = tint;
                ring.enabled = true;
            }
        }

        /// <summary>Remove only the expired recipient recharge visual.</summary>
        private void ClearResourceBuff(int slot)
        {
            if (recipientRings[slot] != null) Object.Destroy(recipientRings[slot].gameObject);
            recipientRings[slot] = null;
            recipientEffectIds[slot] = 0;
        }

        /// <summary>Display a ship-local field only while the native device visual is active.</summary>
        private void SetDeviceBuff(DeviceBuffView buff, bool docked)
        {
            if (docked || buff == null || !buff.VisualActive || buff.Name.Contains("Turbo"))
            { ClearDeviceBuff(); return; }
            if (deviceHalo != null && deviceEffectId == buff.VisualEffectId &&
                deviceDescriptor == buff.VisualDescriptor) return;
            ClearDeviceBuff();
            deviceEffectId = buff.VisualEffectId;
            deviceDescriptor = buff.VisualDescriptor;
            deviceTint = DeviceColor(buff.Name);
            deviceHalo = Line("Active device field", .035f, deviceTint, 49);
            deviceTrace = Line("Active device trace", .055f, deviceTint, 17);
            foreach (LineRenderer line in new[] { deviceHalo, deviceTrace })
                if (line != null)
                {
                    line.useWorldSpace = false;
                    line.enabled = false;
                }
        }

        /// <returns>Color: A readable field tint for the server-named active device.</returns>
        private static Color DeviceColor(string name)
        {
            if (name.Contains("Turbo")) return new Color(1f, .61f, .25f);
            if (name.Contains("Shield")) return new Color(.42f, .86f, 1f);
            if (name.Contains("Reactor")) return new Color(.48f, 1f, .74f);
            if (name.Contains("Deflect")) return new Color(.72f, .62f, 1f);
            return new Color(.55f, .86f, 1f);
        }

        /// <summary>Pulse a restrained orbital trace around the own ship without altering its pose.</summary>
        private void TickDeviceBuff()
        {
            if (deviceHalo == null || deviceTrace == null) return;
            float time = Time.unscaledTime;
            float pulse = .7f + .3f * Mathf.Sin(time * 4.5f);
            deviceHalo.transform.position = player.position;
            deviceTrace.transform.position = player.position;
            deviceHalo.transform.rotation = player.rotation * Quaternion.Euler(22, time * 12f, 0);
            deviceTrace.transform.rotation = player.rotation * Quaternion.Euler(-22, -time * 18f, 0);
            for (int index = 0; index < 49; index++)
            {
                float angle = index * Mathf.PI * 2 / 48;
                deviceHalo.SetPosition(index, new Vector3(Mathf.Cos(angle) * .9f,
                    Mathf.Sin(angle) * .55f, 0));
            }
            for (int index = 0; index < 17; index++)
            {
                float angle = (time * 1.7f + index / 16f * .68f) * Mathf.PI * 2;
                deviceTrace.SetPosition(index, new Vector3(Mathf.Cos(angle) * .94f,
                    Mathf.Sin(angle) * .58f, 0));
            }
            Color halo = deviceTint; halo.a = .22f * pulse;
            Color trace = deviceTint; trace.a = .6f * pulse;
            deviceHalo.startColor = deviceHalo.endColor = halo;
            deviceTrace.startColor = deviceTrace.endColor = trace;
            deviceHalo.enabled = deviceTrace.enabled = true;
        }

        /// <summary>Remove a native device field when its buff or visual ends.</summary>
        private void ClearDeviceBuff()
        {
            if (deviceHalo != null) Object.Destroy(deviceHalo.gameObject);
            if (deviceTrace != null) Object.Destroy(deviceTrace.gameObject);
            deviceHalo = deviceTrace = null;
            deviceEffectId = 0;
            deviceDescriptor = 0;
        }

        /// <summary>Animate only a server-started wreck transfer, fading when its timer completes.</summary>
        private void TickLoot()
        {
            if (lootActivity == null || lootHalo == null || lootCore == null ||
                lootFilament == null || lootRing == null) return;
            float progress = lootActivity.Progress(Stopwatch.GetTimestamp());
            float pulse = .7f + .3f * Mathf.Sin(Time.unscaledTime * 14f);
            Vector3 origin = player.position + player.forward * .3f;
            lootHalo.SetPosition(0, lootPoint); lootHalo.SetPosition(1, origin);
            lootCore.SetPosition(0, lootPoint); lootCore.SetPosition(1, origin);
            lootHalo.enabled = lootCore.enabled = true;
            Color amber = new Color(1f, .55f, .2f, (.28f + .12f * pulse) * (1f - progress * .5f));
            lootHalo.startColor = lootHalo.endColor = amber;
            Color core = new Color(1f, .93f, .72f, .65f * (1f - progress * .4f));
            lootCore.startColor = lootCore.endColor = core;
            Vector3 axis = (origin - lootPoint).normalized;
            Camera camera = ViewCamera();
            Vector3 tangent = Vector3.Cross(axis, camera != null ? camera.transform.forward : Vector3.up).normalized;
            if (tangent.sqrMagnitude < .01f) tangent = Vector3.right;
            for (int index = 0; index < 16; index++)
            {
                float along = index / 15f;
                float wave = Mathf.Sin(along * Mathf.PI * 5f - Time.unscaledTime * 12f) *
                    Mathf.Sin(along * Mathf.PI) * .13f;
                lootFilament.SetPosition(index, Vector3.Lerp(lootPoint, origin, along) + tangent * wave);
            }
            lootFilament.enabled = true;
            amber.a = .75f;
            lootFilament.startColor = lootFilament.endColor = amber;
            lootRing.transform.position = lootPoint;
            if (camera != null) lootRing.transform.rotation = camera.transform.rotation;
            float radius = .38f + progress * .5f + .06f * pulse;
            for (int index = 0; index < 33; index++)
            {
                float angle = index * Mathf.PI * 2 / 32;
                lootRing.SetPosition(index, new Vector3(Mathf.Cos(angle) * radius,
                    Mathf.Sin(angle) * radius, 0));
            }
            lootRing.enabled = true;
            lootRing.startColor = lootRing.endColor = amber;
        }

        /// <summary>Follow only the own-ship warp phase decoded from the native server event.</summary>
        private void SetWarpPhase(WarpPhase phase, bool arrived)
        {
            if (warpPhase == phase && warpArrived == arrived) return;
            warpPhase = phase; warpArrived = arrived; warpChangedAt = Time.unscaledTime;
            if (phase == WarpPhase.Idle)
            { ClearWarp(); return; }
            if (phase == WarpPhase.Charging || phase == WarpPhase.Traveling)
            {
                for (int index = beams.Count - 1; index >= 0; index--) RemoveBeam(index);
                for (int index = shieldPreviews.Count - 1; index >= 0; index--)
                    RemoveShieldPreview(index);
                for (int index = impacts.Count - 1; index >= 0; index--) RemoveImpact(index);
                ClearMining();
                ClearLoot();
            }
            for (int index = 0; index < warpStreaks.Length; index++)
                if (warpStreaks[index] == null)
                {
                    warpStreaks[index] = Line("Warp velocity streak", .045f,
                        new Color(.45f, .84f, 1f, .4f), 8);
                    if (warpStreaks[index] != null)
                    {
                        warpStreaks[index].widthCurve = new AnimationCurve(
                            new Keyframe(0, .05f), new Keyframe(.35f, 1f),
                            new Keyframe(1, .1f));
                        warpStreaks[index].enabled = false;
                    }
                }
            TickWarp();
        }

        /// <summary>Curve phase-specific star trails through the distorted warp view.</summary>
        private void TickWarp()
        {
            if (warpPhase == WarpPhase.Idle) return;
            Camera camera = ViewCamera();
            if (camera == null) return;
            if (camera.orthographic)
            {
                foreach (LineRenderer streak in warpStreaks)
                    if (streak != null) streak.enabled = false;
                return;
            }
            float age = Time.unscaledTime - warpChangedAt;
            float fade = warpPhase == WarpPhase.Charging ? Mathf.Clamp01(age / 1.2f) :
                warpPhase == WarpPhase.Recovering ? 1 - Mathf.Clamp01(age / 2f) : 1;
            float targetFieldOfView = warpPhase == WarpPhase.Traveling ? normalFieldOfView + 11f :
                warpPhase == WarpPhase.Charging ? normalFieldOfView + 4f : normalFieldOfView;
            camera.fieldOfView = Mathf.Lerp(camera.fieldOfView, targetFieldOfView,
                1 - Mathf.Exp(-5f * Time.unscaledDeltaTime));
            for (int index = 0; index < warpStreaks.Length; index++)
            {
                LineRenderer streak = warpStreaks[index];
                if (streak == null) continue;
                float angle = index * 2.39996f;
                float radius = .085f + (index % 7) * .036f;
                float rate = warpPhase == WarpPhase.Traveling ? .8f : .28f;
                float drift = Mathf.Repeat(Time.unscaledTime * rate + index * .217f, 1f) * .19f;
                float length = warpPhase == WarpPhase.Traveling ? .24f :
                    warpPhase == WarpPhase.Charging ? .055f : .1f;
                for (int point = 0; point < 8; point++)
                {
                    float progress = point / 7f;
                    float twist = fade * progress * progress *
                        (.07f * Mathf.Sin(age * 1.7f + index * .83f) +
                        .025f * Mathf.Sin(progress * 6f + index));
                    float reach = radius + drift + length * progress;
                    float heading = angle + twist;
                    streak.SetPosition(point, camera.ViewportToWorldPoint(new Vector3(
                        .5f + Mathf.Cos(heading) * reach,
                        .5f + Mathf.Sin(heading) * reach, 8f)));
                }
                streak.enabled = true;
                Color color = warpArrived ? new Color(1f, .74f, .34f) : new Color(.44f, .83f, 1f);
                color.a = .22f * fade;
                streak.startColor = streak.endColor = color;
                Color tip = color; tip.a = .78f * fade;
                streak.endColor = tip;
                streak.widthMultiplier = (warpPhase == WarpPhase.Traveling ? .07f : .045f) * fade;
            }
        }

        /// <summary>Draw tapered engine wakes from authoritative speed and warp phase.</summary>
        private void TickDrive()
        {
            bool active = driveSpeed > 12f || warpPhase == WarpPhase.Charging ||
                warpPhase == WarpPhase.Traveling || warpPhase == WarpPhase.Recovering;
            float speed = Mathf.Clamp01(driveSpeed / 220f);
            float warp = warpPhase == WarpPhase.Traveling ? 1f :
                warpPhase == WarpPhase.Charging ? .48f :
                warpPhase == WarpPhase.Recovering ? .32f : 0f;
            float pulse = .9f + .1f * Mathf.Sin(Time.unscaledTime * (warp > 0 ? 19f : 9f));
            float length = (.22f + speed * .18f + warp * .48f) * pulse;
            for (int side = 0; side < 2; side++)
            {
                LineRenderer halo = driveHalos[side], core = driveCores[side], fin = driveFins[side];
                if (halo == null || core == null || fin == null) continue;
                halo.enabled = core.enabled = active;
                fin.enabled = active && warp > 0;
                if (!active) continue;
                float across = side == 0 ? -.38f : .38f;
                Vector3 nozzle = player.TransformPoint(new Vector3(across, .04f, -2.48f));
                Vector3 rear = -player.forward;
                Vector3 drift = player.right * ((side == 0 ? -1f : 1f) * .06f);
                for (int point = 0; point < 4; point++)
                {
                    float along = point / 3f;
                    Vector3 position = nozzle + rear * (length * along) + drift * along;
                    halo.SetPosition(point, position);
                    core.SetPosition(point, position);
                }
                Color plume = warp > 0 ? new Color(.43f, .62f, 1f, .13f + warp * .12f) :
                    new Color(.24f, .78f, 1f, .16f + speed * .09f);
                halo.startColor = plume; plume.a *= .15f; halo.endColor = plume;
                Color light = warp > 0 ? new Color(.76f, .84f, 1f, .56f) :
                    new Color(.7f, .95f, 1f, .48f);
                core.startColor = light; light.a = .03f; core.endColor = light;
                halo.widthMultiplier = .075f + warp * .045f;
                core.widthMultiplier = .018f + warp * .008f;
                if (!fin.enabled) continue;
                Vector3 tip = nozzle + rear * (length * 1.2f) + drift * 3f;
                fin.SetPosition(0, nozzle + player.up * .07f);
                fin.SetPosition(1, nozzle + rear * (length * .45f) + drift * 1.5f);
                fin.SetPosition(2, tip);
                Color shear = new Color(.47f, .77f, 1f, .12f * warp);
                fin.startColor = shear; shear.a = 0; fin.endColor = shear;
                fin.widthMultiplier = .018f + warp * .014f;
            }
        }

        /// <summary>Remove travel streaks when the server returns to idle or the scene resets.</summary>
        private void ClearWarp()
        {
            if (effectCamera != null) effectCamera.fieldOfView = normalFieldOfView;
            for (int index = 0; index < warpStreaks.Length; index++)
            {
                if (warpStreaks[index] != null) Object.Destroy(warpStreaks[index].gameObject);
                warpStreaks[index] = null;
            }
        }

        /// <summary>Remove both independent mining visuals when leaving the sector or warping.</summary>
        private void ClearMining()
        {
            ClearMiningVisual(prospectVisual);
            ClearMiningVisual(tractorVisual);
        }

        /// <summary>Remove one native phase without erasing its overlapping companion.</summary>
        private void ClearMiningVisual(MiningVisual visual)
        {
            foreach (LineRenderer line in new[] { visual.Halo, visual.Core, visual.Contact, visual.Orbit })
                if (line != null) Object.Destroy(line.gameObject);
            foreach (LineRenderer packet in visual.Packets)
                if (packet != null) Object.Destroy(packet.gameObject);
            System.Array.Clear(visual.Packets, 0, visual.Packets.Length);
            visual.Halo = visual.Core = visual.Contact = visual.Orbit = null;
            visual.Activity = null;
        }

        /// <summary>Remove a wreck tractor as soon as its native transfer ends.</summary>
        private void ClearLoot()
        {
            foreach (LineRenderer line in new[] { lootHalo, lootCore, lootFilament, lootRing })
                if (line != null) Object.Destroy(line.gameObject);
            lootHalo = lootCore = lootFilament = lootRing = null;
            lootActivity = null;
        }

        /// <summary>Release one shot body and let its last exhaust particles disperse.</summary>
        private void RemoveBeam(int index)
        {
            Beam shot = beams[index];
            Object.Destroy(shot.Halo.gameObject);
            Object.Destroy(shot.Core.gameObject);
            if (shot.Exhaust != null)
            {
                shot.Exhaust.transform.SetParent(parent, true);
                shot.Exhaust.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                Object.Destroy(shot.Exhaust.gameObject, 1f);
                lingeringExhaust.Add(shot.Exhaust);
            }
            if (shot.Body != null) Object.Destroy(shot.Body.gameObject);
            beams.RemoveAt(index);
        }

        /// <summary>Release the fire, smoke, and light of one completed missile hit.</summary>
        private void RemoveDetonation(int index)
        {
            Detonation burst = detonations[index];
            if (burst.Fire != null) Object.Destroy(burst.Fire.gameObject);
            if (burst.Smoke != null) Object.Destroy(burst.Smoke.gameObject);
            if (burst.Flash != null) Object.Destroy(burst.Flash.gameObject);
            detonations.RemoveAt(index);
        }

        /// <summary>Destroy one expired impact ring.</summary>
        private void RemoveImpact(int index)
        {
            if (impacts[index].Flash != null) Object.Destroy(impacts[index].Flash.gameObject);
            impacts[index].ShieldVisual?.Dispose();
            Object.Destroy(impacts[index].Ring.gameObject); impacts.RemoveAt(index);
        }

        /// <summary>Release one early beam contact after it ends or is ruled a miss.</summary>
        private void RemoveShieldPreview(int index)
        {
            shieldPreviews[index].Visual.Dispose();
            shieldPreviews.RemoveAt(index);
        }

        /// <summary>Release every scene object created for one confirmed breakup.</summary>
        private void RemoveDestruction(int index)
        {
            Destruction burst = destructions[index];
            foreach (LineRenderer fragment in burst.Fragments)
                if (fragment != null) Object.Destroy(fragment.gameObject);
            foreach (Transform sprite in burst.Fire)
                if (sprite != null) Object.Destroy(sprite.gameObject);
            foreach (Transform sprite in burst.Smoke)
                if (sprite != null) Object.Destroy(sprite.gameObject);
            if (burst.Flash != null) Object.Destroy(burst.Flash.gameObject);
            if (burst.Ring != null) Object.Destroy(burst.Ring.gameObject);
            if (burst.InnerRing != null) Object.Destroy(burst.InnerRing.gameObject);
            destructions.RemoveAt(index);
        }

        /// <summary>Clear effects on sector travel or logout.</summary>
        public void Clear()
        {
            for (int index = beams.Count - 1; index >= 0; index--) RemoveBeam(index);
            for (int index = shieldPreviews.Count - 1; index >= 0; index--)
                RemoveShieldPreview(index);
            foreach (SapVisualEvent effect in sapVisuals) effect.Visual.Dispose();
            sapVisuals.Clear();
            foreach (ParticleSystem exhaust in lingeringExhaust)
                if (exhaust != null) Object.Destroy(exhaust.gameObject);
            lingeringExhaust.Clear();
            for (int index = impacts.Count - 1; index >= 0; index--) RemoveImpact(index);
            for (int index = detonations.Count - 1; index >= 0; index--) RemoveDetonation(index);
            for (int index = destructions.Count - 1; index >= 0; index--) RemoveDestruction(index);
            mobPositions.Clear(); mobSeenAt.Clear(); recentMobDamage.Clear();
            actorHulls.Clear(); ownHullKnown = false; lastOwnHull = 0;
            knownWrecks.Clear(); wreckBaseline = false;
            ClearMining();
            ClearLoot();
            ClearDeviceBuff();
            turboEnergy?.Dispose(); turboEnergy = null;
            shieldBurnEnergy?.Dispose(); shieldBurnEnergy = null;
            shieldBurnTargetId = 0;
            ClearPeerBuff();
            ClearResourceBuff(0);
            ClearResourceBuff(1);
            ClearRallyBuff();
            ClearCombatTrance();
            ClearEnvironmentShield();
            ClearPsionicShield();
            ClearShieldCharging();
            ClearHackingBeams();
            ClearMenaceVisuals();
            ClearBefriendVisuals();
            ClearRechargeVisuals();
            ClearInversionVisual();
            ClearSelfDestructVisual();
            ClearFoldVisuals();
            ClearWormholeVisual();
            ClearWarp(); warpPhase = WarpPhase.Idle; warpArrived = false;
            driveSpeed = 0;
            for (int side = 0; side < 2; side++)
                foreach (LineRenderer line in new[] { driveHalos[side], driveCores[side], driveFins[side] })
                    if (line != null) line.enabled = false;
            reportedShields.Clear();
            loadout.Reset();
            lastShotSequence = lastDamageSequence = 0; playerId = 0;
        }

        /// <summary>Release the packaged material after all effects are gone.</summary>
        public void Dispose()
        {
            Clear();
            for (int side = 0; side < 2; side++)
                foreach (LineRenderer line in new[] { driveHalos[side], driveCores[side], driveFins[side] })
                    if (line != null) Object.Destroy(line.gameObject);
            if (material != null) Object.Destroy(material);
            if (impactMaterial != null) Object.Destroy(impactMaterial);
            if (fireMaterial != null) Object.Destroy(fireMaterial);
            if (smokeMaterial != null) Object.Destroy(smokeMaterial);
            if (exhaustMaterial != null) Object.Destroy(exhaustMaterial);
            if (missileHullMaterial != null) Object.Destroy(missileHullMaterial);
            if (missileFinMaterial != null) Object.Destroy(missileFinMaterial);
            if (projectileMaterial != null) Object.Destroy(projectileMaterial);
            if (nozzleMaterial != null) Object.Destroy(nozzleMaterial);
            loadout.Dispose();
        }
    }
}
