"""Install the supplied procedural planet as optional, local Unity resources."""

from __future__ import annotations

import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile


MAPS = {
    'textures/diffuse.png': 'planet_diffuse.png',
    'textures/normal.png': 'planet_normal.png',
    'textures/rougness.png': 'planet_roughness.png',
    'textures/ao.png': 'planet_ao.png',
}


def export(archive: Path, blender: str) -> Path:
    """Extract packed maps and export the source planet mesh without its dense modifier.

    Returns:
        Path: Git-ignored resource directory with the five Unity source files.

    Raises:
        FileNotFoundError: The supplied archive or Blender executable is absent.
        KeyError: The archive is missing a required source asset.
        subprocess.CalledProcessError: Blender could not export the mesh.
    """
    if not archive.is_file():
        raise FileNotFoundError(archive)
    output = Path(__file__).resolve().parents[1] / 'Assets/FortyTwoAndBeyond/Resources/PlanetPack'
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='planet-export-') as temporary:
        source = Path(temporary) / 'planet.blend'
        with zipfile.ZipFile(archive) as bundle:
            with bundle.open('source/Procedural Planet Sketchfab.blend') as stream, source.open('wb') as target:
                shutil.copyfileobj(stream, target)
            for member, name in MAPS.items():
                with bundle.open(member) as stream, (output / name).open('wb') as target:
                    shutil.copyfileobj(stream, target)
        mesh = output / 'planet.obj'
        instruction = (
            'import bpy; '
            'ship=bpy.data.objects["Planet"]; '
            'bpy.ops.object.select_all(action="DESELECT"); '
            'ship.select_set(True); '
            'bpy.context.view_layer.objects.active=ship; '
            'ship.location=(0,0,0); '
            f'bpy.ops.wm.obj_export(filepath={str(mesh)!r},'
            'export_selected_objects=True,export_materials=False,'
            'apply_modifiers=False,forward_axis="Y",up_axis="Z")'
        )
        subprocess.run([blender, '-b', str(source), '--python-expr', instruction], check=True)
    return output


def main() -> None:
    """Accept an archive location while keeping the imported game art local."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archive', type=Path, nargs='?',
                        default=Path.home() / 'Downloads/procedural-planet-shader.zip')
    parser.add_argument('--blender', default='blender')
    arguments = parser.parse_args()
    print(export(arguments.archive, arguments.blender))


if __name__ == '__main__':
    main()
