//! Organic debuffs alter actual launch cadence and warning admission.
use super::*;
use crate::biorepression::BioEffect;

/// Returns:
///     Mob: One organic actor with deterministic weapon output.
fn actor() -> Mob {
    let mut definition: MobDefinition = serde_json::from_str(
        r#"{"source_id":940001,"template_id":46,"name":"Organic target","level":1,"asset":48,"spawn":[500,0,0],"respawn_seconds":20,"aggressive":true,"mob_type":2}"#,
    ).unwrap();
    definition.combat_override = Some(MobStats {
        hull: 100.,
        shield: 0.,
        dps: 40.,
        reload: 1.,
        range: 1_000.,
        effect: 35,
        damage_type: 3,
        variance: false,
    });
    Mob::new(definition)
}

/// Returns:
///     BioEffect: Generation-pinned current-life effect for the native tier.
fn effect(mob: &Mob, ability: u32, rank: u32) -> BioEffect {
    BioEffect {
        source: 0x4000_0001,
        source_epoch: 7,
        effect: 91,
        ability,
        trained_rank: rank,
        mob_generation: mob.death_generation,
        weapon_slot: (ability == 21).then_some(0),
        deadline_ms: crate::resources::unix_ms() + 15_000,
    }
}

#[test]
fn slow_changes_authoritative_reload_and_survives_restart() {
    let mut mob = actor();
    assert!(mob.apply_bio(effect(&mob, 24, 3)));
    assert!(!mob.apply_bio(effect(&mob, 24, 3)));
    assert_eq!(mob.bio_reload_factor(), 1.5);
    assert_eq!(mob.bio_skill_use_fraction(), 0.6);
    assert!(mob.tick(0.1, [100., 0., 0.]) > 0.);
    assert!((mob.shot_cooldown() - 1.5).abs() < 0.001);
    let saved: Mob = serde_json::from_slice(&serde_json::to_vec(&mob).unwrap()).unwrap();
    let mut restored = actor();
    restored.restore_from(saved).unwrap();
    assert_eq!(restored.bio_reload_factor(), 1.5);
    assert!(restored.shot_cooldown() > 0.);
    restored.bio[0].deadline_ms = crate::resources::unix_ms() - 1;
    assert_eq!(restored.bio_reload_factor(), 1.);
    restored.damage(1_000_000_000.);
    assert!(restored.bio.is_empty());
}

#[test]
fn biocessation_disables_only_organic_weapon_until_expiry() {
    let mut mob = actor();
    assert!(mob.apply_bio(effect(&mob, 21, 7)));
    assert!(mob.bio_weapon_disabled());
    assert_eq!(mob.tick(1., [100., 0., 0.]), 0.);
    assert_eq!(mob.shot_cooldown(), 0.);
    mob.bio[0].deadline_ms = crate::resources::unix_ms() - 1;
    assert!(mob.tick(0.1, [100., 0., 0.]) > 0.);
    let mut machine = actor();
    machine.definition.mob_type = 0;
    assert!(!machine.apply_bio(effect(&machine, 21, 7)));
}
