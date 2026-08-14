"""Apply the M2.44 layout corrections to an existing editable arcade blend without rebuilding it."""
import bpy
import bmesh
import math


TARGET_YAWS = {
    'Pinball Machine 01': -180.0,
    'Pinball Machine 02': -180.0,
    'Pinball Machine 03': -180.0,
    'Pinball Machine 04': -180.0,
    'Arcade Machine 01': -90.0,
    'Arcade Machine 02': 90.0,
}

TARGET_LOCATIONS = {
    # Keep pinballs 1/2 on the west wall, and move 3/4 beside the Doom cabinets.
    'Pinball Machine 03': (-0.65, 3.58, 0.745),
    'Pinball Machine 04': (0.45, 3.58, 0.745),
}


def move_street_fighter_door_to_right(obj):
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
        if (max(xs) - min(xs) < 0.20 and 0.80 < max(ys) - min(ys) < 1.30
                and 1.50 < max(zs) - min(zs) < 2.30 and (min(xs) + max(xs)) * 0.5 > 2.30):
            door_component = component
            break

    if door_component is None:
        bm.free()
        raise RuntimeError('Could not isolate the physical Street Fighter door component.')

    current_y = sum(vert.co.y for vert in door_component) / len(door_component)
    for vert in door_component:
        vert.co.y += 2.30 - current_y
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()


for name, yaw in TARGET_YAWS.items():
    obj = bpy.data.objects.get(name)
    if obj is None:
        raise RuntimeError(f'Missing required arcade prop: {name}')
    obj.rotation_euler = (0.0, 0.0, math.radians(yaw))
    # Once the model's real front/back axis is used, its old origin position intersects the
    # west wall. Move just the pinball row 12 cm into the room, yielding a small flush gap.
    if name in TARGET_LOCATIONS:
        obj.location = TARGET_LOCATIONS[name]
    elif name.startswith('Pinball Machine'):
        obj.location.x = -2.67

environment = bpy.data.objects.get('Enviorment')
if environment is None:
    raise RuntimeError('Missing Enviorment mesh.')
move_street_fighter_door_to_right(environment)

bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath)
print('Applied M2.44 orientation and Street Fighter door fixes to the editable Blender scene.')
