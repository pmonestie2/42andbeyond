//! Atomic caster-energy and selected-recipient Repulsor Field delivery.
use super::*;

impl GameState {
    /// Settle charged fields and retry recipient mirrors after a failed checkpoint.
    pub(super) fn deliver_repulsor_fields(&mut self) -> io::Result<()> {
        let sources: Vec<u32> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| session.pending_repulsor_field().map(|_| *id))
            .collect();
        for source in sources {
            let pending = self.sessions[&source].pending_repulsor_field().unwrap();
            if !pending.valid() {
                return Err(invalid("Invalid saved Repulsor Field cast."));
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .checkpoint_session(true)?;
            let epoch = self.sessions[&source].energy_transfer_epoch();
            if !self
                .energy_ledger
                .repulsor_field_seen(source, epoch, pending.effect)
            {
                self.update_views();
                let caster = &self.sessions[&source];
                let recipient = self.sessions.get(&pending.target);
                let band = crate::repulsor_field::tier(pending.ability, pending.trained_rank)
                    .ok_or_else(|| invalid("Unknown Repulsor Field tier."))?;
                let eligible = recipient.is_some_and(|recipient| {
                    caster.account.character.class().code() == "ps"
                        && caster.repulsor_field_available()
                        && recipient.repulsor_field_available()
                        && recipient.sector.id == caster.sector.id
                        && caster.repulsor_field_contact(pending.target)
                        && crate::repulsor_field::target_in_range(
                            band,
                            source,
                            pending.target,
                            caster.position,
                            recipient.position,
                            recipient.hull > 0.,
                            true,
                        )
                });
                if !eligible {
                    self.sessions
                        .get_mut(&source)
                        .unwrap()
                        .clear_pending_repulsor_field()?;
                    continue;
                }
                let recipient = recipient.unwrap();
                self.energy_ledger.cast_repulsor_field(
                    source,
                    epoch,
                    pending.effect,
                    caster.energy_balance().0,
                    pending.target,
                    crate::repulsor_field::RepulsorBuff {
                        revision: 0,
                        source,
                        target_epoch: recipient.energy_transfer_epoch(),
                        ability: pending.ability,
                        trained_rank: pending.trained_rank,
                        effective_rank: pending.effective_rank,
                        expires_at_ms: crate::resources::unix_ms().saturating_add(
                            crate::repulsor_field::duration_ms(pending.effective_rank),
                        ),
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
                    crate::server_error!("Repulsor caster checkpoint retry for {source}: {error}");
                }
            }
            if let Some(buff) = self.energy_ledger.repulsor_field_buff(pending.target) {
                if buff.source == source {
                    if let Some(recipient) = self.sessions.get_mut(&pending.target) {
                        if let Err(error) = recipient.apply_repulsor_field_buff(buff.clone()) {
                            crate::server_error!(
                                "Repulsor recipient checkpoint retry for {}: {error}",
                                pending.target
                            );
                        }
                    }
                }
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .clear_pending_repulsor_field()?;
        }
        Ok(())
    }
}
