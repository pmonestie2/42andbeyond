//! Explicitly published nine-class demo pilots. Only this marker is advertised
//! before authentication; ordinary local accounts stay private.
use crate::account::CharacterProfile;
use crate::accounts::AccountDirectory;
use crate::classes::CharacterClass;
use crate::equipment_stats::EquipmentStats;
use crate::inventory::{Inventory, ItemInstance};
use crate::items::ItemCatalog;
use crate::protocol::invalid;
use crate::sandbox_loadout::SandboxLoadout;
use crate::skills::SkillCatalog;
use serde::{Deserialize, Serialize};
use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};

pub const CODES: [&str; 9] = ["te", "tt", "ts", "jd", "js", "je", "pw", "pp", "ps"];
const ROSTER: &str = "demo_roster.local.json";
const TURBO_WARRIOR: u32 = 2363;
const TURBO_JENQUAI_EXPLORER: u32 = 2412;
const TURBO_GENERAL: u32 = 2265;

#[derive(Serialize, Deserialize)]
struct DemoRoster {
    version: u32,
    codes: Vec<String>,
}

/// Returns:
///     PathBuf: Durable public roster marker in the chosen state directory.
fn roster_path(root: &Path) -> PathBuf {
    root.join(ROSTER)
}

/// Returns:
///     Inventory: Class legal max sandbox gear and ammunition for a new pilot.
fn legacy_inventory(profile: &CharacterProfile, items: &ItemCatalog) -> io::Result<Inventory> {
    let skills = SkillCatalog::for_profile(profile)?;
    let grant: SandboxLoadout = serde_json::from_str(include_str!("../data/sandbox_loadout.json"))?;
    let starter = Inventory::starter_for(profile, items)?;
    let mut inventory = grant
        .apply(&starter, profile, items, &skills)?
        .ok_or_else(|| invalid("Demo loadout did not apply to a fresh pilot."))?;
    let class = profile.class();
    let launcher_and_ammo = match class.race() {
        0 => Some((2834, 752)), // Level-nine Prototype N missiles.
        2 => Some((3178, 751)), // Level-nine Prototype Z projectiles.
        _ => None,              // Jenquai's level-nine beams consume no ammo.
    };
    if let Some((launcher_id, ammo_id)) = launcher_and_ammo {
        let launcher = items.get(launcher_id)?;
        let ammunition = items.get(ammo_id)?;
        launcher.check_requirements(profile, &skills, [50, 50, 50])?;
        if launcher.level != 9
            || ammunition.level != 9
            || launcher.ammo_type == 0
            || launcher.ammo_type != ammunition.ammo_type
            || ammunition.item_type != 10
        {
            return Err(invalid("Demo launcher and ammunition catalog mismatch."));
        }
        for slot in 3..3 + class.weapons() {
            class.check_mount(slot, launcher.item_type)?;
            if let Some(old) = inventory.equipped[slot].replace(ItemInstance::stock(launcher_id, 1))
            {
                let vault_slot = inventory
                    .vault
                    .iter_mut()
                    .find(|value| value.is_none())
                    .ok_or_else(|| invalid("No demo vault space for replaced weapon."))?;
                *vault_slot = Some(old);
            }
            inventory.ammunition[slot] = Some(ItemInstance::stock(ammo_id, ammunition.max_stack));
        }
        for cargo in inventory
            .cargo
            .iter_mut()
            .filter(|item| item.is_none())
            .take(2)
        {
            *cargo = Some(ItemInstance::stock(ammo_id, ammunition.max_stack));
        }
    }
    // A later global sandbox revision must not replace these curated demo
    // loadouts or refill spent credits on login.
    inventory.sandbox_grant = u32::MAX;
    inventory.validate_for_profile(profile, items, &skills)?;
    let stats = EquipmentStats::derive(&inventory, items, &skills)?;
    if stats.weapons.len() != class.weapons()
        || stats.ship.device_mounts != class.devices()
        || stats.weapons.iter().any(|weapon| weapon.damage <= 0.)
    {
        return Err(invalid("Incomplete nine-class demo equipment."));
    }
    Ok(inventory)
}

/// Returns:
///     u32: Class-legal public Turbo Weapons device template.
fn turbo_template(profile: &CharacterProfile, items: &ItemCatalog) -> io::Result<u32> {
    let skills = SkillCatalog::for_profile(profile)?;
    let id = match profile.class().0 {
        0 | 3 | 6 => TURBO_WARRIOR,
        5 => TURBO_JENQUAI_EXPLORER,
        _ => TURBO_GENERAL,
    };
    let template = items.get(id)?;
    if template.item_type != 11
        || template
            .activated
            .iter()
            .all(|effect| effect.description != "Turbo Weapons (Activated)")
    {
        return Err(invalid("Missing public turbo device effect."));
    }
    template.check_requirements(profile, &skills, [50, 50, 50])?;
    Ok(id)
}

/// Equip a turbo and keep the replaced stock device in the vault.
///
/// Returns:
///     Inventory: A class-legal revised demo loadout.
fn expected_inventory(profile: &CharacterProfile, items: &ItemCatalog) -> io::Result<Inventory> {
    let mut inventory = legacy_inventory(profile, items)?;
    let slot = 9 + profile.class().devices() - 1;
    let old =
        inventory.equipped[slot].replace(ItemInstance::stock(turbo_template(profile, items)?, 1));
    if let Some(old) = old {
        let vault_slot = inventory
            .vault
            .iter_mut()
            .find(|value| value.is_none())
            .ok_or_else(|| invalid("No demo vault space for replaced device."))?;
        *vault_slot = Some(old);
    }
    let skills = SkillCatalog::for_profile(profile)?;
    inventory.validate_for_profile(profile, items, &skills)?;
    Ok(inventory)
}

/// Preserve owned state while upgrading a published pilot's unchanged stock mount.
///
/// Returns:
///     Option<Inventory>: Updated inventory, or none when already equipped.
///
/// Raises:
///     io::Error: Every device mount has earned gear or no vault space remains.
fn turbo_upgrade(
    current: &Inventory,
    profile: &CharacterProfile,
    items: &ItemCatalog,
) -> io::Result<Option<Inventory>> {
    let class = profile.class();
    let turbo_id = turbo_template(profile, items)?;
    let mut mounts = 9..9 + class.devices();
    if mounts.clone().any(|slot| {
        current.equipped[slot]
            .as_ref()
            .is_some_and(|item| item.template == turbo_id && item.structure > 0.)
    }) {
        return Ok(None);
    }
    let baseline = legacy_inventory(profile, items)?;
    let preferred = 9 + class.devices() - 1;
    let slot = if current.equipped[preferred] == baseline.equipped[preferred] {
        preferred
    } else {
        mounts
            .find(|&slot| current.equipped[slot].is_none())
            .ok_or_else(|| invalid("No unchanged or empty demo device mount for turbo."))?
    };
    let mut candidate = current.clone();
    let old = candidate.equipped[slot].replace(ItemInstance::stock(turbo_id, 1));
    if let Some(old) = old {
        let vault_slot = candidate
            .vault
            .iter_mut()
            .find(|value| value.is_none())
            .ok_or_else(|| invalid("No demo vault space for replaced device."))?;
        *vault_slot = Some(old);
    }
    let skills = SkillCatalog::for_profile(profile)?;
    candidate.validate_for_profile(profile, items, &skills)?;
    Ok(Some(candidate))
}

/// Persist a new state file or resume a matching interrupted temp write.
fn save_new_bytes(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let temporary = path.with_extension("demo.next");
    if temporary.exists() {
        if fs::read(&temporary)? != bytes {
            return Err(invalid("Conflicting interrupted demo state write."));
        }
    } else {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)?;
        file.write_all(bytes)?;
        file.sync_all()?;
    }
    if path.exists() {
        fs::remove_file(&temporary)?;
        return Err(invalid("Demo state appeared during provisioning."));
    }
    fs::rename(temporary, path)
}

/// Create missing demo pilots, preserving all existing owned account state.
///
/// Returns:
///     Vec<String>: Published two-letter class codes in canonical order.
///
/// Raises:
///     io::Error: A conflicting account or preexisting inventory needs review.
pub fn seed(root: &Path) -> io::Result<Vec<String>> {
    let items = ItemCatalog::load()?;
    let accounts = AccountDirectory::new(root);
    let published = roster_path(root).exists();
    if published {
        published_codes(root)?;
    }
    // Validate every class loadout before the first account file is created.
    let planned = CODES
        .iter()
        .map(|code| {
            let class = CharacterClass::parse(code)?;
            let profile = CharacterProfile {
                game_id: 0,
                name: format!("Play{}", code.to_ascii_uppercase()),
                race: class.race(),
                profession: class.profession(),
            };
            Ok((
                *code,
                profile.clone(),
                expected_inventory(&profile, &items)?,
            ))
        })
        .collect::<io::Result<Vec<_>>>()?;
    for (code, profile, inventory) in planned {
        let username = format!("p{code}");
        let existing = accounts
            .load()?
            .into_iter()
            .find(|entry| entry.account.username == username);
        let directory = if let Some(stored) = existing {
            if stored.account.character.class() != profile.class()
                || stored.account.character.name != profile.name
                || !stored.account.verify(&username, &username)
            {
                return Err(invalid(
                    "Existing demo account has conflicting credentials or class.",
                ));
            }
            stored.directory
        } else {
            accounts
                .create_class(
                    username.clone(),
                    &username,
                    profile.name.clone(),
                    profile.class(),
                )?
                .directory
        };
        let path = directory.join("inventory.json");
        if path.exists() {
            let existing: Inventory = serde_json::from_slice(&fs::read(&path)?)?;
            if published {
                if let Some(candidate) = turbo_upgrade(&existing, &profile, &items)? {
                    crate::durable_json::replace(&path, &candidate)?;
                }
            } else {
                if existing != inventory {
                    return Err(invalid(
                        "Unpublished demo account has preexisting player state.",
                    ));
                }
            }
        } else {
            save_new_bytes(&path, &serde_json::to_vec_pretty(&inventory)?)?;
        }
    }
    if !published {
        let roster = DemoRoster {
            version: 1,
            codes: CODES.iter().map(|code| (*code).to_owned()).collect(),
        };
        let path = roster_path(root);
        save_new_bytes(&path, &serde_json::to_vec_pretty(&roster)?)?;
    }
    published_codes(root)
}

/// Read only explicitly published, present demo accounts for the login menu.
///
/// Returns:
///     Vec<String>: Two-letter class codes safe to advertise before login.
pub fn published_codes(root: &Path) -> io::Result<Vec<String>> {
    let path = roster_path(root);
    if !path.exists() {
        return Ok(Vec::new());
    }
    let marker: DemoRoster = serde_json::from_slice(&fs::read(path)?)?;
    if marker.version != 1
        || marker.codes
            != CODES
                .iter()
                .map(|code| (*code).to_owned())
                .collect::<Vec<_>>()
    {
        return Err(invalid("Invalid published demo roster."));
    }
    let accounts = AccountDirectory::new(root).load()?;
    let mut result = Vec::new();
    for code in CODES {
        if !marker.codes.iter().any(|entry| entry == code) {
            continue;
        }
        let username = format!("p{code}");
        let class = CharacterClass::parse(code)?;
        if accounts.iter().any(|stored| {
            stored.account.username == username
                && stored.account.character.class() == class
                && stored.directory.join("inventory.json").is_file()
        }) {
            result.push(code.to_owned());
        }
    }
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::accounts::AccountDirectory;

    #[test]
    /// Verify seeding publishes every class and leaves later owned edits intact.
    fn nine_demo_pilots_are_maxed_published_and_reruns_preserve_owned_state() {
        let root = std::env::temp_dir().join(format!("beyond-demo-{}", rand::random::<u64>()));
        let accounts = AccountDirectory::new(&root);
        accounts
            .create("private".into(), "private-secret", "Private".into())
            .unwrap();
        let private = fs::read(root.join("account.local.json")).unwrap();
        assert!(published_codes(&root).unwrap().is_empty());
        assert_eq!(seed(&root).unwrap(), CODES.map(str::to_owned));
        assert_eq!(published_codes(&root).unwrap(), CODES.map(str::to_owned));
        let items = ItemCatalog::load().unwrap();
        for code in CODES {
            let username = format!("p{code}");
            let stored = accounts
                .load()
                .unwrap()
                .into_iter()
                .find(|entry| entry.account.username == username)
                .unwrap();
            assert!(stored.account.verify(&username, &username));
            assert_eq!(
                stored.account.character.class(),
                CharacterClass::parse(code).unwrap()
            );
            assert_eq!(stored.account.character.total_level(), 150);
            let inventory: Inventory =
                serde_json::from_slice(&fs::read(stored.directory.join("inventory.json")).unwrap())
                    .unwrap();
            let skills = SkillCatalog::for_profile(&stored.account.character).unwrap();
            let stats = EquipmentStats::derive(&inventory, &items, &skills).unwrap();
            assert_eq!(
                stats.weapons.len(),
                stored.account.character.class().weapons()
            );
            assert_eq!(inventory.credits, 100_000_000);
            assert_eq!(inventory.sandbox_grant, u32::MAX);
            assert!(inventory.equipped[0].is_some());
            assert!(inventory.equipped[1].is_some());
            assert!(inventory.equipped[2].is_some());
            assert_eq!(
                inventory.equipped[9..].iter().flatten().count(),
                stored.account.character.class().devices()
            );
            let turbo = turbo_template(&stored.account.character, &items).unwrap();
            assert!(inventory.equipped[9..]
                .iter()
                .flatten()
                .any(|item| item.template == turbo));
            if stored.account.character.race != 1 {
                assert!(stats.weapons.iter().all(|weapon| weapon.rounds > 0));
                assert!(inventory.ammunition[3..3 + stats.weapons.len()]
                    .iter()
                    .all(Option::is_some));
                assert_eq!(
                    inventory
                        .cargo
                        .iter()
                        .flatten()
                        .filter(|item| items.get(item.template).unwrap().item_type == 10)
                        .count(),
                    2
                );
            }
        }
        let te_inventory = root.join("accounts/pte/inventory.json");
        let te_profile = accounts
            .load()
            .unwrap()
            .into_iter()
            .find(|entry| entry.account.username == "pte")
            .unwrap()
            .account
            .character;
        let mut spent = legacy_inventory(&te_profile, &items).unwrap();
        spent.credits = 123;
        spent.explore_xp = 789;
        spent.cargo[5] = Some(ItemInstance::stock(2837, 3));
        fs::write(&te_inventory, serde_json::to_vec(&spent).unwrap()).unwrap();
        assert_eq!(seed(&root).unwrap(), CODES.map(str::to_owned));
        let after: Inventory = serde_json::from_slice(&fs::read(&te_inventory).unwrap()).unwrap();
        let turbo = turbo_template(&te_profile, &items).unwrap();
        let slot = 9 + te_profile.class().devices() - 1;
        assert_eq!(after.equipped[slot].as_ref().unwrap().template, turbo);
        assert!(after
            .vault
            .iter()
            .flatten()
            .any(|item| Some(item) == spent.equipped[slot].as_ref()));
        assert_eq!(after.credits, spent.credits);
        assert_eq!(after.explore_xp, spent.explore_xp);
        assert_eq!(after.cargo, spent.cargo);
        for mounted in 0..20 {
            if mounted != slot {
                assert_eq!(after.equipped[mounted], spent.equipped[mounted]);
            }
        }
        assert_eq!(seed(&root).unwrap(), CODES.map(str::to_owned));
        let repeated: Inventory =
            serde_json::from_slice(&fs::read(&te_inventory).unwrap()).unwrap();
        assert_eq!(repeated, after);
        let tt_inventory = root.join("accounts/ptt/inventory.json");
        let tt_profile = accounts
            .load()
            .unwrap()
            .into_iter()
            .find(|entry| entry.account.username == "ptt")
            .unwrap()
            .account
            .character;
        let mut edited = legacy_inventory(&tt_profile, &items).unwrap();
        edited.equipped.swap(9, 13);
        let edited_bytes = serde_json::to_vec(&edited).unwrap();
        fs::write(&tt_inventory, &edited_bytes).unwrap();
        assert!(seed(&root).is_err());
        assert_eq!(fs::read(&tt_inventory).unwrap(), edited_bytes);
        assert_eq!(fs::read(root.join("account.local.json")).unwrap(), private);
        assert_eq!(accounts.load().unwrap().len(), 10);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    /// A colliding account cannot acquire public demo credentials by seeding.
    fn conflicting_existing_demo_account_is_not_overwritten_or_advertised() {
        let root =
            std::env::temp_dir().join(format!("beyond-demo-conflict-{}", rand::random::<u64>()));
        let accounts = AccountDirectory::new(&root);
        accounts
            .create_class(
                "pte".into(),
                "not-the-demo-password",
                "PlayTE".into(),
                CharacterClass::parse("te").unwrap(),
            )
            .unwrap();
        let before = fs::read(root.join("account.local.json")).unwrap();
        assert!(seed(&root).is_err());
        assert_eq!(fs::read(root.join("account.local.json")).unwrap(), before);
        assert!(!roster_path(&root).exists());
        fs::remove_dir_all(root).unwrap();
    }
}
