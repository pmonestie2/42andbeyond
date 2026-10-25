//! One atomic world file commits targeted reactor transfers and friendly buffs.
//! Session checkpoints may lag that file; revisions decide which resource or
//! status value wins after a process crash or short server replacement.
use crate::inventory::{ItemInstance, EQUIPMENT_SLOTS};
use crate::peer_buffs::PeerBuff;
use crate::protocol::invalid;
use crate::rally::RallyBuff;
use crate::reactor_optimization::ReactorOptimizationBuff;
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
pub struct HullBalance {
    pub revision: u64,
    pub hull: f32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct EquipmentBalance {
    pub revision: u64,
    pub equipped: Vec<Option<ItemInstance>>,
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
    #[serde(default)]
    hulls: BTreeMap<u32, HullBalance>,
    // Historical on-disk name; this also deduplicates peer-buff activations.
    last_transfer: BTreeMap<u32, TransferSequence>,
    #[serde(default)]
    last_recharge: BTreeMap<u32, TransferSequence>,
    #[serde(default)]
    last_hull_repair: BTreeMap<u32, TransferSequence>,
    #[serde(default)]
    equipment: BTreeMap<u32, EquipmentBalance>,
    #[serde(default)]
    last_equipment_repair: BTreeMap<u32, TransferSequence>,
    #[serde(default)]
    peer_buffs: BTreeMap<u32, StoredPeerBuffs>,
    #[serde(default)]
    rally_buffs: BTreeMap<u32, RallyBuff>,
    #[serde(default)]
    last_rally: BTreeMap<u32, TransferSequence>,
    #[serde(default)]
    reactor_optimization_buffs: BTreeMap<u32, ReactorOptimizationBuff>,
    #[serde(default)]
    last_reactor_optimization: BTreeMap<u32, TransferSequence>,
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
                hulls: BTreeMap::new(),
                last_transfer: BTreeMap::new(),
                last_recharge: BTreeMap::new(),
                last_hull_repair: BTreeMap::new(),
                equipment: BTreeMap::new(),
                last_equipment_repair: BTreeMap::new(),
                peer_buffs: BTreeMap::new(),
                rally_buffs: BTreeMap::new(),
                last_rally: BTreeMap::new(),
                reactor_optimization_buffs: BTreeMap::new(),
                last_reactor_optimization: BTreeMap::new(),
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
            || data
                .last_hull_repair
                .iter()
                .any(|(player, last)| *player == 0 || last.epoch == 0 || last.sequence == 0)
            || data.shields.iter().any(|(player, balance)| {
                *player == 0
                    || balance.revision == 0
                    || balance.revision > data.revision
                    || !balance.shield.is_finite()
                    || !(0. ..=1_000_000_000.).contains(&balance.shield)
            })
            || data.hulls.iter().any(|(player, balance)| {
                *player == 0
                    || balance.revision == 0
                    || balance.revision > data.revision
                    || !balance.hull.is_finite()
                    || !(0. ..=1_000_000_000.).contains(&balance.hull)
            })
            || data
                .last_equipment_repair
                .iter()
                .any(|(player, last)| *player == 0 || last.epoch == 0 || last.sequence == 0)
            || data.equipment.iter().any(|(player, balance)| {
                *player == 0
                    || balance.revision == 0
                    || balance.revision > data.revision
                    || balance.equipped.len() != EQUIPMENT_SLOTS
                    || balance.equipped.iter().flatten().any(|item| {
                        item.quantity != 1
                            || !item.structure.is_finite()
                            || !(0. ..=1.).contains(&item.structure)
                    })
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
            || data
                .last_rally
                .iter()
                .any(|(source, last)| *source == 0 || last.epoch == 0 || last.sequence == 0)
            || data.rally_buffs.iter().any(|(target, buff)| {
                *target == 0
                    || buff.revision == 0
                    || buff.revision > data.revision
                    || buff.source == 0
                    || buff.target_epoch == 0
                    || buff.skill_rank < buff.tactic.rank()
                    || buff.skill_rank > 7
                    || buff.expires_at_ms == 0
            })
            || data
                .last_reactor_optimization
                .iter()
                .any(|(source, last)| *source == 0 || last.epoch == 0 || last.sequence == 0)
            || data
                .reactor_optimization_buffs
                .iter()
                .any(|(target, buff)| {
                    *target == 0
                        || buff.revision == 0
                        || buff.revision > data.revision
                        || buff.source == 0
                        || buff.target_epoch == 0
                        || crate::reactor_optimization::tier(buff.ability, buff.trained_rank)
                            .is_none()
                        || buff.expires_at_ms == 0
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
    ///     Option<HullBalance>: Last committed cross-session hull level.
    pub fn hull_balance(&self, player: u32) -> Option<HullBalance> {
        self.data.hulls.get(&player).copied()
    }

    /// Returns:
    ///     Option<&EquipmentBalance>: Latest committed equipped-structure snapshot.
    pub fn equipment_balance(&self, player: u32) -> Option<&EquipmentBalance> {
        self.data.equipment.get(&player)
    }

    /// Returns:
    ///     bool: Whether this stream tick already entered the durable world ledger.
    pub fn equipment_repair_seen(&self, player: u32, epoch: u64, sequence: u32) -> bool {
        self.data
            .last_equipment_repair
            .get(&player)
            .is_some_and(|last| last.epoch == epoch && sequence <= last.sequence)
    }

    /// Commit the caster's reactor and every repaired target in one world file.
    ///
    /// Returns:
    ///     u64: Shared revision for the paid stream tick.
    pub fn repair_equipment(
        &mut self,
        source: u32,
        epoch: u64,
        sequence: u32,
        source_energy: f32,
        targets: BTreeMap<u32, Vec<Option<ItemInstance>>>,
    ) -> io::Result<u64> {
        if source == 0
            || epoch == 0
            || sequence == 0
            || self.equipment_repair_seen(source, epoch, sequence)
            || !source_energy.is_finite()
            || !(0. ..=1_000_000_000.).contains(&source_energy)
            || targets.is_empty()
            || targets.len() > 32
            || targets.iter().any(|(target, slots)| {
                *target == 0
                    || slots.len() != EQUIPMENT_SLOTS
                    || slots.iter().flatten().any(|item| {
                        item.quantity != 1
                            || !item.structure.is_finite()
                            || !(0. ..=1.).contains(&item.structure)
                    })
            })
        {
            return Err(invalid("Invalid or repeated equipment repair tick."));
        }
        let mut next = self.data.clone();
        next.revision = next
            .revision
            .checked_add(1)
            .ok_or_else(|| invalid("Equipment repair revision exhausted."))?;
        next.balances.insert(
            source,
            EnergyBalance {
                revision: next.revision,
                energy: source_energy,
            },
        );
        for (target, equipped) in targets {
            next.equipment.insert(
                target,
                EquipmentBalance {
                    revision: next.revision,
                    equipped,
                },
            );
        }
        next.last_equipment_repair
            .insert(source, TransferSequence { epoch, sequence });
        let directory = self
            .path
            .parent()
            .ok_or_else(|| invalid("Equipment repair ledger has no directory."))?;
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
    ///     bool: Whether this Hull Patch cast already settled.
    pub fn hull_repair_seen(&self, player: u32, epoch: u64, effect: u32) -> bool {
        self.data
            .last_hull_repair
            .get(&player)
            .is_some_and(|last| last.epoch == epoch && effect <= last.sequence)
    }

    /// Atomically commit the caster's reactor and recipient's repaired hull.
    ///
    /// Returns:
    ///     u64: World revision shared by both resources.
    pub fn repair_hull(
        &mut self,
        source: u32,
        target: u32,
        epoch: u64,
        effect: u32,
        source_energy: f32,
        target_hull: f32,
    ) -> io::Result<u64> {
        self.repair_hulls(
            source,
            epoch,
            effect,
            source_energy,
            BTreeMap::from([(target, target_hull)]),
        )
    }

    /// Atomically repair every eligible area recipient with the caster's reactor receipt.
    ///
    /// Returns:
    ///     u64: One world revision shared by all repaired hulls.
    pub fn repair_hulls(
        &mut self,
        source: u32,
        epoch: u64,
        effect: u32,
        source_energy: f32,
        targets: BTreeMap<u32, f32>,
    ) -> io::Result<u64> {
        if source == 0
            || epoch == 0
            || effect == 0
            || self.hull_repair_seen(source, epoch, effect)
            || !source_energy.is_finite()
            || !(0. ..=1_000_000_000.).contains(&source_energy)
            || targets.is_empty()
            || targets.len() > 6
            || targets.iter().any(|(target, hull)| {
                *target == 0 || !hull.is_finite() || !(0. ..=1_000_000_000.).contains(hull)
            })
        {
            return Err(invalid("Invalid or repeated Hull Patch cast."));
        }
        let mut next = self.data.clone();
        next.revision = next
            .revision
            .checked_add(1)
            .ok_or_else(|| invalid("Hull repair revision exhausted."))?;
        next.balances.insert(
            source,
            EnergyBalance {
                revision: next.revision,
                energy: source_energy,
            },
        );
        for (target, hull) in targets {
            next.hulls.insert(
                target,
                HullBalance {
                    revision: next.revision,
                    hull,
                },
            );
        }
        next.last_hull_repair.insert(
            source,
            TransferSequence {
                epoch,
                sequence: effect,
            },
        );
        let directory = self
            .path
            .parent()
            .ok_or_else(|| invalid("Hull repair ledger has no directory."))?;
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
        self.recharge_shields_area(
            source,
            epoch,
            effect,
            source_energy,
            BTreeMap::from([(target, target_shield)]),
        )
    }

    /// Atomically recharge every eligible area recipient with the caster's reactor receipt.
    ///
    /// Returns:
    ///     u64: One world revision shared by all recharged shields.
    pub fn recharge_shields_area(
        &mut self,
        source: u32,
        epoch: u64,
        effect: u32,
        source_energy: f32,
        targets: BTreeMap<u32, f32>,
    ) -> io::Result<u64> {
        if source == 0
            || epoch == 0
            || effect == 0
            || self.recharge_seen(source, epoch, effect)
            || !source_energy.is_finite()
            || !(0. ..=1_000_000_000.).contains(&source_energy)
            || targets.is_empty()
            || targets.len() > 6
            || targets.iter().any(|(target, shield)| {
                *target == 0 || !shield.is_finite() || !(0. ..=1_000_000_000.).contains(shield)
            })
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
        for (target, shield) in targets {
            next.shields.insert(
                target,
                ShieldBalance {
                    revision: next.revision,
                    shield,
                },
            );
        }
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
    ///     Option<&RallyBuff>: Last atomic Rally status for this pilot.
    pub fn rally_buff(&self, target: u32) -> Option<&RallyBuff> {
        self.data.rally_buffs.get(&target)
    }

    /// Returns:
    ///     bool: Whether this Rally cast was already committed.
    pub fn rally_seen(&self, source: u32, epoch: u64, effect: u32) -> bool {
        self.data
            .last_rally
            .get(&source)
            .is_some_and(|last| last.epoch == epoch && effect <= last.sequence)
    }

    /// Commit one caster cost and every in-range group recipient atomically.
    ///
    /// Returns:
    ///     u64: Shared world revision for the Rally cast.
    pub fn cast_rally(
        &mut self,
        source: u32,
        epoch: u64,
        effect: u32,
        source_energy: f32,
        recipients: Vec<(u32, RallyBuff)>,
    ) -> io::Result<u64> {
        if source == 0
            || epoch == 0
            || effect == 0
            || self.rally_seen(source, epoch, effect)
            || !source_energy.is_finite()
            || !(0. ..=1_000_000_000.).contains(&source_energy)
            || recipients.is_empty()
            || recipients.len() > 6
            || !recipients.iter().any(|(target, _)| *target == source)
            || recipients
                .iter()
                .enumerate()
                .any(|(index, (target, buff))| {
                    *target == 0
                        || buff.source != source
                        || buff.target_epoch == 0
                        || buff.skill_rank < buff.tactic.rank()
                        || buff.skill_rank > 7
                        || buff.expires_at_ms == 0
                        || recipients[..index].iter().any(|(prior, _)| prior == target)
                })
        {
            return Err(invalid("Invalid or repeated Rally cast."));
        }
        let mut next = self.data.clone();
        next.revision = next
            .revision
            .checked_add(1)
            .ok_or_else(|| invalid("Rally revision exhausted."))?;
        next.balances.insert(
            source,
            EnergyBalance {
                revision: next.revision,
                energy: source_energy,
            },
        );
        for (target, mut buff) in recipients {
            buff.revision = next.revision;
            next.rally_buffs.insert(target, buff);
        }
        next.last_rally.insert(
            source,
            TransferSequence {
                epoch,
                sequence: effect,
            },
        );
        let directory = self
            .path
            .parent()
            .ok_or_else(|| invalid("Rally ledger has no directory."))?;
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
    ///     Option<&ReactorOptimizationBuff>: Last committed timed reactor status.
    pub fn reactor_optimization_buff(&self, target: u32) -> Option<&ReactorOptimizationBuff> {
        self.data.reactor_optimization_buffs.get(&target)
    }

    /// Returns:
    ///     bool: Whether an authenticated Reactor Optimization cast was settled.
    pub fn reactor_optimization_seen(&self, source: u32, epoch: u64, effect: u32) -> bool {
        self.data
            .last_reactor_optimization
            .get(&source)
            .is_some_and(|last| last.epoch == epoch && effect <= last.sequence)
    }

    /// Atomically commit the caster's charged energy and one recipient's status.
    ///
    /// Returns:
    ///     u64: Shared durable world revision for the cast.
    pub fn cast_reactor_optimization(
        &mut self,
        source: u32,
        epoch: u64,
        effect: u32,
        source_energy: f32,
        target: u32,
        mut buff: ReactorOptimizationBuff,
    ) -> io::Result<u64> {
        if source == 0
            || epoch == 0
            || effect == 0
            || self.reactor_optimization_seen(source, epoch, effect)
            || target == 0
            || buff.source != source
            || buff.target_epoch == 0
            || buff.expires_at_ms == 0
            || crate::reactor_optimization::tier(buff.ability, buff.trained_rank).is_none()
            || !source_energy.is_finite()
            || !(0. ..=1_000_000_000.).contains(&source_energy)
        {
            return Err(invalid("Invalid or repeated Reactor Optimization cast."));
        }
        let mut next = self.data.clone();
        next.revision = next
            .revision
            .checked_add(1)
            .ok_or_else(|| invalid("Reactor Optimization revision exhausted."))?;
        next.balances.insert(
            source,
            EnergyBalance {
                revision: next.revision,
                energy: source_energy,
            },
        );
        buff.revision = next.revision;
        next.reactor_optimization_buffs.insert(target, buff);
        next.last_reactor_optimization.insert(
            source,
            TransferSequence {
                epoch,
                sequence: effect,
            },
        );
        let directory = self
            .path
            .parent()
            .ok_or_else(|| invalid("Reactor Optimization ledger has no directory."))?;
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

    /// Hull Patch shares the world revision without changing shield receipts.
    #[test]
    fn hull_repair_is_atomic_and_replays_legacy_ledgers() {
        let root = std::env::temp_dir().join(format!("enb-hull-ledger-{}", rand::random::<u64>()));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("energy_transfers.local.json");
        let mut ledger = EnergyLedger::load(path.clone()).unwrap();
        let temporary = path.with_extension("json.tmp");
        fs::create_dir(&temporary).unwrap();
        assert!(ledger.repair_hull(1, 2, 7, 300, 75., 420.).is_err());
        assert_eq!(ledger.revision(), 0);
        fs::remove_dir(&temporary).unwrap();
        assert_eq!(ledger.repair_hull(1, 2, 7, 300, 75., 420.).unwrap(), 1);
        let mut restored = EnergyLedger::load(path).unwrap();
        assert!(restored.hull_repair_seen(1, 7, 300));
        assert_eq!(restored.balance(1).unwrap().energy, 75.);
        assert_eq!(restored.hull_balance(2).unwrap().hull, 420.);
        assert!(restored.repair_hull(1, 2, 7, 300, 75., 840.).is_err());
        assert_eq!(restored.hull_balance(2).unwrap().hull, 420.);
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
