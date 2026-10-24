//! One-time nav discovery awards from authoritative ship proximity.
use super::*;

impl PlayerSession {
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
                    && nav.navigation_type > 0
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
            candidate.explore_xp = candidate
                .explore_xp
                .checked_add(amount)
                .ok_or_else(|| invalid("Nav exploration XP overflow."))?;
            awards.push((nav.name.as_str(), amount));
        }
        self.commit_inventory(candidate)?;
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
    }
}
