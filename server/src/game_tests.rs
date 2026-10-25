//! Behavioral integration tests with two authenticated sessions and one shared
//! simulation. These construct packets locally; they never launch a live client.
use super::*;
use crate::account::FIRST_PLAYER_ID;
use crate::mob::distance;
use crate::protocol::SECTOR_PORT;
use crate::sector_combat::MOB_ID;
use crate::warp::WarpPhase;
use std::fs;

pub(super) fn game() -> GameState {
    let root = std::env::temp_dir().join(format!("enb-game-test-{}", rand::random::<u64>()));
    let accounts = AccountDirectory::new(&root);
    accounts
        .create("alice".into(), "example", "Alice".into())
        .unwrap();
    accounts
        .create("bob".into(), "example", "Bob".into())
        .unwrap();
    // These tests exercise expensive high-rank support abilities. Supply real
    // level-nine reactors instead of relying on the former fixed 1000 energy.
    let catalog = crate::items::ItemCatalog::load().unwrap();
    for directory in [&root, &root.join("accounts/bob")] {
        let mut inventory = crate::inventory::Inventory::starter(&catalog).unwrap();
        inventory.equipped[1].as_mut().unwrap().template = 3340;
        fs::write(
            directory.join("inventory.json"),
            serde_json::to_vec(&inventory).unwrap(),
        )
        .unwrap();
    }
    GameState::new(
        serde_json::from_str(include_str!("../data/sector.json")).unwrap(),
        Ipv4Addr::new(127, 0, 0, 2),
        root,
    )
    .unwrap()
}

pub(super) fn login(game: &mut GameState, id: u32, port: u16, username: &str) {
    let peer: SocketAddr = format!("127.0.0.1:{port}").parse().unwrap();
    game.handle_udp(
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
    let ticket = game.issue_ticket(username, "example", peer.ip()).unwrap();
    assert_eq!(game.consume_ticket(&ticket, peer.ip()), Some(id));
    let reply = game.select(id, 0, peer.ip(), false).unwrap();
    game.handle_udp(
        3808,
        peer,
        Datagram {
            opcode: 0x2008,
            player: id,
            sequence: 0,
            body: 1076u32.to_le_bytes().to_vec(),
        },
    )
    .unwrap();
    let mut body = vec![0; 137];
    body[..64].copy_from_slice(&reply.body[4..]);
    game.handle_udp(
        SECTOR_PORT,
        peer,
        Datagram {
            opcode: 2,
            player: id,
            sequence: 1,
            body,
        },
    )
    .unwrap();
}

fn activate(game: &mut GameState, owner: u32, target: u32, ability: u32) {
    game.handle_input(
        owner,
        0x17,
        &[owner.to_le_bytes(), target.to_le_bytes()].concat(),
    )
    .unwrap();
    game.handle_input(
        owner,
        0x58,
        &[
            owner.to_le_bytes(),
            0u32.to_le_bytes(),
            ability.to_le_bytes(),
        ]
        .concat(),
    )
    .unwrap();
}

/// Send the native untargeted Rally activation without a selection packet.
fn rally_cast(game: &mut GameState, owner: u32, ability: u32) {
    game.handle_input(
        owner,
        0x58,
        &[
            owner.to_le_bytes(),
            0u32.to_le_bytes(),
            ability.to_le_bytes(),
        ]
        .concat(),
    )
    .unwrap();
}

/// Rally grants one durable status to nearby group members, with atomic retry.
#[test]
fn enforcer_rally_is_group_scoped_and_recovers_a_torn_recipient_checkpoint() {
    let root = std::env::temp_dir().join(format!("enb-rally-{}", rand::random::<u64>()));
    let accounts = AccountDirectory::new(&root);
    let alice = accounts
        .create_class(
            "alice".into(),
            "example",
            "Alice".into(),
            crate::classes::CharacterClass::parse("te").unwrap(),
        )
        .unwrap()
        .account
        .character
        .game_id;
    let bob = accounts
        .create("bob".into(), "example", "Bob".into())
        .unwrap()
        .account
        .character
        .game_id;
    let outsider = accounts
        .create("outsider".into(), "example", "Outsider".into())
        .unwrap()
        .account
        .character
        .game_id;
    let inventory_path = root.join("inventory.json");
    let profile = crate::account::Account::load(&root).unwrap().character;
    let mut inventory = crate::inventory::Inventory::starter_for(
        &profile,
        &crate::items::ItemCatalog::load().unwrap(),
    )
    .unwrap();
    inventory.equipped[1].as_mut().unwrap().template = 3337;
    fs::write(&inventory_path, serde_json::to_vec(&inventory).unwrap()).unwrap();
    let sector: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
    let bind = Ipv4Addr::new(127, 0, 0, 2);
    let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    login(&mut game, alice, 22941, "alice");
    login(&mut game, bob, 22942, "bob");
    login(&mut game, outsider, 22943, "outsider");
    game.groups.invite(alice, bob).unwrap();
    game.groups.accept(bob).unwrap();
    game.sessions.get_mut(&bob).unwrap().position = [5_500., 0., 0.];
    game.sessions.get_mut(&outsider).unwrap().position = [2_000., 0., 0.];
    let baseline = game.sessions[&bob].peer_snapshot().unwrap().stats;
    let outsider_baseline = game.sessions[&outsider].peer_snapshot().unwrap().stats;
    let energy = game.sessions[&alice].energy_balance().0;
    activate(&mut game, alice, outsider, 116); // Target selection cannot redirect Rally.
    assert_eq!(game.sessions[&alice].energy_balance().0, energy - 400.);
    assert_eq!(game.sessions[&bob].peer_snapshot().unwrap().stats, baseline);
    game.sessions
        .get_mut(&bob)
        .unwrap()
        .checkpoint_session(true)
        .unwrap();
    let torn = root.join("accounts/bob/session.local.json.tmp");
    fs::create_dir(&torn).unwrap();
    advance(&mut game, 2.2);
    assert_eq!(game.energy_ledger.revision(), 1);
    assert_eq!(
        game.energy_ledger.rally_buff(bob).unwrap().tactic,
        crate::rally::Tactic::Stealth
    );
    assert!(game.energy_ledger.rally_buff(outsider).is_none());
    assert_eq!(
        game.sessions[&outsider].peer_snapshot().unwrap().stats,
        outsider_baseline
    );
    let committed_energy = game.energy_ledger.balance(alice).unwrap().energy;
    fs::remove_dir(&torn).unwrap();
    drop(game);
    let mut resumed = GameState::new(sector, bind, root.clone()).unwrap();
    let buffed = resumed.sessions[&bob].peer_snapshot().unwrap().stats;
    assert_eq!(buffed.signature, baseline.signature * 0.75);
    assert_eq!(buffed.deflects[0], baseline.deflects[0] + 14.);
    assert_eq!(
        resumed.sessions[&alice].energy_balance().0,
        committed_energy
    );
    assert_eq!(resumed.energy_ledger.revision(), 1);
    assert!(resumed.sessions[&bob].rally_revision() > 0);
    // A replacement replays the world revision without charging again.
    resumed.deliver_rallies().unwrap();
    assert_eq!(resumed.energy_ledger.revision(), 1);
    drop(resumed);
    fs::remove_dir_all(root).unwrap();
}

/// A low-tier Rally respects training, range, energy and skill recovery.
#[test]
fn rally_damage_tactics_rejects_wrong_class_and_excludes_distant_groupmate() {
    let root = std::env::temp_dir().join(format!("enb-rally-range-{}", rand::random::<u64>()));
    let accounts = AccountDirectory::new(&root);
    let alice = accounts
        .create_class(
            "alice".into(),
            "example",
            "Alice".into(),
            crate::classes::CharacterClass::parse("te").unwrap(),
        )
        .unwrap()
        .account
        .character
        .game_id;
    let bob = accounts
        .create("bob".into(), "example", "Bob".into())
        .unwrap()
        .account
        .character
        .game_id;
    let sector: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
    let mut game = GameState::new(sector, Ipv4Addr::new(127, 0, 0, 2), root.clone()).unwrap();
    login(&mut game, alice, 22951, "alice");
    login(&mut game, bob, 22952, "bob");
    game.groups.invite(alice, bob).unwrap();
    game.groups.accept(bob).unwrap();
    game.sessions.get_mut(&bob).unwrap().position = [7_000., 0., 0.];
    let bob_energy = game.sessions[&bob].energy_balance().0;
    rally_cast(&mut game, bob, 34);
    assert_eq!(game.sessions[&bob].energy_balance().0, bob_energy);
    game.sessions
        .get_mut(&alice)
        .unwrap()
        .set_energy_for_test(10.);
    rally_cast(&mut game, alice, 34);
    assert_eq!(game.sessions[&alice].energy_balance().0, 10.);
    assert_eq!(game.energy_ledger.revision(), 0);
    game.sessions
        .get_mut(&alice)
        .unwrap()
        .set_energy_for_test(100.);
    rally_cast(&mut game, alice, 34);
    advance(&mut game, 2.2);
    assert_eq!(
        game.energy_ledger.rally_buff(alice).unwrap().tactic,
        crate::rally::Tactic::Damage
    );
    assert!(game.energy_ledger.rally_buff(bob).is_none());
    let revision = game.energy_ledger.revision();
    game.sessions
        .get_mut(&alice)
        .unwrap()
        .set_energy_for_test(100.);
    rally_cast(&mut game, alice, 34);
    assert_eq!(game.energy_ledger.revision(), revision);
    assert_eq!(game.sessions[&alice].energy_balance().0, 100.);
    drop(game);
    fs::remove_dir_all(root).unwrap();
}

/// Rank-three Recharge Shields heals a visible pilot once across replacement.
#[test]
fn recharge_shields_is_durable_for_two_pilots() {
    let root = std::env::temp_dir().join(format!("enb-shield-ability-{}", rand::random::<u64>()));
    let accounts = AccountDirectory::new(&root);
    let alice = accounts
        .create_class(
            "alice".into(),
            "example",
            "Alice".into(),
            crate::classes::CharacterClass::parse("tt").unwrap(),
        )
        .unwrap()
        .account
        .character
        .game_id;
    let bob = accounts
        .create("bob".into(), "example", "Bob".into())
        .unwrap()
        .account
        .character
        .game_id;
    let sector: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
    let bind = Ipv4Addr::new(127, 0, 0, 2);
    let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    login(&mut game, alice, 22901, "alice");
    login(&mut game, bob, 22902, "bob");
    game.sessions.get_mut(&bob).unwrap().set_shield_for_test(0.);
    game.sessions
        .get_mut(&bob)
        .unwrap()
        .checkpoint_session(true)
        .unwrap();
    game.update_views();
    let before_energy = game.sessions[&alice].energy_balance().0;
    let capacity = game.sessions[&bob].shield_balance().1;
    activate(&mut game, alice, bob, 89);
    assert_eq!(
        game.sessions[&alice].energy_balance().0,
        before_energy - 125.
    );
    advance(&mut game, 1.2);
    assert_eq!(game.energy_ledger.revision(), 1);
    assert_eq!(game.sessions[&bob].shield_balance().0, capacity.min(2520.));
    assert!(game.sessions[&alice].pending_shield_recharge().is_none());

    // A torn recipient checkpoint must recover from the committed world file.
    let recipient_path = root.join("accounts/bob/session.local.json");
    let mut stale: serde_json::Value =
        serde_json::from_slice(&fs::read(&recipient_path).unwrap()).unwrap();
    stale["shield"] = serde_json::json!(0.);
    stale["shield_revision"] = serde_json::json!(0);
    fs::write(&recipient_path, serde_json::to_vec(&stale).unwrap()).unwrap();
    drop(game);
    let mut restored = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    assert_eq!(
        restored.sessions[&bob].shield_balance().0,
        capacity.min(2520.)
    );
    assert_eq!(restored.energy_ledger.revision(), 1);
    restored.deliver_shield_recharges().unwrap();
    assert_eq!(restored.energy_ledger.revision(), 1);

    // A failed ledger rename leaves the completed cast in its session outbox.
    restored
        .sessions
        .get_mut(&bob)
        .unwrap()
        .set_shield_for_test(0.);
    restored
        .sessions
        .get_mut(&bob)
        .unwrap()
        .checkpoint_session(true)
        .unwrap();
    let full_reactor = restored.sessions[&alice].energy_balance().1;
    restored
        .sessions
        .get_mut(&alice)
        .unwrap()
        .set_energy_for_test(full_reactor);
    restored
        .sessions
        .get_mut(&alice)
        .unwrap()
        .checkpoint_session(true)
        .unwrap();
    restored.update_views();
    activate(&mut restored, alice, bob, 89);
    let temporary = root.join("energy_transfers.local.json.tmp");
    fs::create_dir(&temporary).unwrap();
    assert!(restored.advance(1.2).is_err());
    assert_eq!(restored.energy_ledger.revision(), 1);
    assert!(restored.sessions[&alice]
        .pending_shield_recharge()
        .is_some());
    fs::remove_dir(&temporary).unwrap();
    drop(restored);
    let mut retried = GameState::new(sector, bind, root.clone()).unwrap();
    assert!(retried.sessions[&alice].pending_shield_recharge().is_some());
    retried.deliver_shield_recharges().unwrap();
    assert_eq!(retried.energy_ledger.revision(), 2);
    assert!(retried.sessions[&alice].pending_shield_recharge().is_none());
    retried.deliver_shield_recharges().unwrap();
    assert_eq!(retried.energy_ledger.revision(), 2);
    fs::remove_dir_all(root).unwrap();
}

/// Hull Patch and peer Repair Hull survive a failed commit and replacement.
#[test]
fn hull_patch_repairs_self_and_peer_once_after_restart() {
    let root = std::env::temp_dir().join(format!("enb-hull-ability-{}", rand::random::<u64>()));
    let accounts = AccountDirectory::new(&root);
    let alice = accounts
        .create_class(
            "alice".into(),
            "example",
            "Alice".into(),
            crate::classes::CharacterClass::parse("tt").unwrap(),
        )
        .unwrap()
        .account
        .character
        .game_id;
    let bob = accounts
        .create("bob".into(), "example", "Bob".into())
        .unwrap()
        .account
        .character
        .game_id;
    let sector: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
    let bind = Ipv4Addr::new(127, 0, 0, 2);
    let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    login(&mut game, alice, 22911, "alice");
    login(&mut game, bob, 22912, "bob");
    let self_capacity = game.sessions[&alice].hull_balance().1;
    let peer_capacity = game.sessions[&bob].hull_balance().1;
    game.sessions
        .get_mut(&alice)
        .unwrap()
        .set_hull_for_test(self_capacity * 0.25);
    game.sessions
        .get_mut(&alice)
        .unwrap()
        .checkpoint_session(true)
        .unwrap();
    game.update_views();
    let self_energy = game.sessions[&alice].energy_balance().0;
    activate(&mut game, alice, bob, 77); // Rank one always repairs self.
    assert_eq!(game.sessions[&alice].energy_balance().0, self_energy - 50.);
    advance(&mut game, 0.2);
    assert_eq!(game.energy_ledger.revision(), 1);
    assert!(game.sessions[&alice].hull_balance().0 > self_capacity * 0.25);
    assert_eq!(game.sessions[&bob].hull_balance().0, peer_capacity);

    game.sessions
        .get_mut(&bob)
        .unwrap()
        .set_hull_for_test(peer_capacity * 0.25);
    game.sessions
        .get_mut(&bob)
        .unwrap()
        .checkpoint_session(true)
        .unwrap();
    let full_reactor = game.sessions[&alice].energy_balance().1;
    game.sessions
        .get_mut(&alice)
        .unwrap()
        .set_energy_for_test(full_reactor);
    game.sessions
        .get_mut(&alice)
        .unwrap()
        .checkpoint_session(true)
        .unwrap();
    game.update_views();
    activate(&mut game, alice, bob, 95);
    assert_eq!(
        game.sessions[&alice].energy_balance().0,
        full_reactor - 125.
    );
    let temporary = root.join("energy_transfers.local.json.tmp");
    fs::create_dir(&temporary).unwrap();
    assert!(game.advance(1.2).is_err());
    assert_eq!(game.energy_ledger.revision(), 1);
    assert!(game.sessions[&alice].pending_hull_repair().is_some());
    fs::remove_dir(&temporary).unwrap();
    drop(game);
    let mut restored = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    restored.deliver_hull_repairs().unwrap();
    assert_eq!(restored.energy_ledger.revision(), 2);
    assert_eq!(
        restored.sessions[&bob].hull_balance().0,
        (peer_capacity * 0.25 + 2520.).min(peer_capacity)
    );
    assert!(restored.sessions[&alice].pending_hull_repair().is_none());

    let recipient_path = root.join("accounts/bob/session.local.json");
    let mut stale: serde_json::Value =
        serde_json::from_slice(&fs::read(&recipient_path).unwrap()).unwrap();
    stale["hull"] = serde_json::json!(peer_capacity * 0.25);
    stale["hull_revision"] = serde_json::json!(0);
    fs::write(&recipient_path, serde_json::to_vec(&stale).unwrap()).unwrap();
    drop(restored);
    let mut replayed = GameState::new(sector, bind, root.clone()).unwrap();
    assert_eq!(
        replayed.sessions[&bob].hull_balance().0,
        (peer_capacity * 0.25 + 2520.).min(peer_capacity)
    );
    replayed.deliver_hull_repairs().unwrap();
    assert_eq!(replayed.energy_ledger.revision(), 2);

    // A visible pilot just beyond the archived 3004m code range costs nothing.
    replayed.sessions.get_mut(&bob).unwrap().position = [3005., 0., 0.];
    let full_reactor = replayed.sessions[&alice].energy_balance().1;
    replayed
        .sessions
        .get_mut(&alice)
        .unwrap()
        .set_energy_for_test(full_reactor);
    replayed.update_views();
    activate(&mut replayed, alice, bob, 95);
    assert_eq!(replayed.sessions[&alice].energy_balance().0, full_reactor);
    assert!(replayed.sessions[&alice].pending_hull_repair().is_none());
    fs::remove_dir_all(root).unwrap();
}

/// Area repair charges once and persists exactly one grant for nearby group members.
#[test]
fn area_shield_and_hull_repair_only_nearby_group_members_once() {
    let root = std::env::temp_dir().join(format!("enb-area-repair-{}", rand::random::<u64>()));
    let accounts = AccountDirectory::new(&root);
    let alice = accounts
        .create_class(
            "alice".into(),
            "example",
            "Alice".into(),
            crate::classes::CharacterClass::parse("tt").unwrap(),
        )
        .unwrap()
        .account
        .character
        .game_id;
    let bob = accounts
        .create("bob".into(), "example", "Bob".into())
        .unwrap()
        .account
        .character
        .game_id;
    let outsider = accounts
        .create("outsider".into(), "example", "Outsider".into())
        .unwrap()
        .account
        .character
        .game_id;
    let catalog = crate::items::ItemCatalog::load().unwrap();
    let profile = crate::account::Account::load(&root).unwrap().character;
    let mut inventory = crate::inventory::Inventory::starter_for(&profile, &catalog).unwrap();
    inventory.equipped[1].as_mut().unwrap().template = 3337;
    fs::write(
        root.join("inventory.json"),
        serde_json::to_vec(&inventory).unwrap(),
    )
    .unwrap();
    let sector: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
    let bind = Ipv4Addr::new(127, 0, 0, 2);
    let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    login(&mut game, alice, 22971, "alice");
    login(&mut game, bob, 22972, "bob");
    login(&mut game, outsider, 22973, "outsider");
    game.groups.invite(alice, bob).unwrap();
    game.groups.accept(bob).unwrap();
    let center = game.sessions[&alice].position;
    game.sessions.get_mut(&bob).unwrap().position = [center[0] + 1000., center[1], center[2]];
    game.sessions.get_mut(&outsider).unwrap().position = [center[0] + 500., center[1], center[2]];
    for id in [alice, bob, outsider] {
        let ship = game.sessions.get_mut(&id).unwrap();
        ship.set_shield_for_test(0.);
        ship.set_hull_for_test(ship.hull_balance().1 * 0.1);
        ship.checkpoint_session(true).unwrap();
    }
    game.update_views();
    let energy = game.sessions[&alice].energy_balance().0;
    activate(&mut game, alice, outsider, 14);
    assert_eq!(game.sessions[&alice].energy_balance().0, energy - 300.);
    advance(&mut game, 4.2);
    assert!(game.sessions[&alice].shield_balance().0 > 0.);
    assert!(game.sessions[&bob].shield_balance().0 > 0.);
    assert!(game.energy_ledger.shield_balance(outsider).is_none());
    let after_shield = game.sessions[&bob].shield_balance().0;
    assert_eq!(game.energy_ledger.revision(), 1);
    drop(game);
    let mut restored = GameState::new(sector, bind, root.clone()).unwrap();
    restored.deliver_shield_recharges().unwrap();
    assert_eq!(restored.sessions[&bob].shield_balance().0, after_shield);
    assert_eq!(restored.energy_ledger.revision(), 1);
    restored.update_views();
    let outside_hull = restored.sessions[&outsider].hull_balance().0;
    activate(&mut restored, alice, outsider, 9);
    advance(&mut restored, 4.2);
    assert!(restored.sessions[&bob].hull_balance().0 > outside_hull);
    assert_eq!(restored.sessions[&outsider].hull_balance().0, outside_hull);
    assert_eq!(restored.energy_ledger.revision(), 2);
    restored.sessions.get_mut(&bob).unwrap().position = [center[0] + 2500., center[1], center[2]];
    restored
        .sessions
        .get_mut(&bob)
        .unwrap()
        .set_shield_for_test(0.);
    restored
        .sessions
        .get_mut(&bob)
        .unwrap()
        .checkpoint_session(true)
        .unwrap();
    restored.update_views();
    activate(&mut restored, alice, outsider, 61);
    let temporary = root.join("energy_transfers.local.json.tmp");
    fs::create_dir(&temporary).unwrap();
    assert!(restored.advance(5.2).is_err());
    assert_eq!(restored.energy_ledger.revision(), 2);
    assert!(restored.sessions[&alice]
        .pending_shield_recharge()
        .is_some());
    fs::remove_dir(&temporary).unwrap();
    drop(restored);
    let mut replayed = GameState::new(
        serde_json::from_str(include_str!("../data/sector.json")).unwrap(),
        bind,
        root.clone(),
    )
    .unwrap();
    replayed.deliver_shield_recharges().unwrap();
    assert_eq!(replayed.energy_ledger.revision(), 3);
    assert!(replayed.sessions[&bob].shield_balance().0 > 0.);
    assert!(replayed.energy_ledger.shield_balance(outsider).is_none());
    replayed.deliver_shield_recharges().unwrap();
    assert_eq!(replayed.energy_ledger.revision(), 3);
    fs::remove_dir_all(root).unwrap();
}

/// Native rank-five support and rank-one self repair retain paid casts.
#[test]
fn combat_repair_ranks_resume_and_self_rank_ignores_selected_peer() {
    let root = std::env::temp_dir().join(format!("enb-repair-ranks-{}", rand::random::<u64>()));
    let accounts = AccountDirectory::new(&root);
    let alice_stored = accounts
        .create_class(
            "alice".into(),
            "example",
            "Alice".into(),
            crate::classes::CharacterClass::parse("tt").unwrap(),
        )
        .unwrap();
    let alice = alice_stored.account.character.game_id;
    let bob = accounts
        .create("bob".into(), "example", "Bob".into())
        .unwrap()
        .account
        .character
        .game_id;
    let items = crate::items::ItemCatalog::load().unwrap();
    let mut inventory =
        crate::inventory::Inventory::starter_for(&alice_stored.account.character, &items).unwrap();
    inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3335, 1));
    fs::write(
        alice_stored.directory.join("inventory.json"),
        serde_json::to_vec(&inventory).unwrap(),
    )
    .unwrap();
    let sector: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
    let bind = Ipv4Addr::new(127, 0, 0, 2);
    let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    login(&mut game, alice, 22921, "alice");
    login(&mut game, bob, 22922, "bob");
    assert!(game.sessions[&alice].energy_balance().1 >= 500.);
    game.sessions.get_mut(&bob).unwrap().set_shield_for_test(0.);
    let bob_hull = game.sessions[&bob].hull_balance().1;
    game.sessions
        .get_mut(&bob)
        .unwrap()
        .set_hull_for_test(bob_hull * 0.2);
    game.sessions
        .get_mut(&bob)
        .unwrap()
        .checkpoint_session(true)
        .unwrap();
    game.update_views();
    let energy = game.sessions[&alice].energy_balance().0;
    activate(&mut game, alice, bob, 32);
    assert_eq!(game.sessions[&alice].energy_balance().0, energy - 250.);
    drop(game);
    let mut resumed = GameState::new(sector, bind, root.clone()).unwrap();
    advance(&mut resumed, 3.1);
    assert_eq!(resumed.energy_ledger.revision(), 1);
    assert!(resumed.sessions[&bob].shield_balance().0 > 0.);
    assert!(resumed.sessions[&alice].pending_shield_recharge().is_none());

    resumed.update_views();
    activate(&mut resumed, alice, bob, 31);
    advance(&mut resumed, 3.1);
    assert_eq!(resumed.energy_ledger.revision(), 2);
    assert!(resumed.sessions[&bob].hull_balance().0 > bob_hull * 0.2);
    resumed
        .sessions
        .get_mut(&alice)
        .unwrap()
        .set_shield_for_test(0.);
    resumed
        .sessions
        .get_mut(&alice)
        .unwrap()
        .checkpoint_session(true)
        .unwrap();
    resumed.update_views();
    activate(&mut resumed, alice, bob, 91);
    advance(&mut resumed, 0.1);
    assert_eq!(resumed.energy_ledger.revision(), 3);
    assert!(resumed.sessions[&alice].shield_balance().0 > 0.);
    assert_eq!(
        resumed.energy_ledger.shield_balance(bob).unwrap().revision,
        1
    );
    fs::remove_dir_all(root).unwrap();
}

fn advance(game: &mut GameState, seconds: f32) {
    for _ in 0..(seconds / 0.1).ceil() as usize {
        game.advance(0.1).unwrap();
    }
}

/// PW area Gravity Link and PS single-target casts survive restart without
/// doubling reactor cost or widening either pilot's target selection.
#[test]
fn gravity_link_area_and_single_target_persist_with_typed_damage() {
    let root = std::env::temp_dir().join(format!("enb-gravity-{}", rand::random::<u64>()));
    let accounts = AccountDirectory::new(&root);
    let warrior = accounts
        .create_class(
            "warrior".into(),
            "example",
            "Warrior".into(),
            crate::classes::CharacterClass::parse("pw").unwrap(),
        )
        .unwrap()
        .account
        .character
        .game_id;
    let sentinel = accounts
        .create_class(
            "sentinel".into(),
            "example",
            "Sentinel".into(),
            crate::classes::CharacterClass::parse("ps").unwrap(),
        )
        .unwrap()
        .account
        .character
        .game_id;
    let mut sector: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
    let mut first = sector.mobs[0].clone();
    first.source_id = 910_001;
    first.spawn = [1000., 0., 0.];
    first.aggressive = false;
    let mut second = first.clone();
    second.source_id = 910_002;
    second.spawn = [1800., 0., 0.];
    let mut distant = first.clone();
    distant.source_id = 910_003;
    distant.spawn = [5500., 0., 0.];
    sector.mobs = vec![first, second, distant];
    let bind = Ipv4Addr::new(127, 0, 0, 2);
    let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    login(&mut game, warrior, 23431, "warrior");
    login(&mut game, sentinel, 23432, "sentinel");
    game.update_views();
    let warrior_energy = game.sessions[&warrior].energy_balance().0;
    activate(&mut game, warrior, MOB_ID, 10);
    assert_eq!(
        game.sessions[&warrior].energy_balance().0,
        warrior_energy - 55.
    );
    let torn_world = root.join("combat/1076.json.next");
    fs::create_dir(&torn_world).unwrap();
    game.advance(1.1).unwrap(); // the game loop defers a failed session independently
    assert!(lock_combat(game.combats.get(&sector.id).unwrap())
        .unwrap()
        .actors[0]
        .gravity
        .is_empty());
    assert_eq!(
        game.sessions[&warrior].energy_balance().0,
        warrior_energy - 55.
    );
    fs::remove_dir(torn_world).unwrap();
    advance(&mut game, 1.1);
    let world = lock_combat(game.combats.get(&sector.id).unwrap()).unwrap();
    assert_eq!(world.actors[0].gravity.len(), 1);
    assert_eq!(world.actors[1].gravity.len(), 1);
    assert!(world.actors[2].gravity.is_empty());
    assert!(world.actors[0].gravity_speed_fraction() < 1.);
    let mut struck = world.actors[0].clone();
    drop(world);
    let ordinary = struck.damage_by_typed(10., warrior, 3);
    let impact = struck.damage_by_typed(10., warrior, 0);
    assert_eq!(ordinary, 10.);
    assert_eq!(impact, 13.);
    let sentinel_energy = game.sessions[&sentinel].energy_balance().0;
    activate(&mut game, sentinel, MOB_ID, 10); // PS cannot use PW area tier.
    assert_eq!(game.sessions[&sentinel].energy_balance().0, sentinel_energy);
    activate(&mut game, sentinel, MOB_ID + 2, 59);
    assert_eq!(
        game.sessions[&sentinel].energy_balance().0,
        sentinel_energy - 45.
    );
    advance(&mut game, 1.1);
    let world = lock_combat(game.combats.get(&sector.id).unwrap()).unwrap();
    assert_eq!(world.actors[2].gravity.len(), 1);
    assert_eq!(world.actors[1].gravity.len(), 1);
    drop(world);
    game.sessions
        .get_mut(&warrior)
        .unwrap()
        .checkpoint_session(true)
        .unwrap();
    game.sessions
        .get_mut(&sentinel)
        .unwrap()
        .checkpoint_session(true)
        .unwrap();
    let warrior_saved_energy = game.sessions[&warrior].energy_balance().0;
    let sentinel_saved_energy = game.sessions[&sentinel].energy_balance().0;
    drop(game);
    let restored = GameState::new(sector, bind, root.clone()).unwrap();
    let world = lock_combat(restored.combats.get(&1076).unwrap()).unwrap();
    assert_eq!(world.actors[0].gravity.len(), 1);
    assert_eq!(world.actors[1].gravity.len(), 1);
    assert_eq!(world.actors[2].gravity.len(), 1);
    assert_eq!(
        restored.sessions[&warrior].energy_balance().0,
        warrior_saved_energy
    );
    assert_eq!(
        restored.sessions[&sentinel].energy_balance().0,
        sentinel_saved_energy
    );
    drop(world);
    fs::remove_dir_all(root).unwrap();
}

/// A selected ally's mounted item and the caster's reactor settle once across two failures.
#[test]
fn equipment_repair_toggle_is_atomic_and_replays_without_free_ally_structure() {
    let root = std::env::temp_dir().join(format!("enb-equip-repair-{}", rand::random::<u64>()));
    let accounts = AccountDirectory::new(&root);
    let alice = accounts
        .create_class(
            "alice".into(),
            "example",
            "Alice".into(),
            crate::classes::CharacterClass::parse("te").unwrap(),
        )
        .unwrap()
        .account
        .character
        .game_id;
    let bob_account = accounts
        .create("bob".into(), "example", "Bob".into())
        .unwrap();
    let bob = bob_account.account.character.game_id;
    let items = crate::items::ItemCatalog::load().unwrap();
    let mut inventory =
        crate::inventory::Inventory::starter_for(&bob_account.account.character, &items).unwrap();
    inventory.equipped[0].as_mut().unwrap().structure = 0.4;
    fs::write(
        root.join("accounts/bob/inventory.json"),
        serde_json::to_vec(&inventory).unwrap(),
    )
    .unwrap();
    let sector: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
    let bind = Ipv4Addr::new(127, 0, 0, 2);
    let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    login(&mut game, alice, 23201, "alice");
    login(&mut game, bob, 23202, "bob");
    game.update_views();
    activate(&mut game, alice, bob, 94);
    assert_eq!(
        game.sessions[&alice].equipment_repair_target_for_test(),
        Some(bob)
    );
    game.sessions
        .get_mut(&alice)
        .unwrap()
        .equipment_repair_due_for_test();
    let blocked = root.join("energy_transfers.local.json.tmp");
    fs::create_dir(&blocked).unwrap();
    assert!(game.advance(0.1).is_err());
    assert_eq!(game.energy_ledger.revision(), 0);
    assert!(game.sessions[&alice].pending_equipment_repair().is_some());
    fs::remove_dir(&blocked).unwrap();
    let energy_before = game.sessions[&alice].energy_balance().0;
    let torn_recipient = root.join("accounts/bob/inventory.json.tmp");
    fs::create_dir(&torn_recipient).unwrap();
    assert!(game.deliver_equipment_repairs().is_err());
    assert_eq!(game.energy_ledger.revision(), 1);
    fs::remove_dir(&torn_recipient).unwrap();
    drop(game);
    let mut restored = GameState::new(sector, bind, root.clone()).unwrap();
    assert!(
        (restored.sessions[&bob].inventory.equipped[0]
            .as_ref()
            .unwrap()
            .structure
            - 0.43)
            .abs()
            < 0.0001
    );
    assert_eq!(
        restored.sessions[&alice].energy_balance().0,
        energy_before - crate::equipment_repair::ENERGY_PER_TICK
    );
    restored.deliver_equipment_repairs().unwrap();
    assert_eq!(restored.energy_ledger.revision(), 1);
    assert!(restored.sessions[&alice]
        .pending_equipment_repair()
        .is_none());
    activate(&mut restored, alice, bob, 94); // switch off without another debit
    assert_eq!(
        restored.sessions[&alice].equipment_repair_target_for_test(),
        None
    );
    fs::remove_dir_all(root).unwrap();
}

/// Native self and area tiers use distinct structure rates and bounded recipients.
#[test]
fn equipment_repair_self_and_area_tiers_only_touch_eligible_pilots() {
    let root = std::env::temp_dir().join(format!("enb-equip-area-{}", rand::random::<u64>()));
    let accounts = AccountDirectory::new(&root);
    let alice = accounts
        .create_class(
            "alice".into(),
            "example",
            "Alice".into(),
            crate::classes::CharacterClass::parse("te").unwrap(),
        )
        .unwrap()
        .account
        .character
        .game_id;
    let others: Vec<_> = ["bob", "carol", "dave"]
        .into_iter()
        .map(|username| {
            accounts
                .create(username.into(), "example", username.into())
                .unwrap()
        })
        .collect();
    let ids: Vec<_> = others
        .iter()
        .map(|stored| stored.account.character.game_id)
        .collect();
    let items = crate::items::ItemCatalog::load().unwrap();
    for (directory, profile) in std::iter::once((
        root.as_path(),
        crate::account::Account::load(&root).unwrap().character,
    ))
    .chain(
        others
            .iter()
            .map(|stored| (stored.directory.as_path(), stored.account.character.clone())),
    ) {
        let mut inventory = crate::inventory::Inventory::starter_for(&profile, &items).unwrap();
        inventory.equipped[0].as_mut().unwrap().structure = 0.4;
        fs::write(
            directory.join("inventory.json"),
            serde_json::to_vec(&inventory).unwrap(),
        )
        .unwrap();
    }
    let sector: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
    let bind = Ipv4Addr::new(127, 0, 0, 2);
    let mut game = GameState::new(sector, bind, root.clone()).unwrap();
    for (id, port, name) in [
        (alice, 23211, "alice"),
        (ids[0], 23212, "bob"),
        (ids[1], 23213, "carol"),
        (ids[2], 23214, "dave"),
    ] {
        login(&mut game, id, port, name);
    }
    game.sessions.get_mut(&ids[0]).unwrap().position = [1000., 0., 0.];
    game.sessions.get_mut(&ids[1]).unwrap().position = [1800., 0., 0.];
    game.sessions.get_mut(&ids[2]).unwrap().position = [5000., 0., 0.];
    game.update_views();
    activate(&mut game, alice, ids[0], 8);
    game.sessions
        .get_mut(&alice)
        .unwrap()
        .equipment_repair_due_for_test();
    game.advance(0.1).unwrap();
    for (id, expected) in [(alice, 0.46), (ids[0], 0.7), (ids[1], 0.46), (ids[2], 0.4)] {
        assert!(
            (game.sessions[&id].inventory.equipped[0]
                .as_ref()
                .unwrap()
                .structure
                - expected)
                .abs()
                < 0.0001
        );
    }
    activate(&mut game, alice, ids[0], 8); // toggle area stream off
    assert_eq!(
        game.sessions[&alice].equipment_repair_target_for_test(),
        None
    );
    activate(&mut game, alice, ids[0], 90); // rank one ignores selected peer
    assert_eq!(
        game.sessions[&alice].equipment_repair_target_for_test(),
        Some(alice)
    );
    game.sessions
        .get_mut(&alice)
        .unwrap()
        .equipment_repair_due_for_test();
    game.advance(0.1).unwrap();
    assert!(
        (game.sessions[&alice].inventory.equipped[0]
            .as_ref()
            .unwrap()
            .structure
            - 0.47)
            .abs()
            < 0.0001
    );
    assert!(
        (game.sessions[&ids[0]].inventory.equipped[0]
            .as_ref()
            .unwrap()
            .structure
            - 0.7)
            .abs()
            < 0.0001
    );
    activate(&mut game, alice, ids[0], 90); // switch self stream off
    game.sessions
        .get_mut(&alice)
        .unwrap()
        .set_energy_for_test(0.);
    activate(&mut game, alice, alice, 90);
    assert_eq!(
        game.sessions[&alice].equipment_repair_target_for_test(),
        None
    );
    game.sessions
        .get_mut(&alice)
        .unwrap()
        .set_energy_for_test(100.);
    game.sessions.get_mut(&ids[0]).unwrap().position = [5000., 0., 0.];
    game.update_views();
    activate(&mut game, alice, ids[0], 94);
    assert_eq!(
        game.sessions[&alice].equipment_repair_target_for_test(),
        None
    );
    fs::remove_dir_all(root).unwrap();
}

fn land_npc_shots(game: &mut GameState) {
    let deadline = game
        .combats
        .values()
        .filter_map(|combat| {
            lock_combat(combat)
                .unwrap()
                .pending_mob_shots()
                .iter()
                .map(|shot| shot.due_ms)
                .max()
        })
        .max()
        .expect("expected a launched NPC shot");
    game.advance_at(0., deadline).unwrap();
}

/// Returns:
///     Vec<Datagram>: Native attacker updates emitted to authenticated pilots.
fn attacker_updates(game: &mut GameState) -> Vec<Datagram> {
    game.take_outgoing()
        .into_iter()
        .filter(|outbound| outbound.port == SECTOR_PORT)
        .filter_map(|outbound| Datagram::decode(&outbound.data).ok())
        .flat_map(|packet| {
            let mut frames = Vec::new();
            let mut remaining = packet.body.as_slice();
            while remaining.len() >= 4 {
                let length = u16::from_le_bytes([remaining[0], remaining[1]]) as usize;
                if length < 4 || length > remaining.len() {
                    break;
                }
                if let Ok(frame) = Frame::decode(&remaining[..length]) {
                    if frame.opcode == 0x8b {
                        frames.push(Datagram {
                            opcode: frame.opcode,
                            player: packet.player,
                            sequence: packet.sequence,
                            body: frame.body,
                        });
                    }
                }
                remaining = &remaining[length..];
            }
            frames
        })
        .collect()
}

/// Verify native attacker changes across two pilots and short replacement.
#[test]
fn mob_attacker_updates_follow_actual_shots_and_survive_replacement() {
    let mut game = game();
    let alice = FIRST_PLAYER_ID;
    let bob = alice + 1;
    login(&mut game, alice, 22901, "alice");
    login(&mut game, bob, 22902, "bob");
    game.take_outgoing();
    game.sessions.get_mut(&alice).unwrap().position = [15_000., 15_000., 0.];
    game.sessions.get_mut(&bob).unwrap().position = [30_000., 30_000., 0.];
    game.advance(0.1).unwrap();
    let started = attacker_updates(&mut game);
    assert_eq!(started.len(), 1);
    assert_eq!(started[0].player, alice);
    assert_eq!(started[0].body, packets::attacker_update(MOB_ID, true).body);
    game.advance(0.1).unwrap();
    assert!(attacker_updates(&mut game).is_empty());

    let root = game.root.clone();
    let sector = game.initial_sector.clone();
    let bind = game.bind;
    drop(game);
    let mut restored = GameState::new(sector, bind, root).unwrap();
    restored.advance(0.1).unwrap();
    assert!(attacker_updates(&mut restored).is_empty());
    restored.sessions.get_mut(&alice).unwrap().position = [30_000., 30_000., 0.];
    restored.sessions.get_mut(&bob).unwrap().position = [15_000., 15_000., 0.];
    lock_combat(&restored.sessions[&bob].combat).unwrap().actors[0].damage_by(1., bob);
    restored.advance(0.1).unwrap();
    let stopped = attacker_updates(&mut restored);
    assert_eq!(stopped.len(), 1);
    assert_eq!(stopped[0].player, alice);
    assert_eq!(
        stopped[0].body,
        packets::attacker_update(MOB_ID, false).body
    );
    advance(&mut restored, 3.);
    let switched = attacker_updates(&mut restored);
    assert_eq!(switched.len(), 1);
    assert_eq!(switched[0].player, bob);
    assert_eq!(
        switched[0].body,
        packets::attacker_update(MOB_ID, true).body
    );
    lock_combat(&restored.sessions[&bob].combat).unwrap().actors[0].damage_by(1_000_000., bob);
    restored.advance(0.1).unwrap();
    let ended = attacker_updates(&mut restored);
    assert_eq!(ended.len(), 1);
    assert_eq!(ended[0].player, bob);
    assert_eq!(ended[0].body, packets::attacker_update(MOB_ID, false).body);
}

/// A single ledger revision survives stale checkpoints and rejects repeat actions.
#[test]
fn targeted_reactor_transfer_is_atomic_across_two_players_and_replacement() {
    let initial = game();
    let root = initial.root.clone();
    let sector = initial.initial_sector.clone();
    let bind = initial.bind;
    drop(initial);
    let inventory_path = root.join("inventory.json");
    let mut inventory: crate::inventory::Inventory =
        serde_json::from_slice(&fs::read(&inventory_path).unwrap()).unwrap();
    inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1946, 1));
    fs::write(&inventory_path, serde_json::to_vec(&inventory).unwrap()).unwrap();

    let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    let alice = FIRST_PLAYER_ID;
    let bob = alice + 1;
    login(&mut game, alice, 22731, "alice");
    login(&mut game, bob, 22732, "bob");
    game.sessions
        .get_mut(&bob)
        .unwrap()
        .set_energy_for_test(100.);
    game.sessions
        .get_mut(&bob)
        .unwrap()
        .checkpoint_session(true)
        .unwrap();
    game.advance_at(0., crate::resources::unix_ms()).unwrap();
    game.handle_input(
        alice,
        0x17,
        &[alice.to_le_bytes(), bob.to_le_bytes()].concat(),
    )
    .unwrap();
    let before = game.sessions[&alice].energy_balance().0;
    let epoch = game.sessions[&alice].energy_transfer_epoch();
    let request = Datagram {
        opcode: 0x5d,
        player: alice,
        sequence: 2,
        body: [alice.to_le_bytes().as_slice(), &[2, 9]].concat(),
    };
    game.handle_udp(
        SECTOR_PORT,
        "127.0.0.1:22731".parse().unwrap(),
        request.clone(),
    )
    .unwrap();
    game.advance_at(0., crate::resources::unix_ms()).unwrap();
    assert_eq!(game.sessions[&alice].energy_balance().0, before - 85.);
    assert_eq!(game.sessions[&bob].energy_balance().0, 229.);
    assert_eq!(game.energy_ledger.revision(), 1);
    game.handle_udp(SECTOR_PORT, "127.0.0.1:22731".parse().unwrap(), request)
        .unwrap();
    game.advance_at(0., crate::resources::unix_ms()).unwrap();
    assert_eq!(game.energy_ledger.revision(), 1);
    game.sessions.get_mut(&bob).unwrap().position = [5_000., 0., 0.];
    assert!(game
        .handle_event(
            alice,
            ClientEvent::EnergyTransfer {
                target: bob,
                slot: 9,
                epoch,
                sequence: 3
            }
        )
        .is_err());
    assert_eq!(game.energy_ledger.revision(), 1);
    game.sessions.get_mut(&bob).unwrap().position = [0.; 3];

    // Simulate a crash after the atomic ledger rename but before either
    // session checkpoint reached storage.
    for (directory, old_energy) in [(&root, before), (&root.join("accounts/bob"), 100.)] {
        let path = directory.join("session.local.json");
        let mut saved: serde_json::Value =
            serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
        saved["energy"] = serde_json::json!(old_energy);
        saved["energy_revision"] = serde_json::json!(0);
        fs::write(path, serde_json::to_vec(&saved).unwrap()).unwrap();
    }
    drop(game);
    let mut restored = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    assert_eq!(restored.sessions[&alice].energy_balance().0, before - 85.);
    assert_eq!(restored.sessions[&bob].energy_balance().0, 229.);
    restored
        .handle_event(
            alice,
            ClientEvent::EnergyTransfer {
                target: bob,
                slot: 9,
                epoch,
                sequence: 2,
            },
        )
        .unwrap();
    assert_eq!(restored.energy_ledger.revision(), 1);

    let temporary = root.join("energy_transfers.local.json.tmp");
    fs::create_dir(&temporary).unwrap();
    assert!(restored
        .handle_event(
            alice,
            ClientEvent::EnergyTransfer {
                target: bob,
                slot: 9,
                epoch,
                sequence: 3
            }
        )
        .is_err());
    assert_eq!(restored.energy_ledger.revision(), 1);
    assert_eq!(restored.sessions[&alice].energy_balance().0, before - 85.);
    assert_eq!(restored.sessions[&bob].energy_balance().0, 229.);
    fs::remove_dir(temporary).unwrap();

    // The ledger commit still credits Bob when his checkpoint write fails.
    // Restart then replays only Bob's lagging revision from that one file.
    let recipient_temporary = root.join("accounts/bob/session.local.json.tmp");
    fs::create_dir(&recipient_temporary).unwrap();
    restored
        .handle_event(
            alice,
            ClientEvent::EnergyTransfer {
                target: bob,
                slot: 9,
                epoch,
                sequence: 3,
            },
        )
        .unwrap();
    assert_eq!(restored.energy_ledger.revision(), 2);
    assert_eq!(restored.sessions[&alice].energy_balance().0, before - 170.);
    assert_eq!(restored.sessions[&bob].energy_balance().0, 358.);
    fs::remove_dir(recipient_temporary).unwrap();
    drop(restored);
    let mut restored = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    assert_eq!(restored.sessions[&alice].energy_balance().0, before - 170.);
    assert_eq!(restored.sessions[&bob].energy_balance().0, 358.);

    // Real logout acknowledges the revision in player.json and retains the
    // game's existing full-reactor behavior for the next ordinary login.
    restored.sessions.get_mut(&alice).unwrap().logout().unwrap();
    restored.sessions.get_mut(&bob).unwrap().logout().unwrap();
    drop(restored);
    let ordinary_login = GameState::new(sector, bind, root).unwrap();
    assert_eq!(
        ordinary_login.sessions[&alice].energy_transfer_revision(),
        2
    );
    assert_eq!(ordinary_login.sessions[&bob].energy_transfer_revision(), 2);
    assert_eq!(
        ordinary_login.sessions[&alice].energy_balance().0,
        ordinary_login.sessions[&alice].energy_balance().1
    );
    assert_eq!(
        ordinary_login.sessions[&bob].energy_balance().0,
        ordinary_login.sessions[&bob].energy_balance().1
    );
}

/// One native device request atomically spends reactor energy and buffs a peer.
#[test]
fn grouped_scan_boost_replays_both_sides_after_stale_checkpoints() {
    let initial = game();
    let root = initial.root.clone();
    let sector = initial.initial_sector.clone();
    let bind = initial.bind;
    drop(initial);
    let path = root.join("inventory.json");
    let mut inventory: crate::inventory::Inventory =
        serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
    inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1813, 1));
    fs::write(&path, serde_json::to_vec(&inventory).unwrap()).unwrap();

    let alice = FIRST_PLAYER_ID;
    let bob = alice + 1;
    let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    login(&mut game, alice, 22751, "alice");
    login(&mut game, bob, 22752, "bob");
    game.advance(0.1).unwrap();
    game.handle_input(
        alice,
        0x17,
        &[alice.to_le_bytes(), bob.to_le_bytes()].concat(),
    )
    .unwrap();
    let before_energy = game.sessions[&alice].energy_balance().0;
    let cost = crate::peer_buffs::friendly_offer(
        crate::items::ItemCatalog::load()
            .unwrap()
            .get(1813)
            .unwrap(),
    )
    .unwrap()
    .cost;
    let before_scan = game.sessions[&bob]
        .peer_snapshot()
        .unwrap()
        .stats
        .scan_range;
    let epoch = game.sessions[&alice].energy_transfer_epoch();
    assert!(game
        .handle_event(
            alice,
            ClientEvent::PeerDeviceBuff {
                target: bob,
                slot: 9,
                epoch,
                sequence: 2,
            },
        )
        .is_err()); // A visible stranger is not a grouped friendly target.
    game.groups.invite(alice, bob).unwrap();
    game.groups.accept(bob).unwrap();
    game.sessions.get_mut(&bob).unwrap().position = [5_000., 0., 0.];
    assert!(game
        .handle_event(
            alice,
            ClientEvent::PeerDeviceBuff {
                target: bob,
                slot: 9,
                epoch,
                sequence: 2,
            },
        )
        .is_err());
    game.sessions.get_mut(&bob).unwrap().position = [100., 0., 0.];
    let temporary = root.join("energy_transfers.local.json.tmp");
    fs::create_dir(&temporary).unwrap();
    assert!(game
        .handle_event(
            alice,
            ClientEvent::PeerDeviceBuff {
                target: bob,
                slot: 9,
                epoch,
                sequence: 2,
            },
        )
        .is_err());
    assert_eq!(game.sessions[&alice].energy_balance().0, before_energy);
    assert_eq!(
        game.sessions[&bob]
            .peer_snapshot()
            .unwrap()
            .stats
            .scan_range,
        before_scan
    );
    fs::remove_dir(temporary).unwrap();
    let recipient_temporary = root.join("accounts/bob/session.local.json.tmp");
    fs::create_dir(&recipient_temporary).unwrap();
    game.handle_udp(
        SECTOR_PORT,
        "127.0.0.1:22751".parse().unwrap(),
        Datagram {
            opcode: 0x5d,
            player: alice,
            sequence: 2,
            body: [alice.to_le_bytes().as_slice(), &[2, 9]].concat(),
        },
    )
    .unwrap();
    game.advance_at(0., crate::resources::unix_ms()).unwrap();
    fs::remove_dir(recipient_temporary).unwrap();
    assert_eq!(game.energy_ledger.revision(), 1);
    assert_eq!(
        game.sessions[&alice].energy_balance().0,
        before_energy - cost
    );
    assert!(
        game.sessions[&bob]
            .peer_snapshot()
            .unwrap()
            .stats
            .scan_range
            > before_scan
    );
    assert!(game.energy_ledger.peer_buff(bob).is_some());
    game.handle_event(
        alice,
        ClientEvent::PeerDeviceBuff {
            target: bob,
            slot: 9,
            epoch,
            sequence: 2,
        },
    )
    .unwrap();
    assert_eq!(game.energy_ledger.revision(), 1);

    // Crash after ledger rename: both session checkpoints still show pre-cast state.
    for (directory, sender) in [(&root, true), (&root.join("accounts/bob"), false)] {
        let path = directory.join("session.local.json");
        let mut saved: serde_json::Value =
            serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
        if sender {
            saved["energy"] = serde_json::json!(before_energy);
            saved["energy_revision"] = serde_json::json!(0);
        } else {
            saved["peer_buff"] = serde_json::Value::Null;
            saved["peer_buffs"] = serde_json::json!([]);
            saved["peer_buff_revision"] = serde_json::json!(0);
        }
        fs::write(path, serde_json::to_vec(&saved).unwrap()).unwrap();
    }
    drop(game);
    let restored = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    assert_eq!(
        restored.sessions[&alice].energy_balance().0,
        before_energy - cost
    );
    assert!(
        restored.sessions[&bob]
            .peer_snapshot()
            .unwrap()
            .stats
            .scan_range
            > before_scan
    );
    let saved: serde_json::Value =
        serde_json::from_slice(&fs::read(root.join("accounts/bob/session.local.json")).unwrap())
            .unwrap();
    assert_eq!(saved["peer_buff_revision"], 1);
    drop(restored);
    let mut corrupt = saved;
    corrupt["peer_buff_revision"] = serde_json::json!(999);
    fs::write(
        root.join("accounts/bob/session.local.json"),
        serde_json::to_vec(&corrupt).unwrap(),
    )
    .unwrap();
    assert!(GameState::new(sector, bind, root).is_err());
}

/// Both public recharge families spend caster energy and survive stale checkpoints.
#[test]
fn grouped_recharge_devices_restore_recipient_stats_and_sender_energy() {
    for (template, stat) in [(1805, "shield"), (2172, "reactor")] {
        let initial = game();
        let root = initial.root.clone();
        let sector = initial.initial_sector.clone();
        let bind = initial.bind;
        drop(initial);
        if stat == "shield" {
            let account_path = root.join("account.local.json");
            let mut account: serde_json::Value =
                serde_json::from_slice(&fs::read(&account_path).unwrap()).unwrap();
            account["character"]["race"] = serde_json::json!(2);
            fs::write(account_path, serde_json::to_vec(&account).unwrap()).unwrap();
        }
        let path = root.join("inventory.json");
        let mut inventory: crate::inventory::Inventory = if stat == "shield" {
            let account = crate::account::Account::load(&root).unwrap();
            crate::inventory::Inventory::starter_for(
                &account.character,
                &crate::items::ItemCatalog::load().unwrap(),
            )
            .unwrap()
        } else {
            serde_json::from_slice(&fs::read(&path).unwrap()).unwrap()
        };
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(template, 1));
        fs::write(&path, serde_json::to_vec(&inventory).unwrap()).unwrap();

        let alice = FIRST_PLAYER_ID;
        let bob = alice + 1;
        let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
        login(&mut game, alice, 22761, "alice");
        login(&mut game, bob, 22762, "bob");
        game.groups.invite(alice, bob).unwrap();
        game.groups.accept(bob).unwrap();
        game.advance(0.1).unwrap();
        game.handle_input(
            alice,
            0x17,
            &[alice.to_le_bytes(), bob.to_le_bytes()].concat(),
        )
        .unwrap();
        let before = game.sessions[&bob].peer_snapshot().unwrap().stats;
        let before_energy = game.sessions[&alice].energy_balance().0;
        let catalog = crate::items::ItemCatalog::load().unwrap();
        let offer = crate::peer_buffs::friendly_offer(catalog.get(template).unwrap()).unwrap();
        let epoch = game.sessions[&alice].energy_transfer_epoch();
        game.handle_udp(
            SECTOR_PORT,
            "127.0.0.1:22761".parse().unwrap(),
            Datagram {
                opcode: 0x5d,
                player: alice,
                sequence: 2,
                body: [alice.to_le_bytes().as_slice(), &[2, 9]].concat(),
            },
        )
        .unwrap();
        game.advance_at(0., crate::resources::unix_ms()).unwrap();
        assert_eq!(game.energy_ledger.revision(), 1);
        assert_eq!(
            game.sessions[&alice].energy_balance().0,
            before_energy - offer.cost
        );
        let after = game.sessions[&bob].peer_snapshot().unwrap().stats;
        match stat {
            "shield" => assert!(after.shield_recharge > before.shield_recharge),
            "reactor" => assert!(after.energy_recharge > before.energy_recharge),
            _ => unreachable!(),
        }
        let recipient_buff = game.energy_ledger.peer_buff(bob).unwrap();
        assert_eq!(recipient_buff.effect, offer.effect);
        assert_eq!(recipient_buff.operation, offer.operation);
        for (directory, sender) in [(&root, true), (&root.join("accounts/bob"), false)] {
            let checkpoint = directory.join("session.local.json");
            let mut saved: serde_json::Value =
                serde_json::from_slice(&fs::read(&checkpoint).unwrap()).unwrap();
            if sender {
                saved["energy"] = serde_json::json!(before_energy);
                saved["energy_revision"] = serde_json::json!(0);
            } else {
                saved["peer_buff"] = serde_json::Value::Null;
                saved["peer_buffs"] = serde_json::json!([]);
                saved["peer_buff_revision"] = serde_json::json!(0);
            }
            fs::write(checkpoint, serde_json::to_vec(&saved).unwrap()).unwrap();
        }
        drop(game);
        let mut restored = GameState::new(sector, bind, root).unwrap();
        assert_eq!(
            restored.sessions[&alice].energy_balance().0,
            before_energy - offer.cost
        );
        let replayed = restored.sessions[&bob].peer_snapshot().unwrap().stats;
        match stat {
            "shield" => assert_eq!(replayed.shield_recharge, after.shield_recharge),
            "reactor" => assert_eq!(replayed.energy_recharge, after.energy_recharge),
            _ => unreachable!(),
        }
        restored
            .handle_event(
                alice,
                ClientEvent::PeerDeviceBuff {
                    target: bob,
                    slot: 9,
                    epoch,
                    sequence: 2,
                },
            )
            .unwrap();
        assert_eq!(restored.energy_ledger.revision(), 1);
    }
}

/// Three catalog ally effects coexist through ledger replay and refresh.
#[test]
fn grouped_scan_reactor_and_signature_buffs_stack_and_resume_independently() {
    let initial = game();
    let root = initial.root.clone();
    let sector = initial.initial_sector.clone();
    let bind = initial.bind;
    drop(initial);
    let path = root.join("inventory.json");
    let mut inventory: crate::inventory::Inventory =
        serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
    inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1813, 1));
    inventory.equipped[10] = Some(crate::inventory::ItemInstance::stock(2172, 1));
    inventory.equipped[11] = Some(crate::inventory::ItemInstance::stock(1945, 1));
    fs::write(&path, serde_json::to_vec(&inventory).unwrap()).unwrap();
    // The fixture's Aspiration reactor already reduces signature by 74%,
    // stronger than Musca's 25%; give the recipient a neutral reactor.
    let recipient_path = root.join("accounts/bob/inventory.json");
    let mut recipient_inventory: crate::inventory::Inventory =
        serde_json::from_slice(&fs::read(&recipient_path).unwrap()).unwrap();
    recipient_inventory.equipped[1].as_mut().unwrap().template = 3337;
    fs::write(
        recipient_path,
        serde_json::to_vec(&recipient_inventory).unwrap(),
    )
    .unwrap();

    let alice = FIRST_PLAYER_ID;
    let bob = alice + 1;
    let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    login(&mut game, alice, 22851, "alice");
    login(&mut game, bob, 22852, "bob");
    game.groups.invite(alice, bob).unwrap();
    game.groups.accept(bob).unwrap();
    game.advance(0.1).unwrap();
    game.handle_input(
        alice,
        0x17,
        &[alice.to_le_bytes(), bob.to_le_bytes()].concat(),
    )
    .unwrap();
    let before = game.sessions[&bob].peer_snapshot().unwrap().stats;
    let source_energy = game.sessions[&alice].energy_balance().0;
    let epoch = game.sessions[&alice].energy_transfer_epoch();
    for (sequence, slot) in [(2, 9), (3, 10), (4, 11)] {
        game.handle_udp(
            SECTOR_PORT,
            "127.0.0.1:22851".parse().unwrap(),
            Datagram {
                opcode: 0x5d,
                player: alice,
                sequence,
                body: [alice.to_le_bytes().as_slice(), &[2, slot]].concat(),
            },
        )
        .unwrap();
        game.advance_at(0., crate::resources::unix_ms()).unwrap();
    }
    assert_eq!(game.energy_ledger.revision(), 3);
    assert_eq!(game.energy_ledger.peer_buffs(bob).len(), 3);
    let boosted = game.sessions[&bob].peer_snapshot().unwrap().stats;
    assert!(boosted.scan_range > before.scan_range);
    assert!(boosted.energy_recharge > before.energy_recharge);
    assert!(boosted.signature < before.signature);
    assert_eq!(boosted.signature, before.signature * 0.75);
    let mob_scan = 2500. + 150. * 10.;
    let between_detection_ranges = mob_scan + (before.signature + boosted.signature) / 2.;
    assert!(crate::mob_targeting::mob_sees_player(
        10,
        before.signature,
        between_detection_ranges,
        false
    ));
    assert!(!crate::mob_targeting::mob_sees_player(
        10,
        game.sessions[&bob].threat().signature,
        between_detection_ranges,
        false
    ));
    let after_energy = game.sessions[&alice].energy_balance().0;
    assert!(after_energy < source_energy);
    for (directory, sender) in [(&root, true), (&root.join("accounts/bob"), false)] {
        let checkpoint = directory.join("session.local.json");
        let mut saved: serde_json::Value =
            serde_json::from_slice(&fs::read(&checkpoint).unwrap()).unwrap();
        if sender {
            saved["energy"] = serde_json::json!(source_energy);
            saved["energy_revision"] = serde_json::json!(0);
        } else {
            saved["peer_buff"] = serde_json::Value::Null;
            saved["peer_buffs"] = serde_json::json!([]);
            saved["peer_buff_revision"] = serde_json::json!(0);
        }
        fs::write(checkpoint, serde_json::to_vec(&saved).unwrap()).unwrap();
    }
    drop(game);
    let mut restored = GameState::new(sector, bind, root).unwrap();
    assert_eq!(restored.sessions[&alice].energy_balance().0, after_energy);
    let checkpoint: serde_json::Value = serde_json::from_slice(
        &fs::read(restored.root.join("accounts/bob/session.local.json")).unwrap(),
    )
    .unwrap();
    assert_eq!(checkpoint["peer_buffs"].as_array().unwrap().len(), 3);
    let replayed = restored.sessions[&bob].peer_snapshot().unwrap().stats;
    assert_eq!(replayed.scan_range, boosted.scan_range);
    assert_eq!(replayed.energy_recharge, boosted.energy_recharge);
    assert_eq!(replayed.signature, boosted.signature);
    restored
        .handle_event(
            alice,
            ClientEvent::PeerDeviceBuff {
                target: bob,
                slot: 9,
                epoch,
                sequence: 2,
            },
        )
        .unwrap();
    assert_eq!(restored.energy_ledger.revision(), 3);
    restored
        .handle_event(
            alice,
            ClientEvent::PeerDeviceBuff {
                target: bob,
                slot: 9,
                epoch,
                sequence: 5,
            },
        )
        .unwrap();
    assert_eq!(restored.energy_ledger.revision(), 4);
    assert_eq!(restored.energy_ledger.peer_buffs(bob).len(), 3);
    let refreshed = restored.sessions[&bob].peer_snapshot().unwrap().stats;
    assert!(refreshed.scan_range > before.scan_range);
    assert!(refreshed.energy_recharge > before.energy_recharge);
    assert_eq!(refreshed.signature, boosted.signature);
}

#[test]
fn shared_combat_kill_awards_one_durable_combat_xp_receipt() {
    let initial = game();
    let root = initial.root.clone();
    let sector = initial.initial_sector.clone();
    let bind = initial.bind;
    drop(initial);
    let path = root.join("inventory.json");
    let mut inventory: serde_json::Value =
        serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
    inventory["xp_start_level"] = serde_json::json!(1);
    fs::write(&path, serde_json::to_vec(&inventory).unwrap()).unwrap();

    let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    let player = FIRST_PLAYER_ID;
    login(&mut game, player, 22451, "alice");
    let combat = Arc::clone(&game.sessions[&player].combat);
    let spawn = {
        let mut world = lock_combat(&combat).unwrap();
        let mob = &mut world.actors[0];
        assert_eq!(mob.definition.level, 1);
        mob.damage_by(1_000_000., player);
        mob.definition.source_id
    };
    game.advance_at(0., crate::resources::unix_ms()).unwrap();
    let saved: serde_json::Value = serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
    assert_eq!(saved["combat_xp"], 1000);
    let key = ((1076u64 << 32) | spawn as u64).to_string();
    assert_eq!(saved["combat_awards"][&key], 1);
    assert!(lock_loot(&game.loot_worlds[&1076])
        .unwrap()
        .pending_awards(player)
        .is_empty());

    game.advance_at(0., crate::resources::unix_ms()).unwrap();
    drop(game);
    let restored = GameState::new(sector, bind, root).unwrap();
    let saved: serde_json::Value = serde_json::from_slice(
        &fs::read(restored.sessions[&player].state_dir.join("inventory.json")).unwrap(),
    )
    .unwrap();
    assert_eq!(saved["combat_xp"], 1000);
}

#[test]
fn grouped_nav_explore_xp_replays_failed_recipient_after_restart() {
    let mut game = game();
    let root = game.root.clone();
    let sector = game.initial_sector.clone();
    let bind = game.bind;
    let alice = FIRST_PLAYER_ID;
    let bob = alice + 1;
    login(&mut game, alice, 22511, "alice");
    login(&mut game, bob, 22512, "bob");
    game.groups.invite(alice, bob).unwrap();
    game.groups.accept(bob).unwrap();
    let mut nav = game.sessions[&alice].sector.navpoints[0].clone();
    nav.id = 999_999;
    nav.name = "Group discovery".into();
    nav.object_type = 37;
    nav.navigation_type = 1;
    nav.signature = 1_000.;
    nav.radius = 0.;
    nav.position = [0.; 3];
    for (id, position) in [(alice, [0.; 3]), (bob, [39_000., 0., 0.])] {
        let session = game.sessions.get_mut(&id).unwrap();
        session.sector.navpoints = vec![nav.clone()];
        session.position = position;
    }
    let blocked = root.join("accounts/bob/inventory.json.tmp");
    fs::create_dir(&blocked).unwrap();
    game.advance_at(0., crate::resources::unix_ms()).unwrap();
    assert_eq!(game.sessions[&alice].inventory.explore_xp, 1200);
    assert_eq!(game.sessions[&bob].inventory.explore_xp, 0);
    assert_eq!(
        game.sessions[&alice].inventory.pending_explore_awards.len(),
        1
    );
    drop(game);

    fs::remove_dir(blocked).unwrap();
    let mut restored = GameState::new(sector, bind, root.clone()).unwrap();
    restored
        .advance_at(0., crate::resources::unix_ms())
        .unwrap();
    assert_eq!(restored.sessions[&alice].inventory.explore_xp, 1200);
    assert_eq!(restored.sessions[&bob].inventory.explore_xp, 1200);
    assert!(restored.sessions[&alice]
        .inventory
        .pending_explore_awards
        .is_empty());
    restored
        .advance_at(0., crate::resources::unix_ms())
        .unwrap();
    assert_eq!(restored.sessions[&bob].inventory.explore_xp, 1200);
}

#[test]
/// A current group peer waits for the credited pilot's private corpse lock.
fn logged_out_killer_releases_corpse_lock_for_group_peer() {
    let mut game = game();
    let alice = FIRST_PLAYER_ID;
    let bob = alice + 1;
    login(&mut game, alice, 22521, "alice");
    login(&mut game, bob, 22522, "bob");
    game.groups.invite(alice, bob).unwrap();
    game.groups.accept(bob).unwrap();
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
        &crate::items::ItemCatalog::load().unwrap(),
    )
    .unwrap();
    let now = crate::resources::unix_ms();
    let world = &game.loot_worlds[&1076];
    let id = lock_loot(world)
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
                killer: alice,
                position: [0.; 3],
                now_ms: now,
            },
            &catalog,
            &crate::items::ItemCatalog::load().unwrap(),
        )
        .unwrap()
        .unwrap();
    assert!(lock_loot(world)
        .unwrap()
        .prepare_collection(bob, id, None, now + 1)
        .is_err());
    game.sessions.get_mut(&alice).unwrap().logout().unwrap();
    game.advance_at(0., now + 1).unwrap();
    assert!(lock_loot(&game.loot_worlds[&1076])
        .unwrap()
        .prepare_collection(bob, id, None, now + 1)
        .is_ok());
}

#[test]
fn group_kill_freezes_two_awards_and_replays_only_failed_recipient() {
    let initial = game();
    let root = initial.root.clone();
    let sector = initial.initial_sector.clone();
    let bind = initial.bind;
    drop(initial);
    for path in [
        root.join("inventory.json"),
        root.join("accounts/bob/inventory.json"),
    ] {
        let mut inventory: serde_json::Value =
            serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
        inventory["xp_start_level"] = serde_json::json!(1);
        fs::write(path, serde_json::to_vec(&inventory).unwrap()).unwrap();
    }
    let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    let alice = FIRST_PLAYER_ID;
    let bob = alice + 1;
    login(&mut game, alice, 22461, "alice");
    login(&mut game, bob, 22462, "bob");
    game.groups.invite(alice, bob).unwrap();
    game.groups.accept(bob).unwrap();
    game.sessions.get_mut(&alice).unwrap().position = [0.; 3];
    game.sessions.get_mut(&bob).unwrap().position = [40_000., 0., 0.];
    assert_eq!(game.group_kill_credits(alice, 1076, 1).unwrap().len(), 1);
    game.sessions.get_mut(&bob).unwrap().position = [100., 0., 0.];
    let combat = Arc::clone(&game.sessions[&alice].combat);
    lock_combat(&combat).unwrap().actors[0].damage_by(1_000_000., alice);

    let blocked = root.join("accounts/bob/inventory.json.tmp");
    fs::create_dir(&blocked).unwrap();
    assert!(game.advance_at(0., crate::resources::unix_ms()).is_err());
    let alice_inventory: serde_json::Value =
        serde_json::from_slice(&fs::read(root.join("inventory.json")).unwrap()).unwrap();
    let bob_inventory: serde_json::Value =
        serde_json::from_slice(&fs::read(root.join("accounts/bob/inventory.json")).unwrap())
            .unwrap();
    assert_eq!(alice_inventory["combat_xp"], 1000);
    assert_eq!(bob_inventory["combat_xp"], 0);
    let pending = lock_loot(&game.loot_worlds[&1076])
        .unwrap()
        .pending_awards(bob);
    assert_eq!(pending.len(), 1);
    assert_eq!(pending[0].amount, Some(1000));
    drop(game);

    fs::remove_dir(blocked).unwrap();
    let mut restored = GameState::new(sector, bind, root.clone()).unwrap();
    login(&mut restored, bob, 22463, "bob");
    restored
        .advance_at(0., crate::resources::unix_ms())
        .unwrap();
    let alice_inventory: serde_json::Value =
        serde_json::from_slice(&fs::read(root.join("inventory.json")).unwrap()).unwrap();
    let bob_inventory: serde_json::Value =
        serde_json::from_slice(&fs::read(root.join("accounts/bob/inventory.json")).unwrap())
            .unwrap();
    assert_eq!(alice_inventory["combat_xp"], 1000);
    assert_eq!(bob_inventory["combat_xp"], 1000);
    assert!(lock_loot(&restored.loot_worlds[&1076])
        .unwrap()
        .pending_awards(bob)
        .is_empty());
}

#[test]
fn native_jumpstart_action_recovers_an_incapacitated_player_without_a_group() {
    let mut game = game();
    let source = FIRST_PLAYER_ID;
    let target = source + 1;
    login(&mut game, source, 22611, "alice");
    login(&mut game, target, 22612, "bob");
    {
        let victim = game.sessions.get_mut(&target).unwrap();
        victim.position = [1000., 0., 0.];
        victim
            .receive_mob_hit(&crate::npc_projectiles::MobHit {
                sector: 1076,
                sequence: 1,
                target,
                target_epoch: victim.threat().session_epoch,
                attacker_index: 0,
                attacker_source: 46,
                attacker_template: 0,
                attacker_generation: 0,
                damage: 1_000_000.,
                damage_type: 3,
            })
            .unwrap();
    }
    game.advance(0.1).unwrap();
    assert!(!game.groups.together(source, target));
    game.handle_input(
        source,
        0x2c,
        &[source, 26, target, 0].map(u32::to_le_bytes).concat(),
    )
    .unwrap();
    game.advance(0.1).unwrap();
    assert!(game.jumpstart_offers.contains_key(&target));
    assert!(game.sessions[&source].jumpstart_offer_info(target).is_ok());
    game.handle_input(
        target,
        0x2c,
        &[target, 11, 0, 0].map(u32::to_le_bytes).concat(),
    )
    .unwrap();
    game.advance(0.1).unwrap();
    assert!(!game.jumpstart_offers.contains_key(&target));
    assert!(game.sessions[&source].jumpstart_offer_info(target).is_err());
    advance(&mut game, 8.);
    assert!(game.sessions[&target].peer_snapshot().unwrap().hull > 0.);
    assert!(!game.groups.together(source, target));
}

#[test]
fn declined_jumpstart_allows_another_offer_without_joining_a_group() {
    let mut game = game();
    let source = FIRST_PLAYER_ID;
    let target = source + 1;
    login(&mut game, source, 22711, "alice");
    login(&mut game, target, 22712, "bob");
    {
        let victim = game.sessions.get_mut(&target).unwrap();
        victim.position = [1000., 0., 0.];
        victim
            .receive_mob_hit(&crate::npc_projectiles::MobHit {
                sector: 1076,
                sequence: 1,
                target,
                target_epoch: victim.threat().session_epoch,
                attacker_index: 0,
                attacker_source: 46,
                attacker_template: 0,
                attacker_generation: 0,
                damage: 1_000_000.,
                damage_type: 3,
            })
            .unwrap();
    }
    game.advance(0.1).unwrap();
    game.handle_input(
        source,
        0x2c,
        &[source, 26, target, 0].map(u32::to_le_bytes).concat(),
    )
    .unwrap();
    game.advance(0.1).unwrap();
    assert!(game.jumpstart_offers.contains_key(&target));
    game.handle_input(
        target,
        0x2c,
        &[target, 12, 0, 0].map(u32::to_le_bytes).concat(),
    )
    .unwrap();
    game.advance(0.1).unwrap();
    assert!(!game.jumpstart_offers.contains_key(&target));
    assert!(game.sessions[&source].jumpstart_offer_info(target).is_ok());
    assert!(!game.groups.together(source, target));
}

#[test]
fn npc_ack_failure_and_server_replacement_never_repeat_committed_player_damage() {
    let mut game = game();
    let player = FIRST_PLAYER_ID;
    login(&mut game, player, 22431, "alice");
    game.sessions.get_mut(&player).unwrap().position = [0.; 3];
    let combat = Arc::clone(&game.sessions[&player].combat);
    {
        let mut world = lock_combat(&combat).unwrap();
        world.actors[0].position = [100., 0., 0.];
        world.actors[0].definition.spawn = [100., 0., 0.];
    }
    game.advance(0.1).unwrap();
    let shot = lock_combat(&combat).unwrap().pending_mob_shots()[0].clone();
    let before = game.sessions[&player].peer_snapshot().unwrap().shield;
    let blocked = game.root.join("combat/1076.json.next");
    fs::create_dir(&blocked).unwrap();
    game.advance_at(0., shot.due_ms).unwrap();
    assert_eq!(
        game.sessions[&player].peer_snapshot().unwrap().shield,
        before - shot.hit.damage
    );
    assert_eq!(lock_combat(&combat).unwrap().pending_mob_shots().len(), 1);
    let root = game.root.clone();
    let sector = game.initial_sector.clone();
    let bind = game.bind;
    drop(game);
    fs::remove_dir(blocked).unwrap();
    let mut restored = GameState::new(sector, bind, root).unwrap();
    assert_eq!(
        restored.sessions[&player].peer_snapshot().unwrap().shield,
        before - shot.hit.damage
    );
    restored.advance_at(0., shot.due_ms).unwrap();
    assert_eq!(
        restored.sessions[&player].peer_snapshot().unwrap().shield,
        before - shot.hit.damage
    );
    assert!(lock_combat(&restored.sessions[&player].combat)
        .unwrap()
        .pending_mob_shots()
        .is_empty());
}

#[test]
fn npc_projectile_cannot_damage_its_target_after_a_sector_handoff() {
    let mut game = game();
    let player = FIRST_PLAYER_ID;
    login(&mut game, player, 22441, "alice");
    game.sessions.get_mut(&player).unwrap().position = [0.; 3];
    let source = Arc::clone(&game.sessions[&player].combat);
    {
        let mut world = lock_combat(&source).unwrap();
        world.actors[0].position = [100., 0., 0.];
        world.actors[0].definition.spawn = [100., 0., 0.];
    }
    game.advance(0.1).unwrap();
    let shot = lock_combat(&source).unwrap().pending_mob_shots()[0].clone();
    let before = game.sessions[&player].peer_snapshot().unwrap().shield;
    game.handle_event(
        player,
        ClientEvent::Chat {
            channel: 0,
            text: "/d_jump Saturn".into(),
        },
    )
    .unwrap();
    game.advance_at(0., shot.due_ms).unwrap();
    assert_eq!(game.sessions[&player].sector.id, 1071);
    // Handoff sessions are intentionally absent from peer snapshots.
    let checkpoint: serde_json::Value = serde_json::from_slice(
        &fs::read(game.sessions[&player].state_dir.join("session.local.json")).unwrap(),
    )
    .unwrap();
    assert_eq!(checkpoint["shield"].as_f64().unwrap() as f32, before);
    assert!(lock_combat(&source).unwrap().pending_mob_shots().is_empty());
}

#[test]
fn storage_failure_for_first_character_does_not_skip_other_players_or_groups() {
    let mut game = game();
    let alice = FIRST_PLAYER_ID;
    let bob = alice + 1;
    login(&mut game, alice, 22001, "alice");
    login(&mut game, bob, 22002, "bob");
    game.save().unwrap();
    let alice_dir = game.sessions[&alice].state_dir.clone();
    let bob_dir = game.sessions[&bob].state_dir.clone();
    fs::remove_file(alice_dir.join("groups.local.json")).unwrap();
    fs::create_dir(alice_dir.join("player.json.tmp")).unwrap();
    game.sessions.get_mut(&bob).unwrap().position = [100., 200., 0.];
    assert!(game.save().is_err());
    let player: serde_json::Value =
        serde_json::from_slice(&fs::read(bob_dir.join("player.json")).unwrap()).unwrap();
    let checkpoint: serde_json::Value =
        serde_json::from_slice(&fs::read(bob_dir.join("session.local.json")).unwrap()).unwrap();
    assert_eq!(player["position"], serde_json::json!([100., 200., 0.]));
    assert_eq!(checkpoint["position"], serde_json::json!([100., 200., 0.]));
    assert!(alice_dir.join("groups.local.json").is_file());
    fs::create_dir(alice_dir.join("session.local.json.tmp")).unwrap();
    game.sessions.get_mut(&bob).unwrap().position = [200., 300., 0.];
    assert!(game.checkpoint_session(true).is_err());
    let checkpoint: serde_json::Value =
        serde_json::from_slice(&fs::read(bob_dir.join("session.local.json")).unwrap()).unwrap();
    assert_eq!(checkpoint["position"], serde_json::json!([200., 300., 0.]));
}

#[test]
fn authentication_isolates_ids_endpoints_and_private_account_state() {
    let mut game = game();
    let a = FIRST_PLAYER_ID;
    let b = a + 1;
    login(&mut game, a, 22001, "alice");
    login(&mut game, b, 22002, "bob");
    game.advance(0.05).unwrap();
    assert!(Arc::ptr_eq(
        &game.sessions[&a].combat,
        &game.sessions[&b].combat
    ));
    assert_ne!(game.sessions[&a].state_dir, game.sessions[&b].state_dir);
    let peer: SocketAddr = "127.0.0.1:22001".parse().unwrap();
    assert!(game
        .handle_udp(
            SECTOR_PORT,
            peer,
            Datagram {
                opcode: 0x14,
                player: b,
                sequence: 2,
                body: [b.to_le_bytes().as_slice(), &[2]].concat()
            }
        )
        .is_err());
    let chars = game.characters(b).unwrap();
    assert_eq!(
        u32::from_be_bytes(chars.body[20..24].try_into().unwrap()),
        b & 0xffffff
    );
}

#[test]
fn combat_is_shared_and_group_jumpstart_and_cloak_apply_to_other_player() {
    let mut game = game();
    let a = FIRST_PLAYER_ID;
    let b = a + 1;
    login(&mut game, a, 22011, "alice");
    login(&mut game, b, 22012, "bob");
    game.sessions.get_mut(&a).unwrap().position = [0.; 3];
    game.sessions.get_mut(&b).unwrap().position = [50.; 3];
    lock_combat(&game.sessions[&a].combat).unwrap().actors[0].position = [100.; 3];
    lock_combat(&game.sessions[&a].combat).unwrap().actors[0]
        .definition
        .spawn = [100.; 3];
    let shield_before = game.sessions[&b].peer_snapshot().unwrap().shield;
    game.advance(0.1).unwrap();
    assert_eq!(
        game.sessions[&b].peer_snapshot().unwrap().shield,
        shield_before
    );
    land_npc_shots(&mut game);
    assert_eq!(
        game.sessions[&b].peer_snapshot().unwrap().shield,
        shield_before - 5.
    );
    activate(&mut game, a, MOB_ID, 106);
    advance(&mut game, 1.2);
    assert_eq!(
        lock_combat(&game.sessions[&b].combat).unwrap().actors[0].shield,
        75.
    );
    game.handle_event(
        a,
        ClientEvent::Group {
            action: 10,
            target: b,
        },
    )
    .unwrap();
    game.handle_event(
        b,
        ClientEvent::Group {
            action: 11,
            target: 0,
        },
    )
    .unwrap();
    let victim = game.sessions[&b].peer_snapshot().unwrap();
    let lethal_damage = (victim.hull + victim.shield) * 4.;
    let hull_capacity = crate::ship_progression::hull_capacity(
        victim.profile.class(),
        crate::ship_progression::SANDBOX_HULL_UPGRADE,
    )
    .unwrap();
    game.sessions
        .get_mut(&b)
        .unwrap()
        .receive_hit(0, lethal_damage)
        .unwrap();
    assert_eq!(game.sessions[&b].peer_snapshot().unwrap().hull, 0.);
    game.advance(0.1).unwrap();
    activate(&mut game, a, b, 65);
    game.advance(0.1).unwrap();
    assert!(game.jumpstart_offers.contains_key(&b));
    game.handle_event(
        b,
        ClientEvent::Group {
            action: 11,
            target: 0,
        },
    )
    .unwrap();
    advance(&mut game, 7.2);
    assert_eq!(
        game.sessions[&b].peer_snapshot().unwrap().hull,
        hull_capacity * 0.7
    );
    activate(&mut game, a, a, 51);
    advance(&mut game, 3.2);
    assert!(game.sessions[&a].peer_snapshot().unwrap().cloaked);
    assert!(game.sessions[&b].peer_snapshot().unwrap().cloaked);
    game.handle_event(
        b,
        ClientEvent::Group {
            action: 14,
            target: 0,
        },
    )
    .unwrap();
    game.advance(0.1).unwrap();
    assert!(!game.sessions[&b].peer_snapshot().unwrap().cloaked);
}

#[test]
fn groups_and_both_space_sessions_survive_restart_then_share_wormhole() {
    let mut game = game();
    let a = FIRST_PLAYER_ID;
    let b = a + 1;
    login(&mut game, a, 22021, "alice");
    login(&mut game, b, 22022, "bob");
    game.handle_event(
        a,
        ClientEvent::Group {
            action: 10,
            target: b,
        },
    )
    .unwrap();
    game.handle_event(
        b,
        ClientEvent::Group {
            action: 11,
            target: 0,
        },
    )
    .unwrap();
    game.advance(0.1).unwrap();
    game.save().unwrap();
    let mut replacement =
        GameState::new(game.initial_sector.clone(), game.bind, game.root.clone()).unwrap();
    assert!(replacement.groups.together(a, b));
    assert!(replacement.sessions[&a].logged_in && replacement.sessions[&b].logged_in);
    assert!(Arc::ptr_eq(
        &replacement.sessions[&a].combat,
        &replacement.sessions[&b].combat
    ));
    activate(&mut replacement, a, a, 67);
    advance(&mut replacement, 3.2);
    assert_eq!(replacement.sessions[&a].sector.id, 1910);
    assert_eq!(replacement.sessions[&b].sector.id, 1910);
    assert!(replacement.groups.together(a, b));
    let handoff_tick = replacement.sessions[&a].tick_ms();
    replacement.save().unwrap();
    let resumed = GameState::new(
        replacement.initial_sector.clone(),
        replacement.bind,
        replacement.root.clone(),
    )
    .unwrap();
    assert!(resumed.sessions[&a].connected() && !resumed.sessions[&a].logged_in);
    assert!(resumed.sessions[&a].tick_ms() >= handoff_tick);
    assert!(resumed.groups.together(a, b));
}

#[test]
fn formation_follows_rotated_leader_restores_and_detaches_on_manual_thrust() {
    let mut game = game();
    let a = FIRST_PLAYER_ID;
    let b = a + 1;
    login(&mut game, a, 22031, "alice");
    login(&mut game, b, 22032, "bob");
    game.groups.invite(a, b).unwrap();
    game.groups.accept(b).unwrap();
    let command = |action| ClientEvent::Formation { action, target: 0 };
    assert!(game.handle_event(b, command(5)).is_err());
    assert!(game.handle_event(b, command(7)).is_err());
    game.handle_event(a, command(5)).unwrap();
    game.sessions.get_mut(&b).unwrap().position = [10_000., 0., 0.];
    assert!(game.handle_event(b, command(7)).is_err());
    game.sessions.get_mut(&b).unwrap().position = [100., 0., 0.];
    game.handle_event(b, command(7)).unwrap();
    game.handle_input(a, 0x14, &[a.to_le_bytes().as_slice(), &[2]].concat())
        .unwrap();
    advance(&mut game, 1.);
    let leader = game.sessions[&a].peer_snapshot().unwrap();
    let follower = game.sessions[&b].peer_snapshot().unwrap();
    assert!(
        distance(
            follower.position,
            [
                leader.position[0],
                leader.position[1] - 200.,
                leader.position[2]
            ]
        ) < 0.01
    );
    assert_eq!(leader.motion.speed, follower.motion.speed);
    game.save().unwrap();
    let mut replacement =
        GameState::new(game.initial_sector.clone(), game.bind, game.root.clone()).unwrap();
    replacement.advance(0.1).unwrap();
    assert!(replacement.groups.group(b).unwrap().formed.contains(&b));
    replacement
        .handle_input(b, 0x14, &[b.to_le_bytes().as_slice(), &[4]].concat())
        .unwrap();
    replacement.advance(0.1).unwrap();
    assert!(!replacement.groups.group(b).unwrap().formed.contains(&b));
    replacement.handle_event(b, command(7)).unwrap();
    replacement.advance(0.1).unwrap();
    replacement.handle_event(a, command(9)).unwrap();
    replacement.advance(0.1).unwrap();
    assert_eq!(replacement.groups.group(a).unwrap().formation, 0);
}

#[test]
fn formed_group_keeps_relative_position_through_leader_warp() {
    let mut game = game();
    let leader = FIRST_PLAYER_ID;
    let follower = leader + 1;
    login(&mut game, leader, 22501, "alice");
    login(&mut game, follower, 22502, "bob");
    game.groups.invite(leader, follower).unwrap();
    game.groups.accept(follower).unwrap();
    game.handle_event(
        leader,
        ClientEvent::Formation {
            action: 5,
            target: 0,
        },
    )
    .unwrap();
    game.sessions.get_mut(&follower).unwrap().position = [100., 0., 0.];
    game.handle_event(
        follower,
        ClientEvent::Formation {
            action: 7,
            target: 0,
        },
    )
    .unwrap();
    advance(&mut game, 0.1);
    let route = [
        (leader & 0xffffff).to_le_bytes().as_slice(),
        &1u16.to_le_bytes(),
        &100000u32.to_le_bytes(),
    ]
    .concat();
    game.handle_input(leader, 0x9b, &route).unwrap();
    advance(&mut game, 5.);
    let lead = game.sessions[&leader].peer_snapshot().unwrap();
    let wing = game.sessions[&follower].peer_snapshot().unwrap();
    assert!(lead.position[0] > 6000.);
    assert!(game
        .groups
        .group(follower)
        .unwrap()
        .formed
        .contains(&follower));
    let offset = crate::groups::formation_offset(5, 1);
    let rotated = lead.motion.rotate(offset);
    assert!(
        distance(
            wing.position,
            std::array::from_fn(|axis| lead.position[axis] + rotated[axis])
        ) < 1.
    );
    game.handle_input(leader, 0x9b, &route).unwrap();
    advance(&mut game, 0.1);
    assert!(game
        .groups
        .group(follower)
        .unwrap()
        .formed
        .contains(&follower));
}

/// Formed pilots share native warp phases, the slowest drive, and recovery.
#[test]
fn formed_group_warp_survives_replacement_and_cancels_on_leader_loss() {
    let initial = game();
    let root = initial.root.clone();
    let sector = initial.initial_sector.clone();
    let bind = initial.bind;
    drop(initial);
    let inventory_path = root.join("inventory.json");
    let mut inventory: crate::inventory::Inventory =
        serde_json::from_slice(&fs::read(&inventory_path).unwrap()).unwrap();
    inventory.equipped[2].as_mut().unwrap().template = 2510; // Faster Waking Blue Tail.
    fs::write(&inventory_path, serde_json::to_vec(&inventory).unwrap()).unwrap();

    let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
    let leader = FIRST_PLAYER_ID;
    let follower = leader + 1;
    login(&mut game, leader, 22511, "alice");
    login(&mut game, follower, 22512, "bob");
    let group_speed = game.sessions[&follower].formation_warp_speed();
    assert!(game.sessions[&leader].formation_warp_speed() > group_speed);
    game.groups.invite(leader, follower).unwrap();
    game.groups.accept(follower).unwrap();
    game.handle_event(
        leader,
        ClientEvent::Formation {
            action: 5,
            target: 0,
        },
    )
    .unwrap();
    game.sessions.get_mut(&follower).unwrap().position = [100., 0., 0.];
    game.handle_event(
        follower,
        ClientEvent::Formation {
            action: 7,
            target: 0,
        },
    )
    .unwrap();
    advance(&mut game, 0.1);
    let route = [
        (leader & 0xffffff).to_le_bytes().as_slice(),
        &1u16.to_le_bytes(),
        &100000u32.to_le_bytes(),
    ]
    .concat();
    game.handle_input(leader, 0x9b, &route).unwrap();
    advance(&mut game, 0.1);
    assert_eq!(
        game.sessions[&leader].formation_warp().phase,
        WarpPhase::Charging
    );
    assert_eq!(
        game.sessions[&follower].formation_warp().phase,
        WarpPhase::Charging
    );
    assert_eq!(game.sessions[&leader].formation_warp().speed, group_speed);
    assert_eq!(game.sessions[&follower].formation_warp().speed, group_speed);
    advance(&mut game, 2.1);
    assert_eq!(
        game.sessions[&leader].formation_warp().phase,
        WarpPhase::Traveling
    );
    assert_eq!(
        game.sessions[&follower].formation_warp().phase,
        WarpPhase::Traveling
    );
    game.save().unwrap();
    drop(game);

    let mut replacement = GameState::new(sector, bind, root).unwrap();
    advance(&mut replacement, 0.5);
    assert_eq!(
        replacement.sessions[&leader].formation_warp().speed,
        group_speed
    );
    assert_eq!(
        replacement.sessions[&follower].formation_warp().phase,
        WarpPhase::Traveling
    );
    let lead = replacement.sessions[&leader].peer_snapshot().unwrap();
    let wing = replacement.sessions[&follower].peer_snapshot().unwrap();
    let offset = lead.motion.rotate(crate::groups::formation_offset(5, 1));
    assert!(
        distance(
            wing.position,
            std::array::from_fn(|i| lead.position[i] + offset[i])
        ) < 1.
    );
    replacement.handle_input(leader, 0x9b, &route).unwrap();
    advance(&mut replacement, 0.1);
    assert_eq!(
        replacement.sessions[&leader].formation_warp().phase,
        WarpPhase::Recovering
    );
    assert_eq!(
        replacement.sessions[&follower].formation_warp().phase,
        WarpPhase::Recovering
    );
    assert!(replacement
        .groups
        .group(follower)
        .unwrap()
        .formed
        .contains(&follower));
    advance(&mut replacement, 2.1);
    assert_eq!(
        replacement.sessions[&leader].formation_warp().phase,
        WarpPhase::Idle
    );
    assert_eq!(
        replacement.sessions[&follower].formation_warp().phase,
        WarpPhase::Idle
    );
    assert_eq!(
        replacement.sessions[&leader].formation_warp().speed,
        replacement.sessions[&leader].formation_warp_speed()
    );
    assert_eq!(
        replacement.sessions[&follower].formation_warp().speed,
        group_speed
    );

    replacement.handle_input(leader, 0x9b, &route).unwrap();
    advance(&mut replacement, 0.1);
    replacement.sessions.get_mut(&leader).unwrap().hull = 0.;
    advance(&mut replacement, 0.1);
    assert!(!replacement
        .groups
        .group(follower)
        .unwrap()
        .formed
        .contains(&follower));
    assert_eq!(
        replacement.sessions[&follower].formation_warp().phase,
        WarpPhase::Recovering
    );

    replacement.sessions.get_mut(&leader).unwrap().hull = 100.;
    advance(&mut replacement, 2.1);
    replacement
        .handle_event(
            follower,
            ClientEvent::Formation {
                action: 7,
                target: 0,
            },
        )
        .unwrap();
    assert!(replacement
        .groups
        .group(follower)
        .unwrap()
        .formed
        .contains(&follower));
    replacement.sessions.get_mut(&leader).unwrap().sector.id = 1071;
    advance(&mut replacement, 0.1);
    assert!(!replacement
        .groups
        .group(follower)
        .unwrap()
        .formed
        .contains(&follower));
}

#[test]
fn failed_weapon_storage_for_one_player_does_not_stop_another_players_tick() {
    let mut game = game();
    let a = FIRST_PLAYER_ID;
    let b = a + 1;
    login(&mut game, a, 22401, "alice");
    login(&mut game, b, 22402, "bob");
    game.sessions.get_mut(&a).unwrap().position = [0.; 3];
    game.sessions.get_mut(&b).unwrap().position = [0.; 3];
    lock_combat(&game.sessions[&a].combat).unwrap().actors[0].position = [100., 0., 0.];
    game.advance(0.).unwrap();
    game.handle_input(a, 0x17, &[a.to_le_bytes(), MOB_ID.to_le_bytes()].concat())
        .unwrap();
    game.handle_input(a, 0x5d, &[a.to_le_bytes().as_slice(), &[2, 3]].concat())
        .unwrap();
    game.handle_input(b, 0x14, &[b.to_le_bytes().as_slice(), &[2]].concat())
        .unwrap();
    let blocked = game.root.join("combat/1076.json.next");
    fs::create_dir(&blocked).unwrap();
    game.advance(0.1).unwrap();
    assert!(game.sessions[&b].position[0] > 0.);
    let alice: serde_json::Value =
        serde_json::from_slice(&fs::read(game.root.join("inventory.json")).unwrap()).unwrap();
    let bob: serde_json::Value =
        serde_json::from_slice(&fs::read(game.root.join("accounts/bob/inventory.json")).unwrap())
            .unwrap();
    assert!(!alice["pending_launch"].is_null());
    assert!(bob["pending_launch"].is_null());
    fs::remove_dir(blocked).unwrap();
    game.sessions
        .get_mut(&a)
        .unwrap()
        .recover_weapon_launch()
        .unwrap();
}

#[test]
fn multiplayer_hate_targets_the_attacker_and_dev_jump_clears_the_departed_identity() {
    let mut game = game();
    let a = FIRST_PLAYER_ID;
    let b = a + 1;
    login(&mut game, a, 22411, "alice");
    login(&mut game, b, 22412, "bob");
    game.sessions.get_mut(&a).unwrap().position = [0.; 3];
    game.sessions.get_mut(&b).unwrap().position = [400., 0., 0.];
    let source = Arc::clone(&game.sessions[&a].combat);
    {
        let mut world = lock_combat(&source).unwrap();
        let mob = &mut world.actors[0];
        mob.position = [500., 0., 0.];
        mob.definition.spawn = mob.position;
        mob.definition.aggressive = false;
        mob.damage_by(1., a);
    }
    let shield_a = game.sessions[&a].peer_snapshot().unwrap().shield;
    let shield_b = game.sessions[&b].peer_snapshot().unwrap().shield;
    game.advance(0.1).unwrap();
    assert_eq!(game.sessions[&a].peer_snapshot().unwrap().shield, shield_a);
    land_npc_shots(&mut game);
    assert_eq!(
        game.sessions[&a].peer_snapshot().unwrap().shield,
        shield_a - 5.
    );
    assert_eq!(game.sessions[&b].peer_snapshot().unwrap().shield, shield_b);
    game.handle_event(
        a,
        ClientEvent::Chat {
            channel: 0,
            text: "/d_jump Saturn".into(),
        },
    )
    .unwrap();
    game.advance(0.).unwrap();
    let query = [crate::sector_combat::PlayerThreat {
        id: a,
        session_epoch: 1,
        position: [0.; 3],
        visible: true,
        signature: 0.,
        stealth: None,
        factions: crate::factions::FactionReactions::neutral(),
    }];
    assert_eq!(
        lock_combat(&source).unwrap().actors[0].select_target(&query),
        None
    );
}
