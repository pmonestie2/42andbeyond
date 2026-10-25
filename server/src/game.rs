//! Shared server coordinator. Sessions own authentication, movement and wire
//! sequence state; sectors own one combat simulation. All mutation runs under
//! the server's existing mutex, and socket I/O remains outside that mutex.
use crate::accounts::AccountDirectory;
use crate::content::Sector;
use crate::energy_ledger::EnergyLedger;
use crate::events::ClientEvent;
use crate::groups::GroupManager;
use crate::loot_world::{lock_loot, KillCredit, LootWorld};
use crate::packets;
use crate::peers::PeerSnapshot;
use crate::player_session::{Outbound, PlayerSession};
use crate::protocol::{invalid, Datagram, Frame, Reader};
use crate::resources::{lock_resources, ResourceField};
use crate::sector_combat::{lock_combat, MobLaunchVisual, SectorCombat, MOB_ID};
use crate::skills::SupportAction;
use std::collections::{BTreeMap, HashMap, HashSet};
use std::io;
use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

#[path = "game_actions.rs"]
mod actions;
#[path = "dev_commands.rs"]
mod dev_commands;
#[cfg(test)]
#[path = "game_tests.rs"]
mod tests;

pub struct GameState {
    sessions: BTreeMap<u32, PlayerSession>,
    combats: HashMap<u32, Arc<Mutex<SectorCombat>>>,
    resource_fields: HashMap<u32, Arc<Mutex<ResourceField>>>,
    loot_worlds: HashMap<u32, Arc<Mutex<LootWorld>>>,
    groups: GroupManager,
    energy_ledger: EnergyLedger,
    jumpstart_offers: HashMap<u32, JumpstartOffer>,
    registrations: HashMap<IpAddr, Instant>,
    outgoing: Vec<Outbound>,
    root: PathBuf,
    bind: Ipv4Addr,
    initial_sector: Sector,
    connected: HashSet<u32>,
    groups_dirty: bool,
    last_deferred_error: Option<Instant>,
    player_sectors: HashMap<u32, u32>,
}

struct JumpstartOffer {
    source: u32,
    source_epoch: u64,
    target_epoch: u64,
    expires: Instant,
}

impl GameState {
    pub fn new(sector: Sector, bind: Ipv4Addr, root: PathBuf) -> io::Result<Self> {
        let mut state = Self {
            sessions: BTreeMap::new(),
            combats: HashMap::new(),
            resource_fields: HashMap::new(),
            loot_worlds: HashMap::new(),
            groups: GroupManager::default(),
            energy_ledger: EnergyLedger::load(root.join("energy_transfers.local.json"))?,
            jumpstart_offers: HashMap::new(),
            registrations: HashMap::new(),
            outgoing: Vec::new(),
            root,
            bind,
            initial_sector: sector,
            connected: HashSet::new(),
            groups_dirty: true,
            last_deferred_error: None,
            player_sectors: HashMap::new(),
        };
        state.load_accounts()?;
        let clock = state
            .sessions
            .values()
            .filter(|session| session.connected())
            .map(PlayerSession::tick_ms)
            .max()
            .unwrap_or(0);
        for session in state.sessions.values_mut() {
            session.synchronize_clock(clock);
        }
        state.connected = state
            .sessions
            .iter()
            .filter(|(_, session)| session.connected())
            .map(|(id, _)| *id)
            .collect();
        let path = state.root.join("groups.local.json");
        if path.exists() {
            state.groups = serde_json::from_slice(&std::fs::read(path)?)?;
        }
        state.groups.restore_index(&state.connected)?;
        // Prefer a resumed simulation over a fresh offline account's content.
        let mut ids: Vec<u32> = state.sessions.keys().copied().collect();
        ids.sort_by_key(|id| !state.sessions[id].logged_in);
        for id in ids {
            state.attach_session(id)?;
        }
        state.load_saved_loot_worlds()?;
        for world in state.loot_worlds.values() {
            lock_loot(world)?.release_missing_killers(&state.connected)?;
        }
        // A restart can occur before a departing player's cleanup tick. Clear
        // those account identities from each loaded sector's saved hate table.
        for (&sector, combat) in &state.combats {
            let mut world = lock_combat(combat)?;
            for (id, session) in &state.sessions {
                if !session.logged_in || session.sector.id != sector {
                    for mob in &mut world.actors {
                        mob.remove_player_hate(*id);
                    }
                }
            }
        }
        state.player_sectors = state.active_sector_membership();
        Ok(state)
    }

    fn load_accounts(&mut self) -> io::Result<()> {
        for stored in AccountDirectory::new(&self.root).load()? {
            let id = stored.account.character.game_id;
            if let Some(session) = self.sessions.get(&id) {
                if session.account.username != stored.account.username {
                    return Err(invalid(
                        "Account identity changed; restart with matching account files.",
                    ));
                }
                continue;
            }
            if self.sessions.len() >= 32 {
                return Err(invalid("Local server supports up to 32 accounts."));
            }
            let mut session = PlayerSession::with_account(
                self.initial_sector.clone(),
                self.bind,
                stored.directory,
                stored.account,
                self.root.clone(),
            )?;
            if session.energy_transfer_revision() > self.energy_ledger.revision() {
                return Err(invalid(
                    "Player transfer revision exceeds the durable ledger.",
                ));
            }
            if session.shield_recharge_revision() > self.energy_ledger.revision()
                || session.shield_recharge_revision() > 0
                    && self.energy_ledger.shield_balance(id).is_none()
            {
                return Err(invalid(
                    "Player shield revision exceeds the durable ledger.",
                ));
            }
            if session.hull_repair_revision() > self.energy_ledger.revision()
                || session.hull_repair_revision() > 0
                    && self.energy_ledger.hull_balance(id).is_none()
            {
                return Err(invalid("Player hull revision exceeds the durable ledger."));
            }
            if session.peer_buff_revision() > self.energy_ledger.revision()
                || session.peer_buff_revision() > 0 && self.energy_ledger.peer_buffs(id).is_empty()
            {
                return Err(invalid(
                    "Player targeted buff revision exceeds the durable ledger.",
                ));
            }
            if session.rally_revision() > self.energy_ledger.revision()
                || session.rally_revision() > 0 && self.energy_ledger.rally_buff(id).is_none()
            {
                return Err(invalid("Player Rally revision exceeds the durable ledger."));
            }
            if let Some(balance) = self.energy_ledger.balance(id) {
                session.apply_energy_balance(balance)?;
            }
            if let Some(balance) = self.energy_ledger.shield_balance(id) {
                session.apply_shield_balance(balance)?;
            }
            if let Some(balance) = self.energy_ledger.hull_balance(id) {
                session.apply_hull_balance(balance)?;
            }
            let clock = self
                .sessions
                .values()
                .map(PlayerSession::tick_ms)
                .max()
                .unwrap_or(0);
            if !session.connected() {
                session.synchronize_clock(clock);
            }
            for buff in self.energy_ledger.peer_buffs(id) {
                session.apply_peer_buff(buff.clone())?;
            }
            if let Some(buff) = self.energy_ledger.rally_buff(id) {
                session.apply_rally_buff(buff.clone())?;
            }
            for (&peer, at) in &self.registrations {
                if at.elapsed() < Duration::from_secs(600) {
                    session.register_peer(peer);
                }
            }
            for field in self.resource_fields.values() {
                session.register_resource_receipts(&mut *lock_resources(field)?)?;
            }
            for world in self.loot_worlds.values() {
                session.register_loot_receipts(&mut *lock_loot(world)?)?;
            }
            self.sessions.insert(id, session);
        }
        Ok(())
    }

    /// Load saved sectors with pending kills even when no pilot is there now.
    fn load_saved_loot_worlds(&mut self) -> io::Result<()> {
        let directory = self.root.join("loot");
        if !directory.is_dir() {
            return Ok(());
        }
        let items = crate::items::ItemCatalog::load()?;
        for entry in std::fs::read_dir(directory)? {
            let entry = entry?;
            let path = entry.path();
            if path.extension().and_then(|value| value.to_str()) != Some("json") {
                continue;
            }
            let Some(sector) = path
                .file_stem()
                .and_then(|value| value.to_str())
                .and_then(|value| value.parse::<u32>().ok())
                .filter(|sector| (1..10_000).contains(sector))
            else {
                continue;
            };
            if self.loot_worlds.contains_key(&sector) {
                continue;
            }
            let mut world = LootWorld::load(path, sector, &items)?;
            for session in self.sessions.values() {
                session.register_loot_receipts(&mut world)?;
            }
            self.loot_worlds.insert(sector, Arc::new(Mutex::new(world)));
        }
        Ok(())
    }
    fn attach_session(&mut self, id: u32) -> io::Result<()> {
        let sector = self
            .sessions
            .get(&id)
            .ok_or_else(|| invalid("Unknown player."))?
            .sector
            .id;
        if !self.resource_fields.contains_key(&sector) {
            let items = crate::items::ItemCatalog::load()?;
            let mut field = ResourceField::load(
                &self.sessions[&id].sector,
                &items,
                self.root.join(format!("resources/{sector}.json")),
            )?;
            for session in self.sessions.values() {
                session.register_resource_receipts(&mut field)?;
            }
            self.resource_fields
                .insert(sector, Arc::new(Mutex::new(field)));
        }
        let resources = Arc::clone(&self.resource_fields[&sector]);
        if sector < 10000 && !self.loot_worlds.contains_key(&sector) {
            let mut world = LootWorld::load(
                self.root.join(format!("loot/{sector}.json")),
                sector,
                &crate::items::ItemCatalog::load()?,
            )?;
            for session in self.sessions.values() {
                session.register_loot_receipts(&mut world)?;
            }
            self.loot_worlds.insert(sector, Arc::new(Mutex::new(world)));
        }
        let loot = self.loot_worlds.get(&sector).map(Arc::clone);
        let session = self
            .sessions
            .get_mut(&id)
            .ok_or_else(|| invalid("Unknown player."))?;
        let shared = if let Some(shared) = self.combats.get(&sector) {
            Arc::clone(shared)
        } else {
            let initial = lock_combat(&session.combat)?;
            let world = SectorCombat::persistent(
                self.root.join(format!("combat/{sector}.json")),
                sector,
                &initial,
            )?;
            let shared = Arc::new(Mutex::new(world));
            self.combats.insert(sector, Arc::clone(&shared));
            shared
        };
        session.attach_resource_field(resources)?;
        let changed = !Arc::ptr_eq(&session.combat, &shared);
        session.attach_combat(shared)?;
        if let Err(error) = if changed {
            session.recover_weapon_launch()
        } else {
            Ok(())
        } {
            crate::server_error!(
                "Weapon journal recovery deferred: {}; {error}",
                session.account.character.name
            );
        }
        if let Some(loot) = loot {
            session.attach_loot_world(loot)?;
        }
        Ok(())
    }
    pub fn account_count(&self) -> usize {
        self.sessions.len()
    }
    pub fn has_player(&self, id: u32) -> bool {
        self.sessions.contains_key(&id)
    }
    pub fn selected_from(&self, id: u32, peer: IpAddr) -> bool {
        self.sessions
            .get(&id)
            .is_some_and(|session| session.selected_from(peer))
    }
    pub fn issue_ticket(
        &mut self,
        username: &str,
        password: &str,
        peer: IpAddr,
    ) -> io::Result<String> {
        if !self
            .sessions
            .values()
            .any(|session| session.account.username == username)
        {
            self.load_accounts()?;
        }
        self.sessions
            .values_mut()
            .find(|session| session.account.username == username)
            .ok_or_else(|| invalid("invalid local sandbox credentials"))?
            .issue_ticket(username, password, peer)
    }
    pub fn consume_ticket(&mut self, ticket: &str, peer: IpAddr) -> Option<u32> {
        self.sessions
            .iter_mut()
            .find_map(|(id, session)| session.consume_ticket(ticket, peer).then_some(*id))
    }
    pub fn authentication_account(
        &mut self,
        username: &str,
    ) -> io::Result<crate::account::Account> {
        if !self
            .sessions
            .values()
            .any(|session| session.account.username == username)
        {
            self.load_accounts()?;
        }
        self.sessions
            .values()
            .find(|session| session.account.username == username)
            .map(|session| session.account.clone())
            .ok_or_else(|| invalid("invalid local sandbox credentials"))
    }
    pub fn issue_verified_ticket(&mut self, id: u32, peer: IpAddr) -> io::Result<String> {
        self.sessions
            .get_mut(&id)
            .ok_or_else(|| invalid("Unknown account identity."))?
            .issue_verified_ticket(peer)
    }
    pub fn characters(&self, id: u32) -> io::Result<Frame> {
        let session = self
            .sessions
            .get(&id)
            .ok_or_else(|| invalid("Unknown account character."))?;
        Ok(packets::characters(
            &session.sector,
            &session.account.character,
            &session.inventory,
        ))
    }
    pub fn select(
        &mut self,
        id: u32,
        slot: u32,
        peer: IpAddr,
        via_proxy: bool,
    ) -> io::Result<Frame> {
        self.sessions
            .get_mut(&id)
            .ok_or_else(|| invalid("Unknown account character."))?
            .select(slot, peer, via_proxy)
    }
    pub fn handle_input(&mut self, id: u32, opcode: u16, body: &[u8]) -> io::Result<()> {
        self.sessions
            .get_mut(&id)
            .ok_or_else(|| invalid("Unknown player."))?
            .handle_input(opcode, body)
    }
    pub fn handle_udp(&mut self, port: u16, peer: SocketAddr, packet: Datagram) -> io::Result<()> {
        if port == 3806 && packet.opcode == 0x1002 {
            if packet.player != 0 || packet.sequence != 0 || !packet.body.is_empty() {
                return Err(invalid(
                    "Demo roster request must be empty and unauthenticated.",
                ));
            }
            let codes = crate::demo_accounts::published_codes(&self.root)?;
            let mut body = vec![1, codes.len() as u8];
            for code in codes {
                body.extend_from_slice(code.as_bytes());
            }
            self.outgoing.push(Outbound {
                port,
                peer,
                data: Datagram {
                    opcode: 0x1003,
                    player: 0,
                    sequence: 0,
                    body,
                }
                .encode()?,
            });
            return Ok(());
        }
        if port == 3806 && packet.opcode == 0x1000 {
            if packet.body.len() != 4 {
                return Err(invalid("registration requires a version"));
            }
            let version = Reader::new(&packet.body).u32()?;
            if version >= 146 {
                if self.registrations.len() >= 64 && !self.registrations.contains_key(&peer.ip()) {
                    return Err(invalid("registration capacity reached"));
                }
                self.registrations.insert(peer.ip(), Instant::now());
                for session in self.sessions.values_mut() {
                    session.register_peer(peer.ip());
                }
            }
            self.outgoing.push(Outbound {
                port,
                peer,
                data: Datagram {
                    opcode: if version >= 146 { 0x1001 } else { 0x1009 },
                    player: 0,
                    sequence: 0,
                    body: if version >= 146 {
                        vec![0; 8]
                    } else {
                        246u32.to_le_bytes().to_vec()
                    },
                }
                .encode()?,
            });
            return Ok(());
        }
        self.sessions
            .get_mut(&packet.player)
            .ok_or_else(|| invalid("Unknown UDP player identity."))?
            .handle_udp(port, peer, packet)
    }

    /// Returns:
    ///     Vec<KillCredit>: Eligible pilots and their XP fixed at death capture.
    fn group_kill_credits(
        &self,
        killer: u32,
        sector: u32,
        level: u32,
    ) -> io::Result<Vec<KillCredit>> {
        let Some(owner) = self.sessions.get(&killer) else {
            return Ok(vec![KillCredit {
                player: killer,
                amount: None,
            }]);
        };
        if !owner.logged_in || owner.sector.id != sector {
            return Ok(vec![KillCredit {
                player: killer,
                amount: None,
            }]);
        }
        let members = self.groups.members(killer);
        let recipients = if members.contains(&killer) {
            members
        } else {
            vec![killer]
        };
        let mut credits = Vec::new();
        for player in recipients {
            let Some(session) = self.sessions.get(&player) else {
                continue;
            };
            if player != killer
                && (!session.logged_in
                    || session.sector.id != sector
                    || crate::mob::distance(owner.position, session.position) >= 40_000.
                    || session.peer_snapshot().is_none_or(|peer| peer.hull <= 0.))
            {
                continue;
            }
            credits.push(KillCredit {
                player,
                amount: Some(crate::experience::mob_xp(level, session.combat_level()?)),
            });
        }
        Ok(credits)
    }

    /// Commit owner receipts before removing durable sector kill events.
    fn settle_combat_awards(&mut self) -> io::Result<()> {
        let worlds: Vec<_> = self.loot_worlds.values().cloned().collect();
        for world in worlds {
            let awards = {
                let state = lock_loot(&world)?;
                self.sessions
                    .iter()
                    .filter(|(_, session)| session.logged_in)
                    .flat_map(|(id, _)| state.pending_awards(*id))
                    .collect::<Vec<_>>()
            };
            for award in awards {
                if let Some(session) = self.sessions.get_mut(&award.killer) {
                    session.settle_kill_award(&award)?;
                    lock_loot(&world)?.acknowledge_award(&award)?;
                }
            }
        }
        Ok(())
    }

    /// Returns:
    ///     Vec<u32>: Living accepted group members within native Explore XP range.
    fn explore_group_members(&self, owner: u32) -> Vec<u32> {
        let Some(source) = self.sessions.get(&owner) else {
            return Vec::new();
        };
        if !source.logged_in || source.hull <= 0. || source.sector.station.is_some() {
            return Vec::new();
        }
        self.groups
            .members(owner)
            .into_iter()
            .filter(|member| *member != owner)
            .filter(|member| {
                self.sessions.get(member).is_some_and(|session| {
                    session.logged_in
                        && session.hull > 0.
                        && session.sector.id == source.sector.id
                        && crate::mob::distance(source.position, session.position) < 40_000.
                })
            })
            .collect()
    }

    /// Replay owner-persisted Explore XP events until every recipient is durable.
    fn settle_explore_awards(&mut self) -> io::Result<()> {
        let events: Vec<_> = self
            .sessions
            .iter()
            .flat_map(|(owner, session)| {
                session
                    .inventory
                    .pending_explore_awards
                    .iter()
                    .map(|(sequence, award)| (*owner, *sequence, award.clone()))
            })
            .collect();
        for (owner, sequence, award) in events {
            for recipient in &award.recipients {
                let session = self
                    .sessions
                    .get_mut(recipient)
                    .ok_or_else(|| invalid("Exploration award recipient no longer exists."))?;
                session.settle_received_explore_award(owner, sequence, &award)?;
            }
            self.sessions
                .get_mut(&owner)
                .ok_or_else(|| invalid("Exploration award owner no longer exists."))?
                .acknowledge_explore_award(sequence)?;
        }
        Ok(())
    }

    pub fn advance(&mut self, dt: f32) -> io::Result<()> {
        self.advance_at(dt, crate::resources::unix_ms())
    }

    /// Explicit combat wall clock for deterministic flight-deadline tests.
    pub fn advance_at(&mut self, dt: f32, now_ms: u64) -> io::Result<()> {
        if !dt.is_finite() || dt < 0. {
            return Err(invalid("Invalid simulation duration."));
        }
        let mut first_error = None;
        for field in self.resource_fields.values() {
            lock_resources(field)?.advance()?;
        }
        let ids: Vec<u32> = self.sessions.keys().copied().collect();
        for id in &ids {
            let events = self.sessions.get_mut(id).unwrap().take_events();
            for event in events {
                if let Err(error) = self.handle_event(*id, event) {
                    self.sessions
                        .get_mut(id)
                        .unwrap()
                        .message(&error.to_string());
                }
            }
        }
        self.advance_formations();
        self.update_views();
        for id in &ids {
            let recipients = self.explore_group_members(*id);
            let session = self.sessions.get_mut(id).unwrap();
            session.explore_group_members = recipients;
            if let Err(error) = session.advance_player(dt) {
                if first_error.is_none() {
                    first_error = Some(error);
                }
            }
        }
        if let Err(error) = self.settle_explore_awards() {
            if first_error.is_none() {
                first_error = Some(error);
            }
        }
        self.advance_formations();
        for id in &ids {
            self.attach_session(*id)?;
        }
        self.deliver_shield_recharges()?;
        self.deliver_hull_repairs()?;
        self.deliver_rallies()?;
        self.deliver_support()?;
        for id in &ids {
            self.attach_session(*id)?;
        }
        self.synchronize_sector_hate()?;
        self.expire_group_cloak()?;
        let mut hits = Vec::new();
        for (&sector, combat) in &self.combats {
            if let Some(world) = self.loot_worlds.get(&sector) {
                lock_loot(world)?.capture_deaths_with(
                    &*lock_combat(combat)?,
                    crate::resources::unix_ms(),
                    |killer, level| self.group_kill_credits(killer, sector, level),
                )?;
            }
            let threats: Vec<_> = self
                .sessions
                .values()
                .filter(|session| session.sector.id == sector && session.logged_in)
                .map(PlayerSession::threat)
                .collect();
            match lock_combat(combat)?.advance_at(dt, &threats, now_ms) {
                Ok(resolved) => hits.extend(resolved),
                Err(error) => {
                    if first_error.is_none() {
                        first_error = Some(error);
                    }
                }
            }
        }
        for hit in hits {
            let reflection = match self.sessions.get_mut(&hit.target) {
                Some(session) => session.receive_mob_hit(&hit),
                None => Ok(0.),
            };
            let acknowledged = reflection.and_then(|reflection| {
                let combat = self
                    .combats
                    .get(&hit.sector)
                    .ok_or_else(|| invalid("NPC impact sector is no longer hosted."))?;
                lock_combat(combat)?.acknowledge_mob_hit(hit.sequence, reflection)
            });
            match acknowledged {
                Ok(()) => {
                    if let Some(session) = self.sessions.get_mut(&hit.target) {
                        session.forget_mob_hit_receipt(hit.sector, hit.sequence);
                    }
                }
                Err(error) => {
                    if first_error.is_none() {
                        first_error = Some(error);
                    }
                }
            }
        }
        for (&sector, combat) in &self.combats {
            if let Some(world) = self.loot_worlds.get(&sector) {
                lock_loot(world)?.capture_deaths_with(
                    &*lock_combat(combat)?,
                    crate::resources::unix_ms(),
                    |killer, level| self.group_kill_credits(killer, sector, level),
                )?;
            }
        }
        self.settle_combat_awards()?;
        let connected: HashSet<u32> = self
            .sessions
            .iter()
            .filter(|(_, session)| session.connected())
            .map(|(id, _)| *id)
            .collect();
        for world in self.loot_worlds.values() {
            if let Err(error) = lock_loot(world)?.release_missing_killers(&connected) {
                if first_error.is_none() {
                    first_error = Some(error);
                }
            }
        }
        for id in self.connected.difference(&connected) {
            self.groups.leave(*id);
            self.groups_dirty = true;
        }
        self.connected = connected;
        self.update_views();
        self.expire_group_cloak()?;
        if self.groups_dirty {
            self.publish_groups();
            self.save_groups()?;
            self.groups_dirty = false;
        }
        let mut damage_reports = Vec::new();
        let mut projectile_visuals = Vec::new();
        let mut mob_launches = Vec::new();
        for (&sector, combat) in &self.combats {
            mob_launches.extend(lock_combat(combat)?.take_mob_launch_visuals());
            for report in lock_combat(combat)?.take_damage_reports() {
                damage_reports.push((sector, report));
            }
            for visual in lock_combat(combat)?.take_projectile_visuals() {
                projectile_visuals.push((sector, visual));
            }
        }
        if let Err(error) = self.publish_attacker_updates(&mob_launches) {
            if first_error.is_none() {
                first_error = Some(error);
            }
        }
        let peers = self.snapshots();
        let mut station_rooms: HashMap<(u32, u32), Vec<crate::peers::StationPeerSnapshot>> =
            HashMap::new();
        for avatar in self
            .sessions
            .values()
            .filter_map(PlayerSession::station_snapshot)
        {
            station_rooms
                .entry((avatar.station, avatar.pose.room))
                .or_default()
                .push(avatar);
        }
        let effects: Vec<_> = self
            .sessions
            .iter()
            .filter(|(_, session)| session.logged_in)
            .map(|(id, session)| (*id, session.sector.id, session.public_effects()))
            .collect();
        let harvest_visuals: Vec<_> = self
            .sessions
            .values_mut()
            .flat_map(PlayerSession::take_harvest_visuals)
            .collect();
        for (id, session) in self.sessions.iter_mut() {
            for (sector, visual) in &projectile_visuals {
                if session.sector.id == *sector {
                    session.publish_projectile_visual(visual);
                }
            }
            for (sector, report) in &damage_reports {
                if session.sector.id == *sector {
                    session.publish_weapon_damage(report);
                }
            }
            session.publish_mobs()?;
            // Publish newly visible NPCs before their first launch effect.
            // Player shot visuals above still precede target-death removals.
            for visual in &mob_launches {
                session.publish_mob_launch(visual);
            }
            session.publish_resources()?;
            session.publish_corpses()?;
            session.publish_peers(&peers);
            let avatars = session
                .station_location()
                .and_then(|room| station_rooms.get(&room));
            session.publish_station_peers(avatars.map_or(&[], Vec::as_slice));
            for visual in &harvest_visuals {
                if *id != visual.source
                    && session.logged_in
                    && session.sector.id == visual.sector
                    && crate::mob::distance(session.position, visual.position) <= 20_000.
                {
                    session.queue([visual.frame.clone()]);
                }
            }
            for (source, sector, frames) in &effects {
                if *source != *id && session.logged_in && session.sector.id == *sector {
                    session.queue(frames.iter().cloned());
                }
            }
            if let Err(error) = session.finish_tick() {
                if first_error.is_none() {
                    first_error = Some(error);
                }
            }
        }
        if let Some(error) = first_error {
            if self
                .last_deferred_error
                .is_none_or(|last| last.elapsed() >= Duration::from_secs(5))
            {
                crate::server_error!(
                    "Gameplay work deferred; independent sessions remain active: {error}"
                );
                self.last_deferred_error = Some(Instant::now());
            }
        } else {
            self.last_deferred_error = None;
        }
        Ok(())
    }
    fn active_sector_membership(&self) -> HashMap<u32, u32> {
        self.sessions
            .iter()
            .filter(|(_, session)| session.logged_in)
            .map(|(id, session)| (*id, session.sector.id))
            .collect()
    }
    fn synchronize_sector_hate(&mut self) -> io::Result<()> {
        let current = self.active_sector_membership();
        for (player, sector) in &self.player_sectors {
            if current.get(player) != Some(sector) {
                if let Some(combat) = self.combats.get(sector) {
                    for mob in &mut lock_combat(combat)?.actors {
                        mob.remove_player_hate(*player);
                    }
                }
            }
        }
        self.player_sectors = current;
        Ok(())
    }

    fn snapshots(&self) -> Vec<PeerSnapshot> {
        self.sessions
            .values()
            .filter_map(PlayerSession::peer_snapshot)
            .collect()
    }
    /// Reconcile actual MOB launches with each pilot's native attacker list.
    fn publish_attacker_updates(&mut self, launches: &[MobLaunchVisual]) -> io::Result<()> {
        let mut targeted: HashMap<(u32, u32), HashSet<u32>> = HashMap::new();
        for (&sector, combat) in &self.combats {
            for (index, mob) in lock_combat(combat)?.actors.iter().enumerate() {
                if let Some(player) = mob.targeted_player() {
                    targeted
                        .entry((sector, player))
                        .or_default()
                        .insert(MOB_ID + index as u32);
                }
            }
        }
        let mut fired: HashMap<(u32, u32), HashSet<u32>> = HashMap::new();
        for shot in launches {
            fired
                .entry((shot.sector, shot.target))
                .or_default()
                .insert(MOB_ID + shot.attacker_index as u32);
        }
        let empty = HashSet::new();
        for (&player, session) in &mut self.sessions {
            let key = (session.sector.id, player);
            session.sync_attackers(
                targeted.get(&key).unwrap_or(&empty),
                fired.get(&key).unwrap_or(&empty),
            )?;
        }
        Ok(())
    }
    fn update_views(&mut self) {
        let peers = self.snapshots();
        for (id, session) in self.sessions.iter_mut() {
            session.update_peer_view(&peers, &self.groups.members(*id));
        }
    }
    pub fn take_outgoing(&mut self) -> Vec<Outbound> {
        let mut packets = std::mem::take(&mut self.outgoing);
        for session in self.sessions.values_mut() {
            packets.extend(session.take_outgoing());
        }
        packets
    }
    pub fn save(&mut self) -> io::Result<()> {
        let mut first_error = None;
        for combat in self.combats.values() {
            if let Err(error) = lock_combat(combat).and_then(|mut world| world.persist()) {
                if first_error.is_none() {
                    first_error = Some(error);
                }
            }
        }
        for session in self.sessions.values_mut() {
            if let Err(error) = session.save() {
                crate::server_error!(
                    "Character save failed: {}; {error}",
                    session.account.character.name
                );
                if first_error.is_none() {
                    first_error = Some(error);
                }
            }
            if let Err(error) = session.checkpoint_session(true) {
                crate::server_error!(
                    "Character checkpoint failed: {}; {error}",
                    session.account.character.name
                );
                if first_error.is_none() {
                    first_error = Some(error);
                }
            }
        }
        if let Err(error) = self.save_groups() {
            if first_error.is_none() {
                first_error = Some(error);
            }
        }
        match first_error {
            Some(error) => Err(error),
            None => Ok(()),
        }
    }
    pub fn checkpoint_session(&mut self, force: bool) -> io::Result<()> {
        let mut first_error = None;
        if force {
            for combat in self.combats.values() {
                if let Err(error) = lock_combat(combat).and_then(|mut world| world.persist()) {
                    if first_error.is_none() {
                        first_error = Some(error);
                    }
                }
            }
        }
        for session in self.sessions.values_mut() {
            if let Err(error) = session.checkpoint_session(force) {
                crate::server_error!(
                    "Character checkpoint failed: {}; {error}",
                    session.account.character.name
                );
                if first_error.is_none() {
                    first_error = Some(error);
                }
            }
        }
        match first_error {
            Some(error) => Err(error),
            None => Ok(()),
        }
    }
}
