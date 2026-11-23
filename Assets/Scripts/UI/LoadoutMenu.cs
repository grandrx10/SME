using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Loadout screen (Loadout key, B by default). No instructions — it explains itself:
    ///  - top bar: the five loadouts (the lit one is what you take into the next round), your face, close
    ///  - the five ability keys, each with a card slot under it
    ///  - your deck: every card you own, 5 per row, scrolling
    /// Drag a card from the deck onto a slot to equip it, between slots to swap, back into the deck to take it off.
    /// Clicking a deck card drops it into the first empty slot. Hold Alt over any card to see it up close.
    /// </summary>
    public class LoadoutMenu
    {
        static readonly Vector2 WindowSize = new(1080, 960);
        static readonly Vector2 MusicSize = new(680, 960);
        const float PanelGap = 20f;
        const float Pad = 28f;
        const float SlotW = 164f, SlotGap = 26f;
        const float DeckGap = 20f;
        const float BarH = 60f;

        public bool IsOpen { get; private set; }
        public static bool IsTyping
        {
            get
            {
                var go = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                var f = go != null ? go.GetComponent<InputField>() : null;
                return f != null && f.isFocused;
            }
        }

        readonly RectTransform _root, _window;
        readonly Action _onVisibilityChanged;
        readonly GameObject _music;
        readonly Vector2 _windowWithMusic;
        RectTransform _page;
        bool _faceOpen;
        float _deckScroll = 1f;

        public LoadoutMenu(Transform canvas, Action onVisibilityChanged, Action openShop)
        {
            _onVisibilityChanged = onVisibilityChanged;
            _openShop = openShop;
            var dim = UIFactory.Image("LoadoutMenu", canvas, new Color(0.02f, 0.03f, 0.05f, 0.86f));
            _root = UIFactory.Stretch(dim.rectTransform);
            // its own canvas: the HUD changing every frame no longer forces this whole menu to be rebuilt
            var sub = dim.gameObject.AddComponent<Canvas>();
            sub.overrideSorting = true;
            sub.sortingOrder = 20;
            dim.gameObject.AddComponent<GraphicRaycaster>();
            dim.gameObject.AddComponent<CardInspector>();

            float total = WindowSize.x + PanelGap + MusicSize.x;
            var win = UIFactory.Image("Window", _root, new Color(0.075f, 0.085f, 0.11f, 0.98f), CardView.Rounded);
            win.type = Image.Type.Sliced;
            _window = win.rectTransform;
            _windowWithMusic = new Vector2(-total / 2 + WindowSize.x / 2, 0);
            UIFactory.Place(_window, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), _windowWithMusic, WindowSize);
            _music = MusicPanel.Create(_root, new Vector2(total / 2 - MusicSize.x / 2, 0), MusicSize).gameObject;
            _root.gameObject.SetActive(false);
            Loadouts.Changed += () => { if (IsOpen && !_faceOpen && !DragCard.Dragging) Rebuild(); };
            // new cards from a pack, gold earned or spent
            Collection.Changed += () => { if (IsOpen && !_faceOpen && !DragCard.Dragging) Rebuild(); };
            Wallet.Changed += () => { if (IsOpen && !_faceOpen && !DragCard.Dragging) Rebuild(); };
        }

        readonly Action _openShop;

        public void SetMusicVisible(bool on)
        {
            if (_music.activeSelf != on) _music.SetActive(on);
            _window.anchoredPosition = on ? _windowWithMusic : Vector2.zero;
        }

        public void Open()
        {
            IsOpen = true;
            _faceOpen = false;
            Collection.LoadoutHint = false; // made it here after the first pack
            Rebuild();
            _root.gameObject.SetActive(true);
            _onVisibilityChanged?.Invoke();
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            _faceOpen = false;
            SecretReveal.Stop();
            DragCard.Cancel();
            FacePreview.SetRendering(false);
            _root.gameObject.SetActive(false);
            _onVisibilityChanged?.Invoke();
        }

        public void Toggle() { if (IsOpen) Close(); else Open(); }

        public void SetShown(bool shown)
        {
            _root.gameObject.SetActive(IsOpen && shown);
            FacePreview.SetRendering(IsOpen && shown && _faceOpen);
        }

        public void Back()
        {
            if (SecretReveal.Playing) { SecretReveal.Dismiss(); return; }
            if (_faceOpen) { _faceOpen = false; FacePreview.SetRendering(false); Rebuild(); }
            else Close();
        }

        RectTransform NewPage()
        {
            if (_page != null)
            {
                var sr = _page.GetComponentInChildren<ScrollRect>();
                if (sr != null) _deckScroll = sr.verticalNormalizedPosition;
                UnityEngine.Object.Destroy(_page.gameObject);
            }
            _page = UIFactory.Stretch(UIFactory.Rect("Page", _window));
            return _page;
        }

        // ------------------------------------------------------------------ layout

        void Rebuild()
        {
            if (_faceOpen) return;
            var page = NewPage();
            int li = Loadouts.SelectedIndex;
            var loadout = Loadouts.Get(li);
            float W = WindowSize.x;

            // ---- top bar: loadouts | face, close ----
            for (int i = 0; i < Loadouts.Count; i++)
            {
                int idx = i;
                bool sel = i == li;
                var b = RoundButton(page, (i + 1).ToString(), () => Loadouts.Select(idx), sel ? UIFactory.Accent : new Color(0.17f, 0.19f, 0.24f, 1f));
                UIFactory.Label(b).color = sel ? Color.black : UIFactory.TextCol;
                UIFactory.Label(b).fontStyle = FontStyle.Bold;
                UIFactory.Label(b).fontSize = 28;
                UIFactory.Place((RectTransform)b.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(Pad + i * (BarH + 10), -Pad), new Vector2(BarH, BarH));
            }
            var close = RoundButton(page, "✕", Close, new Color(0.17f, 0.19f, 0.24f, 1f));
            UIFactory.Place((RectTransform)close.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-Pad, -Pad), new Vector2(BarH, BarH));
            var face = RoundButton(page, "", OpenFace, FacePreview.SkinColor);
            UIFactory.Place((RectTransform)face.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-Pad - BarH - 12, -Pad), new Vector2(BarH, BarH));
            FacePreview.Painter.Draw(FaceArt.Strokes);
            var thumb = UIFactory.Rect("Drawing", face.transform).gameObject.AddComponent<RawImage>();
            thumb.texture = FacePreview.Painter.Texture;
            thumb.raycastTarget = false;
            UIFactory.Stretch(thumb.rectTransform, 4, 4, 4, 4);

            // shop: a pack and your gold
            var shop = RoundButton(page, "", () => _openShop?.Invoke(), new Color(0.17f, 0.19f, 0.24f, 1f));
            UIFactory.Place((RectTransform)shop.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(Pad + Loadouts.Count * (BarH + 10) + 20, -Pad), new Vector2(200, BarH));
            var pack = UIFactory.Rect("Pack", shop.transform).gameObject.AddComponent<RawImage>();
            pack.texture = ShopArt.Pack;
            pack.raycastTarget = false;
            UIFactory.Place(pack.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(12, 0), new Vector2(36, 50));
            var coin = UIFactory.Rect("Coin", shop.transform).gameObject.AddComponent<RawImage>();
            coin.texture = ShopArt.Coin;
            coin.raycastTarget = false;
            UIFactory.Place(coin.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(60, 0), new Vector2(30, 30));
            var gold = UIFactory.Text("Gold", shop.transform, Wallet.Gold.ToString(), 28, TextAnchor.MiddleLeft, new Color(1f, 0.86f, 0.4f), FontStyle.Bold);
            UIFactory.Place(gold.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(98, 0), new Vector2(96, 40));

            // practice range (in a game only): lit while you're in there, and pressing it again leaves
            var me = PlayerController.Local;
            if (me != null && me.IsSpawned)
            {
                bool inside = me.InPractice;
                var practice = RoundButton(page, "", () =>
                {
                    var pc = PlayerController.Local;
                    if (pc == null || !pc.IsSpawned) return;
                    pc.RequestPractice(pc.InPractice ? PlayerController.PracticeAction.Leave : PlayerController.PracticeAction.Enter);
                    Close();
                }, inside ? UIFactory.Accent : new Color(0.17f, 0.19f, 0.24f, 1f));
                UIFactory.Place((RectTransform)practice.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-Pad - 2 * (BarH + 12), -Pad), new Vector2(BarH, BarH));
                var target = UIFactory.Rect("Target", practice.transform).gameObject.AddComponent<RawImage>();
                target.texture = ShopArt.Target;
                target.raycastTarget = false;
                UIFactory.Stretch(target.rectTransform, 6, 6, 6, 6);
            }

            // ---- the five ability slots ----
            float slotsTop = Pad + BarH + 26;
            float rowW = Loadout.Slots * SlotW + (Loadout.Slots - 1) * SlotGap;
            float x0 = (W - rowW) * 0.5f;
            float slotH = SlotW * CardView.Aspect;
            for (int s = 0; s < Loadout.Slots; s++)
            {
                float x = x0 + s * (SlotW + SlotGap);
                var key = UIFactory.Text("Key", page, GameSettings.Binds[GameSettings.AbilityAction(s)].ShortName, 22, TextAnchor.MiddleCenter, UIFactory.Dim, FontStyle.Bold);
                UIFactory.Place(key.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(x, -slotsTop), new Vector2(SlotW, 28));

                var slot = UIFactory.Image("Slot" + (s + 1), page, new Color(1, 1, 1, 0.05f), CardView.Rounded);
                slot.type = Image.Type.Sliced;
                UIFactory.Place(slot.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(x, -slotsTop - 32), new Vector2(SlotW, slotH));
                var o = slot.gameObject.AddComponent<Outline>();
                o.effectColor = new Color(1, 1, 1, 0.18f);
                o.effectDistance = new Vector2(2, -2);
                var drop = slot.gameObject.AddComponent<SlotDrop>();
                drop.slot = s;
                drop.menu = this;

                var card = loadout.Get(s);
                if (card != CardId.None) AddCard(slot.rectTransform, card, SlotW, s, true);
            }

            // ---- the deck ----
            float deckTop = slotsTop + 32 + slotH + 28;
            var line = UIFactory.Image("Divider", page, new Color(1, 1, 1, 0.07f));
            UIFactory.Place(line.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -(deckTop - 14)), new Vector2(W - Pad * 2, 2));

            // collection, as a tag on the divider: a card back, then how many cards you own out of every card in the game
            // (a secret card only counts once it's found)
            int ownedAll = 0, cardsAll = 0;
            foreach (var c in Cards.All)
            {
                bool own = Collection.Owns(c.id);
                if (own) ownedAll++;
                if (own || c.rarity != Rarity.Secret) cardsAll++;
            }
            var tag = UIFactory.Image("Collection", page, new Color(0.17f, 0.19f, 0.24f, 1f), CardView.Rounded);
            tag.type = Image.Type.Sliced;
            UIFactory.Place(tag.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 0.5f), new Vector2(0, -(deckTop - 14)), new Vector2(124, 26));
            var back = UIFactory.Rect("CardBack", tag.rectTransform).gameObject.AddComponent<RawImage>();
            back.texture = ShopArt.CardBack;
            back.raycastTarget = false;
            UIFactory.Place(back.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(12, 0), new Vector2(16, 16 * CardView.Aspect - 1));
            var count = UIFactory.Text("Count", tag.rectTransform, ownedAll + " / " + cardsAll, 20, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            UIFactory.Stretch(count.rectTransform, 30, 0, 6, 0);
            var deckArea = UIFactory.Rect("Deck", page);
            UIFactory.Place(deckArea, new Vector2(0, 1), new Vector2(0, 1), new Vector2(Pad, -deckTop), new Vector2(W - Pad * 2, WindowSize.y - deckTop - Pad));
            var deckBg = deckArea.gameObject.AddComponent<Image>();
            deckBg.color = new Color(0, 0, 0, 0.22f);
            deckBg.sprite = CardView.Rounded;
            deckBg.type = Image.Type.Sliced;
            var deckDrop = deckArea.gameObject.AddComponent<DeckDrop>();
            deckDrop.menu = this;
            BuildDeck(deckArea, li, W - Pad * 2);
        }

        static Button RoundButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick, Color colour)
        {
            var b = UIFactory.Button("Button", parent, label, onClick, 28);
            var img = (Image)b.targetGraphic;
            img.sprite = CardView.Rounded;
            img.type = Image.Type.Sliced;
            img.color = colour;
            return b;
        }

        /// <summary>A card filling <paramref name="parent"/>: draggable (unless used up), hover-inspectable.</summary>
        void AddCard(RectTransform parent, CardId id, float width, int fromSlot, bool draggable)
        {
            var view = CardView.Build(parent, id, width);
            UIFactory.Stretch(view);
            var hit = view.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0); // catches the pointer
            view.gameObject.AddComponent<CardHover>().card = id;
            if (!draggable)
            {
                view.gameObject.AddComponent<CanvasGroup>().alpha = 0.28f;
                return;
            }
            var drag = view.gameObject.AddComponent<DragCard>();
            drag.card = id;
            drag.fromSlot = fromSlot;
            drag.menu = this;
        }

        void BuildDeck(RectTransform area, int loadoutIndex, float areaW)
        {
            // every copy you own, grouped by element; copies this loadout uses up show faded
            var copies = new List<(CardId id, bool free)>();
            var cards = new List<CardInfo>(Cards.All);
            cards.Sort((a, b) => a.element != b.element ? a.element.CompareTo(b.element) : Cards.Order(a.id).CompareTo(Cards.Order(b.id)));
            foreach (var c in cards)
            {
                int owned = Collection.CopiesOf(c.id);
                int free = Loadouts.Available(loadoutIndex, c.id);
                for (int k = 0; k < owned; k++) copies.Add((c.id, k < free));
            }

            const float inner = 18f, bar = 10f;
            float usable = areaW - inner * 2 - bar - 8;
            float cellW = Mathf.Min(SlotW, (usable - DeckGap * 4) / 5f);

            var viewport = UIFactory.Rect("Viewport", area);
            UIFactory.Stretch(viewport, inner, inner, inner + bar + 8, inner);
            viewport.gameObject.AddComponent<RectMask2D>();
            viewport.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0);
            var content = UIFactory.Rect("Content", viewport);
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.sizeDelta = Vector2.zero;
            // one section per card type: a heading, then that type's cards 5 to a row
            var column = content.gameObject.AddComponent<VerticalLayoutGroup>();
            column.childControlWidth = column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            column.spacing = 6;
            column.padding = new RectOffset(0, 0, 4, 10);
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // per type: how many you own, out of how many there are (every type shows, even with none owned)
            var have = new Dictionary<Element, int>();
            var total = new Dictionary<Element, int>();
            foreach (var c in cards)
            {
                bool own = Collection.Owns(c.id);
                total.TryGetValue(c.element, out int t);
                have.TryGetValue(c.element, out int h);
                total[c.element] = t + (own || c.rarity != Rarity.Secret ? 1 : 0);
                have[c.element] = h + (own ? 1 : 0);
            }

            Element? current = null;
            RectTransform grid = null;
            int next = 0; // walks the sorted cards so types with nothing owned still get a heading
            void Heading(Element e)
            {
                current = e;
                string count = " <size=20><color=#ffffff88>" + have[e] + " / " + total[e] + "</color></size>";
                var heading = UIFactory.Text("Heading " + e, content, Cards.ElementName(e) + count, 26, TextAnchor.LowerLeft,
                    Color.Lerp(Cards.ElementColor(e), Color.white, 0.25f), FontStyle.Bold);
                heading.supportRichText = true;
                heading.gameObject.AddComponent<LayoutElement>().preferredHeight = 40;
                heading.gameObject.AddComponent<Outline>().effectColor = new Color(0, 0, 0, 0.7f);
                grid = UIFactory.Rect("Cards " + e, content);
                var g = grid.gameObject.AddComponent<GridLayoutGroup>();
                g.cellSize = new Vector2(cellW, cellW * CardView.Aspect);
                g.spacing = new Vector2(DeckGap, DeckGap);
                g.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                g.constraintCount = 5;
                g.childAlignment = TextAnchor.UpperCenter;
                g.padding = new RectOffset(0, 0, 2, 6);
            }
            void HeadingsUpTo(Element? e)
            {
                // headings for every type up to (and including) e that hasn't had one yet
                for (; next < cards.Count && (e == null || cards[next].element <= e.Value); next++)
                    if (current != cards[next].element) Heading(cards[next].element);
            }
            // The World waits, blacked out, at the end of a complete set of Tarot cards
            bool secret = Collection.SecretReady, secretShown = false;
            void SecretCell()
            {
                secretShown = true;
                var cell = UIFactory.Rect("Secret", grid);
                SecretCard.Add(cell, cellW, _root, CardId.TheWorld);
            }
            foreach (var (id, free) in copies)
            {
                var info = Cards.Get(id);
                if (secret && !secretShown && current == Element.Tarot && info.element != Element.Tarot) SecretCell();
                if (current != info.element) HeadingsUpTo(info.element);
                var cell = UIFactory.Rect("Cell", grid);
                AddCard(cell, id, cellW, -1, free);
            }
            if (secret && !secretShown && current == Element.Tarot) SecretCell();
            HeadingsUpTo(null); // types after the last one you own

            var track = UIFactory.Image("Scrollbar", area, new Color(1, 1, 1, 0.06f));
            var tr = track.rectTransform;
            tr.anchorMin = new Vector2(1, 0);
            tr.anchorMax = new Vector2(1, 1);
            tr.pivot = new Vector2(1, 0.5f);
            tr.sizeDelta = new Vector2(bar, -inner * 2);
            tr.anchoredPosition = new Vector2(-inner * 0.5f, 0);
            var handle = UIFactory.Image("Handle", UIFactory.Stretch(UIFactory.Rect("Sliding Area", tr)), new Color(1, 1, 1, 0.3f));
            handle.rectTransform.sizeDelta = Vector2.zero;
            var sb = track.gameObject.AddComponent<Scrollbar>();
            sb.handleRect = handle.rectTransform;
            sb.targetGraphic = handle;
            sb.direction = Scrollbar.Direction.BottomToTop;

            var scroll = area.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            scroll.verticalScrollbar = sb;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = _deckScroll;
        }

        // ------------------------------------------------------------------ drag & drop results

        internal void DropOnSlot(DragCard from, int slot)
        {
            int li = Loadouts.SelectedIndex;
            if (from.fromSlot >= 0) Loadouts.Swap(li, from.fromSlot, slot);
            else Loadouts.Equip(li, slot, from.card);
            Rebuild();
        }

        internal void DropOnDeck(DragCard from)
        {
            if (from.fromSlot >= 0) Loadouts.Equip(Loadouts.SelectedIndex, from.fromSlot, CardId.None);
            Rebuild();
        }

        internal void ClickDeckCard(CardId id)
        {
            int li = Loadouts.SelectedIndex;
            var l = Loadouts.Get(li);
            for (int s = 0; s < Loadout.Slots; s++)
                if (l.Get(s) == CardId.None) { Loadouts.Equip(li, s, id); break; }
            Rebuild();
        }

        internal void Refresh() => Rebuild();

        // ------------------------------------------------------------------ face

        void OpenFace()
        {
            _faceOpen = true;
            var page = NewPage();
            var close = RoundButton(page, "✕", Back, new Color(0.17f, 0.19f, 0.24f, 1f));
            UIFactory.Place((RectTransform)close.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-Pad, -Pad), new Vector2(BarH, BarH));
            FaceEditor.Build(page, WindowSize);
            FacePreview.SetRendering(true);
        }
    }

    // ====================================================================== dragging

    /// <summary>
    /// A card you can pick up. While dragged it's a solid card in your hand: lifted (bigger, with a shadow), swinging
    /// and tilting with the mouse's motion, and its old spot left empty. Dropped on nothing, it eases back home.
    /// Click a deck card to drop it in the first empty slot.
    /// </summary>
    public class DragCard : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        public CardId card;
        public int fromSlot = -1;
        internal LoadoutMenu menu;

        public static bool Dragging => _held != null;
        static HeldCard _held;
        static DragCard _active;
        bool _dropped;

        public void OnBeginDrag(PointerEventData e)
        {
            Cancel();
            _active = this;
            _dropped = false;
            var canvas = GetComponentInParent<Canvas>().rootCanvas;
            var rt = (RectTransform)transform;
            _held = HeldCard.Create((RectTransform)canvas.transform, card, rt.rect.width, rt, e);
            // the spot it came from is empty while it's in your hand
            var cg = GetComponent<CanvasGroup>();
            if (cg == null) cg = gameObject.AddComponent<CanvasGroup>();
            cg.alpha = 0f;
        }

        public void OnDrag(PointerEventData e)
        {
            if (_held != null) _held.Follow(e);
        }

        public void OnEndDrag(PointerEventData e)
        {
            bool dropped = _dropped;
            var held = _held;
            _held = null;
            _active = null;
            var cg = GetComponent<CanvasGroup>();
            if (dropped)
            {
                if (held != null) Destroy(held.gameObject);
                return;
            }
            // dropped on nothing: it glides back to where it came from
            if (held != null) held.ReturnTo((RectTransform)transform, () => { if (cg != null) cg.alpha = 1f; });
            else if (cg != null) cg.alpha = 1f;
        }

        internal void MarkDropped() => _dropped = true;

        public void OnPointerClick(PointerEventData e)
        {
            if (e.dragging || fromSlot >= 0 || menu == null) return;
            menu.ClickDeckCard(card);
        }

        public static void Cancel()
        {
            if (_held != null) Destroy(_held.gameObject);
            _held = null;
            _active = null;
        }

        internal static DragCard Active => _active;
    }

    /// <summary>The card in your hand: springs after the pointer, tilts with its speed, casts a shadow.</summary>
    public class HeldCard : MonoBehaviour
    {
        RectTransform _rt, _card, _shadow, _canvas;
        Vector2 _target, _vel, _grab;
        float _tilt;
        RectTransform _home;
        Action _onHome;
        float _homeT = -1f;
        Vector2 _homeFrom;

        public static HeldCard Create(RectTransform canvas, CardId id, float width, RectTransform source, PointerEventData e)
        {
            var root = UIFactory.Rect("HeldCard", canvas);
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(width, width * CardView.Aspect);
            var c = root.gameObject.AddComponent<HeldCard>();
            c._rt = root;
            c._canvas = canvas;
            var sub = root.gameObject.AddComponent<Canvas>();
            sub.overrideSorting = true;
            sub.sortingOrder = 40;
            var cg = root.gameObject.AddComponent<CanvasGroup>();
            cg.blocksRaycasts = false; // so slots underneath get the drop
            // shadow, then the card
            var sh = UIFactory.Image("Shadow", root, new Color(0, 0, 0, 0.45f), CardView.Rounded);
            sh.type = Image.Type.Sliced;
            c._shadow = UIFactory.Stretch(sh.rectTransform);
            c._card = CardView.Build(root, id, width);
            UIFactory.Stretch(c._card);
            // start exactly where the card was, keeping the grab point under the pointer
            Vector2 start = c.ToCanvas(RectTransformUtility.WorldToScreenPoint(null, source.TransformPoint(source.rect.center)));
            c._target = c.ToCanvas(e.position);
            c._grab = start - c._target;
            root.anchoredPosition = start;
            return c;
        }

        Vector2 ToCanvas(Vector2 screen)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvas, screen, null, out var p);
            return p;
        }

        public void Follow(PointerEventData e) => _target = ToCanvas(e.position);

        public void ReturnTo(RectTransform home, Action done)
        {
            _home = home;
            _onHome = done;
            _homeT = 0f;
            _homeFrom = _rt.anchoredPosition;
        }

        void Update()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (_homeT >= 0f)
            {
                _homeT += dt / 0.18f;
                Vector2 to = _home != null ? ToCanvas(RectTransformUtility.WorldToScreenPoint(null, _home.TransformPoint(_home.rect.center))) : _homeFrom;
                float k = 1f - (1f - Mathf.Clamp01(_homeT)) * (1f - Mathf.Clamp01(_homeT));
                _rt.anchoredPosition = Vector2.Lerp(_homeFrom, to, k);
                _rt.localScale = Vector3.one * Mathf.Lerp(1.08f, 1f, k);
                _rt.localRotation = Quaternion.Slerp(_rt.localRotation, Quaternion.identity, k);
                _shadow.anchoredPosition = Vector2.Lerp(new Vector2(14, -18), Vector2.zero, k);
                if (_homeT >= 1f) { _onHome?.Invoke(); Destroy(gameObject); }
                return;
            }
            // a springy follow: it lags a touch and overshoots, so it feels like it has weight
            Vector2 goal = _target + _grab;
            Vector2 pos = _rt.anchoredPosition;
            Vector2 force = (goal - pos) * 420f - _vel * 32f;
            _vel += force * dt;
            pos += _vel * dt;
            _rt.anchoredPosition = pos;
            _grab = Vector2.Lerp(_grab, Vector2.zero, 1f - Mathf.Exp(-10f * dt)); // the card settles under the pointer
            // tilt into the motion, swing back when it stops
            float wantTilt = Mathf.Clamp(-_vel.x * 0.025f, -22f, 22f);
            _tilt = Mathf.Lerp(_tilt, wantTilt, 1f - Mathf.Exp(-14f * dt));
            _rt.localRotation = Quaternion.Euler(0, 0, _tilt);
            _rt.localScale = Vector3.one * Mathf.Lerp(_rt.localScale.x, 1.08f, 1f - Mathf.Exp(-16f * dt));
            _shadow.anchoredPosition = new Vector2(14, -18);
        }
    }

    /// <summary>An ability slot that accepts a dropped card.</summary>
    public class SlotDrop : MonoBehaviour, IDropHandler
    {
        public int slot;
        internal LoadoutMenu menu;

        public void OnDrop(PointerEventData e)
        {
            var d = DragCard.Active;
            if (d == null || menu == null) return;
            d.MarkDropped();
            DragCard.Cancel();
            menu.DropOnSlot(d, slot);
        }
    }

    /// <summary>The deck: dropping an equipped card here takes it off.</summary>
    public class DeckDrop : MonoBehaviour, IDropHandler
    {
        internal LoadoutMenu menu;

        public void OnDrop(PointerEventData e)
        {
            var d = DragCard.Active;
            if (d == null || menu == null) return;
            d.MarkDropped();
            DragCard.Cancel();
            menu.DropOnDeck(d);
        }
    }

    // ====================================================================== Alt to inspect

    /// <summary>Remembers which card the pointer is over (for the Alt close-up).</summary>
    public class CardHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public CardId card;
        public static CardId Hovered { get; private set; }
        static CardHover _over;

        /// <summary>The card under the pointer is somewhere inside <paramref name="root"/>.</summary>
        public static bool HoveredUnder(Transform root) => _over != null && _over.transform.IsChildOf(root);

        public void OnPointerEnter(PointerEventData e) { _over = this; Hovered = card; }

        public void OnPointerExit(PointerEventData e)
        {
            if (_over != this) return;
            _over = null;
            Hovered = CardId.None;
        }

        void OnDisable()
        {
            if (_over == this) { _over = null; Hovered = CardId.None; }
        }
    }

    /// <summary>
    /// Hold Alt over a card: a big copy of it in the middle of the screen, so you can read it properly.
    /// While a card is hovered (and not being inspected), "ALT to Inspect" shows at the bottom of the screen.
    /// </summary>
    public class CardInspector : MonoBehaviour
    {
        const float Width = 420f;
        RectTransform _shown;
        CardId _card;
        float _t;
        Text _hint;

        void Update()
        {
            var kb = Keyboard.current;
            bool alt = kb != null && (kb.leftAltKey.isPressed || kb.rightAltKey.isPressed);
            CardId want = alt && !DragCard.Dragging ? CardHover.Hovered : CardId.None;

            // only the screen the hovered card is on shows the hint (the shop sits over the loadout screen)
            bool hint = want == CardId.None && !DragCard.Dragging && CardHover.Hovered != CardId.None && CardHover.HoveredUnder(transform);
            if (hint && _hint == null) _hint = MakeHint();
            if (_hint != null && _hint.gameObject.activeSelf != hint) _hint.gameObject.SetActive(hint);
            if (want != _card)
            {
                if (_shown != null) Destroy(_shown.gameObject);
                _shown = null;
                _card = want;
                _t = 0f;
                if (want != CardId.None)
                {
                    var holder = UIFactory.Rect("Inspect", transform);
                    holder.anchorMin = holder.anchorMax = new Vector2(0.5f, 0.5f);
                    holder.sizeDelta = new Vector2(Width, Width * CardView.Aspect);
                    var sub = holder.gameObject.AddComponent<Canvas>();
                    sub.overrideSorting = true;
                    sub.sortingOrder = 50;
                    holder.gameObject.AddComponent<CanvasGroup>().blocksRaycasts = false;
                    var sh = UIFactory.Image("Shadow", holder, new Color(0, 0, 0, 0.5f), CardView.Rounded);
                    sh.type = Image.Type.Sliced;
                    UIFactory.Stretch(sh.rectTransform);
                    sh.rectTransform.anchoredPosition = new Vector2(16, -20);
                    UIFactory.Stretch(CardView.Build(holder, want, Width));
                    _shown = holder;
                }
            }
            if (_shown != null)
            {
                _t += Time.unscaledDeltaTime;
                float k = 1f - Mathf.Pow(1f - Mathf.Clamp01(_t / 0.12f), 3f);
                _shown.localScale = Vector3.one * Mathf.Lerp(0.85f, 1f, k);
            }
        }

        Text MakeHint()
        {
            var t = UIFactory.Text("AltHint", transform, "ALT to Inspect", 26, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            t.raycastTarget = false;
            var o = t.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(0.05f, 0.03f, 0.08f, 1f);
            o.effectDistance = new Vector2(2f, -2f);
            UIFactory.Place(t.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 14), new Vector2(400, 40));
            var sub = t.gameObject.AddComponent<Canvas>();
            sub.overrideSorting = true;
            sub.sortingOrder = 50;
            return t;
        }
    }
}
