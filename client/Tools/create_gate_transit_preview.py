"""Animate the current segmented game gate, counter-rotating collars and a JE crossing its field."""
from __future__ import annotations
import math
from pathlib import Path
import runpy
import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / 'Art/Blender'


def empty(name: str, parent: bpy.types.Object | None = None) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: A separately editable mechanical animation pivot.
    """
    obj = bpy.data.objects.new(name, None); bpy.context.scene.collection.objects.link(obj); obj.parent = parent
    return obj


def metal(name: str, color: tuple, metallic: float = .8) -> bpy.types.Material:
    """Returns:
        bpy.types.Material: Brushed metallic armor with fine surface relief and roughness variation.
    """
    mat = bpy.data.materials.new(name); mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    surface = next(node for node in nodes if node.type == 'BSDF_PRINCIPLED')
    surface.inputs['Base Color'].default_value = (*color, 1); surface.inputs['Metallic'].default_value = metallic
    surface.inputs['Roughness'].default_value = .33; surface.inputs['Coat Weight'].default_value = .2
    coordinate = nodes.new('ShaderNodeTexCoord'); mapping = nodes.new('ShaderNodeMapping')
    mapping.inputs['Scale'].default_value = (1, 18, 1); links.new(coordinate.outputs['Object'], mapping.inputs['Vector'])
    grain = nodes.new('ShaderNodeTexNoise'); grain.inputs['Scale'].default_value = 24; grain.inputs['Detail'].default_value = 3
    links.new(mapping.outputs['Vector'], grain.inputs['Vector'])
    bump = nodes.new('ShaderNodeBump'); bump.inputs['Strength'].default_value = .18; bump.inputs['Distance'].default_value = .018
    links.new(grain.outputs['Fac'], bump.inputs['Height']); links.new(bump.outputs['Normal'], surface.inputs['Normal'])
    rough = nodes.new('ShaderNodeMapRange'); rough.inputs['To Min'].default_value = .25; rough.inputs['To Max'].default_value = .48
    links.new(grain.outputs['Fac'], rough.inputs['Value']); links.new(rough.outputs['Result'], surface.inputs['Roughness'])
    return mat


def bevel(obj: bpy.types.Object, width: float = .045) -> None:
    """Give manufactured edges a narrow real highlight rather than a painted outline."""
    kinds = [item.identifier for item in bpy.types.Modifier.bl_rna.properties['type'].enum_items]
    modifier = obj.modifiers.new('Machined edge highlights', next(kind for kind in kinds if kind == 'BEVEL'))
    modifier.width = width; modifier.segments = 3


def arc(name: str, parent: bpy.types.Object, inner: float, outer: float, start: float, end: float,
        depth: float, material: bpy.types.Material, mesh_factory) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: One solid gate segment with inner, outer, front, rear and end faces.
    """
    vertices, faces = [], []
    steps = 24
    for step in range(steps + 1):
        angle = start + (end - start) * step / steps
        for radius, y in [(inner, -depth/2), (outer, -depth/2), (inner, depth/2), (outer, depth/2)]:
            vertices.append((radius*math.cos(angle), y, radius*math.sin(angle)))
    for step in range(steps):
        a = step*4; b = a+4
        faces.extend([(a,b,b+1,a+1), (a+2,a+3,b+3,b+2), (a+1,b+1,b+3,a+3), (a,a+2,b+2,b)])
    faces.extend([(0,1,3,2),(steps*4,steps*4+2,steps*4+3,steps*4+1)])
    obj = mesh_factory(name, vertices, faces, material); obj.parent = parent; bevel(obj)
    return obj


def box(name: str, parent: bpy.types.Object, radius: float, angle: float, y: float,
        dimensions: tuple, material: bpy.types.Material) -> bpy.types.Object:
    """Returns:
        bpy.types.Object: A radial actuator, armor inset or illuminated index block.
    """
    bpy.ops.mesh.primitive_cube_add(size=1)
    obj = bpy.context.object; obj.name = name; obj.parent = parent
    obj.location = (radius*math.cos(angle), y, radius*math.sin(angle))
    obj.rotation_euler[1] = -angle; obj.scale = dimensions; obj.data.materials.append(material)
    # Bake scale so edge widths remain consistent across the housing.
    for vertex in obj.data.vertices:
        vertex.co.x *= dimensions[0]; vertex.co.y *= dimensions[1]; vertex.co.z *= dimensions[2]
    obj.scale = (1,1,1); bevel(obj, .035)
    return obj


def rotation_keys(obj: bpy.types.Object, keys: list) -> None:
    """Animate gate rotation about the opening normal, with acceleration and coast-down."""
    for frame, degrees in keys:
        obj.rotation_euler[1] = math.radians(degrees); obj.keyframe_insert('rotation_euler', frame=frame)


def clip_ship_at_field(ship: bpy.types.Object) -> None:
    """Make the real hull disappear progressively as each surface crosses the gate plane."""
    seen = set()
    for obj in ship.children:
        for slot in obj.material_slots:
            material = slot.material
            if material is None or material in seen: continue
            seen.add(material)
            nodes, links = material.node_tree.nodes, material.node_tree.links
            output = next(node for node in nodes if node.type == 'OUTPUT_MATERIAL')
            original = output.inputs['Surface'].links[0].from_socket
            geometry = nodes.new('ShaderNodeNewGeometry'); separate = nodes.new('ShaderNodeSeparateXYZ')
            links.new(geometry.outputs['Position'], separate.inputs[0])
            before_plane = nodes.new('ShaderNodeMath')
            before_plane.operation = next(item.identifier for item in before_plane.bl_rna.properties['operation'].enum_items if item.identifier == 'LESS_THAN')
            before_plane.inputs[1].default_value = .02; links.new(separate.outputs['Y'], before_plane.inputs[0])
            transparent = nodes.new('ShaderNodeBsdfTransparent'); mix = nodes.new('ShaderNodeMixShader')
            links.new(before_plane.outputs[0], mix.inputs[0]); links.new(transparent.outputs[0], mix.inputs[1]); links.new(original, mix.inputs[2]); links.new(mix.outputs[0], output.inputs['Surface'])


def energy_disk(root: bpy.types.Object, mesh_factory) -> tuple:
    """Returns:
        tuple: The animated gate field and its activation node.
    """
    material = bpy.data.materials.new('Gate • flowing induction field'); material.use_nodes = True
    nodes, links = material.node_tree.nodes, material.node_tree.links; nodes.clear()
    output = nodes.new('ShaderNodeOutputMaterial'); coordinate = nodes.new('ShaderNodeTexCoord')
    noise = nodes.new('ShaderNodeTexNoise'); noise.noise_dimensions = next(item.identifier for item in noise.bl_rna.properties['noise_dimensions'].enum_items if item.identifier == '4D')
    noise.inputs['Scale'].default_value = 4.5; noise.inputs['Detail'].default_value = 5; noise.inputs['Roughness'].default_value = .65
    links.new(coordinate.outputs['Object'], noise.inputs['Vector'])
    for frame, value in [(1,0),(216,5)]:
        noise.inputs['W'].default_value = value; noise.inputs['W'].keyframe_insert('default_value',frame=frame)
    ramp = nodes.new('ShaderNodeValToRGB'); ramp.color_ramp.elements[0].color=(.002,.018,.06,1); ramp.color_ramp.elements[1].color=(.06,.5,.85,1)
    links.new(noise.outputs['Fac'],ramp.inputs[0])
    emission=nodes.new('ShaderNodeEmission');emission.inputs['Strength'].default_value=1.8;links.new(ramp.outputs['Color'],emission.inputs['Color'])
    strength=nodes.new('ShaderNodeValue');strength.label='Gate activation envelope'
    for frame, value in [(1,.015),(24,.02),(54,.18),(94,.23),(114,.37),(138,.23),(174,.04),(198,.015),(216,.015)]:
        strength.outputs[0].default_value=value;strength.outputs[0].keyframe_insert('default_value',frame=frame)
    transparent=nodes.new('ShaderNodeBsdfTransparent');mix=nodes.new('ShaderNodeMixShader')
    links.new(strength.outputs[0],mix.inputs[0]);links.new(transparent.outputs[0],mix.inputs[1]);links.new(emission.outputs[0],mix.inputs[2]);links.new(mix.outputs[0],output.inputs[0])
    material.surface_render_method=next(item.identifier for item in material.bl_rna.properties['surface_render_method'].enum_items if item.identifier=='BLENDED')
    vertices, faces = [(0,0,0)], []
    for i in range(160):
        angle=math.tau*i/160;vertices.append((3.95*math.cos(angle),0,3.95*math.sin(angle)))
    for i in range(160):faces.append((0,1+i,1+(i+1)%160))
    obj=mesh_factory('Gate • active aperture membrane',vertices,faces,material);obj.parent=root
    return obj,strength


def contract_gate(scene: bpy.types.Scene) -> None:
    """Retract the armor and actuator carriages while keeping the ship's aperture clear."""
    envelope = [(1,0),(24,0),(60,.35),(90,1),(122,1),(148,.65),(174,0),(216,0)]
    for obj in scene.objects:
        sector = None
        if obj.name.startswith(('Rotor • armored sector ', 'Rotor • recessed circuit ', 'Rotor • outer machined edge ')):
            sector = int(obj.name.rsplit(' ',1)[-1])-1
        elif obj.name == 'Rotor • amber position index':
            sector = 0
        actuator = obj.name.startswith(('Frame • actuator ', 'Frame • face armor ', 'Frame • status lamp '))
        if sector is None and not actuator:
            continue
        if 'contraction_rest' not in obj:
            obj['contraction_rest'] = list(obj.location)
        rest = Vector(obj['contraction_rest'])
        angle = (sector+.5)*math.tau/8 if sector is not None else math.atan2(rest.z,rest.x)
        stroke = .48 if sector is not None else .22
        for frame, amount in envelope:
            obj.location = rest + Vector((-math.cos(angle)*stroke*amount, .18*amount if actuator else 0, -math.sin(angle)*stroke*amount))
            obj.keyframe_insert('location',frame=frame)
    collar = bpy.data.objects.get('GATE • counter-rotating induction collar')
    field = bpy.data.objects.get('Gate • active aperture membrane')
    for frame, amount in envelope:
        if collar:
            collar.location.y = .7*amount; collar.keyframe_insert('location',frame=frame)
        if field:
            field.scale = (1-.095*amount,1,1-.095*amount);field.keyframe_insert('scale',frame=frame)
    for index,begin in enumerate([99,124]):
        pivot = bpy.data.objects.get(f'Gate • contact ripple {index+1}')
        if pivot:
            pivot.animation_data_clear()
            for frame,scale in [(1,.001),(begin-1,.001),(begin,.08),(begin+16,3.45),(begin+19,3.6),(216,3.6)]:
                pivot.scale = (scale,)*3;pivot.keyframe_insert('scale',frame=frame)
    if 'CONTRACTION LOCK' not in scene.timeline_markers:
        scene.timeline_markers.new('CONTRACTION LOCK',frame=90)
    scene['contraction_notes'] = 'Armored sectors slide inward 0.48m, actuator carriages retract 0.22m and 0.18m axially; collar recesses 0.7m. Field follows the clear opening. Relaxation finishes at frame 174.'


def polish_whole_gate(scene: bpy.types.Scene) -> None:
    """Compress the entire assembly and sequence its glowing channels around a translucent field."""
    root = bpy.data.objects.get('GATE • complete assembly') or bpy.data.objects.get('GATE • stationary structure')
    if root is None:
        raise RuntimeError('The gate assembly is missing.')
    root.name = 'GATE • complete assembly'
    for frame,scale in [(1,1),(24,1),(54,.96),(88,.82),(108,.76),(120,.76),(150,1.015),(174,1),(216,1)]:
        root.scale = (scale,)*3;root.keyframe_insert('scale',frame=frame)
    for index in range(8):
        name = f'Gate • sequenced channel {index+1:02}'
        material = bpy.data.materials.get(name)
        if material is None:
            material = bpy.data.materials['Gate • cyan conduction channels'].copy();material.name=name
        light = next(node for node in material.node_tree.nodes if node.type=='EMISSION')
        light.inputs[0].default_value=(.025,.48,.95,1)
        for frame,strength in [(1,.35),(24+index*2,.35),(46+index*2,3.5),(92+index,5),(116+index,7),(140+index*2,1.8),(178,.35),(216,.35)]:
            light.inputs[1].default_value=strength;light.inputs[1].keyframe_insert('default_value',frame=frame)
        for prefix in ['Rotor • recessed circuit ','Frame • status lamp ']:
            obj=bpy.data.objects.get(prefix+f'{index+1:02}')
            if obj:
                obj.data.materials.clear();obj.data.materials.append(material)
    material=bpy.data.materials['Gate • cyan conduction channels']
    light=next(node for node in material.node_tree.nodes if node.type=='EMISSION')
    for frame,strength in [(1,.6),(54,1.6),(88,3.5),(108,5),(120,6),(144,2),(174,.6),(216,.6)]:
        light.inputs[1].default_value=strength;light.inputs[1].keyframe_insert('default_value',frame=frame)
    for name in ['Gate • brushed blue titanium','Gate • silver edge trim']:
        mat=bpy.data.materials[name]
        surface=next(node for node in mat.node_tree.nodes if node.type=='BSDF_PRINCIPLED')
        surface.inputs['Coat Weight'].default_value=.42
        for node in mat.node_tree.nodes:
            if node.type=='MAP_RANGE':node.inputs['To Min'].default_value=.20;node.inputs['To Max'].default_value=.36
    field=bpy.data.materials['Gate • flowing induction field']
    nodes,links=field.node_tree.nodes,field.node_tree.links
    coordinate=next(node for node in nodes if node.type=='TEX_COORD')
    envelope=next(node for node in nodes if node.type=='VALUE')
    mix=next(node for node in nodes if node.type=='MIX_SHADER')
    radius=nodes.new('ShaderNodeVectorMath');radius.operation=next(item.identifier for item in radius.bl_rna.properties['operation'].enum_items if item.identifier=='LENGTH');links.new(coordinate.outputs['Object'],radius.inputs[0])
    normalized=nodes.new('ShaderNodeMath');normalized.operation=next(item.identifier for item in normalized.bl_rna.properties['operation'].enum_items if item.identifier=='DIVIDE');normalized.inputs[1].default_value=3.95;links.new(radius.outputs['Value'],normalized.inputs[0])
    rim=nodes.new('ShaderNodeMath');rim.operation=next(item.identifier for item in rim.bl_rna.properties['operation'].enum_items if item.identifier=='POWER');rim.inputs[1].default_value=4;links.new(normalized.outputs[0],rim.inputs[0])
    mask=nodes.new('ShaderNodeMath');mask.operation=next(item.identifier for item in mask.bl_rna.properties['operation'].enum_items if item.identifier=='MULTIPLY_ADD');mask.inputs[1].default_value=.78;mask.inputs[2].default_value=.15;links.new(rim.outputs[0],mask.inputs[0])
    opacity=nodes.new('ShaderNodeMath');opacity.operation=next(item.identifier for item in opacity.bl_rna.properties['operation'].enum_items if item.identifier=='MULTIPLY');links.new(mask.outputs[0],opacity.inputs[0]);links.new(envelope.outputs[0],opacity.inputs[1]);links.new(opacity.outputs[0],mix.inputs[0])
    scene['whole_gate_notes']='The entire assembly, including pylons and the bearing, contracts to 76% scale. Sequential cyan channels chase around the rim, followed by a 1.5% release overshoot and settled rest size. Ship scale stays unchanged.'
    notes=bpy.data.texts.get('READ ME • gate transit')
    if notes and 'WHOLE GATE' not in notes.as_string():
        notes.write('\nWHOLE GATE UPDATE\nThe entire frame and pylons shrink with the aperture.\nCyan channels charge in sequence; the clear central field preserves\nthe ship silhouette as it crosses. The frame returns to rest after transit.\n')


def build() -> None:
    """Create a separate gate animation, retaining the approved wormhole and cloak scenes."""
    library=runpy.run_path(str(ROOT/'Tools/create_je_wormhole_preview.py'),run_name='gate_preview_library')
    source=bpy.data.objects.get('JE cloak strength • animate 0 to 1')
    if source is None:raise RuntimeError('Open je_wormhole_preview.blend or je_cloak_preview.blend first.')
    previous=bpy.context.scene
    scene=bpy.data.scenes.new('Gate • charge — counter-rotate — transit');bpy.context.window.scene=scene
    scene['creator']='42 and Beyond gate animation preview';scene.render.engine=previous.render.engine
    scene.world=previous.world.copy();scene.render.resolution_x=1280;scene.render.resolution_y=720;scene.render.resolution_percentage=100
    scene.render.fps=24;scene.frame_start=1;scene.frame_end=216
    scene.eevee.taa_render_samples=32;scene.eevee.use_raytracing=True
    image_formats=[item.identifier for item in scene.render.image_settings.bl_rna.properties['file_format'].enum_items]
    scene.render.image_settings.file_format=next(value for value in image_formats if value=='PNG')
    root=empty('GATE • complete assembly')
    rotor=empty('GATE • armored rotor',root);collar=empty('GATE • counter-rotating induction collar',root)
    armor=metal('Gate • brushed blue titanium',(.11,.2,.29));inset=metal('Gate • dark ceramic steel',(.018,.045,.072),.65)
    inset_metal=metal('Gate • silver edge trim',(.3,.39,.46))
    cyan=library['emission']('Gate • cyan conduction channels',(.04,.42,.8),2)
    amber=library['emission']('Gate • amber rotor index',(.8,.22,.025),1.8)
    mesh_factory=library['mesh_object']
    for sector in range(8):
        start=(sector+.09)*math.tau/8;end=(sector+.91)*math.tau/8
        arc(f'Rotor • armored sector {sector+1:02}',rotor,4.18,5.42,start,end,1.06,armor,mesh_factory)
        trim=arc(f'Rotor • recessed circuit {sector+1:02}',rotor,4.32,4.39,start+.03,end-.03,.018,cyan,mesh_factory);trim.location.y=-.56
        trim=arc(f'Rotor • outer machined edge {sector+1:02}',rotor,5.27,5.34,start+.02,end-.02,.04,inset_metal,mesh_factory);trim.location.y=-.55
        angle=sector*math.tau/8
        box(f'Frame • actuator {sector+1:02}',root,6.02,angle,0,(1.05,1.65,.85),inset)
        box(f'Frame • face armor {sector+1:02}',root,6.02,angle,-.88,(.7,.14,.48),armor)
        box(f'Frame • status lamp {sector+1:02}',root,5.79,angle,-.98,(.29,.035,.15),cyan)
    arc('Frame • stationary bearing housing',root,5.48,5.67,0,math.tau,.55,inset,mesh_factory)
    race=arc('Frame • telescopic bearing race',root,4.65,5.6,0,math.tau,.15,inset,mesh_factory);race.location.y=.63
    arc('Inductor • inner bearing',collar,3.98,4.11,0,math.tau,.22,inset_metal,mesh_factory)
    for i in range(24):
        angle=i*math.tau/24
        box(f'Inductor • luminous tooth {i+1:02}',collar,4.02,angle,-.3,(.16,.12,.24),amber if i==0 else cyan)
    box('Rotor • amber position index',rotor,4.9,.1,-.61,(.3,.07,.17),amber)
    rotation_keys(rotor,[(1,0),(24,0),(60,35),(96,110),(132,250),(168,310),(216,315)])
    rotation_keys(collar,[(1,0),(24,0),(60,-55),(96,-165),(132,-350),(168,-440),(216,-450)])
    energy_disk(root,mesh_factory)
    # Two pressure ripples expand at nose contact and as the last hull surface enters.
    for index, begin in enumerate([99,124]):
        pivot=empty(f'Gate • contact ripple {index+1}',root)
        ripple=arc(f'Gate • contact wave {index+1}',pivot,.97,1,0,math.tau,.015,cyan,mesh_factory);ripple.location.y=-.035
        for frame,scale in [(1,.001),(begin-1,.001),(begin,.08),(begin+16,3.9),(begin+19,4.1),(216,.001)]:
            pivot.scale=(scale,)*3;pivot.keyframe_insert('scale',frame=frame)
        for frame,hidden in [(1,True),(begin-1,True),(begin,False),(begin+17,True),(216,True)]:
            ripple.hide_render=hidden;ripple.keyframe_insert('hide_render',frame=frame)
            ripple.hide_viewport=hidden;ripple.keyframe_insert('hide_viewport',frame=frame)
    ship=library['clone_ship'](source);ship.name='JE • gating hull';clip_ship_at_field(ship)
    for frame,y in [(1,-14),(24,-11),(60,-7),(90,-3),(114,0),(144,7),(180,14),(216,18)]:
        ship.location=(0,y,0);ship.rotation_euler=(0,0,-math.pi/2)
        ship.keyframe_insert('location',frame=frame);ship.keyframe_insert('rotation_euler',frame=frame)
    action=ship.animation_data.action
    curves=list(action.fcurves) if hasattr(action,'fcurves') else [curve for layer in action.layers for strip in layer.strips for bag in getattr(strip,'channelbags',[]) for curve in bag.fcurves]
    for curve in curves:
        if curve.data_path=='location':
            for point in curve.keyframe_points:point.interpolation=next(item.identifier for item in point.bl_rna.properties['interpolation'].enum_items if item.identifier=='LINEAR')
    library['stars']()
    scene.camera=library['camera']('Gate • three-quarter transit camera',(13,-33,13),(0,0,0),45)
    library['light']('Gate • broad cool key',(1,-10,12),(0,0,0),(.48,.7,1),5200,11)
    library['light']('Gate • warm edge light',(-10,3,6),(0,0,0),(1,.56,.25),6500,8)
    library['light']('Gate • aperture spill',(0,-.8,1),(0,-5,0),(.1,.5,1),600,7)
    spill=bpy.data.objects['Gate • aperture spill'].data
    for frame,power in [(1,30),(24,60),(60,1000),(99,1400),(114,2400),(138,1000),(174,60),(216,30)]:
        spill.energy=power;spill.keyframe_insert('energy',frame=frame)
    for frame,label in [(1,'APPROACH'),(24,'SPOOL UP'),(60,'RINGS COUNTER-ROTATE'),(99,'NOSE ENTERS FIELD'),(114,'HULL CROSSES'),(129,'SHIP CLEAR'),(174,'COAST DOWN')]:
        scene.timeline_markers.new(label,frame=frame)
    if previous.compositing_node_group:
        group=previous.compositing_node_group.copy();group.name='Gate • restrained bloom'
        for node in group.nodes:
            if node.type=='R_LAYERS':node.scene=scene
        scene.compositing_node_group=group
    notes=bpy.data.texts.new('READ ME • gate transit')
    notes.write('GATE TRANSIT / 9 seconds at 24fps\n\nShift Left Arrow: rewind. Space: play / pause.\nThe entire gate contracts. The armored rotor and inner\ninduction collar counter-rotate, accelerating as the gate energizes.\nThe JE disappears progressively at the aperture plane.\nContact waves are keyed to the nose and trailing hull crossing.\nSelect GATE • armored rotor or the induction collar to edit rotation.\n')
    contract_gate(scene)
    polish_whole_gate(scene)
    scene.frame_set(104)
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type=='VIEW_3D':
                view=area.spaces.active;view.overlay.show_overlays=False
                view.shading.type=next(item.identifier for item in view.shading.bl_rna.properties['type'].enum_items if item.identifier=='RENDERED')
                if view.region_3d:
                    view.region_3d.view_perspective=next(item.identifier for item in view.region_3d.bl_rna.properties['view_perspective'].enum_items if item.identifier=='CAMERA')
                    view.region_3d.view_camera_zoom=0
    ART.mkdir(parents=True,exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(ART/'gate_transit_preview.blend'))
    print('Saved gate preview:',bpy.data.filepath,'objects',len(scene.objects))


if __name__=='__main__':
    build()
