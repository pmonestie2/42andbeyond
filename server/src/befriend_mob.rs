//! Durable per-pilot MOB reaction and higher-tier calming.
use super::*;
use crate::befriend::BefriendEffect;

impl Mob {
    /// Returns:
    ///     bool: Whether this MOB currently treats that pilot as a friend.
    pub fn befriended(&self, player: u32, epoch: u64) -> bool {
        let now = crate::resources::unix_ms();
        self.befriend
            .iter()
            .any(|effect| effect.befriends(player, epoch, now, self.death_generation))
    }

    /// Returns:
    ///     f32: Strongest active calming range reduction.
    pub fn calm_fraction(&self) -> f32 {
        let now = crate::resources::unix_ms();
        self.befriend
            .iter()
            .filter(|effect| effect.active(now, self.death_generation))
            .map(BefriendEffect::calm_fraction)
            .fold(1., f32::min)
    }

    /// Returns:
    ///     f32: Strongest typed Psionic vulnerability on this MOB.
    pub fn psionic_vulnerability(&self) -> f32 {
        let now = crate::resources::unix_ms();
        self.befriend
            .iter()
            .filter(|effect| effect.active(now, self.death_generation))
            .map(BefriendEffect::psionic_vulnerability)
            .fold(0., f32::max)
    }

    /// Returns:
    ///     Option<&BefriendEffect>: Latest active native visual receipt.
    pub fn active_befriend_visual(&self) -> Option<&BefriendEffect> {
        let now = crate::resources::unix_ms();
        self.befriend
            .iter()
            .filter(|effect| effect.active(now, self.death_generation))
            .max_by_key(|effect| (effect.deadline_ms, effect.effect))
    }

    /// Returns:
    ///     bool: Whether a new successful cast altered this MOB once.
    pub fn apply_befriend(&mut self, effect: BefriendEffect) -> bool {
        if self.state == MobState::Dead
            || !effect.active(crate::resources::unix_ms(), self.death_generation)
            || self.befriend.iter().any(|existing| {
                existing.source == effect.source
                    && existing.source_epoch == effect.source_epoch
                    && existing.effect == effect.effect
            })
        {
            return false;
        }
        self.befriend
            .retain(|existing| existing.active(crate::resources::unix_ms(), self.death_generation));
        if self.befriend.len() == 16 {
            return false;
        }
        self.aggro.remove(effect.source);
        self.warnings.forget_player(effect.source);
        self.befriend.push(effect);
        true
    }
}
