//! Catalog-backed component sources and direct station trades, independent of NPC dialogue windows.
use super::*;

const MAX_MARKET_SOURCES: usize = 128;
const MAX_MARKET_STOCK: usize = 2048;
const MAX_MARKET_QUANTITY: u32 = 10_000;

struct MarketSource {
    station: u32,
    npc: u32,
    buy_price: u64,
    sell_price: u64,
    vendor_slot: u32,
    station_name: String,
    sector_name: String,
    vendor_name: String,
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Returns:
    ///     PlayerSession: An owned aggregate-client TT fixture in Aragoth station.
    fn market_ship() -> PlayerSession {
        let root =
            std::env::temp_dir().join(format!("enb-component-market-{}", rand::random::<u64>()));
        let mut account =
            crate::account::Account::create("trader".into(), "fixture", "Trader".into()).unwrap();
        account.character.race = 0;
        account.character.profession = 1;
        account.save_new(&root).unwrap();
        let initial: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
        let mut ship = PlayerSession::new(initial, Ipv4Addr::LOCALHOST, root).unwrap();
        ship.sector = ship.sectors.sector(17051).unwrap();
        ship.logged_in = true;
        ship.aggregate_station = true;
        ship.station_pose = Some(StationPose {
            room: 0,
            orientation: 0.,
            position: [0.; 3],
            action: 0x41,
        });
        ship.inventory.cargo.fill(None);
        ship.inventory.credits = 10_000;
        ship.pending.clear();
        ship
    }

    /// The directory lists actual local stock first with consistent negotiated prices and bounded metadata.
    #[test]
    fn component_market_sources_and_stock_use_real_component_vendors() {
        let mut ship = market_ship();
        ship.component_market_stock().unwrap();
        let stock = ship.pending.last().unwrap();
        assert_eq!(stock.opcode, 0x2039);
        let mut reader = Reader::new(&stock.body);
        assert_eq!(reader.u32().unwrap(), 17051);
        let count = reader.u32().unwrap();
        assert!(count > 0 && count <= 2048);
        let ids: Vec<_> = (0..count).map(|_| reader.u32().unwrap()).collect();
        assert!(ids.contains(&1520));
        assert!(ids
            .iter()
            .all(|id| ship.items.get(*id).unwrap().item_type == 13));
        assert_eq!(
            ship.pending[..ship.pending.len() - 1]
                .iter()
                .filter(|frame| frame.opcode == 0x25)
                .count(),
            count as usize
        );
        ship.pending.clear();
        ship.component_market_query(1520, false).unwrap();
        let response = ship.pending.last().unwrap();
        assert_eq!(response.opcode, 0x2035);
        let mut reader = Reader::new(&response.body);
        assert_eq!(reader.u32().unwrap(), 1520);
        assert_eq!(reader.u32().unwrap(), 17051);
        assert_eq!(reader.u32().unwrap(), 0);
        let count = reader.u32().unwrap();
        assert!(count > 0 && count <= 128);
        let mut local = None;
        let mut current_finished = false;
        for _ in 0..count {
            let station = reader.u32().unwrap();
            let npc = reader.u32().unwrap();
            let buy = u64::from_le_bytes(reader.take(8).unwrap().try_into().unwrap());
            let sell = u64::from_le_bytes(reader.take(8).unwrap().try_into().unwrap());
            let slot = reader.u32().unwrap();
            let station_name = reader.string().unwrap();
            let sector_name = reader.string().unwrap();
            let vendor_name = reader.string().unwrap();
            assert!(
                station_name.len() <= 128 && sector_name.len() <= 128 && vendor_name.len() <= 128
            );
            if station != 17051 {
                current_finished = true;
            } else {
                assert!(!current_finished);
            }
            if station == 17051 && npc == 9 {
                local = Some((buy, sell, slot));
            }
        }
        assert!(reader.rest().is_empty());
        let group = ship.npcs.group(ship.npcs.npc(9, 17051).unwrap()).unwrap();
        let offers = group.inventory(&ship.items).unwrap();
        let slot = offers.iter().position(|offer| offer.item == 1520).unwrap();
        assert_eq!(
            local.unwrap(),
            (
                ship.negotiated_purchase_price(offers[slot].price).unwrap(),
                ship.negotiated_sale_price(group.buy_price(1520, &ship.items).unwrap())
                    .unwrap(),
                slot as u32
            )
        );
        ship.sector = ship.sectors.sector(1076).unwrap();
        ship.pending.clear();
        ship.component_market_stock().unwrap();
        assert_eq!(ship.pending.last().unwrap().body, vec![0; 8]);
    }

    /// Direct trading persists cargo/credits while preserving the selected workbench and NPC interaction.
    #[test]
    fn component_market_trades_are_durable_local_and_preserve_service_windows() {
        let mut ship = market_ship();
        ship.aggregate_station = false;
        assert!(ship.component_market_trade(9, 1520, 0, 0, 1).is_err()); // Native avatar is in room zero; vendor is in room two.
        ship.aggregate_station = true;
        ship.initialise_workbench_lab(true).unwrap();
        ship.workbench_input(
            0x79,
            &[ship.player_id().to_be_bytes(), 1u32.to_be_bytes()].concat(),
        )
        .unwrap();
        ship.open_npc(9).unwrap();
        let dialogue = serde_json::to_value(&ship.npc_interaction).unwrap();
        let workbench = serde_json::to_value(&ship.workbench).unwrap();
        let group = ship.npcs.group(ship.npcs.npc(9, 17051).unwrap()).unwrap();
        let offer = group
            .inventory(&ship.items)
            .unwrap()
            .into_iter()
            .find(|offer| offer.item == 1520)
            .unwrap();
        let buy = ship.negotiated_purchase_price(offer.price).unwrap();
        let sell = ship
            .negotiated_sale_price(group.buy_price(1520, &ship.items).unwrap())
            .unwrap();
        ship.component_market_trade(9, 1520, 0, 0, 2).unwrap();
        assert_eq!(ship.inventory.credits, 10_000 - buy * 2);
        assert_eq!(ship.inventory.cargo[0].as_ref().unwrap().quantity, 2);
        ship.component_market_trade(9, 1520, 1, 0, 1).unwrap();
        assert_eq!(ship.inventory.credits, 10_000 - buy * 2 + sell);
        assert_eq!(ship.inventory.cargo[0].as_ref().unwrap().quantity, 1);
        assert_eq!(
            serde_json::to_value(&ship.npc_interaction).unwrap(),
            dialogue
        );
        assert_eq!(serde_json::to_value(&ship.workbench).unwrap(), workbench);
        let saved: Inventory =
            serde_json::from_slice(&fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        assert_eq!(saved, ship.inventory);
        let unchanged = ship.inventory.clone();
        for (npc, template, action, slot, quantity) in [
            (16, 1520, 0, 0, 1),
            (9, 1520, 0, 0, 0),
            (9, 1520, 0, 0, 10_001),
            (9, 1520, 1, 0, 2),
            (9, 1521, 1, 0, 1),
            (9, 1520, 9, 0, 1),
        ] {
            assert!(ship
                .component_market_trade(npc, template, action, slot, quantity)
                .is_err());
            assert_eq!(ship.inventory, unchanged);
        }
        let spoofed = [ship.player_id() + 1, 9, 1520, 0, 0, 1]
            .map(u32::to_le_bytes)
            .concat();
        assert!(ship.handle_input(0x2036, &spoofed).is_err());
        assert!(ship
            .handle_input(
                0x2037,
                &[ship.player_id() + 1, 17051].map(u32::to_le_bytes).concat()
            )
            .is_err());
        fs::create_dir_all(ship.state_dir.join("inventory.json.tmp")).unwrap();
        assert!(ship.component_market_trade(9, 1520, 0, 0, 1).is_err());
        assert_eq!(ship.inventory, unchanged);
    }
}

/// Append a bounded native-length UTF-8 source label.
/// Raises:
///     io::Error: A catalog label exceeds the component market's frame budget.
fn source_name(body: &mut Vec<u8>, name: &str) -> io::Result<()> {
    if name.len() > 128 {
        return Err(invalid("Component market source label is too long."));
    }
    body.extend_from_slice(&(name.len() as u16).to_le_bytes());
    body.extend_from_slice(name.as_bytes());
    Ok(())
}

impl PlayerSession {
    /// Accept native components and other item types explicitly used as manufacturing ingredients.
    /// Raises:
    ///     io::Error: The template is absent, too high level, or is not a manufacturing component.
    fn check_market_component(&self, template: u32) -> io::Result<()> {
        let item = self.items.get(template)?;
        if !(1..=9).contains(&item.level)
            || item.item_type != 13
                && !self
                    .items
                    .iter()
                    .any(|candidate| candidate.components.contains(&template))
        {
            return Err(invalid("Select a component or manufacturing ingredient."));
        }
        Ok(())
    }

    /// Publish actual buying/selling sources and this pilot's negotiated quotes.
    /// Raises:
    ///     io::Error: The character/template is invalid or catalog data exceeds bounded transport limits.
    pub(crate) fn component_market_query(
        &mut self,
        template: u32,
        developer_enabled: bool,
    ) -> io::Result<()> {
        if !self.logged_in {
            return Err(invalid("Enter the game before querying component sources."));
        }
        self.check_market_component(template)?;
        let current = self.sector.station.as_ref().map_or(0, |station| station.id);
        let mut rows = Vec::new();
        for sector in self.sectors.hosted_stations() {
            let station = sector.station.as_ref().unwrap();
            let parent = self.sectors.sector(station.parent)?;
            for placed in &station.npcs {
                let npc = self.npcs.npc(placed.id, station.id)?;
                let Ok(group) = self.npcs.group(npc) else {
                    continue;
                };
                let offers = group.inventory(&self.items)?;
                let offered = offers
                    .iter()
                    .enumerate()
                    .find(|(_, offer)| offer.item == template);
                let buy_price = offered.map_or(Ok(0), |(_, offer)| {
                    self.negotiated_purchase_price(offer.price)
                })?;
                let sell_price =
                    self.negotiated_sale_price(group.buy_price(template, &self.items)?)?;
                if buy_price == 0 && sell_price == 0 {
                    continue;
                }
                rows.push(MarketSource {
                    station: station.id,
                    npc: npc.id,
                    buy_price,
                    sell_price,
                    vendor_slot: offered.map_or(u32::MAX, |(slot, _)| slot as u32),
                    station_name: station.name.clone(),
                    sector_name: parent.name.clone(),
                    vendor_name: npc.name.clone(),
                });
            }
        }
        rows.sort_by(|a, b| {
            (
                a.station != current,
                a.buy_price == 0,
                &a.station_name,
                &a.vendor_name,
                a.npc,
            )
                .cmp(&(
                    b.station != current,
                    b.buy_price == 0,
                    &b.station_name,
                    &b.vendor_name,
                    b.npc,
                ))
        });
        rows.truncate(MAX_MARKET_SOURCES);
        let mut body = Vec::new();
        for value in [
            template,
            current,
            u32::from(developer_enabled),
            rows.len() as u32,
        ] {
            body.extend_from_slice(&value.to_le_bytes());
        }
        for row in rows {
            body.extend_from_slice(&row.station.to_le_bytes());
            body.extend_from_slice(&row.npc.to_le_bytes());
            body.extend_from_slice(&row.buy_price.to_le_bytes());
            body.extend_from_slice(&row.sell_price.to_le_bytes());
            body.extend_from_slice(&row.vendor_slot.to_le_bytes());
            source_name(&mut body, &row.station_name)?;
            source_name(&mut body, &row.sector_name)?;
            source_name(&mut body, &row.vendor_name)?;
        }
        self.pending.push(self.items.get(template)?.packet());
        self.pending.push(Frame::new(0x2035, body));
        Ok(())
    }

    /// Publish all local native component stock with template metadata before the directory.
    /// Raises:
    ///     io::Error: The session is unauthenticated or local stock exceeds its transport bound.
    pub(super) fn component_market_stock(&mut self) -> io::Result<()> {
        if !self.logged_in {
            return Err(invalid(
                "Enter the game before opening the component market.",
            ));
        }
        let mut stock = BTreeSet::new();
        let station = self.sector.station.as_ref();
        if let Some(station) = station {
            for placed in &station.npcs {
                let npc = self.npcs.npc(placed.id, station.id)?;
                let Ok(group) = self.npcs.group(npc) else {
                    continue;
                };
                for offer in group.inventory(&self.items)? {
                    let item = self.items.get(offer.item)?;
                    if item.item_type == 13 && (1..=9).contains(&item.level) {
                        stock.insert(item.id);
                    }
                }
            }
        }
        if stock.len() > MAX_MARKET_STOCK {
            return Err(invalid(
                "Local component stock exceeds the market directory limit.",
            ));
        }
        let mut body = Vec::from(station.map_or(0, |station| station.id).to_le_bytes());
        body.extend_from_slice(&(stock.len() as u32).to_le_bytes());
        for template in stock {
            self.pending.push(self.items.get(template)?.packet());
            body.extend_from_slice(&template.to_le_bytes());
        }
        self.pending.push(Frame::new(0x2039, body));
        Ok(())
    }

    /// Commit a local catalog-priced purchase or sale while preserving dialogue and production windows.
    /// Raises:
    ///     io::Error: The vendor, item, cargo, quantity, or durable inventory transaction is invalid.
    pub(super) fn component_market_trade(
        &mut self,
        npc: u32,
        template: u32,
        action: u32,
        cargo_slot: u32,
        quantity: u32,
    ) -> io::Result<()> {
        if !self.logged_in
            || self.hull <= 0.
            || self.sector.station.is_none()
            || self.handoff.is_some()
            || self.transition_pending.is_some()
            || !self.npc_in_current_room(npc)
        {
            return Err(invalid("Use a vendor in your current station concourse."));
        }
        if quantity == 0 || quantity > MAX_MARKET_QUANTITY {
            return Err(invalid("Component trade quantity must be 1 through 10000."));
        }
        self.check_market_component(template)?;
        let vendor = self.npcs.npc(npc, self.sector.id)?;
        let group = self.npcs.group(vendor)?;
        let vendor_name = vendor.name.clone();
        let candidate = match action {
            0 => {
                let offers = group.inventory(&self.items)?;
                let offer = offers
                    .iter()
                    .find(|offer| offer.item == template)
                    .ok_or_else(|| invalid("This vendor does not sell that component."))?;
                if group
                    .offers
                    .iter()
                    .find(|row| row.item == template)
                    .is_some_and(|row| row.quantity > 0 && quantity > row.quantity as u32)
                {
                    return Err(invalid(
                        "Requested quantity exceeds this vendor's catalog stock.",
                    ));
                }
                self.inventory.purchased(
                    template,
                    quantity,
                    self.negotiated_purchase_price(offer.price)?,
                    cargo_slot as usize,
                    &self.items,
                )?
            }
            1 => {
                if self
                    .inventory
                    .cargo
                    .get(cargo_slot as usize)
                    .and_then(Option::as_ref)
                    .is_none_or(|item| item.template != template)
                {
                    return Err(invalid("Sale cargo no longer matches this component."));
                }
                self.inventory.sold(
                    cargo_slot as usize,
                    quantity,
                    self.negotiated_sale_price(group.buy_price(template, &self.items)?)?,
                    &self.items,
                )?
            }
            _ => return Err(invalid("Unknown component market trade action.")),
        };
        self.commit_inventory(candidate)?;
        self.refresh_workbench(true)?;
        self.message(&format!(
            "{} {quantity} x {} {} {vendor_name}.",
            if action == 0 { "Purchased" } else { "Sold" },
            self.items.get(template)?.name,
            if action == 0 { "from" } else { "to" }
        ));
        self.client_events
            .push(ClientEvent::ComponentMarketQuery { template });
        Ok(())
    }
}
