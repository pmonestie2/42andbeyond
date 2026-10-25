//! Completion of a charged Gravity Link cast against persistent hostile MOBs.
use super::*;
use crate::gravity_link::{self, GravityDebuff};
use crate::skills::{AbilityDefinition, Cast};

impl PlayerSession {
    /// Returns:
    ///     u64: Current death generation of the selected living hostile MOB.
    pub(super) fn gravity_target_generation(&self, target: u32) -> io::Result<u64> {
        let index = target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Select a hostile MOB for Gravity Link."))?;
        let combat = lock_combat(&self.combat)?;
        let mob = combat
            .actors
            .get(index as usize)
            .ok_or_else(|| invalid("Gravity Link target is missing."))?;
        if !self.mob_visible_in_scan(index as usize, mob) {
            return Err(invalid("Gravity Link target is not visible."));
        }
        Ok(mob.death_generation)
    }

    /// Returns:
    ///     bool: Whether a visible, in-range target retained the durable cast.
    pub(super) fn apply_gravity_link(
        &self,
        ability: &AbilityDefinition,
        cast: &Cast,
    ) -> io::Result<bool> {
        let trained = self.skill_catalog.level(25);
        let (rank, range, radius) = gravity_link::tier(ability.id, trained)
            .ok_or_else(|| invalid("Gravity Link tier is no longer trained."))?;
        if cast.gravity_generation.is_none() || self.sector.station.is_some() {
            return Ok(false);
        }
        let index =
            cast.target
                .checked_sub(MOB_ID)
                .ok_or_else(|| invalid("Gravity Link target is not a MOB."))? as usize;
        let mut combat = lock_combat(&self.combat)?;
        // A failed session checkpoint after world persistence retries this
        // exact cast without extending duration or charging reactor twice.
        if combat.actors.iter().any(|mob| {
            mob.gravity.iter().any(|effect| {
                effect.source == self.player_id()
                    && effect.epoch == self.session_epoch
                    && effect.effect == cast.effect
            })
        }) {
            return Ok(true);
        }
        let Some(primary) = combat.actors.get(index) else {
            return Ok(false);
        };
        if primary.death_generation != cast.gravity_generation.unwrap()
            || !self.mob_visible_in_scan(index, primary)
            || distance(self.position, primary.position) > range
        {
            return Ok(false);
        }
        let center = primary.position;
        let combat_level =
            crate::experience::track(self.inventory.xp_start_level, self.inventory.combat_xp)?
                .level;
        let now = crate::resources::unix_ms();
        let original = combat.actors.clone();
        let mut changed = false;
        for (victim, mob) in combat.actors.iter_mut().enumerate() {
            if mob.state == crate::mob::MobState::Dead
                || victim != index && (radius == 0. || distance(mob.position, center) > radius)
            {
                continue;
            }
            let effect = GravityDebuff {
                source: self.player_id(),
                epoch: self.session_epoch,
                effect: cast.effect,
                deadline_ms: now + gravity_link::DURATION_MS,
                speed_fraction: gravity_link::speed_fraction(
                    rank,
                    self.equipment.ship.gravity_link_skill,
                    combat_level,
                    mob.definition.level,
                ),
                impact_explosive_bonus: if rank >= 3 && rank != 6 { 0.3 } else { 0. },
            };
            changed |= mob.apply_gravity(effect);
        }
        if changed {
            if let Err(error) = combat.persist() {
                combat.actors = original;
                return Err(error);
            }
        }
        Ok(changed)
    }
}
