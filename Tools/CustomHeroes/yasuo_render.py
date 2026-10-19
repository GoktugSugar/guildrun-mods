"""Renders of a League .glb with its own clips (Yasuo, Malphite): the portrait cut-out and clip preview frames.

  blender -b --python yasuo_render.py -- <yasuo.glb> <out dir> portrait
  blender -b --python yasuo_render.py -- <yasuo.glb> <out dir> preview <clip> <frame times in s, comma list> [side|front|top]

portrait: <out dir>/<glb name>_render.png (transparent full body, 3/4 view) and <glb name>_head.png (square head close-up).
preview:  <out dir>/<clip>_<t>.png, small EEVEE frames to check poses, facing and hit timing.
"""
import bpy, sys, math, os
from mathutils import Vector

argv = sys.argv[sys.argv.index('--') + 1:]
GLB, OUT, MODE = argv[0], argv[1], argv[2]
NAME = os.path.splitext(os.path.basename(GLB))[0].lower()   # yasuo, malphite: output file names

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=GLB)
sc = bpy.context.scene
rig = next(o for o in sc.objects if o.type == 'ARMATURE')
meshes = [o for o in sc.objects if o.type == 'MESH']

# the glTF materials are unlit (League look): emission-like shading, no lights needed
sc.render.film_transparent = True
for eng in ('BLENDER_EEVEE_NEXT', 'BLENDER_EEVEE'):
    try: sc.render.engine = eng; break
    except Exception: pass
sc.view_settings.view_transform = 'Standard'
world = bpy.data.worlds.new('w'); sc.world = world
world.color = (1, 1, 1)

def set_pose(clip, t):
    act = bpy.data.actions.get(clip) or next((a for a in bpy.data.actions if a.name.startswith(clip)), None)
    if act is None: raise SystemExit('no clip ' + clip + ': ' + ', '.join(a.name for a in bpy.data.actions))
    rig.animation_data_create()
    rig.animation_data.action = act
    try:
        slots = getattr(act, 'slots', None)
        if slots: rig.animation_data.action_slot = slots[0]
    except Exception: pass
    sc.render.fps = 30
    sc.frame_set(int(round(t * 30)))
    bpy.context.view_layer.update()

def bounds():
    dg = bpy.context.evaluated_depsgraph_get()
    lo, hi = Vector((1e9,) * 3), Vector((-1e9,) * 3)
    for o in meshes:
        e = o.evaluated_get(dg); m = e.to_mesh()
        for v in m.vertices:
            p = e.matrix_world @ v.co
            if p.z < -0.05: continue   # hidden props (flute, bottles) parked below the floor
            lo = Vector(map(min, lo, p)); hi = Vector(map(max, hi, p))
        e.to_mesh_clear()
    return lo, hi

def camera(target, dist, yaw_deg, pitch_deg, ortho):
    cam = bpy.data.objects.get('cam') or bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    if cam.name not in sc.collection.objects: sc.collection.objects.link(cam)
    yaw, pitch = math.radians(yaw_deg), math.radians(pitch_deg)
    # Blender is Z-up after glTF import; the model faces -Y in Blender when it faces +Z in glTF
    d = Vector((math.sin(yaw) * math.cos(pitch), -math.cos(yaw) * math.cos(pitch), math.sin(pitch)))
    cam.location = target + d * dist
    cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
    cam.data.type = 'ORTHO'; cam.data.ortho_scale = ortho
    sc.camera = cam

if MODE == 'preview':
    clip, times = argv[3], [float(x) for x in argv[4].split(',')]
    view = argv[5] if len(argv) > 5 else 'side'
    sc.render.resolution_x = sc.render.resolution_y = 320
    for t in times:
        set_pose(clip, t)
        lo, hi = bounds()
        c = Vector((0, 0, 1.0))
        yaw, pitch = {'side': (90, 5), 'front': (0, 5), 'top': (0, 80)}[view]
        camera(c, 10, yaw, pitch, 3.6)
        sc.render.filepath = f'{OUT}/{clip}_{t:.2f}_{view}.png'
        bpy.ops.render.render(write_still=True)
        print('RENDER', sc.render.filepath, 'bounds', tuple(round(x, 2) for x in lo), tuple(round(x, 2) for x in hi))
else:
    clip = argv[3] if len(argv) > 3 else 'Idle1_Raw'
    t = float(argv[4]) if len(argv) > 4 else 0.0
    set_pose(clip, t)
    lo, hi = bounds()
    h = hi.z - max(lo.z, 0)
    sc.render.resolution_x, sc.render.resolution_y = 1600, 2400
    camera(Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, h / 2)), 20, 25, 6, max(h * 1.08, (hi.x - lo.x) * 1.6 * 1.08))
    sc.render.filepath = f'{OUT}/{NAME}_render.png'
    bpy.ops.render.render(write_still=True)
    head = next(b for b in rig.pose.bones if b.name.lower() == 'head')
    hp = rig.matrix_world @ head.head
    sc.render.resolution_x = sc.render.resolution_y = 768
    camera(hp + Vector((0, 0, 0.2)), 20, 20, 4, 0.95)
    sc.render.filepath = f'{OUT}/{NAME}_head.png'
    bpy.ops.render.render(write_still=True)
    print('RENDER portrait done, height', round(h, 2))
