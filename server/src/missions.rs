//! Public mission definitions and durable per-character journal state.
//! This first runtime slice offers NPC-started missions and records stage one;
//! later objective and reward handlers must advance the same journal.
use crate::account::CharacterProfile;
use crate::experience;
use crate::inventory::Inventory;
use crate::npcs::{Branch, TalkNode};
use crate::protocol::invalid;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::io;
use std::sync::{Arc, OnceLock};

#[derive(Clone, Debug, Deserialize)]
pub struct MissionCondition {
    pub kind: u32,
    pub data: i64,
    pub flags: i64,
}

#[derive(Clone, Debug, Deserialize)]
pub struct MissionCompletion {
    pub kind: u32,
    pub data: i64,
    pub count: u32,
}

#[derive(Clone, Debug, Deserialize)]
pub struct MissionBranch {
    pub node: u8,
    pub text: String,
}

#[derive(Clone, Debug, Deserialize)]
pub struct MissionNode {
    pub id: u8,
    pub text: String,
    pub flag: u32,
    pub destination: i32,
    pub branches: Vec<MissionBranch>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct MissionStage {
    pub index: u32,
    pub description: String,
    pub completion: Vec<MissionCompletion>,
    #[serde(default)]
    pub rewards: Vec<MissionReward>,
    pub nodes: Vec<MissionNode>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct MissionReward {
    pub kind: u32,
    pub data: MissionRewardData,
    pub flags: i64,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(untagged)]
pub enum MissionRewardData {
    Number(i64),
    Script(String),
}

#[derive(Clone, Debug, Deserialize)]
pub struct MissionDefinition {
    pub id: u32,
    pub name: String,
    pub summary: String,
    pub forfeitable: bool,
    pub time_limit: u32,
    pub conditions: Vec<MissionCondition>,
    pub stages: Vec<MissionStage>,
}

#[derive(Deserialize)]
struct CatalogFile {
    version: u32,
    missions: Vec<MissionDefinition>,
}

#[derive(Clone)]
pub struct MissionCatalog(Arc<BTreeMap<u32, MissionDefinition>>);

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct MissionJournal {
    #[serde(default)]
    pub active: Vec<MissionProgress>,
    #[serde(default)]
    pub completed: BTreeSet<u32>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct MissionProgress {
    pub id: u32,
    pub stage: u32,
    pub started_ms: u64,
    #[serde(default)]
    pub counts: BTreeMap<usize, u32>,
}

impl MissionCatalog {
    /// Returns:
    ///     MissionCatalog: Cached, validated public mission definitions.
    pub fn load() -> io::Result<Self> {
        static CATALOG: OnceLock<Arc<BTreeMap<u32, MissionDefinition>>> = OnceLock::new();
        if let Some(missions) = CATALOG.get() {
            return Ok(Self(Arc::clone(missions)));
        }
        let file: CatalogFile = serde_json::from_str(include_str!("../data/missions.json"))?;
        if file.version != 1 || file.missions.len() > 1_000 {
            return Err(invalid("Invalid public mission catalog version or size."));
        }
        let mut missions = BTreeMap::new();
        for mission in file.missions {
            if mission.id == 0
                || mission.name.is_empty()
                || mission.name.len() > 256
                || mission.summary.len() > 1024
                || !mission.name.is_ascii()
                || !mission.summary.is_ascii()
                || mission.stages.is_empty()
                || mission.stages.len() > 20
                || mission.stages.iter().enumerate().any(|(index, stage)| {
                    stage.index != index as u32
                        || stage.description.len() >= 512
                        || stage.nodes.len() > 255
                        || stage.nodes.iter().any(|node| {
                            node.id == 0 || node.text.len() > 2048 || node.branches.len() > 255
                        })
                })
                || missions.insert(mission.id, mission).is_some()
            {
                return Err(invalid("Invalid or duplicate public mission definition."));
            }
        }
        let _ = CATALOG.set(Arc::new(missions));
        Ok(Self(Arc::clone(CATALOG.get().unwrap())))
    }

    /// Returns:
    ///     &MissionDefinition: Public definition for an active mission.
    pub fn get(&self, id: u32) -> io::Result<&MissionDefinition> {
        self.0
            .get(&id)
            .ok_or_else(|| invalid("Unknown public mission."))
    }

    /// Returns:
    ///     Option<u32>: First eligible NPC-started mission in native ID order.
    pub fn offer(
        &self,
        npc: u32,
        profile: &CharacterProfile,
        inventory: &Inventory,
    ) -> Option<u32> {
        self.0.values().find_map(|mission| {
            (matches!(mission.id, 3 | 90 | 102 | 104 | 341)
                && mission.stages.len() >= 2
                && mission.time_limit == 0
                && !mission.forfeitable
                && Self::start_tree_supported(&mission.stages[0])
                && mission.stages[0]
                    .completion
                    .iter()
                    .any(|entry| entry.kind == 1 && entry.data == i64::from(npc))
                && !inventory.missions.contains(mission.id)
                && inventory.missions.active.len() < 12
                && self.conditions_met(mission, profile, inventory))
            .then_some(mission.id)
        })
    }

    /// Returns:
    ///     Option<(u32, u32)>: Active mission and stage awaiting this NPC.
    pub fn active_for_npc(&self, npc: u32, journal: &MissionJournal) -> Option<(u32, u32)> {
        journal.active.iter().find_map(|progress| {
            let stage = self
                .get(progress.id)
                .ok()?
                .stages
                .get(progress.stage as usize)?;
            (stage
                .completion
                .iter()
                .any(|objective| objective.kind == 1 && objective.data == i64::from(npc))
                && stage.nodes.iter().any(|node| node.id == 1))
            .then_some((progress.id, progress.stage))
        })
    }

    /// Returns:
    ///     bool: Whether the offer conversation has only supported, linked nodes.
    fn start_tree_supported(stage: &MissionStage) -> bool {
        let ids: BTreeSet<u8> = stage.nodes.iter().map(|node| node.id).collect();
        ids.len() == stage.nodes.len()
            && ids.contains(&1)
            && stage
                .nodes
                .iter()
                .any(|node| node.flag == 5 && node.destination == 1)
            && stage.nodes.iter().all(|node| {
                matches!(node.flag, 0 | 2 | 5)
                    && (node.flag != 5 || node.destination == 1)
                    && node
                        .branches
                        .iter()
                        .all(|branch| ids.contains(&branch.node))
            })
    }

    /// Returns:
    ///     TalkNode: A branch-validated native NPC dialogue node.
    pub fn start_node(&self, mission: u32, node: u8) -> io::Result<TalkNode> {
        self.stage_node(mission, 0, node)
    }

    /// Returns:
    ///     TalkNode: Public dialogue for an offered or active mission stage.
    pub fn stage_node(&self, mission: u32, stage: u32, node: u8) -> io::Result<TalkNode> {
        let entry = self
            .get(mission)?
            .stages
            .get(stage as usize)
            .and_then(|stage| stage.nodes.iter().find(|entry| entry.id == node))
            .ok_or_else(|| invalid("Mission conversation node does not exist."))?;
        Ok(TalkNode {
            id: entry.id,
            text: entry.text.clone(),
            flag: entry.flag,
            branches: entry
                .branches
                .iter()
                .map(|branch| Branch {
                    node: branch.node,
                    text: branch.text.clone(),
                })
                .collect(),
        })
    }

    /// Returns:
    ///     bool: Whether supported native restrictions allow this pilot.
    fn conditions_met(
        &self,
        mission: &MissionDefinition,
        profile: &CharacterProfile,
        inventory: &Inventory,
    ) -> bool {
        let combat = experience::track(inventory.xp_start_level, inventory.combat_xp).ok();
        let trade = experience::track(inventory.xp_start_level, inventory.trade_xp).ok();
        let explore = experience::track(inventory.xp_start_level, inventory.explore_xp).ok();
        let Some((combat, trade, explore)) =
            combat.zip(trade).zip(explore).map(|((c, t), e)| (c, t, e))
        else {
            return false;
        };
        let race = mission
            .conditions
            .iter()
            .filter(|entry| entry.kind == 5)
            .collect::<Vec<_>>();
        let profession = mission
            .conditions
            .iter()
            .filter(|entry| entry.kind == 6)
            .collect::<Vec<_>>();
        if !race.is_empty()
            && !race
                .iter()
                .any(|entry| entry.data == i64::from(profile.race))
        {
            return false;
        }
        if !profession.is_empty()
            && !profession
                .iter()
                .any(|entry| entry.data == i64::from(profile.profession))
        {
            return false;
        }
        mission.conditions.iter().all(|entry| match entry.kind {
            1 => i64::from(combat.level + trade.level + explore.level) >= entry.data,
            2 => i64::from(combat.level) >= entry.data,
            3 => i64::from(explore.level) >= entry.data,
            4 => i64::from(trade.level) >= entry.data,
            5 | 6 => true,
            // Hull upgrade and reputation checks await their progression service.
            7 | 8 => false,
            9 => {
                inventory
                    .cargo
                    .iter()
                    .flatten()
                    .filter(|item| i64::from(item.template) == entry.flags)
                    .map(|item| u64::from(item.quantity))
                    .sum::<u64>()
                    >= entry.data.max(0) as u64
            }
            10 => u32::try_from(entry.data)
                .ok()
                .is_some_and(|id| inventory.missions.completed.contains(&id)),
            _ => false,
        })
    }
}

impl MissionJournal {
    /// Returns:
    ///     bool: Whether this mission is active or already completed.
    pub fn contains(&self, id: u32) -> bool {
        self.completed.contains(&id) || self.active.iter().any(|mission| mission.id == id)
    }

    /// Add stage one only after the NPC's offered branch is selected.
    pub fn start(&mut self, id: u32, now_ms: u64, catalog: &MissionCatalog) -> io::Result<()> {
        let mission = catalog.get(id)?;
        if now_ms == 0 || self.contains(id) || self.active.len() >= 12 || mission.stages.len() < 2 {
            return Err(invalid("Mission cannot be started for this pilot."));
        }
        self.active.push(MissionProgress {
            id,
            stage: 1,
            started_ms: now_ms,
            counts: BTreeMap::new(),
        });
        Ok(())
    }

    /// Advance only along the chosen native dialogue destination.
    pub fn advance(
        &mut self,
        id: u32,
        destination: u32,
        catalog: &MissionCatalog,
    ) -> io::Result<()> {
        let progress = self
            .active
            .iter_mut()
            .find(|progress| progress.id == id)
            .ok_or_else(|| invalid("Mission is not active."))?;
        let mission = catalog.get(id)?;
        if destination != progress.stage + 1 || destination as usize >= mission.stages.len() {
            return Err(invalid("Mission stage destination is invalid."));
        }
        progress.stage = destination;
        progress.counts.clear();
        Ok(())
    }

    /// Move a completed final stage to durable mission history.
    pub fn complete(&mut self, id: u32, catalog: &MissionCatalog) -> io::Result<()> {
        let index = self
            .active
            .iter()
            .position(|progress| progress.id == id)
            .ok_or_else(|| invalid("Mission is not active."))?;
        if self.active[index].stage as usize + 1 != catalog.get(id)?.stages.len() {
            return Err(invalid("Mission final stage is not current."));
        }
        self.active.remove(index);
        self.completed.insert(id);
        Ok(())
    }

    /// Count a destroying pilot's MOB kill and advance a complete kill stage.
    ///
    /// Returns:
    ///     Option<(u32, u32)>: Mission ID and new stage when one advanced.
    pub fn credit_kill(
        &mut self,
        template: u32,
        catalog: &MissionCatalog,
    ) -> io::Result<Option<(u32, u32)>> {
        if template == 0 {
            return Ok(None);
        }
        for progress in &mut self.active {
            let definition = catalog.get(progress.id)?;
            let stage = &definition.stages[progress.stage as usize];
            let mut changed = false;
            for (index, objective) in stage.completion.iter().enumerate() {
                if objective.kind == 4 && objective.data == i64::from(template) {
                    let count = progress.counts.entry(index).or_default();
                    if *count < objective.count {
                        *count += 1;
                        changed = true;
                    }
                }
            }
            if !changed {
                continue;
            }
            if stage
                .completion
                .iter()
                .enumerate()
                .all(|(index, objective)| {
                    objective.kind == 4
                        && progress.counts.get(&index).copied().unwrap_or(0) >= objective.count
                })
                && (progress.stage as usize + 1) < definition.stages.len()
            {
                progress.stage += 1;
                progress.counts.clear();
                return Ok(Some((progress.id, progress.stage)));
            }
        }
        Ok(None)
    }

    /// Reject malformed or stale persisted stage references.
    pub fn validate(&self, catalog: &MissionCatalog) -> io::Result<()> {
        if self.active.len() > 12 || self.completed.len() > 1_000 {
            return Err(invalid("Mission journal capacity exceeded."));
        }
        let mut active = BTreeSet::new();
        for progress in &self.active {
            let mission = catalog.get(progress.id)?;
            if !active.insert(progress.id)
                || self.completed.contains(&progress.id)
                || progress.started_ms == 0
                || progress.stage == 0
                || progress.stage as usize >= mission.stages.len()
                || progress.counts.iter().any(|(index, count)| {
                    mission.stages[progress.stage as usize]
                        .completion
                        .get(*index)
                        .is_none_or(|objective| objective.kind != 4 || *count > objective.count)
                })
            {
                return Err(invalid("Invalid active mission stage."));
            }
        }
        for id in &self.completed {
            catalog.get(*id)?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::account::CharacterProfile;

    #[test]
    fn public_catalog_offers_earthcorps_starter_only_to_eligible_pilots() {
        let catalog = MissionCatalog::load().unwrap();
        assert_eq!(catalog.0.len(), 364);
        let mut profile = CharacterProfile {
            game_id: 0x40000001,
            name: "Pilot".into(),
            race: 0,
            profession: 0,
        };
        let items = crate::items::ItemCatalog::load().unwrap();
        let mut inventory = Inventory::starter(&items).unwrap();
        inventory.xp_start_level = 0;
        assert_eq!(catalog.offer(46, &profile, &inventory), Some(102));
        profile.race = 1;
        assert_ne!(catalog.offer(46, &profile, &inventory), Some(102));
        profile.race = 0;
        inventory.missions.start(102, 1, &catalog).unwrap();
        assert_ne!(catalog.offer(46, &profile, &inventory), Some(102));
        inventory.missions.validate(&catalog).unwrap();
    }
}
