using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Hammerang on screen: the user's hammer at half size, flying head first (no spin), with three
    /// inked speed lines streaming off behind it (as the sketch has them). Knocked down, it falls and lies along the ground
    /// until it comes back. It dithers in out of the hand, and away when it's caught (or gone).
    /// </summary>
    public class RangView : MonoBehaviour
    {
        static readonly int FadeId = Shader.PropertyToID("_Fade");

        WorldNet.RangFx _fx;
        Transform _spinner;
        Renderer[] _renderers;
        MaterialPropertyBlock _mpb;
        readonly List<(TrailRenderer core, TrailRenderer edge)> _lines = new();
        Vector3 _last, _face;
        bool _ended;

        /// <summary>The hammer's handle runs up +Y to its head: turned so the head points the way it's going (+Z).</summary>
        static readonly Quaternion HeadFirst = Quaternion.Euler(90f, 0f, 0f);

        public static RangView Create(WorldNet.RangFx fx)
        {
            var go = new GameObject("Hammerang") { layer = Layers.Fx };
            var v = go.AddComponent<RangView>();
            v._fx = fx;
            v._face = fx.dir.sqrMagnitude > 1e-4f ? fx.dir.normalized : Vector3.forward;
            go.transform.position = v._last = WorldNet.RangPosition(fx, WorldNet.Now, out _);
            go.transform.rotation = Quaternion.LookRotation(v._face);
            // root (faces the way it flies) > the hammer turned head first, its middle on the root
            v._spinner = new GameObject("Spin") { layer = Layers.Fx }.transform;
            v._spinner.SetParent(go.transform, false);
            var model = HammerModel.Spawn().transform;
            model.SetParent(v._spinner, false);
            model.localScale = Vector3.one * SpellPlans.RangScale;
            model.localPosition = Vector3.down * (HammerModel.HeadReach * 0.4f * SpellPlans.RangScale);
            v._renderers = model.GetComponentsInChildren<Renderer>(true);
            v._mpb = new MaterialPropertyBlock();
            // three speed lines: above, through the middle, below
            foreach (float y in new[] { 0.3f, 0f, -0.3f })
            {
                var t = new GameObject("SpeedLine") { layer = Layers.Fx }.transform;
                t.SetParent(go.transform, false);
                t.localPosition = new Vector3(0f, y, 0f);
                v._lines.Add(ToonLine.Trail(t, y == 0f ? 0.07f : 0.05f, 0.16f));
            }
            v.LateUpdate();
            return v;
        }

        void LateUpdate()
        {
            if (_fx == null) { Destroy(gameObject); return; }
            double now = WorldNet.Now;
            Vector3 pos = WorldNet.RangPosition(_fx, now, out byte phase);
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            Vector3 moved = pos - _last;
            _last = pos;
            if (moved.sqrMagnitude > 0.0004f) _face = moved.normalized;
            bool resting = phase == WorldNet.RangResting;
            bool flying = !resting && _fx.ended < 0;

            transform.position = pos;
            Vector3 face = resting ? new Vector3(_face.x, 0f, _face.z) : _face;
            if (face.sqrMagnitude < 1e-4f) face = transform.forward;
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(face.normalized), 1f - Mathf.Exp(-20f * dt));

            // no spin: head first along the way it's going (the handle trailing); lying down, it lies along the ground
            _spinner.localRotation = HeadFirst;
            foreach (var (core, edge) in _lines) { core.emitting = flying && phase != WorldNet.RangFalling; edge.emitting = core.emitting; }

            // dithered in out of the hand, away when it's over
            float fade = Mathf.Clamp01((float)(now - _fx.start) / 0.08f);
            if (_fx.ended >= 0)
            {
                if (!_ended)
                {
                    _ended = true;
                    if (_fx.caught) CameraRig.ShakeFrom(pos, 0.15f, 0.12f);
                }
                fade = 1f - Mathf.Clamp01((float)(now - _fx.ended) / (_fx.caught ? 0.15f : 0.3f));
                if (fade <= 0f)
                {
                    foreach (var (core, edge) in _lines) { Glow.DropTrail(core); Glow.DropTrail(edge); }
                    Destroy(gameObject);
                    return;
                }
            }
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetFloat(FadeId, fade);
                r.SetPropertyBlock(_mpb);
            }
        }
    }
}
