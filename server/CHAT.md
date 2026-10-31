# Chat protocol

The server accepts both native chat requests:

- `0x0033`: owner (u32 LE), channel (u8), text length (u16 LE), text. Channels 0/1/3/4 select the targeted pilot, group, local area, or sector. Guild chat requires a guild implementation and is rejected explicitly.
- `0x00a3`: owner and kind (u32 LE), three u16-LE length-prefixed strings (recipient, channel, message), then a native u32 option or a u32-length-prefixed presentation trailer (maximum 2048 bytes). The Windows formatted trailer is ignored; only the plain message fields determine routing and content. Kind 0 sends to a channel; 1 sends privately by character name; 6 subscribes; 7 unsubscribes. The inner owner may use the authenticated pilot’s full ID or matching low 24 bits. Sender identity always comes from the authenticated session. Auxiliary startup kinds 29/30 are parsed and ignored, matching the archived server.

Named channels match the archived Windows client, including General Chat, Market, New Players, race/class channels, Broadcast and Explorers. Named subscriptions are scoped to the running server. Local chat is limited to 50 km in the same sector; sector chat stays in that sector; group chat reaches logged-in members across sectors. Private chat works across sectors and echoes to its sender.

User messages use the original `0x00a5` ClientChatEvent response. The body contains u32 values 3 and 0, sender and sender length-strings, a zero u16, channel and message length-strings, then six zero bytes. Strings have no terminator. System notices continue to use `0x001d`.

`/tell NAME MESSAGE` (also `/t`, `/whisper`, `/w`) sends a private message. `/reply MESSAGE` or `/r MESSAGE` replies to the previous private correspondent. `/help` lists commands.

Focused validation:

```sh
cargo test --manifest-path server/Cargo.toml --lib native_chat::tests
PYTHONPATH=protocol python3 server/tools/smoke_chat.py --binary server/target/debug/beyond-server
```

The socket smoke owns a disposable loopback server and temporary accounts. It never drives the live Windows or Mac clients. Rust and C# share `client/Tests/Fixtures/native_chat_event.hex`.
