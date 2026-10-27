"""Assemble the original player hull, profession, wing, and engine parts.

The native components share ship coordinates. This combines their converted
meshes without individually normalizing or repositioning them.
"""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

import bpy
sys.path.insert(0, str(Path(__file__).resolve().parent))
from legacy_ship_manifest import MAX_UPGRADE_SHIPS


RESOURCES = Path(__file__).resolve().parents[1] / \
    "Assets/FortyTwoAndBeyond/Resources/LegacyShips"
SHIPS = MAX_UPGRADE_SHIPS
PW_ARM_OBJECTS = {
    "R_MOUNT0": "PWArmRMount", "R_30": "PWArmR3",
    "R_20": "PWArmR2", "R_10": "PWArmR1",
    "L_MOUNT010": "PWArmLMount", "L_30": "PWArmL3",
    "L_20": "PWArmL2", "L_10": "PWArmL1",
}


def export_selected(name: str, resources: Path) -> None:
    """Export selected native mesh objects without changing their shared coordinates."""
    bpy.ops.wm.obj_export(filepath=str(resources / (name + ".obj")),
                          export_selected_objects=True, apply_modifiers=True,
                          export_materials=True, forward_axis="NEGATIVE_Z",
                          up_axis="Y", path_mode="RELATIVE")


def assemble(name: str, parts: tuple[str, ...], resources: Path) -> None:
    """Export one ship from its four original visual components."""
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for stem in parts:
        bpy.ops.wm.obj_import(filepath=str(resources / (stem + ".obj")))
        if name == "OriginalPW" and stem == "spp23":
            for source, destination in PW_ARM_OBJECTS.items():
                arm = bpy.data.objects.get(source)
                if arm is None:
                    raise ValueError(f"Missing articulated PW arm object: {source}")
                bpy.ops.object.select_all(action="DESELECT")
                arm.select_set(True)
                bpy.context.view_layer.objects.active = arm
                export_selected(destination, resources)
                bpy.data.objects.remove(arm, do_unlink=True)
    bpy.ops.object.select_all(action="SELECT")
    export_selected(name, resources)
    print("EXPORTED", name, "from", ", ".join(parts))


def main() -> None:
    """Write ready-to-import max-upgrade originals for all nine classes."""
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=RESOURCES)
    options = parser.parse_args(arguments)
    for name, parts in SHIPS.items():
        assemble(name, parts, options.output_dir)
    assemble("OriginalJEBase", ("sjh33", "sje01a"), options.output_dir)


if __name__ == "__main__":
    main()
