//! Authenticated client requests that affect more than one player. A session
//! validates its wire owner/endpoint before the shared coordinator handles them.
pub enum ClientEvent {
    TradeAction { action: u32, target: u32, optional: u32 },
    TradeMove { transfer: crate::inventory::ItemTransfer },
    ComponentMarketQuery {
        template: u32,
    },
    ComponentMarketTravel {
        station: u32,
    },
    LootOpen {
        target: u32,
        toggle: bool,
    },
    LootBegin {
        target: u32,
        slot: usize,
    },
    Group {
        action: u32,
        target: u32,
    },
    JumpstartOffer {
        target: u32,
    },
    NamedChat { kind: u32, recipient: String, channel: String, text: String },
    Chat {
        channel: u8,
        text: String,
    },
    Formation {
        action: u32,
        target: u32,
    },
    EnergyTransfer {
        target: u32,
        slot: u8,
        epoch: u64,
        sequence: u32,
    },
    PeerDeviceBuff {
        target: u32,
        slot: u8,
        epoch: u64,
        sequence: u32,
    },
}
