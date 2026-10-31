//! One durable decision commits both inventories; lagging account files replay it.
use crate::{inventory::Inventory, items::ItemCatalog, protocol::invalid};
use serde::{Deserialize, Serialize};
use std::{
    fs, io,
    path::{Component, Path, PathBuf},
};

#[derive(Serialize, Deserialize)]
pub(crate) struct TradeParty {
    pub character: u32,
    pub directory: PathBuf,
    pub inventory: Inventory,
}

#[derive(Serialize, Deserialize)]
struct TradeCommit {
    version: u32,
    parties: [TradeParty; 2],
}

/// Returns:
///     PathBuf: Journal-owned account directory, restricted to this server root.
/// Raises:
///     io::Error: The path or account identity is invalid.
fn party_directory(root: &Path, party: &TradeParty) -> io::Result<PathBuf> {
    if party.directory.is_absolute()
        || party
            .directory
            .components()
            .any(|part| !matches!(part, Component::Normal(_) | Component::CurDir))
    {
        return Err(invalid(
            "Trade journal account path escapes the server root.",
        ));
    }
    let path = root.join(&party.directory);
    if !path.join("account.local.json").is_file() {
        return Err(invalid("Trade journal account is missing."));
    }
    let account = crate::account::Account::load(&path)?;
    if account.character.game_id != party.character {
        return Err(invalid("Trade journal account identity changed."));
    }
    Ok(path)
}

/// Replay a committed pair without overwriting newer inventory mutations.
/// Raises:
///     io::Error: A journal is invalid or account storage cannot be updated.
pub(crate) fn recover(root: &Path) -> io::Result<()> {
    let path = root.join("trade_commit.local.json");
    let data = match fs::read(&path) {
        Ok(data) => data,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error),
    };
    let commit: TradeCommit = serde_json::from_slice(&data)?;
    if commit.version != 1 || commit.parties[0].character == commit.parties[1].character {
        return Err(invalid("Invalid committed player trade."));
    }
    let items = ItemCatalog::load()?;
    let mut writes = Vec::new();
    for party in &commit.parties {
        party.inventory.validate(&items)?;
        if party.inventory.trade_revision == 0 {
            return Err(invalid("Trade journal has no committed revision."));
        }
        let path = party_directory(root, party)?.join("inventory.json");
        let revision = match fs::read(&path) {
            Ok(data) => serde_json::from_slice::<Inventory>(&data)?.trade_revision,
            Err(error) if error.kind() == io::ErrorKind::NotFound => 0,
            Err(error) => return Err(error),
        };
        if revision < party.inventory.trade_revision {
            writes.push((path, &party.inventory));
        }
    }
    for (path, inventory) in writes {
        crate::durable_json::replace(&path, inventory)?;
    }
    fs::remove_file(path)
}

/// Persist the atomic commit decision before either account changes in memory.
/// Raises:
///     io::Error: An earlier decision cannot be replayed or durable storage fails.
pub(crate) fn commit(root: &Path, parties: [TradeParty; 2]) -> io::Result<()> {
    recover(root)?;
    for party in &parties {
        party_directory(root, party)?;
    }
    crate::durable_json::replace(
        &root.join("trade_commit.local.json"),
        &TradeCommit {
            version: 1,
            parties,
        },
    )
}
