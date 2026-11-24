using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Shatter Hex taking hold: splinters of glass rush in from all round and close over the body, which hangs there in
    /// the air while the glass trembles and cracks run out of it. Then it all goes (<see cref="GlassBurst"/>).
    /// </summary>
    public class GlassCase : MonoBehaviour
    {
        class Pane { public Transform t; public Vector3 dir; public Quaternion rot; public float size, delay, from; }

        Combatant _c;
        double _from;
        readonly List<Pane> _panes = new();
        readonly List<GlassCrack> _cracks = new();
        float _out = 1f;
        bool _cracked;

        const int Panes = 44, Cracks = 11;
        static readonly Vector3 Shell = new(0.55f, 1.12f, 0.55f);

        public static void Create(Combatant c, double from)
        {
            if (c == null || Debris.Suppressed) return;
            var go = new GameObject("GlassCase") { layer = Layers.Fx };
            var g = go.AddComponent<GlassCase>();
            g._c = c;
            g._from = from;
            for (int i = 0; i < Panes; i++)
            {
                // spread evenly over the shell
                float y = 1f - 2f * (i + 0.5f) / Panes, a = i * 2.39996f;
                float r = Mathf.Sqrt(1f - y * y);
                var dir = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
                var t = Glow.Piece(go.transform, GlassBurst.Shape(i), GlassBurst.Colour(), "Pane");
                t.localScale = Vector3.zero;
                g._panes.Add(new Pane
                {
                    t = t, dir = dir, size = Random.Range(0.26f, 0.44f), delay = Random.Range(0f, 0.14f), from = Random.Range(2.4f, 4f),
                    rot = Quaternion.LookRotation(dir) * Quaternion.Euler(Random.Range(-22f, 22f), Random.Range(-22f, 22f), Random.Range(0f, 360f)),
                });
            }
            EnergyBurst.Create(g.Middle, new EnergyBurst.Settings
            {
                flashSize = 1.8f, flashTime = 0.14f, sparkCount = 14, sparkSize = 0.3f, sparkSpeed = new Vector2(4f, 9f), sparkLife = 0.3f, colour = SpellPlans.HexWhite,
            });
            CameraRig.ShakeFrom(g.Middle, 0.4f, 0.2f);
            g.LateUpdate();
        }

        Vector3 Middle => _c.RagdollFocus != null ? _c.RagdollFocus.position : _c.transform.position;

        void LateUpdate()
        {
            bool on = _c != null && _c.IsSpawned && _c.IsAlive && _c.Shattering && _c.Status.Value.ShatterFrom == _from;
            if (!on)
            {
                // gone off (the burst has taken over), or let go
                _out -= Time.deltaTime / 0.08f;
                if (_out <= 0f) { Destroy(gameObject); return; }
                foreach (var p in _panes) p.t.localScale = Vector3.one * (p.size * _out);
                foreach (var k in _cracks) k.Draw(1f - _out, 1f);
                return;
            }
            transform.position = Middle;
            float grow = SpellPlans.BodyGrow(_c) * _c.transform.localScale.y;
            float age = (float)(WorldNet.Now - _from), u = Mathf.Clamp01(age / SpellPlans.ShatterHold);
            float strain = Mathf.InverseLerp(0.35f, 1f, u);
            // straining outward, then a breath in just before it goes
            float swell = 1f + 0.16f * strain * strain - 0.3f * Mathf.InverseLerp(0.9f, 1f, u);
            foreach (var p in _panes)
            {
                float k = Mathf.Clamp01((age - p.delay) / 0.13f);
                float e = 1f - (1f - k) * (1f - k);
                Vector3 at = Vector3.Scale(p.dir, Shell) * (Mathf.Lerp(p.from, 1f, e) * swell * grow);
                p.t.localPosition = at + Random.insideUnitSphere * (0.045f * strain);
                p.t.localRotation = Quaternion.AngleAxis((1f - e) * 260f, p.dir) * p.rot;
                p.t.localScale = Vector3.one * (p.size * grow * Mathf.Min(1f, k * 3f));
            }
            if (!_cracked && u > 0.3f)
            {
                _cracked = true;
                for (int i = 0; i < Cracks; i++)
                {
                    Vector3 d = Random.onUnitSphere;
                    _cracks.Add(new GlassCrack(transform, transform.position + Vector3.Scale(d, Shell) * (0.8f * grow), d, Random.Range(0.6f, 1.25f), 0.035f));
                }
            }
            // in jerks, not smoothly
            float run = Mathf.Ceil(Mathf.InverseLerp(0.3f, 0.95f, u) * 4f) / 4f;
            foreach (var k in _cracks) k.Draw(0f, run);
        }

        void OnDestroy()
        {
            foreach (var k in _cracks) k.Destroy();
        }
    }

    /// <summary>A jagged, inked white crack through the air, drawn between two fractions of its length.</summary>
    public class GlassCrack
    {
        readonly ToonLine _line;
        readonly List<Vector3> _path = new(), _part = new();

        public GlassCrack(Transform parent, Vector3 from, Vector3 dir, float length, float width)
        {
            _line = new ToonLine(parent, width, SpellPlans.HexWhite);
            const int steps = 6;
            Vector3 side = Vector3.Cross(dir, Random.onUnitSphere).normalized, up = Vector3.Cross(dir, side);
            _path.Add(from);
            for (int i = 1; i <= steps; i++)
            {
                float wild = length * 0.13f * (i < steps ? 1f : 0.4f);
                _path.Add(from + dir * (length * i / steps) + side * (Random.Range(0.4f, 1f) * wild * (i % 2 == 0 ? 1f : -1f)) + up * Random.Range(-wild, wild));
            }
        }

        public void Draw(float tail, float head)
        {
            if (head <= tail + 0.001f) { _line.Hide(); return; }
            Swoosh.Part(_path, tail, head, _part);
            _line.Set(_part);
        }

        public void Destroy() => _line.Destroy();
    }

    /// <summary>
    /// Shatter Hex going off: the glass bursts outward in every direction (big panes, shards and splinters, inked), bites
    /// to a halt in the air a moment, and rains down; cracks and white spikes shoot out of the middle. Shards that meet
    /// the ground stick in it, and shrink away.
    /// </summary>
    public class GlassBurst : MonoBehaviour
    {
        class Shard { public Transform t; public Vector3 v, axis; public float spin, size; public bool solid, stuck; }

        readonly List<Shard> _shards = new();
        readonly List<(Transform t, Vector3 dir, float length)> _spikes = new();
        readonly List<GlassCrack> _cracks = new();
        Vector3 _point;
        float _age;

        const float Life = 2.2f, Hang = 0.42f, SpikeLife = 0.34f, CrackLife = 0.55f;
        const int Big = 22, Middling = 44, Splinters = 44, Spikes = 18, Cracks = 12;
        static Mesh[] _meshes;
        static readonly Color[] Glass = { new(1.25f, 1.75f, 2.2f, 1f), new(1.9f, 2.1f, 2.3f, 1f), new(0.7f, 1.05f, 1.5f, 1f) };

        public static Mesh Shape(int i)
        {
            _meshes ??= new[] { MakeShard(0.55f, 1f, 0.35f), MakeShard(0.9f, 0.8f, -0.1f), MakeShard(0.4f, 1.2f, 0.1f) };
            return _meshes[i % _meshes.Length];
        }

        public static Color Colour() => Glass[Random.Range(0, Glass.Length)];

        public static void Create(Vector3 point, Vector3 dir)
        {
            var go = new GameObject("GlassBurst") { layer = Layers.Fx };
            go.transform.position = point;
            var b = go.AddComponent<GlassBurst>();
            b._point = point;
            for (int i = 0; i < Big + Middling + Splinters; i++)
            {
                bool big = i < Big, splinter = i >= Big + Middling;
                // all round, a little more of it the way the body's thrown
                Vector3 d = Random.onUnitSphere;
                Vector3 way = (d + dir * Random.Range(0f, 0.6f) + Vector3.up * 0.2f).normalized;
                var t = Glow.Piece(go.transform, Shape(i), Colour(), "Shard");
                t.SetPositionAndRotation(point + Vector3.Scale(d, new Vector3(0.45f, 0.95f, 0.45f)), Random.rotation);
                b._shards.Add(new Shard
                {
                    t = t, axis = Random.onUnitSphere, spin = Random.Range(300f, 1100f), solid = !splinter,
                    v = way * (big ? Random.Range(10f, 20f) : splinter ? Random.Range(16f, 38f) : Random.Range(12f, 30f)),
                    size = big ? Random.Range(0.5f, 0.9f) : splinter ? Random.Range(0.09f, 0.18f) : Random.Range(0.22f, 0.45f),
                });
            }
            for (int i = 0; i < Spikes; i++)
            {
                Vector3 d = Random.onUnitSphere;
                var t = Glow.Piece(go.transform, Glow.PrismMesh, SpellPlans.HexWhite, "Spike");
                t.SetPositionAndRotation(point, Quaternion.LookRotation(d));
                b._spikes.Add((t, d, Random.Range(1.4f, 2.8f)));
            }
            for (int i = 0; i < Cracks; i++)
            {
                Vector3 d = Random.onUnitSphere;
                b._cracks.Add(new GlassCrack(go.transform, point + d * 0.4f, d, Random.Range(1.6f, 3f), 0.06f));
            }
            EnergyBurst.Create(point, new EnergyBurst.Settings
            {
                flashSize = 4f, flashTime = 0.26f, sparkCount = 40, sparkSize = 0.6f, sparkSpeed = new Vector2(10f, 26f), sparkLife = 0.55f, colour = SpellPlans.HexWhite,
            });
            CameraRig.ShakeFrom(point, 1.5f, 0.6f);
            b.Update();
        }

        void Update()
        {
            float dt = Time.deltaTime;
            _age += dt;
            if (_age >= Life) { Destroy(gameObject); return; }
            float s = Mathf.Min(1f, _age * 30f + 0.4f) * (1f - Mathf.InverseLerp(Life * 0.65f, Life, _age));
            // flung out, dragged almost to a stop, then down it comes
            bool hang = _age < Hang;
            float drag = Mathf.Exp(-(hang ? 5f : 0.6f) * dt), fall = hang ? 1.5f : 17f, turn = hang ? Mathf.Lerp(1f, 0.15f, _age / Hang) : 0.8f;
            foreach (var p in _shards)
            {
                p.t.localScale = Vector3.one * (p.size * s);
                if (p.stuck) continue;
                p.v = p.v * drag + Vector3.down * (fall * dt);
                Vector3 step = p.v * dt;
                float dist = step.magnitude;
                if (p.solid && dist > 1e-4f && Physics.Raycast(p.t.position, step / dist, out var hit, dist, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                {
                    // stuck in it, point first
                    p.stuck = true;
                    Vector3 into = (step / dist - hit.normal * 0.6f).normalized;
                    p.t.SetPositionAndRotation(hit.point, Quaternion.FromToRotation(Vector3.up, into) * Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
                    continue;
                }
                p.t.position += step;
                p.t.rotation = Quaternion.AngleAxis(p.spin * turn * dt, p.axis) * p.t.rotation;
            }

            float u = Mathf.Clamp01(_age / SpikeLife), shoot = 1f - Mathf.Pow(1f - Mathf.Clamp01(u / 0.4f), 3f), thin = 1f - Mathf.InverseLerp(0.3f, 1f, u);
            foreach (var (t, dir, length) in _spikes)
            {
                float len = length * shoot * thin;
                t.position = _point + dir * (length * shoot - len * 0.5f + 0.3f);
                t.localScale = new Vector3(0.3f * thin, 0.3f * thin, Mathf.Max(0.001f, len));
            }
            if (_spikes.Count > 0 && u >= 1f)
            {
                foreach (var x in _spikes) Destroy(x.t.gameObject);
                _spikes.Clear();
            }

            float k = Mathf.Clamp01(_age / CrackLife);
            foreach (var c in _cracks) c.Draw(Mathf.InverseLerp(0.35f, 1f, k), Mathf.Clamp01(k / 0.15f));
            if (_cracks.Count > 0 && k >= 1f) ClearCracks();
        }

        void ClearCracks()
        {
            foreach (var c in _cracks) c.Destroy();
            _cracks.Clear();
        }

        void OnDestroy() => ClearCracks();

        /// <summary>A thin triangular splinter (pointing up), centred on its origin (so its ink grows evenly round it).</summary>
        static Mesh MakeShard(float width, float length, float skew)
        {
            Vector3[] tri = { new(-width * 0.5f, -length * 0.4f, 0f), new(width * 0.5f, -length * 0.4f + skew * 0.3f, 0f), new(skew, length * 0.6f, 0f) };
            Vector3 mid = (tri[0] + tri[1] + tri[2]) / 3f;
            for (int i = 0; i < 3; i++) tri[i] -= mid;
            const float half = 0.05f;
            var v = new List<Vector3>();
            void Face(Vector3 a, Vector3 b, Vector3 c)
            {
                // wound to face outward
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), a + b + c) < 0f) (b, c) = (c, b);
                v.Add(a); v.Add(b); v.Add(c);
            }
            Vector3 F(int i) => tri[i] + Vector3.forward * half;
            Vector3 B(int i) => tri[i] + Vector3.back * half;
            Face(F(0), F(1), F(2));
            Face(B(0), B(1), B(2));
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                Face(F(i), F(j), B(j));
                Face(F(i), B(j), B(i));
            }
            var idx = new int[v.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            var m = new Mesh { name = "Glass Shard" };
            m.SetVertices(v);
            m.SetTriangles(idx, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
