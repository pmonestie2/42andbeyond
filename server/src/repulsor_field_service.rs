//! Recipient Repulsor Field status and durable cast handoff.
use super::*;
use crate::repulsor_field::{PendingRepulsorField, RepulsorBuff};

impl PlayerSession {
    /// Returns:
    ///     Option<&RepulsorBuff>: Recipient field, for durable game tests.
    #[cfg(test)]
    pub(crate) fn test_repulsor_field_buff(&self) -> Option<&RepulsorBuff> {
        self.repulsor_field_buff.as_ref()
    }

    /// Returns:
    ///     bool: Whether the recipient's native AUX queue advertises a field.
    #[cfg(test)]
    pub(crate) fn test_repulsor_status_queued(&self) -> bool {
        self.pending.iter().any(|frame| {
            frame.opcode == 0x1b
                && frame
                    .body
                    .windows(b"Repulsor_Field".len())
                    .any(|window| window == b"Repulsor_Field")
        })
    }

    /// Returns:
    ///     u64: Last Repulsor world revision applied to this pilot.
    pub(crate) fn repulsor_field_revision(&self) -> u64 {
        self.repulsor_field_revision
    }

    /// Returns:
    ///     Option<PendingRepulsorField>: Charged cast awaiting world settlement.
    pub(crate) fn pending_repulsor_field(&self) -> Option<PendingRepulsorField> {
        self.skills.pending_repulsor_field.clone()
    }

    /// Returns:
    ///     bool: Whether this pilot can cast or receive a field in space.
    pub(crate) fn repulsor_field_available(&self) -> bool {
        self.logged_in
            && self.hull > 0.
            && self.sector.station.is_none()
            && !self.warp.active()
            && self.gate_transit.is_none()
    }

    /// Returns:
    ///     bool: Whether the selected recipient remains visible.
    pub(crate) fn repulsor_field_contact(&self, target: u32) -> bool {
        target == self.player_id() || self.peer_contacts.contains_key(&target)
    }

    /// Remove a settled cast only after its session receipt is durable.
    pub(crate) fn clear_pending_repulsor_field(&mut self) -> io::Result<()> {
        let prior = self.skills.pending_repulsor_field.take();
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.pending_repulsor_field = prior;
            return Err(error);
        }
        Ok(())
    }

    /// Apply one world-committed field without replaying an older revision.
    pub(crate) fn apply_repulsor_field_buff(&mut self, buff: RepulsorBuff) -> io::Result<()> {
        if buff.revision <= self.repulsor_field_revision {
            return Ok(());
        }
        let prior = self.repulsor_field_buff.clone();
        let prior_revision = self.repulsor_field_revision;
        self.repulsor_field_revision = buff.revision;
        self.repulsor_field_buff = (buff.valid(crate::resources::unix_ms(), self.session_epoch)
            && self.hull > 0.
            && self.sector.station.is_none())
        .then_some(buff);
        if let Err(error) = self.checkpoint_session(true) {
            self.repulsor_field_buff = prior;
            self.repulsor_field_revision = prior_revision;
            return Err(error);
        }
        if prior.is_some() {
            self.pending.push(packets::ship_active_buff_slot(
                self.player_id(),
                self.repulsor_status_slot(),
                "",
                0,
            ));
        }
        self.publish_repulsor_field_resume();
        Ok(())
    }

    /// Returns:
    ///     usize: Recipient-class slot that cannot collide with exclusive skills.
    fn repulsor_status_slot(&self) -> usize {
        crate::repulsor_field::buff_slot(self.account.character.class().code())
    }

    /// Replay the timed native icon after a short server replacement.
    pub(super) fn publish_repulsor_field_resume(&mut self) {
        let Some(buff) = self.repulsor_field_buff.as_ref() else {
            return;
        };
        let remaining = buff
            .expires_at_ms
            .saturating_sub(crate::resources::unix_ms());
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            self.repulsor_status_slot(),
            "Repulsor_Field",
            self.tick_ms().wrapping_add(remaining as u32),
        ));
        self.message(&format!(
            "Repulsor Field active for {} seconds.",
            remaining.div_ceil(1000)
        ));
    }

    /// Remove an expired field and its class-safe native status icon.
    pub(super) fn advance_repulsor_field_buff(&mut self) -> io::Result<()> {
        let expired = self.repulsor_field_buff.as_ref().is_some_and(|buff| {
            !buff.valid(crate::resources::unix_ms(), self.session_epoch)
                || self.hull <= 0.
                || self.sector.station.is_some()
        });
        if !expired {
            return Ok(());
        }
        let prior = self.repulsor_field_buff.take();
        if let Err(error) = self.checkpoint_session(true) {
            self.repulsor_field_buff = prior;
            return Err(error);
        }
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            self.repulsor_status_slot(),
            "",
            0,
        ));
        self.message("Repulsor Field ended.");
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::account::{Account, FIRST_PLAYER_ID};
    use std::net::Ipv4Addr;

    /// Returns:
    ///     PlayerSession: Disposable max-trained Progen pilot in space.
    fn pilot(class: &str, name: &str, id: u32) -> PlayerSession {
        let root = std::env::temp_dir().join(format!(
            "enb-repulsor-status-{name}-{}",
            rand::random::<u64>()
        ));
        let mut account = Account::create(name.into(), "test", name.into()).unwrap();
        account.character.race = 2;
        account.character.profession = if class == "pw" { 0 } else { 2 };
        account.character.game_id = id;
        account.save_new(&root).unwrap();
        let sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
        let mut ship = PlayerSession::new(sector, Ipv4Addr::LOCALHOST, root).unwrap();
        ship.inventory.equipped[1] = Some(crate::inventory::ItemInstance::stock(3337, 1));
        ship.refresh_equipment().unwrap();
        ship.energy = ship.equipment.ship.energy_capacity;
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:22343".parse().unwrap());
        ship
    }

    /// Combat Trance and Power Down keep their own icons beside Repulsor.
    #[test]
    fn class_exclusive_statuses_overlap_and_replay_without_slot_collision() {
        let now = crate::resources::unix_ms();
        let mut sentinel = pilot("ps", "Sentinel", FIRST_PLAYER_ID);
        sentinel.begin_ability_for(4, sentinel.player_id()).unwrap();
        assert!(sentinel.skills.power_down_ability.is_some());
        let sentinel_buff = RepulsorBuff {
            revision: 1,
            source: sentinel.player_id(),
            target_epoch: sentinel.session_epoch,
            ability: 72,
            trained_rank: 7,
            effective_rank: 7.,
            expires_at_ms: now + crate::repulsor_field::duration_ms(7.),
        };
        sentinel.apply_repulsor_field_buff(sentinel_buff).unwrap();
        assert!(sentinel.pending.iter().any(|frame| frame.opcode == 0x1b
            && frame
                .body
                .windows(b"Power_Down".len())
                .any(|w| w == b"Power_Down")));
        assert!(sentinel.test_repulsor_status_queued());
        let sentinel_root = sentinel.state_dir.clone();
        let sentinel_sector = sentinel.sector.clone();
        drop(sentinel);
        let sentinel =
            PlayerSession::new(sentinel_sector, Ipv4Addr::LOCALHOST, sentinel_root).unwrap();
        assert!(sentinel.skills.power_down_ability.is_some());
        assert!(sentinel.test_repulsor_field_buff().is_some());
        assert!(sentinel.test_repulsor_status_queued());

        let mut warrior = pilot("pw", "Warrior", FIRST_PLAYER_ID + 1);
        warrior.advance_combat_trance().unwrap();
        assert!(warrior.combat_trance.active);
        let warrior_buff = RepulsorBuff {
            revision: 1,
            source: FIRST_PLAYER_ID,
            target_epoch: warrior.session_epoch,
            ability: 72,
            trained_rank: 7,
            effective_rank: 7.,
            expires_at_ms: now + crate::repulsor_field::duration_ms(7.),
        };
        warrior.apply_repulsor_field_buff(warrior_buff).unwrap();
        assert!(warrior.test_repulsor_status_queued());
        assert!(warrior.pending.iter().any(|frame| frame.opcode == 0x1b
            && frame
                .body
                .windows(b"Combat_Trance".len())
                .any(|w| w == b"Combat_Trance")));
        let warrior_root = warrior.state_dir.clone();
        let warrior_sector = warrior.sector.clone();
        drop(warrior);
        let warrior =
            PlayerSession::new(warrior_sector, Ipv4Addr::LOCALHOST, warrior_root).unwrap();
        assert!(warrior.combat_trance.active);
        assert!(warrior.test_repulsor_field_buff().is_some());
        assert!(warrior.test_repulsor_status_queued());
    }

    /// Expiry clears the authoritative status exactly once across restart.
    #[test]
    fn field_expiry_removes_native_status() {
        let now = crate::resources::unix_ms();
        let mut ship = pilot("ps", "Expiry", FIRST_PLAYER_ID);
        ship.apply_repulsor_field_buff(RepulsorBuff {
            revision: 1,
            source: ship.player_id(),
            target_epoch: ship.session_epoch,
            ability: 75,
            trained_rank: 1,
            effective_rank: 1.,
            expires_at_ms: now + 60_000,
        })
        .unwrap();
        ship.repulsor_field_buff.as_mut().unwrap().expires_at_ms = now - 1;
        ship.advance_repulsor_field_buff().unwrap();
        assert!(ship.repulsor_field_buff.is_none());
        assert!(ship.pending.contains(&packets::ship_active_buff_slot(
            ship.player_id(),
            crate::repulsor_field::buff_slot("ps"),
            "",
            0,
        )));
        let count = ship.pending.len();
        ship.advance_repulsor_field_buff().unwrap();
        assert_eq!(ship.pending.len(), count);
    }
}
