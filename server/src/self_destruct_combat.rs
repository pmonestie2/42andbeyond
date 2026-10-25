//! Atomic absolute blast and one-result receipt in a shared sector snapshot.
use super::*;
use crate::factions::FactionReactions;
use crate::mob::MobState;
use crate::protocol::invalid;
use crate::self_destruct::{self, BlastReceipt, HostileLife, PendingSelfDestruct};

impl SectorCombat {
    /// Returns:
    ///     Option<BlastReceipt>: Existing outcome for an authenticated cast retry.
    pub fn self_destruct_receipt(&self, cast: PendingSelfDestruct) -> Option<BlastReceipt> {
        self.self_destruct_receipts
            .get(&cast.source)
            .filter(|receipt| {
                receipt.source_epoch == cast.source_epoch
                    && receipt.effect == cast.effect
                    && receipt.ability == cast.ability
            })
            .cloned()
    }

    /// Commit absolute damage to every hostile life before player settlement.
    ///
    /// Returns:
    ///     BlastReceipt: Persisted result, or the same result on crash replay.
    pub fn cast_self_destruct(
        &mut self,
        cast: PendingSelfDestruct,
        reputation: &FactionReactions,
    ) -> io::Result<BlastReceipt> {
        if !cast.valid() || cast.sector != self.sector {
            return Err(invalid("Invalid Self Destruct cast or sector."));
        }
        if let Some(prior) = self.self_destruct_receipts.get(&cast.source) {
            if prior.source_epoch == cast.source_epoch {
                if prior.effect == cast.effect {
                    return Ok(prior.clone());
                }
                if prior.effect > cast.effect {
                    return Err(invalid("Stale Self Destruct cast."));
                }
            }
        }
        let lives: Vec<HostileLife> = self
            .actors
            .iter()
            .enumerate()
            .map(|(index, mob)| HostileLife {
                object_id: MOB_ID + index as u32,
                generation: mob.death_generation,
                position: mob.position,
                alive: mob.state != MobState::Dead
                    && reputation.relationship(mob.definition.faction, mob.targets(cast.source))
                        == 0,
            })
            .collect();
        let plan = self_destruct::plan_blast(
            true,
            cast.source_epoch,
            cast.effect,
            cast.ability,
            cast.trained_rank,
            cast.position,
            &lives,
        )
        .ok_or_else(|| invalid("Invalid Self Destruct target plan."))?;
        let receipt = BlastReceipt {
            source: cast.source,
            source_epoch: cast.source_epoch,
            sector: cast.sector,
            effect: cast.effect,
            ability: cast.ability,
            trained_rank: cast.trained_rank,
            targets: plan.targets,
        };
        if !receipt.valid() {
            return Err(invalid("Invalid Self Destruct blast result."));
        }
        let before = self.actors.clone();
        let previous = self.self_destruct_receipts.get(&cast.source).cloned();
        for target in &receipt.targets {
            let index = target.object_id.saturating_sub(MOB_ID) as usize;
            let mob = &mut self.actors[index];
            if mob.death_generation != target.generation || mob.state == MobState::Dead {
                self.actors = before;
                return Err(invalid("Self Destruct MOB life changed during blast."));
            }
            mob.damage_by_typed(target.damage, cast.source, target.damage_type);
        }
        self.self_destruct_receipts
            .insert(cast.source, receipt.clone());
        if let Err(error) = self.persist() {
            self.actors = before;
            if let Some(previous) = previous {
                self.self_destruct_receipts.insert(cast.source, previous);
            } else {
                self.self_destruct_receipts.remove(&cast.source);
            }
            return Err(error);
        }
        Ok(receipt)
    }
}
