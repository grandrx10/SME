using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>Small helper for building uGUI from code (no prefabs / TMP import needed).</summary>
    public static class UIFactory
    {
        public static readonly Color PanelCol = new(0.06f, 0.07f, 0.09f, 0.92f);
        public static readonly Color ButtonCol = new(0.16f, 0.18f, 0.22f, 1f);
        public static readonly Color ButtonHoverCol = new(0.26f, 0.29f, 0.35f, 1f);
        public static readonly Color Accent = new(1f, 0.62f, 0.2f, 1f);
        public static readonly Color TextCol = new(0.93f, 0.94f, 0.96f, 1f);
        public static readonly Color Dim = new(0.6f, 0.63f, 0.68f, 1f);

        static Font _font;
        static Sprite _white;

        public static Font Font => _font ??= Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        public static Sprite White
        {
            get
            {
                if (_white == null)
                {
                    var t = Texture2D.whiteTexture;
                    _white = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f));
                }
                return _white;
            }
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static RectTransform Stretch(RectTransform r, float l = 0, float b = 0, float rr = 0, float t = 0)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(l, b);
            r.offsetMax = new Vector2(-rr, -t);
            return r;
        }

        public static RectTransform Place(RectTransform r, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            r.anchorMin = r.anchorMax = anchor;
            r.pivot = pivot;
            r.anchoredPosition = pos;
            r.sizeDelta = size;
            return r;
        }

        public static Image Image(string name, Transform parent, Color c, Sprite s = null)
        {
            var r = Rect(name, parent);
            var img = r.gameObject.AddComponent<Image>();
            img.sprite = s ?? White;
            img.color = c;
            return img;
        }

        public static Text Text(string name, Transform parent, string text, int size, TextAnchor align = TextAnchor.MiddleCenter, Color? color = null, FontStyle style = FontStyle.Normal)
        {
            var r = Rect(name, parent);
            var t = r.gameObject.AddComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.color = color ?? TextCol;
            t.fontStyle = style;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        public static Button Button(string name, Transform parent, string label, UnityAction onClick, int fontSize = 26)
        {
            var img = Image(name, parent, ButtonCol);
            var b = img.gameObject.AddComponent<Button>();
            var colors = b.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.5f, 1.5f, 1.5f, 1);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1);
            colors.selectedColor = Color.white;
            colors.colorMultiplier = 1.4f;
            b.colors = colors;
            b.targetGraphic = img;
            if (onClick != null) b.onClick.AddListener(onClick);
            var t = Text("Label", img.transform, label, fontSize);
            Stretch(t.rectTransform);
            return b;
        }

        public static Text Label(Button b) => b.GetComponentInChildren<Text>();

        public static VerticalLayoutGroup VList(RectTransform r, float spacing, int padding = 0)
        {
            var v = r.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing;
            v.padding = new RectOffset(padding, padding, padding, padding);
            v.childControlHeight = true;
            v.childControlWidth = true;
            v.childForceExpandHeight = false;
            v.childForceExpandWidth = true;
            return v;
        }

        public static LayoutElement Size(Component c, float height, float width = -1)
        {
            var le = c.gameObject.GetComponent<LayoutElement>();
            if (le == null) le = c.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
            if (width > 0) le.preferredWidth = width;
            return le;
        }

        /// <summary>A labelled row: "Label ............ [control]".</summary>
        public static RectTransform Row(Transform parent, string label, out RectTransform controlArea)
        {
            var row = Rect(label + "Row", parent);
            Size(row, 46);
            var l = Text("Label", row, label, 22, TextAnchor.MiddleLeft);
            l.rectTransform.anchorMin = new Vector2(0, 0);
            l.rectTransform.anchorMax = new Vector2(0.45f, 1);
            l.rectTransform.offsetMin = new Vector2(10, 0);
            l.rectTransform.offsetMax = Vector2.zero;
            controlArea = Rect("Control", row);
            controlArea.anchorMin = new Vector2(0.45f, 0.1f);
            controlArea.anchorMax = new Vector2(1, 0.9f);
            controlArea.offsetMin = controlArea.offsetMax = Vector2.zero;
            return row;
        }

        /// <summary>"&lt; value &gt;" option cycler.</summary>
        public static void Cycler(Transform parent, string label, Func<string[]> options, Func<int> get, Action<int> set)
        {
            Row(parent, label, out var area);
            Text value = null;
            void Refresh()
            {
                var o = options();
                int i = Mathf.Clamp(get(), 0, Mathf.Max(0, o.Length - 1));
                value.text = o.Length > 0 ? o[i] : "-";
            }
            void Step(int d)
            {
                var o = options();
                if (o.Length == 0) return;
                set((get() + d + o.Length) % o.Length);
                Refresh();
            }
            var left = Button("Prev", area, "<", () => Step(-1));
            Place((RectTransform)left.transform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), Vector2.zero, new Vector2(40, 36));
            var right = Button("Next", area, ">", () => Step(1));
            Place((RectTransform)right.transform, new Vector2(1, 0.5f), new Vector2(1, 0.5f), Vector2.zero, new Vector2(40, 36));
            value = Text("Value", area, "", 22);
            Stretch(value.rectTransform, 44, 0, 44, 0);
            Refresh();
        }

        public static Slider Slider(Transform parent, string label, float min, float max, float value, Action<float> onChange, Func<float, string> fmt)
        {
            Row(parent, label, out var area);
            var root = Rect("Slider", area);
            Stretch(root, 0, 0, 70, 0);
            var bg = Image("Background", root, new Color(0.2f, 0.22f, 0.26f, 1));
            var bgr = bg.rectTransform;
            bgr.anchorMin = new Vector2(0, 0.4f); bgr.anchorMax = new Vector2(1, 0.6f); bgr.offsetMin = bgr.offsetMax = Vector2.zero;
            var fillArea = Rect("Fill Area", root);
            fillArea.anchorMin = new Vector2(0, 0.4f); fillArea.anchorMax = new Vector2(1, 0.6f); fillArea.offsetMin = fillArea.offsetMax = Vector2.zero;
            var fill = Image("Fill", fillArea, Accent);
            fill.rectTransform.sizeDelta = Vector2.zero;
            var handleArea = Rect("Handle Area", root);
            Stretch(handleArea, 8, 0, 8, 0);
            var handle = Image("Handle", handleArea, Color.white);
            handle.rectTransform.sizeDelta = new Vector2(16, 0);
            var s = root.gameObject.AddComponent<Slider>();
            s.fillRect = fill.rectTransform;
            s.handleRect = handle.rectTransform;
            s.targetGraphic = handle;
            s.minValue = min;
            s.maxValue = max;
            s.value = value;
            var vt = Text("Value", area, fmt(value), 20, TextAnchor.MiddleRight, Dim);
            vt.rectTransform.anchorMin = new Vector2(1, 0); vt.rectTransform.anchorMax = new Vector2(1, 1);
            vt.rectTransform.pivot = new Vector2(1, 0.5f);
            vt.rectTransform.sizeDelta = new Vector2(64, 0);
            vt.rectTransform.anchoredPosition = Vector2.zero;
            s.onValueChanged.AddListener(v => { vt.text = fmt(v); onChange(v); });
            return s;
        }

        public static InputField Input(Transform parent, string placeholder, string value, int charLimit, Action<string> onEndEdit, int fontSize = 24)
        {
            var bg = Image("Input", parent, new Color(0.12f, 0.13f, 0.16f, 0.95f));
            var field = bg.gameObject.AddComponent<InputField>();
            var text = Text("Text", bg.transform, "", fontSize, TextAnchor.MiddleLeft);
            Stretch(text.rectTransform, 12, 0, 12, 0);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            var ph = Text("Placeholder", bg.transform, placeholder, fontSize, TextAnchor.MiddleLeft, Dim, FontStyle.Italic);
            Stretch(ph.rectTransform, 12, 0, 12, 0);
            field.textComponent = text;
            field.placeholder = ph;
            field.targetGraphic = bg;
            field.characterLimit = charLimit;
            field.lineType = InputField.LineType.SingleLine;
            field.text = value;
            if (onEndEdit != null) field.onEndEdit.AddListener(v => onEndEdit(v));
            return field;
        }

        /// <summary>
        /// Turns an already-placed rect into a vertically scrolling list and returns the content rect
        /// (a VList — add rows to it like any other list). Mouse wheel and a thin scrollbar both scroll it.
        /// </summary>
        public static RectTransform ScrollList(RectTransform root, float spacing)
        {
            const float barWidth = 8f;
            var viewport = Rect("Viewport", root);
            Stretch(viewport, 0, 0, barWidth + 8, 0);
            viewport.gameObject.AddComponent<RectMask2D>();
            viewport.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0); // catches the wheel over gaps

            var content = Rect("Content", viewport);
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.sizeDelta = Vector2.zero;
            content.anchoredPosition = Vector2.zero;
            VList(content, spacing);
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var track = Image("Scrollbar", root, new Color(1, 1, 1, 0.06f));
            var tr = track.rectTransform;
            tr.anchorMin = new Vector2(1, 0);
            tr.anchorMax = new Vector2(1, 1);
            tr.pivot = new Vector2(1, 0.5f);
            tr.sizeDelta = new Vector2(barWidth, 0);
            tr.anchoredPosition = Vector2.zero;
            var slideArea = Stretch(Rect("Sliding Area", tr));
            var handle = Image("Handle", slideArea, new Color(1, 1, 1, 0.35f));
            handle.rectTransform.sizeDelta = Vector2.zero;
            var bar = track.gameObject.AddComponent<Scrollbar>();
            bar.handleRect = handle.rectTransform;
            bar.targetGraphic = handle;
            bar.direction = Scrollbar.Direction.BottomToTop;

            var scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            scroll.verticalScrollbar = bar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            return content;
        }

        public static void Toggle(Transform parent, string label, Func<bool> get, Action<bool> set) =>
            Cycler(parent, label, () => new[] { "Off", "On" }, () => get() ? 1 : 0, i => set(i == 1));
    }
}
