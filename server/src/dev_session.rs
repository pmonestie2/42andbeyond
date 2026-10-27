//! Session-side developer operations reuse normal scene transitions and keep
//! orientation/ship-state access inside the character that owns those fields.
use super::*;

impl PlayerSession {
    /// Returns:
    ///     ([f32; 3], f32, String): Authoritative local object center, radius, and display name.
    /// Raises:
    ///     io::Error: The target is absent or is a defeated MOB.
    pub(crate) fn developer_warp_target(&self, id: u32) -> io::Result<([f32; 3], f32, String)> {
        if let Some(nav) = self.sector.navpoints.iter().find(|nav| nav.id == id) {
            return Ok((nav.position, nav.radius, nav.name.clone()));
        }
        if let Some(resource) = self
            .sector
            .resources
            .iter()
            .find(|node| node.object.id == id)
        {
            return Ok((
                resource.object.position,
                resource.object.radius,
                resource.object.name.clone(),
            ));
        }
        if let Some(position) = self.visible_corpses.get(&id) {
            return Ok((*position, 150., "Wreck".into()));
        }
        let combat = lock_combat(&self.combat)?;
        if let Some(mob) = id
            .checked_sub(MOB_ID)
            .and_then(|index| combat.actors.get(index as usize))
            .filter(|mob| mob.state != MobState::Dead)
        {
            return Ok((
                mob.position,
                mob.definition.radius,
                mob.definition.name.clone(),
            ));
        }
        Err(invalid(
            "Select an object in this sector or use /d_warp OBJECT_ID.",
        ))
    }

    /// Instantly move to a safe point outside the target's hull, committing before publication.
    /// Raises:
    ///     io::Error: The ship/target is invalid, no safe arrival fits, or checkpoint storage fails.
    pub(crate) fn developer_warp_to(
        &mut self,
        target: u32,
        center: [f32; 3],
        radius: f32,
    ) -> io::Result<()> {
        if !self.logged_in
            || self.hull <= 0.
            || self.sector.station.is_some()
            || self.handoff.is_some()
            || self.transition_pending.is_some()
            || self.gate_transit.is_some()
            || self.docking_target.is_some()
            || target == self.player_id()
        {
            return Err(invalid(
                "Use /d_warp alive in space after docking and sector transitions finish.",
            ));
        }
        if !radius.is_finite() || radius < 0. || !self.sector.bounds.contains(center) {
            return Err(invalid("Developer warp target lies outside this sector."));
        }
        let offset = std::array::from_fn::<_, 3, _>(|axis| self.position[axis] - center[axis]);
        let length = distance(self.position, center);
        let primary = if length > 0.01 {
            offset.map(|value| value / length)
        } else {
            self.motion.heading().map(|value| -value)
        };
        let clearance = radius + crate::following::PLAYER_RADIUS + 300.;
        let candidates = [
            primary,
            [1., 0., 0.],
            [-1., 0., 0.],
            [0., 1., 0.],
            [0., -1., 0.],
            [0., 0., 1.],
            [0., 0., -1.],
        ];
        let destination = candidates
            .iter()
            .map(|direction| std::array::from_fn(|axis| center[axis] + direction[axis] * clearance))
            .find(|position| self.sector.bounds.contains(*position))
            .ok_or_else(|| invalid("No safe developer warp arrival fits around this target."))?;
        self.cancel_mining("Developer warp interrupted prospecting.")?;
        self.cancel_looting("Developer warp interrupted looting.")?;
        let previous = (
            self.position,
            self.motion.clone(),
            self.warp.clone(),
            self.following,
            self.follow_peer,
            self.last_motion_report,
            self.target,
            self.player_firing,
            self.weapon_firing,
            self.warp_effects.clone(),
            self.teleport_report_guard,
            self.dirty,
        );
        self.position = destination;
        self.warp.reset(&mut self.motion);
        self.motion
            .face(std::array::from_fn(|axis| center[axis] - destination[axis]))?;
        self.following = false;
        self.follow_peer = None;
        self.last_motion_report = None;
        self.teleport_report_guard = Some(Instant::now());
        self.target = target;
        self.player_firing = false;
        self.weapon_firing.fill(false);
        self.warp_effects.clear();
        self.dirty = true;
        if let Err(error) = self.checkpoint_session(true) {
            (
                self.position,
                self.motion,
                self.warp,
                self.following,
                self.follow_peer,
                self.last_motion_report,
                self.target,
                self.player_firing,
                self.weapon_firing,
                self.warp_effects,
                self.teleport_report_guard,
                self.dirty,
            ) = previous;
            return Err(error);
        }
        self.detach_formation();
        for effect in previous.9 {
            self.pending.push(packets::remove_effect(effect));
        }
        self.pending
            .push(Frame::new(0x9c, (-1i32).to_le_bytes().to_vec()));
        self.pending
            .push(packets::ship_target(self.player_id(), target));
        self.publish_weapon_states();
        self.emit_warp_event(WarpEvent::Ready);
        self.refresh_target_verbs(true);
        // The checkpoint owns the committed position; the ordinary player file catches up immediately.
        if let Err(error) = self.save() {
            crate::server_error!("Developer warp committed; player save pending: {error}");
            self.dirty = true;
        }
        Ok(())
    }

    /// Publish an instantly moved peer's final pose and idle warp state to current observers.
    pub(crate) fn developer_warp_peer_update(&mut self, peer: &crate::peers::PeerSnapshot) {
        if self.logged_in && self.visible_peers.contains(&peer.profile.game_id) {
            self.pending.push(packets::warp_state(
                peer.profile.game_id,
                WarpPhase::Idle,
                None,
                self.tick_ms(),
            ));
            self.pending.push(packets::player_movement(
                peer.profile.game_id,
                peer.position,
                self.tick_ms(),
                &peer.motion,
            ));
        }
    }

    /// Returns:
    ///     [f32; 3]: This authenticated pilot's current in-space position for local MOB tuning.
    /// Raises:
    ///     io::Error: The pilot is docked, warping, logged out, or changing sectors.
    pub(crate) fn developer_mob_level_origin(&self) -> io::Result<[f32; 3]> {
        if !self.logged_in
            || self.sector.station.is_some()
            || self.handoff.is_some()
            || self.transition_pending.is_some()
            || self.warp.active()
            || self.gate_transit.is_some()
        {
            return Err(invalid(
                "Use /d_mob_cl in space outside warp and sector transitions.",
            ));
        }
        Ok(self.position)
    }

    /// Publish rescaled resources and combat levels to this client's already-visible MOBs.
    pub(crate) fn developer_refresh_mobs(&mut self, indices: &[usize]) -> io::Result<()> {
        if !self.logged_in {
            return Ok(());
        }
        let combat = lock_combat(&self.combat)?;
        for index in indices {
            if self.mob_visible.get(*index).copied().unwrap_or(false) {
                if let Some(mob) = combat.actors.get(*index) {
                    self.pending
                        .push(packets::mob_status(MOB_ID + *index as u32, mob));
                }
            }
        }
        Ok(())
    }

    /// Returns:
    ///     usize: Number of active leases and skill statuses removed.
    pub(crate) fn developer_clear_buffs(&mut self) -> io::Result<usize> {
        let previous_devices = self.active_device.clone();
        let previous_additional = self.additional_active_devices.clone();
        let previous_peers = self.peer_buffs.clone();
        let previous_rally = self.rally_buff.clone();
        let previous_reactor = self.reactor_optimization_buff.clone();
        let previous_shield = self.shield_charging_buff.clone();
        let previous_psionic = self.psionic_shield_buff.clone();
        let previous_repulsor = self.repulsor_field_buff.clone();
        let previous_trance = self.combat_trance.clone();
        let previous_trance_suppressed = self.combat_trance_suppressed;
        let previous_skills = self.skills.clone();
        let previous_support = self.support_actions.clone();
        let previous_resources = (self.energy, self.shield, self.hull);
        let previous_pending = self.pending.len();
        let count = usize::from(previous_devices.is_some())
            + previous_additional.len()
            + previous_peers.len()
            + usize::from(previous_rally.is_some())
            + usize::from(previous_reactor.is_some())
            + usize::from(previous_shield.is_some())
            + usize::from(previous_psionic.is_some())
            + usize::from(previous_repulsor.is_some())
            + usize::from(previous_trance.active)
            + usize::from(previous_skills.cloak_rank > 0)
            + usize::from(previous_skills.environment_rank > 0)
            + usize::from(previous_skills.combat_cloak_seconds > 0.)
            + usize::from(previous_skills.power_down_ability.is_some())
            + usize::from(previous_skills.active_inversion.is_some())
            + usize::from(previous_skills.equipment_repair.is_some());
        let mut effects = Vec::new();
        for device in previous_devices.iter().chain(&previous_additional) {
            if device.visual_asset(&self.items).is_some() {
                effects.push(device.visual_id(self.player_id()));
            }
        }
        for buff in &previous_peers {
            effects.push(buff.visual_id(self.player_id()));
        }
        if let Some(buff) = &previous_rally {
            effects.push(buff.visual_id(self.player_id()));
        }
        if let Some(buff) = &previous_shield {
            effects.push(buff.visual_id(self.player_id()));
        }
        if let Some(buff) = &previous_psionic {
            effects.push(buff.effect);
        }
        for effect in [
            previous_skills.cloak_effect,
            previous_skills.environment_effect,
            previous_skills.cast.as_ref().map(|cast| cast.effect),
            previous_skills.active_inversion.map(|active| active.effect),
            previous_skills
                .equipment_repair
                .as_ref()
                .map(|active| active.effect),
        ]
        .into_iter()
        .flatten()
        {
            effects.push(effect);
        }
        effects.extend(previous_skills.temporary_effects.keys().copied());
        if previous_trance.visual_descriptor != 0 {
            effects.push(self.combat_trance_visual_id());
        }

        self.active_device = None;
        self.additional_active_devices.clear();
        self.peer_buffs.clear();
        self.rally_buff = None;
        self.reactor_optimization_buff = None;
        self.shield_charging_buff = None;
        self.psionic_shield_buff = None;
        self.repulsor_field_buff = None;
        self.combat_trance = Default::default();
        self.combat_trance_suppressed = true;
        self.skills.cast = None;
        self.skills.cloak_rank = 0;
        self.skills.cloak_effect = None;
        self.skills.cloak_source = None;
        self.skills.cloak_stealth = None;
        self.skills.environment_rank = 0;
        self.skills.environment_seconds = 0.;
        self.skills.environment_effect = None;
        self.skills.environment_skill_bonus = 0.;
        self.skills.combat_cloak_seconds = 0.;
        self.skills.temporary_effects.clear();
        self.skills.power_down_ability = None;
        self.skills.active_inversion = None;
        self.skills.equipment_repair = None;
        self.skills.pending_rally = None;
        self.skills.pending_reactor_optimization = None;
        self.skills.pending_shield_charging = None;
        self.skills.pending_psionic_shield = None;
        self.skills.pending_repulsor_field = None;
        self.support_actions.retain(|action| {
            !matches!(
                action,
                SupportAction::Cloak { .. } | SupportAction::Environment { .. }
            )
        });
        let result = self.refresh_equipment().and_then(|_| {
            self.save_logged_out_devices()?;
            self.checkpoint_session(true)
        });
        if let Err(error) = result {
            self.active_device = previous_devices;
            self.additional_active_devices = previous_additional;
            self.peer_buffs = previous_peers;
            self.rally_buff = previous_rally;
            self.reactor_optimization_buff = previous_reactor;
            self.shield_charging_buff = previous_shield;
            self.psionic_shield_buff = previous_psionic;
            self.repulsor_field_buff = previous_repulsor;
            self.combat_trance = previous_trance;
            self.combat_trance_suppressed = previous_trance_suppressed;
            self.skills = previous_skills;
            self.support_actions = previous_support;
            (self.energy, self.shield, self.hull) = previous_resources;
            let _ = self.refresh_equipment();
            self.pending.truncate(previous_pending);
            let _ = self.save_logged_out_devices();
            return Err(error);
        }
        if self.logged_in {
            for slot in 0..16 {
                self.pending.push(packets::ship_active_buff_slot(
                    self.player_id(),
                    slot,
                    "",
                    0,
                ));
            }
            self.pending.push(packets::active_device_snapshot(&[]));
            self.pending.push(packets::cloak(self.player_id(), false));
            self.pending.push(packets::ability_progress(None, 0, 0));
            effects.sort_unstable();
            effects.dedup();
            for effect in effects {
                self.pending.push(packets::remove_effect(effect));
            }
            self.message(&format!("Dev clear: {count} active effects removed."));
        }
        Ok(count)
    }

    pub(crate) fn developer_spawn_positions(&self, count: usize) -> io::Result<Vec<[f32; 3]>> {
        if !self.logged_in
            || self.sector.station.is_some()
            || self.handoff.is_some()
            || self.transition_pending.is_some()
            || self.warp.active()
            || self.gate_transit.is_some()
        {
            return Err(invalid(
                "Use /d_spawn in space outside warp and sector transitions.",
            ));
        }
        if !(1..=32).contains(&count) {
            return Err(invalid("Developer spawn count must be between 1 and 32."));
        }
        let mut positions = Vec::with_capacity(count);
        for index in 0..count {
            // A short row across the ship's local right axis keeps every actor
            // directly in front, with separated meshes for a multi-spawn.
            let lateral = (index as f32 - (count - 1) as f32 / 2.) * 150.;
            let offset = self.motion.rotate([1000., lateral, 0.]);
            let position = std::array::from_fn(|axis| self.position[axis] + offset[axis]);
            if !self.sector.bounds.contains(position) {
                return Err(invalid(
                    "Spawn formation would cross the sector boundary; move toward its center.",
                ));
            }
            positions.push(position);
        }
        Ok(positions)
    }

    pub(crate) fn developer_jump(&mut self, destination: u32) -> io::Result<()> {
        if !self.logged_in || self.handoff.is_some() || self.transition_pending.is_some() {
            return Err(invalid(
                "Enter the game and finish the current transition before /d_jump.",
            ));
        }
        // This is a dev transport, with no class/skill/energy/range requirement.
        // transfer_sector still protects pending weapon journals and production
        // escrow and commits the same recoverable handoff as a gate/wormhole.
        self.transfer_sector(destination, false)?;
        self.message(&format!(
            "Dev jump: {} ({}).",
            self.sector.name, self.sector.id
        ));
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    /// Instant travel cancels movement modes, saves its safe arrival, and ignores stale proxy reports.
    #[test]
    fn developer_warp_commits_safe_arrival_and_cancels_warp_follow_and_fire() {
        let mut ship = super::super::tests::journal_ship();
        let original = ship.position;
        let energy = ship.energy;
        let station = ship
            .sector
            .navpoints
            .iter_mut()
            .find(|nav| nav.object_type == 12)
            .unwrap();
        station.navigation_present = false;
        station.navigation_type = 0;
        let station_id = station.id;
        assert!(ship.warp_waypoint(station_id).is_ok());
        ship.warp.phase = WarpPhase::Traveling;
        ship.following = true;
        ship.player_firing = true;
        ship.weapon_firing[3] = true;
        ship.warp_effects.push(777);
        let center = [10_000., 0., 0.];
        ship.developer_warp_to(100002, center, 500.).unwrap();
        assert!((distance(ship.position, center) - 1045.).abs() < 0.1);
        assert_eq!(ship.warp.phase, WarpPhase::Idle);
        assert!(!ship.following && !ship.player_firing);
        assert!(ship.weapon_firing.iter().all(|firing| !firing));
        assert!(ship.warp_effects.is_empty());
        assert_eq!(ship.energy, energy);
        assert!(ship.pending.contains(&packets::remove_effect(777)));
        let arrival = ship.position;
        let stale = original.map(f32::to_le_bytes).concat();
        ship.apply_motion_report(&stale).unwrap();
        assert_eq!(ship.position, arrival);
        let mut stale_nearby = arrival;
        stale_nearby[0] += 100.;
        ship.apply_motion_report(&stale_nearby.map(f32::to_le_bytes).concat())
            .unwrap();
        assert_eq!(ship.position, arrival);
        let saved: serde_json::Value =
            serde_json::from_slice(&fs::read(ship.state_dir.join("player.json")).unwrap()).unwrap();
        assert_eq!(saved["position"], serde_json::json!(arrival));
        let restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(restored.position, arrival);
        assert_eq!(restored.warp.phase, WarpPhase::Idle);
        assert!(!restored.following);
    }

    /// A failed movement checkpoint cannot publish or retain the requested teleport.
    #[test]
    fn developer_warp_rejects_invalid_arrival_and_rolls_back_failed_checkpoint() {
        let mut ship = super::super::tests::journal_ship();
        let before = ship.position;
        assert!(ship
            .developer_warp_to(100002, [3_000_000., 0., 0.], 1.)
            .is_err());
        fs::create_dir_all(ship.state_dir.join("session.local.json.tmp")).unwrap();
        ship.warp.phase = WarpPhase::Traveling;
        ship.following = true;
        assert!(ship
            .developer_warp_to(100002, [10_000., 0., 0.], 500.)
            .is_err());
        assert_eq!(ship.position, before);
        assert_eq!(ship.warp.phase, WarpPhase::Traveling);
        assert!(ship.following);
        assert!(ship.pending.is_empty());
    }

    #[test]
    fn spawn_formation_follows_ship_orientation_and_rejects_bad_scene_or_boundaries() {
        let mut ship = super::super::tests::session();
        ship.logged_in = true;
        ship.position = [0.; 3];
        assert_eq!(
            ship.developer_spawn_positions(1).unwrap(),
            [[1000., 0., 0.]]
        );
        ship.motion.face([0., 1., 0.]).unwrap();
        let points = ship.developer_spawn_positions(3).unwrap();
        assert!((points[1][0]).abs() < 0.001);
        assert!((points[1][1] - 1000.).abs() < 0.001);
        assert!((distance(points[0], points[1]) - 150.).abs() < 0.001);
        assert!(ship.developer_spawn_positions(0).is_err());
        assert!(ship.developer_spawn_positions(33).is_err());
        ship.position = ship.sector.bounds.max;
        assert!(ship.developer_spawn_positions(1).is_err());
        ship.logged_in = false;
        assert!(ship.developer_spawn_positions(1).is_err());
    }

    #[test]
    fn developer_jump_uses_durable_handoff_without_energy_or_class_requirements() {
        let mut ship = super::super::tests::journal_ship();
        ship.energy = 0.;
        ship.hull = 0.;
        ship.developer_jump(3605).unwrap();
        assert_eq!(ship.sector.id, 3605);
        assert!(!ship.logged_in);
        assert!(ship.pending.iter().any(|frame| frame.opcode == 0x3a));
        let saved: serde_json::Value =
            serde_json::from_slice(&fs::read(ship.state_dir.join("player.json")).unwrap()).unwrap();
        assert_eq!(saved["sector"], 3605);
        assert_eq!(ship.energy, 0.);
        assert_eq!(ship.hull, 0.);
        assert!(ship.developer_jump(1076).is_err());
    }
}
