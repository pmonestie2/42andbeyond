"""Regenerate and verify the original ship assets consumed by Unity.

Usage::

    python3 client/Tools/import_original_ship_assets.py \
        --addon-root /path/to/OpenSAGE.BlenderPlugin

Original W3D/DDS files stay untouched. Blender writes to a temporary directory;
only a complete validated conversion is copied into Unity's Resources folder.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

from legacy_ship_manifest import MAX_UPGRADE_SHIPS, source_stems
from play_local import find_editor


PROJECT = Path(__file__).resolve().parents[2]
TOOLS = Path(__file__).resolve().parent
SOURCE = PROJECT / "legacy_assets"
DESTINATION = PROJECT / "client/Assets/FortyTwoAndBeyond/Resources/LegacyShips"
BUILD = PROJECT / "client/Builds"
ARM_PARTS = ("PWArmL1", "PWArmL2", "PWArmL3", "PWArmLMount",
             "PWArmR1", "PWArmR2", "PWArmR3", "PWArmRMount")
ANIMATED = ("PWArmsAnimated", "JEProfessionAnimated", "JEWingsAnimated")


def required_models() -> set[str]:
    """Returns:
        set[str]: Names of every OBJ that the conversion must produce.
    """
    return {stem + ".obj" for stem in source_stems()} | {
        stem + ".obj" for stem in MAX_UPGRADE_SHIPS
    } | {"OriginalJEBase.obj"} | {stem + ".obj" for stem in ARM_PARTS}


def verify_sources() -> None:
    """Check the needed original bytes against their pinned source manifests.

    Raises:
        ValueError: A source model, clip, or atlas changed or is missing.
    """
    records: dict[str, tuple[Path, str]] = {}
    for manifest in (SOURCE / "manifest.json", SOURCE / "player_parts/manifest.json"):
        for record in json.loads(manifest.read_text())["files"]:
            path = manifest.parent / record["path"]
            records[path.name.lower()] = (path, record["sha256"])
    needed = {stem.lower() + ".w3d" for stem in source_stems()}
    needed.update({"spp23-1.w3d", "spp23-2.w3d", "sjp33-1.w3d",
                   "sjp33-2.w3d", "sjw33-1.w3d", "sjw33-2.w3d",
                   "ship_all.dds", "grid_env01.dds"})
    for name in sorted(needed):
        if name not in records:
            raise ValueError(f"Original asset is absent from the pinned manifests: {name}")
        path, expected = records[name]
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise ValueError(f"Original asset provenance check failed: {path}")


def verify_products(directory: Path) -> None:
    """Check all meshes, texture references, and animated FBX outputs.

    Raises:
        ValueError: The conversion left a model or referenced texture missing.
    """
    expected = required_models()
    found = {path.name for path in directory.glob("*.obj")}
    if found != expected:
        raise ValueError("Converted OBJ set differs: missing=" +
                         repr(sorted(expected - found)) + " extra=" +
                         repr(sorted(found - expected)))
    for obj in directory.glob("*.obj"):
        if obj.stat().st_size == 0 or not obj.with_suffix(".mtl").is_file():
            raise ValueError(f"Incomplete converted model: {obj}")
    for material in directory.glob("*.mtl"):
        for line in material.read_text(errors="replace").splitlines():
            if not line.startswith(("map_Kd ", "map_d ")):
                continue
            texture = directory / line.split(maxsplit=1)[1]
            if not texture.is_file():
                raise ValueError(f"Missing {texture.name} referenced by {material.name}")
    for name in ANIMATED:
        fbx = directory / (name + ".fbx")
        if not fbx.is_file() or fbx.stat().st_size < 1000:
            raise ValueError(f"Incomplete animated FBX: {fbx}")


def run_blender(blender: Path, script: str, arguments: list[str], log: Path) -> None:
    """Run one isolated conversion step and append diagnostics to the import log.

    Raises:
        RuntimeError: Blender could not convert the requested source models.
    """
    command = [str(blender), "-b", "--factory-startup", "--python",
               str(TOOLS / script), "--", *arguments]
    with log.open("a") as stream:
        stream.write("\nIMPORT STEP " + script + " " + " ".join(arguments) + "\n")
        stream.flush()
        result = subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT,
                                cwd=PROJECT, check=False)
    if result.returncode != 0:
        raise RuntimeError(f"Blender failed in {script}; see {log}")


def publish_products(staging: Path) -> int:
    """Copy validated assets atomically while keeping existing Unity .meta GUIDs.

    Returns:
        int: Number of changed files published to Unity Resources.
    """
    DESTINATION.mkdir(parents=True, exist_ok=True)
    changed = 0
    for source in sorted(staging.iterdir()):
        if source.suffix.lower() not in {".obj", ".mtl", ".png", ".fbx"}:
            continue
        destination = DESTINATION / source.name
        if destination.is_file() and source.read_bytes() == destination.read_bytes():
            continue
        with tempfile.NamedTemporaryFile(dir=DESTINATION, prefix=".asset-import-",
                                         delete=False) as scratch:
            temporary = Path(scratch.name)
        try:
            shutil.copy2(source, temporary)
            os.replace(temporary, destination)
        finally:
            temporary.unlink(missing_ok=True)
        changed += 1
    return changed


def verify_unity(editor: Path) -> None:
    """Ask Unity to import every converted model and validate all three clips.

    Raises:
        RuntimeError: Unity rejected a model or its animation.
    """
    log = BUILD / "asset-import-unity.log"
    result = subprocess.run([str(editor), "-batchmode", "-nographics", "-projectPath",
                             str(PROJECT / "client"), "-executeMethod",
                             "FortyTwoAndBeyond.Editor.LegacyShipImportAudit.VerifyConvertedAssets",
                             "-logFile", str(log), "-quit"], cwd=PROJECT, check=False)
    if result.returncode != 0 or "LEGACY IMPORT PASS" not in log.read_text():
        raise RuntimeError(f"Unity asset import validation failed; see {log}")


def main() -> None:
    """Convert all declared models once, then validate their Unity imports."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--addon-root", type=Path)
    parser.add_argument("--blender", type=Path)
    parser.add_argument("--unity-editor")
    parser.add_argument("--check-only", action="store_true")
    parser.add_argument("--skip-unity", action="store_true")
    options = parser.parse_args()
    BUILD.mkdir(parents=True, exist_ok=True)
    if not options.check_only:
        if options.addon_root is None or not (options.addon_root / "io_mesh_w3d").is_dir():
            parser.error("--addon-root must point to the OpenSAGE W3D Blender add-on")
        blender = options.blender or Path(shutil.which("blender") or "")
        if not blender.is_file():
            parser.error("Blender is missing; supply --blender PATH")
        verify_sources()
        log = BUILD / "asset-import.log"
        log.write_text("42 and Beyond original ship conversion\n")
        with tempfile.TemporaryDirectory(prefix="42b-ship-import-") as temporary:
            staging = Path(temporary)
            common = ["--addon-root", str(options.addon_root.resolve()),
                      "--output-dir", str(staging)]
            run_blender(blender, "import_legacy_ships.py",
                        [*common, "--ships", *source_stems()], log)
            run_blender(blender, "build_original_player_ships.py",
                        ["--output-dir", str(staging)], log)
            for part in ("pw", "je_profession", "je_wings"):
                run_blender(blender, "export_original_animated_parts.py",
                            [*common, "--part", part], log)
            verify_products(staging)
            changed = publish_products(staging)
        print(f"Published {changed} changed converted assets; details: {log}")
    else:
        verify_products(DESTINATION)
        print(f"Verified {len(required_models())} OBJ models and three FBXs in {DESTINATION}")
    if not options.skip_unity:
        verify_unity(find_editor(options.unity_editor))
        print("Unity imported all declared ship models and animation clips.")


if __name__ == "__main__":
    main()
