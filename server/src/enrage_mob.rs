//! MOB-side Enrage target priority and hit penalty.
use super::*;
use crate::enrage::EnrageEffect;

impl Mob {
    /// Returns:
    ///     Option<&EnrageEffect>: Latest active cast affecting this MOB.
    pub fn active_enrage(&self) -> Option<&EnrageEffect> {
        let now = crate::resources::unix_ms();
        self.enrage
            .iter()
            .filter(|effect| effect.active(now, self.death_generation))
            .max_by_key(|effect| effect.expires_at_ms)
    }

    /// Returns:
    ///     bool: Whether one durable cast was added to this living MOB.
    pub fn apply_enrage(&mut self, effect: EnrageEffect) -> bool {
        if self.state == MobState::Dead
            || !effect.valid()
            || effect.target_generation != self.death_generation
            || self.enrage.iter().any(|existing| {
                existing.source == effect.source
                    && existing.source_epoch == effect.source_epoch
                    && existing.effect == effect.effect
            })
        {
            return false;
        }
        self.enrage
            .retain(|existing| existing.active(crate::resources::unix_ms(), self.death_generation));
        if self.enrage.len() >= 16 {
            return false;
        }
        self.enrage.push(effect);
        true
    }

    /// Returns:
    ///     Option<usize>: Most recent visible caster who currently forces aggro.
    pub(super) fn enraged_target(&self, players: &[PlayerThreat]) -> Option<usize> {
        let now = crate::resources::unix_ms();
        self.enrage
            .iter()
            .filter(|effect| effect.active(now, self.death_generation))
            .filter_map(|effect| {
                players
                    .iter()
                    .position(|player| {
                        player.id == effect.source
                            && player.session_epoch == effect.source_epoch
                            && player.visible
                            && mob_detects_cloak(self.definition.level, player.stealth)
                    })
                    .map(|index| (effect.expires_at_ms, index))
            })
            .max_by_key(|(deadline, _)| *deadline)
            .map(|(_, index)| index)
    }

    /// Returns:
    ///     f32: Strongest active beam/projectile accuracy fraction.
    pub(super) fn enrage_accuracy_multiplier(&self) -> f32 {
        let now = crate::resources::unix_ms();
        self.enrage
            .iter()
            .filter(|effect| effect.active(now, self.death_generation))
            .map(|effect| effect.accuracy_multiplier)
            .fold(1., f32::min)
    }
}
