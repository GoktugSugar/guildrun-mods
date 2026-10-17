// Minimal glTF 2.0 binary (.glb) loader: one skinned mesh (every primitive becomes a submesh), its skin,
// base color textures, node rest pose, the first frame of the first animation and every animation as a clip
// (played by HeroModel). Converts glTF's right-handed
// space to Unity's left-handed space by mirroring X.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace CustomHeroes
{
    internal sealed class GlbNode
    {
        public string Name;
        public int Parent = -1;
        public Vector3 T; public Quaternion R = Quaternion.identity; public Vector3 S = Vector3.one;  // rest local TRS
        public Vector3 T0; public Quaternion R0; public Vector3 S0;                                  // first animation frame
    }

    /// <summary>One glTF animation: keyframed translation/rotation/scale channels, in Unity space.</summary>
    internal sealed class GlbClip
    {
        public string Name;
        public float Length;
        public readonly List<GlbChannel> Channels = new();
    }

    internal sealed class GlbChannel
    {
        public int Node;
        public int Path;          // 0 translation, 1 rotation, 2 scale
        public float[] Times;
        public float[] Values;    // Times.Length * 3 (or * 4 for rotations)
        public bool Step;

        public Vector3 Vec(float t)
        {
            int i = Find(t, out float f);
            int a = i * 3, b = Math.Min(i + 1, Times.Length - 1) * 3;
            var va = new Vector3(Values[a], Values[a + 1], Values[a + 2]);
            if (Step || f <= 0) return va;
            return Vector3.LerpUnclamped(va, new Vector3(Values[b], Values[b + 1], Values[b + 2]), f);
        }

        public Quaternion Rot(float t)
        {
            int i = Find(t, out float f);
            int a = i * 4, b = Math.Min(i + 1, Times.Length - 1) * 4;
            var qa = new Quaternion(Values[a], Values[a + 1], Values[a + 2], Values[a + 3]);
            if (Step || f <= 0) return qa;
            return Quaternion.Slerp(qa, new Quaternion(Values[b], Values[b + 1], Values[b + 2], Values[b + 3]), f);
        }

        /// <summary>Key index at or before t, and the fraction towards the next key.</summary>
        int Find(float t, out float f)
        {
            f = 0;
            int n = Times.Length;
            if (n == 0 || t <= Times[0]) return 0;
            if (t >= Times[n - 1]) return n - 1;
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (Times[mid] <= t) lo = mid; else hi = mid; }
            float span = Times[hi] - Times[lo];
            f = span > 1e-6f ? (t - Times[lo]) / span : 0;
            return lo;
        }
    }

    internal sealed class GlbModel
    {
        public readonly List<GlbClip> Clips = new();     // every animation in the file
        public readonly List<GlbNode> Nodes = new();
        public int[] Joints;                 // skin joint index -> node index
        public Matrix4x4[] InverseBind;      // per skin joint, Unity space
        public Vector3[] Positions;
        public Vector2[] UVs;
        public BoneWeight[] Weights;
        public int[] Triangles;              // all submeshes together
        public int[][] SubTriangles;         // per glTF primitive
        public int[] SubImage;               // per primitive: index into Images, -1 = untextured
        public readonly List<byte[]> Images = new();
        public string AnimName;

        JsonElement _root;
        byte[] _bin;

        public static GlbModel Load(string path)
        {
            var f = File.ReadAllBytes(path);
            if (BitConverter.ToUInt32(f, 0) != 0x46546C67) throw new Exception("not a .glb file");
            int jsonLen = BitConverter.ToInt32(f, 12);
            var json = Encoding.UTF8.GetString(f, 20, jsonLen);
            int binStart = 20 + jsonLen;
            int binLen = BitConverter.ToInt32(f, binStart);
            var m = new GlbModel { _root = JsonDocument.Parse(json).RootElement, _bin = new byte[binLen] };
            Buffer.BlockCopy(f, binStart + 8, m._bin, 0, binLen);
            m.Parse();
            return m;
        }

        static Vector3 V(float x, float y, float z) => new(-x, y, z);
        static Quaternion Q(float x, float y, float z, float w) => new(x, -y, -z, w);

        void Parse()
        {
            var nodes = _root.GetProperty("nodes");
            foreach (var n in nodes.EnumerateArray())
            {
                var g = new GlbNode { Name = n.TryGetProperty("name", out var nm) ? nm.GetString() : "node" + Nodes.Count };
                if (n.TryGetProperty("translation", out var t)) g.T = V(F(t, 0), F(t, 1), F(t, 2));
                if (n.TryGetProperty("rotation", out var r)) g.R = Q(F(r, 0), F(r, 1), F(r, 2), F(r, 3));
                if (n.TryGetProperty("scale", out var s)) g.S = new Vector3(F(s, 0), F(s, 1), F(s, 2));
                if (n.TryGetProperty("matrix", out var mx))
                {
                    var mm = Conv(Mat(mx, 0));
                    g.T = mm.GetColumn(3); g.R = Rot(mm); g.S = mm.lossyScale;
                }
                g.T0 = g.T; g.R0 = g.R; g.S0 = g.S;
                Nodes.Add(g);
            }
            int i = 0;
            foreach (var n in nodes.EnumerateArray())
            {
                if (n.TryGetProperty("children", out var ch))
                    foreach (var c in ch.EnumerateArray()) Nodes[c.GetInt32()].Parent = i;
                i++;
            }

            var skin = _root.GetProperty("skins")[0];
            var jl = new List<int>();
            foreach (var j in skin.GetProperty("joints").EnumerateArray()) jl.Add(j.GetInt32());
            Joints = jl.ToArray();
            var ibm = ReadFloats(skin.GetProperty("inverseBindMatrices").GetInt32(), out _);
            InverseBind = new Matrix4x4[Joints.Length];
            for (int k = 0; k < Joints.Length; k++) InverseBind[k] = Conv(MatArr(ibm, k * 16));

            // Every primitive of the first mesh. Primitives often share one vertex buffer (Dr. Mundo's body,
            // legs, cleaver and bag), so vertices are read once per POSITION accessor.
            var pos = new List<Vector3>(); var uvs = new List<Vector2>(); var wts = new List<BoneWeight>();
            var baseOf = new Dictionary<int, int>();
            var subs = new List<int[]>(); var subImg = new List<int>();
            var imgOf = new Dictionary<int, int>();
            foreach (var prim in _root.GetProperty("meshes")[0].GetProperty("primitives").EnumerateArray())
            {
                var attr = prim.GetProperty("attributes");
                int posAcc = attr.GetProperty("POSITION").GetInt32();
                if (!baseOf.TryGetValue(posAcc, out int bas))
                {
                    baseOf[posAcc] = bas = pos.Count;
                    ReadVertices(attr, pos, uvs, wts);
                }
                int vc = pos.Count - bas;
                int[] tris;
                if (prim.TryGetProperty("indices", out var idxA))
                {
                    var idx = ReadFloats(idxA.GetInt32(), out _);
                    tris = new int[idx.Length - idx.Length % 3];
                    for (int k = 0; k + 2 < idx.Length; k += 3)
                    {   // mirroring flips winding
                        tris[k] = bas + (int)idx[k]; tris[k + 1] = bas + (int)idx[k + 2]; tris[k + 2] = bas + (int)idx[k + 1];
                    }
                }
                else
                {
                    tris = new int[vc - vc % 3];
                    for (int k = 0; k + 2 < vc; k += 3) { tris[k] = bas + k; tris[k + 1] = bas + k + 2; tris[k + 2] = bas + k + 1; }
                }
                subs.Add(tris);
                subImg.Add(ImageOf(prim, imgOf));
            }
            Positions = pos.ToArray(); UVs = uvs.ToArray(); Weights = wts.ToArray();
            SubTriangles = subs.ToArray(); SubImage = subImg.ToArray();
            var all = new List<int>();
            foreach (var t in subs) all.AddRange(t);
            Triangles = all.ToArray();

            // first frame of the first animation (used to find where the weapon sits in the hand)
            if (_root.TryGetProperty("animations", out var anims) && anims.GetArrayLength() > 0)
            {
                var a = anims[0];
                AnimName = a.TryGetProperty("name", out var an) ? an.GetString() : "anim0";
                var samplers = a.GetProperty("samplers");
                foreach (var ch in a.GetProperty("channels").EnumerateArray())
                {
                    var tgt = ch.GetProperty("target");
                    if (!tgt.TryGetProperty("node", out var nn)) continue;
                    var node = Nodes[nn.GetInt32()];
                    var smp = samplers[ch.GetProperty("sampler").GetInt32()];
                    bool cubic = smp.TryGetProperty("interpolation", out var ip) && ip.GetString() == "CUBICSPLINE";
                    var outp = ReadFloats(smp.GetProperty("output").GetInt32(), out int comps);
                    int e = cubic ? comps : 0;   // cubic spline: [inTangent, value, outTangent]
                    switch (tgt.GetProperty("path").GetString())
                    {
                        case "translation": node.T0 = V(outp[e], outp[e + 1], outp[e + 2]); break;
                        case "rotation": node.R0 = Q(outp[e], outp[e + 1], outp[e + 2], outp[e + 3]).normalized; break;
                        case "scale": node.S0 = new Vector3(outp[e], outp[e + 1], outp[e + 2]); break;
                    }
                }
                foreach (var anim in anims.EnumerateArray()) Clips.Add(ReadClip(anim));
            }
        }

        GlbClip ReadClip(JsonElement a)
        {
            var clip = new GlbClip { Name = a.TryGetProperty("name", out var an) ? an.GetString() : "anim" + Clips.Count };
            var samplers = a.GetProperty("samplers");
            foreach (var ch in a.GetProperty("channels").EnumerateArray())
            {
                var tgt = ch.GetProperty("target");
                if (!tgt.TryGetProperty("node", out var nn)) continue;
                int path = tgt.GetProperty("path").GetString() switch { "translation" => 0, "rotation" => 1, "scale" => 2, _ => -1 };
                if (path < 0) continue;
                var smp = samplers[ch.GetProperty("sampler").GetInt32()];
                var interp = smp.TryGetProperty("interpolation", out var ip) ? ip.GetString() : "LINEAR";
                var times = ReadFloats(smp.GetProperty("input").GetInt32(), out _);
                var outp = ReadFloats(smp.GetProperty("output").GetInt32(), out int comps);
                bool cubic = interp == "CUBICSPLINE";
                int stride = cubic ? comps * 3 : comps, off = cubic ? comps : 0;   // cubic: [inTangent, value, outTangent]
                var vals = new float[times.Length * comps];
                for (int k = 0; k < times.Length; k++)
                {
                    int s = k * stride + off, d = k * comps;
                    if (path == 0) { var v = V(outp[s], outp[s + 1], outp[s + 2]); vals[d] = v.x; vals[d + 1] = v.y; vals[d + 2] = v.z; }
                    else if (path == 1)
                    {
                        var q = Q(outp[s], outp[s + 1], outp[s + 2], outp[s + 3]).normalized;
                        if (k > 0 && vals[d - 4] * q.x + vals[d - 3] * q.y + vals[d - 2] * q.z + vals[d - 1] * q.w < 0) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                        vals[d] = q.x; vals[d + 1] = q.y; vals[d + 2] = q.z; vals[d + 3] = q.w;
                    }
                    else { vals[d] = outp[s]; vals[d + 1] = outp[s + 1]; vals[d + 2] = outp[s + 2]; }
                }
                clip.Channels.Add(new GlbChannel { Node = nn.GetInt32(), Path = path, Times = times, Values = vals, Step = interp == "STEP" });
                if (times.Length > 0) clip.Length = Math.Max(clip.Length, times[^1]);
            }
            return clip;
        }

        void ReadVertices(JsonElement attr, List<Vector3> positions, List<Vector2> uvs, List<BoneWeight> weights)
        {
            var pos = ReadFloats(attr.GetProperty("POSITION").GetInt32(), out _);
            int vc = pos.Length / 3;
            for (int k = 0; k < vc; k++) positions.Add(V(pos[k * 3], pos[k * 3 + 1], pos[k * 3 + 2]));
            var uv = attr.TryGetProperty("TEXCOORD_0", out var uvA) ? ReadFloats(uvA.GetInt32(), out _) : null;
            for (int k = 0; k < vc; k++) uvs.Add(uv != null ? new Vector2(uv[k * 2], 1f - uv[k * 2 + 1]) : Vector2.zero);
            var js = attr.TryGetProperty("JOINTS_0", out var jA) ? ReadFloats(jA.GetInt32(), out _) : null;
            var ws = attr.TryGetProperty("WEIGHTS_0", out var wA) ? ReadFloats(wA.GetInt32(), out _) : null;
            for (int k = 0; k < vc; k++)
            {
                if (js == null || ws == null) { weights.Add(new BoneWeight { weight0 = 1 }); continue; }
                // Unity wants weights sorted descending.
                var pairs = new (int j, float w)[4];
                float sum = 0;
                for (int c = 0; c < 4; c++) { pairs[c] = ((int)js[k * 4 + c], ws[k * 4 + c]); sum += ws[k * 4 + c]; }
                Array.Sort(pairs, (a, b) => b.w.CompareTo(a.w));
                if (sum <= 0) sum = 1;
                weights.Add(new BoneWeight
                {
                    boneIndex0 = pairs[0].j, weight0 = pairs[0].w / sum,
                    boneIndex1 = pairs[1].j, weight1 = pairs[1].w / sum,
                    boneIndex2 = pairs[2].j, weight2 = pairs[2].w / sum,
                    boneIndex3 = pairs[3].j, weight3 = pairs[3].w / sum,
                });
            }
        }

        /// <summary>The primitive's embedded base color texture (PNG/JPG bytes) as an index into Images, or -1.</summary>
        int ImageOf(JsonElement prim, Dictionary<int, int> imgOf)
        {
            try
            {
                int mat = prim.TryGetProperty("material", out var mi) ? mi.GetInt32() : 0;
                var pbr = _root.GetProperty("materials")[mat].GetProperty("pbrMetallicRoughness");
                int tex = pbr.GetProperty("baseColorTexture").GetProperty("index").GetInt32();
                int img = _root.GetProperty("textures")[tex].GetProperty("source").GetInt32();
                if (imgOf.TryGetValue(img, out int known)) return known;
                var bv = _root.GetProperty("bufferViews")[_root.GetProperty("images")[img].GetProperty("bufferView").GetInt32()];
                int off = bv.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
                var bytes = new byte[bv.GetProperty("byteLength").GetInt32()];
                Buffer.BlockCopy(_bin, off, bytes, 0, bytes.Length);
                Images.Add(bytes);
                return imgOf[img] = Images.Count - 1;
            }
            catch (Exception e) { Plugin.L.LogWarning("No embedded base color texture: " + e.Message); return -1; }
        }

        static float F(JsonElement a, int i) => a[i].GetSingle();

        static Matrix4x4 Mat(JsonElement a, int o)
        {
            var f = new float[16];
            for (int i = 0; i < 16; i++) f[i] = a[o + i].GetSingle();
            return MatArr(f, 0);
        }

        static Matrix4x4 MatArr(float[] f, int o)
        {
            var m = new Matrix4x4();
            for (int c = 0; c < 4; c++)
                m.SetColumn(c, new Vector4(f[o + c * 4], f[o + c * 4 + 1], f[o + c * 4 + 2], f[o + c * 4 + 3]));
            return m;
        }

        /// <summary>Mirror X: S * M * S with S = diag(-1,1,1,1).</summary>
        static Matrix4x4 Conv(Matrix4x4 m)
        {
            m.m01 = -m.m01; m.m02 = -m.m02; m.m03 = -m.m03;
            m.m10 = -m.m10; m.m20 = -m.m20; m.m30 = -m.m30;
            return m;
        }

        public static Quaternion Rot(Matrix4x4 m)
        {
            Vector3 fwd = m.GetColumn(2), up = m.GetColumn(1);
            return fwd.sqrMagnitude < 1e-12f || up.sqrMagnitude < 1e-12f ? Quaternion.identity : Quaternion.LookRotation(fwd, up);
        }

        float[] ReadFloats(int accessor, out int comps)
        {
            var a = _root.GetProperty("accessors")[accessor];
            int count = a.GetProperty("count").GetInt32();
            comps = a.GetProperty("type").GetString() switch
            {
                "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, "MAT3" => 9, "MAT2" => 4, _ => 1
            };
            int ct = a.GetProperty("componentType").GetInt32();
            bool norm = a.TryGetProperty("normalized", out var nz) && nz.GetBoolean();
            int csize = ct switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };
            var bv = _root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
            int off = (bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0)
                    + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
            int stride = bv.TryGetProperty("byteStride", out var st) ? st.GetInt32() : csize * comps;
            var r = new float[count * comps];
            for (int i = 0; i < count; i++)
                for (int c = 0; c < comps; c++)
                {
                    int p = off + i * stride + c * csize;
                    r[i * comps + c] = ct switch
                    {
                        5126 => BitConverter.ToSingle(_bin, p),
                        5125 => BitConverter.ToUInt32(_bin, p),
                        5123 => norm ? BitConverter.ToUInt16(_bin, p) / 65535f : BitConverter.ToUInt16(_bin, p),
                        5122 => norm ? Math.Max(BitConverter.ToInt16(_bin, p) / 32767f, -1f) : BitConverter.ToInt16(_bin, p),
                        5121 => norm ? _bin[p] / 255f : _bin[p],
                        5120 => norm ? Math.Max((sbyte)_bin[p] / 127f, -1f) : (sbyte)_bin[p],
                        _ => 0,
                    };
                }
            return r;
        }
    }
}
