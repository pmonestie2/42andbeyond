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
            public readonly float Started, Lifetime;

            /// <summary>Keep a two-layer shot and its server-derived expiry together.</summary>
            public Beam(uint source, uint target, LineRenderer halo, LineRenderer core, Color color, float lifetime)
            {
                Source = source; Target = target; Halo = halo; Core = core; Color = color;
                Started = Time.unscaledTime; Lifetime = lifetime;
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
        private LineRenderer miningHalo, miningCore, miningContact, miningScanOrbit;
        private readonly LineRenderer[] miningPackets = new LineRenderer[4];
        private MiningActivity miningActivity;
        private Vector3 miningPoint;
        private LineRenderer lootHalo, lootCore, lootFilament, lootRing;
        private LootActivity lootActivity;
        private Vector3 lootPoint;
        private readonly LineRenderer[] warpStreaks = new LineRenderer[28];
        private LineRenderer deviceHalo, deviceTrace;
        private uint deviceEffectId;
        private ushort deviceDescriptor;
        private Color deviceTint;
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
            loadout.Apply(snapshot);
            SetDeviceBuff(snapshot.DeviceBuff, snapshot.Station != null);
            foreach (ShotEvent shot in snapshot.Shots)
            {
                if (shot.Sequence <= lastShotSequence) continue;
                lastShotSequence = shot.Sequence;
                if (snapshot.WarpPhase != WarpPhase.Idle || !TryPoint(shot.Source, out _) ||
                    !TryPoint(shot.Target, out _)) continue;
                Color tint = shot.Source == playerId ? new Color(.3f, .86f, 1f) :
                    new Color(1f, .37f, .18f);
                LineRenderer halo = Line("Weapon corona", .14f, tint, 2);
                LineRenderer core = Line("Weapon core", .025f, Color.white, 2);
                if (halo == null || core == null) continue;
                beams.Add(new Beam(shot.Source, shot.Target, halo, core, tint,
                    Mathf.Clamp(shot.Duration / 1000f, .22f, .9f)));
                if (beams.Count > 32) RemoveBeam(0);
            }
            foreach (DamageEvent damage in snapshot.Damages)
            {
                if (damage.Sequence <= lastDamageSequence) continue;
                lastDamageSequence = damage.Sequence;
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
            MiningActivity activity = snapshot.MiningActivity;
            MapObject resource = activity == null ? null : snapshot.Objects.FirstOrDefault(item =>
                item.Id == activity.TargetId && item.Kind == 38);
            if (resource == null || snapshot.Station != null || !snapshot.ResourceWindowOpen ||
                snapshot.WarpPhase != WarpPhase.Idle)
            { ClearMining(); return; }
            if (miningActivity == null || miningActivity.TargetId != activity.TargetId ||
                miningActivity.Phase != activity.Phase || miningActivity.EffectId != activity.EffectId)
            {
                ClearMining();
                Color tint = activity.Phase == MiningPhase.Prospect ? new Color(.35f, .95f, .9f) :
                    new Color(1f, .72f, .32f);
                miningHalo = Line("Mining field", .12f, tint, 2);
                miningCore = Line("Mining focus", .025f, Color.white, 2);
                miningContact = Line("Ore contact", .035f, tint, 5);
                miningScanOrbit = Line("Ore scan orbit", .025f, tint, 33);
                for (int index = 0; index < miningPackets.Length; index++)
                    miningPackets[index] = Line("Ore transfer pulse", .035f, tint, 2);
            }
            miningActivity = activity;
            miningPoint = player.position + SpaceClient.Convert(resource.Position) -
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
                if (age >= beam.Lifetime || !TryPoint(beam.Source, out Vector3 from) ||
                    !TryPoint(beam.Target, out Vector3 to))
                { RemoveBeam(index); continue; }
                float fade = 1 - age / beam.Lifetime;
                Vector3 direction = (to - from).normalized;
                Vector3 offset = direction * .18f;
                beam.Halo.SetPosition(0, from + offset); beam.Halo.SetPosition(1, to - offset);
                beam.Core.SetPosition(0, from + offset); beam.Core.SetPosition(1, to - offset);
                beam.Halo.widthMultiplier = .14f * fade;
                beam.Core.widthMultiplier = .025f * fade;
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
            if (miningActivity != null && miningHalo != null && miningCore != null && miningContact != null)
            {
                float pulse = .65f + .35f * Mathf.Sin(Time.unscaledTime * 18);
                float progress = miningActivity.Progress(Stopwatch.GetTimestamp());
                Vector3 origin = player.position + player.forward * .3f;
                Vector3 oreSurface = miningPoint + (origin - miningPoint).normalized * .65f;
                miningHalo.SetPosition(0, origin); miningHalo.SetPosition(1, oreSurface);
                miningCore.SetPosition(0, origin); miningCore.SetPosition(1, oreSurface);
                Color tint = miningActivity.Phase == MiningPhase.Prospect ?
                    new Color(.35f, .95f, .9f) : new Color(1f, .72f, .32f);
                tint.a = .3f + .2f * pulse;
                miningHalo.startColor = miningHalo.endColor = tint;
                miningHalo.widthMultiplier = .1f + .035f * pulse;
                Color core = Color.white; core.a = .65f + .25f * pulse;
                miningCore.startColor = miningCore.endColor = core;
                Camera camera = ViewCamera();
                Vector3 right = camera != null ? camera.transform.right : Vector3.right;
                Vector3 up = camera != null ? camera.transform.up : Vector3.up;
                float contactRadius = .12f;
                miningContact.SetPosition(0, oreSurface + right * contactRadius);
                miningContact.SetPosition(1, oreSurface + up * contactRadius);
                miningContact.SetPosition(2, oreSurface - right * contactRadius);
                miningContact.SetPosition(3, oreSurface - up * contactRadius);
                miningContact.SetPosition(4, oreSurface + right * contactRadius);
                tint.a = .8f * pulse;
                miningContact.startColor = miningContact.endColor = tint;
                if (miningScanOrbit != null)
                {
                    miningScanOrbit.enabled = miningActivity.Phase == MiningPhase.Prospect;
                    if (miningScanOrbit.enabled)
                    {
                        float radius = .23f + progress * .35f;
                        for (int point = 0; point < 33; point++)
                        {
                            float angle = point * Mathf.PI * 2 / 32 + Time.unscaledTime * 1.7f;
                            miningScanOrbit.SetPosition(point, oreSurface + right * Mathf.Cos(angle) * radius +
                                up * Mathf.Sin(angle) * radius * .55f);
                        }
                        Color scan = tint; scan.a = .32f * (1f - progress * .5f);
                        miningScanOrbit.startColor = miningScanOrbit.endColor = scan;
                    }
                }
                for (int index = 0; index < miningPackets.Length; index++)
                {
                    LineRenderer packet = miningPackets[index];
                    if (packet == null) continue;
                    float along = Mathf.Repeat(Time.unscaledTime * .65f + index * .25f, 1f);
                    if (miningActivity.Phase == MiningPhase.Tractor) along = 1f - along;
                    Vector3 center = Vector3.Lerp(origin, oreSurface, along);
                    Vector3 travel = (oreSurface - origin).normalized * .045f;
                    packet.SetPosition(0, center - travel);
                    packet.SetPosition(1, center + travel);
                    Color signal = tint; signal.a = .38f + .32f * pulse;
                    packet.startColor = packet.endColor = signal;
                    packet.enabled = true;
                }
            }
            TickLoot();
            TickWarp();
            TickDeviceBuff();
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

        /// <summary>Remove the prospecting and tractor visuals when the native phase ends.</summary>
        private void ClearMining()
        {
            foreach (LineRenderer line in new[] { miningHalo, miningCore, miningContact, miningScanOrbit })
                if (line != null) Object.Destroy(line.gameObject);
            foreach (LineRenderer packet in miningPackets)
                if (packet != null) Object.Destroy(packet.gameObject);
            System.Array.Clear(miningPackets, 0, miningPackets.Length);
            miningHalo = miningCore = miningContact = miningScanOrbit = null;
            miningActivity = null;
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
            ClearWarp(); warpPhase = WarpPhase.Idle; warpArrived = false;
            reportedShields.Clear();
            loadout.Reset();
            lastShotSequence = lastDamageSequence = 0; playerId = 0;
        }

        /// <summary>Release the packaged material after all effects are gone.</summary>
        public void Dispose()
        {
            Clear();
            if (material != null) Object.Destroy(material);
            if (impactMaterial != null) Object.Destroy(impactMaterial);
            loadout.Dispose();
        }
    }
}
