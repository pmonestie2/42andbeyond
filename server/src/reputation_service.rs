//! Session publication and faction-aware NPC attitude. Mutable standings live
//! with cargo/credits so future mission/combat awards can commit them together.
use super::*;

impl PlayerSession {
    pub(super) fn publish_reputation(&mut self) {
        if self.logged_in {
            self.pending
                .push(packets::reputation(&self.factions, &self.reputation));
            self.pending.push(packets::ship_faction(
                self.player_id(),
                &self.reputation.identifier,
            ));
        }
    }
    pub(super) fn mob_relationship(&self, mob: &Mob) -> u32 {
        if mob.befriended(self.player_id(), self.session_epoch) {
            return 2;
        }
        self.reputation
            .relationship(mob.definition.faction, mob.targets(self.player_id()))
    }
}
