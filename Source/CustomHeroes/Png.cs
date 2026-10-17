// Small PNG decoder (8-bit gray/RGB/RGBA/palette, non-interlaced). Unity's ImageConversion.LoadImage
// only has a ReadOnlySpan overload in the interop assemblies, which fails at runtime.
using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace CustomHeroes
{
    internal static class Png
    {
        public static Texture2D Decode(byte[] png, bool mips = true)
        {
            var px = DecodePixels(png, out int w, out int h);
            return ToTexture(px, w, h, mips);
        }

        public static Texture2D ToTexture(Color32[] px, int w, int h, bool mips)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mips);
            tex.SetPixels32(px);
            tex.Apply(mips, false);
            return tex;
        }

        /// <summary>Decodes to bottom-up RGBA pixels (Unity's row order).</summary>
        public static Color32[] DecodePixels(byte[] png, out int w, out int h)
        {
            w = 0; h = 0;
            int depth = 0, type = 0, interlace = 0;
            byte[] palette = null, trns = null;
            var idat = new MemoryStream();
            int p = 8;
            while (p + 8 <= png.Length)
            {
                int len = BE(png, p);
                string chunk = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
                int d = p + 8;
                switch (chunk)
                {
                    case "IHDR": w = BE(png, d); h = BE(png, d + 4); depth = png[d + 8]; type = png[d + 9]; interlace = png[d + 12]; break;
                    case "PLTE": palette = png.AsSpan(d, len).ToArray(); break;
                    case "tRNS": trns = png.AsSpan(d, len).ToArray(); break;
                    case "IDAT": idat.Write(png, d, len); break;
                }
                if (chunk == "IEND") break;
                p = d + len + 4;
            }
            if (depth != 8 || interlace != 0) throw new NotSupportedException($"PNG bit depth {depth}, interlace {interlace}");
            int bpp = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException("PNG color type " + type) };

            idat.Position = 2; // skip zlib header
            var raw = new MemoryStream();
            using (var z = new DeflateStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
            var data = raw.ToArray();

            int stride = w * bpp;
            var cur = new byte[stride];
            var prev = new byte[stride];
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int off = y * (stride + 1);
                byte filter = data[off];
                Buffer.BlockCopy(data, off + 1, cur, 0, stride);
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? cur[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
                    cur[x] = (byte)(cur[x] + filter switch
                    {
                        1 => a, 2 => b, 3 => (a + b) >> 1, 4 => Paeth(a, b, c), _ => 0
                    });
                }
                int row = (h - 1 - y) * w; // Unity textures start at the bottom row
                for (int x = 0; x < w; x++)
                {
                    int i = x * bpp;
                    px[row + x] = type switch
                    {
                        0 => new Color32(cur[i], cur[i], cur[i], 255),
                        2 => new Color32(cur[i], cur[i + 1], cur[i + 2], 255),
                        3 => new Color32(palette[cur[i] * 3], palette[cur[i] * 3 + 1], palette[cur[i] * 3 + 2],
                                         trns != null && cur[i] < trns.Length ? trns[cur[i]] : (byte)255),
                        4 => new Color32(cur[i], cur[i], cur[i], cur[i + 1]),
                        _ => new Color32(cur[i], cur[i + 1], cur[i + 2], cur[i + 3]),
                    };
                }
                (prev, cur) = (cur, prev);
            }

            return px;
        }

        static int BE(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}
