//! Caster-side Shield Inversion activation and per-pulse shield settlement.
use super::*;
use crate::shield_inversion::{self, ActiveShieldInversion, InversionReceipt};
use crate::skills::{AbilityDefinition, Cast};

impl PlayerSession {
    /// Returns:
    ///     u64: Selected hostile MOB generation frozen for the timed cast.
    pub(super) fn inversion_target_generation(&self, target: u32) -> io::Result<u64> {
        let index = target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Select a hostile MOB for Shield Inversion."))?;
        let combat = lock_combat(&self.combat)?;
        let mob = combat
            .actors
            .get(index as usize)
            .ok_or_else(|| invalid("Shield Inversion target is missing."))?;
        if !self.mob_visible_in_scan(index as usize, mob)
            || mob.state == MobState::Dead
            || self.mob_relationship(mob) != 0
        {
            return Err(invalid("Shield Inversion requires a visible hostile MOB."));
        }
        Ok(mob.death_generation)
    }

    /// Returns:
    ///     bool: Whether this living ship can continue an offensive shield cast.
    pub(crate) fn inversion_available(&self) -> bool {
        self.logged_in
            && self.hull > 0.
            && self.shield > 0.
            && self.sector.station.is_none()
            && !self.warp.active()
            && self.gate_transit.is_none()
    }

    /// Start one timed inversion after the selected MOB generation was frozen.
    ///
    /// Returns:
    ///     bool: Whether the original target remained eligible through charge.
    pub(super) fn start_shield_inversion(
        &mut self,
        ability: &AbilityDefinition,
        cast: &Cast,
    ) -> io::Result<bool> {
        let trained = self.skill_catalog.level(52);
        let tier = shield_inversion::tier(ability.id, trained)
            .ok_or_else(|| invalid("Shield Inversion rank is not trained."))?;
        let generation = cast
            .inversion_generation
            .ok_or_else(|| invalid("Shield Inversion target was lost."))?;
        if !self.inversion_available() || self.skills.active_inversion.is_some() {
            return Err(invalid(
                "Shield Inversion is unavailable or already active.",
            ));
        }
        if !self
            .inversion_target_generation(cast.target)
            .is_ok_and(|current| current == generation)
        {
            return Ok(false);
        }
        let index = cast.target.saturating_sub(MOB_ID) as usize;
        if lock_combat(&self.combat)?.actors[index].range_to(self.position) > tier.range {
            return Ok(false);
        }
        let now = crate::resources::unix_ms();
        let duration =
            shield_inversion::duration_ms(self.equipment.ship.shield_inversion_duration_bonus);
        let active = ActiveShieldInversion {
            source: self.player_id(),
            sector: self.sector.id,
            ability: ability.id,
            trained_rank: trained,
            target: cast.target,
            target_generation: generation,
            source_epoch: self.session_epoch,
            effect: cast.effect,
            expires_at_ms: now.saturating_add(duration),
            next_pulse_ms: now.saturating_add(shield_inversion::PULSE_MS),
            pulse_index: 0,
        };
        if !active.valid() {
            return Err(invalid("Invalid Shield Inversion duration."));
        }
        self.skills.active_inversion = Some(active);
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.active_inversion = None;
            return Err(error);
        }
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            skill_status::SHIELD_INVERSION_SLOT,
            "Shield_Inversion",
            self.tick_ms().wrapping_add(duration as u32),
        ));
        if ability.id == 110 {
            self.pending.push(packets::object_effect(
                self.player_id(),
                cast.effect,
                shield_inversion::SHIELD_NOVA_EFFECT,
                self.tick_ms(),
            ));
            self.pending.push(packets::weapon_effect(
                self.player_id(),
                cast.target,
                shield_inversion::SHIELD_NOVA_EFFECT,
                cast.effect.wrapping_add(1).max(1),
                self.tick_ms(),
                duration.min(u16::MAX as u64) as u16,
            ));
        }
        Ok(true)
    }

    /// Returns:
    ///     Option<ActiveShieldInversion>: Current saved timed cast, if present.
    pub(crate) fn active_inversion(&self) -> Option<ActiveShieldInversion> {
        self.skills.active_inversion
    }

    /// Replay the timed native status and verified Nova visual after replacement.
    pub(super) fn publish_inversion_resume(&mut self) {
        let Some(active) = self.skills.active_inversion else {
            return;
        };
        let remaining = active
            .expires_at_ms
            .saturating_sub(crate::resources::unix_ms());
        if remaining == 0 {
            return;
        }
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            skill_status::SHIELD_INVERSION_SLOT,
            "Shield_Inversion",
            self.tick_ms().wrapping_add(remaining as u32),
        ));
        if active.ability == 110 {
            self.pending.push(packets::object_effect(
                self.player_id(),
                active.effect,
                shield_inversion::SHIELD_NOVA_EFFECT,
                self.tick_ms(),
            ));
            self.pending.push(packets::weapon_effect(
                self.player_id(),
                active.target,
                shield_inversion::SHIELD_NOVA_EFFECT,
                active.effect.wrapping_add(1).max(1),
                self.tick_ms(),
                remaining.min(u16::MAX as u64) as u16,
            ));
        }
    }

    /// Returns:
    ///     Arc<FactionReactions>: Current authoritative MOB faction reactions.
    pub(crate) fn inversion_reputation(&self) -> std::sync::Arc<crate::factions::FactionReactions> {
        std::sync::Arc::clone(&self.reputation)
    }

    /// Commit one world pulse's shield cost and next index in one checkpoint.
    pub(crate) fn settle_inversion_pulse(&mut self, receipt: &InversionReceipt) -> io::Result<()> {
        let active = self
            .skills
            .active_inversion
            .ok_or_else(|| invalid("Shield Inversion is no longer active."))?;
        if !receipt.valid()
            || receipt.source != self.player_id()
            || receipt.sector != active.sector
            || receipt.source_epoch != self.session_epoch
            || receipt.effect != active.effect
            || receipt.pulse_index != active.pulse_index
            || receipt.target_generation != active.target_generation
        {
            return Err(invalid("Shield Inversion pulse receipt mismatch."));
        }
        let previous_shield = self.shield;
        // A durable NPC hit may have lowered shields after the world pulse
        // committed but before this account checkpoint. Never block replay.
        self.shield = (self.shield - receipt.shield_spent).max(0.);
        let next = ActiveShieldInversion {
            pulse_index: active.pulse_index + 1,
            next_pulse_ms: active
                .next_pulse_ms
                .saturating_add(shield_inversion::PULSE_MS),
            ..active
        };
        self.skills.active_inversion =
            (next.next_pulse_ms <= next.expires_at_ms && self.shield > 0.).then_some(next);
        if let Err(error) = self.checkpoint_session(true) {
            self.shield = previous_shield;
            self.skills.active_inversion = Some(active);
            return Err(error);
        }
        self.pending.push(packets::ship_resources(
            self.player_id(),
            &self.equipment.ship,
            None,
            Some(self.shield),
            None,
        ));
        if self.skills.active_inversion.is_none() {
            self.publish_inversion_end(active);
        }
        Ok(())
    }

    /// Stop an expired or unavailable cast without charging another pulse.
    pub(crate) fn stop_shield_inversion(&mut self) -> io::Result<()> {
        let Some(active) = self.skills.active_inversion.take() else {
            return Ok(());
        };
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.active_inversion = Some(active);
            return Err(error);
        }
        self.publish_inversion_end(active);
        Ok(())
    }

    /// Remove the timed native icon and verified Shield Nova visual.
    fn publish_inversion_end(&mut self, active: ActiveShieldInversion) {
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            skill_status::SHIELD_INVERSION_SLOT,
            "",
            0,
        ));
        if active.ability == 110 {
            self.pending.push(packets::remove_effect(active.effect));
            self.pending
                .push(packets::remove_effect(active.effect.wrapping_add(1).max(1)));
        }
    }
}
