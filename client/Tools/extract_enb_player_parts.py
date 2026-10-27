"""Extract the original player ship modules from the owner's MIX archives.

The source archives are read only. Outputs include a byte-level provenance manifest.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct


PROJECT = Path(__file__).resolve().parents[2]
OUTPUT = PROJECT / "legacy_assets/player_parts"
PARTS = {
    f"s{race}{piece}{style}3.w3d"
    for race in "tjp" for piece in "hpw" for style in "123"
} | {f"s{race}e01{variant}.w3d" for race in "tjp" for variant in ("", "a")} | {
    "ship_all.dds", "namedecal.dds", "lights_all.dds",
    "sjh01.dds", "sjh02.dds", "sjh04.dds",
    "spp23-1.w3d", "spp23-2.w3d",
    "sjp33-1.w3d", "sjp33-2.w3d",
    "sjw33-1.w3d", "sjw33-2.w3d",
}


def entries(archive: Path) -> list[tuple[str, int, int]]:
    """Returns:
        list[tuple[str, int, int]]: Original name, byte offset, and byte size.

    Raises:
        ValueError: The source archive index is malformed.
    """
    with archive.open("rb") as stream:
        magic, record_offset, name_offset, _ = struct.unpack("<4sIII", stream.read(16))
        if magic != b"MIX1":
            raise ValueError(f"Unsupported MIX archive: {archive}")
        stream.seek(record_offset)
        count = struct.unpack("<I", stream.read(4))[0]
        if count > 100000:
            raise ValueError(f"Invalid MIX entry count: {archive}")
        records = [struct.unpack("<III", stream.read(12)) for _ in range(count)]
        stream.seek(name_offset)
        if struct.unpack("<I", stream.read(4))[0] != count:
            raise ValueError(f"Mismatched MIX name table: {archive}")
        result = []
        for record in records:
            length = stream.read(1)[0]
            name = stream.read(length).rstrip(b"\0").decode("ascii")
            result.append((name, record[1], record[2]))
        return result


def extract(archives: list[Path]) -> None:
    """Copy the selected source bytes and record their archive locations.

    Raises:
        ValueError: A required original component is missing.
    """
    found: dict[str, dict[str, str | int]] = {}
    for archive in archives:
        with archive.open("rb") as stream:
            for name, offset, size in entries(archive):
                key = name.lower()
                if key not in PARTS:
                    continue
                stream.seek(offset)
                content = stream.read(size)
                if len(content) != size:
                    raise ValueError(f"Truncated source part: {name}")
                category = "models" if key.endswith(".w3d") else "textures"
                destination = OUTPUT / category / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(content)
                found[key] = {"path": str(destination.relative_to(OUTPUT)),
                              "archive": archive.name, "offset": offset, "size": size,
                              "sha256": hashlib.sha256(content).hexdigest()}
    missing = PARTS - found.keys()
    if missing:
        raise ValueError("Missing original player parts: " + ", ".join(sorted(missing)))
    (OUTPUT / "manifest.json").write_text(
        json.dumps({"format": "Original Earth & Beyond MIX1 player ship parts",
                    "files": [found[key] for key in sorted(found)]}, indent=2) + "\n")
    print(f"Extracted {len(found)} original ship files to {OUTPUT}")


def main() -> None:
    """Read archive paths from the command line and extract the required parts."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archives", nargs="+", type=Path)
    args = parser.parse_args()
    extract(args.archives)


if __name__ == "__main__":
    main()
