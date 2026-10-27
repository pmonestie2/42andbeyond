//! Per-player simulation and rendering phases. Multiplayer calls these around
//! one shared sector-combat tick, so adding clients cannot multiply MOB speed,
//! damage or respawn timers. The public advance helper is for standalone tests.
use super::*;
#[cfg(test)]
#[path = "session_sensor_tests.rs"]
mod sensor_tests;

impl PlayerSession {
    pub fn advance(&mut self, dt: f32) -> io::Result<()> {
        lock_resources(&self.resource_field)?.advance()?;
        self.advance_player(dt)?;
        if self.logged_in {
            lock_loot(&self.loot_world)?
                .capture_deaths(&*lock_combat(&self.combat)?, crate::resources::unix_ms())?;
            lock_combat(&self.combat)?.bind_sector(self.sector.id)?;
            let hits = lock_combat(&self.combat)?.advance(dt, &[self.threat()])?;
            for hit in hits {
                let reflection = self.receive_mob_hit(&hit)?;
                lock_combat(&self.combat)?.acknowledge_mob_hit(hit.sequence, reflection)?;
                self.forget_mob_hit_receipt(hit.sector, hit.sequence);
            }
            let launches = lock_combat(&self.combat)?.take_mob_launch_visuals();
            let visuals = lock_combat(&self.combat)?.take_projectile_visuals();
            for visual in &visuals {
                self.publish_projectile_visual(visual);
            }
            let reports = lock_combat(&self.combat)?.take_damage_reports();
            for report in &reports {
                self.publish_weapon_damage(report);
            }
            self.publish_mobs()?;
            for launch in &launches {
                self.publish_mob_launch(launch);
            }
            self.publish_resources()?;
            lock_loot(&self.loot_world)?
                .capture_deaths(&*lock_combat(&self.combat)?, crate::resources::unix_ms())?;
            let awards = lock_loot(&self.loot_world)?.pending_awards(self.player_id());
            for award in awards {
                self.settle_kill_award(&award)?;
                lock_loot(&self.loot_world)?.acknowledge_award(&award)?;
            }
            self.publish_corpses()?;
        }
        self.harvest_visuals.clear(); // standalone helper has no peer coordinator
        self.finish_tick()
    }

    pub(crate) fn advance_player(&mut self, dt: f32) -> io::Result<()> {
        if !dt.is_finite() || dt < 0. {
            return Err(invalid("invalid simulation duration"));
        }
        self.recover_weapon_launch()?;
        if self.selected_peer.is_some() && self.last_seen.elapsed() > Duration::from_secs(60) {
            self.logout()?;
        }
        if !self.logged_in {
            return Ok(());
        }
        self.advance_power_down()?;
        self.advance_active_device()?;
        self.advance_peer_buff()?;
        self.advance_rally_buff()?;
        self.advance_reactor_optimization_buff()?;
        self.advance_shield_charging_buff()?;
        self.advance_psionic_shield_buff()?;
        self.advance_repulsor_field_buff()?;
        self.advance_gating()?;
        if !self.logged_in {
            return Ok(());
        }
        self.advance_production(dt)?;
        for slot in 0..20 {
            let before = self.weapon_cooldowns[slot];
            self.weapon_cooldowns[slot] = (before - dt).max(0.);
            if before > 0. && self.weapon_cooldowns[slot] == 0. {
                self.publish_weapon_state(slot);
            }
        }
        if self.sector.station.is_some() {
            self.advance_combat_trance()?;
            self.advance_ship_resources(dt);
            return Ok(());
        }
        self.advance_abilities(dt)?;
        if self.self_destruct_action_locked() {
            self.motion.stop();
            if self.player_firing || self.weapon_firing.iter().any(|firing| *firing) {
                self.stop_weapons();
            }
        }
        if !self.logged_in {
            return Ok(());
        } // wormhole cast handed off
        let previous = self.position;
        let was_warping = self.warp.active();
        // A formed follower uses the leader's phase and route. The coordinator
        // mirrors it after the leader advances and sets our relative position.
        if self.formation_leader.is_none() {
            for event in self.warp.advance(
                &mut self.position,
                &mut self.motion,
                &self.sector.bounds,
                dt,
            )? {
                self.emit_warp_event(event);
            }
        }
        if self.warp.active() {
            self.warp_movement_elapsed += dt;
            while self.warp_movement_elapsed >= 0.1 {
                self.motion.movement_id = self.motion.movement_id.wrapping_add(1);
                self.warp_movement_elapsed -= 0.1;
            }
        } else {
            self.warp_movement_elapsed = 0.;
        }
        let followed = self.advance_following(dt)?;
        if !followed && !was_warping && self.hull > 0. && self.formation_leader.is_none() {
            self.motion
                .advance(&mut self.position, &self.sector.bounds, dt);
        }
        self.advance_combat_trance()?;
        self.advance_mission_arrivals()?;
        self.advance_exploration()?;
        self.dirty |= self.position != previous;
        self.advance_mining(dt)?;
        self.advance_looting(dt)?;
        self.advance_ship_resources(dt);
        self.advance_equipment_repair()?;
        self.fire_weapons()?;
        self.refresh_target_verbs(false);
        Ok(())
    }

    /// Commit native ARRIVE_AT stages only while stationary within 2,500 units.
    fn advance_mission_arrivals(&mut self) -> io::Result<()> {
        if self.warp.active() || self.motion.speed.abs() >= 1. {
            return Ok(());
        }
        let catalog = crate::missions::MissionCatalog::load()?;
        let source_id =
            self.inventory
                .missions
                .active
                .iter()
                .filter_map(|progress| {
                    let stage = catalog
                        .get(progress.id)
                        .ok()?
                        .stages
                        .get(progress.stage as usize)?;
                    let objective = stage.completion.first()?;
                    (stage.completion.len() == 1 && objective.kind == 15)
                        .then(|| u32::try_from(objective.data).ok())
                        .flatten()
                })
                .find(|source_id| {
                    self.sector.navpoints.iter().any(|nav| {
                        nav.source_id == *source_id && nav.range_to(self.position) < 2500.
                    })
                });
        let Some(source_id) = source_id else {
            return Ok(());
        };
        let mut candidate = self.inventory.clone();
        if let Some((mission, stage)) = candidate.missions.credit_arrival(source_id, &catalog)? {
            self.commit_inventory(candidate)?;
            self.pending.push(packets::message(&format!(
                "Mission {}: stage {stage}.",
                catalog.get(mission)?.name
            )));
        }
        Ok(())
    }

    pub(super) fn advance_ship_resources(&mut self, dt: f32) {
        let stats = self.equipment.ship;
        if self.warp.draining_energy() {
            self.energy = (self.energy - stats.warp_drain * dt).max(0.);
            if self.energy == 0. {
                let event = self.warp.finish(FinishReason::Cancelled, &mut self.motion);
                self.emit_warp_event(event);
                self.pending
                    .push(packets::message("Warp ended: reactor depleted."));
            }
        } else if !self.mining_drains_energy() {
            let recharge = self
                .skills
                .power_down_ability
                .and_then(|ability| crate::power_down::tier(ability, self.skill_catalog.level(37)))
                .map_or(stats.energy_recharge, |tier| {
                    crate::power_down::recharge_rate(
                        stats.energy_recharge,
                        stats.energy_recharge_base,
                        crate::power_down::equipped_recharge_fraction(
                            tier.reactor_recharge_fraction,
                            stats.power_down_skill_bonus,
                        ),
                    )
                });
            self.energy = (self.energy + recharge * dt).min(stats.energy_capacity);
        }
        if self.hull > 0. && crate::resources::unix_ms() >= self.shield_recharge_after_ms {
            let recharge = self
                .skills
                .power_down_ability
                .and_then(|ability| crate::power_down::tier(ability, self.skill_catalog.level(37)))
                .map_or(stats.shield_recharge, |tier| {
                    crate::power_down::recharge_rate(
                        stats.shield_recharge,
                        stats.shield_recharge_base,
                        crate::power_down::equipped_recharge_fraction(
                            tier.shield_recharge_fraction,
                            stats.power_down_skill_bonus,
                        ),
                    )
                });
            self.shield = (self.shield + recharge * dt).min(stats.shield_capacity);
        }
        self.resource_sync -= dt;
        if self.resource_sync <= 0. {
            self.pending.push(packets::ship_resources(
                self.player_id(),
                &self.equipment.ship,
                Some(self.energy),
                Some(self.shield),
                Some(self.hull),
            ));
            self.resource_sync = 1.;
        }
    }

    pub(crate) fn threat(&self) -> PlayerThreat {
        PlayerThreat {
            id: self.player_id(),
            session_epoch: self.session_epoch,
            position: self.position,
            visible: self.logged_in && self.hull > 0. && self.skills.power_down_ability.is_none(),
            signature: self.equipment.ship.signature,
            stealth: self.cloak_stealth(),
            factions: Arc::clone(&self.reputation),
        }
    }

    pub(super) fn mob_visible_in_scan(&self, index: usize, mob: &Mob) -> bool {
        mob.state != MobState::Dead
            && crate::mob_targeting::player_sees_mob(
                self.equipment.ship.scan_range,
                mob.range_to(self.position),
                self.mob_visible.get(index).copied().unwrap_or(false),
            )
    }
    pub(super) fn mob_target_in_scan(&self, target: u32) -> io::Result<bool> {
        let Some(index) = target.checked_sub(MOB_ID).map(|index| index as usize) else {
            return Ok(false);
        };
        let combat = lock_combat(&self.combat)?;
        Ok(combat
            .actors
            .get(index)
            .is_some_and(|mob| self.mob_visible_in_scan(index, mob)))
    }

    /// Test convenience for direct damage-rule checks; production always uses
    /// the frozen sector shot and its persisted acknowledgment protocol.
    #[cfg(test)]
    pub(crate) fn receive_hit(&mut self, attacker: usize, damage: f32) -> io::Result<()> {
        let combat = lock_combat(&self.combat)?;
        let mob = combat
            .actors
            .get(attacker)
            .ok_or_else(|| invalid("Unknown MOB attacker."))?;
        let hit = crate::npc_projectiles::MobHit {
            sector: self.sector.id,
            sequence: self
                .mob_damage_receipts
                .iter()
                .map(|receipt| receipt.sequence)
                .max()
                .unwrap_or(0)
                + 1,
            target: self.player_id(),
            target_epoch: self.session_epoch,
            attacker_index: attacker,
            attacker_source: mob.definition.source_id,
            attacker_template: mob.definition.template_id,
            attacker_generation: mob.death_generation,
            damage,
            damage_type: mob.combat.damage_type,
        };
        drop(combat);
        self.receive_mob_hit(&hit).map(|_| ())
    }

    pub(crate) fn publish_mobs(&mut self) -> io::Result<()> {
        if !self.logged_in || self.sector.station.is_some() {
            self.mob_menace_visuals.clear();
            self.mob_hacking_visuals.clear();
            self.mob_befriend_visuals.clear();
            self.mob_enrage_visuals.clear();
            return Ok(());
        }
        let broadcast = self.last_broadcast.elapsed() >= Duration::from_millis(200);
        let tick = self.tick_ms();
        let shared = Arc::clone(&self.combat);
        let combat = lock_combat(&shared)?;
        self.mob_states.resize(combat.actors.len(), MobState::Idle);
        self.mob_visible.resize(combat.actors.len(), false);
        self.mob_relationships.resize(combat.actors.len(), None);
        let refresh_login_mobs = self
            .mob_login_refresh_at
            .is_some_and(|when| Instant::now() >= when);
        for (index, mob) in combat.actors.iter().enumerate() {
            if mob.state != self.mob_states[index] {
                if mob.state == MobState::Dead && self.mob_visible[index] {
                    self.pending
                        .push(packets::message("MOB defeated; waiting for respawn."));
                }
                self.mob_states[index] = mob.state;
            }
            let visible = self.mob_visible_in_scan(index, mob);
            let reaction = self.mob_relationship(mob);
            if visible && !self.mob_visible[index] {
                self.mob_relationships[index] = Some(reaction);
                self.pending.extend(packets::mob_spawn(
                    MOB_ID + index as u32,
                    mob,
                    tick,
                    self.mob_relationship(mob),
                ));
            } else if visible && refresh_login_mobs {
                // The native client can acknowledge START before its sector
                // object manager is ready for the first MOB CREATE burst.
                self.pending.extend(packets::mob_spawn(
                    MOB_ID + index as u32,
                    mob,
                    tick,
                    reaction,
                ));
            } else if !visible && self.mob_visible[index] {
                self.mob_relationships[index] = None;
                self.pending.push(Frame::new(
                    7,
                    (MOB_ID + index as u32).to_le_bytes().to_vec(),
                ));
                if self.target == MOB_ID + index as u32 {
                    self.target = u32::MAX;
                    self.stop_weapons();
                    self.stop_following();
                    self.pending
                        .push(packets::ship_target(self.player_id(), u32::MAX));
                }
            }
            let object_id = MOB_ID + index as u32;
            let visual = visible
                .then(|| mob.active_menace())
                .flatten()
                .and_then(|fear| {
                    crate::menace::tier(fear.ability, fear.trained_rank)
                        .and_then(|tier| tier.verified_effect.map(|asset| (fear.effect, asset)))
                });
            if self.mob_menace_visuals.get(&object_id).copied() != visual.map(|(effect, _)| effect)
            {
                if let Some(previous) = self.mob_menace_visuals.remove(&object_id) {
                    self.pending.push(packets::remove_effect(previous));
                }
                if let Some((effect, asset)) = visual {
                    self.mob_menace_visuals.insert(object_id, effect);
                    self.pending
                        .push(packets::object_effect(object_id, effect, asset, tick));
                }
            }
            let hacking_visual = visible
                .then(|| mob.active_hack_visual())
                .flatten()
                .map(|hack| hack.visual_id(object_id));
            if self.mob_hacking_visuals.get(&object_id).copied() != hacking_visual {
                if let Some(previous) = self.mob_hacking_visuals.remove(&object_id) {
                    self.pending.push(packets::remove_effect(previous));
                }
                if let Some(effect) = hacking_visual {
                    self.mob_hacking_visuals.insert(object_id, effect);
                    self.pending.push(packets::object_effect(
                        object_id,
                        effect,
                        crate::hacking::VISUAL_DESCRIPTOR,
                        tick,
                    ));
                }
            }
            let befriend_visual = visible
                .then(|| mob.active_befriend_visual())
                .flatten()
                .map(|effect| effect.visual_id(object_id));
            if self.mob_befriend_visuals.get(&object_id).copied() != befriend_visual {
                if let Some(previous) = self.mob_befriend_visuals.remove(&object_id) {
                    self.pending.push(packets::remove_effect(previous));
                }
                if let Some(effect) = befriend_visual {
                    self.mob_befriend_visuals.insert(object_id, effect);
                    self.pending.push(packets::object_effect(
                        object_id,
                        effect,
                        crate::befriend::VISUAL_DESCRIPTOR,
                        tick,
                    ));
                }
            }
            let enrage_visual = visible
                .then(|| mob.active_enrage())
                .flatten()
                .map(|effect| effect.visual_id(object_id));
            if self.mob_enrage_visuals.get(&object_id).copied() != enrage_visual {
                if let Some(previous) = self.mob_enrage_visuals.remove(&object_id) {
                    self.pending.push(packets::remove_effect(previous));
                }
                if let Some(effect) = enrage_visual {
                    self.mob_enrage_visuals.insert(object_id, effect);
                    self.pending.push(packets::object_effect(
                        object_id,
                        effect,
                        crate::enrage::VERIFIED_EFFECT,
                        tick,
                    ));
                }
            }
            self.mob_visible[index] = visible;
            if broadcast && visible {
                if self.mob_relationships[index] != Some(reaction) {
                    self.pending
                        .push(packets::relationship(MOB_ID + index as u32, reaction));
                    self.mob_relationships[index] = Some(reaction);
                }
                self.pending
                    .push(packets::mob_movement(MOB_ID + index as u32, mob, tick));
                self.pending
                    .push(packets::mob_status(MOB_ID + index as u32, mob));
            }
        }
        if refresh_login_mobs {
            self.mob_login_refresh_at = None;
        }
        let proxy_predicts = !self.warp.active()
            && self
                .last_motion_report
                .is_some_and(|at| at.elapsed() < Duration::from_millis(500));
        if self.warp.active() {
            if self.last_warp_broadcast.elapsed() >= Duration::from_millis(500) {
                self.pending.push(packets::warp_movement(
                    self.player_id(),
                    self.position,
                    tick,
                    &self.motion,
                ));
                self.last_warp_broadcast = Instant::now();
            }
        } else if broadcast && !proxy_predicts {
            self.pending.push(packets::player_movement(
                self.player_id(),
                self.position,
                tick,
                &self.motion,
            ));
        }
        if broadcast {
            self.last_broadcast = Instant::now();
        }
        Ok(())
    }

    pub(crate) fn finish_tick(&mut self) -> io::Result<()> {
        if self.transition_pending.is_some() {
            if self.transition_retry.elapsed() < Duration::from_millis(250) {
                return Ok(());
            }
            // The journal is already committed. Hold this character's output,
            // not the shared sector loop, while mirror storage is unavailable.
            if self.finish_transition().is_err() {
                return Ok(());
            }
        }
        if self.dirty && self.last_saved.elapsed() > Duration::from_secs(2) {
            self.save()?;
        }
        self.flush()?;
        self.retry_handoff_output()?;
        self.checkpoint_session(false)
    }
}

#[cfg(test)]
mod damage_tests {
    use super::*;

    fn ship(kind: usize) -> PlayerSession {
        let mut ship = super::super::tests::session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22343".parse().unwrap());
        ship.shield = 20.;
        ship.hull = 100.;
        lock_combat(&ship.combat).unwrap().actors[0]
            .combat
            .damage_type = kind;
        ship
    }
    #[test]
    fn incoming_chemical_and_emp_use_real_layer_rules_and_native_damage_feedback() {
        let mut chemical = ship(5);
        chemical.receive_hit(0, 30.).unwrap();
        assert_eq!(chemical.shield, 0.);
        assert_eq!(chemical.hull, 88.);
        let feedback = chemical
            .pending
            .iter()
            .find(|frame| frame.opcode == 0x64)
            .unwrap();
        assert_eq!(feedback.body.len(), 24);
        assert_eq!(
            f32::from_le_bytes(feedback.body[..4].try_into().unwrap()),
            30.
        );
        assert_eq!(
            u32::from_le_bytes(feedback.body[8..12].try_into().unwrap()),
            5
        );
        let mut emp = ship(4);
        emp.receive_hit(0, 100.).unwrap();
        assert_eq!(emp.shield, 0.);
        assert_eq!(emp.hull, 100.);
        let mut plasma = ship(2);
        plasma.receive_hit(0, 19.).unwrap();
        assert_eq!(plasma.shield, 0.);
        assert_eq!(plasma.hull, 100.);
    }
    #[test]
    fn damage_control_is_owned_by_te_and_pw_and_reduces_only_hull_spill() {
        let mut warrior = ship(3);
        warrior.account.character.race = 2;
        warrior.account.character.profession = 0;
        warrior.skill_catalog = SkillCatalog::for_profile(&warrior.account.character).unwrap();
        warrior.receive_hit(0, 60.).unwrap();
        assert_eq!(warrior.shield, 0.);
        assert_eq!(warrior.hull, 80.);
        let mut explorer = ship(3);
        explorer.receive_hit(0, 60.).unwrap();
        assert_eq!(explorer.hull, 60.);
    }
    #[test]
    fn invalid_incoming_damage_is_rejected_before_state_or_feedback_changes() {
        let mut ship = ship(3);
        let before = ship.pending.len();
        assert!(ship.receive_hit(0, f32::NAN).is_err());
        assert!(ship.receive_hit(9999, 10.).is_err());
        assert_eq!(ship.shield, 20.);
        assert_eq!(ship.hull, 100.);
        assert_eq!(ship.pending.len(), before);
    }
    #[test]
    fn emp_hull_spill_cancels_warp_without_incapacitating_the_ship() {
        let mut ship = ship(4);
        ship.warp
            .start(VecDeque::new(), ship.position, &mut ship.motion)
            .unwrap();
        assert!(ship.warp.active());
        ship.receive_hit(0, 30.).unwrap();
        assert!(!ship.warp.active());
        assert_eq!(ship.hull, 100.);
    }
}
