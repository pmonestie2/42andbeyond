//! Bounded reliable UDP output: preserve unacknowledged packets and pace large metadata bursts.
use super::*;
use crate::protocol::{split_frames, MTU_PAYLOAD};

const TRANSMIT_BATCH: usize = 8;
const INFLIGHT_LIMIT: usize = 64;
const QUEUED_LIMIT: usize = 8192;
const ACK_GROUP_BYTES: usize = 16 * MTU_PAYLOAD;

/// Returns:
///     Vec<Datagram>: Native frames and continuations, with custom opcodes isolated from UI messages.
/// Raises:
///     io::Error: A native frame exceeds its protocol length bound.
fn native_packets(player: u32, sequence: &mut u32, frames: &[Frame]) -> io::Result<Vec<Datagram>> {
    let mut packets = Vec::new();
    let mut body = Vec::new();
    for frame in frames {
        let encoded = frame.encode()?;
        if encoded.len() > MTU_PAYLOAD {
            // Net7Proxy 2.46 treats a larger declared frame as the beginning
            // of a 1400-byte fragment stream, even if the UDP packet contains
            // the whole frame. Supply its native 0x201a continuations.
            if !body.is_empty() {
                packets.push(Datagram {
                    opcode: 0x2016,
                    player,
                    sequence: *sequence,
                    body: std::mem::take(&mut body),
                });
                *sequence += 1;
            }
            packets.extend(split_frames(player, sequence, std::slice::from_ref(frame))?);
            continue;
        }
        let isolated = frame.opcode >= 0x1000 || frame.opcode == 5;
        if !body.is_empty() && (isolated || body.len() + encoded.len() > MTU_PAYLOAD) {
            packets.push(Datagram {
                opcode: 0x2016,
                player,
                sequence: *sequence,
                body: std::mem::take(&mut body),
            });
            *sequence += 1;
        }
        body.extend(encoded);
        if isolated || body.len() >= MTU_PAYLOAD {
            packets.push(Datagram {
                opcode: 0x2016,
                player,
                sequence: *sequence,
                body: std::mem::take(&mut body),
            });
            *sequence += 1;
        }
    }
    if !body.is_empty() {
        packets.push(Datagram {
            opcode: 0x2016,
            player,
            sequence: *sequence,
            body,
        });
        *sequence += 1;
    }
    Ok(packets)
}

/// Returns:
///     Vec<Datagram>: Ordered packets with cumulative ACK requests only at complete-frame boundaries.
/// Raises:
///     io::Error: A frame or resulting datagram exceeds native transport limits.
fn acknowledged_packets(
    player: u32,
    sequence: &mut u32,
    frames: &[Frame],
    fragment_enabled: bool,
) -> io::Result<Vec<Datagram>> {
    let mut packets = Vec::new();
    let mut group = Vec::new();
    let mut bytes = 0;
    for (index, frame) in frames.iter().enumerate() {
        bytes += frame.encode()?.len();
        group.push(frame.clone());
        if bytes >= ACK_GROUP_BYTES || index + 1 == frames.len() {
            group.push(Frame::new(0x2020, vec![0; 4]));
            let mut batch = if fragment_enabled {
                split_frames(player, sequence, &group)?
            } else {
                native_packets(player, sequence, &group)?
            };
            // ACK is the group's final complete frame, never inserted inside a
            // fragmented native formula/AUX frame. A group is at most ~40 UDP
            // packets, below the 64-packet window, so its ACK cannot deadlock.
            let last = batch.last_mut().unwrap();
            let end = last.body.len();
            last.body[end - 4..].copy_from_slice(&sequence.to_le_bytes());
            packets.extend(batch);
            group.clear();
            bytes = 0;
        }
    }
    Ok(packets)
}

impl PlayerSession {
    /// Queue complete frames and transmit a bounded prefix without evicting unacknowledged packets.
    /// Raises:
    ///     io::Error: Encoding fails or a client exceeds the bounded unsent backlog.
    pub(super) fn flush(&mut self) -> io::Result<()> {
        let Some(peer) = self.endpoint else {
            self.pending.clear();
            return Ok(());
        };
        if self.awaiting_start_ack {
            if let Some(start) = self.pending.iter().position(|frame| frame.opcode == 5) {
                let mut later = self.pending.split_off(start + 1);
                self.deferred_start_frames.append(&mut later);
            } else {
                self.deferred_start_frames.append(&mut self.pending);
            }
        }
        if !self.pending.is_empty() {
            let mut sequence = self.sequence;
            let packets = if self.aggregate_station || self.ack_capable {
                acknowledged_packets(
                    self.player_id(),
                    &mut sequence,
                    &self.pending,
                    self.aggregate_station,
                )?
            } else {
                native_packets(self.player_id(), &mut sequence, &self.pending)?
            };
            if self.queued_output.len() + packets.len() > QUEUED_LIMIT {
                return Err(invalid(
                    "Reliable output backlog limit exceeded; waiting for client acknowledgement.",
                ));
            }
            self.sequence = sequence;
            self.queued_output.extend(packets);
            self.pending.clear();
        }
        if !self.requested_replay.is_empty() {
            for _ in 0..TRANSMIT_BATCH {
                let Some(packet) = self.requested_replay.pop_front() else {
                    break;
                };
                self.outgoing.push(Outbound {
                    port: SECTOR_PORT,
                    peer,
                    data: packet.encode()?,
                });
            }
            return Ok(());
        }
        let mut transmitted = 0;
        while transmitted < TRANSMIT_BATCH
            && (!self.ack_capable || self.replay.len() < INFLIGHT_LIMIT)
        {
            let Some(packet) = self.queued_output.pop_front() else {
                break;
            };
            self.outgoing.push(Outbound {
                port: SECTOR_PORT,
                peer,
                data: packet.encode()?,
            });
            self.replay.push_back(packet);
            if self.replay.len() > QUEUED_LIMIT {
                return Err(invalid("Native startup replay limit exceeded."));
            }
            if self.replay.len() > 128 && !self.awaiting_start_ack {
                self.replay.pop_front();
            }
            transmitted += 1;
        }
        if transmitted > 0 {
            self.last_handoff_replay = Instant::now();
            self.replay_retry_cursor = None;
        } else {
            self.retry_reliable_output()?;
        }
        Ok(())
    }

    /// Pace byte-identical retransmissions after an idle ACK gap, including terminal handoffs.
    /// Raises:
    ///     io::Error: A cached datagram cannot be encoded.
    fn retry_reliable_output(&mut self) -> io::Result<()> {
        if self.replay.is_empty()
            || self.transition_pending.is_some()
            || !(self.aggregate_station
                || self.ack_capable
                || self.handoff.is_some()
                || self.awaiting_start_ack)
        {
            return Ok(());
        }
        let Some(peer) = self.endpoint else {
            return Ok(());
        };
        if self.replay_retry_cursor.is_none() {
            if self.last_handoff_replay.elapsed() < Duration::from_secs(1) {
                return Ok(());
            }
            // Native proxies treat sequence zero as a destructive stream reset. Only an
            // explicit missing-packet request may replay it; blind retries must not rewind login.
            self.replay_retry_cursor = self
                .replay
                .iter()
                .find(|packet| {
                    !(self.awaiting_start_ack
                        && !self.aggregate_station
                        && !self.ack_capable
                        && packet.sequence == 0)
                })
                .map(|packet| packet.sequence);
            if self.replay_retry_cursor.is_none() {
                return Ok(());
            }
        }
        let cursor = self.replay_retry_cursor.unwrap();
        let mut next = cursor;
        for packet in self
            .replay
            .iter()
            .filter(|packet| packet.sequence >= cursor)
            .take(TRANSMIT_BATCH)
        {
            self.outgoing.push(Outbound {
                port: SECTOR_PORT,
                peer,
                data: packet.encode()?,
            });
            next = packet.sequence + 1;
        }
        if self
            .replay
            .back()
            .is_none_or(|packet| packet.sequence < next)
        {
            self.replay_retry_cursor = None;
            self.last_handoff_replay = Instant::now();
        } else {
            self.replay_retry_cursor = Some(next);
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    /// Match the installed proxy's 1400-byte implicit native fragment contract.
    #[test]
    fn native_large_aux_uses_continuations_before_following_frames() {
        let aux = Frame::new(0x1b, vec![7; 2357]);
        let extension = Frame::new(0x2030, vec![1; 8]);
        let mut sequence = 0;
        let output = native_packets(42, &mut sequence, &[aux.clone(), extension.clone()]).unwrap();
        assert_eq!(output.len(), 3);
        assert_eq!((output[0].opcode, output[1].opcode), (0x2016, 0x201a));
        assert_eq!(output[0].body.len(), MTU_PAYLOAD);
        assert_eq!(
            [output[0].body.as_slice(), output[1].body.as_slice()].concat(),
            aux.encode().unwrap()
        );
        assert_eq!(output[2].body, extension.encode().unwrap());
        assert_eq!(
            output.iter().map(|packet| packet.sequence).collect::<Vec<_>>(),
            [0, 1, 2]
        );
        assert!(output.iter().all(|packet| packet.body.len() <= MTU_PAYLOAD));
    }

    use super::*;
    use std::collections::BTreeMap;

    /// A legacy startup repairs lost early packets without ACK extensions, retaining bootstraps above 128 packets.
    #[test]
    fn reliable_transport_legacy_startup_retries_and_preserves_the_complete_bootstrap() {
        let mut ship = super::super::tests::journal_ship();
        ship.awaiting_start_ack = true;
        ship.ack_capable = false;
        ship.aggregate_station = false;
        ship.pending = (0..180)
            .map(|index| Frame::new(0x2018, vec![index as u8; 40]))
            .collect();
        ship.pending.push(Frame::new(5, vec![2, 0, 0, 0]));
        let mut original = Vec::new();
        for _ in 0..30 {
            ship.flush().unwrap();
            original.extend(ship.take_outgoing());
            if ship.queued_output.is_empty() {
                break;
            }
        }
        assert_eq!(original.len(), 181);
        assert_eq!(ship.replay.len(), 181);
        ship.checkpoint_session(true).unwrap();
        let restored =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert!(restored.awaiting_start_ack);
        assert_eq!(restored.replay.len(), 181);
        let first = original[1].data.clone();
        ship.last_handoff_replay = Instant::now() - Duration::from_secs(2);
        ship.flush().unwrap();
        let retried = ship.take_outgoing();
        assert_eq!(retried.len(), TRANSMIT_BATCH);
        assert_eq!(retried[0].data, first);
        assert!(retried
            .iter()
            .all(|packet| Datagram::decode(&packet.data).unwrap().sequence != 0));
        ship.handle_input(6, &[]).unwrap();
        assert!(!ship.awaiting_start_ack);
        assert_eq!(ship.replay.len(), 128);
        ship.pending.clear();
        ship.last_handoff_replay = Instant::now() - Duration::from_secs(2);
        ship.flush().unwrap();
        assert!(ship.take_outgoing().is_empty());
        std::fs::remove_dir_all(&ship.state_dir).unwrap();
    }

    /// Large native fragments retain complete-frame ACK boundaries and each ACK group fits inside the send window.
    #[test]
    fn reliable_transport_ack_groups_preserve_maximum_native_frames() {
        let frames: Vec<_> = (0..500)
            .map(|index| Frame::new(0x25, vec![index as u8; 1200]))
            .chain([Frame::new(0x1b, vec![77; crate::protocol::MAX_FRAME - 4])])
            .collect();
        let mut sequence = 0;
        let packets = acknowledged_packets(7, &mut sequence, &frames, true).unwrap();
        assert!(packets.len() > 255);
        let mut pending = Vec::new();
        let mut delivered = Vec::new();
        let mut previous_ack = 0;
        for packet in packets {
            pending.extend(packet.body);
            while pending.len() >= 4 {
                let size = u16::from_le_bytes(pending[..2].try_into().unwrap()) as usize;
                if pending.len() < size {
                    break;
                }
                let frame = Frame::decode(&pending[..size]).unwrap();
                pending.drain(..size);
                if frame.opcode == 0x2020 {
                    let ack = u32::from_le_bytes(frame.body.try_into().unwrap());
                    assert_eq!(ack, packet.sequence + 1);
                    assert!(ack - previous_ack < INFLIGHT_LIMIT as u32);
                    previous_ack = ack;
                } else {
                    delivered.push(frame);
                }
            }
        }
        assert!(pending.is_empty());
        assert_eq!(delivered, frames);
        assert_eq!(previous_ack, sequence);
    }

    /// A stream larger than the replay cache survives loss, reordering and restart without dropping its unsent suffix.
    #[test]
    fn reliable_transport_large_burst_survives_backpressure_loss_and_restart() {
        let mut ship = super::super::tests::journal_ship();
        ship.logged_in = true;
        ship.aggregate_station = true;
        ship.ack_capable = true;
        let frames: Vec<_> = (0..500)
            .map(|index| Frame::new(0x25, vec![index as u8; 1200]))
            .collect();
        ship.pending.extend(frames.clone());
        let mut waiting = BTreeMap::new();
        for _ in 0..8 {
            ship.flush().unwrap();
            let output = ship.take_outgoing();
            assert!(output.len() <= TRANSMIT_BATCH);
            for packet in output {
                let packet = Datagram::decode(&packet.data).unwrap();
                if packet.sequence != 3 {
                    waiting.insert(packet.sequence, packet.body);
                }
            }
        }
        assert_eq!(ship.replay.len(), INFLIGHT_LIMIT);
        assert!(ship.queued_output.len() > 128);
        assert_eq!(ship.replay.front().unwrap().sequence, 0);
        ship.flush().unwrap();
        assert!(ship.take_outgoing().is_empty());
        ship.checkpoint_session(true).unwrap();
        let queued = ship.queued_output.clone();
        let replay = ship.replay.clone();
        let next = ship.sequence;
        let mut resumed =
            PlayerSession::new(ship.sector.clone(), ship.bind, ship.state_dir.clone()).unwrap();
        assert_eq!(resumed.queued_output, queued);
        assert_eq!(resumed.replay, replay);
        assert_eq!(resumed.sequence, next);
        resumed.pending.clear(); // This fixture examines the original stream, before restart reconciliation updates.
                                 // Even an oversized client gap request is bounded and repairs the retained earliest packet.
        resumed
            .handle_input(0x2017, &[0, u32::MAX].map(u32::to_le_bytes).concat())
            .unwrap();
        while !resumed.requested_replay.is_empty() {
            resumed.flush().unwrap();
            let output = resumed.take_outgoing();
            assert!(output.len() <= TRANSMIT_BATCH);
            for output in output {
                let packet = Datagram::decode(&output.data).unwrap();
                waiting.insert(packet.sequence, packet.body);
            }
        }
        let mut expected = 0;
        let mut pending = Vec::new();
        let mut delivered = Vec::new();
        for _ in 0..200 {
            while let Some(body) = waiting.remove(&expected) {
                expected += 1;
                pending.extend(body);
                while pending.len() >= 4 {
                    let size = u16::from_le_bytes(pending[..2].try_into().unwrap()) as usize;
                    if pending.len() < size {
                        break;
                    }
                    let frame = Frame::decode(&pending[..size]).unwrap();
                    pending.drain(..size);
                    if frame.opcode == 0x2020 {
                        resumed.handle_input(0x2021, &frame.body).unwrap();
                    } else {
                        delivered.push(frame);
                    }
                }
            }
            if resumed.queued_output.is_empty() && resumed.replay.is_empty() {
                break;
            }
            resumed.flush().unwrap();
            let output = resumed.take_outgoing();
            assert!(output.len() <= TRANSMIT_BATCH);
            assert!(resumed.replay.len() <= INFLIGHT_LIMIT);
            for output in output.into_iter().rev() {
                let packet = Datagram::decode(&output.data).unwrap();
                waiting.insert(packet.sequence, packet.body);
            }
        }
        assert_eq!(delivered, frames);
        assert!(pending.is_empty());
        assert!(resumed.queued_output.is_empty() && resumed.replay.is_empty());
    }
}

#[cfg(test)]
mod native_tests {
    use super::*;

    /// Space and station output progress when the client echoes only server challenges.
    #[test]
    fn reliable_transport_challenge_only_ack_survives_space_station_roundtrip() {
        let mut ship = super::super::tests::journal_ship();
        ship.logged_in = true;
        ship.pending = (0..300)
            .map(|index| Frame::new(0x1d, vec![index as u8; 1200]))
            .collect();
        let mut space_packets = 0;
        while !ship.queued_output.is_empty() || !ship.pending.is_empty() {
            ship.flush().unwrap();
            for output in ship.take_outgoing() {
                let packet = Datagram::decode(&output.data).unwrap();
                assert!(!packet
                    .body
                    .windows(4)
                    .any(|bytes| bytes == [8, 0, 0x20, 0x20]));
                space_packets += 1;
            }
        }
        assert!(space_packets > INFLIGHT_LIMIT);
        assert!(!ship.ack_capable);
        assert!(ship.replay.len() <= 128);

        ship.aggregate_station = true;
        ship.pending = (0..200)
            .map(|index| Frame::new(0x1d, vec![index as u8; 1200]))
            .collect();
        let mut challenges = 0;
        for _ in 0..100 {
            ship.flush().unwrap();
            for output in ship.take_outgoing() {
                let packet = Datagram::decode(&output.data).unwrap();
                let mut body = packet.body.as_slice();
                while !body.is_empty() {
                    let size = u16::from_le_bytes(body[..2].try_into().unwrap()) as usize;
                    let frame = Frame::decode(&body[..size]).unwrap();
                    body = &body[size..];
                    if frame.opcode == 0x2020 {
                        ship.handle_input(0x2021, &frame.body).unwrap();
                        challenges += 1;
                    }
                }
            }
            if ship.queued_output.is_empty() && ship.replay.is_empty() {
                break;
            }
        }
        assert!(challenges > 1);
        assert!(ship.ack_capable);
        assert!(ship.queued_output.is_empty() && ship.replay.is_empty());

        ship.aggregate_station = false;
        ship.pending.push(Frame::new(0x1d, vec![7; 1200]));
        ship.flush().unwrap();
        let mut saw_return_challenge = false;
        for output in ship.take_outgoing() {
            let packet = Datagram::decode(&output.data).unwrap();
            let mut body = packet.body.as_slice();
            while !body.is_empty() {
                let size = u16::from_le_bytes(body[..2].try_into().unwrap()) as usize;
                let frame = Frame::decode(&body[..size]).unwrap();
                body = &body[size..];
                if frame.opcode == 0x2020 {
                    ship.handle_input(0x2021, &frame.body).unwrap();
                    saw_return_challenge = true;
                }
            }
        }
        assert!(saw_return_challenge);
        assert!(ship.replay.is_empty());
        std::fs::remove_dir_all(&ship.state_dir).unwrap();
    }

    /// Native TE manufacturing binds the real lab and delivers all 500 formula slots without requiring ACK extensions.
    #[test]
    fn reliable_transport_native_te_formulas_deliver_without_ack_support() {
        let mut ship = super::super::tests::journal_ship();
        ship.account.character.race = 0;
        ship.account.character.profession = 0;
        ship.skill_catalog =
            crate::skills::SkillCatalog::for_profile(&ship.account.character).unwrap();
        ship.inventory.known_formulas =
            crate::recipe_grants::eligible_formulas(&ship.skill_catalog, &ship.items);
        assert!(ship.inventory.known_formulas.len() > 500);
        ship.sector = ship.sectors.sector(10601).unwrap();
        ship.logged_in = true;
        assert!(!ship.aggregate_station && !ship.ack_capable);
        ship.pending.push(Frame::new(0x2030, vec![1; 20])); // Unsupported legacy extension must not hide the lab that follows.
        ship.initialise_workbench_lab(true).unwrap();
        ship.workbench_input(
            0x79,
            &[ship.player_id().to_be_bytes(), 1u32.to_be_bytes()].concat(),
        )
        .unwrap();
        let products: Vec<_> = ship
            .inventory
            .known_formulas
            .iter()
            .copied()
            .take(500)
            .collect();
        assert_eq!(products.len(), 500);
        let expected_formulas =
            packets::workbench_formula_chunks(ship.workbench_lab_id(), &products, 0, &ship.items);
        assert!(expected_formulas
            .iter()
            .all(|frame| frame.encode().unwrap().len() <= MTU_PAYLOAD));
        let expected = ship.pending.clone();
        assert!(expected.contains(&Frame::new(
            0x7f,
            ship.workbench_lab_id().to_be_bytes().to_vec()
        )));
        assert!(expected.contains(&packets::manufacturing_lab(ship.workbench_lab_id())));
        assert!(expected_formulas
            .iter()
            .all(|frame| expected.contains(frame)));
        let mut bytes = Vec::new();
        let mut count = 0;
        for _ in 0..1000 {
            ship.flush().unwrap();
            let output = ship.take_outgoing();
            assert!(output.len() <= TRANSMIT_BATCH);
            for output in output {
                let packet = Datagram::decode(&output.data).unwrap();
                assert!(matches!(packet.opcode, 0x2016 | 0x201a));
                assert!(
                    packet.body.len() <= MTU_PAYLOAD,
                    "native queued packet too large: {}",
                    packet.body.len()
                );
                bytes.extend(packet.body);
                count += 1;
            }
            assert!(ship.replay.len() <= 128);
            if ship.queued_output.is_empty() {
                break;
            }
        }
        assert!(count > 64);
        assert!(ship.queued_output.is_empty());
        let mut delivered = Vec::new();
        while !bytes.is_empty() {
            let size = u16::from_le_bytes(bytes[..2].try_into().unwrap()) as usize;
            let frame = Frame::decode(&bytes[..size]).unwrap();
            bytes.drain(..size);
            if frame.opcode != 0x2020 {
                delivered.push(frame);
            }
        }
        assert_eq!(delivered, expected);
        assert!(expected_formulas
            .iter()
            .all(|frame| delivered.contains(frame)));
    }
}
