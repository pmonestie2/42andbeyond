//! Completion of a charged Enrage cast against persistent hostile MOBs.
use super::*;
use crate::enrage::{self, EnrageEffect};
use crate::skills::{AbilityDefinition, Cast};

impl PlayerSession {
    /// Returns:
    ///     u64: Current death generation of a selected visible hostile MOB.
    pub(super) fn enrage_target_generation(&self, target: u32) -> io::Result<u64> {
        let index = target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Select a hostile MOB for Enrage."))?;
        let combat = lock_combat(&self.combat)?;
        let mob = combat
            .actors
            .get(index as usize)
            .ok_or_else(|| invalid("Enrage target is missing."))?;
        if !self.mob_visible_in_scan(index as usize, mob)
            || mob.state == MobState::Dead
            || self.mob_relationship(mob) != 0
        {
            return Err(invalid("Enrage target is not a visible hostile MOB."));
        }
        Ok(mob.death_generation)
    }

    /// Returns:
    ///     bool: Whether a visible target or its formation retained the cast.
    pub(super) fn apply_enrage(
        &self,
        ability: &AbilityDefinition,
        cast: &Cast,
    ) -> io::Result<bool> {
        let trained = self.skill_catalog.level(22);
        let tier = enrage::tier(ability.id, trained)
            .ok_or_else(|| invalid("Enrage tier is no longer trained."))?;
        let Some(generation) = cast.enrage_generation else {
            return Ok(false);
        };
        let index = cast
            .target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Enrage target is not a MOB."))? as usize;
        let mut combat = lock_combat(&self.combat)?;
        if combat.actors.iter().any(|mob| {
            mob.enrage.iter().any(|effect| {
                effect.source == self.player_id()
                    && effect.source_epoch == self.session_epoch
                    && effect.effect == cast.effect
            })
        }) {
            return Ok(true);
        }
        let Some(primary) = combat.actors.get(index) else {
            return Ok(false);
        };
        if primary.death_generation != generation
            || !self.mob_visible_in_scan(index, primary)
            || self.mob_relationship(primary) != 0
            || primary.range_to(self.position) > tier.range
        {
            return Ok(false);
        }
        let center = primary.position;
        let formation = primary.definition.spawn_group;
        let caster_combat =
            crate::experience::track(self.inventory.xp_start_level, self.inventory.combat_xp)?
                .level;
        let now = crate::resources::unix_ms();
        let original = combat.actors.clone();
        let mut changed = false;
        for (victim, mob) in combat.actors.iter_mut().enumerate() {
            if mob.state == crate::mob::MobState::Dead
                || self.mob_relationship(mob) != 0
                || victim != index
                    && (!tier.group
                        || !enrage::formation_member(
                            index as u32,
                            victim as u32,
                            formation,
                            mob.definition.spawn_group,
                            distance(center, mob.position),
                        ))
            {
                continue;
            }
            let chance = enrage::success_chance(trained, caster_combat, mob.definition.level);
            if enrage::cast_roll(cast.effect, mob.definition.source_id, mob.death_generation)
                >= chance
            {
                continue;
            }
            let effect = EnrageEffect {
                source: self.player_id(),
                source_epoch: self.session_epoch,
                effect: cast.effect,
                target_generation: mob.death_generation,
                expires_at_ms: now.saturating_add(enrage::DURATION_MS),
                accuracy_multiplier: tier.accuracy_multiplier,
            };
            changed |= mob.apply_enrage(effect);
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
