import bpy
from collections import defaultdict, deque


def components(obj):
    mesh = obj.data
    adjacency = defaultdict(set)
    used = set()
    for poly in mesh.polygons:
        verts = list(poly.vertices)
        used.update(verts)
        for i, a in enumerate(verts):
            b = verts[(i + 1) % len(verts)]
            adjacency[a].add(b)
            adjacency[b].add(a)

    found = []
    unseen = set(used)
    while unseen:
        start = unseen.pop()
        queue = deque([start])
        group = {start}
        while queue:
            current = queue.popleft()
            for linked in adjacency[current]:
                if linked in unseen:
                    unseen.remove(linked)
                    group.add(linked)
                    queue.append(linked)

        coords = [mesh.vertices[i].co for i in group]
        minimum = tuple(min(v[axis] for v in coords) for axis in range(3))
        maximum = tuple(max(v[axis] for v in coords) for axis in range(3))
        poly_count = sum(1 for poly in mesh.polygons if poly.vertices[0] in group)
        found.append((len(group), poly_count, minimum, maximum))

    return sorted(found, key=lambda item: item[0], reverse=True)


for name in (
    'Enviorment',
    'Pinball Machine 01', 'Pinball Machine 02', 'Pinball Machine 03', 'Pinball Machine 04',
    'Arcade Machine 01', 'Arcade Machine 02',
):
    obj = bpy.data.objects.get(name)
    print(f'COMPONENTS {name}:')
    for index, (verts, polys, minimum, maximum) in enumerate(components(obj), 1):
        size = tuple(maximum[i] - minimum[i] for i in range(3))
        center = tuple((minimum[i] + maximum[i]) * 0.5 for i in range(3))
        print(
            f'  {index:02d}: verts={verts:4d} polys={polys:4d} '
            f'center=({center[0]:.3f},{center[1]:.3f},{center[2]:.3f}) '
            f'size=({size[0]:.3f},{size[1]:.3f},{size[2]:.3f})'
        )
