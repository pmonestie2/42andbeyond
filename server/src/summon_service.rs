//! Recipient side of Summon. Group membership and cast range are checked by
//! the world coordinator before these methods move a pilot.
use super::*;

impl PlayerSession {
    /// Returns:
    ///     u64: Current death generation of a selected visible hostile MOB.
    pub(super) fn summon_target_generation(&self, target: u32) -> io::Result<u64> {
        let index = target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Select a hostile MOB for Summon."))?
            as usize;
        let combat = lock_combat(&self.combat)?;
        let mob = combat
            .actors
            .get(index)
            .ok_or_else(|| invalid("Summon target is missing."))?;
        if !self.mob_visible_in_scan(index, mob)
            || mob.state == MobState::Dead
            || self.mob_relationship(mob) != 0
        {
            return Err(invalid("Summon target is not a visible hostile MOB."));
        }
        Ok(mob.death_generation)
    }

    /// Returns:
    ///     bool: Whether this pilot can be summoned while alive in space.
    pub(crate) fn summon_available(&self) -> bool {
        self.logged_in
            && self.sector.station.is_none()
            && self.hull > 0.
            && !self.warp.active()
            && self.gate_transit.is_none()
            && self.docking_target.is_none()
    }

    /// Move an eligible pilot to a bounded point in front of the caster.
    pub(crate) fn receive_summon(
        &mut self,
        caster_sector: u32,
        caster_position: [f32; 3],
        caster_heading: [f32; 3],
    ) -> io::Result<()> {
        if !self.summon_available() || self.sector.id != caster_sector {
            return Err(invalid(
                "Summon recipient left the sector or became unavailable.",
            ));
        }
        let destination = crate::summon::arrival_position(
            caster_position,
            caster_heading,
            self.sector.bounds.min,
            self.sector.bounds.max,
        );
        let original_position = self.position;
        let original_motion = self.motion.clone();
        let original_following = self.following;
        let original_follow_peer = self.follow_peer;
        let original_report = self.last_motion_report;
        self.position = destination;
        self.motion.stop();
        self.following = false;
        self.follow_peer = None;
        self.last_motion_report = None;
        if let Err(error) = self.checkpoint_session(true) {
            self.position = original_position;
            self.motion = original_motion;
            self.following = original_following;
            self.follow_peer = original_follow_peer;
            self.last_motion_report = original_report;
            return Err(error);
        }
        self.detach_formation();
        self.pending.push(packets::player_movement(
            self.player_id(),
            self.position,
            self.tick_ms(),
            &self.motion,
        ));
        self.message("A group member summoned your ship.");
        Ok(())
    }

    /// Return an eligible pilot to their registered docking bay.
    pub(crate) fn receive_return_friend(&mut self) -> io::Result<()> {
        if !self.summon_available() {
            return Err(invalid("Return Friend recipient is unavailable."));
        }
        self.enter_station(self.home_station)?;
        self.message("Returned to your registered port.");
        Ok(())
    }
}
