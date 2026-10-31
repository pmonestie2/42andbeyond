//! Explicit offline cargo-only ammunition grants for every existing pilot.
//! Account ownership and launch journals remain authoritative; no login-time refills.
use crate::accounts::AccountDirectory;
use crate::inventory::{Inventory, ItemInstance};
use crate::items::ItemCatalog;
use crate::protocol::invalid;
use serde_json::Value;
use std::collections::BTreeMap;
use std::io::{self, Write};
use std::path::{Path, PathBuf};

const STACKS_PER_LAUNCHER: u32 = 5;

pub struct AmmoRefill {
    pub username: String,
    pub character: String,
    pub launchers: usize,
    pub rounds_added: u64,
    pub rounds_missing: u64,
    pub deferred_launch: bool,
}

struct RefillPlan {
    path: PathBuf,
    previous: Option<Vec<u8>>,
    candidate: Value,
    report: AmmoRefill,
}

/// Returns:
///     (Inventory, usize, u64, u64): Additive cargo candidate, launchers, rounds added and remaining shortfall.
/// Raises:
///     io::Error: An inventory or launcher has no valid compatible ammunition.
fn prepare(inventory: &Inventory, items: &ItemCatalog) -> io::Result<(Inventory, usize, u64, u64)> {
    inventory.validate(items)?;
    let mut desired: BTreeMap<u32, u64> = BTreeMap::new();
    let mut launchers = 0;
    for mount in 3..=8 {
        let Some(weapon) = inventory.equipped[mount].as_ref() else {
            continue;
        };
        let weapon = items.get(weapon.template)?;
        if !matches!(weapon.item_type, 15 | 16) {
            continue;
        }
        launchers += 1;
        let ammo = if let Some(loaded) = &inventory.ammunition[mount] {
            items.get(loaded.template)?
        } else {
            // Prefer already-owned ammunition when possible. Equal catalog ranks
            // use stable template IDs rather than HashMap iteration order.
            let owned = inventory
                .cargo
                .iter()
                .take(inventory.cargo_capacity)
                .filter_map(Option::as_ref)
                .filter_map(|item| items.get(item.template).ok())
                .filter(|ammo| {
                    ammo.item_type == 10
                        && ammo.ammo_type == weapon.ammo_type
                        && ammo.level <= weapon.level
                })
                .max_by_key(|ammo| (ammo.level, ammo.max_stack, std::cmp::Reverse(ammo.id)));
            owned
                .or_else(|| {
                    items
                        .iter()
                        .filter(|ammo| {
                            ammo.item_type == 10
                                && ammo.ammo_type == weapon.ammo_type
                                && ammo.level <= weapon.level
                        })
                        .max_by_key(|ammo| (ammo.level, ammo.max_stack, std::cmp::Reverse(ammo.id)))
                })
                .ok_or_else(|| invalid("Equipped launcher has no compatible catalog ammunition."))?
        };
        inventory.check_ammunition(mount, &ItemInstance::stock(ammo.id, 1), items)?;
        *desired.entry(ammo.id).or_default() +=
            u64::from(ammo.max_stack) * u64::from(STACKS_PER_LAUNCHER);
    }
    let mut candidate = inventory.clone();
    let mut added = 0u64;
    let mut missing = 0u64;
    for (template, target) in desired {
        let ammo = items.get(template)?;
        let stock = ItemInstance::stock(template, 1);
        let owned: u64 = candidate
            .cargo
            .iter()
            .take(candidate.cargo_capacity)
            .filter_map(Option::as_ref)
            .filter(|item| item.template == template)
            .map(|item| u64::from(item.quantity))
            .sum();
        let shortfall = target.saturating_sub(owned);
        let capacity: u64 = candidate
            .cargo
            .iter()
            .take(candidate.cargo_capacity)
            .map(|slot| match slot {
                None => u64::from(ammo.max_stack),
                Some(item) if item.stacks_with(&stock) => u64::from(ammo.max_stack - item.quantity),
                _ => 0,
            })
            .sum();
        let grant = shortfall.min(capacity).min(u64::from(u32::MAX));
        if grant > 0 {
            candidate = candidate.received_instance(
                &ItemInstance::stock(template, grant as u32),
                u32::MAX as usize,
                items,
            )?;
        }
        added += grant;
        missing += shortfall - grant;
    }
    candidate.validate(items)?;
    Ok((candidate, launchers, added, missing))
}

/// Returns:
///     Vec<AmmoRefill>: Every real account's cargo-only grant or remaining shortfall.
/// Raises:
///     io::Error: A live server owns the state lock, input changed, or durable publication fails.
pub fn refill_cargo_ammunition(state: &Path, dry_run: bool) -> io::Result<Vec<AmmoRefill>> {
    if !state.join("account.local.json").is_file() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            "No existing primary account in the state directory.",
        ));
    }
    let _lock = crate::server::lock_state_directory(state)?;
    let items = ItemCatalog::load()?;
    let mut plans = Vec::new();
    for stored in AccountDirectory::new(state).load()? {
        let path = stored.directory.join("inventory.json");
        let previous = match std::fs::read(&path) {
            Ok(bytes) => Some(bytes),
            Err(error) if error.kind() == io::ErrorKind::NotFound => None,
            Err(error) => return Err(error),
        };
        let mut json: Value = match &previous {
            Some(bytes) => serde_json::from_slice(bytes)?,
            None => {
                serde_json::to_value(Inventory::starter_for(&stored.account.character, &items)?)?
            }
        };
        let mut inventory: Inventory = serde_json::from_value(json.clone())?;
        inventory.migrate_cargo_for_profile(&stored.account.character)?;
        let deferred = inventory.pending_launch.is_some();
        let (candidate, launchers, added, missing) = prepare(&inventory, &items)?;
        if !deferred {
            json["cargo"] = serde_json::to_value(&candidate.cargo)?;
        }
        plans.push(RefillPlan {
            path,
            previous,
            candidate: json,
            report: AmmoRefill {
                username: stored.account.username,
                character: stored.account.character.name,
                launchers,
                rounds_added: if deferred { 0 } else { added },
                rounds_missing: missing + if deferred { added } else { 0 },
                deferred_launch: deferred,
            },
        });
    }
    if !dry_run {
        // Validate all source bytes again before publishing the first account.
        for plan in &plans {
            let current = match std::fs::read(&plan.path) {
                Ok(bytes) => Some(bytes),
                Err(error) if error.kind() == io::ErrorKind::NotFound => None,
                Err(error) => return Err(error),
            };
            if current != plan.previous {
                return Err(invalid(
                    "An account inventory changed while preparing ammunition; no grants applied.",
                ));
            }
        }
        for plan in &plans {
            if plan.report.rounds_added == 0 {
                continue;
            }
            let backup = plan
                .path
                .with_file_name("inventory.before-ammo-refill.json");
            if let Some(previous) = &plan.previous {
                match std::fs::OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(&backup)
                {
                    Ok(mut file) => {
                        if let Err(error) = file.write_all(previous).and_then(|_| file.sync_all()) {
                            drop(file);
                            let _ = std::fs::remove_file(&backup);
                            return Err(error);
                        }
                    }
                    Err(error) if error.kind() == io::ErrorKind::AlreadyExists => {}
                    Err(error) => return Err(error),
                }
            }
            crate::durable_json::replace(&plan.path, &plan.candidate)?;
        }
    }
    Ok(plans.into_iter().map(|plan| plan.report).collect())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::classes::CharacterClass;
    use crate::protocol::{Datagram, SECTOR_PORT};

    /// Returns:
    ///     Inventory: A legal TT launcher with no magazine ammunition and preserved custom cargo.
    fn armed(items: &ItemCatalog) -> Inventory {
        let mut inventory = Inventory::starter(items).unwrap();
        inventory.sandbox_grant = u32::MAX;
        inventory.equipped[3] = Some(ItemInstance::stock(2834, 1));
        inventory.ammunition[3] = Some(ItemInstance::stock(752, 1));
        inventory.cargo[0] = Some(ItemInstance::stock(2837, 1));
        inventory.cargo[0].as_mut().unwrap().builder_name = "Owned gear".into();
        inventory
    }

    /// Cargo top-ups preserve all owned equipment and magazines, count all mounts and remain idempotent.
    #[test]
    fn cargo_refill_uses_compatible_launcher_ammo_without_equipment_changes() {
        let items = ItemCatalog::load().unwrap();
        let mut original = armed(&items);
        original.equipped[4] = Some(ItemInstance::stock(2834, 1));
        let (candidate, launchers, added, missing) = prepare(&original, &items).unwrap();
        assert_eq!(launchers, 2);
        assert_eq!(added, u64::from(items.get(752).unwrap().max_stack) * 10);
        assert_eq!(missing, 0);
        assert_eq!(candidate.equipped, original.equipped);
        assert_eq!(candidate.ammunition, original.ammunition);
        assert_eq!(candidate.cargo[0], original.cargo[0]);
        let (_, _, second, missing) = prepare(&candidate, &items).unwrap();
        assert_eq!((second, missing), (0, 0));
        // The granted stacks can be loaded through the existing native drag path.
        let slot = candidate
            .cargo
            .iter()
            .position(|item| item.as_ref().is_some_and(|item| item.template == 752))
            .unwrap();
        let quantity = candidate.cargo[slot].as_ref().unwrap().quantity;
        let loaded = candidate
            .moved(
                crate::inventory::ItemTransfer {
                    source: 1,
                    source_slot: slot,
                    destination: 2,
                    destination_slot: 3,
                    quantity,
                },
                &items,
            )
            .unwrap();
        assert_eq!(loaded.equipped[3], candidate.equipped[3]);
        assert_eq!(loaded.ammunition[3].as_ref().unwrap().template, 752);
        assert_eq!(
            loaded.ammunition[3].as_ref().unwrap().quantity,
            items.get(752).unwrap().max_stack
        );
    }

    /// Full holds produce a shortfall rather than overwriting gear or bypassing stack limits.
    #[test]
    fn full_cargo_and_beam_only_ships_are_preserved() {
        let items = ItemCatalog::load().unwrap();
        let mut original = armed(&items);
        for slot in original.cargo.iter_mut().take(original.cargo_capacity) {
            *slot = Some(ItemInstance::stock(2837, 1));
        }
        let (candidate, launchers, added, missing) = prepare(&original, &items).unwrap();
        assert_eq!(candidate, original);
        assert_eq!(launchers, 1);
        assert_eq!(added, 0);
        assert!(missing > 0);
        original.equipped[3] = Some(ItemInstance::stock(769, 1));
        original.ammunition[3] = None;
        let (candidate, launchers, added, missing) = prepare(&original, &items).unwrap();
        assert_eq!(candidate, original);
        assert_eq!((launchers, added, missing), (0, 0, 0));
    }

    /// Offline grants cover all accounts while preserving unrelated JSON and refusing a live state lock.
    #[test]
    fn offline_refill_all_accounts_preserves_json_backup_and_state_ownership() {
        let root = std::env::temp_dir().join(format!("42-ammo-all-{}", rand::random::<u64>()));
        let accounts = AccountDirectory::new(&root);
        let items = ItemCatalog::load().unwrap();
        let mut originals = Vec::new();
        for name in ["first", "second"] {
            let stored = accounts
                .create_class(
                    name.into(),
                    "pass",
                    name.into(),
                    CharacterClass::parse("tt").unwrap(),
                )
                .unwrap();
            let mut json = serde_json::to_value(armed(&items)).unwrap();
            json["custom_unknown_field"] = serde_json::json!({"preserved":name});
            let path = stored.directory.join("inventory.json");
            let bytes = serde_json::to_vec(&json).unwrap();
            std::fs::write(&path, &bytes).unwrap();
            originals.push((path, bytes, json));
        }
        let lock = crate::server::lock_state_directory(&root).unwrap();
        assert!(refill_cargo_ammunition(&root, false).is_err());
        drop(lock);
        let preview = refill_cargo_ammunition(&root, true).unwrap();
        assert_eq!(preview.len(), 2);
        assert!(preview
            .iter()
            .all(|report| report.rounds_added > 0 && report.rounds_missing == 0));
        for (path, bytes, _) in &originals {
            assert_eq!(std::fs::read(path).unwrap(), *bytes);
        }
        let reports = refill_cargo_ammunition(&root, false).unwrap();
        assert!(reports.iter().all(|report| report.rounds_added > 0));
        for (path, bytes, mut json) in originals {
            let updated: Value = serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
            json["cargo"] = updated["cargo"].clone();
            assert_eq!(updated, json);
            assert_eq!(
                std::fs::read(path.with_file_name("inventory.before-ammo-refill.json")).unwrap(),
                bytes
            );
        }
        assert!(refill_cargo_ammunition(&root, false)
            .unwrap()
            .iter()
            .all(|report| report.rounds_added == 0));
        std::fs::remove_dir_all(root).unwrap();
    }

    /// A resumed transport keeps newer cargo grants and never restores an old inventory copy.
    #[test]
    fn offline_refill_survives_older_transport_checkpoint() {
        let root = std::env::temp_dir().join(format!("42-ammo-resume-{}", rand::random::<u64>()));
        let stored = AccountDirectory::new(&root)
            .create_class(
                "pilot".into(),
                "pass",
                "Pilot".into(),
                CharacterClass::parse("tt").unwrap(),
            )
            .unwrap();
        let items = ItemCatalog::load().unwrap();
        let mut original = Inventory::starter_for(&stored.account.character, &items).unwrap();
        original.sandbox_grant = u32::MAX;
        original.equipped[3] = Some(ItemInstance::stock(2834, 1));
        original.ammunition[3] = Some(ItemInstance::stock(752, 1));
        crate::durable_json::replace(&root.join("inventory.json"), &original).unwrap();
        let sector: crate::content::Sector =
            serde_json::from_str(include_str!("../data/sector.json")).unwrap();
        let bind = "127.0.0.1".parse().unwrap();
        let peer: std::net::SocketAddr = "127.0.0.1:24001".parse().unwrap();
        let mut session =
            crate::player_session::PlayerSession::new(sector.clone(), bind, root.clone()).unwrap();
        let owner = session.player_id();
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
        let ticket = session.issue_ticket("pilot", "pass", peer.ip()).unwrap();
        assert!(session.consume_ticket(&ticket, peer.ip()));
        let reply = session.select(0, peer.ip(), false).unwrap();
        session
            .handle_udp(
                3808,
                peer,
                Datagram {
                    opcode: 0x2008,
                    player: owner,
                    sequence: 0,
                    body: 1076u32.to_le_bytes().to_vec(),
                },
            )
            .unwrap();
        let mut body = vec![0; 137];
        body[..64].copy_from_slice(&reply.body[4..]);
        session
            .handle_udp(
                SECTOR_PORT,
                peer,
                Datagram {
                    opcode: 2,
                    player: owner,
                    sequence: 1,
                    body,
                },
            )
            .unwrap();
        session.handle_input(6, &[]).unwrap();
        session.checkpoint_session(true).unwrap();
        let checkpoint = std::fs::read(root.join("session.local.json")).unwrap();
        drop(session);
        assert!(refill_cargo_ammunition(&root, false).unwrap()[0].rounds_added > 0);
        assert_eq!(
            std::fs::read(root.join("session.local.json")).unwrap(),
            checkpoint
        );
        let expected: Inventory =
            serde_json::from_slice(&std::fs::read(root.join("inventory.json")).unwrap()).unwrap();
        let resumed =
            crate::player_session::PlayerSession::new(sector, bind, root.clone()).unwrap();
        assert!(resumed.logged_in);
        assert_eq!(resumed.inventory, expected);
        assert_eq!(resumed.inventory.equipped, original.equipped);
        assert_eq!(resumed.inventory.ammunition, original.ammunition);
        drop(resumed);
        std::fs::remove_dir_all(root).unwrap();
    }
}
