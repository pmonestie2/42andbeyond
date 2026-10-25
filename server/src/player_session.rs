//! One authenticated character session: transport identity, movement, skills and
//! per-client visibility. GameState owns shared sectors and multiplayer rules.
use crate::account::Account;
#[cfg(test)]
use crate::account::FIRST_PLAYER_ID;
use crate::content::Sector;
use crate::equipment_stats::EquipmentStats;
use crate::events::ClientEvent;
use crate::inventory::{Inventory, ItemTransfer};
use crate::items::{ItemCatalog, ItemTemplate};
use crate::loot_world::{lock_loot, LootWorld};
use crate::mob::{distance, Mob, MobState};
use crate::motion::Motion;
use crate::npcs::NpcCatalog;
use crate::packets;
use crate::peers::StationPose;
use crate::protocol::SECTOR_PORT;
use crate::protocol::{invalid, split_frames, Datagram, Frame, Reader};
use crate::resources::{lock_resources, ResourceField};
use crate::sector_combat::{lock_combat, PlayerThreat, SectorCombat, MOB_ID};
use crate::sectors::SectorCatalog;
use crate::skills::{GroupContact, SkillCatalog, SkillState, SupportAction};
use crate::warp::{FinishReason, Warp, WarpEvent, WarpPhase, Waypoint};
use rand::RngCore;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeSet, HashSet};
use std::collections::{HashMap, VecDeque};
use std::fs;
use std::io;
use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
#[path = "ability_actions.rs"]
mod ability_actions;
#[path = "attacker_feedback.rs"]
mod attacker_feedback;
#[path = "combat_experience.rs"]
mod combat_experience;
#[path = "dev_session.rs"]
mod dev_session;
#[path = "energy_transfer.rs"]
mod energy_transfer;
#[path = "exploration.rs"]
pub(crate) mod exploration;
#[path = "follow_service.rs"]
mod follow_service;
#[path = "gating_service.rs"]
mod gating_service;
#[path = "harvest_visuals.rs"]
mod harvest_visuals;
#[path = "hull_repair.rs"]
mod hull_repair;
#[path = "looting.rs"]
mod looting;
#[path = "mining.rs"]
mod mining;
#[path = "mob_damage.rs"]
mod mob_damage;
#[path = "native_jumpstart.rs"]
mod native_jumpstart;
#[path = "npc_services.rs"]
mod npc_services;
#[path = "peer_buff_service.rs"]
mod peer_buff_service;
#[path = "production_service.rs"]
mod production_service;
#[path = "rally_service.rs"]
mod rally_service;
#[path = "recovery.rs"]
mod recovery;
#[path = "reputation_service.rs"]
mod reputation_service;
#[path = "sector_transfer.rs"]
mod sector_transfer;
#[path = "sector_transition.rs"]
mod sector_transition;
#[path = "session_checkpoint.rs"]
mod session_checkpoint;
#[path = "session_equipment.rs"]
mod session_equipment;
#[path = "session_peers.rs"]
mod session_peers;
#[path = "session_resources.rs"]
mod session_resources;
#[path = "session_tick.rs"]
mod session_tick;
#[path = "shield_recharge.rs"]
mod shield_recharge;
#[cfg(test)]
#[path = "session_skill_tests.rs"]
mod skill_tests;
#[path = "station_actions.rs"]
mod station_actions;
#[path = "station_peers.rs"]
mod station_peers;
#[cfg(test)]
#[path = "session_takeover_tests.rs"]
mod takeover_tests;
#[path = "target_verbs.rs"]
mod target_verbs;
#[path = "weapon_launch.rs"]
mod weapon_launch;
use std::time::{Duration, Instant};

#[derive(Serialize, Deserialize)]
struct SavedPlayer {
    version: u32,
    sector: u32,
    position: [f32; 3],
    #[serde(default = "default_home_station")]
    home_station: u32,
    #[serde(default)]
    energy_revision: u64,
    #[serde(default)]
    shield_revision: u64,
    #[serde(default)]
    hull_revision: u64,
}
fn default_home_station() -> u32 {
    10761
}

pub struct Outbound {
    pub port: u16,
    pub peer: SocketAddr,
    pub data: Vec<u8>,
}

struct Ticket {
    peer: IpAddr,
    expires: Instant,
}

pub struct PlayerSession {
    pub account: Account,
    sectors: SectorCatalog,
    items: ItemCatalog,
    npcs: NpcCatalog,
    factions: crate::factions::FactionCatalog,
    reputation: Arc<crate::factions::FactionReactions>,
    pub(crate) inventory: Inventory,
    workbench: Option<production_service::WorkbenchWindow>,
    session_epoch: u64,
    workbench_sync: f32,
    workbench_lab_ready: bool,
    equipment: EquipmentStats,
    resource_field: Arc<Mutex<ResourceField>>,
    loot_world: Arc<Mutex<LootWorld>>,
    loot_cast: Option<looting::LootCast>,
    loot_window: Option<u32>,
    visible_corpses: HashMap<u32, [f32; 3]>,
    loot_revision: Option<(u32, Vec<u32>)>,
    last_loot_publish: Option<Instant>,
    world_root: PathBuf,
    mining: Option<mining::MiningCast>,
    tractor_mining: Option<mining::MiningCast>,
    harvest_visuals: Vec<harvest_visuals::HarvestVisual>,
    resource_window: Option<u32>,
    resource_revision: Option<(u32, u64, Vec<u32>)>,
    pub sector: Sector,
    pub bind: Ipv4Addr,
    pub position: [f32; 3],
    pub logged_in: bool,
    home_station: u32,
    motion: Motion,
    selected_peer: Option<IpAddr>,
    pub endpoint: Option<SocketAddr>,
    comms: Option<SocketAddr>,
    nonce: [u8; 20],
    tickets: HashMap<String, Ticket>,
    registrations: HashMap<IpAddr, Instant>,
    sequence: u32,
    replay: VecDeque<Datagram>,
    inputs: VecDeque<u32>,
    pending: Vec<Frame>,
    outgoing: Vec<Outbound>,
    warp: Warp,
    warp_effects: Vec<u32>,
    next_effect: u32,
    last_motion_report: Option<Instant>,
    last_launch: u64,
    mob_damage_receipts: Vec<mob_damage::MobDamageReceipt>,
    attacker_ids: BTreeSet<u32>,
    following: bool,
    follow_peer: Option<crate::following::FollowContact>,
    target: u32,
    last_verbs: Option<Frame>,
    gate_transit: Option<crate::gating::GateTransit>,
    docking_target: Option<u32>,
    handoff: Option<Vec<u8>>,
    transition_pending: Option<sector_transition::TransitionRecord>,
    transition_retry: Instant,
    station_pose: Option<StationPose>,
    station_peers: HashMap<u32, StationPose>,
    station_view_dirty: bool,
    last_station_broadcast: Instant,
    npc_interaction: Option<npc_services::NpcInteraction>,
    pub(crate) combat: Arc<Mutex<SectorCombat>>,
    mob_states: Vec<MobState>,
    mob_visible: Vec<bool>,
    mob_relationships: Vec<Option<u32>>,
    player_firing: bool,
    weapon_cooldowns: [f32; 20],
    weapon_firing: [bool; 20],
    active_device: Option<session_equipment::ActiveDevice>,
    peer_buffs: Vec<crate::peer_buffs::PeerBuff>,
    peer_buff_revision: u64,
    rally_buff: Option<crate::rally::RallyBuff>,
    rally_revision: u64,
    shield: f32,
    pub(crate) hull: f32,
    energy: f32,
    energy_revision: u64,
    shield_revision: u64,
    hull_revision: u64,
    skill_catalog: SkillCatalog,
    skills: SkillState,
    group_contacts: Vec<GroupContact>,
    pub(crate) explore_group_members: Vec<u32>,
    support_actions: Vec<SupportAction>,
    resource_sync: f32,
    last_seen: Instant,
    last_broadcast: Instant,
    last_warp_broadcast: Instant,
    warp_movement_elapsed: f32,
    last_saved: Instant,
    pub state_dir: PathBuf,
    dirty: bool,
    started: Instant,
    tick_offset: u32,
    last_checkpoint: Instant,
    last_ack_request: Instant,
    peer_ids: HashSet<u32>,
    peer_contacts: HashMap<u32, crate::peers::PeerContact>,
    visible_peers: HashSet<u32>,
    peer_stats: HashMap<u32, crate::equipment_stats::ShipStats>,
    visible_resources: HashSet<u32>,
    resource_fractions: HashMap<u32, f32>,
    last_resource_visibility: Option<Instant>,
    last_peer_broadcast: Instant,
    client_events: Vec<ClientEvent>,
    formation_leader: Option<u32>,
    last_formation_broadcast: Instant,
}

impl PlayerSession {
    pub fn new(sector: Sector, bind: Ipv4Addr, state_dir: PathBuf) -> io::Result<Self> {
        let account = Account::load(&state_dir)?;
        Self::with_account(sector, bind, state_dir.clone(), account, state_dir)
    }
    pub(crate) fn with_account(
        mut sector: Sector,
        bind: Ipv4Addr,
        state_dir: PathBuf,
        account: Account,
        world_root: PathBuf,
    ) -> io::Result<Self> {
        sector.validate()?;
        let sectors = SectorCatalog::load()?;
        sector_transition::recover(&state_dir, &account, bind, &sectors, &sector)?;
        let items = ItemCatalog::load()?;
        let npcs = NpcCatalog::load()?;
        let inventory_path = state_dir.join("inventory.json");
        let mut inventory = if inventory_path.exists() {
            serde_json::from_slice(&fs::read(inventory_path)?)?
        } else {
            Inventory::starter_for(&account.character, &items)?
        };
        inventory.migrate_cargo_for_profile(&account.character)?;
        let skill_catalog = SkillCatalog::for_inventory(&account.character, &inventory)?;
        let granted = crate::sandbox_loadout::provision(
            &world_root,
            &state_dir,
            &inventory,
            &account.character,
            &items,
            &skill_catalog,
        )?;
        let grant_applied = granted.is_some();
        let inventory = granted.unwrap_or(inventory);
        inventory.validate_for_profile(&account.character, &items, &skill_catalog)?;
        let equipment = EquipmentStats::derive(&inventory, &items, &skill_catalog)?;
        let factions = crate::factions::FactionCatalog::load()?;
        let reputation =
            factions.reactions(account.character.class(), &inventory.faction_standings)?;
        let stats = equipment.ship;
        let path = state_dir.join("player.json");
        let mut position = [0.; 3];
        let mut home_station = default_home_station();
        let mut energy_revision = 0;
        let mut shield_revision = 0;
        let mut hull_revision = 0;
        if path.exists() {
            let saved: SavedPlayer = serde_json::from_slice(&fs::read(path)?)
                .map_err(|_| invalid("invalid saved player JSON"))?;
            if saved.sector != sector.id {
                sector = sectors.sector(saved.sector)?;
            }
            if saved.version != 1 || !sector.bounds.contains(saved.position) {
                return Err(invalid("invalid saved player position/sector"));
            }
            position = saved.position;
            home_station = saved.home_station;
            energy_revision = saved.energy_revision;
            shield_revision = saved.shield_revision;
            hull_revision = saved.hull_revision;
        }
        let mobs = sector
            .mobs
            .iter()
            .cloned()
            .map(Mob::new)
            .collect::<Vec<_>>();
        let mob_states = vec![MobState::Idle; mobs.len()];
        let mob_visible = vec![false; mobs.len()];
        let mob_relationships = vec![None; mobs.len()];
        let now = Instant::now();
        let next_effect = 1_000_001 + (account.character.game_id & 0xffff) * 1_000_000;
        let mut resource_field = ResourceField::load(
            &sector,
            &items,
            world_root.join(format!("resources/{}.json", sector.id)),
        )?;
        resource_field.register_receipts(account.character.game_id, &inventory.harvests)?;
        let mut loot_world = LootWorld::load(
            world_root.join(format!("loot/{}.json", sector.id)),
            sector.id,
            &items,
        )?;
        loot_world.register_receipts(account.character.game_id, &inventory.loot_receipts)?;
        let mut session = Self {
            account,
            sectors,
            items,
            npcs,
            factions,
            reputation,
            inventory,
            workbench: None,
            session_epoch: rand::random::<u64>().max(1),
            workbench_sync: 0.,
            workbench_lab_ready: false,
            equipment,
            resource_field: Arc::new(Mutex::new(resource_field)),
            loot_world: Arc::new(Mutex::new(loot_world)),
            loot_cast: None,
            loot_window: None,
            visible_corpses: HashMap::new(),
            loot_revision: None,
            last_loot_publish: None,
            world_root,
            mining: None,
            tractor_mining: None,
            harvest_visuals: Vec::new(),
            resource_window: None,
            resource_revision: None,
            sector,
            bind,
            position,
            logged_in: false,
            home_station,
            motion: Motion::default(),
            selected_peer: None,
            endpoint: None,
            comms: None,
            nonce: [0; 20],
            tickets: HashMap::new(),
            registrations: HashMap::new(),
            sequence: 0,
            replay: VecDeque::new(),
            inputs: VecDeque::new(),
            pending: Vec::new(),
            outgoing: Vec::new(),
            warp: Warp::default(),
            warp_effects: Vec::new(),
            next_effect,
            last_motion_report: None,
            last_launch: 0,
            mob_damage_receipts: Vec::new(),
            attacker_ids: BTreeSet::new(),
            following: false,
            follow_peer: None,
            target: u32::MAX,
            last_verbs: None,
            gate_transit: None,
            docking_target: None,
            handoff: None,
            transition_pending: None,
            transition_retry: now,
            station_pose: None,
            station_peers: HashMap::new(),
            station_view_dirty: false,
            last_station_broadcast: now,
            npc_interaction: None,
            combat: Arc::new(Mutex::new(SectorCombat::from_actors(mobs))),
            mob_states,
            mob_visible,
            mob_relationships,
            player_firing: false,
            weapon_cooldowns: [0.; 20],
            weapon_firing: [false; 20],
            active_device: None,
            peer_buffs: Vec::new(),
            peer_buff_revision: 0,
            rally_buff: None,
            rally_revision: 0,
            shield: stats.shield_capacity,
            hull: stats.hull_capacity,
            energy: stats.energy_capacity,
            energy_revision,
            shield_revision,
            hull_revision,
            skill_catalog,
            skills: SkillState::default(),
            group_contacts: Vec::new(),
            explore_group_members: Vec::new(),
            support_actions: Vec::new(),
            resource_sync: 0.,
            last_seen: now,
            last_broadcast: now,
            last_warp_broadcast: now,
            warp_movement_elapsed: 0.,
            last_saved: now,
            state_dir,
            dirty: false,
            started: now,
            tick_offset: 0,
            last_checkpoint: now,
            last_ack_request: now,
            peer_ids: HashSet::new(),
            peer_contacts: HashMap::new(),
            visible_peers: HashSet::new(),
            peer_stats: HashMap::new(),
            visible_resources: HashSet::new(),
            resource_fractions: HashMap::new(),
            last_resource_visibility: None,
            last_peer_broadcast: now,
            client_events: Vec::new(),
            formation_leader: None,
            last_formation_broadcast: now,
        };
        lock_loot(&session.loot_world)?.reconcile_combat(
            &mut *lock_combat(&session.combat)?,
            crate::resources::unix_ms(),
        );
        if let Err(error) = session.restore_session() {
            crate::server_error!("Session recovery skipped: {error}");
            session.abort_all_mining("Session recovery could not complete.")?;
            session.cancel_looting("Session recovery could not complete.")?;
            session.logged_in = false;
            session.selected_peer = None;
            session.endpoint = None;
            session.comms = None;
            session.handoff = None;
            session.pending.clear();
            session.forget_session()?;
        }
        session.refresh_equipment()?;
        if grant_applied {
            session.shield = session.equipment.ship.shield_capacity;
            session.energy = session.equipment.ship.energy_capacity;
            session.pending.push(packets::ship_resources(
                session.player_id(),
                &session.equipment.ship,
                Some(session.energy),
                Some(session.shield),
                Some(session.hull),
            ));
        }
        Ok(session)
    }
    pub fn player_id(&self) -> u32 {
        self.account.character.game_id
    }

    pub fn tick_ms(&self) -> u32 {
        self.tick_offset
            .wrapping_add(self.started.elapsed().as_millis() as u32)
    }

    pub fn issue_ticket(
        &mut self,
        username: &str,
        password: &str,
        peer: IpAddr,
    ) -> io::Result<String> {
        // A deliberately separate demo account. Production credentials are never
        // accepted, saved, or forwarded by this sandbox.
        if !self.account.verify(username, password) {
            return Err(invalid("invalid local sandbox credentials"));
        }
        self.issue_verified_ticket(peer)
    }

    pub(crate) fn issue_verified_ticket(&mut self, peer: IpAddr) -> io::Result<String> {
        if !self
            .registrations
            .get(&peer)
            .is_some_and(|at| at.elapsed() < Duration::from_secs(600))
        {
            return Err(invalid("register UDP before account login"));
        }
        self.tickets
            .retain(|_, ticket| ticket.expires > Instant::now());
        if self.tickets.len() >= 32 {
            return Err(invalid("too many pending login tickets"));
        }
        let mut random = [0u8; 16];
        rand::thread_rng().fill_bytes(&mut random);
        let ticket = format!(
            "{}-{}",
            self.account.username,
            random
                .iter()
                .map(|b| format!("{b:02x}"))
                .collect::<String>()
        );
        self.tickets.insert(
            ticket.clone(),
            Ticket {
                peer,
                expires: Instant::now() + Duration::from_secs(60),
            },
        );
        Ok(ticket)
    }
    pub fn consume_ticket(&mut self, ticket: &str, peer: IpAddr) -> bool {
        // Check peer ownership BEFORE removing: another connection cannot burn
        // somebody else's one-use ticket by guessing its value.
        let valid = self
            .tickets
            .get(ticket)
            .is_some_and(|t| t.peer == peer && t.expires > Instant::now());
        if valid {
            self.tickets.remove(ticket);
        }
        valid
    }
    pub fn select(&mut self, slot: u32, peer: IpAddr, via_proxy: bool) -> io::Result<Frame> {
        self.recover_weapon_launch()?;
        if self.transition_pending.is_some() {
            return Err(invalid(
                "Character transition is waiting for storage recovery.",
            ));
        }
        if slot != 0 {
            return Err(invalid("only character slot 0 exists in this sandbox"));
        }
        if self.selected_peer.is_some() {
            // Selection follows successful account authentication. A fresh
            // login may replace a stale native/proxy connection immediately.
            // Retire its claims, transport and casts before issuing a ticket;
            // old endpoint/comms logout packets then fail the normal pin checks.
            crate::server_log!(
                "Authenticated session takeover: {}",
                self.account.character.name
            );
            self.logout()?;
            self.outgoing.clear();
            self.support_actions.clear();
            self.client_events.clear();
            self.inputs.clear();
            self.replay.clear();
        }
        self.stop_following();
        self.selected_peer = Some(peer);
        self.session_epoch = rand::random::<u64>().max(1);
        self.gate_transit = None;
        self.endpoint = None;
        self.comms = None;
        self.logged_in = false;
        rand::thread_rng().fill_bytes(&mut self.nonce);
        if via_proxy {
            // Net7Proxy::SendGlobalTicket rebuilds the join structure rather
            // than forwarding our ticket. Its fixed text is zero-padded to
            // twenty bytes. Character selection is already authenticated;
            // sector input remains pinned to the selected IP and handoff port.
            self.nonce = [0; 20];
            self.nonce[..16].copy_from_slice(b"MY_Avatar_Ticket");
        }
        self.last_seen = Instant::now();
        crate::server_log!(
            "Character selected: {}, sector={}",
            self.account.character.name,
            self.sector.id
        );
        Ok(packets::character_ticket(
            self.sector.id,
            &self.nonce,
            self.player_id(),
        ))
    }
    fn reply(
        &mut self,
        port: u16,
        peer: SocketAddr,
        opcode: u16,
        player: u32,
        body: Vec<u8>,
    ) -> io::Result<()> {
        self.outgoing.push(Outbound {
            port,
            peer,
            data: Datagram {
                opcode,
                player,
                sequence: 0,
                body,
            }
            .encode()?,
        });
        Ok(())
    }
    pub fn handle_udp(&mut self, port: u16, peer: SocketAddr, packet: Datagram) -> io::Result<()> {
        if port == 3806 {
            match packet.opcode {
                0x1000 => {
                    if packet.body.len() != 4 {
                        return Err(invalid("registration requires a version"));
                    }
                    let version = Reader::new(&packet.body).u32()?;
                    if version < 146 {
                        return self.reply(port, peer, 0x1009, 0, 246u32.to_le_bytes().to_vec());
                    }
                    if self.registrations.len() >= 64
                        && !self.registrations.contains_key(&peer.ip())
                    {
                        return Err(invalid("registration capacity reached"));
                    }
                    self.registrations.insert(peer.ip(), Instant::now());
                    self.reply(port, peer, 0x1001, 0, vec![0; 8])?;
                }
                0x200f
                    if packet.player == self.player_id()
                        && self.selected_peer == Some(peer.ip()) =>
                {
                    self.comms = Some(peer);
                }
                0x1008
                    if self.logged_in
                        && packet.player == self.player_id()
                        && self.comms == Some(peer) =>
                {
                    self.logout()?;
                }
                0x3005 if self.comms == Some(peer) => {
                    self.last_seen = Instant::now();
                }
                0x1004
                    if packet.player == self.player_id()
                        && self.comms == Some(peer)
                        && self.logged_in =>
                {
                    self.last_seen = Instant::now();
                    self.apply_motion_report(&packet.body)?;
                }
                _ => {}
            }
            return Ok(());
        }
        if self.selected_peer != Some(peer.ip()) || packet.player != self.player_id() {
            return Err(invalid("packet does not belong to the selected character"));
        }
        if self.transition_pending.is_some() {
            return Err(invalid(
                "Sector transition is waiting for storage recovery.",
            ));
        }
        if port == 3808 {
            if packet.opcode != 0x2008
                || packet.body.len() < 4
                || Reader::new(&packet.body).u32()? != self.sector.id
            {
                return Err(invalid("invalid sector handoff request"));
            }
            self.endpoint = Some(peer);
            self.sequence = 0;
            self.replay.clear();
            self.inputs.clear();
            self.session_epoch = rand::random::<u64>().max(1);
            self.pending.clear();
            let mut body = Vec::from(self.bind.octets());
            body.extend_from_slice(&SECTOR_PORT.to_le_bytes());
            body.extend_from_slice(&self.player_id().to_le_bytes());
            return self.reply(port, peer, 0x2009, self.player_id(), body);
        }
        if port != SECTOR_PORT || self.endpoint != Some(peer) {
            return Err(invalid("sector endpoint mismatch"));
        }
        self.last_seen = Instant::now();
        if self.inputs.contains(&packet.sequence) {
            return Ok(());
        }
        self.inputs.push_back(packet.sequence);
        if self.inputs.len() > 256 {
            self.inputs.pop_front();
        }
        self.handle_input(packet.opcode, &packet.body)
    }

    pub fn handle_input(&mut self, opcode: u16, body: &[u8]) -> io::Result<()> {
        self.recover_weapon_launch()?;
        if self.transition_pending.is_some() {
            return Err(invalid(
                "Sector transition is waiting for storage recovery.",
            ));
        }
        if self.gate_transit.is_some() && matches!(opcode, 0x12..=0x14 | 0x9b) {
            return Err(invalid("Wait for the gate transition."));
        }
        if self.sector.station.is_some() && matches!(opcode, 0x12..=0x14 | 0x58 | 0x9b) {
            return Err(invalid("Launch into space before controlling the ship."));
        }
        let mut reader = Reader::new(body);
        match opcode {
            2 => {
                if body.len() != 137 {
                    return Err(invalid("login requires the 137-byte Win32 Login structure"));
                }
                let mut join = Reader::new(&body[16..]);
                if join.be_u32()? != self.player_id()
                    || join.be_u32()? != self.sector.id
                    || body[44..64] != self.nonce
                {
                    return Err(invalid("invalid character login ticket"));
                }
                self.logged_in = true;
                self.workbench = None;
                self.cancel_looting("New sector login.")?;
                self.loot_window = None;
                self.visible_corpses.clear();
                self.loot_revision = None;
                self.last_loot_publish = None;
                self.npc_interaction = None;
                self.station_peers.clear();
                self.station_view_dirty = true;
                self.peer_ids.clear();
                self.peer_contacts.clear();
                self.visible_peers.clear();
                self.attacker_ids.clear();
                self.peer_stats.clear();
                self.visible_resources.clear();
                self.resource_fractions.clear();
                self.resource_window = None;
                self.resource_revision = None;
                self.last_resource_visibility = None;
                self.handoff = None;
                self.gate_transit = None;
                self.warp.reset(&mut self.motion);
                self.warp_effects.clear();
                self.last_motion_report = None;
                self.stop_weapons();
                self.motion.stop();
                let time = u32::from_le_bytes(
                    body[64..68]
                        .try_into()
                        .map_err(|_| invalid("missing login time"))?,
                );
                self.pending
                    .push(packets::client_time(time, self.tick_ms()));
                self.pending.extend(packets::login_world(
                    &self.sector,
                    self.position,
                    self.tick_ms(),
                    &self.account.character,
                    &self.motion,
                    &packets::ShipInitialization {
                        catalog: &self.items,
                        item_templates: self.inventory.templates(&self.items)?,
                        inventory: &self.inventory,
                        stats: &self.equipment.ship,
                        resources: self.ship_resources(),
                    },
                ));
                self.publish_reputation();
                let shared = Arc::clone(&self.combat);
                let combat = lock_combat(&shared)?;
                self.mob_visible.fill(false);
                self.mob_relationships.resize(combat.actors.len(), None);
                for (index, mob) in combat.actors.iter().enumerate() {
                    self.mob_visible[index] = self.mob_visible_in_scan(index, mob);
                    if self.mob_visible[index] {
                        self.mob_relationships[index] = Some(self.mob_relationship(mob));
                        self.pending.extend(packets::mob_spawn(
                            MOB_ID + index as u32,
                            mob,
                            self.tick_ms(),
                            self.mob_relationship(mob),
                        ));
                    }
                }
                crate::server_log!(
                    "Sector login complete: {}, navpoints={}, mobs={}",
                    self.account.character.name,
                    self.sector.navpoints.len(),
                    self.mob_states.len()
                );
                self.publish_weapon_states();
                self.initialise_workbench_lab(true)?;
            }
            6 | 0x3004 | 0x3008 => {}
            0x8d => self.incap_request(body)?,
            0x79 | 0x7a | 0x7b | 0x7c | 0x7e | 0x80 => {
                if let Err(error) = self.workbench_input(opcode, body) {
                    self.pending.push(packets::message(&error.to_string()));
                    self.refresh_workbench(true)?;
                }
            }
            0x27 => self.move_inventory(body)?,
            0x57 => {
                if let Err(error) = self.purchase_skill(body) {
                    self.pending.push(packets::message(&format!(
                        "Skill upgrade rejected: {error}"
                    )));
                    if self.logged_in {
                        self.pending.push(packets::player_aux(
                            &self.sector,
                            &self.account.character,
                            &self.inventory,
                            &self.items,
                        ));
                    }
                }
            }
            0x5d => self.activate_equipped_item(body)?,
            0x55 => self.talk_choice(body)?,
            0x9d | 0x9f => self.station_movement(opcode, body)?,
            0x44 => {
                let client_sent = reader.u32()?;
                self.pending
                    .push(packets::client_time(client_sent, self.tick_ms()));
            }
            0x12..=0x14 => {
                if self.hull <= 0. {
                    return Err(invalid("An incapacitated ship cannot fly."));
                }
                let owner = reader.u32()?;
                if !self.logged_in
                    || (owner != self.player_id() && owner != self.player_id() & 0xffffff)
                {
                    return Err(invalid("movement owner mismatch or sector not started"));
                }
                if opcode == 0x14 && body.get(4) == Some(&12) {
                    if let Err(error) = self.begin_following() {
                        self.pending.push(packets::message(&error.to_string()));
                    }
                    return Ok(());
                }
                self.stop_following();
                // Steering or thrust is an explicit request to fly independently.
                self.detach_formation();
                if opcode == 0x14 && body.get(4) == Some(&4) && self.warp.active() {
                    let event = self.warp.finish(FinishReason::Cancelled, &mut self.motion);
                    self.emit_warp_event(event);
                } else if !self.warp.active() {
                    if opcode == 0x14 {
                        let command = reader.take(1)?[0];
                        self.motion.thrust(command)?;
                        if self.skills.cloak_rank == 1 {
                            self.motion.set_speed *= 0.5;
                        }
                    } else {
                        self.motion.steer(opcode == 0x12, reader.f32()?)?;
                    }
                    self.pending.push(packets::player_movement(
                        self.player_id(),
                        self.position,
                        self.tick_ms(),
                        &self.motion,
                    ));
                }
            }
            0x2017 => {
                let first = reader.u32()?;
                let count = reader.u32()?;
                if count > 128 {
                    return Err(invalid("resend window exceeds cache limit"));
                }
                if let Some(peer) = self.endpoint {
                    for p in &self.replay {
                        if p.sequence >= first && p.sequence < first.saturating_add(count) {
                            self.outgoing.push(Outbound {
                                port: SECTOR_PORT,
                                peer,
                                data: p.encode()?,
                            });
                        }
                    }
                }
            }
            0x2021 => {
                let ack = reader.u32()?;
                while self.replay.front().is_some_and(|p| p.sequence < ack) {
                    self.replay.pop_front();
                }
            }
            0x17 => {
                if reader.u32()? != self.player_id() {
                    return Err(invalid("target owner mismatch"));
                }
                let target = reader.u32()?;
                if self.sector.navpoints.iter().any(|n| n.id == target)
                    || self.visible_resources.contains(&target)
                    || self.visible_corpses.contains_key(&target)
                    || target == self.player_id()
                    || self.peer_ids.contains(&target)
                    || self.mob_target_in_scan(target)?
                {
                    if self.target != target {
                        self.cancel_mining("Target changed.")?;
                        self.cancel_looting("Target changed.")?;
                        if self.loot_window.take().is_some() {
                            self.pending.push(packets::resource_interface(false));
                        }
                        self.loot_revision = None;
                        if self.resource_window.take().is_some() {
                            self.pending.push(packets::resource_interface(false));
                        }
                        self.resource_revision = None;
                    }
                    self.target = target;
                    if self.npc_interaction.as_ref().is_some_and(|active| {
                        active.space_object.is_some() && active.space_object != Some(target)
                    }) {
                        self.close_npc();
                    }
                    let mut reply = Vec::from(target.to_le_bytes());
                    reply.extend_from_slice(&u32::MAX.to_le_bytes());
                    self.pending.push(Frame::new(0x19, reply));
                    self.pending
                        .push(packets::ship_target(self.player_id(), target));
                    crate::server_log!(
                        "Target selected: {}; object={target}",
                        self.account.character.name
                    );
                    self.refresh_target_verbs(true);
                    self.refresh_loot_target(true)?;
                    self.refresh_resource_target(true)?;
                    self.open_space_mission(target)?;
                }
            }
            0x5a => {
                let owner = reader.be_u32()?;
                let target = reader.be_u32()?;
                if owner == self.player_id() && target == self.target {
                    self.refresh_target_verbs(true);
                }
            }
            0x9b => {
                let owner = reader.u32()?;
                if !self.logged_in
                    || (owner != self.player_id() & 0xffffff && owner != self.player_id())
                {
                    return Err(invalid("warp owner mismatch"));
                }
                let count = reader.u16()? as usize;
                crate::server_log!(
                    "Warp request: {}; waypoints={count}; phase={:?}",
                    self.account.character.name,
                    self.warp.phase
                );
                if count > 64 || reader.rest().len() != count * 4 {
                    return Err(invalid("invalid warp waypoint count"));
                }
                self.detach_formation();
                if self.warp.active() {
                    let event = self.warp.finish(FinishReason::Cancelled, &mut self.motion);
                    self.emit_warp_event(event);
                    return Ok(());
                }
                if self.hull <= 0. {
                    return Err(invalid("Cannot warp while incapacitated."));
                }
                if self.energy <= 0. {
                    self.pending
                        .push(packets::message("The reactor has no energy for warp."));
                    return Ok(());
                }
                if self.skills.cast.is_some() {
                    return Err(invalid("An ability is charging."));
                }
                if self.skills.cloak_rank > 0 && self.skills.cloak_rank < 6 {
                    self.stop_cloak();
                }
                let mut route = VecDeque::new();
                for _ in 0..count {
                    let id = reader.u32()?;
                    route.push_back(self.warp_waypoint(id)?);
                }
                self.stop_following();
                match self.warp.start(route, self.position, &mut self.motion) {
                    Ok(event) => self.emit_warp_event(event),
                    Err(error) => self.pending.push(packets::message(&error.to_string())),
                }
            }
            0x58 => {
                let owner = reader.u32()?;
                let _action = reader.u32()?;
                let ability = reader.u32()?;
                if owner != self.player_id() && owner != self.player_id() & 0xffffff
                    || !reader.rest().is_empty()
                {
                    return Err(invalid("invalid ability owner or packet length"));
                }
                if ability == 65 && self.target != self.player_id() && self.target != u32::MAX {
                    self.client_events.push(ClientEvent::JumpstartOffer {
                        target: self.target,
                    });
                } else if let Err(error) = self.begin_ability(ability) {
                    self.pending.push(packets::message(&error.to_string()));
                }
            }
            0x1004 => {
                self.apply_motion_report(body)?;
            }
            0x4e => {
                let owner = reader.u32()?;
                let station = reader.u32()?;
                let action = reader.take(1)?[0];
                if body.len() != 9
                    || (owner != self.player_id() && owner != self.player_id() & 0xffffff)
                {
                    return Err(invalid("invalid station request owner or length"));
                }
                self.station_request(station, action)?;
            }
            0x51 => {
                let owner = reader.u32()?;
                let _unknown = reader.u32()?;
                if owner != self.player_id() && owner != self.player_id() & 0xffffff
                    || !reader.rest().is_empty()
                {
                    return Err(invalid("Invalid loot permission request."));
                }
                self.client_events.push(ClientEvent::LootOpen {
                    target: self.target,
                    toggle: false,
                });
            }
            0x2c => {
                if reader.u32()? != self.player_id() {
                    return Err(invalid("action owner mismatch"));
                }
                let action = reader.u32()?;
                let target = reader.u32()?;
                let optional = reader.u32()?;
                if optional != 0 {
                    return Err(invalid("unsupported action option"));
                }
                match action {
                    1 if self.visible_corpses.contains_key(&self.target) => {
                        self.client_events.push(ClientEvent::LootOpen {
                            target: self.target,
                            toggle: true,
                        });
                    }
                    17 => self.open_resource_window(target)?,
                    18 => self.begin_gating(target)?,
                    19 => self.complete_gating(target)?,
                    26 if target == self.player_id() => {
                        if let Err(error) = self.begin_jumpstart(target) {
                            self.pending.push(packets::message(&error.to_string()));
                        }
                    }
                    26 => self
                        .client_events
                        .push(ClientEvent::JumpstartOffer { target }),
                    9 => {
                        if self.skills.cloak_rank > 0 {
                            self.stop_cloak();
                        }
                        self.toggle_all_weapons();
                    }
                    10..=15 => self
                        .client_events
                        .push(ClientEvent::Group { action, target }),
                    28 => self.begin_docking(target)?,
                    7 => self.complete_docking(target)?,
                    8 => self
                        .pending
                        .push(packets::message("Planet landing is not implemented yet.")),
                    _ => {}
                }
            }
            0x33 => {
                if reader.u32()? != self.player_id() {
                    return Err(invalid("chat owner mismatch"));
                }
                let channel = reader.take(1)?[0];
                let size = reader.u16()? as usize;
                if size == 0 || size > 1024 || channel > 4 {
                    return Err(invalid("invalid chat length or channel"));
                }
                let text = String::from_utf8_lossy(reader.take(size)?)
                    .trim_end_matches('\0')
                    .to_owned();
                self.client_events.push(ClientEvent::Chat { channel, text });
            }
            0xbc => {
                if reader.u32()? != self.player_id() {
                    return Err(invalid("formation owner mismatch"));
                }
                let target = reader.u32()?;
                let action = reader.u32()?;
                if !reader.rest().is_empty() {
                    return Err(invalid("invalid formation packet length"));
                }
                self.client_events
                    .push(ClientEvent::Formation { action, target });
            }
            3 | 0xb9 => {
                self.abort_all_mining("Logged out.")?;
                self.cancel_looting("Logged out.")?;
                self.pending.push(Frame::new(0xba, vec![]));
                self.logged_in = false;
                self.npc_interaction = None;
                self.selected_peer = None;
                self.save()?;
                self.forget_session()?;
                self.skills = SkillState::default();
            }
            0x98 | 0xa3 | 0x1a | 0x2025 | 0x3005 => {}
            _ => {
                crate::server_error!(
                    "Unimplemented client opcode {opcode:#06x}, bytes={}",
                    body.len()
                );
            }
        }
        if self.pending.len() > 4096 {
            return Err(invalid("outgoing frame queue exceeded limit"));
        }
        Ok(())
    }
    fn move_inventory(&mut self, body: &[u8]) -> io::Result<()> {
        if !self.logged_in || self.hull <= 0. || self.warp.active() || body.len() != 24 {
            return Err(invalid(
                "inventory transfer unavailable in the current ship state",
            ));
        }
        let mut reader = Reader::new(body);
        let owner = reader.be_u32()?;
        if owner != self.player_id() && owner != self.player_id() & 0xffffff {
            return Err(invalid("inventory owner mismatch"));
        }
        let transfer = ItemTransfer {
            source: reader.be_u32()?,
            source_slot: reader.be_u32()? as usize,
            destination: reader.be_u32()?,
            destination_slot: reader.be_u32()? as usize,
            quantity: reader.be_u32()?,
        };
        if (transfer.source == 6 || transfer.source == 18)
            && self.visible_corpses.contains_key(&self.target)
        {
            self.client_events.push(ClientEvent::LootBegin {
                target: self.target,
                slot: transfer.source_slot,
            });
            return Ok(());
        }
        if transfer.source == 18 {
            if let Err(error) = self.begin_mining(transfer) {
                self.pending.push(packets::message(&error.to_string()));
                self.refresh_resource_target(true)?;
            }
            return Ok(());
        }
        self.transfer_inventory(transfer)
    }

    pub(super) fn transfer_inventory(&mut self, transfer: ItemTransfer) -> io::Result<()> {
        if !self.logged_in || self.hull <= 0. || self.warp.active() {
            return Err(invalid(
                "Inventory transfer unavailable in the current ship state.",
            ));
        }
        let candidate = match self.inventory_candidate(transfer) {
            Ok(candidate) => candidate,
            Err(error) => {
                // A valid request can fail gameplay rules. Explain it and
                // restore the client's authoritative slots without changing
                // disk state. Malformed/foreign-owner packets fail above.
                self.pending.push(packets::message(&format!(
                    "Inventory move rejected: {error}"
                )));
                self.pending.push(packets::inventory_update(
                    self.player_id(),
                    &self.inventory,
                    &self.items,
                    &self.equipment.ship,
                ));
                self.pending
                    .push(packets::vault_update(&self.inventory, &self.items));
                self.publish_weapon_states();
                return Ok(());
            }
        };
        self.commit_inventory(candidate)?;
        if transfer.source == 12 || transfer.destination == 12 {
            self.refresh_workbench_target();
        }
        self.refresh_workbench(true)?;
        crate::server_log!(
            "Inventory move accepted: {}; transfer={transfer:?}",
            self.account.character.name
        );
        Ok(())
    }

    /// Apply one native skill upgrade through the ordinary durable inventory boundary.
    fn purchase_skill(&mut self, body: &[u8]) -> io::Result<()> {
        let mut reader = Reader::new(body);
        let owner = reader.u32()?;
        let _advertised_points = reader.u32()?;
        let skill = reader.u16()? as u32;
        if body.len() != 10
            || !self.logged_in
            || (owner != self.player_id() && owner != self.player_id() & 0xffffff)
        {
            return Err(invalid("Invalid skill action owner or length."));
        }
        let candidate = self.skill_catalog.purchased_rank(&self.inventory, skill)?;
        self.commit_inventory(candidate)?;
        self.pending.push(packets::message(&format!(
            "{} trained to rank {}.",
            self.skill_catalog
                .skills
                .iter()
                .find(|entry| entry.id == skill)
                .map_or("Skill", |entry| entry.name.as_str()),
            self.skill_catalog.level(skill)
        )));
        Ok(())
    }

    /// All inventory operations share this durable publication boundary. The
    /// candidate includes both cargo and credits, so they cannot commit apart.
    fn commit_inventory(&mut self, mut candidate: Inventory) -> io::Result<()> {
        if let Some(trained) = candidate.trained_skills.as_mut() {
            for &skill in candidate
                .mission_awarded_skills
                .difference(&self.inventory.mission_awarded_skills)
            {
                if crate::skills::SkillRule::for_skill(self.skill_catalog.class_index, skill)?
                    .is_some_and(|rule| rule.quested)
                    && self
                        .skill_catalog
                        .skills
                        .iter()
                        .any(|entry| entry.id == skill)
                {
                    trained
                        .entry(skill)
                        .and_modify(|rank| *rank = (*rank).max(1))
                        .or_insert(1);
                }
            }
        }
        candidate.validate(&self.items)?;
        let skill_changed = candidate.trained_skills != self.inventory.trained_skills
            || candidate.spent_skill_points != self.inventory.spent_skill_points;
        let candidate_skills = if skill_changed {
            SkillCatalog::for_inventory(&self.account.character, &candidate)?
        } else {
            self.skill_catalog.clone()
        };
        if candidate.hull_upgrade != self.inventory.hull_upgrade || skill_changed {
            candidate.validate_for_profile(
                &self.account.character,
                &self.items,
                &candidate_skills,
            )?;
        }
        let active_device = self
            .active_device
            .as_ref()
            .filter(|active| active.valid_for(&candidate, &self.items, crate::resources::unix_ms()))
            .cloned();
        let mut active_modifiers = active_device
            .as_ref()
            .map_or_else(Vec::new, session_equipment::ActiveDevice::modifiers);
        active_modifiers.extend(
            self.peer_buffs
                .iter()
                .filter(|buff| {
                    buff.valid(&self.items, crate::resources::unix_ms(), self.session_epoch)
                })
                .map(crate::peer_buffs::PeerBuff::modifier),
        );
        active_modifiers.extend(
            self.rally_buff
                .as_ref()
                .filter(|buff| buff.valid(crate::resources::unix_ms(), self.session_epoch))
                .into_iter()
                .flat_map(crate::rally::RallyBuff::modifiers),
        );
        let equipment = EquipmentStats::derive_with_activated(
            &candidate,
            &self.items,
            &candidate_skills,
            &active_modifiers,
        )?;
        let prices = self.vendor_prices(&candidate)?;
        let reputation = if candidate.faction_standings == self.inventory.faction_standings
            && self.reputation.class_faction == self.account.character.class().faction()
        {
            Arc::clone(&self.reputation)
        } else {
            self.factions
                .reactions(self.account.character.class(), &candidate.faction_standings)?
        };
        let reputation_changed = self.reputation != reputation;
        let previous_templates: std::collections::BTreeSet<u32> = self
            .inventory
            .templates(&self.items)?
            .into_iter()
            .map(|item| item.id)
            .collect();
        let new_metadata: Vec<Frame> = candidate
            .templates(&self.items)?
            .into_iter()
            .filter(|item| !previous_templates.contains(&item.id))
            .map(|item| item.packet())
            .collect();
        let previous_credits = self.inventory.credits;
        let hull_upgraded = candidate.hull_upgrade > self.inventory.hull_upgrade;
        let experience_changed = candidate.combat_xp != self.inventory.combat_xp
            || candidate.explore_xp != self.inventory.explore_xp
            || candidate.trade_xp != self.inventory.trade_xp
            || candidate.xp_start_level != self.inventory.xp_start_level
            || candidate.missions != self.inventory.missions
            || candidate.hull_upgrade != self.inventory.hull_upgrade
            || skill_changed;
        // Publish only after durable replacement. A disk failure leaves the
        // authoritative inventory unchanged and sends no success snapshot.
        fs::create_dir_all(&self.state_dir)?;
        let temporary = self.state_dir.join("inventory.json.tmp");
        crate::durable_json::replace_staged(
            &self.state_dir.join("inventory.json"),
            &temporary,
            &candidate,
        )?;
        self.inventory = candidate;
        self.skill_catalog = candidate_skills;
        self.active_device = active_device;
        self.reputation = reputation;
        self.equipment = equipment;
        self.apply_equipment_parameters();
        if hull_upgraded {
            self.hull = self.equipment.ship.hull_capacity;
        }
        if (hull_upgraded || skill_changed) && self.logged_in {
            self.pending.push(packets::ship_parameters(
                self.player_id(),
                &self.equipment.ship,
                self.ship_resources(),
            ));
        }
        if reputation_changed {
            self.publish_reputation();
        }
        // A restored production job can create a template the client has never
        // seen. Cache metadata before publishing the new owned-item reference.
        self.pending.extend(new_metadata);
        self.pending.push(packets::inventory_update(
            self.player_id(),
            &self.inventory,
            &self.items,
            &self.equipment.ship,
        ));
        self.publish_weapon_states();
        self.pending
            .push(packets::vault_update(&self.inventory, &self.items));
        if experience_changed {
            self.pending.push(packets::player_aux(
                &self.sector,
                &self.account.character,
                &self.inventory,
                &self.items,
            ));
        }
        self.pending.extend(packets::inventory_prices(
            self.player_id(),
            &self.inventory,
            &prices,
        ));
        if let Some(active) = self.npc_interaction.as_ref().filter(|active| active.vendor) {
            crate::server_log!("Vendor inventory committed: {}; npc={}; credits_before={previous_credits}; credits_after={}", self.account.character.name, active.npc, self.inventory.credits);
        }
        Ok(())
    }

    fn inventory_candidate(&self, transfer: ItemTransfer) -> io::Result<Inventory> {
        if (transfer.source == 3 || transfer.destination == 3) && self.sector.station.is_none() {
            return Err(invalid("Vault storage is available only while docked."));
        }
        self.check_workbench_transfer(transfer)?;
        if transfer.source == 4 || transfer.destination == 4 {
            return self.vendor_transaction(transfer);
        }
        let candidate = self.inventory.moved(transfer, &self.items)?;
        candidate.validate_for_profile(
            &self.account.character,
            &self.items,
            &self.skill_catalog,
        )?;
        Ok(candidate)
    }

    fn warp_waypoint(&self, id: u32) -> io::Result<Waypoint> {
        if let Some(position) = self.visible_corpses.get(&id) {
            return Ok(Waypoint {
                id,
                position: *position,
                stand_off: 1500.,
            });
        }
        if let Some(node) = self.sector.resources.iter().find(|node| {
            node.object.id == id && distance(self.position, node.object.position) <= 20_000.
        }) {
            return Ok(Waypoint {
                id,
                position: node.object.position,
                // Mining models can engulf the ship/camera at the old 100m
                // stopping distance. Remain within even rank-one prospect range.
                stand_off: 900.,
            });
        }
        if let Some(nav) = self.sector.navpoints.iter().find(|nav| {
            nav.id == id && nav.navigation_type > 0 && self.sector.bounds.contains(nav.position)
        }) {
            return Ok(Waypoint {
                id,
                position: nav.position,
                stand_off: match nav.object_type {
                    11 => 1500.,
                    12 => 4500.,
                    _ => 100.,
                },
            });
        }
        let combat = lock_combat(&self.combat)?;
        if let Some(index) = id.checked_sub(MOB_ID).map(|index| index as usize) {
            if let Some(mob) = combat
                .actors
                .get(index)
                .filter(|mob| self.mob_visible_in_scan(index, mob))
            {
                return Ok(Waypoint {
                    id,
                    position: mob.position,
                    stand_off: 800.,
                });
            }
        }
        Err(invalid("Warp target is not a known object in this sector."))
    }
    fn apply_motion_report(&mut self, body: &[u8]) -> io::Result<()> {
        if !matches!(body.len(), 12 | 16 | 24 | 28) {
            return Err(invalid("invalid proxy motion report length"));
        }
        let mut reader = Reader::new(body);
        let position = [reader.f32()?, reader.f32()?, reader.f32()?];
        if !self.logged_in
            || self.sector.station.is_some()
            || self.warp.phase != WarpPhase::Idle
            || self.following
            || self.formation_leader.is_some()
            || self.gate_transit.is_some()
        {
            return Ok(());
        }
        if !self.sector.bounds.contains(position) {
            return Err(invalid("motion report outside sector bounds"));
        }
        // The proxy samples the graphical client's predicted position. Keep
        // it in sync, but do not accept a stale report as a teleport after warp.
        if distance(self.position, position) > 2000. {
            return Ok(());
        }
        // Proxy 2.46 appends a four-byte timestamp to either legacy report.
        if body.len() >= 24 {
            let heading = [reader.f32()?, reader.f32()?, reader.f32()?];
            if heading.iter().map(|v| v * v).sum::<f32>() > 0.0001 {
                self.motion.face(heading)?;
            }
        }
        self.position = position;
        self.last_motion_report = Some(Instant::now());
        self.dirty = true;
        Ok(())
    }
    fn add_warp_effect(&mut self, asset: u16) {
        let id = self.next_effect;
        self.next_effect = self.next_effect.wrapping_add(1);
        self.warp_effects.push(id);
        self.pending.push(packets::object_effect(
            self.player_id(),
            id,
            asset,
            self.tick_ms(),
        ));
    }

    fn emit_warp_event(&mut self, event: WarpEvent) {
        let tick = self.tick_ms();
        let send_movement = !matches!(event, WarpEvent::Waypoint { .. });
        if event == WarpEvent::Charging {
            self.warp_movement_elapsed = 0.;
        }
        match event {
            WarpEvent::Charging => {
                self.pending.push(packets::warp_state(
                    self.player_id(),
                    WarpPhase::Charging,
                    None,
                    tick,
                ));
                self.add_warp_effect(443);
                crate::server_log!("Warp charging: {}", self.account.character.name);
            }
            WarpEvent::Departed => {
                self.add_warp_effect(23);
                self.pending.push(packets::warp_state(
                    self.player_id(),
                    WarpPhase::Traveling,
                    None,
                    tick,
                ));
            }
            WarpEvent::Waypoint { index, target } => {
                self.target = target;
                self.pending
                    .push(Frame::new(0x9c, index.to_le_bytes().to_vec()));
                let mut selected = Vec::from(target.to_le_bytes());
                selected.extend_from_slice(&u32::MAX.to_le_bytes());
                self.pending.push(Frame::new(0x19, selected));
                self.pending
                    .push(packets::ship_target(self.player_id(), target));
                self.refresh_target_verbs(true);
            }
            WarpEvent::Finished(reason) => {
                for id in self.warp_effects.drain(..) {
                    self.pending
                        .push(Frame::new(0xf, id.to_le_bytes().to_vec()));
                }
                self.pending.push(packets::warp_state(
                    self.player_id(),
                    WarpPhase::Recovering,
                    Some(reason),
                    tick.wrapping_add((self.warp.recovery_seconds * 1000.) as u32),
                ));
                self.pending
                    .push(Frame::new(0x9c, (-1i32).to_le_bytes().to_vec()));
                self.pending.push(packets::message(match reason {
                    FinishReason::Arrived => "Warp destination reached.",
                    FinishReason::Cancelled => "Warp cancelled.",
                    FinishReason::Boundary => "Warp stopped at the sector boundary.",
                }));
                self.dirty = true;
                crate::server_log!(
                    "Warp stopped: {}; reason={reason:?}",
                    self.account.character.name
                );
            }
            WarpEvent::Ready => self.pending.push(packets::warp_state(
                self.player_id(),
                WarpPhase::Idle,
                None,
                tick,
            )),
        }
        if send_movement {
            self.pending.push(if self.warp.active() {
                packets::warp_movement(self.player_id(), self.position, tick, &self.motion)
            } else {
                packets::player_movement(self.player_id(), self.position, tick, &self.motion)
            });
            self.last_warp_broadcast = Instant::now();
        }
    }
    fn flush(&mut self) -> io::Result<()> {
        let Some(peer) = self.endpoint else {
            self.pending.clear();
            return Ok(());
        };
        if self.pending.is_empty() {
            return Ok(());
        }
        // ACK requests are regular inner frames, not an extra UDP framing layer.
        if self.last_ack_request.elapsed() >= Duration::from_secs(1) {
            self.pending
                .push(Frame::new(0x2020, self.sequence.to_le_bytes().to_vec()));
            self.last_ack_request = Instant::now();
        }
        let frames = std::mem::take(&mut self.pending);
        for packet in split_frames(self.player_id(), &mut self.sequence, &frames)? {
            self.outgoing.push(Outbound {
                port: SECTOR_PORT,
                peer,
                data: packet.encode()?,
            });
            self.replay.push_back(packet);
            if self.replay.len() > 128 {
                self.replay.pop_front();
            }
        }
        Ok(())
    }
    pub fn take_outgoing(&mut self) -> Vec<Outbound> {
        std::mem::take(&mut self.outgoing)
    }
    pub fn logout(&mut self) -> io::Result<()> {
        self.recover_weapon_launch()?;
        self.stop_following();
        self.finish_transition()?;
        self.cancel_looting("Logged out.")?;
        self.abort_all_mining("Logged out.")?;
        self.resource_window = None;
        self.npc_interaction = None;
        self.save()?;
        self.logged_in = false;
        self.handoff = None;
        self.docking_target = None;
        self.selected_peer = None;
        self.gate_transit = None;
        self.endpoint = None;
        self.comms = None;
        self.warp.reset(&mut self.motion);
        self.warp_effects.clear();
        self.last_motion_report = None;
        self.pending.clear();
        self.attacker_ids.clear();
        self.stop_weapons();
        self.active_device = None;
        self.peer_buffs.clear();
        self.peer_buff_revision = 0;
        self.rally_buff = None;
        self.rally_revision = 0;
        self.refresh_equipment()?;
        self.forget_session()?;
        self.skills = SkillState::default();
        crate::server_log!("Local character logged out");
        Ok(())
    }
    pub fn save(&mut self) -> io::Result<()> {
        self.finish_transition()?;
        fs::create_dir_all(&self.state_dir)?;
        let bytes = serde_json::to_vec_pretty(&SavedPlayer {
            version: 1,
            sector: self.sector.id,
            position: self.position,
            home_station: self.home_station,
            energy_revision: self.energy_revision,
            shield_revision: self.shield_revision,
            hull_revision: self.hull_revision,
        })?;
        let temporary = self.state_dir.join("player.json.tmp");
        fs::write(&temporary, bytes)?;
        fs::rename(temporary, self.state_dir.join("player.json"))?;
        self.dirty = false;
        self.last_saved = Instant::now();
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Verify equipped Combat Trance and NPC-hit receipts survive replacement.
    #[test]
    fn progen_combat_trance_gear_and_damage_receipt_survive_replacement() {
        let root = std::env::temp_dir().join(format!("enb-trance-test-{}", rand::random::<u64>()));
        let mut account = Account::create("pw".into(), "pass", "Warrior".into()).unwrap();
        account.character.race = 2;
        account.character.profession = 0;
        account.save_new(&root).unwrap();
        let sector: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
        let bind = Ipv4Addr::new(127, 0, 0, 2);
        let mut ship = PlayerSession::new(sector.clone(), bind, root.clone()).unwrap();
        let ordinary = ship.equipment.ship.deflects[3];
        let mut candidate = ship.inventory.clone();
        candidate.equipped[9] = Some(crate::inventory::ItemInstance::stock(2097, 1));
        ship.commit_inventory(candidate).unwrap();
        assert!(ship.equipment.ship.deflects[3] > ordinary);
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22343".parse().unwrap());
        ship.shield = ship.equipment.ship.shield_capacity;
        let starting_shield = ship.shield;
        let hit = crate::npc_projectiles::MobHit {
            sector: ship.sector.id,
            sequence: 1,
            target: ship.player_id(),
            target_epoch: ship.session_epoch,
            attacker_index: 0,
            attacker_source: 46,
            attacker_template: 0,
            attacker_generation: 0,
            damage: 20.,
            damage_type: 3,
        };
        ship.receive_mob_hit(&hit).unwrap();
        let expected = starting_shield - 20. * (1. - ship.equipment.ship.deflects[3] / 100.);
        assert!((ship.shield - expected).abs() < 0.001);
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        let mut restored = PlayerSession::new(sector, bind, root).unwrap();
        assert_eq!(restored.inventory.equipped[9], ship.inventory.equipped[9]);
        assert_eq!(
            restored.equipment.ship.deflects[3],
            ship.equipment.ship.deflects[3]
        );
        assert_eq!(restored.shield, ship.shield);
        restored.receive_mob_hit(&hit).unwrap();
        assert_eq!(restored.shield, ship.shield);
    }

    /// Verify a saved prospect device changes one pilot without changing another.
    #[test]
    fn equipped_prospect_skill_bonus_survives_replacement_without_affecting_other_pilots() {
        let mut miner = session();
        let other = session();
        let baseline = other.equipment.ship.prospect_bonus;
        let mut candidate = miner.inventory.clone();
        candidate.equipped[9] = Some(crate::inventory::ItemInstance::stock(1760, 1));
        miner.commit_inventory(candidate).unwrap();
        assert_eq!(miner.equipment.ship.prospect_bonus, 0.53);
        assert_eq!(other.equipment.ship.prospect_bonus, baseline);
        miner.save().unwrap();
        miner.checkpoint_session(true).unwrap();
        let restored =
            PlayerSession::new(miner.sector.clone(), miner.bind, miner.state_dir.clone()).unwrap();
        assert_eq!(restored.inventory.equipped[9], miner.inventory.equipped[9]);
        assert_eq!(restored.equipment.ship.prospect_bonus, 0.53);
        assert_eq!(other.equipment.ship.prospect_bonus, baseline);
    }

    pub(super) fn journal_ship() -> PlayerSession {
        let mut ship = gate_ship();
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22343".parse().unwrap());
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        ship.pending.clear();
        ship
    }

    #[test]
    fn legacy_hull_checkpoints_keep_health_fraction_when_native_class_caps_replace_the_prototype() {
        let mut ship = session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22343".parse().unwrap());
        ship.hull = 500.;
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        let path = ship.state_dir.join("session.local.json");
        let mut legacy: serde_json::Value =
            serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
        legacy.as_object_mut().unwrap().remove("hull_capacity");
        fs::write(&path, serde_json::to_vec(&legacy).unwrap()).unwrap();
        let mut resumed =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(resumed.logged_in);
        assert_eq!(resumed.equipment.ship.hull_capacity, 42000.);
        assert_eq!(resumed.hull, 21000.);
        resumed.checkpoint_session(true).unwrap();
        let again =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(again.logged_in);
        assert_eq!(again.hull, 21000.);
    }

    #[test]
    fn handoff_journal_write_failure_keeps_source_location_transport_and_packets_unchanged() {
        let mut ship = journal_ship();
        let position = ship.position;
        let player = fs::read(ship.state_dir.join("player.json")).unwrap();
        let checkpoint = fs::read(ship.state_dir.join("session.local.json")).unwrap();
        let blocked = ship.state_dir.join("sector-transition.local.json.next");
        fs::create_dir(&blocked).unwrap();
        assert!(ship.transfer_sector(1071, true).is_err());
        assert_eq!(ship.sector.id, 1076);
        assert_eq!(ship.position, position);
        assert!(ship.logged_in);
        assert!(ship.handoff.is_none());
        assert!(ship.transition_pending.is_none());
        assert!(!ship.pending.iter().any(|frame| frame.opcode == 0x3a));
        assert_eq!(
            fs::read(ship.state_dir.join("player.json")).unwrap(),
            player
        );
        assert_eq!(
            fs::read(ship.state_dir.join("session.local.json")).unwrap(),
            checkpoint
        );
        fs::remove_dir(blocked).unwrap();
        let journal = ship.state_dir.join("sector-transition.local.json");
        fs::write(&journal, b"unresolved prior transaction").unwrap();
        assert!(ship.transfer_sector(1071, true).is_err());
        assert_eq!(fs::read(&journal).unwrap(), b"unresolved prior transaction");
        assert_eq!(ship.sector.id, 1076);
        assert!(ship.handoff.is_none());
        fs::remove_file(journal).unwrap();
        ship.transfer_sector(1071, true).unwrap();
        assert!(ship.pending.iter().any(|frame| frame.opcode == 0x3a));
        assert!(ship.transition_pending.is_none());
    }

    #[test]
    fn committed_handoff_holds_output_and_input_until_both_mirrors_can_be_installed() {
        let mut ship = journal_ship();
        let old_checkpoint = fs::read(ship.state_dir.join("session.local.json")).unwrap();
        let blocked = ship.state_dir.join("session.local.json.next");
        fs::create_dir(&blocked).unwrap();
        ship.transfer_sector(1071, true).unwrap();
        assert_eq!(ship.sector.id, 1071);
        assert!(!ship.logged_in);
        assert!(ship.transition_pending.is_some());
        assert!(ship
            .state_dir
            .join("sector-transition.local.json")
            .is_file());
        let player: SavedPlayer =
            serde_json::from_slice(&fs::read(ship.state_dir.join("player.json")).unwrap()).unwrap();
        assert_eq!(player.sector, 1071);
        assert_eq!(
            fs::read(ship.state_dir.join("session.local.json")).unwrap(),
            old_checkpoint
        );
        assert!(!ship.pending.iter().any(|frame| frame.opcode == 0x3a));
        assert!(ship.handle_input(2, &[]).is_err());
        assert!(ship.select(0, "127.0.0.1".parse().unwrap(), true).is_err());
        ship.transition_retry = Instant::now() - Duration::from_millis(300);
        ship.finish_tick().unwrap();
        assert!(ship.take_outgoing().is_empty());
        assert!(ship.transition_pending.is_some());
        fs::remove_dir(blocked).unwrap();
        ship.finish_transition().unwrap();
        assert!(ship.transition_pending.is_none());
        assert!(!ship.state_dir.join("sector-transition.local.json").exists());
        assert_eq!(
            ship.pending
                .iter()
                .filter(|frame| frame.opcode == 0x3a)
                .count(),
            1
        );
        ship.finish_transition().unwrap();
        assert_eq!(
            ship.pending
                .iter()
                .filter(|frame| frame.opcode == 0x3a)
                .count(),
            1
        );
    }

    #[test]
    fn journal_replays_a_committed_transition_before_loading_old_or_half_updated_files() {
        for mirror in ["player.json.next", "session.local.json.next"] {
            let mut ship = journal_ship();
            let blocked = ship.state_dir.join(mirror);
            fs::create_dir(&blocked).unwrap();
            ship.transfer_sector(1071, true).unwrap();
            let handoff = ship.handoff.clone();
            let sequence = ship.sequence;
            assert!(ship.transition_pending.is_some());
            fs::remove_dir(blocked).unwrap();
            let mut restored =
                PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
            assert_eq!(restored.sector.id, 1071);
            assert_eq!(restored.handoff, handoff);
            assert_eq!(restored.sequence, sequence);
            assert!(!restored.logged_in);
            assert!(restored.pending.iter().any(|frame| frame.opcode == 0x3a));
            assert!(!restored
                .state_dir
                .join("sector-transition.local.json")
                .exists());
            restored.checkpoint_session(true).unwrap();
            let again = PlayerSession::new(
                restored.sector.clone(),
                restored.bind,
                restored.state_dir.clone(),
            )
            .unwrap();
            assert_eq!(again.handoff, handoff);
            assert_eq!(again.sector.id, 1071);
        }
    }

    #[test]
    fn corrupt_or_foreign_transition_journal_never_overwrites_existing_character_files() {
        let mut ship = journal_ship();
        let blocked = ship.state_dir.join("player.json.next");
        fs::create_dir(&blocked).unwrap();
        let player = fs::read(ship.state_dir.join("player.json")).unwrap();
        let checkpoint = fs::read(ship.state_dir.join("session.local.json")).unwrap();
        ship.transfer_sector(1071, true).unwrap();
        fs::remove_dir(blocked).unwrap();
        let journal = ship.state_dir.join("sector-transition.local.json");
        let mut invalid: serde_json::Value =
            serde_json::from_slice(&fs::read(&journal).unwrap()).unwrap();
        invalid["username"] = "AnotherAccount".into();
        fs::write(&journal, serde_json::to_vec(&invalid).unwrap()).unwrap();
        assert!(
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).is_err()
        );
        assert_eq!(
            fs::read(ship.state_dir.join("player.json")).unwrap(),
            player
        );
        assert_eq!(
            fs::read(ship.state_dir.join("session.local.json")).unwrap(),
            checkpoint
        );
        assert!(journal.exists());
    }

    #[test]
    fn failed_dock_or_tow_keeps_clearance_home_station_and_original_health() {
        let mut ship = journal_ship();
        ship.sector = ship.sectors.sector(1076).unwrap();
        ship.position = ship
            .sector
            .navpoints
            .iter()
            .find(|nav| nav.id == 100002)
            .unwrap()
            .position;
        ship.target = 100002;
        ship.home_station = 17051;
        ship.hull = 500.;
        ship.shield = 12.;
        ship.energy = 77.;
        ship.skills.cloak_rank = 1;
        ship.begin_docking(100002).unwrap();
        fs::create_dir(ship.state_dir.join("sector-transition.local.json.next")).unwrap();
        assert!(ship.complete_docking(100002).is_err());
        assert_eq!(ship.docking_target, Some(100002));
        assert_eq!(ship.home_station, 17051);
        assert_eq!((ship.hull, ship.shield, ship.energy), (500., 12., 77.));
        assert_eq!(ship.skills.cloak_rank, 1);
        assert!(ship.logged_in);
        assert!(!ship.pending.iter().any(|frame| frame.opcode == 0x3a));
        ship.hull = 0.;
        ship.shield = 0.;
        ship.energy = 0.;
        assert!(ship.tow_to_station().is_err());
        assert_eq!((ship.hull, ship.shield, ship.energy), (0., 0., 0.));
        assert_eq!(ship.home_station, 17051);
        assert!(ship.logged_in);
        assert!(ship.handoff.is_none());
    }

    #[test]
    fn transition_recovery_uses_configured_map_bounds_for_a_standalone_sector() {
        let mut ship = journal_ship();
        ship.sector.bounds.max[0] = 1_999_999.;
        ship.position = [1_900_000., 0., 0.];
        ship.sector.validate().unwrap();
        let blocked = ship.state_dir.join("session.local.json.next");
        fs::create_dir(&blocked).unwrap();
        ship.transfer_sector(ship.sector.id, false).unwrap();
        fs::remove_dir(blocked).unwrap();
        let restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(restored.position, ship.position);
        assert!(restored.handoff.is_some());
    }

    fn gate_ship() -> PlayerSession {
        let mut ship = session();
        ship.logged_in = true;
        ship.position = [18_500., 0., 0.];
        ship.target = 100000;
        ship
    }
    #[test]
    fn gate_and_station_admission_use_exported_unscaled_surface_radii() {
        let mut gate_ship = gate_ship();
        let gate = gate_ship
            .sector
            .navpoints
            .iter()
            .find(|nav| nav.id == 100000)
            .unwrap()
            .clone();
        assert!(gate.radius > 0.);
        gate_ship.position = [
            gate.position[0] + gate.radius + 5001.,
            gate.position[1],
            gate.position[2],
        ];
        assert!(gate_ship.begin_gating(gate.id).is_err());
        gate_ship.position[0] -= 2.;
        gate_ship.begin_gating(gate.id).unwrap();

        let mut dock_ship = session();
        dock_ship.sector = dock_ship.sectors.sector(1076).unwrap();
        dock_ship.logged_in = true;
        let station = dock_ship
            .sector
            .navpoints
            .iter()
            .find(|nav| nav.object_type == 12)
            .unwrap()
            .clone();
        assert!(station.radius > 0.);
        dock_ship.target = station.id;
        dock_ship.position = [
            station.position[0] + station.radius + 5001.,
            station.position[1],
            station.position[2],
        ];
        assert!(dock_ship.begin_docking(station.id).is_err());
        dock_ship.position[0] -= 2.;
        dock_ship.begin_docking(station.id).unwrap();
    }
    fn make_gate_camera_due(ship: &mut PlayerSession) {
        ship.gate_transit.as_mut().unwrap().started_at_ms =
            crate::resources::unix_ms() - crate::gating::GATE_CAMERA_DELAY_MS;
    }

    #[test]
    fn gate_opening_defers_camera_and_keeps_early_completion_instead_of_stranding_client() {
        let mut ship = gate_ship();
        ship.pending.clear();
        ship.begin_gating(100000).unwrap();
        assert!(ship
            .pending
            .contains(&packets::gate_render_state(100000, 1)));
        assert!(!ship.pending.iter().any(|frame| frame.opcode == 0x92));
        let started = ship.gate_transit.as_ref().unwrap().started_at_ms;
        ship.begin_gating(100000).unwrap();
        assert_eq!(ship.gate_transit.as_ref().unwrap().started_at_ms, started);
        assert!(ship.complete_gating(12345).is_err());
        ship.complete_gating(100000).unwrap();
        assert!(ship.logged_in);
        assert!(ship.gate_transit.as_ref().unwrap().completion_requested);
        make_gate_camera_due(&mut ship);
        ship.advance_gating().unwrap();
        assert!(!ship.logged_in);
        assert!(ship.gate_transit.is_none());
        assert!(ship.handoff.is_some());
        assert!(ship.pending.iter().any(|frame| frame.opcode == 0x3a));
        assert!(ship.complete_gating(100000).is_err());
    }

    #[test]
    fn gate_camera_and_ack_survive_replacement_without_duplicate_camera_or_lost_handoff() {
        let mut ship = gate_ship();
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22343".parse().unwrap());
        ship.begin_gating(100000).unwrap();
        make_gate_camera_due(&mut ship);
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(restored.gate_transit, ship.gate_transit);
        restored.pending.clear();
        restored.advance_gating().unwrap();
        assert_eq!(
            restored
                .pending
                .iter()
                .filter(|frame| **frame == packets::gate_camera(4, 100000))
                .count(),
            1
        );
        restored.advance_gating().unwrap();
        assert_eq!(
            restored
                .pending
                .iter()
                .filter(|frame| **frame == packets::gate_camera(4, 100000))
                .count(),
            1
        );
        restored.save().unwrap();
        restored.checkpoint_session(true).unwrap();
        let mut again = PlayerSession::new(
            restored.sector.clone(),
            restored.bind,
            restored.state_dir.clone(),
        )
        .unwrap();
        again.pending.clear();
        again.advance_gating().unwrap();
        assert!(!again.pending.iter().any(|frame| frame.opcode == 0x92));
        again.complete_gating(0).unwrap();
        assert!(again.handoff.is_some());
        again.checkpoint_session(true).unwrap();
        let handoff =
            PlayerSession::new(again.sector.clone(), again.bind, again.state_dir.clone()).unwrap();
        assert_eq!(handoff.handoff, again.handoff);
        assert!(handoff.gate_transit.is_none());
        assert!(handoff.pending.iter().any(|frame| frame.opcode == 0x3a));
    }

    #[test]
    fn gate_rejects_incap_range_wrong_target_and_warp_then_expiry_recovers_session() {
        let mut ship = gate_ship();
        ship.hull = 0.;
        assert!(ship.begin_gating(100000).is_err());
        ship.hull = 1000.;
        ship.position = [0.; 3];
        assert!(ship.begin_gating(100000).is_err());
        ship.position = [18_500., 0., 0.];
        ship.target = 100001;
        assert!(ship.begin_gating(100000).is_err());
        ship.target = 100000;
        ship.begin_gating(100000).unwrap();
        assert!(!ship.can_form_up());
        let before = ship.position;
        let report = [
            18_600f32.to_le_bytes(),
            0f32.to_le_bytes(),
            0f32.to_le_bytes(),
        ]
        .concat();
        ship.handle_input(0x1004, &report).unwrap();
        assert_eq!(ship.position, before);
        assert!(ship.handle_input(0x9b, &[]).is_err());
        assert!(ship.begin_ability(29).is_err());
        ship.gate_transit.as_mut().unwrap().started_at_ms =
            crate::resources::unix_ms() - crate::gating::GATE_TIMEOUT_MS - 1;
        ship.advance_gating().unwrap();
        assert!(ship.gate_transit.is_none());
        assert!(ship.logged_in);
        assert!(ship
            .pending
            .contains(&packets::gate_render_state(100000, 3)));
        assert!(ship
            .pending
            .contains(&packets::gate_camera(0, ship.player_id())));
        assert!(ship.complete_gating(100000).is_err());
    }

    fn refinery_ship() -> PlayerSession {
        let mut ship = session();
        ship.sector = ship.sectors.sector(10761).unwrap();
        ship.position = [0.; 3];
        ship.logged_in = true;
        ship.inventory.cargo[0] = Some(crate::inventory::ItemInstance {
            template: 2845,
            quantity: 15,
            quality: 1.,
            structure: 1.,
            builder_name: String::new(),
        });
        ship.initialise_workbench_lab(true).unwrap();
        ship.workbench_input(
            0x79,
            &[ship.player_id().to_be_bytes(), 4u32.to_be_bytes()].concat(),
        )
        .unwrap();
        ship.workbench_input(
            0x7c,
            &[ship.player_id().to_be_bytes(), 3381u32.to_be_bytes()].concat(),
        )
        .unwrap();
        ship
    }

    fn production_packet(
        ship: &mut PlayerSession,
        opcode: u16,
        value: u32,
        sequence: u32,
    ) -> io::Result<()> {
        ship.inputs.push_back(sequence);
        ship.workbench_input(
            opcode,
            &[ship.player_id().to_be_bytes(), value.to_be_bytes()].concat(),
        )
    }

    fn crafting_ship() -> PlayerSession {
        let mut ship = refinery_ship();
        let mut inventory = ship.inventory.clone();
        inventory.cargo.fill(None);
        inventory.cargo[0] = Some(crate::inventory::ItemInstance {
            template: 1758,
            quantity: 1,
            quality: 1.,
            structure: 1.,
            builder_name: String::new(),
        });
        inventory.credits = 1_000_000;
        ship.commit_inventory(inventory).unwrap();
        production_packet(&mut ship, 0x79, 2, 1).unwrap();
        ship.transfer_inventory(ItemTransfer {
            source: 1,
            source_slot: 0,
            destination: 12,
            destination_slot: 0,
            quantity: 1,
        })
        .unwrap();
        ship
    }

    fn make_craft_due(ship: &mut PlayerSession) {
        let mut inventory = ship.inventory.clone();
        inventory.craft_job.as_mut().unwrap().due_ms = 1;
        ship.commit_inventory(inventory).unwrap();
    }

    #[test]
    fn native_dismantle_recovery_is_frozen_and_survives_disk_failure_and_restart() {
        let mut ship = crafting_ship();
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22092".parse().unwrap());
        let mut inventory = ship.inventory.clone();
        inventory.production_target[0]
            .as_mut()
            .unwrap()
            .builder_name = "OldBuilder".into();
        ship.commit_inventory(inventory).unwrap();
        production_packet(&mut ship, 0x79, 3, 8).unwrap();
        production_packet(&mut ship, 0x7e, 2, 9).unwrap();
        let recipe = crate::crafting::CraftRecipe::resolve(1758, &ship.items).unwrap();
        assert_eq!(
            ship.inventory
                .craft_job
                .as_ref()
                .unwrap()
                .recovered_components,
            recipe.components
        );
        make_craft_due(&mut ship);
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        let before = restored.inventory.clone();
        let directory = restored.state_dir.clone();
        let blocked = directory.join("blocked");
        fs::write(&blocked, b"not a directory").unwrap();
        restored.state_dir = blocked;
        assert!(restored.advance_production(0.1).is_err());
        assert_eq!(restored.inventory, before);
        restored.state_dir = directory;
        restored.advance_production(0.1).unwrap();
        assert_eq!(
            restored
                .inventory
                .cargo
                .iter()
                .flatten()
                .map(|i| i.template)
                .collect::<Vec<_>>(),
            recipe.components
        );
        assert!(restored.inventory.production_target[0].is_none());
        assert!(restored.inventory.known_formulas.is_empty());
        assert_eq!(restored.inventory.credits, before.credits - recipe.cost);
        assert_eq!(restored.inventory.trade_xp, before.trade_xp);
        let complete = restored.inventory.clone();
        restored.advance_production(0.1).unwrap();
        assert_eq!(restored.inventory, complete);
        assert!(production_packet(&mut restored, 0x7e, 2, 9).is_err());
    }

    #[test]
    fn new_owned_template_metadata_precedes_inventory_publication_after_commit() {
        let mut ship = session();
        let candidate = ship
            .inventory
            .received(1758, 1, u32::MAX as usize, &ship.items)
            .unwrap();
        ship.pending.clear();
        ship.commit_inventory(candidate).unwrap();
        let metadata = ship
            .pending
            .iter()
            .position(|frame| frame.opcode == 0x25 && frame.body[..4] == 1758u32.to_be_bytes())
            .unwrap();
        let inventory = ship
            .pending
            .iter()
            .position(|frame| frame.opcode == 0x1b)
            .unwrap();
        assert!(metadata < inventory);
    }

    #[test]
    fn native_analysis_escrow_learns_recipe_then_manufactures_with_real_components() {
        let mut ship = crafting_ship();
        let before = ship.inventory.clone();
        assert!(production_packet(&mut ship, 0x79, 1, 2).is_err());
        assert!(production_packet(&mut ship, 0x79, 4, 3).is_err());
        assert_eq!(ship.inventory, before);
        assert!(ship.launch_from_station().is_err());
        production_packet(&mut ship, 0x7e, 2, 10).unwrap();
        assert_eq!(ship.inventory.credits, before.credits);
        assert_eq!(ship.inventory.production_target, before.production_target);
        assert!(ship.inventory.known_formulas.is_empty());
        assert!(production_packet(&mut ship, 0x79, 4, 11).is_err());
        assert!(ship
            .inventory_candidate(ItemTransfer {
                source: 12,
                source_slot: 0,
                destination: 1,
                destination_slot: 0,
                quantity: 1
            })
            .is_err());
        make_craft_due(&mut ship);
        ship.advance_production(0.1).unwrap();
        assert!(ship.inventory.known_formulas.contains(&1758));
        assert!(ship.inventory.production_target[0].is_none());
        let recipe = crate::crafting::CraftRecipe::resolve(1758, &ship.items).unwrap();
        assert_eq!(ship.inventory.credits, before.credits - recipe.cost);
        production_packet(&mut ship, 0x79, 1, 12).unwrap();
        production_packet(&mut ship, 0x7b, 1758, 13).unwrap();
        assert!(production_packet(&mut ship, 0x7e, 2, 14).is_err());
        let mut inventory = ship.inventory.clone();
        for (index, component) in recipe.components.iter().enumerate() {
            inventory.cargo[index] = Some(crate::inventory::ItemInstance {
                template: *component,
                quantity: 1,
                quality: 1.,
                structure: 1.,
                builder_name: String::new(),
            });
        }
        ship.commit_inventory(inventory).unwrap();
        production_packet(&mut ship, 0x7e, 2, 15).unwrap();
        make_craft_due(&mut ship);
        ship.advance_production(0.1).unwrap();
        assert_eq!(
            ship.inventory
                .cargo
                .iter()
                .flatten()
                .map(|item| item.template)
                .collect::<Vec<_>>(),
            vec![1758]
        );
        assert_eq!(ship.inventory.credits, before.credits - 2 * recipe.cost);
        assert_eq!(ship.inventory.trade_xp, 50);
        assert!(production_packet(&mut ship, 0x7e, 2, 15).is_err());
    }

    #[test]
    fn analysis_restart_cancellation_and_failed_disk_write_preserve_owned_sample() {
        let mut ship = crafting_ship();
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22092".parse().unwrap());
        production_packet(&mut ship, 0x7e, 2, 100).unwrap();
        make_craft_due(&mut ship);
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(restored.inventory, ship.inventory);
        let before = restored.inventory.clone();
        let directory = restored.state_dir.clone();
        let blocked = directory.join("blocked");
        fs::write(&blocked, b"not a directory").unwrap();
        restored.state_dir = blocked;
        assert!(restored.advance_production(0.1).is_err());
        assert_eq!(restored.inventory, before);
        restored.state_dir = directory;
        restored.advance_production(0.1).unwrap();
        let complete = restored.inventory.clone();
        restored.advance_production(0.1).unwrap();
        assert_eq!(restored.inventory, complete);
        assert!(complete.known_formulas.contains(&1758));
        let mut cancelled = crafting_ship();
        let sample = cancelled.inventory.production_target.clone();
        let credits = cancelled.inventory.credits;
        production_packet(&mut cancelled, 0x7e, 2, 20).unwrap();
        production_packet(&mut cancelled, 0x7e, 0, 21).unwrap();
        assert!(cancelled.inventory.craft_job.is_none());
        assert_eq!(cancelled.inventory.production_target, sample);
        assert_eq!(cancelled.inventory.credits, credits);
        // Closing leaves the sample owned; it can be returned without reopening.
        cancelled
            .transfer_inventory(ItemTransfer {
                source: 12,
                source_slot: 0,
                destination: 1,
                destination_slot: 0,
                quantity: 1,
            })
            .unwrap();
        assert!(cancelled.inventory.production_target[0].is_none());
        assert_eq!(
            cancelled.inventory.cargo[0].as_ref().unwrap().template,
            1758
        );
        // Native terminal-reset and action-leave take the same cancel path.
        let mut reset = crafting_ship();
        production_packet(&mut reset, 0x7e, 2, 30).unwrap();
        production_packet(&mut reset, 0x79, 0, 31).unwrap();
        assert!(reset.inventory.craft_job.is_none());
        assert!(reset.inventory.production_target[0].is_some());
        assert_eq!(reset.inventory.credits, 1_000_000);
    }

    fn start_refine(ship: &mut PlayerSession, sequence: u32, stack: bool) {
        ship.inputs.push_back(sequence);
        ship.workbench_input(
            0x7e,
            &[
                ship.player_id().to_be_bytes(),
                (if stack { 3u32 } else { 2u32 }).to_be_bytes(),
            ]
            .concat(),
        )
        .unwrap();
    }

    fn make_refine_due(ship: &mut PlayerSession) {
        let mut candidate = ship.inventory.clone();
        candidate.refine_job.as_mut().unwrap().due_ms = 1;
        ship.commit_inventory(candidate).unwrap();
    }

    #[test]
    fn native_refinery_selection_stack_timer_and_atomic_result() {
        let mut ship = refinery_ship();
        let credits = ship.inventory.credits;
        assert!(ship.pending.iter().any(
            |frame| frame.opcode == 0x7f && frame.body == ship.workbench_lab_id().to_be_bytes()
        ));
        start_refine(&mut ship, 100, true);
        assert_eq!(ship.inventory.refine_job.as_ref().unwrap().iterations, 3);
        assert_eq!(ship.inventory.cargo[0].as_ref().unwrap().quantity, 15);
        assert_eq!(ship.inventory.credits, credits);
        assert!(ship.launch_from_station().is_err());
        make_refine_due(&mut ship);
        ship.advance_production(0.1).unwrap();
        assert!(ship.inventory.refine_job.is_none());
        assert_eq!(ship.inventory.cargo[0].as_ref().unwrap().template, 3381);
        assert_eq!(ship.inventory.cargo[0].as_ref().unwrap().quantity, 3);
        assert_eq!(ship.inventory.credits, credits - 450);
        assert_eq!(ship.inventory.trade_xp, 150);
        let after = ship.inventory.clone();
        ship.advance_production(0.1).unwrap();
        assert_eq!(ship.inventory, after);
    }

    #[test]
    fn refinery_wire_uses_private_lab_and_rejects_foreign_owner_space_and_malformed_requests() {
        let mut ship = refinery_ship();
        let lab = ship.workbench_lab_id();
        let create = ship
            .pending
            .iter()
            .find(|frame| frame.opcode == 4 && frame.body[..4] == lab.to_le_bytes())
            .unwrap();
        assert_eq!(&create.body[8..11], &[0xff, 0xff, 0x27]);
        let frame = ship
            .pending
            .iter()
            .rev()
            .find(|frame| {
                frame.opcode == 0x1b
                    && frame.body[..4] == lab.to_le_bytes()
                    && frame.body[7] & 4 != 0
            })
            .unwrap();
        // Native ManufacturingIndex has 21 two-bit fields: six flag bytes.
        let flags_size = (4usize + 21 * 2).div_ceil(8);
        let mut reader = Reader::new(&frame.body[7 + flags_size..]);
        let length = reader.u16().unwrap() as usize;
        assert_eq!(reader.take(length).unwrap(), b"Manufacturing Lab");
        assert_eq!(reader.u32().unwrap(), 4); // workbench mode
        assert_eq!(reader.u32().unwrap(), 13); // valid ready operation
        let original = ship.inventory.clone();
        assert!(ship
            .workbench_input(
                0x7e,
                &[(ship.player_id() + 1).to_be_bytes(), 2u32.to_be_bytes()].concat()
            )
            .is_err());
        assert!(ship.workbench_input(0x79, &[0; 7]).is_err());
        assert!(ship
            .workbench_input(
                0x80,
                &[
                    ship.player_id().to_be_bytes().as_slice(),
                    &[1],
                    &0x200u32.to_be_bytes()
                ]
                .concat()
            )
            .is_err());
        ship.sector = ship.sectors.sector(1076).unwrap();
        assert!(ship
            .workbench_input(
                0x79,
                &[ship.player_id().to_be_bytes(), 4u32.to_be_bytes()].concat()
            )
            .is_err());
        assert_eq!(ship.inventory, original);
    }

    #[test]
    fn refinery_restart_uses_inventory_job_and_rejects_replayed_request_after_completion() {
        let mut ship = refinery_ship();
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22092".parse().unwrap());
        start_refine(&mut ship, 100, false);
        make_refine_due(&mut ship);
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(restored.session_epoch, ship.session_epoch);
        restored.advance_production(0.1).unwrap();
        let after = restored.inventory.clone();
        let mut repeated =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(repeated.inventory.refine_job.is_none());
        assert_eq!(repeated.inventory, after);
        repeated.inputs.push_back(100);
        assert!(repeated
            .workbench_input(
                0x7e,
                &[repeated.player_id().to_be_bytes(), 2u32.to_be_bytes()].concat()
            )
            .is_err());
        assert!(repeated.inventory.refine_job.is_none());
        start_refine(&mut repeated, 101, false);
        assert!(repeated.inventory.refine_job.is_some());
    }

    #[test]
    fn refinery_cancellation_missing_materials_and_disk_failure_do_not_consume_or_duplicate() {
        let mut ship = refinery_ship();
        let original = ship.inventory.clone();
        start_refine(&mut ship, 1, false);
        ship.workbench_input(
            0x7e,
            &[ship.player_id().to_be_bytes(), 0u32.to_be_bytes()].concat(),
        )
        .unwrap();
        assert!(ship.inventory.refine_job.is_none());
        assert_eq!(ship.inventory.cargo, original.cargo);
        assert_eq!(ship.inventory.credits, original.credits);
        ship.workbench_input(
            0x79,
            &[ship.player_id().to_be_bytes(), 4u32.to_be_bytes()].concat(),
        )
        .unwrap();
        ship.workbench_input(
            0x7c,
            &[ship.player_id().to_be_bytes(), 3381u32.to_be_bytes()].concat(),
        )
        .unwrap();
        start_refine(&mut ship, 2, false);
        make_refine_due(&mut ship);
        let before = ship.inventory.clone();
        let directory = ship.state_dir.clone();
        let blocked = directory.join("blocked");
        fs::write(&blocked, b"file not directory").unwrap();
        ship.state_dir = blocked;
        assert!(ship.advance_production(0.1).is_err());
        assert_eq!(ship.inventory, before);
        ship.state_dir = directory;
        ship.advance_production(0.1).unwrap();
        assert_eq!(ship.inventory.cargo[0].as_ref().unwrap().quantity, 10);
        start_refine(&mut ship, 3, false);
        make_refine_due(&mut ship);
        ship.inventory.cargo[0] = None; // Simulate a separate allowed cargo mutation.
        let credits = ship.inventory.credits;
        ship.advance_production(0.1).unwrap();
        assert!(ship.inventory.refine_job.is_none());
        assert_eq!(ship.inventory.credits, credits);
        let mut candidate = ship.inventory.clone();
        candidate.cargo[0] = Some(crate::inventory::ItemInstance {
            template: 2845,
            quantity: 5,
            quality: 1.,
            structure: 1.,
            builder_name: String::new(),
        });
        ship.commit_inventory(candidate).unwrap();
        start_refine(&mut ship, 4, false);
        make_refine_due(&mut ship);
        let mut candidate = ship.inventory.clone();
        candidate.refine_job.as_mut().unwrap().product = 769; // Item exists, recipe was removed/changed.
        candidate.refine_job.as_mut().unwrap().recipe = None;
        ship.commit_inventory(candidate).unwrap();
        ship.advance_production(0.1).unwrap();
        assert!(ship.inventory.refine_job.is_none());
        assert_eq!(ship.inventory.cargo[0].as_ref().unwrap().quantity, 5);
        assert_eq!(ship.inventory.credits, credits);
        start_refine(&mut ship, 5, false);
        make_refine_due(&mut ship);
        let mut candidate = ship.inventory.clone();
        candidate
            .refine_job
            .as_mut()
            .unwrap()
            .recipe
            .as_mut()
            .unwrap()
            .input_quantity = 6;
        ship.commit_inventory(candidate).unwrap();
        ship.advance_production(0.1).unwrap();
        assert!(ship.inventory.refine_job.is_none());
        assert_eq!(ship.inventory.cargo[0].as_ref().unwrap().quantity, 5);
        assert_eq!(ship.inventory.credits, credits);
    }

    fn looting_ship() -> PlayerSession {
        let mut ship = session();
        ship.logged_in = true;
        let catalog = crate::loot::LootCatalog::from_mobs(
            vec![crate::loot::MobDrops {
                id: 99,
                kind: Some(1),
                drops: vec![crate::loot::DropEntry {
                    source_id: 1,
                    item: 2837,
                    chance_percent: 100.,
                    quantity: 1,
                    kind: 0,
                    usage_chance: 0,
                }],
            }],
            &ship.items,
        )
        .unwrap();
        let id = lock_loot(&ship.loot_world)
            .unwrap()
            .spawn(
                crate::loot_world::CorpseBirth {
                    death: crate::loot_world::MobDeath {
                        spawn: 999,
                        generation: 1,
                    },
                    template: 99,
                    name: "Test drone".into(),
                    level: 1,
                    killer: ship.player_id(),
                    position: [ship.position[0] + 1000., ship.position[1], ship.position[2]],
                    now_ms: crate::resources::unix_ms(),
                },
                &catalog,
                &ship.items,
            )
            .unwrap()
            .unwrap();
        ship.publish_corpses().unwrap();
        ship.target = id;
        ship
    }

    #[test]
    fn corpse_credit_permission_tractor_and_depletion_commit_once() {
        let mut ship = looting_ship();
        let corpse = ship.target;
        let credits = ship.inventory.credits;
        ship.open_loot().unwrap();
        assert!(ship.inventory.credits >= credits + 20);
        let after_open = ship.inventory.clone();
        ship.open_loot().unwrap();
        assert_eq!(ship.inventory, after_open);
        assert!(ship
            .pending
            .iter()
            .any(|frame| frame.opcode == 0x8c && frame.body == corpse.to_be_bytes()));
        ship.begin_looting(0).unwrap();
        assert!(ship.pending.iter().any(|frame| frame.opcode == 0x2014));
        assert!(ship.inventory.cargo.iter().all(Option::is_none));
        ship.advance_looting(100.).unwrap();
        assert!(ship.loot_cast.is_none());
        assert_eq!(ship.inventory.cargo[0].as_ref().unwrap().template, 2837);
        assert_eq!(ship.inventory.cargo[0].as_ref().unwrap().quantity, 1);
        ship.last_loot_publish = None;
        ship.publish_corpses().unwrap();
        assert!(!ship.visible_corpses.contains_key(&corpse));
        assert_eq!(ship.loot_window, None);
        assert!(ship.begin_looting(0).is_err());
    }

    #[test]
    fn loot_claim_is_shared_and_private_open_checks_current_killer() {
        let mut ship = looting_ship();
        let mut other = session();
        other.account.character.game_id += 1;
        other.logged_in = true;
        other
            .attach_loot_world(Arc::clone(&ship.loot_world))
            .unwrap();
        other.group_contacts.push(GroupContact {
            id: ship.player_id(),
            sector: ship.sector.id,
            position: ship.position,
            incapacitated: false,
        });
        other.publish_corpses().unwrap();
        other.target = ship.target;
        assert!(other.open_loot().is_err()); // Default group policy retains killer lock.
        ship.open_loot().unwrap(); // The archived opening releases private ownership.
        other.open_loot().unwrap();
        ship.begin_looting(0).unwrap();
        assert!(other.begin_looting(0).is_err());
        ship.cancel_looting("Test cancellation.").unwrap();
        other.begin_looting(0).unwrap();
        other.advance_looting(100.).unwrap();
        assert!(ship.inventory.cargo.iter().all(Option::is_none));
        assert_eq!(other.inventory.cargo[0].as_ref().unwrap().quantity, 1);
    }

    #[test]
    fn loot_restart_resumes_and_discards_already_committed_cast_without_duplicate_award() {
        let mut ship = looting_ship();
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22091".parse().unwrap());
        ship.open_loot().unwrap();
        ship.begin_looting(0).unwrap();
        ship.advance_looting(0.1).unwrap();
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(restored.loot_cast.is_some());
        restored.advance_looting(100.).unwrap();
        let inventory = restored.inventory.clone();
        let repeated =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(repeated.loot_cast.is_none());
        assert_eq!(repeated.inventory, inventory);
        assert!(!lock_loot(&repeated.loot_world)
            .unwrap()
            .get(ship.target)
            .unwrap()
            .available(crate::resources::unix_ms())
            .unwrap());
    }

    #[test]
    fn equipped_tractor_range_and_speed_gate_real_shared_loot_across_replacement() {
        let mut ship = looting_ship();
        let corpse = ship.target;
        let corpse_position = lock_loot(&ship.loot_world)
            .unwrap()
            .get(corpse)
            .unwrap()
            .position;
        ship.position[0] = corpse_position[0] - 2400.;
        let range = lock_loot(&ship.loot_world)
            .unwrap()
            .get(corpse)
            .unwrap()
            .range_to(ship.position)
            .unwrap();
        assert!(range > 2000. && range < 2640.);
        ship.open_loot().unwrap();
        assert!(ship.begin_looting(0).is_err());
        let mut candidate = ship.inventory.clone();
        candidate.equipped[3] = Some(crate::inventory::ItemInstance::stock(813, 1));
        candidate.equipped[9] = Some(crate::inventory::ItemInstance::stock(1758, 1));
        ship.commit_inventory(candidate).unwrap();
        ship.begin_looting(0).unwrap();
        let expected_duration =
            (distance(ship.position, corpse_position) / ship.equipment.ship.tractor_speed - 0.15)
                .max(0.1);
        let tractor = ship
            .pending
            .iter()
            .rev()
            .find(|frame| frame.opcode == 0x2014)
            .unwrap();
        let name_length = u16::from_le_bytes(tractor.body[18..20].try_into().unwrap()) as usize;
        let duration_at = 20 + name_length;
        let milliseconds = u32::from_le_bytes(
            tractor.body[duration_at..duration_at + 4]
                .try_into()
                .unwrap(),
        );
        assert!((milliseconds as f32 / 1000. - expected_duration).abs() < 0.001);

        let mut other = session();
        other.account.character.game_id += 1;
        other.logged_in = true;
        other
            .attach_loot_world(Arc::clone(&ship.loot_world))
            .unwrap();
        other.publish_corpses().unwrap();
        other.target = corpse;
        assert!(other.open_loot().is_err());

        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22091".parse().unwrap());
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(
            restored.equipment.ship.tractor_speed,
            ship.equipment.ship.tractor_speed
        );
        assert_eq!(
            restored.equipment.ship.tractor_range,
            ship.equipment.ship.tractor_range
        );
        assert!(restored.loot_cast.is_some());
        restored.advance_looting(100.).unwrap();
        assert_eq!(restored.inventory.cargo[0].as_ref().unwrap().template, 2837);
        assert!(other.inventory.cargo.iter().all(Option::is_none));
    }

    #[test]
    fn received_scan_buff_expires_stats_icon_and_object_effect() {
        let mut ship = session();
        ship.logged_in = true;
        let base = ship.equipment.ship.scan_range;
        let buff = crate::peer_buffs::PeerBuff {
            revision: 1,
            source: ship.player_id() + 1,
            target_epoch: ship.session_epoch,
            template: 1813,
            effect: crate::peer_buffs::FriendlyEffect::ScanBoost,
            value: 25.,
            operation: 2,
            expires_at_ms: crate::resources::unix_ms() + 10_000,
        };
        let effect_id = buff.visual_id(ship.player_id());
        ship.apply_peer_buff(buff).unwrap();
        assert!(ship.equipment.ship.scan_range > base);
        assert!(ship.pending.iter().any(|frame| frame.opcode == 9));
        assert!(ship.pending.iter().any(|frame| {
            frame.opcode == 0x1b
                && frame
                    .body
                    .windows(b"Increase Scan Range".len())
                    .any(|bytes| bytes == b"Increase Scan Range")
        }));
        ship.pending.clear();
        ship.publish_peer_buff_resume();
        assert!(ship.pending.iter().any(|frame| frame.opcode == 9));
        ship.peer_buffs[0].expires_at_ms = crate::resources::unix_ms() - 1;
        ship.advance_peer_buff().unwrap();
        assert!(ship.peer_buffs.is_empty());
        assert_eq!(ship.equipment.ship.scan_range, base);
        assert!(ship
            .pending
            .iter()
            .any(|frame| { frame.opcode == 0x0f && frame.body == effect_id.to_le_bytes() }));
    }

    #[test]
    fn received_recharge_buffs_change_regeneration_and_clear_on_expiry() {
        for (template, kind, value, operation, buff_name) in [
            (
                1805,
                crate::peer_buffs::FriendlyEffect::RechargeShields,
                10.,
                2,
                "Regenerate Shield",
            ),
            (
                2172,
                crate::peer_buffs::FriendlyEffect::RechargeReactor,
                30.,
                2,
                "Regenerate Energy",
            ),
        ] {
            let mut ship = session();
            ship.logged_in = true;
            let base = ship.equipment.ship;
            let buff = crate::peer_buffs::PeerBuff {
                revision: 1,
                source: ship.player_id() + 1,
                target_epoch: ship.session_epoch,
                template,
                effect: kind,
                value,
                operation,
                expires_at_ms: crate::resources::unix_ms() + 10_000,
            };
            let visual_id = buff.visual_id(ship.player_id());
            ship.apply_peer_buff(buff).unwrap();
            assert!(ship.pending.iter().any(|frame| frame.opcode == 9));
            assert!(ship.pending.iter().any(|frame| frame.opcode == 0x1b
                && frame
                    .body
                    .windows(buff_name.len())
                    .any(|bytes| bytes == buff_name.as_bytes())));
            ship.energy = 0.;
            ship.shield = 0.;
            ship.advance_ship_resources(1.);
            match kind {
                crate::peer_buffs::FriendlyEffect::RechargeShields => {
                    assert!(ship.equipment.ship.shield_recharge > base.shield_recharge);
                    assert_eq!(ship.shield, ship.equipment.ship.shield_recharge);
                }
                crate::peer_buffs::FriendlyEffect::RechargeReactor => {
                    assert!(ship.equipment.ship.energy_recharge > base.energy_recharge);
                    assert_eq!(ship.energy, ship.equipment.ship.energy_recharge);
                }
                _ => unreachable!(),
            }
            ship.peer_buffs[0].expires_at_ms = crate::resources::unix_ms() - 1;
            ship.advance_peer_buff().unwrap();
            assert!(ship.peer_buffs.is_empty());
            assert_eq!(ship.equipment.ship.energy_recharge, base.energy_recharge);
            assert_eq!(ship.equipment.ship.shield_recharge, base.shield_recharge);
            assert!(ship
                .pending
                .iter()
                .any(|frame| frame.opcode == 0x0f && frame.body == visual_id.to_le_bytes()));
        }
    }

    /// Expiring one ally status clears only its native slot and stat.
    #[test]
    fn concurrent_peer_buffs_expire_one_slot_without_erasing_the_other() {
        let mut ship = session();
        ship.logged_in = true;
        let base = ship.equipment.ship;
        let now = crate::resources::unix_ms();
        for (revision, template, effect, value) in [
            (1, 1813, crate::peer_buffs::FriendlyEffect::ScanBoost, 25.),
            (
                2,
                1805,
                crate::peer_buffs::FriendlyEffect::RechargeShields,
                10.,
            ),
            (
                3,
                2172,
                crate::peer_buffs::FriendlyEffect::RechargeReactor,
                30.,
            ),
        ] {
            ship.apply_peer_buff(crate::peer_buffs::PeerBuff {
                revision,
                source: ship.player_id() + 1,
                target_epoch: ship.session_epoch,
                template,
                effect,
                value,
                operation: 2,
                expires_at_ms: now + 20_000,
            })
            .unwrap();
        }
        assert!(ship.equipment.ship.scan_range > base.scan_range);
        assert!(ship.equipment.ship.shield_recharge > base.shield_recharge);
        assert!(ship.equipment.ship.energy_recharge > base.energy_recharge);
        assert_eq!(ship.peer_buffs.len(), 3);
        ship.pending.clear();
        ship.publish_peer_buff_resume();
        assert_eq!(
            ship.pending
                .iter()
                .filter(|frame| frame.opcode == 9)
                .count(),
            3
        );
        ship.pending.clear();
        ship.peer_buffs[0].expires_at_ms = now - 1;
        ship.advance_peer_buff().unwrap();
        assert_eq!(ship.peer_buffs.len(), 2);
        assert!(ship
            .peer_buffs
            .iter()
            .all(|buff| { buff.effect != crate::peer_buffs::FriendlyEffect::ScanBoost }));
        assert_eq!(ship.equipment.ship.scan_range, base.scan_range);
        assert!(ship.equipment.ship.shield_recharge > base.shield_recharge);
        assert!(ship.equipment.ship.energy_recharge > base.energy_recharge);
        assert!(ship
            .pending
            .contains(&packets::ship_active_buff_slot(ship.player_id(), 1, "", 0)));
        assert!(!ship.pending.contains(&packets::ship_active_buff_slot(
            ship.player_id(),
            2,
            "",
            0
        )));
        assert!(!ship.pending.contains(&packets::ship_active_buff_slot(
            ship.player_id(),
            3,
            "",
            0
        )));
    }

    /// A targeted signature reduction changes MOB detection and clears only its own icon.
    #[test]
    fn reduce_signature_peer_buff_changes_threat_until_expiry() {
        let mut ship = session();
        ship.logged_in = true;
        let original = ship.threat().signature;
        let buff = crate::peer_buffs::PeerBuff {
            revision: 1,
            source: ship.player_id() + 1,
            target_epoch: ship.session_epoch,
            template: 1945,
            effect: crate::peer_buffs::FriendlyEffect::ReduceSignature,
            value: 25.,
            operation: 4,
            expires_at_ms: crate::resources::unix_ms() + 10_000,
        };
        ship.apply_peer_buff(buff).unwrap();
        assert_eq!(ship.threat().signature, original * 0.75);
        assert!(ship.pending.iter().any(|frame| frame.opcode == 0x1b
            && frame
                .body
                .windows(16)
                .any(|bytes| bytes == b"Reduce Signature")));
        assert!(!ship.pending.iter().any(|frame| frame.opcode == 9));
        ship.pending.clear();
        ship.peer_buffs[0].expires_at_ms = crate::resources::unix_ms() - 1;
        ship.advance_peer_buff().unwrap();
        assert_eq!(ship.threat().signature, original);
        assert!(ship
            .pending
            .contains(&packets::ship_active_buff_slot(ship.player_id(), 4, "", 0)));
        assert!(!ship.pending.iter().any(|frame| frame.opcode == 0x0f));
    }

    /// Rank-one Rally raises actual weapon critical odds and clears on expiry.
    #[test]
    fn rally_damage_tactics_changes_weapon_roll_and_native_slot_five() {
        let mut ship = session();
        ship.logged_in = true;
        let baseline = ship.equipment.weapons[0].hit_chances;
        let target_epoch = ship.session_epoch;
        let id = ship.player_id();
        ship.apply_rally_buff(crate::rally::RallyBuff {
            revision: 1,
            source: id + 1,
            target_epoch,
            tactic: crate::rally::Tactic::Damage,
            skill_rank: 1,
            expires_at_ms: crate::resources::unix_ms() + 10_000,
        })
        .unwrap();
        assert_ne!(ship.equipment.weapons[0].hit_chances, baseline);
        assert_eq!(
            crate::weapon_accuracy::roll_weapon_damage(
                10.,
                9000,
                ship.equipment.weapons[0].hit_chances
            )
            .unwrap()
            .outcome,
            crate::weapon_accuracy::HitOutcome::Critical
        );
        assert!(ship.pending.iter().any(|frame| frame.opcode == 0x1b
            && frame
                .body
                .windows(b"Damage Tactics".len())
                .any(|part| part == b"Damage Tactics")));
        ship.pending.clear();
        ship.rally_buff.as_mut().unwrap().expires_at_ms = crate::resources::unix_ms() - 1;
        ship.advance_rally_buff().unwrap();
        assert_eq!(ship.equipment.weapons[0].hit_chances, baseline);
        assert!(ship
            .pending
            .contains(&packets::ship_active_buff_slot(id, 5, "", 0)));
    }

    #[test]
    fn loot_cancel_and_inventory_write_failure_preserve_stock_and_release_claim() {
        let mut ship = looting_ship();
        ship.open_loot().unwrap();
        let before = ship.inventory.clone();
        ship.begin_looting(0).unwrap();
        ship.position[0] += 10_000.;
        ship.advance_looting(0.1).unwrap();
        assert!(ship.loot_cast.is_none());
        ship.position[0] -= 10_000.;
        ship.begin_looting(0).unwrap();
        let blocked = ship.state_dir.join("blocked");
        fs::write(&blocked, b"file, not directory").unwrap();
        ship.state_dir = blocked;
        ship.advance_looting(100.).unwrap();
        assert_eq!(ship.inventory, before);
        assert!(ship.loot_cast.is_none());
        assert!(lock_loot(&ship.loot_world)
            .unwrap()
            .get(ship.target)
            .unwrap()
            .remaining()
            .unwrap()
            .items[0]
            .is_some());
        ship.begin_looting(0).unwrap();
        ship.cancel_looting("Cleanup.").unwrap();
    }

    fn mining_ship() -> PlayerSession {
        let mut ship = session();
        let mut object = ship.sector.navpoints[0].clone();
        object.id = 0x20000001;
        object.object_type = 38;
        object.position = [100., 0., 0.];
        object.name = "Asteroid (L1)".into();
        object.asset = 1822;
        ship.sector.resources = vec![crate::content::ResourceNode {
            object,
            field_id: 1,
            level: 1,
            resource_type: 2,
            ore_items: vec![crate::content::ResourceOre {
                item_id: 2837,
                frequency: 1.,
            }],
        }];
        ship.resource_field = Arc::new(Mutex::new(
            ResourceField::load(
                &ship.sector,
                &ship.items,
                ship.state_dir.join("resources/1076.json"),
            )
            .unwrap(),
        ));
        ship.logged_in = true;
        ship.target = 0x20000001;
        ship.open_resource_window(ship.target).unwrap();
        ship
    }

    fn mine_slot(ship: &mut PlayerSession, slot: usize) -> io::Result<()> {
        ship.begin_mining(ItemTransfer {
            source: 18,
            source_slot: slot,
            destination: 0,
            destination_slot: 0,
            quantity: 0,
        })
    }

    #[test]
    fn public_prospect_device_changes_actual_reactor_admission() {
        let mut ordinary = mining_ship();
        let mut equipped = mining_ship();
        for ship in [&mut ordinary, &mut equipped] {
            ship.skill_catalog
                .skills
                .iter_mut()
                .find(|skill| skill.id == 41)
                .unwrap()
                .level = 1;
        }
        let mut candidate = equipped.inventory.clone();
        candidate.equipped[9] = Some(crate::inventory::ItemInstance::stock(1760, 1));
        equipped.commit_inventory(candidate).unwrap();
        let level = {
            let field = lock_resources(&ordinary.resource_field).unwrap();
            let ore = field.get(ordinary.target).unwrap().contents[0]
                .as_ref()
                .unwrap();
            ordinary.items.get(ore.template).unwrap().level
        };
        let rank = ordinary.skill_catalog.level(41) as f32;
        let cost = |bonus: f32| {
            (42.5 * 2f32.powi(i32::from(level)) / 2f32.powf(rank + bonus - 1.)).max(10.)
        };
        let between = (cost(0.) + cost(equipped.equipment.ship.prospect_bonus)) / 2.;
        ordinary.energy = between;
        equipped.energy = between;
        assert!(mine_slot(&mut ordinary, 0).is_err());
        mine_slot(&mut equipped, 0).unwrap();
        assert!(equipped.mining.is_some());
    }

    #[test]
    fn mining_drains_energy_claims_exclusively_and_commits_cargo_with_depletion_and_xp() {
        let mut ship = mining_ship();
        let mut other = mining_ship();
        other.account.character.game_id += 1;
        other
            .attach_resource_field(Arc::clone(&ship.resource_field))
            .unwrap();
        let quantity = lock_resources(&ship.resource_field)
            .unwrap()
            .get(ship.target)
            .unwrap()
            .contents[0]
            .as_ref()
            .unwrap()
            .quantity;
        let initial_energy = ship.energy;
        mine_slot(&mut ship, 0).unwrap();
        assert!(mine_slot(&mut other, 0).is_err());
        ship.advance_mining(0.1).unwrap();
        assert!(ship.energy < initial_energy);
        for _ in 0..100 {
            ship.advance_mining(0.25).unwrap();
        }
        assert!(ship.mining.is_none());
        assert_eq!(
            ship.inventory
                .cargo
                .iter()
                .flatten()
                .filter(|item| item.template == 2837)
                .map(|item| item.quantity)
                .sum::<u32>(),
            quantity
        );
        assert_eq!(
            ship.inventory.harvests[&ship.target].quantities[0],
            quantity
        );
        assert!(ship.inventory.explore_xp > 0);
        assert!(lock_resources(&ship.resource_field)
            .unwrap()
            .get(ship.target)
            .unwrap()
            .contents[0]
            .is_none());
        let saved: Inventory =
            serde_json::from_slice(&fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved, ship.inventory);
        assert!(mine_slot(&mut other, 0).is_err()); // no double award from another client
    }

    #[test]
    fn mining_freezes_group_explore_award_with_the_ore_receipt() {
        let mut ship = mining_ship();
        ship.explore_group_members = vec![ship.player_id() + 1];
        mine_slot(&mut ship, 0).unwrap();
        let cast = ship.mining.clone().unwrap();
        let base = crate::experience::mining_xp(
            u32::from(ship.items.get(cast.template).unwrap().level),
            cast.quantity,
            50,
        );
        for _ in 0..100 {
            ship.advance_mining(0.25).unwrap();
        }
        let award = &ship.inventory.pending_explore_awards[&1];
        assert_eq!(award.recipients, vec![ship.player_id() + 1]);
        assert_eq!(
            award.amount,
            crate::player_session::exploration::group_explore_amount(base, 2)
        );
        assert_eq!(ship.inventory.explore_xp, award.amount);
        assert_eq!(
            ship.inventory.harvests[&ship.target].quantities[cast.slot],
            cast.quantity
        );
        let saved: Inventory =
            serde_json::from_slice(&fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(
            saved.pending_explore_awards,
            ship.inventory.pending_explore_awards
        );
    }

    #[test]
    fn mining_xp_uses_earned_explore_level_instead_of_sandbox_fifty() {
        let mut ship = mining_ship();
        ship.inventory.xp_start_level = 0;
        mine_slot(&mut ship, 0).unwrap();
        let cast = ship.mining.clone().unwrap();
        let ore_level = u32::from(ship.items.get(cast.template).unwrap().level);
        for _ in 0..100 {
            ship.advance_mining(0.25).unwrap();
        }
        assert_eq!(
            ship.inventory.explore_xp,
            crate::experience::mining_xp(ore_level, cast.quantity, 0)
        );
    }

    #[test]
    fn mining_restart_resumes_then_rejects_a_stale_cast_after_durable_collection() {
        let mut ship = mining_ship();
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22090".parse().unwrap());
        mine_slot(&mut ship, 0).unwrap();
        ship.advance_mining(0.1).unwrap();
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(restored.mining.is_some());
        for _ in 0..100 {
            restored.advance_mining(0.25).unwrap();
        }
        let collected = restored.inventory.clone();
        assert!(collected.harvests[&ship.target].quantities[0] > 0);
        // Keep the OLD transport checkpoint, simulating a crash immediately
        // after inventory replacement but before the next session checkpoint.
        let repeated =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(repeated.mining.is_none());
        assert_eq!(repeated.inventory, collected);
        assert!(lock_resources(&repeated.resource_field)
            .unwrap()
            .get(ship.target)
            .unwrap()
            .contents[0]
            .is_none());
    }

    #[test]
    fn prospect_overlaps_prior_tractor_on_same_node_and_recovers_both_awards() {
        let mut ship = mining_ship();
        let chosen = (0x20000001..0x20000100)
            .find_map(|id| {
                let mut sector = ship.sector.clone();
                sector.resources[0].object.id = id;
                let field = ResourceField::load(
                    &sector,
                    &ship.items,
                    ship.state_dir.join("resources/1076.json"),
                )
                .ok()?;
                (field.get(id).ok()?.contents.iter().flatten().count() >= 2)
                    .then_some((sector, field, id))
            })
            .unwrap();
        ship.sector = chosen.0;
        ship.resource_field = Arc::new(Mutex::new(chosen.1));
        ship.target = chosen.2;
        ship.open_resource_window(ship.target).unwrap();
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22091".parse().unwrap());
        let quantities = {
            let field = lock_resources(&ship.resource_field).unwrap();
            let node = field.get(ship.target).unwrap();
            [
                node.contents[0].as_ref().unwrap().quantity,
                node.contents[1].as_ref().unwrap().quantity,
            ]
        };
        mine_slot(&mut ship, 0).unwrap();
        ship.advance_mining(100.).unwrap();
        assert!(ship.mining.is_none());
        assert!(ship.tractor_mining.is_some());
        assert!(ship.inventory.pending_mining.is_some());
        let tractor_effect = ship.tractor_mining.as_ref().unwrap().effect;
        assert!(ship
            .harvest_visuals
            .iter()
            .any(|frame| frame.frame.opcode == 0x2013
                && frame.frame.body[8..12] == tractor_effect.to_le_bytes()));
        assert_eq!(
            ship.inventory.harvests[&ship.target].quantities[0],
            quantities[0]
        );
        mine_slot(&mut ship, 1).unwrap();
        assert!(ship.mining.is_some() && ship.tractor_mining.is_some());
        assert!(
            ship.mining.as_ref().unwrap().remaining
                >= ship.tractor_mining.as_ref().unwrap().remaining + 0.099
        );

        let mut other = mining_ship();
        other.account.character.game_id += 1;
        other.sector = ship.sector.clone();
        other.target = ship.target;
        other
            .attach_resource_field(Arc::clone(&ship.resource_field))
            .unwrap();
        other.open_resource_window(other.target).unwrap();
        assert!(mine_slot(&mut other, 1).is_err());

        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(restored.mining.is_some() && restored.tractor_mining.is_some());
        let replay = restored
            .harvest_visuals
            .iter()
            .map(|frame| frame.frame.opcode)
            .collect::<Vec<_>>();
        assert!(
            replay.iter().position(|opcode| *opcode == 0x2013).unwrap()
                < replay.iter().position(|opcode| *opcode == 0x2012).unwrap()
        );
        for _ in 0..100 {
            restored.advance_mining(0.25).unwrap();
        }
        assert!(restored.mining.is_none() && restored.tractor_mining.is_none());
        assert!(restored.inventory.pending_mining.is_none());
        assert_eq!(
            restored.inventory.harvests[&restored.target].quantities[..2],
            quantities
        );
        let ore: u32 = restored
            .inventory
            .cargo
            .iter()
            .flatten()
            .map(|item| item.quantity)
            .sum();
        assert_eq!(ore, quantities.iter().sum::<u32>());
        other
            .attach_resource_field(Arc::clone(&restored.resource_field))
            .unwrap();
        assert!(mine_slot(&mut other, 0).is_err());
        assert!(mine_slot(&mut other, 1).is_err());
    }

    #[test]
    fn mining_escrow_recovers_after_inventory_rename_beats_phase_checkpoint() {
        let mut ship = mining_ship();
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22092".parse().unwrap());
        mine_slot(&mut ship, 0).unwrap();
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap(); // Old prospect phase stays on disk.
        ship.advance_mining(100.).unwrap();
        let earned = ship.inventory.explore_xp;
        let quantity = ship
            .inventory
            .pending_mining
            .as_ref()
            .unwrap()
            .item
            .quantity;
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(restored.mining.is_none());
        assert!(restored.tractor_mining.is_some());
        restored.advance_mining(100.).unwrap();
        assert!(restored.inventory.pending_mining.is_none());
        assert_eq!(restored.inventory.explore_xp, earned);
        assert_eq!(
            restored
                .inventory
                .cargo
                .iter()
                .flatten()
                .map(|item| item.quantity)
                .sum::<u32>(),
            quantity
        );
        let repeated =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(repeated.mining.is_none() && repeated.tractor_mining.is_none());
        assert_eq!(repeated.inventory.explore_xp, earned);
        assert_eq!(
            repeated
                .inventory
                .cargo
                .iter()
                .flatten()
                .map(|item| item.quantity)
                .sum::<u32>(),
            quantity
        );
    }

    #[test]
    fn cancelling_new_prospect_keeps_previous_tractor_owned_and_deliverable() {
        let mut ship = mining_ship();
        mine_slot(&mut ship, 0).unwrap();
        ship.advance_mining(100.).unwrap();
        let quantity = ship
            .inventory
            .pending_mining
            .as_ref()
            .unwrap()
            .item
            .quantity;
        assert!(ship.tractor_mining.is_some());
        ship.cancel_mining("Target changed.").unwrap();
        assert!(ship.tractor_mining.is_some());
        ship.advance_mining(100.).unwrap();
        assert!(ship.tractor_mining.is_none());
        assert!(ship.inventory.pending_mining.is_none());
        assert_eq!(
            ship.inventory
                .cargo
                .iter()
                .flatten()
                .map(|item| item.quantity)
                .sum::<u32>(),
            quantity
        );
    }

    #[test]
    fn mining_cancellation_and_disk_failure_keep_committed_ore_in_escrow() {
        let mut ship = mining_ship();
        let before = ship.inventory.clone();
        mine_slot(&mut ship, 0).unwrap();
        ship.motion.set_speed = 10.;
        ship.advance_mining(0.1).unwrap();
        assert!(ship.mining.is_none());
        assert_eq!(ship.inventory, before);
        ship.motion.set_speed = 0.;
        mine_slot(&mut ship, 0).unwrap();
        ship.advance_mining(100.).unwrap(); // enter tractor phase
        fs::create_dir_all(&ship.state_dir).unwrap();
        let original_dir = ship.state_dir.clone();
        let blocked = ship.state_dir.join("blocked");
        fs::write(&blocked, b"file, not directory").unwrap();
        ship.state_dir = blocked;
        ship.advance_mining(100.).unwrap();
        assert!(ship.mining.is_none());
        assert!(ship.inventory.pending_mining.is_some());
        assert_eq!(ship.inventory.harvests[&ship.target].quantities[0], 4);
        assert!(lock_resources(&ship.resource_field)
            .unwrap()
            .get(ship.target)
            .unwrap()
            .contents[0]
            .is_none());
        ship.state_dir = original_dir;
        ship.advance_mining(0.).unwrap();
        assert!(ship.inventory.pending_mining.is_none());
        assert_eq!(
            ship.inventory
                .cargo
                .iter()
                .flatten()
                .map(|item| item.quantity)
                .sum::<u32>(),
            4
        );
    }

    #[test]
    fn native_gate_and_dock_verb_ids_enable_in_range_and_refresh_without_selection() {
        let mut ship = session();
        ship.logged_in = true;
        let gate = ship
            .sector
            .navpoints
            .iter()
            .find(|nav| nav.object_type == 11)
            .unwrap()
            .clone();
        ship.target = gate.id;
        ship.position = [
            gate.position[0] - 10_000.,
            gate.position[1],
            gate.position[2],
        ];
        ship.refresh_target_verbs(true);
        assert_eq!(
            ship.pending.last().unwrap(),
            &packets::target_verbs(gate.id, &[(10, 2)])
        );
        ship.position = gate.position;
        ship.advance_player(0.).unwrap();
        assert_eq!(
            ship.pending.last().unwrap(),
            &packets::target_verbs(gate.id, &[(10, 0)])
        );
        ship.position[0] = gate.position[0] + gate.radius + 5000.;
        ship.refresh_target_verbs(true);
        assert_eq!(
            ship.pending.last().unwrap(),
            &packets::target_verbs(gate.id, &[(10, 0)])
        );
        ship.position[0] += 1.;
        ship.refresh_target_verbs(true);
        assert_eq!(
            ship.pending.last().unwrap(),
            &packets::target_verbs(gate.id, &[(10, 2)])
        );
        ship.pending.clear();
        ship.advance_player(0.).unwrap();
        assert!(!ship.pending.iter().any(|frame| frame.opcode == 0x5c));
        ship.sector = ship.sectors.sector(1076).unwrap();
        let station = ship
            .sector
            .navpoints
            .iter()
            .find(|nav| nav.object_type == 12)
            .unwrap()
            .clone();
        ship.target = station.id;
        ship.position = station.position;
        ship.refresh_target_verbs(true);
        assert_eq!(
            ship.pending.last().unwrap(),
            &packets::target_verbs(station.id, &[(8, 0)])
        );
        ship.position[0] = station.position[0] + station.radius + 5000.;
        ship.refresh_target_verbs(true);
        assert_eq!(
            ship.pending.last().unwrap(),
            &packets::target_verbs(station.id, &[(8, 0)])
        );
        ship.position[0] += 1.;
        ship.refresh_target_verbs(true);
        assert_eq!(
            ship.pending.last().unwrap(),
            &packets::target_verbs(station.id, &[(8, 2)])
        );
    }

    #[test]
    fn station_peers_spawn_once_move_on_change_leave_rooms_and_resume_without_duplicates() {
        let mut viewer = session();
        let mut peer = session();
        viewer.sector = viewer.sectors.sector(10761).unwrap();
        peer.sector = viewer.sector.clone();
        viewer.logged_in = true;
        peer.logged_in = true;
        peer.account.character.game_id += 1;
        let pose = StationPose {
            room: 0,
            orientation: 0.5,
            position: [10., 20., 0.],
            action: 1,
        };
        viewer.station_pose = Some(pose.clone());
        peer.station_pose = Some(pose.clone());
        let snapshot = peer.station_snapshot().unwrap();
        viewer.pending.clear();
        viewer.publish_station_peers(std::slice::from_ref(&snapshot));
        assert_eq!(
            viewer
                .pending
                .iter()
                .map(|frame| frame.opcode)
                .collect::<Vec<_>>(),
            [0x61, 0xa0, 0x9e]
        );
        assert_eq!(viewer.station_peers.get(&peer.player_id()), Some(&pose));
        viewer.pending.clear();
        viewer.publish_station_peers(std::slice::from_ref(&snapshot));
        assert!(viewer.pending.is_empty());
        peer.station_pose.as_mut().unwrap().position[0] = 15.;
        let changed = peer.station_snapshot().unwrap();
        viewer.last_station_broadcast = Instant::now() - Duration::from_secs(1);
        viewer.publish_station_peers(std::slice::from_ref(&changed));
        assert_eq!(
            viewer
                .pending
                .iter()
                .map(|frame| frame.opcode)
                .collect::<Vec<_>>(),
            [0x9e]
        );
        viewer.pending.clear();
        viewer.selected_peer = Some("127.0.0.1".parse().unwrap());
        viewer.endpoint = Some("127.0.0.1:22050".parse().unwrap());
        viewer.save().unwrap();
        viewer.checkpoint_session(true).unwrap();
        let mut restored =
            PlayerSession::new(viewer.sector.clone(), viewer.bind, viewer.state_dir.clone())
                .unwrap();
        restored.pending.clear();
        restored.publish_station_peers(std::slice::from_ref(&changed));
        assert!(restored.pending.is_empty());
        peer.station_pose.as_mut().unwrap().room = 1;
        restored.publish_station_peers(&[peer.station_snapshot().unwrap()]);
        assert_eq!(restored.pending.len(), 1);
        assert_eq!(
            restored.pending[0],
            packets::station_avatar_room(peer.player_id(), u32::MAX, 0)
        );
        assert!(restored.station_peers.is_empty());
        restored.pending.clear();
        restored.station_view_dirty = true;
        restored.publish_station_peers(&[changed]);
        assert_eq!(
            restored
                .pending
                .iter()
                .map(|frame| frame.opcode)
                .collect::<Vec<_>>(),
            [0x61, 0xa0, 0x9e]
        );
        let old_ship_position = restored.position;
        restored
            .handle_input(
                0x9f,
                &[
                    restored.player_id().to_le_bytes(),
                    1u32.to_le_bytes(),
                    0u32.to_le_bytes(),
                ]
                .concat(),
            )
            .unwrap();
        assert!(restored.station_pose.is_none()); // await new-room coordinates
        restored.pending.clear();
        restored.publish_station_peers(&[]);
        assert!(restored.station_peers.is_empty());
        assert_eq!(restored.position, old_ship_position);
    }

    fn npc_choice(ship: &mut PlayerSession, choice: u8) -> io::Result<()> {
        ship.talk_choice(&[ship.player_id().to_le_bytes().as_slice(), &[choice]].concat())
    }

    fn enter_npc_room(ship: &mut PlayerSession, npc_id: u32) {
        let room = ship
            .sector
            .station
            .as_ref()
            .unwrap()
            .npcs
            .iter()
            .find(|npc| npc.id == npc_id)
            .unwrap()
            .room;
        ship.station_pose = Some(StationPose {
            room,
            orientation: 0.,
            position: [0.; 3],
            action: 0x41,
        });
    }

    /// Verify native Scout upgrade dialogue, persistence and replay gating.
    #[test]
    fn scout_hull_missions_advance_six_stages_and_resume_without_losing_items() {
        let state_dir =
            std::env::temp_dir().join(format!("beyond-scout-hull-{}", rand::random::<u64>()));
        let mut account =
            crate::account::Account::create("scout".into(), "scout", "Scout".into()).unwrap();
        account.character.race = 0;
        account.character.profession = 2;
        account.save_new(&state_dir).unwrap();
        let items = ItemCatalog::load().unwrap();
        let mut inventory = Inventory::starter_for(&account.character, &items).unwrap();
        inventory.hull_upgrade = 0;
        inventory.cargo_capacity = 25;
        inventory.xp_start_level = 4;
        inventory.cargo[0] = Some(crate::inventory::ItemInstance::stock(2837, 3));
        std::fs::write(
            state_dir.join("inventory.json"),
            serde_json::to_vec(&inventory).unwrap(),
        )
        .unwrap();
        let station = SectorCatalog::load().unwrap().sector(10601).unwrap();
        let mut ship =
            PlayerSession::new(station.clone(), Ipv4Addr::LOCALHOST, state_dir.clone()).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 27);
        for (stage, mission) in [(1, 245), (2, 246), (3, 258), (4, 259), (5, 260), (6, 261)] {
            ship.open_npc(27).unwrap();
            assert_eq!(
                ship.npc_interaction.as_ref().unwrap().mission,
                Some(mission)
            );
            npc_choice(&mut ship, 2).unwrap();
            ship.close_npc();
            ship.open_npc(27).unwrap();
            npc_choice(&mut ship, 2).unwrap();
            assert_eq!(ship.inventory.hull_upgrade, stage);
            assert_eq!(ship.inventory.cargo_capacity, 25 + 2 * stage as usize);
            assert_eq!(ship.inventory.cargo[0], inventory.cargo[0]);
            assert!(ship.inventory.missions.completed.contains(&mission));
            if stage == 1 {
                let mut earned = ship.inventory.clone();
                earned.xp_start_level = 50;
                ship.commit_inventory(earned).unwrap();
                let mut resumed =
                    PlayerSession::new(station.clone(), Ipv4Addr::LOCALHOST, state_dir.clone())
                        .unwrap();
                assert_eq!(resumed.inventory.hull_upgrade, 1);
                assert_eq!(resumed.inventory.cargo[0], inventory.cargo[0]);
                resumed.logged_in = true;
                enter_npc_room(&mut resumed, 27);
                ship = resumed;
            }
        }
        ship.close_npc();
        ship.open_npc(27).unwrap();
        assert!(ship.npc_interaction.as_ref().unwrap().mission.is_none());
        drop(ship);
        std::fs::remove_dir_all(state_dir).unwrap();
    }

    #[test]
    fn earthcorps_npc_mission_acceptance_commits_journal_and_native_aux_once() {
        let mut ship = session();
        ship.account.character.race = 0;
        ship.account.character.profession = 0;
        ship.inventory.xp_start_level = 0;
        ship.sector = ship.sectors.sector(10151).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 46);
        ship.open_npc(46).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(102));
        let blocked = ship.state_dir.join("inventory.json.tmp");
        std::fs::create_dir_all(&ship.state_dir).unwrap();
        std::fs::create_dir(&blocked).unwrap();
        assert!(npc_choice(&mut ship, 2).is_err());
        assert!(ship.inventory.missions.active.is_empty());
        std::fs::remove_dir(blocked).unwrap();
        npc_choice(&mut ship, 2).unwrap();
        assert_eq!(ship.inventory.missions.active[0].id, 102);
        assert_eq!(ship.inventory.missions.active[0].stage, 1);
        assert!(ship.pending.iter().any(|frame| {
            frame.opcode == 0x1b
                && frame
                    .body
                    .windows(b"Basic Shooting Range".len())
                    .any(|window| window == b"Basic Shooting Range")
        }));
        let saved: crate::inventory::Inventory =
            serde_json::from_slice(&std::fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved.missions, ship.inventory.missions);
        assert!(npc_choice(&mut ship, 2).is_err());
        ship.open_npc(46).unwrap();
        assert_ne!(ship.npc_interaction.as_ref().unwrap().mission, Some(102));
    }

    #[test]
    fn mission_mob_kills_share_the_combat_receipt_and_advance_once() {
        let mut ship = session();
        ship.logged_in = true;
        let mut inventory = ship.inventory.clone();
        inventory
            .missions
            .start(102, 1, &crate::missions::MissionCatalog::load().unwrap())
            .unwrap();
        ship.commit_inventory(inventory).unwrap();
        let player = ship.player_id();
        for generation in 1..=3 {
            let award = crate::loot_world::KillAward {
                sector: 1076,
                death: crate::loot_world::MobDeath {
                    spawn: 46,
                    generation,
                },
                killer: player,
                level: 1,
                amount: Some(100),
                source_killer: player,
                template: 1461,
            };
            ship.settle_kill_award(&award).unwrap();
            ship.settle_kill_award(&award).unwrap();
            assert_eq!(
                ship.inventory.missions.active[0].stage,
                if generation == 3 { 2 } else { 1 }
            );
        }
        assert_eq!(ship.inventory.combat_xp, 300);
        assert!(ship.inventory.missions.active[0].counts.is_empty());
        let saved: crate::inventory::Inventory =
            serde_json::from_slice(&std::fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved.missions.active[0].stage, 2);
    }

    #[test]
    fn shooting_range_mission_returns_to_npc_and_commits_final_rewards_once() {
        let mut ship = session();
        ship.account.character.race = 0;
        ship.account.character.profession = 0;
        ship.skill_catalog = SkillCatalog::for_profile(&ship.account.character).unwrap();
        ship.inventory.xp_start_level = 0;
        ship.sector = ship.sectors.sector(10151).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 46);
        ship.open_npc(46).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(102));
        npc_choice(&mut ship, 2).unwrap();
        ship.close_npc();
        let player = ship.player_id();
        for generation in 1..=8 {
            let award = crate::loot_world::KillAward {
                sector: 1015,
                death: crate::loot_world::MobDeath {
                    spawn: 476202,
                    generation,
                },
                killer: player,
                level: 1,
                amount: Some(100),
                source_killer: player,
                template: 1461,
            };
            ship.settle_kill_award(&award).unwrap();
            if generation == 3 {
                assert_eq!(ship.inventory.missions.active[0].stage, 2);
                ship.open_npc(46).unwrap();
                assert_eq!(ship.npc_interaction.as_ref().unwrap().mission_stage, 2);
                for choice in [2, 3, 4] {
                    npc_choice(&mut ship, choice).unwrap();
                }
                assert_eq!(ship.inventory.missions.active[0].stage, 3);
                ship.close_npc();
            }
        }
        assert_eq!(ship.inventory.missions.active[0].stage, 4);
        ship.open_npc(46).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission_stage, 4);
        npc_choice(&mut ship, 2).unwrap();
        assert!(ship.inventory.missions.active.is_empty());
        assert!(ship.inventory.missions.completed.contains(&102));
        assert!(ship.inventory.mission_awarded_skills.contains(&22));
        assert_eq!(ship.inventory.combat_xp, 5800);
        let saved: crate::inventory::Inventory =
            serde_json::from_slice(&std::fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved.missions, ship.inventory.missions);
        assert_eq!(saved.combat_xp, 5800);
        assert!(npc_choice(&mut ship, 2).is_err());
    }

    /// JS combat kills, NPC turn-ins, faction gain and optional permanent refusal persist.
    #[test]
    fn defense_of_sharim_commits_three_kill_stages_and_faction_reward() {
        let mut ship = session();
        ship.account.character.race = 1;
        ship.account.character.profession = 1;
        ship.skill_catalog = SkillCatalog::for_profile(&ship.account.character).unwrap();
        ship.inventory.faction_standings.insert(18, 5000.);
        ship.refresh_equipment().unwrap();
        ship.sector = ship.sectors.sector(10401).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 489);
        let initial_faction = ship.reputation.standing(18);
        ship.open_npc(489).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(81));
        npc_choice(&mut ship, 2).unwrap();
        npc_choice(&mut ship, 3).unwrap();
        assert_eq!(ship.inventory.missions.active[0].stage, 1);
        ship.close_npc();
        let player = ship.player_id();
        let mut generation = 0;
        for (template, count, next_stage) in [(1813, 12, 2), (1815, 6, 4), (1818, 2, 6)] {
            for _ in 0..count {
                generation += 1;
                ship.settle_kill_award(&crate::loot_world::KillAward {
                    sector: 1040,
                    death: crate::loot_world::MobDeath {
                        spawn: 181300,
                        generation,
                    },
                    killer: player,
                    level: 1,
                    amount: Some(100),
                    source_killer: player,
                    template,
                })
                .unwrap();
            }
            assert_eq!(ship.inventory.missions.active[0].stage, next_stage);
            ship.open_npc(489).unwrap();
            if next_stage == 2 {
                npc_choice(&mut ship, 3).unwrap();
                npc_choice(&mut ship, 4).unwrap();
                npc_choice(&mut ship, 5).unwrap();
                assert!(ship.inventory.mission_awarded_skills.contains(&46));
            } else if next_stage == 4 {
                npc_choice(&mut ship, 2).unwrap();
                assert!(ship.inventory.mission_awarded_skills.contains(&24));
                assert!(ship
                    .inventory
                    .cargo
                    .iter()
                    .flatten()
                    .any(|item| item.template == 3644));
            } else {
                npc_choice(&mut ship, 2).unwrap();
                npc_choice(&mut ship, 4).unwrap();
            }
            ship.close_npc();
        }
        assert!(ship.inventory.missions.completed.contains(&81));
        assert!(ship.inventory.missions.active.is_empty());
        assert_eq!(ship.reputation.standing(18), initial_faction + 500.);
        let saved: crate::inventory::Inventory =
            serde_json::from_slice(&std::fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(
            saved.faction_standings.get(&18),
            Some(&(initial_faction + 500.))
        );
        assert!(saved.missions.completed.contains(&81));
        ship.open_npc(489).unwrap();
        assert_ne!(ship.npc_interaction.as_ref().unwrap().mission, Some(81));
    }

    /// Native permanent refusal blocks a later offer without creating an active journal slot.
    #[test]
    fn defense_of_sharim_decline_branch_persists_without_acceptance() {
        let mut ship = session();
        ship.account.character.race = 1;
        ship.account.character.profession = 1;
        ship.skill_catalog = SkillCatalog::for_profile(&ship.account.character).unwrap();
        ship.sector = ship.sectors.sector(10401).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 489);
        ship.open_npc(489).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(81));
        npc_choice(&mut ship, 4).unwrap();
        assert!(ship.inventory.missions.completed.contains(&81));
        assert!(ship.inventory.missions.active.is_empty());
        let saved: crate::inventory::Inventory =
            serde_json::from_slice(&std::fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert!(saved.missions.completed.contains(&81));
        ship.close_npc();
        ship.open_npc(489).unwrap();
        assert_ne!(ship.npc_interaction.as_ref().unwrap().mission, Some(81));
    }

    /// A failed final inventory replacement cannot publish faction or mission rewards.
    #[test]
    fn defense_of_sharim_final_reward_retries_after_disk_failure() {
        let mut ship = session();
        ship.account.character.race = 1;
        ship.account.character.profession = 1;
        ship.skill_catalog = SkillCatalog::for_profile(&ship.account.character).unwrap();
        ship.inventory.faction_standings.insert(18, 5000.);
        ship.refresh_equipment().unwrap();
        ship.sector = ship.sectors.sector(10401).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 489);
        let catalog = crate::missions::MissionCatalog::load().unwrap();
        ship.inventory
            .missions
            .start(81, crate::resources::unix_ms(), &catalog)
            .unwrap();
        ship.inventory.missions.active[0].stage = 6;
        let before = ship.inventory.clone();
        ship.open_npc(489).unwrap();
        npc_choice(&mut ship, 2).unwrap();
        ship.pending.clear();
        let temporary = ship.state_dir.join("inventory.json.tmp");
        std::fs::create_dir_all(&ship.state_dir).unwrap();
        std::fs::create_dir(&temporary).unwrap();
        assert!(npc_choice(&mut ship, 4).is_err());
        assert_eq!(ship.inventory, before);
        assert_eq!(ship.reputation.standing(18), 5000.);
        assert!(ship.pending.is_empty());
        std::fs::remove_dir(temporary).unwrap();
        npc_choice(&mut ship, 4).unwrap();
        assert!(ship.inventory.missions.completed.contains(&81));
        assert_eq!(ship.reputation.standing(18), 5500.);
        assert!(ship
            .pending
            .contains(&packets::reputation(&ship.factions, &ship.reputation)));
    }

    /// The archived -2 talk destination awards and removes each repeatable combat cycle.
    #[test]
    fn scuttle_infestation_reoffers_after_durable_combat_and_faction_rewards() {
        let mut ship = session();
        ship.account.character.race = 0;
        ship.account.character.profession = 0;
        ship.skill_catalog = SkillCatalog::for_profile(&ship.account.character).unwrap();
        ship.inventory.faction_standings.insert(6, 5000.);
        ship.inventory.missions.completed.extend([102, 104, 105]);
        ship.refresh_equipment().unwrap();
        ship.sector = ship.sectors.sector(10151).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 46);
        let initial_credits = ship.inventory.credits;
        let initial_combat = ship.inventory.combat_xp;
        let initial_trade = ship.inventory.trade_xp;
        let initial_explore = ship.inventory.explore_xp;
        let player = ship.player_id();
        let mut generation = 0;
        for cycle in 1..=2 {
            ship.open_npc(46).unwrap();
            assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(303));
            npc_choice(&mut ship, 3).unwrap();
            ship.close_npc();
            for (template, stage) in [(1253, 2), (1254, 3)] {
                for _ in 0..5 {
                    generation += 1;
                    let award = crate::loot_world::KillAward {
                        sector: 1015,
                        death: crate::loot_world::MobDeath {
                            spawn: 125300,
                            generation,
                        },
                        killer: player,
                        level: 1,
                        amount: Some(100),
                        source_killer: player,
                        template,
                    };
                    ship.settle_kill_award(&award).unwrap();
                    ship.settle_kill_award(&award).unwrap();
                }
                assert_eq!(ship.inventory.missions.active[0].stage, stage);
            }
            ship.open_npc(46).unwrap();
            assert_eq!(ship.npc_interaction.as_ref().unwrap().mission_stage, 3);
            if cycle == 2 {
                let before = ship.inventory.clone();
                let temporary = ship.state_dir.join("inventory.json.tmp");
                std::fs::create_dir(&temporary).unwrap();
                assert!(npc_choice(&mut ship, 2).is_err());
                assert_eq!(ship.inventory, before);
                assert_eq!(ship.reputation.standing(6), 5005.);
                std::fs::remove_dir(temporary).unwrap();
            }
            npc_choice(&mut ship, 2).unwrap();
            ship.close_npc();
            assert!(ship.inventory.missions.active.is_empty());
            assert!(!ship.inventory.missions.completed.contains(&303));
            assert_eq!(ship.reputation.standing(6), 5000. + 5. * cycle as f32);
            assert_eq!(ship.inventory.credits, initial_credits + 3000 * cycle);
        }
        assert_eq!(
            ship.inventory.combat_xp,
            initial_combat + 20 * 100 + 2 * 1500
        );
        assert_eq!(ship.inventory.trade_xp, initial_trade + 2 * 1500);
        assert_eq!(ship.inventory.explore_xp, initial_explore + 2 * 1500);
        let saved: crate::inventory::Inventory =
            serde_json::from_slice(&std::fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved.faction_standings.get(&6), Some(&5010.));
        assert!(!saved.missions.contains(303));
    }

    #[test]
    fn negotiation_mission_turn_in_debits_credits_with_repeated_rewards_once() {
        let mut ship = session();
        ship.account.character.race = 0;
        ship.account.character.profession = 1;
        ship.skill_catalog = SkillCatalog::for_profile(&ship.account.character).unwrap();
        ship.sector = ship.sectors.sector(10201).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 41);
        ship.open_npc(41).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(3));
        npc_choice(&mut ship, 3).unwrap();
        npc_choice(&mut ship, 4).unwrap();
        ship.close_npc();
        ship.open_npc(41).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission_stage, 1);
        ship.inventory.credits = 4999;
        assert!(npc_choice(&mut ship, 2).is_err());
        assert!(!ship.inventory.missions.completed.contains(&3));
        ship.inventory.credits = 5000;
        npc_choice(&mut ship, 2).unwrap();
        assert_eq!(ship.inventory.credits, 1000);
        assert_eq!(ship.inventory.explore_xp, 4250);
        assert!(ship.inventory.mission_awarded_skills.contains(&36));
        assert!(ship.inventory.missions.completed.contains(&3));
        assert!(npc_choice(&mut ship, 2).is_err());
        let saved: crate::inventory::Inventory =
            serde_json::from_slice(&std::fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved.credits, 1000);
        assert_eq!(saved.explore_xp, 4250);
    }

    #[test]
    fn reactor_leak_requires_stationary_nav_arrivals_then_npc_turn_in() {
        let mut ship = session();
        ship.account.character.race = 0;
        ship.account.character.profession = 1;
        ship.skill_catalog = SkillCatalog::for_profile(&ship.account.character).unwrap();
        ship.inventory.missions.completed.insert(3);
        ship.sector = ship.sectors.sector(10201).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 41);
        let prior = ship.pending.len();
        ship.open_npc(41).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(5));
        assert_eq!(ship.pending[prior].opcode, 0x1d);
        assert!(ship.pending[prior]
            .body
            .ends_with(b"Mission offer: Reactor Leak.\0"));
        assert_eq!(ship.pending[prior + 1].opcode, 0x54);
        for choice in [3, 4, 5] {
            npc_choice(&mut ship, choice).unwrap();
        }
        assert_eq!(ship.inventory.missions.active[0].stage, 1);
        ship.close_npc();

        ship.sector = ship.sectors.sector(1020).unwrap();
        let waste = ship
            .sector
            .navpoints
            .iter()
            .find(|nav| nav.source_id == 429)
            .unwrap();
        ship.position = waste.position;
        ship.motion.speed = 50.;
        ship.advance_player(0.).unwrap();
        assert_eq!(ship.inventory.missions.active[0].stage, 1);
        ship.motion.speed = 0.;
        let blocked = ship.state_dir.join("inventory.json.tmp");
        std::fs::create_dir(&blocked).unwrap();
        assert!(ship.advance_player(0.).is_err());
        assert_eq!(ship.inventory.missions.active[0].stage, 1);
        std::fs::remove_dir(&blocked).unwrap();
        ship.advance_player(0.).unwrap();
        assert_eq!(ship.inventory.missions.active[0].stage, 2);
        ship.advance_player(0.).unwrap();
        assert_eq!(ship.inventory.missions.active[0].stage, 2);

        let volatile = ship
            .sector
            .navpoints
            .iter()
            .find(|nav| nav.source_id == 1586)
            .unwrap();
        ship.position = volatile.position;
        ship.advance_player(0.).unwrap();
        assert_eq!(ship.inventory.missions.active[0].stage, 3);
        ship.sector = ship.sectors.sector(10201).unwrap();
        enter_npc_room(&mut ship, 41);
        ship.open_npc(41).unwrap();
        assert!(ship.inventory.missions.completed.contains(&5));
        assert!(ship.inventory.mission_awarded_skills.contains(&46));
        assert!(ship
            .inventory
            .cargo
            .iter()
            .flatten()
            .any(|item| item.template == 3641 && item.quantity == 1));
    }

    #[test]
    fn warp_movement_uses_half_second_native_cadence_and_normal_updates_remain() {
        let mut ship = session();
        ship.logged_in = true;
        ship.warp.phase = WarpPhase::Traveling;
        ship.motion.speed = 3000.;
        ship.motion.set_speed = 3000.;
        ship.last_broadcast = Instant::now() - Duration::from_millis(250);
        ship.last_warp_broadcast = Instant::now() - Duration::from_millis(400);
        ship.publish_mobs().unwrap();
        assert!(!ship.pending.iter().any(|packet| {
            packet.opcode == 0x3e && packet.body[2..6] == ship.player_id().to_le_bytes()
        }));
        ship.pending.clear();

        ship.last_warp_broadcast = Instant::now() - Duration::from_millis(510);
        ship.publish_mobs().unwrap();
        let movement = ship
            .pending
            .iter()
            .find(|packet| {
                packet.opcode == 0x3e && packet.body[2..6] == ship.player_id().to_le_bytes()
            })
            .unwrap();
        assert_eq!(&movement.body[..2], &1u16.to_le_bytes());
        assert_eq!(movement.body.len(), 46);
        ship.pending.clear();

        let before = ship.motion.movement_id;
        ship.advance_player(0.5).unwrap();
        assert_eq!(ship.motion.movement_id, before + 5);
        ship.warp.phase = WarpPhase::Idle;
        ship.last_broadcast = Instant::now() - Duration::from_millis(250);
        ship.publish_mobs().unwrap();
        let normal = ship
            .pending
            .iter()
            .find(|packet| {
                packet.opcode == 0x3e && packet.body[2..6] == ship.player_id().to_le_bytes()
            })
            .unwrap();
        assert_eq!(&normal.body[..2], &0x7fu16.to_le_bytes());
    }

    #[test]
    fn supply_run_checks_possession_consumes_cargo_and_learns_formula_once() {
        let mut ship = session();
        ship.account.character.race = 0;
        ship.account.character.profession = 1;
        ship.skill_catalog = SkillCatalog::for_profile(&ship.account.character).unwrap();
        ship.inventory.missions.completed.extend([3, 5]);
        ship.sector = ship.sectors.sector(10201).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 41);
        ship.open_npc(41).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(6));
        for choice in [2, 3, 4] {
            npc_choice(&mut ship, choice).unwrap();
        }
        ship.close_npc();
        let prior = ship.pending.len();
        assert!(ship.open_npc(41).is_err());
        assert!(ship.npc_interaction.is_none());
        assert_eq!(ship.pending.len(), prior);
        assert_eq!(ship.inventory.missions.active[0].stage, 1);

        for template in [1231, 1247, 1265] {
            ship.inventory = ship
                .inventory
                .received(template, 1, u32::MAX as usize, &ship.items)
                .unwrap();
        }
        ship.open_npc(41).unwrap();
        assert_eq!(ship.inventory.missions.active[0].stage, 2);
        assert_eq!(ship.inventory.cargo.iter().flatten().count(), 3);
        ship.close_npc();

        enter_npc_room(&mut ship, 45);
        ship.open_npc(45).unwrap();
        npc_choice(&mut ship, 2).unwrap();
        let blocked = ship.state_dir.join("inventory.json.tmp");
        std::fs::create_dir(&blocked).unwrap();
        assert!(npc_choice(&mut ship, 3).is_err());
        assert_eq!(ship.inventory.missions.active[0].stage, 2);
        assert!(ship
            .inventory
            .cargo
            .iter()
            .flatten()
            .any(|item| item.template == 1231));
        std::fs::remove_dir(&blocked).unwrap();
        npc_choice(&mut ship, 3).unwrap();
        assert_eq!(ship.inventory.missions.active[0].stage, 3);
        assert_eq!(ship.inventory.credits, 11_000);
        assert!(!ship
            .inventory
            .cargo
            .iter()
            .flatten()
            .any(|item| [1231, 1247, 1265].contains(&item.template)));
        ship.close_npc();

        enter_npc_room(&mut ship, 41);
        ship.open_npc(41).unwrap();
        assert!(ship.inventory.missions.completed.contains(&6));
        assert!(ship.inventory.known_formulas.contains(&508));
        assert!(ship.inventory.mission_awarded_skills.contains(&10));
        assert_eq!(ship.inventory.combat_xp, 2500);
        assert_eq!(ship.inventory.explore_xp, 2500);
        assert_eq!(ship.inventory.trade_xp, 2500);
        let saved: crate::inventory::Inventory =
            serde_json::from_slice(&std::fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved.missions, ship.inventory.missions);
        assert_eq!(saved.known_formulas, ship.inventory.known_formulas);
    }

    #[test]
    fn scuttle_larva_mission_requires_prerequisite_and_awards_owned_item() {
        let mut ship = session();
        ship.account.character.race = 0;
        ship.account.character.profession = 0;
        ship.skill_catalog = SkillCatalog::for_profile(&ship.account.character).unwrap();
        ship.sector = ship.sectors.sector(10151).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 46);
        ship.inventory.missions.completed.insert(102);
        ship.open_npc(46).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(104));
        for choice in [2, 3, 4] {
            npc_choice(&mut ship, choice).unwrap();
        }
        ship.close_npc();
        let player = ship.player_id();
        for generation in 1..=3 {
            ship.settle_kill_award(&crate::loot_world::KillAward {
                sector: 1015,
                death: crate::loot_world::MobDeath {
                    spawn: 476202,
                    generation,
                },
                killer: player,
                level: 1,
                amount: Some(100),
                source_killer: player,
                template: 1253,
            })
            .unwrap();
        }
        assert_eq!(ship.inventory.missions.active[0].stage, 2);
        ship.open_npc(46).unwrap();
        for choice in [2, 3] {
            npc_choice(&mut ship, choice).unwrap();
        }
        let cargo = ship.inventory.cargo.clone();
        for slot in &mut ship.inventory.cargo {
            *slot = Some(crate::inventory::ItemInstance::stock(2837, 1));
        }
        assert!(npc_choice(&mut ship, 4).is_err());
        assert!(!ship.inventory.missions.completed.contains(&104));
        ship.inventory.cargo = cargo;
        npc_choice(&mut ship, 4).unwrap();
        assert!(ship.inventory.missions.completed.contains(&104));
        assert_eq!(ship.inventory.combat_xp, 5300);
        assert!(ship
            .inventory
            .cargo
            .iter()
            .flatten()
            .any(|item| item.template == 3640 && item.quantity == 1));
    }

    #[test]
    fn cash_for_roids_commits_each_npc_payment_and_reward_once() {
        let mut ship = session();
        ship.account.character.race = 0;
        ship.account.character.profession = 2;
        ship.skill_catalog = SkillCatalog::for_profile(&ship.account.character).unwrap();
        ship.sector = ship.sectors.sector(12150).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 442);
        ship.open_npc(442).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(341));
        for choice in [2, 3, 4, 5] {
            npc_choice(&mut ship, choice).unwrap();
        }
        ship.close_npc();

        enter_npc_room(&mut ship, 444);
        ship.open_npc(444).unwrap();
        for choice in [2, 3, 4, 5, 6] {
            npc_choice(&mut ship, choice).unwrap();
        }
        assert_eq!(ship.inventory.credits, 60_000);
        assert_eq!(ship.inventory.missions.active[0].stage, 2);
        ship.close_npc();

        enter_npc_room(&mut ship, 442);
        ship.open_npc(442).unwrap();
        for choice in [2, 3] {
            npc_choice(&mut ship, choice).unwrap();
        }
        assert_eq!(ship.inventory.credits, 10_000);
        assert_eq!(ship.inventory.missions.active[0].stage, 3);
        ship.close_npc();

        enter_npc_room(&mut ship, 447);
        ship.open_npc(447).unwrap();
        for choice in [2, 3, 4, 5] {
            npc_choice(&mut ship, choice).unwrap();
        }
        assert_eq!(ship.inventory.credits, 85_000);
        assert_eq!(ship.inventory.missions.active[0].stage, 4);
        ship.close_npc();

        enter_npc_room(&mut ship, 442);
        ship.open_npc(442).unwrap();
        for choice in [2, 3, 4, 5] {
            npc_choice(&mut ship, choice).unwrap();
        }
        assert_eq!(ship.inventory.credits, 10_000);
        assert_eq!(ship.inventory.combat_xp, 5000);
        assert_eq!(ship.inventory.explore_xp, 5000);
        assert_eq!(ship.inventory.trade_xp, 5000);
        assert!(ship.inventory.mission_awarded_skills.contains(&36));
        assert!(ship.inventory.missions.completed.contains(&341));
        let saved: crate::inventory::Inventory =
            serde_json::from_slice(&std::fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved.missions, ship.inventory.missions);
        assert_eq!(saved.credits, 10_000);
    }

    #[test]
    fn repair_training_requires_station_payment_and_records_skill_reward() {
        let mut ship = session();
        ship.account.character.race = 0;
        ship.account.character.profession = 0;
        ship.skill_catalog = SkillCatalog::for_profile(&ship.account.character).unwrap();
        ship.sector = ship.sectors.sector(10601).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 28);
        ship.open_npc(28).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(90));
        npc_choice(&mut ship, 2).unwrap();
        ship.close_npc();
        let prior = ship.pending.len();
        ship.open_npc(28).unwrap();
        assert_eq!(ship.pending[prior].opcode, 0x1d);
        assert!(ship.pending[prior]
            .body
            .ends_with(b"Mission objective: Going for Repairs.\0"));
        assert_eq!(ship.pending[prior + 1].opcode, 0x54);
        for choice in [2, 3, 4, 5] {
            npc_choice(&mut ship, choice).unwrap();
        }
        assert_eq!(ship.inventory.credits, 7500);
        assert!(ship.inventory.mission_awarded_skills.contains(&47));
        assert!(ship.inventory.missions.completed.contains(&90));
    }

    /// A fresh Enforcer earns the quested repair skill at rank one after payment.
    #[test]
    fn native_progression_repair_mission_unlocks_untrained_skill_durably() {
        let root = std::env::temp_dir().join(format!("enb-cadet-repair-{}", rand::random::<u64>()));
        let stored = crate::accounts::AccountDirectory::new(&root)
            .create_class_progression(
                "cadet".into(),
                "pass",
                "Cadet".into(),
                crate::classes::CharacterClass::parse("te").unwrap(),
            )
            .unwrap();
        let initial = SectorCatalog::load().unwrap().sector(1076).unwrap();
        let mut ship =
            PlayerSession::new(initial, Ipv4Addr::LOCALHOST, stored.directory.clone()).unwrap();
        assert_eq!(ship.skill_catalog.level(47), 0);
        let mut earned = ship.inventory.clone();
        earned.combat_xp = 2_000_000;
        earned.credits = 10_000;
        ship.commit_inventory(earned).unwrap();
        ship.sector = ship.sectors.sector(10601).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 28);
        ship.open_npc(28).unwrap();
        npc_choice(&mut ship, 2).unwrap();
        ship.close_npc();
        ship.open_npc(28).unwrap();
        for choice in [2, 3, 4, 5] {
            npc_choice(&mut ship, choice).unwrap();
        }
        assert_eq!(ship.skill_catalog.level(47), 1);
        assert!(ship.inventory.mission_awarded_skills.contains(&47));
        assert_eq!(ship.inventory.spent_skill_points, 0);
        let saved = ship.state_dir.clone();
        drop(ship);
        let resumed = PlayerSession::new(
            SectorCatalog::load().unwrap().sector(1076).unwrap(),
            Ipv4Addr::LOCALHOST,
            saved,
        )
        .unwrap();
        assert_eq!(resumed.skill_catalog.level(47), 1);
        assert!(resumed.inventory.missions.completed.contains(&90));
        drop(resumed);
        std::fs::remove_dir_all(root).unwrap();
    }

    /// A fresh Privateer completes the public space-object talk mission after travel.
    #[test]
    fn native_privateer_waste_disposal_space_dialogue_and_turn_in_survive_restart() {
        let root = std::env::temp_dir().join(format!("enb-pp-waste-{}", rand::random::<u64>()));
        let stored = crate::accounts::AccountDirectory::new(&root)
            .create_class_progression(
                "privateer".into(),
                "pass",
                "Privateer".into(),
                crate::classes::CharacterClass::parse("pp").unwrap(),
            )
            .unwrap();
        let initial = SectorCatalog::load().unwrap().sector(1076).unwrap();
        let mut ship = PlayerSession::new(
            initial.clone(),
            Ipv4Addr::LOCALHOST,
            stored.directory.clone(),
        )
        .unwrap();
        assert_eq!(ship.sector.id, 10371);
        ship.logged_in = true;
        enter_npc_room(&mut ship, 371);
        ship.open_npc(371).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(48));
        for choice in [3, 4, 5] {
            npc_choice(&mut ship, choice).unwrap();
        }
        assert_eq!(ship.inventory.missions.active[0].stage, 1);
        ship.station_request(10371, 1).unwrap();
        assert_eq!(ship.sector.id, 1037);
        ship.logged_in = true; // Complete the disposable native sector handoff.
        let nav = ship
            .sector
            .navpoints
            .iter()
            .find(|nav| nav.source_id == 3059)
            .unwrap()
            .clone();
        let target = [ship.player_id().to_le_bytes(), nav.id.to_le_bytes()].concat();
        ship.position = [
            nav.position[0] + nav.radius + 5001.,
            nav.position[1],
            nav.position[2],
        ];
        ship.handle_input(0x17, &target).unwrap();
        assert!(ship.npc_interaction.is_none());
        ship.position = [
            nav.position[0] + nav.radius + 4999.,
            nav.position[1],
            nav.position[2],
        ];
        ship.handle_input(0x17, &target).unwrap();
        assert_eq!(
            ship.npc_interaction.as_ref().unwrap().space_object,
            Some(nav.id)
        );
        assert!(ship.pending.iter().any(|frame| frame.opcode == 0x54));
        ship.position[0] += 3.;
        assert!(npc_choice(&mut ship, 2).is_err());
        assert_eq!(ship.inventory.missions.active[0].stage, 1);
        ship.position[0] -= 3.;
        npc_choice(&mut ship, 2).unwrap();
        assert_eq!(ship.inventory.missions.active[0].stage, 2);
        let outsider = crate::accounts::AccountDirectory::new(&root)
            .create_class_progression(
                "outsider".into(),
                "pass",
                "Outsider".into(),
                crate::classes::CharacterClass::parse("pp").unwrap(),
            )
            .unwrap();
        let mut peer =
            PlayerSession::new(initial.clone(), Ipv4Addr::LOCALHOST, outsider.directory).unwrap();
        peer.sector = peer.sectors.sector(1037).unwrap();
        peer.position = ship.position;
        peer.logged_in = true;
        peer.handle_input(
            0x17,
            &[peer.player_id().to_le_bytes(), nav.id.to_le_bytes()].concat(),
        )
        .unwrap();
        assert!(peer.npc_interaction.is_none());
        assert!(!peer.pending.iter().any(|frame| frame.opcode == 0x54));
        drop(peer);
        let state = ship.state_dir.clone();
        drop(ship);
        let mut resumed = PlayerSession::new(initial, Ipv4Addr::LOCALHOST, state).unwrap();
        assert_eq!(resumed.sector.id, 1037);
        assert_eq!(resumed.inventory.missions.active[0].stage, 2);
        resumed.transfer_sector(10371, false).unwrap();
        resumed.logged_in = true;
        enter_npc_room(&mut resumed, 371);
        resumed.open_npc(371).unwrap();
        npc_choice(&mut resumed, 2).unwrap();
        assert!(resumed.inventory.missions.completed.contains(&48));
        assert_eq!(resumed.inventory.credits, 10_200);
        drop(resumed);
        std::fs::remove_dir_all(root).unwrap();
    }

    /// A fresh Privateer earns the authored Critter-kill rewards without duplicate receipts.
    #[test]
    fn native_privateer_cleaner_combat_reward_retries_and_survives_restart() {
        let root = std::env::temp_dir().join(format!("enb-pp-cleaner-{}", rand::random::<u64>()));
        let directory = crate::accounts::AccountDirectory::new(&root);
        let stored = directory
            .create_class_progression(
                "privateer".into(),
                "pass",
                "Privateer".into(),
                crate::classes::CharacterClass::parse("pp").unwrap(),
            )
            .unwrap();
        let initial = SectorCatalog::load().unwrap().sector(1076).unwrap();
        let mut ship = PlayerSession::new(
            initial.clone(),
            Ipv4Addr::LOCALHOST,
            stored.directory.clone(),
        )
        .unwrap();
        let mut prior = ship.inventory.clone();
        prior.missions.completed.insert(48); // Durable prerequisite from Waste Disposal.
        ship.commit_inventory(prior).unwrap();
        assert_eq!(ship.skill_catalog.level(40), 0);
        let initial_credits = ship.inventory.credits;
        let initial_faction = ship.reputation.standing(5);
        ship.logged_in = true;
        enter_npc_room(&mut ship, 372);
        ship.open_npc(372).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(59));
        npc_choice(&mut ship, 2).unwrap();
        npc_choice(&mut ship, 3).unwrap();
        assert_eq!(ship.inventory.missions.active[0].stage, 1);
        ship.station_request(10371, 1).unwrap();
        ship.logged_in = true;
        assert_eq!(ship.sector.id, 1037);
        let outsider = directory
            .create_class_progression(
                "outsider".into(),
                "pass",
                "Outsider".into(),
                crate::classes::CharacterClass::parse("pp").unwrap(),
            )
            .unwrap();
        let mut peer =
            PlayerSession::new(initial.clone(), Ipv4Addr::LOCALHOST, outsider.directory).unwrap();
        peer.sector = peer.sectors.sector(1037).unwrap();
        peer.logged_in = true;
        let peer_award = crate::loot_world::KillAward {
            sector: 1037,
            death: crate::loot_world::MobDeath {
                spawn: 13600,
                generation: 1,
            },
            killer: peer.player_id(),
            level: 0,
            amount: Some(100),
            source_killer: peer.player_id(),
            template: 136,
        };
        peer.settle_kill_award(&peer_award).unwrap();
        assert_eq!(ship.inventory.missions.active[0].stage, 1);
        drop(peer);
        for generation in 2..=6 {
            let award = crate::loot_world::KillAward {
                sector: 1037,
                death: crate::loot_world::MobDeath {
                    spawn: 13600,
                    generation,
                },
                killer: ship.player_id(),
                level: 0,
                amount: Some(100),
                source_killer: ship.player_id(),
                template: 136,
            };
            ship.settle_kill_award(&award).unwrap();
            ship.settle_kill_award(&award).unwrap();
        }
        assert_eq!(ship.inventory.missions.active[0].stage, 2);
        let state = ship.state_dir.clone();
        drop(ship);
        let mut resumed = PlayerSession::new(initial, Ipv4Addr::LOCALHOST, state).unwrap();
        assert_eq!(resumed.inventory.missions.active[0].stage, 2);
        resumed.transfer_sector(10371, false).unwrap();
        resumed.logged_in = true;
        enter_npc_room(&mut resumed, 372);
        resumed.open_npc(372).unwrap();
        let before = resumed.inventory.clone();
        let temporary = resumed.state_dir.join("inventory.json.tmp");
        std::fs::create_dir(&temporary).unwrap();
        assert!(npc_choice(&mut resumed, 2).is_err());
        assert_eq!(resumed.inventory, before);
        std::fs::remove_dir(&temporary).unwrap();
        npc_choice(&mut resumed, 2).unwrap();
        assert!(resumed.inventory.missions.completed.contains(&59));
        assert_eq!(resumed.inventory.credits, initial_credits + 2500);
        assert_eq!(resumed.reputation.standing(5), initial_faction + 200.);
        assert_eq!(resumed.skill_catalog.level(40), 1);
        assert_eq!(resumed.inventory.spent_skill_points, 0);
        assert!(resumed
            .inventory
            .cargo
            .iter()
            .flatten()
            .any(|item| item.template == 3023));
        assert!(resumed
            .inventory
            .cargo
            .iter()
            .flatten()
            .any(|item| item.template == 515));
        let saved: Inventory = serde_json::from_slice(
            &std::fs::read(resumed.state_dir.join("inventory.json")).unwrap(),
        )
        .unwrap();
        assert_eq!(saved, resumed.inventory);
        drop(resumed);
        std::fs::remove_dir_all(root).unwrap();
    }

    /// A fresh Enforcer delivers the authored message to the selected space NPC once.
    #[test]
    fn native_enforcer_space_message_consumes_owned_item_after_durable_dialogue() {
        let root = std::env::temp_dir().join(format!("enb-te-message-{}", rand::random::<u64>()));
        let directory = crate::accounts::AccountDirectory::new(&root);
        let stored = directory
            .create_class_progression(
                "enforcer".into(),
                "pass",
                "Enforcer".into(),
                crate::classes::CharacterClass::parse("te").unwrap(),
            )
            .unwrap();
        let initial = SectorCatalog::load().unwrap().sector(1076).unwrap();
        let mut ship = PlayerSession::new(
            initial.clone(),
            Ipv4Addr::LOCALHOST,
            stored.directory.clone(),
        )
        .unwrap();
        let mut prior = ship.inventory.clone();
        prior.missions.completed.extend([102, 104]);
        ship.commit_inventory(prior).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 46);
        ship.open_npc(46).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(105));
        let before = ship.inventory.clone();
        let temporary = ship.state_dir.join("inventory.json.tmp");
        std::fs::create_dir(&temporary).unwrap();
        assert!(npc_choice(&mut ship, 2).is_err());
        assert_eq!(ship.inventory, before);
        std::fs::remove_dir(&temporary).unwrap();
        npc_choice(&mut ship, 2).unwrap();
        assert_eq!(ship.inventory.missions.active[0].stage, 1);
        assert!(ship
            .inventory
            .cargo
            .iter()
            .flatten()
            .any(|item| item.template == 5226));
        let granted = ship.inventory.clone();
        assert!(npc_choice(&mut ship, 2).is_err());
        assert_eq!(ship.inventory, granted);
        ship.close_npc();
        ship.station_request(10151, 1).unwrap();
        ship.logged_in = true;
        assert_eq!(ship.sector.id, 1015);
        let nav = ship
            .sector
            .navpoints
            .iter()
            .find(|nav| nav.source_id == 13451)
            .unwrap()
            .clone();
        let target = [ship.player_id().to_le_bytes(), nav.id.to_le_bytes()].concat();
        ship.position = [
            nav.position[0] + nav.radius + 4999.,
            nav.position[1],
            nav.position[2],
        ];
        ship.handle_input(0x17, &target).unwrap();
        assert_eq!(ship.npc_interaction.as_ref().unwrap().mission, Some(105));
        assert_eq!(
            ship.npc_interaction.as_ref().unwrap().space_object,
            Some(nav.id)
        );
        let outsider = directory
            .create_class_progression(
                "outsider".into(),
                "pass",
                "Outsider".into(),
                crate::classes::CharacterClass::parse("te").unwrap(),
            )
            .unwrap();
        let mut peer =
            PlayerSession::new(initial.clone(), Ipv4Addr::LOCALHOST, outsider.directory).unwrap();
        peer.sector = peer.sectors.sector(1015).unwrap();
        peer.position = ship.position;
        peer.logged_in = true;
        peer.handle_input(
            0x17,
            &[peer.player_id().to_le_bytes(), nav.id.to_le_bytes()].concat(),
        )
        .unwrap();
        assert!(peer.npc_interaction.is_none());
        drop(peer);
        npc_choice(&mut ship, 2).unwrap();
        ship.position[0] += 3.;
        assert!(npc_choice(&mut ship, 3).is_err());
        assert_eq!(ship.inventory, granted);
        ship.position[0] -= 3.;
        let temporary = ship.state_dir.join("inventory.json.tmp");
        std::fs::create_dir(&temporary).unwrap();
        assert!(npc_choice(&mut ship, 3).is_err());
        assert_eq!(ship.inventory, granted);
        std::fs::remove_dir(&temporary).unwrap();
        npc_choice(&mut ship, 3).unwrap();
        assert!(ship.inventory.missions.completed.contains(&105));
        assert!(!ship
            .inventory
            .cargo
            .iter()
            .flatten()
            .any(|item| item.template == 5226));
        assert_eq!(ship.inventory.explore_xp, granted.explore_xp + 5000);
        assert_eq!(ship.inventory.trade_xp, granted.trade_xp + 5000);
        let state = ship.state_dir.clone();
        drop(ship);
        let resumed = PlayerSession::new(initial, Ipv4Addr::LOCALHOST, state).unwrap();
        assert!(resumed.inventory.missions.completed.contains(&105));
        assert!(!resumed
            .inventory
            .cargo
            .iter()
            .flatten()
            .any(|item| item.template == 5226));
        drop(resumed);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn npc_dialogue_and_vendor_require_the_npcs_room_after_a_room_change() {
        let mut ship = session();
        ship.sector = ship.sectors.sector(10761).unwrap();
        ship.logged_in = true;
        assert!(ship.open_npc(16).is_err()); // No confirmed room yet.
        enter_npc_room(&mut ship, 7); // Room 1; vendor16 belongs to room 2.
        assert!(ship.open_npc(16).is_err());
        enter_npc_room(&mut ship, 16);
        ship.open_npc(16).unwrap();
        npc_choice(&mut ship, 2).unwrap();
        ship.inventory.credits = 10_000_000;
        let vendor_transfer = ItemTransfer {
            source: 4,
            source_slot: 0,
            destination: 1,
            destination_slot: 0,
            quantity: 1,
        };
        assert!(ship.inventory_candidate(vendor_transfer).is_ok());
        ship.handle_input(
            0x9f,
            &[ship.player_id(), 1, 2].map(u32::to_le_bytes).concat(),
        )
        .unwrap();
        assert!(ship.npc_interaction.is_none());
        assert!(ship.inventory_candidate(vendor_transfer).is_err());
        assert!(ship.open_npc(16).is_err());
        enter_npc_room(&mut ship, 7);
        ship.open_npc(7).unwrap();
    }

    #[test]
    fn npc_trades_require_station_offered_branch_and_live_vendor_and_persist_together() {
        let mut ship = session();
        ship.logged_in = true;
        assert!(ship.open_npc(16).is_err()); // space
        ship.sector = ship.sectors.sector(10761).unwrap();
        assert!(ship.open_npc(9).is_err()); // another station
        assert!(npc_choice(&mut ship, 2).is_err()); // no dialogue
        enter_npc_room(&mut ship, 16);
        ship.open_npc(16).unwrap();
        assert!(npc_choice(&mut ship, 99).is_err());
        assert!(ship
            .talk_choice(&[(ship.player_id() + 1).to_le_bytes().as_slice(), &[2]].concat())
            .is_err());
        let transfer = ItemTransfer {
            source: 4,
            source_slot: 0,
            destination: 1,
            destination_slot: u32::MAX as usize,
            quantity: 1,
        };
        assert!(ship.inventory_candidate(transfer).is_err()); // greeting is not trade
        npc_choice(&mut ship, 2).unwrap();
        assert!(ship.npc_interaction.as_ref().unwrap().vendor);
        let offers = ship
            .npcs
            .group(ship.npcs.npc(16, 10761).unwrap())
            .unwrap()
            .inventory(&ship.items)
            .unwrap();
        let offer = offers[0];
        ship.inventory.credits = 10_000_000;
        let before = ship.inventory.credits;
        ship.transfer_inventory(transfer).unwrap();
        assert_eq!(
            ship.inventory.cargo[0].as_ref().unwrap().template,
            offer.item
        );
        assert_eq!(ship.inventory.credits, before - offer.price);
        let saved: Inventory =
            serde_json::from_slice(&fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved, ship.inventory);
        let price = ship
            .npcs
            .group(ship.npcs.npc(16, 10761).unwrap())
            .unwrap()
            .buy_price(offer.item, &ship.items)
            .unwrap();
        assert!(price > 0);
        ship.transfer_inventory(ItemTransfer {
            source: 1,
            source_slot: 0,
            destination: 4,
            destination_slot: 0,
            quantity: 1,
        })
        .unwrap();
        assert!(ship.inventory.cargo[0].is_none());
        assert_eq!(ship.inventory.credits, before - offer.price + price);
        npc_choice(&mut ship, 230).unwrap();
        assert!(ship.npc_interaction.is_none());
        assert!(ship.inventory_candidate(transfer).is_err());
        assert!(ship
            .pending
            .iter()
            .any(|frame| frame.opcode == 0x56 && frame.body == (-32i32).to_le_bytes()));
        ship.sector = ship.sectors.sector(12150).unwrap();
        enter_npc_room(&mut ship, 446);
        ship.open_npc(446).unwrap();
        npc_choice(&mut ship, 3).unwrap(); // public trade tree, missing vendor group
        assert!(!ship.npc_interaction.as_ref().unwrap().vendor);
        assert!(ship.inventory_candidate(transfer).is_err());
        npc_choice(&mut ship, 0).unwrap();
        assert!(ship.npc_interaction.is_none());
    }

    #[test]
    fn active_vendor_survives_restart_but_cannot_be_used_in_another_station() {
        let mut ship = session();
        ship.sector = ship.sectors.sector(10761).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 16);
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:21002".parse().unwrap());
        ship.open_npc(16).unwrap();
        npc_choice(&mut ship, 2).unwrap();
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(restored.logged_in && restored.npc_interaction.as_ref().unwrap().vendor);
        let transfer = ItemTransfer {
            source: 4,
            source_slot: 0,
            destination: 1,
            destination_slot: 0,
            quantity: 1,
        };
        restored.inventory.credits = 10_000_000;
        assert!(restored.inventory_candidate(transfer).is_ok());
        restored.sector = restored.sectors.sector(17051).unwrap();
        assert!(restored.inventory_candidate(transfer).is_err());
        restored.restore_npc_service().unwrap();
        assert!(restored.npc_interaction.is_none());
    }

    #[test]
    fn duplicate_vendor_udp_is_ignored_before_and_after_checkpoint_restore() {
        let mut ship = session();
        ship.sector = ship.sectors.sector(10761).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 16);
        let endpoint: SocketAddr = "127.0.0.1:21003".parse().unwrap();
        ship.endpoint = Some(endpoint);
        ship.selected_peer = Some(endpoint.ip());
        ship.open_npc(16).unwrap();
        npc_choice(&mut ship, 2).unwrap();
        ship.inventory.credits = 10_000_000;
        let request = Datagram {
            opcode: 0x27,
            player: ship.player_id(),
            sequence: 50,
            body: [ship.player_id(), 4, 0, 1, u32::MAX, 1]
                .into_iter()
                .flat_map(u32::to_be_bytes)
                .collect(),
        };
        ship.handle_udp(SECTOR_PORT, endpoint, request.clone())
            .unwrap();
        let after = ship.inventory.clone();
        ship.handle_udp(SECTOR_PORT, endpoint, request.clone())
            .unwrap();
        assert_eq!(ship.inventory, after);
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        restored.handle_udp(SECTOR_PORT, endpoint, request).unwrap();
        assert_eq!(restored.inventory, after);
    }

    #[test]
    fn failed_vendor_disk_commit_leaves_cargo_credits_and_success_frames_unchanged() {
        let mut ship = session();
        ship.sector = ship.sectors.sector(10761).unwrap();
        ship.logged_in = true;
        enter_npc_room(&mut ship, 16);
        ship.open_npc(16).unwrap();
        npc_choice(&mut ship, 2).unwrap();
        ship.inventory.credits = 10_000_000;
        fs::create_dir_all(&ship.state_dir).unwrap();
        let blocked = ship.state_dir.join("blocked");
        fs::write(&blocked, b"file, not directory").unwrap();
        ship.state_dir = blocked;
        ship.pending.clear();
        let before = ship.inventory.clone();
        let transfer = ItemTransfer {
            source: 4,
            source_slot: 0,
            destination: 1,
            destination_slot: 0,
            quantity: 1,
        };
        assert!(ship.transfer_inventory(transfer).is_err());
        assert_eq!(ship.inventory, before);
        assert!(ship.pending.is_empty());
    }

    #[test]
    fn restart_retains_known_peer_actors_without_duplicate_creates() {
        let mut ship = session();
        let mut other = session();
        ship.logged_in = true;
        other.logged_in = true;
        other.account.character.game_id += 1;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:21001".parse().unwrap());
        let peer = other.peer_snapshot().unwrap();
        ship.peer_ids.insert(other.player_id());
        ship.publish_peers(std::slice::from_ref(&peer));
        assert!(ship.pending.iter().any(|frame| frame.opcode == 4));
        ship.checkpoint_session(true).unwrap();
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        restored.pending.clear();
        restored.publish_peers(&[peer]);
        assert!(!restored.pending.iter().any(|frame| frame.opcode == 4));
        assert!(restored.visible_peers.contains(&other.player_id()));
    }

    #[test]
    fn beams_use_equipped_damage_energy_and_reload_without_toggle_bypass() {
        let mut ship = session();
        ship.logged_in = true;
        ship.inventory.equipped[4] = ship.inventory.equipped[3].clone();
        ship.refresh_equipment().unwrap();
        ship.target = MOB_ID;
        let shared = Arc::clone(&ship.combat);
        lock_combat(&shared).unwrap().actors[0].position = [100., 0., 0.];
        let before = lock_combat(&shared).unwrap().actors[0].shield;
        ship.toggle_all_weapons();
        ship.fire_weapons_with_roll(|| 5000).unwrap();
        assert_eq!(lock_combat(&shared).unwrap().actors[0].shield, before - 18.);
        assert_eq!(ship.energy, 120.);
        assert_eq!(ship.weapon_cooldowns[3], 2.);
        ship.toggle_all_weapons();
        ship.toggle_all_weapons();
        ship.fire_weapons_with_roll(|| 5000).unwrap();
        assert_eq!(ship.energy, 120.); // Toggling does not reset either reload.
        lock_combat(&shared).unwrap().take_damage_reports();
        ship.advance_player(1.).unwrap();
        assert_eq!(lock_combat(&shared).unwrap().actors[0].shield, before - 18.);
        ship.advance_player(1.).unwrap();
        let reports = lock_combat(&shared).unwrap().take_damage_reports();
        assert_eq!(reports.len(), 2);
        assert!(reports
            .iter()
            .all(|report| matches!((report.damage, report.inflicted), (0. | 9., 0) | (18., 3))));
        let damage: f32 = reports.iter().map(|report| report.damage).sum();
        assert_eq!(
            lock_combat(&shared).unwrap().actors[0].shield,
            before - 18. - damage
        );
        assert_eq!(ship.inventory.shot_sequence, 4);
        ship.player_firing = false;
        ship.weapon_firing.fill(false);
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 3]].concat();
        ship.activate_equipped_item(&activation).unwrap();
        assert!(ship.weapon_firing[3]);
        assert!(!ship.weapon_firing[4]);
    }

    /// Turbo Weapons shortens actual reloads, costs energy and survives restart.
    #[test]
    fn activated_turbo_weapons_shortens_reload_then_expires_after_recovery() {
        let mut ship = session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22491".parse().unwrap());
        let mut inventory = ship.inventory.clone();
        inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3343, 1));
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(2265, 1));
        ship.commit_inventory(inventory).unwrap();
        let ordinary_speed = ship.motion.max_speed;
        let ordinary_reload = ship.equipment.weapons[0].reload;
        ship.energy = ship.equipment.ship.energy_capacity;
        let ordinary_energy = ship.energy;
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 9]].concat();
        ship.energy = 757.;
        ship.activate_equipped_item(&activation).unwrap();
        assert!(ship.active_device.is_none());
        assert_eq!(ship.equipment.weapons[0].reload, ordinary_reload);
        ship.energy = ordinary_energy;
        ship.activate_equipped_item(&activation).unwrap();
        assert_eq!(ship.energy, ordinary_energy - 758.);
        assert_eq!(ship.motion.max_speed, ordinary_speed);
        let active = ship.active_device.as_ref().unwrap().clone();
        let visual_id = active.visual_id(ship.player_id());
        assert!(ship.pending.iter().any(|frame| {
            frame.opcode == 0x09
                && frame.body[0] == 3
                && u16::from_le_bytes(frame.body[5..7].try_into().unwrap()) == 604
                && u32::from_le_bytes(frame.body[7..11].try_into().unwrap()) == visual_id
        }));
        assert!(ship.pending.iter().any(|frame| {
            frame.opcode == 0x1b
                && frame.body.len() > 20
                && frame.body[12] & 0x40 != 0
                && frame
                    .body
                    .windows(b"Turbo Weapons".len())
                    .any(|window| window == b"Turbo Weapons")
        }));
        let boosted_reload = ship.equipment.weapons[0].reload;
        assert!(boosted_reload < ordinary_reload);
        ship.target = MOB_ID;
        lock_combat(&ship.combat).unwrap().actors[0].position = [100., 0., 0.];
        ship.toggle_all_weapons();
        ship.fire_weapons_with_roll(|| 5000).unwrap();
        assert_eq!(ship.weapon_cooldowns[3], boosted_reload);
        ship.activate_equipped_item(&activation).unwrap();
        assert!(ship.energy > 0.);

        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(restored.logged_in);
        assert_eq!(restored.equipment.weapons[0].reload, boosted_reload);
        assert!(restored.pending.iter().any(|frame| {
            frame.opcode == 0x09
                && u32::from_le_bytes(frame.body[7..11].try_into().unwrap()) == visual_id
        }));
        assert!(restored.pending.iter().any(|frame| {
            frame.opcode == 0x1d
                && frame
                    .body
                    .windows(b"Turbo Weapons active for".len())
                    .any(|window| window == b"Turbo Weapons active for")
        }));
        restored.active_device.as_mut().unwrap().expires_at_ms = crate::resources::unix_ms() - 1;
        let expired_visual_id = restored
            .active_device
            .as_ref()
            .unwrap()
            .visual_id(restored.player_id());
        restored.advance_active_device().unwrap();
        assert!(restored.active_device.is_none());
        assert_eq!(restored.motion.max_speed, ordinary_speed);
        assert_eq!(restored.equipment.weapons[0].reload, ordinary_reload);
        assert!(restored.pending.iter().any(|frame| {
            frame.opcode == 0x0f && frame.body == expired_visual_id.to_le_bytes()
        }));
        assert!(restored.pending.iter().any(|frame| {
            frame.opcode == 0x1b && frame.body.len() > 20 && frame.body[12] & 0x40 != 0
        }));
    }

    /// Instant shield rerouting spends owned resources once and survives replacement.
    #[test]
    fn instant_shield_reroute_validates_resources_and_persists() {
        let mut ship = session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22494".parse().unwrap());
        let mut inventory = ship.inventory.clone();
        inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3343, 1));
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1933, 1));
        inventory
            .validate_for_profile(&ship.account.character, &ship.items, &ship.skill_catalog)
            .unwrap();
        ship.commit_inventory(inventory).unwrap();
        let capacity = ship.equipment.ship.energy_capacity;
        let shield_capacity = ship.equipment.ship.shield_capacity;
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 9]].concat();
        ship.shield = shield_capacity;
        ship.energy = 84.;
        ship.activate_equipped_item(&activation).unwrap();
        assert_eq!(ship.energy, 84.);
        assert_eq!(ship.shield, shield_capacity);
        ship.energy = capacity / 4.;
        let before = ship.energy;
        ship.activate_equipped_item(&activation).unwrap();
        assert!((ship.shield - shield_capacity * 0.53).abs() < 0.01);
        assert!((ship.energy - (before - 85. + capacity * 0.18).min(capacity)).abs() < 0.01);
        assert!(ship.active_device.is_none());
        assert!(ship.pending.iter().any(|frame| {
            frame.opcode == 0x1d
                && frame
                    .body
                    .windows(b"converted shields".len())
                    .any(|window| window == b"converted shields")
        }));
        let energy = ship.energy;
        let shield = ship.shield;
        let restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!((restored.energy - energy).abs() < 0.01);
        assert!((restored.shield - shield).abs() < 0.01);
        ship.shield = shield_capacity * 0.47 - 1.;
        ship.activate_equipped_item(&activation).unwrap();
        assert_eq!(ship.energy, energy);
        assert_eq!(ship.shield, shield_capacity * 0.47 - 1.);
    }

    /// Reactor-mounted reroute uses the same native item action as a device mount.
    #[test]
    fn reactor_mounted_shield_reroute_activates_from_slot_one() {
        let mut ship = session();
        ship.logged_in = true;
        let mut inventory = ship.inventory.clone();
        inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3337, 1));
        inventory
            .validate_for_profile(&ship.account.character, &ship.items, &ship.skill_catalog)
            .unwrap();
        ship.commit_inventory(inventory).unwrap();
        let capacity = ship.equipment.ship.energy_capacity;
        let shield_capacity = ship.equipment.ship.shield_capacity;
        ship.energy = capacity / 2.;
        ship.shield = shield_capacity;
        let before = ship.energy;
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 1]].concat();
        ship.activate_equipped_item(&activation).unwrap();
        assert!((ship.shield - shield_capacity * 0.58).abs() < 0.01);
        assert!((ship.energy - (before + capacity * 0.24).min(capacity)).abs() < 0.01);
    }

    /// Impulse Turbo changes authoritative movement and retains its buff on restart.
    #[test]
    fn impulse_turbo_device_changes_speed_and_expires_after_recovery() {
        let mut ship = session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22495".parse().unwrap());
        let mut inventory = ship.inventory.clone();
        inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3343, 1));
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1891, 1));
        inventory
            .validate_for_profile(&ship.account.character, &ship.items, &ship.skill_catalog)
            .unwrap();
        ship.commit_inventory(inventory).unwrap();
        let ordinary_speed = ship.motion.max_speed;
        ship.energy = ship.equipment.ship.energy_capacity;
        let energy = ship.energy;
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 9]].concat();
        ship.activate_equipped_item(&activation).unwrap();
        assert!((ship.motion.max_speed - ordinary_speed * 1.48).abs() < 0.01);
        assert_eq!(ship.energy, energy - 96.);
        assert!(ship.pending.iter().any(|frame| {
            frame.opcode == 0x1b
                && frame
                    .body
                    .windows(b"Increase Combat Speed".len())
                    .any(|window| window == b"Increase Combat Speed")
        }));
        assert!(ship.pending.iter().any(|frame| {
            frame.opcode == 0x1d
                && frame
                    .body
                    .windows(b"Impulse Turbo active for 10 seconds.".len())
                    .any(|window| window == b"Impulse Turbo active for 10 seconds.")
        }));
        assert!(!ship.pending.iter().any(|frame| frame.opcode == 0x09));
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!((restored.motion.max_speed - ordinary_speed * 1.48).abs() < 0.01);
        restored.active_device.as_mut().unwrap().expires_at_ms = crate::resources::unix_ms() - 1;
        restored.advance_active_device().unwrap();
        assert_eq!(restored.motion.max_speed, ordinary_speed);
        assert!(restored.pending.iter().any(|frame| {
            frame.opcode == 0x1d
                && frame
                    .body
                    .windows(b"Impulse Turbo ended.".len())
                    .any(|window| window == b"Impulse Turbo ended.")
        }));
        assert!(!restored.pending.iter().any(|frame| frame.opcode == 0x0f));
    }

    /// Reactor-mounted Impulse Turbo uses native activation slot one.
    #[test]
    fn impulse_turbo_reactor_mount_changes_authoritative_speed() {
        let mut ship = session();
        ship.logged_in = true;
        let mut inventory = ship.inventory.clone();
        inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3343, 1));
        inventory
            .validate_for_profile(&ship.account.character, &ship.items, &ship.skill_catalog)
            .unwrap();
        ship.commit_inventory(inventory).unwrap();
        let ordinary_speed = ship.motion.max_speed;
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 1]].concat();
        ship.activate_equipped_item(&activation).unwrap();
        assert!((ship.motion.max_speed - ordinary_speed * 2.2).abs() < 0.01);
        assert_eq!(ship.active_device.as_ref().unwrap().slot, 1);
    }

    /// Self-targeted maneuverability changes authoritative turn rate and survives recovery.
    #[test]
    fn maneuverability_device_changes_turn_rate_and_expires() {
        let mut ship = session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22496".parse().unwrap());
        let mut inventory = ship.inventory.clone();
        inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3343, 1));
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(2372, 1));
        inventory
            .validate_for_profile(&ship.account.character, &ship.items, &ship.skill_catalog)
            .unwrap();
        ship.commit_inventory(inventory).unwrap();
        let turn = ship.equipment.ship.turn_rate;
        let energy = ship.equipment.ship.energy_capacity;
        ship.energy = energy;
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 9]].concat();
        ship.activate_equipped_item(&activation).unwrap();
        assert!((ship.equipment.ship.turn_rate - turn * 1.3).abs() < 0.01);
        assert!((ship.motion.turn_scale - ship.equipment.ship.turn_rate / 45.).abs() < 0.01);
        assert_eq!(ship.energy, energy - 1750.);
        assert!(ship.pending.iter().any(|frame| {
            frame.opcode == 0x1b
                && frame
                    .body
                    .windows(b"Increase maneuver".len())
                    .any(|window| window == b"Increase maneuver")
        }));
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!((restored.equipment.ship.turn_rate - turn * 1.3).abs() < 0.01);
        restored.active_device.as_mut().unwrap().expires_at_ms = crate::resources::unix_ms() - 1;
        restored.advance_active_device().unwrap();
        assert_eq!(restored.equipment.ship.turn_rate, turn);
    }

    /// A reactor with one native handling effect activates from slot one.
    #[test]
    fn maneuverability_reactor_changes_authoritative_turn_rate() {
        let mut ship = session();
        ship.logged_in = true;
        let mut inventory = ship.inventory.clone();
        inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3254, 1));
        inventory
            .validate_for_profile(&ship.account.character, &ship.items, &ship.skill_catalog)
            .unwrap();
        ship.commit_inventory(inventory).unwrap();
        let turn = ship.equipment.ship.turn_rate;
        ship.energy = ship.equipment.ship.energy_capacity;
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 1]].concat();
        ship.activate_equipped_item(&activation).unwrap();
        assert!((ship.equipment.ship.turn_rate - turn * 1.28).abs() < 0.01);
        assert_eq!(ship.active_device.as_ref().unwrap().slot, 1);
    }

    /// Maximize Shields modifies actual capacity while retaining current points.
    #[test]
    fn maximize_shields_device_changes_capacity_and_restores_it() {
        let mut ship = session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22497".parse().unwrap());
        let mut inventory = ship.inventory.clone();
        inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3343, 1));
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(5894, 1));
        inventory
            .validate_for_profile(&ship.account.character, &ship.items, &ship.skill_catalog)
            .unwrap();
        ship.commit_inventory(inventory).unwrap();
        let capacity = ship.equipment.ship.shield_capacity;
        ship.shield = capacity / 2.;
        let shield = ship.shield;
        ship.energy = ship.equipment.ship.energy_capacity;
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 9]].concat();
        ship.activate_equipped_item(&activation).unwrap();
        assert!((ship.equipment.ship.shield_capacity - capacity * 1.05).abs() < 0.01);
        assert_eq!(ship.shield, shield);
        assert!(ship.pending.iter().any(|frame| {
            frame.opcode == 0x1b
                && frame
                    .body
                    .windows(b"Maximize_Shields".len())
                    .any(|window| window == b"Maximize_Shields")
        }));
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!((restored.equipment.ship.shield_capacity - capacity * 1.05).abs() < 0.01);
        restored.active_device.as_mut().unwrap().expires_at_ms = crate::resources::unix_ms() - 1;
        restored.advance_active_device().unwrap();
        assert_eq!(restored.equipment.ship.shield_capacity, capacity);
        assert_eq!(restored.shield, shield);
    }

    #[test]
    fn catalog_scan_and_speed_devices_change_ship_stats_and_survive_recovery() {
        let mut ship = session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22492".parse().unwrap());
        let mut inventory = ship.inventory.clone();
        inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3343, 1));
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(2500, 1));
        ship.commit_inventory(inventory).unwrap();
        let base_scan = ship.equipment.ship.scan_range;
        let base_speed = ship.motion.max_speed;
        ship.energy = ship.equipment.ship.energy_capacity;
        let energy = ship.energy;
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 9]].concat();
        ship.activate_equipped_item(&activation).unwrap();
        assert_eq!(ship.energy, energy - 1437.);
        assert!(ship.equipment.ship.scan_range > base_scan);
        assert_eq!(ship.motion.max_speed, base_speed);

        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(restored.equipment.ship.scan_range > base_scan);
        restored.active_device.as_mut().unwrap().expires_at_ms = crate::resources::unix_ms() - 1;
        restored.advance_active_device().unwrap();
        assert_eq!(restored.equipment.ship.scan_range, base_scan);

        let mut inventory = restored.inventory.clone();
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(2440, 1));
        restored.commit_inventory(inventory).unwrap();
        let base_speed = restored.motion.max_speed;
        restored.energy = restored.equipment.ship.energy_capacity;
        restored.activate_equipped_item(&activation).unwrap();
        assert!(restored.motion.max_speed > base_speed);
        assert_eq!(
            restored.active_device.as_ref().unwrap().effect,
            session_equipment::ActiveEffect::SpeedBoost
        );

        restored.active_device.as_mut().unwrap().expires_at_ms = crate::resources::unix_ms() - 1;
        restored.advance_active_device().unwrap();
        let mut inventory = restored.inventory.clone();
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(5364, 1));
        restored.commit_inventory(inventory).unwrap();
        let base_capacity = restored.equipment.ship.energy_capacity;
        restored.energy = base_capacity;
        restored.activate_equipped_item(&activation).unwrap();
        assert!(restored.equipment.ship.energy_capacity > base_capacity);
        restored.active_device.as_mut().unwrap().expires_at_ms = crate::resources::unix_ms() - 1;
        restored.advance_active_device().unwrap();
        assert_eq!(restored.equipment.ship.energy_capacity, base_capacity);
    }

    #[test]
    fn activated_recharge_devices_change_actual_energy_and_shield_regeneration() {
        let mut ship = session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22493".parse().unwrap());
        let mut inventory = ship.inventory.clone();
        inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3343, 1));
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1810, 1));
        ship.commit_inventory(inventory).unwrap();
        let base_energy_recharge = ship.equipment.ship.energy_recharge;
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 9]].concat();
        ship.energy = ship.equipment.ship.energy_capacity - 100.;
        ship.activate_equipped_item(&activation).unwrap();
        assert_eq!(
            ship.equipment.ship.energy_recharge,
            base_energy_recharge + 4.
        );
        let energy = ship.energy;
        ship.advance_ship_resources(1.);
        assert!((ship.energy - energy - base_energy_recharge - 4.).abs() < 0.001);
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(
            restored.equipment.ship.energy_recharge,
            base_energy_recharge + 4.
        );
        restored.active_device.as_mut().unwrap().expires_at_ms = crate::resources::unix_ms() - 1;
        restored.advance_active_device().unwrap();
        assert_eq!(
            restored.equipment.ship.energy_recharge,
            base_energy_recharge
        );

        let mut inventory = restored.inventory.clone();
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1805, 1));
        restored.commit_inventory(inventory).unwrap();
        let base_shield_recharge = restored.equipment.ship.shield_recharge;
        restored.shield = 0.;
        restored.activate_equipped_item(&activation).unwrap();
        assert_eq!(restored.active_device.as_ref().unwrap().operation, 2);
        assert_eq!(
            restored.equipment.ship.shield_recharge,
            base_shield_recharge * 1.1
        );
        restored.advance_ship_resources(1.);
        assert!((restored.shield - restored.equipment.ship.shield_recharge).abs() < 0.001);
    }

    #[test]
    fn activated_plasma_deflect_reduces_authoritative_incoming_damage() {
        let mut ship = session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22494".parse().unwrap());
        let mut inventory = ship.inventory.clone();
        inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3343, 1));
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1893, 1));
        ship.commit_inventory(inventory).unwrap();
        ship.energy = ship.equipment.ship.energy_capacity;
        ship.shield = 200.;
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 9]].concat();
        ship.activate_equipped_item(&activation).unwrap();
        assert_eq!(ship.equipment.ship.deflects[2], 20.);
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(restored.equipment.ship.deflects[2], 20.);
        restored.shield = 200.;
        lock_combat(&restored.combat).unwrap().actors[0]
            .combat
            .damage_type = 2;
        restored.receive_hit(0, 100.).unwrap();
        let reduced_loss = 200. - restored.shield;
        restored.active_device.as_mut().unwrap().expires_at_ms = crate::resources::unix_ms() - 1;
        restored.advance_active_device().unwrap();
        assert_eq!(restored.equipment.ship.deflects[2], 0.);
        restored.shield = 200.;
        restored.receive_hit(0, 100.).unwrap();
        assert!(
            200. - restored.shield > reduced_loss,
            "buffed_loss={reduced_loss}; unbuffed_loss={}; shield={}",
            200. - restored.shield,
            restored.shield
        );
    }

    #[test]
    /// Dual-stat handling devices change weapon damage and actual hit rolls.
    fn activated_handling_devices_change_beam_and_projectile_combat() {
        let state_dir =
            std::env::temp_dir().join(format!("enb-handling-test-{}", rand::random::<u64>()));
        crate::accounts::AccountDirectory::new(&state_dir)
            .create_class(
                "warrior".into(),
                "example",
                "Warrior".into(),
                crate::classes::CharacterClass::parse("te").unwrap(),
            )
            .unwrap();
        let mut ship = PlayerSession::new(
            serde_json::from_str(include_str!("../data/sector.json")).unwrap(),
            Ipv4Addr::new(127, 0, 0, 2),
            state_dir,
        )
        .unwrap();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22495".parse().unwrap());
        let mut inventory = ship.inventory.clone();
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1925, 1));
        ship.commit_inventory(inventory).unwrap();
        let base_beam = ship.equipment.weapons[0].damage;
        let base_beam_chances = ship.equipment.weapons[0].hit_chances;
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 9]].concat();
        ship.energy = ship.equipment.ship.energy_capacity;
        ship.activate_equipped_item(&activation).unwrap();
        assert!(ship.equipment.weapons[0].damage > base_beam);
        assert_ne!(ship.equipment.weapons[0].hit_chances, base_beam_chances);
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(restored.equipment.weapons[0].damage > base_beam);
        restored.active_device.as_mut().unwrap().expires_at_ms = crate::resources::unix_ms() - 1;
        restored.advance_active_device().unwrap();
        assert_eq!(restored.equipment.weapons[0].damage, base_beam);

        let mut inventory = restored.inventory.clone();
        inventory.equipped[3] = Some(crate::inventory::ItemInstance::stock(3022, 1));
        inventory.ammunition[3] = Some(crate::inventory::ItemInstance::stock(512, 10));
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1806, 1));
        restored.commit_inventory(inventory).unwrap();
        let base_projectile = restored.equipment.weapons[0].damage;
        let base_projectile_chances = restored.equipment.weapons[0].hit_chances;
        restored.energy = restored.equipment.ship.energy_capacity;
        restored.activate_equipped_item(&activation).unwrap();
        assert!(restored.equipment.weapons[0].damage > base_projectile);
        assert_ne!(
            restored.equipment.weapons[0].hit_chances,
            base_projectile_chances
        );
    }

    #[test]
    /// Shunt and Turbo Warp change their live resource and movement parameters.
    fn activated_shunt_and_turbo_warp_change_authoritative_ship_stats() {
        let mut ship = session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22496".parse().unwrap());
        let mut inventory = ship.inventory.clone();
        inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3343, 1));
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1950, 1));
        ship.commit_inventory(inventory).unwrap();
        let base_energy = ship.equipment.ship.energy_recharge;
        let base_shield = ship.equipment.ship.shield_recharge;
        let activation = [ship.player_id().to_le_bytes().as_slice(), &[2, 9]].concat();
        ship.energy = ship.equipment.ship.energy_capacity;
        ship.activate_equipped_item(&activation).unwrap();
        assert!((ship.equipment.ship.energy_recharge - base_energy * 1.25).abs() < 0.001);
        assert!((ship.equipment.ship.shield_recharge - base_shield * 0.75).abs() < 0.001);
        ship.active_device.as_mut().unwrap().expires_at_ms = crate::resources::unix_ms() - 1;
        ship.advance_active_device().unwrap();
        let state_dir =
            std::env::temp_dir().join(format!("enb-turbo-warp-test-{}", rand::random::<u64>()));
        crate::accounts::AccountDirectory::new(&state_dir)
            .create_class(
                "trader".into(),
                "example",
                "Trader".into(),
                crate::classes::CharacterClass::parse("tt").unwrap(),
            )
            .unwrap();
        let mut traveler = PlayerSession::new(
            serde_json::from_str(include_str!("../data/sector.json")).unwrap(),
            Ipv4Addr::new(127, 0, 0, 2),
            state_dir,
        )
        .unwrap();
        traveler.logged_in = true;
        traveler.selected_peer = Some("127.0.0.1".parse().unwrap());
        traveler.endpoint = Some("127.0.0.1:22497".parse().unwrap());
        let mut inventory = traveler.inventory.clone();
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1836, 1));
        traveler.commit_inventory(inventory).unwrap();
        let base_warp = traveler.warp.speed;
        traveler.energy = traveler.equipment.ship.energy_capacity;
        let activation = [traveler.player_id().to_le_bytes().as_slice(), &[2, 9]].concat();
        traveler.activate_equipped_item(&activation).unwrap();
        let active = traveler.active_device.as_ref().unwrap();
        assert_eq!(active.effect, session_equipment::ActiveEffect::TurboWarp);
        assert!(active.valid_for(
            &traveler.inventory,
            &traveler.items,
            crate::resources::unix_ms()
        ));
        assert_eq!(active.modifiers()[0].stat, "STAT_WARP");
        assert_eq!(traveler.equipment.ship.warp_speed, base_warp + 750.);
        assert_eq!(traveler.warp.speed, base_warp + 750.);
        let mut restored = PlayerSession::new(
            traveler.sector.clone(),
            traveler.bind,
            traveler.state_dir.clone(),
        )
        .unwrap();
        assert_eq!(restored.warp.speed, base_warp + 750.);
        restored.active_device.as_mut().unwrap().expires_at_ms = crate::resources::unix_ms() - 1;
        restored.advance_active_device().unwrap();
        assert_eq!(restored.warp.speed, base_warp);
    }

    #[test]
    fn legacy_turbo_checkpoint_and_catalog_mismatch_are_validated() {
        let ship = session();
        let active: session_equipment::ActiveDevice = serde_json::from_value(serde_json::json!({
            "slot": 9,
            "template": 2265,
            "weapon_turbo_percent": 7.0,
            "expires_at_ms": crate::resources::unix_ms() + 60_000
        }))
        .unwrap();
        assert_eq!(active.effect, session_equipment::ActiveEffect::TurboWeapons);
        assert_eq!(active.value, 7.);
        let mut inventory = ship.inventory.clone();
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(2265, 1));
        assert!(active.valid_for(&inventory, &ship.items, crate::resources::unix_ms()));
        let mut modified = active;
        modified.value = 95.;
        assert!(!modified.valid_for(&inventory, &ship.items, crate::resources::unix_ms()));
    }

    #[test]
    fn reactor_and_engine_removal_affect_regeneration_movement_and_warp() {
        let mut ship = session();
        ship.logged_in = true;
        ship.motion.thrust(2).unwrap();
        ship.advance_player(1.).unwrap();
        assert!(ship.position[0] > 0.);
        let stopped = ship.position;
        ship.unequip("2").unwrap();
        assert_eq!(ship.motion.max_speed, 0.);
        assert_eq!(ship.motion.speed, 0.);
        ship.advance_player(1.).unwrap();
        assert_eq!(ship.position, stopped);
        assert!(ship
            .warp
            .start(VecDeque::new(), ship.position, &mut ship.motion)
            .is_err());
        ship.equip_cargo("0 2").unwrap();
        assert_eq!(ship.motion.max_speed, 192.);
        assert_eq!(ship.warp.speed, 2700.);
        ship.unequip("1").unwrap();
        assert_eq!(ship.energy, 0.);
        ship.advance_player(1.).unwrap();
        assert_eq!(ship.energy, 0.);
        ship.equip_cargo("0 1").unwrap();
        ship.advance_player(1.).unwrap();
        assert!((ship.energy - 5.4).abs() < 0.001);
        ship.shield = 0.;
        ship.advance_player(1.).unwrap();
        assert!((ship.shield - 1.8).abs() < 0.001);
        ship.energy = 0.1;
        ship.warp
            .start(VecDeque::new(), ship.position, &mut ship.motion)
            .unwrap();
        ship.warp
            .advance(
                &mut ship.position,
                &mut ship.motion,
                &ship.sector.bounds,
                2.1,
            )
            .unwrap();
        ship.advance_player(0.1).unwrap();
        assert_eq!(ship.energy, 0.);
        assert!(!ship.warp.active());
    }

    #[test]
    fn restart_preserves_weapon_firing_and_reload_and_reconciles_derived_parameters() {
        let mut ship = session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:21001".parse().unwrap());
        ship.weapon_firing[3] = true;
        ship.player_firing = true;
        ship.weapon_cooldowns[3] = 1.5;
        ship.checkpoint_session(true).unwrap();
        let restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(restored.logged_in);
        assert!(restored.weapon_firing[3]);
        assert!(restored.player_firing);
        assert_eq!(restored.weapon_cooldowns[3], 1.5);
        assert_eq!(restored.motion.max_speed, 192.);
        assert_eq!(restored.warp.speed, 2700.);
    }

    #[test]
    fn denied_equipment_moves_explain_failure_and_resync_without_saving() {
        let mut ship = session();
        ship.logged_in = true;
        let before = ship.inventory.clone();
        let request = [ship.player_id(), 2, 2, 2, 0, 1]
            .into_iter()
            .flat_map(u32::to_be_bytes)
            .collect::<Vec<_>>();
        ship.move_inventory(&request).unwrap();
        assert_eq!(ship.inventory, before);
        assert!(!ship.state_dir.join("inventory.json").exists());
        assert_eq!(ship.pending[0].opcode, 0x1d);
        assert_eq!(
            ship.pending
                .iter()
                .filter(|frame| frame.opcode == 0x1b)
                .count(),
            3
        );
    }

    #[test]
    fn inventory_packets_validate_ownership_and_persist_before_publishing() {
        let mut ship = session();
        ship.logged_in = true;
        let original = ship.inventory.clone();
        let packet = |owner: u32| {
            [owner, 2, 2, 1, 0, 1]
                .into_iter()
                .flat_map(u32::to_be_bytes)
                .collect::<Vec<_>>()
        };
        assert!(ship.move_inventory(&packet(ship.player_id() + 1)).is_err());
        assert!(ship
            .move_inventory(&packet(ship.player_id())[..23])
            .is_err());
        assert_eq!(ship.inventory, original);
        // Simulate a storage failure without editing any real character data.
        fs::create_dir_all(&ship.state_dir).unwrap();
        let blocker = ship.state_dir.join("inventory.json.tmp");
        fs::create_dir(&blocker).unwrap();
        assert!(ship.move_inventory(&packet(ship.player_id())).is_err());
        assert_eq!(ship.inventory, original);
        assert!(ship.pending.is_empty());
        fs::remove_dir(blocker).unwrap();
        ship.move_inventory(&packet(ship.player_id())).unwrap();
        assert!(ship.inventory.equipped[2].is_none());
        assert_eq!(ship.inventory.cargo[0], original.equipped[2]);
        assert_eq!(ship.pending.last().unwrap().opcode, 0x1b);
        let restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(restored.inventory, ship.inventory);
        ship.hull = 0.;
        assert!(ship.move_inventory(&packet(ship.player_id())).is_err());
    }

    #[test]
    fn abilities_validate_targets_charge_once_and_apply_cloak_and_leech() {
        let mut session = session();
        session.logged_in = true;
        let before = session.energy;
        session.target = u32::MAX;
        assert!(session.begin_ability(106).is_err());
        assert_eq!(session.energy, before); // invalid target never spends energy
        session.begin_ability(28).unwrap();
        assert!(session.begin_ability(28).is_err()); // one cast at a time
        for _ in 0..32 {
            session.advance_abilities(0.1).unwrap();
        }
        assert_eq!(session.skills.cloak_rank, 1);
        session.begin_ability(28).unwrap(); // toggling off bypasses cooldown/cost
        assert_eq!(session.skills.cloak_rank, 0);
        session.target = MOB_ID;
        lock_combat(&session.combat).unwrap().actors[0].position = [100.; 3];
        let hull = lock_combat(&session.combat).unwrap().actors[0].health;
        let shield = lock_combat(&session.combat).unwrap().actors[0].shield;
        session.begin_ability(106).unwrap();
        for _ in 0..12 {
            session.advance_abilities(0.1).unwrap();
        }
        assert_eq!(
            lock_combat(&session.combat).unwrap().actors[0].shield,
            shield - 75.
        );
        assert_eq!(lock_combat(&session.combat).unwrap().actors[0].health, hull);
        assert!(session.begin_ability(106).is_err()); // shared skill recovery
    }

    #[test]
    fn fold_space_moves_five_kilometers_and_wormhole_persists_destination() {
        let mut session = session();
        session.logged_in = true;
        session.begin_ability(126).unwrap();
        for _ in 0..12 {
            session.advance_abilities(0.1).unwrap();
        }
        assert!((distance(session.position, [0.; 3]) - 5000.).abs() < 1.);
        session.begin_ability(67).unwrap();
        for _ in 0..32 {
            session.advance_abilities(0.1).unwrap();
        }
        assert_eq!(session.sector.id, 1910);
        assert!(!session.logged_in);
        assert!(session.pending.iter().any(|frame| frame.opcode == 0x3a));
        let restored = PlayerSession::new(
            serde_json::from_str(include_str!("../data/sector.json")).unwrap(),
            session.bind,
            session.state_dir.clone(),
        )
        .unwrap();
        assert_eq!(restored.sector.id, 1910);
    }

    #[test]
    fn restart_preserves_udp_identity_sequence_clock_and_active_warp() {
        let mut original = session();
        original.logged_in = true;
        original.selected_peer = Some("127.0.0.1".parse().unwrap());
        original.endpoint = Some("127.0.0.1:23456".parse().unwrap());
        original.comms = Some("127.0.0.1:23457".parse().unwrap());
        original.sequence = 318;
        original.inputs.push_back(300);
        original
            .handle_input(
                0x9b,
                &[
                    FIRST_PLAYER_ID.to_le_bytes().as_slice(),
                    &0u16.to_le_bytes(),
                ]
                .concat(),
            )
            .unwrap();
        original.tick_offset = 100_000;
        original.checkpoint_session(true).unwrap();
        let mut replacement = PlayerSession::new(
            original.sector.clone(),
            original.bind,
            original.state_dir.clone(),
        )
        .unwrap();
        assert!(replacement.logged_in);
        assert_eq!(replacement.endpoint, original.endpoint);
        assert_eq!(replacement.comms, original.comms);
        assert_eq!(replacement.sequence, 318);
        assert!(replacement.tick_ms() >= 100_000);
        assert_eq!(replacement.warp.phase, WarpPhase::Charging);
        // The same socket can cancel without a new handoff or authentication.
        replacement
            .handle_udp(
                SECTOR_PORT,
                original.endpoint.unwrap(),
                Datagram {
                    opcode: 0x14,
                    player: FIRST_PLAYER_ID,
                    sequence: 301,
                    body: [FIRST_PLAYER_ID.to_le_bytes().as_slice(), &[4]].concat(),
                },
            )
            .unwrap();
        assert_eq!(replacement.warp.phase, WarpPhase::Recovering);
        replacement.logout().unwrap();
        assert!(!replacement.state_dir.join("session.local.json").exists());
        let clean = PlayerSession::new(
            replacement.sector.clone(),
            replacement.bind,
            replacement.state_dir.clone(),
        )
        .unwrap();
        assert!(!clean.logged_in);
    }

    #[test]
    fn selecting_a_gate_updates_the_ship_target_without_resetting_its_state() {
        let mut session = session();
        session.logged_in = true;
        let target = session
            .sector
            .navpoints
            .iter()
            .find(|n| n.object_type == 11)
            .unwrap()
            .id;
        let request = [FIRST_PLAYER_ID.to_le_bytes(), target.to_le_bytes()].concat();
        session.handle_input(0x17, &request).unwrap();
        assert_eq!(session.target, target);
        let display = session.pending.iter().find(|f| f.opcode == 0x19).unwrap();
        assert_eq!(
            display.body,
            [target.to_le_bytes(), u32::MAX.to_le_bytes()].concat()
        );
        let actor = session.pending.iter().find(|f| f.opcode == 0x1b).unwrap();
        assert_eq!(&actor.body[..4], &FIRST_PLAYER_ID.to_le_bytes());
        assert_eq!(&actor.body[7..15], &[2, 0, 0, 128, 0, 0, 0, 0]);
        assert_eq!(&actor.body[15..], &target.to_le_bytes());
        assert!(session.pending.iter().any(|f| f.opcode == 0x5c));
    }

    #[test]
    fn warp_request_supports_free_flight_and_stops_without_resetting_inventory() {
        let mut session = session();
        session.logged_in = true;
        let request = [
            FIRST_PLAYER_ID.to_le_bytes().as_slice(),
            &0u16.to_le_bytes(),
        ]
        .concat();
        session.handle_input(0x9b, &request).unwrap();
        assert_eq!(session.warp.phase, WarpPhase::Charging);
        assert!(session.pending.iter().any(|f| f.opcode == 9));
        for frame in session.pending.iter().filter(|f| f.opcode == 0x1b) {
            assert_eq!(frame.body[7], 2); // one-bit diff: no default resets
        }
        session.handle_input(0x9b, &request).unwrap();
        assert_eq!(session.warp.phase, WarpPhase::Recovering);
        assert!(session.warp_effects.is_empty());
        assert!(session.pending.iter().any(|f| f.opcode == 0xf));
        session.handle_input(0x9b, &request).unwrap();
        assert_eq!(session.warp.phase, WarpPhase::Recovering);
        session.logout().unwrap();
        assert_eq!(session.warp.phase, WarpPhase::Idle);
    }

    #[test]
    fn real_sector_content_has_station_gates_and_valid_geometry() {
        let sector: Sector =
            serde_json::from_str(include_str!("../data/asteroid_belt_alpha.json")).unwrap();
        sector.validate().unwrap();
        assert_eq!(sector.name, "Asteroid Belt Alpha");
        assert!(sector.navpoints.len() > 400);
        assert!(sector
            .navpoints
            .iter()
            .any(|n| n.name == "ECS Dauntless" && n.object_type == 12));
        assert!(sector
            .navpoints
            .iter()
            .any(|n| n.gate_destination == Some(1060)));
        for nav in &sector.navpoints {
            assert!((nav.orientation.iter().map(|v| v * v).sum::<f32>() - 1.).abs() < 0.001);
        }
    }

    #[test]
    fn impulse_commands_require_owner_and_fixed_reports_require_registered_endpoint() {
        let mut session = session();
        let peer: SocketAddr = "127.0.0.1:23000".parse().unwrap();
        session.selected_peer = Some(peer.ip());
        session.comms = Some(peer);
        session.logged_in = true;
        let mut command = Vec::from((FIRST_PLAYER_ID + 1).to_le_bytes());
        command.push(2);
        assert!(session.handle_input(0x14, &command).is_err());
        command[..4].copy_from_slice(&FIRST_PLAYER_ID.to_le_bytes());
        session.handle_input(0x14, &command).unwrap();
        for _ in 0..10 {
            session.advance(0.1).unwrap();
        }
        assert!(session.position[0] > 30.);
        let previous = session.position;
        let packet = Datagram {
            opcode: 0x1004,
            player: FIRST_PLAYER_ID,
            sequence: 0,
            body: [50f32, 10., 0., 0., 1., 0.]
                .into_iter()
                .flat_map(f32::to_le_bytes)
                .collect(),
        };
        session
            .handle_udp(3806, "127.0.0.1:23001".parse().unwrap(), packet.clone())
            .unwrap();
        assert_eq!(session.position, previous);
        session.handle_udp(3806, peer, packet).unwrap();
        assert_eq!(session.position, [50., 10., 0.]);
        assert!(session.motion.heading()[1] > 0.99);
        let timestamped = Datagram {
            opcode: 0x1004,
            player: FIRST_PLAYER_ID,
            sequence: 0,
            body: [60f32, 10., 0., 0., 1., 0.]
                .into_iter()
                .flat_map(f32::to_le_bytes)
                .chain(1234u32.to_le_bytes())
                .collect(),
        };
        session.handle_udp(3806, peer, timestamped).unwrap();
        assert_eq!(session.position, [60., 10., 0.]);
        session
            .handle_input(
                0x14,
                &[FIRST_PLAYER_ID.to_le_bytes().as_slice(), &[4]].concat(),
            )
            .unwrap();
        for _ in 0..20 {
            session.advance(0.1).unwrap();
        }
        assert_eq!(session.motion.speed, 0.);
    }
    pub(super) fn session() -> PlayerSession {
        PlayerSession::new(
            serde_json::from_str(include_str!("../data/sector.json")).unwrap(),
            Ipv4Addr::new(127, 0, 0, 2),
            std::env::temp_dir().join(format!("enb-rust-test-{}", rand::random::<u64>())),
        )
        .unwrap()
    }

    /// Remote vault packets must never move owned items outside a station.
    #[test]
    fn vault_transfer_requires_a_station_and_preserves_items_when_rejected() {
        let mut ship = session();
        ship.logged_in = true;
        ship.inventory.cargo[0] = Some(crate::inventory::ItemInstance::stock(2837, 1));
        let original = ship.inventory.clone();
        let transfer = ItemTransfer {
            source: 1,
            source_slot: 0,
            destination: 3,
            destination_slot: 0,
            quantity: 1,
        };
        ship.transfer_inventory(transfer).unwrap();
        assert_eq!(ship.inventory, original);
        assert!(ship.pending.iter().any(|frame| frame.opcode == 0x1d));

        ship.sector = ship.sectors.sector(10761).unwrap();
        ship.transfer_inventory(transfer).unwrap();
        assert!(ship.inventory.cargo[0].is_none());
        assert_eq!(ship.inventory.vault[0], original.cargo[0]);
    }

    /// Publish XP only after its inventory replacement reaches disk.
    #[test]
    fn earned_xp_is_committed_before_publishing_player_progress() {
        let mut ship = session();
        ship.logged_in = true;
        let mut candidate = ship.inventory.clone();
        candidate.xp_start_level = 0;
        candidate.explore_xp = 500;
        let before = ship.pending.len();
        ship.commit_inventory(candidate).unwrap();
        assert_eq!(
            crate::experience::track(ship.inventory.xp_start_level, ship.inventory.explore_xp)
                .unwrap()
                .level,
            1
        );
        assert!(ship.pending[before..]
            .iter()
            .any(|frame| frame.opcode == 0x1b));
        let saved: Inventory =
            serde_json::from_slice(&fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved.explore_xp, 500);
        assert_eq!(saved.xp_start_level, 0);
    }

    /// A durable kill event replays safely across failed and repeated commits.
    #[test]
    fn combat_kill_award_commits_once_before_world_acknowledgment() {
        let mut ship = session();
        ship.logged_in = true;
        let birth = crate::loot_world::CorpseBirth {
            death: crate::loot_world::MobDeath {
                spawn: 9999,
                generation: 1,
            },
            template: 1,
            name: "XP target".into(),
            level: 50,
            killer: ship.player_id(),
            position: [0.; 3],
            now_ms: crate::resources::unix_ms(),
        };
        let catalog = crate::loot::LootCatalog::load().unwrap();
        lock_loot(&ship.loot_world)
            .unwrap()
            .spawn(birth, &catalog, &ship.items)
            .unwrap();
        let award = lock_loot(&ship.loot_world)
            .unwrap()
            .pending_awards(ship.player_id())
            .pop()
            .unwrap();
        fs::create_dir_all(&ship.state_dir).unwrap();
        let blocked = ship.state_dir.join("inventory.json.tmp");
        fs::create_dir(&blocked).unwrap();
        assert!(ship.settle_kill_award(&award).is_err());
        assert_eq!(ship.inventory.combat_xp, 0);
        assert_eq!(
            lock_loot(&ship.loot_world)
                .unwrap()
                .pending_awards(ship.player_id())
                .len(),
            1
        );
        fs::remove_dir(&blocked).unwrap();

        ship.settle_kill_award(&award).unwrap();
        assert_eq!(ship.inventory.combat_xp, 1000);
        ship.settle_kill_award(&award).unwrap(); // world acknowledgment was interrupted
        assert_eq!(ship.inventory.combat_xp, 1000);
        lock_loot(&ship.loot_world)
            .unwrap()
            .acknowledge_award(&award)
            .unwrap();
        assert!(lock_loot(&ship.loot_world)
            .unwrap()
            .pending_awards(ship.player_id())
            .is_empty());
        let restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(restored.inventory.combat_xp, 1000);
        assert_eq!(restored.inventory.combat_awards[&award.receipt_key()], 1);
    }
    #[test]
    fn resources_stream_once_and_leave_visibility_without_duplicate_creation() {
        let mut ship = session();
        ship.logged_in = true;
        let mut node = ship.sector.navpoints[0].clone();
        node.id = 0x20000001;
        node.object_type = 38;
        node.asset = 1822;
        node.name = "Asteroid (L3)".into();
        node.position = [100., 0., 0.];
        ship.sector.resources.push(crate::content::ResourceNode {
            object: node,
            field_id: 1,
            level: 3,
            resource_type: 2,
            ore_items: vec![crate::content::ResourceOre {
                item_id: 2837,
                frequency: 1.,
            }],
        });
        ship.resource_field = Arc::new(Mutex::new(
            ResourceField::load(
                &ship.sector,
                &ship.items,
                ship.state_dir.join("resource-test.json"),
            )
            .unwrap(),
        ));
        ship.publish_resources().unwrap();
        assert!(ship.visible_resources.contains(&0x20000001));
        assert!(ship
            .pending
            .iter()
            .any(|frame| frame.opcode == 0x2018 && frame.body[4] == 38));
        assert!(ship
            .pending
            .iter()
            .any(|frame| frame.opcode == 0x1b && frame.body[..4] == 0x20000001u32.to_le_bytes()));
        let created = ship
            .pending
            .iter()
            .filter(|frame| frame.opcode == 0x2018)
            .count();
        ship.last_resource_visibility = None;
        ship.publish_resources().unwrap();
        assert_eq!(
            ship.pending
                .iter()
                .filter(|frame| frame.opcode == 0x2018)
                .count(),
            created
        );
        assert_eq!(
            ship.warp_waypoint(0x20000001).unwrap().position,
            [100., 0., 0.]
        );
        // A server replacement keeps the client's already-created IDs. Do not
        // send Create again merely because per-process visibility was reset.
        ship.position = [0.; 3];
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22346".parse().unwrap());
        ship.save().unwrap();
        ship.checkpoint_session(true).unwrap();
        let mut restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        restored.publish_resources().unwrap();
        assert!(restored.visible_resources.contains(&0x20000001));
        assert!(!restored.pending.iter().any(|frame| frame.opcode == 0x2018));
        ship.position = [50_000., 0., 0.];
        ship.last_resource_visibility = None;
        ship.publish_resources().unwrap();
        assert!(ship.visible_resources.is_empty());
        assert!(ship
            .pending
            .iter()
            .any(|frame| frame.opcode == 7 && frame.body == 0x20000001u32.to_le_bytes()));
        assert!(ship.warp_waypoint(0x20000001).is_err());
    }
    #[test]
    fn self_jumpstart_and_native_tow_recover_incapacitated_ships() {
        let mut ship = session();
        ship.logged_in = true;
        ship.hull = 0.;
        ship.target = ship.player_id();
        ship.begin_ability(65).unwrap();
        for _ in 0..80 {
            ship.advance(0.1).unwrap();
        }
        assert_eq!(ship.hull, ship.equipment.ship.hull_capacity * 0.7);
        assert!(ship.shield > 0. && ship.shield <= ship.equipment.ship.shield_capacity);
        assert!(ship.tow_to_station().is_err());
        ship.hull = 0.;
        let owner = ship.player_id();
        ship.handle_input(0x8d, &owner.to_le_bytes()).unwrap();
        assert!(ship.pending.iter().any(|frame| frame.opcode == 0x54));
        assert!(ship
            .handle_input(0x55, &[0u32.to_le_bytes().as_slice(), &[0]].concat())
            .is_err());
        ship.handle_input(0x55, &[owner.to_le_bytes().as_slice(), &[0]].concat())
            .unwrap();
        assert_eq!(ship.sector.id, 10761);
        assert_eq!(ship.hull, ship.equipment.ship.hull_capacity);
        assert!(ship.pending.iter().any(|frame| frame.opcode == 0x3a));
    }
    #[test]
    fn warp_can_resolve_a_nearby_live_mob_but_rejects_dead_or_unknown_targets() {
        let ship = session();
        let mob = MOB_ID;
        assert!(ship.warp_waypoint(mob).is_err()); // outside the visibility radius
        {
            let mut combat = lock_combat(&ship.combat).unwrap();
            combat.actors[0].position = [10_000., 0., 0.];
        }
        assert_eq!(ship.warp_waypoint(mob).unwrap().stand_off, 800.);
        lock_combat(&ship.combat).unwrap().actors[0].damage(1_000_000.);
        assert!(ship.warp_waypoint(mob).is_err());
        assert!(ship.warp_waypoint(0x20000001).is_err());
    }
    #[test]
    fn station_docking_requires_clearance_and_launch_restores_exterior_sector() {
        let mut session = session();
        session.sector = session.sectors.sector(1076).unwrap();
        session.logged_in = true;
        session.target = 100002;
        session.selected_peer = Some("127.0.0.1".parse().unwrap());
        session.endpoint = Some("127.0.0.1:22345".parse().unwrap());
        let exterior = session
            .sector
            .navpoints
            .iter()
            .find(|nav| nav.id == 100002)
            .unwrap()
            .position;
        assert!(session.complete_docking(100002).is_err());
        assert!(session.begin_docking(100002).is_err()); // too far
        session.position = [exterior[0] + 4500., exterior[1], exterior[2]];
        assert!(session.begin_docking(100000).is_err());
        session.begin_docking(100002).unwrap();
        assert!(session
            .pending
            .iter()
            .any(|frame| frame.opcode == 0x92 && frame.body[..4] == 5u32.to_be_bytes()));
        session.complete_docking(100002).unwrap();
        assert_eq!(session.sector.id, 10761);
        assert!(!session.logged_in);
        assert!(session.pending.iter().any(|frame| frame.opcode == 0x3a));
        let mut session = PlayerSession::new(
            session.sectors.sector(1076).unwrap(),
            session.bind,
            session.state_dir.clone(),
        )
        .unwrap();
        assert!(!session.logged_in);
        let handoff = session
            .pending
            .iter()
            .find(|frame| frame.opcode == 0x3a)
            .unwrap();
        assert_eq!(&handoff.body[24..28], &1076u32.to_be_bytes());
        let ticket = packets::character_ticket(10761, &session.nonce, session.player_id());
        let mut login = vec![0; 137];
        login[..64].copy_from_slice(&ticket.body[4..]);
        session.handle_input(2, &login).unwrap();
        assert!(session.pending.iter().any(|frame| frame.opcode == 0x52
            && frame.body.len()
                == 132 + 265 * session.sector.station.as_ref().unwrap().npcs.len()));
        assert!(session.pending.iter().any(|frame| frame.opcode == 0x4f
            && frame.body == [10761u32.to_le_bytes().as_slice(), &[0, 0]].concat()));
        assert!(session
            .handle_input(
                0x9b,
                &[session.player_id().to_le_bytes().as_slice(), &[0, 0]].concat()
            )
            .is_err());
        session.advance_player(1.).unwrap();
        assert_eq!(session.position, [0.; 3]);
        let movement = [
            session.player_id().to_le_bytes().as_slice(),
            &0u32.to_le_bytes(),
            &1f32.to_le_bytes(),
            &10f32.to_le_bytes(),
            &20f32.to_le_bytes(),
            &30f32.to_le_bytes(),
            &1u32.to_le_bytes(),
        ]
        .concat();
        session.handle_input(0x9d, &movement).unwrap();
        assert_eq!(
            session.station_pose.as_ref().unwrap().position,
            [10., 20., 30.]
        );
        assert_eq!(session.position, [0.; 3]); // avatar reports cannot move the ship
        let mut spoofed = movement.clone();
        spoofed[..4].copy_from_slice(&(session.player_id() + 1).to_le_bytes());
        assert!(session.handle_input(0x9d, &spoofed).is_err());
        assert!(session
            .handle_input(
                0x9f,
                &[
                    session.player_id().to_le_bytes(),
                    99u32.to_le_bytes(),
                    0u32.to_le_bytes()
                ]
                .concat()
            )
            .is_err());
        assert!(session.station_request(99999, 1).is_err());
        session.station_request(10761, 1).unwrap();
        assert_eq!(session.sector.id, 1076);
        assert!(distance(session.position, exterior) >= 4499.);
        let saved: SavedPlayer =
            serde_json::from_slice(&fs::read(session.state_dir.join("player.json")).unwrap())
                .unwrap();
        assert_eq!(saved.sector, 1076);
    }
    #[test]
    fn registration_and_one_use_ticket_bound_to_peer() {
        let mut session = session();
        let peer: SocketAddr = "127.0.0.1:12345".parse().unwrap();
        assert!(session.issue_ticket("local", "local", peer.ip()).is_err());
        session
            .handle_udp(
                3806,
                peer,
                Datagram {
                    opcode: 0x1000,
                    player: 0,
                    sequence: 0,
                    body: 246u32.to_le_bytes().to_vec(),
                },
            )
            .unwrap();
        let ticket = session.issue_ticket("local", "local", peer.ip()).unwrap();
        assert!(!session.consume_ticket(&ticket, "127.0.0.3".parse().unwrap()));
        assert!(session.consume_ticket(&ticket, peer.ip()));
        assert!(!session.consume_ticket(&ticket, peer.ip()));
    }
    #[test]
    fn wrong_endpoint_cannot_move_player() {
        let mut session = session();
        session
            .select(0, "127.0.0.1".parse().unwrap(), false)
            .unwrap();
        let packet = Datagram {
            opcode: 0x1004,
            player: FIRST_PLAYER_ID,
            sequence: 1,
            body: vec![0; 12],
        };
        assert!(session
            .handle_udp(SECTOR_PORT, "127.0.0.1:2222".parse().unwrap(), packet)
            .is_err());
        assert_eq!(session.position, [0.; 3]);
    }

    #[test]
    fn proxy_rebuilt_ticket_is_accepted_only_for_proxy_selection() {
        let peer: SocketAddr = "127.0.0.1:22334".parse().unwrap();
        for via_proxy in [false, true] {
            let mut session = session();
            session.select(0, peer.ip(), via_proxy).unwrap();
            session
                .handle_udp(
                    3808,
                    peer,
                    Datagram {
                        opcode: 0x2008,
                        player: FIRST_PLAYER_ID,
                        sequence: 0,
                        body: session.sector.id.to_le_bytes().to_vec(),
                    },
                )
                .unwrap();
            let mut body = vec![0; 137];
            body[16..20].copy_from_slice(&FIRST_PLAYER_ID.to_be_bytes());
            body[20..24].copy_from_slice(&session.sector.id.to_be_bytes());
            body[44..60].copy_from_slice(b"MY_Avatar_Ticket");
            let packet = Datagram {
                opcode: 2,
                player: FIRST_PLAYER_ID,
                sequence: 1,
                body,
            };
            let mut wrong_player = packet.clone();
            wrong_player.body[16..20].copy_from_slice(&(FIRST_PLAYER_ID + 1).to_be_bytes());
            assert!(session.handle_udp(SECTOR_PORT, peer, wrong_player).is_err());
            // Use a fresh sequence because rejected inputs are also deduplicated.
            let mut packet = packet;
            packet.sequence = 2;
            let result = session.handle_udp(SECTOR_PORT, peer, packet);
            assert_eq!(result.is_ok(), via_proxy);
            assert_eq!(session.logged_in, via_proxy);
        }
    }
}
