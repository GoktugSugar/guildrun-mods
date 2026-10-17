"""Portrait renders of Freddy from the film file (with fur): a full-body cut-out (the CustomHeroes plugin composes
every portrait frame from it) and a head close-up for the square/round icons.

  blender -b Movie_FreddyFazbear_COSTUME.blend --python freddy_portraits.py -- <out dir> [--preview]

Writes <out dir>/freddy_render.png (transparent) and <out dir>/freddy_head.png (transparent, square).
--preview renders small and fast with EEVEE to check the pose.
"""
import bpy, sys, math
from mathutils import Vector, Quaternion

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
OUT = argv[0] if argv else '.'
PREVIEW = '--preview' in argv

sc = bpy.context.scene
rig = bpy.data.objects['metarig']
SKIP_COLLS = {'Backboard', 'Ball_Joints_Props', 'Endo_Parts', 'Human_Parts', 'CONTROL_PANELS', 'Boneshapes', 'WIRES_MASK'}
coll_parent = {}
for c in bpy.data.collections:
    for ch in c.children: coll_parent[ch.name] = c
def coll_names(o):
    out = set()
    for c in o.users_collection:
        while c is not None: out.add(c.name); c = coll_parent.get(c.name)
    return out
def uses_rig(o):
    p = o
    while p is not None:
        if p == rig or any(m.type == 'ARMATURE' and m.object == rig for m in getattr(p, 'modifiers', [])): return True
        p = p.parent
    return False

# only Freddy renders (no sets, props or the film's lights)
in_scene = set(bpy.context.view_layer.objects.keys())
for o in bpy.data.objects:
    keep = o.type in ('MESH', 'CURVE') and o.name in in_scene and uses_rig(o) and not o.hide_render and not (coll_names(o) & SKIP_COLLS)
    if o.type in ('LIGHT', 'CAMERA') or not keep: o.hide_render = True

# ---- pose: the showman. IK targets' local axes are the world axes (scaled by the rig's 3.1148).
S = rig.matrix_world.to_scale().x
def target(bone, world, rot=None):
    pb = rig.pose.bones[bone]
    rest = rig.matrix_world @ rig.data.bones[bone].head_local
    pb.location = (Vector(world) - rest) / S
    if rot is not None:
        pb.rotation_mode = 'QUATERNION'; pb.rotation_quaternion = rot
def rot(axis, deg): return Quaternion(Vector({'X': (1, 0, 0), 'Y': (0, 1, 0), 'Z': (0, 0, 1)}[axis]), math.radians(deg))

target('R_arm_IK', (-0.30, -1.15, 5.30), rot('X', -75))          # mic up to the mouth
target('L_arm_IK', (2.25, -1.45, 5.75), rot('Y', 70) @ rot('X', -20))   # other arm raised to the audience
hb = rig.pose.bones['Head']; hb.rotation_mode = 'QUATERNION'
hb.rotation_quaternion = rot('Y', 8) @ rot('X', -6) @ rot('Z', 4)
mb = rig.pose.bones['Mouth']; mb.rotation_mode = 'QUATERNION'; mb.rotation_quaternion = rot('X', 24)
bpy.context.view_layer.update()

# ---- lights
for name, kind, loc, energy, color, size in (
        ('Key', 'AREA', (-3.5, -7.0, 10.0), 2600, (1.0, 0.84, 0.68), 4.0),
        ('Rim', 'AREA', (5.0, 6.0, 9.0), 1800, (0.55, 0.62, 1.0), 3.0),
        ('Fill', 'AREA', (6.0, -6.0, 3.5), 500, (1.0, 0.9, 0.85), 5.0),
        ('Spot', 'SPOT', (0.0, -4.0, 14.0), 3500, (1.0, 0.92, 0.75), 0.6)):
    ld = bpy.data.lights.new(name, kind); ld.energy = energy; ld.color = color
    if kind == 'AREA': ld.size = size
    else: ld.spot_size = math.radians(35); ld.spot_blend = 0.5
    lo = bpy.data.objects.new(name, ld); sc.collection.objects.link(lo); lo.location = loc
    lo.rotation_euler = (Vector((0, 0, 4.0)) - lo.location).to_track_quat('-Z', 'Y').to_euler()
sc.world = sc.world or bpy.data.worlds.new('World')
sc.world.use_nodes = True
bg = sc.world.node_tree.nodes.get('Background')
if bg: bg.inputs[0].default_value = (0.03, 0.025, 0.04, 1); bg.inputs[1].default_value = 1.0

# ---- render settings
if PREVIEW:
    for eng in ('BLENDER_EEVEE', 'BLENDER_EEVEE_NEXT'):
        try: sc.render.engine = eng; break
        except Exception: pass
else:
    sc.render.engine = 'CYCLES'
    try:
        prefs = bpy.context.preferences.addons['cycles'].preferences
        for dev in ('OPTIX', 'CUDA'):
            try: prefs.compute_device_type = dev
            except TypeError: continue
            prefs.get_devices()
            if any(d.type == dev for d in prefs.devices):
                for d in prefs.devices: d.use = d.type == dev
                sc.cycles.device = 'GPU'; break
    except Exception as e: print('GPU setup failed:', e)
    sc.cycles.samples = 128
    sc.cycles.use_denoising = True
sc.render.film_transparent = True
sc.view_settings.view_transform = 'AgX' if 'AgX' in [v.identifier for v in bpy.types.ColorManagedViewSettings.bl_rna.properties['view_transform'].enum_items] else 'Filmic'
sc.view_settings.look = 'None'

cam = bpy.data.objects.new('PortraitCam', bpy.data.cameras.new('PortraitCam')); sc.collection.objects.link(cam); sc.camera = cam
def shoot(path, w, h, lens, azimuth, dist, height, look):
    sc.render.resolution_x, sc.render.resolution_y = (w // 4, h // 4) if PREVIEW else (w, h)
    cam.data.lens = lens
    a = math.radians(azimuth)
    cam.location = Vector((dist * math.sin(a), -dist * math.cos(a), height))
    cam.rotation_euler = (Vector(look) - cam.location).to_track_quat('-Z', 'Y').to_euler()
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print('rendered', path)

shoot(f'{OUT}/freddy_render.png', 1400, 1900, 50, 22, 12.5, 3.2, (0.2, 0.0, 3.75))      # full body, slightly low angle
shoot(f'{OUT}/freddy_head.png', 1024, 1024, 85, 18, 7.5, 6.0, (0.0, -0.3, 6.15))         # head close-up
