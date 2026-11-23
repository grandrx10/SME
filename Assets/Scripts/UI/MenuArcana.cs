using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The main menu's arcane dressing, built from the shop's art (<see cref="ShopArt"/>, <see cref="RuneSparks"/>):
    /// rune circles turning behind the title and buttons, runes written on the air now and then, and a small burst
    /// when a button is pressed (a rune circle stamped behind it, a short ring of runes). Everything lives inside the
    /// menu, so a screen opening over it covers it; nothing lingers. Shrink, never fade.
    /// </summary>
    public class MenuArcana : MonoBehaviour
    {
        const float TitleCircleSize = 560f, BigCircleSize = 1150f;
        /// <summary>How long a pressed button's burst plays before its screen opens.</summary>
        const float PressDelay = 0.14f;
        const float BurstTime = 0.35f;
        Vector2 _titleAnchor;
        bool _hasTitle;
        float _clear = 280f; // half-width of the middle strip kept free of background runes

        RectTransform _menu, _back, _under, _over, _bigCircle, _titleCircle;
        RuneSparks _ambient, _sparks;
        float _t, _write, _titleWrite;
        readonly List<Burst> _live = new();
        Action _pending;
        float _pendingT;

        /// <summary>The title, bobbed gently. Set by the menu after it makes it.</summary>
        public RectTransform Title;

        class Burst
        {
            public RectTransform circle, button;
            public float t;
        }

        /// <summary>
        /// Call before building the panel's title and buttons, so the circles sit behind them. The big circle turns
        /// behind the buttons (<paramref name="buttonsAnchor"/>); with a <paramref name="titleAnchor"/>, a smaller one
        /// turns behind the title and runes are written around it.
        /// </summary>
        public static MenuArcana Create(RectTransform menu, Vector2 buttonsAnchor, Vector2? titleAnchor = null, float bigCircleSize = BigCircleSize, float clearHalfWidth = 280f)
        {
            var a = menu.gameObject.AddComponent<MenuArcana>();
            a._menu = menu;
            a._back = UIFactory.Stretch(UIFactory.Rect("Arcana", menu));
            a._bigCircle = Circle(a._back, "BigCircle", buttonsAnchor, bigCircleSize, new Color(RuneSparks.Arcane.r, RuneSparks.Arcane.g, RuneSparks.Arcane.b, 0.14f));
            a._hasTitle = titleAnchor.HasValue;
            if (a._hasTitle)
            {
                a._titleAnchor = titleAnchor.Value;
                a._titleCircle = Circle(a._back, "TitleCircle", a._titleAnchor, TitleCircleSize, new Color(RuneSparks.Arcane.r, RuneSparks.Arcane.g, RuneSparks.Arcane.b, 0.4f));
            }
            a._ambient = new RuneSparks(UIFactory.Stretch(UIFactory.Rect("Runes", a._back)), 80);
            a._under = UIFactory.Stretch(UIFactory.Rect("PressUnder", a._back)); // press circles: behind the buttons
            a._clear = clearHalfWidth;
            return a;
        }

        static RectTransform Circle(Transform parent, string name, Vector2 anchor, float size, Color c)
        {
            var img = UIFactory.Image(name, parent, c, ShopArt.RuneCircleSprite);
            img.raycastTarget = false;
            return UIFactory.Place(img.rectTransform, anchor, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
        }

        /// <summary>The layer over the buttons (made on first use, after the buttons exist) for the press runes.</summary>
        RectTransform Over()
        {
            if (_over == null)
            {
                _over = UIFactory.Stretch(UIFactory.Rect("PressOver", _menu));
                _sparks = new RuneSparks(_over, 60);
            }
            _over.SetAsLastSibling();
            return _over;
        }

        static Vector2 At(RectTransform layer, RectTransform r) => layer.InverseTransformPoint(r.TransformPoint(r.rect.center));

        /// <summary>
        /// A button was pressed: a rune circle stamps out behind it, a short ring of runes flicks off it, it punches,
        /// and <paramref name="then"/> runs a beat later (so the burst reads before the next screen covers it) — or at
        /// once with <paramref name="instant"/> (controls inside a panel, like tabs).
        /// </summary>
        public void Press(RectTransform button, Action then, bool instant = false)
        {
            if (_pending != null) return; // one press at a time
            var over = Over();
            Vector2 at = At(_under, button);
            float w = Mathf.Min(button.rect.width, 400f);

            var img = UIFactory.Image("Circle", _under, new Color(RuneSparks.ArcaneLight.r, RuneSparks.ArcaneLight.g, RuneSparks.ArcaneLight.b, 0.7f), ShopArt.RuneCircleSprite);
            img.raycastTarget = false;
            var circle = UIFactory.Place(img.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), at, new Vector2(w * 0.75f, w * 0.75f));
            circle.localScale = Vector3.zero;
            _live.Add(new Burst { circle = circle, button = button });

            _sparks.Ring(At(over, button), 8, 520f, 32f, RuneSparks.ArcaneLight, 0.32f);

            if (instant) { then?.Invoke(); return; }
            _pending = then;
            _pendingT = PressDelay;
        }

        /// <summary>
        /// Stamp press circles inside <paramref name="window"/> (behind its contents, over its background) instead of
        /// behind the whole panel — for a panel whose buttons sit on an opaque window.
        /// </summary>
        public void StampsIn(RectTransform window)
        {
            _under = UIFactory.Stretch(UIFactory.Rect("PressUnder", window));
            _under.SetAsFirstSibling();
        }

        /// <summary>The pointer came onto a button: a rune is written at its edge.</summary>
        public void Hover(RectTransform button)
        {
            var over = Over();
            Vector2 half = button.rect.size * 0.5f;
            _sparks.Twinkle(At(over, button) + EdgePoint(half.x + 16f, half.y + 12f), UnityEngine.Random.Range(24f, 32f), RuneSparks.ArcaneLight);
        }

        static Vector2 EdgePoint(float hw, float hh)
        {
            float u = UnityEngine.Random.value * 4f * (hw + hh);
            if (u < 2 * hw) return new Vector2(u - hw, UnityEngine.Random.value < 0.5f ? hh : -hh);
            u -= 2 * hw;
            return new Vector2(UnityEngine.Random.value < 0.5f ? hw : -hw, Mathf.Min(u, 4 * hh) * 0.5f - hh);
        }

        void OnDisable()
        {
            // the menu was hidden: drop the press effects so nothing is left half-played when it comes back
            foreach (var b in _live)
            {
                if (b.circle != null) Destroy(b.circle.gameObject);
                if (b.button != null) b.button.localScale = Vector3.one;
            }
            _live.Clear();
            _sparks?.Clear();
            var then = _pending;
            _pending = null;
            then?.Invoke();
        }

        void Update()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);

            if (_pending != null && (_pendingT -= dt) <= 0f)
            {
                var then = _pending;
                _pending = null;
                then();
            }
            _sparks?.Update(dt);
            UpdateBursts(dt);

            _t += dt;
            _bigCircle.localRotation = Quaternion.Euler(0, 0, _t * 4f);
            if (_titleCircle != null)
            {
                _titleCircle.localRotation = Quaternion.Euler(0, 0, -_t * 12f);
                _titleCircle.localScale = Vector3.one * (1f + Mathf.Sin(_t * 1.7f) * 0.025f);
            }
            if (Title != null)
            {
                Title.anchoredPosition = new Vector2(0f, Mathf.Sin(_t * 1.4f) * 7f);
                Title.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(_t * 0.9f) * 1.2f);
            }

            // now and then a rune is written on the air, away from the buttons
            Vector2 size = _back.rect.size;
            _write -= dt;
            if (_write <= 0f)
            {
                _write = UnityEngine.Random.Range(0.2f, 0.45f);
                for (int tries = 0; tries < 6; tries++)
                {
                    var p = new Vector2((UnityEngine.Random.value - 0.5f) * size.x * 0.94f, (UnityEngine.Random.value - 0.5f) * size.y * 0.9f);
                    if (Mathf.Abs(p.x) < _clear && (!_hasTitle || p.y < size.y * 0.32f)) continue; // the title and buttons
                    _ambient.Twinkle(p, UnityEngine.Random.Range(26f, 48f), UnityEngine.Random.value < 0.6f ? RuneSparks.Arcane : RuneSparks.ArcaneLight);
                    break;
                }
            }
            // and around the title's circle
            _titleWrite -= dt;
            if (_hasTitle && _titleWrite <= 0f)
            {
                _titleWrite = UnityEngine.Random.Range(0.5f, 0.9f);
                float a = UnityEngine.Random.value * Mathf.PI * 2f;
                Vector2 centre = new((_titleAnchor.x - 0.5f) * size.x, (_titleAnchor.y - 0.5f) * size.y);
                _ambient.Twinkle(centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (TitleCircleSize * 0.5f + 24f), UnityEngine.Random.Range(28f, 40f), RuneSparks.ArcaneLight);
            }
            _ambient.Update(dt);
        }

        void UpdateBursts(float dt)
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var b = _live[i];
                b.t += dt;
                // the circle stamps out from behind the button, turns a little, and shrinks back to nothing
                float k = b.t / BurstTime;
                b.circle.localScale = Vector3.one * (k >= 1f ? 0f : k < 0.25f ? RuneSparks.EaseOut(k / 0.25f) : 1f - RuneSparks.EaseIn((k - 0.25f) / 0.75f));
                b.circle.localRotation = Quaternion.Euler(0, 0, -RuneSparks.EaseOut(Mathf.Min(k, 1f)) * 70f);
                // the button gives a small punch
                if (b.button != null)
                {
                    float p = Mathf.Clamp01(b.t / 0.2f);
                    b.button.localScale = Vector3.one * (1f + Mathf.Sin(p * Mathf.PI) * 0.06f);
                }
                if (k >= 1f)
                {
                    Destroy(b.circle.gameObject);
                    if (b.button != null) b.button.localScale = Vector3.one;
                    _live.RemoveAt(i);
                }
            }
        }
    }
}
