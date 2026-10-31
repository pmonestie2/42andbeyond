//! Acquire a visible pilot or group member's selected target through the authoritative session.
use super::*;

impl GameState {
    /// Acquire a selected object's current victim using the same scanner rules as manual targeting.
    /// Raises:
    ///     io::Error: The object is unknown, hidden, idle, or its victim cannot be selected.
    pub(super) fn assist_target(&mut self, source: u32, argument: &str) -> io::Result<()> {
        let argument = argument.trim();
        let object = if argument.is_empty() {
            self.sessions[&source].current_target()
        } else if let Some(number) = argument.strip_prefix('#') {
            number
                .parse::<u32>()
                .map_err(|_| invalid("Invalid Assist object ID."))?
        } else {
            return self.assist_pilot(source, argument);
        };
        if let Some(pilot) = self.sessions.get(&object) {
            let name = pilot.account.character.name.clone();
            return self.assist_pilot(source, &name);
        }
        if !self.sessions[&source].mob_target_in_scan(object)? {
            return Err(invalid("Select a visible pilot or hostile ship to assist."));
        }
        let sector = self.sessions[&source].sector.id;
        let victim = {
            let combat = self
                .combats
                .get(&sector)
                .ok_or_else(|| invalid("Sector combat is unavailable."))?;
            let world = lock_combat(combat)?;
            world
                .actors
                .get((object - MOB_ID) as usize)
                .and_then(|mob| mob.targeted_player())
                .ok_or_else(|| invalid("That ship has no combat target."))?
        };
        let caller = self.sessions.get_mut(&source).unwrap();
        caller.group_target(victim)?;
        if caller.current_target() != victim {
            return Err(invalid("That ship's target is outside your scanner range."));
        }
        Ok(())
    }

    /// Select a visible pilot or group member's target without revealing objects outside the caller's scanner.
    /// Raises:
    ///     io::Error: The pilot is offline, outside visibility/the sector, or has no selectable target.
    pub(super) fn assist_pilot(&mut self, source: u32, name: &str) -> io::Result<()> {
        let member = self
            .sessions
            .iter()
            .find_map(|(id, session)| {
                (session.logged_in
                    && session
                        .account
                        .character
                        .name
                        .eq_ignore_ascii_case(name.trim()))
                .then_some(*id)
            })
            .ok_or_else(|| invalid("Use /assist followed by an online pilot's name."))?;
        self.update_views();
        let grouped = self
            .groups
            .group(source)
            .is_some_and(|group| group.members.contains(&member));
        if member == source || (!grouped && !self.sessions[&source].has_visible_pilot(member)) {
            return Err(invalid(
                "Select a visible pilot or a member of your group to assist.",
            ));
        }
        let pilot = &self.sessions[&member];
        if !self.sessions[&source].logged_in || pilot.sector.id != self.sessions[&source].sector.id
        {
            return Err(invalid("That group member is not in your sector."));
        }
        let target = pilot.current_target();
        if target == 0 || target == u32::MAX {
            return Err(invalid("That group member has no target selected."));
        }
        self.update_views();
        let caller = self.sessions.get_mut(&source).unwrap();
        caller.group_target(target)?;
        if caller.current_target() != target {
            return Err(invalid("That target is outside your scanner range."));
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use crate::account::FIRST_PLAYER_ID;

    /// Assist follows a member's current target and never changes the member's selection.
    #[test]
    fn grouped_pilot_can_acquire_member_target() {
        let mut game = super::super::tests::game();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        super::super::tests::login(&mut game, a, 23541, "alice");
        super::super::tests::login(&mut game, b, 23542, "bob");
        let target = game.sessions[&b].sector.navpoints[0].id;
        game.sessions
            .get_mut(&b)
            .unwrap()
            .group_target(target)
            .unwrap();
        game.sessions.get_mut(&a).unwrap().position = [0., 0., 0.];
        game.sessions.get_mut(&b).unwrap().position = [100., 0., 0.];
        game.assist_pilot(a, "Bob").unwrap();
        game.groups.invite(a, b).unwrap();
        game.groups.accept(b).unwrap();
        game.assist_pilot(a, "bOb").unwrap();
        assert_eq!(game.sessions[&a].current_target(), target);
        assert_eq!(game.sessions[&b].current_target(), target);
        assert!(game.assist_pilot(a, "Alice").is_err());
        game.groups.leave(b);
        game.sessions.get_mut(&b).unwrap().position = [100_000., 0., 0.];
        assert!(game.assist_pilot(a, "Bob").is_err());
        game.groups.invite(a, b).unwrap();
        game.groups.accept(b).unwrap();
        game.sessions.get_mut(&b).unwrap().sector.id = 1060;
        assert!(game.assist_pilot(a, "Bob").is_err());
    }

    /// An empty member target and an unknown pilot preserve the caller's selection.
    #[test]
    fn empty_assist_does_not_clear_existing_target() {
        let mut game = super::super::tests::game();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        super::super::tests::login(&mut game, a, 23543, "alice");
        super::super::tests::login(&mut game, b, 23544, "bob");
        game.groups.invite(a, b).unwrap();
        game.groups.accept(b).unwrap();
        let target = game.sessions[&a].sector.navpoints[0].id;
        game.sessions
            .get_mut(&a)
            .unwrap()
            .group_target(target)
            .unwrap();
        game.sessions.get_mut(&b).unwrap().group_target(0).unwrap();
        assert!(game.assist_pilot(a, "Bob").is_err());
        assert!(game.assist_pilot(a, "Missing").is_err());
        assert_eq!(game.sessions[&a].current_target(), target);
    }
    /// Hostile Assist follows its actual victim, including the caller, without exposing an unscanned MOB.
    #[test]
    fn hostile_assist_acquires_victim_or_self() {
        let mut game = super::super::tests::game();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        super::super::tests::login(&mut game, a, 23545, "alice");
        super::super::tests::login(&mut game, b, 23546, "bob");
        game.sessions.get_mut(&a).unwrap().position = [0., 0., 0.];
        game.sessions.get_mut(&b).unwrap().position = [100., 0., 0.];
        let threat = game.sessions[&b].threat();
        {
            let mut world = super::lock_combat(game.combats.get(&1076).unwrap()).unwrap();
            let mob = &mut world.actors[0];
            mob.position = [200., 0., 0.];
            mob.damage_by(1., b);
            mob.select_target(&[threat]);
            assert_eq!(mob.targeted_player(), Some(b));
        }
        game.update_views();
        let command = format!("#{}", super::MOB_ID);
        game.assist_target(a, &command).unwrap();
        assert_eq!(game.sessions[&a].current_target(), b);
        let threat = game.sessions[&a].threat();
        {
            let mut world = super::lock_combat(game.combats.get(&1076).unwrap()).unwrap();
            let mob = &mut world.actors[0];
            mob.remove_player_hate(b);
            mob.damage_by(1., a);
            mob.select_target(&[threat]);
        }
        game.assist_target(a, &command).unwrap();
        assert_eq!(game.sessions[&a].current_target(), a);
        {
            let mut world = super::lock_combat(game.combats.get(&1076).unwrap()).unwrap();
            world.actors[0].position = [1_000_000., 0., 0.];
        }
        assert!(game.assist_target(a, &command).is_err());
        assert_eq!(game.sessions[&a].current_target(), a);
    }
}
