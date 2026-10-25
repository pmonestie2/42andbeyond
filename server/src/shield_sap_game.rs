//! World settlement for Progen Warrior Shield Sap's MOB drain and group award.
use super::*;
use crate::mob::distance;

impl GameState {
    /// Settle a saved Shield Sap across the sector snapshot and resource ledger.
    pub(super) fn deliver_shield_saps(&mut self) -> io::Result<()> {
        let sources: Vec<u32> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| session.pending_shield_sap().map(|_| *id))
            .collect();
        for source in sources {
            let cast = self.sessions[&source].pending_shield_sap().unwrap();
            if !cast.valid() {
                return Err(invalid("Invalid saved Shield Sap cast."));
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .checkpoint_session(true)?;
            let epoch = self.sessions[&source].energy_transfer_epoch();
            let tier = crate::shield_sap::tier(cast.ability, cast.trained_rank)
                .ok_or_else(|| invalid("Unknown Shield Sap rank."))?;
            let caster = &self.sessions[&source];
            let combat = Arc::clone(&caster.combat);
            let ledger_seen = self
                .energy_ledger
                .shield_sap_seen(source, epoch, cast.effect);
            let prior = lock_combat(&combat)?.shield_sap_receipt(source, epoch, cast.effect);
            if ledger_seen && prior.is_none() {
                return Err(invalid(
                    "Settled Shield Sap has no matching combat receipt.",
                ));
            }
            let receipt = if let Some(prior) = prior {
                prior
            } else {
                self.update_views();
                let caster = &self.sessions[&source];
                let index = cast
                    .target
                    .checked_sub(MOB_ID)
                    .ok_or_else(|| invalid("Shield Sap target is not a MOB."))?
                    as usize;
                let target_position = {
                    let combat = lock_combat(&combat)?;
                    combat.actors.get(index).map(|mob| mob.position)
                };
                let eligible = caster.shield_sap_available()
                    && target_position
                        .is_some_and(|position| distance(caster.position, position) <= tier.range)
                    && caster
                        .shield_sap_target_generation(cast.target)
                        .is_ok_and(|generation| generation == cast.target_generation);
                if !eligible {
                    let caster = self.sessions.get_mut(&source).unwrap();
                    caster.clear_pending_shield_sap()?;
                    caster.message("Shield Sap target left range before completion.");
                    continue;
                }
                let recipients = if tier.rank < 3 {
                    Vec::new()
                } else if tier.group_heal {
                    std::iter::once(source)
                        .chain(
                            self.groups
                                .members(source)
                                .into_iter()
                                .filter(|target| *target != source),
                        )
                        .filter(|target| {
                            self.sessions.get(target).is_some_and(|recipient| {
                                recipient.shield_recharge_available()
                                    && recipient.sector.id == caster.sector.id
                                    && distance(recipient.position, caster.position) <= tier.range
                            })
                        })
                        .collect()
                } else {
                    vec![source]
                };
                lock_combat(&combat)?.apply_shield_sap(
                    source,
                    epoch,
                    &cast,
                    caster.shield_sap_capacity(),
                    recipients,
                )?
            };
            if !ledger_seen {
                let caster = &self.sessions[&source];
                let total = crate::shield_sap::restore_amount(
                    receipt.drained,
                    receipt.trained_rank,
                    tier.rank,
                );
                let per_recipient = if tier.group_heal {
                    crate::shield_sap::group_share(
                        total,
                        receipt.recipients.len(),
                        receipt.caster_shield_capacity,
                    )
                } else {
                    total
                };
                let shields = receipt
                    .recipients
                    .iter()
                    .map(|target| {
                        self.sessions
                            .get(target)
                            .map(|recipient| {
                                let (shield, capacity) = recipient.shield_balance();
                                (*target, (shield + per_recipient).min(capacity))
                            })
                            .ok_or_else(|| invalid("Shield Sap recipient account is missing."))
                    })
                    .collect::<io::Result<Vec<_>>>()?;
                self.energy_ledger.settle_shield_sap(
                    source,
                    &receipt,
                    caster.energy_balance().0,
                    shields,
                )?;
            }
            let energy = self.energy_ledger.balance(source).unwrap();
            if let Err(error) = self
                .sessions
                .get_mut(&source)
                .unwrap()
                .apply_energy_balance(energy)
            {
                crate::server_error!("Shield Sap sender checkpoint retry for {source}: {error}");
            }
            for target in &receipt.recipients {
                if let Some(balance) = self
                    .energy_ledger
                    .shield_balance(*target)
                    .filter(|balance| balance.revision == energy.revision)
                {
                    if let Some(recipient) = self.sessions.get_mut(target) {
                        if let Err(error) = recipient.apply_shield_balance(balance) {
                            crate::server_error!(
                                "Shield Sap recipient checkpoint retry for {target}: {error}"
                            );
                        }
                    }
                }
            }
            let caster = self.sessions.get_mut(&source).unwrap();
            caster.publish_shield_sap(cast.effect, &receipt.targets);
            caster.message(&format!(
                "Shield Sap drained {:.0} shield points.",
                receipt.drained
            ));
            caster.clear_pending_shield_sap()?;
        }
        Ok(())
    }
}
