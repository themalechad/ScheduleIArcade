import bpy
import bmesh
import math
import os
import sys
from pathlib import Path
from mathutils import Vector

PROJECT_ROOT = Path(sys.argv[sys.argv.index('--') + 1]).resolve() if '--' in sys.argv else Path.cwd()
FBX_PATH = PROJECT_ROOT / 'UnityAssetsToCopy' / 'Assets' / 'ScheduleIArcade' / 'Source' / 'arcade.fbx'
TEXTURE_ROOT = PROJECT_ROOT / 'UnityAssetsToCopy' / 'Assets' / 'ScheduleIArcade' / 'Textures'
OUT_DIR = PROJECT_ROOT / 'BlenderEditable'
OUT_BLEND = OUT_DIR / 'ScheduleIArcade_Editable.blend'
OUT_DIR.mkdir(parents=True, exist_ok=True)

CANONICAL = [
    'Enviorment',
    'Pinball Machine',
    'Arcade Machine',
    'Table Top',
    'Crane Game',
    'Air Hockey',
    'Vendind Machine',
]

TEXTURES = {
    'Enviorment': ('Enviorment_Texture.jpg', None, None),
    'Pinball Machine': ('Pinball_base_texture.jpg', 'Pinball_base_texture_NRM.jpg', 'Pinball_base_texture_emissive_map.jpg'),
    'Arcade Machine': ('Arcade_base_texture.jpg', 'Arcade_base_texture_NRM.jpg', 'Arcade_base_texture_emission_map.jpg'),
    'Table Top': ('tabletop_base_texture.jpg', 'tabletop_base_texture_NRM.jpg', 'tabletop_base_texture_emissive_map.jpg'),
    'Crane Game': ('crane_game_base_color.jpg', None, None),
    'Air Hockey': ('arcade_base_texture.jpg', 'arcade_base_texture_for_normal_NRM.jpg', 'arcade_base_texture_emmision_map.jpg'),
    'Vendind Machine': ('Vending_machine_texture.jpg', 'Vending_machine_texture_for_normal_map_NRM.jpg', None),
}

# Runtime M2.38 fallback targets, converted Unity (x,y,z) -> Blender (x,-z,y).
TARGETS = {
    # The source geometry's cabinet/pinball facing axis is local -X (not local Z). Runtime yaw
    # is the inverse of Blender Z yaw: pinball backboxes (local +X) therefore point west at 180°,
    # while upright screens (local -X) point north into the room at +90°.
    'Pinball Machine 01': ((-2.67,  1.55, 0.745), -180.0),
    'Pinball Machine 02': ((-2.67,  0.52, 0.745), -180.0),
    'Pinball Machine 03': ((-2.67, -0.52, 0.745), -180.0),
    'Pinball Machine 04': ((-2.67, -1.55, 0.745), -180.0),
    'Arcade Machine 01': ((-2.34, 3.58, 0.779), -90.0),
    'Arcade Machine 02': ((-1.66, 3.58, 0.779), -90.0),
    'Table Top': ((0.00, 0.00, 0.317), -90.0),
}

FX = lambda x: x * 1.22
FZ = lambda z: z * 1.34
A = Vector((FX(-2.67), FZ(1.56)))
B = Vector((FX(-0.84), FZ(3.06)))
DOOR_T = 0.53
DOOR_RUNTIME = A.lerp(B, DOOR_T)
WALL_ANGLE_RUNTIME = math.degrees(math.atan2((B-A).x, (B-A).y))


def clear_scene():
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    for datablocks in (bpy.data.meshes, bpy.data.materials, bpy.data.images, bpy.data.curves):
        # Keep safe; imported assets will be recreated.
        pass


def canonical_name(name: str):
    low = name.lower()
    for c in CANONICAL:
        if low == c.lower() or low.startswith(c.lower() + '.') or c.lower() in low:
            return c
    return None


def find_object(canonical):
    for obj in bpy.context.scene.objects:
        if obj.type == 'MESH' and canonical_name(obj.name) == canonical:
            return obj
    return None


def apply_rot_scale(obj):
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)


def bbox_world(obj):
    pts = [obj.matrix_world @ Vector(c) for c in obj.bound_box]
    xs = [p.x for p in pts]; ys=[p.y for p in pts]; zs=[p.z for p in pts]
    return min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)


def set_origin_geometry(obj):
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.origin_set(type='ORIGIN_GEOMETRY', center='BOUNDS')


def split_by_nearest_xy(obj, names, seeds):
    if obj is None:
        raise RuntimeError(f'Cannot split missing object into {names}.')

    face_groups = []
    for poly in obj.data.polygons:
        center = poly.center
        face_groups.append(min(range(len(seeds)), key=lambda i: (center.x-seeds[i][0])**2 + (center.y-seeds[i][1])**2))

    created = []
    for group_index, name in enumerate(names):
        mesh = obj.data.copy()
        split = obj.copy()
        split.data = mesh
        split.name = name
        split.animation_data_clear()
        bpy.context.scene.collection.objects.link(split)

        bm = bmesh.new()
        bm.from_mesh(mesh)
        bm.faces.ensure_lookup_table()
        remove = [face for face in bm.faces if face_groups[face.index] != group_index]
        bmesh.ops.delete(bm, geom=remove, context='FACES')
        bm.to_mesh(mesh)
        bm.free()
        mesh.update()
        set_origin_geometry(split)
        created.append(split)

    bpy.data.objects.remove(obj, do_unlink=True)
    return created


def remove_imported_ceiling_light_bars(obj):
    """Remove the two long hanging FBX light-bar components visible below the new tile ceiling."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    unseen = set(bm.verts)
    remove = []
    removed_components = 0
    while unseen:
        start = unseen.pop()
        stack = [start]
        component = [start]
        while stack:
            current = stack.pop()
            for edge in current.link_edges:
                linked = edge.other_vert(current)
                if linked in unseen:
                    unseen.remove(linked)
                    stack.append(linked)
                    component.append(linked)

        xs = [vert.co.x for vert in component]
        ys = [vert.co.y for vert in component]
        zs = [vert.co.z for vert in component]
        size_x = max(xs) - min(xs)
        size_y = max(ys) - min(ys)
        size_z = max(zs) - min(zs)
        center_z = (min(zs) + max(zs)) * 0.5
        long_axis = max(size_x, size_y)
        short_axis = min(size_x, size_y)
        if center_z > 2.55 and 2.0 < long_axis < 3.0 and short_axis < 0.20 and size_z < 0.15:
            remove.extend(component)
            removed_components += 1

    if remove:
        bmesh.ops.delete(bm, geom=remove, context='VERTS')
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    print(f'Removed {removed_components} imported long ceiling-light component(s).')


def move_street_fighter_door_to_right(obj):
    """Move only the separately-authored east-wall door; the mural mesh and UVs stay untouched."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    unseen = set(bm.verts)
    door_component = None
    while unseen:
        start = unseen.pop()
        stack = [start]
        component = [start]
        while stack:
            current = stack.pop()
            for edge in current.link_edges:
                linked = edge.other_vert(current)
                if linked in unseen:
                    unseen.remove(linked)
                    stack.append(linked)
                    component.append(linked)

        xs = [vert.co.x for vert in component]
        ys = [vert.co.y for vert in component]
        zs = [vert.co.z for vert in component]
        size_x = max(xs) - min(xs)
        size_y = max(ys) - min(ys)
        size_z = max(zs) - min(zs)
        center_x = (min(xs) + max(xs)) * 0.5
        # This is the one physical door component: thin across the east wall, about one metre
        # wide, almost two metres high. It is independent from the Street Fighter mural behind it.
        if size_x < 0.20 and 0.80 < size_y < 1.30 and 1.50 < size_z < 2.30 and center_x > 2.30:
            door_component = component
            break

    if door_component is None:
        bm.free()
        raise RuntimeError('Could not isolate the physical Street Fighter wall door component.')

    current_y = sum(vert.co.y for vert in door_component) / len(door_component)
    target_y = 2.30  # Blender Y=-runtime Z; this is the right/south end when viewed from inside.
    for vert in door_component:
        vert.co.y += target_y - current_y
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    print('Moved physical Street Fighter door to the right side; mural geometry and UVs were not flipped.')


def load_image(name):
    if not name:
        return None
    path = TEXTURE_ROOT / name
    if not path.exists():
        return None
    for img in bpy.data.images:
        try:
            if Path(bpy.path.abspath(img.filepath)).resolve() == path.resolve():
                return img
        except Exception:
            pass
    return bpy.data.images.load(str(path), check_existing=True)


def make_material(canonical):
    mat = bpy.data.materials.get('S1_' + canonical) or bpy.data.materials.new('S1_' + canonical)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    links = mat.node_tree.links
    nodes.clear()
    out = nodes.new('ShaderNodeOutputMaterial')
    bsdf = nodes.new('ShaderNodeBsdfPrincipled')
    links.new(bsdf.outputs['BSDF'], out.inputs['Surface'])

    albedo, normal, emission = TEXTURES[canonical]
    img = load_image(albedo)
    if img:
        tex = nodes.new('ShaderNodeTexImage'); tex.image = img; tex.label = albedo
        links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    nimg = load_image(normal)
    if nimg:
        nimg.colorspace_settings.name = 'Non-Color'
        texn = nodes.new('ShaderNodeTexImage'); texn.image = nimg; texn.label = normal
        nmap = nodes.new('ShaderNodeNormalMap')
        links.new(texn.outputs['Color'], nmap.inputs['Color'])
        links.new(nmap.outputs['Normal'], bsdf.inputs['Normal'])
    eimg = load_image(emission)
    if eimg:
        texe = nodes.new('ShaderNodeTexImage'); texe.image = eimg; texe.label = emission
        # Blender 4.x uses Emission Color; older versions use Emission.
        target = bsdf.inputs.get('Emission Color') or bsdf.inputs.get('Emission')
        if target:
            links.new(texe.outputs['Color'], target)
        estr = bsdf.inputs.get('Emission Strength')
        if estr:
            estr.default_value = 0.5
    bsdf.inputs['Roughness'].default_value = 0.72
    return mat


def assign_material(obj, mat):
    obj.data.materials.clear()
    obj.data.materials.append(mat)


def add_guide_box(name, loc, scale, color, rot_z=0.0):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=loc, rotation=(0,0,math.radians(rot_z)))
    obj = bpy.context.active_object
    obj.name = name
    obj.scale = scale
    mat = bpy.data.materials.new(name + '_MAT')
    mat.diffuse_color = (*color, 0.30)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get('Principled BSDF')
    if bsdf:
        bsdf.inputs['Base Color'].default_value = (*color, 1)
        bsdf.inputs['Alpha'].default_value = 0.28
    mat.surface_render_method = 'DITHERED' if hasattr(mat, 'surface_render_method') else getattr(mat, 'blend_method', None)
    obj.data.materials.append(mat)
    obj.display_type = 'WIRE'
    obj.show_in_front = True
    return obj


def add_empty(name, loc, rot_z=0.0):
    obj = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = loc
    obj.rotation_euler[2] = math.radians(rot_z)
    obj.empty_display_type = 'ARROWS'
    obj.empty_display_size = 0.55
    obj.show_in_front = True
    return obj


if not FBX_PATH.exists():
    raise RuntimeError(f'Missing source FBX: {FBX_PATH}')

clear_scene()
bpy.context.scene.unit_settings.system = 'METRIC'
bpy.context.scene.unit_settings.scale_length = 1.0

bpy.ops.import_scene.fbx(filepath=str(FBX_PATH), use_image_search=False)

# Normalize imported model transforms first.
for obj in list(bpy.context.scene.objects):
    if obj.type == 'MESH' and canonical_name(obj.name):
        c = canonical_name(obj.name)
        obj.name = c
        # The source FBX carries a one-frame Take for every object. If those actions remain
        # attached they silently restore the original position/rotation/scale whenever Blender
        # evaluates the scene, overriding both the corrected starting layout and later user edits.
        # Preserve the currently evaluated transform, then remove the stale animation before
        # baking rotation/scale into the mesh.
        obj.animation_data_clear()
        apply_rot_scale(obj)

# Center the imported scene at floor level so Blender coordinates line up with runtime room space.
env = find_object('Enviorment')
if env is None:
    raise RuntimeError('Could not find Enviorment mesh after FBX import.')
minx,maxx,miny,maxy,minz,maxz = bbox_world(env)
shift = Vector((-(minx+maxx)*0.5, -(miny+maxy)*0.5, -minz))
for obj in bpy.context.scene.objects:
    if obj.parent is None and canonical_name(obj.name):
        obj.location += shift

# Apply environment translation into geometry so it exports with origin zero.
bpy.ops.object.select_all(action='DESELECT')
env.select_set(True); bpy.context.view_layer.objects.active = env
bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
remove_imported_ceiling_light_bars(env)
move_street_fighter_door_to_right(env)

# Give props clean own-object pivots and assign materials.
for canonical in CANONICAL:
    obj = find_object(canonical)
    if obj is None:
        print('WARNING: missing', canonical)
        continue
    if canonical != 'Enviorment':
        set_origin_geometry(obj)
    assign_material(obj, make_material(canonical))

# The source pack combines four pinball cabinets and two upright cabinets into two large objects.
# Split them into independently editable machines, grouping each cabinet's disconnected trim/control
# pieces by the nearest known cabinet center.
split_by_nearest_xy(
    find_object('Pinball Machine'),
    [f'Pinball Machine {i:02d}' for i in range(1, 5)],
    [(-0.528,-0.320), (-0.528,0.298), (0.523,0.309), (0.509,-0.308)])
split_by_nearest_xy(
    find_object('Arcade Machine'),
    ['Arcade Machine 01', 'Arcade Machine 02'],
    [(-0.306,0.007), (0.305,-0.006)])

# Hide old placeholder machines but keep them in the file so the exporter still has all 7 meshes.
for canonical in ('Crane Game', 'Vendind Machine'):
    obj = find_object(canonical)
    if obj:
        obj.hide_set(True)
        obj.hide_render = True

# Apply the corrected default placements. Everything remains fully editable by the user.
for object_name, (loc, yaw) in TARGETS.items():
    obj = bpy.data.objects.get(object_name)
    if obj:
        obj.location = loc
        obj.rotation_euler = (0.0, 0.0, math.radians(yaw))

# Colored edit guides matching the user's annotation.
add_guide_box('GREEN_PINBALL_GUIDE', (-3.10, 0.0, 1.0), (0.08, 2.10, 1.05), (0.0,1.0,0.0))
add_guide_box('ORANGE_ARCADE_CABINET_GUIDE', (-2.0, 3.85, 1.0), (0.8, 0.12, 1.05), (1.0,0.35,0.0))
add_guide_box('BLUE_PACMAN_GUIDE', (0.0, 0.0, 0.08), (0.75, 1.25, 0.08), (0.0,0.3,1.0))
# Runtime chamfer -> Blender (x,-z,y). Local runtime wall yaw -> Blender Z yaw negated.
door_bl = (DOOR_RUNTIME.x, -DOOR_RUNTIME.y, 1.09)
add_guide_box('RED_DOOR_GUIDE', door_bl, (0.12, 0.95, 1.09), (1.0,0.0,0.0), rot_z=-WALL_ANGLE_RUNTIME)
door_empty = add_empty('ARCADE_DOOR_TARGET', (DOOR_RUNTIME.x, -DOOR_RUNTIME.y, 0.0), rot_z=-WALL_ANGLE_RUNTIME)
door_empty['door_t'] = DOOR_T

# Put guides in their own collection for easy visibility toggling.
guides = bpy.data.collections.get('LAYOUT_GUIDES') or bpy.data.collections.new('LAYOUT_GUIDES')
if guides.name not in bpy.context.scene.collection.children:
    bpy.context.scene.collection.children.link(guides)
for obj in list(bpy.context.scene.objects):
    if obj.name.endswith('_GUIDE') or obj.name == 'ARCADE_DOOR_TARGET':
        for col in list(obj.users_collection):
            col.objects.unlink(obj)
        guides.objects.link(obj)

# Helpful viewport defaults.
bpy.context.scene.world.color = (0.015,0.015,0.025)
bpy.ops.wm.save_as_mainfile(filepath=str(OUT_BLEND))
print(f'Schedule I Arcade editable Blender scene saved to: {OUT_BLEND}')
