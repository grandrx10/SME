using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// A cartoon stroke: a white line, tapered at the ends, with a dark outline round it. Built from two
    /// camera-facing LineRenderers — the wider dark one drawn first, the white one on top — so every wind line
    /// in the game reads as an inked drawing rather than smoke.
    /// </summary>
    public sealed class ToonLine
    {
        public static readonly Color Ink = new(0.09f, 0.12f, 0.2f, 1f);
        public static readonly Color Paper = new(0.97f, 0.99f, 1f, 1f);
        const float OutlineExtra = 0.055f;

        static Material _core, _edge;
        static AnimationCurve _taper;
        readonly GameObject _go;
        readonly LineRenderer _coreLine, _edgeLine;
        Vector3[] _buf = new Vector3[64];

        static Material Make(string name, int queue)
        {
            var s = Shader.Find("SME/Glow") ?? Shader.Find("Universal Render Pipeline/Unlit");
            var m = new Material(s) { name = name, renderQueue = queue };
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            return m;
        }

        /// <summary>Thin at the tail, full through the middle, rounded off at the head.</summary>
        static AnimationCurve Taper => _taper ??= new AnimationCurve(
            new Keyframe(0f, 0.12f), new Keyframe(0.3f, 1f), new Keyframe(0.85f, 1f), new Keyframe(1f, 0.55f));

        public static Material CoreMaterial => _core ??= Make("ToonLine", 3002);
        public static Material EdgeMaterial => _edge ??= Make("ToonLine Outline", 3001);

        /// <summary>
        /// An inked trail: a white trail with a wider dark one behind it, both tapering to nothing at the tail.
        /// Returns (core, outline); both live under <paramref name="parent"/>.
        /// </summary>
        public static (TrailRenderer core, TrailRenderer edge) Trail(Transform parent, float width, float time)
        {
            TrailRenderer Make1(string name, Material mat, float w, Color c)
            {
                var go = new GameObject(name) { layer = Layers.Fx };
                go.transform.SetParent(parent, false);
                var t = go.AddComponent<TrailRenderer>();
                t.sharedMaterial = mat;
                t.time = time;
                t.widthMultiplier = w;
                t.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.15f, 0.9f), new Keyframe(1f, 0f));
                t.minVertexDistance = 0.04f;
                t.numCapVertices = 4;
                t.numCornerVertices = 2;
                t.alignment = LineAlignment.View;
                t.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                t.receiveShadows = false;
                Glow.Tint(t, c);
                return t;
            }
            return (Make1("Trail", CoreMaterial, width, Paper), Make1("TrailOutline", EdgeMaterial, width + OutlineExtra, Ink));
        }

        public ToonLine(Transform parent, float width, Color? colour = null)
        {
            _core ??= Make("ToonLine", 3002);
            _edge ??= Make("ToonLine Outline", 3001);
            _go = new GameObject("ToonLine") { layer = Layers.Fx };
            _go.transform.SetParent(parent, false);
            _edgeLine = MakeLine("Outline", _edge, width + OutlineExtra, Ink);
            _coreLine = MakeLine("Line", _core, width, colour ?? Paper);
        }

        LineRenderer MakeLine(string name, Material mat, float width, Color c)
        {
            var go = new GameObject(name) { layer = Layers.Fx };
            go.transform.SetParent(_go.transform, false);
            var l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = true;
            l.alignment = LineAlignment.View;
            l.sharedMaterial = mat;
            l.widthCurve = Taper;
            l.widthMultiplier = width;
            l.numCapVertices = 4;
            l.numCornerVertices = 3;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            l.positionCount = 0;
            Glow.Tint(l, c);
            return l;
        }

        public void SetWidth(float width)
        {
            _coreLine.widthMultiplier = width;
            _edgeLine.widthMultiplier = width + OutlineExtra;
        }

        public void Set(List<Vector3> pts)
        {
            int n = pts.Count;
            if (n < 2) { Hide(); return; }
            if (_buf.Length < n) _buf = new Vector3[n * 2];
            for (int i = 0; i < n; i++) _buf[i] = pts[i];
            _coreLine.positionCount = _edgeLine.positionCount = n;
            _coreLine.SetPositions(_buf);
            _edgeLine.SetPositions(_buf);
        }

        public void Hide() => _coreLine.positionCount = _edgeLine.positionCount = 0;

        public void Destroy()
        {
            if (_go != null) Object.Destroy(_go);
        }
    }

    /// <summary>Shapes for wind strokes, and drawing part of a path (a stroke that writes itself on and wipes itself off).</summary>
    public static class Swoosh
    {
        /// <summary>
        /// A gust line in local space: a gently wavy streak along +Z for <paramref name="length"/>, ending in a curl of
        /// radius <paramref name="curl"/> that winds up (<paramref name="up"/> = 1) or down (-1) and back over itself.
        /// </summary>
        public static void Gust(List<Vector3> pts, float length, float curl, float up, float wave)
        {
            pts.Clear();
            const int straight = 12, curlSteps = 22;
            for (int i = 0; i <= straight; i++)
            {
                float z = length * i / straight;
                pts.Add(new Vector3(0f, Mathf.Sin(i / (float)straight * Mathf.PI * 1.5f) * wave, z));
            }
            Vector3 end = pts[^1];
            Vector3 c = end + new Vector3(0f, curl * up, 0f);
            const float sweep = 300f * Mathf.Deg2Rad;
            for (int i = 1; i <= curlSteps; i++)
            {
                float a = sweep * i / curlSteps;
                float r = curl * (1f - 0.45f * i / curlSteps); // spirals in
                pts.Add(c + new Vector3(0f, -Mathf.Cos(a) * r * up, Mathf.Sin(a) * r));
            }
        }

        /// <summary>The part of <paramref name="path"/> between fractions <paramref name="tail"/> and <paramref name="head"/> of its length.</summary>
        public static void Part(List<Vector3> path, float tail, float head, List<Vector3> output)
        {
            output.Clear();
            if (path.Count < 2 || head <= tail) return;
            float total = 0f;
            for (int i = 1; i < path.Count; i++) total += Vector3.Distance(path[i - 1], path[i]);
            float s0 = tail * total, s1 = head * total, s = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                float seg = Vector3.Distance(path[i - 1], path[i]);
                float a = s, b = s + seg;
                s = b;
                if (b < s0) continue;
                if (a > s1) break;
                if (output.Count == 0) output.Add(Vector3.Lerp(path[i - 1], path[i], seg > 0f ? Mathf.Clamp01((s0 - a) / seg) : 0f));
                output.Add(b <= s1 ? path[i] : Vector3.Lerp(path[i - 1], path[i], seg > 0f ? (s1 - a) / seg : 1f));
            }
        }

        /// <summary>How much of a stroke is showing over its life: it writes on fast, then wipes away from the tail.</summary>
        public static void Window(float t, out float tail, out float head)
        {
            head = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / 0.55f), 3f);
            float k = Mathf.Clamp01((t - 0.35f) / 0.65f);
            tail = k * k;
        }

        /// <summary>A circle (or part of one) in the horizontal plane.</summary>
        public static void Arc(List<Vector3> pts, Vector3 centre, float radius, float from, float sweep, int steps, float rise = 0f)
        {
            pts.Clear();
            for (int i = 0; i <= steps; i++)
            {
                float u = i / (float)steps, a = from + sweep * u;
                pts.Add(centre + new Vector3(Mathf.Cos(a) * radius, rise * u, Mathf.Sin(a) * radius));
            }
        }
    }

    // ====================================================================== Shove

    /// <summary>
    /// The Shove gust: no box, just streams of wind. A dozen points spread across the gust's face rush forward with
    /// it, each spiralling as it goes, and draw inked trails (white, dark outline, tapering away) behind them.
    /// When the gust ends the trails are left to shrink away rather than vanish.
    /// </summary>
    public class GustView : MonoBehaviour
    {
        class Stream
        {
            public Transform point;
            public TrailRenderer core, edge;
            public Vector2 home;
            public float radius, speed, phase;
        }

        const int Streams = 12;
        WorldNet.GustFx _fx;
        readonly List<Stream> _streams = new();
        bool _ended;
        float _endedAt;

        public static GustView Create(WorldNet.GustFx fx)
        {
            var go = new GameObject("Gust") { layer = Layers.Fx };
            go.transform.SetPositionAndRotation(fx.origin, Quaternion.LookRotation(fx.dir));
            var v = go.AddComponent<GustView>();
            v._fx = fx;
            var h = SpellPlans.GustHalfSize;
            for (int i = 0; i < Streams; i++)
            {
                // spread over the face of the gust (a jittered grid, so it reads as one wide wall of wind)
                float gx = (i % 4 + 0.5f) / 4f * 2f - 1f, gy = (i / 4 + 0.5f) / 3f * 2f - 1f;
                var p = new GameObject("Stream") { layer = Layers.Fx }.transform;
                p.SetParent(go.transform, false);
                var (core, edge) = ToonLine.Trail(p, Random.Range(0.06f, 0.1f), Random.Range(0.18f, 0.28f));
                var s = new Stream
                {
                    point = p, core = core, edge = edge,
                    home = new Vector2((gx + Random.Range(-0.15f, 0.15f)) * h.x * 0.85f, (gy + Random.Range(-0.15f, 0.15f)) * h.y * 0.85f),
                    radius = Random.Range(0.12f, 0.32f),
                    speed = Random.Range(14f, 22f) * (Random.value < 0.5f ? 1f : -1f),
                    phase = Random.value * Mathf.PI * 2f,
                };
                v._streams.Add(s);
                v.Place(s, 0f);
                core.Clear();
                edge.Clear();
            }
            return v;
        }

        void Place(Stream s, float t)
        {
            float a = s.phase + t * s.speed;
            // a corkscrew round its home spot, pushed out at the very front of the gust
            var local = new Vector3(s.home.x + Mathf.Cos(a) * s.radius, s.home.y + Mathf.Sin(a) * s.radius,
                SpellPlans.GustHalfSize.z * (0.6f + 0.4f * Mathf.Sin(a * 0.5f)));
            s.point.localPosition = local;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            if (!_ended)
            {
                transform.position = SpellPlans.GustCenter(_fx.origin, _fx.dir, _fx.start, now);
                foreach (var s in _streams) Place(s, t);
                if (t > _fx.life)
                {
                    _ended = true;
                    _endedAt = Time.time;
                    foreach (var s in _streams) { s.core.emitting = false; s.edge.emitting = false; }
                }
                return;
            }
            // the trails shrink away where they were drawn
            if (Time.time - _endedAt > 0.35f) Destroy(gameObject);
        }
    }

    // ====================================================================== Tornado

    /// <summary>
    /// The tornado, cartoon style: a stack of inked rings, small at the bottom and wide at the top, each spinning
    /// with a gap in it and swaying, plus loose swoosh strokes spiralling up its side. It also drags any loose
    /// rubble around itself (each client spins its own rubble).
    /// </summary>
    public class TornadoView : MonoBehaviour
    {
        const int Rings = 8;
        class Stroke { public ToonLine line; public float born, life, from, height, radius; }

        WorldNet.TornadoFx _fx;
        readonly ToonLine[] _rings = new ToonLine[Rings];
        ToonLine _ground;
        readonly List<Stroke> _strokes = new();
        readonly List<Vector3> _pts = new(), _part = new();
        readonly List<(Debris d, float phase, float height)> _held = new();
        float _owed;
        bool _ended;

        /// <summary>Size (voxels) of the biggest lump it's carrying right now (the server uses the host's for damage).</summary>
        public int CarriedMaxCells { get; private set; }

        public static TornadoView Create(WorldNet.TornadoFx fx)
        {
            var go = new GameObject("Tornado") { layer = Layers.Fx };
            go.transform.position = fx.origin;
            var v = go.AddComponent<TornadoView>();
            v._fx = fx;
            for (int i = 0; i < Rings; i++) v._rings[i] = new ToonLine(go.transform, Mathf.Lerp(0.07f, 0.14f, i / (float)(Rings - 1)));
            v._ground = new ToonLine(go.transform, 0.07f);
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            transform.position = SpellPlans.TornadoBase(_fx.origin, _fx.dir, _fx.start, now);
            float form = Mathf.Clamp01(t / SpellPlans.TornadoForm);
            float gone = Mathf.Clamp01((t - SpellPlans.TornadoLife) / 0.35f); // shrinks to nothing at the end
            float size = form * (1f - gone);

            if (!_ended && t > SpellPlans.TornadoLife)
            {
                _ended = true;
                ReleaseDebris();
            }
            if (_ended && gone >= 1f && _strokes.Count == 0) { Destroy(gameObject); return; }

            Vector3 b = transform.position;
            // the stacked rings
            for (int i = 0; i < Rings; i++)
            {
                float k = i / (float)(Rings - 1);
                // they appear from the bottom up as it forms
                float show = Mathf.Clamp01(form * Rings - i) * (1f - gone);
                if (show <= 0.01f) { _rings[i].Hide(); continue; }
                float h = k * SpellPlans.TornadoHeight;
                float r = Mathf.Lerp(0.3f, 2.4f, Mathf.Pow(k, 1.25f)) * show;
                Vector3 sway = new(Mathf.Sin(t * 2.3f + k * 2.5f) * 0.35f * k, 0f, Mathf.Cos(t * 1.9f + k * 2f) * 0.35f * k);
                float spin = t * (9f - k * 4f) + i * 1.3f;
                Swoosh.Arc(_pts, b + Vector3.up * h + sway, r, spin, Mathf.Deg2Rad * 290f, 30, 0.25f);
                _rings[i].Set(_pts);
            }
            // a swirl on the ground round its foot
            if (size > 0.01f)
            {
                Swoosh.Arc(_pts, b + Vector3.up * 0.06f, 2.6f * size, -t * 7f, Mathf.Deg2Rad * 220f, 26);
                _ground.Set(_pts);
            }
            else _ground.Hide();

            // loose strokes winding up the side
            if (!_ended && form > 0.3f)
            {
                _owed += 7f * Time.deltaTime;
                while (_owed >= 1f)
                {
                    _owed -= 1f;
                    float hh = Random.Range(0.2f, SpellPlans.TornadoHeight - 1f);
                    _strokes.Add(new Stroke
                    {
                        line = new ToonLine(transform, Random.Range(0.05f, 0.08f)),
                        born = Time.time, life = Random.Range(0.45f, 0.65f), from = Random.Range(0f, Mathf.PI * 2f),
                        height = hh, radius = Mathf.Lerp(0.6f, 2.8f, hh / SpellPlans.TornadoHeight),
                    });
                }
            }
            for (int i = _strokes.Count - 1; i >= 0; i--)
            {
                var s = _strokes[i];
                float u = (Time.time - s.born) / s.life;
                if (u >= 1f) { s.line.Destroy(); _strokes.RemoveAt(i); continue; }
                Swoosh.Arc(_pts, b + Vector3.up * s.height, s.radius * Mathf.Max(0.05f, size), s.from + t * 6f, Mathf.Deg2Rad * 200f, 22, 0.9f);
                Swoosh.Window(u, out float tail, out float head);
                Swoosh.Part(_pts, tail, head, _part);
                s.line.Set(_part);
            }

            if (!_ended) CarryDebris(t);
        }

        void CarryDebris(float t)
        {
            if (t > 0.3f && _held.Count < 80)
                foreach (var d in Debris.All)
                {
                    if (d == null || d.Body == null || d.Held) continue;
                    Vector3 rel = d.Body.position - transform.position;
                    if (rel.y < -1f || rel.y > SpellPlans.TornadoHeight) continue;
                    if (new Vector2(rel.x, rel.z).magnitude > 4.5f) continue;
                    d.Held = true;
                    _held.Add((d, Random.value * Mathf.PI * 2f, Random.Range(0.6f, SpellPlans.TornadoHeight - 0.5f)));
                    if (_held.Count >= 80) break;
                }
            int biggest = 0;
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                var (d, phase, height) = _held[i];
                if (d == null || d.Body == null) { _held.RemoveAt(i); continue; }
                d.KeepAlive(2f);
                d.Body.useGravity = false;
                float a = phase + t * (5f - height * 0.3f);
                float radius = 0.9f + height * 0.35f;
                Vector3 target = transform.position + new Vector3(Mathf.Cos(a) * radius, height, Mathf.Sin(a) * radius);
                d.Body.linearVelocity = (target - d.Body.position) * 6f;
                biggest = Mathf.Max(biggest, d.CellCount);
            }
            CarriedMaxCells = biggest;
        }

        void ReleaseDebris()
        {
            foreach (var (d, _, _) in _held)
                if (d != null && d.Body != null)
                {
                    Vector3 o = d.Body.position - transform.position;
                    o.y = 0f;
                    d.Body.linearVelocity = o.normalized * 9f + Vector3.up * 4f;
                    d.Body.useGravity = true;
                    d.Held = false;
                }
            _held.Clear();
            CarriedMaxCells = 0;
        }

        void OnDestroy()
        {
            foreach (var r in _rings) r?.Destroy();
            _ground?.Destroy();
            foreach (var s in _strokes) s.line.Destroy();
            ReleaseDebris();
        }
    }

    // ====================================================================== Nomadic Dash

    /// <summary>Wind on a dashing body: two inked rings whipping round the waist and chest, and speed lines streaming off behind.</summary>
    public class DashWind
    {
        class Stroke { public ToonLine line; public Vector3 offset; public float born, life, length; }

        readonly Transform _root;
        readonly ToonLine[] _rings = new ToonLine[2];
        readonly List<Stroke> _lines = new();
        readonly List<Vector3> _pts = new(), _part = new();
        float _owed, _age;

        public DashWind(Transform parent)
        {
            _root = new GameObject("DashWind") { layer = Layers.Fx }.transform;
            _root.SetParent(parent, false);
            for (int i = 0; i < 2; i++) _rings[i] = new ToonLine(_root, 0.06f);
        }

        /// <param name="active">Still dashing (no new lines once it stops; the rest finish and the rings shrink).</param>
        /// <returns>False once everything has finished.</returns>
        public bool Tick(Vector3 hips, Vector3 velocity, bool active)
        {
            _age += Time.deltaTime;
            Vector3 dir = new(velocity.x, 0f, velocity.z);
            dir = dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.forward;
            float ring = active ? Mathf.Clamp01(_age / 0.06f) : 0f;
            for (int i = 0; i < 2; i++)
            {
                if (ring <= 0f) { _rings[i].Hide(); continue; }
                float spin = _age * (i == 0 ? 22f : -18f);
                Swoosh.Arc(_pts, hips + Vector3.up * (i == 0 ? -0.25f : 0.35f), (i == 0 ? 0.62f : 0.5f) * ring, spin, Mathf.Deg2Rad * 250f, 24, 0.15f);
                _rings[i].Set(_pts);
            }
            if (active)
            {
                _owed += 40f * Time.deltaTime;
                while (_owed >= 1f)
                {
                    _owed -= 1f;
                    Vector3 side = Vector3.Cross(Vector3.up, dir);
                    _lines.Add(new Stroke
                    {
                        line = new ToonLine(_root, Random.Range(0.04f, 0.07f)),
                        offset = hips + side * Random.Range(-0.5f, 0.5f) + Vector3.up * Random.Range(-0.8f, 0.7f),
                        born = Time.time, life = Random.Range(0.18f, 0.28f), length = Random.Range(1.2f, 2.2f),
                    });
                }
            }
            for (int i = _lines.Count - 1; i >= 0; i--)
            {
                var s = _lines[i];
                float u = (Time.time - s.born) / s.life;
                if (u >= 1f) { s.line.Destroy(); _lines.RemoveAt(i); continue; }
                // streaming straight back from where it started
                _pts.Clear();
                _pts.Add(s.offset - dir * s.length);
                _pts.Add(s.offset);
                Swoosh.Window(u, out float tail, out float head);
                Swoosh.Part(_pts, tail, head, _part);
                s.line.Set(_part);
            }
            return active || _lines.Count > 0;
        }

        public void Destroy()
        {
            if (_root != null) Object.Destroy(_root.gameObject);
        }
    }
}
