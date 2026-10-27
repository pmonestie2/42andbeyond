"""Extract native skill effect dependencies from an original ENB installation.

Run on the machine with ``Data/client/ini`` and ``Data/client/mixfiles``.
The installation is read only. A ZIP contains exact source bytes plus offsets
and hashes; effect and base asset links are resolved from the original INIs.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import zipfile


SECTION = re.compile(r"^\[([^]]+)\]")
TEXTURE_NAME = re.compile(rb"([A-Za-z0-9_\-]{3,64})\.(?:tga|dds)", re.I)
ASSET_STEM = re.compile(rb"[A-Za-z][A-Za-z0-9_]{4,63}")


def read_ini(path: Path) -> dict[str, dict[str, str]]:
    """Returns:
        dict[str, dict[str, str]]: Sections with case-insensitive keys.
    """
    result: dict[str, dict[str, str]] = {}
    current: dict[str, str] | None = None
    for line in path.read_text(encoding="latin1").splitlines():
        match = SECTION.match(line.strip())
        if match:
            current = result.setdefault(match.group(1).lower(), {})
        elif current is not None and "=" in line and not line.lstrip().startswith((";", "#")):
            key, value = line.split("=", 1)
            current[key.strip().lower()] = value.split(";", 1)[0].strip()
    return result


def archive_entries(path: Path) -> list[tuple[str, int, int]]:
    """Returns:
        list[tuple[str, int, int]]: Original names, offsets, and sizes.

    Raises:
        ValueError: The MIX1 index is malformed.
    """
    with path.open("rb") as stream:
        magic, record_offset, name_offset, _ = struct.unpack("<4sIII", stream.read(16))
        if magic != b"MIX1":
            raise ValueError(f"Unsupported archive: {path}")
        stream.seek(record_offset)
        count = struct.unpack("<I", stream.read(4))[0]
        if count > 100000:
            raise ValueError(f"Invalid MIX entry count: {path}")
        records = [struct.unpack("<III", stream.read(12)) for _ in range(count)]
        stream.seek(name_offset)
        if struct.unpack("<I", stream.read(4))[0] != count:
            raise ValueError(f"MIX name count mismatch: {path}")
        result = []
        for _, offset, size in records:
            length = stream.read(1)[0]
            name = stream.read(length).rstrip(b"\0").decode("ascii")
            result.append((name, offset, size))
        return result


def linked_effects(sections: dict[str, dict[str, str]], roots: list[int]) -> dict[int, dict[str, str]]:
    """Returns:
        dict[int, dict[str, str]]: Root effects and their native linked phases.

    Raises:
        ValueError: An effect link points to a missing definition.
    """
    pending = roots[:]
    found: dict[int, dict[str, str]] = {}
    while pending:
        effect_id = pending.pop()
        if effect_id in found:
            continue
        section = sections.get(f"effect-{effect_id}")
        if section is None:
            raise ValueError(f"No original EFFECT-{effect_id}")
        found[effect_id] = section
        for key in ("nextlinkid", "startlinkid"):
            if key in section:
                pending.append(int(section[key]))
    return found


def asset_names(effects: dict[int, dict[str, str]], bases: dict[str, dict[str, str]],
                states: dict[str, dict[str, str]]) -> set[str]:
    """Returns:
        set[str]: W3D and texture names referenced by effect asset records.

    Raises:
        ValueError: A referenced base asset is absent.
    """
    names: set[str] = set()
    for effect in effects.values():
        if "swatch" in effect:
            names.add(effect["swatch"].lower() + ".dds")
        if "baseassetid" not in effect:
            continue
        base_id = effect["baseassetid"]
        base = bases.get(f"base-{base_id}")
        if base is None:
            raise ValueError(f"No original BASE-{base_id}")
        if "file" in base:
            names.add(base["file"].lower())
        if "texture" in base:
            names.add(base["texture"].lower())
        if "rslid" in base:
            prefix = base["rslid"].lower() + "\\state"
            for section_name, state in states.items():
                if not section_name.startswith(prefix):
                    continue
                if state.get("type", "").lower() == "object" and "object" in state:
                    names.add(state["object"].lower() + ".w3d")
                if state.get("type", "").lower() == "anim" and "suffix" in state:
                    origin = states.get(prefix + "0", {}).get("object")
                    if origin:
                        names.add(origin.lower() + state["suffix"].lower() + ".w3d")
    return names


def archive_index(directory: Path) -> dict[str, tuple[Path, str, int, int]]:
    """Returns:
        dict[str, tuple[Path, str, int, int]]: Last archive entry per name.
    """
    result = {}
    for archive in sorted(directory.glob("*.mix")):
        for name, offset, size in archive_entries(archive):
            result[name.lower()] = (archive, name, offset, size)
    return result


def source_entry(name: str, index: dict[str, tuple[Path, str, int, int]]) -> tuple[Path, str, int, int] | None:
    """Returns:
        tuple[Path, str, int, int] | None: Original archive entry, preferring DDS for TGA.
    """
    entry = index.get(name.lower())
    if entry is None and name.lower().endswith(".tga"):
        entry = index.get(name[:-4].lower() + ".dds")
    return entry


def read_payload(entry: tuple[Path, str, int, int]) -> bytes:
    """Returns:
        bytes: Exact archived payload.

    Raises:
        ValueError: The archive has a truncated payload.
    """
    archive, name, offset, size = entry
    with archive.open("rb") as stream:
        stream.seek(offset)
        data = stream.read(size)
    if len(data) != size:
        raise ValueError(f"Truncated archive entry {name} in {archive}")
    return data


def extract(ini_dir: Path, mix_dir: Path, roots: list[int], destination: Path) -> None:
    """Write referenced original definitions, W3D models, and texture bytes.

    Raises:
        ValueError: A required native file is missing from the installation.
    """
    definitions = {name: (ini_dir / name).read_bytes()
                   for name in ("effect.ini", "basset.ini", "rs.ini")}
    effects = linked_effects(read_ini(ini_dir / "effect.ini"), roots)
    names = asset_names(effects, read_ini(ini_dir / "basset.ini"),
                        read_ini(ini_dir / "rs.ini"))
    index = archive_index(mix_dir)
    found: dict[str, tuple[tuple[Path, str, int, int], bytes]] = {}
    pending = set(names)
    missing: set[str] = set()
    while pending:
        name = pending.pop()
        if name.lower() in found:
            continue
        entry = source_entry(name, index)
        if entry is None:
            missing.add(name)
            continue
        payload = read_payload(entry)
        found[entry[1].lower()] = (entry, payload)
        if entry[1].lower().endswith(".w3d"):
            for stem in TEXTURE_NAME.findall(payload):
                texture = stem.decode("ascii").lower() + ".dds"
                if texture not in found and texture in index:
                    pending.add(texture)
            for stem in ASSET_STEM.findall(payload):
                model = stem.decode("ascii").lower() + ".w3d"
                if model not in found and model in index:
                    pending.add(model)
    records = []
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED) as output:
        for name, data in sorted(definitions.items()):
            output.writestr("definitions/" + name, data)
        for name, (entry, data) in sorted(found.items()):
            archive, original, offset, size = entry
            category = "models" if name.endswith(".w3d") else "textures"
            path = f"{category}/{original}"
            output.writestr(path, data)
            records.append({"path": path, "archive": archive.name,
                            "offset": offset, "size": size,
                            "sha256": hashlib.sha256(data).hexdigest()})
        output.writestr("manifest.json", json.dumps({"roots": roots,
            "effects": sorted(effects), "files": records,
            "missing": sorted(missing)}, indent=2) + "\n")
    print(f"Extracted {len(effects)} effect phases, {len(records)} assets to {destination}")
    if missing:
        print("Original installation lacks: " + ", ".join(sorted(missing)))


def main() -> None:
    """Resolve native effect dependencies from the supplied source installation."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ini-dir", required=True, type=Path)
    parser.add_argument("--mix-dir", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--effect-id", required=True, type=int, nargs="+")
    options = parser.parse_args()
    extract(options.ini_dir, options.mix_dir, options.effect_id, options.output)


if __name__ == "__main__":
    main()
