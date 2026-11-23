using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Arcane runes flying about a UI layer (the shop's pack-opening runes, the main menu's): spat out (fly, slow hard,
    /// shrink to nothing) or written in place (pop up, hold, shrink away). Never fade. Uses <see cref="ShopArt.Glyph"/>.
    /// Positions are anchored positions in the layer (centre anchored).
    /// </summary>
    public class RuneSparks
    {
        public static readonly Color Arcane = new(0.28f, 0.45f, 1f);
        public static readonly Color ArcaneLight = new(0.62f, 0.78f, 1f);
        public static readonly Color ArcaneCore = new(0.88f, 0.94f, 1f);

        class Spark
        {
            public RectTransform rt;
            public Vector2 pos, vel;
            public float age, life, spin, drag;
            public bool twinkle; // grows then shrinks in place (instead of flying out and shrinking)
        }

        readonly RectTransform _layer;
        readonly List<Spark> _sparks = new();
        readonly int _max;
        int _glyph;

        public RuneSparks(RectTransform layer, int max = 300)
        {
            _layer = layer;
            _max = max;
        }

        public RectTransform Layer => _layer;

        /// <summary>A light, mid or core arcane blue, at random.</summary>
        public static Color RandomColour()
        {
            float r = Random.value;
            return r < 0.45f ? ArcaneLight : r < 0.75f ? Arcane : ArcaneCore;
        }

        /// <summary>A rune spat out: it flies, slows hard, and shrinks to nothing.</summary>
        public void Spit(Vector2 from, Vector2 vel, float size, Color c, float life) =>
            Add(from, vel, size, c, life, false, 4.5f);

        /// <summary>A rune written in place: it pops up, holds, and shrinks away.</summary>
        public void Twinkle(Vector2 at, float size, Color c) =>
            Add(at, new Vector2(0f, 30f), size, c, Random.Range(0.55f, 0.8f), true, 2f);

        /// <summary>A ring of runes blasting out evenly from <paramref name="at"/> (reads as a shockwave), plus a few strays.</summary>
        public void Ring(Vector2 at, int count, float speed, float size, Color c, float life, int strays = 0)
        {
            float spin = Random.value * Mathf.PI * 2f;
            for (int i = 0; i < count; i++)
            {
                float a = spin + i / (float)count * Mathf.PI * 2f;
                Spit(at, new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * speed, size, c, life);
            }
            for (int i = 0; i < strays; i++)
            {
                Vector2 dir = Random.insideUnitCircle.normalized;
                Spit(at + dir * 20f, dir * Random.Range(speed * 0.35f, speed * 0.8f), Random.Range(size * 0.6f, size), RandomColour(), Random.Range(life * 0.8f, life * 1.3f));
            }
        }

        void Add(Vector2 at, Vector2 vel, float size, Color c, float life, bool twinkle, float drag)
        {
            if (_sparks.Count > _max || _layer == null) return;
            var img = UIFactory.Image("Rune", _layer, c, ShopArt.Glyph(_glyph++));
            img.raycastTarget = false;
            var rt = UIFactory.Place(img.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), at, new Vector2(size, size));
            rt.localScale = twinkle ? Vector3.zero : Vector3.one * 0.6f;
            // runes stay mostly upright and turn slowly: they read as writing, not confetti
            rt.localRotation = Quaternion.Euler(0, 0, Random.Range(-25f, 25f));
            _sparks.Add(new Spark
            {
                rt = rt, pos = at, vel = vel, life = life, twinkle = twinkle, drag = drag,
                spin = Random.Range(-70f, 70f),
            });
        }

        public void Update(float dt)
        {
            for (int i = _sparks.Count - 1; i >= 0; i--)
            {
                var s = _sparks[i];
                s.age += dt;
                float k = s.age / s.life;
                if (k >= 1f || s.rt == null)
                {
                    if (s.rt != null) Object.Destroy(s.rt.gameObject);
                    _sparks.RemoveAt(i);
                    continue;
                }
                s.vel *= Mathf.Exp(-s.drag * dt);
                s.pos += s.vel * dt;
                s.rt.anchoredPosition = s.pos;
                s.rt.localRotation *= Quaternion.Euler(0, 0, s.spin * dt);
                // shrink, never fade
                float sc = s.twinkle
                    ? (k < 0.2f ? EaseOutBack01(k / 0.2f) : k < 0.6f ? 1f : 1f - EaseIn((k - 0.6f) / 0.4f))
                    : (k < 0.1f ? Mathf.Lerp(0.6f, 1.15f, k / 0.1f) : 1.15f * (1f - EaseIn((k - 0.1f) / 0.9f)));
                s.rt.localScale = Vector3.one * sc;
            }
        }

        public void Clear()
        {
            foreach (var s in _sparks) if (s.rt != null) Object.Destroy(s.rt.gameObject);
            _sparks.Clear();
        }

        public static float EaseOut(float k) => 1f - (1f - k) * (1f - k) * (1f - k);
        public static float EaseIn(float k) => k * k;

        public static float EaseOutBack01(float k)
        {
            const float c1 = 1.9f, c3 = c1 + 1f;
            float x = k - 1f;
            return 1f + c3 * x * x * x + c1 * x * x;
        }
    }
}
