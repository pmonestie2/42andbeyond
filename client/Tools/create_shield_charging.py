"""Derive the persistent TT charging field from the shield absorption preview."""

from pathlib import Path

import bpy


ART = Path(__file__).resolve().parents[1] / "Art" / "Blender"
SOURCE = ART / "shield_absorption.blend"
OUTPUT = ART / "shield_charging.blend"


def opacity_keys(material_name, frames):
    """Keep the field visible before impact and brighten it at contact."""
    material = next(mat for mat in bpy.data.materials
                    if mat.name.startswith(material_name))
    node = material.node_tree.nodes["Animated opacity"]
    node.outputs[0].default_value = frames[0][1]
    for frame, opacity in frames:
        node.outputs[0].default_value = opacity
        node.outputs[0].keyframe_insert("default_value", frame=frame)


def expand_shell(name):
    """Expand every shape key to keep the charging shell outside the impact shell."""
    obj = bpy.data.objects[name]
    for key in obj.data.shape_keys.key_blocks:
        for vertex in key.data:
            vertex.co *= 1.045
    obj.name = name.replace("Shield •", "Shield Charging •")


def build_scene():
    """Save the always active field with its existing contact bend and wave."""
    if not SOURCE.exists():
        raise FileNotFoundError(f"Build the shield absorption preview first: {SOURCE}")
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    expand_shell("Shield • spherical energy membrane")
    expand_shell("Shield • flowing energy surface")
    opacity_keys("Shield • translucent light-blue field",
                 [(1, .18), (42, .18), (43, .19), (46, .22),
                  (52, .21), (60, .19), (72, .18), (96, .18)])
    opacity_keys("Shield • flowing cyan energy filaments",
                 [(1, .53), (42, .53), (43, .55), (46, .61),
                  (52, .58), (60, .55), (72, .53), (96, .53)])
    bpy.context.scene.frame_set(48)
    bpy.ops.wm.save_as_mainfile(filepath=str(OUTPUT))
    print(f"Saved Shield Charging preview: {OUTPUT}")


build_scene()
