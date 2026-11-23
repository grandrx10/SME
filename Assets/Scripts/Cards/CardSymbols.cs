using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The black line drawings in the middle of each card (cleaned-up versions of the design sketches),
    /// plus the cost icons. Textures are generated once and cached.
    /// </summary>
    public static class CardSymbols
    {
        public delegate void Draw(LineArt a);

        static readonly Dictionary<(CardId, int), Texture2D> Cache = new();
        static Texture2D _bubble, _heart;

        /// <summary>
        /// The card's symbol. The designer's own drawing (Resources/CardArt/&lt;CardId&gt;.png — the sketch from the
        /// design doc, cleaned up with thicker, smoothed strokes) if there is one; otherwise a vector drawing.
        /// </summary>
        public static Texture2D Get(CardId id, int size = 256)
        {
            if (Cache.TryGetValue((id, size), out var t) && t != null) return t;
            var drawn = Resources.Load<Texture2D>("CardArt/" + id);
            if (drawn != null)
            {
                Cache[(id, size)] = drawn;
                return drawn;
            }
            var info = Cards.Get(id);
            var art = new LineArt();
            info?.symbol?.Invoke(art);
            t = art.Render(size, Color.black);
            t.name = "Symbol " + id;
            Cache[(id, size)] = t;
            return t;
        }

        static Vector2 V(float x, float y) => new(x, y);

        /// <summary>Wall rising out of the ground: slab with a rough top, an up arrow, cracked ground and lift lines.</summary>
        public static void SummonWall(LineArt a)
        {
            const float thin = 0.016f;
            // ground with the trench either side
            a.Poly(V(0.04f, 0.2f), V(0.17f, 0.2f), V(0.19f, 0.16f), V(0.22f, 0.16f));
            a.Poly(V(0.78f, 0.16f), V(0.81f, 0.16f), V(0.83f, 0.2f), V(0.96f, 0.2f));
            // the wall: straight sides, broken stone top
            a.Poly(V(0.22f, 0.16f), V(0.22f, 0.7f), V(0.29f, 0.73f), V(0.36f, 0.7f), V(0.44f, 0.74f), V(0.52f, 0.71f),
                V(0.6f, 0.75f), V(0.68f, 0.71f), V(0.78f, 0.73f), V(0.78f, 0.16f));
            a.Line(V(0.22f, 0.16f), V(0.78f, 0.16f));
            // a few stone joints near the edges
            a.Line(V(0.22f, 0.43f), V(0.33f, 0.43f), thin);
            a.Line(V(0.67f, 0.43f), V(0.78f, 0.43f), thin);
            a.Line(V(0.29f, 0.43f), V(0.29f, 0.58f), thin);
            a.Line(V(0.71f, 0.43f), V(0.71f, 0.58f), thin);
            a.Line(V(0.27f, 0.16f), V(0.27f, 0.29f), thin);
            a.Line(V(0.73f, 0.16f), V(0.73f, 0.29f), thin);
            // up arrow
            a.Line(V(0.5f, 0.27f), V(0.5f, 0.62f), 0.034f);
            a.Head(V(0.5f, 0.63f), Vector2.up, 0.1f, 0.034f);
            // lift lines
            a.Line(V(0.12f, 0.3f), V(0.12f, 0.46f), thin);
            a.Line(V(0.08f, 0.38f), V(0.08f, 0.6f), thin);
            a.Line(V(0.88f, 0.3f), V(0.88f, 0.46f), thin);
            a.Line(V(0.92f, 0.38f), V(0.92f, 0.6f), thin);
            // cracks under the ground
            a.Poly(thin, false, V(0.1f, 0.2f), V(0.12f, 0.13f), V(0.1f, 0.08f));
            a.Poly(thin, false, V(0.9f, 0.2f), V(0.88f, 0.12f), V(0.91f, 0.07f));
        }

        /// <summary>A rough boulder flying right, with speed lines behind it.</summary>
        public static void RockBarrage(LineArt a)
        {
            const float thin = 0.016f;
            a.Circle(V(0.42f, 0.5f), 0.2f, -1f, 48, 0.09f, 3);
            // cracks / facets
            a.Poly(thin, false, V(0.33f, 0.63f), V(0.4f, 0.55f), V(0.38f, 0.45f));
            a.Poly(thin, false, V(0.4f, 0.55f), V(0.5f, 0.57f));
            a.Poly(thin, false, V(0.47f, 0.37f), V(0.5f, 0.44f), V(0.57f, 0.46f));
            a.Dot(V(0.33f, 0.43f), 0.012f);
            a.Dot(V(0.52f, 0.62f), 0.01f);
            // arrow
            a.Line(V(0.68f, 0.5f), V(0.93f, 0.5f), 0.034f);
            a.Head(V(0.94f, 0.5f), Vector2.right, 0.09f, 0.034f);
            // speed lines
            a.Line(V(0.05f, 0.6f), V(0.17f, 0.6f), thin);
            a.Line(V(0.02f, 0.5f), V(0.16f, 0.5f), thin);
            a.Line(V(0.05f, 0.4f), V(0.17f, 0.4f), thin);
            // small chips trailing
            a.Closed(V(0.1f, 0.7f), V(0.13f, 0.72f), V(0.12f, 0.68f));
            a.Closed(V(0.11f, 0.29f), V(0.14f, 0.31f), V(0.135f, 0.27f));
        }

        /// <summary>Two slabs of ground swinging up and closing together in the middle.</summary>
        public static void FoldingHands(LineArt a)
        {
            const float thin = 0.016f;
            // flat slabs (where the ground was)
            a.Closed(V(0.04f, 0.14f), V(0.36f, 0.14f), V(0.36f, 0.23f), V(0.04f, 0.23f));
            a.Closed(V(0.64f, 0.14f), V(0.96f, 0.14f), V(0.96f, 0.23f), V(0.64f, 0.23f));
            a.Line(V(0.12f, 0.14f), V(0.12f, 0.23f), thin);
            a.Line(V(0.88f, 0.14f), V(0.88f, 0.23f), thin);
            // closed slabs in the middle
            a.Closed(V(0.415f, 0.28f), V(0.49f, 0.28f), V(0.49f, 0.84f), V(0.415f, 0.82f));
            a.Closed(V(0.51f, 0.28f), V(0.585f, 0.28f), V(0.585f, 0.82f), V(0.51f, 0.84f));
            a.Line(V(0.415f, 0.5f), V(0.47f, 0.5f), thin);
            a.Line(V(0.53f, 0.62f), V(0.585f, 0.62f), thin);
            // swing arrows
            a.Bezier(V(0.18f, 0.3f), V(0.16f, 0.62f), V(0.26f, 0.72f), V(0.37f, 0.66f));
            a.Head(V(0.38f, 0.655f), V(1f, -0.45f), 0.075f);
            a.Bezier(V(0.82f, 0.3f), V(0.84f, 0.62f), V(0.74f, 0.72f), V(0.63f, 0.66f));
            a.Head(V(0.62f, 0.655f), V(-1f, -0.45f), 0.075f);
            // impact ticks where they meet
            a.Line(V(0.5f, 0.88f), V(0.5f, 0.95f), thin);
            a.Line(V(0.44f, 0.87f), V(0.41f, 0.93f), thin);
            a.Line(V(0.56f, 0.87f), V(0.59f, 0.93f), thin);
        }

        static void Ellipse(LineArt a, Vector2 c, float rx, float ry, float from = 0f, float to = 360f, float w = -1f)
        {
            int steps = Mathf.Max(8, Mathf.RoundToInt(Mathf.Abs(to - from) / 8f));
            Vector2 prev = default;
            for (int i = 0; i <= steps; i++)
            {
                float t = Mathf.Lerp(from, to, i / (float)steps) * Mathf.Deg2Rad;
                var p = c + new Vector2(Mathf.Cos(t) * rx, Mathf.Sin(t) * ry);
                if (i > 0) a.Line(prev, p, w);
                prev = p;
            }
        }

        /// <summary>A gust: a long streak of wind curling into a swirl, with two lighter streaks.</summary>
        public static void Shove(LineArt a)
        {
            const float thin = 0.016f;
            a.Bezier(V(0.06f, 0.3f), V(0.3f, 0.27f), V(0.55f, 0.29f), V(0.7f, 0.34f));
            a.Bezier(V(0.7f, 0.34f), V(0.9f, 0.42f), V(0.88f, 0.74f), V(0.68f, 0.75f));
            a.Bezier(V(0.68f, 0.75f), V(0.52f, 0.76f), V(0.52f, 0.54f), V(0.62f, 0.53f));
            a.Bezier(V(0.62f, 0.53f), V(0.7f, 0.52f), V(0.72f, 0.58f), V(0.68f, 0.62f));
            a.Bezier(V(0.1f, 0.52f), V(0.22f, 0.5f), V(0.34f, 0.5f), V(0.44f, 0.53f), thin);
            a.Bezier(V(0.16f, 0.16f), V(0.3f, 0.14f), V(0.44f, 0.15f), V(0.56f, 0.19f), thin);
            a.Line(V(0.2f, 0.4f), V(0.36f, 0.4f), thin);
        }

        /// <summary>A funnel of wind loops narrowing to a point, the top one carrying on as an arrow.</summary>
        public static void Tornado(LineArt a)
        {
            const float thin = 0.018f;
            Ellipse(a, V(0.4f, 0.8f), 0.27f, 0.07f, 20f, 340f);
            a.Line(V(0.65f, 0.83f), V(0.92f, 0.83f));
            a.Head(V(0.93f, 0.83f), Vector2.right, 0.07f);
            Ellipse(a, V(0.43f, 0.64f), 0.21f, 0.06f, 0f, 360f);
            Ellipse(a, V(0.4f, 0.49f), 0.15f, 0.05f, 0f, 360f);
            Ellipse(a, V(0.43f, 0.36f), 0.09f, 0.04f, 0f, 360f);
            a.Bezier(V(0.43f, 0.32f), V(0.44f, 0.24f), V(0.36f, 0.2f), V(0.33f, 0.1f));
            // a couple of bits of rubble caught in it
            a.Closed(V(0.76f, 0.6f), V(0.8f, 0.63f), V(0.79f, 0.57f));
            a.Closed(V(0.15f, 0.45f), V(0.19f, 0.47f), V(0.18f, 0.42f));
            a.Line(V(0.66f, 0.47f), V(0.74f, 0.46f), thin);
        }

        /// <summary>A figure mid-dash, leaning in, with speed lines behind.</summary>
        public static void NomadicDash(LineArt a)
        {
            const float thin = 0.018f;
            a.Circle(V(0.67f, 0.75f), 0.065f);
            a.Line(V(0.63f, 0.66f), V(0.53f, 0.45f));
            a.Poly(V(0.53f, 0.45f), V(0.41f, 0.37f), V(0.33f, 0.22f));
            a.Poly(V(0.53f, 0.45f), V(0.66f, 0.35f), V(0.64f, 0.19f));
            a.Poly(V(0.6f, 0.6f), V(0.48f, 0.58f), V(0.42f, 0.66f));
            a.Poly(V(0.6f, 0.6f), V(0.73f, 0.54f), V(0.82f, 0.59f));
            a.Line(V(0.06f, 0.68f), V(0.34f, 0.68f), thin);
            a.Line(V(0.12f, 0.52f), V(0.38f, 0.52f), thin);
            a.Line(V(0.05f, 0.36f), V(0.28f, 0.36f), thin);
            a.Bezier(V(0.18f, 0.24f), V(0.26f, 0.16f), V(0.4f, 0.12f), V(0.52f, 0.13f), thin);
        }

        /// <summary>An orb bursting into four curving missiles.</summary>
        public static void ManaMissiles(LineArt a)
        {
            a.Circle(V(0.24f, 0.24f), 0.07f);
            a.Bezier(V(0.22f, 0.33f), V(0.16f, 0.6f), V(0.24f, 0.84f), V(0.46f, 0.86f));
            a.Head(V(0.47f, 0.86f), V(1f, 0f), 0.065f);
            a.Bezier(V(0.29f, 0.32f), V(0.34f, 0.56f), V(0.46f, 0.7f), V(0.66f, 0.7f));
            a.Head(V(0.67f, 0.7f), V(1f, 0f), 0.065f);
            a.Bezier(V(0.33f, 0.28f), V(0.48f, 0.42f), V(0.62f, 0.52f), V(0.8f, 0.5f));
            a.Head(V(0.81f, 0.5f), V(1f, -0.1f), 0.065f);
            a.Bezier(V(0.33f, 0.21f), V(0.5f, 0.24f), V(0.7f, 0.28f), V(0.82f, 0.18f));
            a.Head(V(0.83f, 0.17f), V(0.8f, -0.6f), 0.065f);
            a.Dot(V(0.24f, 0.24f), 0.02f);
        }

        /// <summary>A small figure inside two rings of shield.</summary>
        public static void MagicalBarrier(LineArt a)
        {
            const float thin = 0.018f;
            a.Circle(V(0.5f, 0.5f), 0.42f, -1f, 64, 0.03f, 5);
            // inner ring with a gap at the top, like the sketch
            Ellipse(a, V(0.5f, 0.5f), 0.31f, 0.31f, 110f, 430f);
            Ellipse(a, V(0.5f, 0.5f), 0.36f, 0.36f, 98f, 122f, thin);
            // the figure
            a.Circle(V(0.5f, 0.6f), 0.045f);
            a.Closed(V(0.46f, 0.36f), V(0.54f, 0.36f), V(0.55f, 0.52f), V(0.45f, 0.52f));
            // hex hints on the shield
            for (int i = 0; i < 6; i++)
            {
                float t = (i * 60f + 30f) * Mathf.Deg2Rad;
                var c = V(0.5f, 0.5f) + new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * 0.365f;
                a.Line(c - new Vector2(Mathf.Sin(t), -Mathf.Cos(t)) * 0.025f, c + new Vector2(Mathf.Sin(t), -Mathf.Cos(t)) * 0.025f, thin);
            }
        }

        /// <summary>A fist punching forward, trailing flame.</summary>
        public static void ManaSiphon(LineArt a)
        {
            const float thin = 0.018f;
            // fist
            a.Bezier(V(0.3f, 0.6f), V(0.28f, 0.84f), V(0.5f, 0.86f), V(0.64f, 0.84f));
            a.Bezier(V(0.64f, 0.84f), V(0.8f, 0.8f), V(0.84f, 0.66f), V(0.78f, 0.52f));
            a.Bezier(V(0.78f, 0.52f), V(0.72f, 0.4f), V(0.62f, 0.36f), V(0.52f, 0.37f));
            a.Bezier(V(0.52f, 0.37f), V(0.46f, 0.38f), V(0.42f, 0.42f), V(0.38f, 0.48f));
            // knuckles / fingers
            a.Poly(V(0.5f, 0.66f), V(0.55f, 0.69f), V(0.6f, 0.64f), V(0.64f, 0.58f), V(0.58f, 0.48f), V(0.52f, 0.46f));
            a.Bezier(V(0.56f, 0.66f), V(0.59f, 0.64f), V(0.6f, 0.62f), V(0.6f, 0.6f), thin);
            a.Bezier(V(0.58f, 0.6f), V(0.61f, 0.58f), V(0.62f, 0.56f), V(0.62f, 0.54f), thin);
            a.Bezier(V(0.57f, 0.54f), V(0.6f, 0.52f), V(0.6f, 0.5f), V(0.6f, 0.48f), thin);
            // cuff
            a.Poly(V(0.3f, 0.6f), V(0.2f, 0.58f), V(0.23f, 0.54f), V(0.32f, 0.53f));
            // flames trailing back
            a.Poly(V(0.24f, 0.53f), V(0.12f, 0.4f), V(0.2f, 0.42f), V(0.16f, 0.26f), V(0.27f, 0.36f), V(0.28f, 0.2f),
                V(0.34f, 0.32f), V(0.38f, 0.2f), V(0.39f, 0.32f), V(0.45f, 0.38f));
        }

        /// <summary>Mana cost: a bubble (circle with a little highlight).</summary>
        public static Texture2D Bubble
        {
            get
            {
                if (_bubble != null) return _bubble;
                var a = new LineArt { Width = 0.06f };
                a.Circle(V(0.5f, 0.5f), 0.42f);
                // highlight arc
                Vector2 prev = default;
                for (int i = 0; i <= 10; i++)
                {
                    float t = Mathf.Lerp(1.95f, 2.75f, i / 10f);
                    var p = V(0.5f, 0.5f) + new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * 0.28f;
                    if (i > 0) a.Line(prev, p, 0.045f);
                    prev = p;
                }
                _bubble = a.Render(128, Color.black);
                // filled with mana blue (the HUD's mana colour), under the ink lines
                var fill = new Color(0.45f, 0.75f, 1f, 1f);
                var px = _bubble.GetPixels();
                int n = _bubble.width;
                float rIn = 0.42f * n;
                for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - n * 0.5f, dy = y + 0.5f - n * 0.5f;
                    if (dx * dx + dy * dy > rIn * rIn) continue;
                    int i = y * n + x;
                    var ink = px[i];
                    px[i] = new Color(Mathf.Lerp(fill.r, ink.r, ink.a), Mathf.Lerp(fill.g, ink.g, ink.a), Mathf.Lerp(fill.b, ink.b, ink.a), 1f);
                }
                _bubble.SetPixels(px);
                _bubble.Apply();
                return _bubble;
            }
        }

        /// <summary>Health cost: a heart outline.</summary>
        public static Texture2D Heart
        {
            get
            {
                if (_heart != null) return _heart;
                var a = new LineArt { Width = 0.06f };
                a.Bezier(V(0.5f, 0.12f), V(0.3f, 0.3f), V(0.06f, 0.48f), V(0.1f, 0.7f));
                a.Bezier(V(0.1f, 0.7f), V(0.14f, 0.9f), V(0.42f, 0.92f), V(0.5f, 0.72f));
                a.Bezier(V(0.5f, 0.72f), V(0.58f, 0.92f), V(0.86f, 0.9f), V(0.9f, 0.7f));
                a.Bezier(V(0.9f, 0.7f), V(0.94f, 0.48f), V(0.7f, 0.3f), V(0.5f, 0.12f));
                _heart = a.Render(128, Color.black);
                // filled red inside the outline (flooded out from the middle, stopping at the ink)
                var fill = new Color(0.9f, 0.16f, 0.14f, 1f);
                var px = _heart.GetPixels();
                int n = _heart.width;
                var seen = new bool[px.Length];
                var open = new Stack<int>();
                open.Push((int)(n * 0.5f) * n + n / 2);
                while (open.Count > 0)
                {
                    int i = open.Pop();
                    if (i < 0 || i >= px.Length || seen[i] || px[i].a > 0.5f) continue;
                    seen[i] = true;
                    int x = i % n;
                    if (x > 0) open.Push(i - 1);
                    if (x < n - 1) open.Push(i + 1);
                    open.Push(i - n);
                    open.Push(i + n);
                }
                for (int i = 0; i < px.Length; i++)
                {
                    var ink = px[i];
                    // inside, or the soft inner edge of the line: red under the ink
                    bool inside = seen[i] || ink.a > 0f && ink.a <= 0.5f && (i % n > 0 && seen[i - 1] || i % n < n - 1 && seen[i + 1] || i >= n && seen[i - n] || i + n < px.Length && seen[i + n]);
                    if (!inside) continue;
                    px[i] = new Color(Mathf.Lerp(fill.r, ink.r, ink.a), Mathf.Lerp(fill.g, ink.g, ink.a), Mathf.Lerp(fill.b, ink.b, ink.a), 1f);
                }
                _heart.SetPixels(px);
                _heart.Apply();
                return _heart;
            }
        }

        public static Texture2D CostIcon(CostType t) => t == CostType.Health ? Heart : Bubble;
    }
}
