//! Recipient-owned targeted friendly-device presentation and stat reconciliation.
use super::*;
use crate::peer_buffs::{friendly_offer, FriendlyOffer, PeerBuff};

impl PlayerSession {
    /// Returns:
    ///     u64: Last targeted status revision saved in this session.
    pub(crate) fn peer_buff_revision(&self) -> u64 {
        self.peer_buff_revision
    }

    /// Returns:
    ///     FriendlyOffer: Selected visible peer, legal mount and available energy.
    pub(crate) fn peer_friendly_offer(&self, target: u32, slot: u8) -> io::Result<FriendlyOffer> {
        if !self.logged_in
            || self.hull <= 0.
            || self.sector.station.is_some()
            || self.warp.active()
            || target == self.player_id()
            || target != self.target
            || !self.peer_ids.contains(&target)
            || !(9..20).contains(&usize::from(slot))
        {
            return Err(invalid(
                "Select a visible friendly ship before activating this device.",
            ));
        }
        let item = self.inventory.equipped[usize::from(slot)]
            .as_ref()
            .ok_or_else(|| invalid("No equipped friendly device in this mount."))?;
        if item.structure <= 0. {
            return Err(invalid("Damaged friendly device cannot be activated."));
        }
        let offer = friendly_offer(self.items.get(item.template)?)
            .ok_or_else(|| invalid("This device has no supported friendly effect."))?;
        if self.energy < offer.cost {
            return Err(invalid("Insufficient reactor energy for friendly device."));
        }
        Ok(offer)
    }

    /// Returns:
    ///     u32: Public item template mounted for this friendly device request.
    /// Raises:
    ///     io::Error: The requested device mount is empty or invalid.
    pub(crate) fn peer_friendly_template(&self, slot: u8) -> io::Result<u32> {
        self.inventory
            .equipped
            .get(usize::from(slot))
            .and_then(Option::as_ref)
            .map(|item| item.template)
            .ok_or_else(|| invalid("No equipped friendly device in this mount."))
    }

    /// Returns:
    ///     bool: Whether this recipient can accept another ally effect.
    pub(crate) fn peer_buff_available(&self) -> bool {
        self.logged_in && self.hull > 0. && self.sector.station.is_none() && !self.warp.active()
    }

    /// Apply a durable ledger revision to this recipient before presentation.
    pub(crate) fn apply_peer_buff(&mut self, buff: PeerBuff) -> io::Result<()> {
        if buff.revision <= self.peer_buff_revision {
            return Ok(());
        }
        let previous = self
            .peer_buffs
            .iter()
            .find(|previous| previous.effect == buff.effect)
            .cloned();
        if let Some(previous) = previous.as_ref() {
            self.remove_peer_buff_presentation(previous);
        }
        self.peer_buffs
            .retain(|previous| previous.effect != buff.effect);
        self.peer_buff_revision = buff.revision;
        let accepted = buff.valid(&self.items, crate::resources::unix_ms(), self.session_epoch);
        if accepted {
            self.peer_buffs.push(buff.clone());
            self.peer_buffs.sort_by_key(|existing| existing.revision);
        }
        let checkpoint = self.checkpoint_session(true);
        self.refresh_equipment()?;
        if accepted {
            self.publish_one_peer_buff(&buff);
        }
        checkpoint
    }

    /// Reaffirm a targeted friendly buff after a short transport replacement.
    pub(super) fn publish_peer_buff_resume(&mut self) {
        for buff in self.peer_buffs.clone() {
            self.publish_one_peer_buff(&buff);
        }
    }

    /// Replay one native status icon, visual and duration message.
    fn publish_one_peer_buff(&mut self, buff: &PeerBuff) {
        let Ok(template) = self.items.get(buff.template) else {
            return;
        };
        let Some(effect) = template.activated.first() else {
            return;
        };
        let remaining = buff
            .expires_at_ms
            .saturating_sub(crate::resources::unix_ms());
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            buff.effect.slot(),
            &effect.buff_name,
            self.tick_ms().wrapping_add(remaining as u32),
        ));
        if let Some(visual) = friendly_offer(template)
            .map(|offer| offer.visual)
            .filter(|id| *id != 0)
        {
            self.pending.push(packets::object_effect(
                self.player_id(),
                buff.visual_id(self.player_id()),
                visual,
                self.tick_ms(),
            ));
        }
        self.pending.push(packets::message(&format!(
            "{} received for {} seconds.",
            buff.effect.label(),
            remaining.div_ceil(1000)
        )));
    }

    /// Remove the native icon and object visual for an old recipient buff.
    pub(super) fn remove_peer_buff_presentation(&mut self, buff: &PeerBuff) {
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            buff.effect.slot(),
            "",
            0,
        ));
        if self
            .items
            .get(buff.template)
            .ok()
            .and_then(friendly_offer)
            .is_some_and(|offer| offer.visual != 0)
        {
            self.pending
                .push(packets::remove_effect(buff.visual_id(self.player_id())));
        }
    }

    /// Expire target-derived stats and their native presentation together.
    pub(super) fn advance_peer_buff(&mut self) -> io::Result<()> {
        let now = crate::resources::unix_ms();
        let mut ended = Vec::new();
        self.peer_buffs.retain(|buff| {
            if !buff.valid(&self.items, now, self.session_epoch)
                || self.hull <= 0.
                || self.sector.station.is_some()
            {
                ended.push(buff.clone());
                false
            } else {
                true
            }
        });
        if !ended.is_empty() {
            let checkpoint = self.checkpoint_session(true);
            self.refresh_equipment()?;
            for old in ended {
                self.remove_peer_buff_presentation(&old);
                self.pending
                    .push(packets::message(&format!("{} ended.", old.effect.label())));
            }
            checkpoint?;
        }
        Ok(())
    }
}
