//! Progen Sentinel Call Forward respec transaction for a willing target.
//! The recipient equips a single-use Biostruct Sequence device and chooses
//! the skill. Acceptance and delivery are coordinated separately by the game.
use crate::account::CharacterProfile;
use crate::inventory::Inventory;
use crate::items::ItemCatalog;
use crate::protocol::invalid;
use crate::skills::SkillCatalog;
use std::io;

pub const CALL_FORWARD_SKILL: u32 = 11;
pub const FORBIDDEN_TECH_SKILLS: [u32; 3] = [20, 45, 55];

/// Returns:
///     u32: Skill points returned when trained ranks reset to free rank one.
pub fn refunded_points(rank: u32, first_rank_was_awarded: bool) -> io::Result<u32> {
    if !(2..=9).contains(&rank) {
        return Err(invalid("Call Forward requires a skill above rank one."));
    }
    let later = rank * (rank - 1) / 2;
    Ok(later + u32::from(!first_rank_was_awarded))
}

/// Returns:
///     (Inventory, u32): Validated respec and number of returned skill points.
/// Raises:
///     io::Error: Skill, resequencer, point balance, or remaining gear is invalid.
pub fn reset_skill(
    profile: &CharacterProfile,
    inventory: &Inventory,
    skill_id: u32,
    items: &ItemCatalog,
) -> io::Result<(Inventory, u32)> {
    if FORBIDDEN_TECH_SKILLS.contains(&skill_id) {
        return Err(invalid(
            "Engine, reactor and shield technology cannot be Called Forward.",
        ));
    }
    let catalog = SkillCatalog::for_inventory(profile, inventory)?;
    if !catalog.skills.iter().any(|skill| skill.id == skill_id) {
        return Err(invalid("This pilot does not own that skill."));
    }
    let trained = inventory
        .trained_skills
        .as_ref()
        .ok_or_else(|| invalid("Legacy max-rank sandbox training cannot be reset."))?;
    let rank = trained.get(&skill_id).copied().unwrap_or(0);
    let refund = refunded_points(rank, inventory.mission_awarded_skills.contains(&skill_id))?;
    let (slot, _) = inventory
        .equipped
        .iter()
        .enumerate()
        .find(|(_, item)| {
            item.as_ref().is_some_and(|item| {
                (5990..=5997).contains(&item.template)
                    && item.template - 5988 >= rank
                    && item.structure > 0.
            })
        })
        .ok_or_else(|| invalid("Equip a Biostruct Sequence device for that skill rank."))?;
    let mut candidate = inventory.clone();
    candidate.spent_skill_points = candidate
        .spent_skill_points
        .checked_sub(refund)
        .ok_or_else(|| invalid("Skill point history cannot fund this Call Forward."))?;
    candidate
        .trained_skills
        .as_mut()
        .expect("validated trained ranks")
        .insert(skill_id, 1);
    candidate.equipped[slot] = None; // Biostruct Sequence is single use.
    let remaining = SkillCatalog::for_inventory(profile, &candidate)?;
    candidate.validate_for_profile(profile, items, &remaining)?;
    Ok((candidate, refund))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn refunds_purchased_tiers_but_preserves_free_first_rank() {
        assert_eq!(refunded_points(3, true).unwrap(), 3);
        assert_eq!(refunded_points(3, false).unwrap(), 4);
        assert_eq!(refunded_points(9, true).unwrap(), 36);
        assert!(refunded_points(1, true).is_err());
    }

    #[test]
    fn one_biostruct_sequence_resets_only_the_named_skill() {
        let root = std::env::temp_dir().join(format!("enb-call-forward-{}", rand::random::<u64>()));
        let accounts = crate::accounts::AccountDirectory::new(&root);
        let profile = accounts
            .create_class(
                "sentinel".into(),
                "example",
                "Sentinel".into(),
                crate::classes::CharacterClass::parse("ps").unwrap(),
            )
            .unwrap()
            .account
            .character;
        let items = ItemCatalog::load().unwrap();
        let mut inventory = Inventory::native_starter_for(&profile, &items).unwrap();
        inventory.xp_start_level = 50;
        inventory.trade_xp = 3 * crate::experience::LEVEL_XP[50];
        inventory.mission_awarded_skills.insert(5); // Build Devices begins at free rank one.
        inventory.trained_skills.as_mut().unwrap().insert(5, 3);
        inventory.spent_skill_points = 3;
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(5991, 1));
        let (reset, refunded) = reset_skill(&profile, &inventory, 5, &items).unwrap();
        assert_eq!(refunded, 3);
        assert_eq!(reset.spent_skill_points, 0);
        assert_eq!(reset.trained_skills.as_ref().unwrap()[&5], 1);
        assert!(reset.equipped[9].is_none());
        assert!(reset_skill(&profile, &inventory, 20, &items).is_err());
        inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(5990, 1));
        assert!(reset_skill(&profile, &inventory, 5, &items).is_err());
        assert_eq!(inventory.spent_skill_points, 3);
        std::fs::remove_dir_all(root).unwrap();
    }
}
