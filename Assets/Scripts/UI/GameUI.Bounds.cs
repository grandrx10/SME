using UnityEngine;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>Out of bounds: a red border round the screen, and a ring running out round the seconds left to get back.</summary>
    public partial class GameUI
    {
        RectTransform _boundsRoot, _boundsRing;
        readonly RectTransform[] _boundsEdges = new RectTransform[8]; // ink, then red, for each side
        Image _boundsFill;
        Text _boundsCount;
        int _boundsShown = -1;
        float _boundsPulse;

        void BuildBounds()
        {
            _boundsRoot = UIFactory.Stretch(UIFactory.Rect("OutOfBounds", _hud));
            Color ink = new(0.06f, 0.05f, 0.08f, 1f);
            for (int i = 0; i < _boundsEdges.Length; i++)
            {
                var edge = UIFactory.Image(i < 4 ? "Ink" : "Edge", _boundsRoot, i < 4 ? ink : DeniedRed);
                edge.raycastTarget = false;
                var r = edge.rectTransform;
                bool across = i % 4 < 2, far = i % 2 == 1; // bottom, top, left, right
                r.anchorMin = new Vector2(!across && far ? 1f : 0f, across && far ? 1f : 0f);
                r.anchorMax = new Vector2(!across && !far ? 0f : 1f, across && !far ? 0f : 1f);
                r.pivot = new Vector2(across ? 0.5f : far ? 1f : 0f, across ? far ? 1f : 0f : 0.5f);
                r.anchoredPosition = Vector2.zero;
                _boundsEdges[i] = r;
            }

            _boundsRing = UIFactory.Rect("Ring", _boundsRoot);
            UIFactory.Place(_boundsRing, new Vector2(0.5f, 0.74f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(190, 190));
            UIFactory.Stretch(UIFactory.Image("Back", _boundsRing, new Color(0, 0, 0, 0.45f), _thinRing).rectTransform);
            _boundsFill = Radial(UIFactory.Image("Fill", _boundsRing, DeniedRed, _thinRing));
            UIFactory.Stretch(_boundsFill.rectTransform);
            var skull = UIFactory.Text("Skull", _boundsRing, "☠", 40, TextAnchor.MiddleCenter, DeniedRed, FontStyle.Bold);
            UIFactory.Place(skull.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 50), new Vector2(100, 60));
            Outlined(skull, 2f, ink);
            _boundsCount = UIFactory.Text("Count", _boundsRing, "", 92, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            UIFactory.Place(_boundsCount.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -14), new Vector2(180, 130));
            Outlined(_boundsCount, 4f, ink);
            foreach (var g in _boundsRing.GetComponentsInChildren<Graphic>()) g.raycastTarget = false;
            _boundsRoot.gameObject.SetActive(false);
        }

        void UpdateBounds(PlayerController pc, float dt)
        {
            float left = pc != null && pc.IsSpawned ? pc.OutOfBoundsLeft : -1f;
            bool on = left >= 0f;
            if (_boundsRoot.gameObject.activeSelf != on) _boundsRoot.gameObject.SetActive(on);
            if (!on) { _boundsShown = -1; return; }
            int n = Mathf.Max(1, Mathf.CeilToInt(left));
            if (n != _boundsShown)
            {
                _boundsShown = n;
                _boundsCount.text = n.ToString();
                _boundsPulse = 1f;
            }
            _boundsPulse = Mathf.MoveTowards(_boundsPulse, 0f, dt * 3f);
            _boundsFill.fillAmount = left / PlayerController.OutsideTime;
            _boundsRing.localScale = Vector3.one * (1f + 0.25f * _boundsPulse);
            // the border thickens with every second gone, and jumps as each goes
            float w = Mathf.Lerp(34f, 14f, left / PlayerController.OutsideTime) + 18f * _boundsPulse;
            for (int i = 0; i < _boundsEdges.Length; i++)
            {
                float t = i < 4 ? w + 6f : w;
                _boundsEdges[i].sizeDelta = i % 4 < 2 ? new Vector2(0f, t) : new Vector2(t, 0f);
            }
        }
    }
}
