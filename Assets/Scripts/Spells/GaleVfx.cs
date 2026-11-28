using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// A puff of wind: inked swooshes curling out from a point in every direction, written on and wiped away
    /// (Ephemerate vanishing and coming back, Cloud Step's cloud, a bolt landing).
    /// </summary>
    public class WindPuff : MonoBehaviour
    {
        class Stroke { public ToonLine line; public readonly List<Vector3> path = new(); }
        readonly List<Stroke> _strokes = new();
        readonly List<Vector3> _part = new();
        float _age, _life;

        /// <param name="flat">Strokes only round the sides (a cloud under the feet), not up and down.</param>
        public static WindPuff Create(Vector3 at, float radius, int strokes, float life, bool flat = false)
        {
            if (Debris.Suppressed) return null;
            var go = new GameObject("WindPuff") { layer = Layers.Fx };
            go.transform.position = at;
            var p = go.AddComponent<WindPuff>();
            p._life = life;
            var local = new List<Vector3>();
            for (int i = 0; i < strokes; i++)
            {
                Vector3 dir = flat ? Quaternion.Euler(0f, i * 360f / strokes + Random.Range(-15f, 15f), 0f) * Vector3.forward
                    : Random.onUnitSphere;
                if (flat) dir.y = Random.Range(-0.05f, 0.15f);
                var s = new Stroke { line = new ToonLine(go.transform, Random.Range(0.06f, 0.1f) * Mathf.Max(0.6f, radius)) };
                Swoosh.Gust(local, radius * Random.Range(0.7f, 1f), radius * 0.25f, Random.value < 0.5f ? 1f : -1f, radius * 0.06f);
                var rot = Quaternion.LookRotation(dir.normalized, Mathf.Abs(dir.normalized.y) > 0.95f ? Vector3.forward : Vector3.up);
                foreach (var q in local) s.path.Add(at + rot * (q + Vector3.forward * radius * 0.15f));
                p._strokes.Add(s);
            }
            return p;
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            float t = _age / _life;
            if (t >= 1f) { Destroy(gameObject); return; }
            Swoosh.Window(t, out float tail, out float head);
            foreach (var s in _strokes)
            {
                Swoosh.Part(s.path, tail, head, _part);
                s.line.Set(_part);
            }
        }

        void OnDestroy() { foreach (var s in _strokes) s.line.Destroy(); }
    }

    /// <summary>Compressed Shot's bolt: a tight streak of air shot from the hand to where it hit, a swirl round it, a puff where it lands.</summary>
    public class WindBolt : MonoBehaviour
    {
        ToonLine _core, _spiral;
        Vector3 _from, _to;
        float _age, _len, _charge;
        bool _landed;
        readonly List<Vector3> _pts = new();

        public static void Create(Vector3 from, Vector3 to, float charge)
        {
            var go = new GameObject("WindBolt") { layer = Layers.Fx };
            var b = go.AddComponent<WindBolt>();
            b._from = from; b._to = to; b._charge = charge;
            b._len = (to - from).magnitude;
            float w = Mathf.Lerp(0.14f, 0.26f, charge);
            b._core = new ToonLine(go.transform, w);
            b._spiral = new ToonLine(go.transform, w * 0.45f);
            CameraRig.ShakeFrom(from, 0.08f + 0.12f * charge, 0.1f);
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            float travel = _age * SpellPlans.ShotSpeed;
            Vector3 dir = _len > 1e-3f ? (_to - _from) / _len : Vector3.forward;
            float head = Mathf.Min(travel, _len), tail = Mathf.Max(0f, travel - 4f - 4f * _charge);
            if (travel >= _len && !_landed)
            {
                _landed = true;
                WindPuff.Create(_to, 0.9f + 0.8f * _charge, 8, 0.45f);
                CameraRig.ShakeFrom(_to, 0.1f + 0.15f * _charge, 0.12f);
            }
            if (tail >= _len) { Destroy(gameObject); return; }
            _pts.Clear();
            _pts.Add(_from + dir * tail);
            _pts.Add(_from + dir * head);
            _core.Set(_pts);
            // a swirl wound round the streak
            var rot = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > 0.95f ? Vector3.forward : Vector3.up);
            _pts.Clear();
            float r = Mathf.Lerp(0.18f, 0.32f, _charge);
            for (float s = tail; s <= head; s += 0.12f)
            {
                float a = s * 4.5f;
                _pts.Add(_from + dir * s + rot * new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f));
            }
            _spiral.Set(_pts);
        }

        void OnDestroy() { _core?.Destroy(); _spiral?.Destroy(); }
    }

    /// <summary>
    /// The Wind and Water states drawn on a body: Compressed Shot charging (wind turning steadily round the body and the
    /// held-out hand, a ball of air gathering in the palm), Cloud Step's cloud, Ephemerate's puffs and the streak it leaves
    /// while gone, and Submerge: the splash going in, the pool (ripples, bubbles) or a ball of water in the air, and the
    /// geyser coming out. The body itself is hidden by the animator meanwhile.
    /// </summary>
    public class GaleBodyFx : MonoBehaviour
    {
        public static readonly Color Deep = new(0.1f, 0.7f, 1.05f, 1f), Water = new(0.35f, 2.1f, 2.5f, 1f), Foam = new(2.4f, 3f, 3.1f, 1f);
        static readonly Color Air = new(2.6f, 2.8f, 3f, 1f);
        Combatant _c;
        readonly List<ToonLine> _swirl = new();
        readonly List<Vector3> _pts = new();
        double _cloudFor = -1, _ephemFor = -1, _ephemBack = -1, _subIn = -1, _subOut = -1;
        Transform _trail, _pool, _poolTop, _ball, _ballCore, _orb;
        (TrailRenderer core, TrailRenderer edge) _trailR;
        readonly List<(Transform t, float age, float life, Vector3 v)> _bubbles = new();
        float _bubbleOwed, _poolSize, _spin, _nextRipple, _nextDrip, _flipFor, _ballGrow;
        bool _subAir;

        void Awake() => _c = GetComponent<Combatant>();

        void LateUpdate()
        {
            if (_c == null || !_c.IsSpawned || Debris.Suppressed) return;
            double now = WorldNet.Now;
            var st = _c.Status.Value;
            Vector3 body = _c.transform.position;
            Charge(_c.IsAlive && _c.ViewPose == SpellPose.WindCharge, _c.ViewPoseTime);
            // Cloud Step: a little cloud left under the feet
            double poseStart = now - _c.ViewPoseTime;
            if (_c.ViewPose == SpellPose.CloudStep && _c.ViewPoseTime < 0.2f && System.Math.Abs(poseStart - _cloudFor) > 0.15)
            {
                _cloudFor = poseStart;
                Cloud(_c.FeetPosition);
            }
            Ephemerate(st, now, body);
            Submerge(st, now);
        }

        // ---------------- Compressed Shot

        void Charge(bool on, float t)
        {
            if (!on)
            {
                foreach (var l in _swirl) l.Destroy();
                _swirl.Clear();
                if (_orb != null) { Destroy(_orb.gameObject); _orb = null; }
                return;
            }
            while (_swirl.Count < 5) _swirl.Add(new ToonLine(transform, 0.06f));
            float k = SpellPlans.ShotCharge(t);
            _spin += Time.deltaTime; // (always the same speed)
            Vector3 c = _c.FeetPosition + Vector3.up * 0.95f;
            // three arcs round the body, longer as it charges, turning steadily
            for (int i = 0; i < 3; i++)
            {
                float a0 = _spin * 2.4f + i * 2.094f;
                Swoosh.Arc(_pts, c + Vector3.up * (i * 0.45f - 0.55f), 0.85f, a0, 0.5f + 1.5f * k, 16, 0.2f);
                _swirl[i].SetWidth(0.035f + 0.045f * k);
                _swirl[i].Set(_pts);
            }
            // two round the hand, tightening as the air's pressed in
            Vector3 hand = SpellVisuals.RightHand(_c);
            Vector3 aim = Quaternion.Euler(_c.ViewPitch, _c.ViewYaw, 0f) * Vector3.forward;
            var rot = Quaternion.LookRotation(aim, Mathf.Abs(aim.y) > 0.95f ? Vector3.forward : Vector3.up);
            float r = 0.3f - 0.13f * k;
            for (int i = 3; i < 5; i++)
            {
                _pts.Clear();
                float a0 = _spin * 5f + i * Mathf.PI;
                for (int s = 0; s <= 12; s++)
                {
                    float a = a0 + s * 0.32f;
                    _pts.Add(hand + rot * new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0.12f - s * 0.015f));
                }
                _swirl[i].SetWidth(0.03f + 0.03f * k);
                _swirl[i].Set(_pts);
            }
            // a ball of air gathering in the palm
            if (_orb == null) _orb = Glow.Piece(null, Glow.SphereMesh, Air, "AirBall");
            _orb.position = hand + aim * 0.12f;
            _orb.localScale = Glow.SphereScale((0.05f + 0.12f * k) * (1f + 0.05f * Mathf.Sin(Time.time * 30f)));
        }

        /// <summary>A little cloud: white outlined lumps that puff out and shrink away, and a flat ring of wind.</summary>
        static void Cloud(Vector3 feet)
        {
            WindPuff.Create(feet + Vector3.up * 0.05f, 0.9f, 7, 0.45f, true);
            var go = new GameObject("Cloud") { layer = Layers.Fx };
            go.transform.position = feet;
            go.AddComponent<CloudPuff>();
        }

        // ---------------- Ephemerate

        void Ephemerate(BodyStatus st, double now, Vector3 body)
        {
            bool gone = st.Ephemeral(now);
            if (gone && _ephemFor != st.EphemFrom)
            {
                _ephemFor = st.EphemFrom;
                WindPuff.Create(body, SpellPlans.EphemPuff, 10, 0.5f);
                CameraRig.ShakeFrom(body, 0.12f, 0.12f);
            }
            if (!gone && st.EphemUntil > 0 && _ephemBack != st.EphemUntil && now >= st.EphemUntil && now < st.EphemUntil + 0.5)
            {
                _ephemBack = st.EphemUntil;
                WindPuff.Create(body, SpellPlans.EphemPuff, 10, 0.5f);
            }
            // while gone: a streak of wind where they are
            if (gone)
            {
                if (_trail == null)
                {
                    _trail = new GameObject("EphemTrail") { layer = Layers.Fx }.transform;
                    _trail.position = body;
                    _trailR = ToonLine.Trail(_trail, 0.22f, 0.35f);
                }
                _trail.position = body;
            }
            else if (_trail != null)
            {
                Glow.DropTrail(_trailR.core);
                Glow.DropTrail(_trailR.edge);
                Destroy(_trail.gameObject, 0.5f);
                _trail = null;
            }
        }

        // ---------------- Submerge

        void Submerge(BodyStatus st, double now)
        {
            bool diving = st.SubUntil > now && now < st.SubFrom;
            bool under = st.Under(now) && _c.IsAlive;
            Vector3 feet = _c.FeetPosition;
            // pool on the ground, ball in the air. The grounded flag flickers on voxel steps and little hops (the ball
            // then grew and shrank over and over), so go by how high the feet really are over the ground, with a gap
            // between the heights that switch it each way, and only once that's held for a moment.
            float drop = Physics.Raycast(feet + Vector3.up * 0.3f, Vector3.down, out var gh, 30f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)
                ? gh.distance - 0.3f : 999f;
            bool high = _subAir ? drop > 0.6f : drop > 1.4f;
            if (!under) { _subAir = drop > 1f; _flipFor = 0f; }
            else if (high != _subAir)
            {
                _flipFor += Time.deltaTime;
                if (_flipFor > (_subAir ? 0.2f : 0.12f)) { _subAir = high; _flipFor = 0f; }
            }
            else _flipFor = 0f;
            bool grounded = !_subAir;
            // in: a splash, droplets thrown up, a ring of water
            if (under && _subIn != st.SubFrom)
            {
                _subIn = st.SubFrom;
                WaterDrops.Burst(feet + Vector3.up * 0.1f, 18, 3.5f, 7f, 0.6f, 0.06f, 0.12f);
                Ripple.Create(feet, 0.3f, SpellPlans.SubPool * 1.25f, 0.6f, 0.09f);
                CameraRig.ShakeFrom(feet, 0.12f, 0.15f);
            }
            // the pool: it opens as they come down into it, and stays under them on the ground
            float want = diving ? Mathf.Clamp01(1f - (float)(st.SubFrom - now) / (SpellPlans.SubDive * 0.6f)) : under && grounded ? 1f : 0f;
            _poolSize = Mathf.MoveTowards(_poolSize, want, Time.deltaTime / 0.15f);
            VoxelPool(feet, SpellPlans.SubPool * _poolSize, under && grounded);
            // rings of ripple running out across it
            if (_poolSize > 0.5f && under && grounded && Time.time >= _nextRipple)
            {
                _nextRipple = Time.time + 0.45f;
                Ripple.Create(feet + Vector3.up * 0.16f, 0.25f, SpellPlans.SubPool * 0.95f, 0.7f, 0.05f);
            }
            // in the air: a ball of water with a bright core, dripping
            // (it grows in and shrinks away, never pops)
            _ballGrow = Mathf.MoveTowards(_ballGrow, under && !grounded ? 1f : 0f, Time.deltaTime / 0.12f);
            if (_ballGrow > 0f)
            {
                // a child of the body, at its middle: it moves exactly as the body (and so the camera) does, whatever
                // order things update in. (Eased after the body before, it swam about on screen and seemed to throb.)
                var root = _c.transform;
                if (_ball == null)
                {
                    _ball = Glow.Piece(root, Glow.SphereMesh, Water, "WaterBall");
                    _ballCore = Glow.Piece(root, Glow.SphereMesh, Foam, "WaterBallCore", false);
                }
                float g = _ballGrow * _ballGrow * (3f - 2f * _ballGrow);
                float unit = 1f / Mathf.Max(0.0001f, root.lossyScale.x); // (world sizes, whatever the root's scale)
                _ball.localPosition = Vector3.zero;
                _ball.localScale = Glow.SphereScale(SpellPlans.SubBall * g) * unit;
                _ballCore.localPosition = Quaternion.Inverse(root.rotation) * (new Vector3(-0.12f, 0.16f, 0f) * g) * unit;
                _ballCore.localScale = Glow.SphereScale(SpellPlans.SubBall * 0.32f * g) * unit;
                if (Time.time >= _nextDrip)
                {
                    _nextDrip = Time.time + 0.07f;
                    WaterDrops.Burst(_c.transform.position + Vector3.down * SpellPlans.SubBall * 0.8f, 1, 0.2f, 1f, 1f, 0.04f, 0.07f);
                }
            }
            else if (_ball != null) { Destroy(_ball.gameObject); Destroy(_ballCore.gameObject); _ball = _ballCore = null; }
            // bubbles rising out of the pool
            if (under && grounded)
            {
                _bubbleOwed += Time.deltaTime * 10f;
                while (_bubbleOwed >= 1f)
                {
                    _bubbleOwed -= 1f;
                    Vector2 o = Random.insideUnitCircle * SpellPlans.SubPool * 0.75f;
                    var b = Glow.Piece(null, Glow.SphereMesh, Foam, "Bubble");
                    b.position = feet + new Vector3(o.x, 0.05f, o.y);
                    _bubbles.Add((b, 0f, Random.Range(0.35f, 0.65f), Vector3.up * Random.Range(0.4f, 1f)));
                }
            }
            for (int i = _bubbles.Count - 1; i >= 0; i--)
            {
                var (t, age, life, v) = _bubbles[i];
                age += Time.deltaTime;
                if (age >= life || t == null) { if (t != null) Destroy(t.gameObject); _bubbles.RemoveAt(i); continue; }
                t.position += v * Time.deltaTime;
                float u = age / life;
                t.localScale = Glow.SphereScale(0.08f * Mathf.Sin(u * Mathf.PI));
                _bubbles[i] = (t, age, life, v);
            }
            // out: a geyser, water spiralling up round them, a wide ring
            if (st.SubUntil > 0 && st.SubUntil <= now && now - st.SubUntil < 0.5 && _subOut != st.SubUntil && st.SubFrom <= st.SubUntil)
            {
                _subOut = st.SubUntil;
                WaterDrops.Burst(feet + Vector3.up * 0.1f, 30, 7f, 13f, 0.35f, 0.07f, 0.15f);
                WaterSpiral.Create(feet, SpellPlans.SubEmergeReach * 0.6f, 2.6f);
                Ripple.Create(feet + Vector3.up * 0.05f, 0.4f, SpellPlans.SubEmergeReach, 0.5f, 0.12f);
                EnergyBurst.Create(feet + Vector3.up * 0.4f, new EnergyBurst.Settings
                {
                    flashSize = SpellPlans.SubEmergeReach * 0.8f, flashTime = 0.15f, sparkCount = 14, sparkSize = 0.25f,
                    sparkSpeed = new Vector2(4f, 9f), sparkLife = 0.35f, colour = Water,
                });
                CameraRig.ShakeFrom(feet, 0.4f, 0.25f);
            }
        }

        // the pool: the ground's top voxels round them turned to water, popping in ahead and shrinking away behind
        class PoolCell { public Transform t; public float g, seed; public bool want, rim; }
        readonly Dictionary<Vector3Int, PoolCell> _poolCells = new();
        readonly List<Vector3Int> _poolGone = new();
        readonly List<(Transform t, float age, float life, float h)> _foam = new();
        float _foamOwed;

        void VoxelPool(Vector3 feet, float radius, bool live)
        {
            var w = VoxelWorld.Current;
            foreach (var c in _poolCells.Values) c.want = false;
            if (w != null && radius > 0.05f)
            {
                const float S = VoxelWorld.Size;
                int n = Mathf.CeilToInt(radius / S);
                Vector3Int mid = w.CellOf(feet + Vector3.up * 0.1f);
                for (int dx = -n; dx <= n; dx++)
                for (int dz = -n; dz <= n; dz++)
                {
                    float d = new Vector2(dx, dz).magnitude * S;
                    if (d > radius) continue;
                    // the top of the ground in this column, near the feet
                    for (int dy = 1; dy >= -12; dy--) // (diving in from the hop: the ground under them)
                    {
                        var cell = new Vector3Int(mid.x + dx, mid.y + dy, mid.z + dz);
                        if (!w.IsSolid(cell) || w.IsSolid(cell + Vector3Int.up)) continue;
                        if (!_poolCells.TryGetValue(cell, out var pc))
                        {
                            pc = new PoolCell { seed = Random.value * 10f };
                            _poolCells[cell] = pc;
                        }
                        bool rim = d > radius - S * 1.2f;
                        if (pc.t == null || pc.rim != rim)
                        {
                            if (pc.t != null) Destroy(pc.t.gameObject);
                            pc.t = Glow.Piece(null, Glow.CubeMesh, rim ? Deep : Water, "PoolVoxel");
                            pc.rim = rim;
                        }
                        pc.want = true;
                        break;
                    }
                }
            }
            // each cell pops up when it's wanted, shrinks back into the ground when it's not; waves run across the water
            _poolGone.Clear();
            float dt = Time.deltaTime;
            foreach (var kv in _poolCells)
            {
                var c = kv.Value;
                c.g = Mathf.MoveTowards(c.g, c.want ? 1f : 0f, dt / (c.want ? 0.1f : 0.18f));
                if (c.g <= 0f && !c.want) { if (c.t != null) Destroy(c.t.gameObject); _poolGone.Add(kv.Key); continue; }
                if (w == null || c.t == null) continue;
                Vector3 at = w.CellCenter(kv.Key);
                float pop = c.g < 1f ? Mathf.Sin(c.g * Mathf.PI) * 0.25f : 0f;  // a little overshoot as it pops in
                float wave = 0.03f * Mathf.Sin(Time.time * 6f + (at.x + at.z) * 3f + c.seed);
                float s = VoxelWorld.Size * (c.g + pop);
                c.t.position = at + Vector3.up * (VoxelWorld.Size * 0.5f + wave - s * 0.45f);
                c.t.localScale = new Vector3(s * 1.02f, s, s * 1.02f);
            }
            foreach (var k in _poolGone) _poolCells.Remove(k);
            // foam: a bright block bobbing up out of the water now and then, shrinking away
            if (live && _poolCells.Count > 0)
            {
                _foamOwed += dt * 9f;
                while (_foamOwed >= 1f)
                {
                    _foamOwed -= 1f;
                    int pick = Random.Range(0, _poolCells.Count), i = 0;
                    foreach (var kv in _poolCells)
                    {
                        if (i++ != pick) continue;
                        if (!kv.Value.want || w == null) break;
                        var f = Glow.Piece(null, Glow.CubeMesh, Foam, "Foam");
                        f.position = w.CellCenter(kv.Key) + Vector3.up * VoxelWorld.Size * 0.6f;
                        _foam.Add((f, 0f, Random.Range(0.3f, 0.5f), Random.Range(0.25f, 0.5f)));
                        break;
                    }
                }
            }
            for (int i = _foam.Count - 1; i >= 0; i--)
            {
                var (t, age, life, h) = _foam[i];
                age += dt;
                if (age >= life || t == null) { if (t != null) Destroy(t.gameObject); _foam.RemoveAt(i); continue; }
                float u = age / life;
                t.position += Vector3.up * (h / life * dt);
                t.rotation = Quaternion.Euler(0f, u * 120f, 0f);
                t.localScale = Vector3.one * (VoxelWorld.Size * 0.55f * Mathf.Sin(u * Mathf.PI));
                _foam[i] = (t, age, life, h);
            }
        }

        void OnDestroy()
        {
            foreach (var c in _poolCells.Values) if (c.t != null) Destroy(c.t.gameObject);
            foreach (var f in _foam) if (f.t != null) Destroy(f.t.gameObject);
            foreach (var l in _swirl) l.Destroy();
            foreach (var t in new[] { _pool, _poolTop, _ball, _ballCore, _orb, _trail }) if (t != null) Destroy(t.gameObject);
            foreach (var b in _bubbles) if (b.t != null) Destroy(b.t.gameObject);
        }
    }

    /// <summary>Water droplets: outlined aqua beads flung out from a point, falling, shrinking away.</summary>
    public class WaterDrops : MonoBehaviour
    {
        readonly List<(Transform t, Vector3 v, float size, float life)> _drops = new();
        float _age;

        /// <param name="spread">0: straight up .. 1: out in every direction (above the point).</param>
        public static void Burst(Vector3 at, int n, float speedMin, float speedMax, float spread, float sizeMin, float sizeMax)
        {
            if (Debris.Suppressed) return;
            var go = new GameObject("WaterDrops") { layer = Layers.Fx };
            var d = go.AddComponent<WaterDrops>();
            for (int i = 0; i < n; i++)
            {
                Vector3 dir = Vector3.Slerp(Vector3.up, new Vector3(Random.Range(-1f, 1f), Random.Range(0f, 0.6f), Random.Range(-1f, 1f)).normalized, spread);
                var t = Glow.Piece(go.transform, Glow.SphereMesh, Random.value < 0.7f ? GaleBodyFx.Water : GaleBodyFx.Foam, "Drop");
                t.position = at + Random.insideUnitSphere * 0.1f;
                d._drops.Add((t, dir.normalized * Random.Range(speedMin, speedMax), Random.Range(sizeMin, sizeMax), Random.Range(0.45f, 0.8f)));
            }
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            bool any = false;
            for (int i = 0; i < _drops.Count; i++)
            {
                var (t, v, size, life) = _drops[i];
                if (t == null) continue;
                float u = _age / life;
                if (u >= 1f) { Destroy(t.gameObject); continue; }
                any = true;
                v += Vector3.down * 20f * Time.deltaTime;
                t.position += v * Time.deltaTime;
                // stretched along the way it flies, shrinking at the end
                t.rotation = Quaternion.LookRotation(v.sqrMagnitude > 0.01f ? v : Vector3.down);
                float s = size * (1f - u * u);
                t.localScale = Vector3.Scale(Glow.SphereScale(s), new Vector3(1f, 1f, 1.6f));
                _drops[i] = (t, v, size, life);
            }
            if (!any) Destroy(gameObject);
        }
    }

    /// <summary>A ring of water spreading flat across the ground (an inked aqua line), thinning as it goes.</summary>
    public class Ripple : MonoBehaviour
    {
        ToonLine _line;
        readonly List<Vector3> _pts = new();
        float _age, _r0, _r1, _life, _w;

        public static void Create(Vector3 at, float from, float to, float life, float width)
        {
            if (Debris.Suppressed) return;
            var go = new GameObject("Ripple") { layer = Layers.Fx };
            go.transform.position = at;
            var r = go.AddComponent<Ripple>();
            r._r0 = from; r._r1 = to; r._life = life; r._w = width;
            r._line = new ToonLine(go.transform, width, new Color(0.8f, 2.6f, 2.9f, 1f));
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            float u = _age / _life;
            if (u >= 1f) { Destroy(gameObject); return; }
            float e = 1f - (1f - u) * (1f - u);
            Swoosh.Arc(_pts, transform.position, Mathf.Lerp(_r0, _r1, e), 0f, Mathf.PI * 2f, 40);
            _line.SetWidth(_w * (1f - u));
            _line.Set(_pts);
        }

        void OnDestroy() => _line?.Destroy();
    }

    /// <summary>Water spiralling up round a point (two inked aqua strokes winding up, written on and wiped away).</summary>
    public class WaterSpiral : MonoBehaviour
    {
        readonly List<(ToonLine line, List<Vector3> path)> _strokes = new();
        readonly List<Vector3> _part = new();
        float _age;
        const float Life = 0.6f;

        public static void Create(Vector3 feet, float radius, float height)
        {
            if (Debris.Suppressed) return;
            var go = new GameObject("WaterSpiral") { layer = Layers.Fx };
            var s = go.AddComponent<WaterSpiral>();
            for (int k = 0; k < 2; k++)
            {
                var path = new List<Vector3>();
                for (int i = 0; i <= 40; i++)
                {
                    float u = i / 40f, a = k * Mathf.PI + u * Mathf.PI * 3f;
                    float r = radius * (1f - 0.55f * u);
                    path.Add(feet + new Vector3(Mathf.Cos(a) * r, u * height, Mathf.Sin(a) * r));
                }
                s._strokes.Add((new ToonLine(go.transform, 0.11f, new Color(0.8f, 2.6f, 2.9f, 1f)), path));
            }
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            float t = _age / Life;
            if (t >= 1f) { Destroy(gameObject); return; }
            Swoosh.Window(t, out float tail, out float head);
            foreach (var (line, path) in _strokes)
            {
                Swoosh.Part(path, tail, head, _part);
                line.Set(_part);
            }
        }

        void OnDestroy() { foreach (var s in _strokes) s.line.Destroy(); }
    }

    /// <summary>Cloud Step's cloud: a few white outlined lumps that puff out under the feet and shrink away.</summary>
    public class CloudPuff : MonoBehaviour
    {
        static readonly Color White = new(2.6f, 2.7f, 2.8f, 1f);
        readonly List<(Transform t, Vector3 at, float size)> _lumps = new();
        float _age;
        const float Life = 0.55f;

        void Start()
        {
            for (int i = 0; i < 6; i++)
            {
                float a = i / 6f * Mathf.PI * 2f + Random.Range(-0.3f, 0.3f);
                var t = Glow.Piece(transform, Glow.SphereMesh, White, "Lump");
                _lumps.Add((t, new Vector3(Mathf.Cos(a), Random.Range(-0.05f, 0.08f), Mathf.Sin(a)) * Random.Range(0.25f, 0.45f), Random.Range(0.16f, 0.26f)));
            }
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            float u = _age / Life;
            if (u >= 1f) { Destroy(gameObject); return; }
            float grow = 1f - Mathf.Pow(1f - Mathf.Clamp01(u / 0.3f), 3f), shrink = 1f - Mathf.Clamp01((u - 0.35f) / 0.65f);
            foreach (var (t, at, size) in _lumps)
            {
                t.localPosition = at * (0.6f + 0.8f * grow);
                t.localScale = Glow.SphereScale(Mathf.Max(0.001f, size * grow * shrink));
            }
        }
    }

    /// <summary>
    /// Bubble Bomb: a watery red bubble with a little bomb in it, wobbling as it drifts; once it's live it throbs. Set off:
    /// a burst exactly the blast's size; popped: it just pops.
    /// </summary>
    public class MineView : MonoBehaviour
    {
        static readonly Color BombBlack = new(0.08f, 0.07f, 0.08f, 1f), Fuse = new(3.2f, 1.8f, 0.4f, 1f);
        WorldNet.MineFx _fx;
        Transform _bubble, _bomb, _spark;
        float _endAge = -1f;

        public static MineView Create(WorldNet.MineFx fx)
        {
            var go = new GameObject("BubbleBomb") { layer = Layers.Fx };
            var v = go.AddComponent<MineView>();
            v._fx = fx;
            v._bomb = Glow.Piece(go.transform, Glow.SphereMesh, BombBlack, "Bomb");
            v._bomb.localScale = Glow.SphereScale(SpellPlans.MineRadius * 0.4f);
            v._spark = Glow.Piece(go.transform, Glow.SphereMesh, Fuse, "Spark");
            v._spark.localPosition = new Vector3(0.05f, SpellPlans.MineRadius * 0.5f, 0f);
            v._spark.localScale = Glow.SphereScale(0.04f);
            v._bubble = Glow.Piece(go.transform, Glow.SphereMesh, SpellPlans.MineRed, "Bubble");
            v.LateUpdate();
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            transform.position = WorldNet.MinePosition(_fx, now);
            float t = (float)(now - _fx.start);
            if (_fx.ended >= 0)
            {
                if (_endAge < 0f)
                {
                    _endAge = 0f;
                    if (_fx.burst)
                    {
                        EnergyBurst.Create(transform.position, new EnergyBurst.Settings
                        {
                            flashSize = SpellPlans.MineBlast, flashTime = 0.2f, sparkCount = 26, sparkSize = 0.32f,
                            sparkSpeed = new Vector2(5f, 11f), sparkLife = 0.4f, colour = SpellPlans.MineRed,
                        });
                        CameraRig.ShakeFrom(transform.position, 0.4f, 0.25f);
                        Destroy(gameObject);
                        return;
                    }
                    var small = EnergyBurst.Small;
                    small.colour = SpellPlans.MineRed;
                    EnergyBurst.Create(transform.position, small);
                }
                _endAge += Time.deltaTime;
                float k = 1f - _endAge / 0.15f;
                if (k <= 0f) { Destroy(gameObject); return; }
                transform.localScale = Vector3.one * k;
                return;
            }
            // grows in, wobbles; once live it throbs
            float grow = Mathf.Clamp01(t / 0.2f);
            bool live = t >= SpellPlans.MineArm;
            float wob = live ? 0.07f * Mathf.Sin(Time.time * 14f) : 0.04f * Mathf.Sin(Time.time * 6f);
            _bubble.localScale = Vector3.Scale(Glow.SphereScale(SpellPlans.MineRadius * grow), new Vector3(1f + wob, 1f - wob, 1f + wob));
            _bomb.localRotation = Quaternion.Euler(0f, t * 60f, Mathf.Sin(t * 2f) * 15f);
            _spark.localScale = Glow.SphereScale(0.035f + (live ? 0.025f * Mathf.Abs(Mathf.Sin(Time.time * 20f)) : 0f));
        }
    }
}
