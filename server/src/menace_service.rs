//! Resolve a completed Menace cast against durable, shared MOB state.
use super::*;
use crate::menace::{self, MenaceFear};
use crate::skills::{AbilityDefinition, Cast};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct MenaceOutcome {
    pub affected: usize,
    pub resisted: usize,
}

impl PlayerSession {
    /// Returns:
    ///     u64: Death generation of a visible, living selected hostile MOB.
    pub(crate) fn menace_target_generation(&self, target: u32) -> io::Result<u64> {
        let index = target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Select a hostile MOB for Menace."))?
            as usize;
        let combat = lock_combat(&self.combat)?;
        let mob = combat
            .actors
            .get(index)
            .ok_or_else(|| invalid("Menace target is missing."))?;
        if !self.mob_visible_in_scan(index, mob)
            || mob.state == MobState::Dead
            || self.mob_relationship(mob) != 0
        {
            return Err(invalid("Menace target is not a visible hostile MOB."));
        }
        Ok(mob.death_generation)
    }

    /// Returns:
    ///     Option<MenaceOutcome>: Applied/resisted recipients, or a lost target.
    pub(crate) fn apply_menace(
        &self,
        ability: &AbilityDefinition,
        cast: &Cast,
    ) -> io::Result<Option<MenaceOutcome>> {
        let trained = self.skill_catalog.level(menace::SKILL_ID);
        let tier = menace::tier(ability.id, trained)
            .ok_or_else(|| invalid("Menace tier is no longer trained."))?;
        let Some(generation) = cast.menace_generation else {
            return Ok(None);
        };
        if !self.logged_in
            || self.hull <= 0.
            || self.sector.station.is_some()
            || self.warp.active()
            || self.gate_transit.is_some()
        {
            return Ok(None);
        }
        let index = cast
            .target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Menace target is not a MOB."))? as usize;
        let mut combat = lock_combat(&self.combat)?;
        let Some(primary) = combat.actors.get(index) else {
            return Ok(None);
        };
        if primary.state == MobState::Dead
            || primary.death_generation != generation
            || !self.mob_visible_in_scan(index, primary)
            || self.mob_relationship(primary) != 0
            || distance(primary.position, self.position) > tier.range
        {
            return Ok(None);
        }
        let center = primary.position;
        let combat_level =
            crate::experience::track(self.inventory.xp_start_level, self.inventory.combat_xp)?
                .level;
        let before = combat.actors.clone();
        let now = crate::resources::unix_ms();
        let mut outcome = MenaceOutcome::default();
        let mut changed = false;
        for (victim, mob) in combat.actors.iter_mut().enumerate() {
            if tier.radius == 0. && victim != index {
                continue;
            }
            if !menace::eligible_target(
                tier,
                self.position,
                center,
                mob.position,
                mob.state != MobState::Dead,
            ) || self.mob_relationship(mob) != 0
            {
                continue;
            }
            if mob.menace.iter().any(|existing| {
                existing.source == self.player_id()
                    && existing.source_epoch == self.session_epoch
                    && existing.effect == cast.effect
            }) {
                outcome.affected += 1;
                continue;
            }
            let chance = menace::success_chance(trained, combat_level, mob.definition.level);
            let roll =
                menace::cast_roll(cast.effect, mob.definition.source_id, mob.death_generation);
            if !menace::succeeds(chance, roll) {
                outcome.resisted += 1;
                continue;
            }
            let fear = MenaceFear {
                source: self.player_id(),
                source_epoch: self.session_epoch,
                effect: cast.effect,
                ability: ability.id,
                trained_rank: trained,
                mob_generation: mob.death_generation,
                origin: self.position,
                deadline_ms: now + tier.duration_ms,
            };
            if mob.apply_menace(fear) {
                outcome.affected += 1;
                changed = true;
            }
        }
        if changed {
            if let Err(error) = combat.persist() {
                combat.actors = before;
                return Err(error);
            }
        }
        Ok(Some(outcome))
    }
}
