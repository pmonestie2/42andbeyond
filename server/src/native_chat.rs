//! Original 0xa3 channel requests and 0xa5 events, shared by native and Unity pilots.
use super::*;

const CHANNELS: &[&str] = &[
    "Broadcast",
    "General Chat",
    "Explorers",
    "Private Channel",
    "Out of Context",
    "Warriors",
    "Market",
    "Tradesman",
    "New Players",
    "Sentinels",
    "Terran",
    "Defenders",
    "Jenquai",
    "Enforcers",
    "Progen",
    "Scouts",
    "Seekers",
    "Privateers",
];

/// Returns:
///     String: A bounded native length-prefixed field without a trailing terminator.
/// Raises:
///     io::Error: The field exceeds its bound or the packet is truncated.
pub(crate) fn read_text(reader: &mut Reader<'_>, limit: usize) -> io::Result<String> {
    let size = usize::from(reader.u16()?);
    if size > limit {
        return Err(invalid("Chat text field is too long."));
    }
    let value = String::from_utf8_lossy(reader.take(size)?)
        .trim_end_matches('\0')
        .to_owned();
    if value.contains('\0') {
        return Err(invalid("Chat text contains an embedded terminator."));
    }
    Ok(value)
}

/// Consume optional native presentation data without using it as chat content or identity.
/// Raises:
///     io::Error: The trailing field is missing, oversized, or its declared length is inconsistent.
pub(crate) fn read_trailer(reader: &mut Reader<'_>) -> io::Result<()> {
    let declared = reader.u32()? as usize;
    let remaining = reader.rest().len();
    // Some clients send only an option word. Windows also sends a length-prefixed
    // formatted duplicate of the message, which the archived server ignores.
    if remaining != 0 {
        if remaining > 2048 || declared != remaining {
            return Err(invalid("Invalid native chat presentation length."));
        }
        reader.take(remaining)?;
    }
    Ok(())
}

/// Returns:
///     Frame: Original ClientChatEvent with sender, channel and message kept separate.
pub(super) fn event(sender: &str, channel: &str, message: &str) -> Frame {
    let mut body = Vec::from(3u32.to_le_bytes());
    body.extend(0u32.to_le_bytes());
    crate::protocol::string(&mut body, sender);
    crate::protocol::string(&mut body, sender);
    body.extend(0u16.to_le_bytes());
    crate::protocol::string(&mut body, channel);
    crate::protocol::string(&mut body, message);
    body.extend([0; 6]);
    Frame::new(0xa5, body)
}

impl GameState {
    /// Deliver chat through the original client's channel-aware response, without duplicate log messages.
    pub(super) fn deliver_chat(
        &mut self,
        source: u32,
        recipients: Vec<u32>,
        channel: &str,
        text: &str,
    ) {
        let name = self.sessions[&source].account.character.name.clone();
        let mut unique = HashSet::new();
        for id in recipients {
            if unique.insert(id) {
                if let Some(session) = self
                    .sessions
                    .get_mut(&id)
                    .filter(|session| session.logged_in)
                {
                    session.queue([event(&name, channel, text)]);
                }
            }
        }
    }

    /// Send a private message to an authenticated online name, retaining reply identities.
    /// Raises:
    ///     io::Error: The destination is offline or the message is empty/too large.
    pub(super) fn private_chat(
        &mut self,
        source: u32,
        recipient: &str,
        text: &str,
    ) -> io::Result<()> {
        let target = self
            .sessions
            .iter()
            .find_map(|(id, session)| {
                (session.logged_in
                    && session
                        .account
                        .character
                        .name
                        .eq_ignore_ascii_case(recipient.trim()))
                .then_some(*id)
            })
            .ok_or_else(|| invalid("That character is not online."))?;
        self.validate_chat_text(text)?;
        self.chat_replies.insert(source, target);
        self.chat_replies.insert(target, source);
        self.deliver_chat(source, vec![source, target], "Private", text.trim());
        Ok(())
    }

    /// Validate one bounded nonempty chat message.
    /// Raises:
    ///     io::Error: The message is empty, oversized or contains a terminator.
    fn validate_chat_text(&self, text: &str) -> io::Result<()> {
        if text.trim().is_empty() || text.len() > 1024 || text.contains('\0') {
            return Err(invalid("Chat must contain 1–1024 bytes of text."));
        }
        Ok(())
    }

    /// Route native channel messages, tells and channel subscriptions using the server's sender identity.
    /// Raises:
    ///     io::Error: The channel, request kind, recipient or command is unavailable.
    pub(super) fn named_chat(
        &mut self,
        source: u32,
        kind: u32,
        recipient: &str,
        channel: &str,
        text: &str,
    ) -> io::Result<()> {
        if kind == 1 {
            return self.private_chat(source, recipient, text);
        }
        if !matches!(kind, 0 | 6 | 7) {
            return Err(invalid("Unsupported chat request."));
        }
        if kind == 0 && text.trim_start().starts_with('/') {
            return self.chat(source, 4, text);
        }
        let trimmed = channel.trim();
        if matches!(kind, 6 | 7)
            && [
                "Group",
                "Local",
                "Local Area",
                "Sector",
                "Entire Sector",
                "Guild",
            ]
            .iter()
            .any(|name| name.eq_ignore_ascii_case(trimmed))
        {
            return Ok(());
        }
        if kind == 0 {
            self.validate_chat_text(text)?;
            match trimmed.to_ascii_lowercase().as_str() {
                "group" => return self.chat(source, 1, text),
                "local" | "local area" => return self.chat(source, 3, text),
                "sector" | "entire sector" | "" => return self.chat(source, 4, text),
                "guild" => return Err(invalid("Guilds are not implemented yet.")),
                _ => {}
            }
        }
        let canonical = CHANNELS
            .iter()
            .copied()
            .find(|name| name.eq_ignore_ascii_case(trimmed))
            .or_else(|| match trimmed.to_ascii_lowercase().as_str() {
                "general" => Some("General Chat"),
                "newplayers" => Some("New Players"),
                _ => None,
            })
            .ok_or_else(|| invalid("Unknown chat channel."))?;
        let key = canonical.to_ascii_lowercase();
        if kind == 6 || kind == 7 {
            self.chat_subscriptions
                .entry(source)
                .or_default()
                .insert(key, kind == 6);
            return Ok(());
        }
        let recipients = self
            .sessions
            .iter()
            .filter_map(|(id, session)| {
                (session.logged_in
                    && (*id == source
                        || self
                            .chat_subscriptions
                            .get(id)
                            .and_then(|channels| channels.get(&key))
                            .copied()
                            .unwrap_or(false)))
                .then_some(*id)
            })
            .collect();
        self.deliver_chat(source, recipients, canonical, text.trim());
        Ok(())
    }

    /// Reply to the last private conversation without accepting an arbitrary client-supplied player ID.
    /// Raises:
    ///     io::Error: There is no online previous private correspondent.
    pub(super) fn reply_chat(&mut self, source: u32, text: &str) -> io::Result<()> {
        let id = self
            .chat_replies
            .get(&source)
            .ok_or_else(|| invalid("No private message to reply to."))?;
        let name = self
            .sessions
            .get(id)
            .filter(|session| session.logged_in)
            .map(|session| session.account.character.name.clone())
            .ok_or_else(|| invalid("That character is not online."))?;
        self.private_chat(source, &name, text)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::account::FIRST_PLAYER_ID;

    /// Returns:
    ///     GameState: Two authenticated disposable pilots with their startup traffic drained.
    fn pair() -> GameState {
        let mut game = super::super::tests::game();
        super::super::tests::login(&mut game, FIRST_PLAYER_ID, 23621, "alice");
        super::super::tests::login(&mut game, FIRST_PLAYER_ID + 1, 23622, "bob");
        drain(&mut game);
        game
    }

    /// Returns:
    ///     HashMap<u32, Vec<Frame>>: Only channel-aware native chat responses from drained transport.
    fn drain(game: &mut GameState) -> HashMap<u32, Vec<Frame>> {
        let mut messages: HashMap<u32, Vec<Frame>> = HashMap::new();
        for (id, session) in &mut game.sessions {
            for _ in 0..128 {
                session.finish_tick().unwrap();
                let output = session.take_outgoing();
                if output.is_empty() {
                    break;
                }
                for outbound in output {
                    let packet = Datagram::decode(&outbound.data).unwrap();
                    if packet.opcode != 0x2016 {
                        continue;
                    }
                    let mut body = packet.body.as_slice();
                    while body.len() >= 4 {
                        let size = usize::from(u16::from_le_bytes(body[..2].try_into().unwrap()));
                        if size < 4 || size > body.len() {
                            break;
                        }
                        let frame = Frame::decode(&body[..size]).unwrap();
                        if frame.opcode == 0xa5 {
                            messages.entry(*id).or_default().push(frame);
                        }
                        body = &body[size..];
                    }
                }
            }
        }
        messages
    }

    /// Returns:
    ///     Vec<u8>: The original Windows ClientChatRequest body including its trailing option.
    fn request(owner: u32, kind: u32, recipient: &str, channel: &str, message: &str) -> Vec<u8> {
        let mut body = Vec::from(owner.to_le_bytes());
        body.extend(kind.to_le_bytes());
        for value in [recipient, channel, message] {
            crate::protocol::string(&mut body, value);
        }
        body.extend(0u32.to_le_bytes());
        body
    }

    /// Decode the authenticated wire request and run its ordinary coordinator event.
    fn submit(game: &mut GameState, source: u32, body: &[u8]) {
        game.handle_input(source, 0xa3, body).unwrap();
        for event in game.sessions.get_mut(&source).unwrap().take_events() {
            game.handle_event(source, event).unwrap();
        }
    }

    /// Named global channels respect subscribe/unsubscribe and cross sector boundaries.
    #[test]
    fn native_channel_subscription_and_delivery() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        game.sessions.get_mut(&b).unwrap().sector.id = 1060;
        submit(&mut game, b, &request(b, 6, "", "General Chat", ""));
        submit(&mut game, a, &request(a, 0, "", "General Chat", "Hello"));
        let messages = drain(&mut game);
        assert_eq!(messages[&a], vec![event("Alice", "General Chat", "Hello")]);
        assert_eq!(messages[&b], messages[&a]);
        submit(&mut game, b, &request(b, 7, "", "General Chat", ""));
        submit(
            &mut game,
            a,
            &request(a, 0, "", "General Chat", "Not subscribed"),
        );
        let messages = drain(&mut game);
        assert!(messages.contains_key(&a));
        assert!(!messages.contains_key(&b));
    }

    /// Private requests and replies use the authenticated sender, including cross-sector tells.
    #[test]
    fn private_messages_and_reply() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        game.sessions.get_mut(&b).unwrap().sector.id = 1060;
        submit(&mut game, a, &request(a, 1, "bOb", "", "Private hello"));
        let messages = drain(&mut game);
        assert_eq!(
            messages[&b],
            vec![event("Alice", "Private", "Private hello")]
        );
        assert_eq!(messages[&a], messages[&b]);
        game.chat(b, 4, "/reply Hello back").unwrap();
        assert_eq!(
            drain(&mut game)[&a],
            vec![event("Bob", "Private", "Hello back")]
        );
        assert!(game.private_chat(a, "Offline", "hello").is_err());
    }

    /// Sector, local and group requests keep their established visibility limits.
    #[test]
    fn native_local_sector_and_group_routing() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        game.sessions.get_mut(&b).unwrap().position = [100_000., 0., 0.];
        submit(&mut game, a, &request(a, 0, "", "Local", "Nearby"));
        assert!(!drain(&mut game).contains_key(&b));
        submit(&mut game, a, &request(a, 0, "", "Sector", "Sector hello"));
        assert_eq!(
            drain(&mut game)[&b],
            vec![event("Alice", "Sector", "Sector hello")]
        );
        assert!(game.named_chat(a, 0, "", "Group", "Ungrouped").is_err());
        game.groups.invite(a, b).unwrap();
        game.groups.accept(b).unwrap();
        game.sessions.get_mut(&b).unwrap().sector.id = 1060;
        submit(&mut game, a, &request(a, 0, "", "Group", "Crew hello"));
        assert_eq!(
            drain(&mut game)[&b],
            vec![event("Alice", "Group", "Crew hello")]
        );
        submit(&mut game, a, &request(a, 0, "", "Sector", "Sector only"));
        assert!(!drain(&mut game).contains_key(&b));
    }

    /// Spoofed owners, unsupported kinds, oversized fields and truncated packets enqueue nothing.
    #[test]
    fn malformed_native_requests_are_rejected() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        let valid = request(a, 0, "", "General Chat", "Hello");
        for end in 0..valid.len() {
            assert!(game.handle_input(a, 0xa3, &valid[..end]).is_err());
        }
        for body in [
            request(a + 1, 0, "", "General Chat", "Spoof"),
            request(a, 5, "", "General Chat", "Wrong kind"),
            request(a, 0, "", "General Chat", &"x".repeat(1025)),
        ] {
            assert!(game.handle_input(a, 0xa3, &body).is_err());
        }
        assert!(game.sessions.get_mut(&a).unwrap().take_events().is_empty());
    }

    /// Net7Proxy may retain the original 24-bit character ID inside an authenticated chat request.
    #[test]
    fn masked_native_chat_owner_still_routes_as_authenticated_pilot() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        let b = a + 1;
        submit(
            &mut game,
            a,
            &request(a & 0x00ff_ffff, 1, "Bob", "", "Masked sender"),
        );
        assert_eq!(
            drain(&mut game)[&b],
            vec![event("Alice", "Private", "Masked sender")]
        );
        assert!(game
            .handle_input(a, 0xa3, &request(b & 0x00ff_ffff, 1, "Bob", "", "Spoof"))
            .is_err());
    }

    /// Empty native startup requests are parsed safely without publishing chat.
    #[test]
    fn native_startup_requests_do_not_publish_messages() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        for kind in [29, 30] {
            game.handle_input(a, 0xa3, &request(a & 0x00ff_ffff, kind, "", "", "")).unwrap();
        }
        assert!(game.sessions.get_mut(&a).unwrap().take_events().is_empty());
    }

    /// The captured Windows self-tells contain a bounded formatted-text trailer after their plain text.
    #[test]
    fn captured_windows_formatted_messages_are_delivered_once() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        game.sessions.get_mut(&a).unwrap().account.character.name = "NativeProbe".into();
        for (hex, text) in [
            ("0b000000010000000b004e617469766550726f6265000014006e61746976652d636861742d7665726966792d321d000000014915006e61746976652d636861742d7665726966792d320008000000", "native-chat-verify-2"),
            ("0b000000010000000b004e617469766550726f6265000006002f77686572650f000000014807002f77686572650008000000", "/where"),
        ] {
            let mut body: Vec<u8> = hex.as_bytes().chunks_exact(2)
                .map(|pair| u8::from_str_radix(std::str::from_utf8(pair).unwrap(), 16).unwrap()).collect();
            body[..4].copy_from_slice(&(a & 0x00ff_ffff).to_le_bytes());
            submit(&mut game, a, &body);
            assert_eq!(drain(&mut game)[&a], vec![event("NativeProbe", "Private", text)]);
            // Truncating the presentation body must not publish an incomplete request.
            body.pop();
            assert!(game.handle_input(a, 0xa3, &body).is_err());
            assert!(game.sessions.get_mut(&a).unwrap().take_events().is_empty());
        }
    }

    /// Presentation metadata remains bounded and cannot replace authenticated chat fields.
    #[test]
    fn malformed_formatted_chat_trailers_are_rejected() {
        let mut game = pair();
        let a = FIRST_PLAYER_ID;
        for (declared, actual) in [(2u32, 1usize), (1, 2), (2049, 2049)] {
            let mut body = request(a, 1, "Bob", "", "Safe text");
            let tail = body.len() - 4;
            body[tail..].copy_from_slice(&declared.to_le_bytes());
            body.extend(vec![0; actual]);
            assert!(game.handle_input(a, 0xa3, &body).is_err());
        }
        assert!(game.sessions.get_mut(&a).unwrap().take_events().is_empty());
    }

    /// Both implementations consume the exact archived length-string event format.
    #[test]
    fn native_event_matches_client_fixture() {
        let expected = include_str!("../../client/Tests/Fixtures/native_chat_event.hex").trim();
        let actual = event("Alice", "General Chat", "Hello")
            .body
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect::<String>();
        assert_eq!(actual, expected);
    }
}
