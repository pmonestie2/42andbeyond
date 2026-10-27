"""Extract native item icons from a locally copied original client installation.

The source MIX archives and basset.ini are read only. PNGs are named by the
server's icon ID so Unity can load the exact image advertised by each item.
"""

from __future__ import annotations

import argparse
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import sys
from zipfile import ZIP_DEFLATED, ZipFile

from PIL import Image

from extract_legacy_effect_assets import archive_entries, read_ini


def archive_index(directory: Path) -> dict[str, tuple[Path, int, int]]:
    """Returns:
        dict[str, tuple[Path, int, int]]: Source archive, offset and length by asset name.
    """
    result: dict[str, tuple[Path, int, int]] = {}
    for archive in sorted(directory.glob("*.mix")):
        for name, offset, length in archive_entries(archive):
            result[name.lower()] = (archive, offset, length)
    return result


def source_icon_name(icon_id: int, sections: dict[str, dict[str, str]]) -> str:
    """Returns:
        str: Original texture filename assigned to this base asset ID.

    Raises:
        ValueError: The original base asset has no icon texture.
    """
    name = sections.get(f"base-{icon_id}", {}).get("icon", "")
    if not name:
        raise ValueError(f"BASE-{icon_id} has no native Icon= texture")
    return name.lower()


def extract(items: Path, basset: Path, mixes: Path, output: Path,
            source_zip: Path) -> dict[str, object]:
    """Returns:
        dict[str, object]: Provenance and missing native assets for the generated icon set.

    Raises:
        ValueError: An advertised icon lacks its original base asset definition.
    """
    catalog = json.loads(items.read_text(encoding="utf-8"))
    icon_ids = sorted({int(item["icon"]) for item in catalog if item.get("icon") is not None})
    sections = read_ini(basset)
    index = archive_index(mixes)
    output.mkdir(parents=True, exist_ok=True)
    source_zip.parent.mkdir(parents=True, exist_ok=True)
    entries: list[dict[str, object]] = []
    missing: list[dict[str, object]] = []
    raw_assets: dict[str, bytes] = {}
    for icon_id in icon_ids:
        named = source_icon_name(icon_id, sections)
        found_name = named if named in index else named.rsplit(".", 1)[0] + ".dds"
        if found_name not in index:
            missing.append({"icon_id": icon_id, "texture": named})
            continue
        archive, offset, length = index[found_name]
        with archive.open("rb") as stream:
            stream.seek(offset)
            payload = stream.read(length)
        if len(payload) != length:
            raise ValueError(f"Truncated {found_name} in {archive}")
        with Image.open(BytesIO(payload)) as source:
            image = source.convert("RGBA")
            dimensions = [image.width, image.height]
            image.save(output / f"{icon_id}.png", optimize=True)
        raw_assets[found_name] = payload
        entries.append({"icon_id": icon_id, "texture": found_name,
                        "archive": archive.name.removeprefix("42_"),
                        "offset": offset, "length": length,
                        "dimensions": dimensions, "sha256": sha256(payload).hexdigest()})
    manifest: dict[str, object] = {
        "origin": "Original Windows Earth & Beyond Data/client/mixfiles and Data/client/ini/basset.ini",
        "basset_sha256": sha256(basset.read_bytes()).hexdigest(),
        "item_count": len(icon_ids), "extracted_count": len(entries),
        "missing": missing, "icons": entries,
    }
    with ZipFile(source_zip, "w", compression=ZIP_DEFLATED) as bundle:
        bundle.writestr("basset.ini", basset.read_bytes())
        bundle.writestr("manifest.json", json.dumps(manifest, indent=2) + "\n")
        for name, data in sorted(raw_assets.items()):
            bundle.writestr("dds/" + name, data)
    (source_zip.parent / "manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def main() -> int:
    """Returns:
        int: Zero after extraction, or one if any advertised original icon is missing.
    """
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--items", type=Path, required=True)
    parser.add_argument("--basset", type=Path, required=True)
    parser.add_argument("--mix-dir", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-zip", type=Path, required=True)
    args = parser.parse_args()
    manifest = extract(args.items, args.basset, args.mix_dir, args.output, args.source_zip)
    print(f"Extracted {manifest['extracted_count']}/{manifest['item_count']} native item icons")
    for entry in manifest["missing"]:
        print(f"Missing BASE-{entry['icon_id']}: {entry['texture']}", file=sys.stderr)
    return 1 if manifest["missing"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
