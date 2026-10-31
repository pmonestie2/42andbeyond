//! Per-recipient peer visibility and support effects. The coordinator supplies
//! authenticated snapshots and group membership; this module owns wire updates.
use super::*;
use crate::peers::PeerSnapshot;

impl PlayerSession {
    pub(crate) fn launch_from_station(&mut self) -> io::Result<()> {
        self.station_request(0, 1)
    }
    pub(crate) fn detach_formation(&mut self) {
        if self.formation_leader.take().is_some() {
            self.client_events.push(ClientEvent::Formation {
                action: 8,
                target: 0,
            });
            self.motion.stop();
        }
    }
    pub(crate) fn release_formation(&mut self) {
        if self.formation_leader.take().is_some() {
            if self.warp.active() {
                let event = self.warp.finish(FinishReason::Cancelled, &mut self.motion);
                self.emit_warp_event(event);
            } else {
                self.motion.stop();
            }
            self.warp.speed = self.equipment.ship.warp_speed;
            self.queue([packets::player_movement(
                self.player_id(),
                self.position,
                self.tick_ms(),
                &self.motion,
            )]);
        }
    }
    /// Returns:
    ///     f32: This ship's equipped warp speed before a group cap is applied.
    pub(crate) fn formation_warp_speed(&self) -> f32 {
        self.equipment.ship.warp_speed
    }
    /// Returns:
    ///     Warp: Frozen leader drive phase and route for formed followers.
    pub(crate) fn formation_warp(&self) -> Warp {
        self.warp.clone()
    }
    /// Cap the leader and formed members at their slowest working drive.
    pub(crate) fn set_formation_warp_speed(&mut self, speed: f32) {
        self.warp.speed = speed;
    }
    /// Mirror the leader's native warp lifecycle for this formed ship.
    pub(crate) fn sync_formation_warp(
        &mut self,
        leader: &PeerSnapshot,
        warp: &Warp,
    ) -> io::Result<()> {
        if self.warp.phase == WarpPhase::Idle && warp.active() {
            if self.energy <= 0.
                || self.equipment.ship.warp_speed <= 0.
                || self.skills.cast.is_some()
            {
                return Err(invalid("Formed ship cannot engage its warp drive."));
            }
            self.abort_all_mining("Formation warp started.")?;
            self.cancel_looting("Formation warp started.")?;
            self.stop_following();
            if self.skills.cloak_rank > 0 && self.skills.cloak_rank < 6 {
                self.stop_cloak();
            }
        }
        self.formation_leader = Some(leader.profile.game_id);
        let events = self.warp.follow_leader(warp, &mut self.motion);
        if !self.warp.active() {
            self.warp.speed = self.equipment.ship.warp_speed;
        }
        if !events.is_empty() {
            for event in events {
                self.emit_warp_event(event);
            }
            if let Err(error) = self.checkpoint_session(true) {
                crate::server_error!(
                    "Formation warp checkpoint retry for {}: {error}",
                    self.account.character.name
                );
            }
        }
        Ok(())
    }
    /// Returns:
    ///     bool: Whether this formed ship can remain with its current leader.
    pub(crate) fn can_follow_formation(&self, leader_warping: bool) -> bool {
        self.logged_in
            && self.sector.station.is_none()
            && self.hull > 0.
            && self.gate_transit.is_none()
            && !(leader_warping && self.warp.phase == WarpPhase::Recovering)
            && !(leader_warping && self.energy <= 0.)
            && !(leader_warping && self.equipment.ship.warp_speed <= 0.)
    }
    pub(crate) fn follow_formation(&mut self, leader: &PeerSnapshot, offset: [f32; 3]) {
        // The native client consumes a leader-relative offset (opcode 0x41).
        // The server also follows it, so combat and range checks use the same
        // position instead of trusting the proxy's independent prediction.
        self.stop_following();
        let changed = self.formation_leader != Some(leader.profile.game_id);
        self.formation_leader = Some(leader.profile.game_id);
        self.motion = leader.motion.clone();
        self.motion.max_speed = self.equipment.ship.impulse_speed;
        self.motion.acceleration = self.equipment.ship.acceleration;
        self.motion.turn_scale = self.equipment.ship.turn_rate / 45.;
        let rotated = self.motion.rotate(offset);
        for (axis, coordinate) in rotated.iter().enumerate() {
            self.position[axis] = (leader.position[axis] + coordinate)
                .clamp(self.sector.bounds.min[axis], self.sector.bounds.max[axis]);
        }
        self.dirty = true;
        let mut body = Vec::from(self.player_id().to_le_bytes());
        body.extend_from_slice(&leader.profile.game_id.to_le_bytes());
        for coordinate in offset {
            body.extend_from_slice(&coordinate.to_le_bytes());
        }
        if changed || self.last_formation_broadcast.elapsed() >= Duration::from_millis(200) {
            self.queue([Frame::new(0x41, body)]);
            self.last_formation_broadcast = Instant::now();
        }
    }
    pub(crate) fn can_form_up(&self) -> bool {
        self.logged_in
            && self.sector.station.is_none()
            && self.hull > 0.
            && !self.warp.active()
            && self.gate_transit.is_none()
    }
    /// Whether this ship can keep leading formed members, including during warp.
    pub(crate) fn can_lead_formation(&self) -> bool {
        self.logged_in
            && self.sector.station.is_none()
            && self.hull > 0.
            && self.gate_transit.is_none()
    }
    pub(crate) fn peer_snapshot(&self) -> Option<PeerSnapshot> {
        (self.logged_in && self.sector.station.is_none()).then(|| PeerSnapshot {
            stats: self.equipment.ship,
            profile: self.account.character.clone(),
            sector: self.sector.id,
            position: self.position,
            motion: self.motion.clone(),
            shield: self.shield,
            hull: self.hull,
            cloaked: self.skills.cloak_rank > 0,
            shield_charging: self.hull > 0.
                && self.shield_charging_buff.as_ref().is_some_and(|buff| {
                    buff.valid(crate::resources::unix_ms(), self.session_epoch)
                }),
        })
    }
    pub(crate) fn update_peer_view(&mut self, peers: &[PeerSnapshot], group: &[u32]) {
        self.group_contacts = peers
            .iter()
            .filter(|peer| {
                peer.profile.game_id != self.player_id() && group.contains(&peer.profile.game_id)
            })
            .map(|peer| GroupContact {
                id: peer.profile.game_id,
                sector: peer.sector,
                position: peer.position,
                incapacitated: peer.hull <= 0.,
            })
            .collect();
        self.peer_contacts = peers
            .iter()
            .filter(|peer| {
                peer.profile.game_id != self.player_id()
                    && peer.sector == self.sector.id
                    && distance(peer.position, self.position) <= 20_000.
                    && (!peer.cloaked || group.contains(&peer.profile.game_id))
            })
            .map(|peer| {
                (
                    peer.profile.game_id,
                    crate::peers::PeerContact {
                        position: peer.position,
                        incapacitated: peer.hull <= 0.,
                    },
                )
            })
            .collect();
        self.peer_ids = self.peer_contacts.keys().copied().collect();
        self.follow_peer = peers
            .iter()
            .find(|peer| {
                self.following
                    && peer.profile.game_id == self.target
                    && self.peer_ids.contains(&self.target)
            })
            .map(|peer| crate::following::FollowContact {
                position: peer.position,
                speed: peer.motion.speed,
                radius: crate::following::PLAYER_RADIUS,
            });
    }
    pub(crate) fn publish_peers(&mut self, peers: &[PeerSnapshot]) {
        if !self.logged_in {
            return;
        }
        let removed: Vec<u32> = self
            .visible_peers
            .difference(&self.peer_ids)
            .copied()
            .collect();
        for id in removed {
            self.peer_stats.remove(&id);
            self.peer_shield_charging.remove(&id);
            self.pending.push(Frame::new(7, id.to_le_bytes().to_vec()));
        }
        let broadcast = self.last_peer_broadcast.elapsed() >= Duration::from_millis(200);
        let refresh_login_peers = self
            .peer_login_refresh_at
            .is_some_and(|when| Instant::now() >= when);
        for peer in peers
            .iter()
            .filter(|peer| self.peer_ids.contains(&peer.profile.game_id))
        {
            let id = peer.profile.game_id;
            if !self.visible_peers.contains(&id) {
                self.pending
                    .extend(packets::peer_spawn(peer, self.tick_ms()));
                self.peer_stats.insert(id, peer.stats);
            } else if refresh_login_peers {
                // START acknowledgment can precede the native sector object's
                // readiness; repeat nearby actor creation once after login.
                self.pending
                    .extend(packets::peer_spawn(peer, self.tick_ms()));
                self.peer_stats.insert(id, peer.stats);
            } else if broadcast {
                self.pending.push(packets::player_movement(
                    id,
                    peer.position,
                    self.tick_ms(),
                    &peer.motion,
                ));
                self.pending.push(packets::ship_resources(
                    id,
                    &peer.stats,
                    None,
                    Some(peer.shield),
                    Some(peer.hull),
                ));
                self.pending.push(packets::cloak(id, peer.cloaked));
                self.pending
                    .push(packets::incapacitated(id, peer.hull <= 0.));
            }
            if self.peer_stats.get(&id) != Some(&peer.stats) {
                self.pending
                    .push(packets::public_ship_parameters(id, &peer.stats));
                self.peer_stats.insert(id, peer.stats);
            }
            if self.peer_shield_charging.get(&id) != Some(&peer.shield_charging) {
                self.pending
                    .push(packets::peer_shield_charging(id, peer.shield_charging));
                self.peer_shield_charging.insert(id, peer.shield_charging);
            }
        }
        self.visible_peers = self.peer_ids.clone();
        if refresh_login_peers {
            self.peer_login_refresh_at = None;
        }
        if broadcast {
            self.last_peer_broadcast = Instant::now();
        }
    }
    pub(crate) fn attach_combat(&mut self, shared: Arc<Mutex<SectorCombat>>) -> io::Result<()> {
        let count = lock_combat(&shared)?.actors.len();
        self.combat = shared;
        self.mob_states.resize(count, MobState::Idle);
        self.mob_visible.resize(count, false);
        self.mob_relationships.resize(count, None);
        Ok(())
    }
    pub(crate) fn register_peer(&mut self, peer: IpAddr) {
        self.registrations.insert(peer, Instant::now());
    }
    pub(crate) fn selected_from(&self, peer: IpAddr) -> bool {
        self.selected_peer == Some(peer)
    }
    pub(crate) fn connected(&self) -> bool {
        self.selected_peer.is_some()
    }
    pub(crate) fn cloak_parent(&self) -> Option<u32> {
        self.skills.cloak_source
    }
    pub(crate) fn cloak_stealth(&self) -> Option<u32> {
        (self.skills.cloak_rank > 0).then(|| {
            self.skills.cloak_stealth.unwrap_or_else(|| {
                crate::mob_targeting::cloak_stealth(
                    self.account.character.total_level(),
                    self.skill_catalog.level(12),
                )
            })
        })
    }
    pub(crate) fn inherit_cloak_stealth(&mut self, source: u32, stealth: u32) {
        if self.skills.cloak_source == Some(source) {
            self.skills.cloak_stealth = Some(stealth);
        }
    }
    pub(crate) fn message(&mut self, message: &str) {
        self.pending.push(packets::message(message));
    }
    pub(crate) fn queue(&mut self, frames: impl IntoIterator<Item = Frame>) {
        self.pending.extend(frames);
    }
    pub(crate) fn public_effects(&self) -> Vec<Frame> {
        self.pending
            .iter()
            .filter(|frame| matches!(frame.opcode, 9 | 0x0b | 0x0f))
            .cloned()
            .collect()
    }
    pub(crate) fn take_support(&mut self) -> Vec<SupportAction> {
        std::mem::take(&mut self.support_actions)
    }
    pub(crate) fn take_events(&mut self) -> Vec<ClientEvent> {
        std::mem::take(&mut self.client_events)
    }
    pub(crate) fn synchronize_clock(&mut self, tick: u32) {
        self.tick_offset = tick;
        self.started = Instant::now();
    }
    pub(crate) fn receive_support(
        &mut self,
        source: u32,
        action: &SupportAction,
    ) -> io::Result<()> {
        match *action {
            SupportAction::Environment {
                rank,
                skill_bonus,
                duration_bonus,
                ..
            } => self.apply_environment(
                rank,
                crate::environment_gear::Strength::bounded(skill_bonus, duration_bonus),
            )?,
            SupportAction::Energy { amount } => {
                self.energy = (self.energy + amount).min(self.equipment.ship.energy_capacity);
                self.pending.push(packets::ship_resources(
                    self.player_id(),
                    &self.equipment.ship,
                    Some(self.energy),
                    None,
                    None,
                ));
            }
            SupportAction::Jumpstart {
                repair_fraction, ..
            } => {
                if !repair_fraction.is_finite() || repair_fraction <= 0. || repair_fraction > 1. {
                    return Err(invalid("Invalid frozen Jumpstart repair fraction."));
                }
                if self.hull > 0. {
                    return Ok(());
                }
                self.hull = self.equipment.ship.hull_capacity * repair_fraction;
                self.shield = 0.;
                self.apply_equipment_parameters();
                self.pending
                    .push(packets::incapacitated(self.player_id(), false));
                self.pending.push(packets::ship_resources(
                    self.player_id(),
                    &self.equipment.ship,
                    None,
                    Some(self.shield),
                    Some(self.hull),
                ));
                self.message(if source == self.player_id() {
                    "Self-jumpstart restored your ship."
                } else {
                    "Another player jumpstarted your ship."
                });
            }
            SupportAction::Fold { distance_bonus, .. } => self.fold_self(distance_bonus),
            SupportAction::Summon { .. } => {
                return Err(invalid(
                    "Summon must be delivered by the world coordinator.",
                ));
            }
            SupportAction::Cloak {
                enabled: true,
                rank,
                stealth,
            } => {
                if self.skills.cloak_rank == 0 || self.skills.cloak_source.is_some() {
                    self.skills.cloak_rank = rank;
                    self.skills.cloak_source = Some(source);
                    self.skills.cloak_stealth = Some(stealth);
                    self.stop_weapons();
                    self.pending.push(packets::cloak(self.player_id(), true));
                    self.publish_cloak_status(false);
                }
            }
            SupportAction::Cloak { enabled: false, .. } => {
                if self.skills.cloak_source == Some(source) {
                    self.stop_cloak();
                }
            }
            SupportAction::Wormhole { destination, .. } => self.transfer_wormhole(destination)?,
        }
        Ok(())
    }
    /// Returns:
    ///     bool: Whether this pilot is currently visible as a space or station peer.
    pub(crate) fn has_visible_pilot(&self, pilot: u32) -> bool {
        self.peer_ids.contains(&pilot) || self.station_peers.contains_key(&pilot)
    }

    pub(crate) fn current_target(&self) -> u32 {
        self.target
    }
    pub(crate) fn group_target(&mut self, target: u32) -> io::Result<()> {
        self.handle_input(
            0x17,
            &[self.player_id().to_le_bytes(), target.to_le_bytes()].concat(),
        )
    }
}
