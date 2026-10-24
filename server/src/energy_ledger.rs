//! One atomic world file commits both sides of a targeted reactor transfer.
//! Session checkpoints may lag that file; their transfer revision decides which
//! resource value wins after a process crash or a short server replacement.
use crate::protocol::invalid;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fs::{self, File};
use std::io::{self, Write};
use std::path::PathBuf;

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
pub struct EnergyBalance {
    pub revision: u64,
    pub energy: f32,
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
struct TransferSequence {
    epoch: u64,
    sequence: u32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct LedgerData {
    version: u32,
    revision: u64,
    balances: BTreeMap<u32, EnergyBalance>,
    last_transfer: BTreeMap<u32, TransferSequence>,
}

pub struct EnergyLedger {
    path: PathBuf,
    data: LedgerData,
}

impl EnergyLedger {
    /// Returns:
    ///     EnergyLedger: Validated atomic transfer state, or an empty ledger.
    pub fn load(path: PathBuf) -> io::Result<Self> {
        let data = if path.exists() {
            serde_json::from_slice::<LedgerData>(&fs::read(&path)?)?
        } else {
            LedgerData {
                version: 1,
                revision: 0,
                balances: BTreeMap::new(),
                last_transfer: BTreeMap::new(),
            }
        };
        if data.version != 1
            || data.balances.iter().any(|(player, balance)| {
                *player == 0
                    || balance.revision == 0
                    || balance.revision > data.revision
                    || !balance.energy.is_finite()
                    || !(0. ..=1_000_000_000.).contains(&balance.energy)
            })
            || data
                .last_transfer
                .iter()
                .any(|(player, last)| *player == 0 || last.epoch == 0 || last.sequence == 0)
        {
            return Err(invalid("Invalid targeted energy transfer ledger."));
        }
        Ok(Self { path, data })
    }

    /// Returns:
    ///     Option<EnergyBalance>: Latest atomic transfer balance for a player.
    pub fn balance(&self, player: u32) -> Option<EnergyBalance> {
        self.data.balances.get(&player).copied()
    }

    /// Returns:
    ///     u64: Latest atomic transfer revision in this world file.
    pub fn revision(&self) -> u64 {
        self.data.revision
    }

    /// Returns:
    ///     bool: Whether this authenticated action already reached the ledger.
    pub fn seen(&self, player: u32, epoch: u64, sequence: u32) -> bool {
        self.data
            .last_transfer
            .get(&player)
            .is_some_and(|last| last.epoch == epoch && sequence <= last.sequence)
    }

    /// Commit both balances and the request identity through one atomic rename.
    ///
    /// Returns:
    ///     u64: Revision shared by sender and recipient after the commit.
    pub fn transfer(
        &mut self,
        source: u32,
        target: u32,
        epoch: u64,
        sequence: u32,
        source_energy: f32,
        target_energy: f32,
    ) -> io::Result<u64> {
        if source == 0
            || target == 0
            || source == target
            || epoch == 0
            || sequence == 0
            || self.seen(source, epoch, sequence)
            || !source_energy.is_finite()
            || !(0. ..=1_000_000_000.).contains(&source_energy)
            || !target_energy.is_finite()
            || !(0. ..=1_000_000_000.).contains(&target_energy)
        {
            return Err(invalid("Invalid or repeated targeted energy transfer."));
        }
        let mut next = self.data.clone();
        next.revision = next
            .revision
            .checked_add(1)
            .ok_or_else(|| invalid("Energy transfer revision exhausted."))?;
        next.balances.insert(
            source,
            EnergyBalance {
                revision: next.revision,
                energy: source_energy,
            },
        );
        next.balances.insert(
            target,
            EnergyBalance {
                revision: next.revision,
                energy: target_energy,
            },
        );
        next.last_transfer
            .insert(source, TransferSequence { epoch, sequence });
        let directory = self
            .path
            .parent()
            .ok_or_else(|| invalid("Energy transfer ledger has no directory."))?;
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

#[cfg(test)]
mod tests {
    use super::*;

    /// Failed writes leave both balances and the last sequence unchanged.
    #[test]
    fn transfer_is_atomic_and_replayed_request_is_identified() {
        let root =
            std::env::temp_dir().join(format!("enb-energy-ledger-{}", rand::random::<u64>()));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("energy_transfers.local.json");
        let mut ledger = EnergyLedger::load(path.clone()).unwrap();
        assert_eq!(ledger.transfer(1, 2, 7, 10, 100., 200.).unwrap(), 1);
        assert!(ledger.seen(1, 7, 10));
        let mut restored = EnergyLedger::load(path).unwrap();
        assert_eq!(restored.balance(1).unwrap().energy, 100.);
        assert_eq!(restored.balance(2).unwrap().energy, 200.);
        assert!(restored.transfer(1, 2, 7, 10, 0., 999.).is_err());
        assert_eq!(restored.balance(2).unwrap().energy, 200.);
        assert_eq!(restored.transfer(1, 2, 7, 11, 80., 220.).unwrap(), 2);
        assert_eq!(restored.balance(1).unwrap().revision, 2);
        assert_eq!(restored.balance(2).unwrap().revision, 2);
        fs::remove_dir_all(root).unwrap();
    }
}
