//! Catalog-driven, target-only reactor transfer admission and ledger replay.
use super::*;
use crate::energy_ledger::EnergyBalance;
use crate::items::ItemTemplate;

#[derive(Clone, Copy)]
pub(crate) struct TransferOffer {
    pub cost: f32,
    pub amount: f32,
    pub range: f32,
}

/// Returns:
///     Option<TransferOffer>: Validated instant target-energy effect from the catalog.
pub(super) fn transfer_reactor_energy(template: &ItemTemplate) -> Option<TransferOffer> {
    let effect = template.activated.first()?;
    let modifier = effect.modifiers.first()?;
    if template.item_type != 11
        || template.activated.len() != 1
        || effect.description != "Transfer Reactor Energy (Instant)"
        || effect.flag1 != 16
        || effect.flag2 != 1
        || effect.buff_name != "BUFF_NONE"
        || effect.visual != 0
        || effect.modifiers.len() != 1
        || modifier.stat != "STAT_ENERGY"
        || modifier.operation != 1
        || !modifier.value.is_finite()
        || !(0. ..=10_000.).contains(&modifier.value)
        || !template.activation.range.is_finite()
        || !(1. ..=5_000.).contains(&template.activation.range)
        || !template.activation.energy.is_finite()
        || !(0. ..=10_000.).contains(&template.activation.energy)
    {
        return None;
    }
    Some(TransferOffer {
        cost: template.activation.energy,
        amount: modifier.value,
        range: template.activation.range,
    })
}

impl PlayerSession {
    /// Returns:
    ///     TransferOffer: Current selected-peer request after local state checks.
    pub(crate) fn energy_transfer_offer(&self, target: u32, slot: u8) -> io::Result<TransferOffer> {
        if !self.logged_in
            || self.hull <= 0.
            || self.sector.station.is_some()
            || self.warp.active()
            || target == self.player_id()
            || target != self.target
            || !self.peer_ids.contains(&target)
            || !(9..20).contains(&usize::from(slot))
        {
            return Err(invalid(
                "Select a visible peer in space before transferring energy.",
            ));
        }
        let item = self.inventory.equipped[usize::from(slot)]
            .as_ref()
            .ok_or_else(|| invalid("No equipped transfer device in this mount."))?;
        if item.structure <= 0. {
            return Err(invalid("Damaged transfer device cannot be activated."));
        }
        let template = self.items.get(item.template)?;
        let offer = transfer_reactor_energy(template)
            .ok_or_else(|| invalid("This item cannot transfer reactor energy."))?;
        if self.energy < offer.cost {
            return Err(invalid("Insufficient reactor energy for transfer."));
        }
        Ok(offer)
    }

    /// Returns:
    ///     (f32, f32): Current reactor points and capacity.
    pub(crate) fn energy_balance(&self) -> (f32, f32) {
        (self.energy, self.equipment.ship.energy_capacity)
    }

    /// Returns:
    ///     u64: Authenticated session lifetime used by the transfer receipt.
    pub(crate) fn energy_transfer_epoch(&self) -> u64 {
        self.session_epoch
    }

    /// Apply the newest atomic transfer balance to an owned session.
    pub(crate) fn apply_energy_balance(&mut self, balance: EnergyBalance) -> io::Result<()> {
        if balance.revision <= self.energy_revision {
            return Ok(());
        }
        self.energy = balance.energy.min(self.equipment.ship.energy_capacity);
        self.energy_revision = balance.revision;
        let checkpoint = self.checkpoint_session(true);
        if self.logged_in {
            self.pending.push(packets::ship_resources(
                self.player_id(),
                &self.equipment.ship,
                Some(self.energy),
                None,
                None,
            ));
        }
        checkpoint
    }

    /// Returns:
    ///     u64: Last transfer revision saved in this pilot's location record.
    pub(crate) fn energy_transfer_revision(&self) -> u64 {
        self.energy_revision
    }

    /// Set a disposable pilot's reactor level for cross-session transfer tests.
    #[cfg(test)]
    pub(crate) fn set_energy_for_test(&mut self, energy: f32) {
        assert!((0. ..=self.equipment.ship.energy_capacity).contains(&energy));
        self.energy = energy;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// All seven public target devices share one bounded instant rule.
    #[test]
    fn public_transfer_devices_match_catalog() {
        let items = ItemCatalog::load().unwrap();
        for id in [1946, 2028, 2120, 2210, 2301, 2404, 2495] {
            let offer = transfer_reactor_energy(items.get(id).unwrap()).unwrap();
            assert!((129. ..=1166.).contains(&offer.amount));
            assert!((4000. ..=5000.).contains(&offer.range));
        }
    }
}
