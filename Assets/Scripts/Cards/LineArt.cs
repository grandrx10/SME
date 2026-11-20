using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Tiny vector line drawing: strokes in 0..1 space (x right, y up), rasterised with anti-aliasing into a
    /// texture of black (or any colour) lines on transparent. Used for card symbols and HUD icons.
    /// </summary>
    public class LineArt
    {
        struct Seg { public Vector2 a, b; public float w; }
        readonly List<Seg> _segs = new();
        readonly List<(Vector2 c, float r)> _dots = new();
        /// <summary>Default stroke width (fraction of the texture size).</summary>
        public float Width = 0.028f;

        public LineArt Line(Vector2 a, Vector2 b, float w = -1f)
        {
            _segs.Add(new Seg { a = a, b = b, w = w > 0 ? w : Width });
            return this;
        }

        public LineArt Line(float ax, float ay, float bx, float by, float w = -1f) => Line(new Vector2(ax, ay), new Vector2(bx, by), w);

        public LineArt Poly(float w, bool closed, params Vector2[] pts)
        {
            for (int i = 0; i + 1 < pts.Length; i++) Line(pts[i], pts[i + 1], w);
            if (closed && pts.Length > 2) Line(pts[^1], pts[0], w);
            return this;
        }

        public LineArt Poly(params Vector2[] pts) => Poly(-1f, false, pts);
        public LineArt Closed(params Vector2[] pts) => Poly(-1f, true, pts);

        public LineArt Bezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float w = -1f, int steps = 24)
        {
            Vector2 prev = p0;
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps, it = 1f - t;
                Vector2 p = it * it * it * p0 + 3f * it * it * t * p1 + 3f * it * t * t * p2 + t * t * t * p3;
                Line(prev, p, w);
                prev = p;
            }
            return this;
        }

        public LineArt Circle(Vector2 c, float r, float w = -1f, int steps = 40, float wobble = 0f, int seed = 0)
        {
            Vector2 prev = default;
            for (int i = 0; i <= steps; i++)
            {
                float a = i / (float)steps * Mathf.PI * 2f;
                float rr = r * (1f + wobble * (Mathf.Sin(a * 3f + seed) * 0.6f + Mathf.Sin(a * 7f + seed * 2.3f) * 0.4f));
                Vector2 p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr;
                if (i > 0) Line(prev, p, w);
                prev = p;
            }
            return this;
        }

        /// <summary>Arrowhead at <paramref name="tip"/> pointing along <paramref name="dir"/>.</summary>
        public LineArt Head(Vector2 tip, Vector2 dir, float size = 0.07f, float w = -1f)
        {
            dir.Normalize();
            Vector2 n = new(-dir.y, dir.x);
            Line(tip, tip - dir * size + n * size * 0.75f, w);
            Line(tip, tip - dir * size - n * size * 0.75f, w);
            return this;
        }

        public LineArt Dot(Vector2 c, float r)
        {
            _dots.Add((c, r));
            return this;
        }

        public Texture2D Render(int size, Color color)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            // Each stroke only touches the pixels around it (its bounding box), instead of testing every
            // pixel against every stroke — orders of magnitude cheaper for detailed symbols.
            var cover = new float[size * size];
            float aa = 1.2f / size;
            void Stamp(Vector2 lo, Vector2 hi, System.Func<Vector2, float> dist)
            {
                int x0 = Mathf.Max(0, Mathf.FloorToInt(lo.x * size) - 1), x1 = Mathf.Min(size - 1, Mathf.CeilToInt(hi.x * size) + 1);
                int y0 = Mathf.Max(0, Mathf.FloorToInt(lo.y * size) - 1), y1 = Mathf.Min(size - 1, Mathf.CeilToInt(hi.y * size) + 1);
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * size + x;
                    if (cover[i] >= 1f) continue;
                    float c = Mathf.Clamp01(0.5f - dist(new Vector2((x + 0.5f) / size, (y + 0.5f) / size)) / aa);
                    if (c > cover[i]) cover[i] = c;
                }
            }
            foreach (var s in _segs)
            {
                float pad = s.w * 0.5f + aa;
                var seg = s;
                Stamp(Vector2.Min(s.a, s.b) - Vector2.one * pad, Vector2.Max(s.a, s.b) + Vector2.one * pad,
                    p => DistToSeg(p, seg.a, seg.b) - seg.w * 0.5f);
            }
            foreach (var (c, r) in _dots)
            {
                var cc = c;
                float rr = r;
                Stamp(c - Vector2.one * (r + aa), c + Vector2.one * (r + aa), p => (p - cc).magnitude - rr);
            }
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(color.r, color.g, color.b, color.a * cover[i]);
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        static float DistToSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude > 1e-10f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (p - (a + ab * t)).magnitude;
        }
    }
}
