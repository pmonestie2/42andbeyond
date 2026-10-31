"""Check actual game-facing bootstrap delivery without opening a live client."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def read_frames(path: Path) -> list[dict]:
    """Read a gameplay-only JSONL capture.

    Returns:
        list[dict]: Ordered captured frames.
    """
    return [json.loads(line) for line in path.read_text().splitlines() if line.strip()]


def check(folder: Path) -> dict:
    """Require the initial player AUX before START and compare optional UDP evidence.

    Returns:
        dict: Delivery sizes, hashes, and whether optional UDP frames match.

    Raises:
        ValueError: The captured bootstrap lacks START or the initial player AUX.
    """
    tcp = read_frames(folder / 'tcp-delivered-frames.jsonl')
    starts = [i for i, frame in enumerate(tcp) if frame['opcode'] == 5]
    if not starts:
        raise ValueError('No START reached the game-facing TCP capture')
    before = tcp[:starts[0]]
    player = [
        frame for frame in before
        if frame['opcode'] == 0x1b and frame['body_hex'].startswith('00000000')
    ]
    if not player:
        raise ValueError('Initial player AUX0 did not reach TCP before START')
    initial = player[0]
    body = bytes.fromhex(initial['body_hex'])
    if len(body) < 12 or body[6] != 1 or not body[7] & 4:
        raise ValueError('Initial player AUX is not a full two-bit snapshot')
    # PlayerIndex has 18 fields; RPGInfo is field 6 (presence bit 10).
    if not int.from_bytes(body[7:12], 'little') & (1 << 10):
        raise ValueError('Initial player AUX snapshot does not include RPGInfo')
    created = next((frame for frame in before if frame['opcode'] == 4), None)
    own_id = created['body_hex'][:8] if created is not None else None
    ship = [
        frame for frame in before
        if own_id is not None and frame['opcode'] == 0x1b
        and frame['body_hex'].startswith(own_id)
    ]
    result = {
        'initial_player_aux_size': initial['size'],
        'initial_player_aux_sha256': hashlib.sha256(body).hexdigest(),
        'ship_aux_sizes': [frame['size'] for frame in ship],
        'tcp_frames': len(tcp),
        'initial_aux_before_start': True,
        'initial_aux_matches_udp': None,
    }
    if (folder / 'frames.jsonl').exists():
        udp = read_frames(folder / 'frames.jsonl')
        result['initial_aux_matches_udp'] = any(
            frame['opcode'] == 0x1b and frame['body_hex'] == initial['body_hex'] for frame in udp
        )
        if not result['initial_aux_matches_udp']:
            raise ValueError('Delivered initial player AUX differs from optional UDP capture')
    return result


def main() -> None:
    """Print capture evidence; process survival/UI checks remain separate."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('folder', type=Path)
    args = parser.parse_args()
    try:
        print(json.dumps(check(args.folder), indent=2))
    except (OSError, ValueError, KeyError) as error:
        parser.exit(1, f'Native bootstrap check failed: {error}\n')


if __name__ == '__main__':
    main()
