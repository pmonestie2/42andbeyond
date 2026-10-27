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
            public bool Actor, Resource, Navigation;
        }

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
        private bool dragging, showResources = true;
        private string sector = "";
        private uint selectedId, pressedId;

        /// <summary>Bind the map to foreground selection and presentation controls.</summary>
        public TacticalSectorMap(SpaceClient client, FlightHud hud)
        { this.client = client; this.hud = hud; }

        /// <returns>int: Every server-advertised navigation point retained by this map.</returns>
        public int NavigationCount => markers.Count(marker => marker.Navigation);

        /// <returns>Rect: The drawable map area in HUD coordinates.</returns>
        public Rect PlotBounds => plot;

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
        public bool ContainsPointer(Vector2 point) => dragging || bounds.Contains(point);

        /// <summary>Draw pan, zoom, markers, selection details, and sector navigation controls.</summary>
        public void Draw(Snapshot snapshot)
        {
            EnsureStyles();
            float width = Screen.width / hud.Scale, height = Screen.height / hud.Scale;
            bounds = new Rect(335, 104, Mathf.Max(420, width - 670), Mathf.Max(330, height - 245));
            plot = new Rect(bounds.x + 12, bounds.y + 77, bounds.width - 24, bounds.height - 183);
            bool changedSector = sector != snapshot.Sector;
            if (changedSector || Time.unscaledTime >= refreshAt)
            {
                RefreshMarkers(snapshot);
                selectedId = client.Target?.Id ?? client.TargetActor?.Id ?? selectedId;
                refreshAt = Time.unscaledTime + .2f;
                if (changedSector) { sector = snapshot.Sector; FitSector(snapshot); }
            }
            bool previousEnabled = GUI.enabled;
            bool blocked = !previousEnabled || hud.RetainedOwnsPointer || client.ChatOpen || hud.GroupInviteOpen;
            GUI.enabled = !blocked;
            if (!blocked) HandleInput();
            else if (Event.current.rawType == EventType.MouseUp) dragging = false;
            Fill(bounds, new Color(.012f, .022f, .032f, .99f));
            Fill(new Rect(bounds.x, bounds.y, bounds.width, 2), new Color(.35f, .72f, .82f, .8f));
            GUI.Label(new Rect(bounds.x + 15, bounds.y + 10, 180, 24), "TACTICAL SECTOR MAP", title);
            GUI.Label(new Rect(bounds.xMax - 180, bounds.y + 13, 165, 20),
                radiusKm.ToString("0.#") + " km radius", caption);
            float x = bounds.x + 13, y = bounds.y + 40;
            if (GUI.Button(new Rect(x, y, 86, 27), "My ship", hud.ButtonStyle)) centerKm = Coordinate(snapshot.Position);
            if (GUI.Button(new Rect(x + 94, y, 88, 27), "Fit sector", hud.ButtonStyle)) FitSector(snapshot);
            if (GUI.Button(new Rect(x + 190, y, 31, 27), "−", hud.ButtonStyle)) Zoom(1.35f, plot.center);
            if (GUI.Button(new Rect(x + 227, y, 31, 27), "+", hud.ButtonStyle)) Zoom(1 / 1.35f, plot.center);
            if (GUI.Button(new Rect(x + 271, y, 118, 27), showResources ? "Resources: on" : "Resources: off", hud.ButtonStyle))
                showResources = !showResources;
            Fill(plot, new Color(.008f, .017f, .025f, 1));
            DrawGrid();
            Marker hovered = Hovered(Event.current.mousePosition);
            Marker selected = markers.FirstOrDefault(marker => marker.Id == selectedId);
            labels.Clear(); labelHits.Clear(); plotted.Clear();
            if (selected != null)
                ClippedLine(Project(snapshot.Position), Project(selected.Position), plot,
                    new Color(.86f, .65f, .3f, .5f));
            Rect markerArea = new Rect(plot.x + 13, plot.y + 13, plot.width - 26, plot.height - 26);
            foreach (Marker marker in markers)
            {
                if (marker.Resource && !showResources) continue;
                Vector2 point = Project(marker.Position);
                if (!markerArea.Contains(point)) continue;
                plotted.Add((marker, point, marker.Id == selectedId || hovered?.Id == marker.Id));
            }
            // Complete every glyph first; labels must never be overwritten by a later marker stroke.
            foreach (var item in plotted.OrderBy(item => item.Marker.Id == selectedId ? 2 : item.Emphasized ? 1 : 0))
            {
                float size = item.Emphasized ? 7 : item.Marker.Shape == 2 ? 2.5f : 4.5f;
                Glyph(item.Point, item.Marker.Shape, item.Marker.Color, size);
                if (item.Emphasized) Ring(item.Point, 11,
                    new Color(item.Marker.Color.r, item.Marker.Color.g, item.Marker.Color.b, .65f));
            }
            Vector2 own = Project(snapshot.Position);
            if (markerArea.Contains(own))
            { Glyph(own, 3, Color.white, 7); Ring(own, 13, new Color(.7f, .9f, 1, .35f)); }
            // Reserve the selected and hovered label positions before ordinary navigation names.
            foreach (var item in plotted.OrderBy(item => item.Marker.Id == selectedId ? 0 : item.Emphasized ? 1 : item.Marker.Navigation ? 2 : 3))
                if (item.Marker.Navigation || item.Emphasized || labels.Count < 7)
                    DrawLabel(item.Marker, item.Point, item.Emphasized);
            DrawFooter(snapshot, hovered ?? selected);
            GUI.enabled = previousEnabled;
        }

        /// <summary>Create readable styles once, sharing the user's HUD scale.</summary>
        private void EnsureStyles()
        {
            if (title != null) return;
            title = new GUIStyle(hud.BodyStyle) { fontSize = 14, fontStyle = FontStyle.Bold, wordWrap = false };
            label = new GUIStyle(hud.SmallStyle) { fontSize = 13, wordWrap = true, alignment = TextAnchor.UpperLeft };
            caption = new GUIStyle(hud.SmallStyle) { fontSize = 11, wordWrap = false };
            details = new GUIStyle(hud.BodyStyle) { fontSize = 13, wordWrap = true };
        }

        /// <summary>Keep every known navigation point, without a distance or row limit.</summary>
        private void RefreshMarkers(Snapshot snapshot)
        {
            markers.Clear();
            foreach (MapObject item in snapshot.Objects)
            {
                bool nav = item.Navigable || item.Kind == 11 || item.Kind == 12 || item.Kind == 37;
                if (!nav && item.Kind != 25 && item.Kind != 38) continue;
                markers.Add(new Marker { Id = item.Id, Name = item.Name, Position = item.Position,
                    Category = item.Kind == 12 ? "STATION" : item.Kind == 11 ? "GATE" : item.Kind == 38 ? "RESOURCE" : item.Kind == 25 ? "WRECK" : "NAVIGATION",
                    Color = item.Kind == 12 ? new Color(.96f, .77f, .4f) : item.Kind == 11 ? new Color(.72f, .56f, 1) :
                        item.Kind == 38 ? new Color(.39f, .81f, .57f) : item.Kind == 25 ? new Color(.55f, .61f, .65f) : new Color(.35f, .77f, .88f),
                    Shape = item.Kind == 12 ? 0 : item.Kind == 11 ? 1 : item.Kind == 38 ? 4 : 2,
                    Navigation = nav, Resource = item.Kind == 38 });
            }
            foreach (ShipActor actor in snapshot.Players.Concat(snapshot.Mobs).Where(actor => actor.HasPosition))
                markers.Add(new Marker { Id = actor.Id, Name = actor.Name, Position = actor.Position, Actor = true,
                    Category = actor.IsPlayer ? "PILOT" : actor.Hostile ? "HOSTILE" : "NEUTRAL SHIP",
                    Shape = actor.IsPlayer ? 3 : 5,
                    Color = actor.IsPlayer ? new Color(.4f, .76f, 1) : actor.Hostile ? new Color(1, .36f, .29f) : new Color(.91f, .77f, .39f) });
        }

        /// <summary>Fit all advertised navigation points and the pilot into the map viewport.</summary>
        private void FitSector(Snapshot snapshot)
        {
            Vector2 minimum = Coordinate(snapshot.Position), maximum = minimum;
            foreach (Marker marker in markers.Where(marker => marker.Navigation))
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

        /// <summary>Pan empty space or use the middle/right mouse button, and select markers on click.</summary>
        private void HandleInput()
        {
            Event input = Event.current;
            if (input.type == EventType.ScrollWheel && plot.Contains(input.mousePosition))
            { Zoom(Mathf.Exp(input.delta.y * .09f), input.mousePosition); input.Use(); }
            if (input.type == EventType.MouseDown && plot.Contains(input.mousePosition) && input.button <= 2)
            {
                dragging = true; dragStart = input.mousePosition; dragCenter = centerKm;
                pressedId = input.button == 0 ? Hovered(input.mousePosition)?.Id ?? 0 : 0;
                input.Use();
            }
            if (input.type == EventType.MouseDrag && dragging)
            {
                Vector2 delta = input.mousePosition - dragStart;
                centerKm = dragCenter + new Vector2(-delta.x, delta.y) * radiusKm * 2 / Mathf.Max(1, plot.height);
                input.Use();
            }
            if (input.rawType == EventType.MouseUp && dragging)
            {
                if (pressedId != 0 && Vector2.Distance(dragStart, input.mousePosition) < 5)
                {
                    SelectAtPoint(input.mousePosition);
                }
                dragging = false; input.Use();
            }
        }

        /// <summary>
        /// Returns:
        ///     Marker: The nearest visible marker with a generous selection area.
        /// </summary>
        private Marker Hovered(Vector2 pointer)
        {
            if (!plot.Contains(pointer)) return null;
            Marker best = null; float distance = 16;
            foreach (Marker marker in markers)
            {
                if (marker.Resource && !showResources) continue;
                float next = Vector2.Distance(pointer, Project(marker.Position));
                if (next >= distance) continue;
                best = marker; distance = next;
            }
            if (best != null) return best;
            foreach (var hit in labelHits)
                if (hit.Bounds.Contains(pointer)) return markers.FirstOrDefault(marker => marker.Id == hit.Id);
            return best;
        }

        /// <summary>Publish a selected marker through the existing authoritative target path.</summary>
        private void Select(Marker marker)
        {
            selectedId = marker.Id;
            if (marker.Actor) hud.SelectContact(marker.Id);
            else if (marker.Category == "WRECK") hud.SelectWreck(marker.Id);
            else client.Select(marker.Id);
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

        /// <summary>Keep complete wrapped names inside the map and avoid label collisions.</summary>
        private void DrawLabel(Marker marker, Vector2 point, bool emphasized)
        {
            float width = Mathf.Min(164, plot.width * .4f);
            float height = label.CalcHeight(new GUIContent(marker.Name), width - 12) + 10;
            foreach (Vector2 offset in new[] { new Vector2(11, -height - 3), new Vector2(11, 9), new Vector2(-width - 11, 9), new Vector2(-width - 11, -height - 3) })
            {
                Rect rect = new Rect(point + offset, new Vector2(width, height));
                if (rect.xMin < plot.xMin + 3 || rect.xMax > plot.xMax - 3 ||
                    rect.yMin < plot.yMin + 27 || rect.yMax > plot.yMax - 44 ||
                    labels.Any(other => other.Overlaps(rect))) continue;
                labels.Add(rect);
                labelHits.Add((rect,marker.Id));
                Fill(rect, new Color(.012f, .024f, .034f, emphasized ? .98f : .88f));
                Fill(new Rect(rect.x, rect.y, 2, rect.height), marker.Color);
                GUI.Label(new Rect(rect.x + 7, rect.y + 4, rect.width - 12, rect.height - 8), marker.Name, label);
                break;
            }
        }

        /// <summary>Show full hover/selection details, target actions, legend, and navigation help.</summary>
        private void DrawFooter(Snapshot snapshot, Marker marker)
        {
            float y = plot.yMax + 8;
            if (marker != null)
            {
                GUI.Label(new Rect(bounds.x + 14, y, bounds.width - 146, 42), marker.Name, details);
                GUI.Label(new Rect(bounds.x + 14, y + 42, bounds.width - 24, 19),
                    marker.Category + "  ·  " + (marker.Position.Distance(snapshot.Position) / 1000).ToString("0.00") + " km  ·  E " + (marker.Position.Y / 1000).ToString("0.0") + " / N " + (marker.Position.X / 1000).ToString("0.0"), caption);
                if (GUI.Button(new Rect(bounds.xMax - 124, y, 110, 27), "Center target", hud.ButtonStyle)) centerKm = Coordinate(marker.Position);
                if (marker.Navigation && GUI.Button(new Rect(bounds.xMax - 124, y + 34, 110, 27), "Warp here", hud.ButtonStyle))
                { Select(marker); client.WarpSelected(); }
            }
            else
            {
                GUI.Label(new Rect(bounds.x + 14, y, bounds.width - 28, 24), snapshot.Sector, title);
                GUI.Label(new Rect(bounds.x + 14, y + 29, bounds.width - 28, 22), "Drag to pan  ·  Wheel to zoom  ·  Click a marker to select", caption);
            }
            string[] legend = { "■ Station", "◇ Gate", "+ Nav", "▲ Hostile", "› Pilot", "◆ Resource" };
            Color[] colors = { new Color(.96f,.77f,.4f),new Color(.72f,.56f,1),new Color(.35f,.77f,.88f),new Color(1,.36f,.29f),new Color(.4f,.76f,1),new Color(.39f,.81f,.57f) };
            for (int index = 0; index < legend.Length; index++)
            { Color old = GUI.color; GUI.color = colors[index]; GUI.Label(new Rect(bounds.x + 14 + index * (bounds.width - 28) / 6, bounds.yMax - 24, (bounds.width - 28) / 6, 20), legend[index], caption); GUI.color = old; }
        }

        /// <summary>Draw distinguishable marker silhouettes without allocating textures.</summary>
        private static void Glyph(Vector2 p, int shape, Color color, float size)
        {
            if (shape == 0) { Fill(new Rect(p.x-size,p.y-size,size*2,size*2),color); Fill(new Rect(p.x-size+2,p.y-size+2,size*2-4,size*2-4),new Color(.01f,.02f,.03f)); }
            else if (shape == 1 || shape == 4) { Line(p+Vector2.up*size,p+Vector2.right*size,color,2); Line(p+Vector2.right*size,p-Vector2.up*size,color,2); Line(p-Vector2.up*size,p-Vector2.right*size,color,2); Line(p-Vector2.right*size,p+Vector2.up*size,color,2); if(shape==4) Fill(new Rect(p.x-2,p.y-2,4,4),color); }
            else if (shape == 3 || shape == 5) { Line(p+new Vector2(0,-size),p+new Vector2(size,size),color,2); Line(p+new Vector2(0,-size),p+new Vector2(-size,size),color,2); if(shape==5)Line(p+new Vector2(-size,size),p+new Vector2(size,size),color,2); }
            else { Fill(new Rect(p.x-size,p.y-1,size*2,2),color); Fill(new Rect(p.x-1,p.y-size,2,size*2),color); }
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
        public void CancelInteraction() { dragging = false; }
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
