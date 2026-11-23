using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    // ====================================================================== The Hierophant

    /// <summary>
    /// The Hierophant's wall: while it's coming (0.5 s) its frame and runes write themselves in where it will stand;
    /// then a slightly see-through mahogany pane pops in, framed in glowing ink-outlined bars, exactly the size it
    /// reflects at. Whatever bounces off it flashes there. At the end it shrinks away.
    /// </summary>
    public class HierophantView : MonoBehaviour
    {
        static readonly Color PaneTint = new(1.15f, 0.36f, 0.16f, 0.6f);
        const float Bar = 0.07f;

        WorldNet.HierophantFx _fx;
        Transform _body, _pane;
        RuneDrawing _runes;
        bool _popped;
        float _pulse;
        static Material _paneMat;

        public static HierophantView Create(WorldNet.HierophantFx fx)
        {
            var go = new GameObject("HierophantWall") { layer = Layers.Fx };
            go.transform.SetPositionAndRotation(fx.center, fx.Rotation);
            var v = go.AddComponent<HierophantView>();
            v._fx = fx;
            float w = SpellPlans.HierophantWidth, h = SpellPlans.HierophantHeight;

            // the solid part (pane + frame) that pops in; the runes are drawn first, on their own
            v._body = new GameObject("Body") { layer = Layers.Fx }.transform;
            v._body.SetParent(go.transform, false);
            v._pane = MakePane(v._body);
            v._pane.localScale = new Vector3(w, h, SpellPlans.HierophantThick);
            void FrameBar(Vector3 pos, Vector3 size)
            {
                var b = Glow.Piece(v._body, Glow.CubeMesh, TarotVfx.Mahogany, "Frame");
                b.localPosition = pos;
                b.localScale = size;
            }
            FrameBar(new Vector3(0f, h * 0.5f, 0f), new Vector3(w + Bar, Bar, Bar));
            FrameBar(new Vector3(0f, -h * 0.5f, 0f), new Vector3(w + Bar, Bar, Bar));
            FrameBar(new Vector3(w * 0.5f, 0f, 0f), new Vector3(Bar, h + Bar, Bar));
            FrameBar(new Vector3(-w * 0.5f, 0f, 0f), new Vector3(Bar, h + Bar, Bar));
            v._body.localScale = Vector3.zero;

            // the runes: a row of them across the pane, inside an inset frame (drawn in the pane's own plane)
            var r = new GameObject("Runes") { layer = Layers.Fx };
            r.transform.SetParent(go.transform, false);
            r.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f); // the drawing's XZ plane → the pane's XY
            v._runes = new RuneDrawing(r);
            v._runes.AddRune(RuneStrokes(), w, 0.045f);
            v._runes.SetColour(TarotVfx.Mahogany);
            v._runes.Progress = 0f;
            return v;
        }

        static Transform MakePane(Transform parent)
        {
            if (_paneMat == null)
            {
                var s = Shader.Find("SME/Pane");
                _paneMat = s != null ? new Material(s) { name = "Pane" } : Glow.DoubleSided;
            }
            var go = new GameObject("Pane") { layer = Layers.Fx };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = Glow.CubeMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _paneMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            Glow.Tint(mr, PaneTint);
            return go.transform;
        }

        /// <summary>
        /// In the drawing's units (x ±1 = the pane's width): an inset frame, and five runes inside it (a row along the
        /// middle of a wide pane; on a squarer one, the middle and the four corners, like the five on a die).
        /// </summary>
        static List<List<Vector2>> RuneStrokes()
        {
            float hx = 0.93f, hy = Mathf.Min(SpellPlans.HierophantHeight / SpellPlans.HierophantWidth, 1f) * 0.93f;
            var s = new List<List<Vector2>>
            {
                new() { new(-hx, -hy), new(hx, -hy), new(hx, hy), new(-hx, hy), new(-hx, -hy) },
            };
            const int n = 5;
            bool row = hy < 0.5f;
            float size = row ? hy * 0.6f : Mathf.Min(hx, hy) * 0.26f;
            for (int i = 0; i < n; i++)
            {
                Vector2 at = row
                    ? new Vector2(Mathf.Lerp(-hx + 0.22f, hx - 0.22f, i / (float)(n - 1)), 0f)
                    : i == 0 ? Vector2.zero : new Vector2((i & 1) == 0 ? -0.55f * hx : 0.55f * hx, i < 3 ? 0.55f * hy : -0.55f * hy);
                foreach (var line in RuneGlyphs.Rune(i))
                {
                    var l = new List<Vector2>(line.Count);
                    foreach (var p in line) l.Add(at + p * size);
                    s.Add(l);
                }
            }
            return s;
        }

        /// <summary>Something bounced off it here: a flash, and the pane jolts.</summary>
        public void Bounce(Vector3 point)
        {
            Spark(point, 1f);
            _pulse = 1f;
        }

        /// <summary>A mahogany burst (also where two chariots meet).</summary>
        public static void Spark(Vector3 point, float scale)
        {
            EnergyBurst.Create(point, new EnergyBurst.Settings
            {
                flashSize = 0.8f * scale, flashTime = 0.14f, sparkCount = Mathf.RoundToInt(8 * scale), sparkSize = 0.28f * scale,
                sparkSpeed = new Vector2(4f, 9f) * scale, sparkLife = 0.3f, colour = TarotVfx.Mahogany,
            });
            CameraRig.ShakeFrom(point, 0.08f * scale, 0.1f);
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.up); // < 0 while it's coming
            float end = SpellPlans.HierophantTime;
            if (t > end + SpellPlans.HierophantShrink) { Destroy(gameObject); return; }

            if (t < 0f)
            {
                // written in where it will stand, over the delay
                _runes.Progress = 1f + t / SpellPlans.HierophantDelay;
                return;
            }
            _runes.Progress = 1f;
            if (!_popped)
            {
                _popped = true;
                if (t < 0.2f)
                {
                    EnergyBurst.Create(transform.position, new EnergyBurst.Settings
                    {
                        flashSize = 1.4f, flashTime = 0.16f, sparkCount = 14, sparkSize = 0.3f,
                        sparkSpeed = new Vector2(5f, 10f), sparkLife = 0.35f, colour = TarotVfx.Mahogany,
                    });
                    CameraRig.ShakeFrom(transform.position, 0.1f, 0.15f);
                }
            }

            // pops in (overshooting a little), stands, then shrinks away to nothing
            float s;
            if (t < SpellPlans.HierophantGrow)
            {
                float u = t / SpellPlans.HierophantGrow;
                s = 1f + 0.15f * Mathf.Sin(u * Mathf.PI) * (1f - u * 0.5f);
                s *= 1f - Mathf.Pow(1f - u, 3f);
            }
            else if (t > end) s = 1f - Mathf.Clamp01((t - end) / SpellPlans.HierophantShrink);
            else s = 1f;
            _pulse = Mathf.MoveTowards(_pulse, 0f, Time.deltaTime * 6f);
            float jolt = 1f + 0.06f * _pulse;
            _body.localScale = Vector3.one * (s * jolt);
            _runes.Host.localScale = Vector3.one * Mathf.Max(0.001f, (t < SpellPlans.HierophantGrow ? 1f : s) * jolt); // already drawn: it stays while the pane pops in round it
        }

        void OnDestroy() => _runes?.Release();
    }

    // ====================================================================== The Lovers

    /// <summary>
    /// The Lovers: a mahogany beam thrown from the caster's hand, its head homing in on the target and trailing the
    /// beam back to the hand. Caught: the beam links them, waving gently.
    /// Missed or blocked: the head pops where it stopped. Snapped: the beam shrinks away to nothing.
    /// </summary>
    public class LoversView : MonoBehaviour
    {
        static readonly Color Beam = new(2.4f, 0.55f, 0.42f, 1f);
        const float Width = 0.09f, EdgeExtra = 0.06f;

        WorldNet.LoversFx _fx;
        LineRenderer _core, _edge;
        Transform _head;
        Vector3 _pos, _dir;
        double _simTime;
        float _endScale = 1f;
        bool _endShown;
        readonly List<Vector3> _pts = new();
        Vector3[] _buf = new Vector3[48];

        public static LoversView Create(WorldNet.LoversFx fx)
        {
            var go = new GameObject("LoversBeam") { layer = Layers.Fx };
            var v = go.AddComponent<LoversView>();
            v._fx = fx;
            v._edge = MakeLine(go.transform, ToonLine.EdgeMaterial, Width + EdgeExtra, Glow.Ink);
            v._core = MakeLine(go.transform, ToonLine.CoreMaterial, Width, Beam);
            v._head = Glow.Piece(go.transform, Glow.SphereMesh, Beam, "Head");
            v._head.localScale = Vector3.one * 0.22f;
            v._pos = fx.origin;
            v._dir = fx.dir.sqrMagnitude > 1e-6f ? fx.dir.normalized : Vector3.forward;
            v._simTime = fx.start;
            return v;
        }

        static LineRenderer MakeLine(Transform parent, Material mat, float width, Color c)
        {
            var go = new GameObject("Line") { layer = Layers.Fx };
            go.transform.SetParent(parent, false);
            var l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = true;
            l.alignment = LineAlignment.View;
            l.sharedMaterial = mat;
            l.widthMultiplier = width;
            l.numCapVertices = 4;
            l.numCornerVertices = 2;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            l.positionCount = 0;
            Glow.Tint(l, c);
            return l;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            var caster = WorldNet.FindCombatant(_fx.caster);
            var target = WorldNet.FindCombatant(_fx.target);
            Vector3 hand = caster != null ? SpellVisuals.RightHand(caster) : _fx.origin;
            Vector3 chest = target != null ? SpellPlans.LoversChest(target) : _pos;
            bool tethered = _fx.tether >= 0;
            float dt = Time.deltaTime;

            if (!tethered)
            {
                // the head flies on, homing in (the same steering the server uses, from where it was thrown)
                while (_simTime < now && (_fx.ended < 0 || _simTime < _fx.ended))
                {
                    float step = Mathf.Min(1f / 120f, (float)(now - _simTime));
                    if (step <= 0f) break;
                    if ((_pos - chest).magnitude > SpellPlans.LoversCatch * 0.5f) SpellPlans.StepLovers(ref _pos, ref _dir, chest, step);
                    _simTime += step;
                }
                if (_fx.ended >= 0)
                {
                    // missed or blocked: the head pops where the server stopped it, and the beam shrinks back away
                    if (!_endShown) { _endShown = true; _pos = _fx.endPoint; HierophantView.Spark(_pos, 0.6f); }
                    _endScale = Mathf.MoveTowards(_endScale, 0f, dt / 0.2f);
                    if (_endScale <= 0f) { Destroy(gameObject); return; }
                }
                _head.position = _pos;
                _head.localScale = Vector3.one * (0.22f * _endScale);
                DrawBeam(hand, _pos, 0f, _endScale);
                return;
            }

            // tethered: the head is gone, the beam links hand and chest
            if (_head.gameObject.activeSelf)
            {
                _head.gameObject.SetActive(false);
                HierophantView.Spark(chest, 0.5f);
            }
            if (_fx.ended >= 0)
            {
                // snapped / let go: the beam shrinks away to nothing (no fade)
                if (!_endShown) { _endShown = true; HierophantView.Spark(Vector3.Lerp(hand, chest, 0.5f), 0.6f); }
                _endScale = Mathf.MoveTowards(_endScale, 0f, dt / 0.25f);
                if (_endScale <= 0f) { Destroy(gameObject); return; }
            }
            DrawBeam(hand, chest, 1f, _endScale);
        }

        /// <summary>The beam from a to b, waving a little (<paramref name="wave"/>), <paramref name="scale"/> thick.</summary>
        void DrawBeam(Vector3 a, Vector3 b, float wave, float scale)
        {
            _pts.Clear();
            Vector3 span = b - a;
            float len = span.magnitude;
            int n = Mathf.Clamp(Mathf.CeilToInt(len / 0.3f), 2, 46);
            Vector3 side = Vector3.Cross(span.sqrMagnitude > 1e-6f ? span.normalized : Vector3.forward, Vector3.up);
            if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
            side.Normalize();
            float time = Time.time;
            for (int i = 0; i <= n; i++)
            {
                float u = i / (float)n;
                float sway = Mathf.Sin(u * Mathf.PI) * wave * 0.12f;
                _pts.Add(a + span * u + (side * Mathf.Sin(u * 9f - time * 7f) + Vector3.up * Mathf.Cos(u * 7f - time * 5f) * 0.6f) * sway);
            }
            if (_buf.Length < _pts.Count) _buf = new Vector3[_pts.Count * 2];
            for (int i = 0; i < _pts.Count; i++) _buf[i] = _pts[i];
            _core.positionCount = _edge.positionCount = _pts.Count;
            _core.SetPositions(_buf);
            _edge.SetPositions(_buf);
            _core.widthMultiplier = Width * scale;
            _edge.widthMultiplier = (Width + EdgeExtra) * scale;
        }
    }

    // ====================================================================== The Chariot

    /// <summary>
    /// The Chariot: the caster is rolled up into a big mahogany ball (exactly <see cref="SpellPlans.ChariotRadius"/>
    /// round, ink outline included: the size it collides, smashes and runs people over at), rolling faster and faster,
    /// glowing bands tumbling round it and mahogany trails streaming off behind. The crash: a big burst and a hard shake.
    /// At the end the ball shrinks away and the caster is back.
    /// </summary>
    public class ChariotView : MonoBehaviour
    {
        static readonly Color Shell = new(0.7f, 0.27f, 0.15f, 1f); // mahogany (not glowing: it's the body)

        WorldNet.ChariotFx _fx;
        Transform _spin, _ball;
        readonly List<RuneDrawing> _bands = new();
        readonly List<TrailRenderer> _trails = new();
        float _roll, _scale;
        bool _endShown;

        public static ChariotView Create(WorldNet.ChariotFx fx)
        {
            var go = new GameObject("Chariot") { layer = Layers.Fx };
            var v = go.AddComponent<ChariotView>();
            v._fx = fx;
            float R = SpellPlans.ChariotRadius;
            v._spin = new GameObject("Roll") { layer = Layers.Fx }.transform;
            v._spin.SetParent(go.transform, false);
            v._ball = Glow.Piece(v._spin, Glow.SphereMesh, Shell, "Ball");
            v._ball.localScale = Vector3.one * (2f * R / ForceView.OutlineGrow); // its ink edge lands on the radius
            // glowing bands round it (the card's wheel: a rim and crossing spokes), just over the coloured surface
            float band = R / ForceView.OutlineGrow + 0.015f;
            Quaternion[] planes =
            {
                Quaternion.Euler(0f, 0f, 90f),                         // the rim: round the rolling axis
                Quaternion.identity,                                   // two crossing bands through the rolling axis,
                Quaternion.Euler(90f, 0f, 0f),                         // which tumble as it rolls
            };
            for (int i = 0; i < 3; i++)
            {
                var b = new GameObject("Band") { layer = Layers.Fx };
                b.transform.SetParent(v._spin, false);
                b.transform.localRotation = planes[i];
                var d = new RuneDrawing(b);
                d.AddRune(new List<List<Vector2>> { TarotVfx.Ring(1f, 0f, 48) }, band * 2f, i == 0 ? 0.09f : 0.06f);
                d.Progress = 1f;
                d.SetColour(TarotVfx.Mahogany);
                v._bands.Add(d);
            }
            // trails off its lower sides (they don't roll with it)
            for (int s = -1; s <= 1; s += 2)
            {
                var p = new GameObject("TrailPoint") { layer = Layers.Fx }.transform;
                p.SetParent(go.transform, false);
                p.localPosition = new Vector3(s * R * 0.7f, -R * 0.55f, -R * 0.3f);
                v._trails.Add(Glow.Trail(p, TarotVfx.Mahogany, 0.3f, 0.24f));
            }
            go.transform.localScale = Vector3.zero;
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            var c = WorldNet.FindCombatant(_fx.caster);
            float dt = Time.deltaTime;
            if (c == null) { Finish(); return; }
            // the way its driver is steering it (frozen once it's over)
            Vector3 fwd = _fx.ended < 0 ? SpellPlans.ChariotDir(c.ViewYaw) : transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            transform.SetPositionAndRotation(SpellPlans.ChariotBallCenter(c.transform.position), Quaternion.LookRotation(fwd));

            if (_fx.ended >= 0 && !_endShown)
            {
                _endShown = true;
                if (_fx.crashed && !Debris.Suppressed)
                {
                    EnergyBurst.Create(_fx.point, new EnergyBurst.Settings
                    {
                        flashSize = 2.6f, flashTime = 0.2f, sparkCount = 22, sparkSize = 0.45f,
                        sparkSpeed = new Vector2(7f, 15f), sparkLife = 0.45f, colour = TarotVfx.Mahogany,
                    });
                    CameraRig.ShakeFrom(_fx.point, 0.35f, 0.35f);
                }
                foreach (var t in _trails) Glow.DropTrail(t);
                _trails.Clear();
            }

            // pops in (a little overshoot); shrinks away to nothing when it's over
            if (_fx.ended >= 0) _scale = Mathf.MoveTowards(_scale, 0f, dt / 0.15f);
            else _scale = Mathf.MoveTowards(_scale, 1f, dt / 0.12f);
            if (_fx.ended >= 0 && _scale <= 0f) { Finish(); return; }
            float pop = _fx.ended < 0 && _scale < 1f ? 1f + 0.12f * Mathf.Sin(_scale * Mathf.PI) : 1f;
            transform.localScale = Vector3.one * (_scale * pop);

            // rolling at the speed it's going (forwards: about its right-hand axis)
            if (_fx.ended < 0) _roll += SpellPlans.ChariotSpeed((float)(now - _fx.start)) / SpellPlans.ChariotRadius * Mathf.Rad2Deg * dt;
            _spin.localRotation = Quaternion.Euler(_roll, 0f, 0f);
        }

        void Finish()
        {
            foreach (var t in _trails) Glow.DropTrail(t);
            _trails.Clear();
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            foreach (var b in _bands) b.Release();
        }
    }
}
