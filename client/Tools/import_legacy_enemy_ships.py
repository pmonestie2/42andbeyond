"""Convert the extracted enemy catalog with original materials inside Blender."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
import tempfile
import struct

import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))
from extract_legacy_effect_assets import read_ini
from import_legacy_ships import clear_models, export_hull, ignore_animation, sanitize_stage1_mapping, staged_sources


def chunks(data: bytes) -> list[tuple[int, bytes]]:
    """Returns:
        list[tuple[int, bytes]]: Bounded native chunk payloads in file order.

    Raises:
        ValueError: A chunk extends beyond the original file.
    """
    result = []
    offset = 0
    while offset + 8 <= len(data):
        kind, length = struct.unpack_from("<II", data, offset)
        length &= 0x7fffffff
        if offset + 8 + length > len(data):
            raise ValueError("Truncated W3D chunk")
        result.append((kind, data[offset + 8:offset + 8 + length]))
        offset += 8 + length
    return result


def native_name(data: bytes) -> str:
    """Returns:
        str: A null-terminated native asset or bone name.
    """
    return data.split(b"\0", 1)[0].decode("ascii").lower()


def load_assembly(stem: str, staging: Path, originals: dict[str, Path], visited: tuple = ()) -> tuple[list, object]:
    """Returns:
        tuple[list, object]: Original body meshes and their rest-pose skeleton.

    Raises:
        ValueError: A required body file is missing or recursively references itself.
    """
    from mathutils import Matrix
    if stem in visited:
        raise ValueError("Cyclic model assembly: " + stem)
    model = originals.get(stem + ".w3d")
    if model is None:
        raise ValueError("Missing assembly body: " + stem)
    records = chunks(model.read_bytes())
    if records and records[0][0] == 0x600:
        info = next(payload for kind, payload in chunks(records[0][1]) if kind == 0x602)
        base = native_name(info[:32])
        meshes, rig = load_assembly(base, staging, originals, visited + (stem,))
        count = struct.unpack_from("<I", info, 32)[0]
        for index in range(count):
            entry = info[36 + index * 64:100 + index * 64]
            identifier, bone_name = native_name(entry[:32]), native_name(entry[32:64])
            child, _, mesh_name = identifier.partition(".")
            if child + ".w3d" not in originals:
                continue  # Native particle emitters share this table with model attachments.
            attached, _ = load_assembly(child, staging, originals, visited + (stem,))
            bone = next((bone for bone in rig.data.bones if bone.name.lower() == bone_name), None) if rig else None
            matrix = rig.matrix_world @ bone.matrix_local if bone else Matrix.Identity(4)
            for obj in attached:
                if mesh_name and not obj.name.lower().startswith(mesh_name):
                    continue
                obj.matrix_world = matrix @ obj.matrix_world
                meshes.append(obj)
        return meshes, rig
    before = set(bpy.data.objects)
    bpy.ops.import_mesh.westwood_w3d(filepath=str(staging / model.name))
    added = [obj for obj in bpy.data.objects if obj not in before]
    meshes = [obj for obj in added if obj.type == "MESH" and obj.visible_get() and "flare" not in obj.name.lower()]
    rig = next((obj for obj in added if obj.type == "ARMATURE"), None)
    if meshes:
        return meshes, rig
    # Some native HLODs contain only bone attachments to separate hull files.
    for kind, payload in records:
        if kind != 0x700:
            continue
        records = chunks(payload)
        lods = [data for child, data in records if child == 0x702]
        arrays = lods[-1:] + [data for child, data in records if child == 0x705]
        if not arrays:
            continue
        for child_kind, child_data in [record for array in arrays for record in chunks(array)]:
            if child_kind != 0x704:
                continue
            bone_index = struct.unpack_from("<I", child_data)[0]
            identifier = native_name(child_data[4:36])
            child, _, mesh_name = identifier.partition(".")
            if child + ".w3d" not in originals or child == stem:
                continue
            attached, _ = load_assembly(child, staging, originals, visited + (stem,))
            matrix = rig.matrix_world @ rig.data.bones[bone_index].matrix_local if rig and bone_index < len(rig.data.bones) else Matrix.Identity(4)
            for obj in attached:
                if mesh_name and not obj.name.lower().startswith(mesh_name):
                    continue
                obj.matrix_world = matrix @ obj.matrix_world
                meshes.append(obj)
    return meshes, rig


def repair_material_indices() -> None:
    """Use native vertex material IDs instead of texture IDs when binding multi-pass materials."""
    from io_mesh_w3d.common.utils import material_import, mesh_import

    def create_materials(context, principleds, structure, mesh, b_mesh, name, triangles, mesh_ob):
        """Bind original textures to their declared mesh material slots."""
        if len(structure.material_passes) == 1 and len(structure.textures) > 1:
            vertex = structure.vert_materials[0]
            original_name = vertex.vm_name
            for texture in structure.textures:
                vertex.vm_name = texture.id
                material, principled = material_import.create_material_from_vertex_material(name, vertex)
                mesh.materials.append(material); principleds.append(principled)
                nodes = material.node_tree
                node = nodes.nodes.new("ShaderNodeTexImage")
                node.image = material_import.find_texture(context, texture.file, texture.id)
                shader = nodes.nodes.get("Principled BSDF")
                nodes.links.new(node.outputs["Color"], shader.inputs["Base Color"])
                nodes.links.new(node.outputs["Alpha"], shader.inputs["Alpha"])
            vertex.vm_name = original_name
            untextured, _ = material_import.create_material_from_vertex_material(name + ".untextured", vertex)
            mesh.materials.append(untextured)
            material_pass = structure.material_passes[0]
            material_import.create_uvlayer(context, mesh, b_mesh, triangles, material_pass)
            ids = material_pass.tx_stages[0].tx_ids[0]
            for face in mesh.polygons:
                texture_id = ids[face.index] if face.index < len(ids) else ids[0]
                face.material_index = texture_id if texture_id < len(structure.textures) else len(structure.textures)
            return
        for vertex in structure.vert_materials:
            material, principled = material_import.create_material_from_vertex_material(name, vertex)
            mesh.materials.append(material); principleds.append(principled)
        for index, material_pass in enumerate(structure.material_passes):
            material_import.create_uvlayer(context, mesh, b_mesh, triangles, material_pass)
            if not material_pass.tx_stages:
                continue
            material_id = material_pass.vertex_material_ids[0]
            texture_id = material_pass.tx_stages[0].tx_ids[0][0]
            if material_id >= len(mesh.materials) or texture_id >= len(structure.textures):
                raise ValueError("Native material or texture ID is outside its declared table")
            texture = structure.textures[texture_id]
            image = material_import.find_texture(context, texture.file, texture.id)
            nodes = mesh.materials[material_id].node_tree
            shader = nodes.nodes.get("Principled BSDF")
            node = nodes.nodes.new("ShaderNodeTexImage"); node.image = image
            nodes.links.new(node.outputs["Color"], shader.inputs["Base Color"])
            nodes.links.new(node.outputs["Alpha"], shader.inputs["Alpha"])
            if index == 0:
                ids = material_pass.vertex_material_ids
                for face in mesh.polygons:
                    face.material_index = ids[face.index] if face.index < len(ids) else ids[0]
        for material in mesh.materials:
            material_import.set_blend_method(material, "CLIP")

    mesh_import.create_vertex_material = create_materials


def convert(source: Path, output: Path, addon: Path, reuse_existing: bool = False) -> None:
    """Export each mapped enemy once and publish a server-ID resource catalog.

    Raises:
        ValueError: No original model can be resolved for a catalog entry.
    """
    sys.path.insert(0, str(addon.resolve()))
    import io_mesh_w3d
    from io_mesh_w3d import import_utils
    from io_mesh_w3d.common.utils import material_import
    import_utils.create_animation = ignore_animation
    material_import.create_material_from_vertex_material = sanitize_stage1_mapping(
        material_import.create_material_from_vertex_material)
    io_mesh_w3d.register()
    repair_material_indices()
    manifest = json.loads((source / "manifest.json").read_text())
    bases = read_ini(source / "definitions/basset.ini")
    states = read_ini(source / "definitions/rs.ini")
    mappings, failures, converted = [], [], set()
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="42-enemy-conversion-") as temporary:
        staging = Path(temporary)
        originals = staged_sources(staging, source)
        for spec in manifest["assets"]:
            base = bases[f"base-{spec['asset']}"]
            name = base.get("file", "")
            if not name and "rslid" in base:
                name = states.get(base["rslid"].lower() + "\\state0", {}).get("object", "")
            stem = Path(name).stem.lower()
            if not stem:
                failures.append({"asset": spec["asset"], "error": "No root model"})
                continue
            if stem not in converted:
                try:
                    if not reuse_existing or not (output / (stem + ".obj")).is_file():
                        clear_models()
                        hull, _ = load_assembly(stem, staging, originals)
                        if not hull:
                            raise ValueError("No visible original hull: " + stem)
                        export_hull(stem, hull, output)
                    converted.add(stem)
                except Exception as error:
                    failures.append({"asset": spec["asset"], "model": stem, "error": str(error)})
                    print("ENEMY_CONVERSION_FAILURE", stem, repr(error), flush=True)
                    continue
            mappings.append(f"{spec['asset']}\t{stem}")
    (output / "Catalog.txt").write_text("\n".join(mappings) + "\n")
    (output / "conversion.json").write_text(json.dumps({"mapped": len(mappings),
        "models": sorted(converted), "failures": failures}, indent=2) + "\n")
    print(f"ENEMY_CONVERSION: {len(mappings)} IDs, {len(converted)} models, {len(failures)} failures", flush=True)


def main() -> None:
    """Convert the supplied original source bundle into Unity resources."""
    args = sys.argv[sys.argv.index("--") + 1:]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--addon-root", required=True, type=Path)
    parser.add_argument("--reuse-existing", action="store_true")
    options = parser.parse_args(args)
    convert(options.source, options.output, options.addon_root, options.reuse_existing)


if __name__ == "__main__":
    main()
