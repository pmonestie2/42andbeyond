using System;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Show mineable resources and native prospecting controls over the flight view.</summary>
    public sealed class MiningHud
    {
        private readonly SpaceClient client;
        private readonly FlightHud flightHud;
        private uint selectedId;
        private MapObject[] nearby = Array.Empty<MapObject>();
        private Vector2 oreScroll;
        private float refreshedAt;
        private string sector = "";
        private float CanvasWidth => Screen.width / flightHud.Scale;
        private float CanvasHeight => Screen.height / flightHud.Scale;

        /// <summary>Share the foreground controller without owning network state.</summary>
        public MiningHud(SpaceClient client, FlightHud flightHud)
        { this.client = client; this.flightHud = flightHud; }

        /// <summary>Discard a resource selection after sector travel or logout.</summary>
        public void Clear() { selectedId = 0; nearby = Array.Empty<MapObject>(); sector = ""; }

        /// <returns>bool: Whether the pointer falls on the selected resource panel.</returns>
        public bool ContainsPointer()
        {
            if (selectedId == 0) return false;
            Vector2 point = new Vector2(Input.mousePosition.x / flightHud.Scale,
                (Screen.height - Input.mousePosition.y) / flightHud.Scale);
            Snapshot snapshot = client.CurrentSnapshot;
            bool open = snapshot != null && snapshot.ResourceWindowOpen && snapshot.ResourceTargetId == selectedId;
            return PanelRect(open, open && snapshot.MiningProspect != null &&
                snapshot.MiningTractor != null).Contains(point);
        }

        /// <returns>bool: Whether a visible resource was selected under the pointer.</returns>
        public bool SelectAtPointer(Snapshot snapshot, Vector2 pointer)
        {
            if (snapshot == null) return false;
            float closest = 36; uint found = 0;
            foreach (MapObject resource in snapshot.Objects.Where(item => item.Kind == 38))
            {
                Vector3 projected = client.Project(resource.Position);
                if (projected.z <= 0) continue;
                float distance = Vector2.Distance(new Vector2(projected.x, projected.y), pointer);
                if (distance < closest) { closest = distance; found = resource.Id; }
            }
            if (found == 0) return false;
            SelectResource(found);
            return true;
        }

        /// <summary>Select a visible resource for prospecting and native warp targeting.</summary>
        public void SelectResource(uint id)
        {
            selectedId = id; oreScroll = Vector2.zero;
            flightHud.ClearSelection();
            client.SelectWorldObject(id);
        }

        /// <returns>The centered bounds of the selected resource panel.</returns>
        private Rect PanelRect(bool open, bool concurrent)
        {
            float height = open ? concurrent ? 338 : 286 : 160;
            return new Rect((CanvasWidth - 370) * .5f, CanvasHeight - height - 122, 370, height);
        }

        /// <summary>Draw limited world labels and the selected asteroid's server-owned ore stock.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (snapshot == null || snapshot.Station != null) return;
            if (snapshot.WarpPhase == WarpPhase.Charging || snapshot.WarpPhase == WarpPhase.Traveling)
            { selectedId = 0; return; }
            if (sector != snapshot.Sector || Time.unscaledTime >= refreshedAt)
            {
                nearby = snapshot.Objects.Where(item => item.Kind == 38)
                    .OrderBy(item => item.Position.Distance(snapshot.Position)).Take(12).ToArray();
                sector = snapshot.Sector; refreshedAt = Time.unscaledTime + .25f;
            }
            if (selectedId == 0 && snapshot.MiningActivity != null &&
                snapshot.Objects.Any(item => item.Id == snapshot.MiningActivity.TargetId && item.Kind == 38))
                selectedId = snapshot.MiningActivity.TargetId;
            if (selectedId != 0 && !snapshot.Objects.Any(item => item.Id == selectedId && item.Kind == 38))
                selectedId = 0;
            if (Event.current.type == EventType.Repaint) DrawLabels(snapshot);
            if (selectedId == 0) return;
            MapObject resource = snapshot.Objects.First(item => item.Id == selectedId);
            bool open = snapshot.ResourceWindowOpen && snapshot.ResourceTargetId == selectedId;
            bool prospecting = snapshot.MiningProspect?.TargetId == selectedId;
            bool tractoring = snapshot.MiningTractor?.TargetId == selectedId;
            Rect panel = PanelRect(open, prospecting && tractoring);
            flightHud.Panel(panel);
            Fill(new Rect(panel.x + 12, panel.y + 13, 5, 5), new Color(.25f, .95f, .85f));
            GUI.Label(new Rect(panel.x + 25, panel.y + 7, panel.width - 37, 18), "RESOURCE SCAN", flightHud.SmallStyle);
            GUI.Label(new Rect(panel.x + 12, panel.y + 27, panel.width - 24, 22),
                resource.Name.ToUpperInvariant(), flightHud.BodyStyle);
            float distance = resource.Position.Distance(snapshot.Position);
            ResourceView stock = snapshot.Resources.FirstOrDefault(item => item.Id == selectedId);
            string detail = $"LEVEL {stock?.Level ?? 0}   ·   {distance / 1000:0.00} KM";
            if (stock != null) detail += $"   ·   {stock.Fraction * 100:0}% REMAINING";
            GUI.Label(new Rect(panel.x + 12, panel.y + 53, panel.width - 24, 20), detail, flightHud.SmallStyle);
            GUI.Label(new Rect(panel.x + 12, panel.yMax - 25, panel.width - 24, 18), client.Status, flightHud.SmallStyle);
            bool stopped = Mathf.Abs(snapshot.Speed) <= .5f && Mathf.Abs(snapshot.TargetSpeed) <= .5f;
            bool? prospectVerb = snapshot.Verbs.Available(selectedId, 9);
            if (!open)
            {
                GUI.enabled = client.Ready && !client.Incapacitated &&
                    (!stopped || prospectVerb == true);
                if (GUI.Button(new Rect(panel.x + 12, panel.y + 79, panel.width - 24, 32),
                    !stopped ? "BRAKE SHIP TO PROSPECT" : prospectVerb == true ?
                    "OPEN PROSPECTING WINDOW" : prospectVerb == false ||
                    snapshot.Verbs.TargetId == selectedId ?
                    "SERVER: PROSPECT UNAVAILABLE" : "WAITING FOR PROSPECT VERB", flightHud.ButtonStyle))
                { if (stopped) client.OpenResource(selectedId); else client.Thrust(4); }
                GUI.enabled = true;
                GUI.Label(new Rect(panel.x + 12, panel.y + 115, panel.width - 24, 18),
                    "Server checks skill, range, warp, and shared claim.", flightHud.SmallStyle);
                return;
            }
            if (GUI.Button(new Rect(panel.x + panel.width - 88, panel.y + 7, 76, 28), "CLOSE", flightHud.ButtonStyle))
                client.CloseResource();
            if (prospecting) DrawMiningProgress(panel, snapshot.MiningProspect,
                snapshot.ProspectProgress, snapshot.ProspectRemainingSeconds, 80);
            if (tractoring) DrawMiningProgress(panel, snapshot.MiningTractor,
                snapshot.TractorProgress, snapshot.TractorRemainingSeconds, prospecting ? 132 : 80);
            float listTop = prospecting && tractoring ? 183 :
                prospecting || tractoring ? 131 : 79;
            GUILayout.BeginArea(new Rect(panel.x + 12, panel.y + listTop,
                panel.width - 24, panel.height - listTop - 37));
            oreScroll = GUILayout.BeginScrollView(oreScroll);
            if (stock == null) GUILayout.Label("Reading resource inventory…", flightHud.SmallStyle);
            else
            {
                ItemView[] items = stock.Items.Where(item => item != null).ToArray();
                if (items.Length == 0) GUILayout.Label("Resource depleted", flightHud.SmallStyle);
                foreach (ItemView item in items)
                {
                    GUI.enabled = !prospecting && stopped && prospectVerb == true;
                    if (GUILayout.Button($"PROSPECT  {item.Name}  ×{item.Quantity}", flightHud.ButtonStyle, GUILayout.Height(34)))
                        client.ProspectResource(item.Slot);
                    GUI.enabled = true;
                }
            }
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        /// <summary>Show the server's timed prospect or tractor phase and its local countdown.</summary>
        private void DrawMiningProgress(Rect panel, MiningActivity activity, float progress,
            float remainingSeconds, float y)
        {
            string phase = activity.Phase == MiningPhase.Prospect ? "PROSPECTING" : "TRACTORING ORE";
            GUI.Label(new Rect(panel.x + 12, panel.y + y, panel.width - 24, 20),
                $"{phase}   ·   {remainingSeconds:0.0} S", flightHud.BodyStyle);
            Rect track = new Rect(panel.x + 12, panel.y + y + 28, panel.width - 24, 8);
            Fill(track, new Color(.08f, .2f, .25f));
            Fill(new Rect(track.x, track.y, track.width * Mathf.Clamp01(progress), track.height),
                activity.Phase == MiningPhase.Prospect ? new Color(.3f, .86f, .8f) :
                new Color(.98f, .75f, .32f));
        }

        /// <summary>Mark a bounded set of nearby mineable objects without covering ship contacts.</summary>
        private void DrawLabels(Snapshot snapshot)
        {
            Vector2[] contactPoints = snapshot.Players.Concat(snapshot.Mobs)
                .Where(actor => actor.HasPosition)
                .Select(actor => client.Project(actor.Position))
                .Where(point => point.z > 0)
                .Select(point => new Vector2(point.x / flightHud.Scale, point.y / flightHud.Scale))
                .ToArray();
            foreach (MapObject resource in nearby)
            {
                Vector3 point = client.Project(resource.Position);
                point.x /= flightHud.Scale; point.y /= flightHud.Scale;
                if (point.z <= 0 || point.x < 320 || point.x > CanvasWidth - 335 ||
                    point.y < 100 || point.y > CanvasHeight - 160) continue;
                Color signal = selectedId == resource.Id ? new Color(1, .77f, .35f) :
                    new Color(.38f, .87f, .82f);
                Fill(new Rect(point.x - 5, point.y - 5, 10, 2), signal);
                Fill(new Rect(point.x - 5, point.y - 5, 2, 10), signal);
                if (contactPoints.Any(contact => Mathf.Abs(contact.x - point.x) < 190 &&
                    Mathf.Abs(contact.y - point.y) < 60)) continue;
                if (client.Chart && selectedId != resource.Id) continue;
                Fill(new Rect(point.x + 9, point.y - 12, 172, 42), new Color(.005f, .018f, .032f, .86f));
                Fill(new Rect(point.x + 9, point.y - 12, 3, 42), signal);
                GUI.Label(new Rect(point.x + 18, point.y - 10, 160, 20), resource.Name, flightHud.BodyStyle);
                GUI.Label(new Rect(point.x + 18, point.y + 9, 160, 18),
                    "ORE   ·   " + (resource.Position.Distance(snapshot.Position) / 1000).ToString("0.0") + " km",
                    flightHud.SmallStyle);
            }
        }

        /// <summary>Draw a tinted mining marker without changing later GUI colors.</summary>
        private static void Fill(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color; GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = previous;
        }
    }
}
