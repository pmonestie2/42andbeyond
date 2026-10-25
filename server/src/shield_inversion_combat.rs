//! One durable world pulse burns shield power into hostile MOB energy damage.
//! The receipt is also the source's idempotency key for shield settlement.
use super::*;
use crate::factions::FactionReactions;
use crate::mob::{distance, MobState};
use crate::shield_inversion::{self, ActiveShieldInversion, InversionReceipt};

impl SectorCombat {
    /// Returns:
    ///     Option<InversionReceipt>: Previously committed pulse for this cast.
    pub fn inversion_receipt(&self, active: ActiveShieldInversion) -> Option<InversionReceipt> {
        self.inversion_receipts
            .get(&active.source)
            .filter(|receipt| {
                receipt.source == active.source
                    && receipt.sector == active.sector
                    && receipt.source_epoch == active.source_epoch
                    && receipt.effect == active.effect
                    && receipt.pulse_index == active.pulse_index
                    && receipt.target_generation == active.target_generation
            })
            .cloned()
    }

    /// Returns:
    ///     Option<InversionReceipt>: A saved damage pulse or no eligible target.
    pub fn cast_inversion(
        &mut self,
        active: ActiveShieldInversion,
        caster_position: [f32; 3],
        caster_reputation: &FactionReactions,
        shield_available: f32,
        now_ms: u64,
    ) -> io::Result<Option<InversionReceipt>> {
        if let Some(receipt) = self.inversion_receipt(active) {
            return Ok(Some(receipt));
        }
        let tier = shield_inversion::tier(active.ability, active.trained_rank)
            .ok_or_else(|| crate::protocol::invalid("Unknown Shield Inversion tier."))?;
        if !active.due(now_ms)
            || self.sector != active.sector
            || !caster_position.iter().all(|value| value.is_finite())
            || !shield_available.is_finite()
            || shield_available <= 0.
        {
            return Ok(None);
        }
        let Some(index) = active
            .target
            .checked_sub(MOB_ID)
            .map(|index| index as usize)
        else {
            return Ok(None);
        };
        let Some(primary) = self.actors.get(index) else {
            return Ok(None);
        };
        if primary.state == MobState::Dead
            || primary.death_generation != active.target_generation
            || primary.range_to(caster_position) > tier.range
            || caster_reputation
                .relationship(primary.definition.faction, primary.targets(active.source))
                != 0
        {
            return Ok(None);
        }
        let center = primary.position;
        let pulse = shield_inversion::pulse(tier, shield_available, shield_inversion::PULSE_MS);
        if pulse.shield_spent == 0. {
            return Ok(None);
        }
        let before = self.actors.clone();
        let mut targets = Vec::new();
        for (victim, mob) in self.actors.iter_mut().enumerate() {
            if mob.state == MobState::Dead
                || victim != index
                    && (tier.radius == 0. || distance(center, mob.position) > tier.radius)
                || caster_reputation
                    .relationship(mob.definition.faction, mob.targets(active.source))
                    != 0
            {
                continue;
            }
            mob.damage_by_typed(pulse.damage_per_target, active.source, 3);
            targets.push(MOB_ID + victim as u32);
        }
        let receipt = InversionReceipt {
            source: active.source,
            sector: active.sector,
            source_epoch: active.source_epoch,
            effect: active.effect,
            pulse_index: active.pulse_index,
            target_generation: active.target_generation,
            shield_spent: pulse.shield_spent,
            damage_per_target: pulse.damage_per_target,
            targets,
        };
        if !receipt.valid() {
            self.actors = before;
            return Err(crate::protocol::invalid(
                "Invalid Shield Inversion pulse receipt.",
            ));
        }
        // Only the most recent pulse can be unsettled in the account. Replacing
        // that source's preceding receipt bounds the world ledger indefinitely.
        let previous_receipt = self
            .inversion_receipts
            .insert(active.source, receipt.clone());
        if let Err(error) = self.persist() {
            self.actors = before;
            if let Some(previous) = previous_receipt {
                self.inversion_receipts.insert(active.source, previous);
            } else {
                self.inversion_receipts.remove(&active.source);
            }
            return Err(error);
        }
        Ok(Some(receipt))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn committed_pulse_replays_after_restart_without_second_damage() {
        let root =
            std::env::temp_dir().join(format!("enb-inversion-world-{}", rand::random::<u64>()));
        let path = root.join("combat.json");
        let sector: crate::content::Sector =
            serde_json::from_str(include_str!("../data/sector.json")).unwrap();
        let initial = SectorCombat::new(&sector.mobs);
        let mut world = SectorCombat::persistent(path.clone(), sector.id, &initial).unwrap();
        let target = &world.actors[0];
        let active = ActiveShieldInversion {
            source: 0x4000_0001,
            sector: sector.id,
            ability: 111,
            trained_rank: 1,
            target: MOB_ID,
            target_generation: target.death_generation,
            source_epoch: 1,
            effect: 7,
            expires_at_ms: 26_000,
            next_pulse_ms: 12_000,
            pulse_index: 0,
        };
        let before = target.health + target.shield;
        let position = target.position;
        let receipt = world
            .cast_inversion(active, position, &FactionReactions::default(), 100., 12_000)
            .unwrap()
            .unwrap();
        assert_eq!(receipt.shield_spent, 3.);
        assert_eq!(receipt.targets, vec![MOB_ID]);
        let after = world.actors[0].health + world.actors[0].shield;
        assert!(after < before);
        drop(world);
        let mut restored = SectorCombat::persistent(path.clone(), sector.id, &initial).unwrap();
        assert_eq!(restored.actors[0].health + restored.actors[0].shield, after);
        assert_eq!(
            restored
                .cast_inversion(active, position, &FactionReactions::default(), 100., 12_000)
                .unwrap()
                .unwrap()
                .shield_spent,
            3.
        );
        assert_eq!(restored.actors[0].health + restored.actors[0].shield, after);
        let next = ActiveShieldInversion {
            pulse_index: 1,
            next_pulse_ms: 13_000,
            ..active
        };
        std::fs::create_dir(path.with_extension("json.next")).unwrap();
        assert!(restored
            .cast_inversion(next, position, &FactionReactions::default(), 100., 13_000)
            .is_err());
        assert_eq!(restored.actors[0].health + restored.actors[0].shield, after);
        assert!(restored.inversion_receipt(next).is_none());
        assert!(restored.inversion_receipt(active).is_some());
        std::fs::remove_dir_all(root).unwrap();
    }
}
