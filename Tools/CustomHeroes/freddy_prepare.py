"""Turns the Freddy movie rig (Movie_FreddyFazbear_COSTUME.blend) into a game-ready model:
one skinned mesh, a clean deform skeleton (Root, Spine1-3, Neck, Head, L_/R_ limbs), one baked texture.

Run headless with Blender 5.x:
  blender -b Movie_FreddyFazbear_COSTUME.blend --python freddy_prepare.py -- <out.blend> [texture size] [--count]

--count only reports which parts survive and their triangle counts (no bake, nothing saved).
The output .blend holds just the armature "Freddy_Rig" and the mesh "Freddy"; freddy_animate.py adds the
animations to it and exports the GLB.
"""
import bpy, sys, math, time
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
OUT = argv[0] if argv else None
TEX = next((int(a) for a in argv[1:] if a.isdigit()), 2048)
COUNT_ONLY = '--count' in argv
SUBSURF_MAX = 1          # subdivision levels kept (the film file renders at 2-3)
TRI_BUDGET = 45000       # parts get decimated to an even density (by world-space area) until the total fits
VISIBLE_MIN = 0.30       # share of a part's surface that must be visible from outside (else it's culled)
DETAIL_BOOST = {'Jaw': 2.0, 'EYES': 2.0, 'Eyelids': 1.5, 'Hands': 1.5, 'HAT': 1.5}   # density multipliers
HEIGHT = 2.2             # final height (glTF units = meters), floor at 0

# Showcase props and the endoskeleton hidden inside the suit.
SKIP_COLLS = {'Backboard', 'Ball_Joints_Props', 'Endo_Parts', 'Human_Parts', 'CONTROL_PANELS', 'Boneshapes', 'WIRES_MASK'}
# Inside the head but shown when the jaw opens or the eyes move: never culled.
ALWAYS_KEEP = {'Jaw', 'Endo_Mouths', 'EYES', 'Eyelids'}

# metarig bone -> exported name
RENAME = {
    'Root': 'Base', 'Pelvis': 'Root', 'Body_Lower': 'Spine1', 'Body_Upper': 'Spine3', 'Neck': 'Neck', 'Head': 'Head',
    'thigh.L': 'L_Hip', 'shin.L': 'L_Knee', 'foot.L': 'L_Foot', 'thigh.R': 'R_Hip', 'shin.R': 'R_Knee', 'foot.R': 'R_Foot',
    'Leg_Pad_L': 'L_LegPad', 'Leg_Pad_R': 'R_LegPad',
    'upper_arm.L': 'L_Shoulder', 'forearm.L': 'L_Elbow', 'hand.L': 'L_Hand',
    'upper_arm.R': 'R_Shoulder', 'forearm.R': 'R_Elbow', 'hand.R': 'R_Hand',
    'Shoulder_pad_L': 'L_ShoulderPad', 'Shoulder_pad_R': 'R_ShoulderPad',
    'hand.R.F3_01.001': 'Mic',
    'Body_Front': 'BodyFront', 'Bowtie': 'Bowtie', 'Body_Back': 'BodyBack',
    'Mouth': 'Jaw', 'EndoMouth_LOWER': 'EndoJaw', 'EndoMouth_UPPER': 'EndoUpper',
    'eye.L': 'L_Eye', 'eye.R': 'R_Eye',
    'Upper_Eyelid_L': 'L_LidUp', 'Lower_Eyelid_L': 'L_LidLow', 'Upper_Eyelid_R': 'R_LidUp', 'Lower_Eyelid_R': 'R_LidLow',
    'MASK': 'Mask', 'Mask_opening': 'MaskBack', 'Hat': 'Hat', 'Eyebrow_L': 'L_Brow', 'Eyebrow_R': 'R_Brow',
    'EAR_L_lower': 'L_Ear1', 'EAR_L_upper': 'L_Ear2', 'EAR_R_lower': 'R_Ear1', 'EAR_R_upper': 'R_Ear2',
}
for side in 'LR':
    for f, n in (('F1', 'Thumb'), ('F2', 'Index'), ('F3', 'Middle'), ('F4', 'Ring')):
        RENAME[f'hand.{side}.{f}_01'] = f'{side}_{n}1'
        RENAME[f'hand.{side}.{f}_02'] = f'{side}_{n}2'
# control / jiggle bones that are dropped: their weights go to these bones
MERGE = {
    'forearm.L_Jiggle': 'forearm.L', 'upper_arm.L_Jiggle': 'upper_arm.L', 'forearm.R_Jiggle': 'forearm.R',
    'upper_arm.R_Jiggle': 'upper_arm.R', 'Pelvis_Bone_jiggle': 'Pelvis',
    'Jiggle_Left_Shin': 'shin.L', 'Jiggle_LEFT_Thigh': 'thigh.L', 'Jiggle_Right_Shin': 'shin.R', 'Jiggle_Right_Thigh': 'thigh.R',
    'Eye_Control_ALL': 'Head', 'Eye_Control_LEFT': 'Head', 'Eye_Control_RIGHT': 'Head',
    'IK_Leg_L': 'foot.L', 'IK_Leg_R': 'foot.R', 'L_arm_IK': 'hand.L', 'R_arm_IK': 'hand.R',
    'L_pole_arm_IK': 'Body_Upper', 'R_pole_arm_IK': 'Body_Upper', 'Pole_leg_IK_L': 'thigh.L', 'Pole_leg_IK_R': 'thigh.R',
    'IK/FK_SWITCH': 'Pelvis',
}
PARENT_OVERRIDE = {'foot.L': 'shin.L', 'foot.R': 'shin.R'}   # feet hang off IK bones in the film rig

t0 = time.time()
def log(*a): print(f'[{time.time() - t0:6.1f}s]', *a, flush=True)

sc = bpy.context.scene
rig = bpy.data.objects['metarig']

coll_parent = {}
for c in bpy.data.collections:
    for ch in c.children: coll_parent[ch.name] = c
def coll_names(o):
    out = set()
    for c in o.users_collection:
        while c is not None:
            out.add(c.name)
            c = coll_parent.get(c.name)
    return out
def coll_render_visible(o):
    for c in o.users_collection:
        while c is not None:
            if c.hide_render: return False
            c = coll_parent.get(c.name)
    return True
def arm_mod(o):
    return next((m for m in o.modifiers if m.type == 'ARMATURE' and m.object == rig), None)
def bone_of(o):
    """Bone a rigid part hangs off (walking up object parents)."""
    p = o
    while p is not None:
        if p.parent == rig:
            return p.parent_bone if p.parent_type == 'BONE' and p.parent_bone in rig.data.bones else None
        p = p.parent
    return None
def uses_rig(o):
    p = o
    while p is not None:
        if p == rig or arm_mod(p): return True
        p = p.parent
    return False

# ---------------------------------------------------------------- 1. parts, in the rest (T) pose
rig.data.pose_position = 'REST'
for c in bpy.data.collections: c.hide_viewport = False
def unexclude(lc):
    lc.exclude = False; lc.hide_viewport = False
    for ch in lc.children: unexclude(ch)
unexclude(bpy.context.view_layer.layer_collection)

parts = []
in_scene = set(bpy.context.view_layer.objects.keys())
for o in bpy.data.objects:
    if o.type not in ('MESH', 'CURVE') or o.name not in in_scene: continue
    if o.hide_render or not coll_render_visible(o) or not uses_rig(o): continue
    if coll_names(o) & SKIP_COLLS: continue
    o.hide_viewport = False; o.hide_set(False); o.hide_select = False
    parts.append(o)
for o in bpy.data.objects:
    if o not in parts and o.type not in ('ARMATURE',): o.hide_render = True
log(f'{len(parts)} candidate parts')

for o in parts:
    for m in o.modifiers:
        if m.type == 'SUBSURF':
            m.levels = min(m.levels, SUBSURF_MAX); m.render_levels = min(m.render_levels, SUBSURF_MAX)
        elif m.type in ('ARMATURE', 'PARTICLE_SYSTEM'):
            m.show_viewport = False
bpy.context.view_layer.update()
dg = bpy.context.evaluated_depsgraph_get()

# Realize every part as a plain mesh object (modifiers applied, rest-pose geometry, same transform/parent).
info = {}   # object name -> (bone, skinned)
real = []
for o in parts:
    ev = o.evaluated_get(dg)
    try:
        me = bpy.data.meshes.new_from_object(ev, preserve_all_data_layers=True, depsgraph=dg)
    except Exception as e:
        log('skip', o.name, e); continue
    if len(me.polygons) == 0:
        bpy.data.meshes.remove(me); continue
    skinned = arm_mod(o) is not None and len(o.vertex_groups) > 0
    if o.type == 'MESH':
        o.modifiers.clear()
        o.data = me
        obj = o
    else:   # curve -> new mesh object at the same place
        obj = bpy.data.objects.new(o.name + '_mesh', me)
        for c in o.users_collection: c.objects.link(obj)
        obj.matrix_world = o.matrix_world.copy()
        for i, m in enumerate(o.data.materials):
            if i >= len(me.materials): me.materials.append(m)
        skinned = False
    info[obj.name] = (bone_of(o), skinned)
    real.append(obj)
parts = real
log(f'{len(parts)} parts realized')

def tris(o): return sum(len(p.vertices) - 2 for p in o.data.polygons)
def deselect_all():
    for x in bpy.context.view_layer.objects: x.select_set(False)

# ---------------------------------------------------------------- 2. drop parts nobody can see
verts, faces, owner = [], [], []
for i, o in enumerate(parts):
    mw = o.matrix_world; base = len(verts)
    me = o.data; me.calc_loop_triangles()
    verts.extend(mw @ v.co for v in me.vertices)
    for t in me.loop_triangles:
        faces.append(tuple(base + k for k in t.vertices)); owner.append(i)
bvh = BVHTree.FromPolygons(verts, faces, all_triangles=True)
dirs = []
n_dirs = 96
for k in range(n_dirs):   # Fibonacci sphere
    z = 1 - 2 * (k + 0.5) / n_dirs; r = math.sqrt(1 - z * z); a = k * math.pi * (3 - math.sqrt(5))
    dirs.append(Vector((r * math.cos(a), r * math.sin(a), z)))
R = 30.0
kept, culled = [], []
visibility = {}
for i, o in enumerate(parts):
    if coll_names(o) & ALWAYS_KEEP: kept.append(o); continue
    me = o.data; mw = o.matrix_world
    pts = [mw @ (sum((me.vertices[v].co for v in t.vertices), Vector()) / 3) for t in me.loop_triangles]
    pts = pts[::max(1, len(pts) // 160)]
    seen = 0
    for p in pts:
        for d in dirs:
            hit = bvh.ray_cast(p + d * R, -d, R * 2)
            if hit[2] is not None and owner[hit[2]] == i and (hit[0] - p).length < 0.02:
                seen += 1; break
    # glimpses through the eye holes / seams don't count: a good share of the part must show
    frac = seen / max(1, len(pts))
    visibility[o.name] = frac
    (kept if frac >= VISIBLE_MIN else culled).append(o)
log(f'visible: {len(kept)} parts, culled {len(culled)}: ' + ', '.join(f'{o.name} ({visibility[o.name]:.0%})' for o in culled))
for o in culled: bpy.data.objects.remove(o)
parts = kept

def report():
    by = {}
    for o in parts:
        c = sorted(coll_names(o) - {'FREDDY_COSTUME_MOVIE', 'Costume', 'Human & Metallic Props', 'EYE_SECONDARY_CONTROL'})
        k = c[0] if c else '-'
        n, t = by.get(k, (0, 0)); by[k] = (n + 1, t + tris(o))
    for k, (n, t) in sorted(by.items(), key=lambda kv: -kv[1][1]): log(f'   {k:20s} parts={n:3d} tris={t}')
    log('   TOTAL tris', sum(tris(o) for o in parts))
report()

# ---------------------------------------------------------------- 3. polygon budget
def area(o):
    """World-space surface area (handles the film file's non-uniform object scales)."""
    mw = o.matrix_world; me = o.data
    co = [mw @ v.co for v in me.vertices]
    a = 0.0
    for t in me.loop_triangles:
        p0, p1, p2 = (co[k] for k in t.vertices)
        a += (p1 - p0).cross(p2 - p0).length * 0.5
    return a
def boost(o): return max([DETAIL_BOOST.get(c, 1.0) for c in coll_names(o)] + [1.0])
total = sum(tris(o) for o in parts)
if total > TRI_BUDGET:
    # Every surface gets the same triangle density, so a dense little bowtie or screw shrinks the most.
    w = {o.name: area(o) * boost(o) for o in parts}
    density = TRI_BUDGET / (sum(w.values()) or 1.0)
    for _ in range(4):   # parts already under their share keep their count; the rest share the remainder
        under = [o for o in parts if tris(o) <= w[o.name] * density]
        spare = TRI_BUDGET - sum(tris(o) for o in under)
        over_w = sum(w[o.name] for o in parts if o not in under) or 1.0
        density = max(density, spare / over_w)
    for o in parts:
        target = max(24, w[o.name] * density)
        if tris(o) <= target * 1.05: continue
        before = tris(o)
        m = o.modifiers.new('Budget', 'DECIMATE'); m.ratio = max(0.02, target / before)
        with bpy.context.temp_override(object=o, active_object=o):
            bpy.ops.object.modifier_apply(modifier=m.name)
        o.data.calc_loop_triangles()
        if before > 1000: log(f'   decimate {o.name}: {before} -> {tris(o)}')
    report()

for o in parts:
    bone, skinned = info.get(o.name, (None, False))
    log(f'   part {o.name:36s} tris={tris(o):6d} vis={visibility.get(o.name, 1.0):4.0%} bone={bone} skinned={skinned} mats={[m.name for m in o.data.materials if m]}')
if COUNT_ONLY:
    log('count only: done'); sys.exit(0)

# ---------------------------------------------------------------- 4. one UV atlas for everything
for o in parts:
    me = o.data
    # faces on fur-only slots (particle hair materials) take the part's main material
    fur = [i for i, m in enumerate(me.materials) if m and m.name.startswith('Fur_')]
    main = next((i for i, m in enumerate(me.materials) if m and not m.name.startswith('Fur_')), 0)
    if fur:
        for p in me.polygons:
            if p.material_index in fur: p.material_index = main
    if len(me.materials) == 0:
        me.materials.append(bpy.data.materials.get('Freddy1_ANIMATRONIC VER'))
    render_uv = next((u for u in me.uv_layers if u.active_render), me.uv_layers[0] if me.uv_layers else None)
    bake = me.uv_layers.new(name='BakeUV')
    me.uv_layers.active = bake
    if render_uv: render_uv.active_render = True

# Unwrap one combined world-space copy (so every part gets the same texel density and nothing overlaps),
# then copy its UVs back to the parts loop by loop. The parts themselves stay as they are: their materials'
# object/generated texture coordinates must not change before the bake.
verts, faces, spans = [], [], []
for o in parts:
    mw = o.matrix_world; me = o.data; base = len(verts)
    verts.extend(mw @ v.co for v in me.vertices)
    first = len(faces)
    for p in me.polygons: faces.append([base + v for v in p.vertices])
    spans.append((o, first, len(me.polygons)))
atlas = bpy.data.meshes.new('AtlasTmp')
atlas.from_pydata(verts, [], faces)
atlas.uv_layers.new(name='BakeUV')
ao_ = bpy.data.objects.new('AtlasTmp', atlas)
sc.collection.objects.link(ao_)
deselect_all()
ao_.select_set(True)
bpy.context.view_layer.objects.active = ao_
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.004, area_weight=0.0, correct_aspect=True, scale_to_bounds=False)
bpy.ops.uv.pack_islands(margin=0.004, rotate=True)
bpy.ops.object.mode_set(mode='OBJECT')
if len(atlas.polygons) != len(faces): raise Exception(f'atlas lost faces: {len(atlas.polygons)} != {len(faces)}')
src = atlas.uv_layers['BakeUV'].data
for o, first, count in spans:
    me = o.data; dst = me.uv_layers['BakeUV'].data
    for k, p in enumerate(me.polygons):
        ap = atlas.polygons[first + k]
        for j in range(p.loop_total):
            dst[p.loop_start + j].uv = src[ap.loop_start + j].uv
bpy.data.objects.remove(ao_); bpy.data.meshes.remove(atlas)
deselect_all()
for o in parts: o.select_set(True)
bpy.context.view_layer.objects.active = parts[0]
log('UV atlas packed (combined unwrap)')

# ---------------------------------------------------------------- 5. bake albedo, emission, AO
sc.render.engine = 'CYCLES'
try:
    prefs = bpy.context.preferences.addons['cycles'].preferences
    for dev in ('OPTIX', 'CUDA', 'HIP', 'ONEAPI'):
        try:
            prefs.compute_device_type = dev
        except TypeError:
            continue
        prefs.get_devices()
        if any(d.type == dev for d in prefs.devices):
            for d in prefs.devices: d.use = d.type == dev
            sc.cycles.device = 'GPU'
            log('baking on', dev)
            break
except Exception as e:
    log('GPU setup failed, CPU bake:', e)

imgs = {}
for key, cs in (('albedo', 'sRGB'), ('emit', 'sRGB'), ('ao', 'Non-Color')):
    im = bpy.data.images.new('Freddy_' + key, TEX, TEX, alpha=False)
    im.colorspace_settings.name = cs
    imgs[key] = im
mats = {m for o in parts for m in o.data.materials if m}
bake_nodes = {}
for m in mats:
    m.use_nodes = True
    nt = m.node_tree
    for n in nt.nodes:
        if n.type == 'BSDF_PRINCIPLED':
            inp = n.inputs.get('Metallic')
            if inp is not None:
                for l in list(inp.links): nt.links.remove(l)
                inp.default_value = 0.0      # metal bakes black in the diffuse pass otherwise
    node = nt.nodes.new('ShaderNodeTexImage'); node.name = 'BakeTarget'
    nt.nodes.active = node
    bake_nodes[m] = node

def bake(kind, key, samples, **kw):
    for node in bake_nodes.values(): node.image = imgs[key]
    sc.cycles.samples = samples
    log('baking', kind, '...')
    bpy.ops.object.bake(type=kind, margin=8, margin_type='EXTEND', use_clear=True, target='IMAGE_TEXTURES', **kw)
    log('baked', kind)

bake('DIFFUSE', 'albedo', 4, pass_filter={'COLOR'})
bake('EMIT', 'emit', 1)
sc.world = sc.world or bpy.data.worlds.new('World')
sc.world.light_settings.distance = 0.45
bake('AO', 'ao', 48)

import numpy as np
def pixels(im):
    a = np.empty(TEX * TEX * 4, np.float32); im.pixels.foreach_get(a); return a.reshape(TEX, TEX, 4)
alb, emit, ao = pixels(imgs['albedo']), pixels(imgs['emit']), pixels(imgs['ao'])
out = np.ones_like(alb)
shade = 0.42 + 0.58 * ao[..., :1]
out[..., :3] = np.clip(alb[..., :3] * shade + emit[..., :3], 0.0, 1.0)
final = bpy.data.images.new('Freddy_Baked', TEX, TEX, alpha=False)
final.pixels.foreach_set(out.ravel())
final.update()
final.pack()
log('final texture combined')

# ---------------------------------------------------------------- 6. one material, rigid parts skinned to their bone
mat = bpy.data.materials.new('Freddy')
mat.use_nodes = True
nt = mat.node_tree
bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
tex = nt.nodes.new('ShaderNodeTexImage'); tex.image = final
uvn = nt.nodes.new('ShaderNodeUVMap'); uvn.uv_map = 'BakeUV'
nt.links.new(uvn.outputs['UV'], tex.inputs['Vector'])
nt.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
bsdf.inputs['Roughness'].default_value = 1.0

deform = {b.name for b in rig.data.bones}
def nearest_bone(o):
    """Kept bone closest to a part that hangs off no bone (rest pose, world space)."""
    me = o.data
    c = o.matrix_world @ (sum((v.co for v in me.vertices), Vector()) / max(1, len(me.vertices)))
    best, bd = 'Spine3', 1e9
    for b in rig.data.bones:
        if b.name not in RENAME: continue
        h, t = rig.matrix_world @ b.head_local, rig.matrix_world @ b.tail_local
        ab = t - h
        k = max(0.0, min(1.0, (c - h).dot(ab) / max(ab.length_squared, 1e-9)))
        d = (h + ab * k - c).length
        if d < bd: best, bd = b.name, d
    return best
for o in parts:
    me = o.data
    me.materials.clear(); me.materials.append(mat)
    for p in me.polygons: p.material_index = 0
    for u in [u for u in me.uv_layers if u.name != 'BakeUV']: me.uv_layers.remove(u)
    bone, skinned = info.get(o.name, (None, False))
    if bone is None and not skinned:
        bone = nearest_bone(o)
        log(f'   {o.name}: no parent bone, nearest is {bone}')
    groups = [g for g in o.vertex_groups if g.name in deform]
    if not skinned or not groups:
        for g in list(o.vertex_groups): o.vertex_groups.remove(g)
        g = o.vertex_groups.new(name=bone or 'Head')
        g.add(list(range(len(me.vertices))), 1.0, 'REPLACE')
    else:
        idx = {g.index for g in groups}
        loose = [v.index for v in me.vertices if sum(e.weight for e in v.groups if e.group in idx) < 1e-3]
        if loose:
            g = o.vertex_groups.get(bone) or o.vertex_groups.new(name=bone or 'Body_Upper')
            g.add(loose, 1.0, 'REPLACE')
            log(f'   {o.name}: {len(loose)} unweighted verts -> {g.name}')
    mw = o.matrix_world.copy()
    o.parent = None
    o.matrix_world = Matrix.Identity(4)
    me.transform(mw)
    if mw.determinant() < 0: me.flip_normals()

deselect_all()
for o in parts: o.select_set(True)
body = parts[0]
bpy.context.view_layer.objects.active = body
bpy.ops.object.join()
body.name = 'Freddy'; body.data.name = 'Freddy'
log('joined:', len(body.data.vertices), 'verts', tris(body), 'tris')

# ---------------------------------------------------------------- 7. clean skeleton, weights, scale
me = body.data
names = {g.index: g.name for g in body.vertex_groups}
w = [dict() for _ in me.vertices]
for v in me.vertices:
    for e in v.groups:
        n = names[e.group]
        n = MERGE.get(n, n)
        if n not in RENAME: n = 'Head' if n in deform else None
        if n is None: continue
        w[v.index][n] = w[v.index].get(n, 0.0) + e.weight
for g in list(body.vertex_groups): body.vertex_groups.remove(g)
groups = {}
for vi, d in enumerate(w):
    s = sum(d.values())
    if s <= 0: d = {'Spine3': 1.0}; s = 1.0
    for n, x in d.items():
        nn = RENAME[n] if n in RENAME else n
        if nn not in groups: groups[nn] = body.vertex_groups.new(name=nn)
        groups[nn].add([vi], x / s, 'REPLACE')

zs = [v.co.z for v in me.vertices]
pelvis = rig.matrix_world @ rig.data.bones['Pelvis'].head_local
shift = Vector((-pelvis.x, -pelvis.y, -min(zs)))
scale = HEIGHT / (max(zs) - min(zs))
xf = Matrix.Scale(scale, 4) @ Matrix.Translation(shift)
me.transform(xf)
log(f'height {max(zs) - min(zs):.3f} -> {HEIGHT} (x{scale:.4f})')

arm = bpy.data.armatures.new('Freddy_Rig')
ro = bpy.data.objects.new('Freddy_Rig', arm)
sc.collection.objects.link(ro)
bpy.context.view_layer.objects.active = ro
deselect_all(); ro.select_set(True)
bpy.ops.object.mode_set(mode='EDIT')
rmw = rig.matrix_world
for old, new in RENAME.items():
    b = rig.data.bones.get(old)
    if b is None: log('missing bone', old); continue
    eb = arm.edit_bones.new(new)
    eb.head = xf @ (rmw @ b.head_local)
    eb.tail = xf @ (rmw @ b.tail_local)
    z = (rmw.to_3x3() @ b.matrix_local.to_3x3() @ Vector((0, 0, 1))).normalized()
    eb.align_roll(z)
    eb.use_deform = True
for old, new in RENAME.items():
    b = rig.data.bones.get(old)
    if b is None: continue
    p = PARENT_OVERRIDE.get(old) or (b.parent.name if b.parent else None)
    while p is not None and p not in RENAME:   # dropped parent: climb
        p = rig.data.bones[p].parent.name if rig.data.bones[p].parent else None
    if p is not None: arm.edit_bones[new].parent = arm.edit_bones[RENAME[p]]
bpy.ops.object.mode_set(mode='OBJECT')
body.parent = ro
mod = body.modifiers.new('Armature', 'ARMATURE'); mod.object = ro
log('skeleton:', len(arm.bones), 'bones')

# ---------------------------------------------------------------- 8. strip everything else and save
for keep in (ro, body):   # the joined mesh only lives in the film file's collections, which go away below
    if keep.name not in sc.collection.objects: sc.collection.objects.link(keep)
for o in list(bpy.data.objects):
    if o not in (ro, body): bpy.data.objects.remove(o)
for c in list(bpy.data.collections): bpy.data.collections.remove(c)
for _ in range(4): bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
if OUT:
    bpy.ops.wm.save_as_mainfile(filepath=OUT, compress=True)
    log('saved', OUT)
