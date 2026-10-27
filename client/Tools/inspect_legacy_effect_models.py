"""Inspect original W3D effect geometry through the pinned OpenSAGE add-on."""

from __future__ import annotations

import argparse
from pathlib import Path
import subprocess
import sys
import tempfile

import bpy


PROJECT = Path(__file__).resolve().parents[2]
SOURCE = PROJECT / "legacy_assets/effects"


def main() -> None:
    """Print imported effect meshes, rigs, and native animation actions."""
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--addon-root", required=True, type=Path)
    parser.add_argument("--source-dir", type=Path, default=SOURCE)
    parser.add_argument("--models", nargs="+", required=True)
    options = parser.parse_args(arguments)
    sys.path.insert(0, str(options.addon_root.resolve()))
    sys.path.insert(0, str(PROJECT / "client/Tools"))
    import io_mesh_w3d
    from io_mesh_w3d.common.utils import material_import
    from import_legacy_ships import sanitize_stage1_mapping

    material_import.create_material_from_vertex_material = sanitize_stage1_mapping(
        material_import.create_material_from_vertex_material)
    io_mesh_w3d.register()
    with tempfile.TemporaryDirectory(prefix="42b-effect-inspect-") as temporary:
        staging = Path(temporary)
        for source in (options.source_dir / "models").iterdir():
            if source.suffix.lower() != ".w3d":
                continue
            (staging / source.name.lower()).symlink_to(source.resolve())
        for source in (options.source_dir / "textures").iterdir():
            if source.suffix.lower() != ".dds":
                continue
            subprocess.run(["/usr/bin/sips", "-s", "format", "png", str(source),
                            "--out", str(staging / (source.stem.lower() + ".png"))],
                           stdout=subprocess.DEVNULL, check=True)
        for stem in options.models:
            bpy.ops.object.select_all(action="SELECT")
            bpy.ops.object.delete(use_global=False)
            print("EFFECT MODEL", stem)
            bpy.ops.import_mesh.westwood_w3d(filepath=str(staging / (stem.lower() + ".w3d")))
            for obj in bpy.context.scene.objects:
                action = obj.animation_data.action if obj.animation_data else None
                print("  OBJECT", obj.name, obj.type, "dims", tuple(round(x, 3)
                    for x in obj.dimensions), "action", action.name if action else None)


if __name__ == "__main__":
    main()
