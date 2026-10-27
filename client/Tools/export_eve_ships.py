"""Bake two optional EVE ship examples from the user's local Blender scene.

Run with Blender, for example:
blender -b '/path/to/Updated_Release_EVE_Online _Ship_shader_.blend' --python client/Tools/export_eve_ships.py
The exported game art stays in the git-ignored EveShips resource directory.
"""

from pathlib import Path

import bpy


OUTPUT = Path(__file__).resolve().parents[1] / 'Assets/FortyTwoAndBeyond/Resources/EveShips'
SHIPS = (('cc3_t1', 'cc3', 'cc3_t1_g.png'), ('gc3', 'gc3', 'gc3_t1_g.png'))


def export_ship(object_name: str, stem: str, glow_name: str) -> None:
    """Bake the hull and export its UV mesh and packed material maps."""
    ship = bpy.data.objects.get(object_name)
    if ship is None or ship.type != 'MESH':
        raise ValueError(f'Missing EVE example ship: {object_name}')
    bpy.ops.object.select_all(action='DESELECT')
    ship.select_set(True)
    bpy.context.view_layer.objects.active = ship
    image = bpy.data.images.new(stem + '_unity_albedo', width=2048, height=2048, alpha=True)
    for material in ship.data.materials:
        node = material.node_tree.nodes.new('ShaderNodeTexImage')
        node.name = 'Unity baked albedo target'
        node.image = image
        material.node_tree.nodes.active = node
    bpy.ops.object.bake(type='DIFFUSE')
    image.filepath_raw = str(OUTPUT / (stem + '_albedo.png'))
    image.file_format = 'PNG'
    image.save()

    glow = bpy.data.images.get(glow_name)
    if glow is None:
        raise ValueError(f'Missing EVE glow mask for {object_name}')
    glow.filepath_raw = str(OUTPUT / (stem + '_glow.png'))
    glow.file_format = 'PNG'
    glow.save()

    for suffix, output_name in (('_n.png', 'normal'), ('_r.png', 'roughness')):
        detail = bpy.data.images.get(stem + '_t1' + suffix)
        if detail is None:
            raise ValueError(f'Missing EVE {output_name} map for {object_name}')
        detail.filepath_raw = str(OUTPUT / (stem + '_' + output_name + '.png'))
        detail.file_format = 'PNG'
        detail.save()

    ship.location = (0, 0, 0)
    ship.rotation_euler = (0, 0, 0)
    bpy.context.view_layer.update()
    bpy.ops.wm.obj_export(filepath=str(OUTPUT / (stem + '.obj')),
                          export_selected_objects=True, export_materials=False,
                          forward_axis='Y', up_axis='Z', global_scale=1.0)


def main() -> None:
    """Export optional ships without changing or saving the user's Blender file."""
    if not bpy.data.filepath:
        raise ValueError('Open the supplied EVE .blend file before running this script.')
    OUTPUT.mkdir(parents=True, exist_ok=True)
    sky = bpy.data.images.get('a13_cube.png')
    if sky is not None:
        sky.filepath_raw = str(OUTPUT / 'a13_cube.png')
        sky.file_format = 'PNG'
        sky.save()
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 16
    scene.cycles.bake_type = 'DIFFUSE'
    scene.render.bake.use_pass_direct = False
    scene.render.bake.use_pass_indirect = False
    scene.render.bake.use_pass_color = True
    scene.render.bake.margin = 12
    for object_name, stem, glow_name in SHIPS:
        export_ship(object_name, stem, glow_name)
        print('Exported optional EVE ship:', stem)


if __name__ == '__main__':
    main()
