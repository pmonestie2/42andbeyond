"""Convert selected original W3D ships into Unity-ready OBJ meshes and PNG maps.

Run inside Blender with the OpenSAGE W3D add-on checkout::

    blender -b --factory-startup --python client/Tools/import_legacy_ships.py -- \
        --addon-root /path/to/OpenSAGE.BlenderPlugin --ships TVF01A npc01

The files in legacy_assets remain unchanged. The add-on is a conversion tool only.
"""

from __future__ import annotations

import argparse
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

import bpy
sys.path.insert(0, str(Path(__file__).resolve().parent))
from legacy_ship_manifest import STATIC_SHIPS


PROJECT = Path(__file__).resolve().parents[2]
SOURCE = PROJECT / "legacy_assets"
OUTPUT = PROJECT / "client/Assets/FortyTwoAndBeyond/Resources/LegacyShips"
DEFAULT_SHIPS = STATIC_SHIPS


def ignore_animation(context: object, rig: object, animation: object,
                     hierarchy: object) -> None:
    """Keep the static ship geometry when old W3D animation data is unsupported."""


def sanitize_stage1_mapping(create_material):
    """Returns:
        callable: OpenSAGE material creation with unsupported ENB mapping clamped.
    """
    from io_mesh_w3d.w3d.structs.mesh_structs.vertex_material import STAGE1_MAPPING_MASK

    def compatible_material(name, source_material):
        """Returns:
            tuple: Blender material and shader nodes for the source material.
        """
        attributes = source_material.vm_info.attributes
        if attributes & STAGE1_MAPPING_MASK > 0x00001200:
            source_material.vm_info.attributes = attributes & ~STAGE1_MAPPING_MASK
        return create_material(name, source_material)

    return compatible_material


def staged_sources(directory: Path, source_root: Path = SOURCE) -> dict[str, Path]:
    """Returns:
        dict[str, Path]: Case-insensitive names of original model and texture files.
    """
    originals: dict[str, Path] = {}
    source_root = source_root.resolve()
    for source in (source_root, source_root / "player_parts"):
        if not (source / "models").is_dir():
            continue
        for original in (source / "models").iterdir():
            if original.is_file():
                originals[original.name.lower()] = original
                destination = directory / original.name
                if not destination.exists():
                    destination.symlink_to(original)
        for original in (source / "textures").iterdir():
            if not original.is_file():
                continue
            key = original.name.lower()
            originals[key] = original
            if original.suffix.lower() == ".dds":
                subprocess.run(["/usr/bin/sips", "-s", "format", "png", str(original),
                                "--out", str(directory / (original.stem + ".png"))],
                               stdout=subprocess.DEVNULL, check=True)
            else:
                destination = directory / original.name
                if not destination.exists():
                    destination.symlink_to(original)
    return originals


def clear_models() -> None:
    """Remove imported models including objects in hidden LOD collections."""
    # Hidden LOD collections are not selected by select_all. Remove their data explicitly
    # so a subsequent model cannot accidentally inherit meshes from an earlier import.
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for collection in list(bpy.data.collections):
        bpy.data.collections.remove(collection)


def export_ship(stem: str, staging: Path, originals: dict[str, Path], output: Path) -> None:
    """Export one highest-detail assembled hull and its actual source textures.

    Raises:
        ValueError: The requested model or its visible hull is absent.
    """
    model = originals.get(stem.lower() + ".w3d")
    if model is None:
        raise ValueError("Missing original W3D model: " + stem)
    clear_models()
    bpy.ops.import_mesh.westwood_w3d(filepath=str(staging / model.name))
    collection = next((item for item in bpy.data.collections
                       if item.name.lower() == stem.lower()), None)
    candidates = collection.objects if collection is not None else bpy.context.scene.objects
    hull = [obj for obj in candidates
            if obj.type == "MESH" and obj.visible_get() and
            not obj.name.upper().startswith("FLARE") and ".FLARE" not in obj.name.upper()]
    if not hull:
        raise ValueError("W3D has no visible hull geometry: " + stem)
    export_hull(stem, hull, output)


def export_hull(stem: str, hull: list, output: Path) -> None:
    """Export selected assembled hull meshes with their original texture assignments."""
    output.mkdir(parents=True, exist_ok=True)
    used_images = {node.image for obj in hull for material in obj.data.materials
                   if material is not None and material.use_nodes
                   for node in material.node_tree.nodes
                   if node.type == "TEX_IMAGE" and node.image is not None}
    for image in used_images:
        if image.source != "FILE":
            continue
        source_image = Path(bpy.path.abspath(image.filepath))
        if not source_image.is_file():
            continue
        destination = output / (source_image.stem.lower() + source_image.suffix.lower())
        if not destination.exists():
            shutil.copy2(source_image, destination)
        image.filepath = str(destination)
    bpy.ops.object.select_all(action="DESELECT")
    for obj in hull:
        obj.hide_set(False)
        obj.select_set(True)
    bpy.context.view_layer.objects.active = hull[0]
    destination = output / (stem + ".obj")
    bpy.ops.wm.obj_export(filepath=str(destination), export_selected_objects=True,
                          apply_modifiers=True, export_materials=True,
                          forward_axis="Z", up_axis="Y", path_mode="RELATIVE")
    print("EXPORTED", stem, len(hull), "hull parts,", len(used_images), "textures")


def main() -> None:
    """Convert the requested ships using a disposable model/texture search folder."""
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--addon-root", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, default=OUTPUT)
    parser.add_argument("--ships", nargs="+", default=DEFAULT_SHIPS)
    options = parser.parse_args(arguments)
    sys.path.insert(0, str(options.addon_root.resolve()))
    import io_mesh_w3d
    from io_mesh_w3d import import_utils
    from io_mesh_w3d.common.utils import material_import
    import_utils.create_animation = ignore_animation
    material_import.create_material_from_vertex_material = sanitize_stage1_mapping(
        material_import.create_material_from_vertex_material)
    io_mesh_w3d.register()
    with tempfile.TemporaryDirectory(prefix="42b-original-ships-") as temporary:
        staging = Path(temporary)
        originals = staged_sources(staging)
        for stem in options.ships:
            export_ship(stem, staging, originals, options.output_dir)


if __name__ == "__main__":
    main()
