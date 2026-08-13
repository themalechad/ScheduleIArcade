#!/usr/bin/env python3
"""Convert the supplied binary FBX into a tiny runtime mesh package.

This avoids requiring Unity Editor just to test the downloaded arcade scene.
The generated package is consumed by RawArcadeSceneService at runtime.

Supported input is the provided FBX 7.4 scene: Mesh Geometry + ByPolygonVertex
normals + indexed UVs. The converter intentionally fails loudly if that shape
changes so a bad asset cannot silently generate broken collision.
"""
from __future__ import annotations
import argparse
import math
import os
import shutil
import struct
import zlib
from pathlib import Path

MAGIC = b"S1ARAW01"
UNIT_TO_METERS = 0.0254  # FBX UnitScaleFactor=2.54 cm -> one FBX unit is one inch.

TEXTURE_MAP = {
    "Enviorment": (
        "Enviorment_Texture.jpg",
        "Enviorment_Texture_NRM.jpg",
        "",
    ),
    "Pinball Machine": (
        "Pinball_base_texture.jpg",
        "Pinball_base_texture_NRM.jpg",
        "Pinball_base_texture_emissive_map.jpg",
    ),
    "Arcade Machine": (
        "Arcade_base_texture.jpg",
        "Arcade_base_texture_NRM.jpg",
        "Arcade_base_texture_emission_map.jpg",
    ),
    "Table Top": (
        "tabletop_base_texture.jpg",
        "tabletop_base_texture_NRM.jpg",
        "tabletop_base_texture_emissive_map.jpg",
    ),
    "Crane Game": (
        "crane_game_base_color.jpg",
        "",
        "",
    ),
    "Air Hockey": (
        "arcade_base_texture.jpg",
        "arcade_base_texture_for_normal_NRM.jpg",
        "arcade_base_texture_emmision_map.jpg",
    ),
    "Vendind Machine": (
        "Vending_machine_texture.jpg",
        "Vending_machine_texture_for_normal_map_NRM.jpg",
        "",
    ),
}


class Node:
    __slots__ = ("name", "props", "children")

    def __init__(self, name, props, children):
        self.name = name
        self.props = props
        self.children = children


def _read_prop(buf: bytes, off: int):
    t = chr(buf[off])
    off += 1
    if t == "Y":
        return struct.unpack_from("<h", buf, off)[0], off + 2
    if t == "C":
        return bool(buf[off]), off + 1
    if t == "I":
        return struct.unpack_from("<i", buf, off)[0], off + 4
    if t == "F":
        return struct.unpack_from("<f", buf, off)[0], off + 4
    if t == "D":
        return struct.unpack_from("<d", buf, off)[0], off + 8
    if t == "L":
        return struct.unpack_from("<q", buf, off)[0], off + 8
    if t in "fdlib":
        count, encoding, comp_len = struct.unpack_from("<III", buf, off)
        off += 12
        raw = buf[off : off + comp_len]
        off += comp_len
        if encoding == 1:
            raw = zlib.decompress(raw)
        elif encoding != 0:
            raise ValueError(f"Unsupported FBX array encoding {encoding}")
        fmt = {"f": "f", "d": "d", "l": "q", "i": "i", "b": "b"}[t]
        return list(struct.unpack("<" + fmt * count, raw)), off
    if t in "SR":
        length = struct.unpack_from("<I", buf, off)[0]
        off += 4
        raw = buf[off : off + length]
        off += length
        if t == "S":
            return raw.decode("utf-8", "replace"), off
        return raw, off
    raise ValueError(f"Unsupported FBX property type {t!r}")


def _read_node(buf: bytes, off: int, version: int):
    if version >= 7500:
        end, prop_count, prop_len = struct.unpack_from("<QQQ", buf, off)
        off += 24
        null_len = 25
    else:
        end, prop_count, prop_len = struct.unpack_from("<III", buf, off)
        off += 12
        null_len = 13
    name_len = buf[off]
    off += 1
    if end == 0:
        return None, off + (null_len - 1)
    name = buf[off : off + name_len].decode("utf-8", "replace")
    off += name_len
    props = []
    for _ in range(prop_count):
        value, off = _read_prop(buf, off)
        props.append(value)
    children = []
    child_limit = end - null_len
    while off < child_limit:
        child, next_off = _read_node(buf, off, version)
        if child is None:
            off = next_off
            break
        children.append(child)
        off = next_off
    return Node(name, props, children), int(end)


def parse_fbx(path: Path):
    buf = path.read_bytes()
    if not buf.startswith(b"Kaydara FBX Binary"):
        raise ValueError("Only binary FBX files are supported")
    version = struct.unpack_from("<I", buf, 23)[0]
    off = 27
    nodes = []
    while off < len(buf) - 13:
        node, next_off = _read_node(buf, off, version)
        if node is None:
            break
        nodes.append(node)
        off = next_off
    return version, nodes


def first(node: Node, name: str):
    for c in node.children:
        if c.name == name:
            return c
    return None


def clean_name(value):
    if isinstance(value, str):
        return value.split("\x00", 1)[0]
    return str(value)


def properties70(node: Node):
    result = {}
    p70 = first(node, "Properties70")
    if p70 is None:
        return result
    for p in p70.children:
        if p.name == "P" and len(p.props) >= 5:
            result[p.props[0]] = p.props[4:]
    return result


def vec3(values, default):
    if values is None or len(values) < 3:
        return tuple(float(x) for x in default)
    return float(values[0]), float(values[1]), float(values[2])


def mat_mul(a, b):
    return tuple(
        tuple(sum(a[r][k] * b[k][c] for k in range(3)) for c in range(3))
        for r in range(3)
    )


def mat_vec(m, v):
    return (
        m[0][0] * v[0] + m[0][1] * v[1] + m[0][2] * v[2],
        m[1][0] * v[0] + m[1][1] * v[1] + m[1][2] * v[2],
        m[2][0] * v[0] + m[2][1] * v[1] + m[2][2] * v[2],
    )


def transpose(m):
    return tuple(tuple(m[c][r] for c in range(3)) for r in range(3))


def euler_xyz(degrees):
    x, y, z = (math.radians(float(v)) for v in degrees)
    cx, sx = math.cos(x), math.sin(x)
    cy, sy = math.cos(y), math.sin(y)
    cz, sz = math.cos(z), math.sin(z)
    rx = ((1, 0, 0), (0, cx, -sx), (0, sx, cx))
    ry = ((cy, 0, sy), (0, 1, 0), (-sy, 0, cy))
    rz = ((cz, -sz, 0), (sz, cz, 0), (0, 0, 1))
    return mat_mul(rz, mat_mul(ry, rx))


def transform_for_model(model: Node):
    p = properties70(model)
    translation = vec3(p.get("Lcl Translation"), (0, 0, 0))
    rotation = vec3(p.get("Lcl Rotation"), (0, 0, 0))
    pre_rotation = vec3(p.get("PreRotation"), (0, 0, 0))
    scaling = vec3(p.get("Lcl Scaling"), (1, 1, 1))
    rotation_matrix = mat_mul(euler_xyz(pre_rotation), euler_xyz(rotation))
    return translation, rotation_matrix, scaling


def transform_position(v, translation, rotation, scaling):
    scaled = (v[0] * scaling[0], v[1] * scaling[1], v[2] * scaling[2])
    r = mat_vec(rotation, scaled)
    return (r[0] + translation[0], r[1] + translation[1], r[2] + translation[2])


def transform_normal(v, rotation, scaling):
    # Inverse-transpose of scale followed by rotation. Scaling is diagonal.
    scaled = (
        v[0] / scaling[0] if abs(scaling[0]) > 1e-9 else v[0],
        v[1] / scaling[1] if abs(scaling[1]) > 1e-9 else v[1],
        v[2] / scaling[2] if abs(scaling[2]) > 1e-9 else v[2],
    )
    r = mat_vec(rotation, scaled)
    length = math.sqrt(r[0] * r[0] + r[1] * r[1] + r[2] * r[2]) or 1.0
    return (r[0] / length, r[1] / length, r[2] / length)


def extract_scene(nodes):
    objects = next(n for n in nodes if n.name == "Objects")
    connections = next(n for n in nodes if n.name == "Connections")
    by_id = {
        obj.props[0]: obj
        for obj in objects.children
        if obj.props and isinstance(obj.props[0], int)
    }
    geom_to_model = {}
    for c in connections.children:
        if c.name != "C" or len(c.props) < 3 or c.props[0] != "OO":
            continue
        src, dst = c.props[1], c.props[2]
        if src in by_id and dst in by_id:
            if by_id[src].name == "Geometry" and by_id[dst].name == "Model":
                geom_to_model[src] = dst

    meshes = []
    for geom in (o for o in objects.children if o.name == "Geometry"):
        if geom.props[0] not in geom_to_model:
            continue
        model = by_id[geom_to_model[geom.props[0]]]
        name = clean_name(model.props[1])
        if name not in TEXTURE_MAP:
            raise ValueError(f"Unexpected mesh model {name!r}")

        vertices_node = first(geom, "Vertices")
        polygon_node = first(geom, "PolygonVertexIndex")
        normal_layer = first(geom, "LayerElementNormal")
        uv_layer = first(geom, "LayerElementUV")
        if not all((vertices_node, polygon_node, normal_layer, uv_layer)):
            raise ValueError(f"Mesh {name} is missing required geometry channels")

        mapping_n = first(normal_layer, "MappingInformationType").props[0]
        ref_n = first(normal_layer, "ReferenceInformationType").props[0]
        mapping_uv = first(uv_layer, "MappingInformationType").props[0]
        ref_uv = first(uv_layer, "ReferenceInformationType").props[0]
        if (mapping_n, ref_n) != ("ByPolygonVertex", "Direct"):
            raise ValueError(f"Unsupported normal mapping on {name}: {mapping_n}/{ref_n}")
        if (mapping_uv, ref_uv) != ("ByPolygonVertex", "IndexToDirect"):
            raise ValueError(f"Unsupported UV mapping on {name}: {mapping_uv}/{ref_uv}")

        raw_vertices = vertices_node.props[0]
        control = [tuple(raw_vertices[i : i + 3]) for i in range(0, len(raw_vertices), 3)]
        polygon_indices = polygon_node.props[0]
        raw_normals = first(normal_layer, "Normals").props[0]
        normals = [tuple(raw_normals[i : i + 3]) for i in range(0, len(raw_normals), 3)]
        raw_uv = first(uv_layer, "UV").props[0]
        uvs = [tuple(raw_uv[i : i + 2]) for i in range(0, len(raw_uv), 2)]
        uv_indices = first(uv_layer, "UVIndex").props[0]

        translation, rotation, scaling = transform_for_model(model)
        control = [transform_position(v, translation, rotation, scaling) for v in control]
        normals = [transform_normal(n, rotation, scaling) for n in normals]

        polygons = []
        current = []
        for seq, raw_index in enumerate(polygon_indices):
            end = raw_index < 0
            control_index = -raw_index - 1 if end else raw_index
            current.append((control_index, seq))
            if end:
                if len(current) >= 3:
                    polygons.append(current)
                current = []
        if current:
            raise ValueError(f"Mesh {name} has an unterminated polygon")

        expanded_pos = []
        expanded_normal = []
        expanded_uv = []
        for poly in polygons:
            for i in range(1, len(poly) - 1):
                for control_index, seq in (poly[0], poly[i], poly[i + 1]):
                    expanded_pos.append(control[control_index])
                    expanded_normal.append(normals[seq])
                    uv_index = uv_indices[seq]
                    expanded_uv.append(uvs[uv_index])

        meshes.append(
            {
                "name": name,
                "positions": expanded_pos,
                "normals": expanded_normal,
                "uvs": expanded_uv,
                "textures": TEXTURE_MAP[name],
            }
        )

    if len(meshes) != 7:
        raise ValueError(f"Expected 7 scene meshes, found {len(meshes)}")

    environment = next(m for m in meshes if m["name"] == "Enviorment")
    env_pos = environment["positions"]
    min_x = min(v[0] for v in env_pos) * UNIT_TO_METERS
    max_x = max(v[0] for v in env_pos) * UNIT_TO_METERS
    floor_y = min(v[1] for v in env_pos) * UNIT_TO_METERS
    min_z = min(v[2] for v in env_pos) * UNIT_TO_METERS
    max_z = max(v[2] for v in env_pos) * UNIT_TO_METERS
    origin = ((min_x + max_x) * 0.5, floor_y, (min_z + max_z) * 0.5)

    for mesh in meshes:
        centered = []
        for x, y, z in mesh["positions"]:
            centered.append(
                (
                    x * UNIT_TO_METERS - origin[0],
                    y * UNIT_TO_METERS - origin[1],
                    z * UNIT_TO_METERS - origin[2],
                )
            )
        mesh["positions"] = centered

    return meshes


def write_string(fp, value: str):
    raw = value.encode("utf-8")
    fp.write(struct.pack("<i", len(raw)))
    fp.write(raw)


def write_scene(meshes, output: Path):
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("wb") as fp:
        fp.write(MAGIC)
        fp.write(struct.pack("<i", 1))  # format version
        fp.write(struct.pack("<i", len(meshes)))
        for mesh in meshes:
            write_string(fp, mesh["name"])
            for texture_name in mesh["textures"]:
                write_string(fp, texture_name)
            count = len(mesh["positions"])
            fp.write(struct.pack("<i", count))
            for p in mesh["positions"]:
                fp.write(struct.pack("<fff", *p))
            for n in mesh["normals"]:
                fp.write(struct.pack("<fff", *n))
            for uv in mesh["uvs"]:
                fp.write(struct.pack("<ff", *uv))
            # Vertices are intentionally expanded one-per-triangle-corner, so indices are sequential.
            fp.write(struct.pack("<i", count))
            for i in range(count):
                fp.write(struct.pack("<i", i))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("fbx", type=Path)
    parser.add_argument("textures", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("output_textures", type=Path)
    args = parser.parse_args()

    version, nodes = parse_fbx(args.fbx)
    if version != 7400:
        raise ValueError(f"This test converter expects FBX 7400, got {version}")
    meshes = extract_scene(nodes)
    write_scene(meshes, args.output)

    args.output_textures.mkdir(parents=True, exist_ok=True)
    required = set()
    for mesh in meshes:
        required.update(x for x in mesh["textures"] if x)
    # Also copy a few maps used by the runtime door/screen helper.
    required.add("Bright_map.jpg")
    required.add("Enviorment_Texture.jpg")
    for name in sorted(required):
        src = args.textures / name
        if not src.exists():
            raise FileNotFoundError(src)
        shutil.copy2(src, args.output_textures / name)

    print(f"Wrote {args.output} ({args.output.stat().st_size:,} bytes)")
    for mesh in meshes:
        print(f"  {mesh['name']}: {len(mesh['positions']):,} expanded vertices")
    print(f"Copied {len(required)} runtime textures to {args.output_textures}")


if __name__ == "__main__":
    main()
