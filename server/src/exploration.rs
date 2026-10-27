//! One-time nav discovery awards from authoritative ship proximity.
use super::*;
use std::collections::BTreeSet;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ExploreAward {
    pub recipients: Vec<u32>,
    pub amount: u64,
    pub source: String,
}

impl ExploreAward {
    /// Returns:
    ///     bool: Whether this durable group award is bounded and well formed.
    pub(crate) fn valid(&self) -> bool {
        self.recipients.len() <= 5
            && !self.recipients.is_empty()
            && self.recipients.iter().all(|id| *id != 0)
            && self
                .recipients
                .iter()
                .copied()
                .collect::<BTreeSet<_>>()
                .len()
                == self.recipients.len()
            && self.amount <= 1_000_000
            && !self.source.is_empty()
            && self.source.len() <= 128
    }
}

/// Returns:
///     u64: Native integer split plus the group-size exploration bonus.
pub(crate) fn group_explore_amount(base: u64, members: usize) -> u64 {
    const BONUS: [f32; 6] = [1., 1.4, 1.8, 2.2, 2.6, 3.];
    if members <= 1 {
        return base;
    }
    let split = base / members as u64;
    (split as f32 * (1. + BONUS[members.min(6) - 1])) as u64
}

impl PlayerSession {
    /// Add owner XP and freeze the current eligible group for durable fanout.
    pub(super) fn record_explore_award(
        &self,
        candidate: &mut Inventory,
        base: u64,
        source: &str,
    ) -> io::Result<u64> {
        let amount = group_explore_amount(base, self.explore_group_members.len() + 1);
        candidate.explore_xp = candidate
            .explore_xp
            .checked_add(amount)
            .ok_or_else(|| invalid("Exploration XP overflow."))?;
        if !self.explore_group_members.is_empty() {
            candidate.explore_award_sequence = candidate
                .explore_award_sequence
                .checked_add(1)
                .ok_or_else(|| invalid("Exploration award sequence overflow."))?;
            candidate.pending_explore_awards.insert(
                candidate.explore_award_sequence,
                ExploreAward {
                    recipients: self.explore_group_members.clone(),
                    amount,
                    source: source.to_owned(),
                },
            );
        }
        Ok(amount)
    }

    /// Commit one frozen group credit at most once per owner sequence.
    pub(crate) fn settle_received_explore_award(
        &mut self,
        owner: u32,
        sequence: u32,
        award: &ExploreAward,
    ) -> io::Result<()> {
        if owner == 0
            || sequence == 0
            || !award.valid()
            || !award.recipients.contains(&self.player_id())
        {
            return Err(invalid("Invalid exploration award recipient."));
        }
        let receipt = (u64::from(owner) << 32) | u64::from(sequence);
        if self.inventory.received_explore_awards.contains(&receipt) {
            return Ok(());
        }
        let mut candidate = self.inventory.clone();
        candidate.explore_xp = candidate
            .explore_xp
            .checked_add(award.amount)
            .ok_or_else(|| invalid("Exploration XP overflow."))?;
        candidate.received_explore_awards.insert(receipt);
        self.commit_inventory(candidate)?;
        if self.logged_in && award.amount > 0 {
            self.pending.push(packets::message(&format!(
                "{} (+{} group exploration XP).",
                award.source, award.amount
            )));
        }
        Ok(())
    }

    /// Remove a group event only after every recipient's receipt is durable.
    pub(crate) fn acknowledge_explore_award(&mut self, sequence: u32) -> io::Result<()> {
        let mut candidate = self.inventory.clone();
        candidate.pending_explore_awards.remove(&sequence);
        self.commit_inventory(candidate)
    }

    /// Commit nearby public nav discoveries with their exploration XP receipt.
    pub(super) fn advance_exploration(&mut self) -> io::Result<()> {
        if self.hull <= 0. {
            return Ok(());
        }
        let discovered: Vec<_> = self
            .sector
            .navpoints
            .iter()
            .filter(|nav| {
                nav.id != 0
                    && nav.advertised_navigation_type() > 0
                    && nav.object_type != 3 // archived OT_PLANET has no nav award
                    && !self.inventory.explored_navs.contains(&nav.id)
                    && nav.range_to(self.position) < nav.signature.min(3000.)
            })
            .cloned()
            .collect();
        if discovered.is_empty() {
            return Ok(());
        }
        let mut candidate = self.inventory.clone();
        let mut awards = Vec::with_capacity(discovered.len());
        for nav in &discovered {
            let amount = if nav.object_type == 11 || nav.signature < 1500. {
                1000u64 // archived gate override and tiny-signature band
            } else if nav.signature < 5000. {
                500
            } else {
                250
            };
            candidate.explored_navs.insert(nav.id);
            let credited = self.record_explore_award(&mut candidate, amount, &nav.name)?;
            awards.push((nav.name.as_str(), credited));
        }
        self.commit_inventory(candidate)?;
        for nav in &discovered {
            self.pending.extend(packets::static_navigation_refresh(
                &nav.discovered_navigation(),
            ));
        }
        for (name, amount) in awards {
            self.pending.push(packets::message(&format!(
                "Discovered {name} (+{amount} exploration XP)."
            )));
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use crate::packets;
    #[test]
    fn group_explore_split_uses_archived_integer_base_and_size_bonus() {
        assert_eq!(super::group_explore_amount(1000, 1), 1000);
        assert_eq!(super::group_explore_amount(1000, 2), 1200);
        assert_eq!(super::group_explore_amount(1000, 3), 932);
        assert_eq!(super::group_explore_amount(1000, 6), 664);
    }

    #[test]
    fn nav_discovery_awards_native_signature_xp_once_and_retries_storage() {
        let mut ship = super::super::tests::session();
        ship.logged_in = true;
        ship.position = [0.; 3];
        let prototype = ship.sector.navpoints[0].clone();
        ship.sector.navpoints = [
            (1001, 37, 6000., 1),
            (1002, 37, 3000., 1),
            (1003, 37, 1000., 1),
            (1004, 11, 6000., 2),
            (1005, 3, 6000., 2),
            (1006, 37, 1000., 0),
        ]
        .into_iter()
        .map(|(id, object_type, signature, navigation_type)| {
            let mut nav = prototype.clone();
            nav.id = id;
            nav.name = format!("Nav {id}");
            nav.object_type = object_type;
            nav.signature = signature;
            nav.navigation_type = navigation_type;
            nav.navigation_present = false;
            nav.position = [0.; 3];
            nav.radius = 0.;
            nav
        })
        .collect();
        let blocked = ship.state_dir.join("inventory.json.tmp");
        std::fs::create_dir_all(&ship.state_dir).unwrap();
        std::fs::create_dir(&blocked).unwrap();
        let prior = ship.pending.len();
        assert!(ship.advance_player(0.).is_err());
        assert!(ship.inventory.explored_navs.is_empty());
        assert_eq!(ship.pending.len(), prior);
        std::fs::remove_dir(&blocked).unwrap();
        ship.advance_player(0.).unwrap();
        assert_eq!(ship.inventory.explored_navs.len(), 4);
        assert_eq!(ship.inventory.explore_xp, 2750);
        for id in &ship.inventory.explored_navs {
            assert!(ship
                .pending
                .iter()
                .any(|frame| frame.opcode == 0x99 && frame.body[..4] == id.to_le_bytes()));
        }
        assert!(!ship.inventory.explored_navs.contains(&1005));
        assert!(!ship.inventory.explored_navs.contains(&1006));
        let saved: crate::inventory::Inventory =
            serde_json::from_slice(&std::fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved.explored_navs, ship.inventory.explored_navs);
        let prior = ship.pending.len();
        ship.advance_player(0.).unwrap();
        assert_eq!(ship.pending.len(), prior);
        assert_eq!(ship.inventory.explore_xp, 2750);
        ship.position = [50_000., 0., 0.];
        let frames = packets::login_world(
            &ship.sector,
            ship.position,
            0,
            &ship.account.character,
            &ship.motion,
            &packets::ShipInitialization {
                catalog: &ship.items,
                item_templates: Vec::new(),
                inventory: &ship.inventory,
                stats: &ship.equipment.ship,
                resources: packets::ShipResources {
                    energy: ship.energy,
                    shield: ship.shield,
                    hull: ship.hull,
                },
            },
        );
        for id in &ship.inventory.explored_navs {
            let announced = frames
                .iter()
                .find(|frame| frame.opcode == 0x2018 && frame.body[..4] == id.to_le_bytes())
                .unwrap();
            assert_eq!(announced.body[57] & 0xb0, 0xb0);
        }
    }
}
