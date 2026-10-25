//! One atomic world file commits targeted reactor transfers and friendly buffs.
//! Session checkpoints may lag that file; revisions decide which resource or
//! status value wins after a process crash or short server replacement.
use crate::peer_buffs::PeerBuff;
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
pub struct ShieldBalance {
    pub revision: u64,
    pub shield: f32,
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
struct TransferSequence {
    epoch: u64,
    sequence: u32,
}

/// Old ledgers store one object per recipient; new ledgers store one per kind.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(untagged)]
enum StoredPeerBuffs {
    Legacy(PeerBuff),
    Multiple(Vec<PeerBuff>),
}

impl StoredPeerBuffs {
    /// Returns:
    ///     &[PeerBuff]: Borrowed statuses in revision order after migration.
    fn as_slice(&self) -> &[PeerBuff] {
        match self {
            Self::Legacy(buff) => std::slice::from_ref(buff),
            Self::Multiple(buffs) => buffs,
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct LedgerData {
    version: u32,
    revision: u64,
    balances: BTreeMap<u32, EnergyBalance>,
    #[serde(default)]
    shields: BTreeMap<u32, ShieldBalance>,
    // Historical on-disk name; this also deduplicates peer-buff activations.
    last_transfer: BTreeMap<u32, TransferSequence>,
    #[serde(default)]
    last_recharge: BTreeMap<u32, TransferSequence>,
    #[serde(default)]
    peer_buffs: BTreeMap<u32, StoredPeerBuffs>,
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
                shields: BTreeMap::new(),
                last_transfer: BTreeMap::new(),
                last_recharge: BTreeMap::new(),
                peer_buffs: BTreeMap::new(),
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
            || data
                .last_recharge
                .iter()
                .any(|(player, last)| *player == 0 || last.epoch == 0 || last.sequence == 0)
            || data.shields.iter().any(|(player, balance)| {
                *player == 0
                    || balance.revision == 0
                    || balance.revision > data.revision
                    || !balance.shield.is_finite()
                    || !(0. ..=1_000_000_000.).contains(&balance.shield)
            })
            || data.peer_buffs.iter().any(|(target, stored)| {
                let buffs = stored.as_slice();
                *target == 0
                    || buffs.is_empty()
                    || buffs.len() > 3
                    || buffs
                        .windows(2)
                        .any(|pair| pair[0].revision >= pair[1].revision)
                    || buffs.iter().any(|buff| {
                        buff.revision == 0
                            || buff.revision > data.revision
                            || buff.source == 0
                            || buff.target_epoch == 0
                            || !buff.value.is_finite()
                            || buff.expires_at_ms == 0
                    })
                    || buffs.iter().enumerate().any(|(index, buff)| {
                        buffs[..index]
                            .iter()
                            .any(|prior| prior.effect == buff.effect)
                    })
            })
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
    ///     Option<ShieldBalance>: Last committed cross-session shield level.
    pub fn shield_balance(&self, player: u32) -> Option<ShieldBalance> {
        self.data.shields.get(&player).copied()
    }

    /// Returns:
    ///     bool: Whether this charged ability already settled.
    pub fn recharge_seen(&self, player: u32, epoch: u64, effect: u32) -> bool {
        self.data
            .last_recharge
            .get(&player)
            .is_some_and(|last| last.epoch == epoch && effect <= last.sequence)
    }

    /// Atomically settle the caster's reactor and recipient's shield.
    ///
    /// Returns:
    ///     u64: Revision shared by both durable resources.
    pub fn recharge_shields(
        &mut self,
        source: u32,
        target: u32,
        epoch: u64,
        effect: u32,
        source_energy: f32,
        target_shield: f32,
    ) -> io::Result<u64> {
        if source == 0
            || target == 0
            || epoch == 0
            || effect == 0
            || self.recharge_seen(source, epoch, effect)
            || !source_energy.is_finite()
            || !(0. ..=1_000_000_000.).contains(&source_energy)
            || !target_shield.is_finite()
            || !(0. ..=1_000_000_000.).contains(&target_shield)
        {
            return Err(invalid("Invalid or repeated shield recharge."));
        }
        let mut next = self.data.clone();
        next.revision = next
            .revision
            .checked_add(1)
            .ok_or_else(|| invalid("Shield recharge revision exhausted."))?;
        next.balances.insert(
            source,
            EnergyBalance {
                revision: next.revision,
                energy: source_energy,
            },
        );
        next.shields.insert(
            target,
            ShieldBalance {
                revision: next.revision,
                shield: target_shield,
            },
        );
        next.last_recharge.insert(
            source,
            TransferSequence {
                epoch,
                sequence: effect,
            },
        );
        let directory = self
            .path
            .parent()
            .ok_or_else(|| invalid("Shield recharge ledger has no directory."))?;
        fs::create_dir_all(directory)?;
        let temporary = self.path.with_extension("json.tmp");
        let mut file = File::create(&temporary)?;
        file.write_all(&serde_json::to_vec_pretty(&next)?)?;
        file.sync_all()?;
        fs::rename(temporary, &self.path)?;
        self.data = next;
        Ok(self.data.revision)
    }

    /// Returns:
    ///     u64: Latest atomic transfer revision in this world file.
    pub fn revision(&self) -> u64 {
        self.data.revision
    }
    /// Returns:
    ///     &[PeerBuff]: Last atomic targeted buff of each kind for this pilot.
    pub fn peer_buffs(&self, target: u32) -> &[PeerBuff] {
        self.data
            .peer_buffs
            .get(&target)
            .map_or(&[], StoredPeerBuffs::as_slice)
    }

    /// Returns:
    ///     Option<&PeerBuff>: Latest targeted buff, for legacy callers.
    pub fn peer_buff(&self, target: u32) -> Option<&PeerBuff> {
        self.peer_buffs(target).last()
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

    /// Commit sender energy and a target status under one replay identity.
    ///
    /// Returns:
    ///     u64: Durable revision shared by energy and the recipient buff.
    pub fn cast_peer_buff(
        &mut self,
        source: u32,
        target: u32,
        epoch: u64,
        sequence: u32,
        source_energy: f32,
        mut buff: PeerBuff,
    ) -> io::Result<u64> {
        if source == 0
            || target == 0
            || source == target
            || epoch == 0
            || sequence == 0
            || self.seen(source, epoch, sequence)
            || !source_energy.is_finite()
            || !(0. ..=1_000_000_000.).contains(&source_energy)
            || buff.source != source
            || buff.target_epoch == 0
            || !buff.value.is_finite()
            || buff.expires_at_ms == 0
        {
            return Err(invalid("Invalid or repeated targeted device buff."));
        }
        let mut next = self.data.clone();
        next.revision = next
            .revision
            .checked_add(1)
            .ok_or_else(|| invalid("Targeted device revision exhausted."))?;
        buff.revision = next.revision;
        next.balances.insert(
            source,
            EnergyBalance {
                revision: next.revision,
                energy: source_energy,
            },
        );
        let mut buffs = next
            .peer_buffs
            .get(&target)
            .map_or_else(Vec::new, |stored| stored.as_slice().to_vec());
        buffs.retain(|existing| existing.effect != buff.effect);
        buffs.push(buff);
        buffs.sort_by_key(|existing| existing.revision);
        next.peer_buffs
            .insert(target, StoredPeerBuffs::Multiple(buffs));
        next.last_transfer
            .insert(source, TransferSequence { epoch, sequence });
        let directory = self
            .path
            .parent()
            .ok_or_else(|| invalid("Targeted device ledger has no directory."))?;
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

    /// A failed rename cannot spend reactor energy or restore shields.
    #[test]
    fn shield_recharge_commits_both_resources_and_deduplicates_after_reload() {
        let root =
            std::env::temp_dir().join(format!("enb-recharge-ledger-{}", rand::random::<u64>()));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("energy_transfers.local.json");
        let mut ledger = EnergyLedger::load(path.clone()).unwrap();
        let temporary = path.with_extension("json.tmp");
        fs::create_dir(&temporary).unwrap();
        assert!(ledger.recharge_shields(1, 2, 7, 300, 75., 420.).is_err());
        assert_eq!(ledger.revision(), 0);
        fs::remove_dir(&temporary).unwrap();
        assert_eq!(ledger.recharge_shields(1, 2, 7, 300, 75., 420.).unwrap(), 1);
        let mut restored = EnergyLedger::load(path).unwrap();
        assert!(restored.recharge_seen(1, 7, 300));
        assert_eq!(restored.balance(1).unwrap().energy, 75.);
        assert_eq!(restored.shield_balance(2).unwrap().shield, 420.);
        assert!(restored.recharge_shields(1, 2, 7, 300, 75., 840.).is_err());
        assert_eq!(restored.shield_balance(2).unwrap().shield, 420.);
        fs::remove_dir_all(root).unwrap();
    }

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
        let mut legacy: serde_json::Value =
            serde_json::from_slice(&fs::read(&restored.path).unwrap()).unwrap();
        legacy.as_object_mut().unwrap().remove("peer_buffs");
        fs::write(&restored.path, serde_json::to_vec(&legacy).unwrap()).unwrap();
        assert!(EnergyLedger::load(restored.path.clone())
            .unwrap()
            .peer_buff(2)
            .is_none());
        fs::remove_dir_all(root).unwrap();
    }

    /// Old one-buff ledgers migrate when a second effect is committed.
    #[test]
    fn legacy_single_buff_checkpoint_accepts_a_second_kind_atomically() {
        let root = std::env::temp_dir().join(format!("enb-buff-ledger-{}", rand::random::<u64>()));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("energy_transfers.local.json");
        let now = crate::resources::unix_ms();
        let old = PeerBuff {
            revision: 1,
            source: 1,
            target_epoch: 3,
            template: 1813,
            effect: crate::peer_buffs::FriendlyEffect::ScanBoost,
            value: 25.,
            operation: 2,
            expires_at_ms: now + 100_000,
        };
        fs::write(
            &path,
            serde_json::to_vec(&serde_json::json!({
                "version": 1,
                "revision": 1,
                "balances": {},
                "last_transfer": {},
                "peer_buffs": {"2": old},
            }))
            .unwrap(),
        )
        .unwrap();
        let mut ledger = EnergyLedger::load(path.clone()).unwrap();
        assert_eq!(ledger.peer_buffs(2).len(), 1);
        let second = PeerBuff {
            revision: 0,
            source: 1,
            target_epoch: 3,
            template: 2172,
            effect: crate::peer_buffs::FriendlyEffect::RechargeReactor,
            value: 30.,
            operation: 2,
            expires_at_ms: now + 100_000,
        };
        let temporary = path.with_extension("json.tmp");
        fs::create_dir(&temporary).unwrap();
        assert!(ledger
            .cast_peer_buff(1, 2, 7, 2, 100., second.clone())
            .is_err());
        assert_eq!(ledger.peer_buffs(2).len(), 1);
        fs::remove_dir(&temporary).unwrap();
        ledger.cast_peer_buff(1, 2, 7, 2, 100., second).unwrap();
        let restored = EnergyLedger::load(path).unwrap();
        assert_eq!(restored.peer_buffs(2).len(), 2);
        assert_eq!(restored.peer_buffs(2)[0].effect, old.effect);
        assert_eq!(
            restored.peer_buffs(2)[1].effect,
            crate::peer_buffs::FriendlyEffect::RechargeReactor
        );
        fs::remove_dir_all(root).unwrap();
    }
}
