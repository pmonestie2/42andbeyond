//! Native per-pilot attacker list, keyed by an actual MOB weapon launch.
use super::*;

impl PlayerSession {
    /// Commit engagement changes before publishing native 0x8b add/remove packets.
    pub(crate) fn sync_attackers(
        &mut self,
        targeted: &HashSet<u32>,
        launched: &HashSet<u32>,
    ) -> io::Result<()> {
        let next: BTreeSet<u32> =
            if self.logged_in && self.hull > 0. && self.sector.station.is_none() {
                self.attacker_ids
                    .iter()
                    .filter(|id| targeted.contains(id))
                    .chain(launched.iter().filter(|id| targeted.contains(id)))
                    .copied()
                    .collect()
            } else {
                BTreeSet::new()
            };
        if next == self.attacker_ids {
            return Ok(());
        }
        let previous = std::mem::replace(&mut self.attacker_ids, next);
        if let Err(error) = self.checkpoint_session(true) {
            self.attacker_ids = previous;
            return Err(error);
        }
        for id in previous.difference(&self.attacker_ids) {
            self.pending.push(packets::attacker_update(*id, false));
        }
        for id in self.attacker_ids.difference(&previous) {
            self.pending.push(packets::attacker_update(*id, true));
        }
        self.flush()
    }
}
