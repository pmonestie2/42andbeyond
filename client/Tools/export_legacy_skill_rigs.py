"""Export supported native animated skill W3D rigs as Unity FBX assets."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile

import bpy


PROJECT = Path(__file__).resolve().parents[2]
SOURCE = PROJECT / "legacy_assets/effects"
OUTPUT = PROJECT / "client/Assets/FortyTwoAndBeyond/Resources/LegacyEffects"
PARTS = {"damage": ("sk_rally0301b", "RallyDamageAnimated", SOURCE),
         "firing": ("sk_rally04b", "RallyFiringAnimated", SOURCE),
         "environment": ("shld_env01b", "EnvironmentShieldAnimated", SOURCE / "other"),
         "environment_loop": ("shld_env01a", "EnvironmentShieldLoopAnimated",
                              SOURCE / "other"),
         "psionic": ("shld_psy02a", "PsionicShieldAnimated", SOURCE / "defensive"),
         "menace": ("sk_menc01a", "MenaceAnimated", SOURCE / "defensive"),
         "inversion": ("shld_inv01d", "ShieldInversionAnimated", SOURCE / "defensive"),
         "recharge": ("shld_reg07c", "ShieldRechargeAnimated", SOURCE / "other")}


def verify_originals() -> None:
    """Check effect source bytes against their archive provenance manifests.

    Raises:
        ValueError: An original W3D or DDS payload changed.
    """
    for directory in (SOURCE, SOURCE / "other", SOURCE / "defensive"):
        records = json.loads((directory / "manifest.json").read_text())["files"]
        for record in records:
            path = directory / record["path"]
            if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != record["sha256"]:
                raise ValueError(f"Original effect provenance failed: {path}")


def main() -> None:
    """Import one native W3D rig and clip, then export its selected meshes."""
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--addon-root", required=True, type=Path)
    parser.add_argument("--part", required=True, choices=tuple(PARTS))
    parser.add_argument("--output-dir", type=Path, default=OUTPUT)
    options = parser.parse_args(arguments)
    verify_originals()
    stem, exported, source_dir = PARTS[options.part]
    sys.path.insert(0, str(options.addon_root.resolve()))
    sys.path.insert(0, str(PROJECT / "client/Tools"))
    import io_mesh_w3d
    from io_mesh_w3d.common.utils import material_import
    from import_legacy_ships import sanitize_stage1_mapping

    material_import.create_material_from_vertex_material = sanitize_stage1_mapping(
        material_import.create_material_from_vertex_material)
    io_mesh_w3d.register()
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    with tempfile.TemporaryDirectory(prefix="42b-rally-export-") as temporary:
        staging = Path(temporary)
        for name in (stem + ".w3d", stem + "-1.w3d"):
            source = source_dir / "models" / name
            if not source.is_file():
                raise ValueError(f"Missing native Rally model or clip: {source}")
            (staging / name).symlink_to(source)
        for source in (source_dir / "textures").glob("*.dds"):
            subprocess.run(["/usr/bin/sips", "-s", "format", "png", str(source),
                            "--out", str(staging / (source.stem + ".png"))],
                           stdout=subprocess.DEVNULL, check=True)
        bpy.ops.import_mesh.westwood_w3d(filepath=str(staging / (stem + ".w3d")))
        collection = next((item for item in bpy.data.collections
                           if item.name.lower() == stem), None)
        if collection is None:
            raise ValueError(f"W3D skill collection did not import: {stem}")
        meshes = [obj for obj in collection.objects if obj.type == "MESH"]
        bpy.ops.import_mesh.westwood_w3d(filepath=str(staging / (stem + "-1.w3d")))
        rig = next((obj for obj in bpy.data.objects
                    if obj.name.lower() == stem and obj.type == "ARMATURE"), None)
        if (rig is None or not meshes or rig.animation_data is None or
                rig.animation_data.action is None):
            raise ValueError(f"Native skill rig or clip did not import: {stem}")
        rig.animation_data.action.name = exported + " Loop"
        action = rig.animation_data.action
        bpy.context.scene.frame_start = int(action.frame_range[0])
        bpy.context.scene.frame_end = int(action.frame_range[1])
        bpy.context.scene.frame_set(bpy.context.scene.frame_start)
        bpy.ops.object.select_all(action="DESELECT")
        rig.select_set(True)
        for mesh in meshes:
            mesh.hide_set(False)
            mesh.select_set(True)
        bpy.context.view_layer.objects.active = rig
        options.output_dir.mkdir(parents=True, exist_ok=True)
        destination = options.output_dir / (exported + ".fbx")
        bpy.ops.export_scene.fbx(filepath=str(destination), use_selection=True,
                                 object_types={"ARMATURE", "MESH"}, add_leaf_bones=False,
                                 bake_anim=True, bake_anim_use_all_actions=False,
                                 bake_anim_use_nla_strips=False, bake_anim_step=1,
                                 axis_forward="-Z", axis_up="Y", path_mode="COPY",
                                 embed_textures=True)
    print(f"EXPORTED_RALLY {exported} {len(meshes)} meshes to {destination}")


if __name__ == "__main__":
    main()
