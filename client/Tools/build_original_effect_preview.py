"""Build a Blender file for browsing converted animated effect rigs."""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

import bpy
from mathutils import Quaternion


ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "client/Assets/FortyTwoAndBeyond/Resources/LegacyEffects"
def main() -> None:
    """Import each available FBX into a separately toggleable collection."""
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    names = sorted(path.stem for path in ASSETS.glob("*.fbx"))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--show", choices=names, default="RallyFiringAnimated")
    options = parser.parse_args(arguments)
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    preferred = options.show
    names = [preferred] + [name for name in names if name != preferred]
    for index, name in enumerate(names, 1):
        before = set(bpy.data.objects)
        bpy.ops.import_scene.fbx(filepath=str(ASSETS / f"{name}.fbx"))
        imported = [item for item in bpy.data.objects if item not in before]
        collection = bpy.data.collections.new(f"{index:02d} {name}")
        bpy.context.scene.collection.children.link(collection)
        for item in imported:
            for previous in tuple(item.users_collection):
                previous.objects.unlink(item)
            collection.objects.link(item)
        collection.hide_viewport = name != preferred
        print(f"EFFECT_PREVIEW {name} {len(imported)} objects")
    scene = bpy.context.scene
    scene.frame_start = 1
    scene.frame_end = 120
    scene.render.fps = 30
    scene.frame_set(1)
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type != "VIEW_3D":
                continue
            area.spaces.active.region_3d.view_location = (0, 0, 0)
            area.spaces.active.region_3d.view_distance = 8
            area.spaces.active.region_3d.view_rotation = Quaternion((.89, .33, .22, .22))
            area.spaces.active.shading.type = "MATERIAL"
    output = ROOT / "client/Builds" / (preferred + "Preview.blend")
    output.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(output))
    print(f"Saved {output}")


if __name__ == "__main__":
    main()
