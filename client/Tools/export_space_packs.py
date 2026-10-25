"""Export the user's two local FBX packs as optional, compact Unity resources."""

from __future__ import annotations

import argparse
from pathlib import Path
import subprocess
import tempfile
import zipfile

from PIL import Image


ROOT = Path(__file__).resolve().parents[1] / 'Assets/FortyTwoAndBeyond/Resources'
PACKS = (
    ('asteroids-pack-metallic-version.zip', 'AsteroidPack',
     'source/AsteroidsPackSF.fbx', tuple(f'Asteroid_no_{index}' for index in range(1, 11)),
     {'Mat1_Base_Color.jpeg': ('rock_color.jpg', 2048),
      'Mat1_Normal_DirectX.jpeg': ('rock_normal.jpg', 2048),
      'Mat1_Metallic.jpeg': ('rock_metal.jpg', 1024),
      'Mat1_Roughness.jpeg': ('rock_roughness.jpg', 1024),
      'Mat1_Mixed_AO.jpeg': ('rock_ao.jpg', 1024)}),
    ('blackhole.zip', 'BlackHolePack',
     'source/f4f6765107c64d0bb4c474a92a72bdc9.fbx.fbx',
     ('Blackhole_ring', 'Blackhole_core'),
     {'ring_diff_B.jpg': ('ring_color.jpg', 1536),
      'ring_clouds_A.jpg': ('ring_clouds.jpg', 1536),
      'ring_opa.jpg': ('ring_opacity.jpg', 1536)}),
)


def export_models(blender: str, source: Path, output: Path, names: tuple[str, ...]) -> None:
    """Use Blender to export centered UV meshes without its sample camera or light.

    Raises:
        subprocess.CalledProcessError: Blender cannot load or export the source FBX.
    """
    instruction = f'''import bpy
bpy.ops.wm.fbx_import(filepath={str(source)!r})
for name in {names!r}:
    bpy.ops.object.select_all(action="DESELECT")
    model = bpy.data.objects[name]
    model.select_set(True)
    bpy.context.view_layer.objects.active = model
    model.location = (0, 0, 0)
    bpy.ops.wm.obj_export(filepath={str(output)!r} + "/" + name + ".obj",
                          export_selected_objects=True, export_materials=False,
                          forward_axis="Y", up_axis="Z")
'''
    subprocess.run([blender, '-b', '--python-expr', instruction], check=True)


def export_pack(archive: Path, blender: str, specification: tuple) -> Path:
    """Extract one archive's maps and meshes into a git-ignored Resources folder.

    Returns:
        Path: Destination of the imported local game art.

    Raises:
        FileNotFoundError: The archive is absent.
        KeyError: An expected FBX or texture is absent.
    """
    if not archive.is_file():
        raise FileNotFoundError(archive)
    _, folder, fbx, names, maps = specification
    output = ROOT / folder
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='space-pack-') as temporary:
        source = Path(temporary) / 'source.fbx'
        with zipfile.ZipFile(archive) as bundle:
            source.write_bytes(bundle.read(fbx))
            for source_name, (target_name, width) in maps.items():
                with bundle.open('textures/' + source_name) as stream:
                    with Image.open(stream) as image:
                        image.thumbnail((width, width), Image.Resampling.LANCZOS)
                        image.save(output / target_name, quality=88, optimize=True)
        export_models(blender, source, output, names)
    return output


def main() -> None:
    """Regenerate both optional art folders from the user's Downloads archives."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--downloads', type=Path, default=Path.home() / 'Downloads')
    parser.add_argument('--blender', default='blender')
    args = parser.parse_args()
    for specification in PACKS:
        print(export_pack(args.downloads / specification[0], args.blender, specification))


if __name__ == '__main__':
    main()
