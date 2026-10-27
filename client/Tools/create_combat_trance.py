"""Build an animated Combat Trance ring from the saved shield ship scene."""

from pathlib import Path
import math

import bpy
from mathutils import Vector


ART = Path(__file__).resolve().parents[1] / "Art" / "Blender"
SOURCE = ART / "shield_absorption.blend"
OUTPUT = ART / "combat_trance.blend"
PREVIEW = ART / "combat_trance_preview.png"


def soft_glow(name):
    """Returns:
        bpy.types.Material: Animated copper circuitry with restrained energy glow.
    """
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    nodes.clear()
    links = material.node_tree.links
    output = nodes.new("ShaderNodeOutputMaterial")
    surface = nodes.new("ShaderNodeBsdfPrincipled")
    surface.inputs["Metallic"].default_value = 0.62
    surface.inputs["Roughness"].default_value = 0.27
    surface.inputs["Emission Color"].default_value = (1, 0.34, 0.09, 1)
    coordinates = nodes.new("ShaderNodeTexCoord")
    separate = nodes.new("ShaderNodeSeparateXYZ")
    links.new(coordinates.outputs["Object"], separate.inputs["Vector"])
    angle = nodes.new("ShaderNodeMath")
    angle.operation = "ARCTAN2"
    links.new(separate.outputs["Y"], angle.inputs[0])
    links.new(separate.outputs["X"], angle.inputs[1])
    frequency = nodes.new("ShaderNodeMath")
    frequency.operation = "MULTIPLY"
    frequency.inputs[1].default_value = 25
    links.new(angle.outputs[0], frequency.inputs[0])
    phase = nodes.new("ShaderNodeMath")
    phase.operation = "ADD"
    links.new(frequency.outputs[0], phase.inputs[0])
    phase_offset = nodes.new("ShaderNodeValue")
    for frame, value in [(1, 0), (97, 6 * math.pi)]:
        phase_offset.outputs[0].default_value = value
        phase_offset.outputs[0].keyframe_insert(data_path="default_value", frame=frame)
    links.new(phase_offset.outputs[0], phase.inputs[1])
    oscillation = nodes.new("ShaderNodeMath")
    oscillation.operation = "SINE"
    links.new(phase.outputs[0], oscillation.inputs[0])
    stripe = nodes.new("ShaderNodeMath")
    stripe.operation = "GREATER_THAN"
    stripe.inputs[1].default_value = 0.93
    links.new(oscillation.outputs[0], stripe.inputs[0])

    grain = nodes.new("ShaderNodeTexNoise")
    grain.inputs["Scale"].default_value = 19
    grain.inputs["Detail"].default_value = 3
    grain.inputs["Roughness"].default_value = 0.65
    links.new(coordinates.outputs["Object"], grain.inputs["Vector"])
    copper = nodes.new("ShaderNodeValToRGB")
    copper.color_ramp.elements[0].position = 0.18
    copper.color_ramp.elements[0].color = (0.045, 0.009, 0.005, 1)
    copper.color_ramp.elements[1].position = 0.82
    copper.color_ramp.elements[1].color = (0.29, 0.068, 0.019, 1)
    links.new(grain.outputs["Factor"], copper.inputs["Factor"])
    stripes = nodes.new("ShaderNodeMixRGB")
    stripes.blend_type = "MIX"
    stripes.inputs[2].default_value = (0.9, 0.34, 0.08, 1)
    links.new(stripe.outputs[0], stripes.inputs[0])
    links.new(copper.outputs["Color"], stripes.inputs[1])
    dot_frequency = nodes.new("ShaderNodeMath")
    dot_frequency.operation = "MULTIPLY"
    dot_frequency.inputs[1].default_value = 13
    links.new(angle.outputs[0], dot_frequency.inputs[0])
    dot_phase = nodes.new("ShaderNodeMath")
    dot_phase.operation = "ADD"
    links.new(dot_frequency.outputs[0], dot_phase.inputs[0])
    links.new(phase_offset.outputs[0], dot_phase.inputs[1])
    dot_sine = nodes.new("ShaderNodeMath")
    dot_sine.operation = "SINE"
    links.new(dot_phase.outputs[0], dot_sine.inputs[0])
    dot_peak = nodes.new("ShaderNodeMapRange")
    dot_peak.interpolation_type = "SMOOTHSTEP"
    dot_peak.inputs["From Min"].default_value = 0.97
    dot_peak.inputs["From Max"].default_value = 0.998
    links.new(dot_sine.outputs[0], dot_peak.inputs["Value"])
    top_surface = nodes.new("ShaderNodeMapRange")
    top_surface.interpolation_type = "SMOOTHSTEP"
    top_surface.inputs["From Min"].default_value = -0.115
    top_surface.inputs["From Max"].default_value = -0.09
    links.new(separate.outputs["Z"], top_surface.inputs["Value"])
    dots = nodes.new("ShaderNodeMath")
    dots.operation = "MULTIPLY"
    links.new(dot_peak.outputs["Result"], dots.inputs[0])
    links.new(top_surface.outputs["Result"], dots.inputs[1])
    dot_color = nodes.new("ShaderNodeMixRGB")
    dot_color.blend_type = "MIX"
    dot_color.inputs[2].default_value = (0.86, 0.95, 1, 1)
    links.new(dots.outputs[0], dot_color.inputs[0])
    links.new(stripes.outputs["Color"], dot_color.inputs[1])
    links.new(dot_color.outputs["Color"], surface.inputs["Base Color"])

    micro_bump = nodes.new("ShaderNodeBump")
    micro_bump.inputs["Strength"].default_value = 0.075
    micro_bump.inputs["Distance"].default_value = 0.018
    links.new(grain.outputs["Factor"], micro_bump.inputs["Height"])
    links.new(micro_bump.outputs["Normal"], surface.inputs["Normal"])
    edge = nodes.new("ShaderNodeLayerWeight")
    edge.inputs["Blend"].default_value = 0.32
    edge_glow = nodes.new("ShaderNodeMath")
    edge_glow.operation = "MULTIPLY"
    edge_glow.inputs[1].default_value = 0.45
    links.new(edge.outputs["Fresnel"], edge_glow.inputs[0])
    stripe_glow = nodes.new("ShaderNodeMath")
    stripe_glow.operation = "MULTIPLY"
    stripe_glow.inputs[1].default_value = 1.35
    links.new(stripe.outputs[0], stripe_glow.inputs[0])
    add_glow = nodes.new("ShaderNodeMath")
    add_glow.operation = "ADD"
    links.new(edge_glow.outputs[0], add_glow.inputs[0])
    links.new(stripe_glow.outputs[0], add_glow.inputs[1])
    base_glow = nodes.new("ShaderNodeMath")
    base_glow.operation = "ADD"
    base_glow.inputs[1].default_value = 0.16
    links.new(add_glow.outputs[0], base_glow.inputs[0])
    dot_glow = nodes.new("ShaderNodeMath")
    dot_glow.operation = "MULTIPLY"
    dot_glow.inputs[1].default_value = 2.5
    links.new(dots.outputs[0], dot_glow.inputs[0])
    final_glow = nodes.new("ShaderNodeMath")
    final_glow.operation = "ADD"
    links.new(base_glow.outputs[0], final_glow.inputs[0])
    links.new(dot_glow.outputs[0], final_glow.inputs[1])
    links.new(final_glow.outputs[0], surface.inputs["Emission Strength"])
    links.new(surface.outputs[0], output.inputs["Surface"])
    material.diffuse_color = (0.9, 0.29, 0.07, 1)
    return material


def arc(name, radius, start, sweep, height, thickness, material, parent):
    """Returns:
        bpy.types.Object: A smooth glowing arc attached to one rotor.
    """
    path = bpy.data.curves.new(name, "CURVE")
    path.dimensions = "3D"
    path.resolution_u = 24
    path.bevel_depth = thickness
    path.bevel_resolution = 3
    samples = max(12, int(100 * sweep / (2 * math.pi)))
    spline = path.splines.new("POLY")
    spline.points.add(samples)
    for index, point in enumerate(spline.points):
        angle = start + sweep * index / samples
        point.co = (radius * math.cos(angle), radius * math.sin(angle),
                    height, 1)
    result = bpy.data.objects.new(name, path)
    bpy.context.scene.collection.objects.link(result)
    result.data.materials.append(material)
    result.parent = parent
    return result


def rotor(name, rotations):
    """Returns:
        bpy.types.Object: A keyed parent for the ring's orbiting sections.
    """
    result = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(result)
    result.rotation_euler.z = 0
    result.keyframe_insert(data_path="rotation_euler", frame=1)
    result.rotation_euler.z = rotations * 2 * math.pi
    result.keyframe_insert(data_path="rotation_euler", frame=97)
    for layer in result.animation_data.action.layers:
        for strip in layer.strips:
            for channelbag in strip.channelbags:
                for curve in channelbag.fcurves:
                    for key in curve.keyframe_points:
                        key.interpolation = "LINEAR"
    return result


def pulse_scale(obj):
    """Give the main halo a restrained, repeating breathing motion."""
    for frame, scale in [(1, 0.99), (13, 1.035), (25, 0.99),
                         (37, 1.035), (49, 0.99), (61, 1.035),
                         (73, 0.99), (85, 1.035), (97, 0.99)]:
        obj.scale = (scale, scale, 1)
        obj.keyframe_insert(data_path="scale", frame=frame)


def build_scene():
    """Replace shield effects with a luminous ring around the retained ship."""
    if Path(bpy.data.filepath).resolve() != SOURCE.resolve():
        raise RuntimeError("Open shield_absorption.blend before building Combat Trance.")
    keep = set()
    for group in bpy.data.collections:
        if group.name.startswith("01 — Defender ship") or group.name.startswith(
                "06 — Camera and lighting"):
            keep.update(group.objects)
    if not keep or bpy.context.scene.camera not in keep:
        raise RuntimeError("The source ship or its camera is missing.")
    for obj in list(bpy.data.objects):
        if obj not in keep:
            bpy.data.objects.remove(obj, do_unlink=True)
    for group in list(bpy.data.collections):
        if not group.objects and group.users == 0:
            bpy.data.collections.remove(group)

    amber = soft_glow("Trance • softly charged amber")

    main = rotor("Combat Trance • main halo", 0)
    pulse_scale(main)
    arc("Combat Trance • continuous power rail", 2.17, 0, 2 * math.pi,
        -0.13, 0.042, amber, main)

    scene = bpy.context.scene
    scene.frame_start, scene.frame_end = 1, 96
    scene.render.fps = 24
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x, scene.render.resolution_y = 1600, 900
    scene.render.resolution_percentage = 100
    scene.frame_set(48)
    camera = scene.camera
    camera.location = (7.2, -9.2, 5.5)
    camera.rotation_euler = (Vector((0, 0, 0)) - camera.location).to_track_quat(
        "-Z", "Y").to_euler()
    camera.data.ortho_scale = 7.2
    bpy.ops.wm.save_as_mainfile(filepath=str(OUTPUT))
    scene.render.filepath = str(PREVIEW)
    bpy.ops.render.render(write_still=True)
    scene.render.filepath = ""
    print("Combat Trance scene:", OUTPUT)
    print("Combat Trance preview:", PREVIEW)


if __name__ == "__main__":
    build_scene()
