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
                "Use /d_jump SECTOR_NAME_OR_ID, for example /d_jump BBW.",
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

    fn creates(game: &mut GameState, object: u32) -> HashMap<u32, usize> {
        let mut viewers = HashMap::new();
        for output in game.take_outgoing() {
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

    #[test]
    fn authenticated_dev_jump_resolves_names_and_uses_the_normal_committed_handoff() {
        let mut game = super::super::tests::game();
        super::super::tests::login(&mut game, FIRST_PLAYER_ID, 22611, "alice");
        assert!(chat(&mut game, "/d_jump asteroid belt").is_err());
        assert_eq!(game.sessions[&FIRST_PLAYER_ID].sector.id, 1076);
        chat(&mut game, "/d_jump BBW").unwrap();
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
