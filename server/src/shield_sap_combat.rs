//! Atomic MOB shield drain and its cast receipt in the sector combat snapshot.
use super::*;
use crate::mob::{distance, MobState};
use crate::protocol::invalid;
use crate::shield_sap::{self, PendingShieldSap, SapReceipt};

impl SectorCombat {
    /// Returns:
    ///     Option<SapReceipt>: Frozen result for an authenticated retry.
    pub fn shield_sap_receipt(&self, source: u32, epoch: u64, effect: u32) -> Option<SapReceipt> {
        self.sap_receipts
            .get(&source)
            .filter(|receipt| receipt.epoch == epoch && receipt.effect == effect)
            .cloned()
    }

    /// Drain once and freeze the MOB targets and friendly recipients for retry.
    ///
    /// Returns:
    ///     SapReceipt: Persisted result, including any earlier result for this cast.
    pub fn apply_shield_sap(
        &mut self,
        source: u32,
        epoch: u64,
        cast: &PendingShieldSap,
        caster_shield_capacity: f32,
        recipients: Vec<u32>,
    ) -> io::Result<SapReceipt> {
        if source < 0x4000_0001
            || epoch == 0
            || !cast.valid()
            || !caster_shield_capacity.is_finite()
            || caster_shield_capacity <= 0.
        {
            return Err(invalid("Invalid Shield Sap combat receipt."));
        }
        if let Some(prior) = self.sap_receipts.get(&source) {
            if prior.epoch == epoch {
                if prior.effect == cast.effect {
                    return Ok(prior.clone());
                }
                if prior.effect > cast.effect {
                    return Err(invalid("Stale Shield Sap cast sequence."));
                }
            }
        }
        let tier = shield_sap::tier(cast.ability, cast.trained_rank)
            .ok_or_else(|| invalid("Unknown Shield Sap rank."))?;
        let index = cast
            .target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Invalid Shield Sap MOB."))? as usize;
        let primary = self
            .actors
            .get(index)
            .ok_or_else(|| invalid("Shield Sap MOB left the sector."))?;
        if primary.state == MobState::Dead || primary.death_generation != cast.target_generation {
            return Err(invalid("Shield Sap MOB changed before completion."));
        }
        if tier.rank < 3 && !recipients.is_empty()
            || tier.rank >= 3 && !recipients.contains(&source)
            || !tier.group_heal && recipients.iter().any(|target| *target != source)
        {
            return Err(invalid("Invalid Shield Sap friendly recipients."));
        }
        let center = primary.position;
        let indices: Vec<usize> = self
            .actors
            .iter()
            .enumerate()
            .filter_map(|(index, mob)| {
                (mob.state != MobState::Dead
                    && (index == cast.target.saturating_sub(MOB_ID) as usize
                        || tier.radius > 0. && distance(mob.position, center) <= tier.radius))
                    .then_some(index)
            })
            .collect();
        let original = self.actors.clone();
        let previous = self.sap_receipts.get(&source).cloned();
        let mut targets = Vec::with_capacity(indices.len());
        let mut drained = 0.;
        for index in indices {
            let mob = &mut self.actors[index];
            let amount =
                shield_sap::drain_amount(mob.shield, caster_shield_capacity, cast.trained_rank);
            drained += mob.drain_shield_by(amount, source);
            targets.push(MOB_ID + index as u32);
        }
        let receipt = SapReceipt {
            epoch,
            effect: cast.effect,
            ability: cast.ability,
            trained_rank: cast.trained_rank,
            caster_shield_capacity,
            drained,
            targets,
            recipients,
        };
        if !receipt.valid() {
            self.actors = original;
            return Err(invalid("Invalid Shield Sap outcome."));
        }
        self.sap_receipts.insert(source, receipt.clone());
        if let Err(error) = self.persist() {
            self.actors = original;
            if let Some(previous) = previous {
                self.sap_receipts.insert(source, previous);
            } else {
                self.sap_receipts.remove(&source);
            }
            return Err(error);
        }
        Ok(receipt)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn drain_receipt_replays_without_a_second_mob_hit_and_rolls_back_failed_save() {
        let root = std::env::temp_dir().join(format!("enb-sap-world-{}", rand::random::<u64>()));
        let path = root.join("combat.json");
        let sector: crate::content::Sector =
            serde_json::from_str(include_str!("../data/sector.json")).unwrap();
        let initial = SectorCombat::new(&sector.mobs);
        let mut world = SectorCombat::persistent(path.clone(), sector.id, &initial).unwrap();
        let cast = PendingShieldSap {
            target: MOB_ID,
            target_generation: world.actors[0].death_generation,
            effect: 7,
            ability: 112,
            trained_rank: 1,
        };
        let before = world.actors[0].shield;
        let receipt = world
            .apply_shield_sap(0x4000_0001, 1, &cast, 1000., Vec::new())
            .unwrap();
        assert_eq!(receipt.targets, vec![MOB_ID]);
        assert!((receipt.drained - before * 0.15).abs() < 0.01);
        assert!((world.actors[0].shield - (before - receipt.drained)).abs() < 0.01);
        drop(world);
        let mut restored = SectorCombat::persistent(path.clone(), sector.id, &initial).unwrap();
        let after = restored.actors[0].shield;
        assert_eq!(
            restored
                .apply_shield_sap(0x4000_0001, 1, &cast, 1000., Vec::new())
                .unwrap()
                .drained,
            receipt.drained
        );
        assert_eq!(restored.actors[0].shield, after);
        let mut next = cast.clone();
        next.effect = 8;
        std::fs::create_dir(path.with_extension("json.next")).unwrap();
        assert!(restored
            .apply_shield_sap(0x4000_0001, 1, &next, 1000., Vec::new())
            .is_err());
        assert_eq!(restored.actors[0].shield, after);
        assert!(restored.shield_sap_receipt(0x4000_0001, 1, 8).is_none());
        std::fs::remove_dir_all(root).unwrap();
    }
}
