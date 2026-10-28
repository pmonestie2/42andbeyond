using System;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Browse the known gate map and supervise client-side automatic travel.</summary>
    public sealed class GalaxyTravelHud
    {
        private readonly SpaceClient client;
        private readonly FlightHud hud;
        private bool open, positioned, dragging, inputFocused, searchPending;
        private string search = "";
        private uint flags = 3, requestedPage;
        private float searchAt;
        private Vector2 position, dragOffset, catalogScroll, routeScroll;
        private Rect window;
        private GalaxyDestination selected;
        private GUIStyle title, body, small, eyebrow, rowButton, searchStyle;
        private static readonly Color Cyan = new Color(.36f, .82f, .9f);
        private static readonly Color Gold = new Color(.95f, .77f, .43f);
        private static readonly Color Muted = new Color(.46f, .59f, .67f);

        /// Returns:
        ///     bool: Whether the galaxy travel overlay is visible.
        public bool IsOpen => open;
        /// Returns:
        ///     bool: Whether its search field owns keyboard input.
        public bool InputFocused => open && inputFocused;

        /// <summary>Bind the browser to the client's known gate map and automatic travel controls.</summary>
        public GalaxyTravelHud(SpaceClient client, FlightHud hud)
        { this.client = client; this.hud = hud; }

        /// <summary>Open the network browser and refresh its current search.</summary>
        public void Open()
        {
            open = true; RequestPage(0);
            if (selected != null) client.PreviewGalaxyRoute(selected.Sector, selected.Id);
        }

        /// <summary>Dismiss the browser while allowing active travel to continue.</summary>
        public void Close() { open = false; dragging = false; inputFocused = false; }

        /// Returns:
        ///     bool: Whether the modal backdrop owns a HUD-coordinate pointer.
        public bool ContainsPointer(float x, float y) => open;

        /// <summary>Draw known destinations, client-planned route legs and active travel controls.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (!open || snapshot == null) return;
            EnsureStyles();
            float canvasWidth = Screen.width / hud.Scale, canvasHeight = Screen.height / hud.Scale;
            float width = Mathf.Min(1090, canvasWidth - 32), height = Mathf.Min(710, canvasHeight - 32);
            if (!positioned)
            { position = new Vector2((canvasWidth - width) * .5f, (canvasHeight - height) * .5f); positioned = true; }
            position.x = Mathf.Clamp(position.x, 8, Mathf.Max(8, canvasWidth - width - 8));
            position.y = Mathf.Clamp(position.y, 8, Mathf.Max(8, canvasHeight - height - 8));
            window = new Rect(position, new Vector2(width, height));
            HandleDrag();
            Fill(new Rect(0, 0, canvasWidth, canvasHeight), new Color(.003f, .008f, .016f, .88f));
            Fill(window, new Color(.012f, .023f, .034f, 1));
            Fill(new Rect(window.x, window.y, width, 3), Cyan);
            Fill(new Rect(window.x, window.y + 3, width, 83), new Color(.022f, .045f, .061f));
            GUI.Label(new Rect(window.x + 24, window.y + 16, width - 160, 18), "NAVIGATION NETWORK  /  GALAXY TRAVEL", eyebrow);
            GUI.Label(new Rect(window.x + 24, window.y + 38, width - 160, 34), "Choose your next horizon", title);
            if (GUI.Button(new Rect(window.xMax - 102, window.y + 25, 78, 32), "Close", hud.ButtonStyle))
            { Close(); return; }
            float leftWidth = Mathf.Clamp(width * .43f, 230, 450);
            float contentHeight = height - 193;
            Rect browser = new Rect(window.x + 22, window.y + 103, leftWidth - 10, contentHeight);
            Rect preview = new Rect(browser.xMax + 25, browser.y, width - leftWidth - 59, contentHeight);
            Fill(new Rect(browser.xMax + 12, browser.y, 1, browser.height), new Color(.12f, .25f, .31f));
            DrawCatalog(snapshot, browser);
            DrawRoute(snapshot, preview);
            DrawTravelBar(snapshot, new Rect(window.x + 22, window.yMax - 74, width - 44, 60));
            if (searchPending && Time.unscaledTime >= searchAt)
            { searchPending = false; RequestPage(0); }
        }

        /// <summary>Draw a debounced known-map search with type filters and page navigation.</summary>
        private void DrawCatalog(Snapshot snapshot, Rect rect)
        {
            GUI.SetNextControlName("galaxy-travel-search");
            string next = GUI.TextField(new Rect(rect.x, rect.y, rect.width, 34), search, searchStyle);
            inputFocused = GUI.GetNameOfFocusedControl() == "galaxy-travel-search";
            if (next != search) { search = next; searchPending = true; searchAt = Time.unscaledTime + .3f; }
            if (search.Length == 0) GUI.Label(new Rect(rect.x + 12, rect.y + 8, rect.width - 24, 22), "Find a station, gate or sector…", small);
            float buttonWidth = (rect.width - 12) / 3;
            DrawFilter(new Rect(rect.x, rect.y + 43, buttonWidth, 28), "All", 3);
            DrawFilter(new Rect(rect.x + buttonWidth + 6, rect.y + 43, buttonWidth, 28), "Gates", 1);
            DrawFilter(new Rect(rect.x + (buttonWidth + 6) * 2, rect.y + 43, buttonWidth, 28), "Stations", 2);
            GalaxyCatalogPage catalog = snapshot.GalaxyCatalog;
            string count = catalog == null ? "READING KNOWN GATE MAP…" : catalog.Total + " DESTINATIONS";
            GUI.Label(new Rect(rect.x, rect.y + 84, rect.width, 20), count, eyebrow);
            Rect list = new Rect(rect.x, rect.y + 112, rect.width, Mathf.Max(40, rect.height - 157));
            GUILayout.BeginArea(list);
            catalogScroll = GUILayout.BeginScrollView(catalogScroll);
            if (catalog != null)
            {
                foreach (GalaxyDestination entry in catalog.Entries) DrawDestination(entry, rect.width - 20);
                if (catalog.Entries.Count == 0) GUILayout.Label("No destinations match. Try a sector name or another filter.", body);
            }
            else GUILayout.Label("Reading known gates and stations…", body);
            GUILayout.EndScrollView(); GUILayout.EndArea();
            uint page = catalog?.Page ?? requestedPage;
            uint size = Math.Max(1u, catalog?.PageSize ?? 64u);
            uint pages = catalog == null ? 1 : Math.Max(1u, (catalog.Total + size - 1) / size);
            float bottom = rect.yMax - 32;
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && !searchPending && page > 0;
            if (GUI.Button(new Rect(rect.x, bottom, 96, 30), "Previous", hud.ButtonStyle)) RequestPage(page - 1);
            GUI.enabled = enabled;
            GUI.Label(new Rect(rect.x + 107, bottom + 5, rect.width - 191, 24), (page + 1) + " / " + pages, small);
            GUI.enabled = enabled && !searchPending && page + 1 < pages;
            if (GUI.Button(new Rect(rect.xMax - 77, bottom, 77, 30), "Next", hud.ButtonStyle)) RequestPage(page + 1);
            GUI.enabled = enabled;
        }

        /// <summary>Draw a distinct gate or station row with readable wrapped names.</summary>
        private void DrawDestination(GalaxyDestination entry, float width)
        {
            bool active = selected != null && selected.Id == entry.Id && selected.Sector == entry.Sector;
            float nameHeight = Mathf.Max(22, body.CalcHeight(new GUIContent(entry.Name), width - 66));
            string location = entry.SystemName + " / " + entry.SectorName;
            float locationHeight = Mathf.Max(18, small.CalcHeight(new GUIContent(location), width - 66));
            float rowHeight = nameHeight + locationHeight + 39;
            Rect row = GUILayoutUtility.GetRect(0, rowHeight, GUILayout.ExpandWidth(true));
            Fill(row, active ? new Color(.065f, .11f, .13f) : new Color(.022f, .039f, .052f));
            if (GUI.Button(row, GUIContent.none, rowButton))
            { selected = entry; routeScroll = Vector2.zero; client.PreviewGalaxyRoute(entry.Sector, entry.Id); }
            Fill(new Rect(row.x, row.y, 3, row.height), active ? Gold : entry.Kind == 11 ? Cyan * .7f : Muted);
            DrawKindIcon(new Rect(row.x + 14, row.y + 15, 25, 25), entry.Kind == 11, active ? Gold : Cyan);
            GUI.Label(new Rect(row.x + 51, row.y + 9, row.width - 61, nameHeight), entry.Name, body);
            GUI.Label(new Rect(row.x + 51, row.y + 12 + nameHeight, row.width - 61, locationHeight), location, small);
            GUI.Label(new Rect(row.x + 51, row.yMax - 23, row.width - 61, 18), entry.Kind == 11 ?
                (entry.DestinationSector == 0 ? "GATE · LINK UNAVAILABLE" : "GATE · CROSSING DESTINATION") : "STATION · DOCK ON ARRIVAL", eyebrow);
            GUILayout.Space(6);
        }

        /// <summary>Draw a selected destination and the legs planned from the known gate map.</summary>
        private void DrawRoute(Snapshot snapshot, Rect rect)
        {
            GUI.Label(new Rect(rect.x, rect.y + 2, rect.width, 20), "ROUTE PREVIEW", eyebrow);
            if (selected == null)
            {
                GUI.Label(new Rect(rect.x, rect.y + 65, rect.width, 65), "A connected galaxy,\none destination at a time.", title);
                GUI.Label(new Rect(rect.x, rect.y + 150, rect.width, 110), "Choose a gate to route to its sector and cross it. Choose a station to travel there and dock automatically.", body);
                return;
            }
            GalaxyRoutePlan plan = snapshot.GalaxyRoute;
            bool matches = PlanMatches(plan);
            float nameHeight = Mathf.Max(34, title.CalcHeight(new GUIContent(selected.Name), rect.width));
            GUI.Label(new Rect(rect.x, rect.y + 33, rect.width, nameHeight), selected.Name, title);
            float y = rect.y + 40 + nameHeight;
            string arrival = matches && plan.Success && plan.Legs.Count > 0 ? plan.Legs[plan.Legs.Count - 1].DestinationName : "its linked sector";
            string behavior = selected.Kind == 11 ? (selected.DestinationSector == 0 ?
                "This gate has no available destination link. Automatic travel is unavailable." :
                "Gate to " + arrival + ": first reach " + selected.SectorName + ", then cross this selected gate.") :
                "Travel to this station and finish docked.";
            float descriptionHeight = small.CalcHeight(new GUIContent(behavior), rect.width);
            GUI.Label(new Rect(rect.x, y, rect.width, descriptionHeight), behavior, small); y += descriptionHeight + 13;
            Fill(new Rect(rect.x, y, rect.width, 1), new Color(.16f, .33f, .4f)); y += 13;
            Rect legs = new Rect(rect.x, y, rect.width, Mathf.Max(30, rect.yMax - y - 57));
            GUILayout.BeginArea(legs); routeScroll = GUILayout.BeginScrollView(routeScroll);
            if (!matches) GUILayout.Label("Plotting a route through your known gate map…", body);
            else if (!plan.Success) GUILayout.Label(string.IsNullOrEmpty(plan.Error) ? "Your known gate map has no usable route to this destination." : plan.Error, body);
            else
            {
                int jumps = plan.Legs.Count(leg => leg.Kind == 0);
                GUILayout.Label(jumps + (jumps == 1 ? " gate crossing" : " gate crossings") + "  ·  " + plan.Legs.Count + " travel steps", eyebrow);
                GUILayout.Space(13);
                for (int i = 0; i < plan.Legs.Count; i++) DrawLeg(snapshot, plan.Legs[i], i);
                if (plan.Legs.Count == 0) GUILayout.Label("You are already at this destination.", body);
            }
            GUILayout.EndScrollView(); GUILayout.EndArea();
            bool enabled = GUI.enabled;
            bool active = snapshot.GalaxyTravel != null && snapshot.GalaxyTravel.Active;
            bool ready = matches && plan.Success && plan.StartSector == snapshot.SectorId &&
                selected.DestinationSector != 0 && !active;
            GUI.enabled = enabled && ready;
            if (GUI.Button(new Rect(rect.x, rect.yMax - 43, rect.width - 103, 38), "GO · AUTO TRAVEL", hud.ButtonStyle)) client.StartGalaxyTravel();
            GUI.enabled = enabled && !active;
            if (GUI.Button(new Rect(rect.xMax - 94, rect.yMax - 43, 94, 38), "Replot", hud.ButtonStyle)) client.PreviewGalaxyRoute(selected.Sector, selected.Id);
            GUI.enabled = enabled;
        }

        /// <summary>Draw a route step with a restrained connection line and active-leg marker.</summary>
        private void DrawLeg(Snapshot snapshot, GalaxyRouteLeg leg, int index)
        {
            bool current = snapshot.GalaxyTravel != null && snapshot.GalaxyTravel.Active && snapshot.GalaxyTravel.LegIndex == index &&
                snapshot.GalaxyTravel.TargetSector == selected?.Sector && snapshot.GalaxyTravel.TargetObject == selected?.Id;
            string action = leg.Kind == 0 ? "CROSS GATE" : leg.Kind == 1 ? "LAUNCH" : "DOCK";
            string description = leg.Kind == 0 ? leg.SourceName + "  →  " + leg.DestinationName :
                leg.Kind == 1 ? "Depart " + leg.SourceName : "Arrive at " + leg.DestinationName;
            float available = Mathf.Max(150, window.width * .5f - 105);
            float height = Mathf.Max(65, body.CalcHeight(new GUIContent(description), available) + 35);
            Rect row = GUILayoutUtility.GetRect(0, height, GUILayout.ExpandWidth(true));
            Fill(new Rect(row.x + 10, row.y, 1, height), new Color(.17f, .32f, .38f));
            Fill(new Rect(row.x + 6, row.y + 7, 9, 9), current ? Gold : Cyan * .65f);
            GUI.Label(new Rect(row.x + 28, row.y, row.width - 30, 19), (index + 1).ToString("00") + " / " + action + (current ? " · IN PROGRESS" : ""), eyebrow);
            GUI.Label(new Rect(row.x + 28, row.y + 23, row.width - 32, height - 26), description, body);
        }

        /// <summary>Keep active travel cancellation available independently of the selected destination.</summary>
        private void DrawTravelBar(Snapshot snapshot, Rect rect)
        {
            Fill(new Rect(rect.x, rect.y - 7, rect.width, 1), new Color(.16f, .31f, .38f));
            GalaxyTravelView travel = snapshot.GalaxyTravel;
            bool active = travel != null && travel.Active;
            bool meaningfulStatus = travel != null && !string.IsNullOrWhiteSpace(travel.Status) && travel.Status != "Choose a destination.";
            string status = travel != null && travel.Completed ? "Arrival complete. " + travel.Status : meaningfulStatus ? travel.Status :
                "Closing this window keeps active travel running.";
            GUI.Label(new Rect(rect.x + 2, rect.y + 1, rect.width - (active ? 170 : 4), 32), status, small);
            if (active && GUI.Button(new Rect(rect.xMax - 152, rect.y + 2, 152, 32), "CANCEL TRAVEL", hud.ButtonStyle)) client.CancelGalaxyTravel();
            GUI.Label(new Rect(rect.x + 2, rect.yMax - 22, rect.width - 4, 22),
                "Routes use your known gate map. Manual flight cancels automatic travel.", small);
        }

        /// Returns:
        ///     bool: Whether a route belongs to the currently selected destination.
        private bool PlanMatches(GalaxyRoutePlan plan) => plan != null && selected != null && plan.TargetSector == selected.Sector && plan.TargetObject == selected.Id;

        /// <summary>Change catalog type and refresh the first page.</summary>
        private void DrawFilter(Rect rect, string label, uint value)
        {
            Color previous = GUI.backgroundColor;
            if (flags == value) GUI.backgroundColor = Cyan;
            if (GUI.Button(rect, label, hud.ButtonStyle) && flags != value)
            { flags = value; searchPending = false; RequestPage(0); }
            GUI.backgroundColor = previous;
        }

        /// <summary>Request one filtered page from the client's known destination catalog.</summary>
        private void RequestPage(uint page)
        { requestedPage = page; catalogScroll = Vector2.zero; client.QueryGalaxyDestinations(search, page, flags); }

        /// <summary>Move the overlay from its title strip while preserving normal control input.</summary>
        private void HandleDrag()
        {
            Event evt = Event.current;
            Rect handle = new Rect(window.x, window.y, window.width - 120, 82);
            if (evt.type == EventType.MouseDown && evt.button == 0 && handle.Contains(evt.mousePosition))
            { dragging = true; dragOffset = evt.mousePosition - position; evt.Use(); }
            if (dragging && evt.type == EventType.MouseDrag)
            { position = evt.mousePosition - dragOffset; evt.Use(); }
            if (evt.type == EventType.MouseUp) dragging = false;
        }

        /// <summary>Create readable shared-font typography once.</summary>
        private void EnsureStyles()
        {
            if (title != null) return;
            title = new GUIStyle(hud.BodyStyle) { fontSize = 23, fontStyle = FontStyle.Bold, wordWrap = true };
            body = new GUIStyle(hud.BodyStyle) { fontSize = 15, wordWrap = true };
            small = new GUIStyle(hud.SmallStyle) { fontSize = 12, wordWrap = true };
            eyebrow = new GUIStyle(hud.SmallStyle) { fontSize = 10, fontStyle = FontStyle.Bold, wordWrap = false };
            rowButton = new GUIStyle(hud.ButtonStyle);
            searchStyle = new GUIStyle(GUI.skin.textField) { font = hud.BodyStyle.font, fontSize = 14, padding = new RectOffset(11, 9, 8, 6) };
        }

        /// <summary>Draw geometric gate and station symbols without depending on icon fonts.</summary>
        private static void DrawKindIcon(Rect rect, bool gate, Color color)
        {
            Fill(new Rect(rect.x, rect.y, rect.width, 2), color); Fill(new Rect(rect.x, rect.yMax - 2, rect.width, 2), color);
            Fill(new Rect(rect.x, rect.y, 2, rect.height), color); Fill(new Rect(rect.xMax - 2, rect.y, 2, rect.height), color);
            if (gate) Fill(new Rect(rect.center.x - 3, rect.y + 7, 6, rect.height - 14), color);
            else { Fill(new Rect(rect.x + 7, rect.center.y - 1, rect.width - 14, 2), color); Fill(new Rect(rect.center.x - 1, rect.y + 7, 2, rect.height - 14), color); }
        }

        /// <summary>Fill a rectangle without leaking GUI tint into surrounding controls.</summary>
        private static void Fill(Rect rect, Color color)
        { Color previous = GUI.color; GUI.color = color; GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = previous; }
    }
}
