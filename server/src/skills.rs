//! The class catalog is generated once from public content; runtime cooldowns
//! and casts are separate state. This keeps wire indices out of gameplay rules.
use crate::protocol::invalid;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::io;
use std::sync::OnceLock;
#[path = "jumpstart_rules.rs"]
mod jumpstart_rules;
pub use jumpstart_rules::JumpstartPlan;

#[derive(Clone, Debug, Deserialize)]
pub struct SkillDefinition {
    pub id: u32,
    pub name: String,
    pub level: u32,
    pub active: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AbilityKind {
    Cloak,
    Contemplation,
    Wormhole,
    Environment,
    Fold,
    Jumpstart,
    Leech,
    EnergyLeech,
    RechargeShields,
    HullPatch,
    EquipmentRepair,
    Enrage,
    GravityLink,
    Hacking,
    Befriend,
    Biorepression,
    Menace,
    Rally,
    ReactorOptimization,
    ShieldCharging,
    PsionicShield,
    RepulsorField,
    SelfDestruct,
    ShieldInversion,
    ShieldSap,
    Summon,
    PowerDown,
    Unsupported,
}

#[derive(Clone, Debug, Deserialize)]
pub struct AbilityDefinition {
    pub id: u32,
    pub skill: u32,
    pub rank: u32,
    pub name: String,
    pub description: String,
    pub kind: AbilityKind,
    pub energy: f32,
    pub charge: f32,
    pub cooldown: f32,
    pub range: u32,
    pub radius: u32,
    pub destination: Option<u32>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct SkillCatalog {
    #[serde(default = "je_index")]
    pub class_index: usize,
    pub skills: Vec<SkillDefinition>,
    pub abilities: Vec<AbilityDefinition>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct SkillRule {
    pub class_index: usize,
    pub skill: u32,
    pub track: String,
    pub scheme: String,
    pub quested: bool,
    pub learn_level: u32,
}

const LEVEL_GATES: [[i32; 9]; 5] = [
    [0, 0, 6, 12, 18, 24, 30, 36, 42],
    [0, 7, 14, 21, 28, 35, 42, 49, -1],
    [0, 0, 20, 40, 60, 80, 100, 120, 140],
    [0, 0, 30, 50, 70, 90, 110, 130, -1],
    [0, 0, 5, 15, 25, 35, 45, -1, -1],
];

impl SkillRule {
    /// Returns:
    ///     Option<SkillRule>: Archived requirement for this class skill, if present.
    pub fn for_skill(class_index: usize, skill: u32) -> io::Result<Option<Self>> {
        static RULES: OnceLock<Vec<SkillRule>> = OnceLock::new();
        let rules = RULES.get_or_init(|| {
            serde_json::from_str(include_str!("../data/skill_rules.json"))
                .expect("validated built-in archived skill rules")
        });
        Ok(rules
            .iter()
            .find(|rule| rule.class_index == class_index && rule.skill == skill)
            .cloned())
    }

    /// Returns:
    ///     Option<u32>: Required track level for the next rank, or None if prohibited.
    pub fn required_level(&self, current_rank: u32) -> Option<u32> {
        let scheme = match self.scheme.as_str() {
            "PW" => 0,
            "SW" => 1,
            "PT" => 2,
            "ST" => 3,
            "NON" => 4,
            _ => return None,
        };
        let required = *LEVEL_GATES[scheme].get(current_rank as usize)?;
        (required >= 0).then_some((required as u32).max(self.learn_level))
    }

    /// Returns:
    ///     Option<u32>: The pilot's level on this rule's required XP track.
    pub fn track_level(&self, inventory: &crate::inventory::Inventory) -> io::Result<Option<u32>> {
        let combat = crate::experience::track(inventory.xp_start_level, inventory.combat_xp)?.level;
        let trade = crate::experience::track(inventory.xp_start_level, inventory.trade_xp)?.level;
        let explore =
            crate::experience::track(inventory.xp_start_level, inventory.explore_xp)?.level;
        Ok(match self.track.as_str() {
            "combat" => Some(combat),
            "trade" => Some(trade),
            "explore" => Some(explore),
            "total" => Some(combat.saturating_add(trade).saturating_add(explore)),
            _ => None,
        })
    }

    /// Returns:
    ///     [u32; 4]: Native upgrade status, requirement and point cost fields.
    pub fn availability(
        &self,
        inventory: &crate::inventory::Inventory,
        current: u32,
        maximum: u32,
    ) -> io::Result<[u32; 4]> {
        if current >= maximum {
            return Ok([4, 0x40, 0, 0]);
        }
        if self.quested && current == 0 && !inventory.mission_awarded_skills.contains(&self.skill) {
            return Ok([3, 0, 0, 0]);
        }
        let Some(required) = self.required_level(current) else {
            return Ok([4, 0x04, 0, current.max(1)]);
        };
        let below_level = self
            .track_level(inventory)?
            .is_none_or(|level| level < required);
        let error = if below_level {
            match self.track.as_str() {
                "total" => 0x01,
                "combat" => 0x08,
                "trade" => 0x10,
                "explore" => 0x20,
                _ => 0x04,
            }
        } else if available_points(inventory)? < current.max(1) {
            0x02
        } else {
            0
        };
        Ok([
            4,
            error,
            if below_level { required } else { 0 },
            current.max(1),
        ])
    }
}

/// Returns:
///     u32: Earned points remaining after durable purchases.
pub fn available_points(inventory: &crate::inventory::Inventory) -> io::Result<u32> {
    let earned = [
        inventory.combat_xp,
        inventory.trade_xp,
        inventory.explore_xp,
    ]
    .into_iter()
    .map(|xp| crate::experience::track(inventory.xp_start_level, xp))
    .collect::<io::Result<Vec<_>>>()?
    .into_iter()
    .fold(0u64, |total, track| {
        total.saturating_add(track.skill_points)
    });
    let remaining = earned.saturating_sub(u64::from(inventory.spent_skill_points));
    Ok(remaining.min(u64::from(u32::MAX)) as u32)
}

/// Reject malformed saved training without changing older max-rank pilots.
pub fn validate_training(inventory: &crate::inventory::Inventory) -> io::Result<()> {
    let Some(trained) = &inventory.trained_skills else {
        if inventory.spent_skill_points != 0 {
            return Err(invalid("Skill points spent without trained ranks."));
        }
        return Ok(());
    };
    if trained.len() > 64
        || trained
            .iter()
            .any(|(&skill, &rank)| skill >= 64 || rank > 9)
    {
        return Err(invalid("Invalid trained skill ranks."));
    }
    let earned = [
        inventory.combat_xp,
        inventory.trade_xp,
        inventory.explore_xp,
    ]
    .into_iter()
    .map(|xp| crate::experience::track(inventory.xp_start_level, xp))
    .collect::<io::Result<Vec<_>>>()?
    .into_iter()
    .fold(0u64, |total, track| {
        total.saturating_add(track.skill_points)
    });
    if u64::from(inventory.spent_skill_points) > earned {
        return Err(invalid("Spent skill points exceed earned points."));
    }
    Ok(())
}

fn je_index() -> usize {
    5
}

impl SkillCatalog {
    pub fn for_profile(profile: &crate::account::CharacterProfile) -> io::Result<Self> {
        let catalogs: Vec<Self> = serde_json::from_str(include_str!("../data/class_skills.json"))?;
        if catalogs.len() != 9 {
            return Err(invalid("Incomplete class skill catalog."));
        }
        let class = profile.class();
        let catalog = catalogs
            .into_iter()
            .nth(class.0)
            .ok_or_else(|| invalid("Missing class catalog."))?;
        if catalog.class_index != class.0
            || catalog.skills.is_empty()
            || catalog
                .skills
                .iter()
                .any(|skill| skill.id >= 64 || skill.level == 0 || skill.level > 9)
            || catalog
                .abilities
                .iter()
                .any(|ability| ability.id >= 170 || catalog.level(ability.skill) < ability.rank)
        {
            return Err(invalid("Invalid class skill catalog."));
        }
        Ok(catalog)
    }
    pub fn load() -> io::Result<Self> {
        let catalog: Self = serde_json::from_str(include_str!("../data/je_skills.json"))?;
        if catalog.skills.len() != 18
            || catalog.abilities.len() != 27
            || catalog.abilities.iter().any(|ability| {
                ability.id >= 170
                    || !catalog
                        .skills
                        .iter()
                        .any(|skill| skill.id == ability.skill && skill.level >= ability.rank)
            })
        {
            return Err(invalid("invalid Jenquai Explorer skill catalog"));
        }
        Ok(catalog)
    }
    /// Returns:
    ///     SkillCatalog: Actual trained ranks, preserving legacy maxed characters.
    pub fn for_inventory(
        profile: &crate::account::CharacterProfile,
        inventory: &crate::inventory::Inventory,
    ) -> io::Result<Self> {
        validate_training(inventory)?;
        let mut catalog = Self::for_profile(profile)?;
        if let Some(trained) = &inventory.trained_skills {
            for (&id, &rank) in trained {
                let ceiling = catalog
                    .skills
                    .iter()
                    .find(|definition| definition.id == id)
                    .ok_or_else(|| invalid("Trained skill is not owned by this class."))?;
                if rank > ceiling.level {
                    return Err(invalid("Trained skill exceeds its class ceiling."));
                }
            }
            for skill in &mut catalog.skills {
                skill.level = trained.get(&skill.id).copied().unwrap_or(0);
            }
        }
        Ok(catalog)
    }

    /// Returns:
    ///     Inventory: Durable candidate after one native rank purchase.
    pub fn purchased_rank(
        &self,
        inventory: &crate::inventory::Inventory,
        skill_id: u32,
    ) -> io::Result<crate::inventory::Inventory> {
        let trained = inventory
            .trained_skills
            .as_ref()
            .ok_or_else(|| invalid("This sandbox pilot already has maximum training."))?;
        let ceiling = Self::for_class_index(self.class_index)?
            .skills
            .into_iter()
            .find(|skill| skill.id == skill_id)
            .ok_or_else(|| invalid("This class cannot train that skill."))?
            .level;
        let current = trained.get(&skill_id).copied().unwrap_or(0);
        if current >= ceiling {
            return Err(invalid("Skill is already at its class maximum."));
        }
        let rule = SkillRule::for_skill(self.class_index, skill_id)?
            .ok_or_else(|| invalid("Archived training requirement is unavailable."))?;
        if rule.quested && !inventory.mission_awarded_skills.contains(&skill_id) && current == 0 {
            return Err(invalid("This skill requires its mission unlock."));
        }
        let requirement = rule
            .required_level(current)
            .ok_or_else(|| invalid("This rank is unavailable under its skill scheme."))?;
        if rule
            .track_level(inventory)?
            .is_none_or(|level| level < requirement)
        {
            return Err(invalid("Pilot level is too low for this skill rank."));
        }
        let cost = current.max(1);
        if available_points(inventory)? < cost {
            return Err(invalid("Not enough skill points."));
        }
        let mut candidate = inventory.clone();
        candidate.spent_skill_points = candidate
            .spent_skill_points
            .checked_add(cost)
            .ok_or_else(|| invalid("Skill point balance overflow."))?;
        candidate
            .trained_skills
            .as_mut()
            .expect("validated training map")
            .insert(skill_id, current + 1);
        Ok(candidate)
    }

    /// Returns:
    ///     SkillCatalog: Validated immutable class ceilings before training.
    fn for_class_index(index: usize) -> io::Result<Self> {
        let catalogs: Vec<Self> = serde_json::from_str(include_str!("../data/class_skills.json"))?;
        catalogs
            .into_iter()
            .find(|catalog| catalog.class_index == index)
            .ok_or_else(|| invalid("Missing class catalog."))
    }
    pub fn ability(&self, id: u32) -> io::Result<AbilityDefinition> {
        self.abilities
            .iter()
            .find(|ability| ability.id == id && self.level(ability.skill) >= ability.rank)
            .cloned()
            .ok_or_else(|| invalid("This character does not have that ability."))
    }
    pub fn level(&self, skill: u32) -> u32 {
        self.skills
            .iter()
            .find(|definition| definition.id == skill)
            .map_or(0, |definition| definition.level)
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Cast {
    pub ability: u32,
    pub target: u32,
    pub remaining: f32,
    pub effect: u32,
    /// Absent only for old checkpoints or other abilities. Old in-flight
    /// Jumpstarts retain their committed cost, remaining time and legacy repair.
    #[serde(default)]
    pub jumpstart: Option<JumpstartPlan>,
    #[serde(default)]
    pub recharge_amount: Option<f32>,
    #[serde(default)]
    pub hull_repair_amount: Option<f32>,
    #[serde(default)]
    pub gravity_generation: Option<u64>,
    #[serde(default)]
    pub menace_generation: Option<u64>,
    #[serde(default)]
    pub hacking_generation: Option<u64>,
    #[serde(default)]
    pub befriend_generation: Option<u64>,
    pub bio_generation: Option<u64>,
    #[serde(default)]
    pub enrage_generation: Option<u64>,
    #[serde(default)]
    pub summon_generation: Option<u64>,
    #[serde(default)]
    pub sap_generation: Option<u64>,
    #[serde(default)]
    pub leech_generation: Option<u64>,
    #[serde(default)]
    pub inversion_generation: Option<u64>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PendingShieldRecharge {
    pub target: u32,
    pub effect: u32,
    pub amount: f32,
    #[serde(default)]
    pub splash_amount: f32,
    pub range: f32,
    #[serde(default)]
    pub radius: f32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PendingHullRepair {
    pub target: u32,
    pub effect: u32,
    pub amount: f32,
    #[serde(default)]
    pub splash_amount: f32,
    pub range: f32,
    #[serde(default)]
    pub radius: f32,
}

impl PendingHullRepair {
    /// Returns:
    ///     bool: Whether this frozen repair can be settled after replacement.
    pub fn valid(&self) -> bool {
        self.target != 0
            && self.effect != 0
            && self.amount.is_finite()
            && self.amount > 0.
            && self.amount <= 51_840.
            && self.splash_amount.is_finite()
            && (0. ..=16_200.).contains(&self.splash_amount)
            && (self.radius == 0. || self.splash_amount > 0.)
            && self.range.is_finite()
            && (0. ..=10_000.).contains(&self.range)
            && self.radius.is_finite()
            && (0. ..=4_000.).contains(&self.radius)
    }
}

impl PendingShieldRecharge {
    /// Returns:
    ///     bool: Whether the saved recharge can be retried safely.
    pub fn valid(&self) -> bool {
        self.target != 0
            && self.effect != 0
            && self.amount.is_finite()
            && self.amount > 0.
            && self.amount <= 51_840.
            && self.splash_amount.is_finite()
            && (0. ..=16_200.).contains(&self.splash_amount)
            && (self.radius == 0. || self.splash_amount > 0.)
            && self.range.is_finite()
            && (0. ..=10_000.).contains(&self.range)
            && self.radius.is_finite()
            && (0. ..=4_000.).contains(&self.radius)
    }
}

impl Cast {
    pub fn valid(&self) -> bool {
        self.ability < 170
            && self.remaining.is_finite()
            && self.remaining >= 0.
            && self.jumpstart.as_ref().is_none_or(|plan| {
                self.ability == 65 && plan.valid() && self.remaining <= plan.charge_seconds
            })
            && self.recharge_amount.is_none_or(|amount| {
                matches!(self.ability, 14 | 32 | 61 | 89 | 91)
                    && amount.is_finite()
                    && (1. ..=51_840.).contains(&amount)
            })
            && self.hull_repair_amount.is_none_or(|amount| {
                matches!(self.ability, 9 | 31 | 60 | 77 | 95)
                    && amount.is_finite()
                    && (1. ..=51_840.).contains(&amount)
            })
            && self.gravity_generation.is_none_or(|generation| {
                matches!(self.ability, 10 | 12 | 47 | 59 | 73) && generation < 1_000_000_000
            })
            && self.menace_generation.is_none_or(|generation| {
                matches!(self.ability, 11 | 18 | 64 | 99 | 127) && generation < 1_000_000_000
            })
            && self.hacking_generation.is_none_or(|generation| {
                matches!(self.ability, 13 | 16 | 57 | 58 | 76) && generation < 1_000_000_000
            })
            && self.befriend_generation.is_none_or(|generation| {
                matches!(self.ability, 15 | 20 | 43 | 63 | 115) && generation < 1_000_000_000
            })
            && self.bio_generation.is_none_or(|generation| {
                matches!(self.ability, 21..=25) && generation < 1_000_000_000
            })
            && self.enrage_generation.is_none_or(|generation| {
                matches!(self.ability, 6 | 7 | 27 | 41 | 42) && generation < 1_000_000_000
            })
            && self.summon_generation.is_none_or(|generation| {
                matches!(self.ability, 117 | 118) && generation < 1_000_000_000
            })
            && self.sap_generation.is_none_or(|generation| {
                matches!(self.ability, 54 | 55 | 98 | 112 | 114) && generation < 1_000_000_000
            })
            && self.leech_generation.is_none_or(|generation| {
                matches!(self.ability, 38 | 39 | 40 | 92 | 93) && generation < 1_000_000_000
            })
            && self.inversion_generation.is_none_or(|generation| {
                matches!(self.ability, 105 | 107 | 110 | 111 | 113) && generation < 1_000_000_000
            })
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct SkillState {
    pub cast: Option<Cast>,
    pub cooldowns: BTreeMap<u32, f32>,
    pub cloak_rank: u32,
    pub cloak_effect: Option<u32>,
    pub environment_rank: u32,
    pub environment_seconds: f32,
    pub environment_effect: Option<u32>,
    pub combat_cloak_seconds: f32,
    #[serde(default)]
    pub cloak_source: Option<u32>,
    #[serde(default)]
    pub cloak_stealth: Option<u32>,
    #[serde(default)]
    pub temporary_effects: BTreeMap<u32, f32>,
    #[serde(default)]
    pub pending_shield_recharge: Option<PendingShieldRecharge>,
    #[serde(default)]
    pub pending_hull_repair: Option<PendingHullRepair>,
    #[serde(default)]
    pub pending_rally: Option<crate::rally::PendingRally>,
    #[serde(default)]
    pub pending_shield_sap: Option<crate::shield_sap::PendingShieldSap>,
    #[serde(default)]
    pub pending_energy_leech: Option<crate::energy_leech::PendingEnergyLeech>,
    #[serde(default)]
    pub pending_reactor_optimization:
        Option<crate::reactor_optimization::PendingReactorOptimization>,
    #[serde(default)]
    pub pending_shield_charging: Option<crate::shield_charging::PendingShieldCharging>,
    #[serde(default)]
    pub pending_psionic_shield: Option<crate::psionic_shield::PendingPsionicShield>,
    #[serde(default)]
    pub pending_repulsor_field: Option<crate::repulsor_field::PendingRepulsorField>,
    #[serde(default)]
    pub active_inversion: Option<crate::shield_inversion::ActiveShieldInversion>,
    #[serde(default)]
    pub pending_self_destruct: Option<crate::self_destruct::PendingSelfDestruct>,
    #[serde(default)]
    pub self_destruct_until_ms: u64,
    #[serde(default)]
    pub equipment_repair: Option<crate::equipment_repair::ActiveEquipmentRepair>,
    #[serde(default)]
    pub power_down_ability: Option<u32>,
    #[serde(default)]
    pub pending_equipment_repair: Option<crate::equipment_repair::PendingEquipmentRepair>,
}

#[derive(Clone, Debug)]
pub struct GroupContact {
    pub id: u32,
    pub sector: u32,
    pub position: [f32; 3],
    pub incapacitated: bool,
}

#[derive(Clone, Debug)]
pub enum SupportAction {
    Cloak {
        enabled: bool,
        rank: u32,
        stealth: u32,
    },
    Environment {
        target: u32,
        rank: u32,
    },
    Jumpstart {
        target: u32,
        repair_fraction: f32,
        range: f32,
    },
    Fold {
        target: u32,
    },
    Summon {
        ability: u32,
        trained_rank: u32,
        target: u32,
        origin_sector: u32,
        origin_position: [f32; 3],
        origin_heading: [f32; 3],
    },
    Energy {
        amount: f32,
    },
    Wormhole {
        destination: u32,
        origin_sector: u32,
        origin: [f32; 3],
    },
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn native_indices_and_class_limits_are_preserved() {
        let catalog = SkillCatalog::load().unwrap();
        assert_eq!(catalog.level(24), 5); // JE has Fold Space 5, not area fold 7.
        assert_eq!(catalog.level(1), 9);
        assert_eq!(catalog.ability(0).unwrap().kind, AbilityKind::Cloak);
        assert_eq!(catalog.ability(37).unwrap().destination, Some(2210));
        assert!(catalog.ability(17).is_err()); // Area Teleport is not a JE ability.
    }

    /// XML and public SQL disagree in places; only evidenced gates can be bought.
    #[test]
    fn archived_training_rules_gate_class_track_and_missing_entries() {
        let je = SkillRule::for_skill(5, 1).unwrap().unwrap();
        assert_eq!(je.track, "combat");
        assert_eq!(je.scheme, "PW");
        assert_eq!(je.required_level(1), Some(0));
        assert_eq!(je.required_level(2), Some(6));
        assert!(SkillRule::for_skill(4, 24).unwrap().is_none());
        assert_eq!(SkillRule::for_skill(5, 1).unwrap().unwrap().skill, 1);
    }

    /// Old inventory JSON still means maximum trained class ranks.
    #[test]
    fn legacy_maxed_inventory_does_not_gain_spent_points_or_lose_abilities() {
        let items = crate::items::ItemCatalog::load().unwrap();
        let inventory = crate::inventory::Inventory::starter(&items).unwrap();
        let profile = crate::account::Account::create("legacy".into(), "pass", "Legacy".into())
            .unwrap()
            .character;
        let catalog = SkillCatalog::for_inventory(&profile, &inventory).unwrap();
        assert_eq!(
            catalog.level(1),
            SkillCatalog::for_profile(&profile).unwrap().level(1)
        );
        assert_eq!(inventory.spent_skill_points, 0);
        assert!(catalog.ability(0).is_ok());
    }
}
