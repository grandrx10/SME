using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The shop: a card pack in the middle with its price (coin + 100) under it, your gold top right.
    /// Click the pack: a rune circle winds up under it while it shakes and spits runes, it squashes, freezes for a
    /// beat, then blows apart in a ring of runes with a big screen shake, and deals its cards face down. Each card
    /// writes the odd rune until you click it; it flips over and slams down on a rune circle in its element's colour.
    /// A card that came along with one (Remote Detonation) peeks out behind it. When all are up: ✓ to go back to the pack. The cards are added to your collection the
    /// moment the pack is bought.
    /// </summary>
    public partial class ShopMenu : MonoBehaviour
    {
        enum Phase { Idle, Shaking, Dealing, Revealing, Done, Opening, Reeling, Won }

        const float PackW = 256f, PackH = 360f;
        const float CardW = 220f, CardGap = 40f;
        const float ShakeTime = 1.25f, Squash = 0.16f, DealTime = 0.38f, DealStagger = 0.09f, FlipTime = 0.3f;
        const float CircleSize = 720f;
        static readonly Vector2 PackPos = new(0, 50);
        static readonly Color Arcane = RuneSparks.Arcane;
        static readonly Color ArcaneLight = RuneSparks.ArcaneLight;
        static readonly Color ArcaneCore = RuneSparks.ArcaneCore;

        public bool IsOpen { get; private set; }
        Action _onVisibilityChanged;

        RectTransform _root, _shakeRoot, _stage, _fx;
        RectTransform _pack, _price, _circle, _flash, _buttons;
        Image _circleImg, _flashImg;
        Text _priceText, _goldText;
        RectTransform _goldBox;
        float _goldPulse;

        Phase _phase;
        float _t, _idleT, _denyT, _shakeKick, _flashT = -1f, _circleBurstT = -1f, _hitstop, _circleSpin;
        bool _packHover;
        readonly List<Dealt> _dealt = new();
        RuneSparks _runes;

        class Dealt
        {
            public CardId id;
            public RectTransform slot, flip, back;
            public Vector2 target;
            public float startRot, delay, age, twinkle, flipT = -1f, burstT = -1f, punch;
            public float ringScale = 1f; // how big its reveal ring is (by rarity)
            public bool hover, faceUp, swapped, epic;
            public RectTransform circle;
            // a card that came along with it (Remote Detonation), tucked a little behind it: click either to bring it to the front
            public CardId with;
            public RectTransform extra;
            public bool extraFront, extraOnTop;
            public float extraT, swap; // slid out from behind; 0 this card in front, 1 the other
        }

        // ------------------------------------------------------------------ creation

        Action _openLoadout;
        bool _toLoadout; // the done button leads to the loadout screen (after your first pack)

        public static ShopMenu Create(Transform canvas, Action onVisibilityChanged, Action openLoadout)
        {
            var dim = UIFactory.Image("Shop", canvas, new Color(0.02f, 0.025f, 0.05f, 0.93f));
            var root = UIFactory.Stretch(dim.rectTransform);
            var sub = dim.gameObject.AddComponent<Canvas>();
            sub.overrideSorting = true;
            sub.sortingOrder = 30; // over the loadout screen
            dim.gameObject.AddComponent<GraphicRaycaster>();
            dim.gameObject.AddComponent<CardInspector>();
            var shop = dim.gameObject.AddComponent<ShopMenu>();
            shop._root = root;
            shop._onVisibilityChanged = onVisibilityChanged;
            shop._openLoadout = openLoadout;
            shop.Build();
            Wallet.Changed += shop.OnGold;
            root.gameObject.SetActive(false);
            return shop;
        }

        void OnDestroy() => Wallet.Changed -= OnGold;

        void Build()
        {
            _shakeRoot = UIFactory.Stretch(UIFactory.Rect("Shake", _root));
            _stage = UIFactory.Stretch(UIFactory.Rect("Stage", _shakeRoot));

            // a rune circle turning slowly behind the pack
            _circleImg = UIFactory.Image("RuneCircle", _stage, IdleCircleColour, ShopArt.RuneCircleSprite);
            _circleImg.raycastTarget = false;
            _circle = UIFactory.Place(_circleImg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), PackPos, new Vector2(CircleSize, CircleSize));

            // the pack: it's the button
            var pack = UIFactory.Rect("Pack", _stage);
            UIFactory.Place(pack, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), PackPos, new Vector2(PackW, PackH));
            var packImg = pack.gameObject.AddComponent<RawImage>();
            packImg.texture = ShopArt.Pack;
            var click = pack.gameObject.AddComponent<ShopClick>();
            click.onClick = ClickPack;
            click.onHover = h => _packHover = h;
            _pack = pack;
            BuildCrate();

            // price: coin + 100
            _price = UIFactory.Rect("Price", _stage);
            UIFactory.Place(_price, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), PackPos + new Vector2(0, -PackH / 2 - 48), new Vector2(200, 56));
            _priceText = CoinAmount(_price, Rewards.PackCost.ToString(), 44);

            // gold (top right, left of the close button)
            _goldBox = UIFactory.Rect("Gold", _root);
            UIFactory.Place(_goldBox, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-120, -30), new Vector2(240, 60));
            _goldText = CoinAmount(_goldBox, Wallet.Gold.ToString(), 44, true);

            var close = RoundButton(_root, "✕", Close, new Color(0.17f, 0.19f, 0.24f, 1f));
            UIFactory.Place((RectTransform)close.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-30, -30), new Vector2(64, 64));

            // the burst: an inked core of light that pops up and shrinks away
            _flashImg = UIFactory.Image("Flash", _shakeRoot, ArcaneCore, ShopArt.DiscSprite);
            _flashImg.raycastTarget = false;
            _flash = UIFactory.Place(_flashImg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), PackPos, new Vector2(128, 128));
            _flash.gameObject.SetActive(false);

            // flying runes on top of everything
            _fx = UIFactory.Stretch(UIFactory.Rect("Runes", _shakeRoot));
            _fx.SetAsLastSibling();
            _runes = new RuneSparks(_fx);
        }

        static Color IdleCircleColour => new(Arcane.r, Arcane.g, Arcane.b, 0.35f);

        /// <summary>A coin and a number, centred in <paramref name="parent"/> (or right-aligned).</summary>
        public static Text CoinAmount(RectTransform parent, string amount, int size, bool rightAlign = false)
        {
            var row = UIFactory.Rect("Row", parent);
            UIFactory.Stretch(row);
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.childAlignment = rightAlign ? TextAnchor.MiddleRight : TextAnchor.MiddleCenter;
            h.spacing = 10;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            var coin = UIFactory.Rect("Coin", row).gameObject.AddComponent<RawImage>();
            coin.texture = ShopArt.Coin;
            coin.raycastTarget = false;
            var le = coin.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = le.preferredHeight = size;
            var t = UIFactory.Text("Amount", row, amount, size, TextAnchor.MiddleLeft, new Color(1f, 0.86f, 0.4f), FontStyle.Bold);
            var o = t.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(0.05f, 0.03f, 0.08f, 1f);
            o.effectDistance = new Vector2(2.5f, -2.5f);
            return t;
        }

        static Button RoundButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick, Color colour)
        {
            var b = UIFactory.Button("Button", parent, label, onClick, 30);
            var img = (Image)b.targetGraphic;
            img.sprite = CardView.Rounded;
            img.type = Image.Type.Sliced;
            img.color = colour;
            return b;
        }

        // ------------------------------------------------------------------ open / close

        public void Open()
        {
            IsOpen = true;
            Wallet.ShopHint = false; // found the shop: the first-round nudge has done its job
            ResetToPack(false);
            OnGold();
            _root.gameObject.SetActive(true);
            _onVisibilityChanged?.Invoke();
        }

        public void Close() => Close(true);

        /// <summary>Closes without sending you on to the loadout screen (e.g. leaving the game).</summary>
        public void CloseQuietly() => Close(false);

        void Close(bool toLoadout)
        {
            if (!IsOpen) return;
            IsOpen = false;
            ResetToPack(false);
            _root.gameObject.SetActive(false);
            _onVisibilityChanged?.Invoke();
            // just opened your first pack: however you leave, you land on the loadout screen to try the new cards
            if (toLoadout && Collection.LoadoutHint) _openLoadout?.Invoke();
        }

        public void SetShown(bool shown) => _root.gameObject.SetActive(IsOpen && shown);

        void OnGold()
        {
            if (_goldText == null) return;
            _goldText.text = Wallet.Gold.ToString();
            _goldPulse = 1f;
        }

        void ResetToPack(bool popIn)
        {
            foreach (var d in _dealt) if (d.slot != null) Destroy(d.slot.gameObject);
            _dealt.Clear();
            _runes.Clear();
            if (_buttons != null) Destroy(_buttons.gameObject);
            _buttons = null;
            _phase = Phase.Idle;
            _t = 0f;
            _flashT = -1f;
            _circleBurstT = -1f;
            _hitstop = 0f;
            _flash.gameObject.SetActive(false);
            _shakeKick = 0f;
            _pack.gameObject.SetActive(true);
            _price.gameObject.SetActive(true);
            _circle.gameObject.SetActive(true);
            _pack.localScale = popIn ? Vector3.zero : Vector3.one;
            _circle.localScale = popIn ? Vector3.zero : Vector3.one;
            _circleImg.color = IdleCircleColour;
            ResetCrate(popIn);
        }

        // ------------------------------------------------------------------ input

        void ClickPack()
        {
            if (_phase != Phase.Idle || _slideT < 1f) return;
            if (_crateMode) BuyCrate();
            else BuyAndOpen();
        }

        void BuyAndOpen()
        {
            var cards = Packs.Open();
            if (cards == null) { _denyT = 0.45f; return; } // can't afford: the pack shakes no, the price flashes red
            if (_phase != Phase.Idle) ResetToPack(false);
            _phase = Phase.Shaking;
            _t = 0f;
            HideArrows();
            _price.gameObject.SetActive(false);
            for (int i = 0; i < cards.Length; i++) _dealt.Add(new Dealt { id = cards[i].id, with = cards[i].with });
        }

        void ClickCard(Dealt d)
        {
            if (d.faceUp && d.extra != null) { d.extraFront = false; return; }
            if (_phase != Phase.Revealing || d.faceUp || d.flipT >= 0f || d.age < d.delay + DealTime) return;
            d.flipT = 0f;
        }

        // ------------------------------------------------------------------ animation

        void Update()
        {
            float realDt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);

            // screen shake: runs through hitstop (that's when it hits hardest), rattles and twists the whole screen
            _shakeKick = Mathf.MoveTowards(_shakeKick, 0f, realDt * 2.2f);
            float trauma = _shakeKick * _shakeKick;
            _shakeRoot.anchoredPosition = UnityEngine.Random.insideUnitCircle * (trauma * 60f);
            _shakeRoot.localRotation = Quaternion.Euler(0, 0, UnityEngine.Random.Range(-1f, 1f) * trauma * 3.5f);

            // gold counter pulse
            _goldPulse = Mathf.MoveTowards(_goldPulse, 0f, realDt * 4f);
            _goldBox.localScale = Vector3.one * (1f + 0.3f * _goldPulse * _goldPulse);

            // hitstop: everything else freezes for a beat on the big hits
            if (_hitstop > 0f) { _hitstop -= realDt; return; }
            float dt = realDt;
            _t += dt;
            _idleT += dt;

            // the rune circle always turns; it winds up hard while the pack charges
            _circleSpin += dt * (_phase == Phase.Shaking ? 25f + 420f * Mathf.Pow(Mathf.Clamp01(_t / ShakeTime), 2f) : 10f);
            _circle.localRotation = Quaternion.Euler(0, 0, -_circleSpin);

            switch (_phase)
            {
                case Phase.Idle: UpdateIdle(dt); break;
                case Phase.Shaking: UpdateShaking(dt); break;
                case Phase.Opening: UpdateOpening(dt); break;
                case Phase.Reeling: UpdateReeling(dt); break;
                case Phase.Won: UpdateWon(dt); break;
                case Phase.Dealing:
                case Phase.Revealing:
                case Phase.Done: UpdateCards(dt); break;
            }

            // the burst core: pops big, then shrinks to nothing
            if (_flashT >= 0f)
            {
                _flashT += dt;
                float k = _flashT / 0.32f;
                if (k >= 1f) { _flashT = -1f; _flash.gameObject.SetActive(false); }
                else
                {
                    float s = k < 0.18f ? Mathf.Lerp(1.5f, 4.2f, EaseOut(k / 0.18f)) : Mathf.Lerp(4.2f, 0f, EaseIn((k - 0.18f) / 0.82f));
                    _flash.localScale = Vector3.one * s;
                }
            }

            // the pack's rune circle after the burst: blasts out, then shrinks to nothing
            if (_circleBurstT >= 0f)
            {
                _circleBurstT += dt;
                float k = _circleBurstT / 0.55f;
                if (k >= 1f) { _circleBurstT = -1f; _circle.gameObject.SetActive(false); }
                else _circle.localScale = Vector3.one * (k < 0.15f ? Mathf.Lerp(0.35f, 1.6f, EaseOut(k / 0.15f)) : 1.6f * (1f - EaseIn((k - 0.15f) / 0.85f)));
            }

            _runes.Update(dt);
        }

        void UpdateIdle(float dt)
        {
            // bob, sway, swell when hovered; shake "no" when you can't pay
            float want = _packHover ? 1.07f : 1f; // (also grows it back in after a pack's been opened)
            float ease = 1f - Mathf.Exp(-12f * dt);
            _pack.localScale = Vector3.one * Mathf.Lerp(_pack.localScale.x, want, ease);
            _circle.localScale = Vector3.one * Mathf.Lerp(_circle.localScale.x, _packHover ? 1.04f : 1f, ease * 0.6f);
            _denyT = Mathf.MoveTowards(_denyT, 0f, dt);
            float deny = _denyT > 0f ? Mathf.Sin(_denyT * 70f) * 16f * (_denyT / 0.45f) : 0f;
            _pack.anchoredPosition = PackPos + new Vector2(deny, Mathf.Sin(_idleT * 2f) * 8f);
            _pack.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(_idleT * 1.3f) * 3f);
            _priceText.color = _denyT > 0f ? new Color(1f, 0.25f, 0.2f) : (CanAfford ? new Color(1f, 0.86f, 0.4f) : new Color(0.6f, 0.55f, 0.5f));
            _price.localScale = Vector3.one * (1f + (_denyT > 0f ? 0.15f : 0f));
            UpdateIdleCrate(dt, deny);

            // now and then a rune is written on the air around the pack
            if (UnityEngine.Random.value < dt * 1.6f)
                Twinkle(PackPos + PerimeterPoint(PackW * 0.5f, PackH * 0.5f, 26f), UnityEngine.Random.Range(30f, 44f), ArcaneLight);
        }

        void UpdateShaking(float dt)
        {
            float k = Mathf.Clamp01(_t / ShakeTime);
            float amp = k * k;
            float charge = ShakeTime - Squash; // then a short squash before it blows
            if (_t < charge)
            {
                // trembles harder and harder; the rune circle tightens in around it and glows brighter
                _pack.anchoredPosition = PackPos + UnityEngine.Random.insideUnitCircle * (3f + 18f * amp);
                _pack.localRotation = Quaternion.Euler(0, 0, UnityEngine.Random.Range(-1f, 1f) * (1.5f + 8f * amp));
                _pack.localScale = Vector3.one * (1f + 0.12f * amp);
                _circle.localScale = Vector3.one * Mathf.Lerp(1f, 0.55f, EaseOut(k));
                _circleImg.color = Color.Lerp(IdleCircleColour, new Color(ArcaneLight.r, ArcaneLight.g, ArcaneLight.b, 0.9f), k);
                _shakeKick = Mathf.Max(_shakeKick, 0.45f * amp);

                // runes spat out of the pack, faster and faster
                int n = Mathf.FloorToInt(dt * (6f + 40f * amp) + UnityEngine.Random.value);
                for (int i = 0; i < n; i++)
                {
                    Vector2 dir = UnityEngine.Random.insideUnitCircle.normalized;
                    Vector2 from = PackPos + new Vector2(dir.x * PackW * 0.35f, dir.y * PackH * 0.35f);
                    Spit(from, dir * UnityEngine.Random.Range(380f, 820f), UnityEngine.Random.Range(30f, 48f), RuneColour(), UnityEngine.Random.Range(0.45f, 0.7f));
                }
            }
            else
            {
                // anticipation: crushes down and goes still, the circle snaps in tight
                float s = Mathf.Clamp01((_t - charge) / Squash);
                _pack.anchoredPosition = PackPos + UnityEngine.Random.insideUnitCircle * 3f;
                _pack.localRotation = Quaternion.identity;
                _pack.localScale = new Vector3(1.12f + 0.2f * EaseOut(s), 1.12f - 0.32f * EaseOut(s), 1f);
                _circle.localScale = Vector3.one * Mathf.Lerp(0.55f, 0.35f, EaseOut(s));
            }

            if (_t < ShakeTime) return;

            // BURST: a freeze-frame, then the pack's gone in a core of light, a ring of runes blasts out, the
            // circle blows wide and the screen kicks hard; the cards come out
            _hitstop = 0.11f;
            _shakeKick = 1.25f;
            _pack.gameObject.SetActive(false);
            _flash.gameObject.SetActive(true);
            _flash.anchoredPosition = PackPos;
            _flash.localScale = Vector3.one * 1.5f;
            _flashImg.color = ArcaneCore;
            _flashT = 0f;
            _circleBurstT = 0f;
            _circleImg.color = new Color(ArcaneLight.r, ArcaneLight.g, ArcaneLight.b, 0.95f);
            const int ring = 14;
            for (int i = 0; i < ring; i++)
            {
                // the ring: evenly spaced, all at one speed, so it reads as a shockwave
                float a = i / (float)ring * Mathf.PI * 2f;
                Spit(PackPos, new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 1500f, 64f, ArcaneCore, 0.6f);
            }
            for (int i = 0; i < 26; i++)
            {
                Vector2 dir = UnityEngine.Random.insideUnitCircle.normalized;
                Spit(PackPos, dir * UnityEngine.Random.Range(500f, 1250f), UnityEngine.Random.Range(36f, 62f), RuneColour(), UnityEngine.Random.Range(0.5f, 0.85f));
            }
            DealCards();
        }

        void DealCards()
        {
            _phase = Phase.Dealing;
            _t = 0f;
            float h = CardW * CardView.Aspect;
            float rowW = _dealt.Count * CardW + (_dealt.Count - 1) * CardGap;
            for (int i = 0; i < _dealt.Count; i++)
            {
                var d = _dealt[i];
                d.target = new Vector2(-rowW / 2 + CardW / 2 + i * (CardW + CardGap), 20);
                d.delay = 0.12f + i * DealStagger;
                d.startRot = UnityEngine.Random.Range(-40f, 40f);
                d.twinkle = UnityEngine.Random.value * 0.2f;

                d.slot = UIFactory.Rect("Card" + i, _stage);
                UIFactory.Place(d.slot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), PackPos, new Vector2(CardW, h));
                d.slot.localScale = Vector3.zero;

                // its own rune circle, stamped under it when it flips
                var circle = UIFactory.Image("RuneCircle", d.slot, Color.white, ShopArt.RuneCircleSprite);
                circle.raycastTarget = false;
                d.circle = UIFactory.Place(circle.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(CardW * 2.1f, CardW * 2.1f));
                d.circle.gameObject.SetActive(false);

                d.flip = UIFactory.Stretch(UIFactory.Rect("Flip", d.slot));
                var back = UIFactory.Rect("Back", d.flip).gameObject.AddComponent<RawImage>();
                back.texture = ShopArt.CardBack;
                d.back = UIFactory.Stretch(back.rectTransform);

                var click = d.slot.gameObject.AddComponent<ShopClick>();
                var hit = d.slot.gameObject.AddComponent<Image>();
                hit.color = new Color(0, 0, 0, 0);
                var dd = d;
                click.onClick = () => ClickCard(dd);
                click.onHover = on => dd.hover = on;
            }
            _fx.SetAsLastSibling();
        }

        void UpdateCards(float dt)
        {
            bool allArrived = true;
            int up = 0;
            foreach (var d in _dealt)
            {
                d.age += dt;
                float k = Mathf.Clamp01((d.age - d.delay) / DealTime);
                if (k < 1f) allArrived = false;

                // deal: flies from the pack to its place, spinning upright, growing in
                float e = EaseOutBack01(k);
                Vector2 pos = Vector2.LerpUnclamped(PackPos, d.target, EaseOut(k));
                float lift = 0f, hoverScale = 1f;
                if (k >= 1f && !d.faceUp && d.flipT < 0f)
                {
                    // face down, waiting: floats a little, lifts when hovered, writes the odd rune at its edge
                    pos.y += Mathf.Sin(_idleT * 2.2f + d.target.x * 0.01f) * 5f;
                    lift = d.hover ? 14f : 0f;
                    hoverScale = d.hover ? 1.06f : 1f;
                    d.twinkle -= dt;
                    if (d.twinkle <= 0f)
                    {
                        d.twinkle = UnityEngine.Random.Range(0.35f, 0.7f) * (d.hover ? 0.4f : 1f);
                        Twinkle(d.target + PerimeterPoint(CardW * 0.5f, CardW * CardView.Aspect * 0.5f, 22f), UnityEngine.Random.Range(26f, 40f),
                            UnityEngine.Random.value < 0.7f ? ArcaneLight : Arcane);
                    }
                }
                pos.y += lift;
                d.slot.anchoredPosition = pos;
                d.slot.localRotation = Quaternion.Euler(0, 0, Mathf.Lerp(d.startRot, 0f, EaseOut(k)) + (d.hover && !d.faceUp ? Mathf.Sin(_idleT * 9f) * 1.5f : 0f));
                d.punch = Mathf.MoveTowards(d.punch, 0f, dt * 3f);
                float scale = Mathf.Max(0f, e);
                d.slot.localScale = Vector3.one * (scale * hoverScale + d.punch * 0.18f);

                // flip: squash to a sliver, swap to the face, open back up — with a burst in the card's colour
                if (d.flipT >= 0f)
                {
                    d.flipT += dt;
                    float f = Mathf.Clamp01(d.flipT / FlipTime);
                    float sx = f < 0.5f ? Mathf.Cos(f * Mathf.PI) : -Mathf.Cos(f * Mathf.PI);
                    if (f >= 0.5f && !d.swapped) Reveal(d);
                    d.flip.localScale = new Vector3(Mathf.Max(0.001f, sx), 1f + Mathf.Sin(f * Mathf.PI) * 0.12f, 1f);
                    if (f >= 1f)
                    {
                        d.flipT = -1f;
                        d.faceUp = true;
                        d.flip.localScale = Vector3.one;
                        if (d.with != CardId.None) BuildExtra(d);
                    }
                }
                if (d.burstT >= 0f)
                {
                    // the rune circle slams out under the card, turns, and shrinks back to nothing
                    d.burstT += dt;
                    float b = d.burstT / 0.7f;
                    if (b >= 1f) { d.burstT = -1f; d.circle.gameObject.SetActive(false); }
                    else
                    {
                        d.circle.localScale = Vector3.one * d.ringScale * (b < 0.12f ? EaseOut(b / 0.12f) : 1f - EaseIn((b - 0.12f) / 0.88f));
                        d.circle.localRotation = Quaternion.Euler(0, 0, -EaseOut(b) * 150f);
                    }
                }
                // Epic cards keep writing purple arcane runes around their edge, some drifting off
                if (d.epic && d.swapped)
                {
                    d.twinkle -= dt;
                    if (d.twinkle <= 0f)
                    {
                        d.twinkle = UnityEngine.Random.Range(0.08f, 0.18f);
                        Color ec = Cards.RarityColor(Rarity.Epic);
                        Color glow = UnityEngine.Random.value < 0.5f ? Color.Lerp(ec, Color.white, 0.35f) : ec;
                        Vector2 at = d.target + PerimeterPoint(CardW * 0.5f, CardW * CardView.Aspect * 0.5f, 18f);
                        if (UnityEngine.Random.value < 0.55f) Twinkle(at, UnityEngine.Random.Range(24f, 38f), glow);
                        else
                        {
                            Vector2 dir = (at - d.target).normalized;
                            Spit(at, (dir + Vector2.up * 0.6f).normalized * UnityEngine.Random.Range(120f, 240f), UnityEngine.Random.Range(22f, 34f), glow, UnityEngine.Random.Range(0.7f, 1.1f));
                        }
                    }
                }
                if (d.extra != null) UpdateExtra(d, dt);
                if (d.faceUp) up++;
            }

            if (_phase == Phase.Dealing && allArrived) _phase = Phase.Revealing;
            if (_phase == Phase.Revealing && up == _dealt.Count)
            {
                _phase = Phase.Done;
                _t = 0f;
                BuildDoneButtons();
            }
            if (_phase == Phase.Done && _buttons != null)
            {
                float pulse = _toLoadout ? 1f + 0.08f * Mathf.Sin(_t * 6f) : 1f; // the way to the loadout throbs
                _buttons.localScale = Vector3.one * (EaseOutBack01(Mathf.Clamp01(_t / 0.35f)) * pulse);
            }
        }

        static readonly Vector2 ExtraOffset = new(30, 28);

        /// <summary>The card that came along slides out from behind the one just revealed.</summary>
        void BuildExtra(Dealt d)
        {
            d.extra = UIFactory.Rect("With", d.slot);
            UIFactory.Place(d.extra, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(CardW, CardW * CardView.Aspect));
            d.extra.SetSiblingIndex(d.flip.GetSiblingIndex()); // (just behind)
            var face = CardView.Build(d.extra, d.with, CardW);
            UIFactory.Stretch(face);
            face.gameObject.AddComponent<CardHover>().card = d.with; // Alt to look closer
            var click = d.extra.gameObject.AddComponent<ShopClick>();
            click.onClick = () => d.extraFront = true;
        }

        /// <summary>The two cards: one in front, the other peeking out behind it; they slide round each other to change places.</summary>
        void UpdateExtra(Dealt d, float dt)
        {
            d.extraT = Mathf.MoveTowards(d.extraT, 1f, dt / 0.3f);
            d.swap = Mathf.MoveTowards(d.swap, d.extraFront ? 1f : 0f, dt / 0.22f);
            float s = Mathf.SmoothStep(0f, 1f, d.swap);
            Vector2 apart = new(Mathf.Sin(s * Mathf.PI) * 70f, 0f);
            float shown = EaseOut(d.extraT);
            d.flip.anchoredPosition = ExtraOffset * s - apart;
            d.flip.localRotation = Quaternion.Euler(0f, 0f, -5f * s);
            d.extra.anchoredPosition = ExtraOffset * (shown * (1f - s)) + apart;
            d.extra.localRotation = Quaternion.Euler(0f, 0f, -5f * shown * (1f - s));
            bool top = d.swap > 0.5f;
            if (top == d.extraOnTop) return;
            d.extraOnTop = top;
            (top ? d.extra : d.flip).SetAsLastSibling();
        }

        void Reveal(Dealt d)
        {
            d.swapped = true;
            d.back.gameObject.SetActive(false);
            var face = CardView.Build(d.flip, d.id, CardW);
            UIFactory.Stretch(face);
            face.gameObject.AddComponent<CardHover>().card = d.id; // Alt to look closer

            var info = Cards.Get(d.id);
            Color c = info != null ? Cards.ElementColor(info.element) : Color.white;
            // the ring is the rarity's colour, and grows with it
            var rarity = info != null ? Cards.PackTier(info.rarity) : Rarity.Common;
            Color rc = Cards.RarityColor(rarity);
            Color bright = Color.Lerp(rc, Color.white, 0.3f);
            float big = rarity switch { Rarity.Epic => 1.9f, Rarity.Rare => 1.4f, _ => 1f };
            d.ringScale = big;
            d.circle.gameObject.SetActive(true);
            d.circle.GetComponent<Image>().color = new Color(bright.r, bright.g, bright.b, 0.9f);
            d.circle.localScale = Vector3.zero;
            d.burstT = 0f;
            d.punch = big;
            // rarer hits harder, but only a little more
            float kick = 1f + (big - 1f) * 0.4f;
            _hitstop = Mathf.Max(_hitstop, 0.06f * kick);
            _shakeKick = Mathf.Max(_shakeKick, 0.75f * kick);
            d.epic = rarity == Rarity.Epic;
            d.twinkle = 0.2f;
            // runes blown off the card: a ring in its rarity's colour (more, bigger, further the rarer it is), a few strays
            int ring = Mathf.RoundToInt(10 * big);
            float spin = UnityEngine.Random.value * Mathf.PI * 2f;
            for (int i = 0; i < ring; i++)
            {
                float a = spin + i / (float)ring * Mathf.PI * 2f;
                Spit(d.target, new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 1050f * big, 48f * Mathf.Sqrt(big), bright, 0.5f);
            }
            for (int i = 0; i < 8; i++)
            {
                Vector2 dir = UnityEngine.Random.insideUnitCircle.normalized;
                Spit(d.target + dir * 60f, dir * UnityEngine.Random.Range(350f, 800f), UnityEngine.Random.Range(28f, 44f),
                    i % 2 == 0 ? ArcaneCore : c, UnityEngine.Random.Range(0.4f, 0.65f));
            }
        }

        void BuildDoneButtons()
        {
            _buttons = UIFactory.Rect("Done", _stage);
            UIFactory.Place(_buttons, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -300), new Vector2(110, 110));
            _buttons.localScale = Vector3.zero;

            // after your first pack it's gold, throbs, and leads to the loadout screen;
            // otherwise → back to the pack: an inked green box with a cream arrow
            _toLoadout = Collection.LoadoutHint;
            Color face = _toLoadout ? new Color(0.95f, 0.66f, 0.12f, 1f) : new Color(0.3f, 0.74f, 0.42f, 1f);
            var ink = UIFactory.Image("Ink", _buttons, new Color(0.07f, 0.055f, 0.1f, 1f), CardView.Rounded);
            ink.type = Image.Type.Sliced;
            ink.raycastTarget = false;
            UIFactory.Place(ink.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(110, 110));
            var ok = RoundButton(_buttons, "", _toLoadout ? (UnityEngine.Events.UnityAction)Close : () => ResetToPack(true), face);
            UIFactory.Place((RectTransform)ok.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(100, 100));
            // keep the face its own colour (the default button tint brightens it and washes it out); a little lift on hover
            var cb = ok.colors;
            cb.colorMultiplier = 1f;
            cb.normalColor = cb.selectedColor = Color.white;
            cb.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            cb.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            ok.colors = cb;
            var arrow = UIFactory.Rect("Arrow", ok.transform).gameObject.AddComponent<RawImage>();
            arrow.texture = ShopArt.Arrow;
            arrow.color = new Color(1f, 0.95f, 0.82f, 1f);
            arrow.raycastTarget = false;
            UIFactory.Place(arrow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(2, -2), new Vector2(76, 76));
        }

        // ------------------------------------------------------------------ flying runes

        static Color RuneColour() => RuneSparks.RandomColour();

        void Spit(Vector2 from, Vector2 vel, float size, Color c, float life) => _runes.Spit(from, vel, size, c, life);

        void Twinkle(Vector2 at, float size, Color c) => _runes.Twinkle(at, size, c);

        static Vector2 PerimeterPoint(float hw, float hh, float outset)
        {
            float per = 4f * (hw + hh), u = UnityEngine.Random.value * per;
            hw += outset;
            hh += outset;
            if (u < 2 * hw) return new Vector2(u - hw, hh);
            u -= 2 * hw;
            if (u < 2 * hh) return new Vector2(hw, hh - u);
            u -= 2 * hh;
            if (u < 2 * hw) return new Vector2(hw - u, -hh);
            u -= 2 * hw;
            return new Vector2(-hw, -hh + Mathf.Min(u, 2 * hh));
        }

        static float EaseOut(float k) => 1f - (1f - k) * (1f - k) * (1f - k);
        static float EaseIn(float k) => k * k;

        static float EaseOutBack01(float k)
        {
            const float c1 = 1.9f, c3 = c1 + 1f;
            float x = k - 1f;
            return 1f + c3 * x * x * x + c1 * x * x;
        }
    }

    /// <summary>Click + hover callbacks for a UI element.</summary>
    public class ShopClick : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public Action onClick;
        public Action<bool> onHover;
        public void OnPointerClick(PointerEventData e) { if (e.button == PointerEventData.InputButton.Left) onClick?.Invoke(); }
        public void OnPointerEnter(PointerEventData e) => onHover?.Invoke(true);
        public void OnPointerExit(PointerEventData e) => onHover?.Invoke(false);
        void OnDisable() => onHover?.Invoke(false);
    }

    /// <summary>"+20" with a coin: pops up top right when you earn gold, then shrinks away.</summary>
    public class GoldPopup : MonoBehaviour
    {
        static readonly List<GoldPopup> Live = new();
        float _age;
        RectTransform _rt;
        int _index;

        public static void Show(Transform canvas, int amount)
        {
            var root = UIFactory.Rect("GoldPopup", canvas);
            var c = root.gameObject.AddComponent<Canvas>();
            c.overrideSorting = true;
            c.sortingOrder = 60;
            UIFactory.Place(root, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-40, 120), new Vector2(240, 64));
            var row = root.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.childAlignment = TextAnchor.MiddleRight;
            row.spacing = 10;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = row.childForceExpandHeight = false;
            var t = UIFactory.Text("Amount", root, "+" + amount, 46, TextAnchor.MiddleRight, new Color(1f, 0.86f, 0.4f), FontStyle.Bold);
            var o = t.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(0.05f, 0.03f, 0.08f, 1f);
            o.effectDistance = new Vector2(3f, -3f);
            var coin = UIFactory.Rect("Coin", root).gameObject.AddComponent<RawImage>();
            coin.texture = ShopArt.Coin;
            coin.raycastTarget = false;
            var le = coin.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = le.preferredHeight = 50;
            var p = root.gameObject.AddComponent<GoldPopup>();
            p._rt = root;
            root.pivot = new Vector2(1f, 0.5f);
            root.localScale = Vector3.zero;
            // stack under the ones already showing
            p._index = Live.Count;
            Live.Add(p);
        }

        void OnDestroy() => Live.Remove(this);

        void Update()
        {
            _age += Time.unscaledDeltaTime;
            const float Life = 1.8f;
            float s;
            if (_age < 0.25f) s = 1f + Mathf.Sin(_age / 0.25f * Mathf.PI) * 0.35f - (1f - _age / 0.25f);
            else if (_age < Life - 0.25f) s = 1f;
            else s = Mathf.Max(0f, 1f - (_age - (Life - 0.25f)) / 0.25f);
            _rt.localScale = Vector3.one * Mathf.Max(0f, s);
            int idx = Live.IndexOf(this);
            _rt.anchoredPosition = Vector2.Lerp(_rt.anchoredPosition, new Vector2(-40, 120 - idx * 66 + _age * 10f), 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime));
            if (_age >= Life) Destroy(gameObject);
        }
    }

    /// <summary>
    /// The shop's drawings, made in code: the pack, the card back, a coin, the arcane runes (the same shapes the
    /// spells write, <see cref="RuneGlyphs"/>), a rune circle and a core of light. Cartoony, flat colours, every
    /// shape inked with a dark outline. The white ones are tinted (the ink stays dark).
    /// </summary>
    public static partial class ShopArt
    {
        static readonly Color32 Ink = new(18, 14, 26, 255);
        static readonly Color32 White = new(255, 255, 255, 255);
        static Texture2D _pack, _back, _coin, _target, _arrow;
        static Sprite _circleSprite, _discSprite;
        static readonly Sprite[] _glyphSprites = new Sprite[GlyphCount];
        const int GlyphCount = 4;

        public static Texture2D Pack => _pack != null ? _pack : _pack = MakePack();
        public static Texture2D CardBack => _back != null ? _back : _back = MakeBack();
        public static Texture2D Coin => _coin != null ? _coin : _coin = MakeCoin();
        public static Texture2D Target => _target != null ? _target : _target = MakeTarget();
        /// <summary>A white arrow pointing right, inked (tint it).</summary>
        public static Texture2D Arrow => _arrow != null ? _arrow : _arrow = MakeArrow();
        public static Sprite RuneCircleSprite => _circleSprite != null ? _circleSprite : _circleSprite = ToSprite(MakeRuneCircle());
        public static Sprite DiscSprite => _discSprite != null ? _discSprite : _discSprite = ToSprite(MakeDisc());

        /// <summary>One of the arcane runes, white with an ink outline (cycles through them by index).</summary>
        public static Sprite Glyph(int index)
        {
            int i = (index % GlyphCount + GlyphCount) % GlyphCount;
            return _glyphSprites[i] != null ? _glyphSprites[i] : _glyphSprites[i] = ToSprite(MakeGlyph(i));
        }

        static Sprite ToSprite(Texture2D t) => Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);

        // ---- runes as strokes

        static Vector2[][][] _runeLines;

        static Vector2[][] RuneLines(int i)
        {
            if (_runeLines == null)
            {
                _runeLines = new Vector2[GlyphCount][][];
                for (int g = 0; g < GlyphCount; g++)
                {
                    var lines = RuneGlyphs.Rune(g);
                    _runeLines[g] = new Vector2[lines.Count][];
                    for (int l = 0; l < lines.Count; l++) _runeLines[g][l] = lines[l].ToArray();
                }
            }
            return _runeLines[(i % GlyphCount + GlyphCount) % GlyphCount];
        }

        static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            return (p - (a + ab * t)).magnitude;
        }

        /// <summary>Is (x, y) on rune <paramref name="index"/> drawn at (cx, cy), <paramref name="size"/> px from centre to edge, turned <paramref name="deg"/>, stroke half-width <paramref name="halfW"/> px?</summary>
        static bool Rune(float x, float y, float cx, float cy, float size, float deg, int index, float halfW)
        {
            float dx = x - cx, dy = y - cy, reach = size * 1.42f + halfW;
            if (dx * dx + dy * dy > reach * reach) return false;
            float a = -deg * Mathf.Deg2Rad, cs = Mathf.Cos(a), sn = Mathf.Sin(a);
            var p = new Vector2(dx * cs - dy * sn, dx * sn + dy * cs) / size;
            float w = halfW / size;
            foreach (var line in RuneLines(index))
                for (int k = 1; k < line.Length; k++)
                    if (SegDist(p, line[k - 1], line[k]) <= w) return true;
            return false;
        }

        // ---- shapes (x, y in pixels from the bottom left)

        static bool RoundBox(float x, float y, float x0, float y0, float x1, float y1, float r)
        {
            float cx = Mathf.Clamp(x, x0 + r, x1 - r), cy = Mathf.Clamp(y, y0 + r, y1 - r);
            return x >= x0 && x <= x1 && y >= y0 && y <= y1 && (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;
        }

        /// <summary>Four-pointed sparkle (an astroid-ish star) of radius r.</summary>
        static bool Star(float x, float y, float cx, float cy, float r)
        {
            float dx = Mathf.Abs(x - cx) / r, dy = Mathf.Abs(y - cy) / r;
            return Mathf.Sqrt(dx) + Mathf.Sqrt(dy) <= 1f;
        }

        static bool Disc(float x, float y, float cx, float cy, float r) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;

        static float Tri(float t) => Mathf.Abs(t - Mathf.Floor(t) - 0.5f) * 2f; // 1 .. 0 .. 1

        /// <summary>
        /// Paints one shape: <paramref name="fill"/> inside it and an ink outline <paramref name="ink"/> px wide around
        /// it (a chamfer distance transform of the shape's mask), over whatever is already drawn.
        /// </summary>
        static void Layer(Color32[] px, int w, int h, Func<float, float, bool> inside, Func<int, int, Color32> fill, float ink)
        {
            const float Far = 1e6f, Diag = 1.4142f;
            var d = new float[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                d[y * w + x] = inside(x + 0.5f, y + 0.5f) ? 0f : Far;
            if (ink > 0f)
            {
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float v = d[i];
                    if (x > 0) v = Mathf.Min(v, d[i - 1] + 1f);
                    if (y > 0)
                    {
                        v = Mathf.Min(v, d[i - w] + 1f);
                        if (x > 0) v = Mathf.Min(v, d[i - w - 1] + Diag);
                        if (x < w - 1) v = Mathf.Min(v, d[i - w + 1] + Diag);
                    }
                    d[i] = v;
                }
                for (int y = h - 1; y >= 0; y--)
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = y * w + x;
                    float v = d[i];
                    if (x < w - 1) v = Mathf.Min(v, d[i + 1] + 1f);
                    if (y < h - 1)
                    {
                        v = Mathf.Min(v, d[i + w] + 1f);
                        if (x < w - 1) v = Mathf.Min(v, d[i + w + 1] + Diag);
                        if (x > 0) v = Mathf.Min(v, d[i + w - 1] + Diag);
                    }
                    d[i] = v;
                }
            }
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float v = d[i];
                if (v <= 0f) { px[i] = fill(x, y); continue; }
                if (v > ink + 1f) continue;
                float a = Mathf.Clamp01(ink + 1f - v); // soft outer edge
                var under = px[i];
                var c = Color32.Lerp(under.a == 0 ? new Color32(Ink.r, Ink.g, Ink.b, 0) : under, Ink, a);
                c.a = (byte)Mathf.Max(under.a, a * 255f);
                px[i] = c;
            }
        }

        static Texture2D Finish(Color32[] px, int w, int h, string name)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = name };
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        static Color32 C(float r, float g, float b) => new((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), 255);
        static Color32 Mix(Color32 a, Color32 b, float k) => Color32.Lerp(a, b, k);

        /// <summary>A foil pack: crimped top and bottom, deep arcane blue to violet, a shine, a gold coin seal with a sparkle.</summary>
        static Texture2D MakePack()
        {
            const int w = 256, h = 360;
            const float crimp = 40f;
            var px = new Color32[w * h];
            Color32 lo = C(0.13f, 0.18f, 0.62f), hi = C(0.5f, 0.2f, 0.72f), foil = C(0.78f, 0.76f, 0.9f), foilDark = C(0.55f, 0.52f, 0.7f);
            float Zig(float x) => 8f + 9f * Tri(x / 16f);
            Layer(px, w, h, (x, y) => x >= 8 && x <= w - 8 && y >= Zig(x) && y <= h - Zig(x), (x, y) =>
            {
                bool band = y < crimp || y > h - crimp;
                if (Mathf.Abs(y - crimp) < 2.5f || Mathf.Abs(y - (h - crimp)) < 2.5f) return Ink;
                if (band) return (x / 7) % 2 == 0 ? foil : foilDark; // crimp ridges
                var c = Mix(lo, hi, (y - crimp) / (h - 2 * crimp));
                float shine = (x + y * 0.55f) % 230f;
                if (shine > 40f && shine < 62f) c = Mix(c, C(1, 1, 1), 0.3f);
                if (shine > 70f && shine < 76f) c = Mix(c, C(1, 1, 1), 0.2f);
                return c;
            }, 6f);
            // the seal: a gold disc with a cream sparkle
            Color32 gold = C(1f, 0.78f, 0.25f), goldDark = C(0.85f, 0.55f, 0.12f);
            Layer(px, w, h, (x, y) => Disc(x, y, w / 2f, h / 2f, 66f), (x, y) =>
            {
                float r = Mathf.Sqrt((x - w / 2f) * (x - w / 2f) + (y - h / 2f) * (y - h / 2f));
                return r > 52f ? goldDark : gold;
            }, 5f);
            // a rune stamped in the seal
            Layer(px, w, h, (x, y) => Rune(x, y, w / 2f, h / 2f, 36f, 0f, 3, 6f), (x, y) => C(1f, 0.98f, 0.88f), 3.5f);
            // small runes on the foil
            Layer(px, w, h, (x, y) => Rune(x, y, 56, h - 96, 17f, -12f, 0, 3.2f) || Rune(x, y, w - 54, 100, 19f, 10f, 1, 3.4f)
                                      || Rune(x, y, w - 62, h - 112, 12f, 6f, 2, 2.6f),
                (x, y) => C(0.72f, 0.82f, 1f), 3f);
            return Finish(px, w, h, "ShopPack");
        }

        /// <summary>Card back: inked edge, deep arcane blue, a rune circle with a big rune in it, runes in the corners.</summary>
        static Texture2D MakeBack()
        {
            const int w = 200, h = 280;
            var px = new Color32[w * h];
            Color32 deep = C(0.05f, 0.08f, 0.3f), mid = C(0.12f, 0.2f, 0.62f), line = C(0.5f, 0.66f, 1f), bright = C(0.82f, 0.9f, 1f);
            Layer(px, w, h, (x, y) => RoundBox(x, y, 0, 0, w, h, 18), (x, y) => Ink, 0f);
            Layer(px, w, h, (x, y) => RoundBox(x, y, 5, 5, w - 5, h - 5, 14), (x, y) =>
            {
                float r = Mathf.Sqrt((x - w / 2f) * (x - w / 2f) + (y - h / 2f) * (y - h / 2f));
                return Mix(mid, deep, Mathf.Clamp01(r / 150f));
            }, 0f);
            // inner border line
            Layer(px, w, h, (x, y) => RoundBox(x, y, 15, 15, w - 15, h - 15, 10) && !RoundBox(x, y, 19, 19, w - 19, h - 19, 7), (x, y) => line, 0f);
            // rune circle: two rings with six small runes between them, facing out
            float cx = w / 2f, cy = h / 2f;
            Layer(px, w, h, (x, y) =>
            {
                float r = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (Mathf.Abs(r - 76f) < 2.6f || Mathf.Abs(r - 52f) < 1.6f) return true;
                for (int i = 0; i < 6; i++)
                {
                    float a = (90f + i * 60f) * Mathf.Deg2Rad;
                    if (Rune(x, y, cx + Mathf.Cos(a) * 64f, cy + Mathf.Sin(a) * 64f, 7.5f, i * 60f, i, 1.7f)) return true;
                }
                return false;
            }, (x, y) => line, 2f);
            // the big rune in the middle
            Layer(px, w, h, (x, y) => Rune(x, y, cx, cy, 30f, 0f, 3, 4.5f), (x, y) => bright, 3.5f);
            // a rune in each corner
            Layer(px, w, h, (x, y) => Rune(x, y, 38, 38, 11f, 0f, 2, 2.4f) || Rune(x, y, w - 38, 38, 11f, 0f, 1, 2.4f)
                                      || Rune(x, y, 38, h - 38, 11f, 0f, 1, 2.4f) || Rune(x, y, w - 38, h - 38, 11f, 0f, 2, 2.4f),
                (x, y) => line, 2.5f);
            return Finish(px, w, h, "ShopCardBack");
        }

        /// <summary>A bullseye (the practice range button): red and white rings, each inked.</summary>
        static Texture2D MakeTarget()
        {
            const int s = 128;
            var px = new Color32[s * s];
            Color32 red = C(0.86f, 0.18f, 0.16f), cream = C(0.98f, 0.95f, 0.88f);
            float[] radii = { 58f, 45f, 32f, 19f, 8f };
            for (int i = 0; i < radii.Length; i++)
            {
                float r = radii[i];
                var col = i % 2 == 0 ? red : cream;
                Layer(px, s, s, (x, y) => Disc(x, y, s / 2f, s / 2f, r), (x, y) => col, i == 0 ? 5f : 3f);
            }
            return Finish(px, s, s, "Target");
        }

        static Texture2D MakeArrow()
        {
            const int n = 128;
            var px = new Color32[n * n];
            const float cy = n / 2f, x0 = 18f, neck = 66f, tip = 112f, shaftH = 13f, headH = 40f, r = 6f;
            Layer(px, n, n, (x, y) =>
            {
                // the shaft: a rounded bar; the head: a triangle
                if (RoundBox(x, y, x0, cy - shaftH, neck + 4f, cy + shaftH, r)) return true;
                if (x < neck || x > tip) return false;
                return Mathf.Abs(y - cy) <= headH * (tip - x) / (tip - neck);
            }, (x, y) => White, 6f);
            return Finish(px, n, n, "Arrow");
        }

        static Texture2D MakeCoin()
        {
            const int n = 64;
            var px = new Color32[n * n];
            Color32 gold = C(1f, 0.8f, 0.25f), dark = C(0.82f, 0.52f, 0.1f);
            Layer(px, n, n, (x, y) => Disc(x, y, n / 2f, n / 2f, 27f), (x, y) =>
            {
                float r = Mathf.Sqrt((x - n / 2f) * (x - n / 2f) + (y - n / 2f) * (y - n / 2f));
                return Mathf.Abs(r - 19f) < 2f ? dark : gold;
            }, 3f);
            Layer(px, n, n, (x, y) => Star(x, y, n / 2f, n / 2f, 12f), (x, y) => C(1f, 0.97f, 0.8f), 0f);
            return Finish(px, n, n, "Coin");
        }

        /// <summary>One white rune with an ink outline (tint it).</summary>
        static Texture2D MakeGlyph(int index)
        {
            const int n = 96;
            var px = new Color32[n * n];
            Layer(px, n, n, (x, y) => Rune(x, y, n / 2f, n / 2f, 36f, 0f, index, 5f), (x, y) => White, 4f);
            return Finish(px, n, n, "Rune" + index);
        }

        /// <summary>
        /// A white rune circle (tint it): an outer ring, an inner ring, eight runes between them facing out, and a
        /// six-pointed star of two triangles inside. All inked.
        /// </summary>
        static Texture2D MakeRuneCircle()
        {
            const int n = 384;
            const float c = n / 2f;
            var px = new Color32[n * n];
            var star = new Vector2[6];
            for (int i = 0; i < 6; i++)
            {
                float a = (90f + i * 60f) * Mathf.Deg2Rad;
                star[i] = new Vector2(c + Mathf.Cos(a) * 128f, c + Mathf.Sin(a) * 128f);
            }
            Layer(px, n, n, (x, y) =>
            {
                float dx = x - c, dy = y - c, r = Mathf.Sqrt(dx * dx + dy * dy);
                if (Mathf.Abs(r - 178f) < 5f || Mathf.Abs(r - 134f) < 3f) return true;
                if (r > 138f && r < 174f)
                {
                    // the rune nearest this point's angle
                    float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                    int i = Mathf.RoundToInt((ang - 22.5f) / 45f);
                    float a = (22.5f + i * 45f) * Mathf.Deg2Rad;
                    return Rune(x, y, c + Mathf.Cos(a) * 156f, c + Mathf.Sin(a) * 156f, 12.5f, 22.5f + i * 45f - 90f, (i % 4 + 4) % 4, 2.8f);
                }
                if (r < 132f)
                {
                    var p = new Vector2(x, y);
                    for (int i = 0; i < 6; i++)
                        if (SegDist(p, star[i], star[(i + 2) % 6]) < 2.5f) return true;
                }
                return false;
            }, (x, y) => White, 3.5f);
            return Finish(px, n, n, "RuneCircle");
        }

        /// <summary>A white disc with an ink outline (tint it): the core of light at a burst.</summary>
        static Texture2D MakeDisc()
        {
            const int n = 128;
            var px = new Color32[n * n];
            Layer(px, n, n, (x, y) => Disc(x, y, n / 2f, n / 2f, 56f), (x, y) => White, 5f);
            return Finish(px, n, n, "Disc");
        }
    }
}
