using UnityEngine;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The local player's screen while The World has them. Taken: they go on seeing where they are (their body warping
    /// away) as black closes in from above and below until the screen is shut; only then does it open, on THE WORLD (<see cref="WorldCinematic"/>) while the sphere is made, and opens again inside it.
    /// Then, across the top: ONLY ONE MAY REMAIN for a few seconds, and FIGHT OR DIE once both are dying.
    /// </summary>
    public class WorldScreen : MonoBehaviour
    {
        const float Beat = 0.12f, OpenTime = 0.45f, CloseTime = 0.25f; // shut for a beat, then opened; shut again at the end
        Canvas _canvas;
        RectTransform _layer, _top, _bottom;
        Text _banner;
        WorldCinematic _show;
        double _showFor;

        void LateUpdate()
        {
            var me = PlayerController.Local;
            double now = WorldNet.Now;
            bool there = me != null && me.IsSpawned && me.IsAlive;
            var st = there ? me.Status.Value : default;
            there &= st.WorldUntil > 0 && now >= st.WorldFrom;

            float shut = there ? Shut(st, now) : 0f;
            bool taken = there && st.Taken(now);
            if (taken && _showFor != st.WorldFrom)
            {
                _showFor = st.WorldFrom;
                EndShow();
                Build();
                _show = WorldCinematic.Create(WorldNet.FindCaster(st.WorldBy), _layer);
            }
            if (_show != null)
            {
                if (!taken) EndShow();
                else _show.SetShown(now >= st.WorldFrom + SpellPlans.WorldCrush); // behind the shut bars
                if (_show != null) _show.Tick((float)(now - st.WorldFrom) - SpellPlans.WorldCrush - Beat, SpellPlans.WorldAway - SpellPlans.WorldCrush - Beat);
            }

            string words = there ? Banner(me, st, now) : null;
            bool any = shut > 0.001f || words != null || _show != null;
            if (_canvas != null && _canvas.gameObject.activeSelf != any) _canvas.gameObject.SetActive(any);
            if (!any) return;
            Build();
            bool bars = shut > 0.001f;
            _top.gameObject.SetActive(bars);
            _bottom.gameObject.SetActive(bars);
            _top.anchorMin = new Vector2(0f, 1f - 0.505f * shut);
            _bottom.anchorMax = new Vector2(1f, 0.505f * shut);
            _banner.gameObject.SetActive(words != null);
        }

        /// <summary>How far the screen is crushed shut: 0 open .. 1 black.</summary>
        float Shut(in BodyStatus st, double now)
        {
            float t = (float)(now - st.WorldFrom), left = (float)(st.WorldUntil - now);
            if (left <= 0f)
            {
                float o = Mathf.Clamp01(-left / 0.4f);
                return (1f - o) * (1f - o);
            }
            // (not at once: the warp is seen first)
            float u = Mathf.InverseLerp(SpellPlans.WorldBarsFrom, SpellPlans.WorldCrush, t);
            if (u < 1f) return u * u * (3f - 2f * u);
            if (_show == null) return 1f; // (nothing to show: dark until it's ready)
            float open = Mathf.Clamp01((t - SpellPlans.WorldCrush - Beat) / OpenTime), close = Mathf.Clamp01(1f - left / CloseTime);
            return Mathf.Max((1f - open) * (1f - open), close * close);
        }

        /// <summary>The words across the top right now (and their size), or null.</summary>
        string Banner(PlayerController me, in BodyStatus st, double now)
        {
            float age = (float)(now - st.WorldUntil);
            if (age < 0f || WorldNet.WorldOf(me) == null) return null;
            bool dying = age >= SpellPlans.WorldGrace;
            if (!dying && age >= SpellPlans.WorldCallout) return null;
            Build();
            float t = dying ? age - SpellPlans.WorldGrace : age;
            float scale = t < 0.16f ? Mathf.Lerp(2f, 1f, t / 0.16f) : 1f;                              // punches in
            if (dying) scale *= 1f + 0.05f * Mathf.Max(0f, Mathf.Sin(t * Mathf.PI * 2f));               // a beat a second, as the damage comes
            else scale *= Mathf.Clamp01((SpellPlans.WorldCallout - t) / 0.2f);                          // shrinks away
            _banner.text = dying ? "FIGHT OR DIE" : "ONLY ONE MAY REMAIN";
            _banner.rectTransform.localScale = Vector3.one * scale;
            return _banner.text;
        }

        void EndShow()
        {
            if (_show != null) _show.Remove();
            _show = null;
        }

        void OnDestroy() => EndShow();

        void Build()
        {
            if (_canvas != null) return;
            var go = new GameObject("WorldScreen");
            go.transform.SetParent(transform, false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 12; // over the HUD (10), under the menus (20+)
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            _layer = UIFactory.Stretch(UIFactory.Rect("Show", go.transform));
            RectTransform Half(string name, bool top)
            {
                var rt = UIFactory.Image(name, go.transform, Color.black).rectTransform;
                rt.anchorMin = new Vector2(0f, top ? 1f : 0f);
                rt.anchorMax = new Vector2(1f, top ? 1f : 0f);
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                rt.GetComponent<Image>().raycastTarget = false;
                return rt;
            }
            _top = Half("Top", true);
            _bottom = Half("Bottom", false);
            _banner = UIFactory.Text("Banner", go.transform, "", 76, TextAnchor.MiddleCenter, new Color(0.95f, 0.1f, 0.07f), FontStyle.Bold);
            UIFactory.Place(_banner.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, -110f), new Vector2(1600f, 110f));
            Ink(_banner, 4f);
        }

        /// <summary>A thick black edge all round some lettering.</summary>
        public static void Ink(Text t, float px)
        {
            for (int i = 0; i < 2; i++)
            {
                var o = t.gameObject.AddComponent<Outline>();
                o.effectColor = new Color(0.03f, 0.015f, 0.015f, 1f);
                o.effectDistance = i == 0 ? new Vector2(px, -px) : new Vector2(px * 0.6f, px * 0.6f);
            }
        }
    }
}
