"""Build a spaced Blender gallery of every converted Earth & Beyond OBJ.

Run with ``blender -b --python client/Tools/build_ship_model_gallery.py``.
Each model has a named collection and an empty root for easy Outliner browsing.
"""

from __future__ import annotations

import math
from pathlib import Path

import bpy
from mathutils import Quaternion, Vector


PROJECT = Path(__file__).resolve().parents[2]
SOURCE = PROJECT / "client/Assets/FortyTwoAndBeyond/Resources/LegacyShips"
DESTINATION = PROJECT / "client/Builds/ConvertedShipGallery.blend"
PW_ARMS = tuple(sorted(SOURCE.glob("PWArm*.obj")))
COLS = 7
SPACING = 8.5


def imported_meshes(path: Path) -> list[bpy.types.Object]:
    """Returns:
        list[bpy.types.Object]: Mesh objects created by importing one OBJ.
    """
    before = set(bpy.data.objects)
    bpy.ops.wm.obj_import(filepath=str(path))
    return [obj for obj in bpy.data.objects if obj not in before and obj.type == "MESH"]


def move_to_collection(objects: list[bpy.types.Object], collection: bpy.types.Collection) -> None:
    """Group all pieces of a converted model under one Outliner collection."""
    for obj in objects:
        for old in tuple(obj.users_collection):
            old.objects.unlink(obj)
        collection.objects.link(obj)


def add_model(path: Path, index: int, total: int) -> None:
    """Import, normalize, space, and label one converted model."""
    collection = bpy.data.collections.new(path.stem)
    bpy.context.scene.collection.children.link(collection)
    objects = imported_meshes(path)
    if path.stem == "OriginalPW":
        for arm in PW_ARMS:
            objects.extend(imported_meshes(arm))
    if not objects:
        raise ValueError(f"No mesh in {path}")
    # Make each gallery entry one selectable object while retaining its materials.
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    objects = [objects[0]]
    objects[0].name = path.stem
    move_to_collection(objects, collection)
    corners = [obj.matrix_world @ Vector(corner)
               for obj in objects for corner in obj.bound_box]
    minimum = Vector(min(point[axis] for point in corners) for axis in range(3))
    maximum = Vector(max(point[axis] for point in corners) for axis in range(3))
    center = (minimum + maximum) * .5
    scale = 4.8 / max(1, *(maximum[axis] - minimum[axis] for axis in range(3)))
    root = bpy.data.objects.new(path.stem + " — select to frame", None)
    collection.objects.link(root)
    for obj in objects:
        obj.parent = root
    row, col = divmod(index, COLS)
    root.scale = (scale,) * 3
    root.location = Vector((col * SPACING, -row * SPACING, 0)) - center * scale
    root.empty_display_type = "CUBE"
    root.empty_display_size = 2.5
    label = bpy.data.curves.new(path.stem + " label", "FONT")
    label.body = path.stem + ("  (complete with arms)" if path.stem == "OriginalPW" else "")
    label.size = .56
    label.align_x = "CENTER"
    text = bpy.data.objects.new(path.stem + " label", label)
    collection.objects.link(text)
    text.location = (col * SPACING, -row * SPACING - 3.3, 0)
    print(f"GALLERY {index + 1}/{total} {path.stem} ({len(objects)} pieces)")


def fix_opacity() -> None:
    """Keep old TGA alpha channels from hiding solid hull plating in Blender."""
    for material in bpy.data.materials:
        if not material.use_nodes:
            continue
        for node in material.node_tree.nodes:
            if node.type != "BSDF_PRINCIPLED":
                continue
            alpha = node.inputs.get("Alpha")
            if alpha is None:
                continue
            for link in tuple(alpha.links):
                material.node_tree.links.remove(link)
            alpha.default_value = 1


def main() -> None:
    """Save every declared converted OBJ as a navigable Blender scene."""
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    models = sorted(SOURCE.glob("*.obj"), key=lambda path:
                    (not path.stem.startswith("Original"), path.stem.lower()))
    for index, path in enumerate(models):
        add_model(path, index, len(models))
    fix_opacity()
    rows = math.ceil(len(models) / COLS)
    midpoint = Vector(((COLS - 1) * SPACING * .5,
                       -(rows - 1) * SPACING * .5, 0))
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type != "VIEW_3D":
                continue
            area.spaces.active.region_3d.view_location = midpoint
            area.spaces.active.region_3d.view_distance = rows * SPACING * .85
            area.spaces.active.region_3d.view_rotation = Quaternion((1, 0, 0, 0))
            area.spaces.active.shading.type = "MATERIAL"
    DESTINATION.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(DESTINATION))
    print(f"Saved {len(models)} converted models to {DESTINATION}")


if __name__ == "__main__":
    main()
