using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The shop's other side: the cosmetics crate (arrows either side swap it with the pack). Click it: it rattles, its lid
    /// blows off, and a reel of cosmetics spins past a marker, slower and slower, until it stops on the one you got. That
    /// one comes out of the reel onto a turntable in a ring of its rarity's colour, with its name under it. It's in your
    /// wardrobe the moment the crate is bought.
    /// </summary>
    public partial class ShopMenu
    {
        const float SideX = 480f, SlideX = 1100f, SlideTime = 0.3f;
        const float OpenTime = 1f, LidTime = 0.75f, ReelTime = 6.2f, SettleTime = 0.45f, CollapseTime = 0.22f;
        const float ReelW = 1240f, ReelH = 208f, TileW = 176f, TilePitch = 188f;
        const int WinAt = 46, ReelTiles = 54;
        static readonly Color ReelInk = new(0.07f, 0.055f, 0.1f, 1f);

        bool _crateMode;
        float _slideT = 1f, _slideDir = 1f, _marker, _lidT = -1f, _winT;
        RectTransform _crate, _crateLid, _left, _right, _reel, _strip, _markerRect, _won;
        bool _crateHover, _leftHover, _rightHover, _winEpic;
        int _passed;
        float _stopAt;
        CosmeticItem _prize;
        Image _wonRing;
        Text _wonName;
        RectTransform _wonPic;

        void BuildCrate()
        {
            _crate = UIFactory.Rect("Crate", _stage);
            UIFactory.Place(_crate, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), PackPos, new Vector2(ShopArt.CrateW, ShopArt.CrateBodyH + ShopArt.CrateLidH - ShopArt.LidOverlap));
            var hit = _crate.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            var click = _crate.gameObject.AddComponent<ShopClick>();
            click.onClick = ClickPack;
            click.onHover = h => _crateHover = h;
            var body = UIFactory.Rect("Body", _crate).gameObject.AddComponent<RawImage>();
            body.texture = ShopArt.CrateBody;
            body.raycastTarget = false;
            UIFactory.Place(body.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), Vector2.zero, new Vector2(ShopArt.CrateW, ShopArt.CrateBodyH));
            var lid = UIFactory.Rect("Lid", _crate).gameObject.AddComponent<RawImage>();
            lid.texture = ShopArt.CrateLid;
            lid.raycastTarget = false;
            _crateLid = UIFactory.Place(lid.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 0.5f), LidHome, new Vector2(ShopArt.CrateW, ShopArt.CrateLidH));

            _left = SideArrow(-1f, h => _leftHover = h);
            _right = SideArrow(1f, h => _rightHover = h);
            _crate.gameObject.SetActive(false);
        }

        static Vector2 LidHome => new(0, -ShopArt.CrateLidH * 0.5f);

        RectTransform SideArrow(float side, System.Action<bool> onHover)
        {
            var root = UIFactory.Rect(side < 0f ? "Left" : "Right", _stage);
            UIFactory.Place(root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), PackPos + new Vector2(side * SideX, 0), new Vector2(110, 110));
            var img = root.gameObject.AddComponent<RawImage>();
            img.texture = ShopArt.Arrow;
            img.color = ArcaneLight;
            var c = root.gameObject.AddComponent<ShopClick>();
            c.onClick = () => Swap(side);
            c.onHover = onHover;
            return root;
        }

        void Swap(float side)
        {
            if (_phase != Phase.Idle || _slideT < 0.6f) return;
            _crateMode = !_crateMode;
            _slideDir = side;
            _slideT = 0f;
            _denyT = 0f;
            _priceText.text = (_crateMode ? Rewards.CrateCost : Rewards.PackCost).ToString();
            (_crateMode ? _crate : _pack).localScale = Vector3.one * 0.6f;
        }

        bool CanAfford => _crateMode ? Crates.CanAfford : Packs.CanAfford;

        void ResetCrate(bool popIn)
        {
            CosmeticIcons.StopLive();
            if (_reel != null) Destroy(_reel.gameObject);
            if (_won != null) Destroy(_won.gameObject);
            _reel = _strip = _markerRect = _won = _wonPic = null;
            _prize = null;
            _lidT = -1f;
            _winEpic = false;
            _slideT = 1f;
            _crateLid.gameObject.SetActive(true);
            _crateLid.anchoredPosition = LidHome;
            _crateLid.localRotation = Quaternion.identity;
            _crateLid.localScale = Vector3.one;
            _crate.localScale = popIn ? Vector3.zero : Vector3.one;
            _crate.anchoredPosition = _pack.anchoredPosition = PackPos;
            _crate.localRotation = Quaternion.identity;
            _crate.gameObject.SetActive(_crateMode);
            _pack.gameObject.SetActive(!_crateMode);
            _left.gameObject.SetActive(true);
            _right.gameObject.SetActive(true);
        }

        // ------------------------------------------------------------------ idle

        /// <summary>After the pack's own idle moves: which of the two is in the middle, sliding past each other when swapped.</summary>
        void UpdateIdleCrate(float dt, float deny)
        {
            _slideT = Mathf.MoveTowards(_slideT, 1f, dt / SlideTime);
            float k = EaseOut(_slideT);
            float ease = 1f - Mathf.Exp(-12f * dt);
            var front = _crateMode ? _crate : _pack;
            var back = _crateMode ? _pack : _crate;
            if (_crateMode)
            {
                _crate.localScale = Vector3.one * Mathf.Lerp(_crate.localScale.x, _crateHover ? 1.07f : 1f, ease);
                _crate.anchoredPosition = PackPos + new Vector2(deny, Mathf.Sin(_idleT * 2f) * 6f);
                _crate.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(_idleT * 1.1f) * 1.5f);
                // the lid lifts a crack when hovered
                _crateLid.anchoredPosition = Vector2.Lerp(_crateLid.anchoredPosition, LidHome + new Vector2(0, _crateHover ? 12f + Mathf.Sin(_idleT * 14f) * 3f : 0f), ease);
            }
            front.gameObject.SetActive(true);
            front.anchoredPosition += new Vector2(_slideDir * SlideX * (1f - k), 0);
            back.gameObject.SetActive(_slideT < 1f);
            back.anchoredPosition = PackPos + new Vector2(-_slideDir * SlideX * k, 0);
            back.localScale = Vector3.one * Mathf.Lerp(1f, 0.6f, k);

            float bob = Mathf.Sin(_idleT * 3f) * 5f;
            ArrowLook(_left, -1f, _leftHover, bob, ease);
            ArrowLook(_right, 1f, _rightHover, bob, ease);
        }

        static void ArrowLook(RectTransform r, float side, bool hover, float bob, float ease)
        {
            float s = Mathf.Lerp(r.localScale.y, hover ? 1.2f : 1f, ease);
            r.localScale = new Vector3(s * side, s, 1f);
            r.anchoredPosition = PackPos + new Vector2(side * (SideX + bob + (hover ? 10f : 0f)), 0);
        }

        void HideArrows()
        {
            _left.gameObject.SetActive(false);
            _right.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ opening

        void BuyCrate()
        {
            var item = Crates.Open();
            if (item == null) { _denyT = 0.45f; return; }
            _prize = item;
            _phase = Phase.Opening;
            _t = 0f;
            _lidT = -1f;
            _price.gameObject.SetActive(false);
            _pack.gameObject.SetActive(false);
            _crateLid.anchoredPosition = LidHome;
            HideArrows();
            BuildReel(); // now, so its pictures are ready when it's shown
        }

        void UpdateOpening(float dt)
        {
            if (_lidT < 0f)
            {
                float k = Mathf.Clamp01(_t / OpenTime), amp = k * k;
                // it hops and rattles, the lid banging up and down harder and harder
                float hop = Mathf.Abs(Mathf.Sin(_t * 17f)) * 26f * amp;
                _crate.anchoredPosition = PackPos + new Vector2(Random.Range(-1f, 1f) * (2f + 9f * amp), hop);
                _crate.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(_t * 23f) * (1f + 6f * amp));
                _crate.localScale = new Vector3(1f + 0.06f * amp - hop * 0.001f, 1f + hop * 0.0015f, 1f);
                _crateLid.anchoredPosition = LidHome + new Vector2(0, Mathf.Abs(Mathf.Sin(_t * 31f)) * 22f * amp);
                _crateLid.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(_t * 41f) * 5f * amp);
                _circle.localScale = Vector3.one * Mathf.Lerp(1f, 0.7f, k);
                _circleImg.color = Color.Lerp(IdleCircleColour, new Color(ArcaneLight.r, ArcaneLight.g, ArcaneLight.b, 0.9f), k);
                _shakeKick = Mathf.Max(_shakeKick, 0.4f * amp);
                int n = Mathf.FloorToInt(dt * (4f + 34f * amp) + Random.value);
                for (int i = 0; i < n; i++)
                    Spit(PackPos + new Vector2(Random.Range(-130f, 130f), 70f), new Vector2(Random.Range(-260f, 260f), Random.Range(350f, 800f)), Random.Range(28f, 46f), RuneColour(), Random.Range(0.4f, 0.65f));
                if (_t < OpenTime) return;

                // BANG: the lid goes spinning off, light and runes pour up out of it
                _lidT = 0f;
                _hitstop = 0.09f;
                _shakeKick = 1.1f;
                _flash.gameObject.SetActive(true);
                _flash.anchoredPosition = PackPos + new Vector2(0, 60);
                _flash.localScale = Vector3.one * 1.3f;
                _flashImg.color = ArcaneCore;
                _flashT = 0f;
                _circleBurstT = 0f;
                for (int i = 0; i < 30; i++)
                    Spit(PackPos + new Vector2(Random.Range(-110f, 110f), 80f), new Vector2(Random.Range(-520f, 520f), Random.Range(500f, 1300f)), Random.Range(34f, 60f), RuneColour(), Random.Range(0.5f, 0.85f));
                return;
            }

            _lidT += dt;
            float u = Mathf.Clamp01(_lidT / LidTime);
            _crateLid.anchoredPosition = LidHome + new Vector2(330f * u, 900f * u - 780f * u * u);
            _crateLid.localRotation = Quaternion.Euler(0, 0, -520f * u);
            _crateLid.localScale = Vector3.one * (1f - EaseIn(u));
            // the box squashes, then shrinks away as the reel opens over it
            float s = Mathf.Clamp01((_lidT - 0.25f) / 0.35f);
            _crate.anchoredPosition = PackPos;
            _crate.localRotation = Quaternion.identity;
            float squash = _lidT < 0.16f ? Mathf.Sin(_lidT / 0.16f * Mathf.PI) * 0.25f : 0f;
            _crate.localScale = new Vector3(1f + squash, 1f - squash, 1f) * (1f - EaseIn(s));
            float open = EaseOutBack01(Mathf.Clamp01((_lidT - 0.35f) / 0.35f));
            _reel.gameObject.SetActive(_lidT > 0.35f);
            _reel.localScale = new Vector3(1f, Mathf.Max(0.001f, open), 1f);
            if (_lidT < LidTime) return;
            _crate.gameObject.SetActive(false);
            _phase = Phase.Reeling;
            _t = 0f;
            _reel.localScale = Vector3.one;
        }

        // ------------------------------------------------------------------ the reel

        static float TileX(int i) => i * TilePitch;

        void BuildReel()
        {
            _reel = UIFactory.Rect("Reel", _stage);
            UIFactory.Place(_reel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), PackPos, new Vector2(ReelW, ReelH));
            var ink = UIFactory.Image("Ink", _reel, ReelInk, CardView.Rounded);
            ink.type = Image.Type.Sliced;
            UIFactory.Stretch(ink.rectTransform, -8, -8, -8, -8);
            var bed = UIFactory.Image("Bed", _reel, new Color(0.12f, 0.15f, 0.3f, 1f), CardView.Rounded);
            bed.type = Image.Type.Sliced;
            UIFactory.Stretch(bed.rectTransform);
            var window = UIFactory.Stretch(UIFactory.Rect("Window", _reel), 8, 0, 8, 0);
            window.gameObject.AddComponent<RectMask2D>();
            _strip = UIFactory.Rect("Strip", window);
            UIFactory.Place(_strip, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            for (int i = 0; i < ReelTiles; i++)
            {
                var item = i == WinAt ? _prize : Crates.Roll();
                Color rc = Cards.RarityColor(item.Info.rarity);
                var edge = UIFactory.Image("Tile", _strip, ReelInk, CardView.Rounded);
                edge.type = Image.Type.Sliced;
                UIFactory.Place(edge.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(TileX(i), 0), new Vector2(TileW + 10, TileW + 10));
                var face = UIFactory.Image("Face", edge.transform, rc, CardView.Rounded);
                face.type = Image.Type.Sliced;
                UIFactory.Stretch(face.rectTransform, 5, 5, 5, 5);
                var inner = UIFactory.Image("Inner", face.transform, Color.Lerp(new Color(0.1f, 0.12f, 0.18f, 1f), rc, 0.3f), CardView.Rounded);
                inner.type = Image.Type.Sliced;
                UIFactory.Stretch(inner.rectTransform, 6, 6, 6, 6);
                CosmeticsPage.Thumb(inner.rectTransform, item, TileW - 26f);
            }
            // the marker: an inked gold line through the middle
            _markerRect = UIFactory.Rect("Marker", _reel);
            UIFactory.Place(_markerRect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(8, ReelH + 44));
            var mi = UIFactory.Image("Ink", _markerRect, ReelInk);
            UIFactory.Stretch(mi.rectTransform, -4, -4, -4, -4);
            var gold = UIFactory.Image("Gold", _markerRect, new Color(1f, 0.8f, 0.28f));
            UIFactory.Stretch(gold.rectTransform);
            foreach (var g in _reel.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;

            // it stops anywhere on the winning tile, then settles on its middle
            _stopAt = TileX(WinAt) + Random.Range(-0.44f, 0.44f) * TileW;
            _passed = 0;
            _marker = 0f;
            SetReel(0f);
            _reel.gameObject.SetActive(false);
            _fx.SetAsLastSibling();
        }

        void SetReel(float x) => _strip.anchoredPosition = new Vector2(-x, 0);

        void UpdateReeling(float dt)
        {
            float start = TileX(3);
            float k = Mathf.Clamp01(_t / ReelTime);
            float x = Mathf.Lerp(start, _stopAt, 1f - Mathf.Pow(1f - k, 4.2f));
            float settle = Mathf.Clamp01((_t - ReelTime - 0.25f) / SettleTime);
            x = Mathf.Lerp(x, TileX(WinAt), Mathf.SmoothStep(0f, 1f, settle));
            SetReel(x);

            // a tick for every tile's edge that goes by: the marker flicks, the whole screen knocks
            int passed = Mathf.FloorToInt(x / TilePitch + 0.5f);
            if (passed != _passed)
            {
                _passed = passed;
                _marker = 1f;
                _shakeKick = Mathf.Max(_shakeKick, 0.16f);
                if (k > 0.35f) Spit(PackPos + new Vector2(0, ReelH * 0.5f + 18f), new Vector2(Random.Range(-160f, 160f), Random.Range(180f, 340f)), 24f, new Color(1f, 0.86f, 0.45f), 0.4f);
            }
            _marker = Mathf.MoveTowards(_marker, 0f, dt * 7f);
            _markerRect.localRotation = Quaternion.Euler(0, 0, -7f * _marker);
            _markerRect.localScale = new Vector3(1f + 0.5f * _marker, 1f + 0.05f * _marker, 1f);

            float end = ReelTime + 0.25f + SettleTime + 0.15f;
            if (_t < end) return;
            float c = Mathf.Clamp01((_t - end) / CollapseTime);
            _reel.localScale = new Vector3(1f, Mathf.Max(0.001f, 1f - EaseIn(c)), 1f);
            if (c >= 1f) ShowPrize();
        }

        // ------------------------------------------------------------------ what you got

        void ShowPrize()
        {
            _reel.gameObject.SetActive(false);
            _phase = Phase.Won;
            _t = 0f;
            _winT = 0f;
            var rarity = _prize.Info.rarity;
            Color rc = Cards.RarityColor(rarity), bright = Color.Lerp(rc, Color.white, 0.3f);
            float big = rarity switch { Rarity.Epic => 1.9f, Rarity.Rare => 1.4f, _ => 1f };
            _winEpic = rarity == Rarity.Epic;

            _won = UIFactory.Rect("Won", _stage);
            UIFactory.Place(_won, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), PackPos, new Vector2(100, 100));
            _wonRing = UIFactory.Image("Ring", _won, new Color(bright.r, bright.g, bright.b, 0.75f), ShopArt.RuneCircleSprite);
            UIFactory.Place(_wonRing.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560, 560));
            var pic = UIFactory.Rect("Picture", _won).gameObject.AddComponent<RawImage>();
            pic.texture = CosmeticIcons.Live(_prize);
            _wonPic = UIFactory.Place(pic.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(400, 400));
            _wonName = UIFactory.Text("Name", _won, _prize.Name, 46, TextAnchor.MiddleCenter, bright, FontStyle.Bold);
            UIFactory.Place(_wonName.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -316), new Vector2(1200, 60));
            var o = _wonName.gameObject.AddComponent<Outline>();
            o.effectColor = ReelInk;
            o.effectDistance = new Vector2(3f, -3f);
            foreach (var g in _won.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            _won.localScale = Vector3.zero;

            float kick = 1f + (big - 1f) * 0.4f;
            _hitstop = 0.08f * kick;
            _shakeKick = 0.85f * kick;
            _flash.gameObject.SetActive(true);
            _flash.anchoredPosition = PackPos;
            _flash.localScale = Vector3.one * 1.5f;
            _flashImg.color = Color.Lerp(rc, Color.white, 0.55f);
            _flashT = 0f;
            int ring = Mathf.RoundToInt(14 * big);
            for (int i = 0; i < ring; i++)
            {
                float a = i / (float)ring * Mathf.PI * 2f;
                Spit(PackPos, new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 1150f * big, 52f * Mathf.Sqrt(big), bright, 0.6f);
            }
            for (int i = 0; i < 14; i++)
                Spit(PackPos, Random.insideUnitCircle.normalized * Random.Range(350f, 900f), Random.Range(28f, 46f), i % 2 == 0 ? ArcaneCore : _prize.Colour, Random.Range(0.45f, 0.7f));
            _fx.SetAsLastSibling();
        }

        void UpdateWon(float dt)
        {
            _winT += dt;
            float pop = EaseOutBack01(Mathf.Clamp01(_winT / 0.45f));
            _won.localScale = Vector3.one * Mathf.Max(0f, pop);
            _wonRing.rectTransform.localRotation = Quaternion.Euler(0, 0, -_winT * 14f);
            // shown as big as it rolled
            _wonPic.localScale = Vector3.one * Mathf.Lerp(0.95f, 1.25f, (_prize.Scale - 1f) * 2f);
            _wonPic.anchoredPosition = new Vector2(0, Mathf.Sin(_winT * 2f) * 8f);
            _wonName.rectTransform.localScale = Vector3.one * EaseOutBack01(Mathf.Clamp01((_winT - 0.2f) / 0.35f));
            if (_winEpic && Random.value < dt * 9f)
            {
                Color ec = Cards.RarityColor(Rarity.Epic);
                Vector2 at = PackPos + Random.insideUnitCircle.normalized * Random.Range(200f, 300f);
                Twinkle(at, Random.Range(26f, 42f), Random.value < 0.5f ? Color.Lerp(ec, Color.white, 0.35f) : ec);
            }
            if (_buttons == null && _winT > 0.6f)
            {
                _t = 0f;
                BuildDoneButtons();
                _buttons.anchoredPosition = new Vector2(0, -420);
            }
            if (_buttons != null) _buttons.localScale = Vector3.one * EaseOutBack01(Mathf.Clamp01(_t / 0.35f));
        }
    }
}
