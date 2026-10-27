//! Gate and wormhole handoff share the original JoinData packet. A transition
//! replaces sector content only after a valid destination has been resolved.
use super::*;

impl PlayerSession {
    pub(super) fn begin_docking(&mut self, target: u32) -> io::Result<()> {
        let station = self
            .sector
            .navpoints
            .iter()
            .find(|nav| nav.id == target && nav.object_type == 12)
            .ok_or_else(|| invalid("Select a station exterior first."))?;
        if !self.logged_in
            || self.target != target
            || self.hull <= 0.
            || station.range_to(self.position) > 5000.
            || self.warp.active()
        {
            return Err(invalid(
                "Dock alive, outside warp, within 5000m of the selected station.",
            ));
        }
        let destination = station
            .gate_destination
            .ok_or_else(|| invalid("Station has no interior destination."))?;
        if self.sectors.sector(destination)?.station.is_none() {
            return Err(invalid("Station interior is unavailable."));
        }
        self.detach_formation();
        self.stop_following();
        self.motion.stop();
        self.docking_target = Some(target);
        let mut camera = Vec::from(5u32.to_be_bytes());
        camera.extend_from_slice(&target.to_be_bytes());
        self.pending.push(Frame::new(0x92, camera));
        self.pending
            .push(packets::message("Docking clearance granted."));
        Ok(())
    }
    pub(super) fn complete_docking(&mut self, target: u32) -> io::Result<()> {
        if self.docking_target != Some(target) || self.hull <= 0. {
            return Err(invalid("Docking was not authorized for this station."));
        }
        let destination = self
            .sector
            .navpoints
            .iter()
            .find(|nav| nav.id == target)
            .and_then(|nav| nav.gate_destination)
            .ok_or_else(|| invalid("Station destination disappeared."))?;
        self.enter_station(destination)
    }

    /// Docking/towing restore the ship only if station entry commits. Before
    /// the journal rename a rejected transfer must retain its original health.
    pub(super) fn enter_station(&mut self, destination: u32) -> io::Result<()> {
        if self.sectors.sector(destination)?.station.is_none() {
            return Err(invalid("Destination is not a station."));
        }
        let before = (
            self.home_station,
            self.skills.clone(),
            self.hull,
            self.shield,
            self.energy,
        );
        self.home_station = destination;
        self.skills = SkillState::default();
        self.hull = self.equipment.ship.hull_capacity;
        self.shield = self.equipment.ship.shield_capacity;
        self.energy = self.equipment.ship.energy_capacity;
        let result = self.transfer_sector(destination, false);
        if result.is_err() {
            (
                self.home_station,
                self.skills,
                self.hull,
                self.shield,
                self.energy,
            ) = before;
        }
        result
    }
    pub(super) fn station_request(&mut self, station_id: u32, action: u8) -> io::Result<()> {
        let station = self
            .sector
            .station
            .clone()
            .ok_or_else(|| invalid("Enter a station first."))?;
        if !self.logged_in {
            return Err(invalid("Station session has not started."));
        }
        match action {
            4 => self.open_npc(station_id),
            1 => {
                if station_id != 0 && station_id != station.id {
                    return Err(invalid("Station launch identity mismatch."));
                }
                self.transfer_sector(station.parent, false)?;
                Ok(())
            }
            _ => {
                self.pending.push(packets::message(
                    "This station service is under development.",
                ));
                Ok(())
            }
        }
    }

    /// Queue native station departure and lab cleanup before a committed handoff, including recovery replay.
    pub(super) fn queue_sector_handoff(&mut self, join: Vec<u8>) {
        self.last_handoff_replay = Instant::now();
        let source = join
            .get(24..28)
            .map(|bytes| u32::from_be_bytes(bytes.try_into().unwrap()));
        if let Some(station) = source
            .and_then(|id| self.sectors.sector(id).ok())
            .and_then(|sector| sector.station)
        {
            self.pending.push(packets::starbase_set(station.id, 1));
            // The native client retains its manufacture object until explicitly
            // reset, even though the authoritative workbench has been cleared.
            self.pending
                .push(Frame::new(0x7f, 0u32.to_be_bytes().to_vec()));
        }
        self.pending.push(Frame::new(0x3a, join));
    }

    /// Retry the bounded unacknowledged departure window without allocating new packet sequences.
    /// Raises:
    ///     io::Error: A previously validated replay datagram cannot be encoded.
    pub(super) fn retry_handoff_output(&mut self) -> io::Result<()> {
        if self.handoff.is_none()
            || self.transition_pending.is_some()
            || self.replay.is_empty()
            || self.last_handoff_replay.elapsed() < Duration::from_secs(1)
        {
            return Ok(());
        }
        let Some(peer) = self.endpoint else {
            return Ok(());
        };
        self.last_handoff_replay = Instant::now();
        // A missing final redirect cannot trigger the proxy's gap detector:
        // simulation stops after departure, so no later sequence arrives. An
        // earlier missing packet also blocks that redirect in the ordered proxy.
        // Replay the existing capped cache; never invent a new sequence or pose.
        for packet in &self.replay {
            self.outgoing.push(Outbound {
                port: SECTOR_PORT,
                peer,
                data: packet.encode()?,
            });
        }
        Ok(())
    }
    pub(super) fn transfer_sector(
        &mut self,
        destination: u32,
        use_return_gate: bool,
    ) -> io::Result<()> {
        self.recover_weapon_launch()?;
        if self.transition_pending.is_some() {
            return Err(invalid(
                "A committed sector transition is waiting for storage.",
            ));
        }
        if self.inventory.refine_job.is_some()
            || self.inventory.craft_job.is_some()
            || self.inventory.production_target.iter().any(Option::is_some)
        {
            return Err(invalid(
                "Finish or cancel production and return the analyzer sample to cargo before leaving.",
            ));
        }
        let previous = self.sector.id;
        // The small loop-gate fixture deliberately re-enters its own content.
        let next = if destination == previous {
            self.sector.clone()
        } else {
            self.sectors.sector(destination)?
        };
        let mut resources = ResourceField::load(
            &next,
            &self.items,
            self.world_root.join(format!("resources/{}.json", next.id)),
        )?;
        resources.register_receipts(self.player_id(), &self.inventory.harvests)?;
        self.abort_all_mining("Sector transition.")?;
        self.cancel_looting("Sector transition.")?;
        let mut loot = LootWorld::load(
            self.world_root.join(format!("loot/{}.json", next.id)),
            next.id,
            &self.items,
        )?;
        loot.register_receipts(self.player_id(), &self.inventory.loot_receipts)?;
        let position = if destination == previous {
            self.position
        } else if use_return_gate {
            next.navpoints
                .iter()
                .find(|nav| nav.object_type == 11 && nav.gate_destination == Some(previous))
                .map(|nav| {
                    std::array::from_fn(|axis| {
                        let toward_center = -nav.position[axis];
                        (nav.position[axis] + toward_center.signum() * 2000.)
                            .clamp(next.bounds.min[axis], next.bounds.max[axis])
                    })
                })
                .unwrap_or([0.; 3])
        } else if let Some(station) = self
            .sector
            .station
            .as_ref()
            .filter(|station| station.parent == destination)
        {
            let exterior = next
                .navpoints
                .iter()
                .find(|nav| nav.id == station.exterior)
                .ok_or_else(|| invalid("Station launch exterior is missing."))?;
            std::array::from_fn(|axis| {
                (exterior.position[axis] + if axis == 0 { 4500. } else { 0. })
                    .clamp(next.bounds.min[axis], next.bounds.max[axis])
            })
        } else {
            [0.; 3]
        };
        let mut join =
            packets::character_ticket(next.id, &self.nonce, self.player_id()).body[4..].to_vec();
        // MasterJoin.FromSectorID is required for a real cross-sector native
        // transition. A loop-gate test cannot detect a missing source sector.
        join[24..28].copy_from_slice(&previous.to_be_bytes());
        for name in [
            &self.sector.name,
            &self.sector.system,
            &next.name,
            &next.system,
        ] {
            crate::protocol::string(&mut join, name);
        }
        // The journal commit precedes every location/transport mutation below.
        let transition = self.prepare_transition(&next, position, &join)?;
        self.sector = next;
        self.workbench = None;
        self.workbench_lab_ready = false;
        self.loot_world = Arc::new(Mutex::new(loot));
        self.visible_corpses.clear();
        self.loot_window = None;
        self.loot_revision = None;
        self.last_loot_publish = None;
        self.resource_field = Arc::new(Mutex::new(resources));
        self.resource_window = None;
        self.resource_revision = None;
        self.npc_interaction = None;
        self.position = position;
        self.stop_following();
        self.motion.stop();
        self.warp.reset(&mut self.motion);
        self.warp_effects.clear();
        self.last_motion_report = None;
        self.target = u32::MAX;
        self.stop_weapons();
        self.combat = Arc::new(Mutex::new(SectorCombat::new(&self.sector.mobs)));
        self.mob_states = vec![MobState::Idle; self.sector.mobs.len()];
        self.mob_visible = vec![false; self.sector.mobs.len()];
        self.mob_relationships = vec![None; self.sector.mobs.len()];
        self.mob_menace_visuals.clear();
        self.mob_hacking_visuals.clear();
        self.mob_befriend_visuals.clear();
        self.mob_enrage_visuals.clear();
        self.logged_in = false;
        self.gate_transit = None;
        self.visible_resources.clear();
        self.last_resource_visibility = None;
        self.docking_target = None;
        self.station_pose = None;
        self.pending.clear();
        self.station_peers.clear();
        self.station_view_dirty = true;
        self.handoff = Some(join.clone());
        self.transition_pending = Some(transition);
        if let Err(error) = self.finish_transition() {
            crate::server_log!("Sector transition committed; waiting for storage: {}; sector={destination}; reason={error}",self.account.character.name);
        }
        crate::server_log!(
            "Sector handoff: {}; {previous} -> {destination}",
            self.account.character.name
        );
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Lost final redirects and predecessor gaps recover with identical sequences, then stop at destination handshake.
    #[test]
    fn handoff_replays_unacked_window_at_a_bounded_rate_until_destination_handshake() {
        let mut ship = super::super::tests::journal_ship();
        ship.sequence = 41;
        for sequence in [39, 40] {
            ship.replay.push_back(Datagram {
                opcode: 0x2016,
                player: ship.player_id(),
                sequence,
                body: packets::message("departure window").encode().unwrap(),
            });
        }
        ship.transfer_sector(1071, true).unwrap();
        ship.finish_tick().unwrap();
        let original = ship.take_outgoing();
        assert!(original
            .iter()
            .any(|output| Datagram::decode(&output.data).unwrap().sequence == 41));
        let expected: Vec<_> = ship
            .replay
            .iter()
            .map(|packet| packet.encode().unwrap())
            .collect();
        let next_sequence = ship.sequence;
        ship.finish_tick().unwrap();
        assert!(ship.take_outgoing().is_empty());
        ship.last_handoff_replay = Instant::now() - Duration::from_secs(2);
        ship.finish_tick().unwrap();
        let retried = ship.take_outgoing();
        assert_eq!(
            retried
                .iter()
                .map(|output| output.data.clone())
                .collect::<Vec<_>>(),
            expected
        );
        assert_eq!(ship.sequence, next_sequence);
        assert_eq!(retried.len(), 3);
        assert!(retried
            .iter()
            .all(|output| output.peer == ship.endpoint.unwrap() && output.port == SECTOR_PORT));
        // Acknowledged predecessors leave the retry window; the final redirect remains repairable.
        ship.handle_input(0x2021, &41u32.to_le_bytes()).unwrap();
        ship.last_handoff_replay = Instant::now() - Duration::from_secs(2);
        ship.finish_tick().unwrap();
        let final_only = ship.take_outgoing();
        assert_eq!(final_only.len(), 1);
        assert_eq!(Datagram::decode(&final_only[0].data).unwrap().sequence, 41);
        let endpoint = "127.0.0.1:22344".parse().unwrap();
        ship.handle_udp(
            3808,
            endpoint,
            Datagram {
                opcode: 0x2008,
                player: ship.player_id(),
                sequence: 0,
                body: 1071u32.to_le_bytes().to_vec(),
            },
        )
        .unwrap();
        ship.take_outgoing();
        assert!(ship.replay.is_empty());
        ship.last_handoff_replay = Instant::now() - Duration::from_secs(2);
        ship.finish_tick().unwrap();
        assert!(ship.take_outgoing().is_empty());
    }

    /// A native departure clears its manufacture binding before redirect, including a recovered handoff.
    #[test]
    fn station_launch_closes_native_lab_before_handoff_and_replays_cleanup() {
        let mut ship = super::super::tests::journal_ship();
        ship.sector = ship.sectors.sector(10151).unwrap();
        ship.logged_in = true;
        ship.initialise_workbench_lab(true).unwrap();
        ship.workbench_input(
            0x79,
            &[ship.player_id().to_be_bytes(), 1u32.to_be_bytes()].concat(),
        )
        .unwrap();
        ship.pending.clear();
        ship.station_request(10151, 1).unwrap();
        assert_eq!(ship.sector.id, 1015);
        assert!(ship.workbench.is_none() && !ship.workbench_lab_ready);
        assert_eq!(
            ship.pending
                .iter()
                .map(|frame| frame.opcode)
                .collect::<Vec<_>>(),
            vec![0x4f, 0x7f, 0x3a]
        );
        assert_eq!(ship.pending[0], packets::starbase_set(10151, 1));
        assert_eq!(ship.pending[1].body, vec![0; 4]);
        assert_eq!(&ship.pending[2].body[24..28], &10151u32.to_be_bytes());
        let mut resumed = PlayerSession::new(
            ship.sectors.sector(1076).unwrap(),
            ship.bind,
            ship.state_dir.clone(),
        )
        .unwrap();
        assert_eq!(resumed.sector.id, 1015);
        assert_eq!(
            resumed
                .pending
                .iter()
                .filter(|frame| matches!(frame.opcode, 0x4f | 0x7f | 0x3a))
                .map(|frame| frame.opcode)
                .collect::<Vec<_>>(),
            vec![0x4f, 0x7f, 0x3a]
        );
        resumed.finish_tick().unwrap();
        resumed.take_outgoing();
        let cached: Vec<_> = resumed
            .replay
            .iter()
            .map(|packet| packet.encode().unwrap())
            .collect();
        resumed.last_handoff_replay = Instant::now() - Duration::from_secs(2);
        resumed.finish_tick().unwrap();
        assert_eq!(
            resumed
                .take_outgoing()
                .into_iter()
                .map(|output| output.data)
                .collect::<Vec<_>>(),
            cached
        );
    }
}
