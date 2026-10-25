//! World-coordinated high Fold travel with frozen plan and recipient receipts.
use super::*;
use crate::fold_space_high::{self, FoldCast, FoldRecipient, PendingHighFold};

impl GameState {
    /// Deliver one previously checkpointed rank-six or rank-seven travel cast.
    pub(super) fn deliver_high_fold(&mut self, source: u32) -> io::Result<()> {
        let Some(pending) = self
            .sessions
            .get(&source)
            .and_then(PlayerSession::high_fold_pending)
        else {
            return Ok(());
        };
        if !pending.valid() {
            return Err(invalid("Invalid pending high Fold travel."));
        }
        if !self.sessions.get(&source).is_some_and(|session| {
            session.high_fold_available(pending.sector, pending.source_epoch)
        }) {
            self.sessions.get_mut(&source).unwrap().finish_high_fold()?;
            return Ok(());
        }
        let plan = if let Some(plan) = pending.plan.clone() {
            plan
        } else {
            let Some(plan) = self.plan_high_fold(source, &pending)? else {
                let caster = self.sessions.get_mut(&source).unwrap();
                caster.finish_high_fold()?;
                caster.message("No eligible Fold Space target remained.");
                return Ok(());
            };
            self.sessions
                .get_mut(&source)
                .unwrap()
                .freeze_high_fold_plan(plan.clone())?;
            plan
        };
        let mut delivered = 0;
        for destination in &plan.recipients {
            if destination.player_id != source
                && !self.groups.together(source, destination.player_id)
            {
                continue;
            }
            let Some(recipient) = self.sessions.get_mut(&destination.player_id) else {
                continue;
            };
            if recipient.receive_high_fold(
                source,
                plan.caster_epoch,
                plan.effect,
                plan.sector,
                *destination,
            )? {
                delivered += 1;
            }
        }
        let caster = self.sessions.get_mut(&source).unwrap();
        caster.finish_high_fold()?;
        caster.message(if delivered > 0 {
            "Fold Space travel completed."
        } else {
            "No eligible Fold Space target remained."
        });
        Ok(())
    }

    /// Returns:
    ///     Option<FoldTravelPlan>: Frozen current members and bounded destinations.
    fn plan_high_fold(
        &self,
        source: u32,
        pending: &PendingHighFold,
    ) -> io::Result<Option<fold_space_high::FoldTravelPlan>> {
        let caster = &self.sessions[&source];
        let tier = fold_space_high::tier(pending.ability, pending.trained_rank, "jd")
            .ok_or_else(|| invalid("Unknown high Fold tier."))?;
        let tier = fold_space_high::with_distance_bonus(tier, pending.distance_bonus);
        let cast = FoldCast {
            caster: source,
            caster_epoch: pending.source_epoch,
            sector: pending.sector,
            effect: pending.effect,
            bounds: (caster.sector.bounds.min, caster.sector.bounds.max),
        };
        if pending.ability == 36 {
            let recipient_id = pending.target;
            if recipient_id != source && !self.groups.together(source, recipient_id) {
                return Ok(None);
            }
            let Some(recipient) = self.sessions.get(&recipient_id) else {
                return Ok(None);
            };
            let Some(peer) = recipient.peer_snapshot() else {
                return Ok(None);
            };
            let subject = FoldRecipient {
                player_id: recipient_id,
                session_epoch: recipient.threat().session_epoch,
                sector: peer.sector,
                position: peer.position,
                alive: recipient.high_fold_available(peer.sector, recipient.threat().session_epoch),
                group_member: recipient_id != source,
            };
            if crate::mob::distance(caster.position, peer.position) > 6_000. {
                return Ok(None);
            }
            return Ok(fold_space_high::directional(
                tier,
                cast,
                subject,
                peer.motion.heading(),
            ));
        }
        let mut members = self.groups.members(source);
        if !members.contains(&source) {
            members.push(source);
        }
        members.sort_unstable();
        let candidates = members
            .into_iter()
            .filter_map(|id| {
                let recipient = self.sessions.get(&id)?;
                let peer = recipient.peer_snapshot()?;
                let epoch = recipient.threat().session_epoch;
                recipient
                    .high_fold_available(pending.sector, epoch)
                    .then_some(FoldRecipient {
                        player_id: id,
                        session_epoch: epoch,
                        sector: peer.sector,
                        position: peer.position,
                        alive: true,
                        group_member: id != source,
                    })
            })
            .collect::<Vec<_>>();
        Ok(fold_space_high::area(
            tier,
            cast,
            caster.position,
            caster.peer_snapshot().unwrap().motion.heading(),
            &candidates,
        ))
    }
}
