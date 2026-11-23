using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>
    /// A blacked-out card in the deck (The World, once you own every other Tarot card). It trembles; the pointer coming onto
    /// it sets it shaking harder, black runes spilling off it, and holding there a moment (or clicking) finds it.
    /// </summary>
    public class SecretCard : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        const float HoverTime = 0.55f;
        CardId _card;
        RectTransform _stage, _view, _runeLayer;
        RuneSparks _runes;
        bool _over, _found;
        float _held, _owed;

        public static void Add(RectTransform cell, float width, RectTransform stage, CardId card)
        {
            var layer = UIFactory.Stretch(UIFactory.Rect("Runes", cell));
            var view = CardView.BuildBlackedOut(cell, width);
            UIFactory.Stretch(view);
            var hit = cell.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0); // catches the pointer
            var s = cell.gameObject.AddComponent<SecretCard>();
            s._card = card;
            s._stage = stage;
            s._view = view;
            s._runeLayer = layer;
            s._runes = new RuneSparks(layer);
        }

        public void OnPointerEnter(PointerEventData e) => _over = true;
        public void OnPointerExit(PointerEventData e) => _over = false;
        public void OnPointerClick(PointerEventData e) { if (!e.dragging) Find(); }
        void OnDisable() => _over = false;

        void Update()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            _held = Mathf.MoveTowards(_held, _over && !DragCard.Dragging ? HoverTime : 0f, dt);
            float k = _held / HoverTime;
            _view.anchoredPosition = Random.insideUnitCircle * (0.6f + 7f * k * k);
            _view.localRotation = Quaternion.Euler(0f, 0f, Random.Range(-1f, 1f) * (0.3f + 3f * k));
            _view.localScale = Vector3.one * (1f + 0.08f * k);
            // the odd black rune at its edge; a stream of them once it's disturbed
            _owed += dt * (1.2f + 30f * k);
            while (_owed >= 1f)
            {
                _owed -= 1f;
                Vector2 dir = Random.insideUnitCircle.normalized, half = _runeLayer.rect.size * 0.46f;
                _runes.Spit(new Vector2(dir.x * half.x, dir.y * half.y), dir * Random.Range(90f, 180f + 260f * k), Random.Range(20f, 30f), SecretReveal.Rune(), Random.Range(0.5f, 0.8f));
            }
            _runes.Update(dt);
            if (_held >= HoverTime) Find();
        }

        void Find()
        {
            if (_found || SecretReveal.Playing) return;
            _found = true;
            SecretReveal.Play(_stage, (RectTransform)transform, _card);
        }
    }

    /// <summary>
    /// A secret card found: it's torn out of the deck to the middle of a mahogany-dark screen, shaking harder and harder as
    /// black runes rush into it and the whole screen rattles; it's crushed, freezes a beat, and bursts open face up in
    /// rings of black runes with a huge kick. Click to go back. The card is yours the moment it starts.
    /// </summary>
    public class SecretReveal : MonoBehaviour
    {
        const float Width = 340f, FlyTime = 0.45f, ShakeTime = 2.2f, Squash = 0.2f;
        static SecretReveal _live;
        /// <summary>A reveal is on (or still showing its card).</summary>
        public static bool Playing => _live != null;

        /// <summary>The black runes: black, with now and then a mahogany one among them.</summary>
        public static Color Rune() => Random.value < 0.82f ? Color.black : new Color(0.42f, 0.13f, 0.07f);

        static readonly Color Backdrop = new(0.5f, 0.2f, 0.12f, 1f), Pale = new(0.98f, 0.9f, 0.84f, 1f);

        CardId _id;
        RectTransform _root, _menu, _shake, _card, _circle, _fx, _flash, _black;
        Image _dim, _circleImg;
        RuneSparks _runes;
        Vector2 _from;
        float _fromScale, _t, _hitstop, _kick, _owed, _flashT = -1f, _burstT = -1f, _spin, _closeT = -1f;
        bool _revealed;
        Vector2 _menuPos;
        Quaternion _menuRot;

        public static void Play(RectTransform menu, RectTransform source, CardId id)
        {
            if (_live != null) return;
            var dim = UIFactory.Image("SecretReveal", menu, new Color(Backdrop.r, Backdrop.g, Backdrop.b, 0f));
            var root = UIFactory.Stretch(dim.rectTransform);
            root.SetAsLastSibling();
            var sub = dim.gameObject.AddComponent<Canvas>();
            sub.overrideSorting = true;
            sub.sortingOrder = 45; // over the loadout screen and anything held
            dim.gameObject.AddComponent<GraphicRaycaster>();
            var r = dim.gameObject.AddComponent<SecretReveal>();
            _live = r;
            r._id = id;
            r._dim = dim;
            r._root = root;
            r._menu = menu;
            r._menuPos = menu.anchoredPosition;
            r._menuRot = menu.localRotation;
            dim.gameObject.AddComponent<ShopClick>().onClick = Dismiss;
            r.Build(source);
            // yours from now on, whatever happens to the show
            Collection.Add(new[] { id });
        }

        /// <summary>A click (or back): once the card is up, it goes home to the deck.</summary>
        public static void Dismiss()
        {
            if (_live != null && _live._revealed && _live._closeT < 0f && _live._t > 0.7f) _live._closeT = 0f;
        }

        /// <summary>The screen closed under it.</summary>
        public static void Stop()
        {
            if (_live != null) Destroy(_live.gameObject);
        }

        void Build(RectTransform source)
        {
            _shake = UIFactory.Stretch(UIFactory.Rect("Shake", _root));
            _circleImg = UIFactory.Image("Circle", _shake, Color.black, ShopArt.RuneCircleSprite);
            _circleImg.raycastTarget = false;
            _circle = UIFactory.Place(_circleImg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(820, 820));
            _circle.localScale = Vector3.zero;

            _card = UIFactory.Rect("Card", _shake);
            UIFactory.Place(_card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(Width, Width * CardView.Aspect));
            _black = UIFactory.Stretch(CardView.BuildBlackedOut(_card, Width));
            // it starts where it lay in the deck
            Vector3 world = source.TransformPoint(source.rect.center);
            _from = _shake.InverseTransformPoint(world);
            _fromScale = Mathf.Max(0.1f, source.rect.width / Width);

            var flash = UIFactory.Image("Flash", _shake, Color.black, ShopArt.DiscSprite);
            flash.raycastTarget = false;
            _flash = UIFactory.Place(flash.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(160, 160));
            _flash.gameObject.SetActive(false);

            _fx = UIFactory.Stretch(UIFactory.Rect("Runes", _shake));
            _runes = new RuneSparks(_fx, 400);
        }

        void Update()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            // the whole screen rattles, menu and all (through the freeze-frame too)
            _kick = Mathf.MoveTowards(_kick, 0f, dt * 1.5f);
            float trauma = _kick * _kick;
            Vector2 off = Random.insideUnitCircle * (trauma * 70f);
            float twist = Random.Range(-1f, 1f) * trauma * 4f;
            _shake.anchoredPosition = off;
            _shake.localRotation = Quaternion.Euler(0f, 0f, twist);
            _menu.anchoredPosition = _menuPos - off * 0.5f;
            _menu.localRotation = _menuRot * Quaternion.Euler(0f, 0f, -twist * 0.5f);

            if (_hitstop > 0f) { _hitstop -= dt; return; }
            _t += dt;
            _spin += dt * (_revealed ? 20f : 30f + 500f * Mathf.Pow(Mathf.Clamp01(_t / (FlyTime + ShakeTime)), 2f));
            _circle.localRotation = Quaternion.Euler(0f, 0f, -_spin);

            if (_closeT >= 0f) Close(dt);
            else if (!_revealed) Wind(dt);
            else Shown(dt);

            if (_flashT >= 0f)
            {
                _flashT += dt;
                float k = _flashT / 0.4f;
                if (k >= 1f) { _flashT = -1f; _flash.gameObject.SetActive(false); }
                else _flash.localScale = Vector3.one * (k < 0.18f ? Mathf.Lerp(2f, 7f, RuneSparks.EaseOut(k / 0.18f)) : Mathf.Lerp(7f, 0f, RuneSparks.EaseIn((k - 0.18f) / 0.82f)));
            }
            _runes.Update(dt);
        }

        /// <summary>Out of the deck, then the winding up: shaking, black runes rushing in and spilling out, until it's crushed.</summary>
        void Wind(float dt)
        {
            float fly = RuneSparks.EaseOut(Mathf.Clamp01(_t / FlyTime));
            float k = Mathf.Clamp01((_t - FlyTime) / ShakeTime), amp = k * k;
            _dim.color = new Color(Backdrop.r, Backdrop.g, Backdrop.b, 0.97f * fly);
            Vector2 pos = Vector2.Lerp(_from, Vector2.zero, fly);
            float crush = Mathf.Clamp01((_t - FlyTime - ShakeTime) / Squash);
            if (crush <= 0f)
            {
                _card.anchoredPosition = pos + Random.insideUnitCircle * (2f + 26f * amp);
                _card.localRotation = Quaternion.Euler(0f, 0f, Random.Range(-1f, 1f) * (1f + 9f * amp) + (1f - fly) * 25f);
                _card.localScale = Vector3.one * (Mathf.Lerp(_fromScale, 1f, fly) * (1f + 0.12f * amp));
                _circle.localScale = Vector3.one * Mathf.Lerp(0f, Mathf.Lerp(1f, 0.62f, k), fly);
                _kick = Mathf.Max(_kick, 0.2f * fly + 0.75f * amp);
                _owed += dt * (10f + 95f * amp) * fly;
                while (_owed >= 1f)
                {
                    _owed -= 1f;
                    Vector2 dir = Random.insideUnitCircle.normalized;
                    if (Random.value < 0.55f)
                        // rushing in from all round
                        _runes.Spit(dir * Random.Range(640f, 900f), -dir * Random.Range(1500f, 2300f), Random.Range(38f, 66f), Rune(), Random.Range(0.45f, 0.6f));
                    else
                        _runes.Spit(new Vector2(dir.x * Width * 0.4f, dir.y * Width * 0.55f), dir * Random.Range(350f, 950f), Random.Range(34f, 58f), Rune(), Random.Range(0.45f, 0.75f));
                }
                return;
            }
            // crushed flat (as The World crushes), and still for a beat
            _card.anchoredPosition = Random.insideUnitCircle * 4f;
            _card.localRotation = Quaternion.identity;
            _card.localScale = new Vector3(1.12f + 0.35f * RuneSparks.EaseOut(crush), 1.12f - 0.95f * RuneSparks.EaseOut(crush), 1f);
            _circle.localScale = Vector3.one * Mathf.Lerp(0.62f, 0.35f, RuneSparks.EaseOut(crush));
            if (crush >= 1f) Burst();
        }

        void Burst()
        {
            _revealed = true;
            _t = 0f;
            _hitstop = 0.18f;
            _kick = 1.7f;
            Destroy(_black.gameObject);
            var face = UIFactory.Stretch(CardView.Build(_card, _id, Width));
            face.gameObject.AddComponent<CardHover>().card = _id;
            _card.SetAsLastSibling();
            _fx.SetAsLastSibling();
            _flash.SetAsLastSibling();
            _flash.gameObject.SetActive(true);
            _flash.localScale = Vector3.one * 2f;
            _flashT = 0f;
            _burstT = 0f;
            // two rings of black runes, and a storm of strays
            _runes.Ring(Vector2.zero, 20, 1900f, 92f, Color.black, 0.75f);
            _runes.Ring(Vector2.zero, 14, 1150f, 70f, new Color(0.42f, 0.13f, 0.07f), 0.8f);
            for (int i = 0; i < 60; i++)
            {
                Vector2 dir = Random.insideUnitCircle.normalized;
                _runes.Spit(dir * 80f, dir * Random.Range(400f, 1700f), Random.Range(36f, 74f), Rune(), Random.Range(0.6f, 1.1f));
            }
        }

        /// <summary>Face up: it slams in, the circle blows wide and settles behind it, and black runes keep writing themselves round its edge.</summary>
        void Shown(float dt)
        {
            _dim.color = new Color(Backdrop.r, Backdrop.g, Backdrop.b, 0.97f);
            float slam = Mathf.Clamp01(_t / 0.3f);
            _card.anchoredPosition = new Vector2(0f, Mathf.Sin(_t * 1.6f) * 8f);
            _card.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(_t * 1.1f) * 1.5f);
            _card.localScale = new Vector3(Mathf.Lerp(1.5f, 1.15f, slam), Mathf.Lerp(0.2f, 1.15f, RuneSparks.EaseOutBack01(slam)), 1f);
            _burstT += dt;
            float b = Mathf.Clamp01(_burstT / 0.5f);
            _circle.localScale = Vector3.one * (b < 0.25f ? Mathf.Lerp(0.35f, 1.7f, RuneSparks.EaseOut(b / 0.25f)) : Mathf.Lerp(1.7f, 1.2f, RuneSparks.EaseOut((b - 0.25f) / 0.75f)));
            _circleImg.color = Color.black;
            _owed += dt * 12f;
            while (_owed >= 1f)
            {
                _owed -= 1f;
                Vector2 dir = Random.insideUnitCircle.normalized;
                Vector2 at = new(Mathf.Clamp(dir.x * 1.6f, -1f, 1f) * Width * 0.66f, Mathf.Clamp(dir.y * 1.6f, -1f, 1f) * Width * CardView.Aspect * 0.64f);
                if (Random.value < 0.5f) _runes.Twinkle(at, Random.Range(34f, 54f), Rune());
                else _runes.Spit(at, (dir + Vector2.up * 0.5f).normalized * Random.Range(140f, 300f), Random.Range(28f, 44f), Rune(), Random.Range(0.8f, 1.2f));
            }
        }

        void Close(float dt)
        {
            _closeT += dt / 0.25f;
            float k = Mathf.Clamp01(_closeT);
            float s = (1f - k) * (1f - k);
            _card.localScale = Vector3.one * (1.15f * s);
            _circle.localScale = Vector3.one * (1.2f * s);
            _dim.color = new Color(Backdrop.r, Backdrop.g, Backdrop.b, 0.97f * (1f - k));
            if (k >= 1f) Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (_live == this) _live = null;
            if (_menu != null) { _menu.anchoredPosition = _menuPos; _menu.localRotation = _menuRot; }
        }
    }
}
