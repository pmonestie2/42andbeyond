//! Reproduce native pre/post rank-up AUX fixtures with disposable in-memory state.
use beyond_server::account::Account;
use beyond_server::inventory::Inventory;
use beyond_server::items::ItemCatalog;
use beyond_server::packets;
use beyond_server::sectors::SectorCatalog;
use beyond_server::skills::SkillCatalog;
use std::io;

/// Returns:
///     String: Lowercase hexadecimal form of a native frame body.
fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

/// Print two full global player AUX bodies around a legal JE beam purchase.
///
/// Returns:
///     io::Result<()>: Successful deterministic fixture generation.
fn main() -> io::Result<()> {
    let profile = Account::create("fixture".into(), "fixture", "Fixture".into())?.character;
    let sector = SectorCatalog::load()?.sector(1076)?;
    let items = ItemCatalog::load()?;
    let mut inventory = Inventory::native_starter_for(&profile, &items)?;
    inventory.combat_xp = 500;
    let picker = packets::characters(&sector, &profile, &inventory);
    let prior = packets::player_aux(&sector, &profile, &inventory, &items);
    let skills = SkillCatalog::for_inventory(&profile, &inventory)?;
    inventory = skills.purchased_rank(&inventory, 1)?;
    let after = packets::player_aux(&sector, &profile, &inventory, &items);
    println!("before 0x{:02x} {}", prior.opcode, hex(&prior.body));
    println!("after  0x{:02x} {}", after.opcode, hex(&after.body));
    println!("picker 0x{:02x} {}", picker.opcode, hex(&picker.body));
    Ok(())
}
