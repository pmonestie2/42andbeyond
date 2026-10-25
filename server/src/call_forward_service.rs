//! Recipient-side Call Forward checks and one durable inventory replacement.
use super::*;

impl PlayerSession {
    /// Returns:
    ///     bool: Whether this pilot can issue or accept Call Forward in space.
    pub(crate) fn call_forward_available(&self) -> bool {
        self.logged_in
            && self.hull > 0.
            && self.sector.station.is_none()
            && !self.warp.active()
            && self.gate_transit.is_none()
    }

    /// Returns:
    ///     bool: Whether this pilot is a trained Progen Sentinel.
    pub(crate) fn can_call_forward(&self) -> bool {
        self.skill_catalog.class_index == 8 && self.skill_catalog.level(11) > 0
    }

    /// Returns:
    ///     u32: The recipient's owned skill matching an exact case-insensitive name.
    /// Raises:
    ///     io::Error: The name is empty or unknown to this pilot.
    pub(crate) fn call_forward_skill_id(&self, name: &str) -> io::Result<u32> {
        let name = name.trim();
        if name.is_empty() {
            return Err(invalid("Use /call SKILL NAME while targeting a pilot."));
        }
        self.skill_catalog
            .skills
            .iter()
            .find(|skill| skill.name.eq_ignore_ascii_case(name))
            .map(|skill| skill.id)
            .ok_or_else(|| invalid("The targeted pilot does not know that skill."))
    }

    /// Validate the recipient's current skill, gear and point refund before an offer.
    /// Raises:
    ///     io::Error: The respec cannot commit against current inventory.
    pub(crate) fn check_call_forward(&self, skill_id: u32) -> io::Result<()> {
        crate::call_forward::reset_skill(
            &self.account.character,
            &self.inventory,
            skill_id,
            &self.items,
        )?;
        Ok(())
    }

    /// Returns:
    ///     u32: Skill points refunded after consuming one equipped resequencer.
    /// Raises:
    ///     io::Error: The selected skill or device fails validation or persistence.
    pub(crate) fn accept_call_forward(&mut self, skill_id: u32) -> io::Result<u32> {
        let (candidate, refunded) = crate::call_forward::reset_skill(
            &self.account.character,
            &self.inventory,
            skill_id,
            &self.items,
        )?;
        self.commit_inventory(candidate)?;
        self.message(&format!("Call Forward returned {refunded} skill points."));
        Ok(refunded)
    }
}
