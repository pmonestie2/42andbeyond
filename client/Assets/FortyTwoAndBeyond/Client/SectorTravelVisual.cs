using System;
using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Present approved sector transfers without modifying server-owned ship motion.</summary>
    public sealed class SectorTravelVisual : IDisposable
    {
        private readonly Transform parent, ship;
        private readonly Camera camera;
        private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        private NativeWormholeVisual portal;
        private Snapshot snapshot;
        private uint sector, effectId, cameraGate;
        private int pendingArrival, entryGeneration;
        private float began, duration, lastEffectAt, arrivalAt = -1;
        private Vector3 mouthWorld, direction;
        private Quaternion entryHeading;
        private Transform gate;
        private Renderer[] clipped = Array.Empty<Renderer>();
        private bool initialized, wormhole, arriving;
        public bool Active => effectId != 0 || cameraGate != 0 || arriving;

        /// <summary>Own transfer presentation independently of the sector's disposable scenery.</summary>
        public SectorTravelVisual(Transform parent, Transform ship, Camera camera)
        { this.parent = parent; this.ship = ship; this.camera = camera; }

        /// <summary>Consume only immutable native cast, camera and gate approval state.</summary>
        public void Observe(Snapshot next, IReadOnlyDictionary<uint, Transform> bodies)
        {
            snapshot = next;
            foreach (KeyValuePair<uint, Transform> body in bodies)
            {
                GateTransitVisual visual = body.Value.GetComponent<GateTransitVisual>();
                if (visual == null) continue;
                GateActivity activity = next.Gates.FirstOrDefault(item => item.Id == body.Key);
                bool arrivalGate = arriving && gate == body.Value;
                float age = activity == null ? 0 : (float)((System.Diagnostics.Stopwatch.GetTimestamp() -
                    activity.StartedAt) / (double)System.Diagnostics.Stopwatch.Frequency);
                visual.Observe(activity != null || arrivalGate, arrivalGate ? 7 : age);
            }
            if (!initialized) { sector = next.SectorId; entryGeneration = next.EntryGeneration; initialized = true; }
            else if (sector != next.SectorId || entryGeneration != next.EntryGeneration)
            {
                sector = next.SectorId; entryGeneration = next.EntryGeneration;
                ClearPresentation();
                pendingArrival = next.ArrivalKind;
            }
            if (pendingArrival != 0 && next.HasPosition && next.Ready)
            {
                if (pendingArrival == 2 || next.Objects.Any(item => item.Kind == 11))
                {
                    arriving = true; wormhole = pendingArrival == 2; pendingArrival = 0;
                    arrivalAt = Time.unscaledTime; duration = 3;
                    direction = SpaceClient.ConvertRotation(next.Orientation) * Vector3.forward;
                    Vector3 destination = SpaceClient.Convert(next.Position);
                    mouthWorld = destination - direction * 4;
                    if (wormhole) portal = new NativeWormholeVisual(parent);
                    else
                    {
                        MapObject nearest = next.Objects.Where(item => item.Kind == 11)
                            .OrderBy(item => item.Position.Distance(next.Position)).FirstOrDefault();
                        if (nearest != null && bodies.TryGetValue(nearest.Id, out gate))
                        {
                            mouthWorld = SpaceClient.Convert(nearest.Position);
                            Vector3 normal = SpaceClient.ConvertRotation(nearest.Orientation) * Vector3.forward;
                            direction = Vector3.Dot(destination - mouthWorld, normal) >= 0 ? normal : -normal;
                        }
                        else { arriving = false; }
                    }
                    entryHeading = Quaternion.LookRotation(direction);
                }
            }
            if (next.Station != null) { ClearPresentation(); return; }
            if (arriving) return;
            NativeObjectEffect effect = next.ObjectEffects.FirstOrDefault(item =>
                item.OwnerId == next.PlayerId && item.Descriptor == 689);
            if (effect != null)
            {
                lastEffectAt = Time.unscaledTime;
                if (effectId != effect.EffectId)
                {
                    ClearPresentation(); effectId = effect.EffectId; wormhole = true;
                    began = Time.unscaledTime; duration = Mathf.Max(.25f, next.CastRemainingSeconds);
                    entryHeading = SpaceClient.ConvertRotation(next.Orientation);
                    direction = entryHeading * Vector3.forward;
                    mouthWorld = SpaceClient.Convert(next.Position) + direction * 6;
                    portal = new NativeWormholeVisual(parent);
                }
                else duration = Mathf.Max(duration, Time.unscaledTime - began + next.CastRemainingSeconds);
            }
            else if (effectId != 0 && Time.unscaledTime - lastEffectAt > .4f) ClearPresentation();
            if (next.GateCameraTarget != 0 && cameraGate != next.GateCameraTarget &&
                bodies.TryGetValue(next.GateCameraTarget, out Transform target))
            {
                ClearPresentation(); cameraGate = next.GateCameraTarget; gate = target;
                began = Time.unscaledTime; duration = 8.8f;
                MapObject item = next.Objects.FirstOrDefault(candidate => candidate.Id == cameraGate);
                if (item == null) { ClearPresentation(); return; }
                mouthWorld = SpaceClient.Convert(item.Position);
                Vector3 normal = target.forward;
                direction = Vector3.Dot(SpaceClient.Convert(next.Position) - mouthWorld, normal) >= 0 ? -normal : normal;
                entryHeading = Quaternion.LookRotation(direction, target.up);
            }
            else if (cameraGate != 0 && next.GateCameraTarget == 0) ClearPresentation();
        }

        /// <summary>Move and clip only the rendered hull; retain authoritative navigation coordinates.</summary>
        public void Tick(Vector3 renderedPosition, bool chart)
        {
            if (arriving && Time.unscaledTime - arrivalAt >= duration) ClearPresentation();
            if (!Active || snapshot == null || chart)
            { portal?.SetVisible(false); SetClip(false, Vector3.zero, Vector3.forward); return; }
            float now = Time.unscaledTime;
            Vector3 mouth = mouthWorld - renderedPosition;
            float progress = Mathf.Clamp01((now - (arriving ? arrivalAt : began)) / duration);
            float passage = wormhole && !arriving ? Mathf.SmoothStep(0, 1, Mathf.Clamp01((progress - .48f) / .5f)) :
                Mathf.SmoothStep(0, 1, progress);
            if (arriving)
            {
                ship.position = Vector3.Lerp(mouth - direction * 1.1f, Vector3.zero, passage);
                ship.rotation = Quaternion.Slerp(entryHeading, ship.rotation, passage);
                if (progress >= 1) { ClearPresentation(); return; }
            }
            else
            {
                ship.position = Vector3.Lerp(Vector3.zero, mouth + direction * 5, passage);
                ship.rotation = Quaternion.Slerp(ship.rotation, entryHeading, Mathf.Clamp01(progress * 3));
            }
            if (portal != null)
            {
                float opening = arriving ? 1 - Mathf.SmoothStep(0, 1, Mathf.Clamp01((progress - .7f) / .3f)) :
                    Mathf.SmoothStep(0, 1, Mathf.Clamp01(progress * 5));
                portal.SetPose(mouth, entryHeading, opening);
            }
            SetClip(true, mouth, arriving ? direction : -direction);
            float cameraBlend = arriving ? 1 - Mathf.SmoothStep(0, 1, Mathf.Clamp01((progress - .65f) / .35f)) :
                Mathf.SmoothStep(0, 1, Mathf.Clamp01(progress * 5));
            Vector3 focus = Vector3.Lerp(ship.position, mouth, .15f);
            float distance = Mathf.Clamp(Vector3.Distance(ship.position, mouth) * .4f + 12, 12, 35);
            Vector3 side = entryHeading * new Vector3(distance * .8f, distance * .4f, -distance);
            Vector3 cinematic = focus + side;
            camera.transform.position = Vector3.Lerp(camera.transform.position, cinematic, cameraBlend);
            camera.transform.rotation = Quaternion.Slerp(camera.transform.rotation,
                Quaternion.LookRotation(focus - cinematic - entryHeading * Vector3.up * distance * .13f,
                    entryHeading * Vector3.up), cameraBlend);
        }

        /// <summary>Clip hull pixels at the aperture while preserving other material overrides.</summary>
        private void SetClip(bool enabled, Vector3 mouth, Vector3 normal)
        {
            if (enabled && clipped.Length == 0) clipped = ship.GetComponentsInChildren<Renderer>();
            Vector4 plane = new Vector4(normal.x, normal.y, normal.z, -Vector3.Dot(normal, mouth));
            foreach (Renderer renderer in clipped)
            {
                if (renderer == null) continue;
                renderer.GetPropertyBlock(properties);
                properties.SetFloat("_TravelClip", enabled ? 1 : 0);
                properties.SetVector("_TravelPlane", plane);
                renderer.SetPropertyBlock(properties);
            }
            if (!enabled) clipped = Array.Empty<Renderer>();
        }

        /// <summary>Release an expired, interrupted or disconnected transfer.</summary>
        private void ClearPresentation()
        {
            SetClip(false, Vector3.zero, Vector3.forward);
            portal?.Dispose(); portal = null; effectId = cameraGate = 0; gate = null;
            arriving = false; ship.localPosition = Vector3.zero;
        }

        /// <summary>Reset sector history on logout.</summary>
        public void Clear() { ClearPresentation(); initialized = false; pendingArrival = 0; snapshot = null; }
        /// <summary>Release all presentation resources.</summary>
        public void Dispose() { Clear(); }
    }
}
