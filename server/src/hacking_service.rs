//! Generation-pinned Hacking completion against the shared MOB world.
use super::*;
use crate::hacking::{self, HackCandidate, HackCast, HackEffect};
use crate::skills::{AbilityDefinition, Cast};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct HackingOutcome {
    pub affected: usize,
    pub resisted: usize,
}

impl PlayerSession {
    /// Returns:
    ///     u64: Generation of a selected visible nonorganic hostile MOB.
    pub(crate) fn hacking_target_generation(&self, target: u32) -> io::Result<u64> {
        let index = target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Select a hostile MOB for Hacking."))?
            as usize;
        let combat = lock_combat(&self.combat)?;
        let mob = combat
            .actors
            .get(index)
            .ok_or_else(|| invalid("Hacking target is missing."))?;
        if !self.mob_visible_in_scan(index, mob)
            || mob.state == MobState::Dead
            || self.mob_relationship(mob) != 0
            || matches!(mob.definition.mob_type, 2 | 3)
        {
            return Err(invalid(
                "Hacking requires a visible nonorganic hostile MOB.",
            ));
        }
        Ok(mob.death_generation)
    }

    /// Returns:
    ///     Option<HackingOutcome>: Persisted subsystem attempts, or lost primary target.
    pub(crate) fn apply_hacking(
        &self,
        ability: &AbilityDefinition,
        cast: &Cast,
    ) -> io::Result<Option<HackingOutcome>> {
        let trained = self.skill_catalog.level(hacking::SKILL_ID);
        let band = hacking::tier(ability.id, trained, self.account.character.class().code())
            .ok_or_else(|| invalid("Hacking tier is no longer trained."))?;
        let Some(generation) = cast.hacking_generation else {
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
            .ok_or_else(|| invalid("Hacking target is not a MOB."))? as usize;
        let mut combat = lock_combat(&self.combat)?;
        let Some(primary) = combat.actors.get(index) else {
            return Ok(None);
        };
        if primary.state == MobState::Dead
            || primary.death_generation != generation
            || !self.mob_visible_in_scan(index, primary)
            || self.mob_relationship(primary) != 0
            || matches!(primary.definition.mob_type, 2 | 3)
            || distance(primary.position, self.position) > band.range
        {
            return Ok(None);
        }
        let candidates: Vec<_> = combat
            .actors
            .iter()
            .enumerate()
            .map(|(i, mob)| {
                HackCandidate {
                    object_id: MOB_ID + i as u32,
                    generation: mob.death_generation,
                    position: mob.position,
                    alive: mob.state != MobState::Dead
                        && self.mob_visible_in_scan(i, mob)
                        && self.mob_relationship(mob) == 0,
                    organic: matches!(mob.definition.mob_type, 2 | 3),
                    // The authoritative MOB simulation currently has one weapon.
                    weapon_slots: usize::from(mob.combat.dps > 0.),
                }
            })
            .collect();
        let plan = hacking::plan(
            band,
            HackCast {
                sector: self.sector.id,
                caster: self.player_id(),
                caster_epoch: self.session_epoch,
                effect: cast.effect,
                source: self.position,
                selected: cast.target,
                ability: ability.id,
            },
            &candidates,
        )
        .ok_or_else(|| invalid("Invalid Hacking target plan."))?;
        let combat_level =
            crate::experience::track(self.inventory.xp_start_level, self.inventory.combat_xp)?
                .level;
        let before = combat.actors.clone();
        let now = crate::resources::unix_ms();
        let mut outcome = HackingOutcome::default();
        let mut changed = false;
        for attempt in plan.attempts {
            let mob = &mut combat.actors[(attempt.object_id - MOB_ID) as usize];
            if mob.death_generation != attempt.generation || mob.state == MobState::Dead {
                continue;
            }
            if mob.hacks.iter().any(|existing| {
                existing.source == self.player_id()
                    && existing.source_epoch == self.session_epoch
                    && existing.effect == cast.effect
            }) {
                outcome.affected += 1;
                continue;
            }
            let chance = hacking::success_chance(
                trained,
                combat_level,
                mob.definition.level,
                self.equipment.ship.hacking_bonus,
            );
            let seed = (cast.effect, mob.definition.source_id, mob.death_generation);
            let system = attempt
                .system
                .filter(|_| hacking::attempt_roll(seed.0, seed.1, seed.2, 1) < chance);
            let weapon_slot = attempt
                .weapon_slot
                .filter(|_| hacking::attempt_roll(seed.0, seed.1, seed.2, 2) < chance);
            let jam_communications =
                attempt.jam_communications && (system.is_some() || weapon_slot.is_some());
            if system.is_none() && weapon_slot.is_none() {
                outcome.resisted += 1;
                continue;
            }
            let effect = HackEffect {
                source: self.player_id(),
                source_epoch: self.session_epoch,
                effect: cast.effect,
                ability: ability.id,
                trained_rank: trained,
                mob_generation: mob.death_generation,
                system,
                weapon_slot,
                jam_communications,
                deadline_ms: now.saturating_add(hacking::duration_ms(trained)),
            };
            if mob.apply_hack(effect) {
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
