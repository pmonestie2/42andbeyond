//! Incoming NPC damage commits HP and a receipt together before external
//! reflection or feedback. The sector retains the shot until acknowledgment.
use super::*;
use crate::combat_damage::{
    damage_control_fraction, player_damage, DamageResult, DamageType, PlayerDefense,
};
use crate::npc_projectiles::{MobHit, MobLaunchVisual};

#[derive(Clone, Debug, Serialize, Deserialize)]
pub(super) struct MobDamageReceipt {
    pub(super) sector: u32,
    pub(super) sequence: u64,
    reflection: f32,
    #[serde(default)]
    wear: Option<EquipmentWear>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct EquipmentWear {
    slot: usize,
    amount: f32,
}

pub(super) fn valid_receipts(receipts: &[MobDamageReceipt]) -> bool {
    let mut identities = HashSet::new();
    receipts.len() <= 16384
        && receipts.iter().all(|receipt| {
            receipt.sector > 0
                && receipt.sequence > 0
                && receipt.reflection.is_finite()
                && (0. ..=crate::repulsor_field::MAX_TOTAL_REFLECTION).contains(&receipt.reflection)
                && receipt.sequence < (1u64 << 48)
                && receipt.wear.as_ref().is_none_or(|wear| {
                    wear.slot < crate::inventory::EQUIPMENT_SLOTS
                        && wear.amount.is_finite()
                        && (0. ..=0.03).contains(&wear.amount)
                })
                && identities.insert((receipt.sector, receipt.sequence))
        })
}

pub(super) struct DamagePlan {
    pub(super) result: DamageResult,
    pub(super) energy: f32,
    pub(super) reflection: f32,
    pub(super) motion: Motion,
    pub(super) warp: Warp,
    pub(super) warp_event: Option<WarpEvent>,
    pub(super) psionic_after: Option<crate::psionic_shield::PsionicShieldBuff>,
    pub(super) psionic_absorbed: f32,
    wear: Option<EquipmentWear>,
}

impl PlayerSession {
    fn plan_mob_damage(&self, hit: &MobHit, kind: DamageType) -> io::Result<DamagePlan> {
        let damage = hit.damage;
        let mut reduced = damage;
        let mut energy = self.energy;
        let mut reflection = 0.;
        if self.skills.environment_rank > 0 {
            let protection = crate::environment_gear::protect(
                self.skills.environment_rank,
                self.skills.environment_skill_bonus,
                reduced,
            );
            reduced = protection.damage;
            reflection = protection.reflection;
            energy = (energy + protection.reactor_gain).min(self.equipment.ship.energy_capacity);
        }
        let interception = crate::psionic_shield::intercept(
            self.psionic_shield_buff.as_ref(),
            reduced,
            crate::resources::unix_ms(),
            self.session_epoch,
        )
        .ok_or_else(|| invalid("Invalid Psionic Shield damage interception."))?;
        reduced = interception.passed;
        let profile = &self.account.character;
        let passive_damage_control = if profile.profession == 0 && matches!(profile.race, 0 | 2) {
            damage_control_fraction(self.skill_catalog.level(17))
        } else {
            0.
        };
        let damage_control = crate::combat_damage::combined_damage_control(
            passive_damage_control,
            self.equipment.ship.hull_damage_multiplier,
        );
        let result = player_damage(
            reduced,
            kind,
            PlayerDefense {
                shield: self.shield,
                hull: self.hull,
                deflect_percent: self.equipment.ship.deflects[kind.index()],
                damage_control,
            },
        )?;
        reflection += result.reported_damage * self.equipment.ship.counterstrike_percent / 100.;
        reflection += crate::energy_feedback::reflected(
            result.reported_damage,
            kind.index(),
            self.equipment.ship.energy_feedback_percent,
        );
        if let Some(field) = &self.repulsor_field_buff {
            let feedback = field
                .feedback(
                    result.reported_damage,
                    kind.index(),
                    crate::resources::unix_ms(),
                    self.session_epoch,
                )
                .ok_or_else(|| invalid("Invalid Repulsor Field feedback."))?;
            reflection += feedback.reflected;
        }
        // All reflected branches share one durable receipt and must remain
        // within its validated bound even for exceptional incoming hit sizes.
        reflection = reflection.min(crate::repulsor_field::MAX_TOTAL_REFLECTION);
        let mut motion = self.motion.clone();
        let mut warp = self.warp.clone();
        let warp_event = (result.hull_branch && warp.active())
            .then(|| warp.finish(FinishReason::Cancelled, &mut motion));
        if result.hull == 0. {
            motion.stop();
        }
        // The original manual permits equipment wear after shields fall. The
        // archived combat branch has no executable wear formula; choose one
        // deterministic mounted item and locally tune wear to hull damage.
        let mounted: Vec<usize> = self
            .inventory
            .equipped
            .iter()
            .enumerate()
            .filter_map(|(slot, item)| {
                item.as_ref()
                    .filter(|item| item.structure > 0.)
                    .map(|_| slot)
            })
            .collect();
        let wear = if result.hull_damage > 0. && !mounted.is_empty() {
            let slot = mounted[(hit.sequence as usize + hit.attacker_index) % mounted.len()];
            let category = crate::equipment_damage_control::category(slot);
            let base =
                (result.hull_damage / self.equipment.ship.hull_capacity * 0.15).clamp(0.001, 0.03);
            Some(EquipmentWear {
                slot,
                // Freeze the category-specific amount before the hit receipt
                // is committed. Gear changes or restart cannot alter a retry.
                amount: (base * self.equipment.ship.equipment_wear_multipliers[category]).min(0.03),
            })
        } else {
            None
        };
        Ok(DamagePlan {
            result,
            energy,
            reflection,
            motion,
            warp,
            warp_event,
            psionic_after: interception.remaining_buff,
            psionic_absorbed: interception.absorbed,
            wear,
        })
    }

    /// The frozen receipt survives a lost world acknowledgment and out-of-order
    /// projectile arrivals. A sequence high-water mark would lose slower shots.
    pub(crate) fn receive_mob_hit(&mut self, hit: &MobHit) -> io::Result<f32> {
        if !hit.valid() || hit.target != self.player_id() {
            return Err(invalid("Invalid incoming NPC shot identity."));
        }
        if let Some(receipt) = self
            .mob_damage_receipts
            .iter()
            .find(|receipt| receipt.sector == hit.sector && receipt.sequence == hit.sequence)
        {
            let receipt = receipt.clone();
            self.settle_mob_equipment_wear(&receipt)?;
            return Ok(receipt.reflection);
        }
        if !self.logged_in
            || self.hull <= 0.
            || self.sector.id != hit.sector
            || self.session_epoch != hit.target_epoch
        {
            return Ok(0.);
        }
        if self.transition_pending.is_some() || self.mob_damage_receipts.len() >= 16384 {
            return Err(invalid(
                "Incoming damage is waiting for pending durable state.",
            ));
        }
        self.advance_psionic_shield_buff()?;
        self.advance_repulsor_field_buff()?;
        let kind = DamageType::from_index(hit.damage_type)?;
        let plan = self.plan_mob_damage(hit, kind)?;
        let receipt = MobDamageReceipt {
            sector: hit.sector,
            sequence: hit.sequence,
            reflection: plan.reflection,
            wear: plan.wear.clone(),
        };
        // Acquire the sole external cleanup dependency before committing. No
        // mutation, packet, mining cancellation or reflection precedes the write.
        let resources = Arc::clone(&self.resource_field);
        let mut field = if self.mining.is_some() {
            Some(lock_resources(&resources)?)
        } else {
            None
        };
        let recharge_after_ms = (plan.result.shield < self.shield)
            .then(|| crate::resources::unix_ms().saturating_add(10_000));
        self.checkpoint_mob_damage(&plan, &receipt, recharge_after_ms)?;
        let prior_psionic = self.psionic_shield_buff.clone();
        self.mob_damage_receipts.push(receipt.clone());
        if let Some(deadline) = recharge_after_ms {
            self.shield_recharge_after_ms = deadline;
        }
        self.shield = plan.result.shield;
        self.hull = plan.result.hull;
        self.energy = plan.energy;
        self.warp = plan.warp;
        self.motion = plan.motion;
        self.psionic_shield_buff = plan.psionic_after;
        if let Some(prior) = prior_psionic.filter(|_| self.psionic_shield_buff.is_none()) {
            self.refresh_equipment()?;
            self.pending.push(packets::remove_effect(prior.effect));
            self.pending.push(packets::ship_active_buff_slot(
                self.player_id(),
                crate::psionic_shield::BUFF_SLOT,
                "",
                0,
            ));
            self.message("Psionic Shield depleted.");
        } else if plan.psionic_absorbed > 0. {
            self.message(&format!(
                "Psionic Shield absorbed {:.0} damage.",
                plan.psionic_absorbed
            ));
        }
        self.settle_mob_equipment_wear(&receipt)?;
        if let Some(cast) = self.mining.take() {
            field
                .as_mut()
                .expect("mining dependency acquired")
                .release(cast.node, self.player_id());
            self.emit_harvest_visual(Frame::new(0xf, cast.effect.to_le_bytes().to_vec()));
            if let Some(article) = cast.article {
                self.emit_harvest_visual(Frame::new(7, article.to_le_bytes().to_vec()));
            }
            self.pending
                .push(packets::message("Incoming fire interrupted prospecting."));
        }
        if let Some(event) = plan.warp_event {
            self.emit_warp_event(event);
        }
        if self.hull == 0. {
            self.stop_weapons();
            self.stop_following();
            self.detach_formation();
            self.motion.stop();
            self.pending
                .push(packets::incapacitated(self.player_id(), true));
        }
        self.pending.push(packets::client_damage(
            self.player_id(),
            MOB_ID + hit.attacker_index as u32,
            plan.result.reported_damage,
            plan.result.reported_damage - hit.damage,
            kind.index() as u32,
            1,
        ));
        self.pending.push(packets::ship_resources(
            self.player_id(),
            &self.equipment.ship,
            Some(self.energy),
            Some(self.shield),
            Some(self.hull),
        ));
        Ok(plan.reflection)
    }

    /// Persist one hull-hit wear receipt without applying it twice on retry.
    fn settle_mob_equipment_wear(&mut self, receipt: &MobDamageReceipt) -> io::Result<()> {
        let Some(wear) = &receipt.wear else {
            return Ok(());
        };
        let key = (u64::from(receipt.sector) << 48) | receipt.sequence;
        if self.inventory.applied_equipment_wear.contains(&key) {
            return Ok(());
        }
        let mut candidate = self.inventory.clone();
        let item = candidate.equipped[wear.slot]
            .as_mut()
            .ok_or_else(|| invalid("Equipment disappeared before hull-hit wear settled."))?;
        item.structure = (item.structure - wear.amount).max(0.);
        candidate.applied_equipment_wear.insert(key);
        self.commit_inventory(candidate)
    }

    /// Apply any durable hit wear left between a checkpoint and inventory write.
    pub(super) fn restore_mob_equipment_wear(&mut self) -> io::Result<()> {
        for receipt in self.mob_damage_receipts.clone() {
            self.settle_mob_equipment_wear(&receipt)?;
        }
        Ok(())
    }

    /// Called only after the authoritative sector has durably retired the shot.
    /// Persisting this cleanup can wait for the ordinary checkpoint; retaining
    /// an old receipt after a crash cannot recreate an acknowledged shot.
    pub(crate) fn forget_mob_hit_receipt(&mut self, sector: u32, sequence: u64) {
        self.mob_damage_receipts
            .retain(|receipt| receipt.sector != sector || receipt.sequence != sequence);
    }

    pub(crate) fn publish_mob_launch(&mut self, visual: &MobLaunchVisual) {
        if !self.logged_in
            || self.sector.id != visual.sector
            || self.sector.station.is_some()
            || !self
                .mob_visible
                .get(visual.attacker_index)
                .copied()
                .unwrap_or(false)
            || (visual.target != self.player_id() && !self.visible_peers.contains(&visual.target))
        {
            return;
        }
        self.pending.push(packets::weapon_effect(
            MOB_ID + visual.attacker_index as u32,
            visual.target,
            visual.effect,
            self.next_effect,
            self.tick_ms(),
            visual.duration_ms.min(u32::from(u16::MAX)) as u16,
        ));
        self.next_effect = self.next_effect.wrapping_add(1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ship() -> PlayerSession {
        let mut ship = super::super::tests::session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22343".parse().unwrap());
        ship.shield = 45.;
        ship.hull = 100.;
        ship.save().unwrap();
        ship
    }

    fn hit(ship: &PlayerSession, sequence: u64) -> MobHit {
        let combat = lock_combat(&ship.combat).unwrap();
        let mob = &combat.actors[0];
        MobHit {
            sector: ship.sector.id,
            sequence,
            target: ship.player_id(),
            target_epoch: ship.session_epoch,
            attacker_index: 0,
            attacker_source: mob.definition.source_id,
            attacker_template: mob.definition.template_id,
            attacker_generation: mob.death_generation,
            damage: 10.,
            damage_type: 3,
        }
    }

    /// Frozen equipped Environment strength changes actual incoming hit settlement.
    #[test]
    fn environment_gear_improves_authoritative_damage_and_feedback() {
        let mut ship = ship();
        ship.skills.environment_rank = 7;
        ship.energy = 10.;
        let mut shot = hit(&ship, 101);
        shot.damage = 100.;
        let baseline = ship
            .plan_mob_damage(&shot, DamageType::from_index(3).unwrap())
            .unwrap();
        ship.skills.environment_skill_bonus = 3.33;
        let geared = ship
            .plan_mob_damage(&shot, DamageType::from_index(3).unwrap())
            .unwrap();
        assert!(geared.result.reported_damage < baseline.result.reported_damage);
        assert!(geared.reflection > baseline.reflection);
        assert!(geared.energy > baseline.energy);
    }

    /// Hull integrity gear reduces a committed MOB hit after shields are gone.
    #[test]
    fn hull_integrity_gear_reduces_actual_mob_hull_damage() {
        let mut ship = ship();
        ship.shield = 0.;
        let mut shot = hit(&ship, 120);
        shot.damage = 50.;
        let baseline = ship.plan_mob_damage(&shot, DamageType::Energy).unwrap();
        ship.inventory.equipped[3] = Some(crate::inventory::ItemInstance::stock(929, 1));
        ship.refresh_equipment().unwrap();
        let geared = ship.plan_mob_damage(&shot, DamageType::Energy).unwrap();
        assert!(geared.result.hull_damage < baseline.result.hull_damage);
        assert_eq!(geared.result.shield_damage, 0.);
        let prior_hull = ship.hull;
        ship.receive_mob_hit(&shot).unwrap();
        assert!((ship.hull - (prior_hull - geared.result.hull_damage)).abs() < 0.001);
        let settled_hull = ship.hull;
        ship.receive_mob_hit(&shot).unwrap();
        assert_eq!(ship.hull, settled_hull);
    }

    /// Counterstrike reflection is frozen with the durable hit receipt.
    #[test]
    fn equipped_counterstrike_reflects_once_across_retry_and_restart() {
        let mut ship = ship();
        ship.inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(2178, 1));
        ship.refresh_equipment().unwrap();
        assert!((ship.equipment.ship.counterstrike_percent - 8.4).abs() < 0.001);
        let mut shot = hit(&ship, 121);
        shot.damage = 50.;
        let expected = ship
            .plan_mob_damage(&shot, DamageType::Energy)
            .unwrap()
            .result
            .reported_damage
            * 0.084;
        let first = ship.receive_mob_hit(&shot).unwrap();
        assert!((first - expected).abs() < 0.001);
        ship.inventory.equipped[9].as_mut().unwrap().structure = 0.;
        ship.refresh_equipment().unwrap();
        assert_eq!(ship.equipment.ship.counterstrike_percent, 0.);
        assert_eq!(ship.receive_mob_hit(&shot).unwrap(), first);
        let root = ship.state_dir.clone();
        let sector = ship.sector.clone();
        let bind = ship.bind;
        drop(ship);
        let mut resumed = PlayerSession::new(sector, bind, root).unwrap();
        assert_eq!(resumed.receive_mob_hit(&shot).unwrap(), first);
    }

    /// Energy Feedback adds only to Energy hits and replays its frozen return.
    #[test]
    fn equipped_energy_feedback_stacks_with_counterstrike_without_repeat() {
        let mut ship = ship();
        ship.inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(5335, 1));
        ship.inventory.equipped[10] = Some(crate::inventory::ItemInstance::stock(2178, 1));
        ship.refresh_equipment().unwrap();
        let mut shot = hit(&ship, 141);
        shot.damage = 50.;
        let energy_plan = ship.plan_mob_damage(&shot, DamageType::Energy).unwrap();
        let basis = energy_plan.result.reported_damage;
        assert!((energy_plan.reflection - basis * (0.042 + 0.084)).abs() < 0.001);
        let explosive_plan = ship.plan_mob_damage(&shot, DamageType::Explosive).unwrap();
        assert!(
            (explosive_plan.reflection - explosive_plan.result.reported_damage * 0.084).abs()
                < 0.001
        );
        let first = ship.receive_mob_hit(&shot).unwrap();
        assert!((first - energy_plan.reflection).abs() < 0.001);
        let mut inventory = ship.inventory.clone();
        inventory.equipped[9].as_mut().unwrap().structure = 0.;
        inventory.equipped[10].as_mut().unwrap().structure = 0.;
        ship.commit_inventory(inventory).unwrap();
        assert_eq!(ship.equipment.ship.energy_feedback_percent, 0.);
        assert_eq!(ship.receive_mob_hit(&shot).unwrap(), first);
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(restored.receive_mob_hit(&shot).unwrap(), first);
    }

    /// A hull hit wears only its selected equipment category at the frozen rate.
    #[test]
    fn category_damage_control_changes_authoritative_structure_wear() {
        let mut ship = ship();
        ship.shield = 0.;
        let mut shot = hit(&ship, 20);
        let baseline = ship.plan_mob_damage(&shot, DamageType::Energy).unwrap();
        assert_eq!(baseline.wear.as_ref().unwrap().slot, 0);
        ship.inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(2102, 1));
        ship.refresh_equipment().unwrap();
        let protected = ship.plan_mob_damage(&shot, DamageType::Energy).unwrap();
        assert_eq!(protected.wear.as_ref().unwrap().slot, 0);
        assert!(
            (protected.wear.as_ref().unwrap().amount / baseline.wear.as_ref().unwrap().amount
                - 100. / 158.)
                .abs()
                < 0.001
        );
        shot.sequence = 21;
        let reactor = ship.plan_mob_damage(&shot, DamageType::Energy).unwrap();
        assert_eq!(reactor.wear.as_ref().unwrap().slot, 1);
        assert!(
            (reactor.wear.as_ref().unwrap().amount - baseline.wear.as_ref().unwrap().amount).abs()
                < 0.001
        );

        // An equipped weapon can worsen reactor tolerance without changing its
        // own weapon category. Operation four is the catalog vulnerability.
        ship.inventory.equipped[3] = Some(crate::inventory::ItemInstance::stock(864, 1));
        ship.refresh_equipment().unwrap();
        let vulnerable = ship.plan_mob_damage(&shot, DamageType::Energy).unwrap();
        assert_eq!(vulnerable.wear.as_ref().unwrap().slot, 1);
        assert!(
            (vulnerable.wear.as_ref().unwrap().amount / reactor.wear.as_ref().unwrap().amount
                - 1.25)
                .abs()
                < 0.001
        );
    }

    /// Device and engine control affect their own slots after a real hull hit.
    #[test]
    fn device_and_engine_damage_control_match_native_mount_categories() {
        let mut ship = ship();
        ship.shield = 0.;
        ship.inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1758, 1));
        ship.refresh_equipment().unwrap();
        let mut shot = hit(&ship, 24);
        let ordinary_device = ship.plan_mob_damage(&shot, DamageType::Energy).unwrap();
        assert_eq!(ordinary_device.wear.as_ref().unwrap().slot, 9);
        ship.inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(2109, 1));
        ship.refresh_equipment().unwrap();
        let protected_device = ship.plan_mob_damage(&shot, DamageType::Energy).unwrap();
        assert!(
            (protected_device.wear.as_ref().unwrap().amount
                / ordinary_device.wear.as_ref().unwrap().amount
                - 100. / 158.)
                .abs()
                < 0.001
        );

        shot.sequence = 22;
        let ordinary_engine = ship.plan_mob_damage(&shot, DamageType::Energy).unwrap();
        assert_eq!(ordinary_engine.wear.as_ref().unwrap().slot, 2);
        ship.inventory.equipped[2] = Some(crate::inventory::ItemInstance::stock(2527, 1));
        ship.refresh_equipment().unwrap();
        let protected_engine = ship.plan_mob_damage(&shot, DamageType::Energy).unwrap();
        assert!(
            (protected_engine.wear.as_ref().unwrap().amount
                / ordinary_engine.wear.as_ref().unwrap().amount
                - 100. / 138.)
                .abs()
                < 0.001
        );
    }

    /// A failed inventory settlement replays the pre-hit protected wear amount.
    #[test]
    fn protected_equipment_wear_receipt_is_frozen_across_restart_and_gear_change() {
        let mut ship = ship();
        ship.shield = 0.;
        ship.inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(2102, 1));
        ship.refresh_equipment().unwrap();
        ship.save().unwrap();
        let shot = hit(&ship, 25);
        let frozen = ship.plan_mob_damage(&shot, DamageType::Energy).unwrap();
        assert_eq!(frozen.wear.as_ref().unwrap().slot, 0);
        let old_structure = ship.inventory.equipped[0].as_ref().unwrap().structure;
        let blocked = ship.state_dir.join("inventory.json.tmp");
        fs::create_dir(&blocked).unwrap();
        assert!(ship.receive_mob_hit(&shot).is_err());
        assert_eq!(
            ship.inventory.equipped[0].as_ref().unwrap().structure,
            old_structure
        );
        fs::remove_dir(&blocked).unwrap();
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        let expected = old_structure - frozen.wear.as_ref().unwrap().amount;
        assert!(
            (restored.inventory.equipped[0].as_ref().unwrap().structure - expected).abs() < 0.0001
        );
        restored.inventory.equipped[9] = None;
        restored.refresh_equipment().unwrap();
        let structure = restored.inventory.equipped[0].as_ref().unwrap().structure;
        restored.receive_mob_hit(&shot).unwrap();
        assert_eq!(
            restored.inventory.equipped[0].as_ref().unwrap().structure,
            structure
        );
        assert_eq!(restored.inventory.applied_equipment_wear.len(), 1);
    }

    #[test]
    fn hp_and_out_of_order_receipts_restore_together_and_replays_never_repeat_feedback() {
        let mut ship = ship();
        let slow = hit(&ship, 1);
        let fast = hit(&ship, 2);
        ship.receive_mob_hit(&fast).unwrap();
        ship.receive_mob_hit(&slow).unwrap();
        assert_eq!(ship.shield, 25.);
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(restored.logged_in);
        assert_eq!(restored.shield, 25.);
        let pending = restored.pending.len();
        restored.receive_mob_hit(&slow).unwrap();
        restored.receive_mob_hit(&fast).unwrap();
        assert_eq!(restored.shield, 25.);
        assert_eq!(restored.pending.len(), pending);
        restored.forget_mob_hit_receipt(slow.sector, slow.sequence);
        assert_eq!(restored.mob_damage_receipts.len(), 1);
    }

    #[test]
    fn failed_damage_commit_preserves_resources_motion_receipts_and_external_mob_health() {
        let mut ship = ship();
        ship.skills.environment_rank = 7;
        ship.energy = 10.;
        let hit = hit(&ship, 1);
        let blocked = ship.state_dir.join("session.local.json.next");
        fs::create_dir(&blocked).unwrap();
        let pending = ship.pending.len();
        let original = lock_combat(&ship.combat).unwrap().actors[0].health;
        assert!(ship.receive_mob_hit(&hit).is_err());
        assert_eq!((ship.shield, ship.hull, ship.energy), (45., 100., 10.));
        assert!(ship.mob_damage_receipts.is_empty());
        assert_eq!(ship.pending.len(), pending);
        assert_eq!(
            lock_combat(&ship.combat).unwrap().actors[0].health,
            original
        );
        fs::remove_dir(blocked).unwrap();
        assert_eq!(ship.receive_mob_hit(&hit).unwrap(), 9.);
        assert_eq!((ship.shield, ship.energy), (36., 10.45));
        assert_eq!(
            lock_combat(&ship.combat).unwrap().actors[0].health,
            original
        );
        assert_eq!(ship.receive_mob_hit(&hit).unwrap(), 9.);
        assert_eq!((ship.shield, ship.energy), (36., 10.45));
    }

    /// A hull-hit receipt replays one mounted-item wear after a torn inventory write.
    #[test]
    fn hull_hit_equipment_wear_survives_restart_without_repeating() {
        let mut ship = ship();
        ship.shield = 0.;
        ship.save().unwrap();
        let pristine = ship.inventory.equipped.clone();
        let shot = hit(&ship, 41);
        let blocked = ship.state_dir.join("inventory.json.tmp");
        fs::create_dir(&blocked).unwrap();
        assert!(ship.receive_mob_hit(&shot).is_err());
        assert_eq!(ship.hull, 90.);
        assert_eq!(ship.mob_damage_receipts.len(), 1);
        assert!(ship.inventory.applied_equipment_wear.is_empty());
        fs::remove_dir(blocked).unwrap();

        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(restored.hull, 90.);
        assert_eq!(restored.inventory.applied_equipment_wear.len(), 1);
        let worn = restored.inventory.equipped.clone();
        assert_ne!(worn, pristine);
        let pending = restored.pending.len();
        restored.receive_mob_hit(&shot).unwrap();
        assert_eq!(restored.inventory.equipped, worn);
        assert_eq!(restored.inventory.applied_equipment_wear.len(), 1);
        assert_eq!(restored.pending.len(), pending);
        let again =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(again.inventory.equipped, worn);
    }

    /// A shield-only impact leaves mounted structure and the wear journal intact.
    #[test]
    fn shield_only_mob_hit_does_not_wear_equipment() {
        let mut ship = ship();
        let initial = ship.inventory.equipped.clone();
        ship.receive_mob_hit(&hit(&ship, 42)).unwrap();
        assert_eq!(ship.inventory.equipped, initial);
        assert!(ship.inventory.applied_equipment_wear.is_empty());
    }

    /// Incoming shield damage pauses passive recharge across session recovery.
    #[test]
    fn mob_shield_hit_delays_passive_recharge_for_ten_seconds() {
        let mut ship = ship();
        let before = ship.shield;
        ship.receive_mob_hit(&hit(&ship, 43)).unwrap();
        assert!(ship.shield < before);
        let damaged = ship.shield;
        let deadline = ship.shield_recharge_after_ms;
        assert!(deadline >= crate::resources::unix_ms().saturating_add(9_000));
        ship.advance_ship_resources(1.);
        assert_eq!(ship.shield, damaged);

        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(restored.shield_recharge_after_ms, deadline);
        restored.advance_ship_resources(1.);
        assert_eq!(restored.shield, damaged);
        restored.shield_recharge_after_ms = crate::resources::unix_ms().saturating_sub(1);
        restored.advance_ship_resources(1.);
        assert!(restored.shield > damaged);
    }

    /// Post-deflect Repulsor feedback is frozen in the NPC receipt across retry.
    #[test]
    fn repulsor_feedback_is_post_deflect_and_replayed_once() {
        let mut ship = ship();
        let now = crate::resources::unix_ms();
        ship.repulsor_field_buff = Some(crate::repulsor_field::RepulsorBuff {
            revision: 1,
            source: ship.player_id(),
            target_epoch: ship.session_epoch,
            ability: 72,
            trained_rank: 7,
            effective_rank: 7.,
            expires_at_ms: now + crate::repulsor_field::duration_ms(7.),
        });
        ship.repulsor_field_revision = 1;
        ship.checkpoint_session(true).unwrap();
        let shot = hit(&ship, 59);
        let plan = ship
            .plan_mob_damage(&shot, DamageType::from_index(3).unwrap())
            .unwrap();
        assert!((plan.reflection - plan.result.reported_damage * 0.6).abs() < 0.01);
        let once = ship.receive_mob_hit(&shot).unwrap();
        assert!((once - plan.reflection).abs() < 0.01);
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        let shield_after = restored.shield;
        assert_eq!(restored.receive_mob_hit(&shot).unwrap(), once);
        assert_eq!(restored.shield, shield_after);
        assert_eq!(restored.mob_damage_receipts.len(), 1);
    }

    /// A barrier and the ship's HP share the NPC-shot receipt across restart.
    #[test]
    fn psionic_barrier_intercepts_once_and_replays_spent_balance() {
        let mut ship = ship();
        let now = crate::resources::unix_ms();
        ship.psionic_shield_buff = Some(crate::psionic_shield::PsionicShieldBuff {
            revision: 1,
            source: ship.player_id(),
            target_epoch: ship.session_epoch,
            ability: 69,
            trained_rank: 3,
            effect: 91,
            expires_at_ms: now + crate::psionic_shield::DURATION_MS,
            remaining: 15.,
        });
        ship.psionic_shield_revision = 1;
        ship.refresh_equipment().unwrap();
        ship.checkpoint_session(true).unwrap();
        ship.publish_psionic_shield_resume();
        assert!(ship.pending.iter().any(|packet| {
            packet.opcode == 9
                && packet.body.get(5..7)
                    == Some(&crate::psionic_shield::VISUAL_DESCRIPTOR.to_le_bytes())
        }));
        let first = hit(&ship, 61);
        let blocked = ship.state_dir.join("session.local.json.next");
        fs::create_dir(&blocked).unwrap();
        assert!(ship.receive_mob_hit(&first).is_err());
        assert_eq!(ship.shield, 45.);
        assert_eq!(ship.psionic_shield_buff.as_ref().unwrap().remaining, 15.);
        fs::remove_dir(&blocked).unwrap();
        ship.receive_mob_hit(&first).unwrap();
        assert_eq!(ship.shield, 45.);
        assert_eq!(ship.psionic_shield_buff.as_ref().unwrap().remaining, 5.);
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(restored.shield, 45.);
        assert_eq!(restored.psionic_shield_buff.as_ref().unwrap().remaining, 5.);
        assert!(restored.pending.iter().any(|packet| {
            packet.opcode == 9
                && packet.body.get(5..7)
                    == Some(&crate::psionic_shield::VISUAL_DESCRIPTOR.to_le_bytes())
        }));
        let original_packets = restored.pending.len();
        restored.receive_mob_hit(&first).unwrap();
        assert_eq!(restored.pending.len(), original_packets);
        assert_eq!(restored.psionic_shield_buff.as_ref().unwrap().remaining, 5.);
        let second = hit(&restored, 62);
        restored.receive_mob_hit(&second).unwrap();
        assert!(restored.psionic_shield_buff.is_none());
        assert_eq!(restored.shield, 40.);
        assert!(restored.pending.contains(&packets::remove_effect(91)));
        assert!(restored.pending.contains(&packets::ship_active_buff_slot(
            restored.player_id(),
            crate::psionic_shield::BUFF_SLOT,
            "",
            0,
        )));
        let mut again =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(again.psionic_shield_buff.is_none());
        assert_eq!(again.shield, 40.);
        again.receive_mob_hit(&second).unwrap();
        assert_eq!(again.shield, 40.);
    }

    #[test]
    fn forged_identity_wrong_sector_and_invalid_receipts_do_not_damage_a_player() {
        let mut ship = ship();
        let mut shot = hit(&ship, 1);
        shot.target += 1;
        assert!(ship.receive_mob_hit(&shot).is_err());
        shot.target -= 1;
        shot.sector += 1;
        assert_eq!(ship.receive_mob_hit(&shot).unwrap(), 0.);
        assert_eq!(ship.shield, 45.);
        assert!(valid_receipts(&[]));
        let receipt = MobDamageReceipt {
            sector: 1076,
            sequence: 1,
            reflection: 0.,
            wear: None,
        };
        assert!(!valid_receipts(&[receipt.clone(), receipt]));
    }

    #[test]
    fn a_fresh_login_cannot_inherit_a_previous_transports_unacknowledged_hit() {
        let mut ship = ship();
        let original = hit(&ship, 1);
        ship.receive_mob_hit(&original).unwrap();
        assert_eq!(ship.shield, 35.);
        ship.logged_in = false;
        ship.forget_session().unwrap();
        let mut fresh =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        fresh.logged_in = true;
        fresh.selected_peer = Some("127.0.0.1".parse().unwrap());
        fresh.endpoint = Some("127.0.0.1:22343".parse().unwrap());
        assert_ne!(fresh.session_epoch, original.target_epoch);
        let shield = fresh.shield;
        assert_eq!(fresh.receive_mob_hit(&original).unwrap(), 0.);
        assert_eq!(fresh.shield, shield);
        assert!(fresh.mob_damage_receipts.is_empty());
    }
}
