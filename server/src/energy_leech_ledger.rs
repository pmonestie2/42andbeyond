//! Durable group reactor awards for one committed Energy Leech sector drain.
use super::*;

impl EnergyLedger {
    /// Returns:
    ///     bool: Whether this source cast already settled all friendly balances.
    pub fn energy_leech_seen(&self, source: u32, epoch: u64, effect: u32) -> bool {
        self.data
            .last_energy_leech
            .get(&source)
            .is_some_and(|last| last.epoch == epoch && effect <= last.sequence)
    }

    /// Commit the charged caster and each frozen recipient at one revision.
    ///
    /// Returns:
    ///     u64: Shared durable revision for all written energy balances.
    pub fn settle_energy_leech(
        &mut self,
        receipt: &crate::energy_leech::LeechReceipt,
        energies: Vec<(u32, f32)>,
    ) -> io::Result<u64> {
        let expected: std::collections::BTreeSet<_> = std::iter::once(receipt.source)
            .chain(receipt.recipients.iter().copied())
            .collect();
        let actual: std::collections::BTreeSet<_> = energies.iter().map(|(id, _)| *id).collect();
        if !receipt.valid()
            || self.energy_leech_seen(receipt.source, receipt.source_epoch, receipt.effect)
            || actual != expected
            || energies.len() != expected.len()
            || energies
                .iter()
                .any(|(_, energy)| !energy.is_finite() || !(0. ..=1_000_000_000.).contains(energy))
        {
            return Err(invalid("Invalid or repeated Energy Leech settlement."));
        }
        let mut next = self.data.clone();
        next.revision = next
            .revision
            .checked_add(1)
            .ok_or_else(|| invalid("Energy Leech revision exhausted."))?;
        for (target, energy) in energies {
            next.balances.insert(
                target,
                EnergyBalance {
                    revision: next.revision,
                    energy,
                },
            );
        }
        next.last_energy_leech.insert(
            receipt.source,
            TransferSequence {
                epoch: receipt.source_epoch,
                sequence: receipt.effect,
            },
        );
        let directory = self
            .path
            .parent()
            .ok_or_else(|| invalid("Energy Leech ledger has no directory."))?;
        fs::create_dir_all(directory)?;
        let temporary = self.path.with_extension("json.tmp");
        let mut file = File::create(&temporary)?;
        file.write_all(&serde_json::to_vec_pretty(&next)?)?;
        file.sync_all()?;
        fs::rename(temporary, &self.path)?;
        self.data = next;
        Ok(self.data.revision)
    }
}
