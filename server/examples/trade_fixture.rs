//! Emit public protocol fixtures only; never opens sockets or private account state.
use beyond_server::{inventory::ItemInstance, items::ItemCatalog, packets};

/// Print native six-slot offer bodies for the independent client decoder tests.
fn main() -> std::io::Result<()> {
    let catalog = ItemCatalog::load()?;
    let mut slots = vec![None; 6];
    slots[2] = Some(ItemInstance {
        template: 2505,
        quantity: 1,
        quality: 1.25,
        structure: 0.5,
        builder_name: "Trader".into(),
    });
    for (label, owner, credits, items) in [
        ("own", 0x40000001, 1234567890123, slots),
        ("peer", 0x40000002, 4321, vec![None; 6]),
    ] {
        let frame = packets::trade_offer(owner, credits, &items, &catalog);
        println!(
            "{label}: {}",
            frame
                .body
                .iter()
                .map(|byte| format!("{byte:02x}"))
                .collect::<String>()
        );
    }
    Ok(())
}
