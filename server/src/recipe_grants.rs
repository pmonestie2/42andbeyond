//! Offline, additive recipe grants derived from each character's actual trained build skills.
use crate::accounts::AccountDirectory;
use crate::crafting::CraftRecipe;
use crate::inventory::Inventory;
use crate::items::ItemCatalog;
use crate::skills::SkillCatalog;
use serde_json::Value;
use std::collections::BTreeSet;
use std::io;
use std::path::{Path, PathBuf};

pub struct RecipeGrant {
    pub username: String,
    pub character: String,
    pub added: usize,
    pub known: usize,
}

struct GrantPlan {
    report: RecipeGrant,
    path: PathBuf,
    previous: Option<Vec<u8>>,
    candidate: Value,
}

/// Returns:
///     BTreeSet<u32>: Every valid recipe currently supported by the character's trained build ranks.
pub fn eligible_formulas(skills: &SkillCatalog, items: &ItemCatalog) -> BTreeSet<u32> {
    items
        .iter()
        .filter_map(|item| {
            CraftRecipe::resolve(item.id, items)
                .ok()
                .filter(|recipe| recipe.check_skill(skills, items).is_ok())
                .map(|recipe| recipe.product)
        })
        .collect()
}

/// Returns:
///     Vec<RecipeGrant>: Additions and resulting counts for every real account's character.
/// Raises:
///     io::Error: The server owns the state lock, an account is invalid, or a durable write fails.
pub fn grant_build_recipes(state: &Path, dry_run: bool) -> io::Result<Vec<RecipeGrant>> {
    if !state.join("account.local.json").is_file() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            "No existing primary account in the state directory.",
        ));
    }
    let _state_lock = crate::server::lock_state_directory(state)?;
    let items = ItemCatalog::load()?;
    let mut plans = Vec::new();
    // Validate and prepare every account before changing the first inventory.
    for stored in AccountDirectory::new(state).load()? {
        let path = stored.directory.join("inventory.json");
        let previous = match std::fs::read(&path) {
            Ok(bytes) => Some(bytes),
            Err(error) if error.kind() == io::ErrorKind::NotFound => None,
            Err(error) => return Err(error),
        };
        let mut candidate: Value = match &previous {
            Some(bytes) => serde_json::from_slice(bytes)?,
            None => {
                serde_json::to_value(Inventory::starter_for(&stored.account.character, &items)?)?
            }
        };
        let mut inventory: Inventory = serde_json::from_value(candidate.clone())?;
        inventory.migrate_cargo_for_profile(&stored.account.character)?;
        inventory.validate(&items)?;
        let skills = SkillCatalog::for_inventory(&stored.account.character, &inventory)?;
        let before = inventory.known_formulas.len();
        inventory
            .known_formulas
            .extend(eligible_formulas(&skills, &items));
        inventory.validate(&items)?;
        let report = RecipeGrant {
            username: stored.account.username,
            character: stored.account.character.name,
            added: inventory.known_formulas.len() - before,
            known: inventory.known_formulas.len(),
        };
        // Preserve cargo, credits, native training, pending jobs, and unknown future JSON fields byte-for-value.
        candidate["known_formulas"] = serde_json::to_value(&inventory.known_formulas)?;
        plans.push(GrantPlan {
            report,
            path,
            previous,
            candidate,
        });
    }
    if !dry_run {
        for plan in &plans {
            if plan.report.added == 0 {
                continue;
            }
            if let Some(previous) = &plan.previous {
                let backup = plan
                    .path
                    .with_file_name("inventory.before-recipe-grant.json");
                match std::fs::OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(&backup)
                {
                    Ok(mut file) => {
                        use std::io::Write;
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
            crate::durable_json::replace_staged(
                &plan.path,
                &plan.path.with_extension("json.tmp"),
                &plan.candidate,
            )?;
        }
    }
    Ok(plans.into_iter().map(|plan| plan.report).collect())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Offline grants retain arbitrary inventory fields, obey native ranks, and keep more than 500 formulas.
    #[test]
    fn recipe_grants_are_skill_scoped_large_durable_and_idempotent() {
        let root =
            std::env::temp_dir().join(format!("enb-recipe-grants-{}", rand::random::<u64>()));
        let accounts = AccountDirectory::new(&root);
        let veteran = accounts
            .create_class(
                "veteran".into(),
                "pass",
                "Veteran".into(),
                crate::classes::CharacterClass::parse("tt").unwrap(),
            )
            .unwrap();
        let native = accounts
            .create_class_progression(
                "native".into(),
                "pass",
                "Native".into(),
                crate::classes::CharacterClass::parse("tt").unwrap(),
            )
            .unwrap();
        let items = ItemCatalog::load().unwrap();
        let inventory = Inventory::starter_for(&veteran.account.character, &items).unwrap();
        let skills = SkillCatalog::for_inventory(&veteran.account.character, &inventory).unwrap();
        let expected = eligible_formulas(&skills, &items);
        assert!(expected.len() > 500);
        let mut original = serde_json::to_value(inventory).unwrap();
        original["future_field"] = serde_json::json!({"untouched": true});
        original["credits"] = serde_json::json!(123456);
        original["known_formulas"] = serde_json::json!([expected.first().unwrap()]);
        let veteran_path = veteran.directory.join("inventory.json");
        let bytes = serde_json::to_vec(&original).unwrap();
        std::fs::write(&veteran_path, &bytes).unwrap();
        let native_path = native.directory.join("inventory.json");
        let mut native_inventory: Inventory =
            serde_json::from_slice(&std::fs::read(&native_path).unwrap()).unwrap();
        native_inventory
            .trained_skills
            .as_mut()
            .unwrap()
            .insert(4, 1);
        std::fs::write(&native_path, serde_json::to_vec(&native_inventory).unwrap()).unwrap();
        let native_skills =
            SkillCatalog::for_inventory(&native.account.character, &native_inventory).unwrap();
        let native_expected = eligible_formulas(&native_skills, &items);
        assert!(!native_expected.is_empty());
        assert!(native_expected.len() < expected.len());

        let preview = grant_build_recipes(&root, true).unwrap();
        assert_eq!(preview.len(), 2);
        assert_eq!(std::fs::read(&veteran_path).unwrap(), bytes);
        {
            let _lock = crate::server::lock_state_directory(&root).unwrap();
            assert_eq!(
                grant_build_recipes(&root, false).err().unwrap().kind(),
                io::ErrorKind::AlreadyExists
            );
        }
        let reports = grant_build_recipes(&root, false).unwrap();
        assert_eq!(reports[0].added, expected.len() - 1);
        let granted_bytes = std::fs::read(&veteran_path).unwrap();
        let mut granted: Value = serde_json::from_slice(&granted_bytes).unwrap();
        assert_eq!(
            granted["known_formulas"],
            serde_json::to_value(&expected).unwrap()
        );
        let validated: Inventory = serde_json::from_value(granted.clone()).unwrap();
        validated.validate(&items).unwrap();
        granted["known_formulas"] = original["known_formulas"].clone();
        assert_eq!(granted, original);
        assert_eq!(
            std::fs::read(veteran.directory.join("inventory.before-recipe-grant.json")).unwrap(),
            bytes
        );
        let native_granted: Inventory =
            serde_json::from_slice(&std::fs::read(&native_path).unwrap()).unwrap();
        assert_eq!(native_granted.known_formulas, native_expected);
        assert_eq!(
            native_granted.trained_skills,
            native_inventory.trained_skills
        );
        assert!(grant_build_recipes(&root, false)
            .unwrap()
            .iter()
            .all(|report| report.added == 0));
        assert_eq!(std::fs::read(&veteran_path).unwrap(), granted_bytes);
        std::fs::remove_dir_all(root).unwrap();
    }
}
