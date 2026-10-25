//! Authoritative JE ability activation. Packet handling chooses an ability;
//! these methods validate target/range, charge energy, advance its cast and
//! apply effects. Group support is queued for the shared-world coordinator.
use super::*;
use crate::skills::{
    AbilityDefinition, AbilityKind, Cast, PendingHullRepair, PendingShieldRecharge, SupportAction,
};

impl PlayerSession {
    pub(super) fn begin_ability(&mut self, index: u32) -> io::Result<()> {
        self.begin_ability_for(index, self.target)
    }

    pub(super) fn begin_ability_for(
        &mut self,
        index: u32,
        requested_target: u32,
    ) -> io::Result<()> {
        if self.gate_transit.is_some() {
            return Err(invalid(
                "Wait for the gate transition before using abilities.",
            ));
        }
        let ability = self.skill_catalog.ability(index)?;
        let durable_repair = matches!(
            ability.kind,
            AbilityKind::RechargeShields | AbilityKind::HullPatch
        );
        let durable_rally = ability.kind == AbilityKind::Rally;
        let durable_optimization = ability.kind == AbilityKind::ReactorOptimization;
        let durable_shield_charging = ability.kind == AbilityKind::ShieldCharging;
        let durable_psionic_shield = ability.kind == AbilityKind::PsionicShield;
        let durable_repulsor = ability.kind == AbilityKind::RepulsorField;
        let durable_blast = ability.kind == AbilityKind::SelfDestruct;
        let durable_inversion = ability.kind == AbilityKind::ShieldInversion;
        let durable_sap = ability.kind == AbilityKind::ShieldSap;
        let durable_leech = ability.kind == AbilityKind::EnergyLeech;
        let durable_gravity = ability.kind == AbilityKind::GravityLink;
        let durable_menace = ability.kind == AbilityKind::Menace;
        let durable_hacking = ability.kind == AbilityKind::Hacking;
        let durable_befriend = ability.kind == AbilityKind::Befriend;
        let durable_bio = ability.kind == AbilityKind::Biorepression;
        let durable_high_fold = ability.kind == AbilityKind::Fold && matches!(ability.id, 17 | 36);
        let durable_summon = ability.kind == AbilityKind::Summon;
        let durable_enrage = ability.kind == AbilityKind::Enrage;
        if ability.kind == AbilityKind::Jumpstart {
            self.jumpstart_definition()?;
        }
        if ability.kind == AbilityKind::Unsupported {
            return Err(invalid("This class ability is not implemented yet."));
        }
        if self.self_destruct_action_locked() {
            return Err(invalid("Ship systems are recovering from Self Destruct."));
        }
        if !self.logged_in || self.sector.station.is_some() {
            return Err(invalid("Enter space before using an ability."));
        }
        if ability.kind == AbilityKind::EquipmentRepair {
            return self.toggle_equipment_repair(&ability, requested_target);
        }
        if ability.kind == AbilityKind::PowerDown {
            return self.toggle_power_down(&ability);
        }
        if ability.kind == AbilityKind::Cloak && self.skills.cloak_rank > 0 {
            self.stop_cloak();
            return Ok(());
        }
        if self.skills.cast.is_some() {
            return Err(invalid("Another ability is charging."));
        }
        if ability.kind == AbilityKind::RechargeShields
            && self.skills.pending_shield_recharge.is_some()
        {
            return Err(invalid("Previous shield recharge is still settling."));
        }
        if ability.kind == AbilityKind::HullPatch && self.skills.pending_hull_repair.is_some() {
            return Err(invalid("Previous hull repair is still settling."));
        }
        if durable_rally && self.skills.pending_rally.is_some() {
            return Err(invalid("Previous Rally is still settling."));
        }
        if durable_optimization && self.skills.pending_reactor_optimization.is_some() {
            return Err(invalid("Previous Reactor Optimization is still settling."));
        }
        if durable_shield_charging && self.skills.pending_shield_charging.is_some() {
            return Err(invalid("Previous Shield Charging is still settling."));
        }
        if durable_psionic_shield && self.skills.pending_psionic_shield.is_some() {
            return Err(invalid("Previous Psionic Shield is still settling."));
        }
        if durable_repulsor && self.skills.pending_repulsor_field.is_some() {
            return Err(invalid("Previous Repulsor Field is still settling."));
        }
        if durable_sap && self.skills.pending_shield_sap.is_some() {
            return Err(invalid("Previous Shield Sap is still settling."));
        }
        if durable_leech && self.skills.pending_energy_leech.is_some() {
            return Err(invalid("Previous Energy Leech is still settling."));
        }
        if durable_inversion && self.skills.active_inversion.is_some() {
            return Err(invalid("Shield Inversion is already active."));
        }
        if durable_blast && self.skills.pending_self_destruct.is_some() {
            return Err(invalid("Previous Self Destruct is still settling."));
        }
        if durable_high_fold && self.skills.pending_high_fold.is_some() {
            return Err(invalid("Previous Fold travel is still settling."));
        }
        let self_jumpstart = ability.kind == AbilityKind::Jumpstart
            && (requested_target == self.player_id() || requested_target == u32::MAX);
        if self.hull <= 0. && !self_jumpstart {
            return Err(invalid(
                "An incapacitated ship cannot activate this ability.",
            ));
        }
        if self.warp.active() && !(ability.kind == AbilityKind::Cloak && ability.rank >= 6) {
            return Err(invalid("Leave warp before activating this ability."));
        }
        if durable_repair && self.mining.is_some() {
            return Err(invalid("Stop prospecting before using repair abilities."));
        }
        if durable_sap && self.mining.is_some() {
            return Err(invalid("Stop prospecting before using Shield Sap."));
        }
        if durable_leech && self.mining.is_some() {
            return Err(invalid("Stop prospecting before using Energy Leech."));
        }
        if durable_inversion && self.mining.is_some() {
            return Err(invalid("Stop prospecting before using Shield Inversion."));
        }
        if durable_blast && self.mining.is_some() {
            return Err(invalid("Stop prospecting before using Self Destruct."));
        }
        if self
            .skills
            .cooldowns
            .get(&ability.skill)
            .is_some_and(|seconds| *seconds > 0.)
        {
            return Err(invalid("This skill is recovering."));
        }
        let target = self.ability_target(&ability, requested_target)?;
        if durable_high_fold
            && crate::fold_space_high::tier(
                ability.id,
                self.skill_catalog.level(crate::fold_space_high::SKILL_ID),
                self.account.character.class().code(),
            )
            .is_none()
        {
            return Err(invalid("High Fold rank is not trained."));
        }
        if durable_optimization
            && crate::reactor_optimization::tier(ability.id, self.skill_catalog.level(43)).is_none()
        {
            return Err(invalid("Reactor Optimization rank is not trained."));
        }
        if durable_shield_charging
            && crate::shield_charging::tier(ability.id, self.skill_catalog.level(51)).is_none()
        {
            return Err(invalid("Shield Charging rank is not trained."));
        }
        if durable_psionic_shield
            && crate::psionic_shield::tier(ability.id, self.skill_catalog.level(42)).is_none()
        {
            return Err(invalid("Psionic Shield rank is not trained."));
        }
        if durable_repulsor
            && crate::repulsor_field::tier(ability.id, self.skill_catalog.level(48)).is_none()
        {
            return Err(invalid("Repulsor Field rank is not trained."));
        }
        if durable_sap
            && crate::shield_sap::tier(ability.id, self.skill_catalog.level(54)).is_none()
        {
            return Err(invalid("Shield Sap rank is not trained."));
        }
        if durable_leech
            && crate::energy_leech::tier(
                ability.id,
                self.skill_catalog.level(19),
                self.account.character.class().code(),
            )
            .is_none()
        {
            return Err(invalid("Energy Leech rank is not trained."));
        }
        if durable_inversion
            && crate::shield_inversion::tier(ability.id, self.skill_catalog.level(52)).is_none()
        {
            return Err(invalid("Shield Inversion rank is not trained."));
        }
        if durable_blast
            && crate::self_destruct::tier(ability.id, self.skill_catalog.level(50)).is_none()
        {
            return Err(invalid("Self Destruct rank is not trained."));
        }
        if durable_menace && crate::menace::tier(ability.id, self.skill_catalog.level(33)).is_none()
        {
            return Err(invalid("Menace rank is not trained."));
        }
        if durable_hacking
            && crate::hacking::tier(
                ability.id,
                self.skill_catalog.level(26),
                self.account.character.class().code(),
            )
            .is_none()
        {
            return Err(invalid("Hacking rank is not trained."));
        }
        if durable_befriend
            && crate::befriend::tier(
                ability.id,
                self.skill_catalog.level(crate::befriend::SKILL_ID),
                self.account.character.class().code(),
            )
            .is_none()
        {
            return Err(invalid("Befriend rank is not trained."));
        }
        if durable_bio
            && crate::biorepression::tier(
                ability.id,
                self.skill_catalog.level(crate::biorepression::SKILL_ID),
                self.account.character.class().code(),
            )
            .is_none()
        {
            return Err(invalid("Biorepression rank is not trained."));
        }
        if durable_enrage && crate::enrage::tier(ability.id, self.skill_catalog.level(22)).is_none()
        {
            return Err(invalid("Enrage rank is not trained."));
        }
        let sap_generation = if durable_sap {
            Some(self.shield_sap_target_generation(target)?)
        } else {
            None
        };
        let leech_generation = if durable_leech {
            Some(self.energy_leech_target_generation(target)?)
        } else {
            None
        };
        let inversion_generation = if durable_inversion {
            Some(self.inversion_target_generation(target)?)
        } else {
            None
        };
        let gravity_generation = if durable_gravity {
            Some(self.gravity_target_generation(target)?)
        } else {
            None
        };
        let menace_generation = if durable_menace {
            Some(self.menace_target_generation(target)?)
        } else {
            None
        };
        let hacking_generation = if durable_hacking {
            Some(self.hacking_target_generation(target)?)
        } else {
            None
        };
        let befriend_generation = if durable_befriend {
            Some(self.befriend_target_generation(target)?)
        } else {
            None
        };
        let bio_generation = if durable_bio {
            Some(self.bio_target_generation(target)?)
        } else {
            None
        };
        let fold_mob_generation =
            if ability.id == 36 && (MOB_ID..MOB_ID + 0x1000_0000).contains(&target) {
                Some(self.high_fold_mob_generation(target)?)
            } else {
                None
            };
        let enrage_generation = if durable_enrage {
            Some(self.enrage_target_generation(target)?)
        } else {
            None
        };
        let summon_generation =
            if ability.kind == AbilityKind::Summon && matches!(ability.id, 117 | 118) {
                Some(self.summon_target_generation(target)?)
            } else {
                None
            };
        let summon_skill_bonus = if ability.kind == AbilityKind::Summon {
            self.equipment.ship.summon_skill_bonus
        } else {
            0.
        };
        let fold_distance_bonus = if ability.kind == AbilityKind::Fold {
            self.equipment.ship.fold_distance_bonus
        } else {
            0.
        };
        let jumpstart = if ability.kind == AbilityKind::Jumpstart {
            Some(self.prepare_jumpstart_plan(target)?)
        } else {
            None
        };
        let energy = jumpstart
            .as_ref()
            .map_or(ability.energy, |plan| plan.energy);
        let repair_multiplier = if durable_repair {
            match ability.rank {
                1 => 90.,
                3 => 360.,
                5 => 1440.,
                6 => 2880.,
                7 => 5760.,
                _ => return Err(invalid("Unknown repair ability rank.")),
            }
        } else {
            0.
        };
        let recharge_amount = (ability.kind == AbilityKind::RechargeShields)
            .then_some(self.equipment.ship.recharge_shields_skill * repair_multiplier);
        let hull_repair_amount = (ability.kind == AbilityKind::HullPatch)
            .then_some(self.equipment.ship.hull_patch_skill * repair_multiplier);
        let charge = if durable_repair {
            let trained = if ability.kind == AbilityKind::HullPatch {
                self.equipment.ship.hull_patch_skill
            } else {
                self.equipment.ship.recharge_shields_skill
            };
            (5. - (trained - ability.rank as f32)).clamp(0., 5.)
        } else if ability.kind == AbilityKind::Wormhole {
            (ability.charge - self.equipment.ship.wormhole_creation_reduction).max(0.25)
        } else {
            jumpstart
                .as_ref()
                .map_or(ability.charge, |plan| plan.charge_seconds)
        };
        if self.energy < energy {
            return Err(invalid("Insufficient reactor energy."));
        }
        if ability.kind == AbilityKind::Wormhole {
            self.sectors.sector(ability.destination.unwrap())?;
        }
        if ability.kind == AbilityKind::Cloak && ability.rank < 5 && self.motion.speed.abs() > 0.1 {
            return Err(invalid("Stop the ship before using this cloak rank."));
        }
        let pending_before = self.pending.len();
        let next_effect_before = self.next_effect;
        self.energy -= energy;
        let effect = self.next_effect;
        self.next_effect = self.next_effect.wrapping_add(1).max(1);
        let asset = match ability.kind {
            AbilityKind::Unsupported => return Err(invalid("Unsupported class ability.")),
            AbilityKind::Cloak => 180,
            AbilityKind::Contemplation => 1179,
            AbilityKind::Wormhole => 689,
            AbilityKind::Environment => 719,
            AbilityKind::Fold => 202,
            AbilityKind::Jumpstart => {
                if target == self.player_id() {
                    411
                } else {
                    414
                }
            }
            AbilityKind::Leech => 1059,
            AbilityKind::EnergyLeech => 0,
            AbilityKind::RechargeShields => 733,
            AbilityKind::HullPatch => 733,
            AbilityKind::Rally => 0,
            AbilityKind::ReactorOptimization => 0,
            AbilityKind::ShieldCharging => 0,
            AbilityKind::PsionicShield => 0,
            AbilityKind::RepulsorField => 0,
            AbilityKind::ShieldInversion => 0,
            AbilityKind::SelfDestruct => 0,
            AbilityKind::GravityLink => 0,
            AbilityKind::Menace => 0,
            AbilityKind::Hacking => 0,
            AbilityKind::Befriend => 0,
            AbilityKind::Biorepression => 0,
            AbilityKind::Enrage => 0,
            AbilityKind::ShieldSap => 733,
            AbilityKind::Summon => 0,
            AbilityKind::EquipmentRepair => unreachable!("toggle handled before charged cast"),
            AbilityKind::PowerDown => unreachable!("toggle handled before charged cast"),
        };
        if ability.kind == AbilityKind::Jumpstart {
            self.pending.push(packets::weapon_effect(
                self.player_id(),
                target,
                asset,
                effect,
                self.tick_ms(),
                500,
            ));
        } else if asset != 0 {
            self.pending.push(packets::object_effect(
                self.player_id(),
                effect,
                asset,
                self.tick_ms(),
            ));
        }
        self.pending.push(packets::ability_progress(
            Some(index),
            self.tick_ms(),
            self.tick_ms().wrapping_add((charge * 1000.) as u32),
        ));
        self.pending.push(packets::ship_resources(
            self.player_id(),
            &self.equipment.ship,
            Some(self.energy),
            None,
            None,
        ));
        self.skills.cast = Some(Cast {
            ability: index,
            target,
            remaining: charge,
            effect,
            jumpstart,
            recharge_amount,
            hull_repair_amount,
            gravity_generation,
            menace_generation,
            hacking_generation,
            befriend_generation,
            bio_generation,
            fold_mob_generation,
            fold_distance_bonus,
            enrage_generation,
            summon_generation,
            summon_skill_bonus,
            sap_generation,
            leech_generation,
            inversion_generation,
        });
        if durable_repair
            || durable_rally
            || durable_gravity
            || durable_menace
            || durable_hacking
            || durable_befriend
            || durable_bio
            || durable_high_fold
            || durable_summon
            || durable_enrage
            || durable_optimization
            || durable_shield_charging
            || durable_psionic_shield
            || durable_repulsor
            || durable_sap
            || durable_leech
            || durable_inversion
            || durable_blast
        {
            if let Err(error) = self.checkpoint_session(true) {
                self.energy += energy;
                self.skills.cast = None;
                self.next_effect = next_effect_before;
                self.pending.truncate(pending_before);
                return Err(error);
            }
        }
        crate::server_log!(
            "Ability charging: {}; {} ({index}); target={target}",
            self.account.character.name,
            ability.name
        );
        Ok(())
    }

    fn ability_target(
        &self,
        ability: &AbilityDefinition,
        requested_target: u32,
    ) -> io::Result<u32> {
        if ability.kind == AbilityKind::Jumpstart {
            return self.jumpstart_target(ability, requested_target);
        }
        if ability.kind == AbilityKind::Rally {
            return Ok(self.player_id());
        }
        if ability.kind == AbilityKind::SelfDestruct {
            return Ok(self.player_id());
        }
        if ability.kind == AbilityKind::Fold && ability.id == 17 {
            return Ok(self.player_id());
        }
        if ability.kind == AbilityKind::Fold && ability.id == 36 {
            let target = if self.target == 0 || self.target == u32::MAX {
                self.player_id()
            } else {
                self.target
            };
            if target == self.player_id() {
                return Ok(target);
            }
            if (MOB_ID..MOB_ID + 0x1000_0000).contains(&target) {
                self.high_fold_mob_generation(target)?;
                let combat = lock_combat(&self.combat)?;
                let mob = &combat.actors[(target - MOB_ID) as usize];
                if distance(self.position, mob.position) > 6_500. {
                    return Err(invalid("Directional Teleport MOB is out of range."));
                }
                return Ok(target);
            }
            let contact = self
                .group_contacts
                .iter()
                .find(|contact| contact.id == target)
                .ok_or_else(|| invalid("Select a visible group pilot for Directional Teleport."))?;
            if contact.incapacitated
                || contact.sector != self.sector.id
                || distance(self.position, contact.position) > 6_000.
            {
                return Err(invalid("Directional Teleport pilot is out of range."));
            }
            return Ok(target);
        }
        if ability.kind == AbilityKind::Summon {
            let tier = crate::summon::tier(ability.id, self.skill_catalog.level(56))
                .ok_or_else(|| invalid("Summon rank is not trained."))?;
            let range =
                crate::summon::effective_range(tier, self.equipment.ship.summon_skill_bonus);
            return match tier.target {
                crate::summon::SummonTarget::Group => Ok(self.player_id()),
                crate::summon::SummonTarget::Friend | crate::summon::SummonTarget::ReturnFriend => {
                    let contact = self
                        .group_contacts
                        .iter()
                        .find(|contact| contact.id == requested_target)
                        .ok_or_else(|| invalid("Select a group pilot for Summon."))?;
                    if contact.sector != self.sector.id
                        || contact.incapacitated
                        || distance(contact.position, self.position) > range
                    {
                        return Err(invalid("Summon recipient is unavailable or out of range."));
                    }
                    Ok(requested_target)
                }
                crate::summon::SummonTarget::Enemy
                | crate::summon::SummonTarget::EnemyFormation => {
                    let index = requested_target
                        .checked_sub(MOB_ID)
                        .ok_or_else(|| invalid("Select a hostile MOB for Summon."))?;
                    let combat = lock_combat(&self.combat)?;
                    let mob = combat
                        .actors
                        .get(index as usize)
                        .ok_or_else(|| invalid("Select a hostile MOB for Summon."))?;
                    if mob.state == MobState::Dead
                        || !self.mob_visible_in_scan(index as usize, mob)
                        || self.mob_relationship(mob) != 0
                        || mob.range_to(self.position) > range
                    {
                        return Err(invalid("Summon enemy is unavailable or out of range."));
                    }
                    Ok(requested_target)
                }
            };
        }
        if ability.kind == AbilityKind::ReactorOptimization {
            if ability.rank < 5
                || requested_target == self.player_id()
                || requested_target == u32::MAX
            {
                return Ok(self.player_id());
            }
            let contact = self
                .peer_contacts
                .get(&requested_target)
                .ok_or_else(|| invalid("Select a visible pilot for Reactor Optimization."))?;
            if contact.incapacitated
                || distance(contact.position, self.position) > ability.range as f32
            {
                return Err(invalid(
                    "Reactor Optimization target is dead or out of range.",
                ));
            }
            return Ok(requested_target);
        }
        if ability.kind == AbilityKind::ShieldCharging {
            if ability.rank < 5
                || requested_target == self.player_id()
                || requested_target == u32::MAX
            {
                return Ok(self.player_id());
            }
            let contact = self
                .peer_contacts
                .get(&requested_target)
                .ok_or_else(|| invalid("Select a visible pilot for Shield Charging."))?;
            if contact.incapacitated
                || distance(contact.position, self.position) > ability.range as f32
            {
                return Err(invalid("Shield Charging target is dead or out of range."));
            }
            return Ok(requested_target);
        }
        if ability.kind == AbilityKind::RepulsorField {
            let band = crate::repulsor_field::tier(ability.id, self.skill_catalog.level(48))
                .ok_or_else(|| invalid("Repulsor Field rank is not trained."))?;
            let recipient = if requested_target == self.player_id() || requested_target == u32::MAX
            {
                self.player_id()
            } else {
                requested_target
            };
            let contact = self.peer_contacts.get(&recipient);
            let position = if recipient == self.player_id() {
                self.position
            } else {
                contact
                    .ok_or_else(|| invalid("Select a visible pilot for Repulsor Field."))?
                    .position
            };
            if !crate::repulsor_field::target_in_range(
                band,
                self.player_id(),
                recipient,
                self.position,
                position,
                contact.is_none_or(|contact| !contact.incapacitated),
                recipient == self.player_id() || contact.is_some(),
            ) {
                return Err(invalid("Repulsor Field target is dead or out of range."));
            }
            return Ok(recipient);
        }
        if ability.kind == AbilityKind::PsionicShield {
            let tier = crate::psionic_shield::tier(ability.id, self.skill_catalog.level(42))
                .ok_or_else(|| invalid("Psionic Shield rank is not trained."))?;
            if requested_target == self.player_id() || requested_target == u32::MAX {
                return Ok(self.player_id());
            }
            let contact = self
                .peer_contacts
                .get(&requested_target)
                .ok_or_else(|| invalid("Select a visible pilot for Psionic Shield."))?;
            if !crate::psionic_shield::target_in_range(
                tier,
                self.position,
                contact.position,
                !contact.incapacitated,
            ) {
                return Err(invalid("Psionic Shield target is dead or out of range."));
            }
            return Ok(requested_target);
        }
        if ability.kind == AbilityKind::RechargeShields {
            if ability.rank == 1
                || requested_target == self.player_id()
                || requested_target == u32::MAX
            {
                return Ok(self.player_id());
            }
            let contact = self
                .peer_contacts
                .get(&requested_target)
                .ok_or_else(|| invalid("Select a visible pilot for Recharge Shields."))?;
            if contact.incapacitated
                || distance(contact.position, self.position) > ability.range as f32
            {
                return Err(invalid("Recharge Shields target is dead or out of range."));
            }
            return Ok(requested_target);
        }
        if ability.kind == AbilityKind::HullPatch {
            if ability.rank == 1
                || requested_target == self.player_id()
                || requested_target == u32::MAX
            {
                return Ok(self.player_id());
            }
            let contact = self
                .peer_contacts
                .get(&requested_target)
                .ok_or_else(|| invalid("Select a visible pilot for Repair Hull."))?;
            if contact.incapacitated
                || distance(contact.position, self.position) > ability.range as f32
            {
                return Err(invalid("Repair Hull target is dead or out of range."));
            }
            return Ok(requested_target);
        }
        let combat = lock_combat(&self.combat)?;
        let enemy = matches!(
            ability.kind,
            AbilityKind::Contemplation | AbilityKind::Leech
        ) || ability.id == 124
            || matches!(
                ability.kind,
                AbilityKind::GravityLink
                    | AbilityKind::ShieldSap
                    | AbilityKind::EnergyLeech
                    | AbilityKind::Menace
                    | AbilityKind::Hacking
                    | AbilityKind::Befriend
                    | AbilityKind::Biorepression
                    | AbilityKind::Enrage
                    | AbilityKind::ShieldInversion
            );
        if enemy {
            let index = self
                .target
                .checked_sub(MOB_ID)
                .ok_or_else(|| invalid("Select a hostile MOB first."))?;
            let mob = combat
                .actors
                .get(index as usize)
                .ok_or_else(|| invalid("Select a hostile MOB first."))?;
            let range = match ability.kind {
                AbilityKind::Hacking => {
                    crate::hacking::tier(
                        ability.id,
                        self.skill_catalog.level(26),
                        self.account.character.class().code(),
                    )
                    .ok_or_else(|| invalid("Hacking rank is not trained."))?
                    .range
                }
                AbilityKind::EnergyLeech => {
                    crate::energy_leech::tier(
                        ability.id,
                        self.skill_catalog.level(19),
                        self.account.character.class().code(),
                    )
                    .ok_or_else(|| invalid("Energy Leech rank is not trained."))?
                    .range
                }
                AbilityKind::Befriend => {
                    crate::befriend::tier(
                        ability.id,
                        self.skill_catalog.level(crate::befriend::SKILL_ID),
                        self.account.character.class().code(),
                    )
                    .ok_or_else(|| invalid("Befriend rank is not trained."))?
                    .range
                }
                AbilityKind::Biorepression => {
                    crate::biorepression::tier(
                        ability.id,
                        self.skill_catalog.level(crate::biorepression::SKILL_ID),
                        self.account.character.class().code(),
                    )
                    .ok_or_else(|| invalid("Biorepression rank is not trained."))?
                    .range
                }
                AbilityKind::Menace => {
                    crate::menace::tier(ability.id, self.skill_catalog.level(33))
                        .ok_or_else(|| invalid("Menace rank is not trained."))?
                        .range
                }
                AbilityKind::Enrage => {
                    crate::enrage::tier(ability.id, self.skill_catalog.level(22))
                        .ok_or_else(|| invalid("Enrage rank is not trained."))?
                        .range
                }
                AbilityKind::ShieldInversion => {
                    crate::shield_inversion::tier(ability.id, self.skill_catalog.level(52))
                        .ok_or_else(|| invalid("Shield Inversion rank is not trained."))?
                        .range
                }
                _ => ability.range as f32,
            };
            if !self.mob_visible_in_scan(index as usize, mob)
                || matches!(
                    ability.kind,
                    AbilityKind::Menace
                        | AbilityKind::Hacking
                        | AbilityKind::EnergyLeech
                        | AbilityKind::Biorepression
                        | AbilityKind::Enrage
                        | AbilityKind::ShieldInversion
                ) && self.mob_relationship(mob) != 0
                || distance(self.position, mob.position) > range
            {
                return Err(invalid("The target is dead or out of ability range."));
            }
            return Ok(self.target);
        }
        if ability.id == 125 {
            let contact = self
                .group_contacts
                .iter()
                .find(|contact| contact.id == self.target)
                .ok_or_else(|| invalid("Select a group member first."))?;
            if contact.sector != self.sector.id
                || distance(contact.position, self.position) > ability.range as f32
            {
                return Err(invalid("Group member is out of fold range."));
            }
            return Ok(contact.id);
        }
        if ability.kind == AbilityKind::Environment
            && self.group_contacts.iter().any(|contact| {
                contact.id == self.target
                    && contact.sector == self.sector.id
                    && distance(contact.position, self.position) <= ability.range as f32
            })
        {
            return Ok(self.target);
        }
        Ok(self.player_id())
    }

    pub(super) fn advance_abilities(&mut self, dt: f32) -> io::Result<()> {
        let expired: Vec<u32> = self
            .skills
            .temporary_effects
            .iter_mut()
            .filter_map(|(effect, seconds)| {
                *seconds -= dt;
                (*seconds <= 0.).then_some(*effect)
            })
            .collect();
        for effect in expired {
            self.skills.temporary_effects.remove(&effect);
            self.remove_effect(effect);
        }
        for seconds in self.skills.cooldowns.values_mut() {
            *seconds = (*seconds - dt).max(0.);
        }
        let combat_cloak_was_active = self.skills.combat_cloak_seconds > 0.;
        self.skills.combat_cloak_seconds = (self.skills.combat_cloak_seconds - dt).max(0.);
        if combat_cloak_was_active && self.skills.combat_cloak_seconds == 0. {
            self.pending.push(packets::ship_active_buff_slot(
                self.player_id(),
                skill_status::COMBAT_CLOAK_SLOT,
                "",
                0,
            ));
        }
        if self.skills.cloak_rank > 0 && self.skills.cloak_source.is_none() {
            let drain = match self.skills.cloak_rank {
                1 => 0.5,
                3 => 1.5,
                5 => 3.,
                6 => 6.,
                _ => 10.,
            };
            self.energy = (self.energy - drain * dt).max(0.);
            if self.energy == 0. || self.hull == 0. {
                self.stop_cloak();
            }
        }
        if self.skills.environment_rank > 0 {
            self.skills.environment_seconds -= dt;
            if self.skills.environment_seconds <= 0. {
                let previous = (
                    self.skills.environment_rank,
                    self.skills.environment_seconds,
                    self.skills.environment_effect,
                    self.skills.environment_skill_bonus,
                );
                self.skills.environment_rank = 0;
                self.skills.environment_skill_bonus = 0.;
                self.skills.environment_seconds = 0.;
                let effect = self.skills.environment_effect.take();
                if let Err(error) = self.checkpoint_session(true) {
                    self.skills.environment_rank = previous.0;
                    self.skills.environment_seconds = previous.1;
                    self.skills.environment_effect = previous.2;
                    self.skills.environment_skill_bonus = previous.3;
                    return Err(error);
                }
                if let Some(effect) = effect {
                    self.remove_effect(effect);
                }
                self.pending.push(packets::ship_active_buff_slot(
                    self.player_id(),
                    skill_status::ENVIRONMENT_SLOT,
                    "",
                    0,
                ));
            }
        }
        if let Some(mut cast) = self.skills.cast.take() {
            let definition = self.skill_catalog.ability(cast.ability)?;
            let self_jumpstart =
                definition.kind == AbilityKind::Jumpstart && cast.target == self.player_id();
            let invalid_jumpstart = definition.kind == AbilityKind::Jumpstart
                && (cast.jumpstart.as_ref().is_some_and(|plan| !plan.valid())
                    || cast.jumpstart.is_none() && self.jumpstart_definition().is_err()
                    || self
                        .jumpstart_target_in_range(
                            cast.target,
                            cast.jumpstart
                                .as_ref()
                                .map_or(definition.range as f32, |plan| plan.range),
                        )
                        .is_err()
                    || self.warp.active()
                    || self.gate_transit.is_some()
                    || !self.logged_in
                    || self.sector.station.is_some());
            let invalid_recharge = matches!(
                definition.kind,
                AbilityKind::RechargeShields
                    | AbilityKind::HullPatch
                    | AbilityKind::ReactorOptimization
                    | AbilityKind::ShieldCharging
            ) && (self.warp.active()
                || self.gate_transit.is_some()
                || self.sector.station.is_some()
                || self.mining.is_some());
            let invalid_psionic = matches!(
                definition.kind,
                AbilityKind::PsionicShield | AbilityKind::RepulsorField
            ) && (self.warp.active()
                || self.gate_transit.is_some()
                || self.sector.station.is_some());
            let invalid_gravity = definition.kind == AbilityKind::GravityLink
                && (self.warp.active()
                    || self.gate_transit.is_some()
                    || self.sector.station.is_some()
                    || !self.logged_in);
            let invalid_hacking = definition.kind == AbilityKind::Hacking
                && (self.warp.active()
                    || self.gate_transit.is_some()
                    || self.sector.station.is_some()
                    || !self.logged_in);
            let invalid_befriend = definition.kind == AbilityKind::Befriend
                && (self.warp.active()
                    || self.gate_transit.is_some()
                    || self.sector.station.is_some()
                    || !self.logged_in);
            let invalid_bio = definition.kind == AbilityKind::Biorepression
                && (self.warp.active()
                    || self.gate_transit.is_some()
                    || self.sector.station.is_some()
                    || !self.logged_in);
            let invalid_menace = definition.kind == AbilityKind::Menace
                && (self.warp.active()
                    || self.gate_transit.is_some()
                    || self.sector.station.is_some()
                    || !self.logged_in);
            let invalid_enrage = definition.kind == AbilityKind::Enrage
                && (self.warp.active()
                    || self.gate_transit.is_some()
                    || self.sector.station.is_some()
                    || !self.logged_in);
            let invalid_sap = definition.kind == AbilityKind::ShieldSap
                && (self.warp.active()
                    || self.gate_transit.is_some()
                    || self.sector.station.is_some()
                    || self.mining.is_some()
                    || !self.logged_in);
            let invalid_leech = definition.kind == AbilityKind::EnergyLeech
                && (self.warp.active()
                    || self.gate_transit.is_some()
                    || self.sector.station.is_some()
                    || self.mining.is_some()
                    || !self.logged_in);
            let invalid_inversion = definition.kind == AbilityKind::ShieldInversion
                && (!self.inversion_available() || self.mining.is_some());
            let invalid_blast = definition.kind == AbilityKind::SelfDestruct
                && (self.warp.active()
                    || self.gate_transit.is_some()
                    || self.sector.station.is_some()
                    || self.mining.is_some()
                    || !self.logged_in);
            let invalid_summon = definition.kind == AbilityKind::Summon
                && (self.warp.active()
                    || self.gate_transit.is_some()
                    || self.sector.station.is_some()
                    || !self.logged_in);
            let invalid_high_fold = definition.kind == AbilityKind::Fold
                && matches!(definition.id, 17 | 36)
                && (self.warp.active()
                    || self.gate_transit.is_some()
                    || self.sector.station.is_some()
                    || !self.logged_in);
            if (self.hull <= 0. && !self_jumpstart)
                || invalid_jumpstart
                || invalid_recharge
                || invalid_psionic
                || invalid_gravity
                || invalid_menace
                || invalid_hacking
                || invalid_befriend
                || invalid_bio
                || invalid_enrage
                || invalid_sap
                || invalid_leech
                || invalid_inversion
                || invalid_blast
                || invalid_summon
                || invalid_high_fold
                || definition.kind == AbilityKind::Cloak
                    && definition.rank < 5
                    && self.motion.speed.abs() > 0.1
            {
                if matches!(
                    definition.kind,
                    AbilityKind::GravityLink
                        | AbilityKind::ShieldSap
                        | AbilityKind::EnergyLeech
                        | AbilityKind::Menace
                        | AbilityKind::Hacking
                        | AbilityKind::Befriend
                        | AbilityKind::Biorepression
                        | AbilityKind::Enrage
                        | AbilityKind::PsionicShield
                        | AbilityKind::RepulsorField
                        | AbilityKind::ShieldInversion
                        | AbilityKind::SelfDestruct
                        | AbilityKind::Fold
                ) {
                    if let Err(error) = self.checkpoint_session(true) {
                        self.skills.cast = Some(cast);
                        return Err(error);
                    }
                }
                self.remove_effect(cast.effect);
                self.pending.push(packets::ability_progress(None, 0, 0));
                self.pending.push(packets::message("Ability interrupted."));
                return Ok(());
            }
            cast.remaining -= dt;
            if cast.remaining > 0. {
                self.skills.cast = Some(cast);
            } else {
                let ability = self.skill_catalog.ability(cast.ability)?;
                let cooldown = cast
                    .jumpstart
                    .as_ref()
                    .map_or(ability.cooldown, |plan| plan.cooldown_seconds);
                if ability.kind == AbilityKind::GravityLink {
                    let applied = match self.apply_gravity_link(&ability, &cast) {
                        Ok(applied) => applied,
                        Err(error) => {
                            self.skills.cast = Some(cast);
                            return Err(error);
                        }
                    };
                    let previous_cooldown = self.skills.cooldowns.insert(ability.skill, cooldown);
                    if let Err(error) = self.checkpoint_session(true) {
                        if let Some(previous) = previous_cooldown {
                            self.skills.cooldowns.insert(ability.skill, previous);
                        } else {
                            self.skills.cooldowns.remove(&ability.skill);
                        }
                        self.skills.cast = Some(cast);
                        return Err(error);
                    }
                    self.pending.push(packets::ability_progress(None, 0, 0));
                    self.pending.push(packets::skill_cooldown(
                        ability.skill,
                        self.tick_ms(),
                        (cooldown * 1000.) as u32,
                    ));
                    self.message(if applied {
                        "Gravity Link applied for 30 seconds."
                    } else {
                        "Gravity Link target was lost before completion."
                    });
                    return Ok(());
                }
                if ability.kind == AbilityKind::Hacking {
                    let outcome = match self.apply_hacking(&ability, &cast) {
                        Ok(outcome) => outcome,
                        Err(error) => {
                            self.skills.cast = Some(cast);
                            return Err(error);
                        }
                    };
                    let previous_cooldown = self.skills.cooldowns.insert(ability.skill, cooldown);
                    if let Err(error) = self.checkpoint_session(true) {
                        if let Some(previous) = previous_cooldown {
                            self.skills.cooldowns.insert(ability.skill, previous);
                        } else {
                            self.skills.cooldowns.remove(&ability.skill);
                        }
                        self.skills.cast = Some(cast);
                        return Err(error);
                    }
                    self.pending.push(packets::ability_progress(None, 0, 0));
                    self.pending.push(packets::skill_cooldown(
                        ability.skill,
                        self.tick_ms(),
                        (cooldown * 1000.) as u32,
                    ));
                    self.message(match outcome {
                        Some(result) if result.affected > 0 => "Hacking disabled hostile systems.",
                        Some(result) if result.resisted > 0 => "Hostile systems resisted Hacking.",
                        _ => "Hacking target was lost before completion.",
                    });
                    return Ok(());
                }
                if ability.kind == AbilityKind::Befriend {
                    let outcome = match self.apply_befriend(&ability, &cast) {
                        Ok(outcome) => outcome,
                        Err(error) => {
                            self.skills.cast = Some(cast);
                            return Err(error);
                        }
                    };
                    let previous_cooldown = self.skills.cooldowns.insert(ability.skill, cooldown);
                    if let Err(error) = self.checkpoint_session(true) {
                        if let Some(previous) = previous_cooldown {
                            self.skills.cooldowns.insert(ability.skill, previous);
                        } else {
                            self.skills.cooldowns.remove(&ability.skill);
                        }
                        self.skills.cast = Some(cast);
                        return Err(error);
                    }
                    self.pending.push(packets::ability_progress(None, 0, 0));
                    self.pending.push(packets::skill_cooldown(
                        ability.skill,
                        self.tick_ms(),
                        (cooldown * 1000.) as u32,
                    ));
                    self.message(match outcome {
                        Some(result) if result.affected > 0 => {
                            "Befriend changed the target's reaction."
                        }
                        Some(result) if result.resisted > 0 => "The MOB resisted Befriend.",
                        _ => "Befriend target was lost before completion.",
                    });
                    return Ok(());
                }
                if ability.kind == AbilityKind::Biorepression {
                    let outcome = match self.apply_biorepression(&ability, &cast) {
                        Ok(outcome) => outcome,
                        Err(error) => {
                            self.skills.cast = Some(cast);
                            return Err(error);
                        }
                    };
                    let previous_cooldown = self.skills.cooldowns.insert(ability.skill, cooldown);
                    if let Err(error) = self.checkpoint_session(true) {
                        if let Some(previous) = previous_cooldown {
                            self.skills.cooldowns.insert(ability.skill, previous);
                        } else {
                            self.skills.cooldowns.remove(&ability.skill);
                        }
                        self.skills.cast = Some(cast);
                        return Err(error);
                    }
                    self.pending.push(packets::ability_progress(None, 0, 0));
                    self.pending.push(packets::skill_cooldown(
                        ability.skill,
                        self.tick_ms(),
                        (cooldown * 1000.) as u32,
                    ));
                    self.message(match outcome {
                        Some(result) if result.affected > 0 => {
                            "Biorepression weakened organic weapons."
                        }
                        Some(result) if result.resisted > 0 => "The MOB resisted Biocessation.",
                        _ => "Biorepression target was lost before completion.",
                    });
                    return Ok(());
                }
                if ability.kind == AbilityKind::Menace {
                    let outcome = match self.apply_menace(&ability, &cast) {
                        Ok(outcome) => outcome,
                        Err(error) => {
                            self.skills.cast = Some(cast);
                            return Err(error);
                        }
                    };
                    let previous_cooldown = self.skills.cooldowns.insert(ability.skill, cooldown);
                    if let Err(error) = self.checkpoint_session(true) {
                        if let Some(previous) = previous_cooldown {
                            self.skills.cooldowns.insert(ability.skill, previous);
                        } else {
                            self.skills.cooldowns.remove(&ability.skill);
                        }
                        self.skills.cast = Some(cast);
                        return Err(error);
                    }
                    self.pending.push(packets::ability_progress(None, 0, 0));
                    self.pending.push(packets::skill_cooldown(
                        ability.skill,
                        self.tick_ms(),
                        (cooldown * 1000.) as u32,
                    ));
                    self.message(match outcome {
                        Some(result) if result.affected > 0 => "Menace frightened hostile ships.",
                        Some(result) if result.resisted > 0 => "Hostile ships resisted Menace.",
                        _ => "Menace target was lost before completion.",
                    });
                    return Ok(());
                }
                if ability.kind == AbilityKind::Enrage {
                    let applied = match self.apply_enrage(&ability, &cast) {
                        Ok(applied) => applied,
                        Err(error) => {
                            self.skills.cast = Some(cast);
                            return Err(error);
                        }
                    };
                    let previous_cooldown = self.skills.cooldowns.insert(ability.skill, cooldown);
                    if let Err(error) = self.checkpoint_session(true) {
                        if let Some(previous) = previous_cooldown {
                            self.skills.cooldowns.insert(ability.skill, previous);
                        } else {
                            self.skills.cooldowns.remove(&ability.skill);
                        }
                        self.skills.cast = Some(cast);
                        return Err(error);
                    }
                    self.pending.push(packets::ability_progress(None, 0, 0));
                    self.pending.push(packets::skill_cooldown(
                        ability.skill,
                        self.tick_ms(),
                        (cooldown * 1000.) as u32,
                    ));
                    self.message(if applied {
                        "Enrage drew hostile attention."
                    } else {
                        "Enrage target was lost or resisted."
                    });
                    return Ok(());
                }
                if ability.kind == AbilityKind::ShieldInversion {
                    let previous_cooldown = self.skills.cooldowns.insert(ability.skill, cooldown);
                    let started = match self.start_shield_inversion(&ability, &cast) {
                        Ok(started) => started,
                        Err(error) => {
                            if let Some(previous) = previous_cooldown {
                                self.skills.cooldowns.insert(ability.skill, previous);
                            } else {
                                self.skills.cooldowns.remove(&ability.skill);
                            }
                            self.skills.cast = Some(cast);
                            return Err(error);
                        }
                    };
                    if !started {
                        if let Err(error) = self.checkpoint_session(true) {
                            if let Some(previous) = previous_cooldown {
                                self.skills.cooldowns.insert(ability.skill, previous);
                            } else {
                                self.skills.cooldowns.remove(&ability.skill);
                            }
                            self.skills.cast = Some(cast);
                            return Err(error);
                        }
                        self.message("Shield Inversion target was lost before completion.");
                    }
                    self.pending.push(packets::ability_progress(None, 0, 0));
                    self.pending.push(packets::skill_cooldown(
                        ability.skill,
                        self.tick_ms(),
                        (cooldown * 1000.) as u32,
                    ));
                    return Ok(());
                }
                if ability.kind == AbilityKind::SelfDestruct {
                    let previous_cooldown = self.skills.cooldowns.insert(ability.skill, cooldown);
                    if let Err(error) = self.prepare_self_destruct(&ability, &cast) {
                        if let Some(previous) = previous_cooldown {
                            self.skills.cooldowns.insert(ability.skill, previous);
                        } else {
                            self.skills.cooldowns.remove(&ability.skill);
                        }
                        self.skills.cast = Some(cast);
                        return Err(error);
                    }
                    self.pending.push(packets::ability_progress(None, 0, 0));
                    self.pending.push(packets::skill_cooldown(
                        ability.skill,
                        self.tick_ms(),
                        (cooldown * 1000.) as u32,
                    ));
                    return Ok(());
                }
                if ability.kind == AbilityKind::EnergyLeech {
                    let previous_cooldown = self.skills.cooldowns.insert(ability.skill, cooldown);
                    if let Err(error) = self.prepare_energy_leech(&ability, &cast) {
                        if let Some(previous) = previous_cooldown {
                            self.skills.cooldowns.insert(ability.skill, previous);
                        } else {
                            self.skills.cooldowns.remove(&ability.skill);
                        }
                        self.skills.cast = Some(cast);
                        return Err(error);
                    }
                    self.pending.push(packets::ability_progress(None, 0, 0));
                    self.pending.push(packets::skill_cooldown(
                        ability.skill,
                        self.tick_ms(),
                        (cooldown * 1000.) as u32,
                    ));
                    return Ok(());
                }
                if ability.kind == AbilityKind::Fold && matches!(ability.id, 17 | 36) {
                    let previous_pending = self.skills.pending_high_fold.clone();
                    let pending_len = self.pending.len();
                    let applied = match self.settle_high_fold(&ability, &cast) {
                        Ok(applied) => applied,
                        Err(error) => {
                            self.skills.cast = Some(cast);
                            return Err(error);
                        }
                    };
                    let previous_cooldown = self.skills.cooldowns.insert(ability.skill, cooldown);
                    if let Err(error) = self.checkpoint_session(true) {
                        self.skills.pending_high_fold = previous_pending;
                        self.pending.truncate(pending_len);
                        if let Some(previous) = previous_cooldown {
                            self.skills.cooldowns.insert(ability.skill, previous);
                        } else {
                            self.skills.cooldowns.remove(&ability.skill);
                        }
                        self.skills.cast = Some(cast);
                        return Err(error);
                    }
                    self.remove_effect(cast.effect);
                    self.pending.push(packets::ability_progress(None, 0, 0));
                    self.pending.push(packets::skill_cooldown(
                        ability.skill,
                        self.tick_ms(),
                        (cooldown * 1000.) as u32,
                    ));
                    if !applied {
                        self.message("High Fold target was lost before completion.");
                    }
                    return Ok(());
                }
                if ability.kind == AbilityKind::Environment {
                    let previous_cooldown = self.skills.cooldowns.insert(ability.skill, cooldown);
                    if let Err(error) = self.apply_ability(&ability, &cast) {
                        if let Some(previous) = previous_cooldown {
                            self.skills.cooldowns.insert(ability.skill, previous);
                        } else {
                            self.skills.cooldowns.remove(&ability.skill);
                        }
                        self.skills.cast = Some(cast);
                        return Err(error);
                    }
                    self.remove_effect(cast.effect);
                    self.pending.push(packets::ability_progress(None, 0, 0));
                    self.pending.push(packets::skill_cooldown(
                        ability.skill,
                        self.tick_ms(),
                        (cooldown * 1000.) as u32,
                    ));
                    return Ok(());
                }
                self.remove_effect(cast.effect);
                self.pending.push(packets::ability_progress(None, 0, 0));
                if cooldown > 0. {
                    self.skills.cooldowns.insert(ability.skill, cooldown);
                } else {
                    self.skills.cooldowns.remove(&ability.skill);
                }
                self.pending.push(packets::skill_cooldown(
                    ability.skill,
                    self.tick_ms(),
                    (cooldown * 1000.) as u32,
                ));
                if let Err(error) = self.apply_ability(&ability, &cast) {
                    self.pending.push(packets::message(&error.to_string()));
                } else {
                    crate::server_log!(
                        "Ability applied: {}; {}",
                        self.account.character.name,
                        ability.name
                    );
                }
            }
        }
        Ok(())
    }

    fn apply_ability(&mut self, ability: &AbilityDefinition, cast: &Cast) -> io::Result<()> {
        let target = cast.target;
        let effect = cast.effect;
        let jumpstart = cast.jumpstart.as_ref();
        let recharge_amount = cast.recharge_amount;
        let hull_repair_amount = cast.hull_repair_amount;
        let sap_generation = cast.sap_generation;
        if ability.kind == AbilityKind::Environment {
            let strength = crate::environment_gear::Strength::bounded(
                self.equipment.ship.environment_skill_bonus,
                self.equipment.ship.environment_duration_bonus,
            );
            if target == self.player_id() {
                self.apply_environment(ability.rank, strength)?;
            }
            if ability.rank == 7 {
                self.support_actions.push(SupportAction::Environment {
                    target: 0,
                    rank: 7,
                    skill_bonus: strength.skill_bonus,
                    duration_bonus: strength.duration_bonus,
                });
            } else if target != self.player_id() {
                self.support_actions.push(SupportAction::Environment {
                    target,
                    rank: ability.rank,
                    skill_bonus: strength.skill_bonus,
                    duration_bonus: strength.duration_bonus,
                });
            }
            self.pending.push(packets::ship_resources(
                self.player_id(),
                &self.equipment.ship,
                Some(self.energy),
                None,
                None,
            ));
            return Ok(());
        }
        let shared = Arc::clone(&self.combat);
        let mut combat = lock_combat(&shared)?;
        match ability.kind {
            AbilityKind::Unsupported => return Err(invalid("Unsupported class ability.")),
            AbilityKind::Cloak => {
                self.skills.cloak_rank = ability.rank;
                self.skills.cloak_source = None;
                let stealth = crate::mob_targeting::cloak_stealth(
                    self.account.character.total_level(),
                    self.skill_catalog.level(12),
                );
                self.skills.cloak_stealth = Some(stealth);
                self.stop_weapons();
                let asset = match ability.rank {
                    1 => 180,
                    3 => 181,
                    5 => 182,
                    6 => 183,
                    _ => 184,
                };
                self.skills.cloak_effect =
                    Some(self.add_skill_effect(self.account.character.game_id, asset));
                self.pending
                    .push(packets::cloak(self.account.character.game_id, true));
                self.publish_cloak_status(false);
                if ability.rank >= 6 {
                    self.support_actions.push(SupportAction::Cloak {
                        enabled: true,
                        rank: ability.rank,
                        stealth,
                    });
                }
            }
            AbilityKind::Contemplation => {
                let mob = combat
                    .actors
                    .get_mut(target.saturating_sub(MOB_ID) as usize)
                    .ok_or_else(|| invalid("Target no longer exists."))?;
                if mob.state == MobState::Dead
                    || distance(mob.position, self.position) > ability.range as f32
                {
                    return Err(invalid("Target left contemplation range."));
                }
                mob.pacify(30.);
                self.add_skill_effect(target, 1179);
            }
            AbilityKind::Environment => unreachable!("Environment settles before the combat lock"),
            AbilityKind::Fold => {
                if ability.id == 126 {
                    self.fold_self(cast.fold_distance_bonus);
                } else if ability.id == 125 {
                    self.support_actions.push(SupportAction::Fold {
                        target,
                        distance_bonus: cast.fold_distance_bonus,
                    });
                } else {
                    let mob = combat
                        .actors
                        .get_mut(target.saturating_sub(MOB_ID) as usize)
                        .ok_or_else(|| invalid("Target no longer exists."))?;
                    if mob.state == MobState::Dead
                        || distance(mob.position, self.position) > ability.range as f32
                    {
                        return Err(invalid("Target left fold range."));
                    }
                    let length = distance(mob.position, self.position).max(1.);
                    for axis in 0..3 {
                        mob.position[axis] = (mob.position[axis]
                            + (mob.position[axis] - self.position[axis]) / length
                                * (5000. + cast.fold_distance_bonus))
                            .clamp(self.sector.bounds.min[axis], self.sector.bounds.max[axis]);
                    }
                    self.pending
                        .push(packets::movement(target, mob.position, self.tick_ms()));
                    self.add_skill_effect(target, 267);
                }
            }
            AbilityKind::Summon => {
                let tier = crate::summon::tier(ability.id, self.skill_catalog.level(56))
                    .ok_or_else(|| invalid("Summon rank is not trained."))?;
                match tier.target {
                    crate::summon::SummonTarget::Friend
                    | crate::summon::SummonTarget::Group
                    | crate::summon::SummonTarget::ReturnFriend => {
                        self.support_actions.push(SupportAction::Summon {
                            ability: ability.id,
                            trained_rank: self.skill_catalog.level(56),
                            skill_bonus: cast.summon_skill_bonus,
                            target,
                            origin_sector: self.sector.id,
                            origin_position: self.position,
                            origin_heading: self.motion.heading(),
                        });
                    }
                    crate::summon::SummonTarget::Enemy
                    | crate::summon::SummonTarget::EnemyFormation => {
                        let selected = target
                            .checked_sub(MOB_ID)
                            .ok_or_else(|| invalid("Summon enemy target disappeared."))?
                            as usize;
                        let anchor = combat
                            .actors
                            .get(selected)
                            .ok_or_else(|| invalid("Summon enemy target disappeared."))?;
                        if anchor.state == MobState::Dead
                            || cast.summon_generation != Some(anchor.death_generation)
                            || !self.mob_visible_in_scan(selected, anchor)
                            || self.mob_relationship(anchor) != 0
                            || anchor.range_to(self.position)
                                > crate::summon::effective_range(tier, cast.summon_skill_bonus)
                        {
                            return Err(invalid("Summon enemy left range."));
                        }
                        let center = anchor.position;
                        let formation = anchor.definition.spawn_group;
                        let heading = self.motion.heading();
                        let destination = crate::summon::arrival_position(
                            self.position,
                            heading,
                            self.sector.bounds.min,
                            self.sector.bounds.max,
                        );
                        let original = combat.actors.clone();
                        let mut moved = Vec::new();
                        for (index, mob) in combat.actors.iter_mut().enumerate() {
                            let selected_target = index == selected;
                            let same_formation = tier.target
                                == crate::summon::SummonTarget::EnemyFormation
                                && formation != 0
                                && mob.definition.spawn_group == formation
                                && distance(mob.position, center) <= 1500.;
                            if mob.state == MobState::Dead
                                || self.mob_relationship(mob) != 0
                                || !(selected_target || same_formation)
                            {
                                continue;
                            }
                            mob.position = destination;
                            mob.motion.stop();
                            mob.speed = 0.;
                            moved.push(MOB_ID + index as u32);
                        }
                        if let Err(error) = combat.persist() {
                            combat.actors = original;
                            return Err(error);
                        }
                        for victim in moved {
                            self.pending.push(packets::movement(
                                victim,
                                destination,
                                self.tick_ms(),
                            ));
                        }
                    }
                }
            }
            AbilityKind::Jumpstart => {
                // Missing plans exist only on pre-native in-flight checkpoints.
                let action = SupportAction::Jumpstart {
                    target,
                    repair_fraction: jumpstart.map_or(0.49, |plan| plan.repair_fraction),
                    range: jumpstart.map_or(ability.range as f32, |plan| plan.range),
                };
                if target == self.player_id() {
                    self.receive_support(self.player_id(), &action)?;
                } else {
                    self.support_actions.push(action);
                }
            }
            AbilityKind::Leech => {
                let center = combat
                    .actors
                    .get(target.saturating_sub(MOB_ID) as usize)
                    .ok_or_else(|| invalid("Target no longer exists."))?
                    .position;
                if distance(center, self.position) > ability.range as f32 {
                    return Err(invalid("Target left leech range."));
                }
                let mut total = 0.;
                for (index, mob) in combat.actors.iter_mut().enumerate() {
                    if MOB_ID + index as u32 == target
                        || ability.radius > 0
                            && distance(center, mob.position) <= ability.radius as f32
                    {
                        total += mob.drain_shield_by(
                            75. * ability.rank as f32,
                            self.account.character.game_id,
                        );
                        self.pending.push(packets::beam(
                            self.account.character.game_id,
                            MOB_ID + index as u32,
                            1047,
                            self.next_effect,
                            self.tick_offset
                                .wrapping_add(self.started.elapsed().as_millis() as u32),
                        ));
                        self.next_effect = self.next_effect.wrapping_add(1);
                    }
                }
                if ability.rank >= 3 {
                    self.energy =
                        (self.energy + total * 0.5).min(self.equipment.ship.energy_capacity);
                }
                if matches!(ability.id, 52 | 53) {
                    self.support_actions.push(SupportAction::Energy {
                        amount: total * 0.5,
                    });
                }
            }
            AbilityKind::EnergyLeech => {
                return Err(invalid(
                    "Energy Leech settles through its durable MOB drain.",
                ));
            }
            AbilityKind::Wormhole => {
                let destination = ability.destination.unwrap();
                self.support_actions.push(SupportAction::Wormhole {
                    destination,
                    origin_sector: self.sector.id,
                    origin: self.position,
                });
                self.transfer_sector(destination, false)?;
            }
            AbilityKind::RechargeShields => {
                let amount = recharge_amount
                    .ok_or_else(|| invalid("Recharge Shields cast has no frozen amount."))?;
                self.skills.pending_shield_recharge = Some(PendingShieldRecharge {
                    target,
                    effect,
                    amount,
                    splash_amount: match ability.rank {
                        6 => amount * 540. / 2880.,
                        7 => amount * 1800. / 5760.,
                        _ => 0.,
                    },
                    range: ability.range as f32,
                    radius: ability.radius as f32,
                });
            }
            AbilityKind::HullPatch => {
                let amount = hull_repair_amount
                    .ok_or_else(|| invalid("Hull repair cast has no frozen amount."))?;
                self.skills.pending_hull_repair = Some(PendingHullRepair {
                    target,
                    effect,
                    amount,
                    splash_amount: match ability.rank {
                        6 => amount * 540. / 2880.,
                        7 => amount * 1800. / 5760.,
                        _ => 0.,
                    },
                    range: ability.range as f32,
                    radius: ability.radius as f32,
                });
            }
            AbilityKind::Rally => {
                let tactic = crate::rally::tactic(ability.id)
                    .ok_or_else(|| invalid("Unknown Rally tactic."))?;
                self.skills.pending_rally = Some(crate::rally::PendingRally {
                    effect,
                    tactic,
                    skill_rank: self.skill_catalog.level(44),
                    skill_bonus: self.equipment.ship.rally_skill_bonus,
                });
            }
            AbilityKind::ReactorOptimization => {
                self.skills.pending_reactor_optimization =
                    Some(crate::reactor_optimization::PendingReactorOptimization {
                        target,
                        effect,
                        ability: ability.id,
                        trained_rank: self.skill_catalog.level(43),
                    });
            }
            AbilityKind::ShieldCharging => {
                self.skills.pending_shield_charging =
                    Some(crate::shield_charging::PendingShieldCharging {
                        target,
                        effect,
                        ability: ability.id,
                        trained_rank: self.skill_catalog.level(51),
                    });
            }
            AbilityKind::RepulsorField => {
                self.skills.pending_repulsor_field =
                    Some(crate::repulsor_field::PendingRepulsorField {
                        target,
                        effect,
                        ability: ability.id,
                        trained_rank: self.skill_catalog.level(48),
                        effective_rank: self.equipment.ship.repulsor_field_skill,
                    });
            }
            AbilityKind::PsionicShield => {
                self.skills.pending_psionic_shield =
                    Some(crate::psionic_shield::PendingPsionicShield {
                        target,
                        effect,
                        ability: ability.id,
                        trained_rank: self.skill_catalog.level(42),
                    });
            }
            AbilityKind::ShieldSap => {
                self.skills.pending_shield_sap = Some(crate::shield_sap::PendingShieldSap {
                    target,
                    target_generation: sap_generation
                        .ok_or_else(|| invalid("Shield Sap cast lost its MOB generation."))?,
                    effect,
                    ability: ability.id,
                    trained_rank: self.skill_catalog.level(54),
                });
            }
            AbilityKind::GravityLink => {
                return Err(invalid(
                    "Gravity Link settles through its durable MOB cast.",
                ));
            }
            AbilityKind::Hacking => {
                return Err(invalid("Hacking settles through its durable MOB cast."));
            }
            AbilityKind::Befriend => {
                return Err(invalid("Befriend settles through its durable MOB cast."));
            }
            AbilityKind::Biorepression => {
                return Err(invalid(
                    "Biorepression settles through its durable MOB cast.",
                ));
            }
            AbilityKind::Menace => {
                return Err(invalid("Menace settles through its durable MOB cast."));
            }
            AbilityKind::Enrage => {
                return Err(invalid("Enrage settles through its durable MOB cast."));
            }
            AbilityKind::ShieldInversion => {
                return Err(invalid(
                    "Shield Inversion settles through timed combat pulses.",
                ));
            }
            AbilityKind::SelfDestruct => {
                return Err(invalid("Self Destruct settles through its durable blast."));
            }
            AbilityKind::EquipmentRepair => {
                return Err(invalid("Repair Equipment has no charged cast."));
            }
            AbilityKind::PowerDown => {
                return Err(invalid("Power Down has no charged cast."));
            }
        }
        drop(combat);
        if matches!(
            ability.kind,
            AbilityKind::RechargeShields
                | AbilityKind::HullPatch
                | AbilityKind::Rally
                | AbilityKind::ReactorOptimization
                | AbilityKind::ShieldCharging
                | AbilityKind::PsionicShield
                | AbilityKind::RepulsorField
                | AbilityKind::ShieldSap
        ) {
            self.checkpoint_session(true)?;
        }
        self.pending.push(packets::ship_resources(
            self.account.character.game_id,
            &self.equipment.ship,
            Some(self.energy),
            None,
            None,
        ));
        Ok(())
    }

    /// Persist one trained Environment tier with the caster's equipped strength.
    ///
    /// Raises:
    ///     io::Error: Recipient status could not be checkpointed.
    pub(super) fn apply_environment(
        &mut self,
        rank: u32,
        strength: crate::environment_gear::Strength,
    ) -> io::Result<()> {
        let previous = (
            self.skills.environment_rank,
            self.skills.environment_seconds,
            self.skills.environment_effect,
            self.skills.environment_skill_bonus,
            self.next_effect,
        );
        let strength = crate::environment_gear::Strength::bounded(
            strength.skill_bonus,
            strength.duration_bonus,
        );
        let effect = self.next_effect;
        self.next_effect = self.next_effect.wrapping_add(1).max(1);
        self.skills.environment_rank = rank;
        self.skills.environment_seconds = 180. + strength.duration_bonus;
        self.skills.environment_skill_bonus = strength.skill_bonus;
        self.skills.environment_effect = Some(effect);
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.environment_rank = previous.0;
            self.skills.environment_seconds = previous.1;
            self.skills.environment_effect = previous.2;
            self.skills.environment_skill_bonus = previous.3;
            self.next_effect = previous.4;
            return Err(error);
        }
        if let Some(previous_effect) = previous.2 {
            self.remove_effect(previous_effect);
        }
        self.pending.push(packets::object_effect(
            self.player_id(),
            effect,
            216,
            self.tick_ms(),
        ));
        self.publish_environment_status(false);
        Ok(())
    }

    /// Returns:
    ///     (u32, f32, f32): Trained tier, frozen gear bonus, and remaining seconds.
    #[cfg(test)]
    pub(crate) fn environment_snapshot(&self) -> (u32, f32, f32) {
        (
            self.skills.environment_rank,
            self.skills.environment_skill_bonus,
            self.skills.environment_seconds,
        )
    }
    /// Move one ship a bounded native random Fold distance.
    pub(super) fn fold_self(&mut self, distance_bonus: f32) {
        let angle = rand::random::<f32>() * std::f32::consts::TAU;
        let distance = 5000.
            + if distance_bonus.is_finite() {
                distance_bonus.clamp(0., crate::fold_space_high::MAX_DISTANCE_BONUS)
            } else {
                0.
            };
        self.position[0] = (self.position[0] + angle.cos() * distance)
            .clamp(self.sector.bounds.min[0], self.sector.bounds.max[0]);
        self.position[1] = (self.position[1] + angle.sin() * distance)
            .clamp(self.sector.bounds.min[1], self.sector.bounds.max[1]);
        self.motion.stop();
        self.last_motion_report = None;
        self.dirty = true;
        self.add_skill_effect(self.player_id(), 267);
        self.pending.push(packets::player_movement(
            self.player_id(),
            self.position,
            self.tick_ms(),
            &self.motion,
        ));
    }
    pub(super) fn stop_cloak(&mut self) {
        let rank = self.skills.cloak_rank;
        let inherited = self.skills.cloak_source.is_some();
        self.skills.cloak_rank = 0;
        self.skills.cloak_source = None;
        self.skills.cloak_stealth = None;
        if let Some(effect) = self.skills.cloak_effect.take() {
            self.remove_effect(effect);
        }
        if rank > 0 {
            self.pending.push(packets::ship_active_buff_slot(
                self.player_id(),
                skill_status::CLOAK_SLOT,
                "",
                0,
            ));
        }
        if rank >= 5 {
            self.skills.combat_cloak_seconds = 5.;
            self.publish_combat_cloak_status();
        }
        self.pending.push(packets::cloak(self.player_id(), false));
        if rank >= 6 && !inherited {
            self.support_actions.push(SupportAction::Cloak {
                enabled: false,
                rank,
                stealth: 0,
            });
        }
    }
    pub(super) fn add_skill_effect(&mut self, target: u32, asset: u16) -> u32 {
        let effect = self.next_effect;
        self.next_effect = self.next_effect.wrapping_add(1);
        self.pending.push(packets::object_effect(
            target,
            effect,
            asset,
            self.tick_ms(),
        ));
        if matches!(asset, 267 | 1179) {
            self.skills.temporary_effects.insert(effect, 2.);
        }
        effect
    }
    fn remove_effect(&mut self, effect: u32) {
        self.pending
            .push(Frame::new(0xf, effect.to_le_bytes().to_vec()));
    }
}
