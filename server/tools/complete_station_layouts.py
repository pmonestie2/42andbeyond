"""Host public station exteriors whose archived interior layouts were not exported.

Existing layouts and NPC placements are preserved. Missing interiors receive the
standard service rooms/terminals of the specified template, without inventing NPCs.
Run after importing stations from a newer public dump; real layouts take priority.
"""
from __future__ import annotations

import argparse
from copy import deepcopy
import json
from pathlib import Path


def complete_layouts(stations: list[dict], sectors: list[dict], template_id: int = 10761) -> list[dict]:
    """Returns:
        list[dict]: Existing layouts plus standard service interiors for missing public links.

    Raises:
        ValueError: The template is absent or public station destinations are ambiguous.
    """
    by_id = {row['id']: row for row in stations}
    if len(by_id) != len(stations) or template_id not in by_id:
        raise ValueError('Duplicate station IDs or missing standard-room template')
    template = by_id[template_id]
    result = deepcopy(stations)
    links: dict[int, tuple[int, int]] = {}
    for sector in sectors:
        for exterior in sector.get('navpoints', []):
            destination = exterior.get('gate_destination')
            if exterior.get('object_type') != 12 or not destination:
                continue
            identity = (sector['id'], exterior['id'])
            if destination in links and links[destination] != identity:
                raise ValueError(f'Ambiguous station exterior for {destination}')
            links[destination] = identity
            if destination in by_id:
                continue
            layout = dict(id=destination, parent=sector['id'], exterior=exterior['id'],
                          name=exterior['name'], kind=template['kind'],
                          rooms=deepcopy(template['rooms']), terminals=deepcopy(template['terminals']),
                          npcs=[], layout_source=f'Standard service layout from {template_id}; public exterior link')
            by_id[destination] = layout
            result.append(layout)
    return result


def main() -> None:
    """Complete compiled public content without touching runtime account or world state."""
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stations', type=Path, default=root / 'data/stations.json')
    parser.add_argument('--sectors', type=Path, default=root / 'data/universe.json')
    parser.add_argument('--template', type=int, default=10761)
    args = parser.parse_args()
    stations = json.loads(args.stations.read_text())
    result = complete_layouts(stations, json.loads(args.sectors.read_text()), args.template)
    args.stations.write_text(json.dumps(result, indent=2) + '\n')
    print(f'Added {len(result) - len(stations)} missing station interiors; preserved {len(stations)} layouts')


if __name__ == '__main__':
    main()
