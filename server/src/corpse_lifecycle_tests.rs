//! Receipt-driven corpse retirement; these fixtures use disposable world/inventory files only.
use super::*;
use crate::inventory::ItemInstance;
use crate::loot::{DropEntry, LootCatalog, MobDrops};
use crate::loot_world::{CorpseBirth, MobDeath};
use std::path::PathBuf;

/// Returns:
///     (PlayerSession, PlayerSession, u32, PathBuf): Two local viewers of a stocked, shared corpse.
fn viewers(drop_count: usize) -> (PlayerSession, PlayerSession, u32, PathBuf) {
    let mut collector = super::super::tests::session();
    let mut observer = super::super::tests::session();
    collector.logged_in = true;
    observer.logged_in = true;
    collector.position = [0.; 3];
    observer.position = [0.; 3];
    observer.account.character.game_id += 1;
    let catalog = LootCatalog::from_mobs(
        vec![MobDrops {
            id: 99,
            kind: Some(1),
            drops: [2837, 5477]
                .into_iter()
                .take(drop_count)
                .enumerate()
                .map(|(slot, item)| DropEntry {
                    source_id: slot as u32 + 1,
                    item,
                    chance_percent: 100.,
                    quantity: 1,
                    kind: 0,
                    usage_chance: 0,
                })
                .collect(),
        }],
        &collector.items,
    )
    .unwrap();
    let path = collector.state_dir.join("lifecycle-world.json");
    let mut world = LootWorld::load(path.clone(), collector.sector.id, &collector.items).unwrap();
    let id = world
        .spawn(
            CorpseBirth {
                death: MobDeath {
                    spawn: 90001,
                    generation: 1,
                },
                template: 99,
                name: "Shared salvage".into(),
                level: 1,
                killer: collector.player_id(),
                position: [0.; 3],
                now_ms: unix_ms(),
            },
            &catalog,
            &collector.items,
        )
        .unwrap()
        .unwrap();
    if drop_count == 2 {
        // Native drop rolls choose one equipment entry. Author a two-stack
        // persisted corpse explicitly so this test controls partial depletion.
        let mut saved: serde_json::Value =
            serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
        let mut slots: Vec<Option<ItemInstance>> = vec![None; crate::loot::LOOT_SLOTS];
        slots[0] = Some(ItemInstance::stock(2837, 1));
        slots[1] = Some(ItemInstance::stock(5477, 1));
        saved["corpses"][id.to_string()]["initial"]["items"] = serde_json::to_value(slots).unwrap();
        fs::write(&path, serde_json::to_vec(&saved).unwrap()).unwrap();
        world = LootWorld::load(path.clone(), collector.sector.id, &collector.items).unwrap();
    }
    let world = Arc::new(Mutex::new(world));
    collector.loot_world = Arc::clone(&world);
    observer.loot_world = world;
    for viewer in [&mut collector, &mut observer] {
        viewer.publish_corpses().unwrap();
        viewer.target = id;
        viewer.pending.clear();
    }
    (collector, observer, id, path)
}

/// Returns:
///     bool: This view published the native remove for this exact corpse, not its tractor article.
fn removed(viewer: &PlayerSession, corpse: u32) -> bool {
    viewer
        .pending
        .iter()
        .any(|frame| frame.opcode == 7 && frame.body == corpse.to_le_bytes())
}

/// Partial salvage remains visible; the last durable item clears both target windows immediately.
#[test]
fn final_loot_retires_all_targeted_views_and_cannot_respawn_after_receipt_recovery() {
    let (mut collector, mut observer, id, path) = viewers(2);
    collector.open_loot().unwrap();
    observer.open_loot().unwrap();
    collector.pending.clear();
    observer.pending.clear();
    collector.begin_looting(0).unwrap();
    collector.advance_looting(1.).unwrap();
    observer.publish_corpses().unwrap();
    let partial = {
        let world = lock_loot(&collector.loot_world).unwrap();
        let corpse = world.get(id).unwrap();
        let remaining = corpse.remaining().unwrap();
        assert!(remaining.items[0].is_none());
        assert!(remaining.items[1].is_some());
        packets::corpse_contents(corpse, &remaining, &collector.items)
    };
    for viewer in [&collector, &observer] {
        assert!(viewer.visible_corpses.contains_key(&id));
        assert_eq!(viewer.target, id);
        assert_eq!(viewer.loot_window, Some(id));
        assert!(!removed(viewer, id));
        assert!(viewer.pending.contains(&partial));
    }
    collector.pending.clear();
    observer.pending.clear();
    collector.last_loot_publish = Some(Instant::now());
    observer.last_loot_publish = Some(Instant::now());
    collector.begin_looting(1).unwrap();
    collector.advance_looting(1.).unwrap();
    observer.publish_corpses().unwrap();
    for viewer in [&collector, &observer] {
        assert!(!viewer.visible_corpses.contains_key(&id));
        assert_eq!(viewer.target, u32::MAX);
        assert!(viewer.loot_window.is_none());
        assert!(viewer.loot_revision.is_none());
        assert!(removed(viewer, id));
        assert!(viewer.pending.contains(&packets::resource_interface(false)));
        assert!(viewer
            .pending
            .contains(&packets::ship_target(viewer.player_id(), u32::MAX)));
        assert!(viewer.pending.contains(&packets::target_verbs(id, &[])));
    }
    let inventory: Inventory =
        serde_json::from_slice(&fs::read(collector.state_dir.join("inventory.json")).unwrap())
            .unwrap();
    // The world birth remains a tombstone. Receipts in the newer authoritative
    // inventory reconstruct depletion even if the process died before view removal.
    let mut restored = LootWorld::load(path, collector.sector.id, &collector.items).unwrap();
    restored
        .register_receipts(collector.player_id(), &inventory.loot_receipts)
        .unwrap();
    assert_eq!(restored.generation(90001), 1);
    assert!(!restored.get(id).unwrap().available(unix_ms()).unwrap());
    observer.loot_world = Arc::new(Mutex::new(restored));
    observer.last_loot_publish = None;
    observer.pending.clear();
    observer.publish_corpses().unwrap();
    assert!(observer.visible_corpses.is_empty());
    assert!(observer.pending.is_empty());
    let roots = [collector.state_dir.clone(), observer.state_dir.clone()];
    drop((collector, observer));
    for root in roots {
        if root.exists() {
            fs::remove_dir_all(root).unwrap();
        }
    }
}

/// An unselected viewer still removes the object during the scan throttle without losing its other target.
#[test]
fn passive_viewer_receives_final_remove_without_waiting_for_next_scan() {
    let (mut collector, mut observer, id, _) = viewers(1);
    observer.target = observer.player_id();
    observer.last_loot_publish = Some(Instant::now());
    observer.weapon_firing[3] = true;
    collector.open_loot().unwrap();
    collector.begin_looting(0).unwrap();
    collector.advance_looting(1.).unwrap();
    observer.publish_corpses().unwrap();
    assert!(removed(&observer, id));
    assert!(!observer.visible_corpses.contains_key(&id));
    assert_eq!(observer.target, observer.player_id());
    assert!(observer.weapon_firing[3]);
}

/// Rejected final inventory writes cannot hide remaining salvage from either participant.
#[test]
fn failed_last_loot_commit_retains_visible_stock_and_target() {
    let (mut collector, mut observer, id, _) = viewers(1);
    collector.open_loot().unwrap();
    collector.begin_looting(0).unwrap();
    collector.pending.clear();
    observer.pending.clear();
    let blocked = collector.state_dir.join("inventory.json.tmp");
    fs::create_dir(&blocked).unwrap();
    collector.advance_looting(1.).unwrap();
    observer.publish_corpses().unwrap();
    for viewer in [&collector, &observer] {
        assert!(viewer.visible_corpses.contains_key(&id));
        assert_eq!(viewer.target, id);
        assert!(!removed(viewer, id));
    }
    assert!(lock_loot(&collector.loot_world)
        .unwrap()
        .get(id)
        .unwrap()
        .available(unix_ms())
        .unwrap());
    fs::remove_dir(blocked).unwrap();
    collector.begin_looting(0).unwrap();
    collector.advance_looting(1.).unwrap();
    observer.publish_corpses().unwrap();
    assert!(removed(&collector, id));
    assert!(removed(&observer, id));
}

/// Imported credit-only wrecks close rather than reopening their interface when the last credits are taken.
#[test]
fn credit_only_final_open_closes_interface_and_retires_every_view() {
    let (mut collector, mut observer, id, path) = viewers(1);
    let mut persisted: serde_json::Value =
        serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
    persisted["corpses"][id.to_string()]["initial"]["items"] =
        serde_json::json!([null, null, null, null, null, null]);
    fs::write(&path, serde_json::to_vec(&persisted).unwrap()).unwrap();
    let world = LootWorld::load(path, collector.sector.id, &collector.items).unwrap();
    let credits = world.get(id).unwrap().initial.credits;
    assert!(credits > 0);
    let shared = Arc::new(Mutex::new(world));
    collector.loot_world = Arc::clone(&shared);
    observer.loot_world = shared;
    let before = collector.inventory.credits;
    collector.open_loot().unwrap();
    observer.publish_corpses().unwrap();
    assert_eq!(collector.inventory.credits, before + credits);
    for viewer in [&collector, &observer] {
        assert!(removed(viewer, id));
        assert_eq!(viewer.target, u32::MAX);
        assert!(viewer.pending.contains(&packets::resource_interface(false)));
        assert!(!viewer.pending.contains(&packets::resource_interface(true)));
    }
}
