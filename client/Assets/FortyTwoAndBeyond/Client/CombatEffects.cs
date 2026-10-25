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

            /// <summary>Keep a two-layer shot and its server-derived expiry together.</summary>
            public Beam(uint source, uint target, LineRenderer halo, LineRenderer core, Color color,
                ShotTravelKind kind, Vector3 launchPoint, float lifetime)
            {
                Source = source; Target = target; Halo = halo; Core = core; Color = color;
                Kind = kind; LaunchPoint = launchPoint; Started = Time.unscaledTime;
                Lifetime = lifetime;
            }
        }

        private sealed class Impact
        {
            public readonly uint Target;
            public readonly LineRenderer Ring;
            public readonly LineRenderer ShieldArc;
            public readonly LineRenderer[] Sparks;
            public readonly Color Color;
            public readonly bool Critical;
            public readonly bool? Shielded;
            public readonly float Started;

            /// <summary>Track a ring spawned only by a confirmed server damage event.</summary>
            public Impact(uint target, LineRenderer ring, LineRenderer shieldArc, LineRenderer[] sparks,
                Color color, bool critical, bool? shielded)
            {
                Target = target; Ring = ring; ShieldArc = shieldArc; Sparks = sparks;
                Color = color; Critical = critical; Shielded = shielded; Started = Time.unscaledTime;
            }
        }

        private readonly List<Beam> beams = new List<Beam>();
        private readonly List<Impact> impacts = new List<Impact>();
        private readonly Dictionary<uint, float> reportedShields = new Dictionary<uint, float>();
        private readonly Transform parent, player;
        private readonly RemoteShips ships;
        private readonly ShipLoadoutVisuals loadout;
        private readonly Material material, impactMaterial;
        private Camera effectCamera;
        private float normalFieldOfView = 60f;
        private sealed class MiningVisual
        {
            public LineRenderer Halo, Core, Contact, Orbit;
            public readonly LineRenderer[] Packets = new LineRenderer[4];
            public MiningActivity Activity;
            public Vector3 Point;
        }
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
        private uint deviceEffectId;
        private ushort deviceDescriptor;
        private Color deviceTint;
        private LineRenderer peerHalo, peerSweep;
        private uint peerEffectId;
        private ushort peerDescriptor;
        private readonly LineRenderer[] recipientRings = new LineRenderer[2];
        private readonly uint[] recipientEffectIds = new uint[2];
        private WarpPhase warpPhase;
        private bool warpArrived;
        private float warpChangedAt;
        private long lastShotSequence, lastDamageSequence;
        private uint playerId;

        /// <summary>Load an explicitly packaged beam shader for the standalone player.</summary>
        public CombatEffects(Transform parent, Transform player, RemoteShips ships)
        {
            this.parent = parent; this.player = player; this.ships = ships;
            loadout = new ShipLoadoutVisuals(player);
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

        /// <summary>Spawn visuals only for new authoritative shot and damage events.</summary>
        public void Apply(Snapshot snapshot)
        {
            playerId = snapshot.PlayerId;
            driveSpeed = Mathf.Abs(snapshot.Speed);
            loadout.Apply(snapshot);
            SetDeviceBuff(snapshot.DeviceBuff, snapshot.Station != null);
            SetPeerBuff(snapshot.PeerBuff, snapshot.Station != null);
            SetResourceBuff(0, snapshot.ShieldBuff, snapshot.Station != null);
            SetResourceBuff(1, snapshot.ReactorBuff, snapshot.Station != null);
            foreach (ShotEvent shot in snapshot.Shots)
            {
                if (shot.Sequence <= lastShotSequence) continue;
                lastShotSequence = shot.Sequence;
                if (snapshot.WarpPhase != WarpPhase.Idle || !TryPoint(shot.Source, out Vector3 launch) ||
                    !TryPoint(shot.Target, out _)) continue;
                ShotTravelKind kind = ShotPresentation.Classify(shot, snapshot.PlayerId,
                    snapshot.Mounted, snapshot.Ammo);
                Color tint = shot.Source == playerId ? new Color(.3f, .86f, 1f) :
                    new Color(1f, .37f, .18f);
                if (kind == ShotTravelKind.Missile && shot.Source == playerId)
                    tint = new Color(1f, .72f, .33f);
                LineRenderer halo = Line(kind == ShotTravelKind.Beam ? "Weapon corona" :
                    "Traveling round corona", .14f, tint, 2);
                LineRenderer core = Line(kind == ShotTravelKind.Beam ? "Weapon core" :
                    "Traveling round core", .025f, Color.white, 2);
                if (halo == null || core == null) continue;
                beams.Add(new Beam(shot.Source, shot.Target, halo, core, tint, kind, launch,
                    kind == ShotTravelKind.Beam ? Mathf.Clamp(shot.Duration / 1000f, .22f, .9f) :
                    Mathf.Clamp(shot.Duration / 1000f, .12f, 65.5f)));
                if (beams.Count > 32) RemoveBeam(0);
            }
            foreach (DamageEvent damage in snapshot.Damages)
            {
                if (damage.Sequence <= lastDamageSequence) continue;
                lastDamageSequence = damage.Sequence;
                RetireTravelingShot(damage.Source, damage.Target);
                if (snapshot.WarpPhase != WarpPhase.Idle || !TryPoint(damage.Target, out _) ||
                    damage.Amount <= 0) continue;
                bool critical = damage.Outcome == DamageOutcome.Critical;
                bool? shielded = ShieldAt(snapshot, damage.Target);
                Color tint = shielded == true ? new Color(.32f, .86f, 1f) : DamageColor(damage);
                LineRenderer ring = Line(shielded == true ? "Confirmed shield impact" :
                    "Confirmed hull or system impact", .035f, tint, 25, true);
                if (ring == null) continue;
                ring.useWorldSpace = false;
                LineRenderer shieldArc = shielded == true ? Line("Shield envelope ripple", .045f, tint, 25, true) : null;
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
                impacts.Add(new Impact(damage.Target, ring, shieldArc, sparks, tint, critical, shielded));
                if (impacts.Count > 24) RemoveImpact(0);
            }
            reportedShields.Clear();
            reportedShields[snapshot.PlayerId] = snapshot.Shield;
            foreach (ShipActor actor in snapshot.Players.Concat(snapshot.Mobs))
                if (actor.Shield >= 0) reportedShields[actor.Id] = actor.Shield;
            SetWarpPhase(snapshot.WarpPhase, snapshot.WarpArrived);
            SetLootActivity(snapshot);
            SetMiningVisual(prospectVisual, snapshot.MiningProspect, snapshot);
            SetMiningVisual(tractorVisual, snapshot.MiningTractor, snapshot);
        }

        /// <summary>End the oldest matching traveling round on a server hit or miss.</summary>
        private void RetireTravelingShot(uint source, uint target)
        {
            for (int index = 0; index < beams.Count; index++)
                if (beams[index].Kind != ShotTravelKind.Beam && beams[index].Source == source &&
                    beams[index].Target == target)
                { RemoveBeam(index); return; }
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
                default: return damage.Target == playerId ? new Color(1f, .42f, .2f) :
                    new Color(.38f, .9f, 1f);
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

        /// <summary>Track moving endpoints and animate the server-confirmed effects.</summary>
        public void Tick()
        {
            for (int index = beams.Count - 1; index >= 0; index--)
            {
                Beam beam = beams[index];
                float age = Time.unscaledTime - beam.Started;
                if (age >= beam.Lifetime || !TryPoint(beam.Target, out Vector3 to) ||
                    beam.Kind == ShotTravelKind.Beam && !TryPoint(beam.Source, out _))
                { RemoveBeam(index); continue; }
                float fade = 1 - age / beam.Lifetime;
                Vector3 start, end;
                if (beam.Kind == ShotTravelKind.Beam)
                {
                    TryPoint(beam.Source, out Vector3 from);
                    Vector3 offset = (to - from).normalized * .18f;
                    start = from + offset; end = to - offset;
                }
                else
                {
                    float progress = Mathf.Clamp01(age / beam.Lifetime);
                    float tail = Mathf.Min(.2f, 1.1f / Mathf.Max(.01f,
                        Vector3.Distance(beam.LaunchPoint, to)));
                    start = Vector3.Lerp(beam.LaunchPoint, to, Mathf.Max(0, progress - tail));
                    end = Vector3.Lerp(beam.LaunchPoint, to, progress);
                }
                beam.Halo.SetPosition(0, start); beam.Halo.SetPosition(1, end);
                beam.Core.SetPosition(0, start); beam.Core.SetPosition(1, end);
                beam.Halo.widthMultiplier = (beam.Kind == ShotTravelKind.Beam ? .14f : .18f) * fade;
                beam.Core.widthMultiplier = (beam.Kind == ShotTravelKind.Beam ? .025f : .045f) * fade;
                Color corona = beam.Color; corona.a = .28f * fade;
                Color core = Color.white; core.a = .9f * fade;
                beam.Halo.startColor = beam.Halo.endColor = corona;
                beam.Core.startColor = beam.Core.endColor = core;
            }
            for (int index = impacts.Count - 1; index >= 0; index--)
            {
                Impact impact = impacts[index];
                float age = Time.unscaledTime - impact.Started;
                if (age >= .48f || !TryPoint(impact.Target, out Vector3 position))
                { RemoveImpact(index); continue; }
                Camera camera = ViewCamera();
                Vector3 facing = camera != null ? (camera.transform.position - position).normalized :
                    (player.position - position).normalized;
                bool ownImpact = impact.Target == playerId;
                impact.Ring.transform.position = ownImpact ? position : position + facing *
                    (impact.Shielded == true ? 1.05f : impact.Shielded == false ? .85f : .7f);
                if (camera != null) impact.Ring.transform.rotation = camera.transform.rotation;
                float radius = ownImpact ? .1f + age * .3f :
                    impact.Shielded == true ? .45f + age * 1.25f :
                    .12f + age * (impact.Critical ? 2f : 1.4f);
                for (int point = 0; point < 25; point++)
                {
                    float angle = point * Mathf.PI * 2 / 24;
                    impact.Ring.SetPosition(point, new Vector3(Mathf.Cos(angle) * radius,
                        Mathf.Sin(angle) * radius, 0));
                }
                Color color = impact.Color; color.a = (impact.Shielded == true ? .95f :
                    impact.Critical ? 1f : .85f) * (1 - age / .48f);
                impact.Ring.startColor = impact.Ring.endColor = color;
                impact.Ring.widthMultiplier = (impact.Shielded == true ? .055f :
                    impact.Critical ? .07f : .04f) * (1 - age / .48f);
                if (impact.ShieldArc != null)
                {
                    for (int point = 0; point < 25; point++)
                    {
                        float angle = point * Mathf.PI * 2 / 24;
                        impact.ShieldArc.SetPosition(point, new Vector3(Mathf.Cos(angle) * radius * 1.13f,
                            Mathf.Sin(angle) * radius * 1.13f, 0));
                    }
                    Color arc = impact.Color; arc.a = .58f * (1 - age / .48f);
                    impact.ShieldArc.startColor = impact.ShieldArc.endColor = arc;
                    impact.ShieldArc.widthMultiplier = .045f * (1 - age / .48f);
                }
                for (int spark = 0; spark < impact.Sparks.Length; spark++)
                {
                    LineRenderer ray = impact.Sparks[spark];
                    if (ray == null) continue;
                    float angle = (spark + .5f) * Mathf.PI * 2f / impact.Sparks.Length;
                    Vector3 direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
                    ray.SetPosition(0, direction * (radius + .08f));
                    ray.SetPosition(1, direction * (radius + .42f * (1 - age / .48f)));
                    Color flash = impact.Critical ? new Color(1f, .87f, .44f,
                        .9f * (1 - age / .48f)) : impact.Color;
                    if (!impact.Critical) flash.a = .72f * (1 - age / .48f);
                    ray.startColor = ray.endColor = flash;
                    ray.widthMultiplier = .055f * (1 - age / .48f);
                    ray.enabled = true;
                }
            }
            TickMiningVisual(prospectVisual);
            TickMiningVisual(tractorVisual);
            TickLoot();
            TickWarp();
            TickDrive();
            TickDeviceBuff();
            TickPeerBuff();
            TickResourceBuffs();
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
            if (docked || buff == null || !buff.VisualActive)
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
                for (int index = impacts.Count - 1; index >= 0; index--) RemoveImpact(index);
                ClearMining();
                ClearLoot();
            }
            for (int index = 0; index < warpStreaks.Length; index++)
                if (warpStreaks[index] == null)
                {
                    warpStreaks[index] = Line("Warp velocity streak", .045f,
                        new Color(.45f, .84f, 1f, .4f), 2);
                    if (warpStreaks[index] != null) warpStreaks[index].enabled = false;
                }
            TickWarp();
        }

        /// <summary>Animate phase-specific speed lines without changing authoritative movement.</summary>
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
                float radius = .21f + (index % 6) * .038f;
                float drift = Mathf.Repeat(Time.unscaledTime * .24f + index * .17f, .11f);
                float x = .5f + Mathf.Cos(angle) * (radius + drift);
                float y = .5f + Mathf.Sin(angle) * (radius + drift);
                float length = warpPhase == WarpPhase.Traveling ? .066f : .02f;
                Vector3 start = camera.ViewportToWorldPoint(new Vector3(x, y, 8f));
                Vector3 end = camera.ViewportToWorldPoint(new Vector3(
                    x + Mathf.Cos(angle) * length, y + Mathf.Sin(angle) * length, 8f));
                streak.SetPosition(0, start);
                streak.SetPosition(1, end);
                streak.enabled = true;
                Color color = warpArrived ? new Color(1f, .74f, .34f) : new Color(.44f, .83f, 1f);
                color.a = .72f * fade;
                streak.startColor = streak.endColor = color;
                streak.widthMultiplier = .045f * fade;
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

        /// <summary>Destroy one expired two-layer beam.</summary>
        private void RemoveBeam(int index)
        {
            Object.Destroy(beams[index].Halo.gameObject);
            Object.Destroy(beams[index].Core.gameObject);
            beams.RemoveAt(index);
        }

        /// <summary>Destroy one expired impact ring.</summary>
        private void RemoveImpact(int index)
        { Object.Destroy(impacts[index].Ring.gameObject); impacts.RemoveAt(index); }

        /// <summary>Clear effects on sector travel or logout.</summary>
        public void Clear()
        {
            for (int index = beams.Count - 1; index >= 0; index--) RemoveBeam(index);
            for (int index = impacts.Count - 1; index >= 0; index--) RemoveImpact(index);
            ClearMining();
            ClearLoot();
            ClearDeviceBuff();
            ClearPeerBuff();
            ClearResourceBuff(0);
            ClearResourceBuff(1);
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
            loadout.Dispose();
        }
    }
}
