// Effects a hero's model spawns at a clip time (hero.json Model.Vfx). Built in code, no asset bundles:
// Tornado = Yasuo's Whirlwind, two twisted funnels of wind streaks and a dust ring on the ground, spinning,
// travelling forward and fading out. Meshes, texture and shader are made once and pinned (Injector.Pin);
// each spawn gets its own materials (for its fade), released when it ends.
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomHeroes
{
    public class TornadoVfx : MonoBehaviour
    {
        public TornadoVfx(IntPtr ptr) : base(ptr) { }

        static Mesh _funnel, _ring;
        static Texture2D _streaks, _dust;
        static Shader _shader;
        static bool _failed;

        Vector3 _from, _dir;
        float _size, _distance, _life, _age;
        readonly List<(Transform t, Material m, float spin, float alpha)> _layers = new();

        public static void Spawn(Vector3 pos, Vector3 dir, float size, float distance, float life)
        {
            if (_failed) return;
            try
            {
                if (_funnel == null) Build();
                var go = new GameObject("CustomHeroes_Tornado");
                var v = go.AddComponent<TornadoVfx>();
                v._from = pos; v._dir = dir; v._size = size; v._distance = distance; v._life = Mathf.Max(0.2f, life);
                go.transform.position = pos;
                go.transform.localScale = Vector3.one * size;
                //               mesh     texture    tint                                  spin deg/s  scale  alpha
                v.Layer(_funnel, _streaks, new Color(0.80f, 0.93f, 1.00f), 620f, new Vector3(1.00f, 1, 1.00f), 0.70f);
                v.Layer(_funnel, _streaks, new Color(1.00f, 1.00f, 1.00f), 980f, new Vector3(0.62f, 0.96f, 0.62f), 0.95f);
                v.Layer(_funnel, _streaks, new Color(0.65f, 0.85f, 1.00f), -420f, new Vector3(1.22f, 0.9f, 1.22f), 0.35f);
                v.Layer(_ring, _dust, new Color(0.92f, 0.88f, 0.80f), 300f, new Vector3(1.1f, 1, 1.1f), 0.55f);
                Injector.Pin(go);
            }
            catch (Exception e) { _failed = true; Plugin.L.LogError("Tornado vfx: " + e); }
        }

        void Layer(Mesh mesh, Texture2D tex, Color tint, float spin, Vector3 scale, float alpha)
        {
            var child = new GameObject("Layer");
            child.transform.SetParent(transform, false);
            child.transform.localScale = scale;
            child.transform.localRotation = Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = child.AddComponent<MeshRenderer>();
            var m = new Material(_shader) { name = "CustomHeroes_TornadoMat" };
            m.mainTexture = tex;
            tint.a = 0;
            m.color = tint;
            m.renderQueue = 3100;
            Injector.Pin(m);
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            _layers.Add((child.transform, m, spin, alpha));
        }

        void Update()
        {
            try
            {
                float dt = Time.deltaTime;
                _age += dt;
                float k = Mathf.Clamp01(_age / _life);
                if (k >= 1f) { End(); return; }
                float travel = Mathf.Lerp(k, 1f - (1f - k) * (1f - k), 0.35f);   // nearly even speed, a little ease out
                transform.position = _from + _dir * (_distance * travel);      // straight line
                float grow = Mathf.SmoothStep(0.35f, 1f, Mathf.Clamp01(_age / 0.15f));
                float fade = Mathf.Clamp01(_age / 0.08f) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(_life - 0.3f, _life, _age)));
                transform.localScale = new Vector3(_size * grow, _size * (0.6f + 0.4f * grow), _size * grow);
                foreach (var l in _layers)
                {
                    l.t.Rotate(0, l.spin * dt, 0, Space.Self);
                    var c = l.m.color; c.a = l.alpha * fade; l.m.color = c;
                }
            }
            catch (Exception e) { Plugin.L.LogError("Tornado vfx: " + e); End(); }
        }

        void End()
        {
            foreach (var l in _layers) { Injector.Unpin(l.m); Object.Destroy(l.m); }
            _layers.Clear();
            Injector.Unpin(gameObject);
            Object.Destroy(gameObject);
        }

        // ---- shared assets

        static void Build()
        {
            foreach (var name in new[] { "Sprites/Default", "Universal Render Pipeline/Particles/Unlit", "Legacy Shaders/Particles/Alpha Blended" })
            {
                foreach (var sh in Resources.FindObjectsOfTypeAll<Shader>()) if (sh.name == name) { _shader = sh; break; }
                _shader ??= Shader.Find(name);
                if (_shader != null) break;
            }
            if (_shader == null) throw new Exception("no transparent shader found");
            _streaks = Pin(Png.ToTexture(Streaks(128, 128), 128, 128, true), TextureWrapMode.Repeat);
            _dust = Pin(Png.ToTexture(Dust(128, 32), 128, 32, true), TextureWrapMode.Repeat);
            _funnel = Pin(Funnel(28, 14));
            _ring = Pin(Ring(28));
            Plugin.L.LogInfo($"Tornado vfx: shader '{_shader.name}'");
        }

        static Texture2D Pin(Texture2D t, TextureWrapMode wrap)
        {
            t.wrapMode = wrap; t.name = "CustomHeroes_Tornado"; t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return Injector.Pin(t);
        }

        static Mesh Pin(Mesh m) { m.hideFlags = HideFlags.DontUnloadUnusedAsset; return Injector.Pin(m); }

        /// <summary>Diagonal wind streaks, white with soft alpha (u wraps around the funnel, v goes up).</summary>
        static Color32[] Streaks(int w, int h)
        {
            var px = new Color32[w * h];
            var rnd = new System.Random(7);
            const int N = 9;
            var off = new float[N]; var wid = new float[N]; var amp = new float[N]; var slope = new float[N];
            for (int i = 0; i < N; i++)
            {
                off[i] = (float)rnd.NextDouble(); wid[i] = 0.015f + 0.05f * (float)rnd.NextDouble();
                amp[i] = 0.45f + 0.55f * (float)rnd.NextDouble(); slope[i] = 0.35f + 0.5f * (float)rnd.NextDouble();
            }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = x / (float)w, v = y / (float)h, a = 0;
                    for (int i = 0; i < N; i++)
                    {
                        float d = u + v * slope[i] - off[i];
                        d -= Mathf.Floor(d); d = Mathf.Min(d, 1 - d);           // distance to the band, wrapping
                        float along = 0.6f + 0.4f * Mathf.Sin((v * 2.3f + off[i]) * Mathf.PI * 2);
                        a += amp[i] * along * Mathf.Exp(-d * d / (wid[i] * wid[i]));
                    }
                    a = Mathf.Clamp01(a * 0.85f + 0.06f);
                    byte g = (byte)(225 + 30 * Mathf.Clamp01(a));
                    px[y * w + x] = new Color32(g, 255, 255, (byte)(a * 255));
                }
            return px;
        }

        /// <summary>Dust swirl for the ground ring: puffs around (u), fading at the inner and outer edge (v).</summary>
        static Color32[] Dust(int w, int h)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = x / (float)w, v = y / (float)(h - 1);
                    float puffs = 0.55f + 0.45f * Mathf.Sin(u * Mathf.PI * 2 * 5 + v * 3f) * Mathf.Sin(u * Mathf.PI * 2 * 3 + 1.3f);
                    float edge = Mathf.Sin(v * Mathf.PI);
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(puffs * edge * edge) * 255));
                }
            return px;
        }

        /// <summary>Twisted open funnel, 1 unit tall, narrow at the ground; alpha fades at the bottom and top rims.</summary>
        static Mesh Funnel(int seg, int rings)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var cols = new List<Color32>(); var tris = new List<int>();
            for (int r = 0; r <= rings; r++)
            {
                float hgt = r / (float)rings;
                float radius = 0.07f + 0.36f * Mathf.Pow(hgt, 1.6f);
                float twist = hgt * 2.2f;
                float a = Mathf.Clamp01(hgt / 0.15f) * Mathf.Clamp01((1f - hgt) / 0.2f);
                for (int s = 0; s <= seg; s++)
                {
                    float ang = s / (float)seg * Mathf.PI * 2 + twist;
                    verts.Add(new Vector3(Mathf.Cos(ang) * radius, hgt, Mathf.Sin(ang) * radius));
                    uvs.Add(new Vector2(s / (float)seg * 2f, hgt));
                    cols.Add(new Color32(255, 255, 255, (byte)(a * 255)));
                }
            }
            int row = seg + 1;
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < seg; s++)
                {
                    int i = r * row + s;
                    tris.Add(i); tris.Add(i + row); tris.Add(i + 1);
                    tris.Add(i + 1); tris.Add(i + row); tris.Add(i + row + 1);
                }
            return MakeMesh("CustomHeroes_TornadoFunnel", verts, uvs, cols, tris);
        }

        /// <summary>Flat ring on the ground around the funnel's foot.</summary>
        static Mesh Ring(int seg)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var cols = new List<Color32>(); var tris = new List<int>();
            for (int s = 0; s <= seg; s++)
            {
                float ang = s / (float)seg * Mathf.PI * 2;
                var d = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
                verts.Add(d * 0.08f + Vector3.up * 0.02f); uvs.Add(new Vector2(s / (float)seg * 2f, 0)); cols.Add(new Color32(255, 255, 255, 255));
                verts.Add(d * 0.42f + Vector3.up * 0.02f); uvs.Add(new Vector2(s / (float)seg * 2f, 1)); cols.Add(new Color32(255, 255, 255, 255));
            }
            for (int s = 0; s < seg; s++)
            {
                int i = s * 2;
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
                tris.Add(i + 2); tris.Add(i + 1); tris.Add(i + 3);
            }
            return MakeMesh("CustomHeroes_TornadoRing", verts, uvs, cols, tris);
        }

        static Mesh MakeMesh(string name, List<Vector3> v, List<Vector2> uv, List<Color32> c, List<int> t)
        {
            var m = new Mesh { name = name };
            m.vertices = v.ToArray();
            m.uv = uv.ToArray();
            m.colors32 = c.ToArray();
            m.triangles = t.ToArray();
            m.RecalculateBounds();
            return m;
        }
    }
}
