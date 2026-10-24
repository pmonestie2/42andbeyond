//! Station-scoped dialogue and vendor sessions. A transfer names a vendor slot,
//! never an arbitrary item ID or a client-provided price. All mutations go
//! through the same durable inventory commit as equipment and vault transfers.
use super::*;
use crate::npcs::{Branch, TalkNode};

#[derive(Clone, Debug, Serialize, Deserialize)]
pub(super) struct NpcInteraction {
    pub(super) npc: u32,
    pub(super) node: u8,
    pub(super) vendor: bool,
    #[serde(default)]
    pub(super) mission: Option<u32>,
    #[serde(default)]
    pub(super) mission_stage: u32,
}

impl PlayerSession {
    /// Require the avatar to occupy the NPC's actual station room.
    fn npc_in_current_room(&self, identity: u32) -> bool {
        self.sector.station.as_ref().is_some_and(|station| {
            station.npcs.iter().any(|npc| {
                npc.id == identity
                    && self
                        .station_pose
                        .as_ref()
                        .is_some_and(|pose| pose.room == npc.room)
            })
        })
    }

    pub(super) fn open_npc(&mut self, identity: u32) -> io::Result<()> {
        let station = self
            .sector
            .station
            .as_ref()
            .filter(|_| self.logged_in && self.hull > 0.)
            .ok_or_else(|| invalid("Enter a station alive before talking to an NPC."))?;
        if !station.npcs.iter().any(|npc| npc.id == identity) {
            return Err(invalid("NPC is not placed in this station."));
        }
        if !self.npc_in_current_room(identity) {
            return Err(invalid("Enter the NPC's room before talking."));
        }
        let npc = self.npcs.npc(identity, station.id)?;
        crate::server_log!(
            "NPC conversation: {}; npc={identity} ({})",
            self.account.character.name,
            npc.name
        );
        self.close_npc();
        let catalog = crate::missions::MissionCatalog::load()?;
        let active = catalog.active_for_npc(identity, &self.inventory.missions);
        let mission = active
            .map(|(id, _)| id)
            .or_else(|| catalog.offer(identity, &self.account.character, &self.inventory));
        self.npc_interaction = Some(NpcInteraction {
            npc: identity,
            node: 1,
            vendor: false,
            mission,
            mission_stage: active.map_or(0, |(_, stage)| stage),
        });
        self.show_npc_node(1)
    }

    /// Returns:
    ///     TalkNode: Offered mission node or this NPC's ordinary conversation.
    fn active_npc_node(&self, active: &NpcInteraction, node: u8) -> io::Result<TalkNode> {
        if let Some(mission) = active.mission {
            crate::missions::MissionCatalog::load()?.stage_node(mission, active.mission_stage, node)
        } else {
            self.npc_node(active.npc, node)
        }
    }

    fn npc_node(&self, identity: u32, node: u8) -> io::Result<TalkNode> {
        let npc = self.npcs.npc(identity, self.sector.id)?;
        if !npc.nodes.is_empty() {
            return npc
                .nodes
                .iter()
                .find(|entry| entry.id == node)
                .cloned()
                .ok_or_else(|| invalid("NPC conversation node does not exist."));
        }
        match node {
            1 => {
                let mut branches = Vec::new();
                if npc.group != 0 {
                    branches.push(Branch {
                        node: 2,
                        text: "I would like to trade.".into(),
                    });
                }
                branches.push(Branch {
                    node: 0,
                    text: "Goodbye.".into(),
                });
                Ok(TalkNode {
                    id: 1,
                    text: format!("/happy1 Welcome, @name. I am {}.", npc.name),
                    flag: 0,
                    branches,
                })
            }
            2 if npc.group != 0 => Ok(TalkNode {
                id: 2,
                text: "/happy2 Thank you for your business.".into(),
                flag: 1,
                branches: Vec::new(),
            }),
            _ => Err(invalid("Unknown NPC dialogue node.")),
        }
    }

    fn talk_text(&self, text: &str) -> String {
        let profile = &self.account.character;
        let race = ["Terran", "Jenquai", "Progen"]
            .get(profile.race as usize)
            .unwrap_or(&"Jenquai");
        let profession = ["Warrior", "Trader", "Explorer"]
            .get(profile.profession as usize)
            .unwrap_or(&"Explorer");
        // Replace longer tokens first to avoid replacing prefixes of another.
        text.replace(
            "@aprofession",
            if *profession == "Explorer" {
                "an Explorer"
            } else if *profession == "Trader" {
                "a Trader"
            } else {
                "a Warrior"
            },
        )
        .replace("@profession", profession)
        .replace("@race", race)
        .replace("@name", &profile.name)
    }

    /// Returns:
    ///     Inventory: Atomic stage turn-in, objective costs and public rewards.
    fn mission_transition_candidate(
        &self,
        mission: u32,
        stage: u32,
        destination: Option<u32>,
    ) -> io::Result<Inventory> {
        let catalog = crate::missions::MissionCatalog::load()?;
        let definition = catalog.get(mission)?;
        let current_stage = definition
            .stages
            .get(stage as usize)
            .ok_or_else(|| invalid("Mission stage is unavailable."))?;
        if !current_stage.completion.iter().any(|objective| {
            objective.kind == 1
                && objective.data == i64::from(self.npc_interaction.as_ref().unwrap().npc)
        }) {
            return Err(invalid("Mission NPC objective is unavailable."));
        }
        let mut candidate = self.inventory.clone();
        for objective in &current_stage.completion {
            match objective.kind {
                1 if objective.data == i64::from(self.npc_interaction.as_ref().unwrap().npc) => {}
                14 => {
                    let fee = u64::try_from(objective.data)
                        .map_err(|_| invalid("Mission credit requirement is invalid."))?;
                    candidate.credits = candidate
                        .credits
                        .checked_sub(fee)
                        .ok_or_else(|| invalid("Insufficient credits for mission turn-in."))?;
                }
                _ => return Err(invalid("Mission turn-in objective is not supported yet.")),
            }
        }
        if let Some(destination) = destination {
            candidate.missions.advance(mission, destination, &catalog)?;
        } else {
            candidate.missions.complete(mission, &catalog)?;
        }
        for reward in &current_stage.rewards {
            let amount = match &reward.data {
                crate::missions::MissionRewardData::Number(value) => u64::try_from(*value)
                    .map_err(|_| invalid("Mission reward amount is negative."))?,
                crate::missions::MissionRewardData::Script(_) => {
                    return Err(invalid("Mission script rewards are not supported yet."));
                }
            };
            match reward.kind {
                1 => {
                    candidate.credits = candidate
                        .credits
                        .checked_add(amount)
                        .ok_or_else(|| invalid("Mission credit reward overflow."))?;
                }
                2 => {
                    candidate.explore_xp = candidate
                        .explore_xp
                        .checked_add(amount)
                        .ok_or_else(|| invalid("Mission explore reward overflow."))?;
                }
                3 => {
                    candidate.combat_xp = candidate
                        .combat_xp
                        .checked_add(amount)
                        .ok_or_else(|| invalid("Mission combat reward overflow."))?;
                }
                4 => {
                    candidate.trade_xp = candidate
                        .trade_xp
                        .checked_add(amount)
                        .ok_or_else(|| invalid("Mission trade reward overflow."))?;
                }
                6 => {
                    let item = u32::try_from(amount)
                        .map_err(|_| invalid("Mission item template is invalid."))?;
                    let quantity = u32::try_from(if reward.flags == 0 { 1 } else { reward.flags })
                        .map_err(|_| invalid("Mission item quantity is invalid."))?;
                    candidate =
                        candidate.received(item, quantity, u32::MAX as usize, &self.items)?;
                }
                9 => {
                    let skill = u32::try_from(amount)
                        .map_err(|_| invalid("Mission skill reward is invalid."))?;
                    if self.skill_catalog.level(skill) == 0 {
                        return Err(invalid(
                            "Mission rewards a skill unavailable to this class.",
                        ));
                    }
                    candidate.mission_awarded_skills.insert(skill);
                }
                _ => return Err(invalid("Mission reward type is not implemented yet.")),
            }
        }
        Ok(candidate)
    }

    fn show_npc_node(&mut self, node: u8) -> io::Result<()> {
        let identity = self
            .npc_interaction
            .as_ref()
            .ok_or_else(|| invalid("No NPC conversation is active."))?
            .npc;
        if !self.npc_in_current_room(identity) {
            return Err(invalid("NPC conversation is no longer in your room."));
        }
        let active = self.npc_interaction.as_ref().unwrap().clone();
        let mut entry = self.active_npc_node(&active, node)?;
        if entry.flag > 1 && !(active.mission.is_some() && matches!(entry.flag, 2 | 5 | 6)) {
            return Err(invalid("This NPC mission action is not implemented yet."));
        }
        if entry.flag == 5 {
            let mission = active.mission.unwrap();
            let catalog = crate::missions::MissionCatalog::load()?;
            let destination = catalog
                .get(mission)?
                .stages
                .get(active.mission_stage as usize)
                .and_then(|stage| stage.nodes.iter().find(|entry| entry.id == node))
                .map(|entry| entry.destination)
                .ok_or_else(|| invalid("Mission dialogue destination is unavailable."))?;
            let destination = u32::try_from(destination)
                .map_err(|_| invalid("Mission dialogue destination is invalid."))?;
            let mut candidate = self.inventory.clone();
            if active.mission_stage == 0 {
                if destination != 1
                    || catalog.offer(identity, &self.account.character, &self.inventory)
                        != Some(mission)
                {
                    return Err(invalid("Mission offer is no longer eligible."));
                }
                candidate
                    .missions
                    .start(mission, crate::resources::unix_ms(), &catalog)?;
            } else {
                candidate = self.mission_transition_candidate(
                    mission,
                    active.mission_stage,
                    Some(destination),
                )?;
            }
            self.commit_inventory(candidate)?;
            self.npc_interaction.as_mut().unwrap().mission_stage = destination;
            self.pending.push(packets::message(&format!(
                "Mission {}: stage {destination}.",
                catalog.get(mission)?.name
            )));
        } else if entry.flag == 6 {
            let mission = active.mission.unwrap();
            let candidate =
                self.mission_transition_candidate(mission, active.mission_stage, None)?;
            self.commit_inventory(candidate)?;
            self.npc_interaction.as_mut().unwrap().mission = None;
            self.pending.push(packets::message(&format!(
                "Mission completed: {}.",
                crate::missions::MissionCatalog::load()?.get(mission)?.name
            )));
        }
        // A few public talk trees advertise trade without a vendor group. Keep
        // the conversation usable while making the content gap explicit.
        if entry.flag == 1
            && self
                .npcs
                .group(self.npcs.npc(identity, self.sector.id)?)
                .is_err()
        {
            entry.flag = 0;
            entry.text = "I have no inventory available at the moment.".into();
            entry.branches = vec![Branch {
                node: 0,
                text: "Goodbye.".into(),
            }];
            crate::server_error!("NPC vendor content unavailable: npc={identity}");
        }
        let text = self.talk_text(&entry.text);
        let mut branches: Vec<(u8, String)> = entry
            .branches
            .iter()
            .map(|branch| (branch.node, self.talk_text(&branch.text)))
            .collect();
        if branches.is_empty() && entry.flag == 0 {
            branches.push((0, "Goodbye.".into()));
        }
        if entry.flag == 1 {
            // Resolve every template/quote before changing the active service.
            let frames = self.vendor_frames(identity)?;
            self.npc_interaction.as_mut().unwrap().vendor = true;
            self.pending.extend(frames);
            self.publish_vendor_prices()?;
            self.pending.push(packets::talk_control(5));
            self.pending.push(packets::talk_control(3));
            self.pending.push(packets::talk_tree(&text, &branches));
            self.pending.push(packets::talk_control(6));
            crate::server_log!(
                "Vendor opened: {}; npc={identity}",
                self.account.character.name
            );
        } else {
            if self
                .npc_interaction
                .as_ref()
                .is_some_and(|active| active.vendor)
            {
                self.npc_interaction.as_mut().unwrap().vendor = false;
                self.pending.push(packets::talk_control(-32));
                self.pending.push(packets::vendor_inventory(&[]));
                self.publish_vendor_prices()?;
            }
            self.pending.push(packets::talk_tree(&text, &branches));
        }
        self.npc_interaction.as_mut().unwrap().node = node;
        Ok(())
    }

    pub(super) fn talk_choice(&mut self, body: &[u8]) -> io::Result<()> {
        if self.sector.station.is_none() {
            return self.rescue_choice(body);
        }
        if body.len() != 5 {
            return Err(invalid("Invalid NPC selection packet length."));
        }
        let mut reader = Reader::new(body);
        let owner = reader.u32()?;
        if owner != self.player_id() && owner != self.player_id() & 0xffffff {
            return Err(invalid("NPC selection owner mismatch."));
        }
        if !self.logged_in || self.hull <= 0. {
            return Err(invalid("NPC interaction unavailable."));
        }
        let choice = reader.take(1)?[0];
        if choice == 230 {
            self.close_npc();
            return Ok(());
        }
        let active = self
            .npc_interaction
            .as_ref()
            .ok_or_else(|| invalid("No NPC conversation is active."))?;
        if !self.npc_in_current_room(active.npc) {
            return Err(invalid("NPC conversation is no longer in your room."));
        }
        let current = self.active_npc_node(active, active.node)?;
        // A modified client cannot jump directly to any trade/mission node.
        if !current.branches.iter().any(|branch| branch.node == choice)
            && !(choice == 0 && (current.branches.is_empty() || active.vendor))
        {
            return Err(invalid("NPC choice was not offered by the current node."));
        }
        if choice == 0 {
            self.close_npc();
            Ok(())
        } else {
            self.show_npc_node(choice)
        }
    }

    fn vendor_frames(&self, identity: u32) -> io::Result<Vec<Frame>> {
        let npc = self.npcs.npc(identity, self.sector.id)?;
        let offers = self.npcs.group(npc)?.inventory(&self.items)?;
        let mut frames: Vec<Frame> = offers
            .iter()
            .map(|offer| self.items.get(offer.item).map(ItemTemplate::packet))
            .collect::<io::Result<_>>()?;
        frames.push(packets::vendor_inventory(&offers));
        Ok(frames)
    }

    pub(super) fn vendor_prices(&self, inventory: &Inventory) -> io::Result<HashMap<u32, u64>> {
        let Some(active) = self.npc_interaction.as_ref().filter(|active| active.vendor) else {
            return Ok(HashMap::new());
        };
        let npc = self.npcs.npc(active.npc, self.sector.id)?;
        let group = self.npcs.group(npc)?;
        inventory
            .templates(&self.items)?
            .into_iter()
            .map(|template| {
                let price = if template.level > 9 {
                    0
                } else {
                    group.buy_price(template.id, &self.items)?
                };
                Ok((template.id, price))
            })
            .collect()
    }

    pub(super) fn publish_vendor_prices(&mut self) -> io::Result<()> {
        let prices = self.vendor_prices(&self.inventory)?;
        self.pending.extend(packets::inventory_prices(
            self.player_id(),
            &self.inventory,
            &prices,
        ));
        Ok(())
    }

    pub(super) fn close_npc(&mut self) {
        if self.npc_interaction.take().is_some() {
            self.pending.push(packets::talk_control(-32));
            self.pending.push(packets::vendor_inventory(&[]));
            self.pending.extend(packets::inventory_prices(
                self.player_id(),
                &self.inventory,
                &HashMap::new(),
            ));
        }
    }

    pub(super) fn restore_npc_service(&mut self) -> io::Result<()> {
        if let Some(active) = self.npc_interaction.clone() {
            if !self.npc_in_current_room(active.npc)
                || self.active_npc_node(&active, active.node).is_err()
            {
                self.close_npc();
            } else if active.vendor {
                self.pending.extend(self.vendor_frames(active.npc)?);
                self.publish_vendor_prices()?;
            }
        }
        Ok(())
    }

    pub(super) fn vendor_transaction(&self, transfer: ItemTransfer) -> io::Result<Inventory> {
        if self.sector.station.is_none() {
            return Err(invalid("Vendor transactions require a station."));
        }
        let active = self
            .npc_interaction
            .as_ref()
            .filter(|active| active.vendor)
            .ok_or_else(|| invalid("Open a vendor before trading."))?;
        if !self.npc_in_current_room(active.npc) {
            return Err(invalid("Enter the vendor's room before trading."));
        }
        let npc = self.npcs.npc(active.npc, self.sector.id)?;
        let group = self.npcs.group(npc)?;
        match (transfer.source, transfer.destination) {
            (4, 1) => {
                let offers = group.inventory(&self.items)?;
                let offer = offers
                    .get(transfer.source_slot)
                    .ok_or_else(|| invalid("Vendor slot is empty or invalid."))?;
                self.inventory.purchased(
                    offer.item,
                    transfer.quantity,
                    offer.price,
                    transfer.destination_slot,
                    &self.items,
                )
            }
            (1, 4) if transfer.destination_slot == 0 => {
                let item = self
                    .inventory
                    .cargo
                    .get(transfer.source_slot)
                    .and_then(Option::as_ref)
                    .ok_or_else(|| invalid("Sale cargo is empty or invalid."))?;
                self.inventory.sold(
                    transfer.source_slot,
                    transfer.quantity,
                    group.buy_price(item.template, &self.items)?,
                    &self.items,
                )
            }
            _ => Err(invalid("Unsupported vendor transfer path or destination.")),
        }
    }
}
