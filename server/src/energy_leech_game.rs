//! Cross-sector MOB drain and group reactor settlement for Defender Energy Leech.
use super::*;
use crate::mob::distance;

impl GameState {
    /// Settle each saved Energy Leech once across the combat and account files.
    pub(super) fn deliver_energy_leeches(&mut self) -> io::Result<()> {
        let sources: Vec<u32> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| session.pending_energy_leech().map(|_| *id))
            .collect();
        for source in sources {
            let cast = self.sessions[&source].pending_energy_leech().unwrap();
            if !cast.valid() {
                return Err(invalid("Invalid saved Energy Leech cast."));
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .checkpoint_session(true)?;
            let caster = &self.sessions[&source];
            let epoch = caster.energy_transfer_epoch();
            let sector = caster.sector.id;
            let combat = Arc::clone(&caster.combat);
            let tier = crate::energy_leech::tier(cast.ability, cast.trained_rank, "jd")
                .ok_or_else(|| invalid("Unknown Energy Leech tier."))?;
            let seen = self
                .energy_ledger
                .energy_leech_seen(source, epoch, cast.effect);
            let prior = lock_combat(&combat)?.energy_leech_receipt(source, epoch, cast.effect);
            if seen && prior.is_none() {
                return Err(invalid("Settled Energy Leech has no combat receipt."));
            }
            let receipt = if let Some(prior) = prior {
                prior
            } else {
                self.update_views();
                let caster = &self.sessions[&source];
                let target_position = lock_combat(&combat)?
                    .actors
                    .get((cast.target - MOB_ID) as usize)
                    .map(|mob| mob.position);
                let eligible = caster.energy_leech_available()
                    && target_position
                        .is_some_and(|position| distance(caster.position, position) <= tier.range)
                    && caster
                        .energy_leech_target_generation(cast.target)
                        .is_ok_and(|generation| generation == cast.target_generation);
                if !eligible {
                    let caster = self.sessions.get_mut(&source).unwrap();
                    caster.clear_pending_energy_leech()?;
                    caster.message("Energy Leech target left range before completion.");
                    continue;
                }
                let recipients = if !tier.transfers_energy {
                    Vec::new()
                } else if tier.shares_group {
                    std::iter::once(source)
                        .chain(
                            self.groups
                                .members(source)
                                .into_iter()
                                .filter(|target| *target != source),
                        )
                        .filter(|target| {
                            self.sessions.get(target).is_some_and(|recipient| {
                                recipient.logged_in
                                    && recipient.hull > 0.
                                    && recipient.sector.id == sector
                                    && distance(recipient.position, caster.position) <= tier.range
                            })
                        })
                        .collect()
                } else {
                    vec![source]
                };
                lock_combat(&combat)?.cast_energy_leech(
                    source,
                    epoch,
                    cast,
                    caster.position,
                    &caster.energy_leech_reputation(),
                    recipients,
                )?
            };
            if !seen {
                let pool = crate::energy_leech::converted_energy(receipt.total_drained(), tier);
                let share = if receipt.recipients.is_empty() {
                    0.
                } else {
                    pool / receipt.recipients.len() as f32
                };
                let recipients: std::collections::BTreeSet<_> =
                    receipt.recipients.iter().copied().collect();
                let energies = std::iter::once(source)
                    .chain(
                        receipt
                            .recipients
                            .iter()
                            .copied()
                            .filter(|id| *id != source),
                    )
                    .map(|id| {
                        self.sessions
                            .get(&id)
                            .map(|recipient| {
                                let (energy, capacity) = recipient.energy_balance();
                                (
                                    id,
                                    (energy + if recipients.contains(&id) { share } else { 0. })
                                        .min(capacity),
                                )
                            })
                            .ok_or_else(|| invalid("Energy Leech recipient account is missing."))
                    })
                    .collect::<io::Result<Vec<_>>>()?;
                self.energy_ledger.settle_energy_leech(&receipt, energies)?;
            }
            for target in std::iter::once(source).chain(
                receipt
                    .recipients
                    .iter()
                    .copied()
                    .filter(|id| *id != source),
            ) {
                if let Some(balance) = self.energy_ledger.balance(target) {
                    if let Some(recipient) = self.sessions.get_mut(&target) {
                        if let Err(error) = recipient.apply_energy_balance(balance) {
                            crate::server_error!(
                                "Energy Leech recipient checkpoint retry for {target}: {error}"
                            );
                        }
                    }
                }
            }
            let caster = self.sessions.get_mut(&source).unwrap();
            caster.message(&format!(
                "Energy Leech drained {:.0} reactor points.",
                receipt.total_drained()
            ));
            caster.clear_pending_energy_leech()?;
        }
        Ok(())
    }
}
