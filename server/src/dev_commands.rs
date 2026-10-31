//! Private development-server chat commands. These are deliberately independent
//! of class abilities; the existing authenticated chat path supplies the owner.
use super::*;
use crate::sectors::SectorCatalog;

struct SpawnRequest<'a> {
    query: &'a str,
    count: usize,
    level: Option<u32>,
}

impl<'a> SpawnRequest<'a> {
    /// Returns:
    ///     Self: Validated template, count, and optional combat level.
    fn parse(argument: &'a str) -> io::Result<Self> {
        let mut argument = argument.trim();
        let mut level = None;
        let tail = argument.rsplit(char::is_whitespace).next().unwrap_or("");
        if let Some(value) = tail.strip_prefix("level=") {
            let parsed = value.parse::<u32>().map_err(|_| {
                invalid("Use /d_spawn MOB_NAME_OR_TEMPLATE_ID [COUNT] [level=1..66].")
            })?;
            if !(1..=66).contains(&parsed) {
                return Err(invalid("Developer MOB level must be between 1 and 66."));
            }
            level = Some(parsed);
            argument = argument[..argument.len() - tail.len()].trim_end();
        }
        if argument.is_empty() {
            return Ok(Self {
                query: "46",
                count: 1,
                level,
            });
        }
        if let Some((query, count)) = argument.rsplit_once(char::is_whitespace) {
            if let Ok(count) = count.parse::<usize>() {
                if !(1..=32).contains(&count) {
                    return Err(invalid(
                        "Use /d_spawn MOB_NAME_OR_TEMPLATE_ID [COUNT] [level=1..66], with count 1 through 32.",
                    ));
                }
                return Ok(Self {
                    query: query.trim(),
                    count,
                    level,
                });
            }
        }
        Ok(Self {
            query: argument,
            count: 1,
            level,
        })
    }
}

impl GameState {
    /// List developer commands as separate, readable native chat messages.
    /// Raises:
    ///     io::Error: The authenticated character no longer has a session.
    pub(super) fn developer_help(&mut self, source: u32) -> io::Result<()> {
        let session = self.sessions.get_mut(&source)
            .ok_or_else(|| invalid("Unknown developer character."))?;
        for line in [
            "Developer commands (sector names accept partial names and abbreviations):",
            "/d_help - list all developer commands.",
            "/d_wh SECTOR_NAME_OR_ID - instant sector travel; aliases: /d_travel, /d_jump.",
            "/d_warp [OBJECT_ID] - move instantly near an object, or your selected target.",
            "/d_spawn [MOB_NAME_OR_ID] [COUNT] [level=1..66] - spawn MOBs ahead (count 1..32).",
            "/d_aspawn [LEVEL 1..9] [COUNT 1..32] - spawn mineable asteroids ahead.",
            "/d_mob_cl LEVEL - set living MOBs within 20 km to combat level 1..66.",
            "/d_clear [mobs] - remove living MOBs within 20 km.",
            "/d_clear buffs - clear all player buffs and device effects.",
        ] {
            session.message(line);
        }
        Ok(())
    }

    /// Instantly move to the selected same-sector target or an explicitly supplied object ID.
    /// Raises:
    ///     io::Error: The target is invalid/off-sector or the durable move cannot complete.
    pub(super) fn developer_warp(&mut self, source: u32, argument: &str) -> io::Result<()> {
        let session = self
            .sessions
            .get(&source)
            .ok_or_else(|| invalid("Unknown developer character."))?;
        let target = if argument.is_empty() {
            session.current_target()
        } else {
            argument
                .parse::<u32>()
                .map_err(|_| invalid("Use /d_warp [OBJECT_ID], or select a target first."))?
        };
        let sector = session.sector.id;
        let (position, radius, name) = if let Some(peer) = self
            .sessions
            .get(&target)
            .and_then(PlayerSession::peer_snapshot)
            .filter(|peer| peer.sector == sector)
        {
            (
                peer.position,
                crate::following::PLAYER_RADIUS,
                peer.profile.name,
            )
        } else {
            session.developer_warp_target(target)?
        };
        self.sessions
            .get_mut(&source)
            .unwrap()
            .developer_warp_to(target, position, radius)?;
        self.update_views();
        if let Some(peer) = self.sessions[&source].peer_snapshot() {
            for observer in self.sessions.values_mut() {
                observer.developer_warp_peer_update(&peer);
            }
        }
        self.sessions
            .get_mut(&source)
            .unwrap()
            .message(&format!("Dev warp: arrived near {name} ({target})."));
        crate::server_log!(
            "Dev warp: {}; target={target}; sector={sector}; target_name={name}",
            self.sessions[&source].account.character.name
        );
        Ok(())
    }

    /// Rescale living MOBs within 20 km and publish their updated native status to observers.
    /// Raises:
    ///     io::Error: The level is invalid, the owner cannot tune this sector, or persistence fails.
    pub(super) fn developer_mob_level(&mut self, source: u32, argument: &str) -> io::Result<()> {
        let level = argument
            .parse::<u32>()
            .ok()
            .filter(|level| (1..=66).contains(level))
            .ok_or_else(|| {
                invalid("Use /d_mob_cl LEVEL, with a combat level from 1 through 66.")
            })?;
        let session = self
            .sessions
            .get(&source)
            .ok_or_else(|| invalid("Unknown developer character."))?;
        let center = session.developer_mob_level_origin()?;
        let sector = session.sector.id;
        self.attach_session(source)?;
        let indices = lock_combat(&self.sessions[&source].combat)?
            .set_nearby_combat_level(center, 20_000., level)?;
        for session in self
            .sessions
            .values_mut()
            .filter(|session| session.sector.id == sector)
        {
            session.developer_refresh_mobs(&indices)?;
        }
        self.sessions.get_mut(&source).unwrap().message(&format!(
            "Dev MOB level: {} living MOBs within 20 km set to combat level {level}; health fractions preserved.", indices.len()
        ));
        crate::server_log!(
            "Dev MOB level: {}; sector={sector}; count={}; level={level}; radius=20000",
            self.sessions[&source].account.character.name,
            indices.len()
        );
        Ok(())
    }

    /// Clear nearby MOBs, or explicitly clear active buffs for every local account.
    pub(super) fn developer_clear(&mut self, source: u32, argument: &str) -> io::Result<()> {
        if !matches!(argument, "" | "mobs" | "buffs") {
            return Err(invalid("Use /d_clear [mobs|buffs]."));
        }
        if !self
            .sessions
            .get(&source)
            .is_some_and(|session| session.logged_in)
        {
            return Err(invalid("Enter the game before using /d_clear."));
        }
        if argument != "buffs" {
            let center = self.sessions[&source].developer_mob_level_origin()?;
            let sector = self.sessions[&source].sector.id;
            self.attach_session(source)?;
            let indices =
                lock_combat(&self.sessions[&source].combat)?.clear_nearby_mobs(center, 20_000.)?;
            for session in self
                .sessions
                .values_mut()
                .filter(|session| session.sector.id == sector && session.logged_in)
            {
                session.publish_mobs()?;
            }
            self.sessions.get_mut(&source).unwrap().message(&format!(
                "Dev clear: {} MOBs removed within 20 km; no loot or XP.",
                indices.len()
            ));
            crate::server_log!(
                "Dev clear MOBs: {}; sector={sector}; count={}; radius=20000",
                self.sessions[&source].account.character.name,
                indices.len()
            );
            return Ok(());
        }
        let mut effects = 0;
        let mut players = 0;
        for session in self.sessions.values_mut() {
            effects += session.developer_clear_buffs()?;
            players += 1;
        }
        self.sessions.get_mut(&source).unwrap().message(&format!(
            "Dev clear: {effects} active effects removed from {players} players."
        ));
        crate::server_log!(
            "Dev clear: {}; effects={effects}; players={players}",
            self.sessions[&source].account.character.name
        );
        Ok(())
    }

    /// Spawn real, persistent mineable asteroids ahead of the pilot and announce them to sector observers.
    /// Raises:
    ///     io::Error: Syntax, flight state, capacity or persistence is invalid.
    pub(super) fn developer_asteroid_spawn(
        &mut self,
        source: u32,
        argument: &str,
    ) -> io::Result<()> {
        let args: Vec<_> = argument.split_whitespace().collect();
        if args.len() > 2 {
            return Err(invalid("Use /d_aspawn [LEVEL 1..9] [COUNT 1..32]."));
        }
        let level = args
            .first()
            .map_or(Ok(1), |arg| arg.trim_start_matches("level=").parse::<u16>())
            .map_err(|_| invalid("Use /d_aspawn [LEVEL 1..9] [COUNT 1..32]."))?;
        let count = args
            .get(1)
            .map_or(Ok(1), |arg| arg.parse::<usize>())
            .map_err(|_| invalid("Invalid asteroid count."))?;
        if !(1..=9).contains(&level) || !(1..=32).contains(&count) {
            return Err(invalid("Use /d_aspawn [LEVEL 1..9] [COUNT 1..32]."));
        }
        let session = self
            .sessions
            .get(&source)
            .ok_or_else(|| invalid("Unknown developer character."))?;
        let positions = session.developer_spawn_positions(count)?;
        let sector = session.sector.id;
        let template = SectorCatalog::load()?.asteroid(level)?;
        let definitions = positions
            .into_iter()
            .map(|position| {
                let mut node = template.clone();
                node.object.position = position;
                node.object.name = format!("Asteroid (L{level})");
                node.object.radius = node.object.radius.min(150.);
                node.field_id = 0;
                node
            })
            .collect();
        self.attach_session(source)?;
        let field = Arc::clone(&self.resource_fields[&sector]);
        let ids = crate::resources::lock_resources(&field)?.spawn_developer(definitions)?;
        for observer in self
            .sessions
            .values_mut()
            .filter(|session| session.sector.id == sector)
        {
            observer.developer_refresh_resources(Arc::clone(&field))?;
        }
        self.sessions.get_mut(&source).unwrap().message(&format!(
            "Dev asteroid spawn: {count} x level {level}, 1000m ahead; first object {}.",
            ids[0]
        ));
        crate::server_log!(
            "Dev asteroid spawn: sector={sector}; count={count}; level={level}; first_object={}",
            ids[0]
        );
        Ok(())
    }

    pub(super) fn developer_spawn(&mut self, source: u32, argument: &str) -> io::Result<()> {
        let request = SpawnRequest::parse(argument)?;
        let catalog = SectorCatalog::load()?;
        let template = catalog.resolve_mob(request.query)?;
        let session = self
            .sessions
            .get(&source)
            .ok_or_else(|| invalid("Unknown developer character."))?;
        let positions = session.developer_spawn_positions(request.count)?;
        self.attach_session(source)?;
        let mut definitions = Vec::with_capacity(request.count);
        for position in positions {
            let mut definition = template.clone();
            definition.spawn = position;
            definition.source_id = 0; // shared world assigns the durable dev identity
            definition.spawn_group = 0;
            definition.spawn_region = None;
            if let Some(level) = request.level {
                definition.level = level;
                definition.combat_override = None;
            }
            definitions.push(definition);
        }
        let stats = crate::mob_stats::MobStats::derive(&definitions[0]);
        let level = definitions[0].level;
        let ids = lock_combat(&self.sessions[&source].combat)?.spawn_developer(definitions)?;
        let first = ids
            .first()
            .copied()
            .ok_or_else(|| invalid("Developer spawn produced no actors."))?;
        self.sessions.get_mut(&source).unwrap().message(&format!(
            "Dev spawn: {} x {} (template {}, level {}, shields {:.0}), 1000m ahead; first object {}.",
            request.count, template.name, template.template_id, level, stats.shield, first
        ));
        crate::server_log!(
            "Dev spawn: {}; template={}; count={}; level={level}; shield={:.0}; first_object={first}",
            self.sessions[&source].account.character.name,
            template.template_id,
            request.count,
            stats.shield
        );
        Ok(())
    }

    pub(super) fn developer_jump(&mut self, source: u32, argument: &str) -> io::Result<()> {
        if argument.trim().is_empty() {
            return Err(invalid(
                "Use /d_wh SECTOR_NAME_OR_ID, for example /d_wh Saturn (aliases: /d_travel, /d_jump).",
            ));
        }
        let destination = SectorCatalog::load()?.resolve_sector(argument)?;
        let session = self
            .sessions
            .get_mut(&source)
            .ok_or_else(|| invalid("Unknown developer character."))?;
        crate::server_log!(
            "Dev jump: {}; destination={} {}",
            session.account.character.name,
            destination.id,
            destination.name
        );
        session.developer_jump(destination.id)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::account::FIRST_PLAYER_ID;
    use crate::protocol::{Datagram, Frame};
    use std::collections::{HashMap, HashSet};

    /// Sector names and station IDs use the same durable handoff and developer policy.
    #[test]
    fn developer_wh_accepts_sector_names_and_station_ids() {
        for (query, destination) in [("BBW", 3605), ("17051", 17051)] {
            let mut game = super::super::tests::game();
            super::super::tests::login(&mut game, FIRST_PLAYER_ID, 22611, "alice");
            game.set_developer_mode(false);
            assert!(chat(&mut game, &format!("/d_wh {query}")).is_err());
            assert!(chat(&mut game, "/d_help").is_err());
            assert_eq!(game.sessions[&FIRST_PLAYER_ID].sector.id, 1076);
            game.set_developer_mode(true);
            chat(&mut game, "/d_help").unwrap();
            chat(&mut game, &format!("/d_wh {query}")).unwrap();
            let session = &game.sessions[&FIRST_PLAYER_ID];
            assert_eq!(session.sector.id, destination);
            assert!(!session.logged_in);
            let saved: serde_json::Value = serde_json::from_slice(
                &std::fs::read(session.state_dir.join("player.json")).unwrap(),
            ).unwrap();
            assert_eq!(saved["sector"], destination);
        }
    }

    /// Spawned ore is shared immediately, survives a field reload, and invalid commands leave it unchanged.
    #[test]
    fn developer_asteroid_spawn_is_shared_mineable_and_persistent() {
        let mut game = super::super::tests::game();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        super::super::tests::login(&mut game, a, 22721, "alice");
        super::super::tests::login(&mut game, b, 22722, "bob");
        for command in [
            "/d_aspawn 0",
            "/d_aspawn 10",
            "/d_aspawn 3 0",
            "/d_aspawn 3 33",
            "/d_aspawn 3 2 extra",
        ] {
            assert!(chat(&mut game, command).is_err());
        }
        chat(&mut game, "/d_aspawn 3 2").unwrap();
        let sector = game.sessions[&a].sector.id;
        let field = lock_resources(&game.resource_fields[&sector]).unwrap();
        let spawned: Vec<_> = field
            .definitions()
            .into_iter()
            .filter(|node| node.object.id >= 0x28000000)
            .collect();
        assert_eq!(spawned.len(), 2);
        for node in &spawned {
            assert_eq!(node.level, 3);
            assert_eq!(node.resource_type, 1);
            assert!(field.get(node.object.id).unwrap().available());
            for id in [a, b] {
                assert!(game.sessions[&id]
                    .sector
                    .resources
                    .iter()
                    .any(|known| known.object.id == node.object.id));
            }
        }
        drop(field);
        let mut original = game.sessions[&a].sector.clone();
        original
            .resources
            .retain(|node| node.object.id < 0x28000000);
        let restored = ResourceField::load(
            &original,
            &crate::items::ItemCatalog::load().unwrap(),
            game.root.join(format!("resources/{sector}.json")),
        )
        .unwrap();
        for node in spawned {
            assert!(restored.get(node.object.id).unwrap().available());
        }
        std::fs::remove_dir_all(&game.root).unwrap();
    }

    #[test]
    fn spawn_request_defaults_and_count_boundaries_are_explicit() {
        let default = SpawnRequest::parse("").unwrap();
        assert_eq!(default.query, "46");
        assert_eq!(default.count, 1);
        assert_eq!(default.level, None);
        let single = SpawnRequest::parse("1429").unwrap();
        assert_eq!(single.query, "1429");
        assert_eq!(single.count, 1);
        let multi = SpawnRequest::parse("The Controller 3").unwrap();
        assert_eq!(multi.query, "The Controller");
        assert_eq!(multi.count, 3);
        let leveled = SpawnRequest::parse("Achilles Drone 3 level=50").unwrap();
        assert_eq!(leveled.query, "Achilles Drone");
        assert_eq!(leveled.count, 3);
        assert_eq!(leveled.level, Some(50));
        assert_eq!(SpawnRequest::parse("46 level=66").unwrap().level, Some(66));
        assert_eq!(SpawnRequest::parse("level=50").unwrap().query, "46");
        assert!(SpawnRequest::parse("46 level=0").is_err());
        assert!(SpawnRequest::parse("46 level=67").is_err());
        assert!(SpawnRequest::parse("46 level=bogus").is_err());
        assert!(SpawnRequest::parse("46 0").is_err());
        assert!(SpawnRequest::parse("46 33").is_err());
    }

    fn chat(game: &mut GameState, text: &str) -> io::Result<()> {
        game.handle_event(
            FIRST_PLAYER_ID,
            ClientEvent::Chat {
                channel: 4,
                text: text.into(),
            },
        )
    }

    /// Returns:
    ///     Vec<Outbound>: Updates drained across the reliable transport's bounded pacing intervals.
    fn paced_updates(game: &mut GameState) -> Vec<Outbound> {
        let mut updates = Vec::new();
        for _ in 0..12 {
            std::thread::sleep(std::time::Duration::from_millis(55));
            game.advance(0.).unwrap();
            updates.extend(game.take_outgoing());
        }
        updates
    }

    fn creates(game: &mut GameState, object: u32) -> HashMap<u32, usize> {
        let mut viewers = HashMap::new();
        for output in paced_updates(game) {
            let packet = Datagram::decode(&output.data).unwrap();
            if packet.opcode != 0x2016 {
                continue;
            }
            let mut body = packet.body.as_slice();
            while body.len() >= 4 {
                let size = u16::from_le_bytes(body[..2].try_into().unwrap()) as usize;
                if size > body.len() {
                    break;
                }
                let frame = Frame::decode(&body[..size]).unwrap();
                if frame.opcode == 4 && frame.body[..4] == object.to_le_bytes() {
                    *viewers.entry(packet.player).or_default() += 1;
                }
                body = &body[size..];
            }
        }
        viewers
    }

    /// A clear removes nearby actors from both clients and releases a selected target without touching distant MOBs.
    #[test]
    fn developer_clear_mobs_updates_all_observers() {
        let mut game = super::super::tests::game();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        super::super::tests::login(&mut game, a, 22621, "alice");
        super::super::tests::login(&mut game, b, 22622, "bob");
        for session in game.sessions.values_mut() {
            session.position = [0.; 3];
        }
        {
            let mut world = lock_combat(&game.sessions[&a].combat).unwrap();
            world.actors[0].position = [1000., 0., 0.];
            let mut definition = world.actors[0].definition.clone();
            definition.spawn = [20_001., 0., 0.];
            definition.spawn_region = None;
            world.spawn_developer(vec![definition]).unwrap();
        }
        game.advance(0.).unwrap();
        game.take_outgoing();
        game.sessions
            .get_mut(&a)
            .unwrap()
            .group_target(crate::sector_combat::MOB_ID)
            .unwrap();
        chat(&mut game, "/d_clear").unwrap();
        assert_eq!(game.sessions[&a].current_target(), u32::MAX);
        {
            let world = lock_combat(&game.sessions[&a].combat).unwrap();
            assert!(world.actors[0].developer_removed);
            assert!(!world.actors[1].developer_removed);
        }
        let mut viewers = HashSet::new();
        for output in paced_updates(&mut game) {
            let packet = Datagram::decode(&output.data).unwrap();
            if packet.opcode != 0x2016 {
                continue;
            }
            let mut body = packet.body.as_slice();
            while body.len() >= 4 {
                let size = u16::from_le_bytes(body[..2].try_into().unwrap()) as usize;
                if size > body.len() {
                    break;
                }
                let frame = Frame::decode(&body[..size]).unwrap();
                if frame.opcode == 7 && frame.body == crate::sector_combat::MOB_ID.to_le_bytes() {
                    viewers.insert(packet.player);
                }
                body = &body[size..];
            }
        }
        assert_eq!(viewers, HashSet::from([a, b]));
        chat(&mut game, "/d_clear mobs").unwrap();
    }

    #[test]
    fn developer_clear_removes_each_players_buff_and_persists_the_clear() {
        let mut game = super::super::tests::game();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        super::super::tests::login(&mut game, a, 22611, "alice");
        super::super::tests::login(&mut game, b, 22612, "bob");
        for id in [a, b] {
            let session = game.sessions.get_mut(&id).unwrap();
            let epoch = session.energy_transfer_epoch();
            session
                .apply_shield_charging_buff(crate::shield_charging::ShieldChargingBuff {
                    revision: 1,
                    source: a,
                    target_epoch: epoch,
                    ability: 74,
                    trained_rank: 7,
                    expires_at_ms: crate::resources::unix_ms() + 60_000,
                })
                .unwrap();
            let saved: serde_json::Value = serde_json::from_slice(
                &std::fs::read(session.state_dir.join("session.local.json")).unwrap(),
            )
            .unwrap();
            assert!(saved["shield_charging_buff"].is_object());
        }
        assert!(chat(&mut game, "/d_clear extra").is_err());
        chat(&mut game, "/d_clear buffs").unwrap();
        for id in [a, b] {
            let session = &game.sessions[&id];
            let saved: serde_json::Value = serde_json::from_slice(
                &std::fs::read(session.state_dir.join("session.local.json")).unwrap(),
            )
            .unwrap();
            assert!(saved["shield_charging_buff"].is_null());
            assert!(saved["active_device"].is_null());
            assert_eq!(
                saved["additional_active_devices"].as_array().unwrap().len(),
                0
            );
            let leases: serde_json::Value = serde_json::from_slice(
                &std::fs::read(session.state_dir.join("active_devices.local.json")).unwrap(),
            )
            .unwrap();
            assert!(leases["active_device"].is_null());
            assert!(leases["additional_active_devices"]
                .as_array()
                .unwrap()
                .is_empty());
        }
    }

    #[test]
    fn authenticated_dev_spawn_is_shared_rendered_and_survives_a_world_restart() {
        let mut game = super::super::tests::game();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        super::super::tests::login(&mut game, a, 22601, "alice");
        super::super::tests::login(&mut game, b, 22602, "bob");
        game.sessions.get_mut(&a).unwrap().position = [0.; 3];
        game.sessions.get_mut(&b).unwrap().position = [0.; 3];
        game.advance(0.).unwrap();
        game.take_outgoing();
        let before = lock_combat(&game.sessions[&a].combat).unwrap().actors.len();
        chat(&mut game, "/d_spawn").unwrap();
        let spawned = {
            let world = lock_combat(&game.sessions[&a].combat).unwrap();
            assert_eq!(world.actors.len(), before + 1);
            let actor = &world.actors[before];
            assert_eq!(actor.definition.template_id, 46);
            assert!(actor.definition.source_id >= 0xe0000000);
            assert_eq!(actor.position, [1000., 0., 0.]);
            actor.definition.source_id
        };
        game.advance(0.).unwrap();
        assert_eq!(
            creates(&mut game, crate::sector_combat::MOB_ID + before as u32),
            HashMap::from([(a, 1), (b, 1)])
        );
        assert!(chat(&mut game, "/d_spawn Achilles Drone 0").is_err());
        assert!(chat(&mut game, "/d_spawn No Such Public Monster").is_err());
        assert_eq!(
            lock_combat(&game.sessions[&a].combat).unwrap().actors.len(),
            before + 1
        );
        game.save().unwrap();
        let mut resumed =
            GameState::new(game.initial_sector.clone(), game.bind, game.root.clone()).unwrap();
        let actor = {
            let world = lock_combat(&resumed.sessions[&a].combat).unwrap();
            assert_eq!(world.actors.len(), before + 1);
            world
                .actors
                .iter()
                .find(|mob| mob.definition.source_id == spawned)
                .unwrap()
                .definition
                .clone()
        };
        assert_eq!(actor.template_id, 46);
        resumed.advance(0.).unwrap();
        assert_eq!(
            creates(&mut resumed, crate::sector_combat::MOB_ID + before as u32),
            HashMap::from([(a, 1), (b, 1)])
        );
        chat(&mut resumed, "/d_spawn 46 2").unwrap();
        chat(&mut resumed, "/d_spawn 46 1 level=50").unwrap();
        let world = lock_combat(&resumed.sessions[&a].combat).unwrap();
        assert_eq!(world.actors.len(), before + 4);
        let leveled = world.actors.last().unwrap();
        assert_eq!(leveled.definition.level, 50);
        assert!(leveled.definition.combat_override.is_none());
        assert!((leveled.max_shield - 160_740.).abs() < 1.);
        assert_eq!(leveled.shield, leveled.max_shield);
        let identities: HashSet<_> = world
            .actors
            .iter()
            .map(|mob| mob.definition.source_id)
            .collect();
        assert_eq!(identities.len(), world.actors.len());
    }

    /// Both observers receive the new level/resources without a MOB recreate or sector transition.
    #[test]
    fn developer_mob_level_updates_nearby_shared_actors_and_observers() {
        let mut game = super::super::tests::game();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        super::super::tests::login(&mut game, a, 22601, "alice");
        super::super::tests::login(&mut game, b, 22602, "bob");
        for session in game.sessions.values_mut() {
            session.position = [0.; 3];
        }
        {
            let mut world = lock_combat(&game.sessions[&a].combat).unwrap();
            world.actors[0].position = [1000., 0., 0.];
        }
        game.advance(0.).unwrap();
        game.take_outgoing();
        assert!(chat(&mut game, "/d_mob_cl 0").is_err());
        assert!(chat(&mut game, "/d_mob_cl 67").is_err());
        assert!(chat(&mut game, "/d_mob_cl 10 extra").is_err());
        chat(&mut game, "/d_mob_cl 10").unwrap();
        let expected = {
            let world = lock_combat(&game.sessions[&a].combat).unwrap();
            assert_eq!(world.actors[0].definition.level, 10);
            packets::mob_status(crate::sector_combat::MOB_ID, &world.actors[0])
        };
        let mut viewers = HashSet::new();
        for output in paced_updates(&mut game) {
            let packet = Datagram::decode(&output.data).unwrap();
            if packet.opcode != 0x2016 {
                continue;
            }
            let mut body = packet.body.as_slice();
            while body.len() >= 4 {
                let size = u16::from_le_bytes(body[..2].try_into().unwrap()) as usize;
                if size > body.len() {
                    break;
                }
                if Frame::decode(&body[..size]).unwrap() == expected {
                    viewers.insert(packet.player);
                }
                body = &body[size..];
            }
        }
        assert_eq!(viewers, HashSet::from([a, b]));
    }

    /// Selected players, MOB IDs, and station IDs all use authoritative centers and notify observers.
    #[test]
    fn developer_warp_selected_peer_and_explicit_mob_or_station_targets() {
        let mut game = super::super::tests::game();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        super::super::tests::login(&mut game, a, 22601, "alice");
        super::super::tests::login(&mut game, b, 22602, "bob");
        game.sessions.get_mut(&a).unwrap().position = [0.; 3];
        game.sessions.get_mut(&b).unwrap().position = [5000., 0., 0.];
        game.advance(0.).unwrap();
        game.take_outgoing();
        game.sessions.get_mut(&a).unwrap().group_target(b).unwrap();
        chat(&mut game, "/d_warp").unwrap();
        let arrival = game.sessions[&a].position;
        assert!((crate::mob::distance(arrival, game.sessions[&b].position) - 790.).abs() < 0.1);
        let mut viewers = HashSet::new();
        for output in paced_updates(&mut game) {
            let packet = Datagram::decode(&output.data).unwrap();
            if packet.opcode != 0x2016 {
                continue;
            }
            let mut body = packet.body.as_slice();
            while body.len() >= 4 {
                let size = u16::from_le_bytes(body[..2].try_into().unwrap()) as usize;
                if size > body.len() {
                    break;
                }
                let frame = Frame::decode(&body[..size]).unwrap();
                if frame.opcode == 0x3e
                    && frame.body.len() >= 22
                    && frame.body[2..6] == a.to_le_bytes()
                    && (0..3).all(|axis| {
                        f32::from_le_bytes(
                            frame.body[10 + axis * 4..14 + axis * 4].try_into().unwrap(),
                        ) == arrival[axis]
                    })
                {
                    viewers.insert(packet.player);
                }
                body = &body[size..];
            }
        }
        assert_eq!(viewers, HashSet::from([a, b]));
        chat(
            &mut game,
            &format!("/d_warp {}", crate::sector_combat::MOB_ID),
        )
        .unwrap();
        let mob = lock_combat(&game.sessions[&a].combat).unwrap().actors[0].clone();
        assert!(
            (crate::mob::distance(game.sessions[&a].position, mob.position)
                - mob.definition.radius
                - 545.)
                .abs()
                < 0.1
        );
        let station = game.sessions[&a]
            .sector
            .navpoints
            .iter()
            .find(|nav| nav.object_type == 12)
            .unwrap()
            .clone();
        chat(&mut game, &format!("/d_warp {}", station.id)).unwrap();
        assert!(
            (crate::mob::distance(game.sessions[&a].position, station.position)
                - station.radius
                - 545.)
                .abs()
                < 0.1
        );
        assert_eq!(game.sessions[&a].current_target(), station.id);
        assert!(chat(&mut game, "/d_warp not-an-id").is_err());
        assert!(chat(&mut game, &format!("/d_warp {a}")).is_err());
    }

    /// Market travel obeys the server policy and persists only hosted-station handoffs.
    #[test]
    fn component_market_travel_requires_developer_mode_and_a_hosted_station() {
        let mut game = super::super::tests::game();
        let player = FIRST_PLAYER_ID;
        super::super::tests::login(&mut game, player, 22611, "alice");
        game.set_developer_mode(false);
        assert!(game
            .handle_event(
                player,
                ClientEvent::ComponentMarketTravel { station: 17051 }
            )
            .is_err());
        assert!(chat(&mut game, "/d_jump 17051").is_err());
        assert!(chat(&mut game, "/d_travel 17051").is_err());
        assert_eq!(game.sessions[&player].sector.id, 1076);
        game.set_developer_mode(true);
        assert!(game
            .handle_event(player, ClientEvent::ComponentMarketTravel { station: 1076 })
            .is_err());
        game.handle_event(
            player,
            ClientEvent::ComponentMarketTravel { station: 17051 },
        )
        .unwrap();
        let session = &game.sessions[&player];
        assert_eq!(session.sector.id, 17051);
        assert!(!session.logged_in);
        let saved: serde_json::Value =
            serde_json::from_slice(&std::fs::read(session.state_dir.join("player.json")).unwrap())
                .unwrap();
        assert_eq!(saved["sector"], 17051);
        let checkpoint: serde_json::Value = serde_json::from_slice(
            &std::fs::read(session.state_dir.join("session.local.json")).unwrap(),
        )
        .unwrap();
        assert!(checkpoint["handoff"].is_array());
    }

    #[test]
    fn authenticated_dev_jump_resolves_names_and_uses_the_normal_committed_handoff() {
        let mut game = super::super::tests::game();
        super::super::tests::login(&mut game, FIRST_PLAYER_ID, 22611, "alice");
        assert!(chat(&mut game, "/d_jump asteroid belt").is_err());
        assert_eq!(game.sessions[&FIRST_PLAYER_ID].sector.id, 1076);
        chat(&mut game, "/d_travel BBW").unwrap();
        let session = &game.sessions[&FIRST_PLAYER_ID];
        assert_eq!(session.sector.id, 3605);
        assert!(!session.logged_in);
        let saved: serde_json::Value =
            serde_json::from_slice(&std::fs::read(session.state_dir.join("player.json")).unwrap())
                .unwrap();
        assert_eq!(saved["sector"], 3605);
        let checkpoint: serde_json::Value = serde_json::from_slice(
            &std::fs::read(session.state_dir.join("session.local.json")).unwrap(),
        )
        .unwrap();
        assert!(checkpoint["handoff"].is_array());
        assert!(chat(&mut game, "/d_jump 1076").is_err()); // finish this handoff first
    }
}
