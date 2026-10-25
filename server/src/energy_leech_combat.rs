//! Atomic MOB reactor drain and replay receipt for Defender Energy Leech.
use super::*;
use crate::energy_leech::{
    self, LeechCandidate, LeechDrain, LeechReceipt, LeechSource, PendingEnergyLeech,
};
use crate::factions::FactionReactions;
use crate::mob::MobState;
use crate::protocol::invalid;

impl SectorCombat {
    /// Returns:
    ///     Option<LeechReceipt>: Committed result for one authenticated retry.
    pub fn energy_leech_receipt(
        &self,
        source: u32,
        source_epoch: u64,
        effect: u32,
    ) -> Option<LeechReceipt> {
        self.energy_leech_receipts
            .get(&source)
            .filter(|receipt| receipt.source_epoch == source_epoch && receipt.effect == effect)
            .cloned()
    }

    /// Persist MOB reactor drains and the replay identity in one world snapshot.
    ///
    /// Returns:
    ///     LeechReceipt: Frozen target lives, drains and friendly recipients.
    pub fn cast_energy_leech(
        &mut self,
        source: u32,
        source_epoch: u64,
        cast: PendingEnergyLeech,
        position: [f32; 3],
        reputation: &FactionReactions,
        recipients: Vec<u32>,
    ) -> io::Result<LeechReceipt> {
        if !cast.valid()
            || !(0x4000_0001..=0x4000_ffff).contains(&source)
            || source_epoch == 0
            || self.sector == 0
        {
            return Err(invalid("Invalid Energy Leech cast or sector."));
        }
        if let Some(prior) = self.energy_leech_receipts.get(&source) {
            if prior.source_epoch == source_epoch {
                if prior.effect == cast.effect {
                    return Ok(prior.clone());
                }
                if prior.effect > cast.effect {
                    return Err(invalid("Stale Energy Leech cast."));
                }
            }
        }
        let primary = cast
            .target
            .checked_sub(MOB_ID)
            .and_then(|index| self.actors.get(index as usize))
            .ok_or_else(|| invalid("Energy Leech primary target is missing."))?;
        if primary.death_generation != cast.target_generation {
            return Err(invalid("Energy Leech target life changed."));
        }
        let band = energy_leech::tier(cast.ability, cast.trained_rank, "jd")
            .ok_or_else(|| invalid("Energy Leech rank is not trained."))?;
        let candidates: Vec<_> = self
            .actors
            .iter()
            .enumerate()
            .map(|(index, mob)| LeechCandidate {
                object_id: MOB_ID + index as u32,
                generation: mob.death_generation,
                position: mob.position,
                alive: mob.state != MobState::Dead,
                hostile: reputation.relationship(mob.definition.faction, mob.targets(source)) == 0,
            })
            .collect();
        let plan = energy_leech::plan(
            band,
            LeechSource {
                sector: self.sector,
                source,
                source_epoch,
                effect: cast.effect,
                origin: position,
            },
            cast.target,
            &candidates,
        )
        .ok_or_else(|| invalid("Energy Leech target left range or is no longer hostile."))?;
        let mut drains = Vec::with_capacity(plan.targets.len());
        for target in plan.targets {
            let mob = &self.actors[(target.object_id - MOB_ID) as usize];
            drains.push(LeechDrain {
                target,
                amount: energy_leech::drained_energy(
                    mob.reactor_balance(),
                    mob.reactor_capacity(),
                    cast.trained_rank,
                ),
            });
        }
        let receipt = LeechReceipt {
            sector: self.sector,
            source,
            source_epoch,
            effect: cast.effect,
            ability: cast.ability,
            trained_rank: cast.trained_rank,
            drains,
            recipients,
        };
        if !receipt.valid() {
            return Err(invalid("Invalid Energy Leech receipt."));
        }
        let before = self.actors.clone();
        let previous = self.energy_leech_receipts.get(&source).cloned();
        for drain in &receipt.drains {
            let mob = &mut self.actors[(drain.target.object_id - MOB_ID) as usize];
            if mob.state == MobState::Dead || mob.death_generation != drain.target.generation {
                self.actors = before;
                return Err(invalid("Energy Leech MOB life changed during settlement."));
            }
            mob.drain_reactor_by(drain.amount);
        }
        self.energy_leech_receipts.insert(source, receipt.clone());
        if let Err(error) = self.persist() {
            self.actors = before;
            if let Some(previous) = previous {
                self.energy_leech_receipts.insert(source, previous);
            } else {
                self.energy_leech_receipts.remove(&source);
            }
            return Err(error);
        }
        Ok(receipt)
    }
}
