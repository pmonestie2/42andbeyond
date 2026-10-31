//! Native ammo is a second inventory indexed by the launcher's mount. Loading
//! uses cargo -> equipment (1 -> 2), rather than replacing the launcher itself.
//! Unity magazine transfers use container 8; container 3 remains station vault storage.
use crate::inventory::{Inventory, ItemInstance, ItemTransfer};
use crate::items::ItemCatalog;
use crate::protocol::invalid;
use std::io;

impl Inventory {
    pub(crate) fn check_ammunition(
        &self,
        slot: usize,
        ammo: &ItemInstance,
        items: &ItemCatalog,
    ) -> io::Result<()> {
        let launcher = self
            .equipped
            .get(slot)
            .and_then(Option::as_ref)
            .ok_or_else(|| invalid("No launcher in that mount."))?;
        let launcher = items.get(launcher.template)?;
        let template = items.get(ammo.template)?;
        if !(3..=8).contains(&slot)
            || !matches!(launcher.item_type, 15 | 16)
            || template.item_type != 10
            || launcher.ammo_type == 0
            || template.ammo_type != launcher.ammo_type
        {
            return Err(invalid("Ammunition does not match this launcher."));
        }
        Ok(())
    }

    pub(crate) fn loaded_ammunition(
        &self,
        cargo: usize,
        mount: usize,
        quantity: u32,
        items: &ItemCatalog,
    ) -> io::Result<Self> {
        let source = self
            .cargo
            .get(cargo)
            .and_then(Option::as_ref)
            .ok_or_else(|| invalid("No ammunition in that cargo slot."))?;
        if quantity == 0 || quantity > source.quantity {
            return Err(invalid("Invalid ammo quantity."));
        }
        self.check_ammunition(mount, source, items)?;
        let mut candidate = self.clone();
        candidate.preferred_ammunition[mount] = Some(source.template);
        let previous = candidate.ammunition[mount].take();
        let mut arriving = source.clone();
        arriving.quantity = quantity;
        let remaining = source.quantity - quantity;
        candidate.cargo[cargo] = (remaining > 0).then(|| {
            let mut item = source.clone();
            item.quantity = remaining;
            item
        });
        match previous {
            Some(mut loaded) if loaded.stacks_with(source) => {
                let moved = quantity.min(items.get(source.template)?.max_stack - loaded.quantity);
                if moved == 0 {
                    return Err(invalid("Launcher ammunition stack is full."));
                }
                loaded.quantity += moved;
                candidate.ammunition[mount] = Some(loaded);
                let leftover = source.quantity - moved;
                candidate.cargo[cargo] = (leftover > 0).then(|| {
                    let mut item = source.clone();
                    item.quantity = leftover;
                    item
                });
            }
            previous => {
                candidate.ammunition[mount] = Some(arriving);
                if let Some(previous) = previous {
                    candidate = candidate.received_instance(&previous, u32::MAX as usize, items)?;
                }
            }
        }
        candidate.validate(items)?;
        Ok(candidate)
    }

    /// Consume one round and optionally reload the same selected template from owned cargo.
    /// The caller prepares this on its inventory copy before persisting a volley.
    pub(crate) fn consume_ammunition(
        &mut self,
        mount: usize,
        items: &ItemCatalog,
    ) -> io::Result<ItemInstance> {
        let mut round = self
            .ammunition
            .get(mount)
            .and_then(Option::as_ref)
            .cloned()
            .ok_or_else(|| invalid("Launcher has no ammunition."))?;
        self.check_ammunition(mount, &round, items)?;
        self.preferred_ammunition[mount] = Some(round.template);
        if round.quantity > 1 {
            self.ammunition[mount]
                .as_mut()
                .expect("loaded stack")
                .quantity -= 1;
        } else {
            self.ammunition[mount] = None;
            self.reload_selected_ammunition(mount, items)?;
        }
        round.quantity = 1;
        Ok(round)
    }

    /// Returns:
    ///     bool: An empty enabled magazine received its exact remembered ammunition template.
    /// Raises:
    ///     io::Error: The mount is not an equipped launcher.
    pub(crate) fn reload_selected_ammunition(
        &mut self,
        mount: usize,
        items: &ItemCatalog,
    ) -> io::Result<bool> {
        let Some(template) = self.preferred_ammunition.get(mount).copied().flatten() else {
            return Ok(false);
        };
        if !self.auto_reload[mount] || self.ammunition[mount].is_some() {
            return Ok(false);
        }
        self.check_ammunition(mount, &ItemInstance::stock(template, 1), items)?;
        let Some(slot) = self
            .cargo
            .iter()
            .take(self.cargo_capacity)
            .position(|item| item.as_ref().is_some_and(|item| item.template == template))
        else {
            return Ok(false);
        };
        self.ammunition[mount] = self.cargo[slot].take();
        Ok(true)
    }

    /// Weapon swaps carry their loaded stacks. Removing or replacing a launcher
    /// returns its ammunition to cargo, or rejects the whole operation if full.
    pub(crate) fn reconcile_ammunition(
        &mut self,
        previous: &Self,
        transfer: ItemTransfer,
        items: &ItemCatalog,
    ) -> io::Result<()> {
        // Explicit unloading retains its previous selection even for a legacy
        // inventory that predates the preference fields.
        for slot in 3..=8 {
            if let Some(ammo) = &previous.ammunition[slot] {
                self.preferred_ammunition[slot] = Some(ammo.template);
            }
        }
        if transfer.source == 2 && transfer.destination == 2 {
            self.ammunition
                .swap(transfer.source_slot, transfer.destination_slot);
            self.preferred_ammunition
                .swap(transfer.source_slot, transfer.destination_slot);
        } else if transfer.source == 8 && transfer.destination == 8 {
            self.preferred_ammunition
                .swap(transfer.source_slot, transfer.destination_slot);
        } else {
            for slot in 0..self.equipped.len() {
                if self.equipped[slot] != previous.equipped[slot] {
                    self.preferred_ammunition[slot] = None;
                    if let Some(ammo) = self.ammunition[slot].take() {
                        *self = self.received_instance(&ammo, u32::MAX as usize, items)?;
                    }
                }
            }
        }
        for slot in 3..=8 {
            if let Some(ammo) = &self.ammunition[slot] {
                self.preferred_ammunition[slot] = Some(ammo.template);
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn armed(items: &ItemCatalog) -> Inventory {
        let mut inventory = Inventory::starter(items).unwrap();
        inventory.equipped[3] = Some(ItemInstance::stock(3022, 1));
        inventory.cargo[0] = Some(ItemInstance::stock(512, 20));
        inventory
    }
    #[test]
    fn magazine_unload_preserves_launcher_and_conserves_rounds() {
        let items = ItemCatalog::load().unwrap();
        let original = armed(&items);
        let loaded = original
            .moved(
                ItemTransfer {
                    source: 1,
                    source_slot: 0,
                    destination: 2,
                    destination_slot: 3,
                    quantity: 20,
                },
                &items,
            )
            .unwrap();
        let unloaded = loaded
            .moved(
                ItemTransfer {
                    source: 8,
                    source_slot: 3,
                    destination: 1,
                    destination_slot: 0,
                    quantity: 20,
                },
                &items,
            )
            .unwrap();
        assert_eq!(unloaded.equipped, original.equipped);
        assert_eq!(unloaded.ammunition[3], None);
        assert_eq!(unloaded.cargo[0], original.cargo[0]);
        assert!(loaded
            .moved(
                ItemTransfer {
                    source: 8,
                    source_slot: 3,
                    destination: 1,
                    destination_slot: 0,
                    quantity: 21,
                },
                &items
            )
            .is_err());
    }

    #[test]
    fn native_load_split_merge_and_weapon_removal_conserve_owned_ammo() {
        let items = ItemCatalog::load().unwrap();
        let original = armed(&items);
        let load = ItemTransfer {
            source: 1,
            source_slot: 0,
            destination: 2,
            destination_slot: 3,
            quantity: 5,
        };
        let loaded = original.moved(load, &items).unwrap();
        assert_eq!(loaded.equipped, original.equipped);
        assert_eq!(loaded.cargo[0].as_ref().unwrap().quantity, 15);
        assert_eq!(loaded.ammunition[3].as_ref().unwrap().quantity, 5);
        let stacked = loaded.moved(load, &items).unwrap();
        assert_eq!(stacked.ammunition[3].as_ref().unwrap().quantity, 10);
        assert_eq!(stacked.cargo[0].as_ref().unwrap().quantity, 10);
        let restored: Inventory =
            serde_json::from_slice(&serde_json::to_vec(&stacked).unwrap()).unwrap();
        assert_eq!(restored, stacked);
        let unequipped = restored
            .moved(
                ItemTransfer {
                    source: 2,
                    source_slot: 3,
                    destination: 1,
                    destination_slot: 1,
                    quantity: 1,
                },
                &items,
            )
            .unwrap();
        assert!(unequipped.ammunition[3].is_none());
        assert!(unequipped.equipped[3].is_none());
        assert_eq!(unequipped.cargo[0].as_ref().unwrap().quantity, 20);
        assert_eq!(unequipped.cargo[1].as_ref().unwrap().template, 3022);
        assert!(original
            .moved(
                ItemTransfer {
                    destination_slot: 4,
                    ..load
                },
                &items
            )
            .is_err());
        let mut wrong = original.clone();
        wrong.cargo[0] = Some(ItemInstance::stock(507, 5));
        assert!(wrong.moved(load, &items).is_err());
    }
    #[test]
    fn swapping_launchers_carries_ammo_and_full_cargo_rejects_without_loss() {
        let items = ItemCatalog::load().unwrap();
        let original = armed(&items);
        let mut loaded = original
            .moved(
                ItemTransfer {
                    source: 1,
                    source_slot: 0,
                    destination: 2,
                    destination_slot: 3,
                    quantity: 20,
                },
                &items,
            )
            .unwrap();
        loaded.equipped[4] = Some(ItemInstance::stock(3022, 1));
        let swapped = loaded
            .moved(
                ItemTransfer {
                    source: 2,
                    source_slot: 3,
                    destination: 2,
                    destination_slot: 4,
                    quantity: 1,
                },
                &items,
            )
            .unwrap();
        assert!(swapped.ammunition[3].is_none());
        assert_eq!(swapped.ammunition[4].as_ref().unwrap().quantity, 20);
        loaded.cargo.fill(Some(ItemInstance::stock(2845, 1)));
        let snapshot = loaded.clone();
        assert!(loaded
            .moved(
                ItemTransfer {
                    source: 2,
                    source_slot: 3,
                    destination: 1,
                    destination_slot: 0,
                    quantity: 1
                },
                &items
            )
            .is_err());
        assert_eq!(loaded, snapshot);
    }
}

#[cfg(test)]
mod preference_tests {
    use super::*;

    /// Returns:
    ///     Inventory: An empty cargo hold with a one-round missile magazine and two compatible variants.
    fn launcher(items: &ItemCatalog) -> Inventory {
        let mut inventory = Inventory::starter(items).unwrap();
        inventory.equipped[3] = Some(ItemInstance::stock(2834, 1));
        inventory.ammunition[3] = Some(ItemInstance::stock(752, 1));
        inventory.cargo[0] = Some(ItemInstance::stock(5430, 7));
        inventory.cargo[1] = Some(ItemInstance::stock(752, 4));
        inventory.cargo[1].as_mut().unwrap().builder_name = "Owned ammunition".into();
        inventory.cargo[1].as_mut().unwrap().quality = 1.5;
        inventory
    }

    /// Remembered exact templates never silently switch to another compatible damage variant.
    #[test]
    fn auto_reload_keeps_exact_damage_variant_and_owned_instance_metadata() {
        let items = ItemCatalog::load().unwrap();
        let mut inventory = launcher(&items);
        let expected = inventory.cargo[1].clone();
        inventory.consume_ammunition(3, &items).unwrap();
        assert_eq!(inventory.preferred_ammunition[3], Some(752));
        assert_eq!(inventory.ammunition[3], expected);
        assert!(inventory.cargo[1].is_none());
        assert_eq!(inventory.cargo[0].as_ref().unwrap().template, 5430);
        for _ in 0..4 {
            inventory.consume_ammunition(3, &items).unwrap();
        }
        assert!(inventory.ammunition[3].is_none());
        assert!(!inventory.reload_selected_ammunition(3, &items).unwrap());
        assert_eq!(inventory.cargo[0].as_ref().unwrap().quantity, 7);
    }

    /// Disabling automatic reload preserves the last selection until explicitly enabled again.
    #[test]
    fn disabled_reload_leaves_empty_and_enable_recovers_previous_selection() {
        let items = ItemCatalog::load().unwrap();
        let mut inventory = launcher(&items);
        inventory.auto_reload[3] = false;
        let owned = inventory.cargo.clone();
        inventory.consume_ammunition(3, &items).unwrap();
        assert!(inventory.ammunition[3].is_none());
        assert_eq!(inventory.cargo, owned);
        assert!(!inventory.reload_selected_ammunition(3, &items).unwrap());
        assert_eq!(inventory.preferred_ammunition[3], Some(752));
        inventory.auto_reload[3] = true;
        assert!(inventory.reload_selected_ammunition(3, &items).unwrap());
        assert_eq!(inventory.ammunition[3], owned[1]);
        assert!(!inventory.reload_selected_ammunition(3, &items).unwrap());
    }

    /// Explicit variant changes and unloads retain the actual selected template without moving unrelated ammo.
    #[test]
    fn manual_selection_and_unload_update_remembered_template() {
        let items = ItemCatalog::load().unwrap();
        let inventory = launcher(&items);
        let changed = inventory
            .moved(
                ItemTransfer {
                    source: 1,
                    source_slot: 0,
                    destination: 2,
                    destination_slot: 3,
                    quantity: 7,
                },
                &items,
            )
            .unwrap();
        assert_eq!(changed.preferred_ammunition[3], Some(5430));
        let unloaded = changed
            .moved(
                ItemTransfer {
                    source: 8,
                    source_slot: 3,
                    destination: 1,
                    destination_slot: 2,
                    quantity: 7,
                },
                &items,
            )
            .unwrap();
        assert_eq!(unloaded.preferred_ammunition[3], Some(5430));
        assert!(unloaded.ammunition[3].is_none());
        let mut reloaded = unloaded.clone();
        assert!(reloaded.reload_selected_ammunition(3, &items).unwrap());
        assert_eq!(reloaded.ammunition[3].as_ref().unwrap().template, 5430);
        assert_eq!(reloaded.equipped, inventory.equipped);
    }

    /// Existing inventory JSON defaults to enabled reload and new settings survive serialization.
    #[test]
    fn reload_preferences_have_legacy_defaults_and_persist_per_mount() {
        let items = ItemCatalog::load().unwrap();
        let inventory = launcher(&items);
        let mut old = serde_json::to_value(&inventory).unwrap();
        old.as_object_mut().unwrap().remove("auto_reload");
        old.as_object_mut().unwrap().remove("preferred_ammunition");
        let mut restored: Inventory = serde_json::from_value(old).unwrap();
        assert!(restored.auto_reload.iter().all(|value| *value));
        assert!(restored.preferred_ammunition.iter().all(Option::is_none));
        restored.auto_reload[3] = false;
        restored.preferred_ammunition[3] = Some(752);
        let durable: Inventory =
            serde_json::from_slice(&serde_json::to_vec(&restored).unwrap()).unwrap();
        assert_eq!(durable, restored);
        durable.validate(&items).unwrap();
    }
}
