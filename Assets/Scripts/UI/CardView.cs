using UnityEngine;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Draws a card: element-coloured face; top left the element, then the name; top right the cost
    /// (bubble = mana, heart = health) with the amount; the symbol in the middle; the description underneath.
    /// All text black. Built from code at any size (height = 1.4 × width).
    /// </summary>
    public static class CardView
    {
        public const float Aspect = 1.4f;
        static Sprite _rounded, _roundedSmall;

        /// <summary>9-sliced rounded rectangle (white), for cards and slots.</summary>
        public static Sprite Rounded => _rounded ??= MakeRounded(64, 14);
        public static Sprite RoundedSmall => _roundedSmall ??= MakeRounded(32, 6);

        public static RectTransform Build(Transform parent, CardId id, float width)
        {
            var info = Cards.Get(id);
            float s = width / 200f; // sizes below are for a 200 px wide card
            var root = UIFactory.Rect("Card " + (info != null ? info.name : "?"), parent);
            root.sizeDelta = new Vector2(width, width * Aspect);

            // black edge, then the element-coloured face
            var edge = UIFactory.Image("Edge", root, Color.black, Rounded);
            edge.type = Image.Type.Sliced;
            UIFactory.Stretch(edge.rectTransform);
            var face = UIFactory.Image("Face", root, info != null ? Cards.ElementColor(info.element) : Color.gray, Rounded);
            face.type = Image.Type.Sliced;
            UIFactory.Stretch(face.rectTransform, 3 * s, 3 * s, 3 * s, 3 * s);
            if (info == null) return root;

            // element (top left)
            var el = UIFactory.Text("Element", face.rectTransform, Cards.ElementName(info.element), Mathf.RoundToInt(15 * s), TextAnchor.UpperLeft, Color.black, FontStyle.Bold);
            UIFactory.Place(el.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(12 * s, -10 * s), new Vector2(130 * s, 20 * s));

            // name (under the element)
            var name = UIFactory.Text("Name", face.rectTransform, info.name, Mathf.RoundToInt(21 * s), TextAnchor.UpperLeft, Color.black, FontStyle.Bold);
            name.horizontalOverflow = HorizontalWrapMode.Wrap;
            name.verticalOverflow = VerticalWrapMode.Truncate;
            name.resizeTextForBestFit = true;
            name.resizeTextMinSize = Mathf.Max(6, Mathf.RoundToInt(12 * s));
            name.resizeTextMaxSize = Mathf.RoundToInt(21 * s);
            UIFactory.Place(name.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(12 * s, -30 * s), new Vector2(135 * s, 50 * s));

            // cost (top right)
            var cost = UIFactory.Rect("Cost", face.rectTransform);
            UIFactory.Place(cost, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-8 * s, -8 * s), new Vector2(42 * s, 42 * s));
            var icon = UIFactory.Rect("Icon", cost).gameObject.AddComponent<RawImage>();
            icon.texture = CardSymbols.CostIcon(info.costType);
            icon.raycastTarget = false;
            UIFactory.Stretch(icon.rectTransform);
            var num = UIFactory.Text("Amount", cost, info.cost.ToString(), Mathf.RoundToInt(20 * s), TextAnchor.MiddleCenter, Color.black, FontStyle.Bold);
            UIFactory.Stretch(num.rectTransform, 0, info.costType == CostType.Health ? 4 * s : 0, 0, 0);

            // symbol (middle)
            // a fixed frame between the name (ends ~82 px down) and the description (starts ~200 px down);
            // the picture is scaled down to fit inside it, keeping its shape
            var frame = UIFactory.Rect("SymbolFrame", face.rectTransform);
            UIFactory.Place(frame, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -90 * s), new Vector2(150 * s, 104 * s));
            var tex = CardSymbols.Get(id);
            var sym = UIFactory.Rect("Symbol", frame).gameObject.AddComponent<RawImage>();
            sym.texture = tex;
            sym.raycastTarget = false;
            float texAspect = tex != null && tex.height > 0 ? tex.width / (float)tex.height : 1f;
            float fw = 150 * s, fh = 104 * s;
            Vector2 fit = fw / fh > texAspect ? new Vector2(fh * texAspect, fh) : new Vector2(fw, fw / texAspect);
            UIFactory.Place(sym.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, fit);

            // description (bottom)
            var desc = UIFactory.Text("Description", face.rectTransform, info.description, Mathf.RoundToInt(15 * s), TextAnchor.UpperCenter, Color.black);
            desc.horizontalOverflow = HorizontalWrapMode.Wrap;
            desc.verticalOverflow = VerticalWrapMode.Truncate;
            desc.resizeTextForBestFit = true;
            desc.resizeTextMinSize = Mathf.Max(6, Mathf.RoundToInt(10 * s));
            desc.resizeTextMaxSize = Mathf.RoundToInt(15 * s);
            UIFactory.Place(desc.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 12 * s), new Vector2(172 * s, 62 * s));
            return root;
        }

        static Sprite MakeRounded(int size, int radius)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                float a = Mathf.Clamp01(radius - d + 0.5f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(radius, radius, radius, radius));
        }
    }
}
