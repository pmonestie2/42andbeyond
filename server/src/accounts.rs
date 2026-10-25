//! Account files and durable character identities. The original primary account
//! stays at state/account.local.json; additional accounts get independent state
//! directories under state/accounts/<username>. Password hashes never go on wire.
use crate::account::{Account, FIRST_PLAYER_ID};
use crate::protocol::invalid;
use serde::Serialize;
use std::collections::HashSet;
use std::io;
use std::path::{Path, PathBuf};

pub struct StoredAccount {
    pub account: Account,
    pub directory: PathBuf,
}

pub struct AccountDirectory {
    root: PathBuf,
}

#[derive(Serialize)]
struct NativeStart {
    version: u32,
    sector: u32,
    position: [f32; 3],
    home_station: u32,
}

impl AccountDirectory {
    pub fn new(root: &Path) -> Self {
        Self {
            root: root.to_path_buf(),
        }
    }

    pub fn load(&self) -> io::Result<Vec<StoredAccount>> {
        let mut accounts = vec![StoredAccount {
            account: Account::load(&self.root)?,
            directory: self.root.clone(),
        }];
        let extra = self.root.join("accounts");
        if extra.exists() {
            let mut paths = std::fs::read_dir(extra)?
                .map(|entry| entry.map(|entry| entry.path()))
                .collect::<io::Result<Vec<_>>>()?;
            paths.sort();
            for path in paths {
                if path.is_dir() && path.join("account.local.json").is_file() {
                    accounts.push(StoredAccount {
                        account: Account::load(&path)?,
                        directory: path,
                    });
                }
            }
        }
        let mut ids = HashSet::new();
        let mut names = HashSet::new();
        let mut usernames = HashSet::new();
        for stored in &accounts {
            if !ids.insert(stored.account.character.game_id)
                || !names.insert(stored.account.character.name.to_ascii_lowercase())
                || !usernames.insert(stored.account.username.to_ascii_lowercase())
            {
                return Err(invalid("duplicate account/character name or game identity"));
            }
        }
        Ok(accounts)
    }

    pub fn create(
        &self,
        username: String,
        password: &str,
        character: String,
    ) -> io::Result<StoredAccount> {
        self.create_class(
            username,
            password,
            character,
            crate::classes::CharacterClass::parse("je")?,
        )
    }

    pub fn create_class(
        &self,
        username: String,
        password: &str,
        character: String,
        class: crate::classes::CharacterClass,
    ) -> io::Result<StoredAccount> {
        // Validate components before using the username as a path component.
        let mut account = Account::create(username, password, character)?;
        account.character.race = class.race();
        account.character.profession = class.profession();
        if !self.root.join("account.local.json").exists() {
            account.save_new(&self.root)?;
            return Ok(StoredAccount {
                account,
                directory: self.root.clone(),
            });
        }
        let existing = self.load()?;
        if existing.iter().any(|stored| {
            stored
                .account
                .username
                .eq_ignore_ascii_case(&account.username)
                || stored
                    .account
                    .character
                    .name
                    .eq_ignore_ascii_case(&account.character.name)
        }) {
            return Err(invalid("account or character name already exists"));
        }
        account.character.game_id = (FIRST_PLAYER_ID..=0x4000ffff)
            .find(|id| {
                existing
                    .iter()
                    .all(|stored| stored.account.character.game_id != *id)
            })
            .ok_or_else(|| invalid("no character identities remain"))?;
        let directory = self.root.join("accounts").join(&account.username);
        account.save_new(&directory)?;
        Ok(StoredAccount { account, directory })
    }

    /// Create an explicit level-zero pilot with durable trained-skill state.
    ///
    /// Returns:
    ///     StoredAccount: Account and isolated character directory.
    pub fn create_class_progression(
        &self,
        username: String,
        password: &str,
        character: String,
        class: crate::classes::CharacterClass,
    ) -> io::Result<StoredAccount> {
        let mut account = Account::create(username, password, character)?;
        account.character.race = class.race();
        account.character.profession = class.profession();
        let existing = self.load()?;
        if existing.iter().any(|stored| {
            stored
                .account
                .username
                .eq_ignore_ascii_case(&account.username)
                || stored
                    .account
                    .character
                    .name
                    .eq_ignore_ascii_case(&account.character.name)
        }) {
            return Err(invalid("account or character name already exists"));
        }
        account.character.game_id = (FIRST_PLAYER_ID..=0x4000ffff)
            .find(|id| {
                existing
                    .iter()
                    .all(|stored| stored.account.character.game_id != *id)
            })
            .ok_or_else(|| invalid("no character identities remain"))?;
        let items = crate::items::ItemCatalog::load()?;
        let inventory =
            crate::inventory::Inventory::native_starter_for(&account.character, &items)?;
        let sectors = crate::sectors::SectorCatalog::load()?;
        let archived_station = class.starting_station();
        let station = if sectors.sector(archived_station).is_ok() {
            archived_station
        } else {
            let fallback = class.starting_station_fallback().ok_or_else(|| {
                invalid("Native starting station is unavailable and has no hosted fallback.")
            })?;
            crate::server_log!(
                "Native {} start {} has no public station layout; using hosted station {}.",
                class.code(),
                archived_station,
                fallback
            );
            fallback
        };
        let sector = sectors.sector(station)?;
        if sector.station.is_none() || !sector.bounds.contains([0.; 3]) {
            return Err(invalid("Native starting station is unavailable."));
        }
        let location = NativeStart {
            version: 1,
            sector: station,
            position: [0.; 3],
            home_station: station,
        };
        let directory = self.root.join("accounts").join(&account.username);
        if directory.exists() {
            return Err(invalid("Account directory already exists."));
        }
        std::fs::create_dir_all(&self.root)?;
        let staging = self.root.join(format!(
            ".native-account-{}-{}",
            account.username,
            rand::random::<u64>()
        ));
        std::fs::create_dir(&staging)?;
        let prepared = (|| -> io::Result<()> {
            account.save_new(&staging)?;
            crate::durable_json::replace_staged(
                &staging.join("inventory.json"),
                &staging.join("inventory.json.tmp"),
                &inventory,
            )?;
            crate::durable_json::replace_staged(
                &staging.join("player.json"),
                &staging.join("player.json.tmp"),
                &location,
            )?;
            std::fs::create_dir_all(self.root.join("accounts"))?;
            std::fs::rename(&staging, &directory)?;
            Ok(())
        })();
        if prepared.is_err() {
            let _ = std::fs::remove_dir_all(&staging);
        }
        prepared?;
        Ok(StoredAccount { account, directory })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn accounts_have_distinct_durable_ids_and_separate_state() {
        let root = std::env::temp_dir().join(format!("enb-accounts-{}", rand::random::<u64>()));
        let directory = AccountDirectory::new(&root);
        let first = directory
            .create("alice".into(), "example", "Alice".into())
            .unwrap();
        let second = directory
            .create("bob".into(), "example", "Bob".into())
            .unwrap();
        assert_ne!(first.directory, second.directory);
        assert_eq!(first.account.character.game_id, FIRST_PLAYER_ID);
        assert_eq!(second.account.character.game_id, FIRST_PLAYER_ID + 1);
        assert_eq!(directory.load().unwrap().len(), 2);
        assert!(directory
            .create("bob".into(), "example", "Other".into())
            .is_err());
        std::fs::remove_dir_all(root).unwrap();
    }

    /// A new progression pilot stays low-rank while older sandbox gear remains intact.
    #[test]
    fn explicit_progression_creation_is_isolated_from_existing_account() {
        let root =
            std::env::temp_dir().join(format!("enb-native-skills-{}", rand::random::<u64>()));
        let directory = AccountDirectory::new(&root);
        let old = directory
            .create_class(
                "old".into(),
                "pass",
                "Veteran".into(),
                crate::classes::CharacterClass::parse("je").unwrap(),
            )
            .unwrap();
        let fresh = directory
            .create_class_progression(
                "new".into(),
                "pass",
                "Cadet".into(),
                crate::classes::CharacterClass::parse("je").unwrap(),
            )
            .unwrap();
        assert!(!old.directory.join("inventory.json").exists());
        let inventory: crate::inventory::Inventory =
            serde_json::from_slice(&std::fs::read(fresh.directory.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(inventory.xp_start_level, 0);
        assert_eq!(inventory.hull_upgrade, 0);
        assert_eq!(inventory.trained_skills.as_ref().unwrap().get(&1), Some(&1));
        assert_eq!(inventory.spent_skill_points, 0);
        assert_eq!(directory.load().unwrap().len(), 2);
        std::fs::remove_dir_all(root).unwrap();
    }

    /// Every class can start at its native hull and keep legal starter mounts.
    #[test]
    fn all_nine_classes_have_legal_explicit_progression_starters() {
        let items = crate::items::ItemCatalog::load().unwrap();
        for code in ["te", "tt", "ts", "jd", "js", "je", "pw", "pp", "ps"] {
            let class = crate::classes::CharacterClass::parse(code).unwrap();
            let mut profile = Account::create(code.into(), "pass", code.into())
                .unwrap()
                .character;
            profile.race = class.race();
            profile.profession = class.profession();
            let inventory =
                crate::inventory::Inventory::native_starter_for(&profile, &items).unwrap();
            let skills = crate::skills::SkillCatalog::for_inventory(&profile, &inventory).unwrap();
            assert_eq!(inventory.xp_start_level, 0);
            assert_eq!(inventory.hull_upgrade, 0);
            assert_eq!(skills.level(1), 1);
            inventory
                .validate_for_profile(&profile, &items, &skills)
                .unwrap();
        }
    }

    /// Native starts use archived stations or documented hosted same-race fallbacks.
    #[test]
    fn all_native_progression_accounts_start_in_hosted_class_stations() {
        let root = std::env::temp_dir().join(format!("enb-class-starts-{}", rand::random::<u64>()));
        let directory = AccountDirectory::new(&root);
        let initial = crate::sectors::SectorCatalog::load()
            .unwrap()
            .sector(1076)
            .unwrap();
        for code in ["te", "tt", "ts", "jd", "js", "je", "pw", "pp", "ps"] {
            let class = crate::classes::CharacterClass::parse(code).unwrap();
            let stored = directory
                .create_class_progression(
                    format!("cadet{code}"),
                    "pass",
                    format!("Cadet{code}"),
                    class,
                )
                .unwrap();
            let ship = crate::player_session::PlayerSession::new(
                initial.clone(),
                std::net::Ipv4Addr::LOCALHOST,
                stored.directory.clone(),
            )
            .unwrap();
            let expected = class
                .starting_station_fallback()
                .unwrap_or(class.starting_station());
            assert_eq!(ship.sector.id, expected);
            assert!(ship.sector.station.is_some());
            let picker = crate::packets::characters(
                &ship.sector,
                &stored.account.character,
                &ship.inventory,
            );
            assert_eq!(&picker.body[4..8], &expected.to_be_bytes());
            let saved: serde_json::Value = serde_json::from_slice(
                &std::fs::read(stored.directory.join("player.json")).unwrap(),
            )
            .unwrap();
            assert_eq!(saved["home_station"], expected);
        }
        assert_eq!(directory.load().unwrap().len(), 10); // local + nine cadets
        std::fs::remove_dir_all(root).unwrap();
    }

    /// A native station launch persists the parent sector across replacement.
    #[test]
    fn first_station_launch_survives_replacement_without_restarting_at_home() {
        let root = std::env::temp_dir().join(format!("enb-first-launch-{}", rand::random::<u64>()));
        let stored = AccountDirectory::new(&root)
            .create_class_progression(
                "scout".into(),
                "pass",
                "Scout".into(),
                crate::classes::CharacterClass::parse("ts").unwrap(),
            )
            .unwrap();
        let initial = crate::sectors::SectorCatalog::load()
            .unwrap()
            .sector(1076)
            .unwrap();
        let mut ship = crate::player_session::PlayerSession::new(
            initial.clone(),
            std::net::Ipv4Addr::LOCALHOST,
            stored.directory.clone(),
        )
        .unwrap();
        assert_eq!(ship.sector.id, 10201);
        ship.logged_in = true;
        let station = ship.sector.station.as_ref().unwrap().clone();
        let action = [
            ship.player_id().to_le_bytes().as_slice(),
            station.id.to_le_bytes().as_slice(),
            &[1],
        ]
        .concat();
        ship.handle_input(0x4e, &action).unwrap();
        assert_eq!(ship.sector.id, station.parent);
        drop(ship);
        let restored = crate::player_session::PlayerSession::new(
            initial,
            std::net::Ipv4Addr::LOCALHOST,
            stored.directory,
        )
        .unwrap();
        assert_eq!(restored.sector.id, station.parent);
        drop(restored);
        std::fs::remove_dir_all(root).unwrap();
    }
}
