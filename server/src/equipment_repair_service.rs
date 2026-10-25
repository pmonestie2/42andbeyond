//! Session half of toggled Repair Equipment; the world coordinator settles
//! each paid tick before its inventory result is published.
use super::*;
use crate::energy_ledger::EquipmentBalance;
use crate::equipment_repair::{self, ActiveEquipmentRepair, PendingEquipmentRepair};
use crate::skills::AbilityDefinition;

impl PlayerSession {
    /// Toggle a trained Repair Equipment tier with native visible-peer validation.
    pub(super) fn toggle_equipment_repair(
        &mut self,
        ability: &AbilityDefinition,
        requested_target: u32,
    ) -> io::Result<()> {
        if self.skills.equipment_repair.is_some() {
            self.stop_equipment_repair("Equipment repair stopped.")?;
            return Ok(());
        }
        if self.hull <= 0. {
            return Err(invalid("An incapacitated ship cannot repair equipment."));
        }
        if self.skills.cast.is_some() || self.skills.pending_equipment_repair.is_some() {
            return Err(invalid("Finish the previous skill action first."));
        }
        if self.warp.active() || self.gate_transit.is_some() || self.mining.is_some() {
            return Err(invalid(
                "Leave warp and stop prospecting before repairing equipment.",
            ));
        }
        let trained = self.skill_catalog.level(47);
        let (rank, range, _, _) = equipment_repair::tier(ability.id, trained)
            .ok_or_else(|| invalid("This Repair Equipment tier is not trained."))?;
        let target =
            if rank == 1 || requested_target == self.player_id() || requested_target == u32::MAX {
                self.player_id()
            } else {
                let contact = self
                    .peer_contacts
                    .get(&requested_target)
                    .ok_or_else(|| invalid("Select a visible pilot for equipment repair."))?;
                if contact.incapacitated || distance(contact.position, self.position) > range {
                    return Err(invalid("Equipment repair target is dead or out of range."));
                }
                requested_target
            };
        if self.energy < equipment_repair::ENERGY_PER_TICK {
            return Err(invalid("Insufficient reactor energy for equipment repair."));
        }
        let old_effect = self.next_effect;
        self.next_effect = self.next_effect.wrapping_add(1).max(1);
        self.skills.equipment_repair = Some(ActiveEquipmentRepair {
            ability: ability.id,
            target,
            rank,
            effect: old_effect,
            next_due_ms: crate::resources::unix_ms() + self.equipment_repair_period_ms()?,
        });
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.equipment_repair = None;
            self.next_effect = old_effect;
            return Err(error);
        }
        self.publish_equipment_repair_resume();
        self.message(&format!("{} active.", ability.name));
        Ok(())
    }

    /// Returns:
    ///     u64: Tick period after the strongest equipped repair-time reduction.
    fn equipment_repair_period_ms(&self) -> io::Result<u64> {
        let mut reduction = 0f32;
        for item in self
            .inventory
            .equipped
            .iter()
            .flatten()
            .filter(|item| item.structure > 0.)
        {
            for effect in &self.items.get(item.template)?.equipped {
                for modifier in &effect.modifiers {
                    if modifier.stat == "STAT_REPAIR_EQUIPMENT" && modifier.operation == 3 {
                        reduction = reduction.max(modifier.value);
                    }
                }
            }
        }
        Ok((1000. * (1. - reduction.clamp(0., 75.) / 100.)).ceil() as u64)
    }

    /// Publish the server-owned toggle icon after startup or replacement.
    pub(super) fn publish_equipment_repair_resume(&mut self) {
        if self.skills.equipment_repair.is_some() {
            self.pending.push(packets::ship_status_buff_slot(
                self.player_id(),
                equipment_repair::BUFF_SLOT,
                "Repair_Equipment",
                true,
                0,
            ));
        }
    }

    /// Stop the stream and remove its native icon.
    pub(crate) fn stop_equipment_repair(&mut self, reason: &str) -> io::Result<()> {
        let active = self.skills.equipment_repair.take();
        if active.is_none() {
            return Ok(());
        }
        let pending = self.skills.pending_equipment_repair.take();
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.equipment_repair = active;
            self.skills.pending_equipment_repair = pending;
            return Err(error);
        }
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            equipment_repair::BUFF_SLOT,
            "",
            0,
        ));
        self.message(reason);
        Ok(())
    }

    /// Freeze one due reactor/structure tick for crash-safe world settlement.
    pub(super) fn advance_equipment_repair(&mut self) -> io::Result<()> {
        let Some(mut active) = self.skills.equipment_repair.clone() else {
            return Ok(());
        };
        if !active.valid(self.skill_catalog.level(47))
            || !self.logged_in
            || self.hull <= 0.
            || self.sector.station.is_some()
            || self.warp.active()
            || self.gate_transit.is_some()
        {
            return self.stop_equipment_repair("Equipment repair interrupted.");
        }
        if self.skills.pending_equipment_repair.is_some()
            || crate::resources::unix_ms() < active.next_due_ms
        {
            return Ok(());
        }
        if self.energy < equipment_repair::ENERGY_PER_TICK {
            return self.stop_equipment_repair("Equipment repair stopped: reactor depleted.");
        }
        let (_, range, radius, _) =
            equipment_repair::tier(active.ability, self.skill_catalog.level(47))
                .ok_or_else(|| invalid("Active repair tier is no longer trained."))?;
        let period = self.equipment_repair_period_ms()?;
        let old_effect = self.next_effect;
        let previous_active = self.skills.equipment_repair.clone();
        self.next_effect = self.next_effect.wrapping_add(1).max(1);
        self.skills.pending_equipment_repair = Some(PendingEquipmentRepair {
            sequence: old_effect,
            target: active.target,
            rank: active.rank,
            range,
            radius,
        });
        active.next_due_ms = crate::resources::unix_ms() + period;
        self.skills.equipment_repair = Some(active);
        if let Err(error) = self.checkpoint_session(true) {
            self.next_effect = old_effect;
            self.skills.equipment_repair = previous_active;
            self.skills.pending_equipment_repair = None;
            return Err(error);
        }
        Ok(())
    }

    /// Returns:
    ///     Option<PendingEquipmentRepair>: Frozen tick awaiting atomic world settlement.
    pub(crate) fn pending_equipment_repair(&self) -> Option<PendingEquipmentRepair> {
        self.skills.pending_equipment_repair.clone()
    }

    /// Persist the removal of one settled tick before another can be prepared.
    pub(crate) fn clear_equipment_repair_tick(&mut self) -> io::Result<()> {
        let previous = self.skills.pending_equipment_repair.take();
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.pending_equipment_repair = previous;
            return Err(error);
        }
        Ok(())
    }

    /// Returns:
    ///     u64: Latest durable equipment repair revision applied to this pilot.
    pub(crate) fn equipment_repair_revision(&self) -> u64 {
        self.inventory.equipment_repair_revision
    }

    /// Returns:
    ///     Vec<Option<ItemInstance>>: Current mounted items for a frozen world tick.
    pub(crate) fn equipped_for_repair(&self) -> Vec<Option<crate::inventory::ItemInstance>> {
        self.inventory.equipped.clone()
    }

    /// Apply a committed world receipt once, preserving item identity and ownership.
    pub(crate) fn apply_equipment_balance(&mut self, balance: &EquipmentBalance) -> io::Result<()> {
        if balance.revision <= self.inventory.equipment_repair_revision {
            return Ok(());
        }
        if balance.equipped.len() != crate::inventory::EQUIPMENT_SLOTS {
            return Err(invalid("Invalid equipment repair balance dimensions."));
        }
        let mut candidate = self.inventory.clone();
        for (owned, repaired) in candidate.equipped.iter_mut().zip(&balance.equipped) {
            match (owned.as_mut(), repaired.as_ref()) {
                (Some(owned), Some(repaired))
                    if owned.template == repaired.template
                        && owned.quantity == repaired.quantity
                        && owned.quality == repaired.quality
                        && owned.builder_name == repaired.builder_name =>
                {
                    owned.structure = owned.structure.max(repaired.structure);
                }
                (None, None) => {}
                _ => return Err(invalid("Equipment changed before repair receipt replay.")),
            }
        }
        candidate.equipment_repair_revision = balance.revision;
        self.commit_inventory(candidate)
    }

    /// Advance one disposable stream tick without waiting for wall time.
    #[cfg(test)]
    pub(crate) fn equipment_repair_due_for_test(&mut self) {
        self.skills
            .equipment_repair
            .as_mut()
            .expect("active repair")
            .next_due_ms = crate::resources::unix_ms() - 1;
    }

    /// Returns:
    ///     Option<u32>: Disposable stream target for gameplay assertions.
    #[cfg(test)]
    pub(crate) fn equipment_repair_target_for_test(&self) -> Option<u32> {
        self.skills
            .equipment_repair
            .as_ref()
            .map(|repair| repair.target)
    }
}
