//! Generation-bound organic weapon and warning suppression.
use super::*;
use crate::biorepression::BioEffect;

impl Mob {
    /// Returns:
    ///     f32: Strongest active reload multiplier on this MOB life.
    pub fn bio_reload_factor(&self) -> f32 {
        let now = crate::resources::unix_ms();
        self.bio
            .iter()
            .filter(|effect| effect.active(now, self.death_generation))
            .map(BioEffect::reload_factor)
            .fold(1., f32::max)
    }

    /// Returns:
    ///     f32: Remaining probability of a warning call during suppression.
    pub fn bio_skill_use_fraction(&self) -> f32 {
        let now = crate::resources::unix_ms();
        self.bio
            .iter()
            .filter(|effect| effect.active(now, self.death_generation))
            .map(BioEffect::skill_use_fraction)
            .fold(1., f32::min)
    }

    /// Returns:
    ///     bool: Whether the single simulated MOB weapon is disabled.
    pub fn bio_weapon_disabled(&self) -> bool {
        let now = crate::resources::unix_ms();
        self.bio.iter().any(|effect| {
            effect.active(now, self.death_generation) && effect.weapon_slot == Some(0)
        })
    }

    /// Returns:
    ///     bool: Whether one successful cast changed this MOB generation once.
    pub fn apply_bio(&mut self, effect: BioEffect) -> bool {
        if self.state == MobState::Dead
            || !crate::biorepression::is_organic(self.definition.mob_type)
            || !effect.active(crate::resources::unix_ms(), self.death_generation)
            || self.bio.iter().any(|prior| {
                prior.source == effect.source
                    && prior.source_epoch == effect.source_epoch
                    && prior.effect == effect.effect
            })
        {
            return false;
        }
        self.bio
            .retain(|prior| prior.active(crate::resources::unix_ms(), self.death_generation));
        if self.bio.len() == 16 {
            return false;
        }
        self.bio.push(effect);
        true
    }
}
