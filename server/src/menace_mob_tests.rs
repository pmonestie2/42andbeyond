use super::*;
use crate::menace::MenaceFear;

/// Returns:
///     Mob: Armed hostile test actor with deterministic combat statistics.
fn armed_mob() -> Mob {
    let mut definition: MobDefinition = serde_json::from_str(
        r#"{"source_id":41,"template_id":46,"name":"Fear target","level":20,"asset":48,"spawn":[500,0,0],"respawn_seconds":20,"aggressive":true}"#,
    )
    .unwrap();
    definition.combat_override = Some(MobStats {
        hull: 100.,
        shield: 0.,
        dps: 40.,
        reload: 1.,
        range: 1000.,
        effect: 35,
        damage_type: 3,
        variance: false,
    });
    Mob::new(definition)
}

/// Returns:
///     MenaceFear: Live Terrify receipt for the actor's current generation.
fn terror(mob: &Mob) -> MenaceFear {
    MenaceFear {
        source: 0x4000_0001,
        source_epoch: 7,
        effect: 91,
        ability: 127,
        trained_rank: 5,
        mob_generation: mob.death_generation,
        origin: [0., 0., 0.],
        deadline_ms: crate::resources::unix_ms() + 15_000,
    }
}

/// An armed MOB flees away, emits movement, and launches no weapon while feared.
#[test]
fn terrify_flees_and_blocks_damage_and_duplicate_receipt() {
    let mut mob = armed_mob();
    assert!(mob.tick(1., [100., 0., 0.]) > 0.);
    let fear = terror(&mob);
    assert!(mob.apply_menace(fear.clone()));
    assert!(!mob.apply_menace(fear));
    let prior_id = mob.motion.movement_id;
    let before = mob.position[0];
    assert_eq!(mob.tick(1., [100., 0., 0.]), 0.);
    assert_eq!(mob.state, MobState::Fleeing);
    assert!(mob.position[0] > before);
    assert!(mob.speed > 0.);
    assert!(mob.motion.movement_id > prior_id);
    assert_eq!(mob.targeted_player(), None);
    mob.menace[0].deadline_ms = crate::resources::unix_ms() - 1;
    assert!(mob.tick(1., [100., 0., 0.]) > 0.);
    assert_eq!(mob.state, MobState::Attacking);
}

/// Intimidate/Scare can still fire while retreating; Terrify disarms.
#[test]
fn lower_menace_tiers_preserve_weapon_fire_during_flight() {
    for (ability, trained_rank, suppress_abilities) in [(64, 1, false), (99, 3, true)] {
        let mut mob = armed_mob();
        let mut fear = terror(&mob);
        fear.ability = ability;
        fear.trained_rank = trained_rank;
        assert_eq!(fear.suppress_abilities(), suppress_abilities);
        assert!(!fear.suppress_weapons());
        assert!(mob.apply_menace(fear));
        assert!(mob.tick(1., [100., 0., 0.]) > 0.);
        assert!(mob.position[0] > 500.);
        assert_eq!(mob.state, MobState::Fleeing);
    }
}

/// Restart retains a live fear, while death/respawn invalidate the generation.
#[test]
fn fear_survives_checkpoint_and_clears_on_death() {
    let mut mob = armed_mob();
    assert!(mob.apply_menace(terror(&mob)));
    let json = serde_json::to_vec(&mob).unwrap();
    let saved: Mob = serde_json::from_slice(&json).unwrap();
    let mut restored = armed_mob();
    restored.restore_from(saved).unwrap();
    assert!(restored.active_menace().is_some());
    assert_eq!(restored.tick(0.5, [100., 0., 0.]), 0.);
    restored.damage(1000.);
    assert_eq!(restored.state, MobState::Dead);
    assert!(restored.menace.is_empty());
    restored.tick(21., [100., 0., 0.]);
    assert!(restored.active_menace().is_none());
}
