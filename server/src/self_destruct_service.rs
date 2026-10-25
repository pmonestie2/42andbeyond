//! Caster-side Self Destruct preparation, resource settlement and action lock.
use super::*;
use crate::self_destruct::{self, BlastReceipt, PendingSelfDestruct};
use crate::skills::{AbilityDefinition, Cast};

impl PlayerSession {
    /// Returns:
    ///     bool: Whether a pending blast or recovery still prevents ship actions.
    pub(crate) fn self_destruct_action_locked(&self) -> bool {
        self.skills.pending_self_destruct.is_some()
            || self.skills.self_destruct_until_ms > crate::resources::unix_ms()
    }

    /// Freeze the caster pose and charge identity before world damage.
    pub(super) fn prepare_self_destruct(
        &mut self,
        ability: &AbilityDefinition,
        cast: &Cast,
    ) -> io::Result<()> {
        if self.skills.pending_self_destruct.is_some()
            || self.self_destruct_action_locked()
            || self.hull <= 0.
            || self.warp.active()
            || self.sector.station.is_some()
        {
            return Err(invalid("Self Destruct is unavailable."));
        }
        let trained_rank = self.skill_catalog.level(self_destruct::SKILL_ID);
        self_destruct::tier(ability.id, trained_rank)
            .ok_or_else(|| invalid("Self Destruct rank is not trained."))?;
        let pending = PendingSelfDestruct {
            source: self.player_id(),
            source_epoch: self.session_epoch,
            sector: self.sector.id,
            effect: cast.effect,
            ability: ability.id,
            trained_rank,
            damage_bonus_percent: self.equipment.ship.self_destruct_damage_bonus_pct,
            position: self.position,
        };
        if !pending.valid() {
            return Err(invalid("Invalid Self Destruct preparation."));
        }
        self.skills.pending_self_destruct = Some(pending);
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.pending_self_destruct = None;
            return Err(error);
        }
        self.motion.stop();
        self.stop_weapons();
        Ok(())
    }

    /// Returns:
    ///     Option<PendingSelfDestruct>: Durable blast awaiting sector settlement.
    pub(crate) fn pending_self_destruct(&self) -> Option<PendingSelfDestruct> {
        self.skills.pending_self_destruct
    }

    /// Returns:
    ///     std::sync::Arc<FactionReactions>: Current hostile MOB relationships.
    pub(crate) fn self_destruct_reputation(
        &self,
    ) -> std::sync::Arc<crate::factions::FactionReactions> {
        std::sync::Arc::clone(&self.reputation)
    }

    /// Apply a committed sector result once, emptying resources and locking controls.
    pub(crate) fn settle_self_destruct(&mut self, receipt: &BlastReceipt) -> io::Result<()> {
        let pending = self
            .skills
            .pending_self_destruct
            .ok_or_else(|| invalid("Self Destruct is no longer pending."))?;
        if !receipt.valid()
            || receipt.source != pending.source
            || receipt.source_epoch != pending.source_epoch
            || receipt.sector != pending.sector
            || receipt.effect != pending.effect
            || receipt.ability != pending.ability
            || receipt.trained_rank != pending.trained_rank
            || receipt.damage_bonus_percent != pending.damage_bonus_percent
        {
            return Err(invalid("Self Destruct receipt mismatch."));
        }
        let previous_energy = self.energy;
        let previous_shield = self.shield;
        let previous_lock = self.skills.self_destruct_until_ms;
        let previous_motion = self.motion.clone();
        let previous_warp = self.warp.clone();
        let previous_player_firing = self.player_firing;
        let previous_weapon_firing = self.weapon_firing;
        self.energy = 0.;
        self.shield = 0.;
        self.skills.pending_self_destruct = None;
        self.skills.self_destruct_until_ms =
            crate::resources::unix_ms().saturating_add(self_destruct::ACTION_LOCK_MS);
        self.warp.reset(&mut self.motion);
        self.motion.stop();
        self.player_firing = false;
        self.weapon_firing.fill(false);
        if let Err(error) = self.checkpoint_session(true) {
            self.energy = previous_energy;
            self.shield = previous_shield;
            self.skills.pending_self_destruct = Some(pending);
            self.skills.self_destruct_until_ms = previous_lock;
            self.motion = previous_motion;
            self.warp = previous_warp;
            self.player_firing = previous_player_firing;
            self.weapon_firing = previous_weapon_firing;
            return Err(error);
        }
        self.pending.push(packets::ship_resources(
            self.player_id(),
            &self.equipment.ship,
            Some(self.energy),
            Some(self.shield),
            None,
        ));
        self.pending.push(packets::player_movement(
            self.player_id(),
            self.position,
            self.tick_ms(),
            &self.motion,
        ));
        self.publish_weapon_states();
        self.pending.push(packets::object_effect(
            self.player_id(),
            receipt.effect,
            self_destruct::VISUAL_DESCRIPTOR,
            self.tick_ms(),
        ));
        self.message("Self Destruct discharged. Ship systems are recovering.");
        Ok(())
    }
}
