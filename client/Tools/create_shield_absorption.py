"""Build a shield absorption scene with impact energy moving into the defender."""

import math
import random
from pathlib import Path

import bpy
import numpy as np
from mathutils import Vector


OUTPUT = Path(__file__).resolve().parents[1] / "Art" / "Blender" / "shield_absorption.blend"
FLOW_TEXTURE = Path(__file__).resolve().parents[1] / "Assets" / "FortyTwoAndBeyond" / "Resources" / "ShieldFlow.png"
FPS = 24
IMPACT = 43
NORMAL = Vector((0.90, 0.35, 0.20)).normalized()
TANGENT = NORMAL.cross(Vector((0, 0, 1))).normalized()
BITANGENT = NORMAL.cross(TANGENT).normalized()


def collection(name):
    """Returns:
        bpy.types.Collection: A new scene collection with the given name.
    """
    result = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(result)
    return result


def put(obj, group):
    """Move an object into its named visual group."""
    for old in list(obj.users_collection):
        old.objects.unlink(obj)
    group.objects.link(obj)
    return obj


def material(name, color, metallic=0.0, roughness=0.35, emission=0.0):
    """Returns:
        bpy.types.Material: A surface material with optional glow.
    """
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    shader = next(node for node in mat.node_tree.nodes if node.type == "BSDF_PRINCIPLED")
    shader.inputs["Base Color"].default_value = (*color, 1)
    shader.inputs["Metallic"].default_value = metallic
    shader.inputs["Roughness"].default_value = roughness
    shader.inputs["Emission Color"].default_value = (*color, 1)
    shader.inputs["Emission Strength"].default_value = emission
    mat.diffuse_color = (*color, 1)
    return mat


def glow(name, color, opacity=1.0, strength=2.0):
    """Returns:
        tuple[bpy.types.Material, bpy.types.Node]: Emissive material and its opacity control.
    """
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    nodes.clear()
    output = nodes.new("ShaderNodeOutputMaterial")
    mix = nodes.new("ShaderNodeMixShader")
    transparent = nodes.new("ShaderNodeBsdfTransparent")
    emit = nodes.new("ShaderNodeEmission")
    emit.inputs["Color"].default_value = (*color, 1)
    emit.inputs["Strength"].default_value = strength
    value = nodes.new("ShaderNodeValue")
    value.name = "Animated opacity"
    value.outputs[0].default_value = opacity
    links = mat.node_tree.links
    links.new(value.outputs[0], mix.inputs[0])
    links.new(transparent.outputs[0], mix.inputs[1])
    links.new(emit.outputs[0], mix.inputs[2])
    links.new(mix.outputs[0], output.inputs["Surface"])
    mat.diffuse_color = (*color, opacity)
    return mat, value


def flowing_energy_texture():
    """Returns:
        bpy.types.Image: A seamless cyan filament texture shared with Unity.
    """
    width, height = 1024, 512
    horizontal = np.arange(width, dtype=np.float32)[None, :] / width
    vertical = np.arange(height, dtype=np.float32)[:, None] / height
    phase = (5 * vertical + horizontal +
             0.38 * np.sin(2 * np.pi * (horizontal + vertical)) +
             0.12 * np.sin(2 * np.pi * (3 * horizontal - vertical)))
    branch = (7 * vertical + 2 * horizontal +
              0.21 * np.sin(2 * np.pi * (2 * horizontal + vertical)))
    principal = np.maximum(np.cos(2 * np.pi * phase), 0) ** 95
    secondary = np.maximum(np.cos(2 * np.pi * branch), 0) ** 125
    current = 0.45 + 0.55 * (0.5 + 0.5 * np.sin(
        2 * np.pi * (7 * horizontal + 4 * vertical)))
    alpha = np.clip((0.62 * principal + 0.22 * secondary) * current, 0, 1)
    pixels = np.empty((height, width, 4), dtype=np.float32)
    pixels[..., 0] = 0.12 + 0.35 * secondary
    pixels[..., 1] = 0.54 + 0.32 * principal
    pixels[..., 2] = 1
    pixels[..., 3] = alpha
    image = bpy.data.images.new("Shield flowing energy • shared with Unity", width, height, alpha=True)
    image.pixels.foreach_set(pixels.ravel())
    image.file_format = "PNG"
    FLOW_TEXTURE.parent.mkdir(parents=True, exist_ok=True)
    image.filepath_raw = str(FLOW_TEXTURE)
    image.save()
    return image


def opacity_keys(control, values):
    """Animate material visibility at a series of frames."""
    for frame, value in values:
        control.outputs[0].default_value = value
        control.outputs[0].keyframe_insert("default_value", frame=frame)


def sphere(name, radius, location, mat, group, segments=32, rings=16):
    """Returns:
        bpy.types.Object: A smooth UV sphere in the requested collection.
    """
    bpy.ops.mesh.primitive_uv_sphere_add(segments=segments, ring_count=rings, radius=radius, location=location)
    obj = put(bpy.context.object, group)
    obj.name = name
    obj.data.materials.append(mat)
    for face in obj.data.polygons:
        face.use_smooth = True
    return obj


def tube(name, radius, depth, location, mat, group, vertices=32):
    """Returns:
        bpy.types.Object: A cylinder used for hull parts and projectiles.
    """
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=depth, location=location)
    obj = put(bpy.context.object, group)
    obj.name = name
    obj.data.materials.append(mat)
    bevel = obj.modifiers.new("Soft machined edges", "BEVEL")
    bevel.width = min(radius * 0.22, 0.07)
    bevel.segments = 2
    obj.modifiers.new("Weighted normals", "WEIGHTED_NORMAL")
    return obj


def curve(name, points, bevel, mat, group):
    """Returns:
        bpy.types.Object: A glowing 3D polyline.
    """
    data = bpy.data.curves.new(name, "CURVE")
    data.dimensions = "3D"
    data.resolution_u = 2
    data.bevel_depth = bevel
    data.bevel_resolution = 3
    spline = data.splines.new("POLY")
    spline.points.add(len(points) - 1)
    for point, position in zip(spline.points, points):
        point.co = (*position, 1)
    obj = bpy.data.objects.new(name, data)
    group.objects.link(obj)
    data.materials.append(mat)
    return obj


def set_scale(obj, values):
    """Animate a uniform scale envelope."""
    for frame, value in values:
        obj.scale = (value,) * 3
        obj.keyframe_insert("scale", frame=frame)


def aim(obj, target):
    """Point a camera or light's negative Z axis at a target."""
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat("-Z", "Y").to_euler()


def dent_offset(angle):
    """Returns:
        float: Radial displacement of the compressed membrane at this angle.
    """
    depression = 0.84 * math.exp(-0.5 * (angle / 0.39) ** 2)
    raised_lip = 0.15 * math.exp(-0.5 * ((angle - 0.50) / 0.16) ** 2)
    return raised_lip - depression


def animate_shield_bend(obj):
    """Animate the impact dent and a damped wave over the whole membrane."""
    obj.shape_key_add(name="Undisturbed shield", from_mix=False)
    dent = obj.shape_key_add(name="Missile impact • compress and rebound", from_mix=False)
    dent.slider_min = -0.2
    for source, target in zip(obj.data.vertices, dent.data):
        direction = source.co.normalized()
        angle = math.acos(max(-1.0, min(1.0, direction.dot(NORMAL))))
        target.co = direction * (source.co.length + dent_offset(angle))
    for frame, value in [(1, 0), (41, 0), (43, 0.35), (46, 1),
                         (50, 0.88), (56, 0.32), (61, -0.12),
                         (68, 0.035), (76, 0), (96, 0)]:
        dent.value = value
        dent.keyframe_insert("value", frame=frame)
    for index, (wave_angle, peak) in enumerate(
            [(0.35, 46), (0.85, 50), (1.35, 54), (1.85, 58),
             (2.35, 62), (2.85, 66)], 1):
        wave = obj.shape_key_add(name=f"Impact wave {index} • across shield", from_mix=False)
        for source, target in zip(obj.data.vertices, wave.data):
            direction = source.co.normalized()
            angle = math.acos(max(-1.0, min(1.0, direction.dot(NORMAL))))
            gap = angle - wave_angle
            displacement = (0.24 * math.cos(gap * 6) *
                            math.exp(-0.5 * (gap / 0.58) ** 2))
            target.co = direction * (source.co.length + displacement)
        for frame, value in [(1, 0), (peak - 5, 0), (peak, 1),
                             (peak + 5, 0), (96, 0)]:
            wave.value = value
            wave.keyframe_insert("value", frame=frame)
    for frame, size in [(1, 1), (42, 1), (46, 0.97), (50, 1.025),
                        (55, 0.975), (61, 1.015), (70, 1), (96, 1)]:
        obj.scale = (size,) * 3
        obj.keyframe_insert("scale", frame=frame)


def build_scene():
    """Create ship, projectile, shield shell, and timed absorption pulses."""
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for old in list(bpy.data.collections):
        if old.name != bpy.context.scene.collection.name and old.users == 0:
            bpy.data.collections.remove(old)

    ship = collection("01 — Defender ship")
    shield = collection("02 — Shield field")
    attack = collection("03 — Incoming missile")
    impact = collection("04 — Absorption and ripples")
    stage = collection("05 — Camera and lighting")

    hull = material("Graphite ceramic hull", (0.017, 0.035, 0.07), 0.85, 0.24)
    panel = material("Gunmetal panels", (0.05, 0.10, 0.16), 0.72, 0.3)
    silver = material("Titanium edges", (0.22, 0.36, 0.44), 0.9, 0.19)
    amber = material("Engine amber", (1.0, 0.23, 0.035), 0.0, 0.25, 4)
    cold = material("Conduit cyan", (0.03, 0.75, 1.0), 0.15, 0.25, 3)

    body = sphere("Defender • sculpted fuselage", 1, (0, 0, 0), hull, ship, 48, 24)
    body.scale = (1.52, 0.66, 0.43)
    for side in (-1, 1):
        pod = sphere(f"Engine pod {side:+}", 0.27, (-0.62, side * 1.0, -0.06), panel, ship)
        pod.scale = (2.3, 0.8, 0.65)
        exhaust = sphere(f"Engine glow {side:+}", 0.15, (-1.17, side * 1.0, -0.06), amber, ship)
        exhaust.scale = (0.35, 1.0, 0.65)
        wing = tube(f"Swept stabilizer {side:+}", 0.16, 1.55, (-0.12, side * 0.65, -0.14), silver, ship, 6)
        wing.rotation_euler[0] = math.pi / 2
        wing.rotation_euler[2] = side * 0.16
    canopy = sphere("Cyan command canopy", 0.45, (0.25, 0, 0.32), cold, ship)
    canopy.scale = (1.6, 0.68, 0.32)
    for side in (-1, 1):
        curve(f"Hull energy seam {side:+}", [(-0.9, side * 0.41, 0.1), (0.2, side * 0.57, 0.04), (1.06, side * 0.22, 0)], 0.012, cold, ship)

    shell_mat, shell_opacity = glow("Shield • translucent light-blue field", (0.18, 0.67, 1.0), 0, 1.25)
    shell = sphere("Shield • spherical energy membrane", 2.5, (0, 0, 0), shell_mat, shield, 64, 32)
    animate_shield_bend(shell)
    opacity_keys(shell_opacity, [(1, 0), (42, 0), (43, 0.07), (46, 0.15),
                                 (52, 0.12), (60, 0.06), (72, 0), (96, 0)])
    flow_image = flowing_energy_texture()
    flow_mat, flow_opacity = glow("Shield • flowing cyan energy filaments", (0.08, 0.55, 1), 0, 2.0)
    nodes = flow_mat.node_tree.nodes
    links = flow_mat.node_tree.links
    mix = next(node for node in nodes if node.type == "MIX_SHADER")
    links.remove(next(link for link in links if link.to_node == mix and link.to_socket == mix.inputs[0]))
    coordinates = nodes.new("ShaderNodeTexCoord")
    mapping = nodes.new("ShaderNodeMapping")
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = flow_image
    texture.extension = "REPEAT"
    multiply = nodes.new("ShaderNodeMath")
    multiply.operation = "MULTIPLY"
    links.new(coordinates.outputs["UV"], mapping.inputs["Vector"])
    links.new(mapping.outputs["Vector"], texture.inputs["Vector"])
    links.new(flow_opacity.outputs[0], multiply.inputs[0])
    links.new(texture.outputs["Alpha"], multiply.inputs[1])
    links.new(multiply.outputs[0], mix.inputs[0])
    flow_shell = sphere("Shield • flowing energy surface", 2.515, (0, 0, 0), flow_mat, shield, 64, 32)
    animate_shield_bend(flow_shell)
    opacity_keys(flow_opacity, [(1, 0), (42, 0), (43, 0.12), (46, 0.48),
                                (52, 0.4), (60, 0.22), (72, 0), (96, 0)])
    for frame, offset in [(1, 0), (42, 0), (46, 0.015), (55, 0.085),
                          (72, 0.17), (96, 0.17)]:
        mapping.inputs["Location"].default_value = (offset, offset * 0.35, 0)
        mapping.inputs["Location"].keyframe_insert("default_value", frame=frame)

    missile_parent = bpy.data.objects.new("MISSILE • animate this flight", None)
    attack.objects.link(missile_parent)
    missile_parent.rotation_euler = Vector((0, 0, 1)).rotation_difference(NORMAL).to_euler()
    for frame, distance in [(1, 10.2), (36, 4.75), (42, 3.55), (43, 3.5)]:
        missile_parent.location = NORMAL * distance
        missile_parent.keyframe_insert("location", frame=frame)
    missile_parent.hide_render = False
    for frame, hide in [(1, False), (42, False), (43, True), (96, True)]:
        missile_parent.hide_render = hide
        missile_parent.keyframe_insert("hide_render", frame=frame)
        missile_parent.hide_viewport = hide
        missile_parent.keyframe_insert("hide_viewport", frame=frame)
    missile_body = tube("Missile • graphite casing", 0.14, 0.85, (0, 0, 0), panel, attack, 12)
    missile_body.parent = missile_parent
    missile_body.location = (0, 0, 0)
    bpy.ops.mesh.primitive_cone_add(vertices=12, radius1=0, radius2=0.14, depth=0.35)
    nose = put(bpy.context.object, attack)
    nose.name = "Missile • luminous penetrator"
    nose.data.materials.append(amber)
    nose.parent = missile_parent
    nose.location = (0, 0, -0.60)
    for angle in (0, math.pi / 2, math.pi, 3 * math.pi / 2):
        fin = sphere("Missile • stabilizer", 0.16, (0, 0, 0), silver, attack, 16, 8)
        fin.parent = missile_parent
        fin.location = (0.14 * math.cos(angle), 0.14 * math.sin(angle), 0.28)
        fin.scale = (0.22, 0.32, 1.1)
        fin.rotation_euler[2] = angle
    plume = sphere("Missile • ion exhaust", 0.17, (0, 0, 0), amber, attack)
    plume.parent = missile_parent
    plume.location = (0, 0, 0.61)
    plume.scale = (0.65, 0.65, 2.2)
    for part in missile_parent.children:
        for frame, hide in [(1, False), (42, False), (43, True), (96, True)]:
            part.hide_render = hide
            part.keyframe_insert("hide_render", frame=frame)
            part.hide_viewport = hide
            part.keyframe_insert("hide_viewport", frame=frame)

    hit = NORMAL * 2.51
    core_mat, core_opacity = glow("Impact • white hot capture", (0.65, 0.97, 1), 0, 5)
    core = sphere("Impact • energy capture point", 0.13, hit, core_mat, impact)
    set_scale(core, [(1, 0.01), (41, 0.01), (43, 2.6), (46, 1.55), (55, 0.25), (60, 0.01), (96, 0.01)])
    opacity_keys(core_opacity, [(1, 0), (41, 0), (43, 1), (48, 0.72), (60, 0), (96, 0)])
    for frame, compression in [(1, 0), (42, 0), (46, 1), (50, 0.88),
                               (56, 0.32), (61, -0.12), (76, 0), (96, 0)]:
        core.location = hit + NORMAL * dent_offset(0) * compression
        core.keyframe_insert("location", frame=frame)

    random.seed(42)
    spark_mat, spark_opacity = glow("Absorbed particles • cyan", (0.12, 0.84, 1), 0, 4)
    opacity_keys(spark_opacity, [(1, 0), (42, 0), (43, 1), (52, 0.7), (63, 0), (96, 0)])
    for index in range(26):
        angle = random.uniform(0, 2 * math.pi)
        spread = random.uniform(0.75, 1.9)
        direction = (TANGENT * math.cos(angle) + BITANGENT * math.sin(angle))
        start = hit + direction * random.uniform(0.02, 0.12)
        spark = sphere(f"Absorbed energy mote {index + 1:02}", 0.015 + random.random() * 0.025, start, spark_mat, impact, 8, 4)
        for frame, distance in [(1, 0), (43, 0), (52, spread * 0.6), (64, spread)]:
            spark.location = hit - NORMAL * distance * 0.65 + direction * distance * 0.25
            spark.keyframe_insert("location", frame=frame)

    world = bpy.data.worlds.new("Deep navy space") if not bpy.data.worlds else bpy.data.worlds[0]
    bpy.context.scene.world = world
    world.use_nodes = True
    background = next(node for node in world.node_tree.nodes if node.type == "BACKGROUND")
    background.inputs["Color"].default_value = (0.004, 0.009, 0.025, 1)
    background.inputs["Strength"].default_value = 0.35
    for name, position, color, energy, size in [
        ("Teal rim", (-4, 2, 5), (0.02, 0.5, 1), 1400, 4),
        ("Warm missile fill", (5, 0, 3), (1, 0.28, 0.09), 900, 3),
        ("Front studio key", (5, -7, 5), (0.5, 0.8, 1), 1600, 5),
    ]:
        data = bpy.data.lights.new(name, "AREA")
        data.energy = energy
        data.color = color
        data.shape = "DISK"
        data.size = size
        light = bpy.data.objects.new(name, data)
        stage.objects.link(light)
        light.location = position
        aim(light, (0, 0, 0))

    camera_data = bpy.data.cameras.new("Cinematic camera")
    camera = bpy.data.objects.new("Cinematic camera", camera_data)
    stage.objects.link(camera)
    camera.location = (8.3, -11.5, 5.3)
    aim(camera, (0.8, -0.3, 0.1))
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = 8.6
    bpy.context.scene.camera = camera

    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 32
    scene.render.resolution_x = 1920
    scene.render.resolution_y = 1080
    scene.render.resolution_percentage = 100
    scene.frame_start = 1
    scene.frame_end = 96
    scene.render.fps = FPS
    scene.frame_set(IMPACT + 10)
    scene.view_settings.view_transform = "AgX"
    scene.render.image_settings.file_format = "PNG"
    scene.render.film_transparent = False
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(OUTPUT))
    print(f"Saved shield impact scene: {OUTPUT}")


build_scene()
