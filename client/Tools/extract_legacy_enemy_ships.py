"""Extract original enemy models and their dependencies without changing the client install."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path
import zipfile

from extract_legacy_effect_assets import (
    TEXTURE_NAME, archive_index, asset_names, read_ini,
    read_payload, source_entry,
)

ASSET_STEM = re.compile(rb"[A-Za-z][A-Za-z0-9_-]{4,63}")


def enemy_catalog(path: Path) -> list[dict[str, object]]:
    """Returns:
        list[dict[str, object]]: Distinct native enemy IDs and names from the universe or a prepared catalog.
    """
    records = json.loads(path.read_text(encoding="utf-8"))
    if not records or "mobs" not in records[0]:
        return records
    names: dict[int, set[str]] = {}
    for sector in records:
        for mob in sector["mobs"]:
            names.setdefault(mob["asset"], set()).add(mob["name"])
    return [{"asset": asset, "mobs": sorted(labels)} for asset, labels in sorted(names.items())]


def extract(catalog: Path, installation: Path, output: Path) -> None:
    """Write exact W3D/DDS bytes with per-model mappings and provenance.

    Raises:
        ValueError: The original archive index or base asset definition is malformed.
    """
    roots = enemy_catalog(catalog)
    ini = installation / "Data/client/ini"
    bases, states = read_ini(ini / "basset.ini"), read_ini(ini / "rs.ini")
    index = archive_index(installation / "Data/client/mixfiles")
    found = {}
    mappings = []
    missing = set()
    for root in roots:
        asset = root["asset"]
        pending = asset_names({asset: {"baseassetid": str(asset)}}, bases, states)
        dependencies = set()
        while pending:
            name = pending.pop()
            entry = source_entry(name, index)
            if entry is None:
                missing.add(name)
                continue
            original = entry[1].lower()
            if original in dependencies:
                continue
            dependencies.add(original)
            if original not in found:
                found[original] = (entry, read_payload(entry))
            data = found[original][1]
            if original.endswith(".w3d"):
                for stem in TEXTURE_NAME.findall(data):
                    texture = stem.decode("ascii").lower() + ".dds"
                    if texture not in dependencies:
                        pending.add(texture)
                for stem in ASSET_STEM.findall(data):
                    model = stem.decode("ascii").lower() + ".w3d"
                    if model in index and model not in dependencies:
                        pending.add(model)
        mappings.append({**root, "definition": bases.get(f"base-{asset}"),
                         "dependencies": sorted(dependencies)})
    output.parent.mkdir(parents=True, exist_ok=True)
    files = []
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
        for name in ("basset.ini", "rs.ini"):
            bundle.writestr("definitions/" + name, (ini / name).read_bytes())
        for name, (entry, data) in sorted(found.items()):
            archive, original, offset, size = entry
            path = ("models/" if name.endswith(".w3d") else "textures/") + original
            bundle.writestr(path, data)
            files.append({"path": path, "archive": archive.name, "offset": offset,
                          "size": size, "sha256": hashlib.sha256(data).hexdigest()})
        manifest = {"installation": str(installation), "assets": mappings,
                    "files": files, "missing": sorted(missing)}
        bundle.writestr("manifest.json", json.dumps(manifest, indent=2) + "\n")
    print(f"Extracted {len(mappings)} enemy asset mappings and {len(files)} files to {output}")
    print("Missing references: " + ", ".join(sorted(missing)))


def main() -> None:
    """Read explicit installation and output paths for the extraction."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True, type=Path)
    parser.add_argument("--installation", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    extract(args.catalog, args.installation, args.output)


if __name__ == "__main__":
    main()
