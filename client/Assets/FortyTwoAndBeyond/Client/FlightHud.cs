using System;
using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;
using CharacterRecord = FortyTwoAndBeyond.Protocol.CharacterInfo;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Flight instruments and navigation controls for the foreground client.</summary>
    public sealed partial class FlightHud : IDisposable
    {
        private readonly SpaceClient client;
        private readonly EquipmentIcons icons;
        internal EquipmentIcons ItemIcons => icons;
        private LoadoutShipPreview shipPreview;
        private Texture2D fittingRing;
        private Texture2D fittingSocket;
        private Vector2 weaponWidgetOffset, weaponWidgetDragStart, weaponWidgetOffsetStart;
        private bool draggingWeaponWidget;
        private static readonly Vector2[] LabelOffsets = { new Vector2(12, -48), new Vector2(12, 12),
            new Vector2(-184, -48), new Vector2(-184, 12) };
        private static readonly Vector2[] ContactLabelOffsets = { new Vector2(-290, -68),
            new Vector2(-290, 24), new Vector2(40, -68), new Vector2(40, 24) };
        private float interfaceSize = 1f;
        /// <summary>
        /// Returns:
        ///     float: A readable HUD scale that still fits the loadout window on screen.
        /// </summary>
        public float Scale
        {
            get
            {
                float fit = Mathf.Min(Screen.width / 1280f, Screen.height / 760f);
                float responsive = fit <= 1f ? fit : Mathf.Min(2f, 1f + (fit - 1f) * .55f);
                float maximum = Mathf.Min(Screen.width / 1200f, Screen.height / 720f);
                return Mathf.Clamp(responsive * interfaceSize, .01f, Mathf.Max(.01f, maximum));
            }
        }
        private float CanvasWidth => Screen.width / Scale;
        private float CanvasHeight => Screen.height / Scale;
        private readonly Color cyan = new Color(.3f, .85f, 1), white = new Color(.91f, .95f, 1), muted = new Color(.68f, .79f, .86f), gold = new Color(1, .72f, .3f), crewColor = new Color(.39f, 1f, .68f);
        private GUIStyle heading, body, wrappedBody, small, commsPreview, section, button, selected, playerButton, hostileButton, damageStyle, input, primaryButton, equipmentRow, equipmentText, equipmentMeta, weaponWidgetText, vitalText, vitalValue, buffText, contactName, hostileName, playerName, contactMeta, dialogueText, dialogueChoice, dialogueMeta;
        private Texture2D buttonTexture, selectedTexture, panelTexture, radarTexture, weaveTexture, frameTexture, buffIcons;
        private static readonly Dictionary<string, int> BuffIconIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Turbo Weapons"] = 0, ["Improved Beam Handling"] = 1,
            ["Improved Projectile Handling"] = 2, ["Combat Trance"] = 3,
            ["Damage Tactics"] = 4, ["Firing Tactics"] = 5,
            ["Recharge Shields"] = 6, ["Shield Charging"] = 7,
            ["Supercharge Shields"] = 7, ["Ultracharge Shields"] = 7,
            ["Supercharge Target"] = 7, ["Ultracharge Target"] = 7,
            ["Megacharge Shields"] = 7,
            ["Maximize Shields"] = 8, ["Deflect Impact"] = 9,
            ["Deflect Explosive"] = 10, ["Deflect Plasma"] = 11,
            ["Deflect Energy"] = 12, ["Deflect EMP"] = 13,
            ["Deflect Chemical"] = 14, ["Resist Psionic"] = 15,
            ["Psionic Shield"] = 16, ["Environment Shield"] = 17,
            ["Reactor Boost"] = 18, ["Recharge Reactor"] = 19,
            ["Reactor Optimization"] = 20, ["Shunt to Reactor"] = 21,
            ["Shield Inversion"] = 22, ["Shield Sap"] = 23,
            ["Increase Speed"] = 24, ["Impulse Turbo"] = 25,
            ["Turbo Warp"] = 26, ["Increase Maneuverability"] = 27,
            ["Scan Boost"] = 28, ["Cloak"] = 29,
            ["Defense Tactics"] = 30, ["Stealth Tactics"] = 31,
            ["Combat Cloak"] = 32, ["Repulsor Field"] = 33,
            ["Repair Equipment"] = 34, ["Power Down"] = 35,
            ["Reduce Ship Signature"] = 29,
        };
        internal GUIStyle BodyStyle => body;
        internal GUIStyle SmallStyle => small;
        internal GUIStyle SectionStyle => section;
        internal GUIStyle ButtonStyle => button;
        private Vector2 scroll;
        private bool navigation = true, equipmentOpen, settingsOpen, crewTab, inspectMount, inspectAmmo, inspectEffects,
            communicationsOpen;
        private bool clearInputFocus;
        private ControlAction? bindingAction;
        private Vector2 settingsScroll, characterScroll, demoScroll;
        private int selectedCargo = -1, selectedMount = -1, selectedAmmo = -1;
        private uint draggedContainer;
        private int draggedSlot = -1;
        private Vector3 dragStartPosition;
        private bool inventoryDragging, dragCanMove;
        private Vector2 mountedOffset, cargoOffset, ammoOffset, panelDragStart, panelOffsetStart;
        private int draggedLoadoutPanel;
        private Vector2 inventoryOffset, inventoryDragStart, inventoryOffsetStart;
        private bool draggingInventoryWindow;
        private Vector2 cargoScroll, mountScroll, ammoWidgetScroll;
        private Vector2 effectScroll, buffScroll;
        public bool EquipmentOpen => equipmentOpen;
        public bool SettingsOpen => settingsOpen;
        public bool GroupInviteOpen => client.CurrentSnapshot?.Confirmation?.Kind == ConfirmationKind.Group;
        private readonly ContactSelection contactSelection = new ContactSelection();
        private uint contactId => contactSelection.SelectedId;
        private float serverSelectedUntil;
        private Vector2 targetPanelPosition, targetDragOffset;
        private bool targetPanelLoaded, draggingTargetPanel;
        private readonly List<Rect> occupiedFlightLabels = new List<Rect>();
        private readonly List<(Rect Bounds, uint Id, byte Kind)> worldLabelHits =
            new List<(Rect Bounds, uint Id, byte Kind)>();
        /// <returns>bool: Whether a visible world label selected its linked object.</returns>
        public bool SelectWorldLabelAtPointer(Vector2 screenPointer)
        {
            Vector2 pointer = screenPointer / Scale;
            for (int index = worldLabelHits.Count - 1; index >= 0; index--)
            {
                var hit = worldLabelHits[index];
                if (!hit.Bounds.Contains(pointer)) continue;
                if (hit.Kind == 1) SelectContact(hit.Id);
                else if (hit.Kind == 2) SelectWreck(hit.Id);
                else client.Select(hit.Id);
                return true;
            }
            return false;
        }
        /// <returns>bool: Whether a flight label fits without covering another label.</returns>
        internal bool ReserveFlightLabel(Rect bounds)
        {
            if (occupiedFlightLabels.Any(other => other.Overlaps(bounds))) return false;
            occupiedFlightLabels.Add(bounds);
            return true;
        }
        public uint SelectedContactId => contactId;
        /// <summary>Keep keyboard-selected contacts aligned with the tactical HUD.</summary>
        public void SelectContact(uint id)
        { contactSelection.SelectLocal(id, Time.unscaledTime); wreckId = 0; client.SelectContact(id); }
        /// <summary>Dismiss fixed ship and wreck panels when another world target takes focus.</summary>
        public void ClearSelection() { contactSelection.ClearLocal(); wreckId = 0; }
        /// <summary>Forget target history when leaving the current sector or character.</summary>
        public void ResetTargetSelection()
        { contactSelection.Reset(); wreckId = 0; serverSelectedUntil = 0; worldLabelHits.Clear(); }
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
        internal readonly ChatRecall ChatHistory = new ChatRecall();
        public bool InputFocused { get; private set; }
        private bool groupChat;
        private uint wreckId;
        private Vector2 lootScroll;
        private uint invitePendingId;
        private float invitePendingAt;
        private Vector2 contactsScroll;
        private Vector2 npcScroll, dialogueScroll;
        private Vector2 vendorScroll, saleScroll;
        private SingleRoomStationHud stationConcourse;
        private bool stationDialogueCollapsed;
        private uint stationDialogueNpc;
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
        public FlightHud(SpaceClient client)
        {
            this.client = client; icons = new EquipmentIcons();
            interfaceSize = Mathf.Clamp(PlayerPrefs.GetFloat("42andbeyond.interface-size", 1f), .85f, 1.3f);
        }

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
        { return Mathf.Clamp(CanvasHeight - 555, 160, 230); }

        /// <returns>Whether the pointer is over foreground controls.</returns>
        public bool ContainsPointer()
        {
            if (client.ComponentMarketOpen) return true;
            Vector2 p = new Vector2(Input.mousePosition.x / Scale, (Screen.height - Input.mousePosition.y) / Scale);
            if (client.Chart && client.CurrentSnapshot?.Station == null &&
                (tacticalMap == null || tacticalMap.ContainsPointer(p))) return true;
            if (deck != null && deck.Visible) return deck.ContainsPointer() ||
                client.CurrentSnapshot?.Station != null ||
                (crewTab && new Rect(24, 100, 280, SocialHeight(client.CurrentSnapshot)).Contains(p)) ||
                p.y > CanvasHeight - 115 || p.y < 65;
            return !client.Connected || (settingsOpen && SettingsRect().Contains(p)) ||
                (equipmentOpen && EquipmentRect().Contains(p)) ||
                (client.CurrentSnapshot?.Station == null && InventoryButtonRect().Contains(p)) ||
                (client.CurrentSnapshot?.Station == null && !equipmentOpen &&
                    WeaponWidgetRect(client.CurrentSnapshot).Contains(p)) ||
                (TargetPanelRect(client.CurrentSnapshot).Contains(p)) ||
                new Rect(24, 100, 280, SocialHeight(client.CurrentSnapshot)).Contains(p) ||
                (p.x < 354 && p.y > CanvasHeight - (communicationsOpen ? 248 : 162)) ||
                new Rect(CanvasWidth - 320, 20, 300,
                    navigation ? NavigationListHeight() +
                        (client.TargetActor != null && client.Target == null ? 100 : 247) : 78).Contains(p) ||
                new Rect(CanvasWidth - 214, CanvasHeight - 198, 190, 174).Contains(p) ||
                (client.CurrentSnapshot != null && ShipVitalsRect(client.CurrentSnapshot).Contains(p)) ||
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
            Font regularFont = Resources.Load<Font>("Fonts/IBMPlexSans-Regular");
            Font mediumFont = Resources.Load<Font>("Fonts/IBMPlexSans-Medium");
            Font semiboldFont = Resources.Load<Font>("Fonts/IBMPlexSans-SemiBold");
            heading = new GUIStyle(GUI.skin.label) { font = semiboldFont, fontSize = 27,
                fontStyle = FontStyle.Normal }; heading.normal.textColor = white;
            body = new GUIStyle(GUI.skin.label) { font = mediumFont, fontSize = 16,
                fontStyle = FontStyle.Normal }; body.normal.textColor = white;
            wrappedBody = new GUIStyle(body) { wordWrap = true };
            small = new GUIStyle(GUI.skin.label) { font = regularFont, fontSize = 14 };
            small.normal.textColor = muted;
            commsPreview = new GUIStyle(small) { wordWrap = false, clipping = TextClipping.Clip };
            vitalText = new GUIStyle(small) { font = semiboldFont, fontSize = 12 };
            vitalText.normal.textColor = white;
            vitalValue = new GUIStyle(small) { font = semiboldFont, fontSize = 16,
                alignment = TextAnchor.MiddleRight, clipping = TextClipping.Clip };
            vitalValue.normal.textColor = white;
            buffText = new GUIStyle(small) { font = mediumFont, fontSize = 13 };
            buffText.normal.textColor = white;
            contactName = new GUIStyle(wrappedBody) { fontSize = 16 };
            hostileName = new GUIStyle(contactName); hostileName.normal.textColor = new Color(1f, .58f, .46f);
            playerName = new GUIStyle(contactName); playerName.normal.textColor = cyan;
            contactMeta = new GUIStyle(small) { wordWrap = true };
            dialogueText = new GUIStyle(wrappedBody) { fontSize = 17, padding = new RectOffset(4, 4, 5, 7) };
            dialogueMeta = new GUIStyle(small) { fontSize = 13, wordWrap = true };
            dialogueMeta.normal.textColor = new Color(.66f, .8f, .89f);
            section = new GUIStyle(GUI.skin.label) { font = semiboldFont, fontSize = 15,
                fontStyle = FontStyle.Normal }; section.normal.textColor = cyan;
            damageStyle = new GUIStyle(GUI.skin.label) { font = semiboldFont, fontSize = 20,
                fontStyle = FontStyle.Normal, alignment = TextAnchor.MiddleCenter };
            damageStyle.normal.textColor = gold;
            panelTexture = SurfaceTexture(new Color(.075f, .09f, .105f, .95f),
                new Color(.045f, .06f, .075f, .95f), 64, 64);
            weaveTexture = Resources.Load<Texture2D>("HudWeave");
            frameTexture = Resources.Load<Texture2D>("HudFrame");
            buffIcons = Resources.Load<Texture2D>("BuffIcons");
            if (weaveTexture != null) weaveTexture.wrapMode = TextureWrapMode.Repeat;
            buttonTexture = SurfaceTexture(new Color(.13f, .16f, .18f, .96f),
                new Color(.08f, .10f, .12f, .96f), 32, 32);
            selectedTexture = SurfaceTexture(new Color(.28f, .24f, .17f, .98f),
                new Color(.17f, .15f, .11f, .98f), 32, 32);
            radarTexture = RadarTexture();
            button = new GUIStyle(GUI.skin.button) { font = mediumFont, fontSize = 15,
                fontStyle = FontStyle.Normal, wordWrap = true,
                alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 8, 4, 4) };
            button.normal.background = buttonTexture; button.normal.textColor = white;
            button.hover.background = selectedTexture; button.hover.textColor = cyan;
            dialogueChoice = new GUIStyle(button) { fontSize = 14, padding = new RectOffset(13, 10, 7, 7) };
            selected = new GUIStyle(button); selected.normal.background = selectedTexture; selected.normal.textColor = cyan;
            playerButton = new GUIStyle(button); playerButton.normal.textColor = cyan;
            hostileButton = new GUIStyle(button); hostileButton.normal.textColor = new Color(1f, .53f, .4f);
            input = new GUIStyle(GUI.skin.textField) { font = regularFont, fontSize = 16,
                padding = new RectOffset(10, 10, 7, 7) };
            input.normal.background = buttonTexture; input.focused.background = selectedTexture;
            input.normal.textColor = white; input.focused.textColor = white;
            primaryButton = new GUIStyle(button) { alignment = TextAnchor.MiddleCenter, fontSize = 17 };
            primaryButton.normal.background = selectedTexture; primaryButton.normal.textColor = white;
            primaryButton.hover.background = selectedTexture; primaryButton.hover.textColor = cyan;
            equipmentRow = new GUIStyle(button) { fontSize = 13, padding = new RectOffset(8, 6, 4, 4) };
            equipmentText = new GUIStyle(GUI.skin.label) { font = mediumFont, fontSize = 14,
                wordWrap = true, alignment = TextAnchor.MiddleLeft };
            equipmentText.normal.textColor = white;
            equipmentMeta = new GUIStyle(GUI.skin.label) { font = regularFont, fontSize = 13,
                alignment = TextAnchor.MiddleLeft, wordWrap = false };
            equipmentMeta.normal.textColor = muted;
            weaponWidgetText = new GUIStyle(equipmentMeta) { font = mediumFont };
            weaponWidgetText.normal.textColor = white;
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

        /// <summary>Draw a layered gauge with a lit edge and restrained segment marks.</summary>
        private void Meter(Rect rect, string label, float value, Color color)
        {
            float fraction = Mathf.Clamp01(value);
            Fill(rect, new Color(.035f, .085f, .13f, .95f));
            Fill(new Rect(rect.x, rect.y, rect.width, 1), new Color(.35f, .63f, .73f, .45f));
            Fill(new Rect(rect.x, rect.yMax - 1, rect.width, 1),
                new Color(.22f, .43f, .55f, .4f));
            if (fraction > 0)
            {
                Rect charge = new Rect(rect.x + 1, rect.y + 1,
                    Mathf.Max(0, (rect.width - 2) * fraction), Mathf.Max(1, rect.height - 2));
                Fill(charge, new Color(color.r * .58f, color.g * .68f, color.b * .75f, .96f));
                Fill(new Rect(charge.x, charge.y, charge.width,
                    Mathf.Max(1, charge.height * .42f)), color);
                if (rect.height >= 8)
                    Fill(new Rect(Mathf.Min(charge.xMax - 2, rect.xMax - 3), rect.y - 2,
                        2, rect.height + 4), new Color(color.r, color.g, color.b, .72f));
            }
            if (rect.height >= 8)
                for (int step = 1; step < 10; step++)
                    Fill(new Rect(rect.x + rect.width * step / 10f, rect.y + 2,
                        1, rect.height - 4), new Color(.02f, .07f, .11f, .36f));
            if (!string.IsNullOrEmpty(label))
                GUI.Label(new Rect(rect.x + 5, rect.y - 4, rect.width - 10, 18), label, small);
        }

        /// <summary>Show one ship resource with its full current and maximum values.</summary>
        private void VitalRow(Rect panel, int index, string title, float current,
            float maximum, Color color)
        {
            float top = panel.y + 29 + index * 39;
            GUI.Label(new Rect(panel.x + 14, top, 125, 22), title, vitalText);
            GUI.Label(new Rect(panel.x + 143, top - 2, panel.width - 158, 24),
                maximum > 0 ? $"{Mathf.Max(0, current):N0} / {maximum:N0}" : "SYNCING",
                vitalValue);
            Meter(new Rect(panel.x + 14, top + 25, panel.width - 28, 11), "",
                maximum > 0 ? current / maximum : 0, color);
        }

        /// <returns>The centered equipment overlay bounds.</returns>
        private Rect EquipmentRect()
        {
            float width = Mathf.Min(1160, CanvasWidth - 32);
            float height = Mathf.Min(700, CanvasHeight - 60);
            return new Rect(Mathf.Clamp((CanvasWidth - width) * .5f + inventoryOffset.x,
                    8, CanvasWidth - width - 8),
                Mathf.Clamp((CanvasHeight - height) * .5f + inventoryOffset.y,
                    8, CanvasHeight - height - 8), width, height);
        }

        /// <returns>Rect: Persistent flight control for the inventory overlay.</returns>
        private Rect InventoryButtonRect()
        { return new Rect(CanvasWidth - 910, 25, 154, 28); }

        /// <returns>Rect: The movable mounted-weapon strip kept above the quickbar.</returns>
        private Rect WeaponWidgetRect(Snapshot snapshot)
        {
            float height = 54 + Mathf.Min(6, (int)snapshot.WeaponMounts) * 31;
            float x = Mathf.Max(310, CanvasWidth - 980) + weaponWidgetOffset.x;
            float y = 125 + weaponWidgetOffset.y;
            return new Rect(Mathf.Clamp(x, 8, CanvasWidth - 234),
                Mathf.Clamp(y, 92, CanvasHeight - height - 15), 226, height);
        }

        /// <summary>Show server-published launcher quantities and reload time in flight.</summary>
        private void WeaponWidget(Snapshot snapshot)
        {
            if (snapshot.WeaponMounts == 0) return;
            Rect rect = WeaponWidgetRect(snapshot);
            Event current = Event.current;
            Rect handle = new Rect(rect.x, rect.y, rect.width - 59, 28);
            if (current.type == EventType.MouseDown && current.button == 0 &&
                handle.Contains(current.mousePosition))
            {
                draggingWeaponWidget = true;
                weaponWidgetDragStart = current.mousePosition;
                weaponWidgetOffsetStart = weaponWidgetOffset;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && draggingWeaponWidget)
            {
                weaponWidgetOffset = weaponWidgetOffsetStart +
                    current.mousePosition - weaponWidgetDragStart;
                current.Use();
            }
            else if (current.type == EventType.MouseUp && current.button == 0)
                draggingWeaponWidget = false;
            FlightPanel(rect);
            if (fittingSocket == null) fittingSocket = CreateFittingSocket();
            GUI.Label(new Rect(rect.x + 9, rect.y + 5, rect.width - 95, 20),
                "WEAPONS   ⋮⋮", section);
            if (GUI.Button(new Rect(rect.xMax - 68, rect.y + 3, 61, 24),
                "OPEN", button)) equipmentOpen = true;
            int count = Mathf.Min(6, (int)snapshot.WeaponMounts);
            for (int index = 0; index < count; index++)
            {
                int slot = 3 + index;
                ItemView weapon = slot < snapshot.Mounted.Length ?
                    snapshot.Mounted[slot] : null;
                ItemView ammo = slot < snapshot.Ammo.Length ? snapshot.Ammo[slot] : null;
                float top = rect.y + 31 + index * 31;
                Fill(new Rect(rect.x + 8, top, rect.width - 16, 1),
                    new Color(.28f, .56f, .65f, .23f));
                Color previous = GUI.color;
                GUI.color = weapon == null ? new Color(.4f, .55f, .61f) :
                    new Color(1f, .73f, .42f);
                GUI.DrawTexture(new Rect(rect.x + 8, top + 2, 26, 26),
                    fittingSocket);
                GUI.color = previous;
                GUI.DrawTexture(new Rect(rect.x + 13, top + 7, 16, 16),
                    weapon == null ? icons.ForType(14u) : icons.ForItem(weapon));
                string name = weapon?.Name ?? "EMPTY";
                while (name.Length > 5 && weaponWidgetText.CalcSize(
                    new GUIContent(name)).x > 104)
                    name = name.Substring(0, name.Length - 2).TrimEnd() + "…";
                GUI.Label(new Rect(rect.x + 35, top + 2, 126, 20),
                    "W" + (index + 1) + " " + name, weaponWidgetText);
                string state = weapon == null ? "—" : weapon.Cooling ?
                    weapon.CooldownSeconds > 0 ?
                        weapon.CooldownSeconds.ToString("0.0") + "s" : "COOL" :
                    weapon.Type == 15 || weapon.Type == 16 ?
                        (ammo?.Quantity ?? 0) + "/" + (ammo?.MaxStack ?? 0) :
                    (weapon.WeaponState & 1) != 0 ? "ARMED" : "SAFE";
                GUI.Label(new Rect(rect.xMax - 61, top + 2, 54, 20),
                    state, weaponWidgetText);
                if (weapon != null && (weapon.Type == 15 || weapon.Type == 16))
                {
                    Fill(new Rect(rect.x + 36, top + 25, rect.width - 48, 3),
                        new Color(.16f, .26f, .3f));
                    Fill(new Rect(rect.x + 36, top + 25,
                        (rect.width - 48) * Mathf.Clamp01(
                            (ammo?.Quantity ?? 0) / (float)Mathf.Max(1, ammo?.MaxStack ?? 0)), 3),
                        weapon.OutOfAmmo ? new Color(1f, .48f, .34f) :
                            new Color(.92f, .68f, .33f));
                }
                if (weapon != null && weapon.Cooling && (weapon.Type == 15 || weapon.Type == 16))
                    GUI.Label(new Rect(rect.x + 35, top + 17, 180, 14),
                        "MAG  " + (ammo?.Quantity ?? 0) + "/" + (ammo?.MaxStack ?? 0),
                        equipmentMeta);
            }
            GUI.Label(new Rect(rect.x + 9, rect.yMax - 17, rect.width - 18, 15),
                "DRAG TO MOVE  ·  OPEN INVENTORY", equipmentMeta);
        }

        /// <returns>The centered keyboard settings overlay bounds.</returns>
        private Rect SettingsRect()
        { return new Rect((CanvasWidth - 580) * .5f, 80, 580, Mathf.Min(610, CanvasHeight - 150)); }

        /// <returns>Rect: A clamped, persistent target action popup for a visible ship.</returns>
        private Rect TargetPanelRect(Snapshot snapshot)
        {
            if (snapshot == null || wreckId != 0) return Rect.zero;
            uint id = contactId != 0 ? contactId : snapshot.SelectedTargetId;
            ShipActor target = snapshot.Players.Concat(snapshot.Mobs)
                .FirstOrDefault(actor => actor.Id == id && actor.HasPosition);
            if (target == null) return Rect.zero;
            bool jumpstart = target.IsPlayer && target.MaxHull > 0 && target.Hull <= 0 &&
                snapshot.AvailableAbilities.Contains(65u);
            float width = 338;
            float nameExtra = wrappedBody == null ? 0 : Mathf.Max(0,
                wrappedBody.CalcHeight(new GUIContent(target.Name.ToUpperInvariant()), width - 30) - 25);
            float height = (jumpstart ? 234 : 190) + nameExtra;
            if (!targetPanelLoaded)
            {
                targetPanelPosition = new Vector2(
                    PlayerPrefs.GetFloat("42andBeyond.target.x",
                        (CanvasWidth - width - 24) / CanvasWidth),
                    PlayerPrefs.GetFloat("42andBeyond.target.y",
                        (NavigationListHeight() + 112) / CanvasHeight));
                targetPanelLoaded = true;
            }
            float x = Mathf.Clamp(targetPanelPosition.x * CanvasWidth, 12,
                Mathf.Max(12, CanvasWidth - width - 12));
            float y = Mathf.Clamp(targetPanelPosition.y * CanvasHeight, 70,
                Mathf.Max(70, CanvasHeight - height - 22));
            return new Rect(x, y, width, height);
        }

        /// <summary>Drag only the target header and save its normalized screen position.</summary>
        private void MoveTargetPanel(Rect panel)
        {
            Event current = Event.current;
            Vector2 pointer = new Vector2(Input.mousePosition.x / Scale,
                (Screen.height - Input.mousePosition.y) / Scale);
            if (current.type == EventType.MouseDown && current.button == 0 &&
                new Rect(panel.x, panel.y, panel.width, 30).Contains(pointer))
            {
                draggingTargetPanel = true;
                targetDragOffset = pointer - panel.position;
                current.Use();
            }
            else if (draggingTargetPanel && current.type == EventType.MouseDrag)
            {
                Vector2 moved = pointer - targetDragOffset;
                targetPanelPosition = new Vector2(
                    Mathf.Clamp(moved.x, 12, CanvasWidth - panel.width - 12) / CanvasWidth,
                    Mathf.Clamp(moved.y, 70, CanvasHeight - panel.height - 22) / CanvasHeight);
                current.Use();
            }
            else if (draggingTargetPanel && current.type == EventType.MouseUp)
            {
                draggingTargetPanel = false;
                PlayerPrefs.SetFloat("42andBeyond.target.x", targetPanelPosition.x);
                PlayerPrefs.SetFloat("42andBeyond.target.y", targetPanelPosition.y);
                PlayerPrefs.Save();
                current.Use();
            }
        }

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
            if (deck != null && deck.Visible)
            {
                if (snapshot.Station != null) { Station(snapshot); ChatEntry(); Settings(); }
                else DrawDeckCompanions(snapshot);
                return;
            }
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
            if (GUI.Button(new Rect(CanvasWidth - 160, 25, 136, 28),
                keys.Display(ControlAction.Chart) + (client.Chart ? "  Flight" : "  Chart"), button)) client.SetChart(!client.Chart);
            if (GUI.Button(InventoryButtonRect(), equipmentOpen ? "CLOSE INVENTORY" :
                "INVENTORY  ·  " + keys.Display(ControlAction.Equipment),
                equipmentOpen ? selected : primaryButton)) ToggleEquipment();
            if (GUI.Button(new Rect(CanvasWidth - 300, 25, 130, 28), "Disconnect", button)) { client.Disconnect(); return; }
            if (snapshot == null) return;
            if (Time.unscaledTime >= refreshAt || sector != snapshot.Sector)
            {
                if (sector != snapshot.Sector) { lastDamage = 0; damageLabels.Clear(); }
                destinations = snapshot.Objects.Where(item => item.Kind == 12 || item.Navigable ||
                    item.Kind == 37 && item.Position.Distance(snapshot.Position) <= 20000)
                    .OrderBy(item => item.Position.Distance(snapshot.Position)).ToArray();
                nearbyWrecks = snapshot.Objects.Where(item => item.Kind == 25)
                    .OrderBy(item => item.Position.Distance(snapshot.Position)).Take(12).ToArray();
                socialContacts = snapshot.Players.Concat(snapshot.Mobs).Where(actor => actor.HasPosition)
                    .OrderBy(actor => actor.Position.Distance(snapshot.Position)).Take(40).ToArray();
                refreshAt = Time.unscaledTime + .25f; sector = snapshot.Sector;
            }
            TrackDamage(snapshot);
            if (client.Chart) DrawTacticalMap(snapshot);
            if (Event.current.type == EventType.Repaint)
            {
                occupiedFlightLabels.Clear();
                worldLabelHits.Clear();
                occupiedFlightLabels.Add(ShipVitalsRect(snapshot));
                occupiedFlightLabels.Add(new Rect((CanvasWidth - 720) * .5f,
                    CanvasHeight - 117, 720, 68));
                if (!client.Chart)
                { WorldLabels(snapshot); PlayerLabels(snapshot); WreckLabels(snapshot); DamageLabels(snapshot); }
            }
            Navigation(snapshot);
            Radar(snapshot);
            Social(snapshot);
            Communications(snapshot);
            if (!client.AbilitiesOpen && !client.Incapacitated)
            { if (!client.Chart) TargetLock(snapshot); WreckPanel(snapshot); }
            if (!equipmentOpen && !client.Chart) WeaponWidget(snapshot);
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
            if (snapshot.Dialogue != null) StationDialogue(snapshot);
            Settings();
        }

        /// <summary>Capture and persist keyboard controls without passing keys to flight input.</summary>
        private void Settings()
        {
            if (equipmentOpen && !settingsOpen) return;
            Rect controls = client.CurrentSnapshot?.Station != null ?
                new Rect(CanvasWidth - 450, 37, 110, 34) : new Rect(CanvasWidth - 430, 25, 110, 28);
            if (GUI.Button(controls, "Controls", button))
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
            GUILayout.BeginArea(new Rect(panel.x + 20, panel.y + 56, panel.width - 40, panel.height - 145));
            settingsScroll = GUILayout.BeginScrollView(settingsScroll);
            GUILayout.Label("INTERFACE SIZE   " + Mathf.RoundToInt(interfaceSize * 100) + "%", body);
            float adjustedSize = GUILayout.HorizontalSlider(interfaceSize, .85f, 1.3f,
                GUILayout.Width(500), GUILayout.Height(24));
            if (Mathf.Abs(adjustedSize - interfaceSize) > .001f)
            {
                interfaceSize = adjustedSize;
                PlayerPrefs.SetFloat("42andbeyond.interface-size", interfaceSize);
            }
            if (Event.current.type == EventType.MouseUp) PlayerPrefs.Save();
            GUILayout.Space(8);
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
            GUI.Label(new Rect(panel.x + 20, panel.yMax - 83, panel.width - 40, 20),
                bindingAction.HasValue ? "Esc cancels · choosing an occupied key swaps its action" :
                "Click a key to change it · occupied keys swap actions", small);
            if (GUI.Button(new Rect(panel.x + 20, panel.yMax - 57, 160, 32),
                "OPEN NEW CLIENT", button)) client.OpenAnotherClient();
            if (GUI.Button(new Rect(panel.xMax - 150, panel.yMax - 57, 130, 32), "Restore defaults", button))
            { client.Bindings.Reset(); bindingAction = null; }
        }

        /// <summary>Present one concourse directory and the selected station service.</summary>
        private void Station(Snapshot snapshot)
        {
            EnsureNpcNames();
            if (stationConcourse == null) stationConcourse = new SingleRoomStationHud(client, this);
            stationConcourse.Draw(snapshot, npcNames);
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
            EnsureNpcNames();
            DialogueState dialogue = snapshot.Dialogue;
            if (stationDialogueNpc != dialogue.NpcId)
            { stationDialogueNpc = dialogue.NpcId; stationDialogueCollapsed = false; }
            bool trading = dialogue.VendorOpen;
            float width = Mathf.Min(trading ? 700 : 660, CanvasWidth - 630), x = (CanvasWidth - width) * .5f;
            float height = trading ? CanvasHeight - 140 :
                snapshot.Station != null && dialogue.MissionKind != MissionDialogueKind.None ?
                Mathf.Min(430, CanvasHeight - 155) : 300;
            float top = trading ? 116 : snapshot.Station == null ?
                Mathf.Max(90, (CanvasHeight - height) * .5f - 40) : CanvasHeight - height - 24;
            if (snapshot.Station != null)
            {
                Rect workspace = SingleRoomStationHud.Workspace(CanvasWidth, CanvasHeight);
                x = workspace.x; top = workspace.y; width = workspace.width; height = workspace.height;
            }
            bool collapsed = snapshot.Station != null && stationDialogueCollapsed;
            Panel(new Rect(x, top, width, collapsed ? 54 : height));
            string title = npcNames.TryGetValue(dialogue.NpcId, out string name) ? name :
                snapshot.Station == null ? snapshot.Objects.FirstOrDefault(item =>
                    item.Id == snapshot.SelectedTargetId)?.Name ?? "Space contact" : "Station contact";
            Fill(new Rect(x + 1, top + 2, 4, 48), cyan);
            GUI.Label(new Rect(x + 19, top + 12, width - 130, 27), title.ToUpperInvariant(), body);
            GUI.Label(new Rect(x + 20, top + 38, width - 130, 19),
                trading ? "STATION CONTACT   /   TRADE" : snapshot.Station == null ?
                "SPACE CONTACT   /   CONVERSATION" : "STATION CONTACT   /   CONVERSATION", dialogueMeta);
            if (GUI.Button(new Rect(x + width - 104, top + 15, 86, 29), "CLOSE", button)) client.ChooseNpc(230);
            if (snapshot.Station != null && GUI.Button(new Rect(x + width - 148, top + 15, 34, 29),
                collapsed ? "+" : "−", button)) stationDialogueCollapsed = !stationDialogueCollapsed;
            if (collapsed || snapshot.Station != null && stationDialogueCollapsed) return;
            Fill(new Rect(x + 18, top + 61, width - 36, 1), new Color(.24f, .55f, .68f, .45f));
            GUILayout.BeginArea(new Rect(x + 18, top + 70, width - 36, trading ? 108 : height - 84));
            dialogueScroll = GUILayout.BeginScrollView(dialogueScroll);
            if (dialogue.MissionKind != MissionDialogueKind.None)
            {
                string phase = dialogue.MissionKind == MissionDialogueKind.Offer ? "MISSION OFFER" :
                    dialogue.MissionKind == MissionDialogueKind.Objective ? "ACTIVE MISSION" :
                    dialogue.MissionKind == MissionDialogueKind.Completed ? "MISSION COMPLETED" :
                    dialogue.MissionStage == 1 ? "MISSION ACCEPTED" : "MISSION UPDATED";
                GUILayout.Label(phase, section);
                GUILayout.Label(dialogue.MissionName, dialogueText);
                if (dialogue.MissionKind == MissionDialogueKind.Completed)
                    GUILayout.Label($"CURRENT BALANCE  ·  {snapshot.Credits:N0} CREDITS",
                        dialogueMeta);
                MissionState mission = snapshot.Missions.FirstOrDefault(entry => entry?.Name == dialogue.MissionName);
                if (mission != null && !string.IsNullOrEmpty(mission.Objective))
                {
                    GUILayout.Label("CURRENT OBJECTIVE", dialogueMeta);
                    GUILayout.Label(mission.Objective, dialogueMeta);
                    GUILayout.Space(8);
                }
            }
            string line = dialogue.Text;
            if (line.StartsWith("/", StringComparison.Ordinal))
            {
                int marker = line.IndexOf(' ');
                if (marker > 0 && marker < 20) line = line.Substring(marker + 1);
            }
            if (line.Length == 0) line = "Waiting for reply…";
            float transcriptHeight = Mathf.Max(45,
                dialogueText.CalcHeight(new GUIContent(line), width - 82) + 15);
            Rect transcript = GUILayoutUtility.GetRect(0, transcriptHeight, GUILayout.ExpandWidth(true));
            Fill(transcript, new Color(.075f, .145f, .19f, .6f));
            Fill(new Rect(transcript.x, transcript.y, 2, transcript.height),
                new Color(.43f, .78f, .91f, .8f));
            GUI.Label(new Rect(transcript.x + 11, transcript.y + 5,
                transcript.width - 22, transcript.height - 10), line, dialogueText);
            GUILayout.Space(9);
            if (dialogue.Choices.Length > 0) GUILayout.Label("YOUR RESPONSE", dialogueMeta);
            foreach (DialogueChoice choice in dialogue.Choices)
            {
                string caption = "›   " + choice.Text;
                float choiceHeight = Mathf.Max(36,
                    dialogueChoice.CalcHeight(new GUIContent(caption), width - 73) + 5);
                if (GUILayout.Button(caption, dialogueChoice, GUILayout.Height(choiceHeight)))
                    client.ChooseNpc(choice.Node);
            }
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
            float height = Mathf.Clamp(158 + characters.Length * 100, 262,
                Mathf.Min(610, CanvasHeight - 90));
            var rect = new Rect((CanvasWidth - 520) * .5f,
                (CanvasHeight - height) * .5f, 520, height);
            Panel(rect);
            GUI.Label(new Rect(rect.x + 22, rect.y + 16, 470, 34), "CHOOSE YOUR PILOT", heading);
            GUI.Label(new Rect(rect.x + 23, rect.y + 53, 470, 22),
                "Select a character to enter the connected server", small);
            Rect viewport = new Rect(rect.x + 21, rect.y + 84, rect.width - 42,
                rect.height - 142);
            characterScroll = GUI.BeginScrollView(viewport, characterScroll,
                new Rect(0, 0, viewport.width - 18, characters.Length * 100));
            for (int i = 0; i < characters.Length; i++)
            {
                CharacterRecord character = characters[i];
                Rect card = new Rect(0, i * 100, viewport.width - 19, 94);
                if (GUI.Button(card, "", button))
                    client.ChooseCharacter(character.Slot);
                Fill(new Rect(card.x + 1, card.y + 2, 3, card.height - 4), cyan);
                GUI.Label(new Rect(card.x + 14, card.y + 9, 218, 25),
                    character.Name, body);
                GUI.Label(new Rect(card.x + 14, card.y + 35, 218, 22),
                    character.ClassName.ToUpperInvariant(), section);
                Fill(new Rect(card.x + 236, card.y + 8, 1, 49),
                    new Color(.32f, .62f, .74f, .32f));
                string[] tracks = { "COMBAT", "TRADE", "EXPLORE" };
                uint[] levels = { character.CombatLevel, character.TradeLevel,
                    character.ExploreLevel };
                for (int track = 0; track < tracks.Length; track++)
                {
                    float column = card.x + 249 + track * 66;
                    GUI.Label(new Rect(column, card.y + 9, 65, 18), tracks[track], small);
                    GUI.Label(new Rect(column, card.y + 29, 65, 27),
                        levels[track].ToString(), body);
                }
                Fill(new Rect(card.x + 13, card.y + 64, card.width - 26, 1),
                    new Color(.31f, .58f, .68f, .3f));
                GUI.Label(new Rect(card.x + 14, card.y + 70, card.width - 28, 20),
                    "START AT   " + character.SectorName, small);
            }
            GUI.EndScrollView();
            if (GUI.Button(new Rect(rect.x + 22, rect.yMax - 47, 150, 31), "Back to login", button))
                client.ReturnToLogin();
            GUI.Label(new Rect(rect.x + 207, rect.yMax - 42, 287, 24),
                characters.Length + " PILOT" + (characters.Length == 1 ? "" : "S") + " AVAILABLE", small);
        }

        /// <summary>Show a compact command line for server chat and /d_ commands.</summary>
        private void ChatEntry()
        {
            if (!client.ChatOpen || RetainedVisible) return;
            groupChat = GUI.Toggle(new Rect(CanvasWidth * .5f - 260, CanvasHeight - 118, 180, 24), groupChat, "Group chat (else sector)");
            float x = CanvasWidth * .5f - 260, y = CanvasHeight - 94;
            Panel(new Rect(x, y, 520, 52));
            GUI.SetNextControlName("42 and Beyond chat command");
            chatText = GUI.TextField(new Rect(x + 10, y + 10, 415, 32), chatText, 180, input);
            if (Event.current.type == EventType.KeyDown &&
                (Event.current.keyCode == KeyCode.UpArrow || Event.current.keyCode == KeyCode.DownArrow))
            {
                chatText = Event.current.keyCode == KeyCode.UpArrow ? ChatHistory.Previous(chatText) : ChatHistory.Next(chatText);
                Event.current.Use();
            }
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
            var rect = new Rect((CanvasWidth - 500) * .5f,
                (CanvasHeight - 660) * .5f, 500, 660);
            Panel(rect);
            if (frameTexture != null)
            {
                Color previous = GUI.color;
                GUI.color = new Color(.65f, .85f, 1f, .5f);
                GUI.DrawTexture(rect, frameTexture, ScaleMode.StretchToFill);
                GUI.color = previous;
            }
            float x = rect.x + 24;
            GUI.Label(new Rect(x, rect.y + 19, 452, 36), "42 AND BEYOND", heading);
            GUI.Label(new Rect(x, rect.y + 55, 452, 22),
                "CAPTAIN'S TERMINAL  /  LOCAL UNIVERSE", small);
            GUI.Label(new Rect(x, rect.y + 91, 452, 18), "Server IPv4", small);
            host = GUI.TextField(new Rect(x, rect.y + 113, 324, 34), host, input);
            if (GUI.Button(new Rect(x + 332, rect.y + 113, 120, 34), "DISCOVER", button))
                client.DiscoverDemoAccounts(host);
            GUI.Label(new Rect(x, rect.y + 168, 452, 22), "PUBLISHED SANDBOX PILOTS", section);
            GUI.Label(new Rect(x, rect.y + 192, 452, 20),
                "Choose a class to fill its public demo login.", small);
            DemoAccount[] demos = host == client.DemoRosterHost ? client.DemoAccounts : null;
            Rect roster = new Rect(x, rect.y + 219, 452, 208);
            int rows = demos == null ? 0 : (demos.Length + 2) / 3;
            float contentHeight = Mathf.Max(roster.height, rows * 46);
            demoScroll = GUI.BeginScrollView(roster, demoScroll,
                new Rect(0, 0, roster.width - 18, contentHeight));
            if (demos == null)
                GUI.Label(new Rect(5, 5, roster.width - 30, 60), host == client.DemoRosterHost ?
                    client.DemoRosterError ?? "Reading the server's demo roster…" :
                    "Select Discover after changing the server address.", wrappedBody);
            else if (demos.Length == 0)
                GUI.Label(new Rect(5, 5, roster.width - 30, 48),
                    "This server advertises no demo pilots.", small);
            else
            {
                float buttonWidth = (roster.width - 30) / 3;
                for (int index = 0; index < demos.Length; index++)
                {
                    DemoAccount demo = demos[index];
                    Rect choice = new Rect(index % 3 * (buttonWidth + 6), index / 3 * 46,
                        buttonWidth, 40);
                    if (GUI.Button(choice, new GUIContent(demo.ClassName, demo.Username),
                        demo.Username == username ? selected : button))
                    { username = demo.Username; password = demo.Password; }
                }
            }
            GUI.EndScrollView();
            Fill(new Rect(x, rect.y + 438, 452, 1), new Color(.3f, .7f, .84f, .4f));
            GUI.Label(new Rect(x, rect.y + 448, 452, 24), "ACCOUNT LOGIN", section);
            GUI.Label(new Rect(x, rect.y + 477, 452, 18), "Account", small);
            username = GUI.TextField(new Rect(x, rect.y + 497, 452, 34), username, input);
            GUI.Label(new Rect(x, rect.y + 538, 452, 18), "Password", small);
            password = GUI.PasswordField(new Rect(x, rect.y + 558, 452, 34), password, '*', input);
            if (GUI.Button(new Rect(x, rect.y + 600, 452, 38), "ENTER SPACE", primaryButton))
                client.StartLocalSession(host, username, password);
            string status = client.Status ?? "";
            GUI.Label(new Rect(x, rect.y + 643, 452, 18),
                new GUIContent(FitCommsPreview(status, 452), status), commsPreview);
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
            if (actorTarget != null && target == null) return;
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
            Rect panel = TargetPanelRect(snapshot);
            MoveTargetPanel(panel);
            panel = TargetPanelRect(snapshot);
            float x = panel.x, y = panel.y;
            string targetName = target.Name.ToUpperInvariant();
            float nameHeight = Mathf.Max(25, wrappedBody.CalcHeight(new GUIContent(targetName), 308));
            float nameExtra = nameHeight - 25;
            Panel(panel);
            Fill(new Rect(x + 1, y + 2, panel.width - 2, panel.height - 4),
                new Color(.005f, .014f, .027f, .33f));
            Color signal = target.IsPlayer ? cyan : target.Hostile ? new Color(1f, .42f, .32f) : gold;
            Fill(new Rect(x + 1, y + 1, 4, panel.height - 2), signal);
            Fill(new Rect(x + 12, y + 29, panel.width - 24, 1), new Color(signal.r, signal.g, signal.b, .3f));
            GUI.Label(new Rect(x + 14, y + 6, 270, 20),
                target.IsPlayer ? "PILOT CONTACT" : target.Hostile ? "HOSTILE CONTACT" : "NEUTRAL CONTACT", section);
            GUI.Label(new Rect(x + panel.width - 60, y + 8, 48, 17), "DRAG", small);
            GUI.Label(new Rect(x + 14, y + 36, 308, nameHeight),
                new GUIContent(targetName, target.Name), wrappedBody);
            float distance = target.Position.Distance(snapshot.Position);
            bool firingAtTarget = snapshot.WeaponsFiring && snapshot.SelectedTargetId == target.Id;
            CombatEngagement engagement = snapshot.Engagement?.TargetId == target.Id ? snapshot.Engagement : null;
            bool recentShot = engagement?.LastShot != null && engagement.LastShotAgeSeconds <= 2f;
            GUI.Label(new Rect(x + 14, y + 64 + nameExtra, 310, 20),
                $"RANGE {distance / 1000:0.00} km   ·   " +
                (!firingAtTarget ? "WEAPONS SAFE" : recentShot ? "SHOT CONFIRMED" : "WEAPONS ARMED"), small);
            GUI.Label(new Rect(x + 14, y + 89 + nameExtra, 145, 18), target.Shield >= 0 ?
                $"SHIELD  {Mathf.Clamp01(target.Shield) * 100:0}%" : "SHIELD  NO DATA", small);
            GUI.Label(new Rect(x + 178, y + 89 + nameExtra, 145, 18), target.MaxHull > 0 ?
                $"HULL  {Mathf.Max(0, target.Hull):0}/{target.MaxHull:0}" : "HULL  NO DATA", small);
            if (target.Shield >= 0) Meter(new Rect(x + 14, y + 108 + nameExtra, 145, 6), "", target.Shield, cyan);
            if (target.MaxHull > 0) Meter(new Rect(x + 178, y + 108 + nameExtra, 145, 6), "", target.Hull / target.MaxHull, gold);
            if (!target.IsPlayer)
            {
                bool hacked = snapshot.ObjectEffects.Any(effect =>
                    effect.OwnerId == target.Id && effect.Descriptor == 193);
                GUI.Label(new Rect(x + 14, y + 121 + nameExtra, 310, 21),
                    hacked ? "SYSTEMS HACKED  ·  SERVER CONFIRMED" :
                    Time.unscaledTime < serverSelectedUntil ? "TARGET UPDATED BY SERVER" :
                    CombatGuidance(engagement, firingAtTarget), small);
                GUI.enabled = client.Ready && !client.Incapacitated;
                if (GUI.Button(new Rect(x + 14, y + 143 + nameExtra, 194, 36),
                    firingAtTarget ? "STOP WEAPONS" : "FIRE ON TARGET", firingAtTarget ? hostileButton : primaryButton))
                { if (firingAtTarget) client.StopWeapons(); else client.ToggleWeapons(target.Id); }
                GUI.enabled = true;
                if (GUI.Button(new Rect(x + 216, y + 143 + nameExtra, 108, 36), "FOLLOW", button))
                    client.Follow(target.Id);
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
            if (GUI.Button(new Rect(x + 14, y + 143 + nameExtra, 194, 36),
                member ? "IN YOUR GROUP" : !leader ? "LEADER INVITES" : full ? "GROUP FULL" :
                pending ? "WAITING FOR SERVER" : sentRecently ? "INVITATION SENT" : "INVITE TO GROUP", button))
            { client.InvitePlayer(target.Id); invitePendingId = target.Id; invitePendingAt = Time.unscaledTime; }
            GUI.enabled = true;
            if (GUI.Button(new Rect(x + 216, y + 143 + nameExtra, 108, 36), "FOLLOW", button))
                client.Follow(target.Id);
            if (canJumpstart)
            {
                GUI.enabled = client.Ready && !client.Incapacitated &&
                    snapshot.CastingAbilityId == uint.MaxValue && snapshot.SelectedTargetId == target.Id;
                if (GUI.Button(new Rect(x + 14, y + 186 + nameExtra, 310, 34),
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

        /// <summary>Project limited markers without enlarging physical models.</summary>
        private void WorldLabels(Snapshot snapshot)
        {
            List<Rect> occupied = occupiedFlightLabels;
            Rect targetPanel = TargetPanelRect(snapshot);
            if (targetPanel.width > 0) occupied.Add(targetPanel);
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
                if (shown >= 5) break;
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
                const float labelWidth = 218;
                float nameHeight = Mathf.Max(21,
                    contactName.CalcHeight(new GUIContent(item.Name), labelWidth - 18));
                float labelHeight = nameHeight + 25;
                foreach (Vector2 offset in LabelOffsets)
                {
                    var candidate = new Rect(offset.x < 0 ? p.x - labelWidth - 12 : p.x + 12,
                        offset.y < 0 ? p.y - labelHeight - 10 : p.y + 12,
                        labelWidth, labelHeight);
                    if (candidate.xMin < 24 || candidate.xMax > CanvasWidth - 332 ||
                        candidate.yMin < 95 || candidate.yMax > CanvasHeight - 122 ||
                        occupied.Any(other => other.Overlaps(candidate))) continue;
                    chosen = candidate; placed = true; break;
                }
                if (!placed) continue;
                shown++; occupied.Add(chosen);
                worldLabelHits.Add((chosen, item.Id, 4));
                Fill(chosen, new Color(.004f, .018f, .035f, active ? .46f : .28f));
                Fill(new Rect(chosen.x, chosen.y, 2, chosen.height), color);
                Fill(new Rect(chosen.x + 2, chosen.y, chosen.width - 2, 1), new Color(color.r, color.g, color.b, .35f));
                GUI.Label(new Rect(chosen.x + 9, chosen.y + 2, labelWidth - 18, nameHeight),
                    item.Name, contactName);
                GUI.Label(new Rect(chosen.x + 9, chosen.y + nameHeight + 2,
                    labelWidth - 18, 20), $"{distance / 1000:0.0} km", small);
            }
        }

        /// <summary>Identify remote ships with clear team colors and backed labels.</summary>
        private void PlayerLabels(Snapshot snapshot)
        {
            int shown = 0;
            List<Rect> occupied = occupiedFlightLabels;
            Rect targetPanel = TargetPanelRect(snapshot);
            if (targetPanel.width > 0) occupied.Add(targetPanel);
            foreach (ShipActor player in snapshot.Players.Concat(snapshot.Mobs)
                .OrderByDescending(item => item.Id == contactId || item.Id == snapshot.SelectedTargetId))
            {
                Vector3 point = client.ProjectPlayer(player.Id); point.x /= Scale; point.y /= Scale;
                if (point.z <= 0 || point.x < 30 || point.x > CanvasWidth - 335 || point.y < 100 || point.y > CanvasHeight - 145) continue;
                if (++shown > 12) break;
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
                const float labelWidth = 260;
                float nameHeight = Mathf.Max(21,
                    contactName.CalcHeight(new GUIContent(player.Name), labelWidth - 18));
                float labelHeight = nameHeight + (active ? 43 : 26);
                foreach (Vector2 offset in ContactLabelOffsets)
                {
                    Rect candidate = new Rect(offset.x < 0 ? point.x - labelWidth - 14 : point.x + 14,
                        offset.y < 0 ? point.y - labelHeight - 10 : point.y + 12,
                        labelWidth, labelHeight);
                    if (candidate.x < 292 || candidate.xMax > CanvasWidth - 315 ||
                        candidate.y < 95 || candidate.yMax > CanvasHeight - 108 ||
                        occupied.Any(area => area.Overlaps(candidate))) continue;
                    label = candidate;
                    break;
                }
                if (label.width == 0) continue;
                occupied.Add(label);
                worldLabelHits.Add((label, player.Id, 1));
                float labelX = label.x, labelY = label.y;
                Fill(label, new Color(.004f, .012f, .024f, active ? .50f : .30f));
                Fill(new Rect(labelX, labelY, 2, labelHeight), color);
                Fill(new Rect(labelX + 2, labelY, labelWidth - 2, 1), new Color(color.r, color.g, color.b, .38f));
                GUI.Label(new Rect(labelX + 9, labelY + 2, labelWidth - 18, nameHeight),
                    player.Name, contactName);
                GUI.Label(new Rect(labelX + 9, labelY + nameHeight + 2, labelWidth - 18, 20),
                    (crewMember ? "CREW" : player.IsPlayer ? "PLAYER" : player.Hostile ? "HOSTILE" : "MOB") +
                    "   ·   " + (player.Position.Distance(snapshot.Position) / 1000).ToString("0.00") + " km", small);
                if (active)
                {
                    if (player.Shield >= 0) Meter(new Rect(labelX + 9, labelY + nameHeight + 26, 81, 5), "", player.Shield, cyan);
                    if (player.MaxHull > 0) Meter(new Rect(labelX + 98, labelY + nameHeight + 26, 81, 5), "", player.Hull / player.MaxHull, gold);
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
                const float labelWidth = 220;
                float nameHeight = Mathf.Max(21,
                    contactName.CalcHeight(new GUIContent(item.Name), labelWidth - 18));
                Rect label = new Rect(point.x + 11, point.y - 9, labelWidth, nameHeight + 26);
                if (label.xMax > CanvasWidth - 315 ||
                    label.yMax > CanvasHeight - 110 || !ReserveFlightLabel(label)) continue;
                worldLabelHits.Add((label, item.Id, 2));
                Fill(label, new Color(.004f, .012f, .024f, active ? .46f : .28f));
                Fill(new Rect(label.x, label.y, 2, label.height), gold);
                Fill(new Rect(label.x + 2, label.y, label.width - 2, 1), new Color(gold.r, gold.g, gold.b, .35f));
                GUI.Label(new Rect(label.x + 9, label.y + 2, labelWidth - 18, nameHeight),
                    item.Name, contactName);
                GUI.Label(new Rect(label.x + 9, label.y + nameHeight + 3, labelWidth - 18, 20),
                    "WRECK  ·  " + (item.Position.Distance(snapshot.Position) / 1000).ToString("0.0") + " km", small);
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
            int buffCount = ActiveEffectCount(snapshot);
            Rect panel = ShipVitalsRect(snapshot);
            FlightPanel(panel);
            GUI.Label(new Rect(panel.x + 13, panel.y + 7, 300, 20), "SHIP SYSTEMS", section);
            if (buffCount > 0)
                GUI.Label(new Rect(panel.x + 257, panel.y + 7, 150, 20),
                    buffCount > 5 ? $"{buffCount} EFFECTS  ·  SCROLL ↓" :
                    $"{buffCount} ACTIVE EFFECTS", small);
            float energy = Mathf.Clamp01(snapshot.Energy);
            float shield = Mathf.Clamp01(snapshot.Shield);
            VitalRow(panel, 0, "REACTOR", energy * snapshot.MaxEnergy,
                snapshot.Energy >= 0 ? snapshot.MaxEnergy : 0, cyan);
            VitalRow(panel, 1, "SHIELDS", shield * snapshot.MaxShield,
                snapshot.Shield >= 0 ? snapshot.MaxShield : 0,
                shield < .25f ? new Color(1f, .49f, .31f) :
                    new Color(.37f, .9f, 1f));
            VitalRow(panel, 2, "HULL", snapshot.Hull, snapshot.MaxHull,
                snapshot.MaxHull > 0 && snapshot.Hull / snapshot.MaxHull < .25f ?
                    new Color(1f, .41f, .29f) : gold);
            if (buffCount == 0) return;
            Rect viewport = new Rect(panel.x + 10, panel.y + 157, panel.width - 20,
                Mathf.Min(buffCount, 5) * 30);
            buffScroll = GUI.BeginScrollView(viewport, buffScroll,
                new Rect(0, 0, viewport.width - (buffCount > 5 ? 16 : 0), buffCount * 30),
                false, buffCount > 5);
            int buffIndex = 0;
            foreach (ActiveDeviceView device in snapshot.ActiveDeviceEffects)
                DrawBuffRow(new Rect(0, buffIndex++ * 30, viewport.width - 16, 25),
                    device.Name, device.RemainingSeconds, gold);
            foreach (DeviceBuffView buff in snapshot.ActiveBuffs)
            {
                if (snapshot.HasActiveDeviceSnapshot && buff.Slot == 0) continue;
                DrawBuffRow(new Rect(0, buffIndex++ * 30, viewport.width - 16, 25),
                    buff.Slot == 13 && buff.Name == "Maximize_Shields" ?
                        "Shield Charging" : buff.Name,
                    buff.RemainingSeconds, BuffColor(buff));
            }
            if (LegacyDeviceFallback(snapshot))
                DrawBuffRow(new Rect(0, buffIndex * 30, viewport.width - 16, 25),
                    snapshot.ActiveDeviceLabel, snapshot.ActiveDeviceRemainingSeconds, gold);
            GUI.EndScrollView();
        }

        /// Returns:
        ///     Rect: Current flight vitals bounds, including visible effect rows.
        private Rect ShipVitalsRect(Snapshot snapshot)
        {
            int buffCount = ActiveEffectCount(snapshot);
            float height = 164 + Mathf.Min(buffCount, 5) * 30;
            bool miningPanel = snapshot.ResourceWindowOpen || snapshot.MiningActivity != null;
            float x = miningPanel ? 24 : 365;
            return new Rect(x, CanvasHeight - (miningPanel ? 241 : 125) - height, 420, height);
        }

        /// <returns>int: Number of visible authoritative and fallback effect rows.</returns>
        internal static int ActiveEffectCount(Snapshot snapshot)
        {
            int native = snapshot.ActiveBuffs.Count;
            if (snapshot.HasActiveDeviceSnapshot && snapshot.DeviceBuff != null) native--;
            return native + snapshot.ActiveDeviceEffects.Count +
                (LegacyDeviceFallback(snapshot) ? 1 : 0);
        }

        /// <returns>bool: Whether legacy device prose is the only current device status.</returns>
        private static bool LegacyDeviceFallback(Snapshot snapshot)
        { return !snapshot.HasActiveDeviceSnapshot && snapshot.DeviceBuff == null &&
            snapshot.ActiveDeviceRemainingSeconds > 0 &&
            !string.IsNullOrEmpty(snapshot.ActiveDeviceLabel); }

        /// <returns>Color: Accent for one server-owned status slot.</returns>
        private Color BuffColor(DeviceBuffView buff)
        {
            if (buff.Slot == 5) return RallyColor(buff.Name);
            if (buff.Slot == 1 || buff.Slot == 13) return cyan;
            if (buff.Slot == 2 || buff.Slot == 7 || buff.Slot == 14)
                return new Color(.47f, .8f, 1f);
            if (buff.Slot == 3 || buff.Slot == 10) return new Color(.48f, 1f, .72f);
            if (buff.Slot == 4 || buff.Slot == 9 || buff.Slot == 15)
                return new Color(.72f, .67f, 1f);
            return gold;
        }

        /// <returns>Color: The accent shared by a Rally status and its ship field.</returns>
        private static Color RallyColor(string name)
        {
            if (name == "Damage_Tactics") return new Color(1f, .44f, .2f);
            if (name == "Defense_Tactics") return new Color(.35f, .82f, 1f);
            if (name == "Firing_Tactics") return new Color(1f, .78f, .28f);
            return new Color(.76f, .56f, 1f);
        }

        /// <returns>string: A short buff label that keeps the complete native name in its tooltip.</returns>
        private static string BuffCaption(string name)
        {
            return name.Replace('_', ' ').ToUpperInvariant();
        }

        /// Returns:
        ///     int: Blender atlas tile for a named effect, or minus one for an unknown effect.
        private static int BuffIconIndex(string name)
        {
            string normalized = name.Replace('_', ' ');
            return BuffIconIndices.TryGetValue(normalized, out int index) ? index : -1;
        }

        /// <summary>Show one authoritative timed buff with room for its complete name.</summary>
        private void DrawBuffRow(Rect row, string name, float remaining, Color accent)
        {
            Fill(row, new Color(.06f, .15f, .2f, .8f));
            int icon = BuffIconIndex(name);
            if (buffIcons != null && icon >= 0)
            {
                int col = icon % 6, sheetRow = icon / 6;
                GUI.DrawTextureWithTexCoords(new Rect(row.x + 2, row.y, 25, 25), buffIcons,
                    new Rect(col / 6f, (5 - sheetRow) / 6f, 1f / 6f, 1f / 6f));
            }
            else
            {
                Fill(new Rect(row.x + 5, row.y + 5, 15, 15), accent);
                Fill(new Rect(row.x + 9, row.y + 8, 7, 9), new Color(.07f, .14f, .19f));
            }
            GUI.Label(new Rect(row.x + 34, row.y + 3, row.width - 108, 20),
                new GUIContent(BuffCaption(name), name), buffText);
            GUI.Label(new Rect(row.x + row.width - 64, row.y + 3, 57, 20),
                float.IsPositiveInfinity(remaining) ? "ON" :
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
            if (snapshot.Confirmation != null && snapshot.Confirmation.Kind == ConfirmationKind.Other)
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

        /// <summary>Present an incoming group invitation above every flight or station panel.</summary>
        public void DrawGroupInvitePopup(Snapshot snapshot)
        {
            if (snapshot?.Confirmation?.Kind != ConfirmationKind.Group) return;
            EnsureStyles();
            Fill(new Rect(0, 0, CanvasWidth, CanvasHeight), new Color(.005f, .012f, .025f, .77f));
            Rect card = new Rect((CanvasWidth - 520) * .5f, (CanvasHeight - 252) * .5f, 520, 252);
            Panel(card);
            Fill(new Rect(card.x + 22, card.y + 25, 4, 78), crewColor);
            GUI.Label(new Rect(card.x + 42, card.y + 22, 440, 30), "CREW INVITATION", heading);
            GUI.Label(new Rect(card.x + 42, card.y + 55, 440, 22),
                "A PILOT HAS INVITED YOU TO JOIN THEIR GROUP", section);
            GUI.Label(new Rect(card.x + 42, card.y + 96, 438, 62),
                snapshot.Confirmation.Text, wrappedBody);
            GUI.Label(new Rect(card.x + 42, card.y + 167, 438, 18),
                "ACCEPT TO JOIN  ·  ESC TO DECLINE", small);
            if (GUI.Button(new Rect(card.x + 42, card.y + 198, 202, 38), "Decline", button))
                client.AnswerConfirmation(false);
            if (GUI.Button(new Rect(card.x + 258, card.y + 198, 222, 38), "Accept invitation", primaryButton))
                client.AnswerConfirmation(true);
        }

        /// <summary>Consume invite input before controls behind the popup receive it.</summary>
        public void HandleGroupInviteInput(Event currentEvent)
        {
            if (!GroupInviteOpen || currentEvent == null) return;
            if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0)
            {
                Rect card = new Rect((CanvasWidth - 520) * .5f, (CanvasHeight - 252) * .5f, 520, 252);
                Vector2 point = currentEvent.mousePosition;
                if (new Rect(card.x + 42, card.y + 198, 202, 38).Contains(point))
                    client.AnswerConfirmation(false);
                else if (new Rect(card.x + 258, card.y + 198, 222, 38).Contains(point))
                    client.AnswerConfirmation(true);
            }
            if (currentEvent.isMouse || currentEvent.isKey)
                currentEvent.Use();
        }

        /// <summary>Draw cargo, hardpoints and a large item inspection bay.</summary>
        private void Equipment(Snapshot snapshot)
        {
            Rect rect = EquipmentRect();
            HandleInventoryWindowDrag(rect);
            Fill(rect, new Color(.002f, .008f, .016f, .98f));
            Panel(rect);
            float left = rect.x + 20, top = rect.y + 15;
            GUI.Label(new Rect(left, top, 570, 32), "INVENTORY  /  SHIP LOADOUT", heading);
            GUI.Label(new Rect(rect.xMax - 284, top + 4, 126, 19), "↔  DRAG WINDOW", small);
            if (GUI.Button(new Rect(rect.xMax - 48, top, 28, 28), "×", button)) equipmentOpen = false;
            if (GUI.Button(new Rect(rect.xMax - 143, top, 80, 28), "RESET", button))
                inventoryOffset = mountedOffset = cargoOffset = ammoOffset = Vector2.zero;
            GUI.Label(new Rect(left, top + 35, 800, 19),
                "HULL STAGE " + (snapshot.Experience == null ? "SYNCING" :
                    snapshot.Experience.HullStage + " / 6") + "   ·   CARGO CAPACITY " +
                    (snapshot.CargoCapacity == 0 ? "SYNCING" : snapshot.CargoCapacity + " SLOTS"), small);
            GUI.Label(new Rect(left, top + 54, 800, 19),
                $"{snapshot.WeaponMounts} WEAPON HARDPOINTS   ·   {snapshot.DeviceMounts} DEVICE SLOTS   ·   SERVER EQUIPMENT", small);
            float listTop = top + 82, listHeight = rect.height - 200;
            float mountedWidth = Mathf.Min(445, (rect.width - 70) * .43f);
            float cargoWidth = Mathf.Min(250, (rect.width - 70) * .25f);
            float detailWidth = rect.width - 40 - mountedWidth - cargoWidth - 30;
            float cargoX = left, mountX = cargoX + cargoWidth + 15;
            float detailX = mountX + mountedWidth + 15;
            float ammoHeight = 148;
            Rect mounts = OffsetPanel(new Rect(mountX, listTop, mountedWidth,
                listHeight), mountedOffset, rect);
            Rect cargoPanel = OffsetPanel(new Rect(cargoX, listTop, cargoWidth,
                listHeight - ammoHeight - 8), cargoOffset, rect);
            Rect magazines = OffsetPanel(new Rect(cargoX, listTop + listHeight - ammoHeight,
                cargoWidth, ammoHeight), ammoOffset, rect);
            HandleLoadoutPanelDrag(mounts, cargoPanel, magazines);
            if (Event.current.type == EventType.MouseDrag && draggedContainer != 0 && dragCanMove &&
                Vector3.Distance(Input.mousePosition, dragStartPosition) > 8)
                inventoryDragging = true;
            DrawFittingRing(snapshot, mounts);
            DrawCargoList(snapshot, cargoPanel);
            DrawAmmoList(snapshot, magazines);
            DrawLoadoutStats(snapshot, new Rect(detailX, listTop, detailWidth, 192));
            DrawItemDetail(snapshot, new Rect(detailX, listTop + 200,
                detailWidth, listHeight - 200));
            ItemView cargo = selectedCargo >= 0 && selectedCargo < snapshot.Cargo.Length ? snapshot.Cargo[selectedCargo] : null;
            ItemView mounted = selectedMount >= 0 && selectedMount < snapshot.Mounted.Length ? snapshot.Mounted[selectedMount] : null;
            ItemView selectedMagazine = selectedAmmo >= 0 && selectedAmmo < snapshot.Ammo.Length ?
                snapshot.Ammo[selectedAmmo] : null;
            bool cargoSpace = snapshot.Cargo.Take((int)snapshot.CargoCapacity)
                .Any(item => item == null);
            bool fits = FitsHardpoint(cargo, selectedMount, snapshot);
            float actionY = rect.yMax - 91;
            Fill(new Rect(left, actionY - 13, rect.width - 40, 1), new Color(.3f, .65f, .75f, .65f));
            GUI.Label(new Rect(left, actionY - 1, rect.width - 40, 19),
                !string.IsNullOrEmpty(GUI.tooltip) ? GUI.tooltip :
                inventoryDragging ? "DROP ON A COMPATIBLE HARDPOINT, MAGAZINE, OR CARGO SLOT" :
                "DRAG ITEMS BETWEEN PANELS  ·  CLICK TO INSPECT  ·  SERVER CONFIRMS MOVES", small);
            GUI.enabled = client.Ready && fits;
            if (GUI.Button(new Rect(mountX, actionY + 23, mountedWidth, 43),
                cargo?.Type == 10 ? "LOAD AMMO  →" : "INSTALL  →", primaryButton))
                client.EquipCargo(selectedCargo, selectedMount);
            GUI.enabled = client.Ready && cargoSpace &&
                (inspectAmmo ? selectedMagazine != null : mounted != null);
            if (GUI.Button(new Rect(cargoX, actionY + 23, cargoWidth, 43),
                !cargoSpace ? "CARGO HOLD FULL" : inspectAmmo ?
                "←  UNLOAD MAGAZINE" : "←  REMOVE TO CARGO", button))
            {
                if (inspectAmmo)
                    client.MoveInventory(new InventoryMove(8, selectedAmmo, 1,
                        FirstFreeCargo(snapshot), selectedMagazine.Quantity));
                else client.UnequipMount(selectedMount);
            }
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
                (weapon || device && (!needsPeer || recipient != null));
            if (GUI.Button(new Rect(detailX, actionY + 23, detailWidth, 43),
                weapon ? (mounted.WeaponState & 1) != 0 ? "STOP SELECTED WEAPON" : "FIRE SELECTED WEAPON" :
                needsPeer ? recipient == null ? "SELECT A PILOT FIRST" :
                    "TRANSFER TO " + recipient.Name.ToUpperInvariant() :
                targetedSupport ? recipient == null ? "ACTIVATE ON SELF" :
                    "SUPPORT " + recipient.Name.ToUpperInvariant() :
                device ? mounted.ActivationLabel.Length > 20 ? "ACTIVATE SELECTED EFFECT" :
                    "ACTIVATE " + mounted.ActivationLabel.ToUpperInvariant() : "SELECT A SYSTEM",
                weapon ? hostileButton : primaryButton))
            {
                if (weapon) client.ActivateWeapon(selectedMount);
                else if (device && (targetedSupport || needsPeer))
                    client.ActivateDeviceFor(selectedMount, recipient?.Id ?? snapshot.PlayerId);
                else if (device) client.ActivateDevice(selectedMount);
            }
            GUI.enabled = true;
            if (Event.current.type == EventType.MouseUp && Event.current.button == 0)
                ClearInventoryDrag();
        }

        /// <summary>Move the entire inventory window by its title without resetting widget offsets.</summary>
        private void HandleInventoryWindowDrag(Rect rect)
        {
            Event current = Event.current;
            Rect handle = new Rect(rect.x + 10, rect.y + 5, rect.width - 180, 45);
            if (current.type == EventType.MouseDown && current.button == 0 &&
                handle.Contains(current.mousePosition))
            {
                draggingInventoryWindow = true;
                inventoryDragStart = current.mousePosition;
                inventoryOffsetStart = inventoryOffset;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && draggingInventoryWindow)
            {
                inventoryOffset = inventoryOffsetStart + current.mousePosition - inventoryDragStart;
                current.Use();
            }
            else if (current.type == EventType.MouseUp && current.button == 0)
                draggingInventoryWindow = false;
        }

        /// <returns>Rect: A loadout widget kept inside the ship overlay.</returns>
        private static Rect OffsetPanel(Rect basis, Vector2 offset, Rect boundary)
        {
            return new Rect(Mathf.Clamp(basis.x + offset.x, boundary.x + 9,
                    boundary.xMax - basis.width - 9),
                Mathf.Clamp(basis.y + offset.y, boundary.y + 65,
                    boundary.yMax - basis.height - 20), basis.width, basis.height);
        }

        /// <summary>Move the three loadout widgets by their headers for this client session.</summary>
        private void HandleLoadoutPanelDrag(Rect mounts, Rect cargoPanel, Rect magazines)
        {
            Event current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0)
            {
                Rect[] panels = { mounts, cargoPanel, magazines };
                for (int i = panels.Length - 1; i >= 0; i--)
                {
                    Rect header = new Rect(panels[i].x, panels[i].y, panels[i].width, 28);
                    if (!header.Contains(current.mousePosition)) continue;
                    draggedLoadoutPanel = i + 1;
                    panelDragStart = current.mousePosition;
                    panelOffsetStart = i == 0 ? mountedOffset : i == 1 ? cargoOffset : ammoOffset;
                    current.Use(); return;
                }
            }
            if (current.type == EventType.MouseDrag && draggedLoadoutPanel != 0)
            {
                Vector2 next = panelOffsetStart + current.mousePosition - panelDragStart;
                if (draggedLoadoutPanel == 1) mountedOffset = next;
                else if (draggedLoadoutPanel == 2) cargoOffset = next;
                else ammoOffset = next;
                current.Use();
            }
            if (current.type == EventType.MouseUp && current.button == 0)
                draggedLoadoutPanel = 0;
        }

        /// <summary>Discard a completed or abandoned inventory drag.</summary>
        private void ClearInventoryDrag()
        { draggedContainer = 0; draggedSlot = -1; inventoryDragging = dragCanMove = false; }

        /// <summary>Draw the live ship with native hardpoints arranged around its hull.</summary>
        private void DrawFittingRing(Snapshot snapshot, Rect rect)
        {
            Panel(rect);
            GUI.Label(new Rect(rect.x + 11, rect.y + 5, rect.width - 22, 21),
                "FITTING BAY   /   " + (snapshot.Mounted.Count(item => item != null)) +
                " SYSTEMS MOUNTED       ⋮⋮", section);
            Rect well = new Rect(rect.x + 9, rect.y + 29, rect.width - 18, rect.height - 37);
            Fill(well, new Color(.008f, .019f, .034f, .9f));
            if (fittingRing == null) fittingRing = CreateFittingRing();
            if (fittingSocket == null) fittingSocket = CreateFittingSocket();
            float diameter = Mathf.Min(well.width - 96, well.height - 74);
            diameter = Mathf.Min(diameter, 306);
            Vector2 center = new Vector2(well.center.x, well.y + well.height * .51f);
            Rect circle = new Rect(center.x - diameter * .5f,
                center.y - diameter * .5f, diameter, diameter);
            if (shipPreview == null) shipPreview = new LoadoutShipPreview();
            if (Event.current.type == EventType.Repaint)
                shipPreview.Render(client.ShipForPreview);
            GUI.DrawTexture(new Rect(center.x - diameter * .49f,
                center.y - diameter * .49f, diameter * .98f, diameter * .98f),
                shipPreview.Texture,
                ScaleMode.ScaleToFit, true);
            GUI.DrawTexture(circle, fittingRing);
            int weapons = Mathf.Min((int)snapshot.WeaponMounts, 6);
            int devices = Mathf.Min((int)snapshot.DeviceMounts,
                Mathf.Max(0, snapshot.Mounted.Length - 9));
            const float socketSize = 61;
            for (int i = 0; i < weapons; i++)
            {
                int column = i % 2, row = i / 2;
                float x = column == 0 ? well.x + 14 + row * 8 :
                    well.xMax - socketSize - 14 - row * 8;
                float y = well.y + 98 + row * 83;
                DrawFittingSocket(snapshot, new Rect(x, y, socketSize, socketSize),
                    3 + i, "WEAPON " + (i + 1));
            }
            for (int i = 0; i < devices; i++)
            {
                float fraction = devices == 1 ? .5f : i / (float)(devices - 1);
                float x = well.x + 45 + fraction * (well.width - 90 - socketSize);
                float y = well.y + well.height * (.75f + .045f *
                    Mathf.Sin(fraction * Mathf.PI));
                DrawFittingSocket(snapshot, new Rect(x, y, socketSize, socketSize),
                    9 + i, "DEVICE " + (i + 1));
            }
            for (int slot = 0; slot < 3; slot++)
            {
                string[] labels = { "SHIELD", "REACTOR", "ENGINE" };
                float x = well.x + 54 + slot * (well.width - 108 - socketSize) / 2f;
                float y = well.y + 3;
                DrawFittingSocket(snapshot, new Rect(x, y, socketSize, socketSize),
                    slot, labels[slot]);
            }
        }

        /// <summary>Draw one native mounted slot and accept compatible cargo drops.</summary>
        private void DrawFittingSocket(Snapshot snapshot, Rect rect, int slot, string label)
        {
            if (slot >= snapshot.Mounted.Length) return;
            ItemView item = snapshot.Mounted[slot];
            bool clicked = HandleInventoryTile(rect, 2, slot, item != null, snapshot);
            bool drop = inventoryDragging && CanInventoryDrop(snapshot, 2, slot);
            Color tint = GUI.color;
            GUI.color = selectedMount == slot || drop ?
                new Color(.88f, 1f, 1f, 1f) : item == null ?
                new Color(.32f, .46f, .53f, .9f) : slot < 3 ?
                new Color(.61f, .75f, .97f, 1f) : slot < 9 ?
                new Color(1f, .73f, .4f, 1f) :
                new Color(.48f, .93f, .83f, 1f);
            GUI.DrawTexture(rect, fittingSocket);
            GUI.color = tint;
            uint type = item?.Type ?? (slot == 0 ? 2u : slot == 1 ? 7u :
                slot == 2 ? 6u : slot < 9 ? 14u : 11u);
            GUI.DrawTexture(new Rect(rect.x + 17, rect.y + 15, 28, 28),
                item == null ? icons.ForType(type) : icons.ForItem(item));
            string status = item == null ? "" : item.OutOfAmmo ?
                "EMPTY" : item.Cooling ? "COOL" : "";
            if (item != null && (item.Type == 15 || item.Type == 16))
            {
                ItemView ammo = slot < snapshot.Ammo.Length ? snapshot.Ammo[slot] : null;
                status = (ammo?.Quantity ?? 0) + "/" + (ammo?.MaxStack ?? 0);
            }
            GUI.Label(new Rect(rect.x - 7, slot < 3 ? rect.yMax + 1 : rect.y - 17,
                rect.width + 14, 16), slot < 3 ? label :
                slot < 9 ? "WEAPON " + (slot - 2) : "DEVICE " + (slot - 8),
                equipmentMeta);
            if (status.Length > 0)
                GUI.Label(new Rect(rect.x + 7, rect.yMax - 18, rect.width - 14, 15),
                    status, equipmentMeta);
            GUI.Label(rect, new GUIContent("", label + "  ·  " +
                (item?.Name ?? "EMPTY SOCKET") + (status.Length > 0 ? "  ·  " + status : "")));
            if (clicked)
            {
                selectedMount = selectedMount == slot ? -1 : slot;
                inspectMount = true; inspectAmmo = false;
                inspectEffects = item?.EquippedEffects.Count > 0;
                effectScroll = Vector2.zero;
            }
        }

        /// <returns>bool: Whether a graphical inventory source was clicked.</returns>
        private bool HandleInventoryTile(Rect rect, uint container, int slot,
            bool canDrag, Snapshot snapshot)
        {
            Event current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0 &&
                rect.Contains(current.mousePosition))
            {
                draggedContainer = container; draggedSlot = slot; dragCanMove = canDrag;
                dragStartPosition = Input.mousePosition; inventoryDragging = false;
                current.Use(); return false;
            }
            if (current.type != EventType.MouseUp || current.button != 0 ||
                !rect.Contains(current.mousePosition) || draggedContainer == 0) return false;
            bool clicked = !inventoryDragging && draggedContainer == container &&
                draggedSlot == slot;
            if (inventoryDragging && dragCanMove &&
                (draggedContainer != container || draggedSlot != slot))
                SubmitInventoryDrop(snapshot, container, slot);
            ClearInventoryDrag(); current.Use();
            return clicked;
        }

        /// <returns>Texture2D: A transparent fitting reticle behind the live ship.</returns>
        private static Texture2D CreateFittingRing()
        {
            const int size = 256;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x - 127.5f, dy = y - 127.5f;
                    float radius = Mathf.Sqrt(dx * dx + dy * dy);
                    float angle = Mathf.Atan2(dy, dx);
                    float shade = Mathf.Clamp01((122 - radius) / 95f);
                    bool ring = Mathf.Abs(radius - 111) < .8f ||
                        Mathf.Abs(radius - 91) < .6f;
                    bool tick = radius > 101 && radius < 110 &&
                        Mathf.Abs(Mathf.Sin(angle * 18)) < .045f;
                    pixels[y * size + x] = ring || tick ?
                        new Color(.29f, .73f, .84f, ring ? .42f : .3f) :
                        radius < 110 ? new Color(.02f, .09f, .12f, shade * .42f) :
                        Color.clear;
                }
            texture.SetPixels(pixels); texture.Apply();
            return texture;
        }

        /// <returns>Texture2D: A restrained circular fitting socket with a lit edge.</returns>
        private static Texture2D CreateFittingSocket()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float radius = Vector2.Distance(new Vector2(x, y),
                        new Vector2(31.5f, 31.5f));
                    pixels[y * size + x] = radius > 30 ? Color.clear :
                        radius > 27.5f ? new Color(.42f, .83f, .92f, .78f) :
                        new Color(.028f, .07f, .105f, .92f);
                }
            texture.SetPixels(pixels); texture.Apply();
            return texture;
        }

        /// <summary>Show only server-supplied defense, reactor and movement values.</summary>
        private void DrawLoadoutStats(Snapshot snapshot, Rect rect)
        {
            Panel(rect);
            float x = rect.x + 10, width = rect.width - 20;
            GUI.Label(new Rect(x, rect.y + 5, width, 18), "LIVE SHIP SYSTEMS", section);
            GUI.Label(new Rect(x, rect.y + 27, width, 17), "DEFENSE", equipmentMeta);
            GUI.Label(new Rect(x, rect.y + 43, width, 17),
                $"SH {snapshot.Shield * snapshot.MaxShield:0}/{snapshot.MaxShield:0}  ·  HULL {snapshot.Hull:0}/{snapshot.MaxHull:0}",
                equipmentText);
            Fill(new Rect(x, rect.y + 62, width, 4), new Color(.15f, .24f, .29f));
            Fill(new Rect(x, rect.y + 62, width * Mathf.Clamp01(snapshot.Shield), 4),
                new Color(.33f, .79f, .92f));
            if (snapshot.Defense.Known)
                GUI.Label(new Rect(x, rect.y + 71, width, 18),
                    "DEFLECT  " + string.Join(" / ", snapshot.Defense.Deflects.Select(
                        value => value.ToString())) + "%", equipmentMeta);
            GUI.Label(new Rect(x, rect.y + 94, width, 17), "REACTOR", equipmentMeta);
            GUI.Label(new Rect(x, rect.y + 110, width, 17),
                $"ENERGY {snapshot.Energy * snapshot.MaxEnergy:0}/{snapshot.MaxEnergy:0}",
                equipmentText);
            Fill(new Rect(x, rect.y + 129, width, 4), new Color(.15f, .24f, .29f));
            Fill(new Rect(x, rect.y + 129, width * Mathf.Clamp01(snapshot.Energy), 4),
                new Color(.77f, .63f, .32f));
            GUI.Label(new Rect(x, rect.y + 142, width, 17), "SPEED", equipmentMeta);
            GUI.Label(new Rect(x, rect.y + 158, width, 17),
                $"{snapshot.Speed:0} m/s  ·  TARGET {snapshot.TargetSpeed:0} m/s",
                equipmentText);
            GUI.Label(new Rect(x, rect.y + 175, width, 17),
                $"ACCEL {snapshot.Acceleration:0} m/s²", equipmentMeta);
        }

        /// <summary>Show each native launcher magazine as a separate drop target.</summary>
        private void DrawAmmoList(Snapshot snapshot, Rect rect)
        {
            Panel(rect);
            GUI.Label(new Rect(rect.x + 11, rect.y + 5, rect.width - 22, 21),
                "MAGAZINES  ·  DROP AMMO     ⋮⋮", section);
            GUILayout.BeginArea(new Rect(rect.x + 7, rect.y + 29,
                rect.width - 14, rect.height - 35));
            ammoWidgetScroll = GUILayout.BeginScrollView(ammoWidgetScroll);
            float tileWidth = Mathf.Max(92, (rect.width - 32) * .5f);
            int column = 0;
            for (int slot = 3; slot < 3 + snapshot.WeaponMounts && slot < snapshot.Mounted.Length; slot++)
            {
                ItemView weapon = snapshot.Mounted[slot];
                if (weapon?.Type != 15 && weapon?.Type != 16) continue;
                if (column == 0) GUILayout.BeginHorizontal();
                ItemView ammo = slot < snapshot.Ammo.Length ? snapshot.Ammo[slot] : null;
                string count = ammo == null ? "0 ROUNDS" :
                    ammo.Quantity + " / " + (ammo.MaxStack == 0 ? "?" : ammo.MaxStack.ToString());
                if (DrawEquipmentTile("W" + (slot - 2), ammo?.Name ?? "DROP AMMO",
                    count, 10, false, tileWidth, 103, ammo == null,
                    3, slot, ammo != null, snapshot, ammo))
                { selectedAmmo = slot; inspectAmmo = true; inspectMount = false;
                  inspectEffects = false; effectScroll = Vector2.zero; }
                if (++column == 2) { GUILayout.EndHorizontal(); column = 0; }
            }
            if (column != 0) GUILayout.EndHorizontal();
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        /// <summary>List installed systems by slot and report weapon ammunition state.</summary>
        private void DrawMountedList(Snapshot snapshot, Rect rect)
        {
            Panel(rect);
            GUI.Label(new Rect(rect.x + 11, rect.y + 5, rect.width - 22, 21),
                "INSTALLED / HARDPOINTS       ⋮⋮", section);
            GUILayout.BeginArea(new Rect(rect.x + 7, rect.y + 29,
                rect.width - 14, rect.height - 35));
            mountScroll = GUILayout.BeginScrollView(mountScroll);
            float tileWidth = Mathf.Max(90, (rect.width - 34) / 3f);
            DrawMountedGroup(snapshot, "CORE SYSTEMS", 0, 3, tileWidth);
            DrawMountedGroup(snapshot, "WEAPON HARDPOINTS", 3,
                (int)snapshot.WeaponMounts, tileWidth);
            DrawMountedGroup(snapshot, "DEVICE SLOTS", 9,
                (int)snapshot.DeviceMounts, tileWidth);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        /// <summary>Draw a labeled row of installed equipment slots as visual drop targets.</summary>
        private void DrawMountedGroup(Snapshot snapshot, string label, int first,
            int count, float tileWidth)
        {
            if (count <= 0) return;
            GUILayout.Label(label, small);
            int column = 0;
            for (int slot = first; slot < first + count && slot < snapshot.Mounted.Length; slot++)
            {
                if (column == 0) GUILayout.BeginHorizontal();
                ItemView item = snapshot.Mounted[slot];
                string slotName = slot < 3 ? new[] { "SHIELD", "REACTOR", "ENGINE" }[slot] : slot < 9 ?
                    "WEAPON " + (slot - 2) : "DEVICE " + (slot - 8);
                string status = item == null ? "DROP ITEM" :
                    (Mathf.Clamp01(item.Structure) * 100).ToString("0") + "% CONDITION";
                bool weapon = item != null && item.Type >= 14 && item.Type <= 16;
                if (weapon)
                {
                    ItemView ammo = slot < snapshot.Ammo.Length ? snapshot.Ammo[slot] : null;
                    status = item.Type == 14 ? "BEAM · " +
                        ((item.WeaponState & 1) != 0 ? "ARMED" : "SAFE") :
                        (ammo?.Quantity ?? 0) + "/" + (ammo?.MaxStack ?? 0) + " ROUNDS";
                }
                uint iconType = item?.Type ?? (slot == 0 ? 2u : slot == 1 ? 7u : slot == 2 ? 6u : slot < 9 ? 14u : 11u);
                if (DrawEquipmentTile(slotName, item?.Name ?? "EMPTY SLOT", status, iconType,
                    selectedMount == slot, tileWidth, 112, item != null && item.OutOfAmmo,
                    2, slot, item != null, snapshot, item))
                    { selectedMount = selectedMount == slot ? -1 : slot; inspectMount = true;
                        inspectAmmo = false;
                        inspectEffects = item?.EquippedEffects.Count > 0; effectScroll = Vector2.zero; }
                if (++column == 3) { GUILayout.EndHorizontal(); column = 0; }
            }
            if (column != 0) GUILayout.EndHorizontal();
        }

        /// <summary>List cargo with category, stack count and condition.</summary>
        private void DrawCargoList(Snapshot snapshot, Rect rect)
        {
            Panel(rect);
            int used = snapshot.Cargo.Count(item => item != null);
            GUI.Label(new Rect(rect.x + 11, rect.y + 5, rect.width - 22, 21),
                "CARGO  " + used + " / " +
                (snapshot.CargoCapacity == 0 ? "?" : snapshot.CargoCapacity.ToString()) + "       ⋮⋮", section);
            GUILayout.BeginArea(new Rect(rect.x + 7, rect.y + 29,
                rect.width - 14, rect.height - 35));
            cargoScroll = GUILayout.BeginScrollView(cargoScroll);
            float tileWidth = rect.width - 50;
            foreach (IGrouping<string, ItemView> group in snapshot.Cargo.Where(item => item != null)
                .GroupBy(item => ItemCategory(item.Type)).OrderBy(group => CargoGroupOrder(group.Key)))
            {
                GUILayout.Label(group.Key + "  ·  " + group.Count(), small);
                foreach (ItemView item in group.OrderBy(entry => entry.Name))
                {
                    string status = "×" + item.Quantity + "  ·  " +
                        (Mathf.Clamp01(item.Structure) * 100).ToString("0") + "%";
                    if (DrawEquipmentTile(group.Key, item.Name, status, item.Type,
                        selectedCargo == item.Slot, tileWidth, 88, false,
                        1, item.Slot, true, snapshot, item))
                    { selectedCargo = selectedCargo == item.Slot ? -1 : item.Slot; inspectMount = false;
                        inspectAmmo = false;
                            inspectEffects = item.EquippedEffects.Count > 0; effectScroll = Vector2.zero; }
                }
            }
            GUILayout.Label(used == 0 ? "EMPTY CARGO SLOTS" : "OPEN CARGO SLOTS", small);
            int shown = 0;
            for (int slot = 0; slot < snapshot.CargoCapacity && slot < snapshot.Cargo.Length && shown < 6; slot++)
            {
                if (snapshot.Cargo[slot] != null) continue;
                DrawEquipmentTile("CARGO " + (slot + 1), "EMPTY SLOT", "DROP ITEM",
                    0, false, tileWidth, 76, false, 1, slot, false, snapshot);
                shown++;
            }
            GUILayout.EndScrollView(); GUILayout.EndArea();
            Event current = Event.current;
            if (inventoryDragging && current.type == EventType.MouseUp && current.button == 0 &&
                rect.Contains(current.mousePosition))
            {
                int free = FirstFreeCargo(snapshot);
                if (free >= 0 && draggedContainer != 1)
                    SubmitInventoryDrop(snapshot, 1, free);
                else if (free >= 0 && draggedContainer == 1)
                    SubmitInventoryDrop(snapshot, 1, free);
                current.Use(); ClearInventoryDrag();
            }
        }

        /// <returns>int: A stable order for the visible cargo category groups.</returns>
        private static int CargoGroupOrder(string category)
        {
            switch (category)
            {
                case "WEAPON": return 0;
                case "AMMUNITION": return 1;
                case "SHIELD": case "REACTOR": case "ENGINE": return 2;
                case "DEVICE": return 3;
                default: return 4;
            }
        }

        /// <returns>int: The first open server-owned cargo slot, or minus one.</returns>
        private static int FirstFreeCargo(Snapshot snapshot)
        {
            for (int i = 0; i < snapshot.CargoCapacity && i < snapshot.Cargo.Length; i++)
                if (snapshot.Cargo[i] == null) return i;
            return -1;
        }

        /// <returns>bool: Whether the user selected a graphical inventory slot.</returns>
        private bool DrawEquipmentTile(string slot, string name, string status, uint type,
            bool active, float tileWidth, float tileHeight, bool warning, uint container, int itemSlot,
            bool canDrag, Snapshot snapshot, ItemView item = null)
        {
            Rect row = GUILayoutUtility.GetRect(tileWidth, tileHeight,
                GUILayout.Width(tileWidth), GUILayout.Height(tileHeight));
            Event current = Event.current;
            bool clicked = false;
            if (current.type == EventType.MouseDown && current.button == 0 &&
                row.Contains(current.mousePosition))
            {
                draggedContainer = container; draggedSlot = itemSlot; dragCanMove = canDrag;
                dragStartPosition = Input.mousePosition; inventoryDragging = false;
                current.Use();
            }
            else if (current.type == EventType.MouseUp && current.button == 0 &&
                row.Contains(current.mousePosition) && draggedContainer != 0)
            {
                if (inventoryDragging && dragCanMove &&
                    (draggedContainer != container || draggedSlot != itemSlot))
                    SubmitInventoryDrop(snapshot, container, itemSlot);
                else if (!inventoryDragging && draggedContainer == container &&
                    draggedSlot == itemSlot) clicked = true;
                ClearInventoryDrag(); current.Use();
            }
            bool drop = inventoryDragging &&
                CanInventoryDrop(snapshot, container, itemSlot);
            GUI.Box(row, new GUIContent("", name + "\n" + status),
                active || drop ? selected : equipmentRow);
            if (drop) Fill(new Rect(row.x + 2, row.y + 2, row.width - 4, 2), cyan);
            if (tileHeight <= 90)
            {
                GUI.DrawTexture(new Rect(row.x + 7, row.y + 22, 28, 28),
                    item == null ? icons.ForType(type) : icons.ForItem(item));
                GUI.Label(new Rect(row.x + 40, row.y + 3, row.width - 46, 17),
                    slot, equipmentMeta);
                string line = name;
                while (line.Length > 4 && equipmentText.CalcHeight(
                    new GUIContent(line), row.width - 48) > 34)
                    line = line.Substring(0, line.Length - 2).TrimEnd() + "…";
                GUI.Label(new Rect(row.x + 40, row.y + 22, row.width - 46, 35),
                    line, equipmentText);
                GUI.Label(new Rect(row.x + 40, row.yMax - 21, row.width - 46, 17),
                    status, equipmentMeta);
                return clicked;
            }
            GUI.DrawTexture(new Rect(row.x + (row.width - 36) * .5f, row.y + 20, 36, 36),
                item == null ? icons.ForType(type) : icons.ForItem(item));
            GUI.Label(new Rect(row.x + 7, row.y + 3, row.width - 14, 17), slot, equipmentMeta);
            string shortName = name;
            while (shortName.Length > 4 && equipmentText.CalcHeight(
                new GUIContent(shortName), row.width - 12) > 31)
                shortName = shortName.Substring(0, shortName.Length - 2).TrimEnd() + "…";
            GUI.Label(new Rect(row.x + 6, row.y + 58, row.width - 12, 31),
                shortName, equipmentText);
            Color old = GUI.color;
            if (warning) GUI.color = new Color(1f, .55f, .43f);
            GUI.Label(new Rect(row.x + 6, row.yMax - 20, row.width - 12, 17),
                status, equipmentMeta);
            GUI.color = old;
            return clicked;
        }

        /// <summary>Submit one drag through the worker, retaining the current snapshot until reply.</summary>
        private void SubmitInventoryDrop(Snapshot snapshot, uint destination, int destinationSlot)
        {
            if (!client.Ready || draggedSlot < 0 || destinationSlot < 0) return;
            ItemView source = draggedContainer == 1 && draggedSlot < snapshot.Cargo.Length ?
                snapshot.Cargo[draggedSlot] : draggedContainer == 2 &&
                draggedSlot < snapshot.Mounted.Length ? snapshot.Mounted[draggedSlot] :
                draggedContainer == 3 && draggedSlot < snapshot.Ammo.Length ?
                snapshot.Ammo[draggedSlot] : null;
            if (source == null) return;
            if (destination == 3 && draggedContainer == 1) destination = 2;
            if (draggedContainer == 1 && destination == 2)
            {
                if (FitsHardpoint(source, destinationSlot, snapshot))
                    client.EquipCargo(draggedSlot, destinationSlot);
                return;
            }
            if (draggedContainer == 2 && destination == 1)
            {
                if (destinationSlot < snapshot.Cargo.Length &&
                    snapshot.Cargo[destinationSlot] == null)
                    client.MoveInventory(new InventoryMove(2, draggedSlot, 1,
                        destinationSlot, 1));
                return;
            }
            if (draggedContainer == 2 && destination == 2 &&
                !FitsHardpoint(source, destinationSlot, snapshot)) return;
            if (draggedContainer == 3 && destination == 2) return;
            if (destination == 1 && draggedContainer == 3 &&
                snapshot.Cargo[destinationSlot] is ItemView occupied &&
                (occupied.Template != source.Template || occupied.Quantity >= occupied.MaxStack)) return;
            if (draggedContainer == destination && draggedSlot == destinationSlot) return;
            // UI container 3 denotes magazines; native container 3 is the station vault.
            client.MoveInventory(new InventoryMove(draggedContainer == 3 ? 8u : draggedContainer,
                draggedSlot, destination == 3 ? 8u : destination,
                destinationSlot, source.Quantity));
        }

        /// <returns>bool: Whether a slot is a plausible visual target for the held item.</returns>
        private bool CanInventoryDrop(Snapshot snapshot, uint destination, int slot)
        {
            if (snapshot == null || slot < 0 || draggedContainer == 0 || draggedSlot < 0 ||
                draggedContainer == destination && draggedSlot == slot) return false;
            ItemView held = draggedContainer == 1 && draggedSlot < snapshot.Cargo.Length ?
                snapshot.Cargo[draggedSlot] : draggedContainer == 2 &&
                draggedSlot < snapshot.Mounted.Length ? snapshot.Mounted[draggedSlot] :
                draggedContainer == 3 && draggedSlot < snapshot.Ammo.Length ?
                snapshot.Ammo[draggedSlot] : null;
            if (held == null) return false;
            if (draggedContainer == 1 && (destination == 2 || destination == 3))
                return FitsHardpoint(held, slot, snapshot);
            if (destination == 1 && slot < snapshot.Cargo.Length)
            {
                ItemView target = snapshot.Cargo[slot];
                if (draggedContainer == 2) return target == null;
                if (draggedContainer == 3) return target == null ||
                    target.Template == held.Template && target.Quantity < target.MaxStack;
                return true;
            }
            if (draggedContainer == 2 && destination == 2)
                return FitsHardpoint(held, slot, snapshot);
            return draggedContainer == 3 && destination == 3 &&
                slot >= 3 && slot < 3 + snapshot.WeaponMounts;
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
            ItemView ammo = selectedAmmo >= 0 && selectedAmmo < snapshot.Ammo.Length ?
                snapshot.Ammo[selectedAmmo] : null;
            ItemView item = inspectAmmo ? ammo ?? cargo ?? mount :
                inspectMount ? mount ?? cargo : cargo ?? mount;
            Panel(rect);
            float x = rect.x + 13, y = rect.y + 14, width = rect.width - 26;
            GUI.Label(new Rect(x, y, width, 22), "ITEM INSPECTOR", section);
            if (item == null)
            {
                GUI.Label(new Rect(x, y + 43, width, 75),
                    "SELECT A SYSTEM", body);
                GUI.Label(new Rect(x, y + 104, width, 76),
                    "Choose an installed hardpoint or cargo item to inspect its condition and use its controls.", wrappedBody);
                Fill(new Rect(x, y + 191, width, 1), new Color(.35f, .7f, .8f, .34f));
                GUI.Label(new Rect(x, y + 207, width, 82),
                    "To install: select a cargo item, then a compatible hardpoint. The server confirms every change.", small);
                return;
            }
            GUI.DrawTexture(new Rect(x, y + 39, 44, 44), icons.ForItem(item));
            GUI.Label(new Rect(x + 52, y + 34, width - 52, 54), item.Name.ToUpperInvariant(),
                item.Name.Length > 16 ? equipmentText : wrappedBody);
            GUI.Label(new Rect(x, y + 91, width, 20), ItemCategory(item.Type) + "   ·   TEMPLATE " + item.Template, small);
            Fill(new Rect(x, y + 115, width, 1), new Color(.35f, .7f, .8f, .32f));
            float meterWidth = (width - 12) * .5f;
            GUI.Label(new Rect(x, y + 124, meterWidth, 18),
                "QUALITY  " + (Mathf.Clamp01(item.Quality) * 100).ToString("0") + "%", small);
            Meter(new Rect(x, y + 147, meterWidth, 8), "", item.Quality, cyan);
            GUI.Label(new Rect(x + meterWidth + 12, y + 124, meterWidth, 18),
                "STRUCTURE  " + (Mathf.Clamp01(item.Structure) * 100).ToString("0") + "%", small);
            Meter(new Rect(x + meterWidth + 12, y + 147, meterWidth, 8), "", item.Structure, gold);
            bool needsPeer = item == mount && (item.TargetsPeer ||
                DeviceEffects.RequiresPeer(item.ActivationLabel));
            if (item.EquippedEffects.Count > 0)
            {
                if (GUI.Button(new Rect(x, y + 169, width, 29), inspectEffects ?
                    "EFFECTS  ·  VIEW STATS" :
                    "VIEW EFFECTS  ·  " + item.EquippedEffects.Count, button))
                    { inspectEffects = !inspectEffects; effectScroll = Vector2.zero; }
            }
            if (inspectEffects && item.EquippedEffects.Count > 0)
                DrawItemEffects(item, new Rect(x, y + 207, width,
                    Mathf.Max(30, rect.height - (needsPeer ? 300 : 218))), item == mount);
            else
            {
                if (item.Range > 0) GUI.Label(new Rect(x, y + 211, width, 24),
                    "RANGE  " + item.Range.ToString("N0") + " m", body);
                if (item.Price > 0) GUI.Label(new Rect(x, y + 239, width, 20),
                    "VALUE  " + item.Price.ToString("N0") + " CREDITS", small);
                GUI.Label(new Rect(x, y + 265, width, 20), "STACK  ×" + item.Quantity, small);
                if (!needsPeer && !string.IsNullOrWhiteSpace(item.Info) && item.Info.Count(char.IsLetter) >= 3 && !item.Info.Contains("^"))
                    GUI.Label(new Rect(x, y + 291, width, rect.height - 300), item.Info, small);
            }
            if (needsPeer) DrawDeviceRecipient(snapshot, rect, item.TargetsPeer);
        }

        /// <summary>Show every server-sent equipped catalog effect without predicting combat math.</summary>
        private void DrawItemEffects(ItemView item, Rect rect, bool installed)
        {
            float width = rect.width - 18, height = 24;
            foreach (ItemEffectInfo effect in item.EquippedEffects)
                height += 24 + Mathf.Max(20, wrappedBody.CalcHeight(new GUIContent(effect.Detail), width)) + 6;
            effectScroll = GUI.BeginScrollView(rect, effectScroll,
                new Rect(0, 0, width, Mathf.Max(rect.height, height)));
            GUI.Label(new Rect(0, 0, width, 20), item.Structure <= 0 ?
                "BROKEN · EFFECTS INACTIVE" : installed ?
                "INSTALLED · SERVER APPLIES" : "EFFECTS WHEN INSTALLED", small);
            float row = 24;
            foreach (ItemEffectInfo effect in item.EquippedEffects)
            {
                Fill(new Rect(0, row - 3, width, 1), new Color(.36f, .68f, .78f, .26f));
                GUI.Label(new Rect(0, row, width, 20), effect.Label, section);
                row += 24;
                float detailHeight = Mathf.Max(20, wrappedBody.CalcHeight(new GUIContent(effect.Detail), width));
                GUI.Label(new Rect(0, row, width, detailHeight), effect.Detail, wrappedBody);
                row += detailHeight + 6;
            }
            GUI.EndScrollView();
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
            if (fittingRing != null) UnityEngine.Object.Destroy(fittingRing);
            if (fittingSocket != null) UnityEngine.Object.Destroy(fittingSocket);
            deck?.Dispose();
            shipPreview?.Dispose();
            icons.Dispose();
        }
    }
}
