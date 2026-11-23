using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Arcane Judgment: a rune circle written onto the ground over a second (double outer ring, inner ring,
    /// runes facing outward with diamonds between them), a bright flare, then an orbital laser column for two
    /// seconds that shrinks away. All glow + ink outline, nothing fades. Judgement (Tarot) is the same in mahogany.
    /// </summary>
    public class JudgmentView : MonoBehaviour
    {
        WorldNet.JudgmentFx _fx;
        RuneDrawing _circle;
        Transform _circleT, _beam, _ink, _core;
        readonly List<Transform> _motes = new();
        float _fizzledAt = -1f;
        static Mesh _tube;
        static Material _beamMat, _inkMat;

        /// <summary>An open tube along +Y: radius 1, from y 0 to y 1, faces outward.</summary>
        static Mesh MakeTube(int sides)
        {
            var v = new List<Vector3>();
            var tri = new List<int>();
            for (int i = 0; i <= sides; i++)
            {
                float a = i / (float)sides * Mathf.PI * 2f;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                v.Add(d);
                v.Add(d + Vector3.up);
            }
            for (int i = 0; i < sides; i++)
            {
                int k = i * 2;
                tri.Add(k); tri.Add(k + 1); tri.Add(k + 2);
                tri.Add(k + 1); tri.Add(k + 3); tri.Add(k + 2);
            }
            var m = new Mesh { name = "Beam Tube" };
            m.SetVertices(v);
            m.SetTriangles(tri, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>A tube renderer; cull 2 = back faces hidden (the glow), 1 = front faces hidden (the ink rim behind it).</summary>
        static Transform TubePiece(Transform parent, string name, Color colour, float cull)
        {
            if (_beamMat == null)
            {
                _beamMat = new Material(Glow.Shared) { name = "Beam" };
                _inkMat = new Material(Glow.Shared) { name = "Beam Ink" };
                if (_inkMat.HasProperty("_Cull")) _inkMat.SetFloat("_Cull", 1f);
            }
            var go = new GameObject(name) { layer = Layers.Fx };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = _tube;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = cull < 1.5f ? _inkMat : _beamMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            Glow.Tint(r, colour);
            return go.transform;
        }

        /// <summary>A beam tube on its own (radius 1, from its base to 1 along +Y; scale in metres), for other shots: the glow, or the ink rim behind it.</summary>
        internal static Transform Tube(Transform parent, string name, Color colour, bool ink)
        {
            _tube ??= MakeTube(48);
            return TubePiece(parent, name, colour, ink ? 1f : 2f);
        }

        /// <summary>Ink drawn inside out (back faces only), for rims round beams.</summary>
        internal static Material InkMaterial
        {
            get
            {
                if (_inkMat == null)
                {
                    _beamMat = new Material(Glow.Shared) { name = "Beam" };
                    _inkMat = new Material(Glow.Shared) { name = "Beam Ink" };
                    if (_inkMat.HasProperty("_Cull")) _inkMat.SetFloat("_Cull", 1f);
                }
                return _inkMat;
            }
        }

        const int RuneCount = 8;
        // dimmer than the usual arcane glow: a 10 m wide surface at full HDR blooms into a halo well outside the beam
        static readonly Color BeamColour = new(0.2f, 0.55f, 1.9f, 1f), BeamCore = new(1.2f, 1.8f, 3f, 1f), Flare = new(2f, 3f, 8f, 1f);
        static readonly Color TarotBeam = new(1.35f, 0.36f, 0.14f, 1f), TarotCore = new(3f, 1.5f, 0.75f, 1f), TarotFlare = new(7f, 2.6f, 1.1f, 1f);
        bool Tarot => _fx.kind == SpellPlans.TarotCircle;
        Color Line => Tarot ? TarotVfx.Mahogany : Glow.Arcane;
        // the beam's edge sits a little inside the damage radius, so whatever looks inside it really is
        const float BeamFit = 1f;
        const float BeamHeight = 70f;

        public static JudgmentView Create(WorldNet.JudgmentFx fx)
        {
            var go = new GameObject("ArcaneJudgment") { layer = Layers.Fx };
            go.transform.position = fx.center;
            var v = go.AddComponent<JudgmentView>();
            v._fx = fx;

            var c = new GameObject("Circle") { layer = Layers.Fx };
            c.transform.SetParent(go.transform, false);
            c.transform.localPosition = Vector3.up * 0.08f;
            v._circleT = c.transform;
            v._circle = new RuneDrawing(c);
            v._circle.AddRune(CircleStrokes(), SpellPlans.JudgmentRadiusOf(fx.kind) * 2f, 0.11f);
            v._circle.Progress = 0f;
            v._circle.SetColour(v.Line);

            // the beam is a tube of exactly the damage radius (radius 1, height 1 mesh, scaled in metres)
            _tube ??= MakeTube(48);
            v._beam = TubePiece(go.transform, "Beam", v.Tarot ? TarotBeam : BeamColour, 2f);
            v._ink = TubePiece(go.transform, "BeamOutline", Glow.Ink, 1f); // back faces only: an inked rim round the beam
            v._core = TubePiece(go.transform, "BeamCore", v.Tarot ? TarotCore : BeamCore, 2f);
            v._beam.localScale = v._ink.localScale = v._core.localScale = Vector3.zero;
            return v;
        }

        /// <summary>The circle in unit coordinates (radius 1), in the order the pen writes it.</summary>
        static List<List<Vector2>> CircleStrokes()
        {
            var s = new List<List<Vector2>>
            {
                Ring(1f, 0f, 72),
                Ring(0.93f, 180f, 72),
                Ring(0.6f, 90f, 56),
            };
            // runes between the rings, their tops facing outward, a diamond between each pair
            float runeR = 0.765f, runeSize = 0.12f;
            for (int i = 0; i < RuneCount; i++)
            {
                float a = i / (float)RuneCount * Mathf.PI * 2f;
                Vector2 radial = new(Mathf.Cos(a), Mathf.Sin(a));
                Vector2 tangent = new(-radial.y, radial.x);
                foreach (var line in RuneGlyphs.Rune(i))
                {
                    var l = new List<Vector2>(line.Count);
                    foreach (var p in line) l.Add(radial * runeR + (tangent * -p.x + radial * p.y) * runeSize);
                    s.Add(l);
                }
                float d = (i + 0.5f) / RuneCount * Mathf.PI * 2f;
                Vector2 dr = new(Mathf.Cos(d), Mathf.Sin(d)), dt = new(-dr.y, dr.x);
                Vector2 dc = dr * runeR;
                const float dl = 0.06f, dw = 0.035f;
                s.Add(new List<Vector2> { dc + dr * dl, dc + dt * dw, dc - dr * dl, dc - dt * dw, dc + dr * dl });
            }
            // a small star in the middle
            var star = new List<Vector2>();
            for (int k = 0; k <= 5; k++)
            {
                float a = (90f + k * 144f) * Mathf.Deg2Rad;
                star.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.4f);
            }
            s.Add(star);
            return s;
        }

        static List<Vector2> Ring(float r, float startDeg, int steps)
        {
            var pts = new List<Vector2>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                float a = (startDeg + 360f * i / steps) * Mathf.Deg2Rad;
                pts.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
            }
            return pts;
        }

        void LateUpdate()
        {
            float t = (float)(WorldNet.Now - _fx.start);
            float draw = SpellPlans.JudgmentDrawOf(_fx.kind), from = SpellPlans.JudgmentStartOf(_fx.kind), until = SpellPlans.JudgmentEndOf(_fx.kind);
            if (t > until + SpellPlans.JudgmentFade || WorldNet.Instance == null) { Destroy(gameObject); return; }
            float R = SpellPlans.JudgmentRadiusOf(_fx.kind);
            transform.position = _fx.CenterAt(WorldNet.Now); // (Judgement's slides forward)

            // the caster was knocked down while drawing it: the half-written circle shrinks away, no beam
            if (_fizzledAt < 0f && WorldNet.Fizzled(_fx.caster, _fx.start, _fx.start + draw)) _fizzledAt = t;
            if (_fizzledAt >= 0f)
            {
                float s = 1f - Mathf.Clamp01((t - _fizzledAt) / 0.25f);
                if (s <= 0f) { Destroy(gameObject); return; }
                _circleT.localScale = Vector3.one * (s * s);
                _circleT.localRotation = Quaternion.Euler(0f, t * 12f, 0f);
                return;
            }

            // writing the circle, slowly turning
            _circle.Progress = t / draw;
            _circleT.localRotation = Quaternion.Euler(0f, t * 12f, 0f);

            // flare: the circle burns white-hot just before the beam
            bool flare = t >= draw && t < from;
            _circle.SetColour(flare ? (Tarot ? TarotFlare : Flare) * (1f + 0.3f * Mathf.Sin(Time.time * 40f)) : Line);
            if (flare) _circleT.localScale = Vector3.one * (1f + 0.04f * Mathf.Sin(Time.time * 50f));

            // the beam
            float beam = 0f;
            if (t >= from)
            {
                float on = Mathf.Clamp01((t - from) / 0.12f);
                float off = 1f - Mathf.Clamp01((t - until) / SpellPlans.JudgmentFade);
                beam = on * off;
                if (on < 1f && t - Time.deltaTime < from + 0.02f && !Debris.Suppressed)
                    CameraRig.ShakeFrom(transform.position, 0.5f, 0.35f);
            }
            float flick = BeamFit * (1f - 0.03f * (0.5f + 0.5f * Mathf.Sin(Time.time * 60f))); // only ever flickers inward
            // radii in metres: the glow exactly fills the damage area, the ink rim a hair outside it
            float rr = R * beam * flick;
            _beam.localScale = beam > 0f ? new Vector3(rr, BeamHeight, rr) : Vector3.zero;
            _ink.localScale = beam > 0f ? new Vector3(rr + 0.08f, BeamHeight, rr + 0.08f) : Vector3.zero;
            _core.localScale = beam > 0f ? new Vector3(rr * 0.5f, BeamHeight, rr * 0.5f) : Vector3.zero;
            // from inside the beam the ink rim would be a dark wall all round you: only draw it from outside
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 d = cam.transform.position - transform.position;
                _ink.gameObject.SetActive(new Vector2(d.x, d.z).magnitude > rr + 0.3f);
            }
            // the circle shrinks away with the beam
            if (t > until) _circleT.localScale = Vector3.one * Mathf.Max(0.001f, beam);

            // sparks shooting up the column while it fires
            if (beam > 0.5f && !Debris.Suppressed && Random.value < 0.6f)
            {
                Vector2 rp = Random.insideUnitCircle * R * 0.9f;
                var m = Glow.Piece(transform, Glow.CubeMesh, Line, "Mote");
                m.localPosition = new Vector3(rp.x, 0.2f, rp.y);
                m.localScale = new Vector3(0.08f, 0.9f, 0.08f);
                _motes.Add(m);
            }
            for (int i = _motes.Count - 1; i >= 0; i--)
            {
                var m = _motes[i];
                m.localPosition += Vector3.up * (18f * Time.deltaTime);
                m.localScale = Vector3.Scale(m.localScale, new Vector3(1f, 1f, 1f)) * Mathf.Exp(-1.5f * Time.deltaTime);
                if (m.localPosition.y > 14f) { Destroy(m.gameObject); _motes.RemoveAt(i); }
            }
        }

        void OnDestroy() => _circle?.Release();
    }
}
