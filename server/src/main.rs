use beyond_server::accounts::AccountDirectory;
use beyond_server::content::Sector;
use beyond_server::server::{self, Config};
use std::io;
use std::net::Ipv4Addr;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

/// Report all process failures with the same timestamped server logger.
fn main() {
    std::panic::set_hook(Box::new(|info| {
        beyond_server::server_error!("Server panic: {info}");
    }));
    if let Err(error) = run() {
        beyond_server::server_error!("Server failed: {error}");
        std::process::exit(1);
    }
}

/// Parse command-line modes, then run the owned server or offline action.
fn run() -> io::Result<()> {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let mut bind = Ipv4Addr::new(127, 0, 0, 1);
    let mut content = root.join("data/asteroid_belt_alpha.json");
    let mut state_dir = root.join("state");
    let mut check = false;
    let mut seed_play_accounts = false;
    let mut create_account: Option<String> = None;
    let mut character = String::from("SandboxPilot");
    let mut class = beyond_server::classes::CharacterClass::parse("je")?;
    let mut arguments = std::env::args().skip(1);
    while let Some(argument) = arguments.next() {
        match argument.as_str() {
            "--bind" => {
                bind = arguments
                    .next()
                    .ok_or_else(|| io::Error::other("--bind needs an IPv4 address"))?
                    .parse()
                    .map_err(|_| io::Error::other("invalid bind address"))?;
            }
            "--content" => {
                content = arguments
                    .next()
                    .map(PathBuf::from)
                    .ok_or_else(|| io::Error::other("--content needs a JSON path"))?
            }
            "--state-dir" => {
                state_dir = arguments
                    .next()
                    .map(PathBuf::from)
                    .ok_or_else(|| io::Error::other("--state-dir needs a directory"))?
            }
            "--check" => check = true,
            "--seed-demo-accounts" | "--seed-play-accounts" => seed_play_accounts = true,
            "--create-account" => create_account = arguments.next(),
            "--class" => {
                class = beyond_server::classes::CharacterClass::parse(
                    &arguments
                        .next()
                        .ok_or_else(|| io::Error::other("--class needs a class code"))?,
                )?;
            }
            "--character" => {
                character = arguments
                    .next()
                    .ok_or_else(|| io::Error::other("--character needs a name"))?
            }
            "--help" | "-h" => {
                beyond_server::server_log!("beyond-server [--bind 127.0.0.1] [--content data/sector.json] [--state-dir state] [--check] [--create-account NAME --character NAME --class te|tt|ts|jd|js|je|pw|pp|ps] [--seed-demo-accounts]\nConnect the 42 and Beyond client, or use win_legacy/launch_original_client.cmd on Windows. Demo account: local / local.");
                return Ok(());
            }
            _ => return Err(io::Error::other(format!("unknown option {argument}"))),
        }
    }
    if seed_play_accounts {
        if check || create_account.is_some() {
            return Err(io::Error::other(
                "Seed demos separately from --check or --create-account.",
            ));
        }
        let codes = beyond_server::demo_accounts::seed(&state_dir)?;
        beyond_server::server_log!("Published demo pilots: {}", codes.join(", "));
        return Ok(());
    }
    if let Some(username) = create_account {
        let mut password = String::new();
        io::stdin().read_line(&mut password)?;
        let password = password.trim_end_matches(['\r', '\n']);
        AccountDirectory::new(&state_dir).create_class(
            username.clone(),
            password,
            character.clone(),
            class,
        )?;
        beyond_server::server_log!("Created local account {username}; character {character}; class {}. Password hash saved.", class.code());
        return Ok(());
    }
    // Loopback is the default. Private LAN addresses also allow a Linux/macOS
    // server to serve the Windows proxy on another machine. Public exposure is
    // outside this prototype's account/transport security model.
    if !bind.is_loopback() && !bind.is_private() {
        return Err(io::Error::other(
            "bind to an explicit loopback or private LAN address",
        ));
    }
    let sector: Sector =
        serde_json::from_slice(&std::fs::read(&content)?).map_err(io::Error::other)?;
    sector.validate()?;
    if check {
        beyond_server::server_log!(
            "Configuration valid: sector={} {}, navpoints={}, mobs={}, bind={bind}",
            sector.id,
            sector.name,
            sector.navpoints.len(),
            sector.mobs.len()
        );
        return Ok(());
    }
    let stop = Arc::new(AtomicBool::new(false));
    let signal = Arc::clone(&stop);
    ctrlc::set_handler(move || signal.store(true, Ordering::Relaxed)).map_err(io::Error::other)?;
    server::run(
        Config {
            bind,
            sector,
            state_dir,
        },
        stop,
    )
}
