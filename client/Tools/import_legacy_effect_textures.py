"""Convert pinned original Earth & Beyond effect textures for Unity.

The archived DDS files remain byte-for-byte unchanged. This converts textures
from the ship archive and the owner's original skill-effect MIX archives.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile

from PIL import Image


PROJECT = Path(__file__).resolve().parents[2]
SOURCE = PROJECT / "legacy_assets"
DESTINATION = PROJECT / "client/Assets/FortyTwoAndBeyond/Resources/LegacyEffects"
EFFECT_TEXTURES = ("GLOW_1", "EXHAST3", "FLRANIM", "LIGHT", "FLICKER", "PGEN0012")
BLACK_KEYED = frozenset({"GLOW_1", "EXHAST3", "FLRANIM", "LIGHT", "FLICKER",
                         "shld_env01", "shld_tun01", "sk_psysh05",
                         "sk_psysh06", "sk_hack02", "sk_menace01", "sk_menace02",
                         "FANTA01", "shldtype07", "shld_inv01", "shld_inv06",
                         "sk_fold01", "tsg0206", "aline", "SH_RIPP", "SH_PART"})


def source_records() -> dict[str, tuple[Path, str]]:
    """Returns:
        dict[str, tuple[Path, str]]: Source paths and pinned hashes by lowercase filename.
    """
    records: dict[str, tuple[Path, str]] = {}
    for manifest_path in (SOURCE / "manifest.json", SOURCE / "effects/manifest.json",
                          SOURCE / "effects/other/manifest.json",
                          SOURCE / "effects/defensive/manifest.json"):
        manifest = json.loads(manifest_path.read_text())
        for record in manifest["files"]:
            path = manifest_path.parent / record["path"]
            records[path.name.lower()] = (path, record["sha256"])
    return records


def verify_source(name: str, records: dict[str, tuple[Path, str]]) -> Path:
    """Returns:
        Path: Verified original DDS path.

    Raises:
        ValueError: The source is missing or differs from its pinned archive bytes.
    """
    entry = records.get((name + ".dds").lower())
    if entry is None:
        raise ValueError(f"No pinned source record for {name}.dds")
    path, expected = entry
    if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
        raise ValueError(f"Original effect texture provenance failed: {path}")
    return path


def decoded_image(name: str, source: Path) -> Image.Image:
    """Decode DDS and turn black sprite backgrounds into transparent pixels.

    Returns:
        Image.Image: RGBA effect sprite ready for Unity.

    Raises:
        ValueError: The DDS is not a square, nonempty effect texture.
    """
    with Image.open(source) as original:
        if original.width != original.height or original.width < 16:
            raise ValueError(f"Unexpected original effect texture dimensions: {source}")
        image = original.convert("RGBA")
    if name == "SH_RIPP":
        pixels = [(*pixel[:3], min(255, max(0, (max(pixel[:3]) - 80) * 3)))
                  for pixel in image.getdata()]
        image.putdata(pixels)
    elif name in BLACK_KEYED or name.lower().startswith("sk_rally"):
        pixels = [(*pixel[:3], max(pixel[:3])) for pixel in image.getdata()]
        image.putdata(pixels)
    return image


def convert(name: str, source: Path, destination: Path) -> None:
    """Write the decoded original effect sprite as a Unity-readable PNG."""
    decoded_image(name, source).save(destination, format="PNG", optimize=True)


def verify_output(directory: Path, records: dict[str, tuple[Path, str]],
                  names: tuple[str, ...]) -> None:
    """Compare every converted PNG pixel to the pinned original DDS.

    Raises:
        ValueError: A converted texture is absent or has altered pixels.
    """
    for name in names:
        source = verify_source(name, records)
        output = directory / (name + ".png")
        if not output.is_file():
            raise ValueError(f"Missing converted effect texture: {output}")
        expected = decoded_image(name, source)
        with Image.open(output) as converted:
            if expected.tobytes() != converted.convert("RGBA").tobytes() or expected.size != converted.size:
                raise ValueError(f"Converted effect texture differs from DDS: {output}")


def main() -> None:
    """Publish original effect sprites after provenance and pixel checks."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check-only", action="store_true")
    options = parser.parse_args()
    records = source_records()
    names = EFFECT_TEXTURES + tuple(sorted(path.stem for path in
        (SOURCE / "effects/textures").glob("sk_rally*.dds"))) + (
            "sk_comtra02", "shld_env01", "shld_tun01", "shld_reg02",
            "GRAD", "sk_psysh05", "sk_psysh06", "sk_hack02",
            "sk_menace01", "sk_menace02", "FANTA01", "shld_reg04",
            "shldtype07", "JVE0108", "shld_inv01", "shld_inv06",
            "sk_sdest01", "sk_fold01", "tsg0206", "aline",
            "SH_RIPP", "SH_PART")
    if not options.check_only:
        DESTINATION.mkdir(parents=True, exist_ok=True)
        for name in names:
            source = verify_source(name, records)
            with tempfile.NamedTemporaryFile(dir=DESTINATION, suffix=".png",
                                             delete=False) as temporary:
                staging = Path(temporary.name)
            try:
                convert(name, source, staging)
                with Image.open(staging) as image:
                    if image.size[0] < 16:
                        raise ValueError(f"Empty conversion: {name}")
                target = DESTINATION / (name + ".png")
                if not target.is_file() or staging.read_bytes() != target.read_bytes():
                    staging.chmod(0o644)
                    os.replace(staging, target)
            finally:
                staging.unlink(missing_ok=True)
    verify_output(DESTINATION, records, names)
    print(f"Verified {len(names)} original effect textures in {DESTINATION}")


if __name__ == "__main__":
    main()
