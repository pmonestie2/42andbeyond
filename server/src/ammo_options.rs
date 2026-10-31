//! Opt-in authoritative launcher options; native item templates and legacy traffic stay unchanged.
use super::*;

impl PlayerSession {
    /// Returns:
    ///     Frame: Full launcher preferences and compatible cargo-slot IDs, all little-endian u32.
    fn ammo_options_frame(&self) -> Frame {
        let mounts: Vec<usize> = (3..=8)
            .filter(|mount| {
                self.inventory.equipped[*mount]
                    .as_ref()
                    .is_some_and(|item| {
                        self.items
                            .get(item.template)
                            .is_ok_and(|item| matches!(item.item_type, 15 | 16))
                    })
            })
            .collect();
        let mut body = [self.player_id(), mounts.len() as u32]
            .map(u32::to_le_bytes)
            .concat();
        for mount in mounts {
            let preferred = self.inventory.ammunition[mount]
                .as_ref()
                .map(|item| item.template)
                .or(self.inventory.preferred_ammunition[mount])
                .unwrap_or(0);
            let cargo: Vec<u32> = self
                .inventory
                .cargo
                .iter()
                .take(self.inventory.cargo_capacity)
                .enumerate()
                .filter_map(|(slot, item)| {
                    item.as_ref()
                        .filter(|item| {
                            self.inventory
                                .check_ammunition(mount, item, &self.items)
                                .is_ok()
                        })
                        .map(|_| slot as u32)
                })
                .collect();
            body.extend(
                [
                    mount as u32,
                    u32::from(self.inventory.auto_reload[mount]),
                    preferred,
                    cargo.len() as u32,
                ]
                .map(u32::to_le_bytes)
                .concat(),
            );
            for slot in cargo {
                body.extend(slot.to_le_bytes());
            }
        }
        Frame::new(0x203c, body)
    }

    /// Send only changed option content unless an explicit request needs a full response.
    pub(super) fn publish_ammo_options(&mut self, force: bool) {
        if !self.ammo_options || !self.logged_in {
            return;
        }
        let frame = self.ammo_options_frame();
        if force || self.last_ammo_snapshot.as_ref() != Some(&frame.body) {
            self.last_ammo_snapshot = Some(frame.body.clone());
            self.pending.push(frame);
        }
    }

    /// Handle authenticated owner/mount/enabled requests without relying on a selected target.
    /// Raises:
    ///     io::Error: Ownership, mount, value or durable publication is invalid.
    pub(super) fn ammo_options_input(&mut self, body: &[u8]) -> io::Result<()> {
        let mut reader = Reader::new(body);
        if body.len() != 12 || reader.u32()? != self.player_id() || !self.logged_in {
            return Err(invalid("Invalid launcher preference owner or length."));
        }
        let mount = reader.u32()?;
        let enabled = reader.u32()?;
        if mount == u32::MAX {
            if enabled != 0 {
                return Err(invalid("Invalid launcher preference query."));
            }
            let previous = self.ammo_options;
            self.ammo_options = true;
            if let Err(error) = self.checkpoint_session(true) {
                self.ammo_options = previous;
                return Err(error);
            }
            self.publish_ammo_options(true);
            return Ok(());
        }
        if enabled > 1 || !(3..=8).contains(&mount) {
            return Err(invalid("Invalid launcher preference mount or value."));
        }
        let mount = mount as usize;
        let launcher = self.inventory.equipped[mount]
            .as_ref()
            .ok_or_else(|| invalid("That mount has no launcher."))?;
        if !matches!(self.items.get(launcher.template)?.item_type, 15 | 16) {
            return Err(invalid(
                "Auto Reload applies to missile and projectile launchers only.",
            ));
        }
        if self.hull <= 0. || self.warp.active() {
            return Err(invalid(
                "Launcher options require a living ship outside warp.",
            ));
        }
        let mut candidate = self.inventory.clone();
        candidate.auto_reload[mount] = enabled == 1;
        if let Some(ammo) = &candidate.ammunition[mount] {
            candidate.preferred_ammunition[mount] = Some(ammo.template);
        }
        if enabled == 1 {
            candidate.reload_selected_ammunition(mount, &self.items)?;
        }
        self.ammo_options = true;
        if candidate != self.inventory {
            self.commit_inventory(candidate)?;
            self.publish_ammo_options(false);
        } else {
            self.publish_ammo_options(true);
        }
        Ok(())
    }

    /// Reload only firing, empty, enabled mounts when their exact remembered stack is available.
    /// Raises:
    ///     io::Error: A remembered selection or durable inventory write is invalid.
    pub(super) fn reload_firing_ammunition(&mut self) -> io::Result<()> {
        if !(3..=8).any(|mount| {
            self.weapon_firing[mount]
                && self.inventory.auto_reload[mount]
                && self.inventory.ammunition[mount].is_none()
                && self.inventory.preferred_ammunition[mount].is_some()
        }) {
            return Ok(());
        }
        let mut candidate = self.inventory.clone();
        let mut changed = false;
        for mount in 3..=8 {
            if self.weapon_firing[mount] {
                changed |= candidate.reload_selected_ammunition(mount, &self.items)?;
            }
        }
        if changed {
            self.commit_inventory(candidate)?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::inventory::ItemInstance;

    /// Returns:
    ///     PlayerSession: An authenticated fixture with real ML-compatible and incompatible cargo.
    fn armed() -> PlayerSession {
        let mut ship = super::super::tests::session();
        ship.logged_in = true;
        ship.selected_peer = Some("127.0.0.1".parse().unwrap());
        ship.endpoint = Some("127.0.0.1:24003".parse().unwrap());
        ship.inventory.equipped[3] = Some(ItemInstance::stock(2834, 1));
        ship.inventory.ammunition[3] = Some(ItemInstance::stock(752, 1));
        ship.inventory.cargo[0] = Some(ItemInstance::stock(5430, 7));
        ship.inventory.cargo[1] = Some(ItemInstance::stock(752, 4));
        ship.inventory.cargo[2] = Some(ItemInstance::stock(512, 10));
        ship.refresh_equipment().unwrap();
        ship.pending.clear();
        ship
    }

    /// Returns:
    ///     Vec<u8>: Authenticated little-endian owner, mount and preference request.
    fn request(ship: &PlayerSession, mount: u32, enabled: u32) -> Vec<u8> {
        [ship.player_id(), mount, enabled]
            .map(u32::to_le_bytes)
            .concat()
    }

    /// Legacy sessions see no extension; modern queries receive only compatible slots and memoized updates.
    #[test]
    fn launcher_options_are_opt_in_authoritative_and_change_driven() {
        let mut ship = armed();
        ship.publish_ammo_options(false);
        assert!(ship.pending.is_empty());
        ship.ammo_options_input(&request(&ship, u32::MAX, 0))
            .unwrap();
        let frame = ship.pending.last().unwrap().clone();
        assert_eq!(frame.opcode, 0x203c);
        let mut reader = Reader::new(&frame.body);
        assert_eq!(reader.u32().unwrap(), ship.player_id());
        assert_eq!(reader.u32().unwrap(), 1);
        for expected in [3, 1, 752, 2, 0, 1] {
            assert_eq!(reader.u32().unwrap(), expected);
        }
        assert!(reader.rest().is_empty());
        ship.pending.clear();
        ship.publish_ammo_options(false);
        assert!(ship.pending.is_empty());
        let candidate = ship
            .inventory
            .moved(
                ItemTransfer {
                    source: 1,
                    source_slot: 0,
                    destination: 2,
                    destination_slot: 3,
                    quantity: 7,
                },
                &ship.items,
            )
            .unwrap();
        ship.commit_inventory(candidate).unwrap();
        let option = ship
            .pending
            .iter()
            .rev()
            .find(|frame| frame.opcode == 0x203c)
            .unwrap();
        assert_ne!(option.body, frame.body);
        assert_eq!(ship.inventory.preferred_ammunition[3], Some(5430));
        let checkpoint = serde_json::to_value(ship.capture_checkpoint().unwrap().unwrap()).unwrap();
        assert_eq!(checkpoint["ammo_options"], true);
    }

    /// Checkbox changes persist, load an empty enabled mount, and reject foreign owners or non-launcher mounts.
    #[test]
    fn launcher_preferences_commit_and_empty_enable_reloads_selected_cargo() {
        let mut ship = armed();
        let before = ship.inventory.clone();
        assert!(ship
            .ammo_options_input(&[ship.player_id() + 1, 3, 0].map(u32::to_le_bytes).concat())
            .is_err());
        assert!(ship.ammo_options_input(&request(&ship, 1, 0)).is_err());
        assert!(ship.ammo_options_input(&request(&ship, 3, 2)).is_err());
        assert_eq!(ship.inventory, before);
        ship.ammo_options_input(&request(&ship, 3, 0)).unwrap();
        assert!(!ship.inventory.auto_reload[3]);
        assert_eq!(ship.inventory.preferred_ammunition[3], Some(752));
        let mut candidate = ship.inventory.clone();
        candidate.consume_ammunition(3, &ship.items).unwrap();
        ship.commit_inventory(candidate).unwrap();
        assert!(ship.inventory.ammunition[3].is_none());
        ship.ammo_options_input(&request(&ship, 3, 1)).unwrap();
        assert_eq!(ship.inventory.ammunition[3].as_ref().unwrap().template, 752);
        assert_eq!(ship.inventory.ammunition[3].as_ref().unwrap().quantity, 4);
        assert_eq!(ship.inventory.cargo[0].as_ref().unwrap().template, 5430);
        let saved: Inventory =
            serde_json::from_slice(&std::fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved, ship.inventory);
    }

    /// Failed preference writes cannot change options or move ammunition; idle retries make no writes.
    #[test]
    fn preference_write_failure_and_empty_fire_retry_preserve_owned_state() {
        let mut ship = armed();
        let before = ship.inventory.clone();
        std::fs::create_dir_all(&ship.state_dir).unwrap();
        crate::durable_json::replace(&ship.state_dir.join("inventory.json"), &before).unwrap();
        let blocked = ship.state_dir.join("inventory.json.tmp");
        std::fs::create_dir(&blocked).unwrap();
        assert!(ship.ammo_options_input(&request(&ship, 3, 0)).is_err());
        assert_eq!(ship.inventory, before);
        let saved: Inventory =
            serde_json::from_slice(&std::fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved, before);
        assert!(ship.pending.iter().all(|frame| frame.opcode != 0x203c));
        std::fs::remove_dir(blocked).unwrap();
        ship.inventory.ammunition[3] = None;
        ship.inventory.preferred_ammunition[3] = Some(752);
        ship.inventory.cargo[1] = None;
        ship.weapon_firing[3] = true;
        ship.reload_firing_ammunition().unwrap();
        assert!(!ship.state_dir.join("inventory.json.tmp").exists());
        assert!(ship.inventory.ammunition[3].is_none());
        ship.inventory.cargo[1] = Some(ItemInstance::stock(752, 3));
        ship.reload_firing_ammunition().unwrap();
        assert_eq!(ship.inventory.ammunition[3].as_ref().unwrap().quantity, 3);
    }
}
