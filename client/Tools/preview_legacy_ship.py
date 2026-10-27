"""Render one converted legacy ship for mesh-orientation inspection in Blender."""

from __future__ import annotations

from pathlib import Path
import sys

import bpy
from mathutils import Vector


def main() -> None:
    """Render the supplied OBJ from a front-quarter camera."""
    arguments = sys.argv[sys.argv.index("--") + 1:]
    model, output = map(Path, arguments[:2])
    rear_view = "--rear" in arguments[2:]
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    bpy.ops.wm.obj_import(filepath=str(model))
    meshes = [obj for obj in bpy.data.objects if obj.type == "MESH"]
    preview_metal = bpy.data.materials.new("Untextured original metal")
    preview_metal.diffuse_color = (.42, .48, .51, 1)
    for obj in meshes:
        if not obj.data.materials:
            obj.data.materials.append(preview_metal)
        if rear_view:
            obj.data.materials.clear()
            obj.data.materials.append(preview_metal)
    for material in bpy.data.materials:
        if not material.use_nodes:
            continue
        for node in material.node_tree.nodes:
            if node.type == "BSDF_PRINCIPLED":
                alpha = node.inputs.get("Alpha")
                if alpha is not None:
                    for link in list(alpha.links):
                        material.node_tree.links.remove(link)
                    alpha.default_value = 1
    corners = [obj.matrix_world @ Vector(corner) for obj in meshes for corner in obj.bound_box]
    center = sum(corners, Vector()) / len(corners)
    span = max(max(point[i] for point in corners) - min(point[i] for point in corners)
               for i in range(3))
    camera = bpy.data.cameras.new("Ship preview camera")
    camera_object = bpy.data.objects.new("Ship preview camera", camera)
    bpy.context.scene.collection.objects.link(camera_object)
    camera_object.location = center + (Vector((span * 1.8, 0, span * .3)) if rear_view
                                       else Vector((span * 1.3, -span * 1.6, span * .85)))
    direction = center - camera_object.location
    camera_object.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    camera.type = "ORTHO"; camera.ortho_scale = span * 2.2
    bpy.context.scene.camera = camera_object
    for position, power in (((1, -1, 2), 1700), ((-2, 1, 1), 900)):
        lamp = bpy.data.lights.new("Studio light", "AREA")
        lamp.energy = power
        lamp_object = bpy.data.objects.new("Studio light", lamp)
        bpy.context.scene.collection.objects.link(lamp_object)
        lamp_object.location = center + Vector(position) * span
        lamp_object.rotation_euler = (center - lamp_object.location).to_track_quat("-Z", "Y").to_euler()
        lamp.shape = "DISK"; lamp.size = span
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "Medium High Contrast"
    scene.view_settings.exposure = 1.5
    scene.render.resolution_x = 900; scene.render.resolution_y = 650
    scene.render.resolution_percentage = 100
    scene.render.filepath = str(output)
    scene.world.color = (.07, .08, .11)
    scene.render.image_settings.file_format = "PNG"
    bpy.ops.render.render(write_still=True)


if __name__ == "__main__":
    main()
