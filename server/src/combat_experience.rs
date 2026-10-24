//! Commit a combat reward and its generation receipt in one owned inventory.
use super::*;
use crate::loot_world::KillAward;

impl PlayerSession {
    /// Returns:
    ///     u32: Current durable combat level for a frozen kill award.
    pub(crate) fn combat_level(&self) -> io::Result<u32> {
        Ok(
            crate::experience::track(self.inventory.xp_start_level, self.inventory.combat_xp)?
                .level,
        )
    }

    /// Persist this kill once before the sector can remove its award event.
    ///
    /// Raises:
    ///     io::Error: Foreign, malformed or uncommittable awards are rejected.
    pub(crate) fn settle_kill_award(&mut self, award: &KillAward) -> io::Result<()> {
        if !self.logged_in
            || award.killer != self.player_id()
            || !(1..20_000).contains(&award.sector)
            || award.death.spawn == 0
            || award.death.generation == 0
            || !(1..=100).contains(&award.level)
            || award.amount.is_some_and(|amount| amount > 8000)
        {
            return Err(invalid("Invalid combat XP award owner or death."));
        }
        let key = award.receipt_key();
        if self
            .inventory
            .combat_awards
            .get(&key)
            .is_some_and(|generation| *generation >= award.death.generation)
        {
            return Ok(());
        }
        let amount = match award.amount {
            Some(amount) => amount,
            None => crate::experience::mob_xp(award.level, self.combat_level()?),
        };
        let mut candidate = self.inventory.clone();
        candidate.combat_xp = candidate
            .combat_xp
            .checked_add(amount)
            .ok_or_else(|| invalid("Combat XP balance overflow."))?;
        candidate.combat_awards.insert(key, award.death.generation);
        self.commit_inventory(candidate)?;
        if amount > 0 {
            self.pending.push(packets::message(&format!(
                "Defeated MOB: {amount} Combat experience earned."
            )));
        }
        crate::server_log!(
            "Combat XP committed: {}; sector={}; spawn={}; generation={}; amount={amount}",
            self.account.character.name,
            award.sector,
            award.death.spawn,
            award.death.generation
        );
        Ok(())
    }
}
