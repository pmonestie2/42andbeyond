"""Export original W3D ship sections and native motions as animated FBX assets.

Run with Blender and ``--addon-root /path/to/OpenSAGE.BlenderPlugin``. Source
W3D and DDS files remain byte-for-byte unchanged.
"""

from __future__ import annotations

import argparse
from pathlib import Path
import sys
import tempfile

import bpy


PROJECT = Path(__file__).resolve().parents[2]
SOURCE = PROJECT / "legacy_assets/player_parts/models"
OUTPUT = PROJECT / "client/Assets/FortyTwoAndBeyond/Resources/LegacyShips"
PW_ARMS = ("L_10", "L_20", "L_30", "L_MOUNT010",
           "R_10", "R_20", "R_30", "R_MOUNT0")
PARTS = {
    "pw": ("SPP23", "PWArmsAnimated", 30),
    "je_profession": ("SJP33", "JEProfessionAnimated", 90),
    "je_wings": ("SJW33", "JEWingsAnimated", 90),
}


def main() -> None:
    """Export one animated ship section with its original rig and texture maps.

    Raises:
        ValueError: A required original rig, texture, or mesh is missing.
    """
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--addon-root", required=True, type=Path)
    parser.add_argument("--output-dir", type=Path, default=OUTPUT)
    parser.add_argument("--part", required=True, choices=tuple(PARTS))
    options = parser.parse_args(arguments)
    output_dir = options.output_dir.resolve()
    model_stem, output_stem, last_frame = PARTS[options.part]
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
    with tempfile.TemporaryDirectory(prefix="42b-animated-fbx-") as temporary:
        staging = Path(temporary)
        for stem in (model_stem, model_stem + "-1"):
            source = SOURCE / (stem + ".w3d")
            if not source.is_file():
                raise ValueError(f"Missing original animation source: {source}")
            (staging / (stem.lower() + ".w3d")).symlink_to(source)
        for texture in output_dir.glob("*.png"):
            if texture.name.lower() == "ship_all.png":
                continue
            (staging / texture.name).symlink_to(texture)
        ship_atlas = output_dir / "ship_all.png"
        if not ship_atlas.is_file():
            raise ValueError(f"Missing converted ship atlas: {ship_atlas}")
        (staging / "Ship_all.png").symlink_to(ship_atlas)
        bpy.ops.import_mesh.westwood_w3d(filepath=str(staging / (model_stem.lower() + ".w3d")))
        collection = bpy.data.collections.get(model_stem)
        original_meshes = [obj for obj in collection.objects if obj.type == "MESH" and
                           not obj.name.upper().startswith("FLARE")] if collection else []
        bpy.ops.import_mesh.westwood_w3d(filepath=str(staging / (model_stem.lower() + "-1.w3d")))
        rig = bpy.data.objects.get(model_stem)
        if rig is None or rig.type != "ARMATURE" or collection is None:
            raise ValueError(f"Original {model_stem} armature did not import")
        if options.part == "pw":
            meshes = [bpy.data.objects.get(name) for name in PW_ARMS]
        else:
            meshes = original_meshes
        if not meshes or any(mesh is None for mesh in meshes):
            raise ValueError(f"Original {model_stem} render meshes did not import")
        rig.animation_data.action.name = output_stem + " Warp"
        bpy.context.scene.frame_start = 0
        bpy.context.scene.frame_end = last_frame
        bpy.context.scene.frame_set(0)
        bpy.ops.object.select_all(action="DESELECT")
        rig.select_set(True)
        for mesh in meshes:
            mesh.hide_set(False)
            mesh.select_set(True)
        bpy.context.view_layer.objects.active = rig
        output_dir.mkdir(parents=True, exist_ok=True)
        destination = output_dir / (output_stem + ".fbx")
        bpy.ops.export_scene.fbx(filepath=str(destination), use_selection=True,
                                 object_types={"ARMATURE", "MESH"},
                                 add_leaf_bones=False, bake_anim=True,
                                 bake_anim_use_all_actions=False,
                                 bake_anim_use_nla_strips=False, bake_anim_step=1,
                                 axis_forward="-Z", axis_up="Y",
                                 path_mode="COPY", embed_textures=True)
    print(f"EXPORTED_ANIMATED {output_stem} {len(meshes)} meshes to {destination}")


if __name__ == "__main__":
    main()
