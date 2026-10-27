"""Create Blender scenes with static hulls and their standard FBX animations."""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

import bpy
from mathutils import Quaternion, Vector


PROJECT = Path(__file__).resolve().parents[2]
RESOURCES = PROJECT / "client/Assets/FortyTwoAndBeyond/Resources/LegacyShips"
PREVIEWS = {
    "pw": ("OriginalPW", ("PWArmsAnimated",), "PWAnimatedPreview", 31),
    "je": ("OriginalJEBase", ("JEProfessionAnimated", "JEWingsAnimated"),
           "JEAnimatedPreview", 91),
}


def main() -> None:
    """Save an editable Blender scene whose timeline plays a native ship clip."""
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ship", required=True, choices=tuple(PREVIEWS))
    options = parser.parse_args(arguments)
    hull_name, animation_names, output_name, last_frame = PREVIEWS[options.ship]
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    before = set(bpy.data.objects)
    bpy.ops.wm.obj_import(filepath=str(RESOURCES / (hull_name + ".obj")))
    static = [obj for obj in bpy.data.objects if obj not in before]
    before = set(bpy.data.objects)
    for name in animation_names:
        bpy.ops.import_scene.fbx(filepath=str(RESOURCES / (name + ".fbx")))
    animated = [obj for obj in bpy.data.objects if obj not in before]
    for label, objects in ((options.ship.upper() + " static hull", static),
                           (options.ship.upper() + " animated sections", animated)):
        collection = bpy.data.collections.new(label)
        bpy.context.scene.collection.children.link(collection)
        for obj in objects:
            for old in tuple(obj.users_collection):
                old.objects.unlink(obj)
            collection.objects.link(obj)
    for material in bpy.data.materials:
        if not material.use_nodes:
            continue
        for node in material.node_tree.nodes:
            if node.type != "BSDF_PRINCIPLED":
                continue
            alpha = node.inputs.get("Alpha")
            if alpha is not None:
                for link in tuple(alpha.links):
                    material.node_tree.links.remove(link)
                alpha.default_value = 1
    scene = bpy.context.scene
    scene.frame_start = 1
    scene.frame_end = last_frame
    scene.render.fps = 30
    scene.frame_set(1)
    meshes = [obj for obj in static + animated if obj.type == "MESH"]
    corners = [obj.matrix_world @ Vector(corner) for obj in meshes for corner in obj.bound_box]
    minimum = Vector(min(point[axis] for point in corners) for axis in range(3))
    maximum = Vector(max(point[axis] for point in corners) for axis in range(3))
    center = (minimum + maximum) * .5
    span = max(maximum[axis] - minimum[axis] for axis in range(3))
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type != "VIEW_3D":
                continue
            area.spaces.active.region_3d.view_location = center
            area.spaces.active.region_3d.view_distance = span * 1.7
            area.spaces.active.region_3d.view_rotation = Quaternion((.89, .33, .22, .22))
            area.spaces.active.shading.type = "MATERIAL"
    output = PROJECT / "client/Builds" / (output_name + ".blend")
    output.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(output))
    print(f"Saved {options.ship.upper()} animation preview to {output}")


if __name__ == "__main__":
    main()
