using System;
using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    public sealed partial class FlightHud
    {
        private enum RimEdge { Top, Right, Bottom, Left }

        private sealed class RimContact
        {
            public uint Id;
            public string Name;
            public ContactCategoryFilter Category;
            public Color Color;
            public float Distance;
            public bool Player, Selected;
            public RimEdge Edge;
            public Vector2 Anchor;
        }

        private sealed class RimCluster
        {
            public readonly List<RimContact> Contacts = new List<RimContact>();
            public RimEdge Edge;
            public Vector2 Anchor;
            public Rect Hit;
            public Rect Footprint;
        }

        private readonly List<RimCluster> contactRim = new List<RimCluster>();
        private Rect[] rimOccupied = Array.Empty<Rect>();
        private ContactCategoryFilter selectedContactFilter;
        private string rimSector = "";

        /// <summary>Choose the same authoritative contact category used by rim and keyboard cycling.</summary>
        public ContactCategoryFilter SelectedContactFilter
        {
            get => selectedContactFilter;
            set { selectedContactFilter = value; contactRim.Clear(); }
        }

        /// Returns:
        ///     bool: Whether this screen-space pointer selected an offscreen advertised contact.
        public bool SelectRimAtPointer(Vector2 screenPoint)
        {
            Snapshot snapshot = client.CurrentSnapshot;
            if (!RimEnabled(snapshot) || contactRim.Count == 0 || rimSector != snapshot.Sector) return false;
            Vector2 pointer = screenPoint / Scale;
            RimCluster hit = contactRim.LastOrDefault(group => group.Hit.Contains(pointer));
            if (hit == null) return false;
            uint selected = contactId != 0 ? contactId : wreckId != 0 ? wreckId :
                snapshot.SelectedTargetId;
            int next = NextClusterIndex(hit.Contacts.Select(item => item.Id).ToArray(), selected);
            RimContact contact = hit.Contacts[next];
            client.SelectAdvertisedObject(contact.Id);
            return true;
        }

        /// <summary>Draw compact screen-edge directions only during unobstructed flight.</summary>
        private void DrawContactRim(Snapshot snapshot)
        {
            contactRim.Clear();
            if (!RimEnabled(snapshot)) return;
            rimSector = snapshot.Sector;
            rimOccupied = RetainedFlightBounds;
            List<RimContact> candidates = BuildRimContacts(snapshot);
            foreach (RimContact item in candidates.OrderBy(item => item.Edge).ThenBy(item =>
                item.Edge == RimEdge.Top || item.Edge == RimEdge.Bottom ? item.Anchor.x : item.Anchor.y))
            {
                RimCluster cluster = contactRim.LastOrDefault(group => group.Edge == item.Edge &&
                    Vector2.Distance(group.Anchor, item.Anchor) < 38);
                if (cluster == null)
                { cluster = new RimCluster { Edge = item.Edge, Anchor = item.Anchor }; contactRim.Add(cluster); }
                cluster.Contacts.Add(item);
            }
            var placed = new List<RimCluster>();
            foreach (RimCluster cluster in contactRim.OrderByDescending(group => group.Contacts.Any(item => item.Selected))
                .ThenBy(group => group.Contacts.Min(item => item.Distance)))
            {
                cluster.Contacts.Sort((left, right) => left.Distance.CompareTo(right.Distance));
                if (!PlaceRimCluster(cluster, placed))
                {
                    RimCluster nearest = placed.OrderBy(group => Vector2.Distance(group.Anchor, cluster.Anchor))
                        .FirstOrDefault();
                    if (nearest != null) nearest.Contacts.AddRange(cluster.Contacts);
                    continue;
                }
                placed.Add(cluster);
            }
            contactRim.Clear(); contactRim.AddRange(placed);
            foreach (RimCluster group in placed) DrawRimCluster(group);
        }

        /// Returns:
        ///     bool: Whether ordinary flight can show and select edge contacts.
        private bool RimEnabled(Snapshot snapshot) => snapshot?.Ready == true && snapshot.Station == null &&
            !equipmentOpen && !client.AbilitiesOpen && !client.Incapacitated &&
            !client.GalaxyTravelOpen && !client.ComponentMarketOpen && !client.ItemBrowserOpen &&
            !client.ChatOpen && !settingsOpen && !GroupInviteOpen && snapshot.Dialogue == null &&
            snapshot.Trade?.Open != true && snapshot.OpenLootId == 0 && !snapshot.ResourceWindowOpen;

        /// Returns:
        ///     List<RimContact>: Offscreen, server-advertised contacts matching the chosen filter.
        private List<RimContact> BuildRimContacts(Snapshot snapshot)
        {
            var result = new List<RimContact>();
            uint chosen = contactId != 0 ? contactId : wreckId != 0 ? wreckId : snapshot.SelectedTargetId;
            foreach (ShipActor actor in snapshot.Players.Concat(snapshot.Mobs))
            {
                if (actor.Id == snapshot.PlayerId || !ContactFiltering.Matches(selectedContactFilter, actor)) continue;
                Vector3 projected = client.ProjectPlayer(actor.Id);
                AddRimContact(result, snapshot, actor.Id, actor.Name, actor.Position, projected,
                    ContactCategoryFilter.Combat, actor.IsPlayer ? new Color(.37f, .92f, .87f) :
                    actor.Hostile ? new Color(1f, .37f, .33f) : new Color(.94f, .73f, .37f),
                    actor.IsPlayer, actor.Id == chosen);
            }
            foreach (MapObject item in snapshot.Objects)
            {
                if (!ContactFiltering.Matches(selectedContactFilter, item)) continue;
                float distance = item.Position.Distance(snapshot.Position);
                if (distance > 80000 && item.Id != chosen) continue;
                ContactCategoryFilter category = item.Kind == 25 ? ContactCategoryFilter.Loot :
                    item.Kind == 37 || item.Kind == 38 ? ContactCategoryFilter.Mining :
                    ContactCategoryFilter.Travel;
                Color color = category == ContactCategoryFilter.Loot ? new Color(1f, .70f, .25f) :
                    category == ContactCategoryFilter.Mining ? new Color(.35f, .95f, .73f) :
                    item.Kind == 11 ? new Color(.70f, .49f, 1f) : new Color(.42f, .91f, .87f);
                AddRimContact(result, snapshot, item.Id, item.Name, item.Position, client.Project(item.Position),
                    category, color, false, item.Id == chosen);
            }
            return result;
        }

        /// <summary>Project one advertised contact toward the correct viewport edge, including behind-camera poses.</summary>
        private void AddRimContact(List<RimContact> result, Snapshot snapshot, uint id, string name,
            Position position, Vector3 projected, ContactCategoryFilter category, Color color,
            bool player, bool selected)
        {
            Vector2 point = new Vector2(projected.x, projected.y) / Scale;
            Rect inside = new Rect(40, 34, CanvasWidth - 80, CanvasHeight - 68);
            if (projected.z > 0 && inside.Contains(point) && !rimOccupied.Any(area =>
                area.width > 0 && area.Contains(point))) return;
            Vector2 center = new Vector2(CanvasWidth * .5f, CanvasHeight * .5f);
            Vector2 direction = point - center;
            if (projected.z <= 0 || float.IsNaN(direction.x) || float.IsInfinity(direction.x) ||
                float.IsNaN(direction.y) || float.IsInfinity(direction.y))
            {
                Camera camera = Camera.main;
                Vector3 local = camera == null ? Vector3.back : camera.transform.InverseTransformDirection(
                    SpaceClient.Convert(position) - SpaceClient.Convert(snapshot.Position));
                direction = BehindDirection(local);
            }
            Vector2 anchor = RimAnchor(center, direction, CanvasWidth, CanvasHeight, out RimEdge edge);
            result.Add(new RimContact { Id = id, Name = name ?? "Contact", Category = category,
                Color = color, Distance = position.Distance(snapshot.Position), Player = player,
                Selected = selected, Anchor = anchor, Edge = edge });
        }

        /// Returns:
        ///     Vector2: The ray intersection with an inset rectangular screen rim.
        private static Vector2 RimAnchor(Vector2 center, Vector2 direction,
            float width, float height, out RimEdge edge)
        {
            float left = 28, right = width - 28, top = 27, bottom = height - 31;
            if (direction.sqrMagnitude < .001f) direction = Vector2.up;
            float horizontal = direction.x > 0 ? (right - center.x) / direction.x :
                direction.x < 0 ? (left - center.x) / direction.x : float.PositiveInfinity;
            float vertical = direction.y > 0 ? (bottom - center.y) / direction.y :
                direction.y < 0 ? (top - center.y) / direction.y : float.PositiveInfinity;
            bool side = horizontal < vertical;
            edge = side ? direction.x > 0 ? RimEdge.Right : RimEdge.Left :
                direction.y > 0 ? RimEdge.Bottom : RimEdge.Top;
            float factor = Mathf.Min(horizontal, vertical);
            return center + direction * factor;
        }

        /// Returns:
        ///     Vector2: Camera-local bearing mapped to GUI directions for a contact behind the pilot.
        private static Vector2 BehindDirection(Vector3 local)
        {
            Vector2 direction = new Vector2(local.x, -local.y);
            return direction.sqrMagnitude < .01f ? Vector2.up : direction;
        }

        /// Returns:
        ///     int: Next member of a dense edge cluster, wrapping after the current selection.
        private static int NextClusterIndex(uint[] ids, uint selected)
        {
            int current = Array.IndexOf(ids, selected);
            return ids.Length == 0 ? -1 : (current + 1) % ids.Length;
        }

        /// Returns:
        ///     bool: Whether a cluster fits without covering active flight controls or another marker.
        private bool PlaceRimCluster(RimCluster cluster, List<RimCluster> placed)
        {
            bool horizontal = cluster.Edge == RimEdge.Top || cluster.Edge == RimEdge.Bottom;
            float extent = horizontal ? CanvasWidth : CanvasHeight;
            for (int step = 0; step < Mathf.CeilToInt(extent / 28f); step++)
            {
                float offset = step == 0 ? 0 : (step % 2 == 1 ? 1 : -1) * ((step + 1) / 2) * 28;
                Vector2 point = cluster.Anchor + (horizontal ? Vector2.right : Vector2.down) * offset;
                if (horizontal) point.x = Mathf.Clamp(point.x, 30, CanvasWidth - 30);
                else point.y = Mathf.Clamp(point.y, 30, CanvasHeight - 34);
                Rect hit = new Rect(point.x - 18, point.y - 18, 36, 36);
                Rect text = RimLabelRect(point, cluster.Edge);
                Rect footprint = Rect.MinMaxRect(Mathf.Min(hit.xMin, text.xMin), Mathf.Min(hit.yMin, text.yMin),
                    Mathf.Max(hit.xMax, text.xMax), Mathf.Max(hit.yMax, text.yMax));
                if (footprint.xMin < 6 || footprint.xMax > CanvasWidth - 6 ||
                    footprint.yMin < 6 || footprint.yMax > CanvasHeight - 6) continue;
                if (!RimPositionClear(footprint, rimOccupied, placed.Select(other => other.Footprint))) continue;
                cluster.Anchor = point; cluster.Hit = hit; cluster.Footprint = footprint; return true;
            }
            return false;
        }

        /// Returns:
        ///     Rect: A rectangle with a small safety margin around active controls.
        private static Rect Expanded(Rect area, float margin) => new Rect(area.x - margin, area.y - margin,
            area.width + margin * 2, area.height + margin * 2);

        /// Returns:
        ///     bool: Whether a marker and its label avoid all reserved HUD and prior rim bounds.
        private static bool RimPositionClear(Rect footprint, IEnumerable<Rect> occupied,
            IEnumerable<Rect> placed) =>
            !occupied.Any(area => area.width > 0 && Expanded(area, 7).Overlaps(footprint)) &&
            !placed.Any(area => Expanded(area, 5).Overlaps(footprint));

        /// <summary>Draw one shaped contact cue, count, distance and hover details.</summary>
        private void DrawRimCluster(RimCluster group)
        {
            RimContact representative = group.Contacts.FirstOrDefault(item => item.Selected) ?? group.Contacts[0];
            Vector2 point = group.Anchor;
            bool selected = group.Contacts.Any(item => item.Selected);
            Color color = representative.Color;
            if (selected) RimDiamond(point, 16, new Color(1f, .82f, .40f), 2);
            Fill(new Rect(point.x - 13, point.y - 13, 26, 26), new Color(.015f, .035f, .055f, .82f));
            switch (representative.Category)
            {
                case ContactCategoryFilter.Combat:
                    if (representative.Player) RimDiamond(point, 9, color, 2);
                    else { RimStroke(point + new Vector2(-8, -6), point + new Vector2(0, 1), color, 3);
                        RimStroke(point + new Vector2(8, -6), point + new Vector2(0, 1), color, 3);
                        RimStroke(point + new Vector2(-7, 5), point + new Vector2(0, 10), color, 2);
                        RimStroke(point + new Vector2(7, 5), point + new Vector2(0, 10), color, 2); }
                    break;
                case ContactCategoryFilter.Loot:
                    Fill(new Rect(point.x - 8, point.y - 7, 16, 3), color);
                    Fill(new Rect(point.x - 6, point.y - 1, 12, 3), color);
                    Fill(new Rect(point.x - 4, point.y + 5, 8, 3), color); break;
                case ContactCategoryFilter.Mining:
                    RimDiamond(point, 9, color, 2);
                    Fill(new Rect(point.x - 2, point.y - 2, 4, 4), color); break;
                default: RimDiamond(point, 10, color, 2); break;
            }
            string distance = (representative.Distance / 1000f).ToString("0.0") + " km";
            string count = group.Contacts.Count > 1 ? " +" + (group.Contacts.Count - 1) : "";
            Rect text = RimLabelRect(point, group.Edge);
            GUI.Label(text, distance + count, small);
            Vector2 pointer = Event.current.mousePosition;
            if (!group.Hit.Contains(pointer)) return;
            string names = string.Join("\n", group.Contacts.Take(4).Select(item =>
                item.Name + "  ·  " + (item.Distance / 1000f).ToString("0.0") + " km"));
            if (group.Contacts.Count > 4) names += "\n+" + (group.Contacts.Count - 4) + " more";
            float height = 43 + Mathf.Min(group.Contacts.Count, 4) * 21;
            Rect tip = new Rect(Mathf.Clamp(point.x + 24, 8, CanvasWidth - 248),
                Mathf.Clamp(point.y - height - 16, 8, CanvasHeight - height - 8), 240, height);
            Fill(tip, new Color(.01f, .025f, .04f, .95f));
            Fill(new Rect(tip.x, tip.y, 2, tip.height), color);
            GUI.Label(new Rect(tip.x + 10, tip.y + 6, tip.width - 18, tip.height - 10),
                names + "\nClick to select" + (group.Contacts.Count > 1 ? " · click again to cycle" : ""), small);
        }

        /// Returns:
        ///     Rect: The distance label area reserved beside an edge marker.
        private static Rect RimLabelRect(Vector2 point, RimEdge edge) => edge == RimEdge.Left ?
            new Rect(point.x + 22, point.y - 10, 112, 20) : edge == RimEdge.Right ?
            new Rect(point.x - 130, point.y - 10, 107, 20) :
            new Rect(point.x + 19, point.y - 9, 105, 20);

        /// <summary>Draw a thin diamond without introducing textures or solid orbs.</summary>
        private static void RimDiamond(Vector2 center, float radius, Color color, float width)
        {
            RimStroke(center + Vector2.up * radius, center + Vector2.right * radius, color, width);
            RimStroke(center + Vector2.right * radius, center + Vector2.down * radius, color, width);
            RimStroke(center + Vector2.down * radius, center + Vector2.left * radius, color, width);
            RimStroke(center + Vector2.left * radius, center + Vector2.up * radius, color, width);
        }

        /// <summary>Draw one short rim segment while restoring the shared IMGUI matrix.</summary>
        private static void RimStroke(Vector2 from, Vector2 to, Color color, float thickness)
        {
            Matrix4x4 matrix = GUI.matrix;
            Vector2 delta = to - from;
            GUI.matrix = matrix * Matrix4x4.TRS(new Vector3(from.x, from.y, 0),
                Quaternion.Euler(0, 0, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg), Vector3.one);
            Fill(new Rect(0, -thickness * .5f, delta.magnitude, thickness), color);
            GUI.matrix = matrix;
        }
    }
}
