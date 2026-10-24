//! Reconcile owned equipment with cached combat/movement parameters. All calls
//! run inside the authoritative player simulation, never from a socket worker.
use super::*;
use crate::items::{ItemCatalog, ItemTemplate, Modifier};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub(super) enum ActiveEffect {
    #[default]
    TurboWeapons,
    ScanBoost,
    SpeedBoost,
    ReactorBoost,
    RechargeReactor,
    RechargeShields,
    DeflectImpact,
    DeflectExplosive,
    DeflectPlasma,
    DeflectEnergy,
    DeflectEmp,
    DeflectChemical,
    ResistPsionic,
    TurboWarp,
    BeamHandling,
    ProjectileHandling,
    ShuntReactor,
}

impl ActiveEffect {
    /// Returns:
    ///     &str: Native effect name used in activation feedback.
    fn label(self) -> &'static str {
        match self {
            Self::TurboWeapons => "Turbo Weapons",
            Self::ScanBoost => "Scan Boost",
            Self::SpeedBoost => "Increase Speed",
            Self::ReactorBoost => "Reactor Boost",
            Self::RechargeReactor => "Recharge Reactor",
            Self::RechargeShields => "Recharge Shields",
            Self::DeflectImpact => "Deflect Impact",
            Self::DeflectExplosive => "Deflect Explosive",
            Self::DeflectPlasma => "Deflect Plasma",
            Self::DeflectEnergy => "Deflect Energy",
            Self::DeflectEmp => "Deflect EMP",
            Self::DeflectChemical => "Deflect Chemical",
            Self::ResistPsionic => "Resist Psionic",
            Self::TurboWarp => "Turbo Warp",
            Self::BeamHandling => "Improved Beam Handling",
            Self::ProjectileHandling => "Improved Projectile Handling",
            Self::ShuntReactor => "Shunt to Reactor",
        }
    }

    /// Returns:
    ///     (&str, u32, f32): Canonical stat, operation and maximum boost.
    fn rule(self) -> (&'static str, u32, f32) {
        match self {
            Self::TurboWeapons => ("STAT_WEAPON_TURBO", 1, 95.),
            Self::ScanBoost => ("STAT_SCAN_RANGE", 2, 100.),
            Self::SpeedBoost => ("STAT_ENGINE_TOP_SPEED", 1, 100.),
            Self::ReactorBoost => ("STAT_ENERGY", 1, 100.),
            Self::RechargeReactor => ("STAT_ENERGY_RECHARGE", 1, 1000.),
            Self::RechargeShields => ("STAT_SHIELD_RECHARGE", 2, 1000.),
            Self::DeflectImpact => ("STAT_IMPACT_DEFLECT", 1, 100.),
            Self::DeflectExplosive => ("STAT_EXPLOSIVE_DEFLECT", 1, 100.),
            Self::DeflectPlasma => ("STAT_PLASMA_DEFLECT", 1, 100.),
            Self::DeflectEnergy => ("STAT_ENERGY_DEFLECT", 1, 100.),
            Self::DeflectEmp => ("STAT_EMP_DEFLECT", 1, 100.),
            Self::DeflectChemical => ("STAT_CHEM_DEFLECT", 1, 100.),
            Self::ResistPsionic => ("STAT_PSIONIC_DEFLECT", 1, 100.),
            Self::TurboWarp => ("STAT_WARP", 1, 10_000.),
            Self::BeamHandling => ("STAT_BEAM_ACCURACY", 2, 100.),
            Self::ProjectileHandling => ("STAT_PROJECTILE_ACCURACY", 2, 100.),
            Self::ShuntReactor => ("STAT_ENERGY_RECHARGE", 2, 100.),
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub(super) struct ActiveDevice {
    pub slot: u8,
    pub template: u32,
    #[serde(default)]
    pub effect: ActiveEffect,
    #[serde(alias = "weapon_turbo_percent")]
    pub value: f32,
    #[serde(default)]
    pub operation: u32,
    #[serde(default)]
    pub secondary: Option<Modifier>,
    pub expires_at_ms: u64,
}

impl ActiveDevice {
    /// Returns:
    ///     u32: Stable native visual identity across a short server replacement.
    pub(super) fn visual_id(&self, player_id: u32) -> u32 {
        (self.expires_at_ms as u32) ^ player_id
    }

    /// Returns:
    ///     bool: Whether the frozen buff still belongs to equipped gear.
    pub fn valid_for(&self, inventory: &Inventory, items: &ItemCatalog, now_ms: u64) -> bool {
        let expected = items.get(self.template).ok().and_then(device_effect);
        (9..20).contains(&usize::from(self.slot))
            && self.value.is_finite()
            && expected.is_some_and(|(effect, value, operation, secondary, duration_ms)| {
                effect == self.effect
                    && (value - self.value).abs() < 0.001
                    && operation == self.effective_operation()
                    && modifier_matches(self.secondary.as_ref(), secondary.as_ref())
                    && self.expires_at_ms <= now_ms.saturating_add(duration_ms)
            })
            && self.expires_at_ms > now_ms
            && inventory.equipped[self.slot as usize]
                .as_ref()
                .is_some_and(|item| item.template == self.template && item.structure > 0.)
    }

    /// Returns:
    ///     Vec<Modifier>: Frozen self-buff applied through ordinary stat rules.
    pub fn modifiers(&self) -> Vec<Modifier> {
        let (stat, _, _) = self.effect.rule();
        let mut modifiers = vec![Modifier {
            stat: stat.into(),
            operation: self.effective_operation(),
            value: self.value,
        }];
        modifiers.extend(self.secondary.iter().cloned());
        modifiers
    }

    /// Returns:
    ///     u32: Stored operation, or the legacy default for old checkpoints.
    fn effective_operation(&self) -> u32 {
        if self.operation == 0 {
            self.effect.rule().1
        } else {
            self.operation
        }
    }
}

/// Returns:
///     bool: Whether a saved second modifier matches the public catalog.
fn modifier_matches(saved: Option<&Modifier>, catalog: Option<&Modifier>) -> bool {
    match (saved, catalog) {
        (None, None) => true,
        (Some(saved), Some(catalog)) => {
            saved.stat == catalog.stat
                && saved.operation == catalog.operation
                && (saved.value - catalog.value).abs() < 0.001
        }
        _ => false,
    }
}

/// Read supported self-buffs and their explicit public duration.
///
/// Returns:
///     Option<(ActiveEffect, f32, u32, Option<Modifier>, u64)>: Frozen effect rules.
fn device_effect(
    template: &ItemTemplate,
) -> Option<(ActiveEffect, f32, u32, Option<Modifier>, u64)> {
    if template.item_type != 11 || template.activated.len() != 1 {
        return None;
    }
    template.activated.iter().find_map(|effect| {
        let kind = match effect.description.as_str() {
            "Turbo Weapons (Activated)" => ActiveEffect::TurboWeapons,
            "Scan Boost (Activated)" => ActiveEffect::ScanBoost,
            "Increase Speed (Activated)" => ActiveEffect::SpeedBoost,
            "Reactor Boost (Activated)" => ActiveEffect::ReactorBoost,
            "Recharge Reactor (Activated)" => ActiveEffect::RechargeReactor,
            "Recharge Shields (Activated)" => ActiveEffect::RechargeShields,
            "Deflect Impact (Activated)" => ActiveEffect::DeflectImpact,
            "Deflect Explosive (Activated)" => ActiveEffect::DeflectExplosive,
            "Deflect Plasma (Activated)" => ActiveEffect::DeflectPlasma,
            "Deflect Energy (Activated)" => ActiveEffect::DeflectEnergy,
            "Deflect EMP (Activated)" => ActiveEffect::DeflectEmp,
            "Deflect Chemical (Activated)" => ActiveEffect::DeflectChemical,
            "Resist Psionic (Activated)" => ActiveEffect::ResistPsionic,
            "Turbo Warp (Activated)" => ActiveEffect::TurboWarp,
            "Improved Beam Handling (Activated)" => ActiveEffect::BeamHandling,
            "Improved Projectile Handling (Activated)" => ActiveEffect::ProjectileHandling,
            "Shunt to Reactor (Activated)" => ActiveEffect::ShuntReactor,
            _ => return None,
        };
        let (stat, default_operation, maximum) = kind.rule();
        let stat_modifiers = effect
            .modifiers
            .iter()
            .filter(|modifier| modifier.stat != "NO_STAT")
            .collect::<Vec<_>>();
        let modifier = stat_modifiers.first()?;
        let secondary_rule = match kind {
            ActiveEffect::BeamHandling => Some(("STAT_BEAM_DAMAGE", 2, 100.)),
            ActiveEffect::ProjectileHandling => Some(("STAT_PROJECTILES_DAMAGE", 2, 100.)),
            ActiveEffect::ShuntReactor => Some(("STAT_SHIELD_RECHARGE", 4, 100.)),
            _ => None,
        };
        let secondary = match (secondary_rule, stat_modifiers.get(1)) {
            (Some((stat, operation, maximum)), Some(modifier))
                if modifier.stat == stat
                    && modifier.operation == operation
                    && modifier.value.is_finite()
                    && (0. ..=maximum).contains(&modifier.value) =>
            {
                Some((*modifier).clone())
            }
            (None, None) => None,
            _ => return None,
        };
        if stat_modifiers.len() != 1 + usize::from(secondary.is_some()) {
            return None;
        }
        let operation = modifier.operation;
        if modifier.stat != stat
            || operation != default_operation
                && !(matches!(
                    kind,
                    ActiveEffect::RechargeReactor | ActiveEffect::RechargeShields
                ) && matches!(operation, 1 | 2))
        {
            return None;
        }
        let bonus = modifier.value;
        let seconds = effect
            .modifiers
            .iter()
            .find(|modifier| modifier.stat == "NO_STAT" && modifier.operation == 5)?
            .value;
        if !bonus.is_finite()
            || !(0. ..=maximum).contains(&bonus)
            || !seconds.is_finite()
            || !(1. ..=3600.).contains(&seconds)
        {
            return None;
        }
        Some((kind, bonus, operation, secondary, (seconds * 1000.) as u64))
    })
}

/// Returns:
///     Option<(f32, f32)>: Shield cost and reactor gain as catalog percentages.
fn reroute_shield_energy(template: &ItemTemplate) -> Option<(f32, f32)> {
    let effect = template.activated.first()?;
    if !matches!(template.item_type, 7 | 11)
        || template.activated.len() != 1
        || effect.description != "Reroute Shield Energy (Instant)"
        || effect.buff_name != "BUFF_NONE"
        || effect.visual != 0
        || template.activation.recharge != 0.
        || effect.modifiers.len() != 2
    {
        return None;
    }
    let shield = &effect.modifiers[0];
    let reactor = &effect.modifiers[1];
    if shield.stat != "STAT_SHIELD"
        || shield.operation != 4
        || !shield.value.is_finite()
        || !(0. ..=100.).contains(&shield.value)
        || reactor.stat != "STAT_ENERGY"
        || reactor.operation != 2
        || !reactor.value.is_finite()
        || !(0. ..=100.).contains(&reactor.value)
    {
        return None;
    }
    Some((shield.value, reactor.value))
}

impl PlayerSession {
    /// Publish the catalog buff name and visual for the current timed device.
    fn publish_active_device_presentation(&mut self, remaining_ms: u64) {
        let Some(active) = self.active_device.as_ref() else {
            return;
        };
        let Some(effect) = self
            .items
            .get(active.template)
            .ok()
            .and_then(|template| template.activated.first())
        else {
            return;
        };
        let name = if effect.buff_name.is_empty() {
            active.effect.label()
        } else {
            effect.buff_name.as_str()
        };
        let removal_tick = self.tick_ms().wrapping_add(remaining_ms as u32);
        self.pending.push(packets::ship_active_buff(
            self.player_id(),
            name,
            removal_tick,
        ));
        if let Ok(visual) = u16::try_from(effect.visual) {
            if visual != 0 {
                self.pending.push(packets::object_effect(
                    self.player_id(),
                    active.visual_id(self.player_id()),
                    visual,
                    self.tick_ms(),
                ));
            }
        }
    }

    pub(super) fn ship_resources(&self) -> packets::ShipResources {
        packets::ShipResources {
            energy: self.energy,
            shield: self.shield,
            hull: self.hull,
        }
    }

    pub(super) fn refresh_equipment(&mut self) -> io::Result<()> {
        let modifiers = self
            .active_device
            .as_ref()
            .filter(|active| {
                active.valid_for(&self.inventory, &self.items, crate::resources::unix_ms())
            })
            .map_or_else(Vec::new, ActiveDevice::modifiers);
        self.equipment = EquipmentStats::derive_with_activated(
            &self.inventory,
            &self.items,
            &self.skill_catalog,
            &modifiers,
        )?;
        self.reputation = self.factions.reactions(
            self.account.character.class(),
            &self.inventory.faction_standings,
        )?;
        self.apply_equipment_parameters();
        Ok(())
    }

    pub(super) fn apply_equipment_parameters(&mut self) {
        let stats = self.equipment.ship;
        self.energy = self.energy.min(stats.energy_capacity).max(0.);
        self.shield = self.shield.min(stats.shield_capacity).max(0.);
        self.hull = self.hull.min(stats.hull_capacity).max(0.);
        self.motion.max_speed = stats.impulse_speed;
        self.motion.acceleration = stats.acceleration;
        self.motion.turn_scale = stats.turn_rate / 45.;
        self.warp.speed = stats.warp_speed;
        self.warp.recovery_seconds = stats.warp_recovery;
        for slot in 0..20 {
            if !self
                .equipment
                .weapons
                .iter()
                .any(|weapon| weapon.slot == slot)
            {
                self.weapon_firing[slot] = false;
            }
        }
        self.player_firing = self.weapon_firing.iter().any(|firing| *firing);
        if stats.warp_speed == 0. && self.warp.active() {
            let event = self.warp.finish(FinishReason::Cancelled, &mut self.motion);
            self.emit_warp_event(event);
        }
        if !self.warp.active() {
            self.motion.speed = self
                .motion
                .speed
                .clamp(-stats.impulse_speed / 2., stats.impulse_speed);
            self.motion.set_speed = self
                .motion
                .set_speed
                .clamp(-stats.impulse_speed / 2., stats.impulse_speed);
        }
        if self.logged_in {
            self.pending.push(packets::ship_parameters(
                self.player_id(),
                &stats,
                self.ship_resources(),
            ));
            self.publish_weapon_states();
        }
    }

    pub(super) fn stop_weapons(&mut self) {
        self.player_firing = false;
        self.weapon_firing.fill(false);
        if self.logged_in {
            self.publish_weapon_states();
        }
    }

    pub(super) fn toggle_all_weapons(&mut self) {
        let enabled = !self.weapon_firing.iter().any(|firing| *firing);
        self.weapon_firing.fill(false);
        for weapon in &self.equipment.weapons {
            self.weapon_firing[weapon.slot] = enabled;
        }
        self.player_firing = enabled;
        self.publish_weapon_states();
    }

    pub(super) fn publish_weapon_states(&mut self) {
        let slots: Vec<usize> = self
            .equipment
            .weapons
            .iter()
            .map(|weapon| weapon.slot)
            .collect();
        for slot in slots {
            self.publish_weapon_state(slot);
        }
    }

    pub(super) fn publish_weapon_state(&mut self, slot: usize) {
        if let Some(weapon) = self
            .equipment
            .weapons
            .iter()
            .find(|weapon| weapon.slot == slot)
        {
            let cooling = self.weapon_cooldowns[slot] > 0.;
            let state = 0x2000
                | u32::from(self.weapon_firing[slot])
                | if cooling { 0x80 } else { 0 }
                | if weapon.item_type != 14 && weapon.ammunition.is_none() {
                    8
                } else {
                    0
                };
            let ready = if cooling {
                self.tick_ms()
                    .wrapping_add((self.weapon_cooldowns[slot] * 1000.) as u32)
            } else {
                0
            };
            self.pending.push(packets::weapon_state(
                self.player_id(),
                slot,
                ready,
                weapon.range,
                state,
            ));
        }
    }

    pub(crate) fn equip_cargo(&mut self, argument: &str) -> io::Result<()> {
        let slots = argument
            .split_whitespace()
            .map(str::parse::<usize>)
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| invalid("Use /equip CARGO_SLOT MOUNT_SLOT (zero-based slots)."))?;
        if slots.len() != 2 {
            return Err(invalid(
                "Use /equip CARGO_SLOT MOUNT_SLOT (zero-based slots).",
            ));
        }
        self.transfer_inventory(ItemTransfer {
            source: 1,
            source_slot: slots[0],
            destination: 2,
            destination_slot: slots[1],
            quantity: 1,
        })
    }

    pub(crate) fn unequip(&mut self, argument: &str) -> io::Result<()> {
        let slot = argument
            .parse::<usize>()
            .map_err(|_| invalid("Use /unequip MOUNT_SLOT (zero-based)."))?;
        let cargo = self
            .inventory
            .cargo
            .iter()
            .position(Option::is_none)
            .ok_or_else(|| invalid("Cargo is full."))?;
        self.transfer_inventory(ItemTransfer {
            source: 2,
            source_slot: slot,
            destination: 1,
            destination_slot: cargo,
            quantity: 1,
        })
    }

    pub(super) fn activate_equipped_item(&mut self, body: &[u8]) -> io::Result<()> {
        if body.len() != 6
            || !self.logged_in
            || self.hull <= 0.
            || self.warp.active()
            || self.sector.station.is_some()
        {
            return Err(invalid(
                "Equipped item unavailable in the current ship state.",
            ));
        }
        let mut reader = Reader::new(body);
        let owner = reader.u32()?;
        let inventory = reader.take(1)?[0];
        let slot = reader.take(1)?[0] as usize;
        if owner != self.player_id() || inventory != 2 || slot >= 20 {
            return Err(invalid("Invalid equipment activation owner or slot."));
        }
        if self
            .equipment
            .weapons
            .iter()
            .any(|weapon| weapon.slot == slot)
        {
            self.weapon_firing[slot] = !self.weapon_firing[slot];
            self.player_firing = self.weapon_firing.iter().any(|firing| *firing);
            self.publish_weapon_state(slot);
            if self.skills.cloak_rank > 0 {
                self.stop_cloak();
            }
        } else {
            let Some(item) = self.inventory.equipped[slot].as_ref() else {
                return Err(invalid("No equipped item in this mount."));
            };
            let template = self.items.get(item.template)?;
            if item.structure <= 0. {
                return Err(invalid("Damaged device cannot be activated."));
            }
            if let Some((shield_percent, reactor_percent)) = reroute_shield_energy(template) {
                let activation_cost = template.activation.energy;
                let shield_cost = self.equipment.ship.shield_capacity * shield_percent / 100.;
                let reactor_gain = self.equipment.ship.energy_capacity * reactor_percent / 100.;
                if self.energy < activation_cost {
                    self.pending.push(packets::message(
                        "Insufficient reactor energy for Reroute Shield Energy.",
                    ));
                    return Ok(());
                }
                if self.shield < shield_cost {
                    self.pending.push(packets::message(
                        "Insufficient shield energy for Reroute Shield Energy.",
                    ));
                    return Ok(());
                }
                let previous_energy = self.energy;
                let previous_shield = self.shield;
                self.shield -= shield_cost;
                self.energy = (self.energy - activation_cost + reactor_gain)
                    .min(self.equipment.ship.energy_capacity);
                if let Err(error) = self.checkpoint_session(true) {
                    self.energy = previous_energy;
                    self.shield = previous_shield;
                    return Err(error);
                }
                self.pending.push(packets::ship_resources(
                    self.player_id(),
                    &self.equipment.ship,
                    Some(self.energy),
                    Some(self.shield),
                    None,
                ));
                self.pending.push(packets::message(
                    "Reroute Shield Energy converted shields to reactor energy.",
                ));
                return Ok(());
            }
            let Some((effect, bonus, operation, secondary, duration_ms)) = device_effect(template)
            else {
                self.pending.push(packets::message(
                    "This item activation is not implemented yet.",
                ));
                return Ok(());
            };
            let now_ms = crate::resources::unix_ms();
            if self
                .active_device
                .as_ref()
                .is_some_and(|active| active.valid_for(&self.inventory, &self.items, now_ms))
            {
                self.pending.push(packets::message(&format!(
                    "{} is already active.",
                    self.active_device.as_ref().unwrap().effect.label()
                )));
                return Ok(());
            }
            if self.energy < template.activation.energy {
                self.pending.push(packets::message(&format!(
                    "Insufficient reactor energy for {}.",
                    effect.label()
                )));
                return Ok(());
            }
            let previous_energy = self.energy;
            let previous_active = self.active_device.clone();
            self.energy -= template.activation.energy;
            self.active_device = Some(ActiveDevice {
                slot: slot as u8,
                template: item.template,
                effect,
                value: bonus,
                operation,
                secondary,
                expires_at_ms: now_ms.saturating_add(duration_ms),
            });
            if let Err(error) = self.checkpoint_session(true) {
                self.energy = previous_energy;
                self.active_device = previous_active;
                return Err(error);
            }
            self.refresh_equipment()?;
            self.publish_active_device_presentation(duration_ms);
            self.pending.push(packets::message(&format!(
                "{} active for {} seconds.",
                effect.label(),
                duration_ms / 1000
            )));
        }
        Ok(())
    }

    /// Expire the self-buff before this tick's weapon firing is simulated.
    pub(super) fn advance_active_device(&mut self) -> io::Result<()> {
        if self.active_device.as_ref().is_some_and(|active| {
            !active.valid_for(&self.inventory, &self.items, crate::resources::unix_ms())
                || self.hull <= 0.
                || self.sector.station.is_some()
        }) {
            let active = self.active_device.take().unwrap();
            self.checkpoint_session(true)?;
            self.refresh_equipment()?;
            self.pending
                .push(packets::ship_active_buff(self.player_id(), "", 0));
            self.pending
                .push(packets::remove_effect(active.visual_id(self.player_id())));
            self.pending.push(packets::message(&format!(
                "{} ended.",
                active.effect.label()
            )));
        }
        Ok(())
    }

    /// Reaffirm a durable self-buff after a transport session resumes.
    pub(super) fn publish_active_device_resume(&mut self) {
        if let Some(active) = &self.active_device {
            let remaining = active
                .expires_at_ms
                .saturating_sub(crate::resources::unix_ms())
                .div_ceil(1000);
            let label = active.effect.label();
            self.publish_active_device_presentation(remaining * 1000);
            self.pending.push(packets::message(&format!(
                "{label} active for {remaining} seconds."
            )));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Every public reactor or device variant retains the bounded instant rule.
    #[test]
    fn public_shield_reroute_items_have_consistent_catalog_rules() {
        let items = ItemCatalog::load().unwrap();
        for id in [
            1933, 2015, 2107, 2197, 2288, 2391, 2482, 3285, 3309, 3323, 3337, 3346, 5884,
        ] {
            let (shield, reactor) = reroute_shield_energy(items.get(id).unwrap()).unwrap();
            assert!((41. ..=47.).contains(&shield), "item {id}");
            assert!((18. ..=24.).contains(&reactor), "item {id}");
        }
        assert!(reroute_shield_energy(items.get(1845).unwrap()).is_none());
    }

    #[test]
    fn catalog_recharge_operations_are_frozen_and_ambiguous_devices_stay_gated() {
        let items = ItemCatalog::load().unwrap();
        for id in [2265, 2363, 2412] {
            assert!(device_effect(items.get(id).unwrap()).is_some());
        }
        let (kind, value, operation, secondary, duration) =
            device_effect(items.get(1810).unwrap()).unwrap();
        assert_eq!(kind, ActiveEffect::RechargeReactor);
        assert!(secondary.is_none());
        assert_eq!((value, operation, duration), (4., 1, 240_000));
        let (kind, value, operation, secondary, duration) =
            device_effect(items.get(2490).unwrap()).unwrap();
        assert_eq!(kind, ActiveEffect::RechargeReactor);
        assert!(secondary.is_none());
        assert_eq!((value, operation, duration), (30., 2, 2_160_000));
        let (kind, value, operation, secondary, duration) =
            device_effect(items.get(1805).unwrap()).unwrap();
        assert_eq!(kind, ActiveEffect::RechargeShields);
        assert!(secondary.is_none());
        assert_eq!((value, operation, duration), (10., 2, 240_000));
        assert!(device_effect(items.get(1875).unwrap()).is_none());
    }

    #[test]
    /// Public dual-modifier devices preserve both combat/recharge operations.
    fn handling_and_shunt_catalog_effects_have_the_expected_second_modifier() {
        let items = ItemCatalog::load().unwrap();
        for (id, kind, stat, operation) in [
            (1925, ActiveEffect::BeamHandling, "STAT_BEAM_DAMAGE", 2),
            (
                1806,
                ActiveEffect::ProjectileHandling,
                "STAT_PROJECTILES_DAMAGE",
                2,
            ),
            (1950, ActiveEffect::ShuntReactor, "STAT_SHIELD_RECHARGE", 4),
        ] {
            let (effect, _, _, second, _) = device_effect(items.get(id).unwrap()).unwrap();
            assert_eq!(effect, kind);
            let second = second.unwrap();
            assert_eq!((second.stat.as_str(), second.operation), (stat, operation));
        }
        let (kind, value, operation, second, duration) =
            device_effect(items.get(1836).unwrap()).unwrap();
        assert_eq!(kind, ActiveEffect::TurboWarp);
        assert_eq!((value, operation, duration), (750., 1, 480_000));
        assert!(second.is_none());
    }
}
