//! Timed Shield Inversion pulses across the durable combat and player ledgers.
use super::*;

impl GameState {
    /// Commit each due damage pulse before charging the caster's shield.
    pub(super) fn deliver_shield_inversions(&mut self, now_ms: u64) -> io::Result<()> {
        let sources: Vec<u32> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| session.active_inversion().map(|_| *id))
            .collect();
        for source in sources {
            let caster = &self.sessions[&source];
            let active = caster.active_inversion().unwrap();
            if !active.valid() || active.source != source {
                return Err(invalid("Invalid saved Shield Inversion cast."));
            }
            let Some(combat) = self.combats.get(&active.sector).cloned() else {
                return Err(invalid("Shield Inversion sector is unavailable."));
            };
            // A prior world commit always wins over expiry, departure or a
            // changed shield balance. Its player charge must be replayed once.
            let prior = lock_combat(&combat)?.inversion_receipt(active);
            if prior.is_none()
                && caster.inversion_available()
                && caster.sector.id == active.sector
                && now_ms < active.next_pulse_ms
            {
                continue;
            }
            let receipt = if let Some(prior) = prior {
                Some(prior)
            } else if !caster.inversion_available()
                || caster.sector.id != active.sector
                || !active.due(now_ms)
            {
                None
            } else {
                lock_combat(&combat)?.cast_inversion(
                    active,
                    caster.position,
                    &caster.inversion_reputation(),
                    caster.shield_balance().0,
                    now_ms,
                )?
            };
            if let Some(receipt) = receipt {
                self.sessions
                    .get_mut(&source)
                    .unwrap()
                    .settle_inversion_pulse(&receipt)?;
            } else {
                self.sessions
                    .get_mut(&source)
                    .unwrap()
                    .stop_shield_inversion()?;
            }
        }
        Ok(())
    }
}
