using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Browse component suppliers and trade through server-owned station offers.</summary>
    public sealed class ComponentMarketHud
    {
        private readonly SpaceClient client;
        private readonly FlightHud hud;
        private readonly List<ItemView> choices = new List<ItemView>();
        private readonly HashSet<uint> choiceIds = new HashSet<uint>();
        private Vector2 componentScroll, supplierScroll, position, dragOffset;
        private Rect window;
        private bool open, positioned, dragging, inputFocused;
        private string search = "";
        private ItemView selected;
        private uint lastStation;
        private GUIStyle title, body, small, eyebrow, rowButton, searchStyle;
        private static readonly Color Cyan = new Color(.38f, .8f, .9f);
        private static readonly Color Gold = new Color(.91f, .73f, .39f);

        /// <returns>bool: Whether the component exchange overlay is open.</returns>
        public bool IsOpen => open;
        /// <returns>bool: Whether its search field owns keyboard input.</returns>
        public bool InputFocused => open && inputFocused;

        /// <summary>Bind market presentation to the client and shared native item icons.</summary>
        public ComponentMarketHud(SpaceClient client, FlightHud hud)
        { this.client = client; this.hud = hud; }

        /// <summary>Open the exchange for one item, or browse current requirements and cargo.</summary>
        public void Open(ItemView item = null)
        {
            open = true;
            lastStation = client.CurrentSnapshot?.Station?.Id ?? 0;
            client.QueryComponentStock();
            if (item != null) Select(item);
            else if (selected != null) client.QueryComponentMarket(selected.Template);
        }

        /// <summary>Dismiss the modal overlay and release its drag state.</summary>
        public void Close() { open = false; dragging = false; inputFocused = false; }

        /// <returns>bool: Whether the modal backdrop owns this HUD-coordinate pointer.</returns>
        public bool ContainsPointer(float x, float y) => open;

        /// <summary>Draw a searchable component list beside station supplier and trade cards.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (!open || snapshot == null) return;
            EnsureStyles();
            float canvasWidth = Screen.width / hud.Scale, canvasHeight = Screen.height / hud.Scale;
            float width = Mathf.Min(1060, canvasWidth - 40), height = Mathf.Min(660, canvasHeight - 40);
            if (!positioned)
            { position = new Vector2((canvasWidth - width) * .5f, (canvasHeight - height) * .5f); positioned = true; }
            position.x = Mathf.Clamp(position.x, 10, Mathf.Max(10, canvasWidth - width - 10));
            position.y = Mathf.Clamp(position.y, 10, Mathf.Max(10, canvasHeight - height - 10));
            window = new Rect(position, new Vector2(width, height));
            HandleDrag();
            Fill(new Rect(0, 0, canvasWidth, canvasHeight), new Color(.003f, .009f, .016f, .85f));
            Fill(window, new Color(.014f, .025f, .036f, .99f));
            Fill(new Rect(window.x, window.y, width, 3), Cyan);
            Fill(new Rect(window.x + 1, window.y + 3, width - 2, 77), new Color(.022f, .043f, .058f));
            GUI.Label(new Rect(window.x + 22, window.y + 15, width - 190, 18), "STATION NETWORK  /  COMPONENT EXCHANGE", eyebrow);
            GUI.Label(new Rect(window.x + 22, window.y + 37, width - 190, 30), "Source & trade components", title);
            if (GUI.Button(new Rect(window.xMax - 104, window.y + 23, 82, 32), "Close", hud.ButtonStyle))
            { Close(); return; }
            RefreshChoices(snapshot);
            if (selected == null && choices.Count > 0) Select(choices[0]);
            uint station = snapshot.Station?.Id ?? 0;
            if (lastStation != station)
            {
                lastStation = station; client.QueryComponentStock();
                if (selected != null) client.QueryComponentMarket(selected.Template);
            }
            float leftWidth = Mathf.Min(280, width * .29f);
            Rect selector = new Rect(window.x + 18, window.y + 97, leftWidth, height - 153);
            DrawSelector(snapshot, selector);
            Rect suppliers = new Rect(selector.xMax + 22, selector.y, width - leftWidth - 58, selector.height);
            DrawSuppliers(snapshot, suppliers);
            Fill(new Rect(window.x + 18, window.yMax - 46, width - 36, 1), new Color(.18f, .35f, .42f, .65f));
            GUI.Label(new Rect(window.x + 22, window.yMax - 34, width - 230, 23),
                "Prices and transfers are confirmed by the station server.", small);
            GUI.Label(new Rect(window.xMax - 203, window.yMax - 34, 180, 23),
                snapshot.Credits.ToString("N0") + " CREDITS", eyebrow);
        }

        /// <summary>Build a unique inventory of current recipe ingredients and owned components.</summary>
        private void RefreshChoices(Snapshot snapshot)
        {
            choices.Clear(); choiceIds.Clear();
            foreach (ItemView item in snapshot.Production.Components.Where(item => item != null)
                .Concat(snapshot.Cargo.Where(item => item != null && item.Type == 13))
                .Concat(snapshot.MarketItems.Where(item => item != null))
                .Concat(selected == null ? Enumerable.Empty<ItemView>() : new[] { selected }))
                if (choiceIds.Add(item.Template)) choices.Add(item);
        }

        /// <summary>Select one component and request its current station source quotes.</summary>
        private void Select(ItemView item)
        {
            selected = item;
            supplierScroll = Vector2.zero;
            client.QueryComponentMarket(item.Template);
        }

        /// <summary>Draw the scrollable, searchable set of ingredients and cargo components.</summary>
        private void DrawSelector(Snapshot snapshot, Rect rect)
        {
            GUI.Label(new Rect(rect.x, rect.y, rect.width, 22), "COMPONENTS", eyebrow);
            GUI.SetNextControlName("component-market-search");
            search = GUI.TextField(new Rect(rect.x, rect.y + 29, rect.width, 32), search, searchStyle);
            inputFocused = GUI.GetNameOfFocusedControl() == "component-market-search";
            if (string.IsNullOrEmpty(search))
                GUI.Label(new Rect(rect.x + 10, rect.y + 35, rect.width - 20, 22), "Search component names…", small);
            GUILayout.BeginArea(new Rect(rect.x, rect.y + 74, rect.width, rect.height - 74));
            componentScroll = GUILayout.BeginScrollView(componentScroll);
            foreach (ItemView item in choices.Where(item => string.IsNullOrWhiteSpace(search) ||
                item.Name.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0))
            {
                float rowHeight = Mathf.Max(70, body.CalcHeight(new GUIContent(item.Name), rect.width - 88) + 36);
                Rect row = GUILayoutUtility.GetRect(0, rowHeight, GUILayout.ExpandWidth(true));
                bool active = selected?.Template == item.Template;
                if (GUI.Button(row, GUIContent.none, rowButton)) Select(item);
                Fill(new Rect(row.x, row.y, 3, row.height), active ? Gold : new Color(.17f, .31f, .38f));
                GUI.DrawTexture(new Rect(row.x + 10, row.y + 12, 44, 44), hud.ItemIcons.ForItem(item), ScaleMode.ScaleToFit);
                GUI.Label(new Rect(row.x + 63, row.y + 9, row.width - 72, row.height - 31), item.Name, body);
                uint owned = (uint)snapshot.Cargo.Where(cargo => cargo?.Template == item.Template).Sum(cargo => (long)cargo.Quantity);
                GUI.Label(new Rect(row.x + 63, row.yMax - 25, row.width - 72, 20), owned + " in cargo", small);
                GUILayout.Space(5);
            }
            if (choices.Count == 0)
                GUILayout.Label("Choose a manufacturing formula or carry components to browse their suppliers.", body);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        /// <summary>Draw the selected component header and its authoritative supplier cards.</summary>
        private void DrawSuppliers(Snapshot snapshot, Rect rect)
        {
            if (selected == null)
            { GUI.Label(new Rect(rect.x, rect.y + 30, rect.width, 80), "Select a component to find its stations and vendors.", body); return; }
            GUI.DrawTexture(new Rect(rect.x, rect.y + 2, 68, 68), hud.ItemIcons.ForItem(selected), ScaleMode.ScaleToFit);
            GUI.Label(new Rect(rect.x + 84, rect.y, rect.width - 180, 59), selected.Name, title);
            ulong owned = (ulong)snapshot.Cargo.Where(item => item?.Template == selected.Template).Sum(item => (long)item.Quantity);
            GUI.Label(new Rect(rect.x + 84, rect.y + 61, rect.width - 84, 20), owned + " IN CARGO  ·  TEMPLATE " + selected.Template, small);
            if (GUI.Button(new Rect(rect.xMax - 88, rect.y + 4, 88, 30), "Refresh", hud.ButtonStyle))
                client.QueryComponentMarket(selected.Template);
            Fill(new Rect(rect.x, rect.y + 93, rect.width, 1), new Color(.19f, .37f, .44f, .7f));
            GUILayout.BeginArea(new Rect(rect.x, rect.y + 107, rect.width, rect.height - 107));
            supplierScroll = GUILayout.BeginScrollView(supplierScroll);
            if (!snapshot.ComponentMarkets.TryGetValue(selected.Template, out ComponentMarketView market))
                GUILayout.Label("Locating station suppliers…", body);
            else if (market.Sources.Count == 0)
                GUILayout.Label("No station vendor lists this component. Explore, loot, or manufacture it.", body);
            else
            {
                uint current = snapshot.Station?.Id ?? 0;
                foreach (ComponentMarketSource source in market.Sources.OrderBy(source => source.Station == current ? 0 : 1)
                    .ThenBy(source => source.BuyPrice > 0 ? 0 : 1).ThenBy(source => source.BuyPrice).ThenBy(source => source.StationName))
                    DrawSupplier(snapshot, market, source);
            }
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        /// <summary>Offer direct buy/sell actions only at the current station, or developer travel.</summary>
        private void DrawSupplier(Snapshot snapshot, ComponentMarketView market, ComponentMarketSource source)
        {
            bool here = snapshot.Station != null && source.Station == snapshot.Station.Id;
            float width = Mathf.Max(300, window.width - Mathf.Min(280, window.width * .29f) - 80);
            float nameHeight = Mathf.Max(24, body.CalcHeight(new GUIContent(source.StationName), width - 132));
            string vendorLabel = source.SectorName + "  /  " + source.VendorName;
            float vendorHeight = Mathf.Max(21, small.CalcHeight(new GUIContent(vendorLabel), width - 30));
            float rowHeight = nameHeight + vendorHeight + 96;
            Rect row = GUILayoutUtility.GetRect(0, rowHeight, GUILayout.ExpandWidth(true));
            Fill(row, here ? new Color(.053f, .064f, .056f) : new Color(.022f, .039f, .052f));
            Fill(new Rect(row.x, row.y, 3, row.height), here ? Gold : Cyan * .65f);
            GUI.Label(new Rect(row.x + 15, row.y + 10, row.width - 139, nameHeight), source.StationName, body);
            Color oldColor = GUI.color; GUI.color = here ? Gold : Cyan;
            GUI.Label(new Rect(row.xMax - 119, row.y + 12, 106, 19), here ? "DOCKED HERE" : "REMOTE STATION", eyebrow);
            GUI.color = oldColor;
            float y = row.y + nameHeight + 15;
            GUI.Label(new Rect(row.x + 15, y, row.width - 30, vendorHeight), vendorLabel, small);
            string buyQuote = source.BuyPrice > 0 ? source.BuyPrice.ToString("N0") + " cr" : "Not sold here";
            string sellQuote = source.SellPrice > 0 ? source.SellPrice.ToString("N0") + " cr" : "No buy offer";
            GUI.Label(new Rect(row.x + 15, y + vendorHeight + 5, row.width - 30, 22),
                "BUY  " + buyQuote + "     SELL  " + sellQuote + "  / item", eyebrow);
            float buttonY = y + vendorHeight + 34;
            bool previous = GUI.enabled;
            if (here)
            {
                GUI.enabled = previous && client.Ready && source.BuyPrice > 0 && source.BuyPrice <= snapshot.Credits;
                if (GUI.Button(new Rect(row.x + 15, buttonY, 78, 28), "BUY 1", hud.ButtonStyle))
                    client.BuyMarketComponent(source.Npc, selected.Template, 1);
                GUI.enabled = previous && client.Ready && source.BuyPrice > 0 && source.BuyPrice <= snapshot.Credits / 5;
                if (GUI.Button(new Rect(row.x + 99, buttonY, 78, 28), "BUY 5", hud.ButtonStyle))
                    client.BuyMarketComponent(source.Npc, selected.Template, 5);
                ItemView cargo = snapshot.Cargo.FirstOrDefault(item => item?.Template == selected.Template);
                GUI.enabled = previous && client.Ready && source.SellPrice > 0 && cargo != null;
                if (GUI.Button(new Rect(row.x + 191, buttonY, 78, 28), "SELL 1", hud.ButtonStyle))
                    client.SellMarketComponent(source.Npc, cargo.Slot, 1);
                if (GUI.Button(new Rect(row.x + 275, buttonY, Mathf.Max(105, row.width - 290), 28),
                    cargo == null ? "SELL STACK" : "SELL " + cargo.Quantity, hud.ButtonStyle))
                    client.SellMarketComponent(source.Npc, cargo.Slot, cargo.Quantity);
            }
            else if (market.DevEnabled)
            {
                GUI.enabled = previous && client.Ready;
                if (GUI.Button(new Rect(row.x + 15, buttonY, 164, 28), "DEV · TRAVEL HERE", hud.ButtonStyle))
                    client.TravelToMarketStation(source.Station);
                GUI.Label(new Rect(row.x + 192, buttonY + 5, row.width - 207, 22), "Dock here to buy or sell.", small);
            }
            else GUI.Label(new Rect(row.x + 15, buttonY + 4, row.width - 30, 23), "Travel to this station to buy or sell.", small);
            GUI.enabled = previous;
            GUILayout.Space(9);
        }

        /// <summary>Move the overlay using its title while preserving the viewport boundary.</summary>
        private void HandleDrag()
        {
            Event input = Event.current;
            Rect titlebar = new Rect(window.x, window.y, window.width - 124, 78);
            if (input.type == EventType.MouseDown && input.button == 0 && titlebar.Contains(input.mousePosition))
            { dragging = true; dragOffset = input.mousePosition - position; input.Use(); }
            if (dragging && input.type == EventType.MouseDrag)
            { position = input.mousePosition - dragOffset; input.Use(); }
            if (dragging && input.rawType == EventType.MouseUp) { dragging = false; input.Use(); }
        }

        /// <summary>Create the restrained exchange typography once.</summary>
        private void EnsureStyles()
        {
            if (title != null) return;
            title = new GUIStyle(hud.BodyStyle) { fontSize = 20, fontStyle = FontStyle.Bold, wordWrap = true };
            body = new GUIStyle(hud.BodyStyle) { fontSize = 15, wordWrap = true };
            small = new GUIStyle(hud.SmallStyle) { fontSize = 12, wordWrap = true };
            eyebrow = new GUIStyle(hud.SmallStyle) { fontSize = 11, fontStyle = FontStyle.Bold, wordWrap = false };
            rowButton = new GUIStyle(hud.ButtonStyle) { padding = new RectOffset(8, 8, 8, 8) };
            searchStyle = new GUIStyle(GUI.skin.textField) { font = hud.BodyStyle.font, fontSize = 14, padding = new RectOffset(10, 8, 7, 5) };
        }

        /// <summary>Fill an exchange panel while preserving the caller's GUI tint.</summary>
        private static void Fill(Rect rect, Color color)
        { Color old = GUI.color; GUI.color = color; GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = old; }
    }
}
