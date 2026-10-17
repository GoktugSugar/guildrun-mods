// A custom hero's portraits and icons (hero.json "Portraits"), made once when its visual config is copied:
//  - <Folder>\<sprite name>.png replaces that sprite as is (full rect size, or the trimmed size Unity keeps),
//  - otherwise the Source cut-out is composed into the template sprite's frame: scaled to Scale of the template
//    hero, centered, head at the template's head height (Drop moves full-body art down so the card's name banner
//    cuts the feet). Square/round icons get a head close-up.
// The pixel work runs on the thread pool (a few hundred portraits on the main thread once froze the game).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace CustomHeroes
{
    internal static class Portraits
    {
        sealed class Job
        {
            public Sprite Orig; public string Name, Key;
            public Rect Rect; public Vector2 Pivot; public float Ppu;
            public Func<(Color32[] px, int w, int h)> Make;
        }

        static readonly Dictionary<string, (Color32[] px, int w, int h)> Pixels = new();
        static readonly Dictionary<string, (int top, int bot, float cx, float hx)> Bounds = new();
        static readonly object CacheLock = new();

        /// <summary>Replacement sprites (keyed by the template sprite) for every sprite there is a picture for.</summary>
        public static Dictionary<IntPtr, Sprite> Make(HeroDefinition d, List<(Sprite orig, string name)> sprites)
        {
            var result = new Dictionary<IntPtr, Sprite>();
            var p = d.Portraits;
            if (p == null) return result;
            var folder = d.Path(p.Folder);
            var source = d.Path(p.Source);
            if (source != null && !File.Exists(source)) { Plugin.L.LogWarning($"{d.Name}: portrait source {source} not found"); source = null; }
            float scale = p.Scale > 0 ? (float)p.Scale : 0.8f, drop = (float)p.Drop;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var jobs = new List<Job>();
            foreach (var (orig, name) in sprites)
            {
                var or = orig.rect;
                var tr = orig.textureRect;
                int rw = Mathf.RoundToInt(or.width), rh = Mathf.RoundToInt(or.height);
                int tw = Mathf.RoundToInt(tr.width), th = Mathf.RoundToInt(tr.height);
                var off = orig.textureRectOffset;
                int ox = Mathf.RoundToInt(off.x), oy = Mathf.RoundToInt(off.y);
                var job = new Job { Orig = orig, Name = name, Rect = or, Pivot = orig.pivot, Ppu = orig.pixelsPerUnit };
                var file = folder != null ? Path.Combine(folder, name + ".png") : null;
                if (file != null && File.Exists(file))
                {
                    // Unity trims transparent margins: textureRect sits at textureRectOffset inside rect.
                    // A PNG of the trimmed size gets the margins added back.
                    job.Key = $"{file}|{rw}x{rh}|{ox},{oy}";
                    job.Make = () =>
                    {
                        var src = LoadPixels(file);
                        if ((tw == rw && th == rh) || src.w != tw || src.h != th) return src;
                        var big = new Color32[rw * rh];
                        for (int y = 0; y < th; y++)
                        {
                            int dy = y + oy;
                            if (dy < 0 || dy >= rh) continue;
                            for (int x = 0; x < tw; x++)
                            {
                                int dx = x + ox;
                                if (dx >= 0 && dx < rw) big[dy * rw + dx] = src.px[y * tw + x];
                            }
                        }
                        return (big, rw, rh);
                    };
                }
                else if (source != null)
                {
                    var kind = KindOf(name);
                    job.Key = $"{source}|{kind}|{rw}x{rh}|{th}|{oy}";
                    job.Make = () => (Compose(source, kind, rw, rh, th, oy, scale, drop), rw, rh);
                }
                else continue;
                jobs.Add(job);
                Plugin.L.LogInfo($"{d.Name}: portrait '{name}' rect {rw}x{rh}, trimmed {tw}x{th} at ({ox}, {oy})");
            }

            // the pixels, once per distinct frame, in parallel
            var tasks = new Dictionary<string, Task<(Color32[] px, int w, int h)>>();
            foreach (var j in jobs) if (!tasks.ContainsKey(j.Key)) tasks[j.Key] = Task.Run(j.Make);
            try { Task.WaitAll(tasks.Values.ToArray()); } catch { }   // failures are reported per sprite below

            var textures = new Dictionary<string, Texture2D>();
            foreach (var j in jobs)
            {
                try
                {
                    if (!textures.TryGetValue(j.Key, out var tex))
                    {
                        var (px, w, h) = tasks[j.Key].Result;
                        tex = Png.ToTexture(px, w, h, false);
                        tex.name = j.Name;
                        tex.wrapMode = TextureWrapMode.Clamp;
                        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        Injector.Pin(tex);
                        textures[j.Key] = tex;
                    }
                    // fit the original's aspect ratio: crop the sides, or crop from the bottom so heads stay in frame
                    var or = j.Rect;
                    float aspect = or.width / or.height, tw = tex.width, th = tex.height;
                    Rect crop = tw / th > aspect
                        ? new Rect((tw - th * aspect) / 2f, 0, th * aspect, th)
                        : new Rect(0, th - tw / aspect, tw, tw / aspect);
                    var pivot = new Vector2(j.Pivot.x / or.width, j.Pivot.y / or.height);
                    float ppu = j.Ppu * crop.width / or.width;
                    var sp = Sprite.Create(tex, crop, pivot, ppu, 0, SpriteMeshType.FullRect);
                    sp.name = j.Name;
                    sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    Injector.Pin(sp);
                    result[j.Orig.Pointer] = sp;
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning($"{d.Name}: portrait '{j.Name}': {(e is AggregateException a ? a.InnerException : e)}");
                }
            }
            Plugin.L.LogInfo($"{d.Name}: {result.Count} of {sprites.Count} portrait sprites made ({textures.Count} pictures) in {sw.ElapsedMilliseconds} ms");
            return result;
        }

        static (Color32[] px, int w, int h) LoadPixels(string file)
        {
            lock (CacheLock) { if (Pixels.TryGetValue(file, out var hit)) return hit; }
            var px = Png.DecodePixels(File.ReadAllBytes(file), out int fw, out int fh);
            lock (CacheLock) { Pixels[file] = (px, fw, fh); }
            return (px, fw, fh);
        }

        enum Kind { Full, Half, Med, Head, Round }

        static Kind KindOf(string n)
        {
            if (n.IndexOf("round", StringComparison.OrdinalIgnoreCase) >= 0) return Kind.Round;
            if (n.IndexOf("Square", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Small", StringComparison.OrdinalIgnoreCase) >= 0) return Kind.Head;
            if (n.EndsWith("_Med", StringComparison.OrdinalIgnoreCase)) return Kind.Med;
            if (n.StartsWith("HalfBody", StringComparison.OrdinalIgnoreCase)) return Kind.Half;
            return Kind.Full;
        }

        /// <summary>Places the cut-out into a rw x rh canvas the way the template hero sits in it.</summary>
        static Color32[] Compose(string source, Kind kind, int rw, int rh, int th, int oy, float scale, float drop)
        {
            var src = LoadPixels(source);
            (int top, int bot, float cx, float hx) b;
            bool known;
            lock (CacheLock) known = Bounds.TryGetValue(source, out b);
            if (!known)
            {
                int top = int.MaxValue, bot = -1, left = int.MaxValue, right = -1;
                for (int y = 0; y < src.h; y++)            // y = row from the top
                {
                    int row = (src.h - 1 - y) * src.w;
                    for (int x = 0; x < src.w; x++)
                        if (src.px[row + x].a > 128) { top = Math.Min(top, y); bot = Math.Max(bot, y); left = Math.Min(left, x); right = Math.Max(right, x); }
                }
                double sx = 0; int cnt = 0;                  // head center: solid pixels in the top 8% of the figure
                for (int y = top; y < top + (bot - top) * 0.08; y++)
                {
                    int row = (src.h - 1 - y) * src.w;
                    for (int x = 0; x < src.w; x++) if (src.px[row + x].a > 128) { sx += x; cnt++; }
                }
                b = (top, bot, (left + right) / 2f, cnt > 0 ? (float)(sx / cnt) : (left + right) / 2f);
                lock (CacheLock) Bounds[source] = b;
            }
            float fh = b.bot - b.top;
            int origTop = rh - oy - th;                       // template hero's head top, from the top of the canvas
            float frac, s, x0, y0, cxOut;
            switch (kind)
            {
                case Kind.Head: case Kind.Round:
                    frac = 0.20f; s = rh * 0.98f / (fh * frac); cxOut = b.hx; y0 = rh * 0.02f - b.top * s; break;
                default:
                    frac = kind == Kind.Med ? 0.48f : kind == Kind.Half ? 0.70f : 1f;
                    s = th * 0.98f * scale / (fh * frac); cxOut = b.cx; y0 = origTop + th * 0.01f - b.top * s;
                    if (kind == Kind.Full) y0 += drop * th;   // full body: let the card's banner cut the feet
                    break;
            }
            x0 = rw / 2f - cxOut * s;
            var outPx = new Color32[rw * rh];
            float inv = 1f / s, r2 = (Math.Min(rw, rh) / 2f) * (Math.Min(rw, rh) / 2f);
            for (int y = 0; y < rh; y++)
            {
                float syf = (y - y0) * inv;
                if (syf < 0 || syf > src.h - 1) continue;
                int row = (rh - 1 - y) * rw;
                for (int x = 0; x < rw; x++)
                {
                    float sxf = (x - x0) * inv;
                    if (sxf < 0 || sxf > src.w - 1) continue;
                    if (kind == Kind.Round) { float dx = x - rw / 2f, dy = y - rh / 2f; if (dx * dx + dy * dy > r2) continue; }
                    outPx[row + x] = Sample(src, sxf, syf);
                }
            }
            return outPx;
        }

        // Bilinear sample at (x, y-from-top), premultiplied so transparent edges don't darken.
        static Color32 Sample((Color32[] px, int w, int h) src, float x, float y)
        {
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, src.w - 1), y1 = Math.Min(y0 + 1, src.h - 1);
            float fx = x - x0, fy = y - y0;
            Color32 P(int px, int py) => src.px[(src.h - 1 - py) * src.w + px];
            float r = 0, g = 0, bl = 0, a = 0;
            void Add(Color32 c, float wgt) { float wa = c.a * wgt; r += c.r * wa; g += c.g * wa; bl += c.b * wa; a += wa; }
            Add(P(x0, y0), (1 - fx) * (1 - fy)); Add(P(x1, y0), fx * (1 - fy)); Add(P(x0, y1), (1 - fx) * fy); Add(P(x1, y1), fx * fy);
            if (a <= 0.001f) return default;
            return new Color32((byte)(r / a), (byte)(g / a), (byte)(bl / a), (byte)Math.Min(255f, a));
        }
    }
}
