//! World-scoped Summon delivery. A cast never gains authority over pilots who
//! leave the group, sector or range while the skill is charging.
use super::*;
use crate::mob::distance;
use crate::summon::SummonTarget;

impl GameState {
    /// Returns:
    ///     bool: Whether at least one currently eligible group pilot was moved.
    pub(super) fn deliver_summon(
        &mut self,
        source: u32,
        action: &SupportAction,
    ) -> io::Result<bool> {
        let SupportAction::Summon {
            ability,
            trained_rank,
            skill_bonus,
            target,
            origin_sector,
            origin_position,
            origin_heading,
        } = *action
        else {
            return Err(invalid("Expected a Summon support action."));
        };
        let tier = crate::summon::tier(ability, trained_rank)
            .ok_or_else(|| invalid("Unknown Summon ability tier."))?;
        let range = crate::summon::effective_range(tier, skill_bonus);
        if matches!(
            tier.target,
            SummonTarget::Enemy | SummonTarget::EnemyFormation
        ) {
            return Err(invalid("Enemy Summon must be applied in sector combat."));
        }
        let caster = self
            .sessions
            .get(&source)
            .ok_or_else(|| invalid("Summon caster disconnected."))?;
        if !caster.summon_available()
            || caster.sector.id != origin_sector
            || distance(caster.position, origin_position) > 500.
        {
            return Ok(false);
        }
        let recipients: Vec<u32> = match tier.target {
            SummonTarget::Friend | SummonTarget::ReturnFriend => vec![target],
            SummonTarget::Group => self.groups.members(source),
            SummonTarget::Enemy | SummonTarget::EnemyFormation => unreachable!(),
        };
        let mut delivered = false;
        for recipient in recipients {
            if recipient == source || !self.groups.together(source, recipient) {
                continue;
            }
            let Some(peer) = self.sessions.get(&recipient) else {
                continue;
            };
            if !peer.summon_available()
                || peer.sector.id != origin_sector
                || distance(peer.position, origin_position) > range
            {
                continue;
            }
            let peer = self.sessions.get_mut(&recipient).unwrap();
            match tier.target {
                SummonTarget::ReturnFriend => peer.receive_return_friend()?,
                SummonTarget::Friend | SummonTarget::Group => {
                    peer.receive_summon(origin_sector, origin_position, origin_heading)?
                }
                SummonTarget::Enemy | SummonTarget::EnemyFormation => unreachable!(),
            }
            delivered = true;
        }
        Ok(delivered)
    }
}
