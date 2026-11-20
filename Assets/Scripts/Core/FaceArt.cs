using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The face you draw on your character. Stored (and sent over the network) as strokes — lists of points on a
    /// 256x256 grid — and turned into a texture by <see cref="FacePainter"/>: white lines with a black outline.
    /// Saved in PlayerPrefs; <see cref="Changed"/> fires when the local face is edited.
    /// </summary>
    public static class FaceArt
    {
        public const int Grid = 256;          // point coordinates are 0..255 (x right, y up, as seen from the front)
        public const int Margin = 10;         // keep lines off the texture edge so they never smear when projected
        public const int MaxBytes = 2200;     // ink limit (fits comfortably in one network value)
        const string PrefKey = "sme_face";
        const byte Version = 1;

        public static event Action Changed;

        static List<List<Vector2Int>> _strokes;
        static string _encoded;

        /// <summary>Your face (a copy — edit it and pass it to <see cref="Set"/>).</summary>
        public static List<List<Vector2Int>> Strokes
        {
            get
            {
                Load();
                var copy = new List<List<Vector2Int>>(_strokes.Count);
                foreach (var s in _strokes) copy.Add(new List<Vector2Int>(s));
                return copy;
            }
        }

        /// <summary>Your face in network form (base64).</summary>
        public static string Encoded { get { Load(); return _encoded; } }

        public static void Set(List<List<Vector2Int>> strokes)
        {
            Load();
            _strokes = new List<List<Vector2Int>>();
            foreach (var s in strokes) if (s != null && s.Count > 0) _strokes.Add(new List<Vector2Int>(s));
            _encoded = Encode(_strokes);
            PlayerPrefs.SetString(PrefKey, _encoded);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        static void Load()
        {
            if (_strokes != null) return;
            _encoded = PlayerPrefs.GetString(PrefKey, "");
            _strokes = Decode(_encoded);
            if (_strokes.Count == 0) _encoded = "";
        }

        public static Vector2Int Clamp(Vector2Int p) =>
            new(Mathf.Clamp(p.x, Margin, Grid - 1 - Margin), Mathf.Clamp(p.y, Margin, Grid - 1 - Margin));

        // ---- encoding: [version] then per stroke [point count 1..255][x y]... ----

        /// <summary>Bytes the strokes take up when encoded (compare against <see cref="MaxBytes"/>).</summary>
        public static int SizeOf(List<List<Vector2Int>> strokes)
        {
            int n = 1;
            foreach (var s in strokes)
            {
                if (s == null) continue;
                int i = 0;
                while (i < s.Count) // same chunking as Encode: long strokes are split, sharing a point
                {
                    int count = Mathf.Min(255, s.Count - i);
                    n += 1 + count * 2;
                    i += count;
                    if (i < s.Count) i--;
                }
            }
            return n;
        }

        public static string Encode(List<List<Vector2Int>> strokes)
        {
            var b = new List<byte> { Version };
            bool any = false;
            foreach (var s in strokes)
            {
                if (s == null || s.Count == 0) continue;
                int i = 0;
                while (i < s.Count)
                {
                    int count = Mathf.Min(255, s.Count - i);
                    b.Add((byte)count);
                    for (int k = 0; k < count; k++)
                    {
                        var p = Clamp(s[i + k]);
                        b.Add((byte)p.x);
                        b.Add((byte)p.y);
                    }
                    any = true;
                    i += count;
                    if (i < s.Count) i--; // the next chunk starts where this one ended, so the line stays joined
                }
            }
            return any ? Convert.ToBase64String(b.ToArray()) : "";
        }

        public static List<List<Vector2Int>> Decode(string encoded)
        {
            var strokes = new List<List<Vector2Int>>();
            if (string.IsNullOrEmpty(encoded)) return strokes;
            byte[] b;
            try { b = Convert.FromBase64String(encoded); }
            catch (FormatException) { return strokes; }
            if (b.Length < 1 || b[0] != Version) return strokes;
            int i = 1;
            while (i < b.Length)
            {
                int count = b[i++];
                if (count == 0 || i + count * 2 > b.Length) break;
                var s = new List<Vector2Int>(count);
                for (int k = 0; k < count; k++, i += 2) s.Add(Clamp(new Vector2Int(b[i], b[i + 1])));
                strokes.Add(s);
            }
            return strokes;
        }
    }

    /// <summary>Rasterises face strokes into a transparent texture: white lines with a black outline, anti-aliased.</summary>
    public class FacePainter
    {
        public const int Size = FaceArt.Grid;
        public const float LineRadius = 4.5f;   // white core, in texture pixels
        public const float OutlineWidth = 3f;   // black border around it

        public readonly Texture2D Texture;
        readonly float[] _white = new float[Size * Size];
        readonly float[] _outline = new float[Size * Size];
        readonly Color32[] _px = new Color32[Size * Size];

        public FacePainter()
        {
            Texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "Face",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 4,
            };
            Texture.SetPixels32(_px);
            Texture.Apply(false);
        }

        public void Draw(List<List<Vector2Int>> strokes)
        {
            Array.Clear(_white, 0, _white.Length);
            Array.Clear(_outline, 0, _outline.Length);
            foreach (var s in strokes)
            {
                if (s == null || s.Count == 0) continue;
                if (s.Count == 1) Stamp(s[0], s[0]);
                for (int i = 1; i < s.Count; i++) Stamp(s[i - 1], s[i]);
            }
            Compose(0, 0, Size - 1, Size - 1);
            Texture.SetPixels32(_px);
            Texture.Apply(false);
        }

        /// <summary>Adds one segment (or a dot when a == b) without redrawing everything — for live drawing.</summary>
        public void AddSegment(Vector2Int a, Vector2Int b)
        {
            Stamp(a, b, out int x0, out int y0, out int x1, out int y1);
            Compose(x0, y0, x1, y1);
            Texture.SetPixels32(_px);
            Texture.Apply(false);
        }

        void Stamp(Vector2Int a, Vector2Int b) => Stamp(a, b, out _, out _, out _, out _);

        void Stamp(Vector2Int ai, Vector2Int bi, out int x0, out int y0, out int x1, out int y1)
        {
            Vector2 a = ai, b = bi;
            float reach = LineRadius + OutlineWidth + 1f;
            x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, b.x) - reach));
            y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, b.y) - reach));
            x1 = Mathf.Min(Size - 1, Mathf.CeilToInt(Mathf.Max(a.x, b.x) + reach));
            y1 = Mathf.Min(Size - 1, Mathf.CeilToInt(Mathf.Max(a.y, b.y) + reach));
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                Vector2 p = new(x, y);
                float t = len2 > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
                float d = Vector2.Distance(p, a + ab * t);
                int i = y * Size + x;
                float w = Mathf.Clamp01(LineRadius + 0.5f - d);
                float o = Mathf.Clamp01(LineRadius + OutlineWidth + 0.5f - d);
                if (w > _white[i]) _white[i] = w;
                if (o > _outline[i]) _outline[i] = o;
            }
        }

        void Compose(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int i = y * Size + x;
                float w = _white[i], a = Mathf.Max(w, _outline[i]);
                byte c = (byte)Mathf.RoundToInt(a > 0f ? 255f * w / a : 0f);
                _px[i] = new Color32(c, c, c, (byte)Mathf.RoundToInt(255f * a));
            }
        }
    }
}
