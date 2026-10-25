//! Consent-based, short-lived Call Forward offers over the existing chat path.
use super::*;
use crate::mob::distance;

const CALL_FORWARD_RANGE: f32 = 5_000.;
const OFFER_LIFETIME: Duration = Duration::from_secs(60);

#[derive(Clone)]
pub(super) struct CallForwardOffer {
    source: u32,
    source_epoch: u64,
    target_epoch: u64,
    skill: u32,
    expires: Instant,
}

impl GameState {
    /// Offer one named respec to the selected pilot, pending their chat acceptance.
    pub(super) fn offer_call_forward(&mut self, source: u32, skill_name: &str) -> io::Result<()> {
        let caster = self
            .sessions
            .get(&source)
            .ok_or_else(|| invalid("Unknown Call Forward pilot."))?;
        if !caster.call_forward_available() || !caster.can_call_forward() {
            return Err(invalid(
                "A trained Progen Sentinel in space must initiate Call Forward.",
            ));
        }
        let target = caster.current_target();
        let recipient = self
            .sessions
            .get(&target)
            .ok_or_else(|| invalid("Select a pilot for Call Forward."))?;
        if !recipient.call_forward_available()
            || recipient.sector.id != caster.sector.id
            || distance(recipient.position, caster.position) > CALL_FORWARD_RANGE
            || !caster.can_recharge_peer(target)
        {
            return Err(invalid(
                "Call Forward target is unavailable or out of range.",
            ));
        }
        let skill = recipient.call_forward_skill_id(skill_name)?;
        recipient.check_call_forward(skill)?;
        if self
            .call_forward_offers
            .get(&target)
            .is_some_and(|offer| offer.expires > Instant::now())
        {
            return Err(invalid("This pilot already has a Call Forward offer."));
        }
        let offer = CallForwardOffer {
            source,
            source_epoch: caster.shield_recharge_epoch(),
            target_epoch: recipient.shield_recharge_epoch(),
            skill,
            expires: Instant::now() + OFFER_LIFETIME,
        };
        let caster_name = caster.account.character.name.clone();
        let target_name = recipient.account.character.name.clone();
        self.call_forward_offers.insert(target, offer);
        self.sessions.get_mut(&target).unwrap().message(&format!(
            "{caster_name} offers Call Forward for {skill_name}. Type /acceptcall or /declinecall within 60 seconds."
        ));
        self.sessions.get_mut(&source).unwrap().message(&format!(
            "Call Forward offer sent to {target_name} for {skill_name}."
        ));
        Ok(())
    }

    /// Resolve a saved-in-memory offer after the target explicitly accepts or declines.
    pub(super) fn answer_call_forward(&mut self, target: u32, accept: bool) -> io::Result<()> {
        let offer = self
            .call_forward_offers
            .get(&target)
            .cloned()
            .ok_or_else(|| invalid("No Call Forward offer is pending."))?;
        let valid = offer.expires > Instant::now()
            && self.sessions.get(&offer.source).is_some_and(|source| {
                source.call_forward_available()
                    && source.can_call_forward()
                    && source.shield_recharge_epoch() == offer.source_epoch
            })
            && self.sessions.get(&target).is_some_and(|recipient| {
                recipient.call_forward_available()
                    && recipient.shield_recharge_epoch() == offer.target_epoch
                    && self.sessions[&offer.source].sector.id == recipient.sector.id
                    && distance(self.sessions[&offer.source].position, recipient.position)
                        <= CALL_FORWARD_RANGE
            });
        if !valid {
            self.call_forward_offers.remove(&target);
            return Err(invalid("Call Forward offer expired or a pilot left range."));
        }
        if !accept {
            self.call_forward_offers.remove(&target);
            self.sessions
                .get_mut(&offer.source)
                .unwrap()
                .message("Call Forward was declined.");
            self.sessions
                .get_mut(&target)
                .unwrap()
                .message("Call Forward declined.");
            return Ok(());
        }
        let refunded = self
            .sessions
            .get_mut(&target)
            .unwrap()
            .accept_call_forward(offer.skill)?;
        self.call_forward_offers.remove(&target);
        self.sessions
            .get_mut(&offer.source)
            .unwrap()
            .message(&format!(
                "Call Forward completed; {refunded} skill points returned."
            ));
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::account::Account;
    use crate::inventory::{Inventory, ItemInstance};
    use crate::items::ItemCatalog;
    use std::fs;

    #[test]
    fn chat_offer_requires_recipient_consent_and_consumes_one_device_durably() {
        let root = std::env::temp_dir().join(format!("enb-call-chat-{}", rand::random::<u64>()));
        let accounts = AccountDirectory::new(&root);
        let sentinel = accounts
            .create_class(
                "sentinel".into(),
                "example",
                "Sentinel".into(),
                crate::classes::CharacterClass::parse("ps").unwrap(),
            )
            .unwrap();
        let target = accounts
            .create("target".into(), "example", "Target".into())
            .unwrap();
        let source_id = sentinel.account.character.game_id;
        let target_id = target.account.character.game_id;
        let items = ItemCatalog::load().unwrap();
        let mut inventory =
            Inventory::native_starter_for(&target.account.character, &items).unwrap();
        inventory.xp_start_level = 50;
        inventory.trade_xp = 3 * crate::experience::LEVEL_XP[50];
        inventory.mission_awarded_skills.insert(5);
        inventory.trained_skills.as_mut().unwrap().insert(5, 3);
        inventory.spent_skill_points = 3;
        inventory.equipped[9] = Some(ItemInstance::stock(5991, 1));
        fs::write(
            target.directory.join("inventory.json"),
            serde_json::to_vec(&inventory).unwrap(),
        )
        .unwrap();
        let sector: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
        let bind = Ipv4Addr::new(127, 0, 0, 2);
        let mut game = GameState::new(sector.clone(), bind, root.clone()).unwrap();
        super::super::tests::login(&mut game, source_id, 23901, "sentinel");
        super::super::tests::login(&mut game, target_id, 23902, "target");
        game.update_views();
        game.handle_input(
            source_id,
            0x17,
            &[source_id.to_le_bytes(), target_id.to_le_bytes()].concat(),
        )
        .unwrap();
        let chat = |game: &mut GameState, owner: u32, text: &str| {
            game.handle_event(
                owner,
                ClientEvent::Chat {
                    channel: 4,
                    text: text.into(),
                },
            )
        };
        chat(&mut game, source_id, "/call Build Devices").unwrap();
        assert_eq!(game.sessions[&target_id].inventory.spent_skill_points, 3);
        chat(&mut game, target_id, "/declinecall").unwrap();
        assert!(game.sessions[&target_id].inventory.equipped[9].is_some());
        chat(&mut game, source_id, "/call Build Devices").unwrap();
        chat(&mut game, target_id, "/acceptcall").unwrap();
        assert_eq!(game.sessions[&target_id].inventory.spent_skill_points, 0);
        assert_eq!(
            game.sessions[&target_id]
                .inventory
                .trained_skills
                .as_ref()
                .unwrap()[&5],
            1
        );
        assert!(game.sessions[&target_id].inventory.equipped[9].is_none());
        assert!(chat(&mut game, target_id, "/acceptcall").is_err());
        drop(game);
        let resumed = GameState::new(sector, bind, root.clone()).unwrap();
        assert_eq!(resumed.sessions[&target_id].inventory.spent_skill_points, 0);
        assert!(resumed.sessions[&target_id].inventory.equipped[9].is_none());
        assert_eq!(
            Account::load(&target.directory).unwrap().character.game_id,
            target_id
        );
        fs::remove_dir_all(root).unwrap();
    }
}
