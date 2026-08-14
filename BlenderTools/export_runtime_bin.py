import bpy
import math
import struct
import sys
from pathlib import Path
from mathutils import Vector, Matrix

PROJECT_ROOT = Path(sys.argv[sys.argv.index('--') + 1]).resolve() if '--' in sys.argv else Path.cwd()
OUT_BIN = Path(sys.argv[sys.argv.index('--') + 2]).resolve() if '--' in sys.argv and len(sys.argv) > sys.argv.index('--') + 2 else PROJECT_ROOT / 'dist' / 'Mods' / 'ScheduleIArcade' / 'Assets' / 'RawScene' / 'arcade_runtime_meshes.bin'
OUT_LAYOUT = OUT_BIN.with_name('arcade_layout.txt')
OUT_BIN.parent.mkdir(parents=True, exist_ok=True)

MAGIC=b'S1ARAW01'
ORDER=[
 'Enviorment',
 'Pinball Machine 01','Pinball Machine 02','Pinball Machine 03','Pinball Machine 04',
 'Arcade Machine 01','Arcade Machine 02',
 'Table Top','Crane Game','Air Hockey','Vendind Machine'
]
TEXTURES={
 'Enviorment':('Enviorment_Texture.jpg','Enviorment_Texture_NRM.jpg',''),
 # The source AC/DC map has incompatible mirrored UV islands. Use one label-free Space Cadet
 # pinball skin for every cabinet so it remains visually correct across each mirrored pair.
 'Pinball Machine 01':('Pinball_SpaceCadetTexture.png','',''),
 'Pinball Machine 02':('Pinball_SpaceCadetTexture.png','',''),
 'Pinball Machine 03':('Pinball_SpaceCadetTexture.png','',''),
 'Pinball Machine 04':('Pinball_SpaceCadetTexture.png','',''),
 'Arcade Machine 01':('Arcade_base_texture.jpg','Arcade_base_texture_NRM.jpg','Arcade_base_texture_emission_map.jpg'),
 'Arcade Machine 02':('Arcade_base_texture.jpg','Arcade_base_texture_NRM.jpg','Arcade_base_texture_emission_map.jpg'),
 'Table Top':('tabletop_base_texture.jpg','tabletop_base_texture_NRM.jpg','tabletop_base_texture_emissive_map.jpg'),
 'Crane Game':('crane_game_base_color.jpg','',''),
 'Air Hockey':('air_hockey_base_texture.jpg','air_hockey_base_texture_NRM.jpg','air_hockey_base_texture_emission.jpg'),
 'Vendind Machine':('Vending_machine_texture.jpg','Vending_machine_texture_for_normal_map_NRM.jpg',''),
}

def find_obj(name):
    for obj in bpy.context.scene.objects:
        if obj.type=='MESH' and (obj.name==name or obj.name.startswith(name+'.')):
            return obj
    raise RuntimeError(f'Missing required mesh object: {name}')

def bvec_to_runtime(v):
    # Blender setup uses X right, Y = -runtime Z, Z up.
    return (float(v.x), float(v.z), float(-v.y))

def write_string(fp,s):
    b=s.encode('utf-8'); fp.write(struct.pack('<i',len(b))); fp.write(b)

def world_decompose(obj):
    loc, rot, scale = obj.matrix_world.decompose()
    e = rot.to_euler('XYZ')
    if abs(math.degrees(e.x)) > 0.5 or abs(math.degrees(e.y)) > 0.5:
        print(f'WARNING {obj.name}: only upright Z rotation is preserved; X/Y tilt will be ignored.')
    return loc, e.z, scale

def export_mesh(obj,name):
    deps=bpy.context.evaluated_depsgraph_get()
    eval_obj=obj.evaluated_get(deps)
    mesh=eval_obj.to_mesh()
    mesh.calc_loop_triangles()
    uv_layer=mesh.uv_layers.active.data if mesh.uv_layers.active else None
    loc, yaw_z, scale=world_decompose(obj)
    pos_runtime=bvec_to_runtime(loc)
    yaw_runtime=-math.degrees(yaw_z)

    positions=[]; normals=[]; uvs=[]
    # Environment is exported in world space because its origin/architecture is room-relative.
    if name=='Enviorment':
        pos_runtime=(0.0,0.0,0.0); yaw_runtime=0.0
        normal_mat=obj.matrix_world.to_3x3().inverted().transposed()
        for tri in mesh.loop_triangles:
            for li in tri.loops:
                vi=mesh.loops[li].vertex_index
                p=obj.matrix_world @ mesh.vertices[vi].co
                n=(normal_mat @ mesh.loops[li].normal).normalized()
                positions.append(bvec_to_runtime(p))
                normals.append(bvec_to_runtime(n))
                uv=uv_layer[li].uv if uv_layer else (0.0,0.0)
                uvs.append((float(uv[0]),float(uv[1])))
    else:
        # Props remain centered/local; runtime applies authored position+yaw as GameObject transform.
        sx,sy,sz=scale.x,scale.y,scale.z
        for tri in mesh.loop_triangles:
            for li in tri.loops:
                vi=mesh.loops[li].vertex_index
                p=mesh.vertices[vi].co
                p=Vector((p.x*sx,p.y*sy,p.z*sz))
                n=mesh.loops[li].normal
                n=Vector((n.x/(sx or 1.0),n.y/(sy or 1.0),n.z/(sz or 1.0))).normalized()
                positions.append(bvec_to_runtime(p))
                normals.append(bvec_to_runtime(n))
                uv=uv_layer[li].uv if uv_layer else (0.0,0.0)
                uvs.append((float(uv[0]),float(uv[1])))
    eval_obj.to_mesh_clear()
    return pos_runtime,yaw_runtime,positions,normals,uvs

def transformed_xz_bounds(exported):
    pos,yaw,positions=exported['pos'],exported['yaw'],exported['positions']
    a=math.radians(yaw); c=math.cos(a); s=math.sin(a)
    xs=[]; zs=[]
    for x,_,z in positions:
        xs.append(pos[0] + c*x + s*z)
        zs.append(pos[2] - s*x + c*z)
    return (min(xs),max(xs),min(zs),max(zs))

def point_in_convex_polygon(x,z,polygon,tolerance=0.025):
    signs=[]
    for i,(ax,az) in enumerate(polygon):
        bx,bz=polygon[(i+1)%len(polygon)]
        cross=(bx-ax)*(z-az)-(bz-az)*(x-ax)
        edge_len=max(0.001,math.hypot(bx-ax,bz-az))
        signs.append(cross/edge_len)
    return not (min(signs)<-tolerance and max(signs)>tolerance)

def validate_layout(exports):
    # Exact runtime five-sided footprint. Hidden placeholder meshes deliberately have no collider
    # and are excluded; every visible cabinet must stay inside and clear every other visible prop.
    fx=lambda x:x*1.22; fz=lambda z:z*1.34
    polygon=[(fx(-2.67),fz(-3.02)),(fx(-2.67),fz(1.56)),(fx(-0.84),fz(3.06)),(fx(2.64),fz(3.06)),(fx(2.64),fz(-3.02))]
    visible={
        'Pinball Machine 01','Pinball Machine 02','Pinball Machine 03','Pinball Machine 04',
        'Arcade Machine 01','Arcade Machine 02','Table Top','Air Hockey'
    }
    active=[e for e in exports if e['name'] in visible]
    errors=[]
    bounds={e['name']:transformed_xz_bounds(e) for e in active}

    for e in active:
        pos,yaw,positions=e['pos'],e['yaw'],e['positions']
        a=math.radians(yaw); c=math.cos(a); s=math.sin(a)
        for x,_,z in positions:
            wx=pos[0]+c*x+s*z; wz=pos[2]-s*x+c*z
            if not point_in_convex_polygon(wx,wz,polygon):
                errors.append(f"{e['name']} crosses the finished room footprint near ({wx:.2f},{wz:.2f})")
                break

    for i,a in enumerate(active):
        aminx,amaxx,aminz,amaxz=bounds[a['name']]
        for b in active[i+1:]:
            bminx,bmaxx,bminz,bmaxz=bounds[b['name']]
            overlap_x=min(amaxx,bmaxx)-max(aminx,bminx)
            overlap_z=min(amaxz,bmaxz)-max(aminz,bminz)
            if overlap_x>0.02 and overlap_z>0.02:
                errors.append(f"{a['name']} overlaps {b['name']} by about {overlap_x:.2f}m x {overlap_z:.2f}m")

    # The two west-wall pinballs and the second row beside the Doom machines must each stay
    # flush to their intended wall, without clipping it.
    for name,pin in bounds.items():
        if not name.startswith('Pinball Machine'):
            continue
        number=int(name.rsplit(' ',1)[1])
        if number <= 2:
            wall_inside=fx(-2.67)+fx(0.18)*0.5
            gap=pin[0]-wall_inside
            label='west'
        else:
            wall_inside=fz(-3.02)+fz(0.18)*0.5
            gap=pin[2]-wall_inside
            label='Doom-row'
        if gap < -0.025 or gap > 0.18:
            errors.append(f"{name} is not flush with the {label} wall row (gap={gap:.3f}m)")
    for name,cabinet in bounds.items():
        if not name.startswith('Arcade Machine'):
            continue
        south_inside=fz(-3.02)+fz(0.18)*0.5
        gap=cabinet[2]-south_inside
        if gap < -0.025 or gap > 0.18:
            errors.append(f"{name} is not flush with the south wall (gap={gap:.3f}m)")

    for name,b in bounds.items():
        print(f"LAYOUT QA {name}: x={b[0]:.2f}..{b[1]:.2f}, z={b[2]:.2f}..{b[3]:.2f}")
    if errors:
        raise RuntimeError('Arcade layout QA failed:\n - '+'\n - '.join(errors))
    print('LAYOUT QA PASSED: visible machines are inside the sealed footprint, non-overlapping, and wall rows are flush.')

exports=[]
with OUT_BIN.open('wb') as fp:
    fp.write(MAGIC)
    fp.write(struct.pack('<i',2))
    fp.write(struct.pack('<i',len(ORDER)))
    for name in ORDER:
        obj=find_obj(name)
        albedo,normal,emission=TEXTURES[name]
        for s in (name,albedo,normal,emission): write_string(fp,s)
        pos,yaw,positions,normals,uvs=export_mesh(obj,name)
        exports.append({'name':name,'pos':pos,'yaw':yaw,'positions':positions})
        fp.write(struct.pack('<ffff',pos[0],pos[1],pos[2],yaw))
        fp.write(struct.pack('<i',len(positions)))
        for p in positions: fp.write(struct.pack('<fff',*p))
        for n in normals: fp.write(struct.pack('<fff',*n))
        for uv in uvs: fp.write(struct.pack('<ff',*uv))
        fp.write(struct.pack('<i',len(positions)))
        for i in range(len(positions)): fp.write(struct.pack('<i',i))

validate_layout(exports)

# Door remains a generated runtime door, but its Blender marker can slide along the chamfer.
door=bpy.data.objects.get('ARCADE_DOOR_TARGET')
door_t=0.53
if door is not None:
    rp=Vector(bvec_to_runtime(door.matrix_world.translation))
    A=Vector((-2.67*1.22,0.0,1.56*1.34)); B=Vector((-0.84*1.22,0.0,3.06*1.34))
    ab=B-A
    door_t=max(0.36,min(0.64,(rp-A).dot(ab)/ab.length_squared))
OUT_LAYOUT.write_text(f'# Generated by BlenderTools/export_runtime_bin.py\ndoor_t={door_t:.6f}\n',encoding='utf-8')
print(f'Exported Blender-authored runtime package v2: {OUT_BIN}')
print(f'Exported Blender door marker layout: {OUT_LAYOUT} (door_t={door_t:.3f})')
