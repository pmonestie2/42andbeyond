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
            public readonly Color Color;
            public readonly bool Critical;
            public readonly float Started;

            /// <summary>Track a ring spawned only by a confirmed server damage event.</summary>
            public Impact(uint target, LineRenderer ring, Color color, bool critical)
            { Target = target; Ring = ring; Color = color; Critical = critical; Started = Time.unscaledTime; }
        }

        private readonly List<Beam> beams = new List<Beam>();
        private readonly List<Impact> impacts = new List<Impact>();
        private readonly Transform parent, player;
        private readonly RemoteShips ships;
        private readonly Material material;
        private Camera effectCamera;
        private LineRenderer miningHalo, miningCore, miningRing;
        private MiningActivity miningActivity;
        private Vector3 miningPoint;
        private LineRenderer lootHalo, lootCore, lootFilament, lootRing;
        private LootActivity lootActivity;
        private Vector3 lootPoint;
        private readonly LineRenderer[] warpStreaks = new LineRenderer[28];
        private WarpPhase warpPhase;
        private bool warpArrived;
        private float warpChangedAt;
        private long lastShotSequence, lastDamageSequence;
        private uint playerId;

        /// <summary>Load an explicitly packaged beam shader for the standalone player.</summary>
        public CombatEffects(Transform parent, Transform player, RemoteShips ships)
        {
            this.parent = parent; this.player = player; this.ships = ships;
            Shader shader = Resources.Load<Shader>("BeamAdditive");
            if (shader != null) material = new Material(shader);
        }

        /// <returns>LineRenderer: A scene-owned luminous line, or null if the shader is unavailable.</returns>
        private LineRenderer Line(string name, float width, Color color, int vertices)
        {
            if (material == null) return null;
            var model = new GameObject(name, typeof(LineRenderer));
            model.transform.SetParent(parent, false);
            LineRenderer line = model.GetComponent<LineRenderer>();
            line.sharedMaterial = material; line.positionCount = vertices;
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
                Color tint = critical ? new Color(1f, .82f, .32f) :
                    damage.Target == playerId ? new Color(1f, .42f, .2f) :
                    new Color(.38f, .9f, 1f);
                LineRenderer ring = Line("Confirmed impact", .035f, tint, 25);
                if (ring == null) continue;
                ring.useWorldSpace = false;
                impacts.Add(new Impact(damage.Target, ring, tint, critical));
                if (impacts.Count > 24) RemoveImpact(0);
            }
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
                miningRing = Line("Ore lock", .035f, tint, 25);
                if (miningRing != null) miningRing.useWorldSpace = false;
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
                effectCamera = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
            return effectCamera;
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
                impact.Ring.transform.position = position;
                Camera camera = ViewCamera();
                if (camera != null) impact.Ring.transform.rotation = camera.transform.rotation;
                float radius = .12f + age * (impact.Critical ? 2f : 1.4f);
                for (int point = 0; point < 25; point++)
                {
                    float angle = point * Mathf.PI * 2 / 24;
                    impact.Ring.SetPosition(point, new Vector3(Mathf.Cos(angle) * radius,
                        Mathf.Sin(angle) * radius, 0));
                }
                Color color = impact.Color; color.a = (impact.Critical ? 1f : .85f) * (1 - age / .48f);
                impact.Ring.startColor = impact.Ring.endColor = color;
                impact.Ring.widthMultiplier = (impact.Critical ? .07f : .04f) * (1 - age / .48f);
            }
            if (miningActivity != null && miningHalo != null && miningCore != null && miningRing != null)
            {
                float progress = miningActivity.Progress(Stopwatch.GetTimestamp());
                float pulse = .65f + .35f * Mathf.Sin(Time.unscaledTime * 18);
                Vector3 origin = player.position + player.forward * .3f;
                miningHalo.SetPosition(0, origin); miningHalo.SetPosition(1, miningPoint);
                miningCore.SetPosition(0, origin); miningCore.SetPosition(1, miningPoint);
                Color tint = miningActivity.Phase == MiningPhase.Prospect ?
                    new Color(.35f, .95f, .9f) : new Color(1f, .72f, .32f);
                tint.a = .3f + .2f * pulse;
                miningHalo.startColor = miningHalo.endColor = tint;
                miningHalo.widthMultiplier = .1f + .035f * pulse;
                Color core = Color.white; core.a = .65f + .25f * pulse;
                miningCore.startColor = miningCore.endColor = core;
                miningRing.transform.position = miningPoint;
                Camera camera = ViewCamera();
                if (camera != null) miningRing.transform.rotation = camera.transform.rotation;
                float radius = .45f + progress * .7f;
                for (int point = 0; point < 25; point++)
                {
                    float angle = point * Mathf.PI * 2 / 24;
                    miningRing.SetPosition(point, new Vector3(Mathf.Cos(angle) * radius,
                        Mathf.Sin(angle) * radius, 0));
                }
                tint.a = .75f;
                miningRing.startColor = miningRing.endColor = tint;
            }
            TickLoot();
            TickWarp();
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
            float age = Time.unscaledTime - warpChangedAt;
            float fade = warpPhase == WarpPhase.Charging ? Mathf.Clamp01(age / 1.2f) :
                warpPhase == WarpPhase.Recovering ? 1 - Mathf.Clamp01(age / 2f) : 1;
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
            for (int index = 0; index < warpStreaks.Length; index++)
            {
                if (warpStreaks[index] != null) Object.Destroy(warpStreaks[index].gameObject);
                warpStreaks[index] = null;
            }
        }

        /// <summary>Remove the prospecting and tractor visuals when the native phase ends.</summary>
        private void ClearMining()
        {
            if (miningHalo != null) Object.Destroy(miningHalo.gameObject);
            if (miningCore != null) Object.Destroy(miningCore.gameObject);
            if (miningRing != null) Object.Destroy(miningRing.gameObject);
            miningHalo = miningCore = miningRing = null;
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
            ClearWarp(); warpPhase = WarpPhase.Idle; warpArrived = false;
            lastShotSequence = lastDamageSequence = 0; playerId = 0;
        }

        /// <summary>Release the packaged material after all effects are gone.</summary>
        public void Dispose() { Clear(); if (material != null) Object.Destroy(material); }
    }
}
