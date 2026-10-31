//! Player-owned trade admission and publication; exchange decisions live in GameState.
use super::*;
use crate::inventory::ItemInstance;

impl PlayerSession {
    /// Returns:
    ///     bool: Whether this pilot can participate in an exchange right now.
    pub(crate) fn trade_ready(&self) -> bool {
        self.logged_in
            && self.hull > 0.
            && !self.warp.active()
            && self.handoff.is_none()
            && self.transition_pending.is_none()
            && self.gate_transit.is_none()
            && self.docking_target.is_none()
            && !self.awaiting_start_ack
            && self.inventory.pending_launch.is_none()
    }

    /// Returns:
    ///     bool: Whether the peer is currently visible to this pilot.
    pub(crate) fn trade_peer_visible(&self, peer: u32) -> bool {
        if self.sector.station.is_some() {
            self.station_peers.contains_key(&peer)
        } else {
            self.peer_contacts.contains_key(&peer)
        }
    }

    /// Publish an offer's item templates and native AUX without changing ownership.
    /// Raises:
    ///     io::Error: An offer references an unknown item.
    pub(crate) fn publish_trade_offer(
        &mut self,
        owner: u32,
        credits: u64,
        items: &[Option<ItemInstance>],
    ) -> io::Result<()> {
        for item in items.iter().flatten() {
            self.pending.push(self.items.get(item.template)?.packet());
        }
        self.pending
            .push(packets::trade_offer(owner, credits, items, &self.items));
        Ok(())
    }

    /// Publish reserved cargo quantities while keeping the durable inventory intact.
    pub(crate) fn publish_trade_cargo(&mut self, displayed: &Inventory) {
        self.pending.push(packets::inventory_update(
            self.player_id(),
            displayed,
            &self.items,
            &self.equipment.ship,
        ));
        self.publish_weapon_states();
        self.publish_ammo_options(false);
    }

    /// Install an exchange already committed in the two-account journal.
    pub(crate) fn install_traded_inventory(&mut self, inventory: Inventory) {
        for template in inventory
            .templates(&self.items)
            .expect("validated trade inventory")
        {
            self.pending.push(template.packet());
        }
        self.inventory = inventory;
        let displayed = self.inventory.clone();
        self.publish_trade_cargo(&displayed);
        self.pending
            .push(packets::vault_update(&self.inventory, &self.items));
    }
}
