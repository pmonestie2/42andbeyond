//! Durable preparation and per-recipient settlement for high Fold tiers.
use super::*;
use crate::fold_space_high::{self, FoldDestination, FoldReceipt, MobFoldReceipt, PendingHighFold};
use crate::skills::{AbilityDefinition, Cast};

impl PlayerSession {
    /// Returns:
    ///     Option<PendingHighFold>: Frozen or newly settled travel awaiting delivery.
    pub(crate) fn high_fold_pending(&self) -> Option<PendingHighFold> {
        self.skills.pending_high_fold.clone()
    }

    /// Persist one world-coordinated plan before any recipient moves.
    pub(crate) fn freeze_high_fold_plan(
        &mut self,
        plan: fold_space_high::FoldTravelPlan,
    ) -> io::Result<()> {
        let before = self.skills.pending_high_fold.clone();
        let pending = self
            .skills
            .pending_high_fold
            .as_mut()
            .ok_or_else(|| invalid("No pending high Fold travel."))?;
        if pending.plan.as_ref().is_some_and(|prior| prior == &plan) {
            return Ok(());
        }
        if pending.plan.is_some()
            || !plan.valid()
            || plan.caster != pending.source
            || plan.caster_epoch != pending.source_epoch
            || plan.effect != pending.effect
            || plan.sector != pending.sector
            || plan.ability != pending.ability
        {
            return Err(invalid("Invalid frozen high Fold travel plan."));
        }
        pending.plan = Some(plan);
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.pending_high_fold = before;
            return Err(error);
        }
        Ok(())
    }

    /// Clear a delivered or no-longer-eligible request only after checkpoint.
    pub(crate) fn finish_high_fold(&mut self) -> io::Result<()> {
        let before = self.skills.pending_high_fold.take();
        if before.is_none() {
            return Ok(());
        }
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.pending_high_fold = before;
            return Err(error);
        }
        Ok(())
    }

    /// Returns:
    ///     u64: Current life of a visible hostile MOB selected for Directional Teleport.
    pub(crate) fn high_fold_mob_generation(&self, target: u32) -> io::Result<u64> {
        let index = target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Select a MOB for Directional Teleport."))?
            as usize;
        let combat = lock_combat(&self.combat)?;
        let mob = combat
            .actors
            .get(index)
            .ok_or_else(|| invalid("Directional Teleport MOB is missing."))?;
        if mob.state == MobState::Dead
            || !self.mob_visible_in_scan(index, mob)
            || self.mob_relationship(mob) != 0
        {
            return Err(invalid(
                "Directional Teleport requires a visible hostile MOB.",
            ));
        }
        Ok(mob.death_generation)
    }

    /// Returns:
    ///     bool: Whether one native high-rank cast settled or queued durable travel.
    pub(crate) fn settle_high_fold(
        &mut self,
        ability: &AbilityDefinition,
        cast: &Cast,
    ) -> io::Result<bool> {
        let band = fold_space_high::tier(
            ability.id,
            self.skill_catalog.level(fold_space_high::SKILL_ID),
            self.account.character.class().code(),
        )
        .ok_or_else(|| invalid("High Fold rank is no longer trained."))?;
        let band = fold_space_high::with_distance_bonus(band, cast.fold_distance_bonus);
        if !self.summon_available() {
            return Ok(false);
        }
        if ability.id == 36 && cast.target >= MOB_ID && cast.target < MOB_ID + 0x1000_0000 {
            let Some(generation) = cast.fold_mob_generation else {
                return Ok(false);
            };
            let index = (cast.target - MOB_ID) as usize;
            let mut combat = lock_combat(&self.combat)?;
            let Some(mob) = combat.actors.get(index) else {
                return Ok(false);
            };
            if mob.last_high_fold.as_ref().is_some_and(|receipt| {
                receipt.caster == self.player_id()
                    && receipt.caster_epoch == self.session_epoch
                    && receipt.effect == cast.effect
                    && receipt.generation == generation
            }) {
                let position = mob.position;
                drop(combat);
                self.pending
                    .push(packets::movement(cast.target, position, self.tick_ms()));
                self.add_skill_effect(cast.target, 267);
                return Ok(true);
            }
            if mob.state == MobState::Dead
                || mob.death_generation != generation
                || !self.mob_visible_in_scan(index, mob)
                || self.mob_relationship(mob) != 0
                || distance(self.position, mob.position) > band.travel_distance
            {
                return Ok(false);
            }
            let Some(destination) = fold_space_high::directional_mob(
                band,
                mob.position,
                mob.motion.heading(),
                (self.sector.bounds.min, self.sector.bounds.max),
            ) else {
                return Ok(false);
            };
            let before = combat.actors.clone();
            let mob = &mut combat.actors[index];
            mob.position = destination;
            mob.speed = 0.;
            mob.motion.stop();
            mob.last_high_fold = Some(MobFoldReceipt {
                caster: self.player_id(),
                caster_epoch: self.session_epoch,
                effect: cast.effect,
                generation,
                destination,
            });
            if let Err(error) = combat.persist() {
                combat.actors = before;
                return Err(error);
            }
            drop(combat);
            self.pending
                .push(packets::movement(cast.target, destination, self.tick_ms()));
            self.add_skill_effect(cast.target, 267);
            return Ok(true);
        }
        if self
            .skills
            .pending_high_fold
            .as_ref()
            .is_some_and(|pending| {
                pending.effect == cast.effect && pending.source_epoch == self.session_epoch
            })
        {
            return Ok(true);
        }
        if self.skills.pending_high_fold.is_some() {
            return Err(invalid("Previous Fold travel is still settling."));
        }
        self.skills.pending_high_fold = Some(PendingHighFold {
            ability: ability.id,
            trained_rank: self.skill_catalog.level(fold_space_high::SKILL_ID),
            source: self.player_id(),
            source_epoch: self.session_epoch,
            sector: self.sector.id,
            effect: cast.effect,
            target: cast.target,
            distance_bonus: cast.fold_distance_bonus,
            plan: None,
        });
        Ok(true)
    }

    /// Returns:
    ///     bool: Whether this ship can be moved in an active Fold group plan.
    pub(crate) fn high_fold_available(&self, sector: u32, epoch: u64) -> bool {
        self.summon_available() && self.sector.id == sector && self.session_epoch == epoch
    }

    /// Returns:
    ///     bool: Whether a frozen recipient move was new, or already committed.
    pub(crate) fn receive_high_fold(
        &mut self,
        source: u32,
        source_epoch: u64,
        effect: u32,
        sector: u32,
        destination: FoldDestination,
    ) -> io::Result<bool> {
        if destination.player_id != self.player_id() {
            return Ok(false);
        }
        if self.high_fold_receipts.iter().any(|receipt| {
            receipt.caster == source
                && receipt.caster_epoch == source_epoch
                && receipt.effect == effect
        }) {
            return Ok(true);
        }
        if !self.high_fold_available(sector, destination.session_epoch)
            || !self.sector.bounds.contains(destination.destination)
        {
            return Ok(false);
        }
        let original_position = self.position;
        let original_motion = self.motion.clone();
        let original_following = self.following;
        let original_follow_peer = self.follow_peer;
        let original_report = self.last_motion_report;
        let original_receipts = self.high_fold_receipts.clone();
        self.position = destination.destination;
        self.motion.stop();
        self.following = false;
        self.follow_peer = None;
        self.last_motion_report = None;
        self.high_fold_receipts.push(FoldReceipt {
            caster: source,
            caster_epoch: source_epoch,
            effect,
            destination: destination.destination,
        });
        if self.high_fold_receipts.len() > fold_space_high::RECEIPT_LIMIT {
            self.high_fold_receipts.remove(0);
        }
        if let Err(error) = self.checkpoint_session(true) {
            self.position = original_position;
            self.motion = original_motion;
            self.following = original_following;
            self.follow_peer = original_follow_peer;
            self.last_motion_report = original_report;
            self.high_fold_receipts = original_receipts;
            return Err(error);
        }
        self.detach_formation();
        self.pending.push(packets::player_movement(
            self.player_id(),
            self.position,
            self.tick_ms(),
            &self.motion,
        ));
        self.add_skill_effect(self.player_id(), 267);
        self.message("Fold Space moved your ship.");
        Ok(true)
    }
}
