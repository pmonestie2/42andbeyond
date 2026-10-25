//! Recipient-side Rally stats, native status icon and durable cast handoff.
use super::*;
use crate::rally::{PendingRally, RallyBuff};

impl PlayerSession {
    /// Returns:
    ///     u64: Latest Rally world revision applied to this session.
    pub(crate) fn rally_revision(&self) -> u64 {
        self.rally_revision
    }

    /// Returns:
    ///     Option<PendingRally>: Completed Rally waiting for atomic group delivery.
    pub(crate) fn pending_rally(&self) -> Option<PendingRally> {
        self.skills.pending_rally.clone()
    }

    /// Returns:
    ///     bool: Whether this pilot can receive Rally now.
    pub(crate) fn rally_available(&self) -> bool {
        self.logged_in && self.hull > 0. && self.sector.station.is_none() && !self.warp.active()
    }

    /// Clear an already settled Rally without reopening its replay window.
    pub(crate) fn clear_pending_rally(&mut self) -> io::Result<()> {
        self.skills.pending_rally = None;
        self.checkpoint_session(true)
    }

    /// Apply a committed recipient status before announcing it to the client.
    pub(crate) fn apply_rally_buff(&mut self, buff: RallyBuff) -> io::Result<()> {
        if buff.revision <= self.rally_revision {
            return Ok(());
        }
        if let Some(previous) = self.rally_buff.as_ref() {
            self.pending
                .push(packets::ship_active_buff_slot(self.player_id(), 5, "", 0));
            self.pending
                .push(packets::remove_effect(previous.visual_id(self.player_id())));
        }
        self.rally_revision = buff.revision;
        let accepted = buff.valid(crate::resources::unix_ms(), self.session_epoch)
            && self.hull > 0.
            && self.sector.station.is_none();
        self.rally_buff = accepted.then_some(buff.clone());
        let checkpoint = self.checkpoint_session(true);
        self.refresh_equipment()?;
        if accepted {
            self.publish_rally_resume();
        }
        checkpoint
    }

    /// Replay the active Rally status after a short session replacement.
    pub(super) fn publish_rally_resume(&mut self) {
        let Some(buff) = self.rally_buff.as_ref() else {
            return;
        };
        let remaining = buff
            .expires_at_ms
            .saturating_sub(crate::resources::unix_ms());
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            5,
            buff.tactic.buff_type(),
            self.tick_ms().wrapping_add(remaining as u32),
        ));
        self.pending.push(packets::object_effect(
            self.player_id(),
            buff.visual_id(self.player_id()),
            buff.tactic.visual_descriptor(),
            self.tick_ms(),
        ));
        self.pending.push(packets::message(&format!(
            "Rally {} active for {} seconds.",
            buff.tactic.label(),
            remaining.div_ceil(1000)
        )));
    }

    /// Expire Rally's weapon and ship modifiers with its native icon.
    pub(super) fn advance_rally_buff(&mut self) -> io::Result<()> {
        let expired = self.rally_buff.as_ref().is_some_and(|buff| {
            !buff.valid(crate::resources::unix_ms(), self.session_epoch)
                || self.hull <= 0.
                || self.sector.station.is_some()
        });
        if expired {
            let previous = self.rally_buff.take().unwrap();
            let checkpoint = self.checkpoint_session(true);
            self.refresh_equipment()?;
            self.pending
                .push(packets::ship_active_buff_slot(self.player_id(), 5, "", 0));
            self.pending
                .push(packets::remove_effect(previous.visual_id(self.player_id())));
            self.pending.push(packets::message("Rally ended."));
            checkpoint?;
        }
        Ok(())
    }
}
