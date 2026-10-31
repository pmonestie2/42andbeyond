"""Stage an editable JE departure, folded-space transit, and distant arrival in Blender."""
from __future__ import annotations
import math
import random
from pathlib import Path
import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / 'Art/Blender'
PROMPT = 'JE ship enters a wormhole and exits at a different location.'


def mesh_object(name: str, vertices: list, faces: list, material: bpy.types.Material) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: A smooth editable effect mesh in the active preview scene.
    """
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    mesh.materials.append(material)
    for face in mesh.polygons:
        face.use_smooth = True
    return obj


def emission(name: str, color: tuple, strength: float) -> bpy.types.Material:
    """Returns:
        bpy.types.Material: An emissive material with real node colors.
    """
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    nodes.clear()
    output = nodes.new('ShaderNodeOutputMaterial')
    light = nodes.new('ShaderNodeEmission')
    light.inputs[0].default_value = (*color, 1)
    light.inputs[1].default_value = strength
    material.node_tree.links.new(light.outputs[0], output.inputs[0])
    return material


def energy_surface(name: str, intensity: float, opacity: float = 1) -> bpy.types.Material:
    """Returns:
        bpy.types.Material: Flowing blue-violet plasma with fine, uneven luminous density.
    """
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    nodes, links = material.node_tree.nodes, material.node_tree.links
    nodes.clear()
    output = nodes.new('ShaderNodeOutputMaterial')
    coordinate = nodes.new('ShaderNodeTexCoord')
    noise = nodes.new('ShaderNodeTexNoise')
    noise.inputs['Scale'].default_value = 3.1
    noise.inputs['Detail'].default_value = 5
    noise.inputs['Roughness'].default_value = .72
    links.new(coordinate.outputs['Object'], noise.inputs['Vector'])
    # Animate a real spatial translation, retaining an editable shader graph.
    mapping = nodes.new('ShaderNodeMapping')
    links.new(coordinate.outputs['Object'], mapping.inputs['Vector'])
    links.new(mapping.outputs['Vector'], noise.inputs['Vector'])
    for frame, offset in [(1, 0), (240, 9)]:
        mapping.inputs['Location'].default_value = (offset, offset * .37, offset * -.23)
        mapping.inputs['Location'].keyframe_insert('default_value', frame=frame)
    ramp = nodes.new('ShaderNodeValToRGB')
    low, high = ramp.color_ramp.elements
    low.position = .28; low.color = (.002, .008, .035, 1)
    high.position = .78; high.color = (.12, .55, 1, 1)
    middle = ramp.color_ramp.elements.new(.51); middle.color = (.035, .13, .42, 1)
    violet = ramp.color_ramp.elements.new(.64); violet.color = (.24, .16, .7, 1)
    links.new(noise.outputs['Fac'], ramp.inputs[0])
    light = nodes.new('ShaderNodeEmission')
    light.inputs['Strength'].default_value = intensity
    links.new(ramp.outputs['Color'], light.inputs['Color'])
    if opacity < 1:
        transparent = nodes.new('ShaderNodeBsdfTransparent')
        mix = nodes.new('ShaderNodeMixShader')
        mix.inputs[0].default_value = opacity
        links.new(transparent.outputs[0], mix.inputs[1]); links.new(light.outputs[0], mix.inputs[2])
        links.new(mix.outputs[0], output.inputs[0])
        methods = [item.identifier for item in material.bl_rna.properties['surface_render_method'].enum_items]
        material.surface_render_method = next(method for method in methods if method == 'BLENDED')
    else:
        links.new(light.outputs[0], output.inputs[0])
    return material


def curve_object(name: str, points: list, width: float, material: bpy.types.Material) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: A soft, shaded strand with an editable three-dimensional path.
    """
    curve = bpy.data.curves.new(name, 'CURVE')
    curve.dimensions = next(item.identifier for item in curve.bl_rna.properties['dimensions'].enum_items if item.identifier == '3D'); curve.bevel_depth = width; curve.bevel_resolution = 3
    spline_kind = next(item.identifier for item in curve.splines.bl_rna.functions['new'].parameters['type'].enum_items if item.identifier == 'POLY')
    spline = curve.splines.new(spline_kind); spline.points.add(len(points) - 1)
    for index, (point, position) in enumerate(zip(spline.points, points)):
        point.co = (*position, 1)
        point.radius = .12 + .88 * math.sin(math.pi * index / max(1, len(points) - 1)) ** .35
    obj = bpy.data.objects.new(name, curve); bpy.context.scene.collection.objects.link(obj)
    curve.materials.append(material)
    return obj


def portal(name: str, location: tuple, bright: bpy.types.Material, veil: bpy.types.Material) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: A turbulent aperture and receding throat, all under one animatable root.
    """
    root = bpy.data.objects.new(name, None); bpy.context.scene.collection.objects.link(root)
    root.location = location
    vertices, faces = [], []
    segments, sides = 180, 14
    for ring in range(segments):
        angle = math.tau * ring / segments
        radius = 3.3 + .055 * math.sin(angle * 7) + .035 * math.sin(angle * 13)
        tube = .16 + .065 * math.sin(angle * 5 + 1) ** 2
        for side in range(sides):
            around = math.tau * side / sides
            radial = radius + tube * math.cos(around)
            vertices.append((radial * math.cos(angle), tube * math.sin(around), radial * math.sin(angle)))
    for ring in range(segments):
        for side in range(sides):
            a = ring * sides + side; b = ((ring + 1) % segments) * sides + side
            faces.append((a, b, ((ring + 1) % segments) * sides + (side + 1) % sides, ring * sides + (side + 1) % sides))
    rim = mesh_object(name + ' • turbulent plasma lip', vertices, faces, bright); rim.parent = root
    # A genuinely recessed throat gives the aperture depth rather than a flat glowing disk.
    vertices, faces = [], []
    for ring in range(46):
        t = ring / 45; radius = 3.24 * (1 - t) ** 1.35 + .18
        for index in range(160):
            angle = math.tau * index / 160 + t * 3.8
            waviness = .025 * math.sin(angle * 9 + t * 18)
            vertices.append(((radius + waviness) * math.cos(angle), .1 + t * 7, (radius + waviness) * math.sin(angle)))
    for ring in range(45):
        for index in range(160):
            a = ring * 160 + index; b = ring * 160 + (index + 1) % 160
            faces.append((a, b, b + 160, a + 160))
    throat = mesh_object(name + ' • folded-space throat', vertices, faces, veil); throat.parent = root
    rng = random.Random(741)
    for strand in range(10):
        phase = rng.uniform(0, math.tau); span = rng.uniform(.7, 2.9)
        base = rng.uniform(3.18, 3.65)
        points = []
        for index in range(88):
            t = index / 87; angle = phase + t * span
            radius = base + .10 * math.sin(angle * 8 + strand) + .09 * t
            points.append((radius * math.cos(angle), -.06 + .12 * math.sin(angle * 4), radius * math.sin(angle)))
        strand_obj = curve_object(name + f' • shearing filament {strand:02}', points, rng.uniform(.008, .027), bright)
        strand_obj.parent = root
    for frame, angle in [(1, 0), (240, -.8)]:
        root.rotation_euler[1] = angle; root.keyframe_insert('rotation_euler', frame=frame)
    lens = bpy.data.materials.new(name + ' • refracting spacetime'); lens.use_nodes = True
    nodes, links = lens.node_tree.nodes, lens.node_tree.links
    surface = next(node for node in nodes if node.type == 'BSDF_PRINCIPLED')
    surface.inputs['Base Color'].default_value = (.7, .84, 1, 1)
    surface.inputs['Transmission Weight'].default_value = 1
    surface.inputs['Roughness'].default_value = .035; surface.inputs['IOR'].default_value = 1.13
    noise = nodes.new('ShaderNodeTexNoise'); noise.inputs['Scale'].default_value = 3.5; noise.inputs['Detail'].default_value = 2
    bump = nodes.new('ShaderNodeBump'); bump.inputs['Strength'].default_value = .18; bump.inputs['Distance'].default_value = .09
    links.new(noise.outputs['Fac'], bump.inputs['Height']); links.new(bump.outputs['Normal'], surface.inputs['Normal'])
    bpy.ops.mesh.primitive_uv_sphere_add(segments=72, ring_count=36, radius=1)
    lens_obj = bpy.context.object; lens_obj.name = name + ' • gravitational lens'
    lens_obj.parent = root; lens_obj.location = (0, .36, 0); lens_obj.scale = (3.14, .28, 3.14)
    lens_obj.data.materials.append(lens)
    for face in lens_obj.data.polygons: face.use_smooth = True
    return root


def set_scale_keys(obj: bpy.types.Object, keys: list) -> None:
    """Animate the aperture opening and sealing without deleting any effect geometry."""
    for frame, scale in keys:
        obj.scale = (scale,) * 3; obj.keyframe_insert('scale', frame=frame)


def camera(name: str, location: tuple, target: tuple, lens: float) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: A camera aimed at a specified shot, ready for a timeline camera cut.
    """
    data = bpy.data.cameras.new(name); data.lens = lens; data.clip_end = 1000
    obj = bpy.data.objects.new(name, data); bpy.context.scene.collection.objects.link(obj)
    obj.location = location; obj.rotation_euler = (Vector(target) - obj.location).to_track_quat('-Z', 'Y').to_euler()
    return obj


def light(name: str, location: tuple, target: tuple, color: tuple, power: float, size: float) -> None:
    """Add a broad light that reveals the textured hull and the portal's silhouette."""
    kinds = [item.identifier for item in bpy.types.Light.bl_rna.properties['type'].enum_items]
    data = bpy.data.lights.new(name, next(kind for kind in kinds if kind == 'AREA'))
    data.color = color; data.energy = power; data.shape = next(item.identifier for item in data.bl_rna.properties['shape'].enum_items if item.identifier == 'DISK'); data.size = size
    obj = bpy.data.objects.new(name, data); bpy.context.scene.collection.objects.link(obj); obj.location = location
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat('-Z', 'Y').to_euler()


def clone_ship(source: bpy.types.Object) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: The actual JE and copied opaque materials, preserving the cloak preview.
    """
    root = source.copy(); root.animation_data_clear(); root.name = 'JE • single travelling hull'
    bpy.context.scene.collection.objects.link(root); root['cloak'] = 0
    materials = {}
    for child in source.children:
        obj = child.copy(); obj.data = child.data.copy(); obj.animation_data_clear()
        bpy.context.scene.collection.objects.link(obj); obj.parent = root
        for slot in obj.material_slots:
            original = slot.material
            if original is None: continue
            if original not in materials:
                material = original.copy(); material.name = 'WH hull • ' + original.name
                if material.node_tree.animation_data:
                    for driver in material.node_tree.animation_data.drivers:
                        for variable in driver.driver.variables:
                            for target in variable.targets:
                                if target.id == source: target.id = root
                materials[original] = material
            slot.material = materials[original]
    root.scale = (.032,) * 3
    return root


def stars() -> None:
    """Build one inexpensive distant star mesh, with a deterministic spatial distribution."""
    rng = random.Random(919)
    vertices, faces = [], []
    for _ in range(1100):
        x, y, z = rng.uniform(-110, 110), rng.uniform(45, 70), rng.uniform(-55, 80)
        radius = rng.choice([.018, .022, .028, .045, .08])
        offset = len(vertices)
        vertices.extend([(x - radius, y, z), (x, y, z + radius), (x + radius, y, z), (x, y, z - radius)])
        faces.append(tuple(range(offset, offset + 4)))
    mesh_object('Far field • 1100 fine stars', vertices, faces, emission('Stars • cool white', (.65, .8, 1), 2.4))


def planet() -> None:
    """Add a warm destination planet so arrival is visibly in another part of space."""
    material = bpy.data.materials.new('Destination • copper clouds and basalt'); material.use_nodes = True
    nodes, links = material.node_tree.nodes, material.node_tree.links
    surface = next(node for node in nodes if node.type == 'BSDF_PRINCIPLED')
    noise = nodes.new('ShaderNodeTexNoise'); noise.inputs['Scale'].default_value = 4; noise.inputs['Detail'].default_value = 6
    ramp = nodes.new('ShaderNodeValToRGB'); ramp.color_ramp.elements[0].color = (.012, .016, .028, 1); ramp.color_ramp.elements[1].color = (.45, .14, .035, 1)
    links.new(noise.outputs['Fac'], ramp.inputs[0]); links.new(ramp.outputs[0], surface.inputs['Base Color'])
    surface.inputs['Roughness'].default_value = .86
    bpy.ops.mesh.primitive_uv_sphere_add(segments=80, ring_count=40, radius=5.5, location=(2, 19, 7))
    obj = bpy.context.object; obj.name = 'Arrival landmark • copper world'; obj.data.materials.append(material)
    for face in obj.data.polygons: face.use_smooth = True
    glow = bpy.data.materials.new('Destination • thin amber atmosphere'); glow.use_nodes = True
    nodes, links = glow.node_tree.nodes, glow.node_tree.links; nodes.clear()
    out = nodes.new('ShaderNodeOutputMaterial'); transparent = nodes.new('ShaderNodeBsdfTransparent')
    fresnel = nodes.new('ShaderNodeLayerWeight'); fresnel.inputs['Blend'].default_value = .4
    power = nodes.new('ShaderNodeMath'); power.operation = next(item.identifier for item in power.bl_rna.properties['operation'].enum_items if item.identifier == 'POWER'); power.inputs[1].default_value = 4
    links.new(fresnel.outputs['Fresnel'], power.inputs[0])
    lit = nodes.new('ShaderNodeEmission'); lit.inputs[0].default_value = (.6, .2, .035, 1); lit.inputs[1].default_value = 2
    mix = nodes.new('ShaderNodeMixShader'); links.new(power.outputs[0], mix.inputs[0]); links.new(transparent.outputs[0], mix.inputs[1]); links.new(lit.outputs[0], mix.inputs[2]); links.new(mix.outputs[0], out.inputs[0])
    methods = [item.identifier for item in glow.bl_rna.properties['surface_render_method'].enum_items]; glow.surface_render_method = next(value for value in methods if value == 'BLENDED')
    halo = obj.copy(); halo.data = obj.data.copy(); bpy.context.scene.collection.objects.link(halo); halo.name = 'Arrival landmark • atmosphere'; halo.scale = (1.015,) * 3; halo.data.materials.clear(); halo.data.materials.append(glow)


def refine_surfaces(scene: bpy.types.Scene) -> None:
    """Replace the draft aperture with detailed plasma, a spiral throat and a soft volumetric fringe."""
    rim_material = bpy.data.materials.get('WH • satin plasma and microturbulence') or bpy.data.materials.new('WH • satin plasma and microturbulence')
    rim_material.use_nodes = True
    nodes, links = rim_material.node_tree.nodes, rim_material.node_tree.links
    nodes.clear()
    out = nodes.new('ShaderNodeOutputMaterial'); surface = nodes.new('ShaderNodeBsdfPrincipled')
    coordinates = nodes.new('ShaderNodeTexCoord')
    macro = nodes.new('ShaderNodeTexNoise'); macro.noise_dimensions = next(item.identifier for item in macro.bl_rna.properties['noise_dimensions'].enum_items if item.identifier == '4D')
    macro.inputs['Scale'].default_value = 2.5; macro.inputs['Detail'].default_value = 5; macro.inputs['Roughness'].default_value = .72
    links.new(coordinates.outputs['Object'], macro.inputs['Vector'])
    for frame, value in [(1, 0), (240, 4)]:
        macro.inputs['W'].default_value = value; macro.inputs['W'].keyframe_insert('default_value', frame=frame)
    ramp = nodes.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements.remove(ramp.color_ramp.elements[1])
    for index, (position, color) in enumerate([(.22, (.001, .003, .009, 1)), (.46, (.004, .025, .065, 1)), (.58, (.015, .13, .23, 1)), (.69, (.1, .55, .8, 1)), (.81, (.7, .9, 1, 1))]):
        element = ramp.color_ramp.elements[0] if index == 0 else ramp.color_ramp.elements.new(position)
        element.position = position; element.color = color
    links.new(macro.outputs['Fac'], ramp.inputs[0]); links.new(ramp.outputs[0], surface.inputs['Base Color']); links.new(ramp.outputs[0], surface.inputs['Emission Color'])
    surface.inputs['Metallic'].default_value = .35; surface.inputs['Roughness'].default_value = .26
    surface.inputs['Coat Weight'].default_value = .3; surface.inputs['Emission Strength'].default_value = 2.4
    grain = nodes.new('ShaderNodeTexNoise'); grain.inputs['Scale'].default_value = 62; grain.inputs['Detail'].default_value = 3
    links.new(coordinates.outputs['Object'], grain.inputs['Vector'])
    bump = nodes.new('ShaderNodeBump'); bump.inputs['Strength'].default_value = .24; bump.inputs['Distance'].default_value = .026
    links.new(grain.outputs['Fac'], bump.inputs['Height']); links.new(bump.outputs[0], surface.inputs['Normal']); links.new(surface.outputs[0], out.inputs[0])
    # Fine moving helices follow actual throat UV depth, revealing the recessed geometry.
    throat_material = bpy.data.materials.get('WH • spiral depth field') or bpy.data.materials.new('WH • spiral depth field')
    throat_material.use_nodes = True; nodes, links = throat_material.node_tree.nodes, throat_material.node_tree.links; nodes.clear()
    out = nodes.new('ShaderNodeOutputMaterial'); uv = nodes.new('ShaderNodeTexCoord'); separate = nodes.new('ShaderNodeSeparateXYZ'); links.new(uv.outputs['UV'], separate.inputs[0])
    around = nodes.new('ShaderNodeMath'); around.operation = next(item.identifier for item in around.bl_rna.properties['operation'].enum_items if item.identifier == 'MULTIPLY'); around.inputs[1].default_value = math.tau * 4; links.new(separate.outputs['X'], around.inputs[0])
    depth = nodes.new('ShaderNodeMath'); depth.operation = around.operation; depth.inputs[1].default_value = 31; links.new(separate.outputs['Y'], depth.inputs[0])
    phase = nodes.new('ShaderNodeMath'); phase.operation = next(item.identifier for item in phase.bl_rna.properties['operation'].enum_items if item.identifier == 'ADD'); links.new(around.outputs[0], phase.inputs[0]); links.new(depth.outputs[0], phase.inputs[1])
    scroll = nodes.new('ShaderNodeMath'); scroll.operation = phase.operation; links.new(phase.outputs[0], scroll.inputs[0])
    for frame, value in [(1,0), (240,-math.tau*5)]:
        scroll.inputs[1].default_value = value; scroll.inputs[1].keyframe_insert('default_value', frame=frame)
    sine = nodes.new('ShaderNodeMath'); sine.operation = next(item.identifier for item in sine.bl_rna.properties['operation'].enum_items if item.identifier == 'SINE'); links.new(scroll.outputs[0], sine.inputs[0])
    sharpen = nodes.new('ShaderNodeMath'); sharpen.operation = next(item.identifier for item in sharpen.bl_rna.properties['operation'].enum_items if item.identifier == 'POWER'); sharpen.inputs[1].default_value = 24; links.new(sine.outputs[0], sharpen.inputs[0])
    opacity = nodes.new('ShaderNodeMath'); opacity.operation = around.operation; opacity.inputs[1].default_value = .13; links.new(sharpen.outputs[0], opacity.inputs[0])
    lit = nodes.new('ShaderNodeEmission'); lit.inputs[0].default_value = (.035, .25, .5, 1); lit.inputs[1].default_value = 1.1
    transparent = nodes.new('ShaderNodeBsdfTransparent'); mix = nodes.new('ShaderNodeMixShader'); links.new(opacity.outputs[0], mix.inputs[0]); links.new(transparent.outputs[0], mix.inputs[1]); links.new(lit.outputs[0], mix.inputs[2]); links.new(mix.outputs[0], out.inputs[0])
    throat_material.surface_render_method = next(item.identifier for item in throat_material.bl_rna.properties['surface_render_method'].enum_items if item.identifier == 'BLENDED')
    fringe = bpy.data.materials.get('WH • volumetric plasma fringe') or bpy.data.materials.new('WH • volumetric plasma fringe'); fringe.use_nodes = True
    nodes, links = fringe.node_tree.nodes, fringe.node_tree.links; nodes.clear()
    out = nodes.new('ShaderNodeOutputMaterial'); volume = nodes.new('ShaderNodeVolumePrincipled'); volume.inputs['Color'].default_value = (.025,.12,.3,1); volume.inputs['Density'].default_value = .7
    volume.inputs['Emission Color'].default_value = (.025,.16,.4,1); volume.inputs['Emission Strength'].default_value = .7; links.new(volume.outputs[0], out.inputs['Volume'])
    for obj in list(scene.objects):
        if obj.type == 'LIGHT' and hasattr(obj.data, 'specular_factor'): obj.data.specular_factor = 0
        if 'turbulent plasma lip' in obj.name:
            obj.data.materials.clear(); obj.data.materials.append(rim_material)
            name = obj.name.replace('turbulent plasma lip', 'volumetric fringe')
            if not bpy.data.objects.get(name):
                halo = obj.copy(); halo.data = obj.data.copy(); scene.collection.objects.link(halo); halo.name = name
                for vertex in halo.data.vertices:
                    angle = math.atan2(vertex.co.z, vertex.co.x); center = 3.3 + .055*math.sin(angle*7) + .035*math.sin(angle*13)
                    radius = math.hypot(vertex.co.x, vertex.co.z); expanded = center + (radius-center)*1.85
                    vertex.co.x = expanded*math.cos(angle); vertex.co.z = expanded*math.sin(angle); vertex.co.y *= 1.85
                halo.data.materials.clear(); halo.data.materials.append(fringe)
        if 'shearing filament' in obj.name:
            obj.data.bevel_depth = .0035
            obj.data.materials.clear(); obj.data.materials.append(bpy.data.materials['WH • translucent folded space'])
        if 'folded-space throat' in obj.name:
            obj.data.materials.clear(); obj.data.materials.append(throat_material)
            layer = obj.data.uv_layers.active or obj.data.uv_layers.new(name='Spiral depth coordinates')
            for polygon in obj.data.polygons:
                for loop_index in polygon.loop_indices:
                    vertex = obj.data.loops[loop_index].vertex_index
                    layer.data[loop_index].uv = ((vertex % 160)/160, (vertex//160)/45)
        if 'gravitational lens' in obj.name:
            material = obj.data.materials[0]; material.use_nodes = True; nodes, links = material.node_tree.nodes, material.node_tree.links; nodes.clear()
            out = nodes.new('ShaderNodeOutputMaterial'); glass = nodes.new('ShaderNodeBsdfPrincipled'); glass.inputs['Base Color'].default_value=(.97,.99,1,1); glass.inputs['Transmission Weight'].default_value=1;glass.inputs['Roughness'].default_value=.025;glass.inputs['IOR'].default_value=1.05;glass.inputs['Specular IOR Level'].default_value=0
            transparent = nodes.new('ShaderNodeBsdfTransparent'); mix = nodes.new('ShaderNodeMixShader');mix.inputs[0].default_value=.035
            links.new(transparent.outputs[0],mix.inputs[1]);links.new(glass.outputs[0],mix.inputs[2]);links.new(mix.outputs[0],out.inputs[0])
            material.surface_render_method=next(item.identifier for item in material.bl_rna.properties['surface_render_method'].enum_items if item.identifier=='BLENDED')
    for name in ['Arrival landmark • copper world','Arrival landmark • atmosphere']:
        obj = bpy.data.objects.get(name)
        if obj:
            obj.location=(7,18,-3); obj.scale=(.65 if name.endswith('world') else .65*1.015,)*3
    if not bpy.data.objects.get('Arrival • distant star key'):
        kinds=[item.identifier for item in bpy.types.Light.bl_rna.properties['type'].enum_items]
        data=bpy.data.lights.new('Arrival • distant star key',next(kind for kind in kinds if kind=='SUN'));data.energy=1.6;data.color=(1,.76,.48)
        obj=bpy.data.objects.new('Arrival • distant star key',data);scene.collection.objects.link(obj);obj.rotation_euler=(.4,-.6,-.4)
    # A dark stretched microtexture keeps transit cinematic rather than a bright cloud tube.
    transit = bpy.data.materials.get('WH • dark stretched transit surface')
    if transit is None:
        transit = rim_material.copy(); transit.name = 'WH • dark stretched transit surface'
    nodes, links = transit.node_tree.nodes, transit.node_tree.links
    surface = next(node for node in nodes if node.type == 'BSDF_PRINCIPLED')
    surface.inputs['Emission Strength'].default_value = .22
    surface.inputs['Metallic'].default_value = .12; surface.inputs['Roughness'].default_value = .58
    macro = next(node for node in nodes if node.type == 'TEX_NOISE' and node.noise_dimensions == '4D')
    coordinate = next(node for node in nodes if node.type == 'TEX_COORD')
    mapping = nodes.new('ShaderNodeMapping'); mapping.inputs['Scale'].default_value = (1,.18,1)
    links.new(coordinate.outputs['Object'],mapping.inputs['Vector']);links.new(mapping.outputs['Vector'],macro.inputs['Vector'])
    rng = random.Random(991)
    for obj in scene.objects:
        if obj.name == 'Transit • rippling spacetime membrane':
            obj.data.materials.clear();obj.data.materials.append(transit)
        elif obj.name.startswith('Transit • stretched stellar filament'):
            obj.data.bevel_depth=.004
            for frame in range(1,241,24):
                start=rng.uniform(.04,.7)
                obj.data.bevel_factor_start=start;obj.data.bevel_factor_end=min(1,start+rng.uniform(.06,.17))
                obj.data.keyframe_insert('bevel_factor_start',frame=frame);obj.data.keyframe_insert('bevel_factor_end',frame=frame)
    cam=bpy.data.objects.get('02 • inside folded space')
    if cam:
        for frame, y in [(97,79),(144,93)]:
            cam.location=(0,y,1.1);cam.keyframe_insert('location',frame=frame)
    for obj in [bpy.data.objects.get('JE • single travelling hull'), cam]:
        action = obj.animation_data.action if obj and obj.animation_data else None
        if action:
            curves = list(action.fcurves) if hasattr(action, 'fcurves') else [curve for layer in action.layers for strip in layer.strips for bag in getattr(strip, 'channelbags', []) for curve in bag.fcurves]
            for curve in curves:
                if curve.data_path == 'location':
                    for point in curve.keyframe_points:
                        point.interpolation = next(item.identifier for item in point.bl_rna.properties['interpolation'].enum_items if item.identifier == 'LINEAR')
    scene['surface_notes']='Satin microturbulence, animated four-start spiral throat, shallow gravitational lens, volumetric plasma edge.'


def build() -> None:
    """Create a separate preview scene and save it without overwriting the approved cloak file."""
    source = bpy.data.objects.get('JE cloak strength • animate 0 to 1')
    if source is None: raise RuntimeError('Open the saved JE cloak preview first to reuse the textured JE hull.')
    original = bpy.context.scene
    scene = bpy.data.scenes.new('JE Wormhole • departure — transit — arrival')
    bpy.context.window.scene = scene; scene['creator'] = '42 and Beyond wormhole preview'; scene['description'] = PROMPT
    scene.render.engine = original.render.engine
    scene.render.resolution_x = 1280; scene.render.resolution_y = 720; scene.render.resolution_percentage = 100
    scene.render.fps = 24; scene.frame_start = 1; scene.frame_end = 240
    formats = [item.identifier for item in scene.render.image_settings.bl_rna.properties['file_format'].enum_items]
    scene.render.image_settings.file_format = next(value for value in formats if value == 'PNG')
    world = bpy.data.worlds.new('WH • interstellar black'); world.use_nodes = True
    next(node for node in world.node_tree.nodes if node.type == 'BACKGROUND').inputs[0].default_value = (.0015, .0025, .008, 1)
    scene.world = world
    if hasattr(scene, 'eevee'):
        scene.eevee.taa_render_samples = 32
        scene.eevee.use_raytracing = True
    plasma = energy_surface('WH • moving plasma filigree', 2.8)
    throat = energy_surface('WH • translucent folded space', .5, .055)
    entrance = portal('Departure • aperture', (-8, 0, 0), plasma, throat)
    departure_keys = [(1, .001), (14, .05), (32, 1), (92, 1), (110, .001), (240, .001)]
    set_scale_keys(entrance, departure_keys)
    exit_portal = portal('Arrival • aperture', (8, 0, 0), plasma, throat)
    set_scale_keys(exit_portal, [(1, .001), (132, .001), (151, 1), (198, 1), (226, .001), (240, .001)])
    ship = clone_ship(source)
    for frame, location, turn in [(1, (-8,-12,0), -90), (25,(-8,-9,0),-90), (78,(-8,-1,0),-90),
                                  (96,(-8,4,0),-90), (97,(0,85,0),-90), (144,(0,99,0),-90),
                                  (145,(8,4,0),90), (168,(8,0,0),90), (206,(8,-9,0),90), (240,(8,-13,1),90)]:
        ship.location = location; ship.rotation_euler = (0, 0, math.radians(turn))
        ship.keyframe_insert('location', frame=frame); ship.keyframe_insert('rotation_euler', frame=frame)
    # The travel corridor is staged far away; timeline camera cuts connect the three locations.
    vertices, faces = [], []
    for ring in range(65):
        t = ring / 64; center_x = .5 * math.sin(t * 4); center_z = .35 * math.cos(t * 5)
        for index in range(120):
            angle = math.tau * index / 120 + t * 4
            radius = 3.5 + .22 * math.sin(angle * 5 + t * 18)
            vertices.append((center_x + radius * math.cos(angle), 80 + t * 27, center_z + radius * math.sin(angle)))
    for ring in range(64):
        for index in range(120):
            a = ring * 120 + index; b = ring * 120 + (index + 1) % 120
            faces.append((a, b, b + 120, a + 120))
    mesh_object('Transit • rippling spacetime membrane', vertices, faces, energy_surface('Transit • deep ultraviolet volume', 1.2))
    for strand in range(25):
        phase = strand * math.tau / 25
        points = [(3.2 * math.cos(phase + t * 2.5), 80 + t * 27, 3.2 * math.sin(phase + t * 2.5)) for t in [index / 100 for index in range(101)]]
        curve_object(f'Transit • stretched stellar filament {strand:02}', points, .012, plasma)
    for obj in list(scene.objects):
        if obj.name.startswith('Transit •'):
            for frame, hidden in [(1, True), (96, True), (97, False), (144, False), (145, True), (240, True)]:
                obj.hide_render = hidden; obj.keyframe_insert('hide_render', frame=frame)
                obj.hide_viewport = hidden; obj.keyframe_insert('hide_viewport', frame=frame)
    stars(); planet()
    light('Departure • cool hull key', (-10,-8,8), (-8,-3,0), (.42,.72,1), 1700, 9)
    light('Arrival • warm stellar key', (11,-4,9), (8,-2,0), (1,.6,.27), 2200, 10)
    light('Arrival • blue aperture spill', (8,1,2), (8,-4,0), (.1,.45,1), 1300, 7)
    light('Transit • aperture spill', (0,89,3), (0,92,0), (.3,.55,1), 900, 5)
    cameras = [(1, camera('01 • departure shot', (-14,-17,7),(-8,-3,0),44)),
               (97, camera('02 • inside folded space', (0,79,1.1),(0,99,0),19)),
               (145,camera('03 • arrival shot', (16,-18,8),(8,-1,0),43))]
    for frame, obj in cameras:
        marker = scene.timeline_markers.new(obj.name, frame=frame); marker.camera = obj
    scene.camera = cameras[0][1]
    for frame, name in [(32,'APERTURE STABLE'), (78,'ENTER'), (120,'FOLDED SPACE'), (168,'EMERGE'), (226,'SEAL')]:
        scene.timeline_markers.new(name, frame=frame)
    # Blender 5 uses a compositor node group rather than the old scene.node_tree.
    group = bpy.data.node_groups.new('WH • restrained optical bloom', 'CompositorNodeTree')
    group.interface.new_socket(name='Image', in_out='OUTPUT', socket_type='NodeSocketColor')
    layers = group.nodes.new('CompositorNodeRLayers'); layers.scene = scene
    blur = group.nodes.new('CompositorNodeBlur')
    blur.inputs['Size'].default_value = tuple(22 for _ in blur.inputs['Size'].default_value)
    bloom = group.nodes.new('ShaderNodeMixRGB')
    bloom.blend_type = next(item.identifier for item in bloom.bl_rna.properties['blend_type'].enum_items if item.identifier == 'ADD')
    bloom.inputs[0].default_value = .33
    output = group.nodes.new('NodeGroupOutput')
    group.links.new(layers.outputs['Image'], blur.inputs['Image']); group.links.new(layers.outputs['Image'], bloom.inputs[1])
    group.links.new(blur.outputs['Image'], bloom.inputs[2]); group.links.new(bloom.outputs[0], output.inputs['Image'])
    scene.compositing_node_group = group
    notes = bpy.data.texts.new('READ ME • JE wormhole animation')
    notes.write('JE WORMHOLE / 10 second effect preview\n\nSpace: play / pause. Home in Timeline: show all 240 frames.\n1–96: blue departure aperture. 97–144: folded-space transit.\n145–240: emergence near the copper planet.\n\nOne textured JE hull travels through all three shots.\nPortal roots control aperture size, curves control plasma strands,\nand material mapping keys control flowing energy.\nThe cloak preview scene and original file remain preserved.\n')
    refine_surfaces(scene)
    scene.frame_set(176)
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type == 'VIEW_3D':
                view = area.spaces.active
                choices = [item.identifier for item in view.shading.bl_rna.properties['type'].enum_items]
                view.shading.type = next(value for value in choices if value == 'RENDERED')
                view.shading.use_scene_world = True; view.shading.use_scene_lights = True
                view.overlay.show_overlays = False
                if view.region_3d:
                    choices = [item.identifier for item in view.region_3d.bl_rna.properties['view_perspective'].enum_items]
                    view.region_3d.view_perspective = next(value for value in choices if value == 'CAMERA')
                    view.region_3d.view_camera_zoom = 0
    ART.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(ART / 'je_wormhole_preview.blend'))
    print('Wormhole preview saved:', bpy.data.filepath, 'objects:',len(scene.objects), 'frames:',scene.frame_start,scene.frame_end)


if __name__ == '__main__':
    build()
