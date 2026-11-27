using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Cartoon fire: outlined flame tongues (prisms that flicker and lick upward), hot glowing cores, and bursts.
    /// Colours are HDR so Bloom makes them glow; every shape has the toon outline. Local-only visuals.
    /// </summary>
    public static class FireVfx
    {
        public static readonly Color Yellow = new(4f, 2.6f, 0.5f, 1f);
        public static readonly Color Orange = new(4f, 1.15f, 0.15f, 1f);
        public static readonly Color Red = new(3f, 0.32f, 0.08f, 1f);
        public static readonly Color Line = new(4f, 0.7f, 0.12f, 1f);

        public static Color Random3() => Random.value < 0.33f ? Yellow : Random.value < 0.5f ? Orange : Red;

        /// <summary>A cartoon explosion of flame at <paramref name="point"/>.</summary>
        public static void Burst(Vector3 point, float scale)
        {
            if (Debris.Suppressed) return;
            EnergyBurst.Create(point, new EnergyBurst.Settings
            {
                flashSize = 2.4f * scale, flashTime = 0.22f, sparkCount = 18, sparkSize = 0.45f * scale,
                sparkSpeed = new Vector2(6f, 13f) * scale, sparkLife = 0.45f, colour = Orange,
            });
            FlameBurst.Create(point, scale);
            for (int i = 0; i < 3; i++)
                SpellFx.Puff(point + Random.insideUnitSphere * 0.6f * scale + Vector3.up * 0.4f, new Color(0.18f, 0.16f, 0.15f), 1.3f * scale);
        }

        /// <summary>Flames licking off something (a burning body, a lump of flaming ground).</summary>
        public static FlameAura Aura(Transform parent, Vector3 localCenter, float radius, int tongues = 7)
        {
            var go = new GameObject("FlameAura") { layer = Layers.Fx };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localCenter;
            var a = go.AddComponent<FlameAura>();
            a.Build(radius, tongues);
            return a;
        }

        /// <summary>One flame tongue: an outlined prism standing on its base, pointing along +Z of its parent.</summary>
        public static Transform Tongue(Transform parent, Color colour)
        {
            var holder = new GameObject("Tongue") { layer = Layers.Fx }.transform;
            holder.SetParent(parent, false);
            var p = Glow.Piece(holder, Glow.PrismMesh, colour, "Flame");
            p.localPosition = new Vector3(0f, 0f, 0.5f); // the prism's base sits on the holder's origin
            return holder;
        }
    }

    /// <summary>Flame tongues round a point that flicker and lick upward. Fade() shrinks them away.</summary>
    public class FlameAura : MonoBehaviour
    {
        class T { public Transform t; public Vector3 dir; public float len, width, phase, speed; }
        readonly List<T> _t = new();
        float _radius, _strength = 1f, _target = 1f;

        public void Build(float radius, int count)
        {
            _radius = radius;
            for (int i = 0; i < count; i++)
            {
                var t = new T
                {
                    t = FireVfx.Tongue(transform, i % 3 == 0 ? FireVfx.Yellow : i % 3 == 1 ? FireVfx.Orange : FireVfx.Red),
                    dir = Random.onUnitSphere, len = Random.Range(0.6f, 1.1f), width = Random.Range(0.25f, 0.4f),
                    phase = Random.value * 10f, speed = Random.Range(9f, 15f),
                };
                t.dir.y = Mathf.Abs(t.dir.y) * 0.5f;
                _t.Add(t);
            }
        }

        /// <summary>Burn brighter / dimmer (0 = out; the tongues shrink to nothing).</summary>
        public void SetStrength(float s) => _target = s;

        void LateUpdate()
        {
            _strength = Mathf.MoveTowards(_strength, _target, Time.deltaTime * 4f);
            float time = Time.time;
            foreach (var t in _t)
            {
                // stood on the surface of a ball round the centre, pointing (mostly) up, flickering
                Vector3 basePos = t.dir * _radius * 0.8f;
                float flick = 0.65f + 0.35f * Mathf.Sin(time * t.speed + t.phase) + 0.15f * Mathf.Sin(time * t.speed * 2.3f + t.phase * 1.7f);
                float len = t.len * _radius * 1.8f * flick * _strength;
                float w = t.width * _radius * _strength;
                t.t.localPosition = basePos;
                Vector3 up = (Vector3.up * 2f + t.dir * 0.6f + new Vector3(Mathf.Sin(time * 3f + t.phase), 0f, Mathf.Cos(time * 2.6f + t.phase)) * 0.25f).normalized;
                t.t.rotation = Quaternion.LookRotation(up);
                t.t.localScale = new Vector3(Mathf.Max(0.001f, w), Mathf.Max(0.001f, w), Mathf.Max(0.001f, len));
            }
        }
    }

    /// <summary>Flame tongues bursting outward from an explosion, then shrinking away.</summary>
    public class FlameBurst : MonoBehaviour
    {
        class T { public Transform t; public Vector3 dir; public float len; }
        readonly List<T> _t = new();
        float _age, _scale;
        const float Life = 0.5f;

        public static void Create(Vector3 point, float scale)
        {
            var go = new GameObject("FlameBurst") { layer = Layers.Fx };
            go.transform.position = point;
            var b = go.AddComponent<FlameBurst>();
            b._scale = scale;
            for (int i = 0; i < 12; i++)
            {
                Vector3 d = Random.onUnitSphere;
                d.y = Mathf.Abs(d.y) + 0.15f; // mostly up and out
                b._t.Add(new T { t = FireVfx.Tongue(go.transform, FireVfx.Random3()), dir = d.normalized, len = Random.Range(1.2f, 2.2f) });
            }
            b.LateUpdate();
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            float u = _age / Life;
            if (u >= 1f) { Destroy(gameObject); return; }
            float grow = 1f - Mathf.Pow(1f - Mathf.Clamp01(u / 0.25f), 3f);
            float shrink = 1f - Mathf.Clamp01((u - 0.3f) / 0.7f);
            foreach (var t in _t)
            {
                t.t.localPosition = t.dir * (0.3f + 1.4f * grow) * _scale;
                t.t.rotation = Quaternion.LookRotation(t.dir);
                float w = 0.55f * _scale * shrink;
                t.t.localScale = new Vector3(Mathf.Max(0.001f, w), Mathf.Max(0.001f, w), Mathf.Max(0.001f, t.len * _scale * grow * shrink));
            }
        }
    }

    // ====================================================================== Ball of Flame

    /// <summary>
    /// The fireball: gathering over the caster's raised hand (streaks rushing in, the ball swelling, flames licking
    /// up), then flying with its flames streaming back and an outlined trail. It ends in the server's flame burst.
    /// </summary>
    public class FireballView : MonoBehaviour
    {
        WorldNet.FireballFx _fx;
        Transform _core;
        FlameAura _flames;
        ArcaneCharge _charge;
        TrailRenderer _trail;

        public static FireballView Create(WorldNet.FireballFx fx)
        {
            var go = new GameObject("Fireball") { layer = Layers.Fx };
            var v = go.AddComponent<FireballView>();
            v._fx = fx;
            go.transform.position = WorldNet.FireballPosition(fx, WorldNet.Now);
            v._core = Glow.Piece(go.transform, Glow.SphereMesh, FireVfx.Yellow, "Core");
            v._flames = FireVfx.Aura(go.transform, Vector3.zero, 0.4f, 9);
            if (!Debris.Suppressed && !fx.meteor)
                v._charge = ArcaneCharge.Create(() => WorldNet.FireballPosition(fx, WorldNet.Now),
                    () => { var c = WorldNet.FindCombatant(fx.caster); return c != null && c.IsAlive; },
                    SpellPlans.FireballCharge, FireVfx.Orange, false);
            return v;
        }

        void LateUpdate()
        {
            if (_fx.done || !WorldNet.Instance || (!WorldNet.Instance.Fireballs.ContainsKey(_fx.id) && !_fx.done))
            {
                if (_trail != null) Glow.DropTrail(_trail);
                if (_charge != null) _charge.Cancel();
                Destroy(gameObject);
                return;
            }
            double now = WorldNet.Now;
            transform.position = WorldNet.FireballPosition(_fx, now);
            if (!_fx.launched)
            {
                // swelling over the hand
                float k = Mathf.Clamp01((float)(now - _fx.start) / SpellPlans.FireballCharge);
                transform.localScale = Vector3.one * Mathf.Lerp(0.15f, 1f, 1f - (1f - k) * (1f - k));
                _core.localScale = Vector3.one * 0.55f * (1f + 0.08f * Mathf.Sin(Time.time * 30f));
                transform.rotation = Quaternion.identity;
                return;
            }
            if (_charge != null) { _charge.Release(); _charge = null; }
            if (_trail == null) _trail = Glow.Trail(transform, FireVfx.Orange, 0.25f, 0.55f * _fx.Scale);
            transform.localScale = Vector3.one * _fx.Scale;
            _core.localScale = Vector3.one * SpellPlans.FireballCore * (1f + 0.08f * Mathf.Sin(Time.time * 30f));
            // the flames stream back off it as it flies
            transform.rotation = Quaternion.FromToRotation(Vector3.up, -_fx.velocity.normalized * 0.8f + Vector3.up * 0.2f);
        }

        void OnDestroy()
        {
            if (_charge != null) _charge.Cancel();
        }
    }

    // ====================================================================== Hellfire Wall

    /// <summary>
    /// The hellfire: a glowing line burns along the ground as it's drawn; then a wall of cartoon flame (a jagged
    /// red-orange sheet with a yellow heart and an ink outline) rises along it from start to end, flickers for a
    /// second, and drops away all at once.
    /// </summary>
    public class HellfireView : MonoBehaviour
    {
        WorldNet.HellfireFx _fx;
        ToonLine _line;
        Mesh _outer, _inner, _ink;
        static Material _innerMat;
        readonly List<Vector3> _samples = new();
        readonly List<Vector3> _v = new();
        readonly List<int> _tri = new();

        public static HellfireView Create(WorldNet.HellfireFx fx)
        {
            var go = new GameObject("Hellfire") { layer = Layers.Fx };
            var v = go.AddComponent<HellfireView>();
            v._fx = fx;
            v._line = new ToonLine(go.transform, 0.14f, FireVfx.Line);
            v._outer = v.MakeLayer("Flame", Glow.DoubleSided, FireVfx.Orange);
            if (_innerMat == null)
            {
                _innerMat = new Material(Glow.DoubleSided) { name = "Hellfire Heart", renderQueue = 2012 };
                if (_innerMat.HasProperty("_ZWrite")) _innerMat.SetFloat("_ZWrite", 0f);
            }
            v._inner = v.MakeLayer("Heart", _innerMat, FireVfx.Yellow);
            v._ink = v.MakeLayer("Outline", Glow.Underlay, Glow.Ink);
            return v;
        }

        Mesh MakeLayer(string name, Material mat, Color c)
        {
            var go = new GameObject(name) { layer = Layers.Fx };
            go.transform.SetParent(transform, false);
            var m = new Mesh { name = name };
            m.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            Glow.SetStrip(r, mat);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            Glow.Tint(r, c);
            return m;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            // the caster sees their own every-frame line while drawing; everyone else the networked points
            var raw = _fx.riseStart < 0 && _fx.local.Count > 1 ? _fx.local : _fx.points;
            Smooth(raw, _smooth);
            // the line grows at a steady speed instead of jumping as points arrive
            float length = PathLength(_smooth);
            _shown = Mathf.MoveTowards(_shown, length, SpellPlans.HellfireLineSpeed * 1.4f * Time.deltaTime);
            if (_fx.riseStart >= 0) _shown = length;
            Trim(_smooth, _shown, _shownPts);
            if (_fx.riseStart < 0)
            {
                // the line being drawn: glowing, pulsing a little
                _line.Set(_shownPts);
                // the caster was knocked down while drawing: the line thins away to nothing
                if (_fizzledAt < 0 && WorldNet.Fizzled(_fx.caster, _fx.start, _fx.start + SpellPlans.HellfireDraw + 0.1)) _fizzledAt = now;
                float thin = _fizzledAt < 0 ? 1f : 1f - Mathf.Clamp01((float)(now - _fizzledAt) / 0.2f);
                _line.SetWidth((0.14f + 0.03f * Mathf.Sin(Time.time * 25f)) * thin);
                if (thin <= 0f) { Destroy(gameObject); return; }
                if (now - _fx.start > SpellPlans.HellfireDraw + 3.0) Destroy(gameObject); // never rose
                return;
            }
            float t = (float)(now - _fx.riseStart);
            float total = SpellPlans.HellfireRise + SpellPlans.HellfireStand + SpellPlans.HellfireFall;
            if (t > total) { Destroy(gameObject); return; }
            _line.Set(_shownPts);
            // falls all at once at the end
            float fall = 1f - Mathf.Clamp01((t - SpellPlans.HellfireRise - SpellPlans.HellfireStand) / SpellPlans.HellfireFall);
            // put out by water: it drops right away in a hiss of steam along its length
            if (_fx.quenched >= 0)
            {
                if (!_steamed)
                {
                    _steamed = true;
                    for (int i = 0; i < _shownPts.Count; i += Mathf.Max(1, _shownPts.Count / 6)) WaterVfx.Steam(_shownPts[i] + Vector3.up * 0.8f, 0.9f);
                }
                fall = Mathf.Min(fall, 1f - Mathf.Clamp01((float)(now - _fx.quenched) / SpellPlans.HellfireFall));
                if (fall <= 0f) { Destroy(gameObject); return; }
            }
            if (fall < 1f) _line.Hide();
            Resample(_shownPts, 0.3f);
            Build(t, fall);
        }

        readonly List<Vector3> _smooth = new(), _shownPts = new(), _tmp = new();
        float _shown;
        bool _steamed;
        double _fizzledAt = -1;

        /// <summary>Rounds off the corners (Chaikin, twice) and evens out the bumps in height, lifted just off the ground.</summary>
        void Smooth(List<Vector3> src, List<Vector3> dst)
        {
            dst.Clear();
            // drop points bunched up closer than 15 cm (they only make kinks)
            foreach (var p in src)
                if (dst.Count == 0 || (p - dst[^1]).sqrMagnitude > 0.0225f) dst.Add(p);
            if (src.Count > 1 && dst.Count == 1) dst.Add(src[^1]);
            for (int pass = 0; pass < 2 && dst.Count > 2; pass++)
            {
                _tmp.Clear();
                _tmp.Add(dst[0]);
                for (int i = 0; i < dst.Count - 1; i++)
                {
                    _tmp.Add(Vector3.Lerp(dst[i], dst[i + 1], 0.25f));
                    _tmp.Add(Vector3.Lerp(dst[i], dst[i + 1], 0.75f));
                }
                _tmp.Add(dst[^1]);
                dst.Clear();
                dst.AddRange(_tmp);
            }
            // heights: a running average, so voxel steps become slopes
            _tmp.Clear();
            _tmp.AddRange(dst);
            for (int i = 0; i < dst.Count; i++)
            {
                float y = 0f;
                int n = 0;
                for (int k = Mathf.Max(0, i - 3); k <= Mathf.Min(dst.Count - 1, i + 3); k++) { y += _tmp[k].y; n++; }
                var p = dst[i];
                p.y = Mathf.Max(y / n, _tmp[i].y) + 0.04f;
                dst[i] = p;
            }
        }

        static float PathLength(List<Vector3> pts)
        {
            float l = 0f;
            for (int i = 1; i < pts.Count; i++) l += Vector3.Distance(pts[i - 1], pts[i]);
            return l;
        }

        static void Trim(List<Vector3> src, float length, List<Vector3> dst)
        {
            dst.Clear();
            if (src.Count == 0) return;
            dst.Add(src[0]);
            float run = 0f;
            for (int i = 1; i < src.Count; i++)
            {
                float seg = Vector3.Distance(src[i - 1], src[i]);
                if (run + seg >= length)
                {
                    if (seg > 1e-5f) dst.Add(Vector3.Lerp(src[i - 1], src[i], (length - run) / seg));
                    return;
                }
                run += seg;
                dst.Add(src[i]);
            }
        }

        void Resample(List<Vector3> pts, float step)
        {
            _samples.Clear();
            if (pts.Count < 2) return;
            _samples.Add(pts[0]);
            float carry = 0f;
            for (int i = 1; i < pts.Count; i++)
            {
                Vector3 a = pts[i - 1], b = pts[i];
                float seg = Vector3.Distance(a, b);
                float s = step - carry;
                while (s <= seg)
                {
                    _samples.Add(Vector3.Lerp(a, b, s / seg));
                    s += step;
                }
                carry = seg - (s - step);
            }
            _samples.Add(pts[^1]);
        }

        void Build(float t, float fall)
        {
            int n = _samples.Count;
            if (n < 2) return;
            var cam = Camera.main;
            float time = Time.time;
            // rises from start to end over the rise time
            float front = Mathf.Clamp01(t / SpellPlans.HellfireRise) * 1.15f;
            Layer(_outer, n, (i, u) =>
            {
                float rise = Mathf.Clamp01((front - u) * 6f);
                float h = SpellPlans.HellfireHeight * (0.82f + 0.18f * Mathf.Sin(u * 9f + time * 3f)) * rise * fall;
                float spike = (i % 2 == 0 ? 0.55f : 0f) * (0.7f + 0.3f * Mathf.Sin(time * 14f + i)) * rise * fall;
                return (-0.05f, h + spike);
            }, Vector3.zero, 0f);
            Vector3 toCam = cam != null ? (cam.transform.position - transform.position).normalized * 0.04f : Vector3.zero;
            Layer(_inner, n, (i, u) =>
            {
                float rise = Mathf.Clamp01((front - u) * 6f);
                float h = SpellPlans.HellfireHeight * 0.55f * (0.8f + 0.2f * Mathf.Sin(u * 13f + time * 4f)) * rise * fall;
                float spike = (i % 2 == 1 ? 0.35f : 0f) * (0.7f + 0.3f * Mathf.Sin(time * 17f + i * 1.3f)) * rise * fall;
                return (0.02f, h + spike);
            }, toCam, 0f);
            Layer(_ink, n, (i, u) =>
            {
                float rise = Mathf.Clamp01((front - u) * 6f);
                float h = SpellPlans.HellfireHeight * (0.82f + 0.18f * Mathf.Sin(u * 9f + time * 3f)) * rise * fall;
                float spike = (i % 2 == 0 ? 0.55f : 0f) * (0.7f + 0.3f * Mathf.Sin(time * 14f + i)) * rise * fall;
                return (-0.12f, h + spike + (h > 0.01f ? 0.12f : 0f));
            }, -toCam, 0.12f);
        }

        /// <summary>A vertical strip along the samples: bottom/top heights per sample from <paramref name="profile"/>.</summary>
        void Layer(Mesh m, int n, System.Func<int, float, (float bottom, float top)> profile, Vector3 shift, float extend)
        {
            _v.Clear();
            _tri.Clear();
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)(n - 1);
                var (b, top) = profile(i, u);
                Vector3 p = _samples[i] + shift;
                // the outline layer reaches a touch past each end
                if (extend > 0f && i == 0) p += (_samples[0] - _samples[1]).normalized * extend;
                if (extend > 0f && i == n - 1) p += (_samples[n - 1] - _samples[n - 2]).normalized * extend;
                _v.Add(transform.InverseTransformPoint(p + Vector3.up * b));
                _v.Add(transform.InverseTransformPoint(p + Vector3.up * Mathf.Max(b + 0.001f, top)));
            }
            for (int i = 0; i < n - 1; i++)
            {
                int a = i * 2;
                _tri.Add(a); _tri.Add(a + 1); _tri.Add(a + 2);
                _tri.Add(a + 1); _tri.Add(a + 3); _tri.Add(a + 2);
            }
            m.Clear();
            m.SetVertices(_v);
            m.SetTriangles(_tri, 0);
            m.RecalculateBounds();
        }

        void OnDestroy()
        {
            _line?.Destroy();
            if (_outer) Destroy(_outer);
            if (_inner) Destroy(_inner);
            if (_ink) Destroy(_ink);
        }
    }

    // ====================================================================== burning bodies

    /// <summary>
    /// Flames on anyone burning, licking off random parts of the body (more burns stacked = more of them).
    /// They follow the tumbling ragdoll when knocked down. Every client draws it from the replicated burn count.
    /// </summary>
    public class BurnFx : MonoBehaviour
    {
        class Spot { public string bone; public FlameAura aura; public Transform ragdollBone; }

        Combatant _c;
        readonly List<Spot> _spots = new();
        GameObject _ragdollSeen;
        static readonly List<string> Candidates = new();

        void Awake() => _c = GetComponent<Combatant>();

        void OnDestroy()
        {
            foreach (var s in _spots) if (s.aura != null) Destroy(s.aura.gameObject);
        }

        void LateUpdate()
        {
            if (_c == null || !_c.IsSpawned) return;
            bool shown = _c.IsAlive && (_c.Visible || _c.HasLiveRagdoll);
            int stacks = shown ? _c.BurnStacks.Value : 0;
            int want = stacks > 0 ? Mathf.Min(3 + stacks * 2, 9) : 0;

            while (_spots.Count > want)
            {
                var s = _spots[^1];
                if (s.aura != null) { s.aura.SetStrength(0f); Destroy(s.aura.gameObject, 0.3f); }
                _spots.RemoveAt(_spots.Count - 1);
            }
            while (_spots.Count < want)
            {
                var s = new Spot { bone = PickBone() };
                s.aura = FireVfx.Aura(null, Vector3.zero, Random.Range(0.1f, 0.15f), 4);
                _spots.Add(s);
            }
            if (_spots.Count == 0) return;

            // knocked down: the flames move over to the matching bones of the ragdoll
            var ragdoll = _c.LiveRagdollObject;
            if (ragdoll != _ragdollSeen)
            {
                _ragdollSeen = ragdoll;
                foreach (var s in _spots) s.ragdollBone = ragdoll != null && s.bone != null ? FindDeep(ragdoll.transform, s.bone) : null;
            }
            foreach (var s in _spots)
            {
                if (ragdoll != null && s.ragdollBone == null && s.bone != null) s.ragdollBone = FindDeep(ragdoll.transform, s.bone);
                Transform host = ragdoll != null ? s.ragdollBone : null;
                if (host == null && s.bone != null && _c.Bones != null) _c.Bones.TryGetValue(s.bone, out host);
                s.aura.transform.position = host != null ? host.position : _c.transform.position + Vector3.up * 0.1f;
            }
        }

        /// <summary>A random body part (not the bone tips), so the fire spreads over the whole body.</summary>
        string PickBone()
        {
            if (_c.Bones == null || _c.Bones.Count == 0) return null;
            Candidates.Clear();
            foreach (var kv in _c.Bones)
                if (kv.Value != null && !kv.Key.EndsWith("_end")) Candidates.Add(kv.Key);
            // prefer ones nobody's on yet
            for (int tries = 0; tries < 6 && Candidates.Count > 0; tries++)
            {
                string pick = Candidates[Random.Range(0, Candidates.Count)];
                if (!_spots.Exists(s => s.bone == pick)) return pick;
            }
            return Candidates.Count > 0 ? Candidates[Random.Range(0, Candidates.Count)] : null;
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform child in root)
            {
                var f = FindDeep(child, name);
                if (f != null) return f;
            }
            return null;
        }
    }
}