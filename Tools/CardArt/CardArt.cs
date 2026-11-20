// Pulls the hand-drawn card sketches out of the design PDFs (and a pasted PNG), and turns each into a clean
// card symbol: black lines on transparent, centred in a square, strokes thickened and edges smoothed.
// Usage: CardArt <outDir> <pdf1> <pdf2> <foldingHands.png>
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

static class CardArt
{
    const int Size = 512;          // output square
    const float Margin = 0.07f;    // empty border, fraction of the size
    const float Half = 7.5f;       // half stroke width in output pixels
    const int MinSpeck = 60;       // ink blobs smaller than this (source px) are stray specks

    static int Main(string[] a)
    {
        string outDir = a[0];
        Directory.CreateDirectory(outDir);
        // CardArt <outDir> --pdf <file.pdf> <CardId for image 1> <CardId for image 2> ...
        if (a.Length > 2 && a[1] == "--pdf")
        {
            var images = PdfImages(a[2]);
            Console.WriteLine($"{images.Count} images");
            for (int i = 3; i < a.Length && i - 3 < images.Count; i++) Save(images[i - 3], Path.Combine(outDir, a[i] + ".png"));
            return 0;
        }
        var first = PdfImages(a[1]);
        var second = PdfImages(a[2]);
        var fold = ReadPng(a[3]);
        Console.WriteLine($"pdf1: {first.Count} images, pdf2: {second.Count} images");
        Save(first[0], Path.Combine(outDir, "SummonWall.png"));
        Save(first[1], Path.Combine(outDir, "RockBarrage.png"));
        Save(fold, Path.Combine(outDir, "FoldingHands.png"));
        string[] names = { "Shove", "Tornado", "NomadicDash", "ManaMissiles", "MagicalBarrier", "ManaSiphon" };
        for (int i = 0; i < names.Length && i < second.Count; i++) Save(second[i], Path.Combine(outDir, names[i] + ".png"));
        return 0;
    }

    class Gray { public int w, h; public float[] ink; } // ink 0..1 (1 = black)

    // ------------------------------------------------------------------ PDF images

    static List<Gray> PdfImages(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.Latin1.GetString(bytes);
        var list = new List<Gray>();
        foreach (Match m in Regex.Matches(text, @"<<[^>]*?/Subtype\s*/Image[^>]*?>>\s*stream\r?\n"))
        {
            string dict = m.Value;
            int w = int.Parse(Regex.Match(dict, @"/Width\s+(\d+)").Groups[1].Value);
            int h = int.Parse(Regex.Match(dict, @"/Height\s+(\d+)").Groups[1].Value);
            int len = int.Parse(Regex.Match(dict, @"/Length\s+(\d+)").Groups[1].Value);
            var raw = Inflate(bytes, m.Index + m.Length, len);
            int n = raw.Length / (w * h);
            var g = new Gray { w = w, h = h, ink = new float[w * h] };
            for (int i = 0; i < w * h; i++)
            {
                float lum = n >= 3 ? (raw[i * n] * 0.299f + raw[i * n + 1] * 0.587f + raw[i * n + 2] * 0.114f) : raw[i * n];
                g.ink[i] = 1f - lum / 255f;
            }
            list.Add(g);
        }
        return list;
    }

    static byte[] Inflate(byte[] src, int offset, int length)
    {
        using var ms = new MemoryStream(src, offset, length);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }

    // ------------------------------------------------------------------ PNG in

    static Gray ReadPng(string path)
    {
        var b = File.ReadAllBytes(path);
        int p = 8, w = 0, h = 0, bitDepth = 0, colorType = 0;
        var idat = new MemoryStream();
        while (p < b.Length)
        {
            int len = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
            string type = Encoding.ASCII.GetString(b, p + 4, 4);
            if (type == "IHDR")
            {
                w = (b[p + 8] << 24) | (b[p + 9] << 16) | (b[p + 10] << 8) | b[p + 11];
                h = (b[p + 12] << 24) | (b[p + 13] << 16) | (b[p + 14] << 8) | b[p + 15];
                bitDepth = b[p + 16];
                colorType = b[p + 17];
            }
            else if (type == "IDAT") idat.Write(b, p + 8, len);
            else if (type == "IEND") break;
            p += 12 + len;
        }
        if (bitDepth != 8) throw new Exception("only 8-bit PNGs");
        int bpp = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new Exception("palette PNG not supported") };
        var data = idat.ToArray();
        var raw = Inflate(data, 0, data.Length);
        int stride = w * bpp;
        var img = new byte[h * stride];
        for (int y = 0; y < h; y++)
        {
            int f = raw[y * (stride + 1)];
            for (int x = 0; x < stride; x++)
            {
                int cur = raw[y * (stride + 1) + 1 + x];
                int left = x >= bpp ? img[y * stride + x - bpp] : 0;
                int up = y > 0 ? img[(y - 1) * stride + x] : 0;
                int ul = x >= bpp && y > 0 ? img[(y - 1) * stride + x - bpp] : 0;
                int v = f switch
                {
                    1 => cur + left,
                    2 => cur + up,
                    3 => cur + ((left + up) >> 1),
                    4 => cur + Paeth(left, up, ul),
                    _ => cur,
                };
                img[y * stride + x] = (byte)v;
            }
        }
        var g = new Gray { w = w, h = h, ink = new float[w * h] };
        for (int i = 0; i < w * h; i++)
        {
            float lum = bpp >= 3 ? img[i * bpp] * 0.299f + img[i * bpp + 1] * 0.587f + img[i * bpp + 2] * 0.114f : img[i * bpp];
            float alpha = bpp == 4 ? img[i * bpp + 3] / 255f : bpp == 2 ? img[i * bpp + 1] / 255f : 1f;
            g.ink[i] = (1f - lum / 255f) * alpha;
        }
        return g;
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ------------------------------------------------------------------ cleanup + render

    static void Save(Gray g, string path)
    {
        // ink mask, minus stray specks (the lone dot in each sketch's corner)
        var mask = new bool[g.w * g.h];
        for (int i = 0; i < mask.Length; i++) mask[i] = g.ink[i] > 0.4f;
        RemoveSpecks(mask, g.w, g.h);

        int x0 = g.w, y0 = g.h, x1 = -1, y1 = -1;
        for (int y = 0; y < g.h; y++)
        for (int x = 0; x < g.w; x++)
            if (mask[y * g.w + x]) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        if (x1 < 0) throw new Exception("no ink in " + path);

        // fit the drawing in the square (leaving room for the thicker strokes)
        float bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        float avail = Size * (1f - 2f * Margin) - 2f * Half;
        float scale = avail / Math.Max(bw, bh);
        // detailed drawings (shrunk a lot to fit) get slightly thinner strokes so their gaps stay open
        float half = Half * Math.Clamp(0.55f + scale * 0.45f, 0.7f, 1f);
        float ox = (Size - bw * scale) * 0.5f, oy = (Size - bh * scale) * 0.5f;

        // ink points at output resolution (centre of every source ink pixel)
        var ink = new bool[Size * Size];
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            if (!mask[y * g.w + x]) continue;
            int px = (int)(ox + (x - x0 + 0.5f) * scale), py = (int)(oy + (y - y0 + 0.5f) * scale);
            // fill the scaled pixel's footprint so lines stay connected when enlarged
            int r = scale > 1f ? (int)Math.Ceiling(scale * 0.5f) : 0;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int qx = px + dx, qy = py + dy;
                if (qx >= 0 && qy >= 0 && qx < Size && qy < Size) ink[qy * Size + qx] = true;
            }
        }

        // round, smooth, thick strokes: alpha from the distance to the nearest ink point
        var dist = DistanceField(ink, Size, (int)Math.Ceiling(Half) + 3);
        var px32 = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            float d = dist[y * Size + x];
            float a = Math.Clamp(half + 0.5f - d, 0f, 1f);
            int o = (y * Size + x) * 4; // PNG rows run top-down, like the source
            px32[o] = px32[o + 1] = px32[o + 2] = 0;
            px32[o + 3] = (byte)(a * 255f + 0.5f);
        }
        WritePng(path, px32, Size, Size);
        Console.WriteLine($"{Path.GetFileName(path)}  from {g.w}x{g.h}, drawing {bw}x{bh}, scale {scale:0.00}");
    }

    static void RemoveSpecks(bool[] m, int w, int h)
    {
        var seen = new bool[m.Length];
        var stack = new Stack<int>();
        var comp = new List<int>();
        for (int i = 0; i < m.Length; i++)
        {
            if (!m[i] || seen[i]) continue;
            comp.Clear();
            stack.Push(i);
            seen[i] = true;
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                comp.Add(c);
                int cx = c % w, cy = c / w;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int n = ny * w + nx;
                    if (m[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
                }
            }
            if (comp.Count < MinSpeck) foreach (int c in comp) m[c] = false;
        }
    }

    /// <summary>Distance (px) from each pixel to the nearest ink pixel, up to <paramref name="reach"/>.</summary>
    static float[] DistanceField(bool[] ink, int n, int reach)
    {
        var d = new float[n * n];
        for (int i = 0; i < d.Length; i++) d[i] = float.MaxValue;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            if (!ink[y * n + x]) continue;
            for (int dy = -reach; dy <= reach; dy++)
            {
                int yy = y + dy;
                if (yy < 0 || yy >= n) continue;
                for (int dx = -reach; dx <= reach; dx++)
                {
                    int xx = x + dx;
                    if (xx < 0 || xx >= n) continue;
                    float dd = MathF.Sqrt(dx * dx + dy * dy);
                    int k = yy * n + xx;
                    if (dd < d[k]) d[k] = dd;
                }
            }
        }
        return d;
    }

    // ------------------------------------------------------------------ PNG out

    static void WritePng(string path, byte[] rgba, int w, int h)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgba, y * w * 4, w * 4);
        }
        using var comp = new MemoryStream();
        using (var z = new ZLibStream(comp, CompressionLevel.Optimal, true)) { raw.Position = 0; raw.CopyTo(z); }
        Chunk(fs, "IDAT", comp.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BE(len, 0, data.Length);
        s.Write(len);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc(t, 0xFFFFFFFFu);
        crc = Crc(data, crc) ^ 0xFFFFFFFFu;
        var c = new byte[4];
        BE(c, 0, (int)crc);
        s.Write(c);
    }

    static uint[] _table;
    static uint Crc(byte[] d, uint c)
    {
        if (_table == null)
        {
            _table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint k = n;
                for (int i = 0; i < 8; i++) k = (k & 1) != 0 ? 0xEDB88320u ^ (k >> 1) : k >> 1;
                _table[n] = k;
            }
        }
        foreach (var b in d) c = _table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c;
    }
}
