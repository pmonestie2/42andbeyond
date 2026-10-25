//! Atomic caster-energy and selected-recipient Psionic Shield settlement.
use super::*;

impl GameState {
    /// Deliver committed casts and retry lagging recipient mirrors by revision.
    pub(super) fn deliver_psionic_shields(&mut self) -> io::Result<()> {
        let sources: Vec<u32> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| session.pending_psionic_shield().map(|_| *id))
            .collect();
        for source in sources {
            let pending = self.sessions[&source].pending_psionic_shield().unwrap();
            if !pending.valid() {
                return Err(invalid("Invalid saved Psionic Shield cast."));
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .checkpoint_session(true)?;
            let epoch = self.sessions[&source].energy_transfer_epoch();
            if !self
                .energy_ledger
                .psionic_shield_seen(source, epoch, pending.effect)
            {
                self.update_views();
                let caster = &self.sessions[&source];
                let recipient = self.sessions.get(&pending.target);
                let tier = crate::psionic_shield::tier(pending.ability, pending.trained_rank)
                    .ok_or_else(|| invalid("Unknown Psionic Shield tier."))?;
                let eligible = recipient.is_some_and(|recipient| {
                    caster.psionic_shield_available()
                        && recipient.psionic_shield_available()
                        && recipient.sector.id == caster.sector.id
                        && caster.psionic_shield_contact(pending.target)
                        && crate::psionic_shield::target_in_range(
                            tier,
                            caster.position,
                            recipient.position,
                            recipient.hull > 0.,
                        )
                });
                if !eligible {
                    self.sessions
                        .get_mut(&source)
                        .unwrap()
                        .clear_pending_psionic_shield()?;
                    continue;
                }
                let recipient = recipient.unwrap();
                self.energy_ledger.cast_psionic_shield(
                    source,
                    epoch,
                    pending.effect,
                    caster.energy_balance().0,
                    pending.target,
                    crate::psionic_shield::PsionicShieldBuff {
                        revision: 0,
                        source,
                        target_epoch: recipient.energy_transfer_epoch(),
                        ability: pending.ability,
                        trained_rank: pending.trained_rank,
                        effect: pending.effect,
                        expires_at_ms: crate::resources::unix_ms()
                            .saturating_add(crate::psionic_shield::DURATION_MS),
                        remaining: tier.capacity(pending.trained_rank),
                    },
                )?;
            }
            if let Some(balance) = self.energy_ledger.balance(source) {
                if let Err(error) = self
                    .sessions
                    .get_mut(&source)
                    .unwrap()
                    .apply_energy_balance(balance)
                {
                    crate::server_error!(
                        "Psionic Shield sender checkpoint retry for {source}: {error}"
                    );
                }
            }
            if let Some(buff) = self.energy_ledger.psionic_shield_buff(pending.target) {
                if buff.source == source {
                    if let Some(recipient) = self.sessions.get_mut(&pending.target) {
                        if let Err(error) = recipient.apply_psionic_shield_buff(buff.clone()) {
                            crate::server_error!(
                                "Psionic Shield recipient checkpoint retry for {}: {error}",
                                pending.target
                            );
                        }
                    }
                }
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .clear_pending_psionic_shield()?;
        }
        Ok(())
    }
}
