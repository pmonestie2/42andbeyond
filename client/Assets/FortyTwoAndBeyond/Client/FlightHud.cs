using System;
using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;
using CharacterRecord = FortyTwoAndBeyond.Protocol.CharacterInfo;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Flight instruments and navigation controls for the foreground client.</summary>
    public sealed class FlightHud : IDisposable
    {
        private readonly SpaceClient client;
        private readonly EquipmentIcons icons;
        private static readonly Vector2[] LabelOffsets = { new Vector2(12, -48), new Vector2(12, 12),
            new Vector2(-184, -48), new Vector2(-184, 12) };
        private static readonly Vector2[] ContactLabelOffsets = { new Vector2(-290, -68),
            new Vector2(-290, 24), new Vector2(40, -68), new Vector2(40, 24) };
        public float Scale => Mathf.Max(.01f, Mathf.Min(1f, Screen.width / 1280f, Screen.height / 760f));
        private float CanvasWidth => Screen.width / Scale;
        private float CanvasHeight => Screen.height / Scale;
        private readonly Color cyan = new Color(.3f, .85f, 1), white = new Color(.86f, .93f, 1), muted = new Color(.46f, .62f, .72f), gold = new Color(1, .72f, .3f), crewColor = new Color(.39f, 1f, .68f);
        private GUIStyle heading, body, wrappedBody, small, commsPreview, section, button, selected, playerButton, hostileButton, damageStyle, input, primaryButton, equipmentRow, equipmentText, equipmentMeta, vitalText, buffText, contactName, hostileName, playerName, contactMeta;
        private Texture2D buttonTexture, selectedTexture, panelTexture, radarTexture, weaveTexture, frameTexture;
        internal GUIStyle BodyStyle => body;
        internal GUIStyle SmallStyle => small;
        internal GUIStyle SectionStyle => section;
        internal GUIStyle ButtonStyle => button;
        private Vector2 scroll;
        private bool navigation = true, equipmentOpen, settingsOpen, crewTab, inspectMount,
            communicationsOpen;
        private bool clearInputFocus;
        private ControlAction? bindingAction;
        private Vector2 settingsScroll;
        private int selectedCargo = -1, selectedMount = -1;
        private Vector2 cargoScroll, mountScroll;
        public bool EquipmentOpen => equipmentOpen;
        public bool SettingsOpen => settingsOpen;
        private readonly ContactSelection contactSelection = new ContactSelection();
        private uint contactId => contactSelection.SelectedId;
        private float serverSelectedUntil;
        public uint SelectedContactId => contactId;
        /// <summary>Keep keyboard-selected contacts aligned with the tactical HUD.</summary>
        public void SelectContact(uint id)
        { contactSelection.SelectLocal(id, Time.unscaledTime); wreckId = 0; client.SelectContact(id); }
        /// <summary>Dismiss fixed ship and wreck panels when another world target takes focus.</summary>
        public void ClearSelection() { contactSelection.ClearLocal(); wreckId = 0; }
        /// <summary>Forget target history when leaving the current sector or character.</summary>
        public void ResetTargetSelection() { contactSelection.Reset(); wreckId = 0; serverSelectedUntil = 0; }
        /// <summary>Show a visible actor chosen by the server, including group target shares.</summary>
        public void ObserveAuthoritativeTarget(Snapshot snapshot)
        {
            uint[] visible = snapshot.Players.Concat(snapshot.Mobs).Where(actor => actor.HasPosition)
                .Select(actor => actor.Id).ToArray();
            ContactSelectionChange change = contactSelection.Observe(snapshot.SelectedTargetId, visible,
                Time.unscaledTime);
            if (change == ContactSelectionChange.ServerSelected && contactId != 0)
            { wreckId = 0; serverSelectedUntil = Time.unscaledTime + 3f; }
        }
        /// <returns>Whether a visible ship was selected at an actual screen point.</returns>
        public bool SelectContactAtPointer(Snapshot snapshot, Vector2 pointer)
        {
            float closest = 34; uint selectedContact = 0;
            foreach (ShipActor actor in snapshot.Players.Concat(snapshot.Mobs).Where(item => item.HasPosition))
            {
                Vector3 point = client.ProjectPlayer(actor.Id);
                if (point.z <= 0) continue;
                float distance = Vector2.Distance(new Vector2(point.x, point.y), pointer);
                if (distance < closest) { closest = distance; selectedContact = actor.Id; }
            }
            if (selectedContact == 0) return false;
            SelectContact(selectedContact);
            return true;
        }
        /// <returns>Whether a visible wreck was selected at an actual screen point.</returns>
        public bool SelectWreckAtPointer(Snapshot snapshot, Vector2 pointer)
        {
            float closest = 32; uint selectedWreck = 0;
            foreach (MapObject item in snapshot.Objects.Where(item => item.Kind == 25))
            {
                Vector3 point = client.Project(item.Position);
                if (point.z <= 0) continue;
                float distance = Vector2.Distance(new Vector2(point.x, point.y), pointer);
                if (distance < closest) { closest = distance; selectedWreck = item.Id; }
            }
            if (selectedWreck == 0) return false;
            SelectWreck(selectedWreck); return true;
        }
        /// <summary>Show a chosen corpse without retaining the previous ship lock.</summary>
        public void SelectWreck(uint id)
        { wreckId = id; contactSelection.ClearLocal(); lootScroll = Vector2.zero; client.SelectWorldObject(id); }
        /// <summary>Open or close the ship equipment overlay.</summary>
        public void ToggleEquipment() { equipmentOpen = !equipmentOpen; }
        private string chatText = "";
        private string inviteName = "";
        public bool InputFocused { get; private set; }
        private bool groupChat;
        private uint wreckId;
        private Vector2 lootScroll;
        private uint invitePendingId;
        private float invitePendingAt;
        private Vector2 contactsScroll;
        private Vector2 npcScroll, dialogueScroll;
        private Vector2 vendorScroll, saleScroll;
        private int vendorPage;
        private Dictionary<uint, string> npcNames;
        private sealed class FloatingDamage
        {
            public readonly uint Target;
            public readonly string Text;
            public readonly Color Color;
            public readonly float Started;
            /// <summary>Retain one short lived combat label.</summary>
            public FloatingDamage(DamageEvent damage, bool incoming)
            {
                Target = damage.Target;
                Text = damage.Outcome == DamageOutcome.Miss ? "MISS" :
                    damage.Outcome == DamageOutcome.Critical ? $"CRIT {damage.Amount:0}" :
                    damage.Amount.ToString("0");
                Color = damage.Outcome == DamageOutcome.Miss ? new Color(.7f, .8f, .9f) :
                    damage.Outcome == DamageOutcome.Critical ? new Color(1f, .84f, .32f) :
                    incoming ? new Color(1f, .45f, .29f) : new Color(.5f, .9f, 1f);
                Started = Time.unscaledTime;
            }
        }
        private readonly List<FloatingDamage> damageLabels = new List<FloatingDamage>();
        private long lastDamage;
        private MapObject[] destinations = Array.Empty<MapObject>();
        private MapObject[] nearbyWrecks = Array.Empty<MapObject>();
        private ShipActor[] socialContacts = Array.Empty<ShipActor>();
        private float refreshAt;
        private string sector = "";

        /// <summary>Bind the HUD to its concrete foreground controller.</summary>
        public FlightHud(SpaceClient client) { this.client = client; icons = new EquipmentIcons(); }

        /// <summary>Release text-field ownership when the pilot returns to flight.</summary>
        public void ReleaseInputFocus()
        { InputFocused = false; clearInputFocus = true; }

        /// <returns>float: A scanner height fitted to visible contacts and salvage controls.</returns>
        private float SocialHeight(Snapshot snapshot)
        {
            if (crewTab) return Mathf.Clamp(CanvasHeight - 380, 280, 420);
            int rows = socialContacts.Length + nearbyWrecks.Length;
            float requested = 164 + socialContacts.Length * 68 +
                nearbyWrecks.Length * 39 + (rows > 0 ? 24 : 0);
            return Mathf.Clamp(requested, 215, Mathf.Min(420, CanvasHeight - 330));
        }

        /// <returns>float: The compact navigation list height at the current canvas size.</returns>
        private float NavigationListHeight()
        { return Mathf.Clamp(CanvasHeight - 555, 185, 260); }

        /// <returns>Whether the pointer is over foreground controls.</returns>
        public bool ContainsPointer()
        {
            Vector2 p = new Vector2(Input.mousePosition.x / Scale, (Screen.height - Input.mousePosition.y) / Scale);
            return !client.Connected || (settingsOpen && SettingsRect().Contains(p)) ||
                (equipmentOpen && EquipmentRect().Contains(p)) ||
                ((contactId != 0 || wreckId != 0) &&
                    new Rect(CanvasWidth < 1500 ? (CanvasWidth - 390) * .5f : CanvasWidth - 720,
                        CanvasWidth < 1500 ? 170 : CanvasHeight - 440, 390, 320).Contains(p)) ||
                new Rect(24, 100, 280, SocialHeight(client.CurrentSnapshot)).Contains(p) ||
                (p.x < 354 && p.y > CanvasHeight - (communicationsOpen ? 248 : 162)) ||
                new Rect(CanvasWidth - 320, 20, 300,
                    navigation ? NavigationListHeight() + 247 : 78).Contains(p) ||
                new Rect(CanvasWidth - 214, CanvasHeight - 198, 190, 174).Contains(p) ||
                p.y > CanvasHeight - 115 || p.y < 85;
        }

        /// <returns>Texture2D: An owned panel texture with subtle scanlines and a vertical light falloff.</returns>
        private static Texture2D SurfaceTexture(Color top, Color bottom, int width, int height)
        {
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                float vertical = (float)y / Mathf.Max(1, height - 1);
                float scanline = y % 4 == 0 ? .975f : 1f;
                float edge = .96f + .04f * Mathf.Sin((float)x / width * Mathf.PI);
                Color color = Color.Lerp(bottom, top, vertical) * scanline * edge;
                pixels[y * width + x] = color;
            }
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels); texture.Apply(); return texture;
        }

        /// <summary>Create styles inside a GUI event where the skin is available.</summary>
        private void EnsureStyles()
        {
            if (heading != null) return;
            heading = new GUIStyle(GUI.skin.label) { fontSize = 27, fontStyle = FontStyle.Bold }; heading.normal.textColor = white;
            body = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold }; body.normal.textColor = white;
            wrappedBody = new GUIStyle(body) { wordWrap = true };
            small = new GUIStyle(GUI.skin.label) { fontSize = 13 }; small.normal.textColor = muted;
            commsPreview = new GUIStyle(small) { wordWrap = false, clipping = TextClipping.Clip };
            vitalText = new GUIStyle(small) { fontSize = 11, fontStyle = FontStyle.Bold };
            vitalText.normal.textColor = white;
            buffText = new GUIStyle(small) { fontSize = 12, fontStyle = FontStyle.Bold };
            buffText.normal.textColor = white;
            contactName = new GUIStyle(wrappedBody) { fontSize = 15 };
            hostileName = new GUIStyle(contactName); hostileName.normal.textColor = new Color(1f, .58f, .46f);
            playerName = new GUIStyle(contactName); playerName.normal.textColor = cyan;
            contactMeta = new GUIStyle(small) { wordWrap = true };
            section = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold }; section.normal.textColor = cyan;
            damageStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            damageStyle.normal.textColor = gold;
            panelTexture = SurfaceTexture(new Color(.028f, .045f, .065f, .92f),
                new Color(.006f, .014f, .027f, .94f), 64, 64);
            weaveTexture = Resources.Load<Texture2D>("HudWeave");
            frameTexture = Resources.Load<Texture2D>("HudFrame");
            if (weaveTexture != null) weaveTexture.wrapMode = TextureWrapMode.Repeat;
            buttonTexture = SurfaceTexture(new Color(.055f, .1f, .13f, .96f),
                new Color(.018f, .038f, .059f, .98f), 32, 32);
            selectedTexture = SurfaceTexture(new Color(.09f, .23f, .29f, .98f),
                new Color(.035f, .1f, .15f, .98f), 32, 32);
            radarTexture = RadarTexture();
            button = new GUIStyle(GUI.skin.button) { fontSize = 15, fontStyle = FontStyle.Bold, wordWrap = true,
                alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 8, 4, 4) };
            button.normal.background = buttonTexture; button.normal.textColor = white;
            button.hover.background = selectedTexture; button.hover.textColor = cyan;
            selected = new GUIStyle(button); selected.normal.background = selectedTexture; selected.normal.textColor = cyan;
            playerButton = new GUIStyle(button); playerButton.normal.textColor = cyan;
            hostileButton = new GUIStyle(button); hostileButton.normal.textColor = new Color(1f, .53f, .4f);
            input = new GUIStyle(GUI.skin.textField) { fontSize = 16, padding = new RectOffset(10, 10, 7, 7) };
            input.normal.background = buttonTexture; input.focused.background = selectedTexture;
            input.normal.textColor = white; input.focused.textColor = white;
            primaryButton = new GUIStyle(button) { alignment = TextAnchor.MiddleCenter, fontSize = 17 };
            primaryButton.normal.background = selectedTexture; primaryButton.normal.textColor = white;
            primaryButton.hover.background = selectedTexture; primaryButton.hover.textColor = cyan;
            equipmentRow = new GUIStyle(button) { fontSize = 13, padding = new RectOffset(8, 6, 4, 4) };
            equipmentText = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold,
                wordWrap = true, alignment = TextAnchor.MiddleLeft };
            equipmentText.normal.textColor = white;
            equipmentMeta = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft };
            equipmentMeta.normal.textColor = muted;
        }

        /// <returns>A shared transparent scanner dial with concentric range rings.</returns>
        private static Texture2D RadarTexture()
        {
            const int size = 128; var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                float dx = x - 63.5f, dy = y - 63.5f;
                float radius = Mathf.Sqrt(dx * dx + dy * dy);
                float rings = Mathf.Min(Mathf.Abs(radius - 20), Mathf.Abs(radius - 41), Mathf.Abs(radius - 62));
                byte alpha = (byte)(rings < .7f ? 95 : Mathf.Abs(dx) < .55f || Mathf.Abs(dy) < .55f ? 40 : radius < 62 ? 9 : 0);
                pixels[y * size + x] = new Color32(55, 190, 210, alpha);
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels); texture.Apply(); return texture;
        }

        /// <summary>Draw a tinted rectangle without retaining GUI color changes.</summary>
        private static void Fill(Rect rect, Color color)
        { Color previous = GUI.color; GUI.color = color; GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = previous; }

        /// <summary>Draw a dark glass instrument panel with subtle depth and a short accent rule.</summary>
        internal void Panel(Rect rect)
        {
            Fill(new Rect(rect.x + 5, rect.y + 7, rect.width, rect.height),
                new Color(0, 0, 0, .22f));
            GUI.DrawTexture(rect, panelTexture);
            if (weaveTexture != null)
            {
                Color previous = GUI.color;
                GUI.color = new Color(.46f, .7f, .85f, .12f);
                GUI.DrawTextureWithTexCoords(rect, weaveTexture,
                    new Rect(0, 0, rect.width / 270f, rect.height / 270f));
                GUI.color = previous;
            }
            Fill(new Rect(rect.x, rect.y, rect.width, 1), new Color(.38f, .65f, .76f, .5f));
            Fill(new Rect(rect.x, rect.y + rect.height - 1, rect.width, 1),
                new Color(.16f, .32f, .4f, .3f));
            Fill(new Rect(rect.x, rect.y + 1, 1, rect.height - 2), new Color(.26f, .47f, .58f, .28f));
            Fill(new Rect(rect.x + rect.width - 1, rect.y + 1, 1, rect.height - 2),
                new Color(.26f, .47f, .58f, .28f));
            Fill(new Rect(rect.x + 14, rect.y, Mathf.Min(54, rect.width - 28), 2),
                new Color(.3f, .78f, .9f, .75f));
        }

        /// <summary>Draw a lighter instrument frame over live flight scenery.</summary>
        private void FlightPanel(Rect rect)
        {
            Color previous = GUI.color;
            GUI.color = new Color(1, 1, 1, .76f);
            GUI.DrawTexture(rect, panelTexture);
            GUI.color = previous;
            Fill(new Rect(rect.x, rect.y, rect.width, 1), new Color(.36f, .68f, .77f, .35f));
            Fill(new Rect(rect.x + 12, rect.y, Mathf.Min(45, rect.width - 24), 2),
                new Color(.31f, .82f, .94f, .65f));
        }

        /// <summary>Draw a shield, energy or hull gauge over a dim track.</summary>
        private void Meter(Rect rect, string label, float value, Color color)
        {
            Fill(rect, new Color(.07f, .13f, .17f, 1));
            Fill(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(value), rect.height), color);
            GUI.Label(new Rect(rect.x + 5, rect.y - 4, rect.width - 10, 18), label, small);
        }

        /// <returns>The centered equipment overlay bounds.</returns>
        private Rect EquipmentRect()
        { return new Rect((CanvasWidth - 960) * .5f, Mathf.Max(80, (CanvasHeight - 620) * .5f), 960, Mathf.Min(620, CanvasHeight - 125)); }

        /// <returns>The centered keyboard settings overlay bounds.</returns>
        private Rect SettingsRect()
        { return new Rect((CanvasWidth - 580) * .5f, 80, 580, Mathf.Min(610, CanvasHeight - 150)); }

        /// <summary>Draw connection controls, flight instruments and navigation.</summary>
        public void Draw(ref string host, ref string username, ref string password)
        {
            if (clearInputFocus) { GUI.FocusControl(null); clearInputFocus = false; }
            EnsureStyles();
            if (settingsOpen) { InputFocused = false; Settings(); return; }
            if (!client.Connected) { InputFocused = false; Connection(ref host, ref username, ref password); Settings(); return; }
            if (client.ChoosingCharacter) { InputFocused = false; CharacterSelection(); Settings(); return; }
            if (client.CurrentSnapshot == null)
            {
                InputFocused = false;
                var waiting = new Rect(CanvasWidth / 2 - 220, CanvasHeight / 2 - 120, 440, 240);
                Panel(waiting); GUI.Label(new Rect(waiting.x + 24, waiting.y + 24, 390, 36), "42 AND BEYOND", heading);
                GUI.Label(new Rect(waiting.x + 24, waiting.y + 74, 390, 48), client.LoginError ?? client.Status, body);
                GUI.Label(new Rect(waiting.x + 24, waiting.y + 128, 390, 40), client.PickingCharacter ? "Reading characters from the server…" : "Entering space…", small);
                if (GUI.Button(new Rect(waiting.x + 24, waiting.y + 182, 150, 34), "Back to login", button)) client.ReturnToLogin();
                Settings();
                return;
            }
            Snapshot snapshot = client.CurrentSnapshot;
            if (client.Incapacitated) equipmentOpen = false;
            if (wreckId != 0 && !snapshot.Objects.Any(item => item.Id == wreckId && item.Kind == 25)) wreckId = 0;
            if (snapshot.Station != null)
            {
                Station(snapshot);
                if (equipmentOpen) Equipment(snapshot);
                InputFocused = false;
                ChatEntry();
                Settings();
                return;
            }
            Panel(new Rect(24, 20, 390, 65)); GUI.Label(new Rect(38, 29, 360, 29), snapshot?.Sector.ToUpperInvariant() ?? "CONNECTING", heading);
            GUI.Label(new Rect(38, 58, 360, 20), "42 AND BEYOND   /   LOCAL UNIVERSE", small);
            GUI.Label(new Rect(430, 28, 190, 20), $"{client.Fps} FPS   ·   {client.FrameMs:0} ms max", small);
            CrewStrip(snapshot);
            WarpStatus(snapshot);
            ShipVitals(snapshot);
            Rect speedPanel = new Rect(24, CanvasHeight - 92, 215, 68);
            FlightPanel(speedPanel);
            GUI.Label(new Rect(37, speedPanel.y + 7, 185, 18),
                snapshot.WarpPhase == WarpPhase.Idle ? "FLIGHT VELOCITY" : "WARP VELOCITY", small);
            GUI.Label(new Rect(37, speedPanel.y + 28, 185, 29),
                $"{Math.Abs(snapshot.Speed):N0} m/s", heading);
            KeyBindings keys = client.Bindings;
            GUI.Label(new Rect(375, CanvasHeight - 46, CanvasWidth - 720, 40),
                $"{keys.Display(ControlAction.Forward)} / {keys.Display(ControlAction.Reverse)} thrust   ·   {keys.Display(ControlAction.TurnLeft)} / {keys.Display(ControlAction.TurnRight)} turn   ·   {keys.Display(ControlAction.Brake)} brake\n" +
                $"{keys.Display(ControlAction.NextHostile)} hostile   ·   {keys.Display(ControlAction.Follow)} follow   ·   {keys.Display(ControlAction.Fire)} fire   ·   RMB orbit", small);
            if (GUI.Button(new Rect(CanvasWidth - 160, 25, 136, 28),
                keys.Display(ControlAction.Chart) + (client.Chart ? "  Flight" : "  Chart"), button)) client.SetChart(!client.Chart);
            if (GUI.Button(new Rect(CanvasWidth - 300, 25, 130, 28), "Disconnect", button)) { client.Disconnect(); return; }
            if (snapshot == null) return;
            if (Time.unscaledTime >= refreshAt || sector != snapshot.Sector)
            {
                if (sector != snapshot.Sector) { lastDamage = 0; damageLabels.Clear(); }
                destinations = snapshot.Objects.Where(item => item.Navigable).OrderBy(item => item.Position.Distance(snapshot.Position)).ToArray();
                nearbyWrecks = snapshot.Objects.Where(item => item.Kind == 25)
                    .OrderBy(item => item.Position.Distance(snapshot.Position)).Take(12).ToArray();
                socialContacts = snapshot.Players.Concat(snapshot.Mobs).Where(actor => actor.HasPosition)
                    .OrderBy(actor => actor.Position.Distance(snapshot.Position)).Take(40).ToArray();
                refreshAt = Time.unscaledTime + .25f; sector = snapshot.Sector;
            }
            TrackDamage(snapshot);
            if (Event.current.type == EventType.Repaint)
            {
                if (client.Chart) ChartGrid(snapshot);
                if (client.Chart) ChartMarkers(snapshot); else WorldLabels(snapshot);
                PlayerLabels(snapshot); WreckLabels(snapshot); DamageLabels(snapshot);
            }
            Navigation(snapshot);
            Radar(snapshot);
            Social(snapshot);
            Communications(snapshot);
            if (!client.AbilitiesOpen && !client.Incapacitated)
            { if (!client.Chart) TargetLock(snapshot); WreckPanel(snapshot); }
            if (equipmentOpen) Equipment(snapshot);
            InputFocused = crewTab && GUI.GetNameOfFocusedControl() == "Group invite";
            ChatEntry();
            if (InputFocused)
            {
                Rect typing = new Rect((CanvasWidth - 440) * .5f, CanvasHeight - 165, 440, 34);
                Panel(typing);
                GUI.Label(new Rect(typing.x + 12, typing.y + 6, typing.width - 24, 23),
                    "TYPING CREW INVITE  ·  ESC OR CLICK EMPTY SPACE TO FLY", small);
            }
            if (!client.Chart)
            {
                float x = CanvasWidth * .5f, y = CanvasHeight * .5f;
                Fill(new Rect(x - 12, y, 8, 1), cyan); Fill(new Rect(x + 4, y, 8, 1), cyan); Fill(new Rect(x, y - 12, 1, 8), cyan); Fill(new Rect(x, y + 4, 1, 8), cyan);
            }
            Settings();
        }

        /// <summary>Capture and persist keyboard controls without passing keys to flight input.</summary>
        private void Settings()
        {
            if (GUI.Button(new Rect(CanvasWidth - 430, 25, 110, 28), "Controls", button))
            { settingsOpen = !settingsOpen; bindingAction = null; }
            if (!settingsOpen) return;
            if (bindingAction.HasValue && Event.current.type == EventType.KeyDown)
            {
                KeyCode key = Event.current.keyCode;
                if (key == KeyCode.Escape) bindingAction = null;
                else if (key != KeyCode.None)
                {
                    client.Bindings.Bind(bindingAction.Value, key);
                    bindingAction = null;
                }
                Event.current.Use();
            }
            Rect panel = SettingsRect(); Panel(panel);
            GUI.Label(new Rect(panel.x + 20, panel.y + 15, 400, 28), "KEYBOARD CONTROLS", heading);
            if (GUI.Button(new Rect(panel.xMax - 49, panel.y + 15, 29, 28), "×", button))
            { settingsOpen = false; bindingAction = null; return; }
            GUILayout.BeginArea(new Rect(panel.x + 20, panel.y + 56, panel.width - 40, panel.height - 115));
            settingsScroll = GUILayout.BeginScrollView(settingsScroll);
            foreach (ControlAction action in KeyBindings.Actions)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(KeyBindings.Label(action), body, GUILayout.Width(320), GUILayout.Height(30));
                string label = bindingAction == action ? "Press a key…" : client.Bindings.Display(action);
                if (GUILayout.Button(label, bindingAction == action ? selected : button,
                    GUILayout.Width(185), GUILayout.Height(28))) bindingAction = action;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView(); GUILayout.EndArea();
            GUI.Label(new Rect(panel.x + 20, panel.yMax - 53, 380, 20),
                bindingAction.HasValue ? "Esc cancels · choosing an occupied key swaps its action" :
                "Click a key to change it · occupied keys swap actions", small);
            if (GUI.Button(new Rect(panel.xMax - 150, panel.yMax - 57, 130, 32), "Restore defaults", button))
            { client.Bindings.Reset(); bindingAction = null; }
        }

        /// <summary>Present the server's station interior and launch controls.</summary>
        private void Station(Snapshot snapshot)
        {
            EnsureNpcNames();
            string name = snapshot.Station.Name.Length > 0 ? snapshot.Station.Name : "STATION " + snapshot.Station.Id;
            Panel(new Rect(24, 20, 470, 72));
            GUI.Label(new Rect(40, 30, 440, 32), name.ToUpperInvariant(), heading);
            GUI.Label(new Rect(40, 64, 440, 20),
                (snapshot.Station.CurrentRoom == 0 ? "DOCKING BAY" :
                    snapshot.Station.CurrentRoom == uint.MaxValue ? "STATION INTERIOR" :
                    "CONCOURSE  /  ROOM " + (snapshot.Station.CurrentRoom + 1)) +
                "   /   42 AND BEYOND", small);
            GUI.Label(new Rect(510, 29, 190, 22), $"{client.Fps} FPS   ·   {client.FrameMs:0} ms max", small);
            GUI.Label(new Rect(510, 57, 250, 22), "W / S WALK   ·   A / D TURN", small);
            Panel(new Rect(24, 110, 300, 265));
            GUI.Label(new Rect(40, 124, 270, 28), "STATION SERVICES", body);
            GUI.Label(new Rect(40, 158, 270, 54),
                $"{snapshot.Station.Rooms} rooms   ·   {snapshot.Station.Terminals} terminals\n{snapshot.Station.Contacts.Count} station contacts", small);
            GUI.Label(new Rect(40, 216, 270, 43), "Cargo and installed equipment are available while docked.", small);
            string equipmentKey = client.Bindings.Display(ControlAction.Equipment);
            if (GUI.Button(new Rect(40, 280, 268, 34), equipmentOpen ? "CLOSE LOADOUT  ·  " + equipmentKey : "SHIP LOADOUT  ·  " + equipmentKey, button))
                ToggleEquipment();
            if (GUI.Button(new Rect(40, 325, 268, 36), "LAUNCH INTO SPACE  ·  " + client.Bindings.Display(ControlAction.Launch), selected)) client.Launch();
            Panel(new Rect(24, 385, 300, 185));
            GUI.Label(new Rect(40, 398, 268, 24), "INTERIOR ROOMS", body);
            GUILayout.BeginArea(new Rect(40, 426, 268, 132));
            foreach (StationRoom room in snapshot.Station.RoomList)
            {
                GUI.enabled = snapshot.Dialogue == null;
                if (GUILayout.Button((room.Index == 0 ? "DOCKING BAY" : "CONCOURSE " + (room.Index + 1)) +
                    (room.Index == snapshot.Station.CurrentRoom ? "   ·   HERE" : ""),
                    room.Index == snapshot.Station.CurrentRoom ? selected : button, GUILayout.Height(27)))
                    client.EnterStationRoom(room.Index);
            }
            GUI.enabled = true;
            GUILayout.EndArea();
            Panel(new Rect(CanvasWidth - 320, 20, 296, 116));
            GUI.Label(new Rect(CanvasWidth - 306, 34, 270, 24), "SHIP STATUS", body);
            GUI.Label(new Rect(CanvasWidth - 306, 63, 270, 58),
                $"HULL   {snapshot.Hull:0} / {snapshot.MaxHull:0}\nSHIELD   {snapshot.Shield:0} / {snapshot.MaxShield:0}", small);
            float contactsHeight = Mathf.Max(210, CanvasHeight - 315);
            Panel(new Rect(CanvasWidth - 320, 150, 296, contactsHeight));
            GUI.Label(new Rect(CanvasWidth - 306, 164, 270, 22),
                "ROOM " + (snapshot.Station.CurrentRoom + 1) + " CONTACTS", body);
            GUILayout.BeginArea(new Rect(CanvasWidth - 308, 192, 272, contactsHeight - 54));
            npcScroll = GUILayout.BeginScrollView(npcScroll);
            foreach (StationContact contact in snapshot.Station.Contacts.Where(entry =>
                entry.Room == snapshot.Station.CurrentRoom))
            {
                uint id = contact.Id;
                string label = npcNames.TryGetValue(id, out string npcName) ? npcName : "Contact " + id;
                GUI.enabled = snapshot.Dialogue == null;
                if (GUILayout.Button(label, button, GUILayout.Height(32))) client.OpenNpc(id);
            }
            if (!snapshot.Station.Contacts.Any(entry => entry.Room == snapshot.Station.CurrentRoom))
                GUILayout.Label("No contacts in this room.", small);
            StationAvatar[] roomPeers = snapshot.StationAvatars.Where(avatar =>
                avatar.Room == snapshot.Station.CurrentRoom).ToArray();
            if (roomPeers.Length > 0)
            {
                GUILayout.Space(12);
                GUILayout.Label("OTHER PILOTS", body);
                foreach (StationAvatar avatar in roomPeers)
                    GUILayout.Label(avatar.Name, small);
            }
            GUI.enabled = true;
            GUILayout.EndScrollView(); GUILayout.EndArea();
            Panel(new Rect(24, CanvasHeight - 174, 445, 150));
            GUI.Label(new Rect(40, CanvasHeight - 162, 410, 20), "STATION COMMUNICATIONS", small);
            GUI.Label(new Rect(40, CanvasHeight - 136, 410, 104),
                string.Join("\n", snapshot.Messages.Reverse().Take(5).Reverse()), small);
            GUI.Label(new Rect(CanvasWidth - 320, CanvasHeight - 56, 296, 28), client.Status, small);
            if (GUI.Button(new Rect(CanvasWidth - 154, 25, 130, 28), "Disconnect", button)) client.Disconnect();
            if (snapshot.Dialogue != null) StationDialogue(snapshot);
        }

        /// <summary>Load archived public NPC labels once for station contact buttons.</summary>
        private void EnsureNpcNames()
        {
            if (npcNames != null) return;
            npcNames = new Dictionary<uint, string>();
            TextAsset asset = Resources.Load<TextAsset>("NpcNames");
            if (asset == null) return;
            foreach (string row in asset.text.Split('\n'))
            {
                int separator = row.IndexOf('\t');
                if (separator <= 0 || !uint.TryParse(row.Substring(0, separator), out uint id)) continue;
                string name = row.Substring(separator + 1).Trim();
                if (id != 0 && name.Length > 0) npcNames[id] = name;
            }
        }

        /// <summary>Present only branches sent by the active server conversation.</summary>
        private void StationDialogue(Snapshot snapshot)
        {
            DialogueState dialogue = snapshot.Dialogue;
            bool trading = dialogue.VendorOpen;
            float width = Mathf.Min(trading ? 680 : 590, CanvasWidth - 625), x = (CanvasWidth - width) * .5f;
            float top = trading ? 116 : CanvasHeight - 285;
            float height = trading ? CanvasHeight - 140 : 260;
            Panel(new Rect(x, top, width, height));
            string title = npcNames.TryGetValue(dialogue.NpcId, out string name) ? name : "Station contact";
            GUI.Label(new Rect(x + 18, top + 11, width - 100, 26), title.ToUpperInvariant(), body);
            if (GUI.Button(new Rect(x + width - 102, top + 12, 84, 26), "Close", button)) client.ChooseNpc(230);
            GUILayout.BeginArea(new Rect(x + 18, top + 47, width - 36, trading ? 125 : 195));
            dialogueScroll = GUILayout.BeginScrollView(dialogueScroll);
            if (dialogue.MissionKind != MissionDialogueKind.None)
            {
                string phase = dialogue.MissionKind == MissionDialogueKind.Offer ? "MISSION OFFER" :
                    dialogue.MissionKind == MissionDialogueKind.Objective ? "ACTIVE MISSION" :
                    dialogue.MissionKind == MissionDialogueKind.Completed ? "MISSION COMPLETED" :
                    dialogue.MissionStage == 1 ? "MISSION ACCEPTED" : "MISSION UPDATED";
                GUILayout.Label(phase + "  ·  " + dialogue.MissionName, section);
                MissionState mission = snapshot.Missions.FirstOrDefault(entry => entry?.Name == dialogue.MissionName);
                if (mission != null && !string.IsNullOrEmpty(mission.Objective))
                {
                    GUILayout.Label("CURRENT OBJECTIVE", small);
                    GUILayout.Label(mission.Objective, small);
                    GUILayout.Space(8);
                }
            }
            string line = dialogue.Text;
            if (line.StartsWith("/", StringComparison.Ordinal))
            {
                int marker = line.IndexOf(' ');
                if (marker > 0 && marker < 20) line = line.Substring(marker + 1);
            }
            GUILayout.Label(line.Length > 0 ? line : "Waiting for reply…", body);
            GUILayout.Space(12);
            foreach (DialogueChoice choice in dialogue.Choices)
                if (GUILayout.Button(choice.Text, button, GUILayout.Height(30))) client.ChooseNpc(choice.Node);
            GUILayout.EndScrollView(); GUILayout.EndArea();
            if (trading) StationVendor(snapshot, new Rect(x + 18, top + 188, width - 36, height - 205));
        }

        /// <summary>Offer bounded server-authoritative buy and sell commands.</summary>
        private void StationVendor(Snapshot snapshot, Rect rect)
        {
            GUI.Label(new Rect(rect.x, rect.y, rect.width, 24), $"TRADE COUNTER   ·   {snapshot.Credits:N0} CREDITS", body);
            ItemView[] offers = snapshot.Vendor.Where(item => item != null).ToArray();
            int pages = Mathf.Max(1, Mathf.CeilToInt(offers.Length / 16f));
            vendorPage = Mathf.Clamp(vendorPage, 0, pages - 1);
            float column = (rect.width - 12) * .5f;
            GUI.Label(new Rect(rect.x, rect.y + 28, column, 20), $"FOR SALE   ·   PAGE {vendorPage + 1}/{pages}", small);
            if (GUI.Button(new Rect(rect.x + column - 82, rect.y + 25, 34, 25), "‹", button)) vendorPage = Mathf.Max(0, vendorPage - 1);
            if (GUI.Button(new Rect(rect.x + column - 42, rect.y + 25, 34, 25), "›", button)) vendorPage = Mathf.Min(pages - 1, vendorPage + 1);
            GUI.Label(new Rect(rect.x + column + 12, rect.y + 28, column, 20), "SELL OWNED CARGO", small);
            float listHeight = rect.height - 58;
            GUILayout.BeginArea(new Rect(rect.x, rect.y + 55, column, listHeight));
            vendorScroll = GUILayout.BeginScrollView(vendorScroll);
            foreach (ItemView item in offers.Skip(vendorPage * 16).Take(16))
                if (GUILayout.Button($"BUY  {item.Name}\n{item.Price:N0} credits", button, GUILayout.Height(42)))
                    client.BuyVendor(item.Slot);
            GUILayout.EndScrollView(); GUILayout.EndArea();
            GUILayout.BeginArea(new Rect(rect.x + column + 12, rect.y + 55, column, listHeight));
            saleScroll = GUILayout.BeginScrollView(saleScroll);
            foreach (ItemView item in snapshot.Cargo.Where(item => item != null))
            {
                string quote = item.Price > 0 ? $"{item.Price:N0} credits" : "No buyer quote";
                GUI.enabled = item.Price > 0;
                if (GUILayout.Button($"SELL  {item.Name}  ×{item.Quantity}\n{quote}", button, GUILayout.Height(42)))
                    client.SellVendor(item.Slot);
            }
            GUI.enabled = true;
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        /// <summary>Let the player choose one of the account's server characters.</summary>
        private void CharacterSelection()
        {
            CharacterRecord[] characters = client.Characters;
            var rect = new Rect(CanvasWidth / 2 - 220, CanvasHeight / 2 - 170, 440, 340);
            Panel(rect); GUI.Label(new Rect(rect.x + 24, rect.y + 20, 390, 34), "CHOOSE YOUR TOON", heading);
            GUI.Label(new Rect(rect.x + 24, rect.y + 58, 390, 24), "Characters on this account", small);
            for (int i = 0; i < characters.Length; i++)
            {
                CharacterRecord character = characters[i];
                if (GUI.Button(new Rect(rect.x + 24, rect.y + 96 + i * 48, 392, 40), character.Name, button))
                    client.ChooseCharacter(character.Slot);
            }
            if (GUI.Button(new Rect(rect.x + 24, rect.y + 286, 150, 32), "Back to login", button)) client.ReturnToLogin();
        }

        /// <summary>Show a compact command line for server chat and /d_ commands.</summary>
        private void ChatEntry()
        {
            if (!client.ChatOpen) return;
            groupChat = GUI.Toggle(new Rect(CanvasWidth * .5f - 260, CanvasHeight - 118, 180, 24), groupChat, "Group chat (else sector)");
            float x = CanvasWidth * .5f - 260, y = CanvasHeight - 94;
            Panel(new Rect(x, y, 520, 52));
            GUI.SetNextControlName("42 and Beyond chat command");
            chatText = GUI.TextField(new Rect(x + 10, y + 10, 415, 32), chatText, 180);
            if (Time.frameCount == client.ChatOpenedFrame + 1) GUI.FocusControl("42 and Beyond chat command");
            if (Time.frameCount > client.ChatOpenedFrame && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
            {
                if (!string.IsNullOrWhiteSpace(chatText)) client.SendChat(chatText, groupChat); chatText = ""; client.CloseChatEntry(); Event.current.Use();
            }
            if (GUI.Button(new Rect(x + 432, y + 10, 78, 32), "Send", button))
            { if (!string.IsNullOrWhiteSpace(chatText)) client.SendChat(chatText, groupChat); chatText = ""; client.CloseChatEntry(); }
        }

        /// <summary>Draw the account form without saving credentials.</summary>
        private void Connection(ref string host, ref string username, ref string password)
        {
            var rect = new Rect(CanvasWidth / 2 - 220, CanvasHeight / 2 - 260, 440, 520); Panel(rect);
            if (frameTexture != null)
            {
                Color previous = GUI.color;
                GUI.color = new Color(.65f, .85f, 1f, .5f);
                GUI.DrawTexture(rect, frameTexture, ScaleMode.StretchToFill);
                GUI.color = previous;
            }
            GUILayout.BeginArea(new Rect(rect.x + 24, rect.y + 20, 392, 480));
            GUILayout.Label("42 AND BEYOND", heading); GUILayout.Label("CAPTAIN'S TERMINAL  /  LOCAL UNIVERSE", small); GUILayout.Space(10);
            GUILayout.Label("Server IPv4", small);
            GUILayout.BeginHorizontal();
            host = GUILayout.TextField(host, input);
            if (GUILayout.Button("DISCOVER", button, GUILayout.Width(112))) client.DiscoverDemoAccounts(host);
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
            GUILayout.Label("PUBLISHED SANDBOX PILOTS", section);
            GUILayout.Label("Choose a class to fill its public demo login.", small);
            DemoAccount[] demos = host == client.DemoRosterHost ? client.DemoAccounts : null;
            if (demos == null)
                GUILayout.Label(host == client.DemoRosterHost ?
                    client.DemoRosterError ?? "Reading the server's demo roster…" :
                    "Select Discover after changing the server address.", small);
            else if (demos.Length == 0)
                GUILayout.Label("This server advertises no demo pilots.", small);
            else
            {
                for (int row = 0; row < demos.Length; row += 3)
                {
                    GUILayout.BeginHorizontal();
                    for (int column = row; column < row + 3 && column < demos.Length; column++)
                    {
                        DemoAccount demo = demos[column];
                        if (GUILayout.Button(demo.ClassName, button, GUILayout.Width(126), GUILayout.Height(33)))
                        { username = demo.Username; password = demo.Password; }
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.Space(12);
            GUILayout.Label("ACCOUNT LOGIN", section);
            GUILayout.Label("Account", small); username = GUILayout.TextField(username, input);
            GUILayout.Label("Password", small); password = GUILayout.PasswordField(password, '*', input); GUILayout.Space(10);
            if (GUILayout.Button("ENTER SPACE", primaryButton, GUILayout.Height(39))) client.StartLocalSession(host, username, password);
            GUILayout.Label(client.Status, small); GUILayout.EndArea();
        }

        /// <summary>Draw distance-sorted destinations and selected-target actions.</summary>
        private void Navigation(Snapshot snapshot)
        {
            float x = CanvasWidth - 320;
            if (GUI.Button(new Rect(x, 68, 296, 30), navigation ? "NAVIGATION   −" : "NAVIGATION   +", button)) navigation = !navigation;
            if (!navigation) return;
            float height = NavigationListHeight(); FlightPanel(new Rect(x, 100, 296, height));
            GUILayout.BeginArea(new Rect(x + 8, 108, 280, height - 16));
            scroll.x = 0;
            scroll = GUILayout.BeginScrollView(scroll);
            MapObject selectedTarget = client.Target;
            if (GUILayout.Button("FREE FLIGHT\nWARP ALONG HEADING",
                client.FreeFlight ? selected : button, GUILayout.Width(250), GUILayout.Height(48)))
                client.SelectFreeFlight();
            foreach (MapObject item in destinations)
                NavigationRow(snapshot, item, selectedTarget?.Id == item.Id);
            GUILayout.EndScrollView(); GUILayout.EndArea(); float top = height + 112;
            MapObject target = client.Target;
            ShipActor actorTarget = client.TargetActor;
            float targetRange = target != null ? target.Position.Distance(snapshot.Position) :
                actorTarget != null ? actorTarget.Position.Distance(snapshot.Position) : -1;
            FlightPanel(new Rect(x, top, 296, 145));
            GUI.Label(new Rect(x + 12, top + 9, 270, 24), target?.Name ?? actorTarget?.Name ?? "Free flight", body);
            GUI.Label(new Rect(x + 12, top + 34, 270, 22), targetRange < 0 ?
                "Warp ahead or choose a destination" : $"RANGE   {targetRange / 1000:0.00} km", small);
            bool warpActive = snapshot.WarpPhase == WarpPhase.Charging ||
                snapshot.WarpPhase == WarpPhase.Traveling;
            bool pilotTarget = actorTarget?.IsPlayer == true;
            bool resourceTooFar = target?.Kind == 38 && targetRange > 20000;
            bool tooClose = targetRange >= 0 && targetRange < 4000;
            GUI.enabled = client.Ready && !client.Incapacitated &&
                snapshot.WarpPhase != WarpPhase.Recovering &&
                (warpActive || !pilotTarget && !resourceTooFar && !tooClose);
            string warpAction = warpActive ? "CANCEL WARP" : pilotTarget ? "PILOT: USE FOLLOW" :
                resourceTooFar ? "RESOURCE OUT OF RANGE" : tooClose ? "TOO CLOSE (<4 KM)" :
                targetRange < 0 ? "WARP AHEAD" : "WARP TO TARGET";
            if (GUI.Button(new Rect(x + 12, top + 63, 272, 32),
                client.Bindings.Display(ControlAction.Warp) + "    " + warpAction, button)) client.WarpSelected();
            bool station = target?.Kind == 12;
            bool? targetAction = target == null ? null : snapshot.Verbs.Available(target.Id,
                station ? (ushort)8 : (ushort)10);
            GUI.enabled = client.Ready && !client.Incapacitated &&
                (target?.Kind == 11 || station) && targetAction == true;
            string actionLabel = target == null || target.Kind != 11 && !station ? "SELECT GATE / PORT" :
                targetAction == true ? station ? "DOCK" : "ENTER GATE" :
                targetAction == false || snapshot.Verbs.TargetId == target.Id ?
                station ? "DOCK UNAVAILABLE" : "GATE UNAVAILABLE" : "TARGET SYNCING";
            if (GUI.Button(new Rect(x + 12, top + 102, 272, 30),
                (station ? client.Bindings.Display(ControlAction.Dock) : client.Bindings.Display(ControlAction.Gate)) +
                "  " + actionLabel, button))
            { if (station) client.DockSelected(); else client.GateSelected(); }
            GUI.enabled = true;
        }

        /// <summary>Show a destination name, type, and range without button-text clipping.</summary>
        private void NavigationRow(Snapshot snapshot, MapObject item, bool active)
        {
            string kind = item.Kind == 11 ? "GATE" : item.Kind == 12 ? "PORT" : "NAV";
            float nameHeight = Mathf.Max(19, contactName.CalcHeight(new GUIContent(item.Name), 226));
            float height = nameHeight + 27;
            Rect row = GUILayoutUtility.GetRect(250, height, GUILayout.Width(250), GUILayout.Height(height));
            if (GUI.Button(row, GUIContent.none, active ? selected : button)) client.Select(item.Id);
            GUI.Label(new Rect(row.x + 8, row.y + 3, 230, nameHeight), item.Name,
                active ? playerName : contactName);
            GUI.Label(new Rect(row.x + 8, row.y + nameHeight + 3, 230, 19),
                $"{kind}   {item.Position.Distance(snapshot.Position) / 1000:0.0} km", contactMeta);
        }

        /// <summary>Draw a world-relative scanner with nearby objects and ships.</summary>
        private void Radar(Snapshot snapshot)
        {
            float x = CanvasWidth - 214, y = CanvasHeight - 198;
            FlightPanel(new Rect(x, y, 190, 174));
            int visibleAttackers = snapshot.Mobs.Count(mob => mob.HasPosition &&
                snapshot.Attackers.Contains(mob.Id));
            if (visibleAttackers > 0)
            {
                if (GUI.Button(new Rect(x + 9, y + 5, 172, 25),
                    $"{visibleAttackers} ATTACKER{(visibleAttackers == 1 ? "" : "S")}  ·  Y", hostileButton))
                    client.TargetNextAttacker();
            }
            else GUI.Label(new Rect(x + 10, y + 7, 170, 18), "RADAR   /   20 KM", small);
            var center = new Vector2(x + 95, y + 97);
            GUI.DrawTexture(new Rect(center.x - 70, center.y - 70, 140, 140), radarTexture);
            float sweep = Time.unscaledTime * .65f;
            Matrix4x4 previous = GUI.matrix;
            GUIUtility.RotateAroundPivot(-sweep * Mathf.Rad2Deg, center);
            Fill(new Rect(center.x - 1, center.y - 67, 2, 67), new Color(.25f, .85f, .9f, .27f));
            GUI.matrix = previous;
            foreach (MapObject item in destinations.Take(12))
                RadarDot(center, snapshot.Position, item.Position, item.Id == client.Target?.Id ? gold : muted, 3);
            foreach (ShipActor actor in snapshot.Players.Concat(snapshot.Mobs).Where(a => a.HasPosition).Take(64))
                RadarDot(center, snapshot.Position, actor.Position,
                    actor.Id == contactId ? white : actor.IsPlayer ? cyan : actor.Hostile ? new Color(1, .32f, .23f) : gold, 5);
            Fill(new Rect(center.x - 3, center.y - 3, 6, 6), white);
            GUI.Label(new Rect(x + 139, y + 148, 43, 18), "N ↑", small);
        }

        /// <summary>Project one contact into the radar's twenty-kilometer radius.</summary>
        private void RadarDot(Vector2 center, Position own, Position contact, Color color, float size)
        {
            float dx = contact.Y - own.Y, dy = contact.X - own.X;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            if (distance > 20000) return;
            Fill(new Rect(center.x + dx * 67 / 20000 - size * .5f,
                center.y - dy * 67 / 20000 - size * .5f, size, size), color);
        }

        /// <summary>Present the selected ship as an actionable combat or rescue target.</summary>
        private void TargetLock(Snapshot snapshot)
        {
            if (wreckId != 0) return;
            uint selectedId = contactId != 0 ? contactId : snapshot.SelectedTargetId;
            ShipActor target = snapshot.Players.Concat(snapshot.Mobs).FirstOrDefault(actor => actor.Id == selectedId);
            if (target == null || !target.HasPosition) return;
            bool canJumpstart = target.IsPlayer && target.MaxHull > 0 && target.Hull <= 0 &&
                snapshot.AvailableAbilities.Contains(65u);
            string targetName = target.Name.ToUpperInvariant();
            float nameHeight = Mathf.Max(26, wrappedBody.CalcHeight(new GUIContent(targetName), 355));
            float nameExtra = nameHeight - 26;
            float height = (target.IsPlayer ? canJumpstart ? 238 : 201 : 238) + nameExtra;
            float x = CanvasWidth >= 1500 ? CanvasWidth - 720 : (CanvasWidth - 390) * .5f;
            float y = CanvasWidth < 1500 ? 76 : CanvasHeight - height - 122;
            FlightPanel(new Rect(x, y, 390, height));
            Color signal = target.IsPlayer ? cyan : target.Hostile ? new Color(1f, .42f, .32f) : gold;
            Fill(new Rect(x + 15, y + 18, 6, 6), signal);
            GUI.Label(new Rect(x + 29, y + 12, 300, 20),
                target.IsPlayer ? "PILOT / ALLIED CONTACT" : target.Hostile ? "THREAT / HOSTILE" : "CONTACT / NEUTRAL", section);
            GUI.Label(new Rect(x + 15, y + 37, 355, nameHeight), targetName, wrappedBody);
            float distance = target.Position.Distance(snapshot.Position);
            bool firingAtTarget = snapshot.WeaponsFiring && snapshot.SelectedTargetId == target.Id;
            CombatEngagement engagement = snapshot.Engagement?.TargetId == target.Id ? snapshot.Engagement : null;
            bool recentShot = engagement?.LastShot != null && engagement.LastShotAgeSeconds <= 2f;
            GUI.Label(new Rect(x + 15, y + 65 + nameExtra, 355, 20),
                $"RANGE {distance / 1000:0.00} km   ·   " +
                (!firingAtTarget ? "WEAPONS SAFE" : recentShot ? "SHOT CONFIRMED" : "WEAPONS ARMED"), small);
            GUI.Label(new Rect(x + 15, y + 91 + nameExtra, 169, 18), target.Shield >= 0 ?
                $"SHIELD  {Mathf.Clamp01(target.Shield) * 100:0}%" : "SHIELD  NO DATA", small);
            GUI.Label(new Rect(x + 204, y + 91 + nameExtra, 169, 18), target.MaxHull > 0 ?
                $"HULL  {Mathf.Max(0, target.Hull):0}/{target.MaxHull:0}" : "HULL  NO DATA", small);
            if (target.Shield >= 0) Meter(new Rect(x + 15, y + 112 + nameExtra, 169, 7), "", target.Shield, cyan);
            if (target.MaxHull > 0) Meter(new Rect(x + 204, y + 112 + nameExtra, 169, 7), "", target.Hull / target.MaxHull, gold);
            if (!target.IsPlayer)
            {
                GUI.Label(new Rect(x + 15, y + 134 + nameExtra, 355, 18),
                    Time.unscaledTime < serverSelectedUntil ? "TARGET UPDATED BY SERVER" :
                    CombatGuidance(engagement, firingAtTarget), small);
                if (engagement?.LastResult != null && engagement.LastResultAgeSeconds <= 2f)
                {
                    string result = engagement.LastResult.Outcome == DamageOutcome.Miss ? "MISS" :
                        engagement.LastResult.Outcome == DamageOutcome.Critical ? "CRITICAL HIT" : "HIT";
                    GUI.Label(new Rect(x + 15, y + 154 + nameExtra, 355, 18),
                        $"SERVER RESULT  {result}  ·  {engagement.LastResultAgeSeconds:0.0}s AGO", small);
                }
                GUI.enabled = client.Ready && !client.Incapacitated;
                if (GUI.Button(new Rect(x + 15, y + 180 + nameExtra, 229, 38),
                    firingAtTarget ? "STOP WEAPONS" : "FIRE ON TARGET", firingAtTarget ? hostileButton : primaryButton))
                { if (firingAtTarget) client.StopWeapons(); else client.ToggleWeapons(target.Id); }
                GUI.enabled = true;
                if (GUI.Button(new Rect(x + 253, y + 180 + nameExtra, 120, 38), "FOLLOW", button)) client.FollowSelected();
                return;
            }
            bool member = snapshot.Group.Members.Any(item => item.Id == target.Id);
            bool leader = !snapshot.Group.Joined || snapshot.Group.IsLeader;
            bool full = snapshot.Group.Joined && snapshot.Group.Members.Length >= 5;
            bool pending = invitePendingId == target.Id && Time.unscaledTime - invitePendingAt < 5 &&
                (Time.unscaledTime - invitePendingAt < .2f || client.Status == "Requesting group invitation");
            bool sentRecently = invitePendingId == target.Id && Time.unscaledTime - invitePendingAt < 5 &&
                client.Status == "Group invitation sent.";
            GUI.enabled = leader && !member && !full && !pending && !sentRecently;
            if (GUI.Button(new Rect(x + 15, y + 136 + nameExtra, 229, 38),
                member ? "IN YOUR GROUP" : !leader ? "LEADER INVITES" : full ? "GROUP FULL" :
                pending ? "WAITING FOR SERVER" : sentRecently ? "INVITATION SENT" : "INVITE TO GROUP", button))
            { client.InvitePlayer(target.Id); invitePendingId = target.Id; invitePendingAt = Time.unscaledTime; }
            GUI.enabled = true;
            if (GUI.Button(new Rect(x + 253, y + 136 + nameExtra, 120, 38), "FOLLOW", button)) client.FollowSelected();
            if (canJumpstart)
            {
                GUI.enabled = client.Ready && !client.Incapacitated &&
                    snapshot.CastingAbilityId == uint.MaxValue && snapshot.SelectedTargetId == target.Id;
                if (GUI.Button(new Rect(x + 15, y + 185 + nameExtra, 358, 36),
                    snapshot.SelectedTargetId == target.Id ? "JUMPSTART PILOT" : "SELECTING JUMPSTART TARGET", selected))
                    client.ActivateAbility(65u);
                GUI.enabled = true;
            }
        }

        /// <returns>string: A concise readiness cue, with approximate range and arc hints.</returns>
        private static string CombatGuidance(CombatEngagement engagement, bool armedIntent)
        {
            if (!armedIntent) return "SELECT FIRE TO ARM MOUNTED WEAPONS";
            if (engagement == null || engagement.Weapons.Length == 0) return "NO MOUNTED WEAPON DATA";
            WeaponReadiness[] armed = engagement.Weapons.Where(weapon => weapon.Armed).ToArray();
            if (armed.Length == 0) return "ARMING WEAPONS  ·  AWAITING SERVER";
            if (armed.All(weapon => weapon.OutOfAmmo)) return "NO AMMO IN ARMED WEAPONS";
            if (armed.All(weapon => weapon.Cooling)) return "RELOADING  ·  SERVER COOLDOWN";
            if (armed.All(weapon => weapon.BeyondListedRange)) return "BEYOND LISTED WEAPON RANGE";
            if (armed.All(weapon => weapon.OutsideFiringArc)) return "AIM TOWARD TARGET  ·  40° ARC";
            return $"{armed.Length} MOUNT{(armed.Length == 1 ? "" : "S")} ARMED  ·  AWAITING SERVER SHOT";
        }

        /// <summary>Keep an opened wreck and its tractorable items beside the flight view.</summary>
        private void WreckPanel(Snapshot snapshot)
        {
            if (wreckId == 0) return;
            MapObject wreck = snapshot.Objects.FirstOrDefault(item => item.Id == wreckId && item.Kind == 25);
            if (wreck == null) return;
            bool open = snapshot.OpenLootId == wreckId;
            bool tractoring = snapshot.LootActivity != null && snapshot.LootActivity.CorpseId == wreckId;
            float height = open ? tractoring ? 332 : 306 : 126;
            float x = CanvasWidth - 740, y = CanvasHeight - height - 122;
            Panel(new Rect(x, y, 360, height));
            GUI.Label(new Rect(x + 12, y + 8, open ? 250 : 336, 22), wreck.Name, body);
            if (open && GUI.Button(new Rect(x + 272, y + 10, 76, 30), "CLOSE", button))
                client.CloseLoot(wreckId);
            float distance = wreck.Position.Distance(snapshot.Position);
            GUI.Label(new Rect(x + 12, y + 33, 336, 20), $"WRECK RANGE  {distance / 1000:0.00} km", small);
            bool? lootVerb = snapshot.Verbs.Available(wreckId, 3);
            GUI.enabled = client.Ready && lootVerb == true && !open;
            if (GUI.Button(new Rect(x + 12, y + 57, 336, 30), open ? "WRECK OPEN" :
                lootVerb == false || snapshot.Verbs.TargetId == wreckId ? "SERVER: LOOT UNAVAILABLE" :
                lootVerb == null ? "WAITING FOR LOOT VERB" : "OPEN WRECK", button))
                client.OpenLoot(wreckId);
            GUI.enabled = true;
            if (!open) return;
            CorpseLoot contents = snapshot.Loot.FirstOrDefault(item => item.Id == wreckId);
            bool collecting = client.LootAllActive;
            bool hasStock = contents?.Items.Any(item => item != null) == true;
            GUI.enabled = client.Ready && (collecting || hasStock && !tractoring);
            if (GUI.Button(new Rect(x + 12, y + 94, 150, 30),
                collecting ? "STOP QUEUE" : hasStock ? "COLLECT ALL" : "NO ITEMS", button))
            {
                if (collecting) client.StopCollectAllLoot();
                else client.CollectAllLoot(wreckId);
            }
            GUI.enabled = true;
            GUI.Label(new Rect(x + 174, y + 99, 174, 23), collecting ?
                client.LootAllStatus : "ONE SERVER CAST AT A TIME", small);
            int usedCargo = snapshot.Cargo.Count(item => item != null);
            GUI.Label(new Rect(x + 12, y + 127, 336, 20),
                $"HOLD {usedCargo}/{snapshot.CargoCapacity}  ·  SERVER CHECKS STACKING", small);
            if (tractoring)
                GUI.Label(new Rect(x + 12, y + 153, 336, 20),
                    $"TRACTORING  {snapshot.LootProgress * 100:0}%  ·  {snapshot.LootRemainingSeconds:0.0}s", small);
            float contentTop = tractoring ? 179 : 153;
            GUILayout.BeginArea(new Rect(x + 12, y + contentTop, 336, height - contentTop - 12));
            lootScroll = GUILayout.BeginScrollView(lootScroll);
            if (contents == null) GUILayout.Label("Reading wreck contents…", small);
            else
            {
                ItemView[] stock = contents.Items.Where(item => item != null).ToArray();
                if (stock.Length == 0) GUILayout.Label("No items remain", small);
                foreach (ItemView item in stock)
                {
                    GUI.enabled = !tractoring && !collecting;
                    if (GUILayout.Button("TRACTOR  " + item.Name + "  ×" + item.Quantity, button,
                        GUILayout.Height(34))) client.TakeLoot(wreckId, item.Slot);
                }
            }
            GUI.enabled = true;
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        /// <summary>Draw a sector coordinate grid, range scale and own-ship axis under chart markers.</summary>
        private void ChartGrid(Snapshot snapshot)
        {
            float left = 320, right = CanvasWidth - 330, top = 92, bottom = CanvasHeight - 120;
            if (right <= left || bottom <= top) return;
            Rect map = new Rect(left, top, right - left, bottom - top);
            Fill(map, new Color(.006f, .028f, .047f, .73f));
            float radiusKm = client.ChartRadiusKm;
            float pixelsPerKm = CanvasHeight / (radiusKm * 2f);
            float stepKm = radiusKm < 35 ? 5 : radiusKm < 90 ? 10 : radiusKm < 250 ? 25 : 50;
            float ownEastKm = snapshot.Position.Y / 1000f, ownNorthKm = snapshot.Position.X / 1000f;
            float centerX = CanvasWidth * .5f, centerY = CanvasHeight * .5f;
            float startEast = Mathf.Floor((ownEastKm - radiusKm * 2) / stepKm) * stepKm;
            float endEast = ownEastKm + radiusKm * 2;
            for (float east = startEast; east <= endEast; east += stepKm)
            {
                float x = centerX + (east - ownEastKm) * pixelsPerKm;
                if (x < left || x > right) continue;
                bool major = Mathf.Abs(Mathf.Repeat(east, stepKm * 4)) < .01f;
                Fill(new Rect(x, top, major ? 2 : 1, map.height),
                    new Color(.14f, .5f, .6f, major ? .24f : .12f));
            }
            float startNorth = Mathf.Floor((ownNorthKm - radiusKm * 2) / stepKm) * stepKm;
            float endNorth = ownNorthKm + radiusKm * 2;
            for (float north = startNorth; north <= endNorth; north += stepKm)
            {
                float y = centerY - (north - ownNorthKm) * pixelsPerKm;
                if (y < top || y > bottom) continue;
                bool major = Mathf.Abs(Mathf.Repeat(north, stepKm * 4)) < .01f;
                Fill(new Rect(left, y, map.width, major ? 2 : 1),
                    new Color(.14f, .5f, .6f, major ? .24f : .12f));
            }
            Fill(new Rect(left, top, map.width, 1), new Color(.26f, .73f, .85f, .35f));
            Fill(new Rect(left, bottom - 1, map.width, 1), new Color(.26f, .73f, .85f, .35f));
            Fill(new Rect(left, top, 1, map.height), new Color(.26f, .73f, .85f, .35f));
            Fill(new Rect(right - 1, top, 1, map.height), new Color(.26f, .73f, .85f, .35f));
            Fill(new Rect(centerX - 8, centerY, 16, 1), white);
            Fill(new Rect(centerX, centerY - 8, 1, 16), white);
            Fill(new Rect(left + 14, top + 13, 262, 47), new Color(.004f, .018f, .032f, .87f));
            GUI.Label(new Rect(left + 23, top + 17, 243, 20), "SECTOR CHART  /  XY", section);
            GUI.Label(new Rect(left + 23, top + 37, 243, 18),
                "GRID " + stepKm.ToString("0") + " km  ·  SCROLL TO ZOOM", small);
            Fill(new Rect(right - 119, bottom - 42, 105, 30), new Color(.004f, .018f, .032f, .87f));
            Fill(new Rect(right - 108, bottom - 19, stepKm * pixelsPerKm, 2), cyan);
            GUI.Label(new Rect(right - 108, bottom - 38, 94, 18),
                stepKm.ToString("0") + " km", small);
        }

        /// <summary>Place readable destinations across the sector chart without label collisions.</summary>
        private void ChartMarkers(Snapshot snapshot)
        {
            var occupied = new List<Rect>();
            foreach (ShipActor actor in snapshot.Players.Concat(snapshot.Mobs).Where(item => item.HasPosition))
            {
                Vector3 point = client.ProjectPlayer(actor.Id);
                if (point.z > 0) occupied.Add(new Rect(point.x / Scale + 8, point.y / Scale - 18, 245, 42));
            }
            int labeled = 0, marked = 0;
            uint selectedId = client.Target?.Id ?? 0;
            foreach (MapObject item in destinations.OrderByDescending(item => item.Id == selectedId))
            {
                Vector3 point = client.Project(item.Position);
                float x = point.x / Scale, y = point.y / Scale;
                if (point.z <= 0 || x < 337 || x > CanvasWidth - 347 ||
                    y < 108 || y > CanvasHeight - 138) continue;
                bool active = item.Id == selectedId;
                if (marked++ < 90)
                {
                    Color marker = active ? gold : item.Kind == 11 ? new Color(.8f, .68f, 1f) : cyan;
                    Fill(new Rect(x - (active ? 6 : 3), y - (active ? 6 : 3), active ? 12 : 6, active ? 12 : 6), marker);
                }
                if (!active && labeled >= 9) continue;
                Rect label = default; bool placed = false;
                foreach (Vector2 offset in LabelOffsets)
                {
                    Rect candidate = new Rect(x + offset.x, y + offset.y, 182, 38);
                    if (candidate.xMin < 339 || candidate.xMax > CanvasWidth - 349 ||
                        candidate.yMin < 112 || candidate.yMax > CanvasHeight - 143 ||
                        occupied.Any(other => other.Overlaps(candidate))) continue;
                    label = candidate; placed = true; break;
                }
                if (!placed) continue;
                occupied.Add(label); labeled++;
                Color color = active ? gold : item.Kind == 11 ? new Color(.8f, .68f, 1f) : cyan;
                Fill(label, new Color(.004f, .018f, .035f, .9f));
                Fill(new Rect(label.x, label.y, 3, label.height), color);
                GUI.Label(new Rect(label.x + 9, label.y + 1, 170, 18), item.Name, body);
                GUI.Label(new Rect(label.x + 9, label.y + 20, 170, 17),
                    (item.Kind == 11 ? "GATE" : item.Kind == 12 ? "STATION" : "NAV") + "   ·   " +
                    (item.Position.Distance(snapshot.Position) / 1000).ToString("0.0") + " km", small);
            }
        }

        /// <summary>Project limited markers without enlarging physical models.</summary>
        private void WorldLabels(Snapshot snapshot)
        {
            var occupied = new List<Rect>();
            if (CanvasWidth < 1500 && snapshot.Players.Concat(snapshot.Mobs).Any(actor =>
                actor.HasPosition && actor.Id == (contactId != 0 ? contactId : snapshot.SelectedTargetId)))
                occupied.Add(new Rect((CanvasWidth - 390) * .5f, 76, 390, 270));
            foreach (ShipActor actor in snapshot.Players.Concat(snapshot.Mobs).Where(item => item.HasPosition))
            {
                Vector3 point = client.ProjectPlayer(actor.Id); point.x /= Scale; point.y /= Scale;
                if (point.z > 0) occupied.Add(new Rect(point.x + 10, point.y - 18, 255, 62));
            }
            occupied.Add(new Rect(CanvasWidth * .5f - 85, CanvasHeight * .5f - 65, 170, 130));
            MapObject selectedTarget = client.Target;
            int shown = 0;
            foreach (MapObject item in destinations.OrderByDescending(item => selectedTarget?.Id == item.Id))
            {
                if (shown >= 7) break;
                bool active = selectedTarget?.Id == item.Id;
                float distance = item.Position.Distance(snapshot.Position);
                if (!active && distance > 60000) continue;
                Vector3 p = client.Project(item.Position); p.x /= Scale; p.y /= Scale;
                if (p.z <= 0 || p.x < 30 || p.x > CanvasWidth - 335 || p.y < 100 || p.y > CanvasHeight - 145) continue;
                Color color = active ? gold : cyan; float radius = active ? 11 : 5;
                Fill(new Rect(p.x - radius, p.y - radius, 5, 1), color); Fill(new Rect(p.x - radius, p.y - radius, 1, 5), color);
                Fill(new Rect(p.x + radius - 5, p.y + radius, 5, 1), color); Fill(new Rect(p.x + radius, p.y + radius - 5, 1, 5), color);
                Rect chosen = default;
                bool placed = false;
                foreach (Vector2 offset in LabelOffsets)
                {
                    var candidate = new Rect(p.x + offset.x, p.y + offset.y, 172, 36);
                    if (candidate.xMin < 24 || candidate.xMax > CanvasWidth - 332 ||
                        candidate.yMin < 95 || candidate.yMax > CanvasHeight - 122 ||
                        occupied.Any(other => other.Overlaps(candidate))) continue;
                    chosen = candidate; placed = true; break;
                }
                if (!placed) continue;
                shown++; occupied.Add(chosen);
                Fill(chosen, new Color(.004f, .018f, .035f, .82f));
                Fill(new Rect(chosen.x, chosen.y, 2, chosen.height), color);
                GUI.color = color;
                GUI.Label(new Rect(chosen.x + 7, chosen.y + 1, chosen.width - 10, chosen.height - 2),
                    $"{item.Name}\n{distance / 1000:0.0} km", small);
                GUI.color = Color.white;
            }
        }

        /// <summary>Identify remote ships with clear team colors and backed labels.</summary>
        private void PlayerLabels(Snapshot snapshot)
        {
            int shown = 0;
            var occupied = new List<Rect>();
            if (CanvasWidth < 1500 && snapshot.Players.Concat(snapshot.Mobs).Any(actor =>
                actor.HasPosition && actor.Id == (contactId != 0 ? contactId : snapshot.SelectedTargetId)))
                occupied.Add(new Rect((CanvasWidth - 390) * .5f, 76, 390, 270));
            foreach (ShipActor player in snapshot.Players.Concat(snapshot.Mobs)
                .OrderByDescending(item => item.Id == contactId || item.Id == snapshot.SelectedTargetId))
            {
                Vector3 point = client.ProjectPlayer(player.Id); point.x /= Scale; point.y /= Scale;
                if (point.z <= 0 || point.x < 30 || point.x > CanvasWidth - 335 || point.y < 100 || point.y > CanvasHeight - 145) continue;
                if (++shown > 32) break;
                bool crewMember = snapshot.Group.Members.Any(member => member.Id == player.Id);
                Color color = crewMember ? crewColor : player.IsPlayer ? cyan :
                    player.Hostile ? new Color(1, .42f, .32f) : gold;
                bool active = contactId == player.Id || snapshot.SelectedTargetId == player.Id;
                float radius = active ? 14 : 7;
                Fill(new Rect(point.x - radius, point.y - radius, 9, 2), color);
                Fill(new Rect(point.x - radius, point.y - radius, 2, 9), color);
                Fill(new Rect(point.x + radius - 9, point.y - radius, 9, 2), color);
                Fill(new Rect(point.x + radius - 1, point.y - radius, 2, 9), color);
                Fill(new Rect(point.x - radius, point.y + radius, 9, 2), color);
                Fill(new Rect(point.x - radius, point.y + radius - 7, 2, 9), color);
                Fill(new Rect(point.x + radius - 9, point.y + radius, 9, 2), color);
                Fill(new Rect(point.x + radius - 1, point.y + radius - 7, 2, 9), color);
                if (client.Chart && !active) continue;
                Rect label = default;
                foreach (Vector2 offset in ContactLabelOffsets)
                {
                    Rect candidate = new Rect(point.x + offset.x, point.y + offset.y, 250, active ? 58 : 41);
                    if (candidate.x < 292 || candidate.xMax > CanvasWidth - 315 ||
                        candidate.y < 95 || candidate.yMax > CanvasHeight - 108 ||
                        occupied.Any(area => area.Overlaps(candidate))) continue;
                    label = candidate;
                    break;
                }
                if (label.width == 0) continue;
                occupied.Add(label);
                float labelX = label.x, labelY = label.y;
                Fill(new Rect(labelX, labelY, 250, active ? 58 : 41), new Color(.004f, .012f, .024f, .94f));
                Fill(new Rect(labelX, labelY, 3, active ? 58 : 41), color);
                GUI.Label(new Rect(labelX + 9, labelY + 3, 237, 19), player.Name, body);
                GUI.Label(new Rect(labelX + 9, labelY + 22, 237, 17),
                    (crewMember ? "CREW" : player.IsPlayer ? "PLAYER" : player.Hostile ? "HOSTILE" : "MOB") +
                    "   ·   " + (player.Position.Distance(snapshot.Position) / 1000).ToString("0.00") + " km", small);
                if (active)
                {
                    if (player.Shield >= 0) Meter(new Rect(labelX + 9, labelY + 43, 81, 5), "", player.Shield, cyan);
                    if (player.MaxHull > 0) Meter(new Rect(labelX + 98, labelY + 43, 81, 5), "", player.Hull / player.MaxHull, gold);
                }
            }
        }

        /// <summary>Identify nearby wrecks independently of navigation points.</summary>
        private void WreckLabels(Snapshot snapshot)
        {
            foreach (MapObject item in nearbyWrecks)
            {
                Vector3 point = client.Project(item.Position); point.x /= Scale; point.y /= Scale;
                if (point.z <= 0 || point.x < 30 || point.x > CanvasWidth - 335 || point.y < 90 || point.y > CanvasHeight - 140) continue;
                bool active = wreckId == item.Id;
                Fill(new Rect(point.x - 5, point.y - 5, active ? 10 : 6, 2), gold);
                GUI.color = gold;
                GUI.Label(new Rect(point.x + 11, point.y - 9, 180, 35),
                    item.Name + "  /  WRECK\n" + (item.Position.Distance(snapshot.Position) / 1000).ToString("0.0") + " km", small);
                GUI.color = Color.white;
            }
        }

        /// <summary>Keep each server damage report as one fading display event.</summary>
        private void TrackDamage(Snapshot snapshot)
        {
            foreach (DamageEvent damage in snapshot.Damages)
            {
                if (damage.Sequence <= lastDamage) continue;
                lastDamage = damage.Sequence;
                damageLabels.Add(new FloatingDamage(damage, damage.Target == snapshot.PlayerId));
                if (damageLabels.Count > 20) damageLabels.RemoveAt(0);
            }
            damageLabels.RemoveAll(label => Time.unscaledTime - label.Started > 1.3f);
        }

        /// <summary>Project transient hit numbers above the impacted ship.</summary>
        private void DamageLabels(Snapshot snapshot)
        {
            foreach (FloatingDamage label in damageLabels)
            {
                Vector3 point = label.Target == snapshot.PlayerId
                    ? new Vector3(CanvasWidth * .5f, CanvasHeight * .5f, 1)
                    : client.ProjectPlayer(label.Target);
                point.x /= label.Target == snapshot.PlayerId ? 1 : Scale;
                point.y /= label.Target == snapshot.PlayerId ? 1 : Scale;
                if (point.z <= 0 || point.x < 280 || point.x > CanvasWidth - 320) continue;
                float age = Time.unscaledTime - label.Started;
                Color previous = GUI.color;
                Color tint = label.Color; tint.a = 1 - age / 1.3f; GUI.color = tint;
                GUI.Label(new Rect(point.x - 55, point.y - 45 - age * 34, 110, 30), label.Text, damageStyle);
                GUI.color = previous;
            }
        }

        /// <summary>Show threats first, with a separate crew management tab.</summary>
        private void Social(Snapshot snapshot)
        {
            float height = SocialHeight(snapshot);
            Rect panel = new Rect(24, 100, 280, height);
            FlightPanel(panel);
            GUI.Label(new Rect(37, 113, 240, 22), "TACTICAL SCANNER", section);
            int hostileCount = socialContacts.Count(actor => actor.Hostile);
            GUI.Label(new Rect(37, 137, 245, 18),
                $"{hostileCount} THREATS   ·   {socialContacts.Length} CONTACTS", small);
            if (GUI.Button(new Rect(37, 165, 124, 31), "CONTACTS", crewTab ? button : selected))
            { crewTab = false; ReleaseInputFocus(); }
            if (GUI.Button(new Rect(166, 165, 124, 31),
                snapshot.Confirmation != null && snapshot.Confirmation.Kind != ConfirmationKind.Jumpstart ? "CREW  !" : "CREW",
                crewTab ? selected : button)) crewTab = true;
            GUILayout.BeginArea(new Rect(35, 205, 256, height - 119));
            contactsScroll = GUILayout.BeginScrollView(contactsScroll);
            if (crewTab) CrewRoster(snapshot);
            else
            {
                DrawContactGroup(snapshot, "HOSTILE SIGNALS", socialContacts.Where(actor => actor.Hostile));
                DrawContactGroup(snapshot, "PILOTS", socialContacts.Where(actor => actor.IsPlayer && !actor.Hostile));
                DrawContactGroup(snapshot, "NEUTRAL SIGNALS", socialContacts.Where(actor => !actor.IsPlayer && !actor.Hostile));
                if (socialContacts.Length == 0) GUILayout.Label("No ships in scanner range", small);
                if (snapshot.WeaponsFiring && GUILayout.Button("STOP ALL WEAPONS", hostileButton, GUILayout.Height(31))) client.StopWeapons();
                if (nearbyWrecks.Length > 0) GUILayout.Label("SALVAGE", section);
                foreach (MapObject wreck in nearbyWrecks)
                    if (GUILayout.Button(wreck.Name + "  ·  " +
                        (wreck.Position.Distance(snapshot.Position) / 1000).ToString("0.0") + " km",
                        wreckId == wreck.Id ? selected : button, GUILayout.Height(33))) SelectWreck(wreck.Id);
                MapObject selectedWreck = nearbyWrecks.FirstOrDefault(item => item.Id == wreckId);
                if (selectedWreck != null)
                {
                    float distance = selectedWreck.Position.Distance(snapshot.Position);
                    bool? lootVerb = snapshot.Verbs.Available(wreckId, 3);
                    bool tractoring = snapshot.LootActivity != null && snapshot.LootActivity.CorpseId == wreckId;
                    GUI.enabled = lootVerb == true && snapshot.OpenLootId != wreckId;
                    if (GUILayout.Button(snapshot.OpenLootId == wreckId ? "WRECK OPEN" :
                        lootVerb == true ? "OPEN WRECK" : "LOOT UNAVAILABLE", button)) client.OpenLoot(wreckId);
                    GUI.enabled = true;
                    if (lootVerb == false) GUILayout.Label("Move closer or leave warp to open", small);
                    if (tractoring) GUILayout.Label($"TRACTORING  {snapshot.LootProgress * 100:0}%  ·  {snapshot.LootRemainingSeconds:0.0}s", small);
                    if (snapshot.OpenLootId == wreckId &&
                        GUILayout.Button(tractoring ? "STOP TRACTOR AND CLOSE" : "CLOSE WRECK", button))
                        client.CloseLoot(wreckId);
                    CorpseLoot contents = snapshot.Loot.FirstOrDefault(item => item.Id == wreckId);
                    if (snapshot.OpenLootId == wreckId && contents != null)
                        foreach (ItemView item in contents.Items.Where(item => item != null))
                        {
                            GUI.enabled = !tractoring;
                            if (GUILayout.Button("TRACTOR  " + item.Name + "  ×" + item.Quantity, button))
                                client.TakeLoot(wreckId, item.Slot);
                            GUI.enabled = true;
                        }
                }
            }
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        /// <summary>Keep the newest server message visible and retain the full recent log on demand.</summary>
        private void Communications(Snapshot snapshot)
        {
            float height = communicationsOpen ? 132 : 46;
            Rect panel = new Rect(24, CanvasHeight - 116 - height, 330, height);
            FlightPanel(panel);
            if (GUI.Button(new Rect(panel.x + 9, panel.y + 7, 91, 25),
                "COMMS", communicationsOpen ? selected : button))
                communicationsOpen = !communicationsOpen;
            if (communicationsOpen)
                GUI.Label(new Rect(panel.x + 12, panel.y + 36, 306, 90),
                    string.Join("\n", snapshot.Messages.Reverse().Take(5).Reverse()), small);
            else
                GUI.Label(new Rect(panel.x + 107, panel.y + 10, 211, 24),
                    new GUIContent(FitCommsPreview((snapshot.Messages.LastOrDefault() ??
                        "No recent messages").Split('\n')[0], 211),
                        "Click COMMS to read the last five server messages"), commsPreview);
        }

        /// <returns>string: A single-line message that ends visibly when the preview is narrower.</returns>
        private string FitCommsPreview(string message, float width)
        {
            if (commsPreview.CalcSize(new GUIContent(message)).x <= width) return message;
            int low = 0, high = message.Length;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (commsPreview.CalcSize(new GUIContent(message.Substring(0, middle) + "…")).x <= width)
                    low = middle;
                else high = middle - 1;
            }
            return message.Substring(0, low).TrimEnd() + "…";
        }

        /// <summary>Draw scanner contacts grouped by threat and affiliation.</summary>
        private void DrawContactGroup(Snapshot snapshot, string title, IEnumerable<ShipActor> actors)
        {
            ShipActor[] group = actors.ToArray();
            if (group.Length == 0) return;
            GUILayout.Space(8); GUILayout.Label(title + "  /  " + group.Length, section);
            foreach (ShipActor actor in group)
            {
                string status = actor.MaxHull > 0 ?
                    $"HULL {Mathf.Clamp01(actor.Hull / actor.MaxHull) * 100:0}%" : "HULL UNKNOWN";
                bool crewMember = snapshot.Group.Members.Any(member => member.Id == actor.Id);
                string detail = (crewMember ? "CREW   ·   " : "") +
                    (actor.Position.Distance(snapshot.Position) / 1000).ToString("0.0") + " km   ·   " + status;
                GUIStyle style = contactId == actor.Id ? selected : actor.Hostile ? hostileButton :
                    actor.IsPlayer ? playerButton : button;
                GUIStyle nameStyle = actor.Hostile ? hostileName : actor.IsPlayer ? playerName : contactName;
                float nameHeight = Mathf.Max(21, nameStyle.CalcHeight(new GUIContent(actor.Name), 220));
                float detailHeight = Mathf.Max(18, contactMeta.CalcHeight(new GUIContent(detail), 220));
                float rowHeight = nameHeight + detailHeight + 14;
                Rect row = GUILayoutUtility.GetRect(0, rowHeight, GUILayout.ExpandWidth(true),
                    GUILayout.Height(rowHeight));
                if (GUI.Button(row, GUIContent.none, style)) SelectContact(actor.Id);
                Fill(new Rect(row.x + 2, row.y + 5, 3, row.height - 10),
                    actor.Hostile ? new Color(1f, .4f, .29f) : actor.IsPlayer ? cyan : gold);
                GUI.Label(new Rect(row.x + 9, row.y + 4, row.width - 18, nameHeight),
                    actor.Name, nameStyle);
                GUI.Label(new Rect(row.x + 9, row.y + 5 + nameHeight,
                    row.width - 18, detailHeight), detail, contactMeta);
            }
        }

        /// <summary>Show the server-owned group roster and formation at a glance during flight.</summary>
        private void CrewStrip(Snapshot snapshot)
        {
            if (!snapshot.Group.Joined) return;
            Rect panel = new Rect(430, 62, 268, 54);
            Panel(panel);
            Fill(new Rect(panel.x + 12, panel.y + 13, 6, 6), crewColor);
            GUI.Label(new Rect(panel.x + 25, panel.y + 7, 225, 21),
                $"CREW  {snapshot.Group.Members.Length + 1} PILOTS", section);
            string formation = snapshot.Group.Formation.Length == 0 ? "NO FORMATION" :
                snapshot.Group.Formation.ToUpperInvariant();
            GUI.Label(new Rect(panel.x + 13, panel.y + 29, 244, 19),
                snapshot.Group.Formed ? formation + "  ·  FORMED" : formation, small);
        }

        /// <summary>Label the native own-ship warp phase while travel effects are visible.</summary>
        private void WarpStatus(Snapshot snapshot)
        {
            if (snapshot.WarpPhase == WarpPhase.Idle) return;
            Rect panel = new Rect(710, 62, 220, 54);
            Panel(panel);
            string phase = snapshot.WarpPhase == WarpPhase.Charging ? "CHARGING" :
                snapshot.WarpPhase == WarpPhase.Traveling ? "TRAVELING" : "RECOVERING";
            Fill(new Rect(panel.x + 12, panel.y + 13, 6, 6), cyan);
            GUI.Label(new Rect(panel.x + 25, panel.y + 7, 183, 21), "WARP DRIVE", section);
            GUI.Label(new Rect(panel.x + 13, panel.y + 29, 196, 19),
                snapshot.WarpArrived ? "ARRIVED  ·  " + phase : phase, small);
        }

        /// <summary>Keep own reactor, shield, and hull state visible during combat and travel.</summary>
        private void ShipVitals(Snapshot snapshot)
        {
            bool deviceActive = snapshot.DeviceBuff != null || !string.IsNullOrEmpty(snapshot.ActiveDeviceLabel);
            int buffCount = (deviceActive ? 1 : 0) + (snapshot.PeerBuff != null ? 1 : 0) +
                (snapshot.ShieldBuff != null ? 1 : 0) + (snapshot.ReactorBuff != null ? 1 : 0) +
                (snapshot.SignatureBuff != null ? 1 : 0);
            float height = 87 + ((buffCount + 1) / 2) * 30;
            bool miningPanel = snapshot.ResourceWindowOpen || snapshot.MiningActivity != null;
            float x = miningPanel ? 24 : 365;
            Rect panel = new Rect(x, CanvasHeight - (miningPanel ? 241 : 125) - height, 420, height);
            FlightPanel(panel);
            GUI.Label(new Rect(panel.x + 13, panel.y + 7, 300, 20), "SHIP SYSTEMS", section);
            float energy = Mathf.Clamp01(snapshot.Energy);
            float shield = Mathf.Clamp01(snapshot.Shield);
            float hull = snapshot.MaxHull > 0 ? Mathf.Clamp01(snapshot.Hull / snapshot.MaxHull) : 0;
            GUI.Label(new Rect(panel.x + 13, panel.y + 33, 124, 18), snapshot.MaxEnergy > 0 ?
                $"REACTOR {energy * snapshot.MaxEnergy:0}/{snapshot.MaxEnergy:0}" : "REACTOR SYNCING", vitalText);
            Meter(new Rect(panel.x + 13, panel.y + 58, 124, 7), "", energy, cyan);
            GUI.Label(new Rect(panel.x + 147, panel.y + 33, 124, 18), snapshot.MaxShield > 0 ?
                $"SHIELD {shield * snapshot.MaxShield:0}/{snapshot.MaxShield:0}" : "SHIELD SYNCING", vitalText);
            Meter(new Rect(panel.x + 147, panel.y + 58, 124, 7), "", shield, new Color(.35f, .9f, 1f));
            GUI.Label(new Rect(panel.x + 281, panel.y + 33, 124, 18), snapshot.MaxHull > 0 ?
                $"HULL {Mathf.Max(0, snapshot.Hull):0}/{snapshot.MaxHull:0}" : "HULL SYNCING", vitalText);
            Meter(new Rect(panel.x + 281, panel.y + 58, 124, 7), "", hull, gold);
            int buffIndex = 0;
            if (deviceActive)
            {
                string name = snapshot.DeviceBuff?.Name ?? snapshot.ActiveDeviceLabel;
                float remaining = snapshot.DeviceBuff?.RemainingSeconds ?? snapshot.ActiveDeviceRemainingSeconds;
                if (remaining < 0 && snapshot.ActiveDeviceRemainingSeconds > 0)
                    remaining = snapshot.ActiveDeviceRemainingSeconds;
                DrawBuffRow(panel, buffIndex++, name, remaining, gold);
            }
            if (snapshot.PeerBuff != null)
                DrawBuffRow(panel, buffIndex++, snapshot.PeerBuff.Name,
                    snapshot.PeerBuff.RemainingSeconds, cyan);
            if (snapshot.ShieldBuff != null)
                DrawBuffRow(panel, buffIndex++, snapshot.ShieldBuff.Name,
                    snapshot.ShieldBuff.RemainingSeconds, new Color(.47f, .8f, 1f));
            if (snapshot.ReactorBuff != null)
                DrawBuffRow(panel, buffIndex++, snapshot.ReactorBuff.Name,
                    snapshot.ReactorBuff.RemainingSeconds, new Color(.48f, 1f, .72f));
            if (snapshot.SignatureBuff != null)
                DrawBuffRow(panel, buffIndex, snapshot.SignatureBuff.Name,
                    snapshot.SignatureBuff.RemainingSeconds, new Color(.72f, .67f, 1f));
        }

        /// <returns>string: A short buff label that keeps the complete native name in its tooltip.</returns>
        private static string BuffCaption(string name)
        {
            if (name.IndexOf("SCAN", StringComparison.OrdinalIgnoreCase) >= 0) return "SCAN RANGE";
            if (name.IndexOf("SHIELD", StringComparison.OrdinalIgnoreCase) >= 0) return "SHIELD REGEN";
            if (name.IndexOf("REACTOR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("ENERGY", StringComparison.OrdinalIgnoreCase) >= 0) return "REACTOR REGEN";
            if (name.IndexOf("SIGNATURE", StringComparison.OrdinalIgnoreCase) >= 0) return "LOW SIGNATURE";
            return name.ToUpperInvariant();
        }

        /// <summary>Show one authoritative timed buff in the compact two-column grid.</summary>
        private void DrawBuffRow(Rect panel, int index, string name, float remaining, Color accent)
        {
            float x = panel.x + 10 + index % 2 * 202;
            float y = panel.y + 86 + index / 2 * 30;
            Fill(new Rect(x, y, 196, 25), new Color(.06f, .15f, .2f, .8f));
            Fill(new Rect(x + 5, y + 5, 15, 15), accent);
            Fill(new Rect(x + 9, y + 8, 7, 9), new Color(.07f, .14f, .19f));
            GUI.Label(new Rect(x + 26, y + 3, 125, 20),
                new GUIContent(BuffCaption(name), name), buffText);
            GUI.Label(new Rect(x + 153, y + 3, 41, 20),
                remaining < 0 ? "SYNC" : $"{Mathf.CeilToInt(remaining)}s", buffText);
        }

        /// <summary>Manage invitations and formation separately from tactical contacts.</summary>
        private void CrewRoster(Snapshot snapshot)
        {
            GUILayout.Label(snapshot.Group.Joined ? "CREW" + (snapshot.Group.IsLeader ? "  /  LEADER" : "") : "CREW  /  SOLO", section);
            GUILayout.Label("Invite any online toon by name", small);
            GUI.SetNextControlName("Group invite");
            inviteName = GUILayout.TextField(inviteName, 20);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Invite", button, GUILayout.Height(28))) InviteByName();
            if (GUILayout.Button("Who is online", button, GUILayout.Height(28))) client.SendChat("/who");
            GUILayout.EndHorizontal();
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return &&
                GUI.GetNameOfFocusedControl() == "Group invite")
            { InviteByName(); Event.current.Use(); }
            if (snapshot.Confirmation != null && snapshot.Confirmation.Kind != ConfirmationKind.Jumpstart)
            {
                GUILayout.Label(snapshot.Confirmation.Text, body);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Accept", button)) client.AnswerConfirmation(true);
                if (GUILayout.Button("Decline", button)) client.AnswerConfirmation(false);
                GUILayout.EndHorizontal();
            }
            foreach (GroupMember member in snapshot.Group.Members)
            {
                GUILayout.Label(member.Name, body);
                GUILayout.Label(member.Formed ? "IN FORMATION" : "CREW MEMBER", small);
                bool visible = snapshot.Players.Any(actor => actor.Id == member.Id && actor.HasPosition);
                GUILayout.BeginHorizontal();
                GUI.enabled = visible;
                if (GUILayout.Button("TARGET", button, GUILayout.Width(77))) SelectContact(member.Id);
                GUI.enabled = visible && !snapshot.Group.Formed;
                if (GUILayout.Button(snapshot.Group.Formed ? "FORMED" : "FOLLOW", button, GUILayout.Width(78)))
                { SelectContact(member.Id); client.FollowSelected(); }
                GUI.enabled = true;
                if (snapshot.Group.IsLeader && GUILayout.Button("KICK", button, GUILayout.Width(57)))
                    client.SendChat("/kick " + member.Name);
                GUILayout.EndHorizontal();
                if (!visible) GUILayout.Label("Outside sensor range", small);
                GUILayout.Space(7);
            }
            if (snapshot.Group.Joined)
            {
                if (snapshot.Group.IsLeader && snapshot.SelectedTargetId != 0 && snapshot.SelectedTargetId != uint.MaxValue &&
                    GUILayout.Button("Share selected target", button)) client.Formation(12);
                GUILayout.Space(8);
                GUILayout.Label("FORMATION  " + (snapshot.Group.Formation.Length > 0 ? snapshot.Group.Formation.ToUpperInvariant() : "NONE"), small);
                if (snapshot.Group.IsLeader)
                {
                    GUILayout.BeginHorizontal();
                    foreach (var choice in new[] { (4u, "Slot Back"), (5u, "Block"), (6u, "Pipe") })
                        if (GUILayout.Button(choice.Item2, snapshot.Group.Formation == choice.Item2 ? selected : button))
                            client.Formation(choice.Item1);
                    GUILayout.EndHorizontal();
                    if (snapshot.Group.Formation.Length > 0 && GUILayout.Button("Release formation", button)) client.Formation(9);
                }
                else if (snapshot.Group.Formation.Length > 0)
                {
                    if (GUILayout.Button(snapshot.Group.Formed ? "Leave formation" : "Form up with leader", button))
                        client.Formation(snapshot.Group.Formed ? 8u : 7u);
                    if (!snapshot.Group.Formed) GUILayout.Label("Join within 5 km of your leader", small);
                    else GUILayout.Label("Leader controls travel and warp; leave formation to fly alone.", small);
                }
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Leave", button)) client.SendChat("/leave");
                if (snapshot.Group.IsLeader && GUILayout.Button("Disband", button)) client.SendChat("/disband");
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>Draw cargo, hardpoints and a large item inspection bay.</summary>
        private void Equipment(Snapshot snapshot)
        {
            Rect rect = EquipmentRect();
            Fill(rect, new Color(.002f, .008f, .016f, .98f));
            Panel(rect);
            float left = rect.x + 20, top = rect.y + 15;
            GUI.Label(new Rect(left, top, 570, 32), "SHIP LOADOUT", heading);
            if (GUI.Button(new Rect(rect.xMax - 48, top, 28, 28), "×", button)) equipmentOpen = false;
            GUI.Label(new Rect(left, top + 35, 800, 19),
                "HULL STAGE " + (snapshot.Experience == null ? "SYNCING" :
                    snapshot.Experience.HullStage + " / 6") + "   ·   CARGO CAPACITY " +
                    (snapshot.CargoCapacity == 0 ? "SYNCING" : snapshot.CargoCapacity + " SLOTS"), small);
            GUI.Label(new Rect(left, top + 54, 800, 19),
                $"{snapshot.WeaponMounts} WEAPON HARDPOINTS   ·   {snapshot.DeviceMounts} DEVICE SLOTS   ·   SERVER EQUIPMENT", small);
            float listTop = top + 82, listHeight = rect.height - 222;
            float mountX = left, cargoX = left + 405, detailX = left + 655;
            DrawMountedList(snapshot, new Rect(mountX, listTop, 390, listHeight));
            DrawCargoList(snapshot, new Rect(cargoX, listTop, 235, listHeight));
            DrawItemDetail(snapshot, new Rect(detailX, listTop, 265, listHeight));
            ItemView cargo = selectedCargo >= 0 && selectedCargo < snapshot.Cargo.Length ? snapshot.Cargo[selectedCargo] : null;
            ItemView mounted = selectedMount >= 0 && selectedMount < snapshot.Mounted.Length ? snapshot.Mounted[selectedMount] : null;
            bool cargoSpace = snapshot.Cargo.Take((int)snapshot.CargoCapacity)
                .Any(item => item == null);
            bool fits = FitsHardpoint(cargo, selectedMount, snapshot);
            float actionY = rect.yMax - 104;
            Fill(new Rect(left, actionY - 13, rect.width - 40, 1), new Color(.3f, .65f, .75f, .65f));
            GUI.Label(new Rect(left, actionY - 1, 920, 19),
                "SELECT CARGO + A COMPATIBLE SLOT TO INSTALL   /   SELECT A FILLED SLOT TO REMOVE", small);
            GUI.enabled = client.Ready && fits;
            if (GUI.Button(new Rect(left, actionY + 23, 390, 43),
                cargo?.Type == 10 ? "LOAD AMMO  →" : "INSTALL  →", primaryButton))
                client.EquipCargo(selectedCargo, selectedMount);
            GUI.enabled = client.Ready && mounted != null && cargoSpace;
            if (GUI.Button(new Rect(cargoX, actionY + 23, 235, 43),
                cargoSpace ? "←  REMOVE TO CARGO" : "CARGO HOLD FULL", button))
                client.UnequipMount(selectedMount);
            bool weapon = mounted != null && mounted.Type >= 14 && mounted.Type <= 16 &&
                selectedMount >= 3 && selectedMount < 3 + snapshot.WeaponMounts;
            bool device = mounted != null && DeviceEffects.MountedSlot(mounted.ActivationLabel,
                mounted.Type, selectedMount, snapshot.DeviceMounts);
            bool targetedSupport = device && mounted.TargetsPeer;
            bool needsPeer = device && DeviceEffects.RequiresPeer(mounted.ActivationLabel);
            ShipActor recipient = device && (targetedSupport || needsPeer) ?
                snapshot.Players.FirstOrDefault(actor =>
                actor.Id == snapshot.SelectedTargetId && actor.HasPosition &&
                (!targetedSupport || snapshot.Group.Members.Any(member => member.Id == actor.Id))) : null;
            GUI.enabled = client.Ready && !client.Incapacitated && snapshot.Station == null &&
                (weapon || device && (DeviceEffects.IsInstant(mounted.ActivationLabel) ||
                    targetedSupport || snapshot.ActiveDeviceLabel.Length == 0) && (!needsPeer || recipient != null));
            if (GUI.Button(new Rect(detailX, actionY + 23, 265, 43),
                weapon ? (mounted.WeaponState & 1) != 0 ? "STOP SELECTED WEAPON" : "FIRE SELECTED WEAPON" :
                needsPeer ? recipient == null ? "SELECT A PILOT FIRST" :
                    "TRANSFER TO " + recipient.Name.ToUpperInvariant() :
                targetedSupport ? recipient == null ? "ACTIVATE ON SELF" :
                    "SUPPORT " + recipient.Name.ToUpperInvariant() :
                device ? !DeviceEffects.IsInstant(mounted.ActivationLabel) && snapshot.ActiveDeviceLabel.Length > 0 ?
                    snapshot.ActiveDeviceLabel.ToUpperInvariant() + " ACTIVE" :
                    mounted.ActivationLabel.Length > 20 ? "ACTIVATE SELECTED EFFECT" :
                    "ACTIVATE " + mounted.ActivationLabel.ToUpperInvariant() : "SELECT A SYSTEM",
                weapon ? hostileButton : primaryButton))
            {
                if (weapon) client.ActivateWeapon(selectedMount);
                else if (device && (targetedSupport || needsPeer))
                    client.ActivateDeviceFor(selectedMount, recipient?.Id ?? snapshot.PlayerId);
                else if (device) client.ActivateDevice(selectedMount);
            }
            GUI.enabled = true;
        }

        /// <summary>List installed systems by slot and report weapon ammunition state.</summary>
        private void DrawMountedList(Snapshot snapshot, Rect rect)
        {
            GUILayout.BeginArea(rect);
            GUILayout.Label("INSTALLED / HARDPOINTS", section);
            mountScroll = GUILayout.BeginScrollView(mountScroll);
            for (int slot = 0; slot < snapshot.Mounted.Length; slot++)
            {
                bool available = slot < 3 || slot >= 3 && slot < 3 + snapshot.WeaponMounts ||
                    slot >= 9 && slot < 9 + snapshot.DeviceMounts;
                if (!available) continue;
                if (slot == 0) GUILayout.Label("CORE SYSTEMS", small);
                if (slot == 3) GUILayout.Label("WEAPONS", small);
                if (slot == 9) GUILayout.Label("DEVICES", small);
                ItemView item = snapshot.Mounted[slot];
                string slotName = slot < 3 ? new[] { "SHIELD", "REACTOR", "ENGINE" }[slot] : slot < 9 ?
                    "WEAPON " + (slot - 2) : "DEVICE " + (slot - 8);
                string status = item == null ? "OPEN HARDPOINT" : "CONDITION " +
                    (Mathf.Clamp01(item.Structure) * 100).ToString("0") + "%";
                bool weapon = item != null && item.Type >= 14 && item.Type <= 16;
                if (weapon)
                {
                    ItemView ammo = slot < snapshot.Ammo.Length ? snapshot.Ammo[slot] : null;
                    status = item.OutOfAmmo ? "◌ NO AMMO" : item.Cooling ?
                        $"◔ RELOADING  {item.CooldownSeconds:0.0}s" :
                        (item.WeaponState & 1) != 0 ? "● FIRING" : "○ SAFE";
                    status += item.Type == 14 ? "   ·   BEAM" : ammo == null ? "" :
                            "   ·   " + ammo.Quantity + " ROUNDS";
                }
                uint iconType = item?.Type ?? (slot == 0 ? 2u : slot == 1 ? 7u : slot == 2 ? 6u : slot < 9 ? 14u : 11u);
                if (DrawEquipmentRow(slotName, item?.Name ?? "EMPTY", status, iconType,
                    selectedMount == slot, rect.width, item != null && item.OutOfAmmo))
                    { selectedMount = selectedMount == slot ? -1 : slot; inspectMount = true; }
            }
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        /// <summary>List cargo with category, stack count and condition.</summary>
        private void DrawCargoList(Snapshot snapshot, Rect rect)
        {
            GUILayout.BeginArea(rect);
            int used = snapshot.Cargo.Count(item => item != null);
            GUILayout.Label("CARGO HOLD  /  " + used + " OF " +
                (snapshot.CargoCapacity == 0 ? "?" : snapshot.CargoCapacity.ToString()), section);
            cargoScroll = GUILayout.BeginScrollView(cargoScroll);
            foreach (ItemView item in snapshot.Cargo)
            {
                if (item == null) continue;
                string category = ItemCategory(item.Type);
                string status = (item.Quantity > 1 ? "×" + item.Quantity + "   ·   " : "") +
                    (Mathf.Clamp01(item.Structure) * 100).ToString("0") + "% CONDITION";
                if (DrawEquipmentRow(category, item.Name, status, item.Type,
                    selectedCargo == item.Slot, rect.width, false))
                    { selectedCargo = selectedCargo == item.Slot ? -1 : item.Slot; inspectMount = false; }
            }
            if (used == 0) GUILayout.Label("Cargo hold empty", small);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        /// <returns>bool: Whether the user selected a structured equipment row.</returns>
        private bool DrawEquipmentRow(string slot, string name, string status, uint type,
            bool active, float areaWidth, bool warning)
        {
            float nameWidth = Mathf.Max(65, areaWidth - 152);
            float height = Mathf.Max(61, equipmentText.CalcHeight(new GUIContent(name), nameWidth) + 30);
            Rect row = GUILayoutUtility.GetRect(0, height, GUILayout.ExpandWidth(true));
            bool clicked = GUI.Button(row, "", active ? selected : equipmentRow);
            GUI.DrawTexture(new Rect(row.x + 7, row.y + 9, 30, 30), icons.ForType(type));
            GUI.Label(new Rect(row.x + 43, row.y + 5, 79, 22), slot, equipmentMeta);
            GUI.Label(new Rect(row.x + 125, row.y + 4, row.width - 133, height - 29), name, equipmentText);
            Color old = GUI.color;
            if (warning) GUI.color = new Color(1f, .55f, .43f);
            GUI.Label(new Rect(row.x + 43, row.yMax - 23, row.width - 49, 20), status, equipmentMeta);
            GUI.color = old;
            return clicked;
        }

        /// <returns>string: Plain language category for one item type.</returns>
        private static string ItemCategory(uint type)
        {
            switch (type)
            {
                case 2: return "SHIELD";
                case 7: return "REACTOR";
                case 6: return "ENGINE";
                case 10: return "AMMUNITION";
                case 11: return "DEVICE";
                case 14: case 15: case 16: return "WEAPON";
                default: return "SUPPLY";
            }
        }

        /// <summary>Show detailed condition and stats for the focused cargo or installed item.</summary>
        private void DrawItemDetail(Snapshot snapshot, Rect rect)
        {
            ItemView cargo = selectedCargo >= 0 && selectedCargo < snapshot.Cargo.Length ? snapshot.Cargo[selectedCargo] : null;
            ItemView mount = selectedMount >= 0 && selectedMount < snapshot.Mounted.Length ? snapshot.Mounted[selectedMount] : null;
            ItemView item = inspectMount ? mount ?? cargo : cargo ?? mount;
            Panel(rect);
            float x = rect.x + 13, y = rect.y + 14, width = rect.width - 26;
            GUI.Label(new Rect(x, y, width, 22), "ITEM INSPECTOR", section);
            if (item == null)
            {
                GUI.Label(new Rect(x, y + 43, width, 75),
                    "SELECT A SYSTEM", body);
                GUI.Label(new Rect(x, y + 104, width, 76),
                    "Choose an installed hardpoint or cargo item to inspect its condition and use its controls.", wrappedBody);
                GUI.Label(new Rect(x, y + 199, width, 82),
                    "To install: select a cargo item, then a compatible hardpoint. The server confirms every change.", small);
                return;
            }
            GUI.DrawTexture(new Rect(x, y + 41, 40, 40), icons.ForType(item.Type));
            GUI.Label(new Rect(x + 49, y + 36, width - 49, 48), item.Name.ToUpperInvariant(),
                item.Name.Length > 16 ? equipmentText : wrappedBody);
            GUI.Label(new Rect(x, y + 88, width, 20), ItemCategory(item.Type) + "   ·   TEMPLATE " + item.Template, small);
            GUI.Label(new Rect(x, y + 118, width, 18),
                "QUALITY  " + (Mathf.Clamp01(item.Quality) * 100).ToString("0") + "%", small);
            Meter(new Rect(x, y + 141, width, 8), "", item.Quality, cyan);
            GUI.Label(new Rect(x, y + 162, width, 18),
                "STRUCTURE  " + (Mathf.Clamp01(item.Structure) * 100).ToString("0") + "%", small);
            Meter(new Rect(x, y + 185, width, 8), "", item.Structure, gold);
            if (item.Range > 0) GUI.Label(new Rect(x, y + 211, width, 24),
                "RANGE  " + item.Range.ToString("N0") + " m", body);
            if (item.Price > 0) GUI.Label(new Rect(x, y + 244, width, 20),
                "VALUE  " + item.Price.ToString("N0") + " CREDITS", small);
            GUI.Label(new Rect(x, y + 270, width, 20), "STACK  ×" + item.Quantity, small);
            bool needsPeer = item == mount && (item.TargetsPeer ||
                DeviceEffects.RequiresPeer(item.ActivationLabel));
            if (!needsPeer && !string.IsNullOrWhiteSpace(item.Info) && item.Info.Count(char.IsLetter) >= 3 && !item.Info.Contains("^"))
                GUI.Label(new Rect(x, y + 298, width, rect.height - 312), item.Info, small);
            if (needsPeer) DrawDeviceRecipient(snapshot, rect, item.TargetsPeer);
        }

        /// <summary>Choose a visible pilot while keeping the loadout panel open.</summary>
        private void DrawDeviceRecipient(Snapshot snapshot, Rect rect, bool crewOnly)
        {
            ShipActor[] pilots = snapshot.Players.Where(actor => actor.HasPosition &&
                    (!crewOnly || snapshot.Group.Members.Any(member => member.Id == actor.Id)))
                .OrderBy(actor => actor.Position.Distance(snapshot.Position)).ToArray();
            ShipActor chosen = pilots.FirstOrDefault(actor => actor.Id == snapshot.SelectedTargetId);
            float x = rect.x + 13, width = rect.width - 26;
            GUI.Label(new Rect(x, rect.yMax - 85, width, 19),
                chosen == null ? crewOnly ? "DEVICE TARGET  ·  YOUR SHIP" :
                    "SELECT A PILOT TO RECEIVE ENERGY" :
                $"{chosen.Position.Distance(snapshot.Position) / 1000:0.0} KM  ·  SERVER CHECKS", small);
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && crewOnly;
            if (GUI.Button(new Rect(x, rect.yMax - 60, 80, 34), "SELF", button))
                client.SelectAbilitySelf();
            GUI.enabled = enabled && pilots.Length > 0;
            if (GUI.Button(new Rect(x + 86, rect.yMax - 60, width - 86, 34),
                chosen == null ? "CHOOSE VISIBLE PILOT" : "RECIPIENT: " + chosen.Name, button))
            {
                int index = Array.FindIndex(pilots, actor => actor.Id == snapshot.SelectedTargetId);
                client.SelectContact(pilots[(index + 1) % pilots.Length].Id);
            }
            GUI.enabled = enabled;
        }

        /// <summary>Check the item category against the unlocked ship hardpoint.</summary>
        /// <returns>bool: Whether the selected cargo can be installed or loaded in this slot.</returns>
        private static bool FitsHardpoint(ItemView cargo, int slot, Snapshot snapshot)
        {
            if (cargo == null || slot < 0 || slot >= snapshot.Mounted.Length) return false;
            if (cargo.Type == 10)
                return slot >= 3 && slot < 3 + snapshot.WeaponMounts &&
                    snapshot.Mounted[slot]?.Type >= 15 && snapshot.Mounted[slot]?.Type <= 16;
            if (cargo.Quantity != 1) return false;
            switch (cargo.Type)
            {
                case 2: return slot == 0;
                case 7: return slot == 1;
                case 6: return slot == 2;
                case 14: case 15: case 16: return slot >= 3 && slot < 3 + snapshot.WeaponMounts;
                case 11: return slot >= 9 && slot < 9 + snapshot.DeviceMounts;
                default: return false;
            }
        }

        /// <summary>Submit a named invitation to the server, including players outside scan range.</summary>
        private void InviteByName()
        {
            string name = inviteName.Trim();
            if (name.Length == 0) return;
            client.SendChat("/invite " + name);
            inviteName = "";
            GUI.FocusControl("");
        }

        /// <summary>Release generated button backgrounds.</summary>
        public void Dispose()
        {
            if (buttonTexture != null) UnityEngine.Object.Destroy(buttonTexture);
            if (selectedTexture != null) UnityEngine.Object.Destroy(selectedTexture);
            if (panelTexture != null) UnityEngine.Object.Destroy(panelTexture);
            if (radarTexture != null) UnityEngine.Object.Destroy(radarTexture);
            icons.Dispose();
        }
    }
}
