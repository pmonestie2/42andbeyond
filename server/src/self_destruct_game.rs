//! Durable sector blast delivery before caster resource and action settlement.
use super::*;

impl GameState {
    /// Deliver each saved Self Destruct exactly once across sector and account.
    pub(super) fn deliver_self_destructs(&mut self) -> io::Result<()> {
        let sources: Vec<u32> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| session.pending_self_destruct().map(|_| *id))
            .collect();
        for source in sources {
            let caster = &self.sessions[&source];
            let cast = caster.pending_self_destruct().unwrap();
            if !cast.valid() || cast.source != source {
                return Err(invalid("Invalid saved Self Destruct cast."));
            }
            let combat = self
                .combats
                .get(&cast.sector)
                .ok_or_else(|| invalid("Self Destruct sector is unavailable."))?;
            let receipt = lock_combat(combat)?
                .cast_self_destruct(cast, &caster.self_destruct_reputation())?;
            self.sessions
                .get_mut(&source)
                .unwrap()
                .settle_self_destruct(&receipt)?;
        }
        Ok(())
    }
}
