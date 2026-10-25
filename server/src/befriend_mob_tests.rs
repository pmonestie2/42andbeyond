//! Befriend changes MOB acquisition and typed damage, not merely messages.
use super::*;
use crate::befriend::BefriendEffect;
use crate::classes::CharacterClass;
use crate::factions::FactionCatalog;
use std::collections::BTreeMap;

/// Returns:
///     Mob: Armed aggressive actor with repeatable target geometry.
fn actor() -> Mob {
    Mob::new(serde_json::from_str(
        r#"{"source_id":733001,"template_id":46,"name":"Befriend target","level":1,"asset":48,"spawn":[0,0,0],"respawn_seconds":20,"aggressive":true,"aggression":3}"#,
    ).unwrap())
}

/// Returns:
///     PlayerThreat: One visible pilot at a chosen distance from the MOB.
fn pilot(id: u32, distance: f32) -> PlayerThreat {
    PlayerThreat {
        id,
        session_epoch: 1,
        position: [distance, 0., 0.],
        visible: true,
        signature: 0.,
        stealth: None,
        factions: FactionCatalog::load()
            .unwrap()
            .reactions(CharacterClass::parse("tt").unwrap(), &BTreeMap::new())
            .unwrap(),
    }
}

/// Returns:
///     BefriendEffect: Current-generation receipt for the selected native tier.
fn effect(mob: &Mob, ability: u32, rank: u32) -> BefriendEffect {
    BefriendEffect {
        source: 0x4000_0001,
        source_epoch: 1,
        effect: 91,
        ability,
        trained_rank: rank,
        mob_generation: mob.death_generation,
        deadline_ms: crate::resources::unix_ms() + 20_000,
    }
}

#[test]
fn friendship_suppresses_caster_threat_and_attack_breaks_it() {
    let mut mob = actor();
    let caster = pilot(0x4000_0001, 100.);
    assert_eq!(mob.select_target(std::slice::from_ref(&caster)), Some(0));
    assert!(mob.apply_befriend(effect(&mob, 20, 1)));
    assert_eq!(mob.select_target(std::slice::from_ref(&caster)), None);
    assert!(mob.befriended(caster.id, caster.session_epoch));
    assert!(!mob.befriended(caster.id, 2));
    let saved: Mob = serde_json::from_slice(&serde_json::to_vec(&mob).unwrap()).unwrap();
    let mut restored = actor();
    restored.restore_from(saved).unwrap();
    assert!(restored.befriended(caster.id, 1));
    restored.damage_by(1., caster.id);
    assert!(!restored.befriended(caster.id, 1));
    assert_eq!(
        restored.select_target(std::slice::from_ref(&caster)),
        Some(0)
    );
}

#[test]
fn calm_limits_other_pilots_and_entrance_affects_psionic_damage() {
    let mut mob = actor();
    assert!(mob.apply_befriend(effect(&mob, 43, 5)));
    let other = pilot(0x4000_0002, 2_000.);
    assert_eq!(mob.calm_fraction(), 0.5);
    assert_eq!(mob.psionic_vulnerability(), 0.2);
    assert_eq!(mob.select_target(std::slice::from_ref(&other)), None);
    let near = pilot(other.id, 100.);
    assert_eq!(mob.select_target(std::slice::from_ref(&near)), Some(0));
    let adjusted = mob.damage_by_typed(10., near.id, 8);
    assert!((adjusted - 12.).abs() < 0.001);
    mob.befriend[0].deadline_ms = crate::resources::unix_ms() - 1;
    assert_eq!(mob.calm_fraction(), 1.);
    assert_eq!(mob.psionic_vulnerability(), 0.);
    mob.damage(1_000_000_000.);
    assert!(mob.befriend.is_empty());
}
