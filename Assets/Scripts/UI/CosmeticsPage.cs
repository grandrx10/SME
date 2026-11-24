using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The dress-up page of the inventory: your character on the left, every cosmetic you own on the right. Click one to put
    /// it on (again to take it off). Drag it over the body to move it: it slides over the surface and sticks to whatever part
    /// it's on. Drag anywhere else to look round, wheel to zoom. Under the picture, for the chosen one: turn it three ways,
    /// move it along three axes, take it off. ↶ undoes one step at a time (a drag, a slider, putting on, taking off).
    /// </summary>
    public class CosmeticsPage : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler, IScrollHandler, IInitializePotentialDragHandler
    {
        const float Pad = 28f, PicW = 540f, PicH = PicW * CosmeticStage.TexHeight / CosmeticStage.TexWidth;
        const float Tile = 128f, TileGap = 12f, MoveRange = 0.3f;
        const int MostSteps = 100;
        const int Columns = 3;
        static readonly Color TileCol = new(0.13f, 0.145f, 0.18f, 1f);

        class Cell
        {
            public CosmeticItem item;
            public Image back, edge;
        }

        readonly List<Cell> _cells = new();
        readonly Slider[] _sliders = new Slider[6]; // three turns, then three moves
        /// <summary>What was worn before each step, and which one the step was about.</summary>
        readonly List<(int uid, List<WornItem> worn)> _steps = new();
        bool _stepped; // this drag (or this hold of a slider) has its step already
        Button _undo;
        RectTransform _pic, _tools;
        Text _name, _count;
        float _full;
        int _uid;

        // the chosen one as it was when it was chosen (or last dragged): the sliders work from there
        Vector3 _basePos;
        Quaternion _baseRot;

        // a drag
        bool _moving, _panning;
        int _finger = int.MinValue;
        Vector2 _grab;
        CosmeticStage.Spot _from;
        Vector3 _offset;
        Quaternion _startRot;

        public static CosmeticsPage Build(RectTransform page, Vector2 window, Action openShop)
        {
            var pic = UIFactory.Image("Dresser", page, new Color(0.11f, 0.12f, 0.15f, 1f), CardView.Rounded);
            pic.type = Image.Type.Sliced;
            UIFactory.Place(pic.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(Pad, -Pad), new Vector2(PicW, PicH));
            var me = pic.gameObject.AddComponent<CosmeticsPage>();
            me._pic = pic.rectTransform;
            var tex = CosmeticStage.Get();
            if (tex != null)
            {
                var img = UIFactory.Rect("Picture", pic.transform).gameObject.AddComponent<RawImage>();
                img.texture = tex;
                img.raycastTarget = false;
                UIFactory.Stretch(img.rectTransform);
            }
            CosmeticStage.ResetView();
            me.BuildTools(page);
            me.ShowUndo();
            me.BuildGrid(page, window, openShop);
            me.Choose(0);
            return me;
        }

        // ------------------------------------------------------------------ layout

        void BuildTools(RectTransform page)
        {
            _name = UIFactory.Text("Name", page, "", 28, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            UIFactory.Place(_name.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(Pad, -Pad - PicH - 8), new Vector2(PicW, 40));

            _tools = UIFactory.Rect("Tools", page);
            UIFactory.Place(_tools, new Vector2(0, 1), new Vector2(0, 1), new Vector2(Pad, -Pad - PicH - 54), new Vector2(PicW, 146));
            var colours = new[] { new Color(1f, 0.4f, 0.36f), new Color(0.45f, 0.9f, 0.45f), new Color(0.42f, 0.66f, 1f), UIFactory.TextCol };
            // turns on the left, moves on the right
            const float column = (PicW - 88f) / 2f;
            for (int i = 0; i < _sliders.Length; i++)
            {
                float x = i / 3 * column, y = -(i % 3) * 48f;
                var icon = UIFactory.Text("Icon", _tools, i < 3 ? "↻" : "XYZ".Substring(i - 3, 1), 26, TextAnchor.MiddleCenter, colours[i % 3], FontStyle.Bold);
                UIFactory.Place(icon.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(x, y - 4), new Vector2(34, 34));
                _sliders[i] = Bar(_tools, new Vector2(x + 38, y - 8), new Vector2(column - 38 - 14, 26), colours[i % 3], i < 3 ? 180f : MoveRange);
                _sliders[i].onValueChanged.AddListener(_ => Slid());
                _sliders[i].gameObject.AddComponent<SliderUp>().onUp = EndStep;
            }
            var off = Round(_tools, "✕", () => { if (_uid != 0) TakeOff(_uid); }, 32);
            UIFactory.Place((RectTransform)off.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(0, -76), new Vector2(66, 66));
            // (always there: taking something off can be undone too)
            _undo = Round(page, "↶", Undo, 38);
            UIFactory.Place((RectTransform)_undo.transform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(Pad + PicW, -Pad - PicH - 54), new Vector2(66, 66));
        }

        // ------------------------------------------------------------------ undo

        /// <summary>About to change something: remember how it was. Once a drag (or a hold of a slider).</summary>
        void Step(int uid)
        {
            if (_stepped) return;
            _steps.Add((uid, Wardrobe.Snapshot()));
            if (_steps.Count > MostSteps) _steps.RemoveAt(0);
            ShowUndo();
        }

        void EndStep()
        {
            _stepped = false;
            Wardrobe.Flush();
        }

        void Undo()
        {
            if (_steps.Count == 0) return;
            var (uid, worn) = _steps[_steps.Count - 1];
            _steps.RemoveAt(_steps.Count - 1);
            Wardrobe.Restore(worn);
            CosmeticStage.Refresh();
            Choose(uid);
            ShowUndo();
        }

        void ShowUndo() => UIFactory.Label(_undo).color = _steps.Count > 0 ? UIFactory.TextCol : new Color(1f, 1f, 1f, 0.2f);

        void BuildGrid(RectTransform page, Vector2 window, Action openShop)
        {
            float x = Pad + PicW + 24f, w = window.x - x - Pad, top = Pad + 60f + 16f;
            _count = UIFactory.Text("Worn", page, "", 24, TextAnchor.MiddleLeft, UIFactory.Dim, FontStyle.Bold);
            UIFactory.Place(_count.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(x + 4, -Pad - 8), new Vector2(160, 44));

            var area = UIFactory.Image("Wardrobe", page, new Color(0, 0, 0, 0.22f), CardView.Rounded);
            area.type = Image.Type.Sliced;
            UIFactory.Place(area.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(x, -top), new Vector2(w, window.y - top - Pad));

            if (Wardrobe.Items.Count == 0)
            {
                // nothing yet: the way to the crates
                var go = UIFactory.Rect("ToShop", area.transform);
                UIFactory.Place(go, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(200, 200));
                var img = go.gameObject.AddComponent<RawImage>();
                img.texture = ShopArt.Crate;
                go.gameObject.AddComponent<ShopClick>().onClick = () => openShop?.Invoke();
                return;
            }

            var scroll = UIFactory.Stretch(UIFactory.Rect("Scroll", area.transform), 10, 10, 4, 10);
            var content = UIFactory.ScrollList(scroll, 0);
            DestroyImmediate(content.GetComponent<VerticalLayoutGroup>());
            var grid = content.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(Tile, Tile);
            grid.spacing = new Vector2(TileGap, TileGap);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = Columns;
            // newest first
            for (int i = Wardrobe.Items.Count - 1; i >= 0; i--) AddCell(content, Wardrobe.Items[i]);
        }

        void AddCell(RectTransform parent, CosmeticItem item)
        {
            var edge = UIFactory.Image("Cell", parent, Color.white, CardView.Rounded);
            edge.type = Image.Type.Sliced;
            var back = UIFactory.Image("Back", edge.transform, TileCol, CardView.Rounded);
            back.type = Image.Type.Sliced;
            back.raycastTarget = false;
            UIFactory.Stretch(back.rectTransform, 4, 4, 4, 4);
            Thumb(back.rectTransform, item, Tile - 14f);
            var cell = new Cell { item = item, back = back, edge = edge };
            _cells.Add(cell);
            var click = edge.gameObject.AddComponent<CellClick>();
            click.onClick = () => Click(cell);
            click.onHover = on => ShowName(on ? cell.item : Wardrobe.Find(_uid));
        }

        /// <summary>A cosmetic's picture: bigger the bigger it rolled, with an arrow if it's large.</summary>
        public static void Thumb(RectTransform parent, CosmeticItem item, float size)
        {
            var img = UIFactory.Rect("Thumb", parent).gameObject.AddComponent<RawImage>();
            img.texture = CosmeticIcons.Get(item.id, item.colour);
            img.raycastTarget = false;
            float s = size * Mathf.Lerp(0.82f, 1f, (item.Scale - 1f) * 2f);
            UIFactory.Place(img.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(s, s));
            if (item.size <= Cosmetics.Regular) return;
            var t = UIFactory.Text("Size", parent, "▲", Mathf.RoundToInt(size * 0.17f), TextAnchor.MiddleCenter, new Color(0.55f, 0.95f, 0.5f));
            UIFactory.Place(t.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-3, -3), new Vector2(size * 0.24f, size * 0.24f));
            var o = t.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(0.05f, 0.03f, 0.08f, 1f);
            o.effectDistance = new Vector2(1.5f, -1.5f);
        }

        static Button Round(Transform parent, string label, UnityEngine.Events.UnityAction onClick, int size)
        {
            var b = UIFactory.Button("Button", parent, label, onClick, size);
            var img = (Image)b.targetGraphic;
            img.sprite = CardView.Rounded;
            img.type = Image.Type.Sliced;
            img.color = new Color(0.17f, 0.19f, 0.24f, 1f);
            return b;
        }

        /// <summary>A slider with nothing in the middle (0) and <paramref name="range"/> either way.</summary>
        static Slider Bar(RectTransform parent, Vector2 pos, Vector2 size, Color colour, float range)
        {
            var root = UIFactory.Rect("Slider", parent);
            UIFactory.Place(root, new Vector2(0, 1), new Vector2(0, 1), pos, size);
            var hit = root.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            var track = UIFactory.Image("Track", root, new Color(1, 1, 1, 0.12f), CardView.Rounded);
            track.type = Image.Type.Sliced;
            track.raycastTarget = false;
            track.rectTransform.anchorMin = new Vector2(0, 0.5f);
            track.rectTransform.anchorMax = new Vector2(1, 0.5f);
            track.rectTransform.sizeDelta = new Vector2(0, 8);
            var mid = UIFactory.Image("Middle", root, new Color(1, 1, 1, 0.3f));
            mid.raycastTarget = false;
            UIFactory.Place(mid.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(3, 18));
            var area = UIFactory.Stretch(UIFactory.Rect("Area", root), 11, 0, 11, 0);
            var handle = UIFactory.Image("Handle", area, colour, CardView.Rounded);
            handle.type = Image.Type.Sliced;
            handle.rectTransform.sizeDelta = new Vector2(22, 0);
            var s = root.gameObject.AddComponent<Slider>();
            s.handleRect = handle.rectTransform;
            s.targetGraphic = handle;
            s.minValue = -range;
            s.maxValue = range;
            s.navigation = new Navigation { mode = Navigation.Mode.None }; // (the movement keys don't slide it)
            s.SetValueWithoutNotify(0f);
            return s;
        }

        // ------------------------------------------------------------------ choosing

        void Click(Cell c)
        {
            int uid = c.item.uid;
            if (Wardrobe.FindWorn(uid) == null)
            {
                if (Wardrobe.Worn.Count >= Wardrobe.MaxWorn) { _full = 0.6f; return; } // wearing as many as you can
                Step(uid);
                Wardrobe.PutOn(uid);
                CosmeticStage.Refresh();
                Choose(uid);
            }
            else if (uid == _uid) TakeOff(uid);
            else Choose(uid);
        }

        void TakeOff(int uid)
        {
            Step(uid);
            Wardrobe.TakeOff(uid);
            CosmeticStage.Refresh();
            Choose(0);
        }

        void Choose(int uid)
        {
            _uid = Wardrobe.FindWorn(uid) != null ? uid : 0;
            _tools.gameObject.SetActive(_uid != 0);
            ShowName(Wardrobe.Find(_uid));
            Rebase();
            foreach (var c in _cells)
            {
                bool worn = Wardrobe.FindWorn(c.item.uid) != null;
                Color rarity = Cards.RarityColor(c.item.Info.rarity);
                c.edge.color = c.item.uid == _uid ? UIFactory.Accent : worn ? Color.white : rarity;
                c.back.color = worn ? Color.Lerp(TileCol, rarity, 0.45f) : Color.Lerp(TileCol, rarity, 0.12f);
            }
            _count.text = Wardrobe.Worn.Count + " / " + Wardrobe.MaxWorn;
        }

        void ShowName(CosmeticItem item)
        {
            _name.text = item != null ? item.Name : "";
            if (item != null) _name.color = Color.Lerp(Cards.RarityColor(item.Info.rarity), Color.white, 0.35f);
        }

        /// <summary>The sliders start again from how it is now.</summary>
        void Rebase()
        {
            foreach (var s in _sliders) s.SetValueWithoutNotify(0f);
            var w = Wardrobe.FindWorn(_uid);
            if (w == null) return;
            _basePos = w.pos;
            _baseRot = w.rot;
        }

        void Slid()
        {
            var w = Wardrobe.FindWorn(_uid);
            var bone = w != null ? CosmeticStage.Bone(CosmeticLook.Bones[w.bone]) : null;
            if (bone == null) return;
            Step(_uid);
            _stepped = true;
            w.rot = _baseRot * Quaternion.Euler(_sliders[0].value, _sliders[1].value, _sliders[2].value);
            Vector3 move = CosmeticStage.Across(new Vector3(_sliders[3].value, _sliders[4].value, _sliders[5].value));
            w.pos = Vector3.ClampMagnitude(_basePos + bone.InverseTransformVector(move), OutfitPiece.Reach);
            CosmeticStage.Move(w);
            Wardrobe.Moved();
        }

        // ------------------------------------------------------------------ the picture

        bool PointIn(PointerEventData e, out Vector2 uv)
        {
            uv = default;
            if (!CosmeticStage.Ready || !RectTransformUtility.ScreenPointToLocalPointInRectangle(_pic, e.position, e.pressEventCamera, out var p)) return false;
            var r = _pic.rect;
            uv = new Vector2((p.x - r.xMin) / r.width, (p.y - r.yMin) / r.height);
            return true;
        }

        public void OnInitializePotentialDrag(PointerEventData e) => e.useDragThreshold = false;

        public void OnPointerDown(PointerEventData e)
        {
            if (_finger != int.MinValue || !PointIn(e, out var uv)) return;
            _finger = e.pointerId;
            _moving = false;
            _panning = e.button == PointerEventData.InputButton.Middle;
            if (e.button != PointerEventData.InputButton.Left) return;
            int uid = CosmeticStage.Pick(CosmeticStage.RayAt(uv));
            var w = Wardrobe.FindWorn(uid);
            var bone = w != null ? CosmeticStage.Bone(CosmeticLook.Bones[w.bone]) : null;
            if (bone == null) return;
            Choose(uid);
            _moving = true;
            Vector3 at = bone.TransformPoint(w.pos);
            _from = CosmeticStage.Nearest(at);
            _offset = at - _from.point;
            _startRot = bone.rotation * w.rot;
            _grab = uv - CosmeticStage.ToPicture(_from.point);
        }

        public void OnDrag(PointerEventData e)
        {
            if (e.pointerId != _finger) return;
            if (!_moving)
            {
                float k = PicH / Mathf.Max(1f, ScreenHeight());
                if (_panning) CosmeticStage.Pan(e.delta.y * k, PicH);
                else CosmeticStage.Orbit(e.delta * k);
                return;
            }
            var w = Wardrobe.FindWorn(_uid);
            if (w == null || !PointIn(e, out var uv)) return;
            var ray = CosmeticStage.RayAt(uv - _grab);
            // off the body: the nearest spot to where the ray passes it
            if (!CosmeticStage.OnBody(ray, out var spot)) spot = CosmeticStage.Nearest(ray.GetPoint(Vector3.Dot(_from.point - ray.origin, ray.direction)));
            var bone = CosmeticStage.Bone(spot.bone);
            int index = Array.IndexOf(CosmeticLook.Bones, spot.bone);
            if (bone == null || index < 0) return;
            Step(_uid);
            _stepped = true;
            // it turns with the surface, keeping how it sat on it
            var turn = Quaternion.FromToRotation(_from.normal, spot.normal);
            w.bone = (byte)index;
            w.pos = Vector3.ClampMagnitude(bone.InverseTransformPoint(spot.point + turn * _offset), OutfitPiece.Reach);
            w.rot = Quaternion.Inverse(bone.rotation) * turn * _startRot;
            CosmeticStage.Move(w);
            Wardrobe.Moved();
        }

        float ScreenHeight()
        {
            var corners = new Vector3[4];
            _pic.GetWorldCorners(corners);
            return Mathf.Abs(corners[1].y - corners[0].y);
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (e.pointerId != _finger) return;
            _finger = int.MinValue;
            if (_moving) Rebase();
            _moving = false;
            EndStep();
        }

        public void OnScroll(PointerEventData e) => CosmeticStage.Zoom(Mathf.Clamp(e.scrollDelta.y, -3f, 3f));

        void Update()
        {
            // "full": the count flashes red
            _full = Mathf.MoveTowards(_full, 0f, Time.unscaledDeltaTime);
            if (_count != null)
            {
                _count.color = _full > 0f ? new Color(1f, 0.25f, 0.2f) : UIFactory.Dim;
                _count.transform.localScale = Vector3.one * (1f + 0.25f * _full);
            }
        }

        void OnDisable()
        {
            _finger = int.MinValue;
            _moving = false;
            EndStep();
        }
    }

    /// <summary>A slider let go of.</summary>
    public class SliderUp : MonoBehaviour, IPointerUpHandler
    {
        public Action onUp;
        public void OnPointerUp(PointerEventData e) => onUp?.Invoke();
    }

    /// <summary>Clicks and hover on a wardrobe tile; drags and the wheel go on to the list it's in.</summary>
    public class CellClick : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public Action onClick;
        public Action<bool> onHover;

        public void OnPointerClick(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Left && !e.dragging) onClick?.Invoke();
        }

        public void OnPointerEnter(PointerEventData e) => onHover?.Invoke(true);
        public void OnPointerExit(PointerEventData e) => onHover?.Invoke(false);
    }
}
