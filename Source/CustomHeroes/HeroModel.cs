// A custom hero's own 3D model (hero.json "Model"). At injection the hero's prefab copy gets the model built in
// (the .glb skeleton plus one skinned mesh, under the template's Animator) and the template's renderers switched
// off, so every spawn is the new hero from its first frame. The template's Animator keeps running unseen: the game
// drives it and its bones carry attachment points. The model plays its own glTF clips (CustomHeroModel below):
// AnimationHandler tells us when an attack, skill or death starts (and at what speed), idle and walk follow the
// hero's movement, and attack/skill clips are time-warped so the clip's hit frame (Model.Hits) lands on the game's
// hit timing from the visual config's AnimationSettings.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ember.Balancing.Sheets.Characters;
using Ember.Simulation.UnityRuntime.Entities.Animations;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomHeroes
{
    /// <summary>One hero's loaded model, shared by its prefabs and every spawn.</summary>
    internal sealed class ModelAsset
    {
        public HeroDefinition Def;
        public GlbModel Glb;
        public Mesh Mesh;
        public Material[] Materials;
        public int[][] NodePaths;                 // child-index path from the model root to each glTF node
        public readonly Dictionary<string, GlbClip> Clips = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> ClipNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Idle"] = "Idle", ["Move"] = "Walk", ["BasicAttack"] = "Attack", ["SkillAttack"] = "Skill", ["Die"] = "Death",
        };
        public readonly Dictionary<string, float> ClipHits = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (float duration, float hit)> Timings = new(StringComparer.OrdinalIgnoreCase);

        readonly Dictionary<string, List<GlbClip>> _lists = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The clips for a game animation ("A,B,C" in Model.Clips = several that take turns).</summary>
        public List<GlbClip> ClipList(string state)
        {
            if (_lists.TryGetValue(state, out var list)) return list;
            list = new List<GlbClip>();
            if (ClipNames.TryGetValue(state, out var names))
                foreach (var n in names.Split(','))
                    if (Clips.TryGetValue(n.Trim(), out var c)) list.Add(c);
            if (list.Count == 0)
            {
                if (state == "Move") list = ClipList("Idle");
                else if (Clips.TryGetValue(state, out var c)) list.Add(c);
            }
            return _lists[state] = list;
        }

        public GlbClip ClipFor(string state)
        {
            var l = ClipList(state);
            return l.Count > 0 ? l[0] : null;
        }

        /// <summary>Clip time of the hit: by clip name, else by game animation.</summary>
        public float HitFor(GlbClip clip, string state)
            => ClipHits.TryGetValue(clip.Name, out var h) || ClipHits.TryGetValue(state, out h) ? h : 0f;
    }

    internal static class HeroModel
    {
        public const string Prefix = "CustomHero_";
        static readonly Dictionary<string, ModelAsset> Assets = new(StringComparer.OrdinalIgnoreCase);   // by hero Key
        static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);
        internal static readonly Dictionary<IntPtr, CustomHeroModel> ByAnimator = new();

        public static ModelAsset Get(string key) => Assets.TryGetValue(key, out var a) ? a : null;

        static ModelAsset Load(HeroDefinition d)
        {
            if (Assets.TryGetValue(d.Key, out var hit)) return hit;
            if (Failed.Contains(d.Key)) return null;
            var path = d.Path(d.Model.File);
            try
            {
                if (path == null || !File.Exists(path)) throw new FileNotFoundException("model not found", path);
                var model = GlbModel.Load(path);
                var mesh = new Mesh { name = d.Key + "_" + Path.GetFileNameWithoutExtension(path) };
                if (model.Positions.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = model.Positions;
                mesh.uv = model.UVs;
                mesh.subMeshCount = model.SubTriangles.Length;
                for (int i = 0; i < model.SubTriangles.Length; i++) mesh.SetTriangles(model.SubTriangles[i], i);
                mesh.boneWeights = model.Weights;
                mesh.bindposes = model.InverseBind;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
                Injector.Pin(mesh);
                var a = new ModelAsset { Def = d, Glb = model, Mesh = mesh, Materials = BuildMaterials(model) };
                foreach (var c in model.Clips) a.Clips[c.Name] = c;
                foreach (var kv in d.Model.Clips) a.ClipNames[kv.Key] = kv.Value;
                foreach (var kv in d.Model.Hits) a.ClipHits[kv.Key] = (float)kv.Value;
                Assets[d.Key] = a;
                Plugin.L.LogInfo($"{d.Name}: loaded {Path.GetFileName(path)}: {model.Positions.Length} verts, {model.Triangles.Length / 3} tris in {model.SubTriangles.Length} part(s), " +
                    $"{model.Joints.Length} joints, clips [{string.Join(", ", model.Clips.Select(c => $"{c.Name} {c.Length:0.##}s"))}]");
                return a;
            }
            catch (Exception e)
            {
                Failed.Add(d.Key);
                Plugin.L.LogError($"{d.Name}: could not load model {path}: {e}");
                return null;
            }
        }

        /// <summary>Puts the hero's model into its prefab copy and switches the template's renderers off.</summary>
        public static void Build(HeroDefinition d, GameObject prefab)
        {
            if (d.Model == null || string.IsNullOrEmpty(d.Model.File)) return;
            var a = Load(d);
            if (a == null) return;
            var model = a.Glb;

            var smrs = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var main = smrs.OrderByDescending(s => s.bones?.Length ?? 0).FirstOrDefault();
            Animator anim = null;
            for (var t = main != null ? main.transform : null; t != null && anim == null; t = t.parent) anim = t.GetComponent<Animator>();
            anim ??= prefab.GetComponentInChildren<Animator>(true);
            var root = anim != null ? anim.transform : prefab.transform;
            if (anim != null) anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;   // its renderers are off, it must still run

            int hidden = 0;
            foreach (var r in smrs) { r.enabled = false; r.forceRenderingOff = true; hidden++; }
            foreach (var r in prefab.GetComponentsInChildren<MeshRenderer>(true)) { r.enabled = false; r.forceRenderingOff = true; hidden++; }
            if (d.Model.HideTemplateEffects)
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    if (r.GetIl2CppType().Name is "ParticleSystemRenderer" or "TrailRenderer" or "LineRenderer")
                    { r.enabled = false; r.forceRenderingOff = true; hidden++; Plugin.L.LogInfo($"{d.Name}: template effect '{r.name}' off"); }

            var go = new GameObject(Prefix + d.Key);
            var gRoot = go.transform;
            gRoot.SetParent(root, false);
            gRoot.localRotation = Quaternion.Euler(0, (float)d.Model.Yaw, 0);

            // skeleton in its rest pose (glTF node TRS)
            int n = model.Nodes.Count;
            var tf = new Transform[n];
            for (int i = 0; i < n; i++) tf[i] = new GameObject(model.Nodes[i].Name).transform;
            for (int i = 0; i < n; i++)
            {
                var nd = model.Nodes[i];
                tf[i].SetParent(nd.Parent >= 0 ? tf[nd.Parent] : gRoot, false);
                tf[i].localPosition = nd.T; tf[i].localRotation = nd.R; tf[i].localScale = nd.S;
            }

            var rendererGo = new GameObject("Mesh");
            rendererGo.transform.SetParent(gRoot, false);
            var smr = rendererGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = a.Mesh;
            smr.bones = model.Joints.Select(j => tf[j]).ToArray();
            smr.rootBone = tf[model.Joints[0]];
            smr.updateWhenOffscreen = true;
            smr.sharedMaterials = a.Materials;
            if (main != null) { rendererGo.layer = main.gameObject.layer; smr.shadowCastingMode = main.shadowCastingMode; }
            go.layer = rendererGo.layer;

            if (a.NodePaths == null)
            {
                a.NodePaths = new int[n][];
                for (int i = 0; i < n; i++)
                {
                    var path = new List<int>();
                    for (var t = tf[i]; t != gRoot; t = t.parent) path.Insert(0, t.GetSiblingIndex());
                    a.NodePaths[i] = path.ToArray();
                }
            }

            // size: hips at the template's hip height (its bounds include weapons and shields), times Model.Scale
            float heroHips = (float)d.Model.HipHeight;
            if (heroHips <= 0)
            {
                Transform hips = null;
                if (main != null && main.bones != null)
                    foreach (var b in main.bones)
                        if (b != null && (b.name.ToLowerInvariant().Contains("hips") || b.name.ToLowerInvariant().Contains("pelvis"))) { hips = b; break; }
                if (hips != null) heroHips = root.InverseTransformPoint(hips.position).y;
            }
            int rootIdx = model.Nodes.FindIndex(x => string.Equals(x.Name, "Root", StringComparison.OrdinalIgnoreCase));
            float modelHips = rootIdx >= 0 ? gRoot.InverseTransformPoint(tf[rootIdx].position).y : 0f;
            float ratio = heroHips > 0.05f && modelHips > 0.05f ? heroHips / modelHips : 1f;
            if (ratio == 1f) Plugin.L.LogWarning($"{d.Name}: no hip heights to match (template {heroHips:0.##}, model {modelHips:0.##}); model kept at its own size");
            float s = ratio * (float)(d.Model.Scale > 0 ? d.Model.Scale : 1);
            gRoot.localScale = Vector3.one * s;
            go.AddComponent<CustomHeroModel>();

            Plugin.L.LogInfo($"{d.Name}: model built into prefab '{prefab.name}' under '{root.name}', {hidden} template renderer(s) off, " +
                $"hips {heroHips:0.##} / model {modelHips:0.##}, scale {s:0.###}");
        }

        /// <summary>The game's animation durations and hit times, from the hero's visual config.</summary>
        public static void ReadTimings(HeroDefinition d, CharacterVisualConfig c)
        {
            var a = Get(d.Key);
            if (a == null) return;
            try
            {
                var list = c.AnimationSettings?._animationTimings;
                if (list == null) return;
                foreach (var t in list)
                    if (t != null) a.Timings[t.Type.ToString()] = (t.Duration.RawValue / 65536f, t.Hit.RawValue / 65536f);
                foreach (var kv in d.Model.Timings)   // hero.json Model.Timings: [duration, hit]
                    if (kv.Value != null && kv.Value.Length >= 2) a.Timings[kv.Key] = ((float)kv.Value[0], (float)kv.Value[1]);
                Plugin.L.LogInfo($"{d.Name}: game animation timings " + string.Join(", ", a.Timings.Select(kv => $"{kv.Key} {kv.Value.duration:0.##}s hit {kv.Value.hit:0.##}s")));
            }
            catch (Exception e) { Plugin.L.LogWarning($"{d.Name}: animation timings: {e.Message}"); }
        }

        /// <summary>One material per submesh, sharing one per distinct texture.</summary>
        static Material[] BuildMaterials(GlbModel model)
        {
            var byImage = new Dictionary<int, Material>();
            var mats = new Material[model.SubImage.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                int img = model.SubImage[i];
                if (!byImage.TryGetValue(img, out var m)) byImage[img] = m = BuildMaterial(img >= 0 ? model.Images[img] : null);
                mats[i] = m;
            }
            return mats;
        }

        static Material BuildMaterial(byte[] png)
        {
            Texture2D tex = null;
            if (png != null)
            {
                tex = Png.Decode(png);
                tex.name = "CustomHeroTexture";
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                Injector.Pin(tex);
            }
            var want = Plugin.ShaderName.Value;
            Shader shader = null;
            foreach (var sh in Resources.FindObjectsOfTypeAll<Shader>()) if (sh.name == want) { shader = sh; break; }
            shader ??= Shader.Find(want);
            if (shader == null) { Plugin.L.LogWarning($"Shader '{want}' not in this build, using Unlit/Texture."); shader = Shader.Find("Unlit/Texture"); }
            var m = new Material(shader) { name = "CustomHeroMaterial" };
            foreach (var p in new[] { "_BaseMap", "_MainTex", "_BaseColorMap", "_Albedo", "_AlbedoMap", "_MainTexture" })
                if (m.HasProperty(p)) m.SetTexture(p, tex);
            foreach (var p in new[] { "_BaseColor", "_Color" })
                if (m.HasProperty(p)) m.SetColor(p, Color.white);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);   // double-sided
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Injector.Pin(m);
            return m;
        }

        // ---- game hooks

        public static void Patch(Harmony harmony)
        {
            var t = typeof(AnimationHandler);
            harmony.Patch(AccessTools.Method(t, nameof(AnimationHandler.TriggerAnimation)), postfix: new HarmonyMethod(typeof(HeroModel), nameof(TriggerPostfix)));
            harmony.Patch(AccessTools.Method(t, nameof(AnimationHandler.PauseAnimation)), postfix: new HarmonyMethod(typeof(HeroModel), nameof(PausePostfix)));
            var play = AccessTools.Method(t, nameof(AnimationHandler.PlayAnimation), new[] { typeof(AnimationType), typeof(float), typeof(int) });
            if (play != null) harmony.Patch(play, postfix: new HarmonyMethod(typeof(HeroModel), nameof(PlayPostfix)));
        }

        static CustomHeroModel Of(AnimationHandler h)
        {
            var a = h?._animator;
            return a != null && ByAnimator.TryGetValue(a.Pointer, out var m) && m != null ? m : null;
        }

        static void TriggerPostfix(AnimationHandler __instance, AnimationType animationType, float timescale)
        {
            try { Of(__instance)?.Trigger(animationType.ToString(), timescale); }
            catch (Exception e) { Plugin.L.LogError("Model trigger: " + e); }
        }

        static void PlayPostfix(AnimationHandler __instance, AnimationType animationType, float normalizedTime)
        {
            try { Of(__instance)?.Trigger(animationType.ToString(), 1f, normalizedTime); }
            catch (Exception e) { Plugin.L.LogError("Model play: " + e); }
        }

        static void PausePostfix(AnimationHandler __instance)
        {
            try { Of(__instance)?.Pause(); } catch { }
        }
    }

    /// <summary>Sits on the model root inside the prefab, so every spawned copy animates itself.</summary>
    public class CustomHeroModel : MonoBehaviour
    {
        public CustomHeroModel(IntPtr ptr) : base(ptr) { }

        const float Fade = 0.15f;              // crossfade between clips, seconds
        const float MoveSpeed = 0.15f;         // root speed (units/s) above which the hero counts as walking

        ModelAsset _a;
        Transform _root;
        Transform[] _tf;
        IntPtr _anim;
        readonly List<Renderer> _template = new();
        Vector3[] _fromT; Quaternion[] _fromR; Vector3[] _fromS;
        GlbClip _clip;
        string _state = "Idle";
        float _elapsed, _speed = 1f, _fadeLeft;
        bool _oneShot, _paused, _broken;
        float _gameDuration, _gameHit, _clipHit;   // time warp for attack/skill (0 = play at _speed)
        Vector3 _lastPos;
        float _moveTimer;
        bool _moving;
        // next clip of each multi-clip animation; starts over each fight like the effects' every-Nth counters
        // (Increment), so a Q1/Q2/Q3 cycle stays in step with an every-3rd-cast effect
        readonly Dictionary<string, int> _turn = new();
        bool _vfxDone;
        float _cueReady;
        bool _blink;                 // a Blink cue holds the model at its target (world pose) until the clip ends
        Vector3 _blinkPos, _homePos;
        Quaternion _blinkRot, _homeRot;

        void OnEnable() => _turn.Clear();   // in case spawns are pooled between fights

        bool Init()
        {
            if (_tf != null) return true;
            if (_broken) return false;
            try
            {
                _a = HeroModel.Get(name.StartsWith(HeroModel.Prefix) ? name[HeroModel.Prefix.Length..] : name);
                if (_a == null || _a.NodePaths == null) { _broken = true; return false; }
                _root = transform.parent;
                int n = _a.NodePaths.Length;
                _tf = new Transform[n];
                for (int i = 0; i < n; i++)
                {
                    var t = transform;
                    foreach (int c in _a.NodePaths[i]) t = t.GetChild(c);
                    _tf[i] = t;
                }
                _fromT = new Vector3[n]; _fromR = new Quaternion[n]; _fromS = new Vector3[n];
                foreach (var r in _root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (!r.transform.IsChildOf(transform)) _template.Add(r);
                foreach (var r in _root.GetComponentsInChildren<MeshRenderer>(true)) _template.Add(r);
                if (_a.Def.Model.HideTemplateEffects)
                    foreach (var r in _root.GetComponentsInChildren<Renderer>(true))
                        if (!r.transform.IsChildOf(transform) && r.GetIl2CppType().Name is "ParticleSystemRenderer" or "TrailRenderer" or "LineRenderer") _template.Add(r);
                var anim = _root.GetComponent<Animator>();
                if (anim != null) { _anim = anim.Pointer; HeroModel.ByAnimator[_anim] = this; }
                _lastPos = _root.position;
                Play("Idle", 1f);
                _fadeLeft = 0;
                return true;
            }
            catch (Exception e)
            {
                _broken = true; _tf = null;
                Plugin.L.LogError($"Model '{name}': {e}");
                return false;
            }
        }

        void OnDestroy()
        {
            if (_anim != IntPtr.Zero && HeroModel.ByAnimator.TryGetValue(_anim, out var m) && m == this) HeroModel.ByAnimator.Remove(_anim);
        }

        /// <summary>The game started an animation on this hero (AnimationHandler.TriggerAnimation).</summary>
        public void Trigger(string state, float timescale, float normalizedTime = 0f)
        {
            if (!Init()) return;
            _paused = false;
            if (_state == "Cue" && state == "BasicAttack") return;   // let a cue (ult) clip finish over auto attacks
            if (state is "Idle" or "Move")
            {   // the game's idle/move calls are frequent; only leave a one-shot clip when it has finished
                if (_state == "Die") Play("Idle", 1f);   // resurrected
                return;
            }
            Play(state, timescale);
            if (normalizedTime > 0 && _clip != null) _elapsed = normalizedTime * (_gameDuration > 0 ? _gameDuration : _clip.Length / _speed);
        }

        public void Pause() => _paused = true;

        void Play(string state, float timescale)
        {
            EndBlink();
            var list = _a.ClipList(state);
            if (list.Count == 0) return;
            var clip = list[0];
            if (list.Count > 1 && state is "BasicAttack" or "SkillAttack" or "Die")
            {
                _turn.TryGetValue(state, out int k);
                clip = list[k % list.Count];
                _turn[state] = k + 1;
            }
            _vfxDone = false;
            for (int i = 0; i < _tf.Length; i++) { _fromT[i] = _tf[i].localPosition; _fromR[i] = _tf[i].localRotation; _fromS[i] = _tf[i].localScale; }
            _fadeLeft = Fade;
            _clip = clip;
            _state = state;
            _elapsed = 0;
            _speed = Mathf.Max(0.05f, timescale);
            _oneShot = state is "BasicAttack" or "SkillAttack" or "Die";
            _gameDuration = _gameHit = _clipHit = 0;
            if (state is "BasicAttack" or "SkillAttack" && _a.Timings.TryGetValue(state, out var tm) && tm.duration > 0)
            {
                float h = _a.HitFor(clip, state);
                if (_a.Def.Model.Warp)
                {
                    _gameDuration = tm.duration / _speed;
                    _gameHit = tm.hit > 0 ? tm.hit / _speed : 0;
                    _clipHit = h > 0 ? Mathf.Clamp(h, 0.01f, clip.Length - 0.01f) : 0;
                }
                else if (tm.hit > 0 && h > 0)
                    _elapsed = -Mathf.Max(0f, tm.hit / _speed - h / _speed);   // own speed; hold the pose until the hit lines up
            }
        }

        /// <summary>A Model.Cues clip: plays at its own speed as a one-shot. Blink: on the cue's target, then back.</summary>
        void PlayCue(CueDef cue, string target)
        {
            if (!_a.Clips.TryGetValue(cue.Clip ?? "", out var clip)) { Plugin.L.LogWarning($"Model '{name}': cue clip '{cue.Clip}' not in the model"); return; }
            EndBlink();
            if (cue.Blink && target != null) StartBlink(target);
            for (int i = 0; i < _tf.Length; i++) { _fromT[i] = _tf[i].localPosition; _fromR[i] = _tf[i].localRotation; _fromS[i] = _tf[i].localScale; }
            _fadeLeft = Fade;
            _clip = clip; _state = "Cue"; _elapsed = 0; _speed = 1f; _oneShot = true; _paused = false;
            _gameDuration = _gameHit = _clipHit = 0;
            _vfxDone = false;
            if (cue.Burst > 0)
            {
                var fwd = transform.forward; fwd.y = 0;
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                TornadoVfx.Spawn(_blink ? _blinkPos : _root.position, fwd.normalized, HeroHeight() * (float)cue.Burst, 0f, 0.6f);
            }
        }

        /// <summary>Moves the model (not the game's character) next to the target's view, facing it.</summary>
        void StartBlink(string target)
        {
            Transform tv = null;
            foreach (var v in Object.FindObjectsOfType<Ember.Simulation.UnityRuntime.Entities.View.EntityViewController>())
            {
                if (v == null) continue;
                string id = null;
                try { id = v.EntityId.Guid.ToString(); } catch { }
                if (id == target) { tv = v.transform; break; }
            }
            if (tv == null) return;
            var dir = tv.position - _root.position; dir.y = 0;
            if (dir.sqrMagnitude < 1e-4f) return;
            _homePos = transform.localPosition; _homeRot = transform.localRotation;
            _blinkPos = tv.position - dir.normalized * HeroHeight() * 0.35f;
            _blinkPos.y = _root.position.y;
            _blinkRot = Quaternion.LookRotation(dir.normalized) * Quaternion.Euler(0, (float)_a.Def.Model.Yaw, 0);
            _blink = true;
            transform.position = _blinkPos; transform.rotation = _blinkRot;
        }

        void EndBlink()
        {
            if (!_blink) return;
            _blink = false;
            transform.localPosition = _homePos; transform.localRotation = _homeRot;
        }

        /// <summary>Height of the hero in world units right now (hips x 2; follows size growth).</summary>
        float HeroHeight()
        {
            int root = _a.Glb.Nodes.FindIndex(x => string.Equals(x.Name, "Root", StringComparison.OrdinalIgnoreCase));
            float hips = root >= 0 ? _tf[root].position.y - _root.position.y : 0f;
            return hips > 0.05f ? hips * 2f : 2f * transform.lossyScale.y;
        }

        void SpawnVfx(float t)
        {
            if (_vfxDone) return;
            foreach (var v in _a.Def.Model.Vfx)
            {
                if (v == null || t < v.At || !string.Equals(v.Clip, _clip.Name, StringComparison.OrdinalIgnoreCase)) continue;
                _vfxDone = true;
                var fwd = transform.forward; fwd.y = 0;
                if (fwd.sqrMagnitude < 1e-4f) fwd = _root.forward;
                float h = HeroHeight();
                if (string.Equals(v.Type, "Tornado", StringComparison.OrdinalIgnoreCase))
                    TornadoVfx.Spawn(_root.position + fwd.normalized * h * 0.4f, fwd.normalized, h * (float)v.Size, h * (float)v.Distance, (float)v.Duration);
                else Plugin.L.LogWarning($"Model '{name}': unknown Vfx type '{v.Type}'");
            }
        }

        /// <summary>Clip time for the time since the clip started (piecewise linear around the hit).</summary>
        float ClipTime()
        {
            if (_gameDuration <= 0) return _elapsed * _speed;
            float L = _clip.Length;
            if (_clipHit > 0 && _gameHit > 0 && _gameHit < _gameDuration)
                return _elapsed <= _gameHit ? _elapsed / _gameHit * _clipHit
                     : _clipHit + (_elapsed - _gameHit) / (_gameDuration - _gameHit) * (L - _clipHit);
            return _elapsed / _gameDuration * L;
        }

        void LateUpdate()
        {
            if (!Init()) return;
            try
            {
                // the game turns renderers back on in some states (spawn, effects); the template must never show
                foreach (var r in _template) if (r != null && !r.forceRenderingOff) r.forceRenderingOff = true;
                float dt = Time.deltaTime;

                // walking: the root moves (idle/walk are not reliably sent as triggers)
                var pos = _root.position;
                float speed = dt > 0 ? (pos - _lastPos).magnitude / dt / Mathf.Max(0.01f, _root.lossyScale.x) : 0f;
                _lastPos = pos;
                if (dt > 0)
                {
                    bool movingNow = speed > MoveSpeed;
                    _moveTimer = movingNow == _moving ? 0 : _moveTimer + dt;
                    if (_moveTimer > 0.08f) { _moving = movingNow; _moveTimer = 0; }
                }
                if (_state != "Die" && _a.Def.Model.Cues.Count > 0)
                {
                    var cue = Cues.Take(_a.Def.Key, out var target);
                    if (cue != null && Time.time >= _cueReady) { _cueReady = Time.time + (float)cue.Cooldown; PlayCue(cue, target); }
                }
                if (_clip == null) return;
                if (!_paused) _elapsed += dt;
                if (_blink) { transform.position = _blinkPos; transform.rotation = _blinkRot; }   // the hero's root keeps moving
                if (_elapsed < 0) return;   // Warp off: holding the pose until the clip's hit lines up with the game's

                float t = ClipTime();
                if (_oneShot && _a.Def.Model.Vfx.Count > 0) SpawnVfx(t);
                if (_oneShot && t >= _clip.Length)
                {
                    if (_state == "Die") t = _clip.Length;          // hold the last frame
                    else { Play(_moving ? "Move" : "Idle", 1f); t = 0; }
                }
                else if (!_oneShot)
                {
                    var want = _moving ? "Move" : "Idle";
                    if (want != _state && _a.ClipFor(want) != _clip) { Play(want, 1f); t = 0; }
                    else if (want != _state) _state = want;
                    t = _clip.Length > 0 ? t % _clip.Length : 0;
                }

                foreach (var ch in _clip.Channels)
                {
                    if (ch.Node < 0 || ch.Node >= _tf.Length) continue;
                    var tr = _tf[ch.Node];
                    switch (ch.Path)
                    {
                        case 0: tr.localPosition = ch.Vec(t); break;
                        case 1: tr.localRotation = ch.Rot(t); break;
                        case 2: tr.localScale = ch.Vec(t); break;
                    }
                }
                if (_fadeLeft > 0)
                {
                    float w = 1f - _fadeLeft / Fade;
                    for (int i = 0; i < _tf.Length; i++)
                    {
                        var tr = _tf[i];
                        tr.localPosition = Vector3.Lerp(_fromT[i], tr.localPosition, w);
                        tr.localRotation = Quaternion.Slerp(_fromR[i], tr.localRotation, w);
                        tr.localScale = Vector3.Lerp(_fromS[i], tr.localScale, w);
                    }
                    _fadeLeft -= dt;
                }
            }
            catch (Exception e) { _broken = true; _tf = null; Plugin.L.LogError($"Model '{name}': {e}"); }
        }
    }
}
