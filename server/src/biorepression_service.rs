//! Settle Biorepression against the durable shared MOB world.
use super::*;
use crate::biorepression::{self, BioCandidate, BioCast, BioEffect};
use crate::skills::{AbilityDefinition, Cast};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct BioOutcome {
    pub affected: usize,
    pub resisted: usize,
}

impl PlayerSession {
    /// Returns:
    ///     u64: Current life generation of a visible organic hostile MOB.
    pub(crate) fn bio_target_generation(&self, target: u32) -> io::Result<u64> {
        let index = target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Select an organic MOB for Biorepression."))?
            as usize;
        let combat = lock_combat(&self.combat)?;
        let mob = combat
            .actors
            .get(index)
            .ok_or_else(|| invalid("Biorepression target is missing."))?;
        if !self.mob_visible_in_scan(index, mob)
            || mob.state == MobState::Dead
            || self.mob_relationship(mob) != 0
            || !biorepression::is_organic(mob.definition.mob_type)
        {
            return Err(invalid(
                "Biorepression requires a visible hostile organic MOB.",
            ));
        }
        Ok(mob.death_generation)
    }

    /// Returns:
    ///     Option<BioOutcome>: Persisted organic effects, or a lost selected life.
    pub(crate) fn apply_biorepression(
        &self,
        ability: &AbilityDefinition,
        cast: &Cast,
    ) -> io::Result<Option<BioOutcome>> {
        let trained = self.skill_catalog.level(biorepression::SKILL_ID);
        let band = biorepression::tier(ability.id, trained, self.account.character.class().code())
            .ok_or_else(|| invalid("Biorepression tier is no longer trained."))?;
        let Some(generation) = cast.bio_generation else {
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
        let index =
            cast.target
                .checked_sub(MOB_ID)
                .ok_or_else(|| invalid("Biorepression target is not a MOB."))? as usize;
        let mut combat = lock_combat(&self.combat)?;
        let Some(primary) = combat.actors.get(index) else {
            return Ok(None);
        };
        if primary.state == MobState::Dead
            || primary.death_generation != generation
            || !self.mob_visible_in_scan(index, primary)
            || self.mob_relationship(primary) != 0
            || !biorepression::is_organic(primary.definition.mob_type)
            || distance(primary.position, self.position) > band.range
        {
            return Ok(None);
        }
        let candidates: Vec<_> = combat
            .actors
            .iter()
            .enumerate()
            .map(|(i, mob)| BioCandidate {
                object_id: MOB_ID + i as u32,
                generation: mob.death_generation,
                position: mob.position,
                alive: mob.state != MobState::Dead
                    && self.mob_visible_in_scan(i, mob)
                    && self.mob_relationship(mob) == 0,
                organic: biorepression::is_organic(mob.definition.mob_type),
                // Public MOB simulation currently equips at most one weapon.
                weapon_slots: usize::from(mob.combat.dps > 0.),
            })
            .collect();
        let plan = biorepression::plan(
            BioCast {
                band,
                sector: self.sector.id,
                source: self.player_id(),
                source_epoch: self.session_epoch,
                effect: cast.effect,
                origin: self.position,
                selected: cast.target,
            },
            &candidates,
        )
        .ok_or_else(|| invalid("Invalid Biorepression target plan."))?;
        let combat_level =
            crate::experience::track(self.inventory.xp_start_level, self.inventory.combat_xp)?
                .level;
        let before = combat.actors.clone();
        let now = crate::resources::unix_ms();
        let mut outcome = BioOutcome::default();
        let mut changed = false;
        for target in plan.targets {
            let mob = &mut combat.actors[(target.object_id - MOB_ID) as usize];
            if mob.death_generation != target.generation || mob.state == MobState::Dead {
                continue;
            }
            if mob.bio.iter().any(|effect| {
                effect.source == self.player_id()
                    && effect.source_epoch == self.session_epoch
                    && effect.effect == cast.effect
            }) {
                outcome.affected += 1;
                continue;
            }
            if target.weapon_slot.is_some() {
                let chance = biorepression::disable_chance(
                    candidates[(target.object_id - MOB_ID) as usize].weapon_slots,
                    combat_level,
                    mob.definition.level,
                );
                let roll = biorepression::disable_roll(
                    cast.effect,
                    mob.definition.source_id,
                    mob.death_generation,
                );
                if roll >= chance {
                    outcome.resisted += 1;
                    continue;
                }
            }
            let effect = BioEffect {
                source: self.player_id(),
                source_epoch: self.session_epoch,
                effect: cast.effect,
                ability: ability.id,
                trained_rank: trained,
                mob_generation: mob.death_generation,
                weapon_slot: target.weapon_slot,
                deadline_ms: now.saturating_add(biorepression::duration_ms(trained)),
            };
            if mob.apply_bio(effect) {
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
