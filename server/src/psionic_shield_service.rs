//! Recipient Psionic Shield status and durable cast handoff.
use super::*;
use crate::psionic_shield::{
    PendingPsionicShield, PsionicShieldBuff, BUFF_SLOT, VISUAL_DESCRIPTOR,
};

impl PlayerSession {
    /// Returns:
    ///     u64: Last Psionic Shield world revision applied to this pilot.
    pub(crate) fn psionic_shield_revision(&self) -> u64 {
        self.psionic_shield_revision
    }

    /// Returns:
    ///     Option<f32>: Current authoritative absorption budget, if active.
    #[cfg(test)]
    pub(crate) fn psionic_shield_balance(&self) -> Option<f32> {
        self.psionic_shield_buff.as_ref().map(|buff| buff.remaining)
    }

    /// Returns:
    ///     Option<PendingPsionicShield>: Charged cast awaiting world settlement.
    pub(crate) fn pending_psionic_shield(&self) -> Option<PendingPsionicShield> {
        self.skills.pending_psionic_shield.clone()
    }

    /// Returns:
    ///     bool: Whether this pilot can cast or receive a barrier in space.
    pub(crate) fn psionic_shield_available(&self) -> bool {
        self.logged_in
            && self.hull > 0.
            && self.sector.station.is_none()
            && !self.warp.active()
            && self.gate_transit.is_none()
    }

    /// Returns:
    ///     bool: Whether a selected recipient remains visible to this pilot.
    pub(crate) fn psionic_shield_contact(&self, target: u32) -> bool {
        target == self.player_id() || self.peer_contacts.contains_key(&target)
    }

    /// Clear a settled cast only after its session receipt is durable.
    pub(crate) fn clear_pending_psionic_shield(&mut self) -> io::Result<()> {
        let prior = self.skills.pending_psionic_shield.take();
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.pending_psionic_shield = prior;
            return Err(error);
        }
        Ok(())
    }

    /// Apply one world-committed barrier without refilling a spent revision.
    pub(crate) fn apply_psionic_shield_buff(&mut self, buff: PsionicShieldBuff) -> io::Result<()> {
        if buff.revision <= self.psionic_shield_revision {
            return Ok(());
        }
        let prior = self.psionic_shield_buff.clone();
        let prior_revision = self.psionic_shield_revision;
        self.psionic_shield_revision = buff.revision;
        self.psionic_shield_buff = (buff.valid(crate::resources::unix_ms(), self.session_epoch)
            && self.hull > 0.
            && self.sector.station.is_none())
        .then_some(buff);
        if let Err(error) = self.checkpoint_session(true) {
            self.psionic_shield_buff = prior;
            self.psionic_shield_revision = prior_revision;
            return Err(error);
        }
        self.refresh_equipment()?;
        if let Some(prior) = prior {
            self.pending.push(packets::remove_effect(prior.effect));
            self.pending.push(packets::ship_active_buff_slot(
                self.player_id(),
                BUFF_SLOT,
                "",
                0,
            ));
        }
        self.publish_psionic_shield_resume();
        Ok(())
    }

    /// Replay one native icon and verified object visual after replacement.
    pub(super) fn publish_psionic_shield_resume(&mut self) {
        let Some(buff) = self.psionic_shield_buff.as_ref() else {
            return;
        };
        let remaining_ms = buff
            .expires_at_ms
            .saturating_sub(crate::resources::unix_ms());
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            BUFF_SLOT,
            "Psionic_Shield",
            self.tick_ms().wrapping_add(remaining_ms as u32),
        ));
        self.pending.push(packets::object_effect(
            self.player_id(),
            buff.effect,
            VISUAL_DESCRIPTOR,
            self.tick_ms(),
        ));
        self.message(&format!(
            "Psionic Shield active: {:.0} damage absorption remaining.",
            buff.remaining
        ));
    }

    /// Remove a timed-out barrier and its psionic resistance.
    pub(super) fn advance_psionic_shield_buff(&mut self) -> io::Result<()> {
        let expired = self.psionic_shield_buff.as_ref().is_some_and(|buff| {
            !buff.valid(crate::resources::unix_ms(), self.session_epoch)
                || self.hull <= 0.
                || self.sector.station.is_some()
        });
        if !expired {
            return Ok(());
        }
        let prior = self.psionic_shield_buff.take();
        if let Err(error) = self.checkpoint_session(true) {
            self.psionic_shield_buff = prior;
            return Err(error);
        }
        self.refresh_equipment()?;
        if let Some(prior) = prior {
            self.pending.push(packets::remove_effect(prior.effect));
            self.pending.push(packets::ship_active_buff_slot(
                self.player_id(),
                BUFF_SLOT,
                "",
                0,
            ));
            self.message("Psionic Shield ended.");
        }
        Ok(())
    }
}
