//! Durable per-pilot half of native Hull Patch and Repair Hull.
use super::*;
use crate::energy_ledger::HullBalance;
use crate::skills::PendingHullRepair;

impl PlayerSession {
    /// Returns:
    ///     Option<PendingHullRepair>: Completed cast waiting for world settlement.
    pub(crate) fn pending_hull_repair(&self) -> Option<PendingHullRepair> {
        self.skills.pending_hull_repair.clone()
    }

    /// Returns:
    ///     u64: Last world hull revision applied to this pilot.
    pub(crate) fn hull_repair_revision(&self) -> u64 {
        self.hull_revision
    }

    /// Returns:
    ///     (f32, f32): Current hull points and equipped capacity.
    pub(crate) fn hull_balance(&self) -> (f32, f32) {
        (self.hull, self.equipment.ship.hull_capacity)
    }

    /// Apply a committed hull repair before publishing its AUX update.
    pub(crate) fn apply_hull_balance(&mut self, balance: HullBalance) -> io::Result<()> {
        if balance.revision <= self.hull_revision {
            return Ok(());
        }
        self.hull = balance.hull.min(self.equipment.ship.hull_capacity);
        self.hull_revision = balance.revision;
        let checkpoint = self.checkpoint_session(true);
        if self.logged_in {
            self.pending.push(packets::ship_resources(
                self.player_id(),
                &self.equipment.ship,
                None,
                None,
                Some(self.hull),
            ));
        }
        checkpoint
    }

    /// Clear a settled or invalid cast after replacing the session checkpoint.
    pub(crate) fn clear_hull_repair(&mut self) -> io::Result<()> {
        let pending = self.skills.pending_hull_repair.take();
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.pending_hull_repair = pending;
            return Err(error);
        }
        Ok(())
    }

    /// Publish the archived self flash or target beam and repair orb.
    pub(crate) fn publish_hull_repair(&mut self, target: u32, effect: u32, self_rank: bool) {
        let tick = self.tick_ms();
        if self_rank {
            let flash = self.next_effect;
            self.next_effect = self.next_effect.wrapping_add(1).max(1);
            self.pending
                .push(packets::object_effect(target, flash, 136, tick));
            self.skills.temporary_effects.insert(flash, 0.9);
        } else {
            self.pending
                .push(packets::beam(self.player_id(), target, 139, effect, tick));
            self.pending.push(packets::beam(
                self.player_id(),
                target,
                166,
                effect.wrapping_add(1),
                tick,
            ));
        }
        self.message("Hull repair completed.");
    }

    /// Set a disposable pilot's hull points for a repair/replacement test.
    #[cfg(test)]
    pub(crate) fn set_hull_for_test(&mut self, hull: f32) {
        assert!(hull > 0. && hull <= self.equipment.ship.hull_capacity);
        self.hull = hull;
    }
}
