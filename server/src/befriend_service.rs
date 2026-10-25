//! Complete Befriend casts against the durable shared MOB world.
use super::*;
use crate::befriend::{self, BefriendCandidate, BefriendEffect, BefriendOrigin};
use crate::skills::{AbilityDefinition, Cast};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct BefriendOutcome {
    pub affected: usize,
    pub resisted: usize,
}

impl PlayerSession {
    /// Returns:
    ///     u64: Death generation of a visible, living selected MOB.
    pub(crate) fn befriend_target_generation(&self, target: u32) -> io::Result<u64> {
        let index = target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Select a MOB for Befriend."))? as usize;
        let combat = lock_combat(&self.combat)?;
        let mob = combat
            .actors
            .get(index)
            .ok_or_else(|| invalid("Befriend target is missing."))?;
        if !self.mob_visible_in_scan(index, mob) || mob.state == MobState::Dead {
            return Err(invalid("Befriend requires a visible living MOB."));
        }
        Ok(mob.death_generation)
    }

    /// Returns:
    ///     Option<BefriendOutcome>: Durable target results, or lost selected generation.
    pub(crate) fn apply_befriend(
        &self,
        ability: &AbilityDefinition,
        cast: &Cast,
    ) -> io::Result<Option<BefriendOutcome>> {
        let trained = self.skill_catalog.level(befriend::SKILL_ID);
        let band = befriend::tier(ability.id, trained, self.account.character.class().code())
            .ok_or_else(|| invalid("Befriend tier is no longer trained."))?;
        let Some(generation) = cast.befriend_generation else {
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
            .ok_or_else(|| invalid("Befriend target is not a MOB."))? as usize;
        let mut combat = lock_combat(&self.combat)?;
        let Some(primary) = combat.actors.get(index) else {
            return Ok(None);
        };
        if primary.state == MobState::Dead
            || primary.death_generation != generation
            || !self.mob_visible_in_scan(index, primary)
            || distance(primary.position, self.position) > band.range
        {
            return Ok(None);
        }
        let candidates: Vec<_> = combat
            .actors
            .iter()
            .enumerate()
            .map(|(i, mob)| BefriendCandidate {
                object_id: MOB_ID + i as u32,
                generation: mob.death_generation,
                position: mob.position,
                alive: mob.state != MobState::Dead && self.mob_visible_in_scan(i, mob),
            })
            .collect();
        let plan = befriend::plan(
            BefriendOrigin {
                band,
                sector: self.sector.id,
                source: self.player_id(),
                source_epoch: self.session_epoch,
                effect: cast.effect,
                position: self.position,
                selected: cast.target,
            },
            &candidates,
        )
        .ok_or_else(|| invalid("Invalid Befriend target plan."))?;
        let trade_level =
            crate::experience::track(self.inventory.xp_start_level, self.inventory.trade_xp)?.level;
        let before = combat.actors.clone();
        let now = crate::resources::unix_ms();
        let mut outcome = BefriendOutcome::default();
        let mut changed = false;
        for target in plan.targets {
            let mob = &mut combat.actors[(target.object_id - MOB_ID) as usize];
            if mob.death_generation != target.generation || mob.state == MobState::Dead {
                continue;
            }
            if mob.befriend.iter().any(|effect| {
                effect.source == self.player_id()
                    && effect.source_epoch == self.session_epoch
                    && effect.effect == cast.effect
            }) {
                outcome.affected += 1;
                continue;
            }
            let chance = befriend::success_chance_with_bonus(
                trained,
                self.equipment.ship.befriend_skill_bonus,
                trade_level,
                mob.definition.level,
            );
            let roll =
                befriend::cast_roll(cast.effect, mob.definition.source_id, mob.death_generation);
            if roll >= chance {
                outcome.resisted += 1;
                continue;
            }
            let effect = BefriendEffect {
                source: self.player_id(),
                source_epoch: self.session_epoch,
                effect: cast.effect,
                ability: ability.id,
                trained_rank: trained,
                mob_generation: mob.death_generation,
                deadline_ms: now.saturating_add(befriend::duration_ms_with_bonus(
                    trained,
                    self.equipment.ship.befriend_skill_bonus,
                )),
            };
            if mob.apply_befriend(effect) {
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
