//! Native player trades reserve offers, reset approvals on edits, and commit both owners together.
use super::*;
use crate::inventory::{Inventory, ItemInstance, ItemTransfer};
use crate::items::ItemCatalog;
use crate::trade_store::TradeParty;

const TRADE_SLOTS: usize = 6;

#[derive(Clone)]
struct OfferedItem {
    cargo_slot: usize,
    item: ItemInstance,
}

#[derive(Clone)]
struct Offer {
    credits: u64,
    items: Vec<Option<OfferedItem>>,
    confirmed: bool,
}

pub(super) struct PlayerTrade {
    parties: [u32; 2],
    epochs: [u64; 2],
    baseline: [Inventory; 2],
    offers: [Offer; 2],
    expires: Instant,
}

impl PlayerTrade {
    /// Returns:
    ///     Inventory: Cargo display with offered quantities reserved in their original slots.
    fn displayed(&self, side: usize, inventory: &Inventory) -> Inventory {
        let mut result = inventory.clone();
        for offer in self.offers[side].items.iter().flatten() {
            let slot = &mut result.cargo[offer.cargo_slot];
            let item = slot.as_mut().expect("validated trade reservation");
            item.quantity -= offer.item.quantity;
            if item.quantity == 0 {
                *slot = None;
            }
        }
        result
    }
}

impl GameState {
    /// Returns:
    ///     u32: Owning trade key for this authenticated participant.
    /// Raises:
    ///     io::Error: The player has no open exchange.
    fn trade_key(&self, player: u32) -> io::Result<u32> {
        self.trades
            .iter()
            .find_map(|(key, trade)| trade.parties.contains(&player).then_some(*key))
            .ok_or_else(|| invalid("There is no open player trade."))
    }

    /// Returns:
    ///     bool: Both participants still own the same reserved cargo and can meet.
    fn trade_valid(&self, trade: &PlayerTrade) -> bool {
        if trade.expires <= Instant::now() {
            return false;
        }
        let [a, b] = trade.parties;
        let (Some(left), Some(right)) = (self.sessions.get(&a), self.sessions.get(&b)) else {
            return false;
        };
        if left.sector.id != right.sector.id
            || !left.trade_ready()
            || !right.trade_ready()
            || !left.trade_peer_visible(b)
            || !right.trade_peer_visible(a)
            || left.sector.station.is_none()
                && crate::mob::distance(left.position, right.position)
                    - crate::following::PLAYER_RADIUS
                    > 1000.
        {
            return false;
        }
        for (side, session) in [left, right].into_iter().enumerate() {
            let before = &trade.baseline[side];
            if session.energy_transfer_epoch() != trade.epochs[side]
                || session.inventory.cargo != before.cargo
                || session.inventory.credits != before.credits
                || session.inventory.cargo_capacity != before.cargo_capacity
            {
                return false;
            }
        }
        true
    }

    /// Publish both offers and reserved cargo to only the two participants.
    /// Raises:
    ///     io::Error: A catalog item is unavailable.
    fn publish_trade(&mut self, trade: &PlayerTrade) -> io::Result<()> {
        for viewer in trade.parties {
            let session = self.sessions.get_mut(&viewer).unwrap();
            for (side, owner) in trade.parties.into_iter().enumerate() {
                let items: Vec<_> = trade.offers[side]
                    .items
                    .iter()
                    .map(|item| item.as_ref().map(|item| item.item.clone()))
                    .collect();
                session.publish_trade_offer(owner, trade.offers[side].credits, &items)?;
            }
            let side = usize::from(viewer != trade.parties[0]);
            let displayed = trade.displayed(side, &session.inventory);
            session.publish_trade_cargo(&displayed);
        }
        Ok(())
    }

    /// Close an exchange without moving any durable items or credits.
    fn close_trade(&mut self, trade: &PlayerTrade, completed: bool, reason: &str) {
        for (side, player) in trade.parties.into_iter().enumerate() {
            if let Some(session) = self.sessions.get_mut(&player) {
                session.queue([packets::trade_action(
                    trade.parties[1 - side],
                    if completed { 2 } else { 1 },
                )]);
                let _ = session.publish_trade_offer(player, 0, &vec![None; TRADE_SLOTS]);
                let _ = session.publish_trade_offer(
                    trade.parties[1 - side],
                    0,
                    &vec![None; TRADE_SLOTS],
                );
                let inventory = session.inventory.clone();
                session.publish_trade_cargo(&inventory);
                session.message(reason);
            }
        }
    }

    /// Reset both approvals before publishing any changed offer.
    fn reset_trade_confirmations(&mut self, trade: &mut PlayerTrade) {
        for (side, offer) in trade.offers.iter_mut().enumerate() {
            offer.confirmed = false;
            self.sessions
                .get_mut(&trade.parties[side])
                .unwrap()
                .queue([packets::trade_action(0, 4)]);
        }
        trade.expires = Instant::now() + Duration::from_secs(300);
    }

    /// Open, confirm, edit credit offers or cancel through the native action packet.
    /// Raises:
    ///     io::Error: Admission, ownership, credit capacity or durable commitment fails.
    pub(super) fn player_trade_action(
        &mut self,
        source: u32,
        action: u32,
        target: u32,
        optional: u32,
    ) -> io::Result<()> {
        if action != 23 {
            crate::server_log!(
                "Player trade action: source={source}; action={action}; target={target}; optional={optional}"
            );
        }
        self.update_views();
        if action == 20 {
            if source == target || self.trade_key(source).is_ok() || self.trade_key(target).is_ok()
            {
                return Err(invalid("One of these pilots is already trading."));
            }
            let target_session = self
                .sessions
                .get(&target)
                .ok_or_else(|| invalid("That pilot is not online."))?;
            let source_session = &self.sessions[&source];
            let offer = Offer {
                credits: 0,
                items: vec![None; TRADE_SLOTS],
                confirmed: false,
            };
            let trade = PlayerTrade {
                parties: [source, target],
                epochs: [
                    source_session.energy_transfer_epoch(),
                    target_session.energy_transfer_epoch(),
                ],
                baseline: [
                    source_session.inventory.clone(),
                    target_session.inventory.clone(),
                ],
                offers: [offer.clone(), offer],
                expires: Instant::now() + Duration::from_secs(300),
            };
            if !self.trade_valid(&trade) {
                return Err(invalid(
                    "Trade requires a visible living pilot within 1 km, or in the same station.",
                ));
            }
            for (side, player) in trade.parties.into_iter().enumerate() {
                self.sessions
                    .get_mut(&player)
                    .unwrap()
                    .queue([packets::trade_action(trade.parties[1 - side], 0)]);
            }
            self.publish_trade(&trade)?;
            self.trades.insert(source, trade);
            return Ok(());
        }
        let key = self.trade_key(source)?;
        let mut trade = self.trades.remove(&key).unwrap();
        if action == 22 || !self.trade_valid(&trade) {
            self.close_trade(&trade, false, "Player trade cancelled.");
            return Ok(());
        }
        let side = usize::from(source != trade.parties[0]);
        let result = match action {
            21 => {
                trade.offers[side].confirmed = true;
                crate::server_log!(
                    "Player trade confirmation: source={source}; peer={}; peer_confirmed={}",
                    trade.parties[1 - side],
                    trade.offers[1 - side].confirmed
                );
                self.sessions
                    .get_mut(&source)
                    .unwrap()
                    .queue([packets::trade_action(0, 3)]);
                self.sessions
                    .get_mut(&trade.parties[1 - side])
                    .unwrap()
                    .queue([packets::trade_action(0, 5)]);
                if trade.offers.iter().all(|offer| offer.confirmed) {
                    match self.commit_player_trade(&trade) {
                        Ok(()) => {
                            self.close_trade(&trade, true, "Player trade completed.");
                            return Ok(());
                        }
                        Err(error) => {
                            self.reset_trade_confirmations(&mut trade);
                            Err(error)
                        }
                    }
                } else {
                    Ok(())
                }
            }
            23 => {
                // Native "keep trading" is not an offer edit. Republishing
                // here also makes the original UI discard a valid approval.
                trade.expires = Instant::now() + Duration::from_secs(300);
                Ok(())
            }
            24 if u64::from(optional) <= self.sessions[&source].inventory.credits => {
                let credits = u64::from(optional);
                if trade.offers[side].credits == credits {
                    // Legacy widgets may resend the current number when
                    // focus changes during confirmation; no offer changed.
                    trade.expires = Instant::now() + Duration::from_secs(300);
                    Ok(())
                } else {
                    self.reset_trade_confirmations(&mut trade);
                    trade.offers[side].credits = credits;
                    self.publish_trade(&trade)
                }
            }
            24 => Err(invalid("Not enough credits for this trade offer.")),
            _ => Err(invalid("Unknown player trade action.")),
        };
        self.trades.insert(key, trade);
        result
    }

    /// Update a cargo reservation through native inventory container16.
    /// Raises:
    ///     io::Error: The stack, amount, destination or item trading policy is invalid.
    pub(super) fn player_trade_move(
        &mut self,
        source: u32,
        transfer: ItemTransfer,
    ) -> io::Result<()> {
        let key = self.trade_key(source)?;
        let mut trade = self.trades.remove(&key).unwrap();
        if !self.trade_valid(&trade) {
            self.close_trade(
                &trade,
                false,
                "Player trade cancelled: cargo, range or session changed.",
            );
            return Err(invalid("Trade is no longer available."));
        }
        let side = usize::from(source != trade.parties[0]);
        let result = self.update_trade_item(&mut trade, side, transfer);
        if result.is_ok() {
            self.reset_trade_confirmations(&mut trade);
            self.publish_trade(&trade)?;
        }
        self.trades.insert(key, trade);
        result
    }

    /// Update one reservation without changing durable ownership.
    /// Raises:
    ///     io::Error: This request cannot be fulfilled from the player's available cargo.
    fn update_trade_item(
        &self,
        trade: &mut PlayerTrade,
        side: usize,
        transfer: ItemTransfer,
    ) -> io::Result<()> {
        let inventory = &self.sessions[&trade.parties[side]].inventory;
        if transfer.source == 1
            && transfer.destination == 16
            && transfer.destination_slot < TRADE_SLOTS
        {
            let slot = transfer.source_slot;
            let item = inventory
                .cargo
                .get(slot)
                .and_then(Option::as_ref)
                .filter(|_| slot < inventory.cargo_capacity)
                .ok_or_else(|| invalid("That cargo slot is empty."))?;
            let catalog = ItemCatalog::load()?;
            let template = catalog.get(item.template)?;
            if template.flags & 1 != 0 || template.category == 90 {
                return Err(invalid("This item cannot be traded to another player."));
            }
            let reserved: u32 = trade.offers[side]
                .items
                .iter()
                .enumerate()
                .filter(|(index, _)| *index != transfer.destination_slot)
                .filter_map(|(_, offer)| offer.as_ref())
                .filter(|offer| offer.cargo_slot == slot)
                .map(|offer| offer.item.quantity)
                .sum();
            let available = item
                .quantity
                .checked_sub(reserved)
                .ok_or_else(|| invalid("Invalid cargo reservation."))?;
            let quantity = if transfer.quantity == u32::MAX {
                available
            } else {
                transfer.quantity
            };
            if quantity == 0 || quantity > available {
                return Err(invalid("Not enough unreserved cargo for this offer."));
            }
            let mut offered = item.clone();
            offered.quantity = quantity;
            trade.offers[side].items[transfer.destination_slot] = Some(OfferedItem {
                cargo_slot: slot,
                item: offered,
            });
        } else if transfer.source == 16
            && transfer.destination == 1
            && transfer.source_slot < TRADE_SLOTS
        {
            let slot = &mut trade.offers[side].items[transfer.source_slot];
            let item = slot
                .as_mut()
                .ok_or_else(|| invalid("That trade slot is empty."))?;
            let quantity = if transfer.quantity == u32::MAX {
                item.item.quantity
            } else {
                transfer.quantity
            };
            if quantity == 0 || quantity > item.item.quantity {
                return Err(invalid("Invalid trade removal quantity."));
            }
            item.item.quantity -= quantity;
            if item.item.quantity == 0 {
                *slot = None;
            }
        } else {
            return Err(invalid("Trade items move between cargo1 and trade16 only."));
        }
        Ok(())
    }

    /// Commit both inventories in one recoverable decision before success is visible.
    /// Raises:
    ///     io::Error: Either cargo cannot fit, credit arithmetic overflows, or the journal fails.
    fn commit_player_trade(&mut self, trade: &PlayerTrade) -> io::Result<()> {
        let items = ItemCatalog::load()?;
        let mut candidates: Vec<Inventory> = trade
            .parties
            .iter()
            .enumerate()
            .map(|(side, player)| trade.displayed(side, &self.sessions[player].inventory))
            .collect();
        for side in 0..2 {
            candidates[side].credits = candidates[side]
                .credits
                .checked_sub(trade.offers[side].credits)
                .and_then(|balance| balance.checked_add(trade.offers[1 - side].credits))
                .ok_or_else(|| invalid("Trade credit balance would overflow."))?;
            for offer in trade.offers[1 - side].items.iter().flatten() {
                candidates[side] =
                    candidates[side].received_instance(&offer.item, u32::MAX as usize, &items)?;
            }
        }
        let revision = candidates
            .iter()
            .map(|inventory| inventory.trade_revision)
            .max()
            .unwrap()
            .checked_add(1)
            .ok_or_else(|| invalid("Player trade revision exhausted."))?;
        let mut parties = Vec::new();
        for (side, player) in trade.parties.into_iter().enumerate() {
            candidates[side].trade_revision = revision;
            candidates[side].validate(&items)?;
            let directory = self.sessions[&player]
                .state_dir
                .strip_prefix(&self.root)
                .map_err(|_| invalid("Trade account does not belong to this server."))?
                .to_path_buf();
            parties.push(TradeParty {
                character: player,
                directory,
                inventory: candidates[side].clone(),
            });
        }
        crate::trade_store::commit(
            &self.root,
            parties
                .try_into()
                .map_err(|_| invalid("Invalid trade pair."))?,
        )?;
        for (player, inventory) in trade.parties.into_iter().zip(candidates) {
            self.sessions
                .get_mut(&player)
                .unwrap()
                .install_traded_inventory(inventory);
        }
        if let Err(error) = crate::trade_store::recover(&self.root) {
            self.trade_recovery_at = Some(Instant::now());
            crate::server_error!("Player trade committed; account file replay pending: {error}");
        }
        crate::server_log!(
            "Player trade completed: {} <-> {}; revision={revision}",
            trade.parties[0],
            trade.parties[1]
        );
        Ok(())
    }

    /// Cancel expired/disconnected/moved trades and retry committed account publication.
    pub(super) fn advance_player_trades(&mut self) {
        let expired: Vec<_> = self
            .trades
            .iter()
            .filter_map(|(key, trade)| (!self.trade_valid(trade)).then_some(*key))
            .collect();
        for key in expired {
            let trade = self.trades.remove(&key).unwrap();
            self.close_trade(
                &trade,
                false,
                "Player trade cancelled: cargo, range or session changed.",
            );
        }
        if self
            .trade_recovery_at
            .is_some_and(|at| at.elapsed() >= Duration::from_secs(1))
        {
            self.trade_recovery_at = if crate::trade_store::recover(&self.root).is_ok() {
                None
            } else {
                Some(Instant::now())
            };
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::account::FIRST_PLAYER_ID;

    /// Returns:
    ///     GameState: Two disposable, nearby pilots with known tradable cargo.
    fn pair() -> GameState {
        let mut game = super::super::tests::game();
        for (side, name) in ["alice", "bob"].into_iter().enumerate() {
            let id = FIRST_PLAYER_ID + side as u32;
            super::super::tests::login(&mut game, id, 22831 + side as u16, name);
            let session = game.sessions.get_mut(&id).unwrap();
            session.position = [side as f32 * 100., 0., 0.];
            session.inventory.cargo.fill(None);
            session.inventory.cargo[0] =
                Some(ItemInstance::stock(if side == 0 { 5477 } else { 3818 }, 10));
            session.inventory.credits = 1000;
            crate::durable_json::replace(
                &session.state_dir.join("inventory.json"),
                &session.inventory,
            )
            .unwrap();
        }
        game.update_views();
        game
    }

    /// Submit the same authenticated event produced by a native action packet.
    fn action(
        game: &mut GameState,
        player: u32,
        action: u32,
        target: u32,
        credits: u32,
    ) -> io::Result<()> {
        game.handle_event(
            player,
            ClientEvent::TradeAction {
                action,
                target,
                optional: credits,
            },
        )
    }

    /// Submit one native cargo/trade reservation move.
    fn offer(game: &mut GameState, player: u32, slot: usize, quantity: u32) -> io::Result<()> {
        game.handle_event(
            player,
            ClientEvent::TradeMove {
                transfer: ItemTransfer {
                    source: 1,
                    source_slot: 0,
                    destination: 16,
                    destination_slot: slot,
                    quantity,
                },
            },
        )
    }

    /// Returns:
    ///     u32: Total cargo quantity for this public template.
    fn quantity(inventory: &Inventory, template: u32) -> u32 {
        inventory
            .cargo
            .iter()
            .flatten()
            .filter(|item| item.template == template)
            .map(|item| item.quantity)
            .sum()
    }

    /// Parse an actual native action body before dispatching its authenticated event.
    /// Raises:
    ///     io::Error: Native input ownership or the resulting trade operation is rejected.
    fn native_action(
        game: &mut GameState,
        player: u32,
        action: u32,
        target: u32,
        optional: u32,
    ) -> io::Result<()> {
        let body: Vec<u8> = [player, action, target, optional]
            .into_iter()
            .flat_map(u32::to_le_bytes)
            .collect();
        let session = game.sessions.get_mut(&player).unwrap();
        session.handle_input(0x2c, &body)?;
        let events = session.take_events();
        for event in events {
            game.handle_event(player, event)?;
        }
        Ok(())
    }

    /// Native keep-trading messages between approvals must not unconfirm the exchange.
    #[test]
    fn native_keep_trading_preserves_confirmation_and_allows_completion() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        action(&mut game, a, 20, b, 0).unwrap();
        offer(&mut game, a, 0, 3).unwrap();
        action(&mut game, a, 21, b, 0).unwrap();
        // Drain the already-published bootstrap, offers and first approval.
        // Exercise the public transport boundary rather than exposing pending state.
        for session in game.sessions.values_mut() {
            let mut drained = false;
            for _ in 0..128 {
                session.finish_tick().unwrap();
                if session.take_outgoing().is_empty() {
                    drained = true;
                    break;
                }
            }
            assert!(drained, "Initial native trade output did not drain.");
        }
        game.take_outgoing();
        // Captured Windows behavior: receiving 0x1f(action4) causes action23
        // from each participant. A reply here produces an unbounded echo loop.
        for _ in 0..64 {
            native_action(&mut game, a, 23, b, 0).unwrap();
            native_action(&mut game, b, 23, a, 0).unwrap();
        }
        for session in game.sessions.values_mut() {
            session.finish_tick().unwrap();
            assert!(
                session.take_outgoing().is_empty(),
                "Keep-trading must emit no packets."
            );
        }
        assert!(game.take_outgoing().is_empty());
        assert!(game.trades[&a].offers[0].confirmed);
        action(&mut game, b, 21, a, 0).unwrap();
        assert!(game.trades.is_empty());
        assert_eq!(quantity(&game.sessions[&a].inventory, 5477), 7);
        assert_eq!(quantity(&game.sessions[&b].inventory, 5477), 3);
    }

    /// Unused native OptionalVar bytes are ignored for open, confirm and keep-trading buttons.
    #[test]
    fn native_trade_buttons_accept_unused_nonzero_optional_fields() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        native_action(&mut game, a, 20, b, u32::MAX).unwrap();
        native_action(&mut game, a, 21, 0, 42).unwrap();
        native_action(&mut game, b, 23, 0, 55).unwrap();
        assert!(game.trades[&a].offers[0].confirmed);
        native_action(&mut game, b, 21, 0, 77).unwrap();
        assert!(game.trades.is_empty());
        assert_eq!(game.sessions[&a].inventory.trade_revision, 1);
        assert_eq!(game.sessions[&b].inventory.trade_revision, 1);

        native_action(&mut game, a, 20, b, 19).unwrap();
        native_action(&mut game, a, 22, b, 91).unwrap();
        assert!(game.trades.is_empty());
        assert_eq!(game.sessions[&a].inventory.trade_revision, 1);
    }

    /// Resending an unchanged credit field while confirming does not edit or unlock an offer.
    #[test]
    fn identical_credit_offer_preserves_approvals_but_changed_credits_reset_them() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        action(&mut game, a, 20, b, 0).unwrap();
        action(&mut game, a, 24, b, 100).unwrap();
        action(&mut game, a, 21, b, 0).unwrap();
        action(&mut game, a, 24, b, 100).unwrap();
        assert!(game.trades[&a].offers[0].confirmed);
        action(&mut game, a, 24, b, 101).unwrap();
        assert!(game.trades[&a].offers.iter().all(|offer| !offer.confirmed));
        action(&mut game, a, 21, b, 0).unwrap();
        action(&mut game, b, 24, a, 0).unwrap();
        assert!(game.trades[&a].offers[0].confirmed);
        action(&mut game, b, 21, a, 0).unwrap();
        assert!(game.trades.is_empty());
        assert_eq!(game.sessions[&a].inventory.credits, 899);
        assert_eq!(game.sessions[&b].inventory.credits, 1101);
    }

    /// Offers remain reservations; edits reset approval, and both owners commit together.
    #[test]
    fn player_trade_mutual_confirmation_preserves_items_credits_and_revision() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        action(&mut game, a, 20, b, 0).unwrap();
        offer(&mut game, a, 2, 4).unwrap();
        offer(&mut game, b, 1, 2).unwrap();
        action(&mut game, a, 24, b, 100).unwrap();
        action(&mut game, b, 24, a, 50).unwrap();
        action(&mut game, a, 21, b, 0).unwrap();
        assert_eq!(quantity(&game.sessions[&a].inventory, 5477), 10);
        assert_eq!(game.sessions[&a].inventory.credits, 1000);
        action(&mut game, b, 24, a, 75).unwrap();
        assert!(game.trades[&a].offers.iter().all(|offer| !offer.confirmed));
        action(&mut game, b, 21, a, 0).unwrap();
        action(&mut game, a, 21, b, 0).unwrap();
        assert!(game.trades.is_empty());
        assert_eq!(
            (
                game.sessions[&a].inventory.credits,
                game.sessions[&b].inventory.credits
            ),
            (975, 1025)
        );
        assert_eq!(quantity(&game.sessions[&a].inventory, 5477), 6);
        assert_eq!(quantity(&game.sessions[&a].inventory, 3818), 2);
        assert_eq!(quantity(&game.sessions[&b].inventory, 5477), 4);
        assert_eq!(quantity(&game.sessions[&b].inventory, 3818), 8);
        for player in [a, b] {
            let session = &game.sessions[&player];
            let saved: Inventory = serde_json::from_slice(
                &std::fs::read(session.state_dir.join("inventory.json")).unwrap(),
            )
            .unwrap();
            assert_eq!(saved, session.inventory);
            assert_eq!(saved.trade_revision, 1);
        }
        assert!(!game.root.join("trade_commit.local.json").exists());
        let after = game.sessions[&a].inventory.clone();
        assert!(action(&mut game, a, 21, b, 0).is_err());
        assert_eq!(after, game.sessions[&a].inventory);
    }

    /// Invalid requests and cancellation cannot remove or duplicate reserved items.
    #[test]
    fn player_trade_reservations_validate_quantities_and_trading_policy() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        game.sessions.get_mut(&a).unwrap().inventory.cargo[1] = Some(ItemInstance::stock(2505, 1));
        let before = game.sessions[&a].inventory.clone();
        action(&mut game, a, 20, b, 0).unwrap();
        assert!(action(&mut game, b, 20, a, 0).is_err());
        assert!(offer(&mut game, a, 0, 11).is_err());
        offer(&mut game, a, 0, 4).unwrap();
        assert!(offer(&mut game, a, 1, 7).is_err());
        assert!(game
            .player_trade_move(
                a,
                ItemTransfer {
                    source: 1,
                    source_slot: 1,
                    destination: 16,
                    destination_slot: 1,
                    quantity: 1
                }
            )
            .is_err());
        assert!(action(&mut game, a, 24, b, 1001).is_err());
        game.player_trade_move(
            a,
            ItemTransfer {
                source: 16,
                source_slot: 0,
                destination: 1,
                destination_slot: 0,
                quantity: 2,
            },
        )
        .unwrap();
        assert_eq!(
            game.trades[&a].offers[0].items[0]
                .as_ref()
                .unwrap()
                .item
                .quantity,
            2
        );
        assert_eq!(game.sessions[&a].inventory, before);
        action(&mut game, b, 22, a, 0).unwrap();
        assert_eq!(game.sessions[&a].inventory, before);
        assert!(game.trades.is_empty());
    }

    /// A full recipient leaves both inventories untouched and the exchange editable.
    #[test]
    fn player_trade_full_cargo_and_journal_failure_do_not_partially_commit() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        let inventory = &mut game.sessions.get_mut(&a).unwrap().inventory;
        for slot in 0..inventory.cargo_capacity {
            inventory.cargo[slot] = Some(ItemInstance::stock(5477, 100));
        }
        let before = [
            game.sessions[&a].inventory.clone(),
            game.sessions[&b].inventory.clone(),
        ];
        action(&mut game, a, 20, b, 0).unwrap();
        offer(&mut game, b, 0, 1).unwrap();
        action(&mut game, a, 21, b, 0).unwrap();
        assert!(action(&mut game, b, 21, a, 0).is_err());
        assert_eq!(game.sessions[&a].inventory, before[0]);
        assert_eq!(game.sessions[&b].inventory, before[1]);
        assert!(game.trades[&a].offers.iter().all(|offer| !offer.confirmed));
        action(&mut game, a, 22, b, 0).unwrap();
        action(&mut game, a, 20, b, 0).unwrap();
        action(&mut game, a, 24, b, 100).unwrap();
        let blocked = game.root.join("trade_commit.local.json.next");
        std::fs::create_dir(&blocked).unwrap();
        action(&mut game, a, 21, b, 0).unwrap();
        assert!(action(&mut game, b, 21, a, 0).is_err());
        assert_eq!(game.sessions[&a].inventory, before[0]);
        assert_eq!(game.sessions[&b].inventory, before[1]);
        assert!(!game.root.join("trade_commit.local.json").exists());
    }

    /// Leaving range, changing owned cargo or logging out releases every reservation.
    #[test]
    fn player_trade_cancels_when_participants_or_cargo_change() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        action(&mut game, a, 20, b, 0).unwrap();
        offer(&mut game, a, 0, 4).unwrap();
        game.sessions.get_mut(&b).unwrap().position = [20_000., 0., 0.];
        game.advance_player_trades();
        assert!(game.trades.is_empty());
        assert_eq!(quantity(&game.sessions[&a].inventory, 5477), 10);
        game.sessions.get_mut(&b).unwrap().position = [100., 0., 0.];
        action(&mut game, a, 20, b, 0).unwrap();
        offer(&mut game, a, 0, 4).unwrap();
        game.sessions.get_mut(&a).unwrap().inventory.cargo[0]
            .as_mut()
            .unwrap()
            .quantity = 9;
        game.advance_player_trades();
        assert!(game.trades.is_empty());
        assert_eq!(quantity(&game.sessions[&a].inventory, 5477), 9);
        action(&mut game, a, 20, b, 0).unwrap();
        game.sessions.get_mut(&a).unwrap().logout().unwrap();
        game.advance_player_trades();
        assert!(game.trades.is_empty());
    }

    /// Replay completes the other account after a crash without undoing newer first-account changes.
    #[test]
    fn player_trade_recovers_partial_account_writes_without_rewinding_later_changes() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        action(&mut game, a, 20, b, 0).unwrap();
        action(&mut game, a, 24, b, 100).unwrap();
        let blocked = game.sessions[&b].state_dir.join("inventory.json.next");
        std::fs::create_dir(&blocked).unwrap();
        action(&mut game, a, 21, b, 0).unwrap();
        action(&mut game, b, 21, a, 0).unwrap();
        assert!(game.root.join("trade_commit.local.json").is_file());
        assert_eq!(game.sessions[&a].inventory.credits, 900);
        assert_eq!(game.sessions[&b].inventory.credits, 1100);
        let mut newer = game.sessions[&a].inventory.clone();
        newer.credits = 901;
        crate::durable_json::replace(&game.sessions[&a].state_dir.join("inventory.json"), &newer)
            .unwrap();
        std::fs::remove_dir(blocked).unwrap();
        crate::trade_store::recover(&game.root).unwrap();
        for (player, credits) in [(a, 901), (b, 1100)] {
            let saved: Inventory = serde_json::from_slice(
                &std::fs::read(game.sessions[&player].state_dir.join("inventory.json")).unwrap(),
            )
            .unwrap();
            assert_eq!(saved.credits, credits);
            assert_eq!(saved.trade_revision, 1);
        }
        assert!(!game.root.join("trade_commit.local.json").exists());
    }
}
