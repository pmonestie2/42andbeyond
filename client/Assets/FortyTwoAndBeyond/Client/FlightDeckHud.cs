using System;
using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    public sealed partial class FlightHud
    {
        private DeckView deck;
        internal bool RetainedVisible => deck?.Visible == true;
        internal bool RetainedOwnsPointer => deck?.ContainsPointer() == true;
        internal Rect RetainedQuickbarRect => deck?.QuickbarBounds ?? Rect.zero;

        /// <summary>Publish the current immutable snapshot to retained flight instruments.</summary>
        public void UpdateRetained()
        {
            if (deck == null) deck = new DeckView(this);
            deck.Update();
        }

        /// <summary>Keep world markers and specialized controls alongside the retained flight deck.</summary>
        private void DrawDeckCompanions(Snapshot snapshot)
        {
            if (client.Incapacitated) equipmentOpen = false;
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
            if (Event.current.type == EventType.Repaint && !equipmentOpen && !client.Chart)
            {
                occupiedFlightLabels.Clear(); worldLabelHits.Clear();
                WorldLabels(snapshot);
                PlayerLabels(snapshot); WreckLabels(snapshot); DamageLabels(snapshot);
            }
            if (!equipmentOpen)
            {
                if (crewTab) Social(snapshot);
                if (!client.AbilitiesOpen && !client.Incapacitated) WreckPanel(snapshot);

                InputFocused = crewTab && GUI.GetNameOfFocusedControl() == "Group invite";
                ChatEntry();
                if (snapshot.Dialogue != null) StationDialogue(snapshot);
                if (settingsOpen) Settings();
            }
        }

        /// <summary>Retained flight and fitting surfaces driven exclusively by server snapshots.</summary>
        private sealed class DeckView : IDisposable
        {
            private readonly FlightHud hud;
            private SpaceClient Client => hud.client;
            private readonly GameObject gameObject;
            private readonly PanelSettings settings;
            private readonly VisualElement root, flight, fitting, ring, cargoColumn, statsColumn, scanner, comms;
            private readonly Label sectorLabel, speedLabel, modeLabel, statusLabel, cargoCount, fittingSubtitle;
            private readonly Label targetName, targetMeta, inspectorTitle, inspectorText, messageLabel;
            private readonly ScrollView contacts, destinations, cargoList, buffs;
            private readonly VisualElement targetPanel, weapons, detail, dragGhost, quickbar;
            private readonly Label[] deflectValues = new Label[7];
            private readonly Label[] quickNames = new Label[6], quickKeys = new Label[6];
            private readonly Image[] quickIcons = new Image[6];
            private readonly VisualElement[] quickSlots = new VisualElement[6];
            private readonly RadarDial radar;
            private readonly Label dragCaption;
            private readonly Image preview;
            private readonly Dial dial;
            private readonly Gauge shield, reactor, hull, targetShield, targetHull, fitShield, fitReactor;
            private readonly Button fire, follow, warp, dock, install, remove, activate, unload, jumpstart;
            private readonly VisualElement travelActions;
            private readonly VisualElement inviteOverlay;
            private readonly Label invitationText;
            private readonly VisualElement chatComposer;
            private readonly ScrollView chatHistory;
            private readonly TextField chatInput;
            private readonly Button chatChannel;
            private bool chatWasOpen;
            private readonly Dictionary<int, SocketView> sockets = new Dictionary<int, SocketView>();
            private readonly Dictionary<int, WeaponView> weaponSlots = new Dictionary<int, WeaponView>();
            private readonly Dictionary<uint, Label> destinationRanges = new Dictionary<uint, Label>();
            private readonly Dictionary<uint, VisualElement> destinationRows = new Dictionary<uint, VisualElement>();
            private readonly List<(VisualElement Element, uint Container, int Slot)> dropTargets =
                new List<(VisualElement, uint, int)>();
            private readonly Dictionary<VisualElement, (StyleLength Left, StyleLength Top, StyleLength Right, StyleLength Bottom)> initialPositions =
                new Dictionary<VisualElement, (StyleLength, StyleLength, StyleLength, StyleLength)>();
            private readonly HashSet<VisualElement> movedPanels = new HashSet<VisualElement>();
            private string inventoryKey = "", contactKey = "", navigationKey = "", buffKey = "";
            private float nextRefresh;
            private bool positioned, draggingItem, quickbarMoved;
            private bool fittingWasOpen;
            private Vector2 lastCanvasSize;
            private Vector2 itemDown;
            private uint itemContainer;
            private int itemSlot = -1;
            private VisualElement itemSource;
            private uint selectedActor;
            public bool Visible { get; private set; }
            public Rect QuickbarBounds => quickbar.worldBound;
            private static readonly Color Amber = new Color(.83f, .69f, .43f);
            private static readonly Color Blue = new Color(.46f, .74f, .85f);

            /// <summary>Create one persistent visual tree with independently movable flight panels.</summary>
            public DeckView(FlightHud hud)
            {
                this.hud = hud;
                settings = ScriptableObject.CreateInstance<PanelSettings>();
                settings.scaleMode = PanelScaleMode.ConstantPixelSize;
                settings.sortingOrder = 10;
                settings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("UI/FlightDeckTheme");
                gameObject = new GameObject("Flight deck UI");
                var document = gameObject.AddComponent<UIDocument>();
                document.panelSettings = settings;
                root = document.rootVisualElement;
                root.AddToClassList("deck"); root.pickingMode = PickingMode.Ignore;
                root.style.flexGrow = 1;
                root.style.unityFont = Resources.Load<Font>("Fonts/IBMPlexSans-Regular");
                root.styleSheets.Add(Resources.Load<StyleSheet>("UI/FlightDeck"));
                flight = Box(root, "flight"); Stretch(flight); flight.pickingMode = PickingMode.Ignore;
                var location = Box(flight, "panel"); Place(location, 22, 18, 330, 65);
                Text(location, "42 / BEYOND     •     FLIGHT DECK", "eyebrow");
                sectorLabel = Text(location, "", "title"); sectorLabel.style.fontSize = 19;
                location.style.paddingTop = 10;
                var toolbar = Box(flight, "row"); toolbar.style.position = UnityEngine.UIElements.Position.Absolute;
                toolbar.style.right = 24; toolbar.style.top = 25;
                ActionButton(toolbar, "Fitting  /  I", () => hud.ToggleEquipment(), "primary");
                ActionButton(toolbar, "Missions", () => Client.MissionsUi.Toggle());
                ActionButton(toolbar, "Skills", () => Client.AbilitiesUi.Toggle());
                ActionButton(toolbar, "Controls", () => hud.settingsOpen = true);
                ActionButton(toolbar, "New client", () => Client.OpenAnotherClient());
                ActionButton(toolbar, "Disconnect", () => Client.Disconnect());
                ActionButton(toolbar, "Chart / Tab", () => Client.SetChart(!Client.Chart));
                foreach (VisualElement action in toolbar.Children()) action.style.marginLeft = 7;

                scanner = Box(flight, "panel"); Place(scanner, 22, 100, 265, 268);
                var scannerHead = Box(scanner, "row spread"); Text(scannerHead, "OVERVIEW", "eyebrow");
                ActionButton(scannerHead, "Crew", () => hud.crewTab = !hud.crewTab).style.minHeight = 22;
                contacts = Scroll(scanner); contacts.style.flexGrow = 1;
                MakeMovable(scanner, scannerHead, "overview");

                var nav = Box(flight, "panel"); nav.style.position = UnityEngine.UIElements.Position.Absolute;
                nav.style.right = 22; nav.style.top = 100; nav.style.width = 265; nav.style.height = 235;
                var navHead = Text(nav, "DESTINATIONS", "eyebrow"); MakeMovable(nav, navHead, "destinations");
                destinations = Scroll(nav); destinations.style.flexGrow = 1;
                ActionButton(nav, "Free flight", () => Client.SelectFreeFlight()).style.marginTop = 6;

                targetPanel = Box(flight, "panel"); targetPanel.style.position = UnityEngine.UIElements.Position.Absolute;
                targetPanel.style.right = 22; targetPanel.style.top = 350; targetPanel.style.width = 265;
                var targetHead = Text(targetPanel, "SELECTED TARGET", "eyebrow"); MakeMovable(targetPanel, targetHead, "target");
                targetName = Text(targetPanel, "No target", "title"); targetName.style.fontSize = 18; targetName.style.whiteSpace = WhiteSpace.Normal;
                targetMeta = Text(targetPanel, "Choose an object in space", "subtle"); targetMeta.style.marginTop = 6;
                targetShield = new Gauge(targetPanel, "Shield", Blue);
                targetHull = new Gauge(targetPanel, "Hull", Amber);
                var targetActions = Box(targetPanel, "row");
                fire = ActionButton(targetActions, "Fire", FireTarget, "primary"); fire.style.flexGrow = 1;
                follow = ActionButton(targetActions, "Follow", () => Client.FollowSelected()); follow.style.marginLeft = 7; follow.style.flexGrow = 1;
                travelActions = Box(targetPanel, "row"); travelActions.style.marginTop = 7;
                warp = ActionButton(travelActions, "Warp", () => Client.WarpSelected()); warp.style.flexGrow = 1;
                dock = ActionButton(travelActions, "Dock", () => { if (Client.Target?.Kind == 12) Client.DockSelected(); else Client.GateSelected(); });
                dock.style.marginLeft = 7; dock.style.flexGrow = 1;

                jumpstart = ActionButton(targetPanel, "Jumpstart pilot", () => Client.ActivateAbility(65u), "primary");
                jumpstart.style.marginTop = 7;
                var vitals = Box(flight, "panel"); vitals.style.position = UnityEngine.UIElements.Position.Absolute;
                vitals.style.left = 22; vitals.style.bottom = 68; vitals.style.width = 280;
                var vitalHead = Box(vitals, "row spread"); Text(vitalHead, "SHIP INTEGRITY", "eyebrow");
                var defenses = ActionButton(vitalHead, "Details", () => Client.DefenseUi.Toggle());
                defenses.style.minHeight = 21; defenses.style.height = 23; defenses.style.fontSize = 11;
                MakeMovable(vitals, vitalHead, "integrity");
                shield = new Gauge(vitals, "Shield", Blue); reactor = new Gauge(vitals, "Reactor", Amber);
                hull = new Gauge(vitals, "Hull", new Color(.66f, .75f, .73f));
                var effectsHead = Text(vitals, "ACTIVE EFFECTS", "eyebrow"); effectsHead.style.marginTop = 8;
                buffs = Scroll(vitals); buffs.style.maxHeight = 126;

                var helm = Box(flight, "panel"); helm.style.position = UnityEngine.UIElements.Position.Absolute;
                helm.style.right = 22; helm.style.bottom = 16; helm.style.width = 265; helm.style.height = 136;
                var helmHead = Text(helm, "PROPULSION", "eyebrow"); MakeMovable(helm, helmHead, "propulsion");
                radar = new RadarDial(); helm.Add(radar); Place(radar, 147, 43, 96, 96); radar.pickingMode = PickingMode.Ignore;
                var speedRow = Box(helm, "row spread"); speedRow.style.width = 120; speedLabel = Text(speedRow, "0", "title"); Text(speedRow, "m / s", "subtle");
                modeLabel = Text(helm, "IMPULSE • STATIONARY", "subtle"); modeLabel.style.marginTop = 5; modeLabel.style.width = 125;

                weapons = Box(flight, "panel"); weapons.style.position = UnityEngine.UIElements.Position.Absolute;
                weapons.style.bottom = 130; weapons.style.width = 440; weapons.style.paddingTop = 9; weapons.style.paddingBottom = 10;
                var weaponHead = Box(weapons, "row spread"); Text(weaponHead, "HARDPOINTS", "eyebrow"); Text(weaponHead, "DRAG TO MOVE", "drag-label");
                MakeMovable(weapons, weaponHead, "hardpoints");
                var weaponRow = Box(weapons, "row"); weaponRow.name = "weapon-row";

                quickbar = Box(flight, "panel row"); quickbar.style.position = UnityEngine.UIElements.Position.Absolute;
                quickbar.style.width = 720; quickbar.style.height = 68; quickbar.style.bottom = 49;
                quickbar.style.paddingLeft = 9; quickbar.style.paddingTop = 8; quickbar.style.paddingBottom = 8;
                for (int i = 0; i < 6; i++)
                {
                    int index = i;
                    var slot = Box(quickbar, "quick-slot"); quickSlots[i] = slot;
                    quickKeys[i] = Text(slot, "", "quick-key");
                    quickIcons[i] = new Image { pickingMode = PickingMode.Ignore }; slot.Add(quickIcons[i]);
                    quickIcons[i].AddToClassList("quick-icon");
                    quickNames[i] = Text(slot, "", "quick-name");
                    slot.RegisterCallback<PointerDownEvent>(evt =>
                    { if (evt.button <= 1) { Client.AbilitiesUi.PressQuickSlot(index, evt.button == 1, Client.CurrentSnapshot); evt.StopPropagation(); } });
                }
                var quickHandle = Box(quickbar, "quick-drag"); quickHandle.tooltip = "Drag to move action bar";
                MakeMovable(quickbar, quickHandle, "quickbar");
                comms = Box(flight, "panel"); comms.style.position = UnityEngine.UIElements.Position.Absolute;
                comms.style.backgroundColor = new Color(.025f, .035f, .047f, .99f);
                comms.style.left = 22; comms.style.bottom = 16; comms.style.width = 420; comms.style.height = 38; comms.style.paddingTop = 3; comms.style.paddingBottom = 3;
                var commsHeader = Box(comms, "row spread");
                ActionButton(commsHeader, "Comms  + / −", () => hud.communicationsOpen = !hud.communicationsOpen).style.minHeight = 25;
                ActionButton(commsHeader, "Talk  /  Enter", () => Client.OpenChatEntry()).style.minHeight = 25;
                MakeMovable(comms, commsHeader, "communications");
                chatHistory = Scroll(comms); chatHistory.style.flexGrow = 1;
                messageLabel = Text(chatHistory, "", "subtle"); messageLabel.style.whiteSpace = WhiteSpace.NoWrap;
                Font chatFont = Resources.Load<Font>("Fonts/IBMPlexSans-Medium");
                messageLabel.style.unityFont = chatFont;
                messageLabel.style.fontSize = 17;
                messageLabel.style.color = new Color(.94f, .96f, .98f);
                messageLabel.style.overflow = Overflow.Hidden; messageLabel.style.textOverflow = TextOverflow.Ellipsis;
                chatComposer = Box(comms, "row"); chatComposer.style.marginTop = 8;
                chatChannel = ActionButton(chatComposer, "Sector", () =>
                { hud.groupChat = !hud.groupChat; chatInput.Focus(); });
                chatChannel.style.width = 75;
                chatInput = new TextField { name = "chat-entry", maxLength = 180 };
                chatInput.style.flexGrow = 1; chatInput.style.minWidth = 0;
                chatInput.style.marginLeft = 7; chatInput.style.marginRight = 7;
                chatInput.style.unityFont = chatFont;
                chatInput.style.fontSize = 18; chatInput.style.height = 42;
                chatInput.tooltip = "Enter sends the message. Escape closes chat.";
                VisualElement textInput = chatInput.Q(className: TextField.inputUssClassName);
                if (textInput != null)
                {
                    textInput.style.unityFont = chatFont;
                    textInput.style.fontSize = 18;
                    textInput.style.color = Color.white;
                    textInput.style.backgroundColor = new Color(.012f, .02f, .03f);
                    textInput.style.paddingLeft = 10; textInput.style.paddingRight = 10;
                    textInput.style.paddingTop = 6; textInput.style.paddingBottom = 6;
                    textInput.style.borderBottomColor = new Color(.43f, .72f, .84f);
                    textInput.style.borderBottomWidth = 2;
                }
                chatComposer.Add(chatInput);
                chatInput.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (evt.keyCode == KeyCode.UpArrow || evt.keyCode == KeyCode.DownArrow)
                    {
                        chatInput.value = evt.keyCode == KeyCode.UpArrow ?
                            hud.ChatHistory.Previous(chatInput.value) : hud.ChatHistory.Next(chatInput.value);
                        chatInput.SelectRange(chatInput.value.Length, chatInput.value.Length);
                        evt.StopPropagation(); evt.PreventDefault();
                    }
                    else if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                    {
                        if (Time.frameCount > Client.ChatOpenedFrame) SubmitChat();
                        evt.StopPropagation(); evt.PreventDefault();
                    }
                    else if (evt.keyCode == KeyCode.Escape)
                    { Client.CloseChatEntry(); evt.StopPropagation(); }
                }, TrickleDown.TrickleDown);
                var send = ActionButton(chatComposer, "Send", SubmitChat);
                send.name = "send-chat"; chatComposer.style.display = DisplayStyle.None;
                send.style.fontSize = 16; chatChannel.style.fontSize = 16;
                send.style.unityFont = chatFont; chatChannel.style.unityFont = chatFont;

                fitting = Box(root, "window"); fitting.name = "fitting-window"; fitting.style.width = 1180; fitting.style.height = 700;
                var header = Box(fitting, "window-header");
                var headerText = Box(header, "grow"); headerText.name = "fitting-handle"; Text(headerText, "SHIP CONFIGURATION", "eyebrow");
                Text(headerText, "Fitting & cargo", "title");
                fittingSubtitle = Text(header, "", "subtle"); fittingSubtitle.style.marginRight = 24;
                ActionButton(header, "Reset layout", ResetLayout).style.marginRight = 8;
                ActionButton(header, "Close  ×", () => hud.ToggleEquipment()); MakeMovable(fitting, headerText, "fitting");
                var content = Box(fitting, "window-content");
                cargoColumn = Box(content, "cargo-column");
                Text(cargoColumn, "CARGO HOLD", "eyebrow"); cargoCount = Text(cargoColumn, "", "readout");
                Text(cargoColumn, "Drag equipment onto a hardpoint", "subtle"); Rule(cargoColumn);
                cargoList = Scroll(cargoColumn); cargoList.style.flexGrow = 1;
                var middle = Box(content, "fitting-column");
                ring = Box(middle, ""); ring.style.width = 480; ring.style.height = 540;
                preview = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                ring.Add(preview); Place(preview, 73, 78, 334, 310);
                dial = new Dial(); ring.Add(dial); Place(dial, 0, 0, 480, 480); dial.pickingMode = PickingMode.Ignore;
                var previewLabel = Text(ring, "HULL ASSEMBLY", "eyebrow"); Place(previewLabel, 165, 327, 150, 20); previewLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                var core = Box(ring, "row"); Place(core, 124, 475, 234, 61);
                for (int slot = 0; slot < 3; slot++)
                {
                    SocketView socket = AddSocket(core, slot, new[] { "SHIELD", "REACTOR", "ENGINE" }[slot]);
                    socket.Root.style.position = UnityEngine.UIElements.Position.Relative;
                    socket.Root.style.marginRight = 10; socket.Root.style.width = 68; socket.Root.style.height = 61;
                }
                statsColumn = Box(content, "stats-column");
                Text(statsColumn, "PERFORMANCE", "eyebrow");
                fitShield = new Gauge(statsColumn, "Shield capacity", Blue);
                fitReactor = new Gauge(statsColumn, "Reactor capacity", Amber);
                statusLabel = Text(statsColumn, "", "subtle");
                var resistances = Box(statsColumn, "row spread"); resistances.style.marginTop = 12;
                string[] channels = { "IMP", "EXP", "PLS", "ENG", "EMP", "CHM", "PSI" };
                string[] channelNames = { "Impact", "Explosive", "Plasma", "Energy", "EMP", "Chemical", "Psionic" };
                for (int i = 0; i < 7; i++)
                {
                    var cell = Box(resistances, ""); cell.tooltip = channelNames[i] + " deflection";
                    Text(cell, channels[i], "socket-id");
                    deflectValues[i] = Text(cell, "—", "subtle"); deflectValues[i].style.fontSize = 11;
                }
                Rule(statsColumn);
                detail = Scroll(statsColumn); detail.style.flexGrow = 1;
                inspectorTitle = Text(detail, "Select a hardpoint", "title"); inspectorTitle.style.fontSize = 18; inspectorTitle.style.whiteSpace = WhiteSpace.Normal;
                inspectorText = Text(detail, "", "inspector");
                install = ActionButton(statsColumn, "Install selected cargo", InstallSelected, "primary"); install.style.marginTop = 8;
                remove = ActionButton(statsColumn, "Return to cargo", RemoveSelected); remove.style.marginTop = 6;
                activate = ActionButton(statsColumn, "Activate", ActivateSelected); activate.style.marginTop = 6;
                unload = ActionButton(statsColumn, "Unload magazine", () =>
                {
                    Snapshot current = Client.CurrentSnapshot;
                    ItemView ammo = Item(current, 3, hud.selectedMount);
                    int free = FirstFreeCargo(current);
                    if (ammo != null && free >= 0) Client.MoveInventory(new InventoryMove(8, hud.selectedMount, 1, free, ammo.Quantity));
                }); unload.style.marginTop = 6;
                var footer = Box(fitting, "window-footer"); Text(footer, "DRAG TO FIT  /  CLICK TO INSPECT", "drag-label");
                Text(footer, "I  CLOSE       •       DRAG HEADER TO MOVE", "drag-label");
                dragGhost = Box(root, "panel row"); dragGhost.style.position = UnityEngine.UIElements.Position.Absolute;
                dragGhost.pickingMode = PickingMode.Ignore; dragCaption = Text(dragGhost, "", "item-name");
                dragCaption.pickingMode = PickingMode.Ignore;
                dragGhost.style.display = DisplayStyle.None;
                root.RegisterCallback<GeometryChangedEvent>(_ => ClampMovedPanels());
                root.RegisterCallback<PointerMoveEvent>(MoveItem);
                root.RegisterCallback<PointerUpEvent>(DropItem);
                flight.Add(fitting);
                MakeCollapsible(scanner, scannerHead, "overview");
                MakeCollapsible(nav, navHead, "destinations");
                MakeCollapsible(targetPanel, targetHead, "target");
                MakeCollapsible(vitals, vitalHead, "integrity");
                MakeCollapsible(helm, helmHead, "propulsion");
                radar.style.top = 8;
                MakeCollapsible(weapons, weaponHead, "hardpoints");
                MakeCollapsible(fitting, header, "fitting");
                foreach (VisualElement panel in new[] { scanner, nav, targetPanel, vitals, helm, weapons, fitting })
                    panel.RegisterCallback<PointerDownEvent>(_ => panel.BringToFront(), TrickleDown.TrickleDown);
                inviteOverlay = Box(root, "invite-overlay"); Stretch(inviteOverlay);
                inviteOverlay.style.backgroundColor = new Color(.005f, .012f, .025f, .72f);
                inviteOverlay.style.alignItems = Align.Center;
                inviteOverlay.style.justifyContent = Justify.Center;
                var invitation = Box(inviteOverlay, "panel"); invitation.style.width = 470;
                Text(invitation, "CREW INVITATION", "eyebrow");
                Text(invitation, "Join this crew?", "title");
                invitationText = Text(invitation, "", "subtle"); invitationText.style.marginTop = 18;
                var responses = Box(invitation, "row"); responses.style.marginTop = 22;
                ActionButton(responses, "Decline", () => Client.AnswerConfirmation(false));
                var accept = ActionButton(responses, "Accept invitation", () => Client.AnswerConfirmation(true), "primary");
                accept.name = "accept-group-invite"; accept.style.marginLeft = 12;
                inviteOverlay.style.display = DisplayStyle.None;
            }

            /// <summary>Refresh measurements at ten hertz while keeping pointer interactions immediate.</summary>
            public void Update()
            {
                Snapshot s = Client.CurrentSnapshot;
                Visible = Client.Connected && !Client.ChoosingCharacter && s != null && !hud.settingsOpen &&
                    (!Client.ComponentMarketOpen || hud.GroupInviteOpen);
                root.style.display = Visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (!Visible) return;
                settings.scale = hud.Scale;
                scanner.style.display = hud.crewTab ? DisplayStyle.None : DisplayStyle.Flex;
                bool expandedComms = hud.communicationsOpen || Client.ChatOpen;
                comms.style.height = Client.ChatOpen ? 255 : hud.communicationsOpen ? 205 : 76;
                comms.style.width = expandedComms ? 620 : 440;
                if (!movedPanels.Contains(comms)) comms.style.left = s.Station == null ? 22 : 344;
                messageLabel.style.whiteSpace = expandedComms ? WhiteSpace.Normal : WhiteSpace.NoWrap;
                chatComposer.style.display = Client.ChatOpen ? DisplayStyle.Flex : DisplayStyle.None;
                chatChannel.text = hud.groupChat ? "Group" : "Sector";
                if (Client.ChatOpen && !chatWasOpen)
                {
                    comms.BringToFront();
                    chatInput.schedule.Execute(() => chatInput.Focus());
                    chatHistory.scrollOffset = new Vector2(0, float.MaxValue);
                }
                if (!Client.ChatOpen && chatWasOpen) chatInput.Blur();
                chatWasOpen = Client.ChatOpen;
                Vector2 canvasSize = new Vector2(root.resolvedStyle.width, root.resolvedStyle.height);
                if (canvasSize.x > 100 && canvasSize.y > 100 &&
                    (!positioned || Vector2.Distance(canvasSize, lastCanvasSize) > 1f))
                {
                    if (!movedPanels.Contains(fitting))
                    {
                        fitting.style.left = (canvasSize.x - 1180) / 2;
                        fitting.style.top = (canvasSize.y - 700) / 2;
                    }
                    if (!movedPanels.Contains(weapons))
                        weapons.style.left = (canvasSize.x - 440) / 2;
                    positioned = true; lastCanvasSize = canvasSize;
                }
                fitting.style.display = hud.equipmentOpen ? DisplayStyle.Flex : DisplayStyle.None;
                flight.style.display = DisplayStyle.Flex;
                foreach (VisualElement instrument in flight.Children().Where(child => child != fitting))
                    instrument.style.visibility = s.Station == null || instrument == comms ? Visibility.Visible : Visibility.Hidden;
                if (hud.equipmentOpen && !fittingWasOpen) fitting.BringToFront();
                fittingWasOpen = hud.equipmentOpen;
                // Modal legacy panels keep their own input and drawing order.
                root.style.visibility = !hud.GroupInviteOpen && (Client.ComponentMarketOpen || Client.AbilitiesOpen || Client.AbilitiesUi.VariantsOpen || s.Dialogue != null && s.Station == null) ? Visibility.Hidden : Visibility.Visible;
                inviteOverlay.style.display = hud.GroupInviteOpen ? DisplayStyle.Flex : DisplayStyle.None;
                if (hud.GroupInviteOpen)
                {
                    invitationText.text = s.Confirmation.Text;
                    inviteOverlay.BringToFront();
                }
                if (hud.equipmentOpen)
                {
                    if (hud.shipPreview == null) hud.shipPreview = new LoadoutShipPreview();
                    preview.image = hud.shipPreview.Render(Client.ShipForPreview);
                }
                if (!quickbarMoved) quickbar.style.left = (root.resolvedStyle.width - 720) / 2;
                quickbar.style.display = Client.AbilitiesUi.VariantsOpen ? DisplayStyle.None : DisplayStyle.Flex;
                if (Time.unscaledTime < nextRefresh) return;
                radar.Snapshot = s; radar.MarkDirtyRepaint();
                nextRefresh = Time.unscaledTime + .1f;
                sectorLabel.text = s.Sector;
                speedLabel.text = Math.Abs(s.Speed).ToString("N0");
                modeLabel.text = s.WarpPhase == WarpPhase.Idle ? "IMPULSE  /  " + (Math.Abs(s.Speed) < 1 ? "STATIONARY" : "UNDER WAY") : "WARP DRIVE  /  " + s.WarpPhase.ToString().ToUpperInvariant();
                shield.Set(s.Shield, s.Shield * s.MaxShield, s.MaxShield);
                reactor.Set(s.Energy, s.Energy * s.MaxEnergy, s.MaxEnergy);
                hull.Set(s.MaxHull > 0 ? s.Hull / s.MaxHull : 0, s.Hull, s.MaxHull);
                fitShield.Set(s.Shield, s.Shield * s.MaxShield, s.MaxShield);
                fitReactor.Set(s.Energy, s.Energy * s.MaxEnergy, s.MaxEnergy);
                statusLabel.text = $"Hull     {s.Hull:N0} / {s.MaxHull:N0}\nSpeed   {s.Speed:N0} m/s     •     Accel   {s.Acceleration:0} m/s²";
                for (int i = 0; i < deflectValues.Length; i++)
                    deflectValues[i].text = s.Defense.Known && i < s.Defense.Deflects.Count ? s.Defense.Deflects[i] + "%" : "—";
                cargoCount.text = $"{s.Cargo.Count(i => i != null):00} / {s.CargoCapacity:00} slots";
                fittingSubtitle.text = $"Hull stage {s.Experience?.HullStage ?? 0}   /   {s.WeaponMounts} weapons   /   {s.DeviceMounts} devices";
                messageLabel.text = expandedComms ? string.Join("\n", s.Messages.Reverse().Take(30).Reverse()) : s.Messages.LastOrDefault() ?? "Press Enter to talk";
                for (int i = 0; i < 6; i++)
                {
                    var slot = Client.AbilitiesUi.QuickSlotDisplay(i, s);
                    quickNames[i].text = slot.Name; quickKeys[i].text = slot.Key;
                    quickIcons[i].image = slot.Icon;
                    quickIcons[i].style.display = slot.Icon == null ? DisplayStyle.None : DisplayStyle.Flex;
                    quickNames[i].style.left = slot.Icon == null ? 10 : 40;
                    quickSlots[i].tooltip = slot.Name + " • right-click to choose rank";
                }
                RefreshInventory(s); RefreshContacts(s); RefreshTarget(s); RefreshBuffs(s); RefreshInspector(s);
            }

            /// Returns:
            ///     bool: Whether a visible retained control owns the current screen pointer.
            public bool ContainsPointer()
            {
                if (!Visible || root.panel == null || root.resolvedStyle.visibility != Visibility.Visible) return false;
                Vector2 point = RuntimePanelUtils.ScreenToPanel(root.panel,
                    new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
                VisualElement hit = root.panel.Pick(point);
                return hit != null && hit != root && hit != flight;
            }

            /// <summary>Rebuild item rows only when inventory content changes; update live socket readouts in place.</summary>
            private void RefreshInventory(Snapshot s)
            {
                if (sockets.Count > 3 && (s.WeaponMounts != weaponSlots.Count ||
                    sockets.Keys.Count(slot => slot >= 9) != Mathf.Min(6, (int)s.DeviceMounts)))
                {
                    foreach (int slot in sockets.Keys.Where(slot => slot >= 3).ToArray())
                    { sockets[slot].Root.RemoveFromHierarchy(); sockets.Remove(slot); }
                    foreach (WeaponView view in weaponSlots.Values) view.Root.RemoveFromHierarchy();
                    weaponSlots.Clear(); dropTargets.RemoveAll(t => t.Container == 2 && t.Slot >= 3);
                }
                int weaponsCount = Mathf.Min(6, (int)s.WeaponMounts), devicesCount = Mathf.Min(6, (int)s.DeviceMounts);
                for (int i = 0; i < weaponsCount; i++)
                {
                    int slot = i + 3;
                    if (!sockets.ContainsKey(slot))
                    {
                        SocketView socket = AddSocket(ring, slot, "W" + (i + 1));
                        float angle = Mathf.Lerp(210, 330, weaponsCount == 1 ? .5f : i / (float)(weaponsCount - 1)) * Mathf.Deg2Rad;
                        Place(socket.Root, 240 + Mathf.Cos(angle) * 191 - 31, 227 + Mathf.Sin(angle) * 191 - 33, 62, 66);
                    }
                    if (!weaponSlots.ContainsKey(slot))
                    {
                        var view = new WeaponView(weapons.Q("weapon-row"), hud.icons);
                        int capturedSlot = slot;
                        view.Root.RegisterCallback<ClickEvent>(_ => { hud.selectedMount = capturedSlot; hud.inspectMount = true; hud.equipmentOpen = true; });
                        weaponSlots.Add(slot, view);
                    }
                }
                for (int i = 0; i < devicesCount; i++)
                {
                    int slot = i + 9;
                    if (sockets.ContainsKey(slot)) continue;
                    SocketView socket = AddSocket(ring, slot, "D" + (i + 1));
                    float angle = Mathf.Lerp(30, 150, devicesCount == 1 ? .5f : i / (float)(devicesCount - 1)) * Mathf.Deg2Rad;
                    Place(socket.Root, 240 + Mathf.Cos(angle) * 191 - 31, 227 + Mathf.Sin(angle) * 191 - 33, 62, 66);
                }
                foreach (var pair in sockets)
                {
                    int slot = pair.Key; SocketView v = pair.Value;
                    bool unlocked = slot < 3 || slot < 9 && slot < 3 + weaponsCount || slot >= 9 && slot < 9 + devicesCount;
                    v.Root.style.display = unlocked ? DisplayStyle.Flex : DisplayStyle.None;
                    ItemView item = slot < s.Mounted.Length ? s.Mounted[slot] : null;
                    ItemView ammo = slot < s.Ammo.Length ? s.Ammo[slot] : null;
                    v.Icon.image = item == null ? hud.icons.ForType(slot == 0 ? 2u : slot == 1 ? 7u : slot == 2 ? 6u : slot < 9 ? 14u : 11u) : hud.icons.ForItem(item);
                    v.Icon.style.opacity = item == null ? .25f : 1;
                    v.Root.EnableInClassList("empty", item == null);
                    v.Root.EnableInClassList("chosen", hud.selectedMount == slot);
                    v.Root.EnableInClassList("drop-valid", draggingItem && FitsHardpoint(Item(s, itemContainer, itemSlot), slot, s));
                    v.State.text = item == null ? "+" : item.Type == 15 || item.Type == 16 ? (ammo?.Quantity ?? 0).ToString() : "ONLINE";
                    v.Root.tooltip = item == null ? "Empty hardpoint • drop equipment here" : item.Name + (ammo != null ? $"\nMagazine: {ammo.Quantity}/{ammo.MaxStack}" : "");
                }
                foreach (var pair in weaponSlots)
                {
                    pair.Value.Root.style.display = pair.Key < 3 + weaponsCount ? DisplayStyle.Flex : DisplayStyle.None;
                    pair.Value.Set(s, pair.Key);
                }
                string key = string.Join("|", s.Cargo.Select(i => i == null ? "-" : $"{i.Slot}:{i.Template}:{i.Quantity}:{i.Structure}")) + ":" + s.CargoCapacity;
                if (key == inventoryKey) return;
                inventoryKey = key;
                dropTargets.RemoveAll(t => t.Container == 1);
                cargoList.Clear();
                foreach (ItemView item in s.Cargo.Where(i => i != null).OrderBy(i => ItemCategory(i.Type)).ThenBy(i => i.Name))
                {
                    var row = Box(cargoList, "cargo-row");
                    var icon = new Image { image = hud.icons.ForItem(item), pickingMode = PickingMode.Ignore }; icon.style.width = 38; icon.style.height = 38; icon.style.marginRight = 7; row.Add(icon);
                    var text = Box(row, "grow"); text.style.flexShrink = 1;
                    Text(text, item.Name, "item-name"); Text(text, $"{ItemCategory(item.Type)}  ·  ×{item.Quantity}", "subtle");
                    row.name = "cargo-" + item.Slot; BindItem(row, 1, item.Slot); row.userData = item.Slot;
                }
                var drop = Box(cargoList, ""); drop.style.minHeight = 100; drop.style.justifyContent = Justify.Center;
                Text(drop, "↓  Return equipment to cargo", "subtle");
                dropTargets.Add((drop, 1, -1));
                dropTargets.Add((cargoColumn, 1, -1));
            }

            /// <summary>Keep the current item description and explicit actions readable at any name length.</summary>
            private void RefreshInspector(Snapshot s)
            {
                foreach (VisualElement row in cargoList.Children())
                    if (row.userData is int slot) row.EnableInClassList("chosen", hud.selectedCargo == slot && !hud.inspectMount);
                ItemView selectedCargo = Item(s, 1, hud.selectedCargo), mounted = Item(s, 2, hud.selectedMount);
                ItemView item = hud.inspectMount ? mounted : selectedCargo;
                inspectorTitle.text = item?.Name ?? (hud.inspectMount ? "Empty hardpoint" : "Inspect equipment");
                if (item == null) inspectorText.text = "Drag cargo onto a matching socket to equip it.\n\nDrag a fitted item anywhere into cargo to remove it.\n\nDrop ammunition onto a weapon to load. Drag its ammunition count back to cargo to unload.";
                else
                {
                    string effects = string.Join("\n\n", item.EquippedEffects.Select(e => e.Label + "\n" + e.Detail));
                    ItemView ammo = hud.inspectMount ? Item(s, 3, hud.selectedMount) : null;
                    inspectorText.text = $"{ItemCategory(item.Type)}\n\nQuality  {item.Quality * 100:0}%     Structure  {item.Structure * 100:0}%" +
                        (ammo == null ? "" : $"\nMagazine  {ammo.Quantity} / {ammo.MaxStack}") +
                        (item.Range > 0 ? $"\nRange  {item.Range:N0} m" : "") +
                        (item.Price > 0 ? $"\nValue  {item.Price:N0} credits" : "") +
                        (effects.Length > 0 ? "\n\n" + effects : "");
                }
                install.SetEnabled(Client.Ready && FitsHardpoint(selectedCargo, hud.selectedMount, s));
                install.text = selectedCargo?.Type == 10 ? "Load selected ammunition" : "Install selected cargo";
                remove.SetEnabled(Client.Ready && mounted != null && FirstFreeCargo(s) >= 0);
                bool usable = mounted != null && (mounted.Type >= 14 && mounted.Type <= 16 ||
                    DeviceEffects.MountedSlot(mounted.ActivationLabel, mounted.Type, hud.selectedMount, s.DeviceMounts));
                activate.SetEnabled(Client.Ready && !Client.Incapacitated && usable);
                unload.style.display = Item(s, 3, hud.selectedMount) != null ? DisplayStyle.Flex : DisplayStyle.None;
                unload.SetEnabled(Client.Ready && FirstFreeCargo(s) >= 0);
                activate.text = mounted?.Type >= 14 && mounted.Type <= 16 ? "Toggle weapon" : "Activate device";
            }

            /// <summary>Refresh sorted contact and destination rows without replacing focused controls every frame.</summary>
            private void RefreshContacts(Snapshot s)
            {
                ShipActor[] actors = s.Players.Concat(s.Mobs).Where(a => a.HasPosition).OrderBy(a => a.Position.Distance(s.Position)).Take(24).ToArray();
                string key = string.Join("|", actors.Select(a => $"{a.Id}:{a.Name}:{a.Position.Distance(s.Position) / 1000:0.0}:{a.Id == hud.contactId}"));
                if (key != contactKey)
                {
                    contactKey = key; contacts.Clear();
                    foreach (ShipActor actor in actors)
                    {
                        var row = Box(contacts, "contact"); row.EnableInClassList("chosen", actor.Id == hud.contactId);
                        Label symbol = Text(row, actor.IsPlayer ? "◇" : "⌖", "symbol"); symbol.style.color = actor.Hostile ? new Color(.89f, .42f, .32f) : Blue;
                        Text(row, actor.Name, "contact-name"); Text(row, $"{actor.Position.Distance(s.Position) / 1000:0.0} km", "range");
                        uint id = actor.Id; row.RegisterCallback<ClickEvent>(_ => hud.SelectContact(id));
                    }
                    if (actors.Length == 0) Text(contacts, "No contacts in sensor range", "subtle");
                }
                MapObject[] stations = s.Objects.Where(o => o.Kind == 12)
                    .OrderBy(o => o.Name).ToArray();
                MapObject[] objects = stations.Concat(s.Objects.Where(o => o.Kind != 12 &&
                    (o.Navigable || o.Kind == 37)).OrderBy(o => o.Kind == 11 ? 0 : 1).ThenBy(o => o.Name)).ToArray();
                string navKey = s.Sector + string.Join("|", objects.Select(o => $"{o.Id}:{o.Kind}:{o.Name}"));
                if (navKey != navigationKey)
                {
                    navigationKey = navKey; destinations.Clear(); destinationRanges.Clear(); destinationRows.Clear();
                    if (stations.Length > 0) Text(destinations, "STATIONS", "eyebrow");
                    foreach (MapObject item in objects)
                    {
                        if (stations.Length > 0 && objects.Length > stations.Length && item == objects[stations.Length])
                            Text(destinations, "NAVIGATION", "eyebrow");
                        var row = Box(destinations, "contact"); destinationRows[item.Id] = row;
                        Text(row, item.Kind == 12 ? "▣" : item.Kind == 11 ? "◇" : "·", "symbol");
                        Text(row, item.Name, "contact-name"); destinationRanges[item.Id] = Text(row, "", "range");
                        uint id = item.Id; row.RegisterCallback<ClickEvent>(_ => Client.Select(id));
                    }
                }
                foreach (MapObject item in objects)
                {
                    destinationRows[item.Id].EnableInClassList("chosen", Client.Target?.Id == item.Id);
                    destinationRanges[item.Id].text = $"{item.Position.Distance(s.Position) / 1000:0.0} km";
                }
            }

            /// <summary>Expose flight actions with the same native range and permission checks as the existing HUD.</summary>
            private void RefreshTarget(Snapshot s)
            {
                ShipActor actor = s.Players.Concat(s.Mobs).FirstOrDefault(a => a.Id == hud.contactId && a.HasPosition);
                MapObject obj = actor == null ? Client.Target : null;
                selectedActor = actor?.Id ?? 0;
                targetName.text = actor?.Name ?? obj?.Name ?? "Free flight";
                float distance = actor != null ? actor.Position.Distance(s.Position) : obj != null ? obj.Position.Distance(s.Position) : -1;
                targetMeta.text = (actor != null ? actor.IsPlayer ? "PILOT" : actor.Hostile ? "HOSTILE" : "NEUTRAL" : obj != null ? "DESTINATION" : "NO TARGET") +
                    (distance >= 0 ? $"  /  {distance / 1000:0.00} km" : "\nWarp along your heading");
                targetShield.Root.style.display = targetHull.Root.style.display = actor != null ? DisplayStyle.Flex : DisplayStyle.None;
                if (actor != null)
                {
                    targetShield.Set(actor.Shield, Mathf.Max(0, actor.Shield) * 100, 100);
                    targetHull.Set(actor.MaxHull > 0 ? actor.Hull / actor.MaxHull : 0, actor.Hull, actor.MaxHull);
                }
                travelActions.style.display = actor == null ? DisplayStyle.Flex : DisplayStyle.None;
                bool canJumpstart = actor?.IsPlayer == true && actor.MaxHull > 0 && actor.Hull <= 0 && s.AvailableAbilities.Contains(65u);
                jumpstart.style.display = canJumpstart ? DisplayStyle.Flex : DisplayStyle.None;
                jumpstart.SetEnabled(Client.Ready && !Client.Incapacitated && s.CastingAbilityId == uint.MaxValue && s.SelectedTargetId == selectedActor);
                fire.text = actor?.IsPlayer == true ? "Invite" : s.WeaponsFiring ? "Cease fire" : "Fire";
                fire.SetEnabled(Client.Ready && !Client.Incapacitated && actor != null && (!actor.IsPlayer ||
                    (!s.Group.Joined || s.Group.IsLeader) && !s.Group.Members.Any(m => m.Id == actor.Id) && s.Group.Members.Length < 6));
                follow.SetEnabled(Client.Ready && (actor != null || obj != null));
                bool active = s.WarpPhase == WarpPhase.Charging || s.WarpPhase == WarpPhase.Traveling;
                warp.text = active ? "Cancel warp" : "Warp";
                warp.SetEnabled(Client.Ready && !Client.Incapacitated && s.WarpPhase != WarpPhase.Recovering &&
                    (active || actor?.IsPlayer != true && (distance < 0 || distance >= 4000) && (obj?.Kind != 38 || distance <= 20000)));
                bool station = obj?.Kind == 12;
                dock.text = station ? "Dock" : "Enter gate";
                dock.SetEnabled(Client.Ready && !Client.Incapacitated && obj != null &&
                    (station || obj.Kind == 11) && s.Verbs.Available(obj.Id, station ? (ushort)8 : (ushort)10) == true);
            }

            /// <summary>Display server-owned effect leases and expire their rows with the next snapshot.</summary>
            private void RefreshBuffs(Snapshot s)
            {
                var entries = new List<(string Name, float Seconds)>();
                entries.AddRange(s.ActiveDeviceEffects.Select(e => (e.Name, e.RemainingSeconds)));
                entries.AddRange(s.ActiveBuffs.Where(e => !s.HasActiveDeviceSnapshot || e.Slot != 0)
                    .Select(e => (e.Slot == 13 && e.Name == "Maximize_Shields" ? "Shield Charging" : e.Name, e.RemainingSeconds)));
                if (LegacyDeviceFallback(s)) entries.Add((s.ActiveDeviceLabel, s.ActiveDeviceRemainingSeconds));
                string key = string.Join("|", entries.Select(e => e.Name + ":" + Mathf.CeilToInt(e.Seconds)));
                if (key == buffKey && buffs.childCount > 0) return;
                buffKey = key; buffs.Clear();
                if (entries.Count == 0) { Text(buffs, "No active effects", "subtle"); return; }
                if (hud.buffIcons == null) hud.buffIcons = Resources.Load<Texture2D>("BuffIcons");
                foreach (var entry in entries)
                {
                    var row = Box(buffs, "buff");
                    int index = BuffIconIndex(entry.Name);
                    if (index >= 0 && hud.buffIcons != null)
                    {
                        var icon = new Image { image = hud.buffIcons, uv = new Rect(index % 6 / 6f, (5 - index / 6) / 6f, 1f / 6, 1f / 6) };
                        icon.style.width = 25; icon.style.height = 25; icon.style.marginRight = 7; row.Add(icon);
                    }
                    Text(row, entry.Name.Replace('_', ' '), "grow subtle"); Text(row, $"{Mathf.CeilToInt(entry.Seconds)}s", "subtle");
                }
            }

            /// <summary>Send the typed sector or group message through the authoritative network worker.</summary>
            private void SubmitChat()
            {
                string text = chatInput.value.Trim();
                if (!Client.Ready || text.Length == 0) return;
                Client.SendChat(text, hud.groupChat);
                chatInput.value = ""; Client.CloseChatEntry();
            }

            /// <summary>Arm weapons or send a group invitation for the selected contact.</summary>
            private void FireTarget()
            {
                ShipActor actor = Client.CurrentSnapshot?.Players.Concat(Client.CurrentSnapshot.Mobs).FirstOrDefault(a => a.Id == selectedActor);
                if (actor == null) return;
                if (actor.IsPlayer) Client.InvitePlayer(actor.Id);
                else if (Client.CurrentSnapshot.WeaponsFiring) Client.StopWeapons();
                else Client.ToggleWeapons(actor.Id);
            }

            /// <summary>Install the selected cargo through the authoritative inventory worker.</summary>
            private void InstallSelected() { Client.EquipCargo(hud.selectedCargo, hud.selectedMount); }

            /// <summary>Remove selected equipment through the authoritative inventory worker.</summary>
            private void RemoveSelected() { Client.UnequipMount(hud.selectedMount); }

            /// <summary>Activate the selected native device, respecting peer and crew targeting.</summary>
            private void ActivateSelected()
            {
                Snapshot s = Client.CurrentSnapshot; ItemView item = Item(s, 2, hud.selectedMount);
                if (item == null) return;
                if (item.Type >= 14 && item.Type <= 16) { Client.ActivateWeapon(hud.selectedMount); return; }
                bool peer = DeviceEffects.RequiresPeer(item.ActivationLabel);
                if (item.TargetsPeer || peer)
                {
                    ShipActor recipient = s.Players.FirstOrDefault(a => a.Id == s.SelectedTargetId && a.HasPosition &&
                        (!item.TargetsPeer || s.Group.Members.Any(m => m.Id == a.Id)));
                    if (peer && recipient == null) return;
                    Client.ActivateDeviceFor(hud.selectedMount, recipient?.Id ?? s.PlayerId);
                }
                else Client.ActivateDevice(hud.selectedMount);
            }

            /// Returns:
            ///     SocketView: A persistent equipment socket with inspection and native drag handling.
            private SocketView AddSocket(VisualElement parent, int slot, string caption)
            {
                var socket = new SocketView(parent, caption);
                socket.Root.name = "mount-" + slot;
                BindItem(socket.Root, 2, slot);
                if (slot >= 3 && slot < 9)
                {
                    socket.State.name = "magazine-" + slot;
                    socket.State.pickingMode = PickingMode.Position;
                    socket.State.tooltip = "Drop ammunition here to load; drag the count to cargo to unload";
                    BindItem(socket.State, 3, slot);
                }
                sockets.Add(slot, socket); return socket;
            }

            /// <summary>Begin pointer capture for an inventory row and keep click inspection independent of dragging.</summary>
            private void BindItem(VisualElement element, uint container, int slot)
            {
                dropTargets.Add((element, container, slot));
                element.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    if (container == 3 && Item(Client.CurrentSnapshot, container, slot) == null) return;
                    if (container == 1) { hud.selectedCargo = slot; hud.inspectMount = false; }
                    else { hud.selectedMount = slot; hud.inspectMount = true; }
                    itemContainer = container; itemSlot = slot; itemDown = evt.position; itemSource = element;
                    draggingItem = false; root.CapturePointer(evt.pointerId); evt.StopPropagation();
                    RefreshInspector(Client.CurrentSnapshot);
                });
            }

            /// <summary>Show a dragged item only after a deliberate movement threshold.</summary>
            private void MoveItem(PointerMoveEvent evt)
            {
                if (itemSource == null || itemSlot < 0) return;
                ItemView item = Item(Client.CurrentSnapshot, itemContainer, itemSlot);
                if (item == null || !draggingItem && Vector2.Distance(itemDown, evt.position) < 7) return;
                draggingItem = true; dragGhost.style.display = DisplayStyle.Flex;
                dragGhost.style.left = evt.position.x + 16; dragGhost.style.top = evt.position.y + 12;
                dragGhost.BringToFront();
                dragCaption.text = item.Name + (itemContainer == 3 ? "  ·  UNLOAD" : "");
                evt.StopPropagation();
            }

            /// <summary>Submit a drop through existing inventory validation without editing local item state.</summary>
            private void DropItem(PointerUpEvent evt)
            {
                if (itemSource == null) return;
                if (draggingItem)
                {
                    Snapshot s = Client.CurrentSnapshot;
                    hud.draggedContainer = itemContainer; hud.draggedSlot = itemSlot;
                    // Walk the visible hit hierarchy so hidden or covered windows cannot take a drop.
                    // An occupied cargo row can fall through to the cargo area's first free slot.
                    VisualElement hit = root.panel.Pick(evt.position);
                    bool submitted = false;
                    for (VisualElement element = hit; element != null && !submitted; element = element.parent)
                    {
                        foreach (var target in dropTargets)
                        {
                            if (target.Element != element) continue;
                            int slot = target.Slot < 0 ? FirstFreeCargo(s) : target.Slot;
                            if (slot < 0 || !hud.CanInventoryDrop(s, target.Container, slot)) continue;
                            hud.SubmitInventoryDrop(s, target.Container, slot);
                            submitted = true; break;
                        }
                    }
                    hud.ClearInventoryDrag();
                }
                root.ReleasePointer(evt.pointerId); itemSource = null; itemSlot = -1;
                draggingItem = false; dragGhost.style.display = DisplayStyle.None; evt.StopPropagation();
            }

            /// Returns:
            ///     ItemView: The snapshot item at a native container slot, or null when absent.
            private static ItemView Item(Snapshot s, uint container, int slot)
            {
                if (s == null || slot < 0) return null;
                ItemView[] items = container == 1 ? s.Cargo : container == 2 ? s.Mounted : s.Ammo;
                return slot < items.Length ? items[slot] : null;
            }

            /// <summary>Keep a window's header visible while its saved collapse state hides the contents.</summary>
            private void MakeCollapsible(VisualElement panel, VisualElement header, string key)
            {
                panel.name = string.IsNullOrEmpty(panel.name) ? "panel-" + key : panel.name;
                if (header is Label)
                {
                    int index = panel.IndexOf(header);
                    var row = new VisualElement(); row.AddToClassList("row");
                    panel.Insert(index, row); row.Add(header); header.style.flexGrow = 1;
                    header = row;
                }
                var contents = new VisualElement { name = "content-" + key };
                contents.style.flexGrow = 1; contents.style.minHeight = 0;
                foreach (VisualElement child in panel.Children().Where(child => child != header).ToArray())
                    contents.Add(child);
                panel.Add(contents);
                StyleLength expandedHeight = panel.style.height;
                bool collapsed = PlayerPrefs.GetInt("42andbeyond.collapse." + key, 0) != 0;
                Button toggle = null;
                Action apply = () =>
                {
                    contents.style.display = collapsed ? DisplayStyle.None : DisplayStyle.Flex;
                    panel.style.height = collapsed ? new StyleLength(StyleKeyword.Auto) : expandedHeight;
                    toggle.text = collapsed ? "+" : "−";
                    toggle.tooltip = collapsed ? "Expand window" : "Collapse window";
                };
                toggle = ActionButton(header, "−", () =>
                {
                    collapsed = !collapsed;
                    PlayerPrefs.SetInt("42andbeyond.collapse." + key, collapsed ? 1 : 0);
                    PlayerPrefs.Save(); apply();
                });
                toggle.name = "collapse-" + key;
                toggle.style.minHeight = 22; toggle.style.height = 24;
                toggle.style.paddingLeft = 7; toggle.style.paddingRight = 7;
                toggle.style.marginLeft = 8;
                apply();
            }

            /// <summary>Move a window with its header, clamping the header within the viewport.</summary>
            private void MakeMovable(VisualElement panel, VisualElement handle, string key)
            {
                initialPositions[panel] = (panel.style.left, panel.style.top, panel.style.right, panel.style.bottom);
                handle.pickingMode = PickingMode.Position;
                bool moving = false; Vector2 start = default, origin = default;
                handle.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0 || evt.target is Button) return;
                    movedPanels.Add(panel);
                    if (panel == quickbar) quickbarMoved = true;
                    moving = true; start = evt.position; origin = new Vector2(panel.resolvedStyle.left, panel.resolvedStyle.top);
                    panel.style.left = origin.x; panel.style.top = origin.y;
                    panel.style.right = StyleKeyword.Auto; panel.style.bottom = StyleKeyword.Auto;
                    handle.CapturePointer(evt.pointerId); evt.StopPropagation();
                });
                handle.RegisterCallback<PointerMoveEvent>(evt =>
                {
                    if (!moving) return;
                    Vector2 point = origin + (Vector2)evt.position - start;
                    panel.style.left = Mathf.Clamp(point.x, 4, Mathf.Max(4, root.resolvedStyle.width - panel.resolvedStyle.width - 4));
                    panel.style.top = Mathf.Clamp(point.y, 4, Mathf.Max(4, root.resolvedStyle.height - panel.resolvedStyle.height - 4));
                    evt.StopPropagation();
                });
                handle.RegisterCallback<PointerUpEvent>(evt =>
                {
                    if (!moving) return; moving = false; handle.ReleasePointer(evt.pointerId); evt.StopPropagation();
                });
            }

            /// <summary>Restore the fitting window and allow responsive centering on the next frame.</summary>
            private void ResetLayout()
            {
                foreach (var entry in initialPositions)
                {
                    entry.Key.style.left = entry.Value.Left; entry.Key.style.top = entry.Value.Top;
                    entry.Key.style.right = entry.Value.Right; entry.Key.style.bottom = entry.Value.Bottom;
                }
                movedPanels.Clear(); positioned = false; quickbarMoved = false;
                quickbar.style.top = StyleKeyword.Auto; quickbar.style.bottom = 49;
                weapons.style.top = StyleKeyword.Auto; weapons.style.bottom = 130;
            }

            /// <summary>Keep moved windows reachable after the client window changes size.</summary>
            private void ClampMovedPanels()
            {
                if (!positioned) return;
                foreach (VisualElement panel in movedPanels.Concat(new[] { fitting }))
                {
                    if (panel.resolvedStyle.display == DisplayStyle.None) continue;
                    float x = Mathf.Clamp(panel.resolvedStyle.left, 4, Mathf.Max(4, root.resolvedStyle.width - panel.resolvedStyle.width - 4));
                    float y = Mathf.Clamp(panel.resolvedStyle.top, 4, Mathf.Max(4, root.resolvedStyle.height - panel.resolvedStyle.height - 4));
                    if (!float.IsNaN(x)) panel.style.left = x;
                    if (!float.IsNaN(y)) panel.style.top = y;
                }
            }

            /// Returns:
            ///     VisualElement: A child container with named presentation classes.
            private static VisualElement Box(VisualElement parent, string classes)
            {
                var element = new VisualElement();
                foreach (string value in classes.Split(' ')) if (value.Length > 0) element.AddToClassList(value);
                parent.Add(element); return element;
            }

            /// Returns:
            ///     Label: A text element which allows its parent to receive pointer events.
            private static Label Text(VisualElement parent, string text, string classes)
            {
                var label = new Label(text) { pickingMode = PickingMode.Ignore };
                foreach (string value in classes.Split(' ')) if (value.Length > 0) label.AddToClassList(value);
                parent.Add(label); return label;
            }

            /// Returns:
            ///     Button: An explicitly bound action styled by the shared flight theme.
            private static Button ActionButton(VisualElement parent, string text, Action action, string style = "")
            {
                var button = new Button(action) { text = text, focusable = false };
                if (style.Length > 0) button.AddToClassList(style);
                parent.Add(button); return button;
            }

            /// Returns:
            ///     ScrollView: A vertical viewport with horizontal overflow disabled.
            private static ScrollView Scroll(VisualElement parent)
            {
                var view = new ScrollView(ScrollViewMode.Vertical);
                view.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                view.style.minHeight = 0; view.style.flexShrink = 1;
                view.contentViewport.style.overflow = Overflow.Hidden;
                view.contentViewport.style.minHeight = 0;
                parent.Add(view); return view;
            }

            /// <summary>Place one visual element in its parent's coordinate space.</summary>
            private static void Place(VisualElement element, float x, float y, float width, float height)
            { element.style.position = UnityEngine.UIElements.Position.Absolute; element.style.left = x; element.style.top = y; element.style.width = width; element.style.height = height; }

            /// <summary>Extend a noninteractive container across its parent.</summary>
            private static void Stretch(VisualElement element)
            { element.style.position = UnityEngine.UIElements.Position.Absolute; element.style.left = element.style.top = element.style.right = element.style.bottom = 0; }

            /// <summary>Add a restrained separator between groups of information.</summary>
            private static void Rule(VisualElement parent) { Box(parent, "rule"); }

            /// <summary>Release only the UI objects owned by this client.</summary>
            public void Dispose() { Object.Destroy(gameObject); Object.Destroy(settings); }

            /// <summary>A cached numeric gauge with an independently updated fill.</summary>
            private sealed class Gauge
            {
                public readonly VisualElement Root;
                private readonly VisualElement fill;
                private readonly Label value;

                /// <summary>Create a labeled readout and a thin continuous value track.</summary>
                public Gauge(VisualElement parent, string title, Color color)
                {
                    Root = Box(parent, "metric"); var row = Box(Root, "row spread");
                    Text(row, title, "subtle"); value = Text(row, "", ""); value.style.fontSize = 13;
                    fill = Box(Box(Root, "track"), "fill"); fill.style.backgroundColor = color;
                }

                /// <summary>Display native absolute values and a safely bounded visual fraction.</summary>
                public void Set(float fraction, float current, float maximum)
                { value.text = maximum > 0 ? $"{Mathf.Max(0, current):N0} / {maximum:N0}" : "—"; fill.style.width = Length.Percent(Mathf.Clamp01(fraction) * 100); }
            }

            /// <summary>A large equipment socket with a readable native slot identity.</summary>
            private sealed class SocketView
            {
                public readonly VisualElement Root;
                public readonly Image Icon;
                public readonly Label State;

                /// <summary>Build the socket once and retain its image and state labels.</summary>
                public SocketView(VisualElement parent, string caption)
                {
                    Root = Box(parent, "socket"); Text(Root, caption, "socket-id");
                    Icon = new Image { pickingMode = PickingMode.Ignore }; Icon.style.width = 32; Icon.style.height = 32; Root.Add(Icon);
                    State = Text(Root, "", "socket-state");
                }
            }

            /// <summary>A compact live weapon socket displaying authoritative ammunition and reload state.</summary>
            private sealed class WeaponView
            {
                public readonly VisualElement Root;
                private readonly Label name, state;
                private readonly Image icon;
                private readonly EquipmentIcons icons;

                /// <summary>Create one clickable weapon tile without allocating textures per refresh.</summary>
                public WeaponView(VisualElement parent, EquipmentIcons icons)
                {
                    this.icons = icons; Root = Box(parent, "weapon"); name = Text(Root, "", "socket-id");
                    icon = new Image { pickingMode = PickingMode.Ignore }; icon.style.width = 30; icon.style.height = 30; Root.Add(icon);
                    state = Text(Root, "", "socket-state");
                }

                /// <summary>Show the weapon's native magazine and remaining cooldown.</summary>
                public void Set(Snapshot s, int slot)
                {
                    ItemView item = Item(s, 2, slot), ammo = Item(s, 3, slot);
                    name.text = "W" + (slot - 2); icon.image = item == null ? icons.ForType(14) : icons.ForItem(item); icon.style.opacity = item == null ? .25f : 1;
                    state.text = item == null ? "—" : item.Cooling ? $"{item.CooldownSeconds:0.0}s" :
                        item.Type == 15 || item.Type == 16 ? $"{ammo?.Quantity ?? 0}/{ammo?.MaxStack ?? 0}" : "READY";
                    Root.tooltip = item?.Name ?? "Empty weapon hardpoint";
                    Root.EnableInClassList("armed", item != null && (item.WeaponState & 1) != 0);
                }
            }

            /// <summary>A compact sensor projection sharing the flight snapshot.</summary>
            private sealed class RadarDial : VisualElement
            {
                public Snapshot Snapshot;
                /// <summary>Register the sensor's vector rendering callback.</summary>
                public RadarDial() { generateVisualContent += Draw; }
                /// <summary>Project nearby actors onto a fixed twenty-kilometer sensor disc.</summary>
                private void Draw(MeshGenerationContext context)
                {
                    var p = context.painter2D; Vector2 center = new Vector2(48, 48);
                    p.lineWidth = 1; p.strokeColor = new Color(.32f, .46f, .53f, .6f);
                    foreach (float radius in new[] { 15f, 30f, 45f })
                    { p.BeginPath(); p.Arc(center, radius, 0, 360); p.Stroke(); }
                    p.BeginPath(); p.MoveTo(new Vector2(3, 48)); p.LineTo(new Vector2(93, 48));
                    p.MoveTo(new Vector2(48, 3)); p.LineTo(new Vector2(48, 93)); p.Stroke();
                    p.fillColor = new Color(.85f, .89f, .88f); p.BeginPath(); p.Arc(center, 2, 0, 360); p.Fill();
                    if (Snapshot == null) return;
                    foreach (ShipActor actor in Snapshot.Players.Concat(Snapshot.Mobs).Where(a => a.HasPosition))
                    {
                        float dx = actor.Position.X - Snapshot.Position.X, dy = actor.Position.Y - Snapshot.Position.Y;
                        Vector2 position = new Vector2(dx, -dy) / 20000f * 45;
                        if (position.magnitude > 44) position = position.normalized * 44;
                        p.fillColor = actor.Hostile ? new Color(.9f, .44f, .32f) : Blue;
                        p.BeginPath(); p.Arc(center + position, 2, 0, 360); p.Fill();
                    }
                }
            }

            /// <summary>Resolution-independent instrument engraving around the fitting preview.</summary>
            private sealed class Dial : VisualElement
            {
                /// <summary>Register one vector drawing callback for this static fitting dial.</summary>
                public Dial() { generateVisualContent += Draw; }

                /// <summary>Draw restrained concentric arcs and calibrated ticks around the ship.</summary>
                private void Draw(MeshGenerationContext context)
                {
                    Painter2D p = context.painter2D; Vector2 center = new Vector2(240, 227);
                    p.lineWidth = 1; p.strokeColor = new Color(.29f, .35f, .40f, .7f);
                    foreach (float radius in new[] { 151f, 170f, 221f })
                    { p.BeginPath(); p.Arc(center, radius, 0, 360); p.Stroke(); }
                    for (int i = 0; i < 120; i++)
                    {
                        float a = i * Mathf.PI / 60; Vector2 direction = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                        p.strokeColor = i % 5 == 0 ? new Color(.48f, .53f, .55f, .8f) : new Color(.29f, .34f, .39f, .5f);
                        p.BeginPath(); p.MoveTo(center + direction * (i % 5 == 0 ? 211 : 216)); p.LineTo(center + direction * 221); p.Stroke();
                    }
                    p.strokeColor = new Color(.7f, .58f, .36f, .7f); p.lineWidth = 3;
                    p.BeginPath(); p.Arc(center, 155, 195, 345); p.Stroke();
                    p.strokeColor = new Color(.35f, .55f, .62f, .7f);
                    p.BeginPath(); p.Arc(center, 155, 15, 165); p.Stroke();
                }
            }
        }
    }
}
