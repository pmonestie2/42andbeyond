using System;
using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>A sector map drawn directly from authoritative server coordinates.</summary>
    public sealed class TacticalSectorMap
    {
        private sealed class Marker
        {
            public uint Id;
            public string Name, Category;
            public Position Position;
            public Color Color;
            public int Shape;
        }

        private readonly HudWindowDrag windowDrag = new HudWindowDrag();
        private readonly SpaceClient client;
        private readonly FlightHud hud;
        private readonly List<Marker> markers = new List<Marker>();
        private readonly List<Rect> labels = new List<Rect>();
        private readonly List<(Rect Bounds, uint Id)> labelHits = new List<(Rect Bounds, uint Id)>();
        private readonly List<(Marker Marker, Vector2 Point, bool Emphasized)> plotted = new List<(Marker Marker, Vector2 Point, bool Emphasized)>();
        private GUIStyle title, label, caption, details;
        private Rect bounds, plot;
        private Vector2 centerKm, dragStart, dragCenter;
        private float radiusKm = 40, refreshAt;
        private bool dragging, panning, showNames, showGrid, resizing;
        private Vector2 windowSize = new Vector2(660, 450), resizeOrigin, resizeStart;
        private int resizeControl;
        private Vector2 initialWindowPosition;
        private bool positioned;
        private int categoryFilter;
        private uint hoveredId;
        private string sector = "";
        private uint selectedId, pressedId;

        /// <summary>Bind the map to foreground selection and presentation controls.</summary>
        public TacticalSectorMap(SpaceClient client, FlightHud hud)
        { this.client = client; this.hud = hud; }

        /// <returns>int: Every server-advertised navigation point retained by this map.</returns>
        public int NavigationCount => markers.Count;

        /// <returns>Rect: The drawable map area in HUD coordinates.</returns>
        public Rect PlotBounds => plot;

        /// Returns:
        ///     Rect: The floating map window in HUD coordinates.
        public Rect WindowBounds => bounds;

        /// <returns>Vector2: The target's GUI position on this sector map.</returns>
        public Vector2 ProjectPosition(Position position) => Project(position);

        /// <summary>
        /// Returns:
        ///     bool: Whether this map point selected an actual advertised marker.
        /// </summary>
        public bool SelectAtPoint(Vector2 point)
        {
            Marker marker = Hovered(point);
            if (marker == null) return false;
            Select(marker); return true;
        }

        /// <summary>
        /// Returns:
        ///     bool: Whether this GUI point is owned by the tactical map.
        /// </summary>
        public bool ContainsPointer(Vector2 point) => dragging || resizing || bounds.Contains(point);

        /// <summary>Draw pan, zoom, markers, selection details, and sector navigation controls.</summary>
        public void Draw(Snapshot snapshot)
        {
            EnsureStyles();
            float width = Screen.width / hud.Scale, height = Screen.height / hud.Scale;
            float mapWidth = Mathf.Min(windowSize.x, width - 8), mapHeight = Mathf.Min(windowSize.y, height - 8);
            if (!positioned)
            { initialWindowPosition = new Vector2((width - mapWidth) * .5f, Mathf.Max(4, Mathf.Min(200, height - mapHeight - 180))); positioned = true; }
            bounds = windowDrag.Handle(new Rect(initialWindowPosition, new Vector2(mapWidth, mapHeight)), width, height);
            HandleResize(width, height);
            plot = new Rect(bounds.x + 14, bounds.y + 86, bounds.width - 28, bounds.height - 166);
            bool changedSector = sector != snapshot.Sector;
            if (changedSector || Time.unscaledTime >= refreshAt)
            {
                RefreshMarkers(snapshot);
                selectedId = client.Target?.Id ?? 0;
                refreshAt = Time.unscaledTime + .2f;
                if (changedSector) { sector = snapshot.Sector; FitSector(snapshot); }
            }
            bool previousEnabled = GUI.enabled;
            bool blocked = !previousEnabled || hud.RetainedOwnsPointer || client.ChatOpen || hud.GroupInviteOpen;
            // Keep the chart readable while another window owns the pointer.
            GUI.enabled = previousEnabled && (!blocked || Event.current.type == EventType.Repaint ||
                Event.current.type == EventType.Layout);
            RebuildHitRegions();
            if (!blocked) HandleInput();
            else if (Event.current.rawType == EventType.MouseUp) { dragging = panning = false; pressedId = 0; }
            RebuildHitRegions();
            Fill(bounds, new Color(.018f, .028f, .038f, .42f));
            Fill(new Rect(bounds.x, bounds.y, bounds.width, 84), new Color(.018f, .028f, .038f, .55f));
            Fill(new Rect(bounds.x, bounds.y, bounds.width, 2), new Color(.31f, .57f, .67f, .8f));
            HudWindowDrag.DrawGrip(bounds);
            GUI.Label(new Rect(bounds.x + 40, bounds.y + 10, bounds.width - 220, 26), snapshot.Sector, title);
            if (GUI.Button(new Rect(bounds.xMax - 132, bounds.y + 9, 88, 27), "Galaxy", hud.ButtonStyle)) client.OpenGalaxyTravel();
            if (GUI.Button(new Rect(bounds.xMax - 39, bounds.y + 9, 32, 27), "X", hud.ButtonStyle)) client.SetChart(false);
            float x = bounds.x + 14, y = bounds.y + 48;
            if (GUI.Button(new Rect(x, y, 86, 29), "My ship", hud.ButtonStyle))
            { centerKm = Coordinate(snapshot.Position); radiusKm = 40; }
            if (GUI.Button(new Rect(x + 93, y, 96, 29), "Fit sector", hud.ButtonStyle)) FitSector(snapshot);
            if (GUI.Button(new Rect(x + 197, y, 31, 29), "−", hud.ButtonStyle)) Zoom(1.35f, plot.center);
            if (GUI.Button(new Rect(x + 235, y, 31, 29), "+", hud.ButtonStyle)) Zoom(1 / 1.35f, plot.center);
            string[] filters = { "All", "Gates", "Stations" };
            if (bounds.width < 740)
            {
                if (GUI.Button(new Rect(x + 280, y, 106, 29), filters[categoryFilter], hud.ButtonStyle))
                    categoryFilter = (categoryFilter + 1) % filters.Length;
            }
            else for (int index = 0; index < filters.Length; index++)
            {
                Color old = GUI.backgroundColor;
                if (categoryFilter == index) GUI.backgroundColor = new Color(.35f, .75f, .84f);
                float offset = index == 0 ? 280 : index == 1 ? 351 : 430;
                float buttonWidth = index == 0 ? 64 : index == 1 ? 72 : 92;
                if (GUI.Button(new Rect(x + offset, y, buttonWidth, 29), filters[index], hud.ButtonStyle)) categoryFilter = index;
                GUI.backgroundColor = old;
            }
            showNames = GUI.Toggle(new Rect(bounds.xMax - 132, y + 4, 68, 24), showNames, "Names");
            showGrid = GUI.Toggle(new Rect(bounds.xMax - 64, y + 4, 52, 24), showGrid, "Grid");
            Fill(plot, new Color(.008f, .017f, .024f, .12f));
            if (showGrid) DrawGrid();
            else GUI.Label(new Rect(plot.x + 9, plot.y + 7, 100, 20), "NORTH ↑", caption);
            Marker hovered = Hovered(Event.current.mousePosition);
            hoveredId = hovered?.Id ?? 0;
            RebuildHitRegions();
            Marker selected = markers.FirstOrDefault(marker => marker.Id == selectedId);
            plotted.Clear();
            if (selected != null)
                ClippedLine(Project(snapshot.Position), Project(selected.Position), plot,
                    new Color(.86f, .65f, .3f, .5f));
            Rect markerArea = new Rect(plot.x + 13, plot.y + 13, plot.width - 26, plot.height - 26);
            foreach (Marker marker in markers)
            {
                Vector2 point = Project(marker.Position);
                if (!VisibleMarker(marker) || !markerArea.Contains(point)) continue;
                plotted.Add((marker, point, marker.Id == selectedId || hovered?.Id == marker.Id));
            }
            // Complete every glyph first; labels must never be overwritten by a later marker stroke.
            foreach (var item in plotted.OrderBy(item => item.Marker.Id == selectedId ? 2 : item.Emphasized ? 1 : 0))
            {
                float size = item.Emphasized ? 8 : item.Marker.Shape == 2 ? 5 : 6;
                Glyph(item.Point, item.Marker.Shape, item.Marker.Color, size);
                if (item.Marker.Id == selectedId)
                { Ring(item.Point, 15, new Color(1f, .83f, .39f, .95f));
                  Ring(item.Point, 20, new Color(1f, .83f, .39f, .42f)); }
                else if (item.Emphasized) Ring(item.Point, 12,
                    new Color(item.Marker.Color.r, item.Marker.Color.g, item.Marker.Color.b, .7f));
            }
            Vector2 own = Project(snapshot.Position);
            if (markerArea.Contains(own))
            {
                Vector3 heading = SpaceClient.ConvertRotation(snapshot.Orientation) * Vector3.forward;
                Vector2 nose = new Vector2(heading.x, -heading.z).normalized;
                if (nose.sqrMagnitude < .01f) nose = Vector2.up;
                Vector2 wing = new Vector2(-nose.y, nose.x);
                Color blue = new Color(.35f, .68f, 1f);
                Line(own + nose * 11, own - nose * 6 + wing * 6, blue, 3);
                Line(own + nose * 11, own - nose * 6 - wing * 6, blue, 3);
                Line(own - nose * 6 - wing * 6, own - nose * 6 + wing * 6, blue, 2);
                Ring(own, 16, new Color(.35f, .68f, 1f, .3f));
            }
            // Reserve the selected and hovered label positions before ordinary navigation names.
            foreach (var item in plotted)
                DrawLabel(item.Marker, item.Emphasized);
            DrawFooter(snapshot, selected);
            DrawResizeGrip();
            GUI.enabled = previousEnabled;
        }

        /// <summary>Resize the floating map independently of its pan and move gestures.</summary>
        private void HandleResize(float canvasWidth, float canvasHeight)
        {
            Event input = Event.current;
            int control = GUIUtility.GetControlID(FocusType.Passive);
            Rect grip = new Rect(bounds.xMax - 24, bounds.yMax - 24, 24, 24);
            if (GUI.enabled && !hud.RetainedOwnsPointer && !client.ChatOpen && !hud.GroupInviteOpen &&
                input.type == EventType.MouseDown && input.button == 0 && grip.Contains(input.mousePosition))
            {
                resizing = true; resizeOrigin = bounds.size; resizeStart = input.mousePosition;
                resizeControl = control; GUIUtility.hotControl = control; input.Use();
            }
            if (resizing && input.type == EventType.MouseDrag)
            {
                Vector2 requested = resizeOrigin + input.mousePosition - resizeStart;
                windowSize = new Vector2(
                    Mathf.Clamp(requested.x, Mathf.Min(560, canvasWidth - 8), Mathf.Max(1, canvasWidth - bounds.x - 4)),
                    Mathf.Clamp(requested.y, Mathf.Min(320, canvasHeight - 8), Mathf.Max(1, canvasHeight - bounds.y - 4)));
                bounds.size = windowSize; input.Use();
            }
            if (resizing && (input.rawType == EventType.MouseUp || input.rawType == EventType.Ignore))
            {
                resizing = false;
                if (GUIUtility.hotControl == resizeControl) GUIUtility.hotControl = 0;
                resizeControl = 0; input.Use();
            }
        }

        /// <summary>Draw a visible bottom-right resize handle without obscuring map actions.</summary>
        private void DrawResizeGrip()
        {
            for (int inset = 5; inset <= 17; inset += 6)
                Line(new Vector2(bounds.xMax - inset, bounds.yMax - 4),
                    new Vector2(bounds.xMax - 4, bounds.yMax - inset), new Color(.55f, .85f, 1f, .85f), 2);
            GUI.Label(new Rect(bounds.xMax - 24, bounds.yMax - 24, 24, 24),
                new GUIContent("", "Drag to resize map · Tab to close"));
        }

        /// <summary>Create readable styles once, sharing the user's HUD scale.</summary>
        private void EnsureStyles()
        {
            if (title != null) return;
            title = new GUIStyle(hud.BodyStyle) { fontSize = 17, fontStyle = FontStyle.Bold, wordWrap = false };
            label = new GUIStyle(hud.SmallStyle) { fontSize = 14, wordWrap = true, alignment = TextAnchor.UpperLeft };
            caption = new GUIStyle(hud.SmallStyle) { fontSize = 11, wordWrap = false };
            details = new GUIStyle(hud.BodyStyle) { fontSize = 13, wordWrap = true };
        }

        /// Returns:
        ///     bool: Whether an advertised object belongs on the navigation-only map.
        public static bool IsNavigationTarget(MapObject item)
        {
            if (item == null) return false;
            string name = item.Name ?? "";
            if (name.IndexOf("Border Demarcation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("BorderDemarcation", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (item.Kind == 11 || item.Kind == 12) return true;
            return item.Navigable && item.Kind != 25 && item.Kind != 38 &&
                !name.StartsWith("Asteroid", StringComparison.OrdinalIgnoreCase);
        }

        /// Returns:
        ///     bool: Whether a destination remains available for map selection at the current zoom.
        public bool ContainsNavigationTarget(uint id) => markers.Any(marker => marker.Id == id);

        /// <summary>Keep every valid navigation point at any distance, excluding scenery and contacts.</summary>
        private void RefreshMarkers(Snapshot snapshot)
        {
            markers.Clear();
            foreach (MapObject item in snapshot.Objects)
            {
                if (!IsNavigationTarget(item)) continue;
                markers.Add(new Marker { Id = item.Id, Name = item.Name, Position = item.Position,
                    Category = item.Kind == 12 ? "STATION" : item.Kind == 11 ? "GATE" : "NAVIGATION",
                    Color = item.Kind == 12 ? new Color(.96f, .77f, .4f) : item.Kind == 11 ? new Color(.72f, .56f, 1) : new Color(.35f, .77f, .88f),
                    Shape = item.Kind == 12 ? 0 : item.Kind == 11 ? 1 : 2 });
            }
        }

        /// Returns:
        ///     bool: Whether the current category filter includes this known navigation point.
        private bool VisibleMarker(Marker marker) => categoryFilter == 0 ||
            categoryFilter == 1 && marker.Shape == 1 || categoryFilter == 2 && marker.Shape == 0;

        /// <summary>Fit all advertised navigation points and the pilot into the map viewport.</summary>
        private void FitSector(Snapshot snapshot)
        {
            Vector2 minimum = Coordinate(snapshot.Position), maximum = minimum;
            foreach (Marker marker in markers)
            { Vector2 point = Coordinate(marker.Position); minimum = Vector2.Min(minimum, point); maximum = Vector2.Max(maximum, point); }
            centerKm = (minimum + maximum) * .5f;
            Vector2 span = maximum - minimum;
            float aspect = plot.width / Mathf.Max(1, plot.height);
            radiusKm = Mathf.Clamp(Mathf.Max(span.y, span.x / aspect) * .62f, 5, 50000);
        }

        /// <summary>
        /// Returns:
        ///     Vector2: Sector east and north coordinates in kilometers.
        /// </summary>
        private static Vector2 Coordinate(Position position) => new Vector2(position.Y, position.X) / 1000f;

        /// <summary>
        /// Returns:
        ///     Vector2: GUI position projected independently of the flight camera and altitude.
        /// </summary>
        private Vector2 Project(Position position)
        {
            Vector2 relative = Coordinate(position) - centerKm;
            float scale = plot.height / (radiusKm * 2);
            return plot.center + new Vector2(relative.x, -relative.y) * scale;
        }

        /// <summary>Zoom around the pointer, preserving the inspected sector coordinate.</summary>
        private void Zoom(float factor, Vector2 pointer)
        {
            float old = radiusKm;
            radiusKm = Mathf.Clamp(radiusKm * factor, .5f, 50000);
            Vector2 offset = pointer - plot.center;
            centerKm += new Vector2(offset.x, -offset.y) * (old - radiusKm) * 2 / Mathf.Max(1, plot.height);
        }

        /// <summary>Keep a pressed marker stable through small pointer jitter; pan only past the drag threshold.</summary>
        private void HandleInput() => HandlePointer(Event.current);

        /// <summary>Apply one IMGUI pointer event; explicit input also permits isolated click tests.</summary>
        private void HandlePointer(Event input)
        {
            if (input == null) return;
            if (input.type == EventType.ScrollWheel && plot.Contains(input.mousePosition))
            { Zoom(Mathf.Exp(input.delta.y * .09f), input.mousePosition); Consume(input); }
            if (input.type == EventType.MouseDown && plot.Contains(input.mousePosition) && input.button <= 2)
            {
                dragging = true; panning = false; dragStart = input.mousePosition; dragCenter = centerKm;
                pressedId = input.button == 0 ? Hovered(input.mousePosition)?.Id ?? 0 : 0;
                Consume(input);
            }
            if (input.type == EventType.MouseDrag && dragging)
            {
                Vector2 delta = input.mousePosition - dragStart;
                if (delta.sqrMagnitude >= 49) panning = true;
                if (panning)
                    centerKm = dragCenter + new Vector2(-delta.x, delta.y) * radiusKm * 2 / Mathf.Max(1, plot.height);
                Consume(input);
            }
            if (input.rawType == EventType.MouseUp && dragging)
            {
                if (!panning && pressedId != 0 && Vector2.Distance(dragStart, input.mousePosition) < 7)
                { Marker pressed = markers.FirstOrDefault(marker => marker.Id == pressedId);
                  if (pressed != null) Select(pressed); }
                dragging = panning = false; pressedId = 0; Consume(input);
            }
        }

        /// <summary>Consume only events dispatched by a live OnGUI pass.</summary>
        private static void Consume(Event input) { if (Event.current == input) input.Use(); }

        /// <summary>
        /// Returns:
        ///     Marker: The nearest visible marker with a generous selection area.
        /// </summary>
        private Marker Hovered(Vector2 pointer)
        {
            if (!plot.Contains(pointer)) return null;
            foreach (var hit in labelHits)
                if (hit.Bounds.Contains(pointer)) return markers.FirstOrDefault(marker => marker.Id == hit.Id);
            Rect visible = new Rect(plot.x + 13, plot.y + 13, plot.width - 26, plot.height - 26);
            Marker best = null; float distance = 22;
            foreach (Marker marker in markers)
            {
                Vector2 projected = Project(marker.Position);
                if (!VisibleMarker(marker) || !visible.Contains(projected)) continue;
                float next = Vector2.Distance(pointer, projected);
                if (next >= distance) continue;
                best = marker; distance = next;
            }
            return best;
        }

        /// <summary>Publish a selected marker through the existing authoritative target path.</summary>
        private void Select(Marker marker)
        {
            selectedId = marker.Id;
            client?.Select(marker.Id);
        }

        /// <summary>Draw a coordinate grid with a scale chosen for the current zoom.</summary>
        private void DrawGrid()
        {
            float requested = radiusKm / 4;
            float magnitude = Mathf.Pow(10, Mathf.Floor(Mathf.Log10(Mathf.Max(.01f, requested))));
            float step = magnitude * (requested / magnitude > 5 ? 10 : requested / magnitude > 2 ? 5 : 2);
            float pixelStep = plot.height * step / (radiusKm * 2);
            float scale = plot.height / (radiusKm * 2);
            float firstX = plot.center.x - Mathf.Repeat(centerKm.x, step) * scale;
            for (float x = firstX; x < plot.xMax; x += pixelStep) if (x >= plot.xMin) Line(new Vector2(x, plot.y), new Vector2(x, plot.yMax), new Color(.18f, .36f, .43f, .3f));
            for (float x = firstX - pixelStep; x > plot.xMin; x -= pixelStep) Line(new Vector2(x, plot.y), new Vector2(x, plot.yMax), new Color(.18f, .36f, .43f, .3f));
            float firstY = plot.center.y + Mathf.Repeat(centerKm.y, step) * scale;
            for (float y = firstY; y < plot.yMax; y += pixelStep) if (y >= plot.y) Line(new Vector2(plot.x, y), new Vector2(plot.xMax, y), new Color(.18f, .36f, .43f, .3f));
            for (float y = firstY - pixelStep; y > plot.y; y -= pixelStep) Line(new Vector2(plot.x, y), new Vector2(plot.xMax, y), new Color(.18f, .36f, .43f, .3f));
            GUI.Label(new Rect(plot.x + 9, plot.y + 7, 70, 20), "NORTH ↑", caption);
            GUI.Label(new Rect(plot.xMax - 112, plot.yMax - 26, 100, 20), step.ToString("0.##") + " km grid", caption);
            float barWidth = Mathf.Min(pixelStep, plot.width * .25f);
            Vector2 bar = new Vector2(plot.x + 12, plot.yMax - 12);
            Line(bar, bar + Vector2.right * barWidth, new Color(.55f, .8f, .89f), 2);
            Line(bar + Vector2.up * -4, bar + Vector2.up * 4, new Color(.55f, .8f, .89f));
            Line(bar + Vector2.right * barWidth + Vector2.up * -4,
                bar + Vector2.right * barWidth + Vector2.up * 4, new Color(.55f, .8f, .89f));
            GUI.Label(new Rect(bar.x, bar.y - 23, 110, 20), (barWidth / scale).ToString("0.##") + " km", caption);
        }

        /// <summary>Build current-frame label hit boxes before processing any pointer event.</summary>
        private void RebuildHitRegions()
        {
            labels.Clear(); labelHits.Clear();
            Rect visible = new Rect(plot.x + 13, plot.y + 13, plot.width - 26, plot.height - 26);
            foreach (Marker marker in markers.OrderBy(item => item.Id == selectedId ? 0 : 1)
                .ThenBy(item => item.Name, StringComparer.Ordinal))
            {
                Vector2 point = Project(marker.Position);
                if (!VisibleMarker(marker) || !visible.Contains(point) ||
                    !showNames && marker.Id != selectedId && marker.Id != hoveredId) continue;
                PlaceLabel(marker, point);
            }
        }

        /// <summary>Reserve one nonoverlapping label that stays inside the current plot.</summary>
        private void PlaceLabel(Marker marker, Vector2 point)
        {
            float width = Mathf.Min(220, Mathf.Min(plot.width * .45f,
                label.CalcSize(new GUIContent(marker.Name)).x + 16));
            float height = label.CalcHeight(new GUIContent(marker.Name), width - 12) + 10;
            foreach (Vector2 offset in new[] { new Vector2(11, -height - 3), new Vector2(11, 9), new Vector2(-width - 11, 9), new Vector2(-width - 11, -height - 3) })
            {
                Rect rect = new Rect(point + offset, new Vector2(width, height));
                if (rect.xMin < plot.xMin + 3 || rect.xMax > plot.xMax - 3 ||
                    rect.yMin < plot.yMin + 27 || rect.yMax > plot.yMax - 44 ||
                    labels.Any(other => other.Overlaps(rect))) continue;
                labels.Add(rect);
                labelHits.Add((rect,marker.Id));
                break;
            }
        }

        /// <summary>Draw an already reserved label so its clickable area matches the visible text.</summary>
        private void DrawLabel(Marker marker, bool emphasized)
        {
            int index = labelHits.FindIndex(hit => hit.Id == marker.Id);
            if (index < 0) return;
            Rect rect = labelHits[index].Bounds;
            bool selected = marker.Id == selectedId;
            Fill(rect, selected ? new Color(.21f, .16f, .06f, .98f) :
                new Color(.012f, .024f, .034f, emphasized ? .95f : .65f));
            Fill(new Rect(rect.x, rect.y, selected ? 3 : 2, rect.height),
                selected ? new Color(1f, .83f, .39f) : marker.Color);
            GUI.Label(new Rect(rect.x + 7, rect.y + 4, rect.width - 12, rect.height - 8), marker.Name, label);
        }

        /// <summary>Show full hover/selection details, target actions, legend, and navigation help.</summary>
        private void DrawFooter(Snapshot snapshot, Marker marker)
        {
            float y = plot.yMax + 9;
            GUI.Label(new Rect(bounds.x + 16, y, bounds.width - 240, 24),
                marker == null ? "Select a destination" : "Destination: " + marker.Name, details);
            GUI.Label(new Rect(bounds.x + 16, y + 26, bounds.width - 200, 22), marker == null ?
                "Click a nav icon · drag empty space to pan · scroll to zoom" :
                marker.Category + "  ·  " + (marker.Position.Distance(snapshot.Position) / 1000).ToString("0.00") + " km", caption);
            if (marker != null)
            {
                if (GUI.Button(new Rect(bounds.xMax - 210, y, 92, 29), "Center", hud.ButtonStyle)) centerKm = Coordinate(marker.Position);
                if (GUI.Button(new Rect(bounds.xMax - 110, y, 96, 29), "Warp here", hud.ButtonStyle))
                { Select(marker); client.WarpSelected(); }
            }
            GUI.Label(new Rect(plot.xMax - 170, plot.yMax - 25, 160, 20),
                (radiusKm * 2).ToString("0.#") + " km north–south", caption);
        }

        /// <summary>Draw distinguishable marker silhouettes without allocating textures.</summary>
        private static void Glyph(Vector2 p, int shape, Color color, float size)
        {
            if (shape == 0) { Fill(new Rect(p.x-size,p.y-size,size*2,size*2),color); Fill(new Rect(p.x-size+2,p.y-size+2,size*2-4,size*2-4),new Color(.01f,.02f,.03f)); }
            else if (shape == 1 || shape == 4) { Line(p+Vector2.up*size,p+Vector2.right*size,color,2); Line(p+Vector2.right*size,p-Vector2.up*size,color,2); Line(p-Vector2.up*size,p-Vector2.right*size,color,2); Line(p-Vector2.right*size,p+Vector2.up*size,color,2); if(shape==4) Fill(new Rect(p.x-2,p.y-2,4,4),color); }
            else if (shape == 3 || shape == 5) { Line(p+new Vector2(0,-size),p+new Vector2(size,size),color,2); Line(p+new Vector2(0,-size),p+new Vector2(-size,size),color,2); if(shape==5)Line(p+new Vector2(-size,size),p+new Vector2(size,size),color,2); }
            else
            {
                Line(p + new Vector2(-size, 0), p + new Vector2(0, -size), color, 1.5f);
                Line(p + new Vector2(0, -size), p + new Vector2(size, 0), color, 1.5f);
                Line(p + new Vector2(-size + 1, 0), p + new Vector2(-size + 1, size), color, 1.5f);
                Line(p + new Vector2(size - 1, 0), p + new Vector2(size - 1, size), color, 1.5f);
                Line(p + new Vector2(-size + 1, size), p + new Vector2(size - 1, size), color, 1.5f);
            }
        }

        /// <summary>Draw a subtle selection ring around a map point.</summary>
        private static void Ring(Vector2 center, float radius, Color color)
        {
            for (int index = 0; index < 24; index++)
            { float a=index*Mathf.PI/12, b=(index+1)*Mathf.PI/12; Line(center+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*radius,center+new Vector2(Mathf.Cos(b),Mathf.Sin(b))*radius,color); }
        }

        /// <summary>Draw one map stroke while preserving the caller's GUI scale.</summary>
        private static void Line(Vector2 start, Vector2 end, Color color, float thickness = 1)
        {
            Matrix4x4 matrix = GUI.matrix;
            Vector2 delta = end-start;
            GUI.matrix = matrix * Matrix4x4.TRS(new Vector3(start.x, start.y, 0),
                Quaternion.Euler(0, 0, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg), Vector3.one);
            Fill(new Rect(0, -thickness * .5f, delta.magnitude, thickness), color);
            GUI.matrix = matrix;
        }

        /// <summary>Clip a route to the map surface before drawing it in root GUI coordinates.</summary>
        private static void ClippedLine(Vector2 start, Vector2 end, Rect clip, Color color)
        {
            Vector2 delta = end - start;
            float near = 0, far = 1;
            if (!ClipPlane(-delta.x, start.x - clip.xMin, ref near, ref far) ||
                !ClipPlane(delta.x, clip.xMax - start.x, ref near, ref far) ||
                !ClipPlane(-delta.y, start.y - clip.yMin, ref near, ref far) ||
                !ClipPlane(delta.y, clip.yMax - start.y, ref near, ref far)) return;
            Line(start + delta * near, start + delta * far, color);
        }

        /// <summary>
        /// Returns:
        ///     bool: Whether a segment intersects this rectangle half-plane.
        /// </summary>
        private static bool ClipPlane(float direction, float distance, ref float near, ref float far)
        {
            if (Mathf.Abs(direction) < .00001f) return distance >= 0;
            float amount = distance / direction;
            if (direction < 0) near = Mathf.Max(near, amount);
            else far = Mathf.Min(far, amount);
            return near <= far;
        }

        /// <summary>Fill a map surface while preserving shared GUI tint.</summary>
        private static void Fill(Rect rect, Color color)
        { Color old=GUI.color; GUI.color=color; GUI.DrawTexture(rect,Texture2D.whiteTexture); GUI.color=old; }

        /// <summary>Release a map pan when the pilot returns to flight.</summary>
        public void CancelInteraction()
        {
            dragging = panning = resizing = false; pressedId = 0;
            if (resizeControl != 0 && GUIUtility.hotControl == resizeControl) GUIUtility.hotControl = 0;
            resizeControl = 0;
        }
    }

    public sealed partial class FlightHud
    {
        private TacticalSectorMap tacticalMap;

        /// <summary>Keep the tactical surface active alongside the persistent flight instruments.</summary>
        private void DrawTacticalMap(Snapshot snapshot)
        {
            if (tacticalMap == null) tacticalMap = new TacticalSectorMap(client, this);
            tacticalMap.Draw(snapshot);
        }

        /// <summary>Release an in-progress map pan when leaving the chart.</summary>
        internal void EndChartInteraction() { tacticalMap?.CancelInteraction(); }
    }
}
