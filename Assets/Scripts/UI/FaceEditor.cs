using UnityEngine;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>The face drawing page: drawing pad on the left, live 3D preview of your head on the right.</summary>
    public static class FaceEditor
    {
        public static void Build(RectTransform page, Vector2 window)
        {
            const float pad = 600f, side = 300f, gap = 30f;
            float left = (window.x - pad - gap - side) * 0.5f;
            float rightX = left + pad + gap;
            float top = -(window.y - pad) * 0.5f;

            var bg = UIFactory.Image("Pad", page, FacePreview.SkinColor);
            UIFactory.Place(bg.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(left, top), new Vector2(pad, pad));
            var bo = bg.gameObject.AddComponent<Outline>();
            bo.effectColor = new Color(1, 1, 1, 0.25f);
            bo.effectDistance = new Vector2(2, -2);
            var vGuide = UIFactory.Image("GuideV", bg.rectTransform, new Color(0, 0, 0, 0.12f));
            vGuide.raycastTarget = false;
            UIFactory.Place(vGuide.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(2, pad));
            var hGuide = UIFactory.Image("GuideH", bg.rectTransform, new Color(0, 0, 0, 0.12f));
            hGuide.raycastTarget = false;
            UIFactory.Place(hGuide.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(pad, 2));

            var drawing = UIFactory.Rect("Drawing", bg.rectTransform).gameObject.AddComponent<RawImage>();
            drawing.texture = FacePreview.Painter.Texture;
            UIFactory.Stretch(drawing.rectTransform);
            var canvas = drawing.gameObject.AddComponent<FaceCanvas>();

            // live preview
            float previewH = side * FacePreview.TexHeight / FacePreview.TexWidth;
            var frame = UIFactory.Image("PreviewFrame", page, new Color(0.11f, 0.12f, 0.15f, 1f));
            UIFactory.Place(frame.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(rightX, top), new Vector2(side, previewH));
            var tex = FacePreview.Get();
            if (tex != null)
            {
                var img = UIFactory.Rect("Preview", frame.rectTransform).gameObject.AddComponent<RawImage>();
                img.texture = tex;
                img.raycastTarget = false;
                UIFactory.Stretch(img.rectTransform);
            }

            // ink left
            float y = top - previewH - 20;
            var barBg = UIFactory.Image("InkBar", page, new Color(0.11f, 0.12f, 0.15f, 1f));
            UIFactory.Place(barBg.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(rightX, y), new Vector2(side, 10));
            var bar = UIFactory.Image("Fill", barBg.rectTransform, UIFactory.Accent);
            bar.rectTransform.anchorMin = Vector2.zero;
            bar.rectTransform.pivot = new Vector2(0, 0.5f);
            bar.rectTransform.offsetMin = bar.rectTransform.offsetMax = Vector2.zero;
            void UpdateInk() => bar.rectTransform.anchorMax = new Vector2(canvas.InkUsed, 1);
            canvas.InkChanged += UpdateInk;
            UpdateInk();

            // undo / clear (icons)
            var undo = UIFactory.Button("Undo", page, "↶", canvas.Undo, 34);
            UIFactory.Place((RectTransform)undo.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(rightX, y - 30), new Vector2((side - 12) / 2, 60));
            var clear = UIFactory.Button("Clear", page, "✕", canvas.Clear, 30);
            UIFactory.Place((RectTransform)clear.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(rightX + (side + 12) / 2, y - 30), new Vector2((side - 12) / 2, 60));
        }
    }
}
