using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Glowing spell energy, built from plain shapes in HDR colour (Bloom does the glow). Nothing fades:
    /// pieces shrink to nothing, so every pixel stays full strength. Local-only visuals.
    /// </summary>
    public static class Glow
    {
        /// <summary>Arcane: deep saturated blue, well above 1 so it blooms.</summary>
        public static readonly Color Arcane = new(0.25f, 0.75f, 4f, 1f);

        static readonly int ColorId = Shader.PropertyToID("_BaseColor");
        static Material _shared, _doubleSided;
        static Mesh _sphere, _cube;
        static MaterialPropertyBlock _block;

        public static Material Shared => _shared ??= Make("Glow", 2f);
        /// <summary>Both faces (trails and flat strips, whose winding can come out either way).</summary>
        public static Material DoubleSided => _doubleSided ??= Make("Glow (double sided)", 0f);
        public static Mesh SphereMesh => _sphere ??= Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
        public static Mesh CubeMesh => _cube ??= Resources.GetBuiltinResource<Mesh>("Cube.fbx");

        static Mesh _prism;
        /// <summary>A long double-pointed prism along +Z (missile shape), 1 unit long, 0.5 wide.</summary>
        public static Mesh PrismMesh
        {
            get
            {
                if (_prism != null) return _prism;
                Vector3 f = new(0, 0, 0.5f), b = new(0, 0, -0.5f);
                Vector3[] ring = { new(0.25f, 0, 0), new(0, 0.25f, 0), new(-0.25f, 0, 0), new(0, -0.25f, 0) };
                var v = new List<Vector3>();
                var t = new List<int>();
                for (int i = 0; i < 4; i++)
                {
                    Vector3 a = ring[i], c = ring[(i + 1) % 4];
                    int n = v.Count;
                    v.Add(f); v.Add(a); v.Add(c);   // front cap faces (wound to face outward)
                    v.Add(b); v.Add(c); v.Add(a);   // back
                    t.Add(n); t.Add(n + 1); t.Add(n + 2);
                    t.Add(n + 3); t.Add(n + 4); t.Add(n + 5);
                }
                _prism = new Mesh { name = "Prism" };
                _prism.SetVertices(v);
                _prism.SetTriangles(t, 0);
                _prism.RecalculateNormals();
                _prism.RecalculateBounds();
                return _prism;
            }
        }

        static Material Make(string name, float cull)
        {
            var s = Shader.Find("SME/Glow");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Unlit");
            var m = new Material(s) { name = name };
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", cull);
            return m;
        }

        public static void Tint(Renderer r, Color c)
        {
            _block ??= new MaterialPropertyBlock();
            r.GetPropertyBlock(_block);
            _block.SetColor(ColorId, c);
            r.SetPropertyBlock(_block);
        }

        // ---------------- toon outlines ----------------

        /// <summary>The dark ink every effect is outlined in (same as the wind lines).</summary>
        public static readonly Color Ink = new(0.09f, 0.12f, 0.2f, 1f);
        /// <summary>How much wider an outline strip is than the strip it outlines (metres, both sides together).</summary>
        public const float OutlineExtra = 0.06f;
        static Material _hull, _underlay;

        /// <summary>Inverted-hull outline for solid shapes.</summary>
        public static Material Hull => _hull ??= new Material(Shader.Find("SME/Outline") ?? Shader.Find("Universal Render Pipeline/Unlit")) { name = "Outline" };

        /// <summary>Flat ink drawn underneath strips (trails, runes): no depth write, drawn just before the glow on top of it.</summary>
        public static Material Underlay
        {
            get
            {
                if (_underlay != null) return _underlay;
                _underlay = Make("Glow Outline (underlay)", 0f);
                if (_underlay.HasProperty("_ZWrite")) _underlay.SetFloat("_ZWrite", 0f);
                _underlay.renderQueue = 2005; // the glow material draws at 2010, over it
                return _underlay;
            }
        }

        /// <summary>A glowing shape (no collider, no shadows), with a toon outline round it.</summary>
        public static Transform Piece(Transform parent, Mesh mesh, Color colour, string name = "Glow", bool outline = true)
        {
            var go = new GameObject(name) { layer = Layers.Fx };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Shared;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            Tint(r, colour);
            if (outline)
            {
                var o = new GameObject("Outline") { layer = Layers.Fx };
                o.transform.SetParent(go.transform, false);
                o.AddComponent<MeshFilter>().sharedMesh = mesh;
                var or = o.AddComponent<MeshRenderer>();
                or.sharedMaterial = Hull;
                or.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                or.receiveShadows = false;
            }
            return go.transform;
        }

        /// <summary>A glowing trail that narrows to nothing at its tail, with an inked edge underneath it.</summary>
        public static TrailRenderer Trail(Transform parent, Color colour, float time, float width)
        {
            var t = MakeTrail(parent, "Trail", DoubleSided, width, time);
            Tint(t, colour);
            // the outline trail rides on the same point (as a child, so it's dropped along with it)
            var o = MakeTrail(t.transform, "TrailOutline", Underlay, width + OutlineExtra, time);
            Tint(o, Ink);
            return t;
        }

        static TrailRenderer MakeTrail(Transform parent, string name, Material mat, float width, float time)
        {
            var go = new GameObject(name) { layer = Layers.Fx };
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TrailRenderer>();
            t.time = time;
            t.widthMultiplier = width;
            t.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            t.minVertexDistance = 0.05f;
            t.numCapVertices = 2;
            t.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            t.receiveShadows = false;
            t.sharedMaterial = mat;
            return t;
        }

        /// <summary>Leaves a trail (and its outline) behind in the world to shrink away instead of vanishing with its owner.</summary>
        public static void DropTrail(TrailRenderer t)
        {
            if (t == null) return;
            t.transform.SetParent(null, true);
            foreach (var tr in t.GetComponentsInChildren<TrailRenderer>()) tr.emitting = false;
            UnityEngine.Object.Destroy(t.gameObject, t.time + 0.05f);
        }
    }

    // ====================================================================== burst

    /// <summary>A burst of energy: a flash that swells and collapses, and sparks flung out that slow and shrink.</summary>
    public class EnergyBurst : MonoBehaviour
    {
        [Serializable]
        public struct Settings
        {
            public float flashSize, flashTime;
            public int sparkCount;
            public float sparkSize;
            public Vector2 sparkSpeed;
            public float sparkLife;
            public Color colour;
        }

        public static readonly Settings Small = new()
        {
            flashSize = 0.6f, flashTime = 0.12f, sparkCount = 6, sparkSize = 0.2f, sparkSpeed = new Vector2(3f, 6f), sparkLife = 0.25f, colour = Glow.Arcane,
        };
        /// <summary>A missile going off: a tiny explosion.</summary>
        public static readonly Settings Impact = new()
        {
            flashSize = 1.1f, flashTime = 0.16f, sparkCount = 12, sparkSize = 0.3f, sparkSpeed = new Vector2(4f, 9f), sparkLife = 0.32f, colour = Glow.Arcane,
        };
        public static readonly Settings Release = new()
        {
            flashSize = 1.2f, flashTime = 0.2f, sparkCount = 14, sparkSize = 0.32f, sparkSpeed = new Vector2(5f, 10f), sparkLife = 0.4f, colour = Glow.Arcane,
        };

        struct Spark { public Transform t; public Vector3 v; }
        readonly List<Spark> _sparks = new();
        Settings _s;
        Transform _flash;
        float _age;

        public static void Create(Vector3 point, Settings s)
        {
            if (Debris.Suppressed) return;
            var go = new GameObject("EnergyBurst") { layer = Layers.Fx };
            go.transform.position = point;
            var b = go.AddComponent<EnergyBurst>();
            b._s = s;
            if (s.flashSize > 0f)
            {
                b._flash = Glow.Piece(go.transform, Glow.SphereMesh, s.colour, "Flash");
                b._flash.localScale = Vector3.zero;
            }
            for (int i = 0; i < s.sparkCount; i++)
            {
                Vector3 dir = UnityEngine.Random.onUnitSphere;
                var p = Glow.Piece(go.transform, Glow.CubeMesh, s.colour, "Spark");
                p.rotation = Quaternion.LookRotation(dir);
                b._sparks.Add(new Spark { t = p, v = dir * UnityEngine.Random.Range(s.sparkSpeed.x, s.sparkSpeed.y) });
            }
            b.Update();
        }

        void Update()
        {
            float dt = Time.deltaTime;
            _age += dt;
            if (_age >= Mathf.Max(_s.flashTime, _s.sparkCount > 0 ? _s.sparkLife : 0f)) { Destroy(gameObject); return; }
            if (_flash != null)
            {
                // swells over the first third, then collapses
                float t = Mathf.Clamp01(_age / _s.flashTime);
                float swell = t < 0.33f ? t / 0.33f : 1f - (t - 0.33f) / 0.67f;
                _flash.localScale = Vector3.one * (_s.flashSize * Mathf.Max(0f, swell));
            }
            float scale = 1f - Mathf.Clamp01(_age / _s.sparkLife);
            float drag = Mathf.Exp(-4.5f * dt / _s.sparkLife);
            for (int i = 0; i < _sparks.Count; i++)
            {
                var sp = _sparks[i];
                sp.v *= drag;
                sp.t.position += sp.v * dt;
                sp.t.localScale = new Vector3(0.2f, 0.2f, 1f) * (_s.sparkSize * scale);
                _sparks[i] = sp;
            }
        }
    }

    // ====================================================================== charge

    /// <summary>
    /// Power gathering at a point (a hand): streaks rushing in from every side and shrinking into the centre,
    /// a core that swells and pulses as it builds, and runes written out of it that drift off and shrink away.
    /// Follows the point without being parented to it, so the streaks fly straight however the arm swings.
    /// </summary>
    public class ArcaneCharge : MonoBehaviour
    {
        class Line { public Transform t; public Vector3 dir; public float dist; }

        readonly List<Line> _lines = new();
        Func<Vector3> _point;
        Func<bool> _alive;
        float _time, _age, _due;
        Transform _core;
        RuneSpew _runes;
        bool _released;
        Color _colour = Glow.Arcane;

        const float CoreSize = 0.32f, Pulse = 0.15f, PulseSpeed = 8f;
        const float LinesPerSecond = 34f, LineDistance = 1.6f, LineSpeed = 5f, LineLength = 0.45f, LineWidth = 0.035f;

        public static ArcaneCharge Create(Func<Vector3> point, Func<bool> alive, float seconds) => Create(point, alive, seconds, Glow.Arcane, true);

        /// <summary>The same gathering in another colour (fire), optionally without the runes.</summary>
        public static ArcaneCharge Create(Func<Vector3> point, Func<bool> alive, float seconds, Color colour, bool runes)
        {
            var go = new GameObject("ArcaneCharge") { layer = Layers.Fx };
            go.transform.position = point();
            var c = go.AddComponent<ArcaneCharge>();
            c._point = point;
            c._alive = alive;
            c._time = Mathf.Max(0.01f, seconds);
            c._colour = colour;
            c._core = Glow.Piece(go.transform, Glow.SphereMesh, colour, "Core");
            c._core.localScale = Vector3.zero;
            if (runes) c._runes = RuneSpew.Create(point, alive, seconds);
            return c;
        }

        /// <summary>The cast goes off: a burst where the core was, a last spray of runes, and the gathering stops.</summary>
        public void Release()
        {
            if (_released) return;
            _released = true;
            var burst = EnergyBurst.Release;
            burst.colour = _colour;
            EnergyBurst.Create(transform.position, burst);
            if (_runes != null) _runes.Release();
            Destroy(gameObject);
        }

        /// <summary>Cut off (caster died): just stops, runes left to shrink away.</summary>
        public void Cancel()
        {
            if (_runes != null) _runes.Stop();
            Destroy(gameObject);
        }

        void LateUpdate()
        {
            if (_alive != null && !_alive()) { Cancel(); return; }
            float dt = Time.deltaTime;
            _age += dt;
            transform.position = _point();
            float built = Mathf.Clamp01(_age / _time);
            float pulse = 1f + Pulse * Mathf.Sin(_age * Mathf.PI * 2f * PulseSpeed);
            _core.localScale = Vector3.one * (CoreSize * built * pulse);

            _due += LinesPerSecond * Mathf.Lerp(0.33f, 1f, built) * dt;
            while (_due >= 1f)
            {
                _due -= 1f;
                _lines.Add(new Line { t = Glow.Piece(transform, Glow.CubeMesh, _colour, "Streak"), dir = UnityEngine.Random.onUnitSphere, dist = LineDistance });
            }
            // faster towards the end, so the last streaks rush in
            float speed = LineSpeed * Mathf.Lerp(1f, 1.75f, built);
            for (int i = _lines.Count - 1; i >= 0; i--)
            {
                var l = _lines[i];
                l.dist -= speed * dt;
                float outer = l.dist + LineLength;
                if (outer <= 0f) { Destroy(l.t.gameObject); _lines.RemoveAt(i); continue; }
                float inner = Mathf.Max(0f, l.dist); // shrinks into the centre as it arrives
                l.t.position = transform.position + l.dir * ((inner + outer) * 0.5f);
                l.t.rotation = Quaternion.LookRotation(l.dir);
                l.t.localScale = new Vector3(LineWidth, LineWidth, outer - inner);
            }
        }
    }

    // ====================================================================== runes

    /// <summary>Runes written out of a point while it charges: each drawn stroke by stroke, drifting off, slowing, shrinking away.</summary>
    public class RuneSpew : MonoBehaviour
    {
        class Rune { public Transform t; public RuneDrawing d; public Vector3 v; public float age, life, roll; }

        readonly List<Rune> _runes = new();
        Func<Vector3> _point;
        Func<bool> _alive;
        float _time, _age, _owed;
        int _index;
        bool _spewing = true;
        Vector3 _origin;

        const float PerSecond = 14f, Drag = 2f, UpBias = 0.5f, Size = 0.22f, Width = 0.022f, WriteTime = 0.12f;
        static readonly Vector2 Speed = new(1.2f, 2.8f), Life = new(0.7f, 1.2f);

        public static RuneSpew Create(Func<Vector3> point, Func<bool> alive, float seconds)
        {
            if (Debris.Suppressed) return null;
            var go = new GameObject("RuneSpew") { layer = Layers.Fx };
            var s = go.AddComponent<RuneSpew>();
            s._point = point;
            s._alive = alive;
            s._time = Mathf.Max(0.01f, seconds);
            s._origin = point();
            return s;
        }

        public void Release()
        {
            if (!_spewing) return;
            for (int i = 0; i < 8; i++) Spew(1.5f);
            _spewing = false;
        }

        public void Stop() => _spewing = false;

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            _age += dt;
            if (_alive != null && !_alive() || _age > _time + 0.5f) _spewing = false;
            else _origin = _point();
            if (_spewing)
            {
                _owed += PerSecond * Mathf.Lerp(0.33f, 1f, Mathf.Clamp01(_age / _time)) * dt;
                while (_owed >= 1f) { _owed -= 1f; Spew(1f); }
            }
            Quaternion facing = RuneDrawing.FacingCamera();
            float drag = Mathf.Exp(-Drag * dt);
            for (int i = _runes.Count - 1; i >= 0; i--)
            {
                var r = _runes[i];
                r.age += dt;
                if (r.age >= r.life) { r.d.Release(); Destroy(r.t.gameObject); _runes.RemoveAt(i); continue; }
                r.v *= drag;
                r.t.SetPositionAndRotation(r.t.position + r.v * dt, facing * Quaternion.AngleAxis(r.roll, Vector3.up));
                r.d.Progress = r.age / WriteTime;
                // shrink away over the second half of its life
                r.t.localScale = Vector3.one * (1f - Mathf.InverseLerp(0.5f * r.life, r.life, r.age));
            }
            if (!_spewing && _runes.Count == 0) Destroy(gameObject);
        }

        void Spew(float speedScale)
        {
            Vector3 dir = (UnityEngine.Random.onUnitSphere + Vector3.up * UpBias).normalized;
            var go = new GameObject("Rune") { layer = Layers.Fx };
            go.transform.SetParent(transform, false);
            go.transform.position = _origin;
            var d = new RuneDrawing(go);
            d.AddRune(RuneGlyphs.Rune(_index++), Size, Width);
            d.Progress = 0f;
            d.SetColour(Glow.Arcane);
            _runes.Add(new Rune
            {
                t = go.transform, d = d, v = dir * (UnityEngine.Random.Range(Speed.x, Speed.y) * speedScale),
                life = UnityEngine.Random.Range(Life.x, Life.y), roll = UnityEngine.Random.Range(-30f, 30f),
            });
        }

        void OnDestroy()
        {
            foreach (var r in _runes) r.d.Release();
        }
    }

    /// <summary>
    /// Glowing lines drawn flat in an object's local XZ plane that write themselves in stroke by stroke as
    /// <see cref="Progress"/> goes 0 → 1 (the pen moves at one speed across all strokes).
    /// </summary>
    public class RuneDrawing
    {
        class Stroke { public List<Vector3> pts; public float[] dist; public float start, end; public float Length => dist[^1]; }

        readonly List<Stroke> _strokes = new();
        readonly List<Vector3> _verts = new(), _partial = new(), _outVerts = new();
        readonly List<int> _tris = new(), _outTris = new();
        readonly Mesh _mesh, _outline;
        readonly MeshRenderer _renderer;
        float _progress = -1f, _half;

        /// <summary>The object the drawing lies on (move / turn it to move the drawing).</summary>
        public Transform Host { get; }

        public RuneDrawing(GameObject host)
        {
            Host = host.transform;
            _mesh = new Mesh { name = "Rune" };
            _mesh.MarkDynamic();
            host.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = host.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = Glow.DoubleSided;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            // the same strokes, wider, in ink underneath
            var o = new GameObject("Outline") { layer = Layers.Fx };
            o.transform.SetParent(host.transform, false);
            _outline = new Mesh { name = "Rune Outline" };
            _outline.MarkDynamic();
            o.AddComponent<MeshFilter>().sharedMesh = _outline;
            var or = o.AddComponent<MeshRenderer>();
            or.sharedMaterial = Glow.Underlay;
            or.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            or.receiveShadows = false;
            Glow.Tint(or, Glow.Ink);
        }

        public float Progress
        {
            get => Mathf.Max(0f, _progress);
            set
            {
                value = Mathf.Clamp01(value);
                if (value == _progress) return;
                _progress = value;
                Rebuild();
            }
        }

        public void SetColour(Color c) => Glow.Tint(_renderer, c);

        /// <summary>Faces the main camera (the drawing lies in local XZ, top along local forward).</summary>
        public static Quaternion FacingCamera()
        {
            var cam = Camera.main;
            return cam != null ? Quaternion.LookRotation(cam.transform.up, -cam.transform.forward) : Quaternion.identity;
        }

        public void AddRune(List<List<Vector2>> rune, float size, float width)
        {
            _half = width * 0.5f;
            float total = 0f;
            foreach (var line in rune) total += RuneGlyphs.PathLength(line);
            float written = 0f;
            foreach (var line in rune)
            {
                var pts = new List<Vector3>(line.Count);
                foreach (var p in line) pts.Add(new Vector3(p.x, 0f, p.y) * (size * 0.5f));
                var dist = new float[pts.Count];
                for (int i = 1; i < pts.Count; i++) dist[i] = dist[i - 1] + Vector3.Distance(pts[i - 1], pts[i]);
                float from = written / total;
                written += RuneGlyphs.PathLength(line);
                _strokes.Add(new Stroke { pts = pts, dist = dist, start = from, end = written / total });
            }
            _progress = -1f;
        }

        public void Release()
        {
            UnityEngine.Object.Destroy(_mesh);
            UnityEngine.Object.Destroy(_outline);
        }

        void Rebuild()
        {
            _verts.Clear();
            _tris.Clear();
            _outVerts.Clear();
            _outTris.Clear();
            foreach (var s in _strokes)
            {
                float t = s.end > s.start ? Mathf.InverseLerp(s.start, s.end, _progress) : (_progress >= s.end ? 1f : 0f);
                if (t <= 0f) continue;
                Trim(s, t * s.Length);
                Ribbon(_verts, _tris, _half);
                Ribbon(_outVerts, _outTris, _half + Glow.OutlineExtra * 0.35f);
            }
            _mesh.Clear();
            _mesh.SetVertices(_verts);
            _mesh.SetTriangles(_tris, 0);
            _mesh.RecalculateBounds();
            _outline.Clear();
            _outline.SetVertices(_outVerts);
            _outline.SetTriangles(_outTris, 0);
            _outline.RecalculateBounds();
        }

        void Trim(Stroke s, float length)
        {
            _partial.Clear();
            _partial.Add(s.pts[0]);
            for (int i = 1; i < s.pts.Count; i++)
            {
                Vector3 p = s.pts[i];
                bool last = s.dist[i] > length;
                if (last)
                {
                    float seg = s.dist[i] - s.dist[i - 1];
                    p = Vector3.Lerp(s.pts[i - 1], p, seg > 0f ? (length - s.dist[i - 1]) / seg : 1f);
                }
                if ((p - _partial[^1]).sqrMagnitude > 1e-6f) _partial.Add(p);
                if (last) break;
            }
        }

        void Ribbon(List<Vector3> verts, List<int> tris, float half)
        {
            int n = _partial.Count;
            if (n < 2) return;
            // a wider (outline) ribbon also runs a little past each end, so the ink wraps the stroke's tips
            float extend = half - _half;
            int first = verts.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 inc = (i > 0 ? _partial[i] - _partial[i - 1] : _partial[1] - _partial[0]).normalized;
                Vector3 outg = (i < n - 1 ? _partial[i + 1] - _partial[i] : inc).normalized;
                Vector3 tan = inc + outg;
                tan = tan.sqrMagnitude > 1e-4f ? tan.normalized : outg;
                Vector3 side = new(-tan.z, 0f, tan.x);
                Vector3 segSide = new(-outg.z, 0f, outg.x);
                float miter = half / Mathf.Max(0.35f, Vector3.Dot(side, segSide));
                Vector3 p = _partial[i];
                if (extend > 0f && i == 0) p -= outg * extend;
                if (extend > 0f && i == n - 1) p += inc * extend;
                verts.Add(p + side * miter);
                verts.Add(p - side * miter);
            }
            for (int i = 0; i < n - 1; i++)
            {
                int a = first + i * 2;
                tris.Add(a); tris.Add(a + 2); tris.Add(a + 1);
                tris.Add(a + 1); tris.Add(a + 2); tris.Add(a + 3);
            }
        }
    }

    /// <summary>Arcane rune shapes in a -1..1 square, top up: the hook, the hourglass, the bowtie, the orbit.</summary>
    public static class RuneGlyphs
    {
        public static List<List<Vector2>> Rune(int index) => ((index % 4 + 4) % 4) switch
        {
            0 => new List<List<Vector2>> { Concat(Arc(new Vector2(0.1f, 0.5f), 0.32f, 200f, -45f, 24), Spiral(new Vector2(0f, -0.45f), 0.48f, 0.07f, 115f, 600f, 64)) },
            1 => new List<List<Vector2>>
            {
                new() { new Vector2(0f, -1f), new Vector2(0f, 1f) },
                Parabola(new Vector2(0f, 0.12f), 0.58f, 0.85f, 20),
                Parabola(new Vector2(0f, -0.12f), 0.58f, -0.85f, 20),
            },
            2 => new List<List<Vector2>> { new() { new(-0.72f, -0.8f), new(0.82f, 0.6f), new(-0.82f, 0.6f), new(0.72f, -0.8f) } },
            _ => new List<List<Vector2>>
            {
                Arc(Vector2.zero, 0.55f, 90f, 450f, 40),
                Arc(new Vector2(0.45f, 0.45f), 0.3f, 225f, 585f, 24),
                Arc(new Vector2(-0.45f, -0.45f), 0.3f, 45f, 405f, 24),
            },
        };

        static List<Vector2> Concat(List<Vector2> a, List<Vector2> b) { a.AddRange(b); return a; }

        static List<Vector2> Arc(Vector2 c, float r, float from, float to, int steps)
        {
            var pts = new List<Vector2>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(from, to, i / (float)steps) * Mathf.Deg2Rad;
                pts.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
            }
            return pts;
        }

        static List<Vector2> Spiral(Vector2 c, float r0, float r1, float from, float sweep, int steps)
        {
            var pts = new List<Vector2>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps, a = (from + sweep * t) * Mathf.Deg2Rad;
                pts.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * Mathf.Lerp(r0, r1, t));
            }
            return pts;
        }

        static List<Vector2> Parabola(Vector2 v, float halfWidth, float height, int steps)
        {
            var pts = new List<Vector2>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                float x = Mathf.Lerp(-1f, 1f, i / (float)steps);
                pts.Add(v + new Vector2(x * halfWidth, height * x * x));
            }
            return pts;
        }

        public static float PathLength(List<Vector2> line)
        {
            float l = 0f;
            for (int i = 1; i < line.Count; i++) l += Vector2.Distance(line[i - 1], line[i]);
            return l;
        }
    }
}
