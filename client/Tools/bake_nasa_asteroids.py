"""Bake downloaded NASA asteroid STL shapes into small, UV-mapped Unity OBJ assets (Blender)."""
from pathlib import Path
import math

import bpy
from mathutils import Vector


def bake_shape(name: str, root: Path) -> None:
    """Normalize and decimate one source asteroid without touching user Blender scenes."""
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    source = root / 'Art/SpaceEnvironment/NASAAsteroids' / (name + '.stl')
    bpy.ops.wm.stl_import(filepath=str(source))
    body = bpy.context.active_object
    center = sum((Vector(point) for point in body.bound_box), Vector()) / 8
    radius = max((vertex.co - center).length for vertex in body.data.vertices)
    for vertex in body.data.vertices:
        vertex.co = (vertex.co - center) / radius
    modifier = body.modifiers.new('Real-time shape budget', 'DECIMATE')
    modifier.ratio = min(1.0, 5200 / max(1, len(body.data.polygons)))
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    uv = body.data.uv_layers.new(name='Photographic surface coordinates')
    for polygon in body.data.polygons:
        polygon.use_smooth = True
        for index in polygon.loop_indices:
            point = body.data.vertices[body.data.loops[index].vertex_index].co.normalized()
            uv.data[index].uv = (math.atan2(point.y, point.x) / (2 * math.pi) + .5,
                                 math.asin(max(-1, min(1, point.z))) / math.pi + .5)
    body.name = name
    target = root / 'Assets/FortyTwoAndBeyond/Resources/SpaceEnvironment/Models' / (name + '.obj')
    target.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.obj_export(filepath=str(target), export_selected_objects=True,
                          export_materials=False, forward_axis='NEGATIVE_Z', up_axis='Y')
    print(f'BAKED {name}: {len(body.data.polygons)} polygons -> {target}')


def main() -> None:
    """Use factory-startup Blender data and the repository-relative source/output paths."""
    root = Path(__file__).resolve().parents[1]
    for name in ('Eros', 'Itokawa'):
        bake_shape(name, root)


if __name__ == '__main__':
    main()
