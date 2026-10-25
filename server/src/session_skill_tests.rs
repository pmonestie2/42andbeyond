use super::*;
use crate::inventory::ItemInstance;

/// Return a disposable low-rank Jenquai pilot beside a second independent account.
///
/// Returns:
///     PlayerSession: Loaded authoritative session with one earned point.
fn training_pilot(root: &std::path::Path, name: &str, id: u32) -> PlayerSession {
    let state = root.join(name);
    std::fs::create_dir_all(&state).unwrap();
    let mut account = Account::create(name.into(), "pass", name.into()).unwrap();
    account.character.game_id = id;
    account.save_new(&state).unwrap();
    let items = ItemCatalog::load().unwrap();
    let mut inventory = Inventory::starter_for(&account.character, &items).unwrap();
    inventory.xp_start_level = 0;
    inventory.combat_xp = 500;
    inventory.trained_skills = Some(
        [(1, 1), (18, 1), (20, 1), (45, 1), (55, 1)]
            .into_iter()
            .collect(),
    );
    inventory.cargo[0] = Some(ItemInstance::stock(507, 2));
    std::fs::write(
        state.join("inventory.json"),
        serde_json::to_vec(&inventory).unwrap(),
    )
    .unwrap();
    let sector = SectorCatalog::load().unwrap().sector(1076).unwrap();
    let mut ship =
        PlayerSession::with_account(sector, Ipv4Addr::LOCALHOST, state, account, root.into())
            .unwrap();
    ship.logged_in = true;
    ship
}

/// Return the packed native request; its advertised points are deliberately untrusted.
///
/// Returns:
///     Vec<u8>: Ten-byte SkillAction body.
fn skill_action(owner: u32, advertised: u32, skill: u16) -> Vec<u8> {
    [
        owner.to_le_bytes().as_slice(),
        advertised.to_le_bytes().as_slice(),
        skill.to_le_bytes().as_slice(),
    ]
    .concat()
}

/// One native purchase updates stats/AUX and survives replacement without spending twice.
#[test]
fn skill_purchase_is_authoritative_durable_and_private_to_one_pilot() {
    let root = std::env::temp_dir().join(format!("beyond-skills-{}", rand::random::<u64>()));
    let mut je = training_pilot(&root, "Explorer", 0x40000001);
    let other = training_pilot(&root, "Wingmate", 0x40000002);
    let before_damage = je.equipment.weapons[0].damage;
    je.hull = 100.;
    assert_eq!(je.skill_catalog.level(1), 1);
    assert_eq!(crate::skills::available_points(&je.inventory).unwrap(), 1);
    let body = skill_action(je.player_id(), u32::MAX, 1);
    je.handle_input(0x57, &body).unwrap();
    assert_eq!(je.skill_catalog.level(1), 2);
    assert_eq!(je.inventory.spent_skill_points, 1);
    assert_eq!(crate::skills::available_points(&je.inventory).unwrap(), 0);
    assert!(je.equipment.weapons[0].damage > before_damage);
    assert_eq!(je.hull, 100.); // training cannot heal a damaged ship
    assert!(je.pending.iter().any(|frame| frame.opcode == 0x1b));
    je.pending.clear();
    je.handle_input(0x57, &body).unwrap();
    assert!(je.pending.iter().any(|frame| frame.opcode == 0x1d));
    assert!(je.pending.iter().any(|frame| frame.opcode == 0x1b));
    assert_eq!(je.inventory.spent_skill_points, 1);
    assert_eq!(other.skill_catalog.level(1), 1);
    assert_eq!(other.inventory.spent_skill_points, 0);

    let state = je.state_dir.clone();
    let account = je.account.clone();
    let sector = je.sector.clone();
    drop(je);
    let mut resumed =
        PlayerSession::with_account(sector, Ipv4Addr::LOCALHOST, state, account, root.clone())
            .unwrap();
    resumed.logged_in = true;
    assert_eq!(resumed.skill_catalog.level(1), 2);
    assert_eq!(resumed.inventory.spent_skill_points, 1);
    assert!(resumed.purchase_skill(&body).is_err());
    assert!(resumed
        .purchase_skill(&skill_action(other.player_id(), 0, 1))
        .is_err());
    assert!(resumed.purchase_skill(&body[..9]).is_err());
    drop(other);
    drop(resumed);
    std::fs::remove_dir_all(root).unwrap();
}

/// An archived mission skill award grants rank one without spending earned points.
#[test]
fn quest_skill_unlock_grants_first_rank_and_survives_replacement() {
    let root = std::env::temp_dir().join(format!("beyond-skill-quest-{}", rand::random::<u64>()));
    let mut je = training_pilot(&root, "QuestPilot", 0x40000001);
    assert_eq!(je.skill_catalog.level(41), 0);
    assert!(je
        .purchase_skill(&skill_action(je.player_id(), 1, 41))
        .is_err());
    let mut candidate = je.inventory.clone();
    candidate.mission_awarded_skills.insert(41);
    je.commit_inventory(candidate).unwrap();
    assert_eq!(je.skill_catalog.level(41), 1);
    assert_eq!(je.inventory.spent_skill_points, 0);
    let resumed = PlayerSession::with_account(
        je.sector.clone(),
        Ipv4Addr::LOCALHOST,
        je.state_dir.clone(),
        je.account.clone(),
        root.clone(),
    )
    .unwrap();
    assert_eq!(resumed.skill_catalog.level(41), 1);
    assert_eq!(resumed.inventory.spent_skill_points, 0);
    drop(je);
    drop(resumed);
    std::fs::remove_dir_all(root).unwrap();
}

/// Archived level gates and a failed durable replacement cannot grant a rank.
#[test]
fn skill_rank_rejects_level_and_failed_write_without_consuming_points() {
    let root = std::env::temp_dir().join(format!("beyond-skill-gate-{}", rand::random::<u64>()));
    let mut je = training_pilot(&root, "Pilot", 0x40000001);
    let first = skill_action(je.player_id(), 1, 1);
    je.handle_input(0x57, &first).unwrap();
    let mut earned = je.inventory.clone();
    earned.combat_xp = 1_500; // rank two -> three needs combat level six
    je.commit_inventory(earned).unwrap();
    assert!(je.purchase_skill(&first).is_err());
    assert_eq!(je.skill_catalog.level(1), 2);
    let mut earned = je.inventory.clone();
    earned.combat_xp = crate::experience::LEVEL_XP[..6].iter().sum();
    je.commit_inventory(earned).unwrap();
    let blocker = je.state_dir.join("inventory.json.tmp");
    std::fs::create_dir(&blocker).unwrap();
    assert!(je.purchase_skill(&first).is_err());
    assert_eq!(je.skill_catalog.level(1), 2);
    assert_eq!(je.inventory.spent_skill_points, 1);
    std::fs::remove_dir(&blocker).unwrap();
    je.handle_input(0x57, &first).unwrap();
    assert_eq!(je.skill_catalog.level(1), 3);
    assert_eq!(je.inventory.spent_skill_points, 3);
    drop(je);
    std::fs::remove_dir_all(root).unwrap();
}
