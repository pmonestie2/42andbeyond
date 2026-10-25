//! Session-side preparation and replay for Defender Energy Leech.
use super::*;
use crate::energy_leech::PendingEnergyLeech;
use crate::skills::{AbilityDefinition, Cast};

impl PlayerSession {
    /// Returns:
    ///     u64: Current generation of a visible, living hostile MOB.
    pub(crate) fn energy_leech_target_generation(&self, target: u32) -> io::Result<u64> {
        let index = target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Select a hostile MOB for Energy Leech."))?
            as usize;
        let combat = lock_combat(&self.combat)?;
        let mob = combat
            .actors
            .get(index)
            .ok_or_else(|| invalid("Energy Leech target is missing."))?;
        if !self.mob_visible_in_scan(index, mob)
            || mob.state == MobState::Dead
            || self.mob_relationship(mob) != 0
        {
            return Err(invalid("Energy Leech target is not hostile and visible."));
        }
        Ok(mob.death_generation)
    }

    /// Returns:
    ///     bool: Whether this Defender can still settle a reactor drain.
    pub(crate) fn energy_leech_available(&self) -> bool {
        self.logged_in
            && self.hull > 0.
            && self.sector.station.is_none()
            && !self.warp.active()
            && self.gate_transit.is_none()
            && self.mining.is_none()
    }

    /// Checkpoint the completed charge before the sector drains any MOB.
    pub(super) fn prepare_energy_leech(
        &mut self,
        ability: &AbilityDefinition,
        cast: &Cast,
    ) -> io::Result<()> {
        let pending = PendingEnergyLeech {
            target: cast.target,
            target_generation: cast
                .leech_generation
                .ok_or_else(|| invalid("Energy Leech lost its target generation."))?,
            effect: cast.effect,
            ability: ability.id,
            trained_rank: self.skill_catalog.level(crate::energy_leech::SKILL_ID),
        };
        if !pending.valid() || !self.energy_leech_available() {
            return Err(invalid("Energy Leech cannot settle this cast."));
        }
        self.skills.pending_energy_leech = Some(pending);
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.pending_energy_leech = None;
            return Err(error);
        }
        Ok(())
    }

    /// Returns:
    ///     Option<PendingEnergyLeech>: Durable cast waiting on shared world settlement.
    pub(crate) fn pending_energy_leech(&self) -> Option<PendingEnergyLeech> {
        self.skills.pending_energy_leech
    }

    /// Clear a settled cast only after the account checkpoint reaches disk.
    pub(crate) fn clear_pending_energy_leech(&mut self) -> io::Result<()> {
        let previous = self.skills.pending_energy_leech.take();
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.pending_energy_leech = previous;
            return Err(error);
        }
        Ok(())
    }

    /// Returns:
    ///     std::sync::Arc<FactionReactions>: Caster's current MOB relationships.
    pub(crate) fn energy_leech_reputation(
        &self,
    ) -> std::sync::Arc<crate::factions::FactionReactions> {
        std::sync::Arc::clone(&self.reputation)
    }
}
