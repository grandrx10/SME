using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Drawing pad for the face editor (sits on the pad's RawImage). Hold the left mouse button and drag to draw.
    /// Strokes go into <see cref="FacePreview.Painter"/> live and are saved (and sent to other players) when you let go.
    /// </summary>
    public class FaceCanvas : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        /// <summary>Points closer together than this (grid units) are skipped — saves ink, looks the same.</summary>
        const float MinStep = 2.5f;

        List<List<Vector2Int>> _strokes;
        List<Vector2Int> _current;
        RectTransform _rect;

        /// <summary>Fired when the ink used changes (for the ink meter).</summary>
        public event Action InkChanged;
        public float InkUsed => Mathf.Clamp01(FaceArt.SizeOf(_strokes) / (float)FaceArt.MaxBytes);
        public bool HasStrokes => _strokes.Count > 0;

        void Awake()
        {
            _rect = (RectTransform)transform;
            _strokes = FaceArt.Strokes;
            FacePreview.Painter.Draw(_strokes);
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left || !ToGrid(e, out var p)) return;
            if (FaceArt.SizeOf(_strokes) + 3 > FaceArt.MaxBytes) return; // out of ink
            _current = new List<Vector2Int> { p };
            _strokes.Add(_current);
            FacePreview.Painter.AddSegment(p, p);
            InkChanged?.Invoke();
        }

        public void OnDrag(PointerEventData e)
        {
            if (_current == null || e.button != PointerEventData.InputButton.Left) return;
            ToGrid(e, out var p); // off the pad: the line sticks to the edge
            var last = _current[_current.Count - 1];
            if (Vector2Int.Distance(p, last) < MinStep) return;
            if (FaceArt.SizeOf(_strokes) + 2 > FaceArt.MaxBytes) return;
            _current.Add(p);
            FacePreview.Painter.AddSegment(last, p);
            InkChanged?.Invoke();
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (_current == null || e.button != PointerEventData.InputButton.Left) return;
            _current = null;
            FaceArt.Set(_strokes);
        }

        void OnDisable()
        {
            // closed mid-stroke: keep what was drawn
            if (_current != null) { _current = null; FaceArt.Set(_strokes); }
        }

        public void Undo()
        {
            if (_strokes.Count == 0) return;
            _current = null;
            _strokes.RemoveAt(_strokes.Count - 1);
            Commit();
        }

        public void Clear()
        {
            if (_strokes.Count == 0) return;
            _current = null;
            _strokes.Clear();
            Commit();
        }

        void Commit()
        {
            FacePreview.Painter.Draw(_strokes);
            FaceArt.Set(_strokes);
            InkChanged?.Invoke();
        }

        bool ToGrid(PointerEventData e, out Vector2Int p)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, e.position, e.pressEventCamera, out var local);
            var n = Rect.PointToNormalized(_rect.rect, local); // clamped to 0..1
            p = FaceArt.Clamp(new Vector2Int(Mathf.RoundToInt(n.x * (FaceArt.Grid - 1)), Mathf.RoundToInt(n.y * (FaceArt.Grid - 1))));
            return _rect.rect.Contains(local);
        }
    }
}
