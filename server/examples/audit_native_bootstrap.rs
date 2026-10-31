//! Read-only native bootstrap audit; never opens sockets or changes account state.
use beyond_server::{
    account::CharacterProfile, inventory::Inventory, items::ItemCatalog, packets,
    sectors::SectorCatalog,
};
use std::{fs, io, path::PathBuf};
/// Print packet sizes without logging credentials or inventory contents.
/// Raises:
///     io::Error: The explicitly supplied account files cannot be decoded.
fn main() -> io::Result<()> {
    let root = PathBuf::from(
        std::env::args()
            .nth(1)
            .ok_or_else(|| io::Error::other("Supply an account directory"))?,
    );
    let account: serde_json::Value =
        serde_json::from_slice(&fs::read(root.join("account.local.json"))?)?;
    let profile: CharacterProfile = serde_json::from_value(account["character"].clone())?;
    let inventory: Inventory = serde_json::from_slice(&fs::read(root.join("inventory.json"))?)?;
    let player: serde_json::Value = serde_json::from_slice(&fs::read(root.join("player.json"))?)?;
    let sector = SectorCatalog::load()?.sector(player["sector"].as_u64().unwrap() as u32)?;
    let items = ItemCatalog::load()?;
    let aux = packets::player_aux(&sector, &profile, &inventory, &items).encode()?;
    let sizes: Vec<_> = inventory
        .templates(&items)?
        .iter()
        .map(|item| item.packet().encode().unwrap().len())
        .collect();
    println!(
        "Own player AUX: {} bytes; metadata count {}; largest metadata {} bytes",
        aux.len(),
        sizes.len(),
        sizes.iter().max().unwrap_or(&0)
    );
    println!(
        "Native continuation required: {}",
        aux.len() > beyond_server::protocol::MTU_PAYLOAD
    );
    let skills = beyond_server::skills::SkillCatalog::for_inventory(&profile, &inventory)?;
    let stats =
        beyond_server::equipment_stats::EquipmentStats::derive(&inventory, &items, &skills)?;
    let frames = packets::login_world(
        &sector,
        [0.; 3],
        0,
        &profile,
        &beyond_server::motion::Motion::default(),
        &packets::ShipInitialization {
            catalog: &items,
            item_templates: inventory.templates(&items)?,
            inventory: &inventory,
            stats: &stats.ship,
            resources: packets::ShipResources {
                energy: stats.ship.energy_capacity,
                shield: stats.ship.shield_capacity,
                hull: stats.ship.hull_capacity,
            },
        },
    );
    for frame in frames.iter().filter(|frame| frame.opcode == 0x1b) {
        println!(
            "Login AUX object {}: {} bytes",
            u32::from_le_bytes(frame.body[..4].try_into().unwrap()),
            frame.encode()?.len()
        );
    }
    Ok(())
}
