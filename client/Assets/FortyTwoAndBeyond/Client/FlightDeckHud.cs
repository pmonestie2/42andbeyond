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
        internal bool SystemPickerOpen => deck?.InputModalOpen == true;
        internal Rect RetainedQuickbarRect => deck?.QuickbarBounds ?? Rect.zero;
        internal Rect[] RetainedFlightBounds => deck?.FlightBounds ?? Array.Empty<Rect>();

        /// <summary>Publish the current immutable snapshot to retained flight instruments.</summary>
        public void UpdateRetained()
        {
            RefreshWreckRequest();
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
                destinations = snapshot.Objects.Where(TacticalSectorMap.IsNavigationTarget)
                    .OrderBy(item => item.Position.Distance(snapshot.Position)).ToArray();
                nearbyWrecks = snapshot.Objects.Where(item => item.Kind == 25)
                    .OrderBy(item => item.Position.Distance(snapshot.Position)).Take(12).ToArray();
                socialContacts = snapshot.Players.Concat(snapshot.Mobs).Where(actor => actor.HasPosition)
                    .OrderBy(actor => actor.Position.Distance(snapshot.Position)).Take(40).ToArray();
                refreshAt = Time.unscaledTime + .25f; sector = snapshot.Sector;
            }
            TrackDamage(snapshot);
            if (client.Chart) DrawTacticalMap(snapshot);
            if (Event.current.type == EventType.Repaint && !equipmentOpen)
            {
                occupiedFlightLabels.Clear(); worldLabelHits.Clear();
                occupiedFlightLabels.AddRange(RetainedFlightBounds);
                if (client.Chart && tacticalMap != null) occupiedFlightLabels.Add(tacticalMap.WindowBounds);
                WorldLabels(snapshot);
                PlayerLabels(snapshot); WreckLabels(snapshot); DamageLabels(snapshot);
                DrawContactRim(snapshot);
            }
            if (!equipmentOpen)
            {
                if (!client.AbilitiesOpen && !client.Incapacitated) WreckPanel(snapshot);
                if (crewTab) Social(snapshot);

                InputFocused = crewTab && GUI.GetNameOfFocusedControl() == "Group invite";
                ChatEntry();
                if (snapshot.Dialogue != null) StationDialogue(snapshot);
                if (settingsOpen) Settings();
            }
        }

        /// <summary>Retained flight and fitting surfaces driven exclusively by server snapshots.</summary>
        private sealed partial class DeckView : IDisposable
        {
            private readonly FlightHud hud;
            private SpaceClient Client => hud.client;
            private readonly GameObject gameObject;
            private readonly PanelSettings settings;
            private readonly VisualElement root, flight, fitting, ring, cargoColumn, statsColumn, scanner, comms;
            private readonly VisualElement nearbyPane, navPane, flightStrip, effectsPanel;
            private readonly ScrollView quickbarDock;
            private readonly VisualElement compactSystemControls;
            private VisualElement propulsionSection;
            private float statusShare = PlayerPrefs.GetFloat("42andbeyond.strip.status", -1);
            private float speedShare = PlayerPrefs.GetFloat("42andbeyond.strip.speed", -1);
            private bool resizingStrip, effectsHovered, effectsPinned;
            private IVisualElementScheduledItem effectsHide;
            private Button effectsBadge;
            private Label activeEffectsTitle;
            private readonly Label zoomHint;
            private string renderedChat = "";
            private VisualElement systemHover;
            private Label systemHoverText;
            private int hoveredSystem = -1;
            private readonly Button[] browseTabs = new Button[3];
            private int browseTab;
            private bool joinedPreviously;
            private readonly Label sectorLabel, pilotLabel, speedLabel, modeLabel, statusLabel, cargoCount, cargoCredits, fittingSubtitle;
            private readonly Label targetName, targetMeta, inspectorTitle, inspectorText;
            private readonly Font chatFont;
            private readonly ScrollView contacts, destinations, cargoList, buffs;
            private readonly VisualElement itemHover;
            private readonly Label itemHoverTitle, itemHoverBody;
            private readonly VisualElement ammoPicker;
            private readonly ScrollView ammoChoices;
            private readonly Toggle ammoReload;
            private readonly Label ammoPickerStatus;
            private readonly Button ammoEditShortcut;
            private int ammoPickerMount = -1;
            private int ammoPickerShortcut = -1;
            private string ammoPickerKey = "";
            private readonly VisualElement fittingContent, fittingMiddle;
            private bool systemsEmbedded;
            private (StyleLength Left, StyleLength Top, StyleLength Right, StyleLength Bottom) compactSystems;
            private readonly VisualElement targetPanel, detail, dragGhost, quickbar;
            private readonly Label[] deflectValues = new Label[7];
            private readonly Label[] quickNames = new Label[AbilityHud.MaximumShortcuts], quickKeys = new Label[AbilityHud.MaximumShortcuts];
            private readonly Label[] quickStates = new Label[AbilityHud.MaximumShortcuts];
            private readonly Image[] quickIcons = new Image[AbilityHud.MaximumShortcuts];
            private readonly VisualElement[] quickSlots = new VisualElement[AbilityHud.MaximumShortcuts];
            private readonly VisualElement[] quickRecovery = new VisualElement[AbilityHud.MaximumShortcuts];
            private readonly VisualElement systemPicker;
            private readonly ScrollView systemPickerList;
            private readonly TextField systemSearch;
            private readonly Label systemPickerTitle;
            private string pickerSourceKey = "";
            private int systemPickerIndex = -1;
            public bool SystemPickerOpen => systemPickerIndex >= 0;
            public bool InputModalOpen => SystemPickerOpen || ammoPickerMount >= 0;
            private readonly Button[] contactCategoryButtons = new Button[Enum.GetValues(typeof(ContactCategoryFilter)).Length];
            private int shownContactFilter = -1;
            private readonly Label contactFilterCaption;
            private readonly Label dragCaption, dragStatus;
            private readonly Image dragIcon;
            private readonly Image preview;
            private readonly Dial dial;
            private readonly ResourceDialWidget reactor, defense, targetDefense;
            private readonly VisualElement crewPanel;
            private readonly Label groupEmpty;
            private readonly VisualElement gaugeHover;
            private readonly Label gaugeHoverText;
            private ResourceDialWidget hoveredGauge;
            private readonly Label performance;
            private readonly VisualElement mobHover;
            private readonly Label mobHoverName,mobHoverValues;
            private readonly ResourceDialWidget mobHoverDial;
            private uint mobHoverRow;
            private readonly ResourceDialWidget[] crewDials=new ResourceDialWidget[5];
            private readonly VisualElement[] crewCards=new VisualElement[5];
            private readonly Label[] crewNames=new Label[5],crewStatus=new Label[5];
            private readonly Button[] crewAssist=new Button[5];
            private readonly uint[] crewIds=new uint[5];
            private readonly VisualElement vitals, menuPanel;
            private bool menuOpen;
            private readonly Gauge fitShield, fitReactor;
            private readonly Button galaxy, fire, follow, trade, warp, dock, lootOpen, jumpstart, assistTarget;
            private readonly VisualElement travelActions, targetActions;
            private readonly VisualElement inviteOverlay;
            private readonly Label invitationText;
            private readonly VisualElement chatComposer;
            private readonly ScrollView chatHistory;
            private readonly TextField chatInput;
            private readonly DropdownField chatChannel;
            private bool commsManualSize, resizingComms;
            private Vector2 commsSize, commsResizeStart, commsResizeOrigin;
            private bool chatWasOpen;
            private readonly Dictionary<int, SocketView> sockets = new Dictionary<int, SocketView>();
            private readonly Dictionary<uint, Label> destinationRanges = new Dictionary<uint, Label>();
            private readonly Dictionary<uint, VisualElement> destinationRows = new Dictionary<uint, VisualElement>();
            private readonly List<(VisualElement Element, uint Container, int Slot)> dropTargets =
                new List<(VisualElement, uint, int)>();
            private readonly Dictionary<VisualElement, (StyleLength Left, StyleLength Top, StyleLength Right, StyleLength Bottom)> initialPositions =
                new Dictionary<VisualElement, (StyleLength, StyleLength, StyleLength, StyleLength)>();
            private readonly HashSet<VisualElement> movedPanels = new HashSet<VisualElement>();
            private string inventoryKey = "", contactKey = "", navigationKey = "", buffKey = "";
            private float nextRefresh;
            private Action expandFitting, expandBrowse;
            private string objectFilter = "All navigation", actorFilter = "All contacts";
            private bool positioned, draggingItem, quickbarMoved;
            private bool fittingWasOpen;
            private Vector2 lastCanvasSize;
            private Vector2 itemDown;
            private uint itemContainer;
            private int itemSlot = -1;
            private VisualElement itemSource;
            private VisualElement animatedTarget;
            private ItemView heldItem, previousDestination;
            private int itemPointer = -1, pendingSlot;
            private uint pendingContainer;
            private string dragPhase = "idle", pendingMessage;
            private Vector2 ghostPosition, animationFrom, animationTo, sourcePosition;
            private float animationStarted, pendingStarted;
            private bool releasingPointer;
            private uint selectedActor;
            public bool Visible { get; private set; }
            public Rect QuickbarBounds => quickbar.worldBound;
            /// Returns:
            ///     Rect[]: Retained flight surfaces that should exclude projected world labels.
            public Rect[] FlightBounds => lootPanel.resolvedStyle.display == DisplayStyle.None ?
                new[] { scanner.worldBound, comms.worldBound, targetPanel.worldBound, flightStrip.worldBound } :
                new[] { scanner.worldBound, comms.worldBound, targetPanel.worldBound, flightStrip.worldBound, lootPanel.worldBound };
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
                scanner = Box(flight, "panel browse-panel"); Place(scanner, 20, 76, 390, 260);
                scanner.name = "panel-browse";
                var scannerHead = Box(scanner, "row browse-tabs");
                string[] tabNames = { "Nearby", "Destinations", "Group" };
                for (int index = 0; index < tabNames.Length; index++)
                {
                    int tab = index;
                    browseTabs[index] = ActionButton(scannerHead, tabNames[index], () => SetBrowseTab(tab));
                    browseTabs[index].name = "browse-tab-" + index;
                }
                nearbyPane = Box(scanner, "browse-pane");
                var contactFilter = new DropdownField(new List<string> { "All contacts", "Players", "Hostiles", "Neutral mobs" }, 0)
                { name = "contact-selection-filter" };
                contactFilter.RegisterValueChangedCallback(evt =>
                { actorFilter = evt.newValue; hud.SelectedContactFilter = ContactCategoryFilter.All; contactKey = null; });
                contactFilter.AddToClassList("selection-filter");
                nearbyPane.Add(contactFilter); contactFilter.style.display = DisplayStyle.None;
                var categories = Box(nearbyPane, "row contact-category-filters");
                string[] shortNames = { "All", "Fight", "Loot", "Mine", "Nav", "Other" };
                string[] fullNames = { "All selectable contacts", "Combat · hostile ships", "Loot · wrecks and corpses", "Mining · resources", "Travel · gates, stations and navpoints", "Non-enemies · pilots, wrecks, resources and navigation" };
                for (int index = 0; index < shortNames.Length; index++)
                {
                    int choice = index;
                    Button pill = ActionButton(categories, shortNames[index], () =>
                    {
                        actorFilter = "All contacts"; hud.SelectedContactFilter = (ContactCategoryFilter)choice;
                        contactFilterCaption.text = fullNames[choice]; contactKey = null;
                    });
                    contactCategoryButtons[index] = pill; pill.name = "contact-category-" + index;
                    pill.style.width = index == 0 ? 36 : index == 1 ? 46 : 42;
                    pill.style.minWidth = pill.style.width; pill.style.flexShrink = 0;
                    pill.style.minHeight = 25; pill.style.height = 25; pill.style.fontSize = 12;
                    pill.style.paddingLeft = pill.style.paddingRight = 3;
                    pill.style.paddingTop = pill.style.paddingBottom = 1; pill.style.marginRight = 4;
                    pill.tooltip = fullNames[index];
                    pill.RegisterCallback<MouseEnterEvent>(_ => contactFilterCaption.text = fullNames[choice]);
                    pill.RegisterCallback<MouseLeaveEvent>(_ => contactFilterCaption.text = fullNames[(int)hud.SelectedContactFilter]);
                }
                contactFilterCaption = Text(nearbyPane, "All selectable contacts", "subtle");
                contactFilterCaption.style.fontSize = 11; contactFilterCaption.style.height = 18;
                contactFilterCaption.style.whiteSpace = WhiteSpace.NoWrap;
                contactFilterCaption.style.overflow = Overflow.Hidden; contactFilterCaption.style.textOverflow = TextOverflow.Ellipsis;
                contacts = Scroll(nearbyPane); contacts.style.flexGrow = 1;
                MakeMovable(scanner, scannerHead, "overview");

                navPane = Box(scanner, "browse-pane"); navPane.name = "panel-destinations";
                var selectionFilter = new DropdownField(new List<string> { "All navigation", "Gates", "Stations", "Navpoints" }, 0)
                { name = "object-selection-filter" };
                selectionFilter.RegisterValueChangedCallback(evt => { objectFilter = evt.newValue; navigationKey = ""; });
                selectionFilter.AddToClassList("selection-filter");
                navPane.Add(selectionFilter);
                destinations = Scroll(navPane); destinations.style.flexGrow = 1;
                ActionButton(navPane, "Free flight", () => Client.SelectFreeFlight()).style.marginTop = 6;

                targetPanel = Box(flight, "panel"); targetPanel.style.position = UnityEngine.UIElements.Position.Absolute;
                targetPanel.style.right = 20; targetPanel.style.top = 76; targetPanel.style.width = 320;
                var targetHead = Text(targetPanel, "SELECTED TARGET", "eyebrow"); MakeMovable(targetPanel, targetHead, "target");
                targetName = Text(targetPanel, "No target", "title"); targetName.style.fontSize = 18; targetName.style.whiteSpace = WhiteSpace.Normal;
                targetMeta = Text(targetPanel, "Choose an object in space", "subtle"); targetMeta.style.marginTop = 6;
                targetDefense=new ResourceDialWidget(ResourceDialKind.Defense,88);targetDefense.name="target-defense";
                targetDefense.style.alignSelf=Align.Center;targetDefense.style.marginBottom=6;targetPanel.Add(targetDefense);
                targetActions = Box(targetPanel, "row");
                fire = ActionButton(targetActions, "Fire", FireTarget, "primary"); fire.style.flexGrow = 1;
                follow = ActionButton(targetActions, "Follow", () => Client.FollowSelected()); follow.style.marginLeft = 7; follow.style.flexGrow = 1;
                trade = ActionButton(targetActions, "Trade", () => Client.OpenTrade(selectedActor));
                trade.style.marginLeft = 7; trade.style.flexGrow = 1;
                assistTarget = ActionButton(targetPanel, "Assist · acquire target", () =>
                {
                    ShipActor pilot = Client.CurrentSnapshot?.Players.Concat(Client.CurrentSnapshot.Mobs).FirstOrDefault(actor =>
                        actor.Id == selectedActor && actor.HasPosition);
                    if (pilot != null) Client.SendChat("/assist #" + pilot.Id);
                }, "primary");
                assistTarget.name = "target-assist";
                assistTarget.tooltip = "Acquire this contact's server-confirmed target";
                assistTarget.style.marginTop = 7;
                travelActions = Box(targetPanel, "row"); travelActions.style.marginTop = 7;
                warp = ActionButton(travelActions, "Warp", () => Client.WarpSelected()); warp.style.flexGrow = 1;
                dock = ActionButton(travelActions, "Dock", () => { if (Client.Target?.Kind == 12) Client.DockSelected(); else Client.GateSelected(); });
                dock.style.marginLeft = 7; dock.style.flexGrow = 1;
                lootOpen = ActionButton(targetPanel, "Open wreck", () =>
                {
                    uint id = Client.Target?.Id ?? 0;
                    if (id == 0) return;
                    if (Client.CurrentSnapshot?.OpenLootId == id) Client.CloseLoot(id);
                    else Client.OpenLoot(id);
                });
                lootOpen.name = "open-wreck-inventory"; lootOpen.style.marginTop = 7;

                jumpstart = ActionButton(targetPanel, "Jumpstart pilot", () => Client.ActivateAbility(65u), "primary");
                jumpstart.style.marginTop = 7;
                flightStrip = Box(flight, "panel flight-strip"); flightStrip.name = "flight-strip";
                flightStrip.style.position = UnityEngine.UIElements.Position.Absolute;
                flightStrip.style.bottom = 12; flightStrip.style.height = 172;
                flightStrip.style.flexDirection = FlexDirection.Row;
                flightStrip.style.alignItems = Align.Center;
                flightStrip.style.paddingRight = 103;
                vitals = Box(flightStrip, "strip-section"); vitals.name = "panel-integrity";
                vitals.style.position = UnityEngine.UIElements.Position.Relative;
                vitals.style.width = 420; vitals.style.height = 144;
                var vitalHead = Box(vitals, "row spread"); Text(vitalHead, "FLIGHT DECK", "eyebrow").style.display = DisplayStyle.None;
                var menuButton = ActionButton(vitalHead, "Menu ▾", () =>
                {
                    menuOpen = !menuOpen;
                    menuPanel.style.display = menuOpen ? DisplayStyle.Flex : DisplayStyle.None;
                    if (menuOpen) menuPanel.BringToFront();
                });
                menuButton.name = "flight-menu"; menuButton.style.minHeight = 22; menuButton.style.height = 23;
                MakeMovable(flightStrip, vitalHead, "flight-strip");
                pilotLabel = Text(vitals, "Pilot", "title"); pilotLabel.name = "flight-pilot-name";
                pilotLabel.style.fontSize = 16; pilotLabel.style.marginTop = 5;
                pilotLabel.style.position = UnityEngine.UIElements.Position.Absolute;
                pilotLabel.style.left = 0; pilotLabel.style.top = 21; pilotLabel.style.width = 145;
                sectorLabel = Text(vitals, "", "subtle"); sectorLabel.name = "flight-sector";
                sectorLabel.style.fontSize = 12; sectorLabel.style.marginBottom = 5;
                sectorLabel.style.position = UnityEngine.UIElements.Position.Absolute;
                sectorLabel.style.left = 0; sectorLabel.style.top = 51; sectorLabel.style.width = 145;
                pilotLabel.tooltip = "Click or press ` to target your ship";
                pilotLabel.RegisterCallback<ClickEvent>(evt => { Client.SelectAbilitySelf(); evt.StopPropagation(); });
                var targetSelf = ActionButton(vitals, "Target self", () => Client.SelectAbilitySelf());
                targetSelf.name = "flight-target-self"; targetSelf.tooltip = "` · Select your ship for shields, repairs and support abilities";
                targetSelf.style.position = UnityEngine.UIElements.Position.Absolute;
                targetSelf.style.left = 0; targetSelf.style.top = 73;
                targetSelf.style.width = 90; targetSelf.style.height = 24; targetSelf.style.minHeight = 24;
                var instruments=Box(vitals,"");instruments.name="resource-dial-assembly";
                instruments.style.position = UnityEngine.UIElements.Position.Absolute;
                instruments.style.left = 155; instruments.style.top = 0;
                instruments.style.width=216;instruments.style.height=142;instruments.style.flexShrink=0;
                reactor=new ResourceDialWidget(ResourceDialKind.Reactor,80);instruments.Add(reactor);Place(reactor,4,0,80,80);
                defense=new ResourceDialWidget(ResourceDialKind.Defense,80);instruments.Add(defense);Place(defense,110,0,80,80);
                BuildExperienceTracks(instruments);
                var effectsHead = Text(vitals, "ACTIVE EFFECTS", "eyebrow"); activeEffectsTitle = effectsHead; effectsHead.style.marginTop = 8;
                effectsPanel = Box(flight, "panel"); effectsPanel.name = "flight-effects";
                effectsPanel.style.position = UnityEngine.UIElements.Position.Absolute;
                effectsPanel.style.width = 260; effectsPanel.style.maxHeight = 260;
                effectsPanel.style.display = DisplayStyle.None;
                effectsHead.RemoveFromHierarchy(); effectsPanel.Add(effectsHead);
                MakeMovable(effectsPanel, effectsHead, "effects");

                effectsBadge = ActionButton(vitals, "Effects · 0", () =>
                { effectsPinned = !effectsPinned; UpdateEffectsVisibility(); });
                effectsBadge.name = "flight-effects-badge";
                effectsBadge.AddToClassList("effects-badge");
                effectsBadge.tooltip = "Hover to see active effects and timers. Click to pin or unpin the list.";
                effectsBadge.style.position = UnityEngine.UIElements.Position.Absolute;
                effectsBadge.style.left = 0; effectsBadge.style.top = 108;
                foreach (VisualElement surface in new VisualElement[] { effectsBadge, effectsPanel })
                {
                    surface.RegisterCallback<MouseEnterEvent>(_ =>
                    { effectsHide?.Pause(); effectsHovered = true; UpdateEffectsVisibility(); });
                    surface.RegisterCallback<MouseLeaveEvent>(_ =>
                    {
                        effectsHide?.Pause();
                        effectsHide = root.schedule.Execute(() =>
                        { effectsHovered = false; UpdateEffectsVisibility(); }).StartingIn(200);
                    });
                }
                buffs = Scroll(effectsPanel); buffs.style.maxHeight = 205;
                menuPanel = Box(flight, "panel"); menuPanel.name = "flight-menu-options";
                menuPanel.style.position = UnityEngine.UIElements.Position.Absolute;
                menuPanel.style.left = 310; menuPanel.style.top = 260; menuPanel.style.width = 188;
                menuPanel.style.display = DisplayStyle.None;
                Text(menuPanel, "INTERFACE ZOOM", "eyebrow");
                var zoomChoices = new List<string> { "85%", "100%", "125%", "150%", "175%", "200%" };
                string zoomValue = Mathf.RoundToInt(hud.InterfaceZoom * 100) + "%";
                if (!zoomChoices.Contains(zoomValue)) zoomChoices.Add(zoomValue);
                var zoomMenu = new DropdownField(zoomChoices, zoomChoices.IndexOf(zoomValue)) { name = "interface-zoom" };
                zoomMenu.style.minHeight = 28; menuPanel.Add(zoomMenu);
                zoomMenu.RegisterValueChangedCallback(evt =>
                {
                    if (int.TryParse(evt.newValue.TrimEnd('%'), out int percent)) hud.SetInterfaceZoom(percent / 100f);
                });
                zoomHint = Text(menuPanel, "Scales text and buttons", "subtle");
                zoomHint.style.fontSize = 10; zoomHint.style.whiteSpace = WhiteSpace.Normal;
                zoomHint.style.marginBottom = 7;
                AddFlightMenuAction("Systems / I", () => hud.ToggleEquipment());
                AddFlightMenuAction("Items", () => Client.OpenItemBrowser());
                AddFlightMenuAction("Missions", () => Client.MissionsUi.Toggle());
                AddFlightMenuAction("Skills", () => Client.AbilitiesUi.Toggle());
                AddFlightMenuAction("Group / Roster", OpenGroupRoster);
                AddFlightMenuAction("Defense", () => Client.DefenseUi.Toggle());
                AddFlightMenuAction("Active Effects", () =>
                { effectsPinned = !effectsPinned; UpdateEffectsVisibility(); });
                AddFlightMenuAction("Add shortcut", () => Client.AbilitiesUi.AddShortcut(Client.CurrentSnapshot));
                AddFlightMenuAction("Remove shortcut", () => Client.AbilitiesUi.RemoveShortcut(
                    Client.AbilitiesUi.ShortcutCount(Client.CurrentSnapshot) - 1, Client.CurrentSnapshot));
                galaxy = AddFlightMenuAction("Galaxy", () => Client.OpenGalaxyTravel());
                AddFlightMenuAction("Chart / Tab", () => Client.SetChart(!Client.Chart));
                AddFlightMenuAction("Controls", () => hud.settingsOpen = true);
                AddFlightMenuAction("New client", () => Client.OpenAnotherClient());
                AddFlightMenuAction("Disconnect", () => Client.Disconnect());

                crewPanel=Box(scanner,"browse-pane");crewPanel.name="panel-crew";
                groupEmpty=Text(crewPanel,"No wing pilots yet", "subtle");
                var crewRow=Scroll(crewPanel);crewRow.name="group-member-list";
                crewRow.style.flexGrow=1;
                for(int index=0;index<5;index++)
                {
                    int slot=index;var card=Box(crewRow,"");crewCards[index]=card;card.name="crew-member-"+index;
                    card.AddToClassList("group-member-row");card.pickingMode=PickingMode.Position;
                    crewNames[index]=Text(card,"","subtle");crewNames[index].style.fontSize=14;
                    crewNames[index].style.position=UnityEngine.UIElements.Position.Absolute;
                    crewNames[index].style.left=12;crewNames[index].style.top=8;
                    crewNames[index].style.height=19;crewNames[index].style.width=144;
                    crewNames[index].style.whiteSpace=WhiteSpace.NoWrap;crewNames[index].style.overflow=Overflow.Hidden;crewNames[index].style.textOverflow=TextOverflow.Ellipsis;
                    crewAssist[index]=ActionButton(card,"Assist",()=>
                    {
                        Snapshot current=Client.CurrentSnapshot;
                        GroupMember member=current?.Group.Members.FirstOrDefault(candidate=>candidate.Id==crewIds[slot]);
                        if(member!=null)Client.SendChat("/assist "+member.Name);
                    });
                    crewAssist[index].name="crew-assist-"+index;
                    crewAssist[index].tooltip="Acquire this crew pilot's server-selected target";
                    crewAssist[index].style.position=UnityEngine.UIElements.Position.Absolute;
                    crewAssist[index].style.right=8;crewAssist[index].style.top=19;
                    crewAssist[index].style.width=62;crewAssist[index].style.minHeight=26;
                    crewAssist[index].style.height=26;crewAssist[index].style.paddingLeft=0;
                    crewAssist[index].style.paddingRight=0;crewAssist[index].style.fontSize=10;
                    crewDials[index]=new ResourceDialWidget(ResourceDialKind.Defense,50);crewDials[index].name="crew-defense-"+index;card.Add(crewDials[index]);
                    crewDials[index].style.position=UnityEngine.UIElements.Position.Absolute;
                    crewDials[index].style.right=78;crewDials[index].style.top=8;
                    crewStatus[index]=Text(card,"","subtle");crewStatus[index].style.fontSize=11;crewStatus[index].style.height=16;
                    crewStatus[index].style.position=UnityEngine.UIElements.Position.Absolute;
                    crewStatus[index].style.left=12;crewStatus[index].style.bottom=8;
                    card.RegisterCallback<PointerDownEvent>(evt=>
                    {
                        if (evt.target is Button) return;
                        if(evt.button==0&&crewIds[slot]!=0&&Client.CurrentSnapshot.Players.Any(actor=>actor.Id==crewIds[slot]&&actor.HasPosition))
                        {Client.SelectContact(crewIds[slot]);evt.StopPropagation();}
                    });
                }
                var manageGroup = ActionButton(crewPanel,"Manage group  ›", OpenGroupRoster);
                manageGroup.name="group-roster";manageGroup.AddToClassList("group-manage");
                crewPanel.style.display=DisplayStyle.None;

                AddStripDivider(0);
                var helm = Box(flightStrip, "strip-section"); propulsionSection = helm; helm.name = "panel-propulsion";
                helm.style.width = 145; helm.style.height = 104;
                var helmHead = Text(helm, "PROPULSION", "eyebrow");
                var speedRow = Box(helm, "row spread"); speedRow.style.width = 120; speedRow.style.marginTop = 10;
                speedLabel = Text(speedRow, "0", "title"); speedLabel.style.fontSize = 19;
                Text(speedRow, "m/s", "subtle");
                modeLabel = Text(helm, "IMPULSE • STATIONARY", "subtle"); modeLabel.style.marginTop = 6;
                modeLabel.style.fontSize = 10; modeLabel.style.width = 125;

                AddStripDivider(1);
                quickbarDock = new ScrollView(ScrollViewMode.Horizontal) { name = "shortcut-viewport" };
                quickbarDock.style.flexGrow = 1; quickbarDock.style.minWidth = 0;
                quickbarDock.style.height = 108; quickbarDock.verticalScrollerVisibility = ScrollerVisibility.Hidden;
                flightStrip.Add(quickbarDock);
                quickbar = Box(quickbarDock, "panel shortcut-compact"); quickbar.name = "shortcut-bar";
                quickbar.style.position = UnityEngine.UIElements.Position.Relative;
                quickbar.style.width = 720; quickbar.style.height = 96;
                quickbar.style.paddingLeft = 9; quickbar.style.paddingTop = 8; quickbar.style.paddingBottom = 8;
                for (int i = 0; i < AbilityHud.MaximumShortcuts; i++)
                {
                    int index = i;
                    var slot = Box(quickbar, "quick-slot"); quickSlots[i] = slot;
                    slot.name = "shortcut-" + i; slot.style.position = UnityEngine.UIElements.Position.Absolute;
                    slot.style.left = 9 + i % 6 * 118; slot.style.top = 32 + i / 6 * 60;
                    quickKeys[i] = Text(slot, "", "quick-key");
                    quickIcons[i] = new Image { pickingMode = PickingMode.Ignore }; slot.Add(quickIcons[i]);
                    quickIcons[i].AddToClassList("quick-icon");
                    quickNames[i] = Text(slot, "", "quick-name");
                    quickStates[i] = Text(slot, "", "quick-state");
                    var mask = Box(slot, "system-recovery"); quickRecovery[i] = mask;
                    mask.pickingMode = PickingMode.Ignore; mask.style.display = DisplayStyle.None;
                    quickStates[i].BringToFront(); quickKeys[i].BringToFront();
                    slot.RegisterCallback<MouseEnterEvent>(_ => ShowSystemHover(index));
                    slot.RegisterCallback<MouseLeaveEvent>(_ =>
                    { if (hoveredSystem == index) { hoveredSystem = -1; systemHover.style.display = DisplayStyle.None; } });
                    slot.RegisterCallback<PointerDownEvent>(evt =>
                    {
                        hoveredSystem = -1; systemHover.style.display = DisplayStyle.None;
                        if (evt.button == 1)
                        {
                            int mount = Client.AbilitiesUi.BoundLauncherMount(index, Client.CurrentSnapshot);
                            if (mount >= 0)
                            { ammoPickerShortcut = index; OpenAmmoPicker(mount, evt.position); }
                            else ShowSystemPicker(index, evt.position);
                        }
                        else if (evt.button == 0)
                        {
                            if (!Client.AbilitiesUi.HasAssignedSystem(index, Client.CurrentSnapshot)) ShowSystemPicker(index, evt.position);
                            else Client.AbilitiesUi.PressQuickSlot(index, false, Client.CurrentSnapshot);
                        }
                        else return;
                        evt.StopPropagation();
                    });
                }
                compactSystemControls = Box(flightStrip, "row"); compactSystemControls.name = "compact-systems-controls";
                compactSystemControls.style.position = UnityEngine.UIElements.Position.Absolute;
                compactSystemControls.style.top = 4; compactSystemControls.style.right = 13;
                compactSystemControls.style.height = 22;
                var compactRemove = ActionButton(compactSystemControls, "−", () =>
                    Client.AbilitiesUi.RemoveShortcut(Client.AbilitiesUi.ShortcutCount(Client.CurrentSnapshot) - 1, Client.CurrentSnapshot));
                compactRemove.name = "remove-shortcut-compact"; compactRemove.tooltip = "Remove last Systems shortcut";
                var compactAdd = ActionButton(compactSystemControls, "+", () => Client.AbilitiesUi.AddShortcut(Client.CurrentSnapshot));
                compactAdd.name = "add-shortcut-compact"; compactAdd.tooltip = "Add Systems shortcut (up to 24)";
                foreach (Button control in new[] { compactRemove, compactAdd })
                {
                    control.style.width = 35; control.style.minWidth = 35;
                    control.style.height = 22; control.style.minHeight = 22;
                    control.style.paddingLeft = control.style.paddingRight = 0;
                    control.style.paddingTop = control.style.paddingBottom = 0;
                    control.style.fontSize = 17; control.style.marginLeft = 3;
                }
                systemHover = Box(root, "panel"); systemHover.name = "systems-hover-overlay";
                systemHover.pickingMode = PickingMode.Ignore;
                systemHover.style.position = UnityEngine.UIElements.Position.Absolute;
                systemHover.style.width = 340; systemHover.style.display = DisplayStyle.None;
                systemHoverText = Text(systemHover, "", "system-hover-copy");
                systemHoverText.style.whiteSpace = WhiteSpace.Normal;
                systemHoverText.style.fontSize = 14; systemHoverText.style.color = new Color(.88f, .94f, .97f);
                var quickHandle = Box(quickbar, "row"); quickHandle.name = "shortcut-bar-header";
                quickHandle.style.position = UnityEngine.UIElements.Position.Absolute;
                quickHandle.style.left = 9; quickHandle.style.top = 1; quickHandle.style.right = 220; quickHandle.style.height = 27;
                quickHandle.tooltip = "Drag to move Systems · right-click a system to choose an effect and level";
                Text(quickHandle, "SYSTEMS", "eyebrow");
                quickHandle.pickingMode = PickingMode.Ignore;
                var expandSystems = ActionButton(quickbar, "Expand / I", () => hud.ToggleEquipment());
                expandSystems.name = "expand-systems"; expandSystems.tooltip = "Expand into ship, mounts, equipment and cargo";
                expandSystems.style.position = UnityEngine.UIElements.Position.Absolute;
                expandSystems.style.top = 0; expandSystems.style.right = 88; expandSystems.style.width = 112;
                expandSystems.style.height = 26; expandSystems.style.minHeight = 26;
                var addShortcut = ActionButton(quickbar, "+", () => Client.AbilitiesUi.AddShortcut(Client.CurrentSnapshot));
                addShortcut.name = "add-shortcut"; addShortcut.tooltip = "Add shortcut slot (up to 24)";
                addShortcut.style.position = UnityEngine.UIElements.Position.Absolute;
                addShortcut.style.top = 0; addShortcut.style.right = 10; addShortcut.style.width = 30;
                addShortcut.style.height = 26; addShortcut.style.minHeight = 26;
                var removeShortcut = ActionButton(quickbar, "−", () =>
                    Client.AbilitiesUi.RemoveShortcut(Client.AbilitiesUi.ShortcutCount(Client.CurrentSnapshot) - 1, Client.CurrentSnapshot));
                removeShortcut.name = "remove-shortcut"; removeShortcut.tooltip = "Remove last shortcut slot";
                removeShortcut.style.position = UnityEngine.UIElements.Position.Absolute;
                removeShortcut.style.top = 0; removeShortcut.style.right = 48; removeShortcut.style.width = 30;
                removeShortcut.style.height = 26; removeShortcut.style.minHeight = 26;
                comms = Box(flight, "panel"); comms.name = "panel-communications"; comms.style.position = UnityEngine.UIElements.Position.Absolute;
                comms.style.backgroundColor = new Color(.025f, .035f, .047f, .99f);
                comms.style.top = 18; comms.style.width = 620; comms.style.height = 158; comms.style.paddingTop = 3; comms.style.paddingBottom = 3;
                var commsHeader = Box(comms, "row spread");
                ActionButton(commsHeader, "COMMUNICATIONS  + / −", () => hud.communicationsOpen = !hud.communicationsOpen).style.minHeight = 25;
                MakeMovable(comms, commsHeader, "communications");
                chatHistory = Scroll(comms); chatHistory.style.flexGrow = 1;
                chatFont = Resources.Load<Font>("Fonts/IBMPlexSans-Medium");
                chatComposer = Box(comms, "row"); chatComposer.style.marginTop = 8;
                chatChannel = new DropdownField(new List<string>
                { "Sector", "Local", "Group", "General Chat", "Market", "New Players" }, 0)
                { name = "chat-channel" };
                chatChannel.style.width = 126; chatComposer.Add(chatChannel);
                chatChannel.style.color = ChatChannelColor("[Sector]");
                chatChannel.RegisterValueChangedCallback(evt =>
                {
                    chatChannel.style.color = ChatChannelColor("[" + evt.newValue + "]");
                    if (evt.newValue != "Sector" && evt.newValue != "Group")
                        Client.SubscribeChatChannel(evt.newValue == "New Players" ? "NewPlayers" : evt.newValue);
                });
                chatInput = new TextField { name = "chat-entry", maxLength = 180 };
                chatInput.AddToClassList("chat-composer-input");
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
                chatInput.RegisterCallback<FocusInEvent>(_ =>
                { chatInput.AddToClassList("composer-focused"); Client.OpenChatEntry(); });
                chatInput.RegisterCallback<FocusOutEvent>(_ =>
                { chatInput.RemoveFromClassList("composer-focused"); Client.CloseChatEntry(); });
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
                    { chatInput.Blur(); Client.CloseChatEntry(); evt.StopPropagation(); }
                }, TrickleDown.TrickleDown);
                var send = ActionButton(chatComposer, "Send", SubmitChat);
                send.name = "send-chat"; chatComposer.style.display = DisplayStyle.Flex;
                send.style.fontSize = 16; chatChannel.style.fontSize = 14;
                send.style.unityFont = chatFont; chatChannel.style.unityFont = chatFont;
                var commsResize = new Label("◢") { name = "communications-resize-grip" };
                commsResize.tooltip = "Drag to resize communications";
                commsResize.style.position = UnityEngine.UIElements.Position.Absolute;
                commsResize.style.right = 4; commsResize.style.bottom = 2;
                commsResize.style.width = 22; commsResize.style.height = 22;
                commsResize.style.unityTextAlign = TextAnchor.MiddleCenter;
                commsResize.style.color = new Color(.7f, .82f, .88f);
                comms.Add(commsResize);
                commsResize.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    resizingComms = true; commsManualSize = true;
                    commsResizeStart = evt.position;
                    commsResizeOrigin = new Vector2(comms.resolvedStyle.width, comms.resolvedStyle.height);
                    commsResize.CapturePointer(evt.pointerId); evt.StopPropagation();
                });
                commsResize.RegisterCallback<PointerMoveEvent>(evt =>
                {
                    if (!resizingComms) return;
                    Vector2 delta = (Vector2)evt.position - commsResizeStart;
                    commsSize = new Vector2(Mathf.Clamp(commsResizeOrigin.x + delta.x, 320,
                        Mathf.Max(320, root.resolvedStyle.width - comms.worldBound.x + root.worldBound.x - 8)),
                        Mathf.Clamp(commsResizeOrigin.y + delta.y, 150,
                        Mathf.Max(150, root.resolvedStyle.height - comms.worldBound.y + root.worldBound.y - 8)));
                    comms.style.width = commsSize.x; comms.style.height = commsSize.y;
                    evt.StopPropagation();
                });
                commsResize.RegisterCallback<PointerUpEvent>(evt =>
                { if (!resizingComms) return; resizingComms = false;
                    commsResize.ReleasePointer(evt.pointerId); evt.StopPropagation(); });

                fitting = Box(root, "window"); fitting.name = "fitting-window"; fitting.style.width = 1180; fitting.style.height = 700;
                var header = Box(fitting, "window-header");
                var headerText = Box(header, "grow"); headerText.name = "fitting-handle"; Text(headerText, "SHIP SYSTEMS", "eyebrow");
                Text(headerText, "Fitting & cargo", "title");
                fittingSubtitle = Text(header, "", "subtle"); fittingSubtitle.style.marginRight = 24;
                var resetLayout = ActionButton(header, "Reset layout", ResetLayout); resetLayout.name = "reset-flight-layout"; resetLayout.style.marginRight = 8;
                var closeFitting = ActionButton(header, "Close  ×", () => hud.ToggleEquipment()); closeFitting.name = "close-fitting"; MakeMovable(fitting, headerText, "fitting");
                var content=Box(fitting,"window-content");fittingContent=content;content.name="systems-fitting-content";
                content.style.minHeight=0;content.style.minWidth=0;content.style.flexShrink=1;content.style.flexBasis=0;
                content.style.overflow=Overflow.Hidden;
                header.style.flexShrink=0;
                cargoColumn = Box(content, "cargo-column");
                Text(cargoColumn, "CARGO HOLD", "eyebrow"); cargoCount = Text(cargoColumn, "", "readout");
                cargoCredits = Text(cargoColumn, "", "readout"); cargoCredits.name = "cargo-credits";
                Text(cargoColumn, "Drag equipment onto a hardpoint", "subtle"); Rule(cargoColumn);
                cargoList = Scroll(cargoColumn); cargoList.style.flexGrow = 1;
                cargoList.contentContainer.style.flexDirection = FlexDirection.Row;
                cargoList.contentContainer.style.flexWrap = Wrap.Wrap;
                var middle = Box(content, "fitting-column");fittingMiddle=middle;
                middle.style.minWidth=0;middle.style.minHeight=0;middle.style.flexBasis=0;
                cargoColumn.style.minHeight=0;cargoColumn.style.flexShrink=0;
                cargoList.style.minHeight=0;cargoList.name="fitting-cargo-scroll";
                ring = Box(middle, ""); ring.style.width = 480; ring.style.height = 540; ring.name="fitting-mount-ring";ring.style.flexShrink=0;
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
                statsColumn.style.minHeight=0;statsColumn.style.flexShrink=0;
                detail = Scroll(statsColumn); detail.style.flexGrow = 1; detail.style.minHeight = 0;
                inspectorTitle = Text(detail, "Select a hardpoint", "title"); inspectorTitle.style.fontSize = 18; inspectorTitle.style.whiteSpace = WhiteSpace.Normal; inspectorTitle.style.flexShrink = 0;
                inspectorText = Text(detail, "", "inspector"); inspectorText.style.flexShrink = 0;
                var footer = Box(fitting, "window-footer");footer.style.flexShrink=0; Text(footer, "DRAG TO FIT  /  CLICK TO INSPECT", "drag-label");
                Text(footer, "I  CLOSE       •       DRAG HEADER TO MOVE", "drag-label");
                dragGhost = Box(root, "inventory-drag-card row"); dragGhost.name = "inventory-drag-ghost";
                dragGhost.style.position = UnityEngine.UIElements.Position.Absolute;
                dragGhost.pickingMode = PickingMode.Ignore;
                dragIcon = new Image { name = "inventory-drag-icon", pickingMode = PickingMode.Ignore };
                dragIcon.AddToClassList("inventory-drag-icon"); dragGhost.Add(dragIcon);
                var dragText = Box(dragGhost, "inventory-drag-text"); dragText.pickingMode = PickingMode.Ignore;
                dragCaption = Text(dragText, "", "item-name"); dragCaption.pickingMode = PickingMode.Ignore;
                dragStatus = Text(dragText, "", "drag-label"); dragStatus.name = "inventory-drag-status";
                dragStatus.pickingMode = PickingMode.Ignore;
                dragGhost.style.display = DisplayStyle.None;
                itemHover = Box(root, "panel item-hover"); itemHover.name = "inventory-item-hover";
                itemHover.style.position = UnityEngine.UIElements.Position.Absolute;
                itemHover.style.width = 310; itemHover.style.display = DisplayStyle.None;
                itemHover.pickingMode = PickingMode.Ignore;
                itemHoverTitle = Text(itemHover, "", "title");
                itemHoverTitle.style.whiteSpace = WhiteSpace.Normal;
                itemHoverBody = Text(itemHover, "", "inspector");
                itemHoverBody.style.whiteSpace = WhiteSpace.Normal;
                itemHoverTitle.pickingMode = PickingMode.Ignore;
                itemHoverBody.pickingMode = PickingMode.Ignore;
                ammoPicker = Box(root, "panel"); ammoPicker.name = "launcher-ammo-picker";
                ammoPicker.style.position = UnityEngine.UIElements.Position.Absolute;
                ammoPicker.style.width = 350; ammoPicker.style.maxHeight = 400;
                ammoPicker.style.display = DisplayStyle.None;
                var ammoHeader = Box(ammoPicker, "row spread");
                var ammoTitle = Text(ammoHeader, "LAUNCHER AMMUNITION", "eyebrow");
                MakeMovable(ammoPicker, ammoTitle, "launcher-ammo");
                var ammoClose = ActionButton(ammoHeader, "×", CloseAmmoPicker);
                ammoClose.name = "close-ammo-picker"; ammoClose.style.width = 28;
                ammoChoices = Scroll(ammoPicker); ammoChoices.style.maxHeight = 270;
                ammoReload = new Toggle("Auto Reload") { name = "launcher-auto-reload" };
                ammoReload.style.marginTop = 8; ammoPicker.Add(ammoReload);
                ammoReload.RegisterValueChangedCallback(evt =>
                {
                    LauncherAmmoOption current = Client.CurrentSnapshot?.LauncherAmmo?.Launchers
                        .FirstOrDefault(option => option.Mount == ammoPickerMount);
                    if (current == null) return;
                    ammoReload.SetValueWithoutNotify(current.AutoReload);
                    Client.SetLauncherAutoReload(ammoPickerMount, evt.newValue);
                });
                ammoPickerStatus = Text(ammoPicker, "", "subtle");
                ammoPickerStatus.style.whiteSpace = WhiteSpace.Normal;
                ammoEditShortcut = ActionButton(ammoPicker, "Edit shortcut…", () =>
                {
                    int index = ammoPickerShortcut;
                    CloseAmmoPicker();
                    if (index >= 0) ShowSystemPicker(index, ammoPicker.worldBound.center);
                });
                ammoEditShortcut.name = "launcher-edit-shortcut";
                CreateLootPanel();
                root.RegisterCallback<GeometryChangedEvent>(_ => ClampMovedPanels());
                quickbar.RegisterCallback<GeometryChangedEvent>(_ => ClampMovedPanels());
                root.RegisterCallback<PointerMoveEvent>(MoveItem);
                root.RegisterCallback<PointerUpEvent>(DropItem);
                root.focusable = true;
                root.RegisterCallback<PointerCancelEvent>(_ => CancelItem("MOVE CANCELLED"));
                root.RegisterCallback<PointerCaptureOutEvent>(_ =>
                { if (!releasingPointer && itemPointer >= 0 && !root.HasPointerCapture(itemPointer)) CancelItem("MOVE CANCELLED"); });
                root.RegisterCallback<KeyDownEvent>(evt =>
                { if (evt.keyCode == KeyCode.Escape && itemPointer >= 0) { CancelItem("MOVE CANCELLED"); evt.StopPropagation(); } });
                flight.Add(fitting);
                MakeCollapsible(scanner, scannerHead, "overview");
                MakeCollapsible(targetPanel, targetHead, "target");
                SetBrowseTab(0);
                vitalHead.style.position = UnityEngine.UIElements.Position.Absolute;
                vitalHead.style.right = 10; vitalHead.style.top = 39; vitalHead.style.width = 90;
                flightStrip.Add(vitalHead);
                MakeCollapsible(fitting, header, "fitting");
                foreach (VisualElement panel in new[] { scanner, targetPanel, flightStrip, fitting })
                    panel.RegisterCallback<PointerDownEvent>(_ => panel.BringToFront(), TrickleDown.TrickleDown);
                inviteOverlay = Box(root, "invite-overlay"); Stretch(inviteOverlay);
                inviteOverlay.style.backgroundColor = new Color(.005f, .012f, .025f, .72f);
                inviteOverlay.style.alignItems = Align.Center;
                inviteOverlay.style.justifyContent = Justify.Center;
                var invitation = Box(inviteOverlay, "panel"); invitation.style.width = 470;
                var invitationHeader = Text(invitation, "CREW INVITATION", "eyebrow");
                MakeMovable(invitation, invitationHeader, "invitation");
                Text(invitation, "Join this crew?", "title");
                invitationText = Text(invitation, "", "subtle"); invitationText.style.marginTop = 18;
                var responses = Box(invitation, "row"); responses.style.marginTop = 22;
                ActionButton(responses, "Decline", () => Client.AnswerConfirmation(false));
                var accept = ActionButton(responses, "Accept invitation", () => Client.AnswerConfirmation(true), "primary");
                accept.name = "accept-group-invite"; accept.style.marginLeft = 12;
                inviteOverlay.style.display = DisplayStyle.None;
                ammoEditShortcut.name = "edit-ammo-shortcut";
                systemPicker = Box(root, "panel systems-picker"); systemPicker.name = "system-selector";
                systemPicker.style.position = UnityEngine.UIElements.Position.Absolute;
                systemPicker.style.width = 420; systemPicker.style.display = DisplayStyle.None;
                var pickerHeader = Box(systemPicker, "row spread");
                systemPickerTitle = Text(pickerHeader, "CHOOSE SYSTEM", "eyebrow");
                MakeMovable(systemPicker, systemPickerTitle, "system-selector");
                ActionButton(pickerHeader, "×", CloseSystemPicker).style.width = 28;
                systemSearch = new TextField { name = "systems-search", label = "Search" }; systemSearch.tooltip = "Search effects, skill names or wormhole destinations";
                systemSearch.labelElement.style.minWidth = 54; systemSearch.labelElement.style.width = 54;
                systemSearch.style.marginBottom = 8; systemSearch.style.minHeight = 30;
                systemPicker.Add(systemSearch); systemSearch.RegisterValueChangedCallback(_ => RefreshSystemPicker());
                systemPickerList = Scroll(systemPicker); systemPickerList.style.flexGrow = 1;
                var pickerFooter = Box(systemPicker, "row spread");
                ActionButton(pickerFooter, "Clear assignment", () =>
                { Client.AbilitiesUi.ClearShortcut(systemPickerIndex, Client.CurrentSnapshot); CloseSystemPicker(); });
                ActionButton(pickerFooter, "Remove slot", () =>
                { Client.AbilitiesUi.RemoveShortcut(systemPickerIndex, Client.CurrentSnapshot); CloseSystemPicker(); });
                root.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (SystemPickerOpen && !systemPicker.worldBound.Contains(evt.position)) CloseSystemPicker();
                    if (ammoPickerMount >= 0 && !ammoPicker.worldBound.Contains(evt.position)) CloseAmmoPicker();
                }, TrickleDown.TrickleDown);
                root.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (evt.keyCode != KeyCode.Escape) return;
                    if (ammoPickerMount >= 0) { CloseAmmoPicker(); evt.StopPropagation(); }
                    else if (SystemPickerOpen) { CloseSystemPicker(); evt.StopPropagation(); }
                });
                gaugeHover=Box(root,"panel");gaugeHover.name="resource-hover";gaugeHover.pickingMode=PickingMode.Ignore;
                gaugeHover.style.position=UnityEngine.UIElements.Position.Absolute;gaugeHover.style.width=340;
                gaugeHover.style.backgroundColor=new Color(.015f,.03f,.045f,.99f);gaugeHover.style.display=DisplayStyle.None;
                gaugeHoverText=Text(gaugeHover,"","subtle");gaugeHoverText.name="resource-hover-values";
                gaugeHoverText.style.fontSize=14;gaugeHoverText.style.color=Color.white;gaugeHoverText.style.whiteSpace=WhiteSpace.Normal;
                BindGaugeHover(reactor);BindGaugeHover(defense);BindGaugeHover(targetDefense);
                foreach(var instrument in crewDials)BindGaugeHover(instrument);
                BindExperienceHover();
                performance=Text(root,"","subtle");performance.name="performance-overlay";performance.pickingMode=PickingMode.Ignore;
                performance.style.position=UnityEngine.UIElements.Position.Absolute;performance.style.left=24;performance.style.top=84;
                performance.style.backgroundColor=new Color(.01f,.02f,.035f,.92f);performance.style.color=Color.white;
                performance.style.fontSize=16;performance.style.display=DisplayStyle.None;
                mobHover=Box(root,"panel");mobHover.name="mob-resource-tooltip";mobHover.pickingMode=PickingMode.Ignore;
                mobHover.style.position=UnityEngine.UIElements.Position.Absolute;mobHover.style.width=280;mobHover.style.display=DisplayStyle.None;
                mobHover.style.backgroundColor=new Color(.015f,.03f,.045f,.99f);
                mobHoverName=Text(mobHover,"","title");mobHoverName.style.fontSize=16;mobHoverName.style.whiteSpace=WhiteSpace.Normal;
                mobHoverDial=new ResourceDialWidget(ResourceDialKind.Defense,128);mobHoverDial.name="mob-tooltip-defense";
                mobHoverDial.pickingMode=PickingMode.Ignore;mobHoverDial.style.alignSelf=Align.Center;mobHover.Add(mobHoverDial);
                mobHoverValues=Text(mobHover,"","subtle");mobHoverValues.name="mob-tooltip-values";mobHoverValues.style.color=Color.white;mobHoverValues.style.fontSize=14;
                targetName.pickingMode=PickingMode.Position;
                targetName.RegisterCallback<PointerEnterEvent>(_=>mobHoverRow=Client.CurrentSnapshot.Mobs.Any(mob=>mob.Id==selectedActor)?selectedActor:0);
                targetName.RegisterCallback<PointerLeaveEvent>(_=>mobHoverRow=0);


            }

            /// <summary>Fit the complete mount assembly in the remaining viewport; only cargo owns a scrollbar.</summary>
            private void FitExpandedInventory()
            {
                float width=fitting.resolvedStyle.width;
                bool twoColumns = width >= 1140;
                cargoColumn.style.width = twoColumns ? 410 : width < 1050 ? 180 : 250;
                statsColumn.style.width = twoColumns ? 230 : width < 1050 ? 230 : 250;
                cargoList.contentContainer.style.flexWrap = twoColumns ? Wrap.Wrap : Wrap.NoWrap;
                float availableWidth=fittingMiddle.resolvedStyle.width;
                float availableHeight=fittingContent.resolvedStyle.height-32;
                if(availableWidth>0&&availableHeight>0)
                    ring.style.scale=new Scale(Vector3.one*Mathf.Min(1,availableWidth/480,availableHeight/540));
            }

            /// <summary>Expand the compact Systems panel into the fitting window without covering equipment mounts.</summary>
            private void SetSystemsExpanded(bool expanded)
            {
                if (systemsEmbedded == expanded) return;
                if (expanded)
                {
                    fitting.Add(quickbar); quickbar.style.position = UnityEngine.UIElements.Position.Relative;
                    quickbar.style.left = quickbar.style.top = quickbar.style.right = quickbar.style.bottom = StyleKeyword.Auto;
                    quickbar.style.alignSelf = Align.Center; quickbar.style.flexShrink = 0;
                    quickbar.style.marginBottom = 8;
                    quickbar.RemoveFromClassList("shortcut-compact");
                }
                else
                {
                    quickbarDock.Add(quickbar); quickbar.style.position = UnityEngine.UIElements.Position.Relative;
                    quickbar.style.left = quickbar.style.top = quickbar.style.right = quickbar.style.bottom = StyleKeyword.Auto;
                    quickbar.style.alignSelf = StyleKeyword.Auto; quickbar.style.marginBottom = 0;
                    quickbar.AddToClassList("shortcut-compact");
                }
                systemsEmbedded = expanded;
            }

            /// <summary>Open all learned effects, sublevels and wormhole destinations inside the viewport.</summary>
            private void ShowSystemPicker(int index, Vector2 pointer)
            {
                systemPickerIndex = index; systemSearch.SetValueWithoutNotify("");
                systemPickerTitle.text = "SYSTEMS · SLOT " + (index + 1);
                float height = Mathf.Min(560, root.resolvedStyle.height - 60);
                Vector2 point = root.WorldToLocal(pointer);
                systemPicker.style.height = height;
                systemPicker.style.left = Mathf.Clamp(point.x - 120, 8, Mathf.Max(8, root.resolvedStyle.width - 428));
                systemPicker.style.top = Mathf.Clamp(point.y - height - 8, 8, Mathf.Max(8, root.resolvedStyle.height - height - 8));
                RefreshSystemPicker(); systemPicker.style.display = DisplayStyle.Flex;
                systemPicker.BringToFront(); root.Focus();
            }

            /// <summary>Group effects under native skills while preserving every learned destination.</summary>
            private void RefreshSystemPicker()
            {
                if (systemPickerIndex < 0) return;
                Snapshot snapshot = Client.CurrentSnapshot;
                pickerSourceKey = PickerSource(snapshot);
                string query = systemSearch.value.Trim(); systemPickerList.Clear();
                SystemChoice[] choices = Client.AbilitiesUi.SystemChoices(systemPickerIndex, snapshot)
                    .Where(choice => query.Length == 0 || (choice.Family + " " + choice.Effect + " " + choice.Name)
                        .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                foreach (var family in choices.GroupBy(choice => choice.Family))
                {
                    var folder = new Foldout { text = family.Key + "  ·  " + family.Count(),
                        value = query.Length > 0 || family.Any(choice => choice.Selected) };
                    folder.AddToClassList("system-family"); systemPickerList.Add(folder);
                    foreach (var effect in family.GroupBy(choice => choice.Effect))
                    {
                        VisualElement parent = folder;
                        if (effect.Count() > 1 && family.Select(choice => choice.Effect).Distinct().Count() > 1)
                        {
                            var levels = new Foldout { text = effect.Key,
                                value = query.Length > 0 || effect.Any(choice => choice.Selected) };
                            folder.Add(levels); parent = levels;
                        }
                        foreach (SystemChoice choice in effect.OrderBy(item => item.Level))
                        {
                            int id = choice.Id;
                            var button = ActionButton(parent, (choice.Selected ? "●  " : "") + choice.Name + "  ·  L" + choice.Level,
                                () => { Client.AbilitiesUi.SelectSystem(systemPickerIndex, id, Client.CurrentSnapshot); CloseSystemPicker(); });
                            button.name = "system-choice-" + id; button.AddToClassList("system-choice");
                        }
                    }
                }
                var equipment = snapshot?.Mounted.Where(item => item != null && (query.Length == 0 ||
                    (item.Name + " " + item.ActivationLabel).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
                if (equipment != null && equipment.Length > 0)
                {
                    var group = new Foldout { text = "Equipped systems  ·  " + equipment.Length, value = query.Length > 0 };
                    systemPickerList.Add(group);
                    foreach (ItemView item in equipment)
                    {
                        int mount = item.Slot;
                        ActionButton(group, item.Name + (string.IsNullOrEmpty(item.ActivationLabel) ? "" : " · " + item.ActivationLabel),
                            () => { Client.AbilitiesUi.BindEquippedItem(systemPickerIndex, mount, Client.CurrentSnapshot); CloseSystemPicker(); }).AddToClassList("system-choice");
                    }
                }
                if (choices.Length == 0 && (equipment == null || equipment.Length == 0))
                    Text(systemPickerList, "No matching systems. Server-advertised actions appear here.", "subtle");
            }

            /// <summary>Return keyboard input to flight without changing assignments.</summary>
            private void CloseSystemPicker()
            { if (systemPickerIndex < 0) return; systemPickerIndex = -1; systemPicker.style.display = DisplayStyle.None; root.Focus(); }

            /// <returns>string: Knowledge and equipment changes that require refreshing an open selector.</returns>
            private static string PickerSource(Snapshot snapshot)
            { return snapshot == null ? "" : snapshot.PlayerId + ":" + string.Join(",", snapshot.AvailableAbilities) + ":" +
                string.Join(",", snapshot.Mounted.Select(item => item?.Template ?? 0)); }

            /// <summary>Refresh measurements at ten hertz while keeping pointer interactions immediate.</summary>
            public void Update()
            {
                Snapshot s = Client.CurrentSnapshot;
                performance.style.display=Client.ShowPerformance?DisplayStyle.Flex:DisplayStyle.None;
                if(Client.ShowPerformance)performance.text=Client.PerformanceText;
                galaxy.text = "Galaxy / " + Client.Bindings.Display(ControlAction.Galaxy);
                Visible = Client.Connected && !Client.ChoosingCharacter && s != null && !hud.settingsOpen &&
                    (!(Client.ComponentMarketOpen || Client.GalaxyTravelOpen || Client.ItemBrowserOpen) || hud.GroupInviteOpen);
                root.style.display = Visible ? DisplayStyle.Flex : DisplayStyle.None;
                TickItem(s);
                if (!Visible) { CloseSystemPicker(); CloseAmmoPicker(); if (itemPointer >= 0) CancelItem("MOVE CANCELLED"); return; }
                RefreshLoot(s);
                if (ammoPickerMount >= 0 &&
                    (!hud.equipmentOpen && ammoPickerShortcut < 0 || ammoPickerShortcut >= 0 &&
                    Client.AbilitiesUi.BoundLauncherMount(ammoPickerShortcut, s) != ammoPickerMount)) CloseAmmoPicker();
                if (ammoPickerMount >= 0) RefreshAmmoPicker(s);
                settings.scale = hud.Scale;
                zoomHint.text = hud.Scale + .01f < CalculateScale(Screen.width, Screen.height, 1) * hud.InterfaceZoom ?
                    "Limited to fit this window" : "Scales text and buttons";
                UpdateMobHover(s);
                if(hoveredGauge!=null)
                {
                    gaugeHoverText.text=hoveredGauge.Subject+"\n"+hoveredGauge.tooltip;
                    if(hud.equipmentOpen||hoveredGauge.resolvedStyle.display==DisplayStyle.None){hoveredGauge=null;gaugeHover.style.display=DisplayStyle.None;}
                }

                if(!hud.equipmentOpen && s.Station==null && root.resolvedStyle.visibility==Visibility.Visible)
                {defense.Tick(Time.unscaledTime);reactor.Tick(Time.unscaledTime);}
                if(!hud.equipmentOpen && s.Station==null)
                {
                    if(targetDefense.resolvedStyle.display!=DisplayStyle.None)targetDefense.Tick(Time.unscaledTime);
                    if(crewPanel.resolvedStyle.display!=DisplayStyle.None)
                        for(int index=0;index<5;index++)if(crewCards[index].resolvedStyle.display!=DisplayStyle.None)crewDials[index].Tick(Time.unscaledTime);
                }
                LayoutFlightPanels(s);
                bool expandedComms = hud.communicationsOpen;
                if (commsManualSize)
                    commsSize = new Vector2(Mathf.Clamp(commsSize.x, 320, Mathf.Max(320, root.resolvedStyle.width - 16)),
                        Mathf.Clamp(commsSize.y, 150, Mathf.Max(150, root.resolvedStyle.height - 26)));
                comms.style.height = commsManualSize ? commsSize.y : expandedComms ? 270 : 158;
                float availableChat = root.resolvedStyle.width - scanner.resolvedStyle.width - targetPanel.resolvedStyle.width - 80;
                float commsWidth = commsManualSize ? commsSize.x : Mathf.Clamp(availableChat, 320, 620);
                comms.style.width = commsWidth;
                if (!movedPanels.Contains(comms))
                    comms.style.left = Mathf.Clamp((root.resolvedStyle.width - commsWidth) / 2,
                        scanner.resolvedStyle.width + 32, Mathf.Max(scanner.resolvedStyle.width + 32,
                        root.resolvedStyle.width - targetPanel.resolvedStyle.width - commsWidth - 32));
                chatComposer.style.display = DisplayStyle.Flex;
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
                        float width = Mathf.Min(1180, canvasSize.x - 16), height = Mathf.Min(860, canvasSize.y - 16);
                        fitting.style.width = width; fitting.style.height = height;
                        fitting.style.left = (canvasSize.x - width) / 2;
                        fitting.style.top = (canvasSize.y - height) / 2;
                    }
                    positioned = true; lastCanvasSize = canvasSize;
                }
                SetSystemsExpanded(hud.equipmentOpen);
                if(hud.equipmentOpen)FitExpandedInventory();
                fitting.style.display = hud.equipmentOpen ? DisplayStyle.Flex : DisplayStyle.None;
                flight.style.display = DisplayStyle.Flex;
                foreach (VisualElement instrument in flight.Children().Where(child => child != fitting))
                {
                    bool available = s.Station == null || instrument == comms || instrument == flightStrip ||
                        instrument == effectsPanel || instrument == menuPanel;
                    instrument.style.visibility = available ? Visibility.Visible : Visibility.Hidden;
                }
                menuPanel.style.display = menuOpen && !hud.equipmentOpen ? DisplayStyle.Flex : DisplayStyle.None;
                if (menuOpen) { PositionFlightMenu(); menuPanel.BringToFront(); }
                if (hud.equipmentOpen && !fittingWasOpen) fitting.BringToFront();
                fittingWasOpen = hud.equipmentOpen;
                if (hud.equipmentOpen) quickbar.BringToFront();
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
                quickbar.Q<Button>("expand-systems").text = hud.equipmentOpen ? "Collapse / I" : "Expand / I";
                quickbarDock.style.display = Client.AbilitiesUi.VariantsOpen || hud.equipmentOpen ? DisplayStyle.None : DisplayStyle.Flex;
                quickbar.style.display = Client.AbilitiesUi.VariantsOpen ? DisplayStyle.None : DisplayStyle.Flex;
                if (Time.unscaledTime < nextRefresh) return;
                if (shownContactFilter != (int)hud.SelectedContactFilter)
                {
                    shownContactFilter = (int)hud.SelectedContactFilter;
                    contactFilterCaption.text = contactCategoryButtons[shownContactFilter].tooltip;
                    actorFilter = "All contacts"; contactKey = "";
                }
                for (int category = 0; category < contactCategoryButtons.Length; category++)
                    contactCategoryButtons[category].EnableInClassList("active", category == (int)hud.SelectedContactFilter);
                nextRefresh = Time.unscaledTime + .1f;
                sectorLabel.text = s.Sector;
                pilotLabel.text = Client.PilotName;
                float displayedSpeed = Client.DisplayedSpeed;
                speedLabel.text = Math.Abs(displayedSpeed).ToString("N0");
                modeLabel.text = s.WarpPhase == WarpPhase.Idle ? "IMPULSE  /  " + (Math.Abs(displayedSpeed) < 1 ? "STATIONARY" : displayedSpeed < 0 ? "REVERSE" : "FORWARD") : "WARP DRIVE  /  " + s.WarpPhase.ToString().ToUpperInvariant();
                defense.SetDefense(s.Shield,s.MaxShield,s.Hull,s.MaxHull,hasHull:true);
                reactor.Set(s.Energy, s.Energy * s.MaxEnergy, s.MaxEnergy);
                RefreshExperience(s.Experience);
                RefreshCrew(s);
                fitShield.Set(s.Shield, s.Shield * s.MaxShield, s.MaxShield);
                fitReactor.Set(s.Energy, s.Energy * s.MaxEnergy, s.MaxEnergy);
                statusLabel.text = $"Hull     {s.Hull:N0} / {s.MaxHull:N0}\nSpeed   {displayedSpeed:N0} m/s     •     Accel   {s.Acceleration:0} m/s²";
                for (int i = 0; i < deflectValues.Length; i++)
                    deflectValues[i].text = s.Defense.Known && i < s.Defense.Deflects.Count ? s.Defense.Deflects[i] + "%" : "—";
                cargoCount.text = $"{s.Cargo.Count(i => i != null):00} / {s.CargoCapacity:00} slots";
                cargoCredits.text = $"Credits   {s.Credits:N0}";
                int fittedWeapons = s.Mounted.Skip(3).Take((int)s.WeaponMounts).Count(item => item != null);
                int fittedDevices = s.Mounted.Skip(9).Take((int)s.DeviceMounts).Count(item => item != null);
                fittingSubtitle.text = $"Hull {s.Experience?.HullStage ?? 0}   ·   {fittedWeapons}/{s.WeaponMounts} weapons fitted   ·   {fittedDevices}/{s.DeviceMounts} devices fitted";
                string history = s.Messages.Length == 0 ? "Press Enter to talk" : string.Join("\n", s.Messages);
                if (renderedChat != history)
                {
                    float scrollEnd = Mathf.Max(0, chatHistory.contentContainer.resolvedStyle.height -
                        chatHistory.contentViewport.resolvedStyle.height);
                    Vector2 previousScroll = chatHistory.scrollOffset;
                    bool followingLatest = previousScroll.y >= scrollEnd - 16;
                    renderedChat = history;
                    chatHistory.Clear();
                    foreach (string line in s.Messages.Length == 0 ? new[] { history } : s.Messages)
                    {
                        Label entry = Text(chatHistory, line, "chat-entry");
                        entry.enableRichText = false;
                        entry.style.whiteSpace = WhiteSpace.Normal;
                        entry.style.fontSize = 14; entry.style.unityFont = chatFont;
                        entry.style.color = ChatChannelColor(line);
                    }
                    chatHistory.schedule.Execute(() => chatHistory.scrollOffset = followingLatest ?
                        new Vector2(0, float.MaxValue) : previousScroll);
                }
                int shortcuts = Client.AbilitiesUi.ShortcutCount(s);
                quickbar.style.height = systemsEmbedded ? 38 + ((shortcuts + 5) / 6) * 60 : 95;
                quickbar.style.width = systemsEmbedded ? 720 : Mathf.Max(160, shortcuts * 72 + 12);
                quickbar.Q<Button>("add-shortcut").SetEnabled(shortcuts < AbilityHud.MaximumShortcuts);
                quickbar.Q<Button>("remove-shortcut").SetEnabled(shortcuts > 1);
                compactSystemControls.style.display = systemsEmbedded ? DisplayStyle.None : DisplayStyle.Flex;
                compactSystemControls.Q<Button>("add-shortcut-compact").SetEnabled(shortcuts < AbilityHud.MaximumShortcuts);
                compactSystemControls.Q<Button>("remove-shortcut-compact").SetEnabled(shortcuts > 1);
                for (int i = 0; i < AbilityHud.MaximumShortcuts; i++)
                {
                    quickSlots[i].style.left = systemsEmbedded ? 9 + i % 6 * 118 : 8 + i * 72;
                    quickSlots[i].style.top = systemsEmbedded ? 32 + i / 6 * 60 : 25;
                    quickSlots[i].style.width = systemsEmbedded ? 110 : 66;
                    quickSlots[i].style.height = systemsEmbedded ? 52 : 62;
                    quickSlots[i].style.display = i < shortcuts ? DisplayStyle.Flex : DisplayStyle.None;
                    if (i >= shortcuts) continue;
                    var slot = Client.AbilitiesUi.QuickSlotDisplay(i, s);
                    quickNames[i].text = slot.Name; quickKeys[i].text = slot.Key; quickStates[i].text = slot.Status;
                    quickIcons[i].image = slot.Icon;
                    quickSlots[i].EnableInClassList("unassigned", slot.Icon == null);
                    if (!systemsEmbedded && slot.Icon == null) quickNames[i].text = "+";
                    quickIcons[i].style.display = slot.Icon == null ? DisplayStyle.None : DisplayStyle.Flex;
                    quickNames[i].style.left = slot.Icon == null ? 10 : 40;
                    SystemActivity state = Client.AbilitiesUi.SystemState(i, s);
                    quickRecovery[i].style.display = state.CoveredFraction > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                    quickRecovery[i].style.height = Length.Percent(state.CoveredFraction * 100);
                    quickSlots[i].EnableInClassList("system-active", state.Active);
                    quickSlots[i].EnableInClassList("system-charging", state.Charging);
                    quickSlots[i].tooltip = slot.Name + " • " + slot.Status + " • right-click: effects and levels";
                }
                if (hoveredSystem >= 0) ShowSystemHover(hoveredSystem);
                if (SystemPickerOpen && pickerSourceKey != PickerSource(s)) RefreshSystemPicker();
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
                if (sockets.Count > 3 && (Mathf.Min(6, (int)s.WeaponMounts) != sockets.Keys.Count(slot => slot >= 3 && slot < 9) ||
                    sockets.Keys.Count(slot => slot >= 9) != Mathf.Min(6, (int)s.DeviceMounts)))
                {
                    foreach (int slot in sockets.Keys.Where(slot => slot >= 3).ToArray())
                    { sockets[slot].Root.RemoveFromHierarchy(); sockets.Remove(slot); }
                    dropTargets.RemoveAll(t => (t.Container == 2 || t.Container == 3) && t.Slot >= 3);
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
                }
                for (int i = 0; i < devicesCount; i++)
                {
                    int slot = i + 9;
                    if (!sockets.ContainsKey(slot))
                    {
                        SocketView socket = AddSocket(ring, slot, "D" + (i + 1));
                        float angle = Mathf.Lerp(30, 150, devicesCount == 1 ? .5f : i / (float)(devicesCount - 1)) * Mathf.Deg2Rad;
                        Place(socket.Root, 240 + Mathf.Cos(angle) * 191 - 31, 227 + Mathf.Sin(angle) * 191 - 33, 62, 66);
                    }
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
                    v.State.text = item == null ? "+" : item.Type == 15 || item.Type == 16 ? (ammo?.Quantity ?? 0).ToString() : "ONLINE";
                    v.Root.tooltip = item == null ? "Empty hardpoint • drop equipment here" : item.Name + (ammo != null ? $"\nMagazine: {ammo.Quantity}/{ammo.MaxStack}" : "");
                }
                string key = string.Join("|", s.Cargo.Select(i => i == null ? "-" : $"{i.Slot}:{i.Template}:{i.Quantity}:{i.Structure}")) + ":" + s.CargoCapacity + ":" + (fitting.resolvedStyle.width >= 1140);
                if (key == inventoryKey) return;
                inventoryKey = key;
                dropTargets.RemoveAll(t => t.Container == 1);
                cargoList.Clear();
                foreach (ItemView item in s.Cargo.Where(i => i != null).OrderBy(i => ItemCategory(i.Type)).ThenBy(i => i.Name))
                {
                    var row = Box(cargoList, "cargo-row");
                    row.style.width = new Length(fitting.resolvedStyle.width >= 1140 ? 49 : 100,
                        LengthUnit.Percent);
                    row.style.minWidth = 0;
                    var icon = new Image { image = hud.icons.ForItem(item), pickingMode = PickingMode.Ignore }; icon.style.width = 38; icon.style.height = 38; icon.style.marginRight = 7; row.Add(icon);
                    var text = Box(row, "grow"); text.style.flexShrink = 1;
                    var name = Text(text, item.Name, "item-name");
                    name.style.whiteSpace = WhiteSpace.NoWrap;
                    name.style.overflow = Overflow.Hidden;
                    name.style.textOverflow = TextOverflow.Ellipsis;
                    Text(text, $"{ItemCategory(item.Type)}  ·  ×{item.Quantity}", "subtle");
                    row.name = "cargo-" + item.Slot; BindItem(row, 1, item.Slot); row.userData = item.Slot;
                }
                var drop = Box(cargoList, ""); drop.style.minHeight = 100; drop.style.justifyContent = Justify.Center;
                drop.style.width = new Length(100, LengthUnit.Percent);
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
            }

            /// <summary>Refresh sorted contact and destination rows without replacing focused controls every frame.</summary>
            private void RefreshContacts(Snapshot s)
            {
                var actorEntries = s.Players.Concat(s.Mobs).Where(actor => actor.Id != s.PlayerId &&
                    ContactFiltering.Matches(hud.SelectedContactFilter, actor) &&
                    (actorFilter == "All contacts" || actorFilter == "Players" && actor.IsPlayer ||
                    actorFilter == "Hostiles" && actor.Hostile || actorFilter == "Neutral mobs" && !actor.IsPlayer && !actor.Hostile))
                    .Select(actor => (Id: actor.Id, Name: actor.Name, Position: actor.Position, Actor: actor, Object: (MapObject)null));
                var worldEntries = s.Objects.Where(item => actorFilter == "All contacts" &&
                    ContactFiltering.Matches(hud.SelectedContactFilter, item))
                    .Select(item => (Id: item.Id, Name: item.Name, Position: item.Position, Actor: (ShipActor)null, Object: item));
                var entries = actorEntries.Concat(worldEntries).OrderBy(entry => entry.Position.Distance(s.Position))
                    .ThenBy(entry => entry.Id).Take(24).ToArray();
                string key = hud.SelectedContactFilter + ":" + actorFilter + ":" + string.Join("|", entries.Select(entry =>
                    $"{entry.Id}:{entry.Name}:{entry.Position.Distance(s.Position) / 1000:0.0}:{entry.Id == hud.contactId || entry.Id == Client.Target?.Id}"));
                if (key != contactKey)
                {
                    contactKey = key; contacts.Clear();
                    foreach (var entry in entries)
                    {
                        ShipActor actor = entry.Actor; MapObject item = entry.Object;
                        var row = Box(contacts, "contact"); row.name = (actor == null ? "object-" : "actor-") + entry.Id;
                        row.EnableInClassList("chosen", entry.Id == hud.contactId || entry.Id == Client.Target?.Id);
                        string glyph = actor != null ? actor.IsPlayer ? "◇" : "⌖" : item.Kind == 25 ? "▱" :
                            item.Kind == 37 || item.Kind == 38 ? "⬡" : item.Kind == 11 ? "◇" : item.Kind == 12 ? "▣" : "·";
                        Label symbol = Text(row, glyph, "symbol");
                        symbol.style.color = actor?.Hostile == true ? new Color(.95f,.4f,.32f) :
                            item?.Kind == 25 ? new Color(.95f,.77f,.4f) : item?.Kind == 38 ? new Color(.4f,.88f,.68f) : Blue;
                        Text(row, entry.Name, "contact-name"); Text(row, $"{entry.Position.Distance(s.Position) / 1000:0.0} km", "range");
                        uint id = entry.Id; row.RegisterCallback<ClickEvent>(_ => Client.SelectAdvertisedObject(id));
                        if (actor != null && !actor.IsPlayer)
                        {
                            row.RegisterCallback<PointerEnterEvent>(_ => mobHoverRow = id);
                            row.RegisterCallback<PointerLeaveEvent>(_ => { if (mobHoverRow == id) mobHoverRow = 0; });
                        }
                    }
                    if (entries.Length == 0) Text(contacts, "No matching contacts", "subtle");
                }
                MapObject[] filtered = s.Objects.Where(TacticalSectorMap.IsNavigationTarget).Where(o =>
                    objectFilter == "All navigation" || objectFilter == "Gates" && o.Kind == 11 ||
                    objectFilter == "Stations" && o.Kind == 12 || objectFilter == "Navpoints" && o.Kind != 11 && o.Kind != 12).ToArray();
                MapObject[] stations = filtered.Where(o => o.Kind == 12)
                    .OrderBy(o => o.Name).ToArray();
                MapObject[] objects = stations.Concat(filtered.Where(o => o.Kind != 12 &&
                    TacticalSectorMap.IsNavigationTarget(o)).OrderBy(o => o.Kind == 11 ? 0 : 1).ThenBy(o => o.Name)).ToArray();
                string navKey = s.Sector + string.Join("|", objects.Select(o => $"{o.Id}:{o.Kind}:{o.Name}"));
                if (navKey != navigationKey)
                {
                    navigationKey = navKey; destinations.Clear(); destinationRanges.Clear(); destinationRows.Clear();
                    if (objects.Length == 0) Text(destinations, "No matching objects in this sector", "subtle");
                    if (stations.Length > 0) Text(destinations, "STATIONS", "eyebrow");
                    foreach (MapObject item in objects)
                    {
                        if (stations.Length > 0 && objects.Length > stations.Length && item == objects[stations.Length])
                            Text(destinations, "NAVIGATION", "eyebrow");
                        var row = Box(destinations, "contact"); row.name = "destination-" + item.Id; destinationRows[item.Id] = row;
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

            /// <summary>Resolve MOB hover from scanner rows, world labels or the ship marker without selecting it.</summary>
            private void UpdateMobHover(Snapshot snapshot)
            {
                if(hud.equipmentOpen||Client.ChatOpen||hud.GroupInviteOpen||SystemPickerOpen)
                {mobHover.style.display=DisplayStyle.None;return;}
                Vector2 screen=new Vector2(Input.mousePosition.x,Screen.height-Input.mousePosition.y);
                ShipActor actor=snapshot.Mobs.FirstOrDefault(mob=>mob.Id==mobHoverRow);
                if(actor==null&&!ContainsPointer())actor=hud.HoveredMob(snapshot,screen);
                if(actor==null){mobHover.style.display=DisplayStyle.None;return;}
                ShowMobTooltip(actor,screen/hud.Scale);
            }

            /// <summary>Place the circular MOB meter and full resource readings in its hover card.</summary>
            private void ShowMobTooltip(ShipActor actor,Vector2 point)
            {
                mobHoverName.text=actor.Name + " · CL " + (actor.CombatLevel?.ToString() ?? "?");mobHoverDial.Subject=actor.Name;
                mobHoverDial.SetDefense(actor.Shield,actor.MaxShield,actor.Hull,actor.MaxHull,actor.MaxHull!=0);
                mobHoverDial.Tick(Time.unscaledTime);mobHoverValues.text=mobHoverDial.tooltip;
                mobHover.style.left=Mathf.Clamp(point.x+18,4,Mathf.Max(4,root.resolvedStyle.width-288));
                mobHover.style.top=Mathf.Clamp(point.y+18,4,Mathf.Max(4,root.resolvedStyle.height-260));
                mobHover.style.display=DisplayStyle.Flex;mobHover.BringToFront();
            }

            /// <summary>Show live numeric details from an actual runtime hover, rather than relying on Editor-only tooltips.</summary>
            private void BindGaugeHover(ResourceDialWidget instrument)
            {
                instrument.RegisterCallback<PointerEnterEvent>(evt=>ShowGaugeHover(instrument,evt.position));
                instrument.RegisterCallback<PointerMoveEvent>(evt=>ShowGaugeHover(instrument,evt.position));
                instrument.RegisterCallback<PointerLeaveEvent>(_=>
                {if(hoveredGauge==instrument){hoveredGauge=null;gaugeHover.style.display=DisplayStyle.None;}});
            }

            /// <summary>Position full resource values beside the cursor, clamped to the active viewport.</summary>
            private void ShowGaugeHover(ResourceDialWidget instrument,Vector2 point)
            {
                hoveredGauge=instrument;gaugeHoverText.text=instrument.Subject+"\n"+instrument.tooltip;
                gaugeHover.style.left=Mathf.Clamp(point.x+16,4,Mathf.Max(4,root.resolvedStyle.width-348));
                gaugeHover.style.top=Mathf.Clamp(point.y+16,4,Mathf.Max(4,root.resolvedStyle.height-110));
                gaugeHover.style.display=DisplayStyle.Flex;gaugeHover.BringToFront();
            }

            /// <summary>Reconcile up to five other pilots without inventing telemetry for missing or off-sector contacts.</summary>
            private void RefreshCrew(Snapshot snapshot)
            {
                GroupMember[] members=snapshot.Group?.Members??Array.Empty<GroupMember>();
                bool joined = snapshot.Group?.Joined == true;
                if (joined && !joinedPreviously) SetBrowseTab(2);
                joinedPreviously = joined;
                groupEmpty.style.display=members.Length==0?DisplayStyle.Flex:DisplayStyle.None;
                for(int index=0;index<5;index++)
                {
                    bool active=index<members.Length;crewCards[index].style.display=active?DisplayStyle.Flex:DisplayStyle.None;
                    crewIds[index]=active?members[index].Id:0;if(!active)continue;
                    GroupMember member=members[index];ShipActor actor=snapshot.Players.FirstOrDefault(item=>item.Id==member.Id);
                    crewNames[index].text=member.Name;crewNames[index].tooltip=member.Name;crewDials[index].Subject=member.Name;
                    crewAssist[index].SetEnabled(Client.Ready && member.Id != snapshot.PlayerId);
                    crewDials[index].SetDefense(actor?.Shield??-1,actor?.MaxShield??-1,actor?.Hull??-1,actor?.MaxHull??-1,hasHull:true);
                    crewStatus[index].text=actor?.HasPosition==true?"IN SECTOR":"NO TELEMETRY";
                    crewCards[index].tooltip=member.Name+"\n"+crewDials[index].tooltip;
                }
            }

            /// <summary>Expose flight actions with the same native range and permission checks as the existing HUD.</summary>
            private void RefreshTarget(Snapshot s)
            {
                ShipActor actor = s.Players.Concat(s.Mobs).FirstOrDefault(a => a.Id == hud.contactId && a.HasPosition);
                bool ownShip = hud.contactId == s.PlayerId && s.SelectedTargetId == s.PlayerId;
                MapObject obj = actor == null && !ownShip ? Client.Target : null;
                selectedActor = actor?.Id ?? 0;
                targetName.text = ownShip ? Client.PilotName : actor?.Name ?? obj?.Name ?? "Free flight";
                float distance = actor != null ? actor.Position.Distance(s.Position) : obj != null ? obj.Position.Distance(s.Position) : -1;
                targetMeta.text = (ownShip ? "YOUR SHIP  /  SELF" : actor != null ? actor.IsPlayer ? "PILOT" : actor.Hostile ? "HOSTILE" : "NEUTRAL" : obj?.Kind == 25 ? "WRECK" : obj != null ? "DESTINATION" : "NO TARGET") +
                    (actor != null && !actor.IsPlayer ? " · CL " + (actor.CombatLevel?.ToString() ?? "?") : "") +
                    (ownShip ? "" : distance >= 0 ? $"  /  {distance / 1000:0.00} km" : "\nWarp along your heading");
                targetDefense.style.display = ownShip || actor != null && (actor.MaxHull > 0 || actor.MaxShield > 0)
                    ? DisplayStyle.Flex : DisplayStyle.None;
                assistTarget.style.display = actor != null ? DisplayStyle.Flex : DisplayStyle.None;
                assistTarget.SetEnabled(Client.Ready && actor?.HasPosition == true);
                if (ownShip)
                { targetDefense.Subject = Client.PilotName; targetDefense.SetDefense(s.Shield,s.MaxShield,s.Hull,s.MaxHull,true); }
                else if (actor != null)
                {
                    targetDefense.Subject=actor.Name;
                    targetDefense.SetDefense(actor.Shield,actor.MaxShield,actor.Hull,actor.MaxHull,actor.IsPlayer||actor.MaxHull!=0);
                }
                targetActions.style.display = ownShip || actor == null && obj == null ? DisplayStyle.None : DisplayStyle.Flex;
                fire.style.display = actor != null && !ownShip ? DisplayStyle.Flex : DisplayStyle.None;
                travelActions.style.display = actor == null && !ownShip ? DisplayStyle.Flex : DisplayStyle.None;
                bool canJumpstart = actor?.IsPlayer == true && actor.MaxHull > 0 && actor.Hull <= 0 && s.AvailableAbilities.Contains(65u);
                jumpstart.style.display = canJumpstart ? DisplayStyle.Flex : DisplayStyle.None;
                jumpstart.SetEnabled(Client.Ready && !Client.Incapacitated && s.CastingAbilityId == uint.MaxValue && s.SelectedTargetId == selectedActor);
                fire.text = actor?.IsPlayer == true ? "Invite" : s.WeaponsFiring ? "Cease fire" : "Fire";
                fire.EnableInClassList("danger", actor != null && !actor.IsPlayer && !ownShip);
                fire.SetEnabled(Client.Ready && !Client.Incapacitated && !ownShip && actor != null && (!actor.IsPlayer ||
                    (!s.Group.Joined || s.Group.IsLeader) && !s.Group.Members.Any(m => m.Id == actor.Id) && s.Group.Members.Length < 6));
                follow.SetEnabled(Client.Ready && !ownShip && (actor != null || obj != null));
                trade.style.display = actor?.IsPlayer == true && s.Verbs.Available(actor.Id, 6) == true ? DisplayStyle.Flex : DisplayStyle.None;
                trade.SetEnabled(Client.Ready && s.SelectedTargetId == selectedActor);
                bool active = s.WarpPhase == WarpPhase.Charging || s.WarpPhase == WarpPhase.Traveling;
                warp.text = active ? "Cancel warp" : "Warp";
                warp.SetEnabled(Client.Ready && !Client.Incapacitated && s.WarpPhase != WarpPhase.Recovering &&
                    (active || actor?.IsPlayer != true && (distance < 0 || distance >= 4000) && (obj?.Kind != 38 || distance <= 20000)));
                bool station = obj?.Kind == 12;
                dock.text = station ? "Dock" : "Enter gate";
                dock.style.display = obj != null && (station || obj.Kind == 11) ? DisplayStyle.Flex : DisplayStyle.None;
                dock.SetEnabled(Client.Ready && !Client.Incapacitated && obj != null &&
                    (station || obj.Kind == 11) && s.Verbs.Available(obj.Id, station ? (ushort)8 : (ushort)10) == true);
                lootOpen.style.display = obj?.Kind == 25 ? DisplayStyle.Flex : DisplayStyle.None;
                lootOpen.text = obj != null && s.OpenLootId == obj.Id ? "Close wreck" : "Open wreck inventory";
                lootOpen.SetEnabled(Client.Ready && obj != null &&
                    (s.OpenLootId == obj.Id || s.Verbs.Available(obj.Id, 3) == true));
            }

            /// <summary>Display server-owned effect leases and expire their rows with the next snapshot.</summary>
            private void RefreshBuffs(Snapshot s)
            {
                var entries = new List<(string Name, float Seconds)>();
                entries.AddRange(s.ActiveDeviceEffects.Select(e => (e.Name, e.RemainingSeconds)));
                entries.AddRange(s.ActiveBuffs.Where(e => !s.HasActiveDeviceSnapshot || e.Slot != 0)
                    .Select(e => (e.Slot == 13 && e.Name == "Maximize_Shields" ? "Shield Charging" : e.Name, e.RemainingSeconds)));
                if (LegacyDeviceFallback(s)) entries.Add((s.ActiveDeviceLabel, s.ActiveDeviceRemainingSeconds));
                effectsBadge.text = "Effects · " + entries.Count;
                effectsBadge.EnableInClassList("has-effects", entries.Count > 0);
                UpdateEffectsVisibility();
                activeEffectsTitle.text = "ACTIVE EFFECTS · " + entries.Count;
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
                    Text(row, entry.Name.Replace('_', ' '), "grow subtle"); Text(row, float.IsPositiveInfinity(entry.Seconds) ? "Sustained" : entry.Seconds < 0 ? "Syncing" : $"{Mathf.CeilToInt(entry.Seconds)}s", "subtle");
                }
            }

            /// <summary>Show live shortcut information above its actual slot in the standalone player.</summary>
            private void ShowSystemHover(int index)
            {
                if (index < 0 || index >= quickSlots.Length ||
                    quickSlots[index].resolvedStyle.display == DisplayStyle.None || itemPointer >= 0 ||
                    InputModalOpen || root.resolvedStyle.visibility != Visibility.Visible)
                { hoveredSystem = -1; systemHover.style.display = DisplayStyle.None; return; }
                hoveredSystem = index;
                systemHoverText.text = Client.AbilitiesUi.QuickSlotHelp(index, Client.CurrentSnapshot);
                Rect slot = quickSlots[index].worldBound;
                Vector2 point = root.WorldToLocal(slot.position);
                systemHover.style.width = Mathf.Min(340, root.resolvedStyle.width - 16);
                systemHover.style.left = Mathf.Clamp(point.x, 8, Mathf.Max(8, root.resolvedStyle.width - 348));
                systemHover.style.top = StyleKeyword.Auto;
                systemHover.style.bottom = Mathf.Max(8, root.resolvedStyle.height - point.y + 10);
                systemHover.style.display = DisplayStyle.Flex; systemHover.BringToFront();
            }

            /// <summary>Reveal the grouped effects popover while hovered or explicitly pinned.</summary>
            private void UpdateEffectsVisibility()
            {
                effectsPanel.style.display = effectsHovered || effectsPinned ? DisplayStyle.Flex : DisplayStyle.None;
                effectsBadge.EnableInClassList("active", effectsPinned);
                if (effectsHovered || effectsPinned) effectsPanel.BringToFront();
            }

            /// Returns:
            ///     Color: A consistent readable channel color; untagged server notices use warm gold.
            private static Color ChatChannelColor(string line)
            {
                int end = line != null && line.StartsWith("[", StringComparison.Ordinal) ? line.IndexOf(']') : -1;
                string channel = end > 0 ? line.Substring(1, end - 1).Replace(" ", "").ToLowerInvariant() : "system";
                switch (channel)
                {
                    case "group": return new Color(.47f, .91f, .69f);
                    case "private": case "tell": return new Color(.93f, .66f, 1f);
                    case "sector": return new Color(.51f, .82f, 1f);
                    case "local": return new Color(.88f, .93f, .96f);
                    case "generalchat": case "broadcast": return new Color(.58f, .91f, .94f);
                    case "market": return new Color(1f, .76f, .46f);
                    case "newplayers": return new Color(.87f, .91f, .57f);
                    case "system": return new Color(.9f, .81f, .59f);
                    default:
                        uint hash = 2166136261;
                        foreach (char value in channel) hash = unchecked((hash ^ value) * 16777619);
                        return Color.HSVToRGB((hash % 360) / 360f, .35f, .95f);
                }
            }

            /// <summary>Send the typed sector or group message through the authoritative network worker.</summary>
            private void SubmitChat()
            {
                string text = chatInput.value.Trim();
                if (!Client.Ready || text.Length == 0) return;
                if (chatChannel.value == "Sector" || chatChannel.value == "Group")
                    Client.SendChat(text, chatChannel.value == "Group");
                else Client.SendNamedChat(text, chatChannel.value == "New Players" ? "NewPlayers" : chatChannel.value);
                chatInput.value = ""; chatInput.Focus();
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

            /// <summary>Capture a source slot without changing inventory.</summary>
            private void BindItem(VisualElement element, uint container, int slot)
            {
                dropTargets.Add((element, container, slot));
                element.RegisterCallback<PointerEnterEvent>(evt => ShowItemHover(container, slot, evt.position));
                element.RegisterCallback<PointerMoveEvent>(evt => ShowItemHover(container, slot, evt.position));
                element.RegisterCallback<PointerLeaveEvent>(_ => itemHover.style.display = DisplayStyle.None);
                element.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button == 1 && (container == 2 || container == 3) && slot >= 3 && slot <= 8)
                    {
                        ammoPickerShortcut = -1;
                        OpenAmmoPicker(slot, evt.position);
                        evt.StopPropagation(); return;
                    }
                    if (evt.button != 0 || dragPhase == "pending") return;
                    if (container == 1) { hud.selectedCargo = slot; hud.inspectMount = false; }
                    else { hud.selectedMount = slot; hud.inspectMount = true; }
                    RefreshInspector(Client.CurrentSnapshot);
                    ItemView item = Item(Client.CurrentSnapshot, container, slot);
                    if (item == null) return;
                    itemHover.style.display = DisplayStyle.None;
                    FinishItem();
                    itemContainer = container; itemSlot = slot; heldItem = item;
                    itemDown = root.WorldToLocal(evt.position); itemSource = element;
                    sourcePosition = root.WorldToLocal(element.worldBound.center) - new Vector2(28, 28);
                    itemPointer = evt.pointerId;
                    root.Focus(); root.CapturePointer(evt.pointerId); evt.StopPropagation();
                });
            }

            /// <summary>Open the server-filtered ammunition choices beside a launcher hardpoint.</summary>
            public void OpenAmmoPicker(int mount, Vector2 pointer)
            {
                Snapshot snapshot = Client.CurrentSnapshot;
                ItemView launcher = snapshot?.Mounted != null && mount < snapshot.Mounted.Length ?
                    snapshot.Mounted[mount] : null;
                if (launcher == null || launcher.Type != 15 && launcher.Type != 16) return;
                ammoPickerMount = mount; ammoPickerKey = "";
                itemHover.style.display = DisplayStyle.None;
                Vector2 local = root.WorldToLocal(pointer);
                ammoPicker.style.left = Mathf.Clamp(local.x + 12, 4, Mathf.Max(4, root.resolvedStyle.width - 354));
                ammoPicker.style.top = Mathf.Clamp(local.y + 12, 4, Mathf.Max(4, root.resolvedStyle.height - 404));
                ammoEditShortcut.style.display = ammoPickerShortcut >= 0 ? DisplayStyle.Flex : DisplayStyle.None;
                ammoPicker.style.display = DisplayStyle.Flex;
                RefreshAmmoPicker(snapshot);
                ammoPicker.BringToFront();
            }

            /// <summary>Close the launcher menu without changing server inventory or preferences.</summary>
            private void CloseAmmoPicker()
            { ammoPickerMount = -1; ammoPickerShortcut = -1; ammoPicker.style.display = DisplayStyle.None; }

            /// <summary>Render only cargo stacks the server marked compatible with this launcher.</summary>
            private void RefreshAmmoPicker(Snapshot snapshot)
            {
                if (ammoPickerMount < 0 || snapshot == null) return;
                ItemView launcher = ammoPickerMount < snapshot.Mounted.Length ? snapshot.Mounted[ammoPickerMount] : null;
                if (launcher == null || launcher.Type != 15 && launcher.Type != 16) { CloseAmmoPicker(); return; }
                LauncherAmmoOption option = snapshot.LauncherAmmo?.Launchers.FirstOrDefault(entry => entry.Mount == ammoPickerMount);
                string key = option == null ? "pending" :
                    option.AutoReload + ":" + option.PreviousTemplate + ":" + string.Join("|",
                    option.CargoSlots.Select(slot => slot + "/" + snapshot.Cargo[slot]?.Template + "/" +
                        snapshot.Cargo[slot]?.Quantity + "/" + snapshot.Cargo[slot]?.Name + "/" + snapshot.Cargo[slot]?.IconId));
                if (key == ammoPickerKey) return;
                ammoPickerKey = key; ammoChoices.Clear();
                ammoReload.SetValueWithoutNotify(option?.AutoReload == true && option.PreviousTemplate != 0);
                ammoReload.SetEnabled(option != null && option.PreviousTemplate != 0);
                if (option == null)
                { ammoPickerStatus.text = "Waiting for server ammunition choices"; return; }
                var items = option.CargoSlots.Where(slot => slot >= 0 && slot < snapshot.Cargo.Length &&
                    snapshot.Cargo[slot] != null).Select(slot => snapshot.Cargo[slot]).ToArray();
                foreach (ItemView item in items)
                {
                    uint total = (uint)items.Where(candidate => candidate.Template == item.Template)
                        .Sum(candidate => (long)candidate.Quantity);
                    int cargoSlot = item.Slot;
                    var choice = ActionButton(ammoChoices, "", () =>
                    { Client.EquipCargo(cargoSlot, ammoPickerMount); CloseAmmoPicker(); });
                    choice.name = "ammo-choice-" + cargoSlot;
                    choice.style.flexDirection = FlexDirection.Row; choice.style.alignItems = Align.Center;
                    choice.style.minHeight = 43; choice.style.marginBottom = 3;
                    var icon = new Image { image = hud.icons.ForItem(item), pickingMode = PickingMode.Ignore };
                    icon.style.width = 28; icon.style.height = 28; icon.style.marginRight = 7; choice.Add(icon);
                    var name = Text(choice, item.Name + "  ×" + item.Quantity +
                        (total == item.Quantity ? "" : "  /  total " + total), "subtle");
                    name.style.width = 284; name.style.whiteSpace = WhiteSpace.NoWrap;
                    name.style.overflow = Overflow.Hidden; name.style.textOverflow = TextOverflow.Ellipsis;
                    choice.tooltip = item.Name + "\nStack " + item.Quantity + " · total " + total +
                        "\nQuality " + (item.Quality * 100).ToString("0") + "%";
                }
                string previous = option.PreviousTemplate == 0 ? "Select ammunition first" :
                    "Last selected: " + (items.FirstOrDefault(item => item.Template == option.PreviousTemplate)?.Name ??
                    "item #" + option.PreviousTemplate);
                ammoPickerStatus.text = (items.Length == 0 ? "No compatible ammunition in cargo.\n" : "") + previous;
            }

            /// <summary>Present live item details beside the pointer while inventory is open.</summary>
            private void ShowItemHover(uint container, int slot, Vector2 position)
            {
                Snapshot snapshot = Client.CurrentSnapshot;
                ItemView item = Item(snapshot, container, slot);
                if (item == null || !hud.EquipmentOpen || ammoPickerMount >= 0 || draggingItem || dragPhase != "idle")
                { itemHover.style.display = DisplayStyle.None; return; }
                itemHoverTitle.text = item.Name;
                string effects = string.Join("\n", item.EquippedEffects.Select(effect =>
                    effect.Label + (effect.Detail == effect.Label ? "" : " · " + effect.Detail)));
                itemHoverBody.text = $"{ItemCategory(item.Type)}  ·  ×{item.Quantity}" +
                    $"\nQuality {item.Quality * 100:0}%  ·  Structure {item.Structure * 100:0}%" +
                    (item.Range > 0 ? $"\nRange {item.Range:N0} m" : "") +
                    (item.Price > 0 ? $"\nValue {item.Price:N0} credits" : "") +
                    (item.MaxStack > 1 ? $"\nStack capacity {item.MaxStack:N0}" : "") +
                    (item.ActivationLabel.Length > 0 ? "\nActivate: " + item.ActivationLabel : "") +
                    (effects.Length > 0 ? "\n\n" + effects : "");
                Vector2 local = root.WorldToLocal(position);
                itemHover.style.left = Mathf.Clamp(local.x + 18, 4,
                    Mathf.Max(4, root.resolvedStyle.width - 314));
                itemHover.style.top = Mathf.Clamp(local.y + 18, 4,
                    Mathf.Max(4, root.resolvedStyle.height - 300));
                itemHover.style.display = DisplayStyle.Flex;
                itemHover.BringToFront();
            }

            /// <summary>Lift a native item icon after a deliberate panel-space movement.</summary>
            private void MoveItem(PointerMoveEvent evt)
            {
                if (itemPointer != evt.pointerId || itemSource == null || heldItem == null) return;
                itemHover.style.display = DisplayStyle.None;
                Vector2 point = root.WorldToLocal(evt.position);
                if (!draggingItem && Vector2.Distance(itemDown, point) < 7) return;
                draggingItem = true; dragPhase = "dragging"; quickbar.BringToFront();
                itemSource.AddToClassList("drag-source"); dragIcon.image = hud.icons.ForItem(heldItem);
                dragCaption.text = heldItem.Name;
                dragStatus.text = heldItem.Quantity + " ×  ·  " + (itemContainer == 3 ? "UNLOAD" : "MOVE / FIT");
                SetGhostPosition(point + new Vector2(14, -28));
                dragGhost.style.display = DisplayStyle.Flex; dragGhost.style.opacity = 1; dragGhost.BringToFront();
                UpdateDropFeedback(Client.CurrentSnapshot, evt.position); evt.StopPropagation();
            }

            /// Returns:
            ///     bool: Whether a visible compatible inventory target lies under the pointer.
            private bool FindDrop(Snapshot s, Vector2 point, out VisualElement destination, out uint container, out int slot)
            {
                destination = null; container = 0; slot = -1;
                hud.draggedContainer = itemContainer; hud.draggedSlot = itemSlot;
                // The root holds pointer capture during a drag. Pick can therefore report
                // the captured source or a child label instead of the magazine below it.
                foreach (var target in dropTargets.OrderBy(t => t.Container == 3 ? 0 : t.Container == 2 ? 1 : 2))
                {
                    if (target.Element.panel != root.panel ||
                        target.Element.resolvedStyle.display == DisplayStyle.None ||
                        !target.Element.worldBound.Contains(point)) continue;
                    int candidate = target.Slot < 0 ? FirstFreeCargo(s) : target.Slot;
                    if (!hud.CanInventoryDrop(s, target.Container, candidate)) continue;
                    destination = target.Element; container = target.Container; slot = candidate; return true;
                }
                return false;
            }

            /// <returns>int: Visible shortcut under an inventory drag, or minus one.</returns>
            private int ShortcutAt(Vector2 point)
            {
                if (itemContainer != 2 || heldItem == null) return -1;
                for (int i = 0; i < Client.AbilitiesUi.ShortcutCount(Client.CurrentSnapshot); i++)
                    if (quickSlots[i].worldBound.Contains(point)) return i;
                return -1;
            }

            /// <summary>Highlight compatible targets and distinguish an invalid hover immediately.</summary>
            private void UpdateDropFeedback(Snapshot s, Vector2 point)
            {
                hud.draggedContainer = itemContainer; hud.draggedSlot = itemSlot;
                foreach (var target in dropTargets)
                {
                    int slot = target.Slot < 0 ? FirstFreeCargo(s) : target.Slot;
                    target.Element.EnableInClassList("drop-valid", hud.CanInventoryDrop(s, target.Container, slot));
                    target.Element.RemoveFromClassList("drop-hover"); target.Element.RemoveFromClassList("drop-invalid");
                }
                int shortcut = ShortcutAt(point);
                foreach (VisualElement target in quickSlots)
                { target.EnableInClassList("drop-valid", itemContainer == 2); target.RemoveFromClassList("drop-hover"); }
                bool valid = FindDrop(s, point, out VisualElement hovered, out uint destination, out _);
                if (shortcut >= 0)
                {
                    quickSlots[shortcut].AddToClassList("drop-hover");
                    dragGhost.RemoveFromClassList("drop-rejected");
                    dragStatus.text = "ASSIGN SHORTCUT " + (shortcut + 1) + " · ITEM STAYS FITTED";
                    hud.ClearInventoryDrag(); return;
                }
                dragGhost.EnableInClassList("drop-rejected", !valid);
                dragStatus.text = heldItem.Quantity + " ×  ·  " + (!valid ? "NO COMPATIBLE SLOT" :
                    destination == 1 ? (itemContainer == 3 ? "UNLOAD TO CARGO" : "MOVE TO CARGO") :
                    heldItem.Type == 10 ? "LOAD MAGAZINE" : "FIT HARDPOINT");
                if (valid) hovered.AddToClassList("drop-hover");
                else
                    for (VisualElement hit = root.panel.Pick(point); hit != null; hit = hit.parent)
                        if (dropTargets.Any(t => t.Element == hit)) { hit.AddToClassList("drop-invalid"); break; }
                hud.ClearInventoryDrag();
            }

            /// <summary>Submit one inventory command and keep its visual pending until confirmed.</summary>
            private void DropItem(PointerUpEvent evt)
            {
                if (itemPointer != evt.pointerId) return;
                if (!draggingItem) { FinishItem(); return; }
                Snapshot s = Client.CurrentSnapshot;
                int shortcut = ShortcutAt(evt.position);
                if (shortcut >= 0 && Client.AbilitiesUi.BindEquippedItem(shortcut, itemSlot, s))
                {
                    FinishItem(); evt.StopPropagation(); return;
                }
                if (FindDrop(s, evt.position, out VisualElement target, out uint container, out int slot))
                {
                    pendingContainer = container == 3 && itemContainer == 1 ? 2u : container;
                    if (heldItem.Type == 10 && pendingContainer == 2) pendingContainer = 3;
                    pendingSlot = slot; previousDestination = Item(s, pendingContainer, slot);
                    animatedTarget = target; pendingMessage = s.Messages.LastOrDefault(); pendingStarted = Time.unscaledTime;
                    dragPhase = "pending"; dragStatus.text = "AWAITING SHIP CONFIRMATION";
                    animationFrom = ghostPosition; animationTo = root.WorldToLocal(target.worldBound.center) - new Vector2(28, 28);
                    animationStarted = Time.unscaledTime;
                    hud.SubmitInventoryDrop(s, container, slot); ClearDropFeedback(); ReleaseItemPointer();
                }
                else CancelItem("INCOMPATIBLE SLOT · RETURNED");
                hud.ClearInventoryDrag(); evt.StopPropagation();
            }

            /// <summary>Release capture without treating expected capture loss as cancellation.</summary>
            private void ReleaseItemPointer()
            {
                int pointer = itemPointer; itemPointer = -1; releasingPointer = true;
                if (pointer >= 0 && root.HasPointerCapture(pointer)) root.ReleasePointer(pointer);
                releasingPointer = false;
            }

            /// <summary>Return a rejected or cancelled item to its original visual position.</summary>
            private void CancelItem(string reason)
            {
                ReleaseItemPointer(); ClearDropFeedback(); hud.ClearInventoryDrag();
                if (!draggingItem) { FinishItem(); return; }
                dragPhase = "returning"; dragStatus.text = reason; dragGhost.AddToClassList("drop-rejected");
                animationFrom = ghostPosition; animationTo = sourcePosition; animationStarted = Time.unscaledTime;
            }

            /// <summary>Clear temporary target highlights without clearing a pending source.</summary>
            private void ClearDropFeedback()
            {
                foreach (var target in dropTargets)
                { target.Element.RemoveFromClassList("drop-valid"); target.Element.RemoveFromClassList("drop-hover"); target.Element.RemoveFromClassList("drop-invalid"); }
                foreach (VisualElement shortcut in quickSlots)
                { shortcut.RemoveFromClassList("drop-valid"); shortcut.RemoveFromClassList("drop-hover"); }

            }

            /// <summary>Advance animations and verify both ends against authoritative snapshots.</summary>
            private void TickItem(Snapshot s)
            {
                if (itemPointer >= 0 && (!Application.isFocused || Input.GetKeyDown(KeyCode.Escape))) CancelItem("MOVE CANCELLED");
                if (dragPhase == "idle" || dragPhase == "dragging") return;
                float elapsed = Time.unscaledTime - animationStarted;
                float t = Mathf.Clamp01(elapsed / (dragPhase == "returning" ? .28f : .22f));
                SetGhostPosition(Vector2.Lerp(animationFrom, animationTo, 1 - Mathf.Pow(1 - t, 3)));
                if (dragPhase == "pending")
                {
                    ItemView destination = Item(s, pendingContainer, pendingSlot);
                    bool sourceChanged = !SameItem(heldItem, Item(s, itemContainer, itemSlot));
                    if (sourceChanged && destination != null && destination.Template == heldItem.Template && !SameItem(previousDestination, destination))
                    {
                        dragPhase = "confirmed"; dragStatus.text = "TRANSFER CONFIRMED";
                        animatedTarget?.AddToClassList("drop-confirmed"); itemSource?.RemoveFromClassList("drag-source");
                        animationFrom = ghostPosition; animationStarted = Time.unscaledTime;
                    }
                    else if (Time.unscaledTime - pendingStarted > 5) CancelItem("NO CONFIRMATION · RETURNED");
                    else if (s != null && s.Messages.LastOrDefault() != pendingMessage && Time.unscaledTime - pendingStarted > .15f && RejectedMessage(s.Messages.LastOrDefault()))
                        CancelItem("SERVER REJECTED MOVE · RETURNED");
                }
                else if (dragPhase == "confirmed")
                { dragGhost.style.opacity = 1 - t; if (elapsed > .32f) FinishItem(); }
                else if (dragPhase == "returning" && elapsed >= .85f) FinishItem();
            }

            /// Returns:
            ///     bool: Whether a server message describes a failed inventory operation.
            private static bool RejectedMessage(string message)
            {
                string text = (message ?? "").ToLowerInvariant();
                return text.Contains("cannot") || text.Contains("unable") || text.Contains("invalid") || text.Contains("incompatible") || text.Contains("not enough") || text.Contains("full");
            }

            /// Returns:
            ///     bool: Whether both snapshot slots describe the same item identity and quantity.
            private static bool SameItem(ItemView first, ItemView second) => first == null ? second == null :
                second != null && first.Template == second.Template && first.Quantity == second.Quantity && first.Structure == second.Structure;

            /// <summary>Place the non-pickable icon card in root-local coordinates.</summary>
            private void SetGhostPosition(Vector2 position)
            { ghostPosition = position; dragGhost.style.left = position.x; dragGhost.style.top = position.y; }

            /// <summary>Restore visual state without mutating inventory.</summary>
            private void FinishItem()
            {
                ReleaseItemPointer(); ClearDropFeedback();
                itemSource?.RemoveFromClassList("drag-source"); animatedTarget?.RemoveFromClassList("drop-confirmed");
                itemSource = null; animatedTarget = null; heldItem = null; itemSlot = -1;
                draggingItem = false; dragPhase = "idle";
                dragGhost.RemoveFromClassList("drop-rejected"); dragGhost.style.display = DisplayStyle.None; hud.ClearInventoryDrag();
            }

            /// Returns:
            ///     ItemView: The snapshot item at a native container slot, or null when absent.
            private static ItemView Item(Snapshot s, uint container, int slot)
            {
                if (s == null || slot < 0) return null;
                ItemView[] items = container == 1 ? s.Cargo : container == 2 ? s.Mounted : s.Ammo;
                return slot < items.Length ? items[slot] : null;
            }

            /// <summary>Open the complete ship preview, cargo and equipment view used by the I shortcut.</summary>
            public void OpenFullFitting()
            {
                expandFitting?.Invoke(); hud.equipmentOpen = true; fitting.BringToFront();
            }

            /// <summary>Keep a window's header visible while its saved collapse state hides the contents.</summary>
            private void MakeCollapsible(VisualElement panel, VisualElement header, string key, Action expanded = null)
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
                foreach (VisualElement child in panel.Children().Where(child => child != header &&
                    child.name != "drag-grip-" + key).ToArray())
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
                    if (!collapsed) expanded?.Invoke();
                });
                toggle.name = "collapse-" + key;
                toggle.style.minHeight = 22; toggle.style.height = 24;
                toggle.style.paddingLeft = 7; toggle.style.paddingRight = 7;
                toggle.style.marginLeft = 8;
                apply();
                if (key == "overview") expandBrowse = () =>
                {
                    collapsed = false; PlayerPrefs.SetInt("42andbeyond.collapse." + key, 0);
                    PlayerPrefs.Save(); apply();
                };
                if (key == "fitting") expandFitting = () =>
                {
                    collapsed = false; PlayerPrefs.SetInt("42andbeyond.collapse." + key, 0);
                    PlayerPrefs.Save(); apply();
                };
            }

            /// <summary>Reveal the group roster and restore a collapsed crew card.</summary>
            private void OpenGroupRoster()
            {
                hud.crewTab = true;
                SetBrowseTab(2);
                menuOpen = false; menuPanel.style.display = DisplayStyle.None;
            }

            /// <summary>Switch the shared upper-left browser without duplicating its server-backed rows.</summary>
            private void SetBrowseTab(int index)
            {
                expandBrowse?.Invoke();
                browseTab = Mathf.Clamp(index, 0, 2);
                if (browseTab != 2) hud.crewTab = false;
                nearbyPane.style.display = browseTab == 0 ? DisplayStyle.Flex : DisplayStyle.None;
                navPane.style.display = browseTab == 1 ? DisplayStyle.Flex : DisplayStyle.None;
                crewPanel.style.display = browseTab == 2 ? DisplayStyle.Flex : DisplayStyle.None;
                for (int tab = 0; tab < browseTabs.Length; tab++)
                    browseTabs[tab].EnableInClassList("active", tab == browseTab);
            }

            /// <summary>Fit the three flight surfaces and unified lower strip inside the active viewport.</summary>
            private void LayoutFlightPanels(Snapshot snapshot)
            {
                float width = root.resolvedStyle.width, height = root.resolvedStyle.height;
                if (width < 400 || height < 400) return;
                float browseWidth = width < 1100 ? 288 : width < 1350 ? 318 : 390;
                scanner.style.width = browseWidth;
                int members = snapshot.Group?.Members?.Length ?? 0;
                scanner.style.height = browseTab == 2 ? Mathf.Min(Mathf.Max(185, height - 256),
                    111 + Mathf.Max(1, members) * 74) : Mathf.Clamp(height * .35f, 215, 310);
                if (!movedPanels.Contains(scanner)) { scanner.style.left = 18; scanner.style.top = 76; }
                targetPanel.style.width = width < 1100 ? 250 : width < 1350 ? 286 : 320;
                if (!movedPanels.Contains(targetPanel))
                { targetPanel.style.right = 18; targetPanel.style.top = 76; }
                float identityWidth = width < 1350 ? 350 : 420;
                int shortcuts = Client.AbilitiesUi.ShortcutCount(snapshot);
                float contentWidth = identityWidth + 145 + Mathf.Min(shortcuts, 8) * 72 + 150;
                float stripWidth = Mathf.Min(width - 28, Mathf.Min(1500, contentWidth));
                flightStrip.style.width = stripWidth;
                if (!movedPanels.Contains(flightStrip))
                { flightStrip.style.left = (width - stripWidth) / 2; flightStrip.style.bottom = 12; }
                if (!resizingStrip)
                {
                    float available = Mathf.Max(1, stripWidth - 149);
                    float status = statusShare < 0 ? identityWidth : available * statusShare;
                    float speed = speedShare < 0 ? 145 : available * speedShare;
                    float scale = Mathf.Min(1, available / 538);
                    status = Mathf.Clamp(status, 310 * scale, Mathf.Max(310 * scale, available - 228 * scale));
                    speed = Mathf.Clamp(speed, 132 * scale, Mathf.Max(132 * scale, available - status - 96 * scale));
                    ApplyStripWidths(status, speed);
                }
                if (!movedPanels.Contains(effectsPanel))
                {
                    effectsPanel.style.left = Mathf.Clamp(flightStrip.worldBound.x - root.worldBound.x + 12,
                        8, Mathf.Max(8, width - 268));
                    effectsPanel.style.top = StyleKeyword.Auto;
                    effectsPanel.style.bottom = Mathf.Max(12, height - (flightStrip.worldBound.y - root.worldBound.y) + 8);
                }
            }

            /// <summary>Resize neighboring flight sections while preserving the strip's overall width.</summary>
            private void AddStripDivider(int boundary)
            {
                var divider = Box(flightStrip, "strip-divider");
                divider.name = "flight-divider-" + boundary;
                divider.tooltip = "Drag left/right to resize neighboring sections. Reset layout restores defaults.";
                var dots = Text(divider, "⋮", ""); dots.pickingMode = PickingMode.Ignore;
                int pointer = -1;
                float start = 0, status = 0, speed = 0, systems = 0;
                divider.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    pointer = evt.pointerId; resizingStrip = true;
                    start = flightStrip.WorldToLocal(evt.position).x;
                    status = vitals.resolvedStyle.width; speed = propulsionSection.resolvedStyle.width;
                    systems = quickbarDock.resolvedStyle.width;
                    divider.CapturePointer(pointer); divider.AddToClassList("dragging");
                    evt.StopPropagation();
                });
                divider.RegisterCallback<PointerMoveEvent>(evt =>
                {
                    if (pointer != evt.pointerId) return;
                    float delta = flightStrip.WorldToLocal(evt.position).x - start;
                    if (boundary == 0)
                    {
                        delta = Mathf.Clamp(delta, Mathf.Min(0, 310 - status), Mathf.Max(0, speed - 132));
                        ApplyStripWidths(status + delta, speed - delta);
                    }
                    else
                    {
                        delta = Mathf.Clamp(delta, Mathf.Min(0, 132 - speed), Mathf.Max(0, systems - 96));
                        ApplyStripWidths(status, speed + delta);
                    }
                    float total = status + speed + systems;
                    statusShare = vitals.style.width.value.value / total;
                    speedShare = propulsionSection.style.width.value.value / total;
                    evt.StopPropagation();
                });
                divider.RegisterCallback<PointerUpEvent>(evt =>
                {
                    if (pointer != evt.pointerId) return;
                    divider.ReleasePointer(pointer); pointer = -1; resizingStrip = false;
                    divider.RemoveFromClassList("dragging");
                    PlayerPrefs.SetFloat("42andbeyond.strip.status", statusShare);
                    PlayerPrefs.SetFloat("42andbeyond.strip.speed", speedShare); PlayerPrefs.Save();
                    evt.StopPropagation();
                });
                divider.RegisterCallback<PointerCaptureOutEvent>(_ =>
                { pointer = -1; resizingStrip = false; divider.RemoveFromClassList("dragging"); });
            }

            /// <summary>Fit labels and instruments into the user's chosen section widths.</summary>
            private void ApplyStripWidths(float status, float speed)
            {
                vitals.style.width = status; propulsionSection.style.width = speed;
                float instrumentsLeft = Mathf.Min(155, Mathf.Max(80, status - 225));
                vitals.Q("resource-dial-assembly").style.left = instrumentsLeft;
                pilotLabel.style.width = sectorLabel.style.width = Mathf.Max(70, instrumentsLeft - 10);
                vitals.Q("flight-target-self").style.width = Mathf.Min(90, instrumentsLeft - 10);
                pilotLabel.style.overflow = sectorLabel.style.overflow = Overflow.Hidden;
                pilotLabel.style.textOverflow = sectorLabel.style.textOverflow = TextOverflow.Ellipsis;
            }

            /// <summary>Move a window with its header, clamping the header within the viewport.</summary>
            private void MakeMovable(VisualElement panel, VisualElement handle, string key)
            {
                initialPositions[panel] = (panel.style.left, panel.style.top, panel.style.right, panel.style.bottom);
                var grip = new Label("⋮⋮") { name = "drag-grip-" + key };
                grip.AddToClassList("window-drag-grip");
                grip.tooltip = "Drag to move window";
                grip.style.position = UnityEngine.UIElements.Position.Absolute;
                grip.style.left = 4; grip.style.top = 4;
                panel.Add(grip);
                if (handle is Label) handle.style.paddingLeft = 16;
                handle.pickingMode = PickingMode.Position;
                bool moving = false; Vector2 start = default, origin = default;
                VisualElement captured = null;
                foreach (VisualElement anchor in new[] { handle, grip })
                {
                    anchor.RegisterCallback<PointerDownEvent>(evt =>
                    {
                        if (evt.button != 0 || evt.target is Button || panel == quickbar && systemsEmbedded) return;
                        movedPanels.Add(panel);
                        if (panel == quickbar) quickbarMoved = true;
                        moving = true; captured = anchor; start = evt.position;
                        origin = panel.parent.WorldToLocal(panel.worldBound.position);
                        panel.style.position = UnityEngine.UIElements.Position.Absolute;
                        panel.style.left = origin.x; panel.style.top = origin.y;
                        panel.style.right = StyleKeyword.Auto; panel.style.bottom = StyleKeyword.Auto;
                        anchor.CapturePointer(evt.pointerId); evt.StopPropagation();
                    });
                    anchor.RegisterCallback<PointerMoveEvent>(evt =>
                    {
                        if (!moving || captured != anchor) return;
                        Vector2 point = origin + (Vector2)evt.position - start;
                        panel.style.left = Mathf.Clamp(point.x, 4,
                            Mathf.Max(4, root.resolvedStyle.width - panel.resolvedStyle.width - 4));
                        panel.style.top = Mathf.Clamp(point.y, 4,
                            Mathf.Max(4, root.resolvedStyle.height - panel.resolvedStyle.height - 4));
                        evt.StopPropagation();
                    });
                    anchor.RegisterCallback<PointerUpEvent>(evt =>
                    {
                        if (!moving || captured != anchor) return;
                        moving = false; captured = null;
                        anchor.ReleasePointer(evt.pointerId); evt.StopPropagation();
                    });
                }
            }

            /// <summary>Restore the fitting window and allow responsive centering on the next frame.</summary>
            private void ResetLayout()
            {
                SetSystemsExpanded(false);
                foreach (var entry in initialPositions)
                {
                    entry.Key.style.left = entry.Value.Left; entry.Key.style.top = entry.Value.Top;
                    entry.Key.style.right = entry.Value.Right; entry.Key.style.bottom = entry.Value.Bottom;
                }
                movedPanels.Clear(); positioned = false; quickbarMoved = false;
                statusShare = speedShare = -1;
                PlayerPrefs.DeleteKey("42andbeyond.strip.status");
                PlayerPrefs.DeleteKey("42andbeyond.strip.speed"); PlayerPrefs.Save();
                quickbar.style.top = StyleKeyword.Auto; quickbar.style.bottom = StyleKeyword.Auto;
            }

            /// <summary>Keep moved windows reachable after the client window changes size.</summary>
            private void ClampMovedPanels()
            {
                if (!positioned) return;
                foreach (VisualElement panel in movedPanels.Concat(new[] { fitting }))
                {
                    if (panel.resolvedStyle.display == DisplayStyle.None || panel == quickbar && systemsEmbedded) continue;
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
            ///     Button: A compact command in the merged flight deck menu.
            private Button AddFlightMenuAction(string caption, Action action)
            {
                Button button = ActionButton(menuPanel, caption, () =>
                {
                    menuOpen = false;
                    menuPanel.style.display = DisplayStyle.None;
                    action();
                });
                button.style.width = new Length(100, LengthUnit.Percent);
                button.style.minHeight = 27;
                button.style.marginBottom = 3;
                return button;
            }

            /// <summary>Keep the open menu beside the dragged flight deck panel and inside the viewport.</summary>
            private void PositionFlightMenu()
            {
                Rect anchor = flightStrip.worldBound;
                Rect viewport = root.worldBound;
                float width = menuPanel.resolvedStyle.width > 100 ? menuPanel.resolvedStyle.width : 188;
                float height = menuPanel.resolvedStyle.height > 100 ? menuPanel.resolvedStyle.height : 390;
                float x = anchor.xMax - viewport.x - width;
                float y = anchor.yMin - viewport.y - height - 8;
                menuPanel.style.left = Mathf.Clamp(x, 8, Mathf.Max(8, viewport.width - width - 8));
                menuPanel.style.top = Mathf.Clamp(y, 8, Mathf.Max(8, viewport.height - height - 8));
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
            public void Dispose() { FinishItem(); defense.Dispose(); reactor.Dispose(); targetDefense.Dispose(); mobHoverDial.Dispose(); foreach(var instrument in crewDials)instrument.Dispose(); Object.Destroy(gameObject); Object.Destroy(settings); }

            /// <summary>A layered resource instrument with authoritative values and a proportional energy track.</summary>
            private sealed class Gauge
            {
                public readonly VisualElement Root;
                private readonly VisualElement fill, track;
                private readonly Label value, percent;
                private readonly Color color;

                /// <summary>Create a resource card with readable numerals, a luminous rail and calibrated ticks.</summary>
                public Gauge(VisualElement parent, string title, Color color)
                {
                    this.color=color;
                    Root=Box(parent,"vital-gauge");Root.name="gauge-"+title.ToLowerInvariant().Replace(' ','-');
                    var row=Box(Root,"row spread");
                    var caption=Text(row,title.ToUpperInvariant(),"vital-caption");caption.style.color=color;
                    percent=Text(row,"","vital-percent");
                    value=Text(Root,"","vital-value");value.name=Root.name+"-value";
                    track=Box(Root,"vital-track");
                    fill=Box(track,"vital-fill");fill.style.backgroundColor=color;
                    Box(fill,"vital-sheen");
                    var ticks=Box(track,"vital-ticks");
                    for(int index=0;index<10;index++)Box(ticks,"vital-tick");
                    Root.pickingMode=PickingMode.Ignore;
                }

                /// <summary>Show actual capacity when known; never invent a 100-point shield capacity.</summary>
                public void Set(float fraction, float current, float maximum)
                {
                    bool known=maximum>=0&&fraction>=0;
                    value.text=known?$"{Mathf.Max(0,current):N0}  /  {maximum:N0}":fraction>=0?"Capacity unavailable":"Awaiting telemetry";
                    percent.text=fraction>=0?$"{Mathf.Clamp01(fraction)*100:0}%":"—";
                    Color signal=fraction>=0&&fraction<.25f?new Color(1,.38f,.23f):color;
                    fill.style.backgroundColor=signal;percent.style.color=signal;
                    Root.style.borderLeftColor=new Color(signal.r,signal.g,signal.b,.5f);
                    fill.style.width=Length.Percent(Mathf.Clamp01(fraction)*100);
                    Root.tooltip=known?$"{current:N0} of {maximum:N0}":"The server has not supplied this capacity.";
                }
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
