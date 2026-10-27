"""Export the saved Blender Combat Trance power rail as a Unity model."""

from pathlib import Path

import bpy


PROJECT = Path(__file__).resolve().parents[2]
SOURCE = PROJECT / "client/Art/Blender/combat_trance.blend"
OUTPUT = PROJECT / "client/Assets/FortyTwoAndBeyond/Resources/LegacyEffects/CombatTranceRing.fbx"
RING_NAME = "Combat Trance • continuous power rail"


def export_ring() -> Path:
    """Returns:
        Path: The isolated mesh exported from the approved Blender scene.

    Raises:
        ValueError: The expected Combat Trance rail is absent.
    """
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    bpy.context.scene.frame_set(1)
    ring = bpy.data.objects.get(RING_NAME)
    if ring is None or ring.type != "CURVE":
        raise ValueError(f"Missing Blender Combat Trance rail: {RING_NAME}")
    bpy.ops.object.select_all(action="DESELECT")
    ring.select_set(True)
    bpy.context.view_layer.objects.active = ring
    world = ring.matrix_world.copy()
    ring.parent = None
    ring.matrix_world = world
    bpy.ops.object.convert(target="MESH")
    mesh = bpy.context.view_layer.objects.active
    if mesh is None or mesh.type != "MESH":
        raise ValueError("Combat Trance rail did not convert to a mesh")
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    bpy.context.view_layer.objects.active = mesh
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=str(OUTPUT), use_selection=True, object_types={"MESH"},
        bake_anim=False, axis_forward="-Z", axis_up="Y", path_mode="AUTO")
    return OUTPUT


if __name__ == "__main__":
    print("EXPORTED_COMBAT_TRANCE", export_ring())
