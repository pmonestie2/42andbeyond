//! Recipient-side Shield Charging status, stats and durable cast handoff.
use super::*;
use crate::shield_charging::{PendingShieldCharging, ShieldChargingBuff, BUFF_SLOT};

impl PlayerSession {
    /// Returns:
    ///     u64: Latest Shield Charging world revision applied to this pilot.
    pub(crate) fn shield_charging_revision(&self) -> u64 {
        self.shield_charging_revision
    }

    /// Returns:
    ///     Option<PendingShieldCharging>: Completed cast awaiting world settlement.
    pub(crate) fn pending_shield_charging(&self) -> Option<PendingShieldCharging> {
        self.skills.pending_shield_charging.clone()
    }

    /// Returns:
    ///     bool: Whether this pilot can cast or receive a shield status in space.
    pub(crate) fn shield_charging_available(&self) -> bool {
        self.logged_in && self.hull > 0. && self.sector.station.is_none() && !self.warp.active()
    }

    /// Returns:
    ///     bool: Whether this selected recipient remains visible to the caster.
    pub(crate) fn shield_charging_contact(&self, target: u32) -> bool {
        target == self.player_id() || self.peer_contacts.contains_key(&target)
    }

    /// Clear an already settled cast without opening another delivery window.
    pub(crate) fn clear_pending_shield_charging(&mut self) -> io::Result<()> {
        let previous = self.skills.pending_shield_charging.take();
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.pending_shield_charging = previous;
            return Err(error);
        }
        Ok(())
    }

    /// Apply one committed status to the recipient before announcing success.
    pub(crate) fn apply_shield_charging_buff(
        &mut self,
        buff: ShieldChargingBuff,
    ) -> io::Result<()> {
        if buff.revision <= self.shield_charging_revision {
            return Ok(());
        }
        let prior = self.shield_charging_buff.clone();
        let prior_revision = self.shield_charging_revision;
        self.shield_charging_revision = buff.revision;
        let accepted = buff.valid(crate::resources::unix_ms(), self.session_epoch)
            && self.hull > 0.
            && self.sector.station.is_none();
        self.shield_charging_buff = accepted.then_some(buff);
        if let Err(error) = self.checkpoint_session(true) {
            self.shield_charging_buff = prior;
            self.shield_charging_revision = prior_revision;
            return Err(error);
        }
        self.refresh_equipment()?;
        if prior.is_some() {
            self.pending.push(packets::ship_active_buff_slot(
                self.player_id(),
                BUFF_SLOT,
                "",
                0,
            ));
        }
        self.publish_shield_charging_resume();
        Ok(())
    }

    /// Replay the native timed status after a short server replacement.
    pub(super) fn publish_shield_charging_resume(&mut self) {
        let Some(buff) = self.shield_charging_buff.as_ref() else {
            return;
        };
        let remaining = buff
            .expires_at_ms
            .saturating_sub(crate::resources::unix_ms());
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            BUFF_SLOT,
            "Shield_Charging",
            self.tick_ms().wrapping_add(remaining as u32),
        ));
        self.message(&format!(
            "Shield Charging active for {} seconds.",
            remaining.div_ceil(1000)
        ));
    }

    /// Remove an expired recipient status and its authoritative ship bonuses.
    pub(super) fn advance_shield_charging_buff(&mut self) -> io::Result<()> {
        let expired = self.shield_charging_buff.as_ref().is_some_and(|buff| {
            !buff.valid(crate::resources::unix_ms(), self.session_epoch)
                || self.hull <= 0.
                || self.sector.station.is_some()
        });
        if expired {
            let previous = self.shield_charging_buff.take();
            if let Err(error) = self.checkpoint_session(true) {
                self.shield_charging_buff = previous;
                return Err(error);
            }
            self.refresh_equipment()?;
            self.pending.push(packets::ship_active_buff_slot(
                self.player_id(),
                BUFF_SLOT,
                "",
                0,
            ));
            self.message("Shield Charging ended.");
        }
        Ok(())
    }
}
