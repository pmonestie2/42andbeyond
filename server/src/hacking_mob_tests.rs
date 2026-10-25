//! Hacking effects alter the authoritative MOB simulation and survive saves.
use super::*;
use crate::hacking::{HackEffect, ShipSystem};

/// Returns:
///     Mob: One mechanical actor with a predictable weapon and shield.
fn actor() -> Mob {
    let mut definition: MobDefinition = serde_json::from_str(
        r#"{"source_id":910001,"template_id":46,"name":"Hack target","level":1,"asset":48,"spawn":[500,0,0],"respawn_seconds":20,"aggressive":true,"mob_type":0}"#,
    ).unwrap();
    definition.combat_override = Some(MobStats {
        hull: 100.,
        shield: 100.,
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
///     HackEffect: Current-generation subsystem receipt.
fn effect(mob: &Mob, system: Option<ShipSystem>, weapon: Option<usize>) -> HackEffect {
    HackEffect {
        source: 0x4000_0001,
        source_epoch: 7,
        effect: 91,
        ability: 76,
        trained_rank: 5,
        mob_generation: mob.death_generation,
        system,
        weapon_slot: weapon,
        jam_communications: true,
        deadline_ms: crate::resources::unix_ms() + 12_000,
    }
}

/// Engine, reactor, shield and weapon attempts affect real AI and damage paths.
#[test]
fn subsystem_and_weapon_suppression_are_authoritative() {
    let mut engine = actor();
    assert!(engine.apply_hack(effect(&engine, Some(ShipSystem::Engine), None)));
    assert!(!engine.apply_hack(effect(&engine, Some(ShipSystem::Engine), None)));
    let prior = engine.position;
    assert_eq!(engine.tick(1., [3_000., 0., 0.]), 0.);
    assert_eq!(engine.position, prior);
    assert!(engine.hacked_communications());
    let saved: Mob = serde_json::from_slice(&serde_json::to_vec(&engine).unwrap()).unwrap();
    let mut restored = actor();
    restored.restore_from(saved).unwrap();
    assert!(restored.hacked_system(ShipSystem::Engine));

    let mut weapon = actor();
    assert!(weapon.apply_hack(effect(&weapon, None, Some(0))));
    assert_eq!(weapon.tick(1., [100., 0., 0.]), 0.);
    assert!(!weapon.may_fire_this_tick(1., &[]));
    let mut reactor = actor();
    assert!(reactor.apply_hack(effect(&reactor, Some(ShipSystem::Reactor), None)));
    assert_eq!(reactor.tick(1., [100., 0., 0.]), 0.);
    let mut shield = actor();
    assert!(shield.apply_hack(effect(&shield, Some(ShipSystem::Shield), None)));
    shield.damage(10.);
    assert_eq!(shield.shield, 90.);
    assert_eq!(shield.health, 95.); // Local 50% shield-system leakage.
    shield.hacks[0].deadline_ms = crate::resources::unix_ms() - 1;
    assert!(!shield.hacked_system(ShipSystem::Shield));
    shield.damage(10.);
    assert_eq!(shield.shield, 80.);
    assert_eq!(shield.health, 95.);
    shield.damage(1000.);
    assert_eq!(shield.state, MobState::Dead);
    assert!(shield.hacks.is_empty());
}
