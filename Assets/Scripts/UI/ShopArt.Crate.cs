using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The cosmetics crate: a plain wooden box with an arcane rune painted on it, and its lid (a piece of its own, so it can fly off).</summary>
    public static partial class ShopArt
    {
        public const int CrateW = 310, CrateBodyH = 250, CrateLidH = 58;
        /// <summary>How far the lid's bottom edge sits over the body's top (px).</summary>
        public const int LidOverlap = 12;
        static Texture2D _crateBody, _crateLid, _crate;

        public static Texture2D CrateBody => _crateBody != null ? _crateBody : _crateBody = MakeCrate(true, false);
        public static Texture2D CrateLid => _crateLid != null ? _crateLid : _crateLid = MakeCrate(false, true);
        /// <summary>The whole thing, shut.</summary>
        public static Texture2D Crate => _crate != null ? _crate : _crate = MakeCrate(true, true);

        static Texture2D MakeCrate(bool body, bool lid)
        {
            int w = CrateW, h = body && lid ? CrateBodyH + CrateLidH - LidOverlap : body ? CrateBodyH : CrateLidH;
            var px = new Color32[w * h];
            Color32 plank = C(0.6f, 0.39f, 0.2f), plank2 = C(0.55f, 0.35f, 0.175f), seam = C(0.3f, 0.17f, 0.09f);
            Color32 board = C(0.72f, 0.5f, 0.28f), boardLight = C(0.8f, 0.58f, 0.34f), boardDark = C(0.62f, 0.41f, 0.22f);
            Color32 rune = C(0.42f, 0.6f, 1f);

            void Nail(float cx, float cy) => Layer(px, w, h, (x, y) => Disc(x, y, cx, cy, 3.6f), (x, y) => seam, 0f);
            void Board(float x0, float y0, float x1, float y1)
            {
                bool across = x1 - x0 > y1 - y0;
                Layer(px, w, h, (x, y) => RoundBox(x, y, x0, y0, x1, y1, 4), (x, y) =>
                {
                    float at = across ? (y - y0) / (y1 - y0) : 1f - (x - x0) / (x1 - x0); // lit along the top, and the left
                    return at > 0.8f ? boardLight : at < 0.16f ? boardDark : board;
                }, 4.5f);
            }

            if (body)
            {
                const float x0 = 18f, x1 = CrateW - 18f, y0 = 8f, y1 = CrateBodyH - 8f, b = 34f;
                // planks across, framed by boards
                Layer(px, w, h, (x, y) => RoundBox(x, y, x0, y0, x1, y1, 5), (x, y) =>
                {
                    int n = (int)((y - y0) / 47f);
                    return y - y0 - n * 47f < 3f && n > 0 ? seam : n % 2 == 0 ? plank : plank2;
                }, 5.5f);
                Board(x0, y0, x0 + b, y1);
                Board(x1 - b, y0, x1, y1);
                Board(x0 + b, y0, x1 - b, y0 + b);
                Board(x0 + b, y1 - b, x1 - b, y1);
                foreach (var cx in new[] { x0 + b / 2, x1 - b / 2 })
                    foreach (var cy in new[] { y0 + 12, y0 + 26, y1 - 12, y1 - 26 }) Nail(cx, cy);
                // the rune, painted on
                Layer(px, w, h, (x, y) => Rune(x, y, w / 2f, (y0 + y1) / 2, 48f, 0f, 2, 8f), (x, y) => rune, 4f);
            }
            if (lid)
            {
                // a flat board, a little wider than the box
                float b = body ? CrateBodyH - LidOverlap : 0f;
                Board(7, b + 8, w - 7, b + CrateLidH - 8);
                foreach (var cx in new[] { 24f, 40f, w - 40f, w - 24f }) Nail(cx, b + CrateLidH / 2f);
            }
            return Finish(px, w, h, "ShopCrate");
        }
    }
}
