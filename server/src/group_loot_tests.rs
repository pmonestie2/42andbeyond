//! Group authorization exercised through authenticated native loot requests.
use super::*;

/// Group membership is resolved by the server at open time, never supplied by a client.
#[test]
fn group_member_can_open_killers_private_corpse_after_accepting_invitation() {
    let mut game = super::tests::game();
    let alice = crate::account::FIRST_PLAYER_ID;
    let bob = alice + 1;
    super::tests::login(&mut game, alice, 24301, "alice");
    super::tests::login(&mut game, bob, 24302, "bob");
    let position = game.sessions[&alice].position;
    game.sessions.get_mut(&bob).unwrap().position = position;
    let items = crate::items::ItemCatalog::load().unwrap();
    let world = Arc::clone(&game.loot_worlds[&1076]);
    let corpse = lock_loot(&world)
        .unwrap()
        .spawn(
            crate::loot_world::CorpseBirth {
                death: crate::loot_world::MobDeath {
                    spawn: 90001,
                    generation: 1,
                },
                template: 2010,
                name: "Grouped salvage".into(),
                level: 10,
                killer: alice,
                position,
                now_ms: crate::resources::unix_ms(),
            },
            &crate::loot::LootCatalog::load().unwrap(),
            &items,
        )
        .unwrap()
        .unwrap();
    game.advance(0.).unwrap();
    game.handle_input(
        bob,
        0x17,
        &[bob.to_le_bytes(), corpse.to_le_bytes()].concat(),
    )
    .unwrap();
    assert!(game
        .handle_event(
            bob,
            ClientEvent::LootOpen {
                target: corpse,
                toggle: false
            }
        )
        .is_err());
    game.groups.invite(alice, bob).unwrap();
    game.groups.accept(bob).unwrap();
    let before = game.sessions[&bob].inventory.credits;
    let credits = lock_loot(&world)
        .unwrap()
        .get(corpse)
        .unwrap()
        .initial
        .credits;
    game.handle_event(
        bob,
        ClientEvent::LootOpen {
            target: corpse,
            toggle: false,
        },
    )
    .unwrap();
    assert_eq!(game.sessions[&bob].inventory.credits, before + credits);
    game.handle_event(
        bob,
        ClientEvent::LootOpen {
            target: corpse,
            toggle: false,
        },
    )
    .unwrap();
    assert_eq!(game.sessions[&bob].inventory.credits, before + credits);
    let root = game.root.clone();
    drop(game);
    std::fs::remove_dir_all(root).unwrap();
}
