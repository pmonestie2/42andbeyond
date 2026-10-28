#!/usr/bin/env python3
"""Build the client's public known-gate catalog offline; no running server is contacted."""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import struct

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "client/Assets/FortyTwoAndBeyond/Resources/GalaxyNavigation.bytes"


def text(value: str) -> bytes:
    """Returns:
        bytes: One bounded UTF-8 label with a little-endian u16 length.

    Raises:
        ValueError: The label exceeds the client catalog's bound.
    """
    encoded = value.encode("utf-8")
    if len(encoded) > 512:
        raise ValueError("Navigation label exceeds 512 bytes")
    return struct.pack("<H", len(encoded)) + encoded


def build() -> bytes:
    """Returns:
        bytes: Deterministic GXY1 public gate/station data assembled from checked-in source catalogs.

    Raises:
        ValueError: A destination has an invalid identity, position, radius, or duplicate key.
    """
    source = ROOT / "server/data"
    sectors = {sector["id"]: sector for sector in json.loads((source / "universe.json").read_text())}
    alpha = json.loads((source / "asteroid_belt_alpha.json").read_text())
    sectors[alpha["id"]] = alpha
    stations = {station["id"]: station for station in json.loads((source / "stations.json").read_text())}
    rows = []
    identities = set()
    for sector in sectors.values():
        for nav in sector["navpoints"]:
            if nav["object_type"] not in (11, 12):
                continue
            identity = (sector["id"], nav["id"])
            position = nav["position"]
            radius = nav.get("radius", 0.0)
            if (not all(identity) or identity in identities or len(position) != 3
                    or not all(math.isfinite(value) and abs(value) < 1e9 for value in position)
                    or not math.isfinite(radius) or not 0 <= radius <= 1e7):
                raise ValueError(f"Invalid navigation entry: {identity}")
            identities.add(identity)
            destination = nav.get("gate_destination") or 0
            if nav["object_type"] == 12:
                destination = destination if destination in stations else 0
            else:
                destination = destination if destination in sectors or destination in stations else 0
            name = stations[destination]["name"] if nav["object_type"] == 12 and destination else nav["name"]
            rows.append((sector["system"], sector["name"], name.strip(), sector["id"], nav["id"],
                         nav["object_type"], destination, position, radius))
    rows.sort(key=lambda row: row[:5])
    if len(rows) > 10000:
        raise ValueError("Navigation catalog exceeds 10000 entries")
    result = bytearray(b"GXY1" + struct.pack("<I", len(rows)))
    for system, sector_name, name, sector, identity, kind, destination, position, radius in rows:
        result.extend(struct.pack("<IIIIffff", sector, identity, kind, destination, *position, radius))
        result.extend(text(name) + text(sector_name) + text(system))
    return bytes(result)


def main() -> None:
    """Generate the local resource or verify it is current without writing files."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    options = parser.parse_args()
    data = build()
    if options.check:
        if not OUTPUT.is_file() or OUTPUT.read_bytes() != data:
            raise SystemExit("Navigation resource is stale; run client/Tools/build_navigation_catalog.py")
    else:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        OUTPUT.write_bytes(data)
    print(f"PASS: client-only known navigation resource: {struct.unpack_from('<I', data, 4)[0]} entries, {len(data)} bytes")


if __name__ == "__main__":
    main()
