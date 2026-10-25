//! Durable generation-bound MOB subsystem and weapon hack state.
use super::*;
use crate::hacking::{HackEffect, ShipSystem};

impl Mob {
    /// Returns:
    ///     bool: Whether one active hack disables the requested subsystem.
    pub fn hacked_system(&self, system: ShipSystem) -> bool {
        let now = crate::resources::unix_ms();
        self.hacks
            .iter()
            .any(|hack| hack.active(now, self.death_generation) && hack.system == Some(system))
    }

    /// Returns:
    ///     bool: Whether a hack disables the MOB's only simulated weapon mount.
    pub fn hacked_weapon(&self) -> bool {
        let now = crate::resources::unix_ms();
        self.hacks
            .iter()
            .any(|hack| hack.active(now, self.death_generation) && hack.weapon_slot == Some(0))
    }

    /// Returns:
    ///     bool: Whether hacked communications prevent native warning calls.
    pub fn hacked_communications(&self) -> bool {
        let now = crate::resources::unix_ms();
        self.hacks
            .iter()
            .any(|hack| hack.active(now, self.death_generation) && hack.jam_communications)
    }

    /// Returns:
    ///     Option<&HackEffect>: Latest visible native hack status on this MOB.
    pub fn active_hack_visual(&self) -> Option<&HackEffect> {
        let now = crate::resources::unix_ms();
        self.hacks
            .iter()
            .filter(|hack| hack.active(now, self.death_generation))
            .max_by_key(|hack| (hack.deadline_ms, hack.effect))
    }

    /// Returns:
    ///     bool: Whether a new successful attempt was accepted once.
    pub fn apply_hack(&mut self, effect: HackEffect) -> bool {
        if self.state == MobState::Dead
            || !effect.valid()
            || !effect.active(crate::resources::unix_ms(), self.death_generation)
            || self.hacks.iter().any(|existing| {
                existing.source == effect.source
                    && existing.source_epoch == effect.source_epoch
                    && existing.effect == effect.effect
            })
        {
            return false;
        }
        self.hacks
            .retain(|existing| existing.active(crate::resources::unix_ms(), self.death_generation));
        if self.hacks.len() == 16 {
            return false;
        }
        if effect.system == Some(ShipSystem::Engine) {
            self.stop_motion();
        }
        self.hacks.push(effect);
        true
    }
}
