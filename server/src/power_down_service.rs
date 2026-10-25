//! Progen Sentinel Power Down: a persistent, stationary combat toggle.
use super::*;
use crate::skills::AbilityDefinition;

impl PlayerSession {
    /// Toggle the trained tier, committing the energy and state before publishing it.
    pub(super) fn toggle_power_down(&mut self, ability: &AbilityDefinition) -> io::Result<()> {
        if self.skills.power_down_ability.is_some() {
            return self.stop_power_down();
        }
        if self.skills.cast.is_some() {
            return Err(invalid("Another ability is charging."));
        }
        if self.hull <= 0. || self.warp.active() || self.gate_transit.is_some() {
            return Err(invalid(
                "Power Down requires an operational ship outside warp.",
            ));
        }
        if self.motion.speed.abs() >= 1. || self.motion.set_speed.abs() >= 1. {
            return Err(invalid("Disengage the engine before using Power Down."));
        }
        if self.weapon_firing.iter().any(|firing| *firing) {
            return Err(invalid("Disarm weapons before using Power Down."));
        }
        if self.following || self.formation_leader.is_some() {
            return Err(invalid(
                "Leave follow or formation before using Power Down.",
            ));
        }
        if self
            .skills
            .cooldowns
            .get(&ability.skill)
            .is_some_and(|remaining| *remaining > 0.)
        {
            return Err(invalid("Power Down is recovering."));
        }
        let tier = crate::power_down::tier(ability.id, self.skill_catalog.level(37))
            .ok_or_else(|| invalid("Power Down rank is not trained."))?;
        let energy_cost = crate::power_down::activation_energy(
            tier.energy,
            self.equipment.ship.power_down_skill_bonus,
        );
        if self.energy < energy_cost {
            return Err(invalid("Insufficient reactor energy."));
        }
        let before = self.energy;
        let old_cooldown = self
            .skills
            .cooldowns
            .insert(37, crate::power_down::COOLDOWN_SECONDS);
        self.energy -= energy_cost;
        self.skills.power_down_ability = Some(ability.id);
        if let Err(error) = self.checkpoint_session(true) {
            self.energy = before;
            self.skills.power_down_ability = None;
            match old_cooldown {
                Some(value) => {
                    self.skills.cooldowns.insert(37, value);
                }
                None => {
                    self.skills.cooldowns.remove(&37);
                }
            }
            return Err(error);
        }
        self.publish_power_down_resume();
        self.pending.push(packets::ship_resources(
            self.player_id(),
            &self.equipment.ship,
            Some(self.energy),
            None,
            None,
        ));
        self.pending.push(packets::message("Power Down active."));
        self.pending.push(packets::skill_cooldown(
            37,
            self.tick_ms(),
            (crate::power_down::COOLDOWN_SECONDS * 1000.) as u32,
        ));
        Ok(())
    }

    /// End Power Down before movement, weapon use, item use, or warp.
    pub(super) fn stop_power_down(&mut self) -> io::Result<()> {
        let Some(ability) = self.skills.power_down_ability.take() else {
            return Ok(());
        };
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.power_down_ability = Some(ability);
            return Err(error);
        }
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            crate::power_down::STATUS_SLOT,
            "",
            0,
        ));
        self.pending.push(packets::message("Power Down ended."));
        Ok(())
    }

    /// Replay an indefinite native status after a short session replacement.
    pub(super) fn publish_power_down_resume(&mut self) {
        if self.skills.power_down_ability.is_some() {
            self.pending.push(packets::ship_status_buff_slot(
                self.player_id(),
                crate::power_down::STATUS_SLOT,
                "Power_Down",
                true,
                0,
            ));
        }
    }

    /// Cancel a restored or active toggle when the engine has engaged.
    pub(super) fn advance_power_down(&mut self) -> io::Result<()> {
        if self.skills.power_down_ability.is_some()
            && (self.motion.speed.abs() >= 1.
                || self.motion.set_speed.abs() >= 1.
                || self.warp.active()
                || self.sector.station.is_some()
                || self.hull <= 0.)
        {
            self.stop_power_down()?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::account::{Account, FIRST_PLAYER_ID};

    /// Returns:
    ///     PlayerSession: A disposable max-trained Progen Sentinel in space.
    fn sentinel(name: &str, id: u32) -> PlayerSession {
        let root =
            std::env::temp_dir().join(format!("enb-power-down-{name}-{}", rand::random::<u64>()));
        let mut account = Account::create(name.into(), "test", name.into()).unwrap();
        account.character.race = 2;
        account.character.profession = 2;
        account.character.game_id = id;
        account.save_new(&root).unwrap();
        let sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
        let mut ship = PlayerSession::new(sector, Ipv4Addr::LOCALHOST, root).unwrap();
        // The starter reactor is too small for the rank-seven 600 energy cost.
        ship.inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3337, 1));
        ship.refresh_equipment().unwrap();
        ship.energy = ship.equipment.ship.energy_capacity;
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22343".parse().unwrap());
        ship
    }

    /// Each public tier changes only base recharge while equipped buffs remain additive.
    #[test]
    fn tier_cost_recharge_and_native_status_are_authoritative() {
        let mut ship = sentinel("Sentinel", FIRST_PLAYER_ID);
        let stats = ship.equipment.ship;
        assert!(stats.energy_recharge_base > 0. && stats.shield_recharge_base > 0.);
        for ability in [78, 1, 2, 3, 4] {
            ship.skills.cooldowns.remove(&37);
            ship.energy = stats.energy_capacity;
            let before = ship.energy;
            let definition = ship.skill_catalog.ability(ability).unwrap();
            let tier = crate::power_down::tier(ability, 7).unwrap();
            ship.begin_ability_for(ability, ship.player_id()).unwrap();
            assert_eq!(ship.skills.power_down_ability, Some(ability));
            assert!((ship.energy - (before - tier.energy)).abs() < 0.001);
            assert!(ship.pending.iter().any(|frame| frame.opcode == 0x1b
                && frame
                    .body
                    .windows(b"Power_Down".len())
                    .any(|w| w == b"Power_Down")));
            assert!(ship.pending.iter().all(|frame| frame.opcode != 0x09));
            ship.energy = 0.;
            ship.shield = 0.;
            ship.advance_ship_resources(1.);
            let expected_energy = (stats.energy_recharge - stats.energy_recharge_base
                + stats.energy_recharge_base * tier.reactor_recharge_fraction)
                .max(0.);
            let expected_shield = (stats.shield_recharge - stats.shield_recharge_base
                + stats.shield_recharge_base * tier.shield_recharge_fraction)
                .max(0.);
            assert!((ship.energy - expected_energy).abs() < 0.001);
            assert!((ship.shield - expected_shield).abs() < 0.001);
            ship.begin_ability_for(ability, ship.player_id()).unwrap();
            assert_eq!(ship.skills.power_down_ability, None);
            assert_eq!(definition.skill, 37);
        }
    }

    /// Equipped Power Down improves cost and recharge without unlocking a tier.
    #[test]
    fn equipped_power_down_bonus_survives_active_restart() {
        let mut ship = sentinel("GearedSentinel", FIRST_PLAYER_ID + 17);
        ship.inventory.equipped[9] = Some(crate::inventory::ItemInstance::stock(1924, 1));
        ship.refresh_equipment().unwrap();
        std::fs::write(
            ship.state_dir.join("inventory.json"),
            serde_json::to_vec(&ship.inventory).unwrap(),
        )
        .unwrap();
        let stats = ship.equipment.ship;
        assert!((stats.power_down_skill_bonus - 1.53).abs() < 0.001);
        assert!(crate::power_down::tier(4, 6).is_none());
        let before = ship.energy;
        ship.begin_ability_for(1, ship.player_id()).unwrap();
        let cost = crate::power_down::activation_energy(75., stats.power_down_skill_bonus);
        assert!((ship.energy - (before - cost)).abs() < 0.001);
        let root = ship.state_dir.clone();
        let sector = ship.sector.clone();
        drop(ship);
        let mut resumed = PlayerSession::new(sector, Ipv4Addr::LOCALHOST, root).unwrap();
        assert!(resumed.restore_session().unwrap());
        assert_eq!(resumed.skills.power_down_ability, Some(1));
        assert!((resumed.equipment.ship.power_down_skill_bonus - 1.53).abs() < 0.001);
        resumed.energy = 0.;
        resumed.shield = 0.;
        resumed.advance_ship_resources(1.);
        let energy_fraction = crate::power_down::equipped_recharge_fraction(0., 1.53);
        let shield_fraction = crate::power_down::equipped_recharge_fraction(0.5, 1.53);
        let expected_energy = crate::power_down::recharge_rate(
            stats.energy_recharge,
            stats.energy_recharge_base,
            energy_fraction,
        );
        let expected_shield = crate::power_down::recharge_rate(
            stats.shield_recharge,
            stats.shield_recharge_base,
            shield_fraction,
        );
        assert!((resumed.energy - expected_energy).abs() < 0.001);
        assert!((resumed.shield - expected_shield).abs() < 0.001);
    }

    /// A hostile MOB drops hate on the powered-down pilot and selects a peer.
    #[test]
    fn two_pilot_mob_hate_and_restart_replay_follow_toggle() {
        let mut ps = sentinel("Sentinel", FIRST_PLAYER_ID);
        let mut peer = sentinel("Wingman", FIRST_PLAYER_ID + 1);
        peer.position = ps.position;
        let mut mob = Mob::new(ps.sector.mobs[0].clone());
        mob.position = ps.position;
        mob.damage_by(20., ps.player_id());
        assert_eq!(mob.select_target(&[ps.threat(), peer.threat()]), Some(0));
        ps.begin_ability_for(4, ps.player_id()).unwrap();
        assert!(!ps.threat().visible);
        assert_eq!(mob.select_target(&[ps.threat(), peer.threat()]), Some(1));
        mob.damage_by(20., peer.player_id());
        assert_eq!(mob.select_target(&[ps.threat(), peer.threat()]), Some(1));
        let root = ps.state_dir.clone();
        let sector = ps.sector.clone();
        drop(ps);
        let mut resumed = PlayerSession::new(sector, Ipv4Addr::LOCALHOST, root).unwrap();
        assert!(resumed.restore_session().unwrap());
        assert_eq!(resumed.skills.power_down_ability, Some(4));
        assert!(!resumed.threat().visible);
        assert!(resumed.pending.iter().any(|frame| frame.opcode == 0x1b
            && frame
                .body
                .windows(b"Power_Down".len())
                .any(|w| w == b"Power_Down")));
        resumed.stop_power_down().unwrap();
        assert!(resumed.threat().visible);
        assert!(resumed.pending.iter().any(|frame| frame
            == &packets::ship_active_buff_slot(
                resumed.player_id(),
                crate::power_down::STATUS_SLOT,
                "",
                0
            )));
    }

    /// Engine movement, weapon activation, and cooldown gate the toggle.
    #[test]
    fn movement_weapon_and_cooldown_cancel_or_reject_power_down() {
        let mut ship = sentinel("Sentinel", FIRST_PLAYER_ID);
        ship.motion.thrust(0).unwrap();
        assert!(ship.begin_ability_for(78, ship.player_id()).is_err());
        ship.motion.stop();
        ship.begin_ability_for(78, ship.player_id()).unwrap();
        ship.motion.thrust(0).unwrap();
        ship.advance_power_down().unwrap();
        assert_eq!(ship.skills.power_down_ability, None);
        ship.motion.stop();
        assert!(ship.begin_ability_for(78, ship.player_id()).is_err());
        ship.advance_abilities(3.).unwrap();
        ship.begin_ability_for(78, ship.player_id()).unwrap();
        let weapon = ship.equipment.weapons[0].slot as u8;
        let mut activation = Vec::from(ship.player_id().to_le_bytes());
        activation.extend_from_slice(&[2, weapon]);
        ship.handle_input(0x5d, &activation).unwrap();
        assert_eq!(ship.skills.power_down_ability, None);
        ship.weapon_firing.fill(false);
        ship.player_firing = false;
        ship.advance_abilities(3.).unwrap();
        ship.begin_ability_for(78, ship.player_id()).unwrap();
        let mut route = Vec::from(ship.player_id().to_le_bytes());
        route.extend_from_slice(&0u16.to_le_bytes());
        ship.handle_input(0x9b, &route).unwrap();
        assert_eq!(ship.skills.power_down_ability, None);
        ship.handle_input(0x9b, &route).unwrap(); // stop the test warp
        ship.advance_abilities(3.).unwrap();
        ship.begin_ability_for(78, ship.player_id()).unwrap();
        let mut movement = Vec::from(ship.player_id().to_le_bytes());
        movement.push(0); // engage forward thrust
        ship.handle_input(0x14, &movement).unwrap();
        assert_eq!(ship.skills.power_down_ability, None);
    }

    /// A failed durable checkpoint cannot spend energy or hide the pilot.
    #[test]
    fn checkpoint_failure_rolls_back_activation() {
        let mut ship = sentinel("Sentinel", FIRST_PLAYER_ID);
        let before = ship.energy;
        let file = ship.state_dir.join("blocked");
        std::fs::write(&file, b"file").unwrap();
        ship.state_dir = file;
        assert!(ship.begin_ability_for(78, ship.player_id()).is_err());
        assert_eq!(ship.energy, before);
        assert_eq!(ship.skills.power_down_ability, None);
        assert!(ship.threat().visible);
    }
}
