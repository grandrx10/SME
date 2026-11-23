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

            // black edge, the rarity-coloured border, a black line, then the element-coloured face
            var edge = UIFactory.Image("Edge", root, Color.black, Rounded);
            edge.type = Image.Type.Sliced;
            UIFactory.Stretch(edge.rectTransform);
            Color rarity = info != null ? Cards.RarityColor(info.rarity) : Color.gray;
            var band = UIFactory.Image("Rarity", root, rarity, Rounded);
            band.type = Image.Type.Sliced;
            UIFactory.Stretch(band.rectTransform, 2 * s, 2 * s, 2 * s, 2 * s);
            bool secret = info != null && info.rarity == Rarity.Secret; // a black border (with a white line inside it), white lettering
            var line = UIFactory.Image("Line", root, secret ? Color.white : Color.black, Rounded);
            line.type = Image.Type.Sliced;
            UIFactory.Stretch(line.rectTransform, 8 * s, 8 * s, 8 * s, 8 * s);
            var face = UIFactory.Image("Face", root, info != null ? Cards.ElementColor(info.element) : Color.gray, Rounded);
            face.type = Image.Type.Sliced;
            UIFactory.Stretch(face.rectTransform, 10 * s, 10 * s, 10 * s, 10 * s);
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

            // cost (in the very top-right corner, over the border, clear of the name)
            var cost = UIFactory.Rect("Cost", root);
            UIFactory.Place(cost, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-2 * s, -2 * s), new Vector2(42 * s, 42 * s));
            var icon = UIFactory.Rect("Icon", cost).gameObject.AddComponent<RawImage>();
            icon.texture = CardSymbols.CostIcon(info.costType);
            icon.raycastTarget = false;
            UIFactory.Stretch(icon.rectTransform);
            var num = UIFactory.Text("Amount", cost, info.costType == CostType.Passive ? "P" : info.cost.ToString(), Mathf.RoundToInt(20 * s), TextAnchor.MiddleCenter, Color.black, FontStyle.Bold);
            UIFactory.Stretch(num.rectTransform, 0, info.costType == CostType.Health ? 4 * s : 0, 0, 0);

            // symbol (middle)
            // a fixed frame between the name and the description; the picture is scaled down to fit inside it,
            // keeping its shape, and sits at the top of the frame (close under the name)
            var nameSet = name.GetGenerationSettings(new Vector2(135 * s, 0));
            nameSet.resizeTextForBestFit = false;
            float ppu = Mathf.Max(0.01f, name.pixelsPerUnit);
            float nameH = Mathf.Min(50 * s, name.cachedTextGeneratorForLayout.GetPreferredHeight(info.name, nameSet) / ppu);
            float top = 30 * s + nameH + 4 * s, bottom = 186 * s; // the description starts ~186 px down
            var frame = UIFactory.Rect("SymbolFrame", face.rectTransform);
            UIFactory.Place(frame, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -top), new Vector2(150 * s, bottom - top));
            var tex = CardSymbols.Get(id);
            var sym = UIFactory.Rect("Symbol", frame).gameObject.AddComponent<RawImage>();
            sym.texture = tex;
            sym.raycastTarget = false;
            float texAspect = tex != null && tex.height > 0 ? tex.width / (float)tex.height : 1f;
            float fw = 150 * s, fh = bottom - top;
            Vector2 fit = fw / fh > texAspect ? new Vector2(fh * texAspect, fh) : new Vector2(fw, fw / texAspect);
            UIFactory.Place(sym.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), Vector2.zero, fit);

            // description (bottom)
            var desc = UIFactory.Text("Description", face.rectTransform, info.description, Mathf.RoundToInt(15 * s), TextAnchor.UpperCenter, Color.black);
            desc.horizontalOverflow = HorizontalWrapMode.Wrap;
            desc.verticalOverflow = VerticalWrapMode.Truncate;
            desc.resizeTextForBestFit = true;
            desc.resizeTextMinSize = Mathf.Max(6, Mathf.RoundToInt(10 * s));
            desc.resizeTextMaxSize = Mathf.RoundToInt(15 * s);
            UIFactory.Place(desc.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 10 * s), new Vector2(164 * s, 62 * s));

            // rarity tag sitting on the bottom border
            var tag = UIFactory.Image("RarityTag", root, Color.black, RoundedSmall);
            tag.type = Image.Type.Sliced;
            UIFactory.Place(tag.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, -1 * s), new Vector2(64 * s, 18 * s));
            var tagFill = UIFactory.Image("Fill", tag.rectTransform, rarity, RoundedSmall);
            tagFill.type = Image.Type.Sliced;
            UIFactory.Stretch(tagFill.rectTransform, 2 * s, 2 * s, 2 * s, 2 * s);
            var tagText = UIFactory.Text("Name", tag.rectTransform, info.rarity.ToString(), Mathf.Max(5, Mathf.RoundToInt(12 * s)), TextAnchor.MiddleCenter, secret ? Color.white : Color.black, FontStyle.Bold);
            UIFactory.Stretch(tagText.rectTransform);
            return root;
        }

        /// <summary>A card blacked out completely: something secret, waiting to be found.</summary>
        public static RectTransform BuildBlackedOut(Transform parent, float width)
        {
            float s = width / 200f;
            var root = UIFactory.Rect("Card ?", parent);
            root.sizeDelta = new Vector2(width, width * Aspect);
            var edge = UIFactory.Image("Edge", root, Color.black, Rounded);
            edge.type = Image.Type.Sliced;
            UIFactory.Stretch(edge.rectTransform);
            var line = UIFactory.Image("Line", root, new Color(0.16f, 0.14f, 0.15f), Rounded);
            line.type = Image.Type.Sliced;
            UIFactory.Stretch(line.rectTransform, 8 * s, 8 * s, 8 * s, 8 * s);
            var face = UIFactory.Image("Face", root, new Color(0.02f, 0.015f, 0.018f), Rounded);
            face.type = Image.Type.Sliced;
            UIFactory.Stretch(face.rectTransform, 10 * s, 10 * s, 10 * s, 10 * s);
            foreach (var g in root.GetComponentsInChildren<Graphic>()) g.raycastTarget = false;
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
