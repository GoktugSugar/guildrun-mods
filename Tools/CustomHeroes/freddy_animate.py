"""Freddy's animations, authored in code, and the GLB export.

Run headless on the game-ready file made by freddy_prepare.py:
  blender -b freddy_game.blend --python freddy_animate.py -- <out.glb> [<out.blend>]

Clips (the CustomHeroes model player's default names; hit times go into hero.json as Model.Hits):
  Idle    2.4 s loop   animatronic servo sway, head turns, ear twitch
  Walk    1.0 s loop   heavy stomping walk
  Attack  2.0 s        overhead microphone bash, hit at 0.6 s   (the template hero's attack: 2.0 s, hit 0.6 s)
  Skill   3.6 s        "Jumpscare!": crouch, lunge, jaw wide open, head shaking screech, hit at 0.85 s (3.67 s, 0.85 s)
  Death   2.0 s        power-down: jolt, go limp, slump down to sit (last frame holds)

Poses give each bone a rotation in armature space relative to its rest (T-pose) orientation (Freddy faces -Y,
+X is his left, +Z up). A bone that isn't listed moves rigidly with its parent; ('rel', q) rotates relative to the
parent instead (jaw, fingers, eyelids). Base@loc / Root@loc move the whole body / the hips.
"""
import bpy, sys, math
from mathutils import Quaternion, Vector

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
OUT_GLB = argv[0] if argv else None
OUT_BLEND = argv[1] if len(argv) > 1 else None
FPS = 30

rig = bpy.data.objects['Freddy_Rig']
sc = bpy.context.scene
sc.render.fps = FPS
bones = rig.data.bones
REST = {b.name: b.matrix_local.to_quaternion() for b in bones}
PARENT = {b.name: (b.parent.name if b.parent else None) for b in bones}
ORDER = []
def _walk(b):
    ORDER.append(b.name)
    for c in b.children: _walk(c)
for b in bones:
    if b.parent is None: _walk(b)
AXES = {'X': Vector((1, 0, 0)), 'Y': Vector((0, 1, 0)), 'Z': Vector((0, 0, 1))}

def q(*rots):
    """Rotations applied in the order given, each about an armature axis, in degrees."""
    out = Quaternion()
    for axis, deg in rots:
        out = Quaternion(AXES[axis], math.radians(deg)) @ out
    return out

def rel(*rots): return ('rel', q(*rots))
I = Quaternion()

# ------------------------------------------------------------------ pose helpers

def arm(side, down=72.0, fwd=8.0, elbow=25.0):
    """Arm hanging from the T-pose: 'down' degrees below horizontal, swung 'fwd' forward, forearm 'elbow' forward."""
    s = 1 if side == 'L' else -1
    return {f'{side}_Shoulder': q(('Y', s * down), ('X', -fwd)), f'{side}_Elbow': q(('Y', s * down), ('X', -elbow))}

def reach(side, out=65.0, up=10.0, fore=80.0):
    """Arm reaching forward at shoulder height (jumpscare grab)."""
    s = 1 if side == 'L' else -1
    return {f'{side}_Shoulder': q(('Z', -s * out), ('X', -up)), f'{side}_Elbow': q(('Z', -s * fore), ('X', -up - 5))}

def leg(side, hip=0.0, knee=None, foot=0.0):
    """Thigh swung forward (-) / back (+) about X; knee = shin angle (absolute), foot = absolute pitch."""
    return {f'{side}_Hip': q(('X', hip)), f'{side}_Knee': q(('X', hip if knee is None else knee)), f'{side}_Foot': q(('X', foot))}

def fingers(side, curl):
    s = 1 if side == 'L' else -1
    out = {}
    for f in ('Index', 'Middle', 'Ring'):
        out[f'{side}_{f}1'] = rel(('Y', s * curl))
        out[f'{side}_{f}2'] = rel(('Y', s * curl * 1.2))
    out[f'{side}_Thumb2'] = rel(('Y', s * curl * 0.6))
    return out

def stand(**over):
    p = {}
    p.update(arm('L', 72, 8, 22)); p.update(arm('R', 72, 10, 42))
    p.update(fingers('L', 15)); p.update(fingers('R', 35))      # right hand grips the mic
    p['Head'] = q(('X', 3))
    p.update(over)
    return p

def merge(*ps):
    out = {}
    for p in ps: out.update(p)
    return out

# ------------------------------------------------------------------ keying

_prev = {}

def apply_pose(pose):
    D = {}
    for name in ORDER:
        par = PARENT[name]
        dp = D[par] if par else I
        spec = pose.get(name)
        if spec is None: d = dp
        elif isinstance(spec, tuple) and spec[0] == 'rel': d = dp @ spec[1]
        else: d = spec
        D[name] = d
        r = REST[name]
        local = r.inverted() @ dp.inverted() @ d @ r
        local.normalize()
        prev = _prev.get(name)
        if prev is not None and prev.dot(local) < 0: local = -local
        _prev[name] = local
        pb = rig.pose.bones[name]
        pb.rotation_mode = 'QUATERNION'
        pb.rotation_quaternion = local
    for name in ('Base', 'Root'):
        delta = Vector(pose.get(name + '@loc', (0, 0, 0)))
        rig.pose.bones[name].location = REST[name].inverted() @ delta

def key(t):
    f = round(t * FPS)
    for pb in rig.pose.bones:
        pb.keyframe_insert('rotation_quaternion', frame=f)
        if pb.name in ('Base', 'Root'): pb.keyframe_insert('location', frame=f)

def make_clip(name, keys, linear=False):
    act = bpy.data.actions.get(name) or bpy.data.actions.new(name)
    act.use_fake_user = True
    rig.animation_data_create()
    rig.animation_data.action = act
    _prev.clear()
    for t, pose in keys:
        apply_pose(pose)
        key(t)
    if linear:
        for fc in _fcurves(act):
            for kp in fc.keyframe_points: kp.interpolation = 'LINEAR'
    act.use_frame_range = True
    act.frame_range = (0, round(keys[-1][0] * FPS))
    print(f'clip {name}: {len(keys)} keys, {keys[-1][0]:.2f}s')
    return act

def _fcurves(act):
    if hasattr(act, 'fcurves') and len(act.fcurves): return list(act.fcurves)
    out = []
    for layer in getattr(act, 'layers', []):
        for strip in layer.strips:
            for bag in strip.channelbags: out.extend(bag.fcurves)
    return out

# ------------------------------------------------------------------ Idle (2.4 s loop)

idle_a = stand()
idle_b = stand(Head=q(('X', 4), ('Z', 9)), Spine3=q(('X', 2)), **{'L_Eye': rel(('Z', 8)), 'R_Eye': rel(('Z', 8))})
idle_c = stand(Head=q(('X', 6), ('Z', 0)), Spine3=q(('X', 1)), Jaw=rel(('X', 7)), L_Ear2=rel(('Y', -12)))
idle_d = stand(Head=q(('X', 2), ('Z', -9)), Spine3=q(('X', 2), ('Z', -2)), **{'L_Eye': rel(('Z', -8)), 'R_Eye': rel(('Z', -8))})
idle_d.update(arm('L', 70, 10, 26))
make_clip('Idle', [
    (0.0, idle_a), (0.35, idle_b), (0.7, idle_b),          # servo moves, then holds: animatronic stiffness
    (1.0, idle_c), (1.3, idle_c), (1.65, idle_d), (2.0, idle_d), (2.4, idle_a),
])

# ------------------------------------------------------------------ Walk (1.0 s loop)

def walk(lead):   # lead = side whose foot strikes forward
    back = 'R' if lead == 'L' else 'L'
    tw = 1 if lead == 'L' else -1
    p = stand()
    p.update(leg(lead, -22, -18, -6)); p.update(leg(back, 18, 32, 22))
    p.update(arm(lead, 72, -12, 2)); p.update(arm(back, 72, 20, 46))   # arms swing opposite the legs
    p['Root'] = q(('Z', 5 * tw), ('Y', 2 * tw))
    p['Spine3'] = q(('X', 4), ('Z', -6 * tw))
    p['Head'] = q(('X', 4), ('Z', 3 * tw))
    p['Root@loc'] = (0, 0, -0.035)
    return p

def passing(plant):
    swing = 'R' if plant == 'L' else 'L'
    p = stand()
    p.update(leg(plant, 2, 6, 0)); p.update(leg(swing, -10, 38, 18))
    p['Spine3'] = q(('X', 3))
    p['Root@loc'] = (0, 0, 0.012)
    return p

make_clip('Walk', [(0.0, walk('L')), (0.25, passing('L')), (0.5, walk('R')), (0.75, passing('R')), (1.0, walk('L'))])

# ------------------------------------------------------------------ Attack: overhead mic bash (hit 0.42 s)

windup = stand(**{
    'R_Shoulder': q(('Y', 78), ('X', -25)), 'R_Elbow': q(('Y', 78), ('X', -100)),
    'Spine1': q(('Z', -6)), 'Spine3': q(('X', -8), ('Z', -18)), 'Head': q(('X', -4)),
    'Root@loc': (0, 0.02, -0.03),
})
windup.update(arm('L', 55, 35, 60))
windup.update(leg('L', -8, -2)); windup.update(leg('R', 8, 14, 4))
hit = stand(**{
    'R_Shoulder': q(('Y', -60), ('X', -55)), 'R_Elbow': q(('Y', -60), ('X', -78)),
    'Spine1': q(('X', 6)), 'Spine3': q(('X', 20), ('Z', 12)), 'Head': q(('X', 12)),
    'Root@loc': (0, -0.07, -0.07),
})
hit.update(arm('L', 72, -20, 0))
hit.update(leg('L', -18, 4, 0)); hit.update(leg('R', 12, 26, 10))
follow = merge(hit, {
    'R_Shoulder': q(('Y', -70), ('X', -38)), 'R_Elbow': q(('Y', -70), ('X', -50)),
    'Spine3': q(('X', 24), ('Z', 16)), 'Head': q(('X', 14)), 'Root@loc': (0, -0.08, -0.08),
})
recover = stand(**{'Spine3': q(('X', 8), ('Z', 5)), 'Root@loc': (0, -0.03, -0.03)})
recover.update(arm('R', 72, 18, 50))
make_clip('Attack', [(0.0, stand()), (0.18, merge(stand(), {'Root@loc': (0, 0.01, -0.03), 'Spine3': q(('X', -3))})),
                     (0.45, windup), (0.6, hit), (0.78, follow), (1.25, recover), (2.0, stand())])

# ------------------------------------------------------------------ Skill: Jumpscare! (hit 0.85 s)

crouch = stand(**{
    'Spine3': q(('X', -10)), 'Head': q(('X', 14)), 'Root@loc': (0, 0.05, -0.08),
})
crouch.update(arm('L', 60, -25, 20)); crouch.update(arm('R', 60, -25, 20))
crouch.update(leg('L', -14, 12, 0)); crouch.update(leg('R', -14, 12, 0))

def scream(jx=0.0, jz=0.0, jaw=40.0, extend=0.0):
    p = stand()
    p.update(reach('L', 62 - extend, 10, 78 - extend)); p.update(reach('R', 62 - extend, 10, 78 - extend))
    p.update(fingers('L', 30)); p.update(fingers('R', 30))
    p.update(leg('L', -30, 4, 0)); p.update(leg('R', 26, 38, 24))
    p.update({
        'Base@loc': (0, -0.25, 0), 'Root@loc': (0, -0.05, -0.08),
        'Spine1': q(('X', 12)), 'Spine3': q(('X', 28 + jx * 0.3)),
        'Head': q(('X', -12 + jx), ('Z', jz), ('Y', jz * 0.4)),
        'Jaw': rel(('X', jaw)), 'EndoJaw': rel(('X', jaw * 0.7)),
        'L_LidUp': rel(('X', -12)), 'R_LidUp': rel(('X', -12)),
        'L_Ear1': rel(('Y', -10)), 'R_Ear1': rel(('Y', 10)),
    })
    return p

skill = [(0.0, stand()), (0.3, crouch), (0.6, scream(jaw=34))]
t, i = 0.6, 0
while t < 2.4:                        # 15 Hz head shake while screeching
    t += 1 / 15; i += 1
    s = -1 if i % 2 else 1
    skill.append((round(t, 4), scream(jx=s * 4, jz=s * 7, jaw=42 + s * 4, extend=6 if 0.75 < t < 0.95 else 0)))
calm = stand(**{'Base@loc': (0, -0.10, 0), 'Spine3': q(('X', 8)), 'Jaw': rel(('X', 8)), 'Head': q(('X', 4))})
skill += [(2.8, calm), (3.6, stand())]
make_clip('Skill', skill)

# ------------------------------------------------------------------ Death: power-down slump (holds the last frame)

jolt = stand(**{'Head': q(('X', -15), ('Z', 6)), 'Spine3': q(('X', -6)), 'Jaw': rel(('X', 15))})
jolt.update(arm('L', 55, 5, 10)); jolt.update(arm('R', 55, 5, 10))
cut = stand(**{'Head': q(('X', 32)), 'Spine3': q(('X', 10)), 'Jaw': rel(('X', 22))})
cut.update(arm('L', 86, 4, 6)); cut.update(arm('R', 86, 4, 6)); cut.update(fingers('L', 5)); cut.update(fingers('R', 20))
buckle = merge(cut, {'Root@loc': (0, 0.05, -0.30), 'Spine3': q(('X', 25)), 'Head': q(('X', 45)),
                     'L_LidUp': rel(('X', 25)), 'R_LidUp': rel(('X', 25))})
buckle.update(leg('L', -55, 35, 0)); buckle.update(leg('R', -50, 40, 0))
def sit(head=52, root_z=-0.62):
    p = merge(cut, {'Root@loc': (0, 0.18, root_z), 'Spine1': q(('X', 10)), 'Spine3': q(('X', 32), ('Y', 6)),
                    'Head': q(('X', head), ('Z', -8)), 'Jaw': rel(('X', 26)), 'EndoJaw': rel(('X', 18)),
                    'L_LidUp': rel(('X', 32)), 'R_LidUp': rel(('X', 32)), 'L_Ear1': rel(('Y', 15)), 'R_Ear1': rel(('Y', -15))})
    p.update(leg('L', -90, -55, -15)); p.update(leg('R', -80, -45, -10))
    p.update(arm('L', 80, -10, 0)); p.update(arm('R', 80, -10, 0))
    return p
make_clip('Death', [(0.0, stand()), (0.15, jolt), (0.45, cut), (0.9, buckle), (1.25, sit(48, -0.64)), (1.45, sit(44, -0.60)), (2.0, sit())])

# ------------------------------------------------------------------ export

rig.animation_data.action = None
for pb in rig.pose.bones:
    pb.rotation_quaternion = (1, 0, 0, 0); pb.location = (0, 0, 0)
if OUT_BLEND:
    bpy.ops.wm.save_as_mainfile(filepath=OUT_BLEND, compress=True)
    print('saved', OUT_BLEND)
if OUT_GLB:
    for o in bpy.data.objects: o.select_set(o.name in ('Freddy', 'Freddy_Rig'))
    bpy.context.view_layer.objects.active = rig
    bpy.ops.export_scene.gltf(
        filepath=OUT_GLB, export_format='GLB', use_selection=True, export_yup=True,
        export_texcoords=True, export_normals=True, export_materials='EXPORT', export_image_format='AUTO',
        export_skins=True, export_def_bones=True, export_animations=True, export_animation_mode='ACTIONS',
        export_force_sampling=True, export_frame_step=1, export_optimize_animation_size=False)
    print('exported', OUT_GLB)
