using System;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Present native peer offers without guessing inventory or confirmation state.</summary>
    public sealed class TradeHud
    {
        private readonly SpaceClient client;
        private readonly FlightHud hud;
        private Rect window = new Rect(420, 110, 650, 680);
        private string creditText = "0";
        private ulong displayedCredits = ulong.MaxValue;
        private string notice = "";
        private int dragCargo = -1, dragOffer = -1, cargoPage;
        private Vector2 dragStart;
        private bool dragging;
        private int dragControlId;
        private GUIStyle itemStyle;

        /// <summary>Keep the trade controls on Unity's main thread.</summary>
        public TradeHud(SpaceClient client, FlightHud hud)
        { this.client = client; this.hud = hud; }

        /// <returns>
        ///     bool: Whether the pointer is over the active native trade window.
        /// </returns>
        public bool ContainsPointer()
        {
            Snapshot state = client.CurrentSnapshot;
            if (state == null) return false;
            Vector2 pointer = new Vector2(Input.mousePosition.x / hud.Scale,
                (Screen.height - Input.mousePosition.y) / hud.Scale);
            if (state.Trade?.Open == true) return window.Contains(pointer);
            float width = Screen.width / hud.Scale;
            uint target = state.SelectedTargetId;
            bool offered = state.Verbs.Available(target, 6) == true &&
                (state.Players.Any(p => p.Id == target) || state.StationAvatars.Any(p => p.Id == target));
            if (offered) return new Rect(width - 265, 317, 234, 30).Contains(pointer);
            if (state.Station == null) return false;
            int peers = state.StationAvatars.Count(p =>
                (state.AggregateStation || p.Room == state.Station.CurrentRoom) && p.Id != state.PlayerId);
            return peers > 0 && new Rect(width - 265, 317, 234, 32 + Math.Min(4, peers) * 28).Contains(pointer);
        }

        /// <summary>Draw a selectable peer entry or the active six-slot exchange.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (snapshot == null) return;
            if (snapshot.Trade?.Open == true)
            {
                window.height = Mathf.Min(680, Mathf.Max(450, Screen.height / hud.Scale - 16));
                window.x = Mathf.Clamp(window.x, 8, Mathf.Max(8, Screen.width / hud.Scale - window.width - 8));
                window.y = Mathf.Clamp(window.y, 8, Mathf.Max(8, Screen.height / hud.Scale - window.height - 8));
                window = GUI.Window(781, window, _ => DrawWindow(snapshot), "", GUI.skin.window);
                return;
            }
            DrawEntry(snapshot);
        }

        /// <summary>Offer a native trade for an acknowledged peer target in space or station.</summary>
        private void DrawEntry(Snapshot snapshot)
        {
            if (snapshot.Station == null && hud.RetainedVisible) return;
            uint target = snapshot.SelectedTargetId;
            string name = snapshot.Players.FirstOrDefault(p => p.Id == target)?.Name ??
                snapshot.StationAvatars.FirstOrDefault(p => p.Id == target)?.Name;
            float width = Screen.width / hud.Scale;
            if (name != null && snapshot.Verbs.Available(target, 6) == true)
            {
                Rect button = new Rect(width - 265, 317, 234, 30);
                GUI.enabled = client.Ready;
                if (GUI.Button(button, "TRADE WITH " + name.ToUpperInvariant(), hud.ButtonStyle))
                    client.OpenTrade(target);
                GUI.enabled = true;
                return;
            }
            if (snapshot.Station == null) return;
            StationAvatar[] peers = snapshot.StationAvatars.Where(p =>
                (snapshot.AggregateStation || p.Room == snapshot.Station.CurrentRoom) && p.Id != snapshot.PlayerId).Take(4).ToArray();
            if (peers.Length == 0) return;
            Rect card = new Rect(width - 265, 317, 234, 32 + peers.Length * 28);
            hud.Panel(card);
            GUI.Label(new Rect(card.x + 9, card.y + 5, 216, 20), "PILOTS AVAILABLE TO TRADE", hud.SmallStyle);
            for (int index = 0; index < peers.Length; index++)
            {
                StationAvatar peer = peers[index];
                if (GUI.Button(new Rect(card.x + 9, card.y + 29 + index * 28, 216, 25),
                    peer.Name + "  ·  SELECT", hud.ButtonStyle)) client.SelectContact(peer.Id);
            }
        }

        /// <summary>Draw owned and partner offers with explicit server confirmation flags.</summary>
        private void DrawWindow(Snapshot snapshot)
        {
            TradeView trade = snapshot.Trade;
            Color panelColor = GUI.color;
            GUI.color = new Color(.015f, .035f, .055f, .98f);
            GUI.DrawTexture(new Rect(0, 0, window.width, window.height), Texture2D.whiteTexture);
            GUI.color = panelColor;
            Event input = Event.current;
            EventType pointerEvent = input.type;
            if (itemStyle == null)
                itemStyle = new GUIStyle(hud.SmallStyle)
                { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, wordWrap = false,
                    fontSize = Mathf.Max(14, hud.SmallStyle.fontSize) };
            HudWindowDrag.DrawGrip(new Rect(0, 0, window.width, window.height));
            GUI.Label(new Rect(36, 11, 450, 26), "PILOT EXCHANGE  /  " + trade.PeerName.ToUpperInvariant(), hud.SmallStyle);
            if (GUI.Button(new Rect(603, 9, 30, 25), "×", hud.ButtonStyle)) client.CancelTrade();
            GUI.DragWindow(new Rect(0, 0, 595, 37));
            GUI.Label(new Rect(20, 42, 294, 20), trade.OwnConfirmed ? "YOUR OFFER  ·  CONFIRMED" : "YOUR OFFER  ·  EDITING", hud.SmallStyle);
            GUI.Label(new Rect(336, 42, 294, 20), trade.PeerConfirmed ? "PARTNER OFFER  ·  CONFIRMED" : "PARTNER OFFER  ·  WAITING", hud.SmallStyle);
            Rect ownPanel = new Rect(15, 65, 303, 206), peerPanel = new Rect(332, 65, 303, 206);
            GUI.Box(ownPanel, ""); GUI.Box(peerPanel, "");
            DrawSlots(trade.Own.Items, ownPanel, true, pointerEvent, input.mousePosition);
            DrawSlots(trade.Peer.Items, peerPanel, false, pointerEvent, input.mousePosition);
            if (displayedCredits != trade.Own.Money)
            { creditText = trade.Own.Money.ToString(); displayedCredits = trade.Own.Money; }
            GUI.Label(new Rect(23, 280, 95, 20), "CREDITS", hud.SmallStyle);
            creditText = GUI.TextField(new Rect(116, 278, 112, 25), creditText, 10);
            if (GUI.Button(new Rect(234, 278, 78, 25), "SET", hud.ButtonStyle))
            {
                if (uint.TryParse(creditText, out uint credits)) client.SetTradeCredits(credits);
                else notice = "Enter 0–4,294,967,295 credits.";
            }
            GUI.Label(new Rect(340, 280, 286, 22), "PARTNER CREDITS   " + trade.Peer.Money, hud.SmallStyle);
            Rect cargoPanel = new Rect(15, 312, 620, window.height - 368);
            GUI.Box(cargoPanel, "");
            GUI.Label(new Rect(23, 314, 285, 19), "CARGO  ·  CLICK OR DRAG TO OFFER", hud.SmallStyle);
            int[] cargoSlots = Enumerable.Range(0, snapshot.Cargo.Length)
                .Where(slot => slot < snapshot.CargoCapacity && snapshot.Cargo[slot] != null).ToArray();
            float cargoRowHeight = Mathf.Min(36, (cargoPanel.height - 28) / 2);
            int rowsPerPage = Mathf.Max(2, Mathf.FloorToInt((cargoPanel.height - 28) / cargoRowHeight));
            int pageCount = Mathf.Max(1, (cargoSlots.Length + rowsPerPage - 1) / rowsPerPage);
            if (pointerEvent == EventType.ScrollWheel && cargoPanel.Contains(input.mousePosition))
            {
                cargoPage += input.delta.y > 0 ? 1 : -1;
                input.Use();
            }
            cargoPage = Mathf.Clamp(cargoPage, 0, pageCount - 1);
            GUI.enabled = cargoPage > 0;
            if (GUI.Button(new Rect(516, 313, 26, 21), "‹", hud.ButtonStyle)) cargoPage--;
            GUI.enabled = cargoPage < pageCount - 1;
            if (GUI.Button(new Rect(548, 313, 26, 21), "›", hud.ButtonStyle)) cargoPage++;
            GUI.enabled = true;
            GUI.Label(new Rect(577, 315, 50, 19), $"{cargoPage + 1}/{pageCount}", hud.SmallStyle);
            Rect[] visibleCargo = new Rect[rowsPerPage];
            int[] visibleSlots = Enumerable.Repeat(-1, rowsPerPage).ToArray();
            for (int row = 0; row < rowsPerPage; row++)
            {
                int index = cargoPage * rowsPerPage + row;
                if (index >= cargoSlots.Length) break;
                int slot = cargoSlots[index];
                ItemView item = snapshot.Cargo[slot];
                Rect bounds = new Rect(23, 339 + row * cargoRowHeight, 602, cargoRowHeight - 3);
                visibleCargo[row] = bounds; visibleSlots[row] = slot;
                DrawItem(bounds, item, "OFFER", 54);
                if (pointerEvent == EventType.MouseDown && bounds.Contains(input.mousePosition))
                    BeginDrag(cargoSlot: slot, offerSlot: -1, pointer: input.mousePosition);
            }
            if (pointerEvent == EventType.MouseDrag && (dragCargo >= 0 || dragOffer >= 0))
            { dragging = dragging || Vector2.Distance(dragStart, input.mousePosition) > 6; input.Use(); }
            if (pointerEvent == EventType.MouseUp && (dragCargo >= 0 || dragOffer >= 0))
            {
                bool cargoClick = !dragging && visibleCargo.Where((bounds, row) =>
                    visibleSlots[row] == dragCargo && bounds.Contains(input.mousePosition)).Any();
                bool offerClick = !dragging && dragOffer >= 0 &&
                    OfferSlotRect(ownPanel, dragOffer).Contains(input.mousePosition);
                if (dragCargo >= 0 && (cargoClick || dragging && ownPanel.Contains(input.mousePosition)))
                    OfferCargo(snapshot, dragCargo);
                else if (dragOffer >= 0 && (offerClick || dragging && cargoPanel.Contains(input.mousePosition)))
                    RemoveOffer(snapshot, dragOffer);
                dragCargo = dragOffer = -1; dragging = false;
                if (GUIUtility.hotControl == dragControlId) GUIUtility.hotControl = 0;
                dragControlId = 0; input.Use();
            }
            if (dragging && (dragCargo >= 0 || dragOffer >= 0) && pointerEvent == EventType.Repaint)
            {
                ItemView moving = dragCargo >= 0 ? snapshot.Cargo[dragCargo] : trade.Own.Items[dragOffer];
                if (moving != null) DrawItem(new Rect(input.mousePosition.x + 12,
                    input.mousePosition.y + 12, 242, 28), moving,
                    dragCargo >= 0 ? "OFFER" : "RETURN", 19);
            }
            GUI.Label(new Rect(18, window.height - 45, 395, 34), string.IsNullOrEmpty(GUI.tooltip) ?
                trade.OwnConfirmed ? "Editing an offer resets both confirmations." : notice : GUI.tooltip,
                hud.SmallStyle);
            GUI.enabled = !trade.OwnConfirmed;
            if (GUI.Button(new Rect(425, window.height - 44, 122, 30), trade.OwnConfirmed ? "CONFIRMED" : "CONFIRM", hud.ButtonStyle)) client.ConfirmTrade();
            GUI.enabled = true;
            if (GUI.Button(new Rect(554, window.height - 44, 81, 30), "CANCEL", hud.ButtonStyle)) client.CancelTrade();
        }

        /// <summary>Draw six native offer slots; clicking an owned item requests its return.</summary>
        private void DrawSlots(System.Collections.Generic.IReadOnlyList<ItemView> slots, Rect panel,
            bool owned, EventType pointerEvent, Vector2 pointer)
        {
            for (int slot = 0; slot < 6; slot++)
            {
                ItemView item = slots[slot];
                Rect bounds = OfferSlotRect(panel, slot);
                if (item == null) GUI.Label(bounds, $"{slot + 1:00}   Empty", hud.SmallStyle);
                else DrawItem(bounds, item, owned ? "RETURN" : "OFFERED", 22);
                if (owned && item != null && pointerEvent == EventType.MouseDown && bounds.Contains(pointer))
                    BeginDrag(cargoSlot: -1, offerSlot: slot, pointer: pointer);
            }
        }

        /// <returns>
        ///     Rect: The row occupied by one native offer slot.
        /// </returns>
        private static Rect OfferSlotRect(Rect panel, int slot)
        { return new Rect(panel.x + 7, panel.y + 7 + slot * 32, panel.width - 14, 29); }

        /// <summary>Track a press before any IMGUI button can consume it.</summary>
        private void BeginDrag(int cargoSlot, int offerSlot, Vector2 pointer)
        {
            dragCargo = cargoSlot; dragOffer = offerSlot; dragStart = pointer; dragging = false;
            dragControlId = GUIUtility.GetControlID(FocusType.Passive);
            GUIUtility.hotControl = dragControlId;
            Event.current.Use();
        }

        /// <summary>Render a native item icon with bounded text and a full hover tooltip.</summary>
        private void DrawItem(Rect bounds, ItemView item, string action, int maximumNameCharacters)
        {
            GUI.Box(bounds, "");
            Texture2D icon = hud.ItemIcons.ForItem(item);
            float iconSize = Mathf.Min(32, bounds.height - 6);
            if (icon != null) GUI.DrawTexture(new Rect(bounds.x + 4, bounds.y + 3, iconSize, iconSize), icon, ScaleMode.ScaleToFit);
            string name = Ellipsis(item.Name, maximumNameCharacters);
            var content = new GUIContent($"{name}   ×{item.Quantity}   ·   {action}",
                $"{item.Name}  ×{item.Quantity}  |  Quality {item.Quality:0.00}  |  Builder {item.BuilderName}");
            GUI.Label(new Rect(bounds.x + iconSize + 9, bounds.y, bounds.width - iconSize - 15, bounds.height), content, itemStyle);
        }

        /// <returns>
        ///     string: A single-line item label that fits the offer row.
        /// </returns>
        private static string Ellipsis(string name, int maximumCharacters)
        { return name.Length <= maximumCharacters ? name : name.Substring(0, maximumCharacters - 1) + "…"; }

        /// <summary>Offer a full cargo stack into the first vacant native trade slot.</summary>
        private void OfferCargo(Snapshot snapshot, int cargoSlot)
        {
            if (snapshot.Trade?.Open != true) return;
            int offerSlot = -1;
            for (int slot = 0; slot < snapshot.Trade.Own.Items.Count; slot++)
                if (snapshot.Trade.Own.Items[slot] == null) { offerSlot = slot; break; }
            if (offerSlot < 0) { notice = "Your six offer slots are full."; return; }
            ItemView item = snapshot.Cargo[cargoSlot];
            if (item != null) client.MoveInventory(new InventoryMove(1, cargoSlot, 16, offerSlot, item.Quantity));
        }

        /// <summary>Return an offered stack to a compatible or empty cargo slot.</summary>
        private void RemoveOffer(Snapshot snapshot, int offerSlot)
        {
            if (snapshot.Trade?.Open != true) return;
            ItemView item = snapshot.Trade.Own.Items[offerSlot];
            if (item == null) return;
            client.MoveInventory(new InventoryMove(16, offerSlot, 1, 0, item.Quantity));
        }
    }
}
