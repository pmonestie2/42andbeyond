using System.Collections.Generic;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Own and interpolate visible remote ships beneath the floating world origin.</summary>
    public sealed class RemoteShips
    {
        private sealed class View
        {
            public readonly Transform Model;
            public Transform Marker;
            public LineRenderer Trail;
            public readonly Vector3[] TrailPoints = new Vector3[12];
            public int TrailCount;
            public float LastTrailSample;
            public bool Crew, Selected, Formed;
            public Vector3 FormationOffset;
            public ShipActor State;
            public Vector3 Destination;
            public Vector3 Velocity;
            public Quaternion Orientation;
            public float PoseAt;

            /// <summary>Place a new ship immediately at its first authoritative pose.</summary>
            public View(Transform model, ShipActor state)
            {
                Model = model;
                Apply(state);
                Model.localPosition = Destination;
                Model.localRotation = Orientation;
            }

            /// <summary>Retain the latest pose for foreground interpolation.</summary>
            public void Apply(ShipActor state)
            {
                Vector3 next = SpaceClient.Convert(state.Position);
                if (State != null && Vector3.Distance(next, Destination) > .0001f)
                {
                    float elapsed = Mathf.Max(.04f, Time.unscaledTime - PoseAt);
                    Vector3 estimate = (next - Destination) / elapsed;
                    Velocity = estimate.magnitude <= 50 ? estimate : Vector3.zero;
                    PoseAt = Time.unscaledTime;
                }
                else if (State == null) PoseAt = Time.unscaledTime;
                State = state;
                Destination = next;
                Orientation = SpaceClient.ConvertRotation(state.Orientation);
            }
        }

        private readonly Dictionary<uint, View> ships = new Dictionary<uint, View>();
        private readonly HashSet<uint> visible = new HashSet<uint>();
        private readonly HashSet<uint> crewIds = new HashSet<uint>();
        private readonly HashSet<uint> formedIds = new HashSet<uint>();
        private readonly List<uint> removed = new List<uint>();
        private readonly SpaceArt art;
        private readonly Transform parent;
        private Camera markerCamera;
        public int Count => ships.Count;

        /// <summary>Share the scene's mesh and material owner.</summary>
        public RemoteShips(SpaceArt art, Transform parent)
        { this.art = art; this.parent = parent; }

        /// <summary>Reconcile server visibility without recreating ships on movement packets.</summary>
        public void Apply(ShipActor[] players, GroupState group, uint selectedTargetId,
            Position ownPosition)
        {
            visible.Clear();
            crewIds.Clear();
            formedIds.Clear();
            if (group != null)
                foreach (GroupMember member in group.Members)
                {
                    crewIds.Add(member.Id);
                    if (group.Formed && member.Formed) formedIds.Add(member.Id);
                }
            Vector3 ownAuthoritative = SpaceClient.Convert(ownPosition);
            foreach (ShipActor player in players)
            {
                if (!player.HasPosition) continue;
                bool crew = player.IsPlayer && crewIds.Contains(player.Id);
                bool formed = crew && formedIds.Contains(player.Id);
                bool selected = player.Id == selectedTargetId;
                visible.Add(player.Id);
                if (!ships.TryGetValue(player.Id, out View view))
                {
                    Transform model = player.IsPlayer ? art.Ship(parent) :
                        art.Mob(parent, player.Asset, player.Hostile);
                    model.localScale = Vector3.one * .25f * Mathf.Clamp(player.Scale, .05f, 100);
                    view = new View(model, player);
                    view.Marker = art.ContactMarker(model, player.IsPlayer, player.Hostile, crew, selected);
                    view.Crew = crew; view.Selected = selected;
                    view.Trail = CreateTrail(player.Name);
                    ships.Add(player.Id, view);
                }
                else if (!ReferenceEquals(view.State, player)) view.Apply(player);
                if (view.Formed != formed)
                {
                    view.TrailCount = 0;
                    if (view.Trail != null) view.Trail.enabled = false;
                }
                view.Formed = formed;
                if (formed) view.FormationOffset = SpaceClient.Convert(player.Position) - ownAuthoritative;
                if (view.Crew != crew || view.Selected != selected)
                {
                    if (view.Marker != null) Object.Destroy(view.Marker.gameObject);
                    view.Marker = art.ContactMarker(view.Model, player.IsPlayer, player.Hostile,
                        crew, selected);
                    view.Crew = crew; view.Selected = selected;
                    view.TrailCount = 0;
                    if (view.Trail != null) view.Trail.enabled = false;
                }
                view.Model.name = player.Name;
            }
            removed.Clear();
            foreach (uint id in ships.Keys) if (!visible.Contains(id)) removed.Add(id);
            foreach (uint id in removed)
            {
                if (ships[id].Trail != null) Object.Destroy(ships[id].Trail.gameObject);
                Object.Destroy(ships[id].Model.gameObject);
                ships.Remove(id);
            }
        }

        /// <returns>LineRenderer: A subtle motion wake, or null when the effect shader is absent.</returns>
        private LineRenderer CreateTrail(string name)
        {
            if (art.EffectLine == null) return null;
            var trailObject = new GameObject(name + " scanner wake", typeof(LineRenderer));
            trailObject.transform.SetParent(parent, false);
            LineRenderer trail = trailObject.GetComponent<LineRenderer>();
            trail.sharedMaterial = art.EffectLine;
            trail.useWorldSpace = false;
            trail.alignment = LineAlignment.View;
            trail.numCapVertices = 3;
            trail.numCornerVertices = 3;
            trail.positionCount = 0;
            trail.startWidth = .005f;
            trail.endWidth = .04f;
            trail.enabled = false;
            return trail;
        }

        /// <summary>Retain a short wake only from interpolated server-owned ship motion.</summary>
        private static void UpdateTrail(View view)
        {
            LineRenderer trail = view.Trail;
            if (trail == null) return;
            bool visibleTrail = view.Selected || view.Crew || view.State.Hostile;
            if (!visibleTrail || view.Formed) { trail.enabled = false; view.TrailCount = 0; return; }
            Vector3 current = view.Model.localPosition;
            if (view.TrailCount > 0 &&
                Vector3.Distance(current, view.TrailPoints[view.TrailCount - 1]) > 20f)
            { view.TrailCount = 0; trail.enabled = false; }
            bool sample = view.TrailCount == 0 ||
                Time.unscaledTime - view.LastTrailSample >= .09f &&
                Vector3.Distance(current, view.TrailPoints[view.TrailCount - 1]) >= .06f;
            if (sample)
            {
                if (view.TrailCount == view.TrailPoints.Length)
                {
                    for (int index = 1; index < view.TrailPoints.Length; index++)
                        view.TrailPoints[index - 1] = view.TrailPoints[index];
                    view.TrailCount--;
                }
                view.TrailPoints[view.TrailCount++] = current;
                view.LastTrailSample = Time.unscaledTime;
            }
            if (view.TrailCount < 2) return;
            trail.positionCount = view.TrailCount;
            for (int index = 0; index < view.TrailCount; index++)
                trail.SetPosition(index, index == view.TrailCount - 1 ? current : view.TrailPoints[index]);
            Color tint = view.Selected ? new Color(1f, .75f, .3f, .72f) :
                view.Crew ? new Color(.3f, 1f, .55f, .6f) :
                new Color(1f, .35f, .2f, .3f);
            Color faded = tint; faded.a = 0;
            trail.startColor = faded; trail.endColor = tint;
            trail.enabled = true;
        }

        /// <summary>Smooth network poses and snap long teleports instead of flying across a sector.</summary>
        public void Tick(float deltaTime)
        {
            float blend = 1 - Mathf.Exp(-12 * deltaTime);
            if (markerCamera == null)
                markerCamera = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
            foreach (View view in ships.Values)
            {
                Transform model = view.Model;
                float age = Time.unscaledTime - view.PoseAt;
                Vector3 predicted = view.Destination +
                    (age < .35f ? view.Velocity * Mathf.Min(age, .25f) : Vector3.zero);
                model.localPosition = view.Formed ? -parent.position + view.FormationOffset :
                    Vector3.Distance(model.localPosition, predicted) > 300 ?
                    predicted : Vector3.Lerp(model.localPosition, predicted, blend);
                model.localRotation = Quaternion.Slerp(model.localRotation, view.Orientation, blend);
                if (view.Marker != null && markerCamera != null)
                {
                    float distance = Vector3.Distance(markerCamera.transform.position, model.position);
                    float angularScale = Mathf.Clamp(1f + Mathf.Max(0, distance - 8f) * .035f, 1f, 2.1f);
                    view.Marker.rotation = markerCamera.transform.rotation;
                    view.Marker.localScale = Vector3.one * (angularScale /
                        Mathf.Max(.01f, model.lossyScale.x));
                }
                UpdateTrail(view);
            }
        }

        /// <returns>The rendered ship position when the actor has a visible model.</returns>
        public bool TryPosition(uint id, out Vector3 position)
        {
            if (ships.TryGetValue(id, out View view)) { position = view.Model.position; return true; }
            position = default; return false;
        }

        /// <summary>Remove departed-sector ships while retaining shared rendering assets.</summary>
        public void Clear()
        {
            foreach (View view in ships.Values)
            {
                if (view.Trail != null) Object.Destroy(view.Trail.gameObject);
                Object.Destroy(view.Model.gameObject);
            }
            ships.Clear(); visible.Clear(); crewIds.Clear(); formedIds.Clear(); removed.Clear();
        }
    }
}
