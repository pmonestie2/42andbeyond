//! Recipient-side Reactor Optimization status, stats and durable cast handoff.
use super::*;
use crate::reactor_optimization::{PendingReactorOptimization, ReactorOptimizationBuff, BUFF_SLOT};

impl PlayerSession {
    /// Returns:
    ///     u64: Latest Reactor Optimization world revision applied to this pilot.
    pub(crate) fn reactor_optimization_revision(&self) -> u64 {
        self.reactor_optimization_revision
    }

    /// Returns:
    ///     Option<PendingReactorOptimization>: Completed cast awaiting world settlement.
    pub(crate) fn pending_reactor_optimization(&self) -> Option<PendingReactorOptimization> {
        self.skills.pending_reactor_optimization.clone()
    }

    /// Returns:
    ///     bool: Whether this pilot can cast or receive a reactor status in space.
    pub(crate) fn reactor_optimization_available(&self) -> bool {
        self.logged_in && self.hull > 0. && self.sector.station.is_none() && !self.warp.active()
    }

    /// Returns:
    ///     bool: Whether this selected recipient remains visible to the caster.
    pub(crate) fn reactor_optimization_contact(&self, target: u32) -> bool {
        target == self.player_id() || self.peer_contacts.contains_key(&target)
    }

    /// Clear an already settled cast without opening another delivery window.
    pub(crate) fn clear_pending_reactor_optimization(&mut self) -> io::Result<()> {
        let previous = self.skills.pending_reactor_optimization.take();
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.pending_reactor_optimization = previous;
            return Err(error);
        }
        Ok(())
    }

    /// Apply one committed status to the recipient before announcing success.
    pub(crate) fn apply_reactor_optimization_buff(
        &mut self,
        buff: ReactorOptimizationBuff,
    ) -> io::Result<()> {
        if buff.revision <= self.reactor_optimization_revision {
            return Ok(());
        }
        let prior = self.reactor_optimization_buff.clone();
        let prior_revision = self.reactor_optimization_revision;
        self.reactor_optimization_revision = buff.revision;
        let accepted = buff.valid(crate::resources::unix_ms(), self.session_epoch)
            && self.hull > 0.
            && self.sector.station.is_none();
        self.reactor_optimization_buff = accepted.then_some(buff);
        if let Err(error) = self.checkpoint_session(true) {
            self.reactor_optimization_buff = prior;
            self.reactor_optimization_revision = prior_revision;
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
        self.publish_reactor_optimization_resume();
        Ok(())
    }

    /// Replay the native timed status after a short server replacement.
    pub(super) fn publish_reactor_optimization_resume(&mut self) {
        let Some(buff) = self.reactor_optimization_buff.as_ref() else {
            return;
        };
        let remaining = buff
            .expires_at_ms
            .saturating_sub(crate::resources::unix_ms());
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            BUFF_SLOT,
            "Reactor_Optimization",
            self.tick_ms().wrapping_add(remaining as u32),
        ));
        self.message(&format!(
            "Reactor Optimization active for {} seconds.",
            remaining.div_ceil(1000)
        ));
    }

    /// Remove an expired recipient status and its authoritative ship bonuses.
    pub(super) fn advance_reactor_optimization_buff(&mut self) -> io::Result<()> {
        let expired = self.reactor_optimization_buff.as_ref().is_some_and(|buff| {
            !buff.valid(crate::resources::unix_ms(), self.session_epoch)
                || self.hull <= 0.
                || self.sector.station.is_some()
        });
        if expired {
            let previous = self.reactor_optimization_buff.take();
            if let Err(error) = self.checkpoint_session(true) {
                self.reactor_optimization_buff = previous;
                return Err(error);
            }
            self.refresh_equipment()?;
            self.pending.push(packets::ship_active_buff_slot(
                self.player_id(),
                BUFF_SLOT,
                "",
                0,
            ));
            self.message("Reactor Optimization ended.");
        }
        Ok(())
    }
}
