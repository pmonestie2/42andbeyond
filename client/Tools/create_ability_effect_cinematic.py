"""Stage three grounded, editable ability effects around archived textured ships."""

from __future__ import annotations

import math
import random
from pathlib import Path

import bpy
from mathutils import Vector


ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / "Art/Blender"
SHIP = ROOT / "Assets/FortyTwoAndBeyond/Resources/LegacyShips/OriginalTE.obj"
BLEND = ART / "ability_effects_cinematic.blend"
ACTIVE: bpy.types.Collection | None = None


def material(name: str, color: tuple[float, float, float, float],
             strength: float = 1.0, opacity: float = 1.0) -> bpy.types.Material:
    """Returns:
        bpy.types.Material: An emission material with a soft transparent halo option.
    """
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    nodes.clear()
    output = nodes.new("ShaderNodeOutputMaterial")
    emission = nodes.new("ShaderNodeEmission")
    emission.inputs[0].default_value = color
    emission.inputs[1].default_value = strength
    if opacity < 1:
        transparent = nodes.new("ShaderNodeBsdfTransparent")
        mix = nodes.new("ShaderNodeMixShader")
        mix.inputs[0].default_value = opacity
        mat.node_tree.links.new(transparent.outputs[0], mix.inputs[1])
        mat.node_tree.links.new(emission.outputs[0], mix.inputs[2])
        mat.node_tree.links.new(mix.outputs[0], output.inputs[0])
        mat.surface_render_method = "BLENDED"
    else:
        mat.node_tree.links.new(emission.outputs[0], output.inputs[0])
    mat.diffuse_color = color
    return mat


def energy_material(name: str, dark: tuple[float, float, float, float],
                    bright: tuple[float, float, float, float],
                    strength: float) -> bpy.types.Material:
    """Returns:
        bpy.types.Material: A shaded energy strand with spatial color and intensity variation.
    """
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    nodes.clear()
    output = nodes.new("ShaderNodeOutputMaterial")
    emission = nodes.new("ShaderNodeEmission")
    coordinates = nodes.new("ShaderNodeTexCoord")
    noise = nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 5.5
    noise.inputs["Detail"].default_value = 3
    noise.inputs["Roughness"].default_value = .62
    ramp = nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].position = .28
    ramp.color_ramp.elements[0].color = dark
    ramp.color_ramp.elements[1].position = .76
    ramp.color_ramp.elements[1].color = bright
    level = nodes.new("ShaderNodeMapRange")
    level.inputs["From Min"].default_value = .25
    level.inputs["From Max"].default_value = .75
    level.inputs["To Min"].default_value = .22 * strength
    level.inputs["To Max"].default_value = strength
    links = mat.node_tree.links
    links.new(coordinates.outputs["Generated"], noise.inputs["Vector"])
    links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
    links.new(noise.outputs["Fac"], level.inputs["Value"])
    links.new(ramp.outputs["Color"], emission.inputs["Color"])
    links.new(level.outputs["Result"], emission.inputs["Strength"])
    links.new(emission.outputs[0], output.inputs[0])
    mat.diffuse_color = bright
    return mat


def link_effect(obj: bpy.types.Object) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: New effect object linked to its named render collection.
    """
    collection = ACTIVE or bpy.context.scene.collection
    collection.objects.link(obj)
    return obj


def sphere(name: str, location: tuple[float, float, float], radius: float,
           mat: bpy.types.Material, segments: int = 24) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: Smooth particle, impact point, or shield shell.
    """
    bpy.ops.mesh.primitive_uv_sphere_add(segments=segments, ring_count=max(12, segments // 2),
                                         radius=radius, location=location)
    obj = bpy.context.object
    obj.name = name
    for collection in tuple(obj.users_collection):
        collection.objects.unlink(obj)
    link_effect(obj)
    obj.data.materials.append(mat)
    for polygon in obj.data.polygons:
        polygon.use_smooth = True
    return obj


def ribbon(name: str, points: list[tuple[float, float, float]],
           mat: bpy.types.Material, width: float,
           taper: bool = True) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: Round 3D energy strand with animatable growth.
    """
    curve = bpy.data.curves.new(name, "CURVE")
    curve.dimensions = "3D"
    curve.bevel_depth = width / 2
    curve.bevel_resolution = 4
    curve.use_fill_caps = True
    spline = curve.splines.new("POLY")
    spline.points.add(len(points) - 1)
    for index, (point, xyz) in enumerate(zip(spline.points, points)):
        point.co = (*xyz, 1)
        if taper:
            t = index / max(1, len(points) - 1)
            point.radius = .10 + .9 * math.sin(math.pi * t)**.45
    obj = link_effect(bpy.data.objects.new(name, curve))
    curve.materials.append(mat)
    return obj


def cubic(a: Vector, b: Vector, c: Vector, d: Vector,
          segments: int = 72) -> list[tuple[float, float, float]]:
    """Returns:
        list[tuple[float, float, float]]: Sampled cubic path in three dimensions.
    """
    points = []
    for index in range(segments + 1):
        t = index / segments
        u = 1 - t
        p = u**3 * a + 3 * u*u*t * b + 3 * u*t*t * c + t**3 * d
        points.append(tuple(p))
    return points


def energized(points: list[tuple[float, float, float]], seed: int,
              amplitude: float) -> list[tuple[float, float, float]]:
    """Returns:
        list[tuple[float, float, float]]: Energy path with fine, deterministic spatial turbulence.
    """
    phase = seed * 1.71
    result = []
    for index, point in enumerate(points):
        t = index / max(1, len(points) - 1)
        fade = math.sin(math.pi * t)**.7
        wobble = (math.sin(19 * t + phase) * .52
                  + math.sin(47 * t + phase * 1.8) * .31
                  + math.sin(91 * t + phase * .6) * .17)
        twist = (math.cos(23 * t + phase * .8) * .6
                 + math.cos(71 * t + phase) * .4)
        result.append((point[0], point[1] + amplitude * fade * wobble,
                       point[2] + amplitude * fade * twist))
    return result


def grow(obj: bpy.types.Object, start: int, end: int) -> None:
    """Grow one energy strand through the shot before it clears."""
    for frame, value in ((1, 0), (start, .015), (end, 1), (38, 1), (48, 0)):
        obj.data.bevel_factor_end = value
        obj.data.keyframe_insert(data_path="bevel_factor_end", frame=frame)


def bead(name: str, points: list[tuple[float, float, float]],
         mat: bpy.types.Material, start: int, end: int,
         radius: float = .065) -> None:
    """Move one small packet along a strand for a readable flow direction."""
    obj = sphere(name, points[0], radius, mat, 16)
    for frame, index in ((1, 0), (start, 0), (end, len(points) - 1),
                         (48, len(points) - 1)):
        obj.location = points[index]
        obj.keyframe_insert(data_path="location", frame=frame)
    for frame, scale in ((1, .1), (start, 1), (end, 1), (48, .1)):
        obj.scale = (scale,) * 3
        obj.keyframe_insert(data_path="scale", frame=frame)


def light(name: str, location: tuple[float, float, float], color: tuple[float, float, float],
          power: float, radius: float, collection: bpy.types.Collection | None = None) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: Area or point light that makes energy affect the hull.
    """
    data = bpy.data.lights.new(name, "POINT")
    data.energy = power
    data.color = color
    data.shadow_soft_size = radius
    obj = bpy.data.objects.new(name, data)
    (collection or bpy.context.scene.collection).objects.link(obj)
    obj.location = location
    return obj


def textured_ship(name: str, location: tuple[float, float, float],
                  facing_right: bool, scale: float = .041) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: Archived TE hull with intact textures and no decal planes.
    """
    before = set(bpy.data.objects)
    bpy.ops.wm.obj_import(filepath=str(SHIP))
    imported = [obj for obj in bpy.context.selected_objects if obj not in before and obj.type == "MESH"]
    points = [obj.matrix_world @ Vector(corner) for obj in imported for corner in obj.bound_box]
    center = Vector(tuple((min(p[axis] for p in points) + max(p[axis] for p in points)) / 2
                          for axis in range(3)))
    root = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(root)
    root.location = location
    root.rotation_euler.z = math.pi if facing_right else 0
    for obj in imported:
        if "DECAL" in obj.name.upper() or obj.name.startswith("LF"):
            bpy.data.objects.remove(obj, do_unlink=True)
            continue
        mesh = obj.data.copy()
        for vertex in mesh.vertices:
            vertex.co = (obj.matrix_world @ vertex.co - center) * scale
        obj.data = mesh
        obj.matrix_world.identity()
        obj.parent = root
        obj.name = f"{name} / {obj.name}"
        for mat in mesh.materials:
            if mat is None or not mat.use_nodes:
                continue
            bsdf = next((node for node in mat.node_tree.nodes
                         if node.type == "BSDF_PRINCIPLED"), None)
            if bsdf is None:
                continue
            bsdf.inputs["Metallic"].default_value = .53
            bsdf.inputs["Roughness"].default_value = .38
            base = bsdf.inputs["Base Color"]
            links = [item for item in mat.node_tree.links if item.to_socket == base]
            if links and not any(node.type == "HUE_SAT" for node in mat.node_tree.nodes):
                source = links[0].from_socket
                hue = mat.node_tree.nodes.new("ShaderNodeHueSaturation")
                hue.inputs["Value"].default_value = .52
                mat.node_tree.links.new(source, hue.inputs["Color"])
                mat.node_tree.links.new(hue.outputs["Color"], base)
    return root


def shield_material() -> bpy.types.Material:
    """Returns:
        bpy.types.Material: Mostly transparent shield with a soft Fresnel edge.
    """
    mat = bpy.data.materials.new("Shield / refractive cyan edge")
    mat.use_nodes = True
    mat.surface_render_method = "BLENDED"
    nodes = mat.node_tree.nodes
    nodes.clear()
    output = nodes.new("ShaderNodeOutputMaterial")
    transparent = nodes.new("ShaderNodeBsdfTransparent")
    emission = nodes.new("ShaderNodeEmission")
    emission.inputs[0].default_value = (.13, .55, .85, 1)
    emission.inputs[1].default_value = 1.3
    layer = nodes.new("ShaderNodeLayerWeight")
    layer.inputs[0].default_value = .17
    multiply = nodes.new("ShaderNodeMath")
    multiply.operation = "MULTIPLY"
    multiply.inputs[1].default_value = .10
    mix = nodes.new("ShaderNodeMixShader")
    links = mat.node_tree.links
    links.new(layer.outputs["Fresnel"], multiply.inputs[0])
    links.new(multiply.outputs[0], mix.inputs[0])
    links.new(transparent.outputs[0], mix.inputs[1])
    links.new(emission.outputs[0], mix.inputs[2])
    links.new(mix.outputs[0], output.inputs[0])
    return mat


def shield_shell(target: tuple[float, float, float]) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: Transparent sphere with pull and burn impact shape keys.
    """
    shell = sphere("Target shield / animated membrane", target, 2.7,
                   shield_material(), 64)
    shell.shape_key_add(name="Rest")
    for name, direction in (("Sap pull", -1), ("Burn indentation", 1)):
        key = shell.shape_key_add(name=name)
        for vertex in key.data:
            p = vertex.co
            if p.x < -1.35:
                falloff = math.exp(-((p.y / .93)**2 + (p.z / .93)**2) * 1.8)
                p.x += direction * .55 * falloff
    return shell


def create_sap(caster: Vector, target: Vector,
               mats: dict[str, bpy.types.Material], shell: bpy.types.Object) -> bpy.types.Collection:
    """Returns:
        bpy.types.Collection: Thin cyan energy torn off the target and drawn to the caster.
    """
    global ACTIVE
    ACTIVE = bpy.data.collections.new("01 Shield Sap / vacuum")
    bpy.context.scene.collection.children.link(ACTIVE)
    shell.data.shape_keys.key_blocks["Sap pull"].value = 1
    origin = target + Vector((-2.48, 0, .15))
    destination = caster + Vector((.05, 0, .62))
    for index, offset in enumerate((-.49, -.11, .34)):
        a = origin + Vector((.10, offset, offset * .40))
        b = origin + Vector((-1.5, offset * 1.1, .9 + offset * .3))
        c = destination + Vector((1.6, offset * .55, .85))
        d = destination + Vector((0, offset * .16, 0))
        points = energized(cubic(a, b, c, d), index + 5, .11)
        grow(ribbon(f"Sap faint sheath {index}", points, mats["sap_haze"], .045),
             3 + index, 19 + index)
        grow(ribbon(f"Sap filament {index}", points, mats["sap"], .010),
             4 + index, 20 + index)
        bead(f"Sap drained shield fragment {index}", points,
             mats["sap_hot"], 4 + index, 20 + index, .014)
    for index in range(7):
        randomizer = random.Random(90 + index)
        angle = randomizer.uniform(0, math.tau)
        pos = origin + Vector((randomizer.uniform(-.28, .15),
                               .76 * math.cos(angle), .76 * math.sin(angle)))
        particle = sphere(f"Sap membrane fleck {index}", pos,
                          randomizer.uniform(.009, .021), mats["sap_hot"], 12)
        particle.keyframe_insert(data_path="location", frame=1)
        particle.location = destination + Vector((randomizer.uniform(-.2, .2),
                                                 randomizer.uniform(-.2, .2),
                                                 randomizer.uniform(-.2, .2)))
        particle.keyframe_insert(data_path="location", frame=24 + index % 6)
    glow = sphere("Shield Sap pinched contact", origin, .12, mats["sap_haze"], 24)
    glow.scale = (1, 1, 1)
    light("Shield Sap cyan bounce", tuple(origin), (.10, .75, 1), 250, 1.8, ACTIVE)
    light("Shield Sap core bounce", tuple(destination), (.15, .8, 1), 340, 2, ACTIVE)
    ACTIVE = None
    return bpy.data.collections["01 Shield Sap / vacuum"]


def create_turbo(caster: Vector,
                 mats: dict[str, bpy.types.Material]) -> bpy.types.Collection:
    """Returns:
        bpy.types.Collection: Three thin red bands converging on a humming hull core.
    """
    global ACTIVE
    ACTIVE = bpy.data.collections.new("02 Turbo / three red bands")
    bpy.context.scene.collection.children.link(ACTIVE)
    heart = caster + Vector((0, 0, .76))
    for index, offset in enumerate((-.95, 0, .95)):
        a = caster + Vector((-2.7, offset * 1.4, .55))
        b = caster + Vector((-1.5, offset * 1.2, 1.08))
        c = caster + Vector((-.53, offset * .55, 1.12))
        d = heart + Vector((0, offset * .09, 0))
        points = energized(cubic(a, b, c, d), index + 17, .035)
        grow(ribbon(f"Turbo atmospheric halo {index}", points,
                    mats["turbo_haze"], .075), 3 + index * 2, 21 + index * 2)
        grow(ribbon(f"Turbo rounded stripe {index}", points,
                    mats["turbo"], .032), 4 + index * 2, 22 + index * 2)
        bead(f"Turbo stripe head {index}", points,
             mats["turbo_hot"], 4 + index * 2, 22 + index * 2, .045)
    core = sphere("Turbo humming heart", heart, .085, mats["turbo_hot"], 24)
    for frame, scale in ((1, .2), (14, .75), (24, 1.35), (34, 1.1), (48, .2)):
        core.scale = (scale,) * 3
        core.keyframe_insert(data_path="scale", frame=frame)
    for index, radius in enumerate((.45, .67, .92)):
        points = [(heart.x + radius * math.cos(math.tau * step / 70),
                   heart.y + radius * math.sin(math.tau * step / 70),
                   heart.z + index * .04) for step in range(71)]
        ring = ribbon(f"Turbo low hum ripple {index}", points,
                      mats["turbo_haze"], .04, taper=False)
        for frame, scale in ((1, .25), (16, .62), (29, 1), (43, 1.22), (48, .25)):
            ring.scale = (scale,) * 3
            # Curves contain world points; preserve the heart as scaling origin.
            ring.location = heart * (1 - scale)
            ring.keyframe_insert(data_path="scale", frame=frame)
            ring.keyframe_insert(data_path="location", frame=frame)
    light("Turbo red hull bounce", tuple(heart), (1, .035, .025), 350, 2.3, ACTIVE)
    ACTIVE = None
    return bpy.data.collections["02 Turbo / three red bands"]


def create_burn(caster: Vector, target: Vector,
                mats: dict[str, bpy.types.Material], shell: bpy.types.Object) -> bpy.types.Collection:
    """Returns:
        bpy.types.Collection: Coherent hot beam and a burning shield impact patch.
    """
    global ACTIVE
    ACTIVE = bpy.data.collections.new("03 Shield Burn / hostile contact")
    bpy.context.scene.collection.children.link(ACTIVE)
    shell.data.shape_keys.key_blocks["Sap pull"].value = 0
    shell.data.shape_keys.key_blocks["Burn indentation"].value = 1
    source = caster + Vector((2.35, 0, .30))
    hit = target + Vector((-2.65, 0, .12))
    for index, offset in enumerate((-.22, 0, .21)):
        points = energized(cubic(source + Vector((0, offset, offset * .25)),
                       source + Vector((.85, offset * 1.5, .26)),
                       hit + Vector((-.82, offset * .8, .35)),
                       hit + Vector((0, offset * .16, 0))), index + 41, .08)
        grow(ribbon(f"Shield Burn plasma haze {index}", points,
                    mats["burn_haze"], .075 if index == 1 else .04),
             2 + index, 16 + index)
        grow(ribbon(f"Shield Burn filament {index}", points,
                    mats["burn_hot" if index == 1 else "burn"],
                    .020 if index == 1 else .010), 3 + index, 17 + index)
        bead(f"Shield Burn packet {index}", points,
             mats["burn_hot"], 3 + index, 17 + index, .035)
    impact = sphere("Shield Burn molten contact", hit, .12,
                    mats["burn_hot"], 24)
    for frame, scale in ((1, .1), (16, .35), (23, 1.4), (39, 1), (48, .1)):
        impact.scale = (scale,) * 3
        impact.keyframe_insert(data_path="scale", frame=frame)
    randomizer = random.Random(42)
    for index in range(10):
        a = randomizer.uniform(math.pi * .60, math.pi * 1.40)
        center = target + Vector((2.7 * math.cos(a),
                                  2.7 * math.sin(a),
                                  randomizer.uniform(-1.1, 1.1)))
        patch = sphere(f"Shield Burn heated cell {index}", center,
                       randomizer.uniform(.035, .10),
                       mats["burn"] if index % 3 else mats["burn_hot"], 12)
        patch.scale = (.1,) * 3
        patch.keyframe_insert(data_path="scale", frame=1)
        patch.scale = (1,) * 3
        patch.keyframe_insert(data_path="scale", frame=22 + index % 8)
    light("Shield Burn orange impact bounce", tuple(hit), (1, .21, .025),
          700, 2.0, ACTIVE)
    ACTIVE = None
    return bpy.data.collections["03 Shield Burn / hostile contact"]


def camera(name: str, position: tuple[float, float, float],
           target: tuple[float, float, float], lens: float) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: Cinematic camera framing one ability shot.
    """
    data = bpy.data.cameras.new(name)
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = position
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat("-Z", "Y").to_euler()
    data.lens = lens
    return obj


def background(scene: bpy.types.Scene) -> None:
    """Light the textured hulls against a restrained deep-space background."""
    scene.world = bpy.data.worlds.new("Deep-space ambient")
    scene.world.use_nodes = True
    backdrop = next(node for node in scene.world.node_tree.nodes
                    if node.type == "BACKGROUND")
    backdrop.inputs[0].default_value = (.008, .016, .035, 1)
    backdrop.inputs[1].default_value = .3
    randomizer = random.Random(11)
    star_mat = material("Distant tiny stars", (.43, .64, .85, 1), .7)
    global ACTIVE
    ACTIVE = bpy.data.collections.new("Distant stars")
    scene.collection.children.link(ACTIVE)
    for index in range(95):
        position = (randomizer.uniform(-13, 13),
                    randomizer.uniform(5, 11),
                    randomizer.uniform(-5, 6))
        sphere(f"Distant star {index}", position,
               randomizer.uniform(.012, .045), star_mat, 8)
    ACTIVE = None
    light("Cool broad hull key", (-3, -4, 8), (.44, .72, 1), 1800, 5)
    light("Warm rear rim", (5, 4, 6), (1, .43, .25), 1700, 4)
    light("Blue side fill", (0, -8, 1), (.24, .49, .92), 900, 6)


def render_variant(scene: bpy.types.Scene, name: str, collection: bpy.types.Collection,
                   shot: bpy.types.Object, frame: int,
                   shell: bpy.types.Object, sap: bool, burn: bool,
                   target_ship: bpy.types.Object) -> None:
    """Render one isolated animated effect without changing the other shots."""
    for candidate in ("01 Shield Sap / vacuum", "02 Turbo / three red bands",
                      "03 Shield Burn / hostile contact"):
        bpy.data.collections[candidate].hide_render = candidate != collection.name
    shell.hide_render = collection.name == "02 Turbo / three red bands"
    target_ship.hide_render = collection.name == "02 Turbo / three red bands"
    shell.data.shape_keys.key_blocks["Sap pull"].value = int(sap)
    shell.data.shape_keys.key_blocks["Burn indentation"].value = int(burn)
    scene.camera = shot
    scene.frame_set(frame)
    scene.render.filepath = str(ART / name)
    bpy.ops.render.render(write_still=True)
    print(f"Rendered {name}")


def build() -> None:
    """Build the realistic pass and save all effects as a reusable Blender file."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.name = "42 and Beyond Cinematic Abilities"
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = 1400
    scene.render.resolution_y = 840
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.frame_start = 1
    scene.frame_end = 48
    scene.render.fps = 24
    scene.view_settings.view_transform = "AgX"
    scene.view_settings.look = "AgX - Medium High Contrast"
    background(scene)
    caster_position = Vector((-4, 0, 0))
    target_position = Vector((4, 0, 0))
    textured_ship("Caster / textured TE hull", tuple(caster_position), True)
    target_ship = textured_ship("Target / textured TE hull", tuple(target_position), False)
    shell = shield_shell(tuple(target_position))
    mats = {
        "sap": energy_material("Sap / blue-white ionized filament",
                               (.018, .23, .34, 1), (.42, .88, 1, 1), 2.3),
        "sap_hot": material("Sap / bright moving charge", (.70, .96, 1, 1), 2.5),
        "sap_haze": material("Sap / transparent suction haze", (.11, .52, .88, 1), 1.7, .08),
        "turbo": energy_material("Turbo / red to amber energized strand",
                                 (.28, .008, .012, 1), (1, .17, .055, 1), 2.8),
        "turbo_hot": material("Turbo / warm heart", (1, .29, .14, 1), 5),
        "turbo_haze": material("Turbo / diffuse red hum", (.88, .035, .035, 1), 1.7, .08),
        "burn": energy_material("Burn / heated orange plasma",
                                (.33, .045, .006, 1), (1, .50, .12, 1), 3),
        "burn_hot": material("Burn / white hot packet", (1, .70, .24, 1), 5),
        "burn_haze": material("Burn / translucent heat sheath", (1, .12, .015, 1), 1.8, .08),
    }
    sap_collection = create_sap(caster_position, target_position, mats, shell)
    turbo_collection = create_turbo(caster_position, mats)
    burn_collection = create_burn(caster_position, target_position, mats, shell)
    sap_camera = camera("01 Shield Sap / camera", (0, -15, 8),
                        (0, 0, .2), 40)
    turbo_camera = camera("02 Turbo / camera", (-4, -8, 5.6),
                          (-4, 0, .2), 55)
    burn_camera = camera("03 Shield Burn / camera", (0, -15, 8),
                         (0, 0, .2), 40)
    ART.mkdir(parents=True, exist_ok=True)
    render_variant(scene, "shield_sap_cinematic.png", sap_collection, sap_camera,
                   24, shell, True, False, target_ship)
    render_variant(scene, "turbo_cinematic.png", turbo_collection, turbo_camera,
                   24, shell, False, False, target_ship)
    render_variant(scene, "shield_burn_cinematic.png", burn_collection, burn_camera,
                   24, shell, False, True, target_ship)
    render_variant(scene, "shield_sap_cinematic_onset.png", sap_collection, sap_camera,
                   8, shell, True, False, target_ship)
    for effect in (sap_collection, turbo_collection, burn_collection):
        effect.hide_render = effect != sap_collection
        effect.hide_viewport = effect != sap_collection
    shell.hide_render = False
    target_ship.hide_render = False
    shell.data.shape_keys.key_blocks["Sap pull"].value = 1
    shell.data.shape_keys.key_blocks["Burn indentation"].value = 0
    scene.camera = sap_camera
    scene.frame_set(24)
    scene.render.filepath = str(ART / "shield_sap_cinematic.png")
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type == "VIEW_3D":
                area.spaces.active.region_3d.view_perspective = "CAMERA"
                area.spaces.active.shading.type = "MATERIAL"
    bpy.ops.file.pack_all()
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
    print(f"Editable cinematic scene: {BLEND}")


if __name__ == "__main__":
    build()
