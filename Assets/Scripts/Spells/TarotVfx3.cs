using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace SuddenMageEncounter
{
    // ====================================================================== The Star

    /// <summary>
    /// Five outlined mahogany-gold stars in an arc over the caster's head, popping in one after another; each flies off in
    /// turn, spinning, with a trail, and bursts where it hits (or shrinks away at the end of its reach).
    /// </summary>
    public class StarsView : MonoBehaviour
    {
        static readonly Color StarColor = Color.Lerp(TarotVfx.Mahogany, TarotVfx.Gold, 0.45f);
        static Mesh _mesh;

        WorldNet.StarFx _fx;
        Transform[] _stars;
        TrailRenderer[] _trails;
        float[] _gone;

        /// <summary>A five-pointed star facing +Z, its points 1 from the middle, a third as thick.</summary>
        static Mesh StarMesh
        {
            get
            {
                if (_mesh != null) return _mesh;
                var v = new List<Vector3>();
                var tri = new List<int>();
                var ring = new Vector3[10];
                for (int i = 0; i < 10; i++)
                {
                    float a = (90f + i * 36f) * Mathf.Deg2Rad, r = i % 2 == 0 ? 1f : 0.46f;
                    ring[i] = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f);
                }
                const float Thick = 0.3f;
                Vector3 front = new(0, 0, Thick), back = new(0, 0, -Thick);
                void Face(Vector3 a, Vector3 b, Vector3 c)
                {
                    int k = v.Count;
                    v.Add(a); v.Add(b); v.Add(c);
                    tri.Add(k); tri.Add(k + 1); tri.Add(k + 2);
                }
                for (int i = 0; i < 10; i++)
                {
                    Vector3 a = ring[i], b = ring[(i + 1) % 10];
                    // (a → b runs anticlockwise seen from +Z; Unity's front faces run clockwise)
                    Face(front, a, b);
                    Face(back, b, a);
                }
                _mesh = new Mesh { name = "Star" };
                _mesh.SetVertices(v);
                _mesh.SetTriangles(tri, 0);
                // make sure every face looks outward
                var t = _mesh.triangles;
                for (int i = 0; i < t.Length; i += 3)
                {
                    Vector3 p = v[t[i]], q = v[t[i + 1]], s = v[t[i + 2]];
                    if (Vector3.Dot(Vector3.Cross(q - p, s - p), p + q + s) < 0f) (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
                }
                _mesh.triangles = t;
                _mesh.RecalculateNormals();
                _mesh.RecalculateBounds();
                return _mesh;
            }
        }

        public static StarsView Create(WorldNet.StarFx fx)
        {
            var go = new GameObject("TheStar") { layer = Layers.Fx };
            var v = go.AddComponent<StarsView>();
            v._fx = fx;
            v._stars = new Transform[fx.stars.Length];
            v._trails = new TrailRenderer[fx.stars.Length];
            v._gone = new float[fx.stars.Length];
            for (int k = 0; k < v._stars.Length; k++)
            {
                v._stars[k] = Glow.Piece(go.transform, StarMesh, StarColor, "Star");
                v._stars[k].localScale = Vector3.zero;
            }
            v.LateUpdate();
            return v;
        }

        public static void Burst(Vector3 p)
        {
            EnergyBurst.Create(p, new EnergyBurst.Settings
            {
                flashSize = 0.9f, flashTime = 0.14f, sparkCount = 10, sparkSize = 0.26f, sparkSpeed = new Vector2(4f, 9f), sparkLife = 0.3f, colour = StarColor,
            });
            CameraRig.ShakeFrom(p, 0.1f, 0.12f);
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            var cam = Camera.main;
            bool any = false;
            // (the ink rim sticks out 14% of the thinnest scale: the points and their ink reach exactly the radius it hits at)
            float size = SpellPlans.StarRadius / 1.14f;
            for (int k = 0; k < _stars.Length; k++)
            {
                var b = _fx.stars[k];
                var s = _stars[k];
                if (s == null) continue;
                if (!b.Alive)
                {
                    _gone[k] += Time.deltaTime / 0.15f;
                    if (_trails[k] != null) { Glow.DropTrail(_trails[k]); _trails[k] = null; }
                    if (_gone[k] >= 1f) { Destroy(s.gameObject); _stars[k] = null; continue; }
                    s.localScale = Vector3.one * (size * (1f - _gone[k]));
                    any = true;
                    continue;
                }
                any = true;
                Vector3 p = WorldNet.StarPosition(_fx, k, now);
                s.position = p;
                if (b.launched)
                {
                    if (_trails[k] == null)
                    {
                        _trails[k] = Glow.Trail(s, StarColor, 0.22f, 0.2f);
                        EnergyBurst.Create(p, new EnergyBurst.Settings { flashSize = 0.5f, flashTime = 0.1f, sparkCount = 4, sparkSize = 0.16f, sparkSpeed = new Vector2(2f, 5f), sparkLife = 0.2f, colour = StarColor });
                    }
                    // spinning like a thrown star, its face to the side
                    s.rotation = Quaternion.LookRotation(b.dir) * Quaternion.Euler(0f, 90f, (float)(now - b.t) * 1100f);
                    s.localScale = Vector3.one * size;
                    continue;
                }
                // waiting: facing the camera, swaying, the next one to go throbbing
                float pop = Mathf.Clamp01((t - k * 0.05f) / 0.18f);
                pop = pop < 1f ? pop * (1f + 0.5f * Mathf.Sin(pop * Mathf.PI)) : 1f;
                bool next = t > SpellPlans.StarEvery * k;
                float throb = next ? 1f - 0.12f * (0.5f + 0.5f * Mathf.Sin(t * 22f)) : 0.82f;
                s.localScale = Vector3.one * (size * pop * throb);
                if (cam != null) s.rotation = Quaternion.LookRotation(cam.transform.position - p) * Quaternion.Euler(0f, 0f, Mathf.Sin(t * 2.4f + k) * 14f);
            }
            if (!any) Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (_trails != null) foreach (var t in _trails) if (t != null) Glow.DropTrail(t);
        }
    }

    // ====================================================================== gathering in the hands

    /// <summary>
    /// Mahogany power (The World: black) gathering in the clasped hands (or the raised right one) while a Tarot card is cast: it goes off when
    /// the cast is done, or just stops if the caster is knocked out of it.
    /// </summary>
    public class TarotGather : MonoBehaviour
    {
        ArcaneCharge _charge;
        double _until;

        public static void Create(ulong caster, bool rightHand, double start, float seconds, bool black)
        {
            var go = new GameObject("TarotGather") { layer = Layers.Fx };
            var g = go.AddComponent<TarotGather>();
            g._until = start + seconds;
            Vector3 last = Vector3.zero;
            Vector3 Hands()
            {
                var c = WorldNet.FindCombatant(caster);
                if (c != null) last = rightHand ? SpellVisuals.RightHand(c) : (SpellVisuals.RightHand(c) + SpellVisuals.LeftHand(c)) * 0.5f;
                return last;
            }
            g._charge = ArcaneCharge.Create(Hands, () =>
            {
                var c = WorldNet.FindCombatant(caster);
                return c != null && c.IsAlive && !WorldNet.Fizzled(caster, start, start + seconds);
            }, seconds, black ? Color.black : TarotVfx.Mahogany, true);
        }

        void LateUpdate()
        {
            if (_charge != null && WorldNet.Now < _until) return;
            if (_charge != null) _charge.Release();
            Destroy(gameObject);
        }
    }

    // ====================================================================== on a body: the moon, the sun, taken by The World

    /// <summary>
    /// Drawn on a body from its replicated <see cref="BodyStatus"/>: The Moon filling over the head, The Sun's dark sun, and
    /// the runes as The World takes someone (and gives them back). Every peer.
    /// </summary>
    public class Tarot3BodyFx : MonoBehaviour
    {
        Combatant _c;
        MoonDisc _moon;
        DarkSun _sun;
        ArcaneCharge _sunCharge;
        double _chargedFor, _takenFor, _backFor;

        void Awake() => _c = GetComponent<Combatant>();

        void LateUpdate()
        {
            if (_c == null || !_c.IsSpawned) return;
            var st = _c.Status.Value;
            double now = WorldNet.Now;
            bool alive = _c.IsAlive && !Debris.Suppressed;

            bool moon = alive && st.Moon(now);
            if (moon && _moon == null) _moon = MoonDisc.Create();
            if (_moon != null)
            {
                if (moon)
                {
                    _moon.transform.position = TarotVfx.Head(_c) + Vector3.up * 0.75f;
                    _moon.Fill = st.MoonFill;
                }
                else { _moon.Go(); _moon = null; }
            }

            // gathering before the sun turns
            if (alive && st.SunUntil > 0 && now >= st.SunCast && now < st.SunFrom && _chargedFor != st.SunFrom)
            {
                _chargedFor = st.SunFrom;
                double from = st.SunFrom;
                _sunCharge = ArcaneCharge.Create(() => TarotVfx.Head(_c) + Vector3.up * 0.85f,
                    () => _c != null && _c.IsAlive && _c.Status.Value.SunFrom == from && _c.Status.Value.SunUntil > 0, (float)(st.SunFrom - now), SpellPlans.SunDark, true);
            }
            bool sun = alive && st.Sun(now);
            if (sun && _sun == null)
            {
                if (_sunCharge != null) { _sunCharge.Release(); _sunCharge = null; }
                _sun = DarkSun.Create();
                CameraRig.ShakeFrom(_c.transform.position, 0.3f, 0.35f);
            }
            if (_sun != null)
            {
                if (sun) _sun.transform.position = TarotVfx.Head(_c) + Vector3.up * 0.85f;
                else { _sun.Go(); _sun = null; }
            }

            // crushed away, and back again
            if (alive && st.Taken(now) && _takenFor != st.WorldFrom)
            {
                _takenFor = st.WorldFrom;
                double until = st.WorldFrom + SpellPlans.WorldWarp;
                Vector3 at = _c.transform.position;
                _warpAt = at;
                _warpRunes = RuneSpew.Create(() => at, () => WorldNet.Now < until, SpellPlans.WorldWarp, Color.black, 70f, new Vector3(0.45f, 0.9f, 0.45f));
                if (_warpRunes != null) _warpRunes.Burst(14);
                CameraRig.ShakeFrom(at, 0.3f, SpellPlans.WorldWarp);
            }
            // folded away to nothing: the last of it goes off black
            if (_goneFor != _takenFor && st.Taken(now) && now >= st.WorldFrom + SpellPlans.WorldWarp)
            {
                _goneFor = _takenFor;
                if (_warpRunes != null) _warpRunes.Burst(30);
                _warpRunes = null;
                BlackBurst(_warpAt, 1.3f);
                CameraRig.ShakeFrom(_warpAt, 0.45f, 0.25f);
            }
            if (alive && st.WorldUntil > 0 && now >= st.WorldUntil && now < st.WorldUntil + SpellPlans.WorldUnfold && _backFor != st.WorldUntil)
            {
                _backFor = st.WorldUntil;
                Vector3 here = _c.transform.position;
                BlackBurst(here, 1.2f);
                var runes = RuneSpew.Create(() => here, () => false, 0.1f, Color.black, 1f, new Vector3(0.45f, 0.9f, 0.45f));
                if (runes != null) runes.Burst(24);
                CameraRig.ShakeFrom(here, 0.2f, 0.2f);
            }
        }

        static void BlackBurst(Vector3 p, float scale) => EnergyBurst.Create(p, new EnergyBurst.Settings
        {
            flashSize = 0.8f * scale, flashTime = 0.18f, sparkCount = Mathf.RoundToInt(16 * scale), sparkSize = 0.34f * scale,
            sparkSpeed = new Vector2(4f, 10f) * scale, sparkLife = 0.4f, colour = Color.black,
        });

        double _goneFor;
        Vector3 _warpAt;
        RuneSpew _warpRunes;

        void OnDestroy()
        {
            if (_moon != null) Destroy(_moon.gameObject);
            if (_sun != null) Destroy(_sun.gameObject);
        }
    }

    /// <summary>A moon facing the camera: a dark inked disc, its lit part growing from a sliver to full.</summary>
    public class MoonDisc : MonoBehaviour
    {
        static readonly Color Dark = new(0.16f, 0.05f, 0.035f, 1f), Lit = new(2.5f, 1.9f, 1.35f, 1f);
        public float Fill;
        float _shown;

        /// <summary>The moon over someone is full: light green, and plus signs rise off them.</summary>
        public static void Full(ulong caster)
        {
            var c = WorldNet.FindCombatant(caster);
            if (c == null) return;
            EnergyBurst.Create(TarotVfx.Head(c) + Vector3.up * 0.75f, new EnergyBurst.Settings
            {
                flashSize = 1.4f, flashTime = 0.2f, sparkCount = 14, sparkSize = 0.3f, sparkSpeed = new Vector2(4f, 9f), sparkLife = 0.4f, colour = SpellPlans.MoonGreen,
            });
            HealView.Create(caster, 0.8f);
        }

        Mesh _disc, _lit;
        Transform _body;
        MeshRenderer _litR;
        float _age, _gone = -1f;
        const int Rows = 40;

        public static MoonDisc Create()
        {
            var go = new GameObject("Moon") { layer = Layers.Fx };
            var m = go.AddComponent<MoonDisc>();
            m._disc = Disc();
            m.Part("Ink", m._disc, Glow.Ink, 1.16f, 0.03f);
            m.Part("Dark", m._disc, Dark, 1f, 0.015f);
            m._lit = new Mesh { name = "MoonLit" };
            m._lit.MarkDynamic();
            m._litR = m.Part("Lit", m._lit, Lit, 1f, 0f);
            go.transform.localScale = Vector3.zero;
            return m;
        }

        MeshRenderer Part(string name, Mesh mesh, Color c, float size, float back)
        {
            var go = new GameObject(name) { layer = Layers.Fx };
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.forward * back;
            go.transform.localScale = Vector3.one * size;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Glow.DoubleSided;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            Glow.Tint(r, c);
            return r;
        }

        static Mesh Disc()
        {
            var v = new List<Vector3> { Vector3.zero };
            var t = new List<int>();
            const int n = 48;
            for (int i = 0; i <= n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2f;
                v.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f));
                if (i > 0) { t.Add(0); t.Add(i); t.Add(i + 1); }
            }
            var m = new Mesh { name = "MoonDisc" };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        static readonly List<Vector3> Verts = new();
        static readonly List<int> Tris = new();

        /// <summary>The lit part: from the left limb to the terminator, an ellipse that sweeps across as it fills (a crescent like the card's).</summary>
        void Shape(float fill)
        {
            Verts.Clear();
            Tris.Clear();
            float edge = Mathf.Lerp(-0.8f, 1f, fill); // where the terminator crosses the middle: -1 the left limb, 1 the right
            for (int i = 0; i <= Rows; i++)
            {
                float y = Mathf.Lerp(-1f, 1f, i / (float)Rows), w = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                Verts.Add(new Vector3(-w, y, 0f));
                Verts.Add(new Vector3(w * edge, y, 0f));
                if (i == 0) continue;
                int k = i * 2;
                Tris.Add(k - 2); Tris.Add(k); Tris.Add(k - 1);
                Tris.Add(k - 1); Tris.Add(k); Tris.Add(k + 1);
            }
            _lit.Clear();
            _lit.SetVertices(Verts);
            _lit.SetTriangles(Tris, 0);
            _lit.RecalculateBounds();
        }

        /// <summary>It pulsed, or it was cut short: it shrinks away.</summary>
        public void Go() => _gone = 0f;

        void LateUpdate()
        {
            _age += Time.deltaTime;
            var cam = Camera.main;
            if (cam != null) transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, cam.transform.up);
            float s = 1f - Mathf.Pow(1f - Mathf.Clamp01(_age / 0.2f), 3f);
            if (_gone >= 0f)
            {
                _gone += Time.deltaTime / 0.2f;
                if (_gone >= 1f) { Destroy(gameObject); return; }
                s *= (1f - _gone) * (1f - _gone);
            }
            else
            {
                _shown = Mathf.MoveTowards(_shown, Fill, Time.deltaTime * 1.5f);
                Shape(_shown);
                // brightening and throbbing as it nears full; pale green at the very end
                float near = Mathf.InverseLerp(0.75f, 1f, _shown);
                s *= 1f + 0.06f * near * Mathf.Sin(_age * 30f);
                Glow.Tint(_litR, Color.Lerp(Lit, SpellPlans.MoonGreen, near));
            }
            transform.localScale = Vector3.one * Mathf.Max(0.001f, SpellPlans.MoonSize * s);
        }

        void OnDestroy()
        {
            if (_disc != null) Destroy(_disc);
            if (_lit != null) Destroy(_lit);
        }
    }

    /// <summary>The Sun's dark sun: a near-black mahogany disc with jagged, trembling rays that burn dull red at their rims.</summary>
    public class DarkSun : MonoBehaviour
    {
        Transform _core, _rim;
        readonly List<Transform> _rays = new();
        float _age, _gone = -1f;
        const int RayCount = 14;

        public static DarkSun Create()
        {
            var go = new GameObject("DarkSun") { layer = Layers.Fx };
            var s = go.AddComponent<DarkSun>();
            s._rim = Glow.Piece(go.transform, Glow.SphereMesh, SpellPlans.SunRim, "Rim");
            s._core = Glow.Piece(go.transform, Glow.SphereMesh, SpellPlans.SunDark, "Core", false);
            for (int i = 0; i < RayCount; i++)
            {
                var holder = new GameObject("Ray") { layer = Layers.Fx }.transform;
                holder.SetParent(go.transform, false);
                var p = Glow.Piece(holder, Glow.PrismMesh, SpellPlans.SunDark, "Prism");
                p.localPosition = new Vector3(0f, 0f, 0.95f);
                // a burning rim instead of plain ink
                var ink = p.Find("Outline")?.GetComponent<MeshRenderer>();
                if (ink != null) Glow.Tint(ink, SpellPlans.SunRim);
                s._rays.Add(holder);
            }
            go.transform.localScale = Vector3.zero;
            return s;
        }

        public void Go() => _gone = 0f;

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            _age += dt;
            var cam = Camera.main;
            if (cam != null) transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
            float s = Mathf.Clamp01(_age / 0.16f);
            s = s < 1f ? s * (1f + 0.6f * Mathf.Sin(s * Mathf.PI)) : 1f;
            if (_gone >= 0f)
            {
                _gone += dt / 0.2f;
                if (_gone >= 1f) { Destroy(gameObject); return; }
                s *= (1f - _gone) * (1f - _gone);
            }
            float beat = 1f + 0.07f * Mathf.Sin(_age * 9f);
            transform.localScale = Vector3.one * Mathf.Max(0.001f, 0.42f * s * beat);
            // (the core sits in front of the rim, so the rim shows as a thin burning edge round it)
            _rim.localScale = Vector3.one;
            _core.localPosition = Vector3.back * 0.25f;
            _core.localScale = Vector3.one * 0.84f;
            for (int i = 0; i < _rays.Count; i++)
            {
                // jagged: long and short by turns, each twitching on its own
                float len = (i % 2 == 0 ? 1.15f : 0.7f) * (1f + 0.3f * (Mathf.PerlinNoise(_age * 7f, i * 3.7f) - 0.5f));
                float ang = i * (360f / RayCount) + _age * 24f + 5f * (Mathf.PerlinNoise(i * 2.1f, _age * 5f) - 0.5f);
                _rays[i].localRotation = Quaternion.Euler(0f, 0f, ang) * Quaternion.Euler(-90f, 0f, 0f);
                _rays[i].localScale = new Vector3(0.55f, 0.55f, len);
            }
        }
    }

    // ====================================================================== The Sun: who's looking

    /// <summary>
    /// The local player's screen: whenever someone's Sun is burning, tells the server whether they're on it (in front of
    /// the camera, inside the view, nothing in the way). Creates itself.
    /// </summary>
    public class SunGazeWatch : MonoBehaviour
    {
        readonly HashSet<ulong> _seen = new(), _now = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Make()
        {
            var go = new GameObject("SunGazeWatch");
            DontDestroyOnLoad(go);
            go.AddComponent<SunGazeWatch>();
            go.AddComponent<WorldScreen>();
        }

        void LateUpdate()
        {
            var me = PlayerController.Local;
            if (me == null || !me.IsSpawned) { _seen.Clear(); return; }
            _now.Clear();
            var cam = Camera.main;
            double now = WorldNet.Now;
            if (cam != null && me.IsAlive)
                foreach (var c in Combatant.Registry)
                    if (c != null && c != me && c.IsAlive && c.Status.Value.Sun(now) && OnScreen(cam, c)) _now.Add(c.NetworkObjectId);
            foreach (var id in _now) if (_seen.Add(id)) me.ReportSunGaze(id, true);
            _seen.RemoveWhere(id =>
            {
                if (_now.Contains(id)) return false;
                me.ReportSunGaze(id, false);
                return true;
            });
        }

        static bool OnScreen(Camera cam, Combatant c)
        {
            Vector3 chest = c.transform.position + Vector3.up * 0.3f;
            Vector3 vp = cam.WorldToViewportPoint(chest);
            if (vp.z <= 0f || vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) return false;
            Vector3 eye = cam.transform.position;
            return !Physics.Linecast(eye, chest, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)
                || !Physics.Linecast(eye, TarotVfx.Head(c), Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }
    }

    // ====================================================================== The World

    /// <summary>
    /// The World's sphere being made: a near-black mahogany ball with a burning rim, swelling from nothing to its full
    /// size (hiding the ground and glass taking shape inside it) over a rune circle, then shrinking away to its middle.
    /// </summary>
    public class WorldView : MonoBehaviour
    {
        static readonly Color Skin = new(0.09f, 0.025f, 0.018f, 1f);
        static Material _rimMat;
        WorldNet.WorldFx _fx;
        Transform _ball, _rim;
        RuneDrawing _circle;
        RuneSpew _runes;
        bool _formed;

        public static WorldView Create(WorldNet.WorldFx fx)
        {
            var go = new GameObject("TheWorld") { layer = Layers.Fx };
            go.transform.position = fx.center;
            var v = go.AddComponent<WorldView>();
            v._fx = fx;
            v._ball = Glow.Piece(go.transform, Glow.SphereMesh, Skin, "Ball", false);
            // the rim: a hair bigger, back faces only, so it's a burning edge round the ball however big it gets
            if (_rimMat == null)
            {
                _rimMat = new Material(Glow.Shared) { name = "World Rim" };
                if (_rimMat.HasProperty("_Cull")) _rimMat.SetFloat("_Cull", 1f);
            }
            v._rim = Glow.Piece(go.transform, Glow.SphereMesh, TarotVfx.Mahogany, "Rim", false);
            v._rim.GetComponent<MeshRenderer>().sharedMaterial = _rimMat;
            v._ball.localScale = v._rim.localScale = Vector3.zero;

            var c = new GameObject("Circle") { layer = Layers.Fx };
            c.transform.SetParent(go.transform, false);
            c.transform.localPosition = Vector3.up * 0.3f;
            v._circle = new RuneDrawing(c);
            // (its inner ring, 0.84 of the outer, runs along the ball's edge)
            v._circle.AddRune(DomainView.Strokes(), SpellPlans.WorldRadius * 2f / 0.84f, 0.16f);
            v._circle.Progress = 1f;
            v._circle.SetColour(TarotVfx.Mahogany);
            c.transform.localScale = Vector3.zero;

            Vector3 at = fx.center + Vector3.up;
            v._runes = RuneSpew.Create(() => at, () => v != null && !v._formed, SpellPlans.WorldGrow, TarotVfx.Mahogany, 30f, Vector3.one * 2f);
            CameraRig.ShakeFrom(fx.center, 0.8f, SpellPlans.WorldGrow);
            return v;
        }

        void LateUpdate()
        {
            float t = (float)(WorldNet.Now - _fx.start);
            float r;
            if (t < SpellPlans.WorldGrow)
            {
                r = SpellPlans.WorldRadiusAt(t);
                if (r > 1f) CameraRig.ShakeFrom(Nearest(r), 0.12f, 0.1f);
            }
            else
            {
                if (!_formed)
                {
                    _formed = true;
                    if (VoxelWorld.Current != null) VoxelWorld.Current.FinishLate();
                    CameraRig.ShakeFrom(Nearest(SpellPlans.WorldRadius), 0.7f, 0.5f);
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * Mathf.PI / 4f;
                        HierophantView.Spark(_fx.center + new Vector3(Mathf.Cos(a), 0.15f, Mathf.Sin(a)) * SpellPlans.WorldRadius, 2f);
                    }
                }
                float u = Mathf.Clamp01((t - SpellPlans.WorldGrow) / 0.35f);
                if (u >= 1f) { Destroy(gameObject); return; }
                r = SpellPlans.WorldRadius * (1f - u) * (1f - u);
            }
            _ball.localScale = Glow.SphereScale(r);
            _rim.localScale = Glow.SphereScale(r + Mathf.Min(0.25f, r * 0.14f));
            _circle.Host.localScale = Vector3.one * Mathf.Max(0.001f, r / SpellPlans.WorldRadius);
            _circle.Host.localRotation = Quaternion.Euler(0f, t * 40f, 0f);
        }

        /// <summary>The point of the ball's edge nearest the camera (so the shake is felt by anyone it's near).</summary>
        Vector3 Nearest(float r)
        {
            var cam = Camera.main;
            if (cam == null) return _fx.center;
            Vector3 d = cam.transform.position - _fx.center;
            return d.magnitude <= r ? cam.transform.position : _fx.center + d.normalized * r;
        }

        /// <summary>The dome comes down.</summary>
        public static void Shatter(Vector3 center)
        {
            Vector3 top = center + Vector3.up * SpellPlans.WorldRadius;
            EnergyBurst.Create(top, new EnergyBurst.Settings
            {
                flashSize = 4f, flashTime = 0.25f, sparkCount = 24, sparkSize = 0.9f, sparkSpeed = new Vector2(10f, 24f), sparkLife = 0.6f, colour = TarotVfx.Mahogany,
            });
            var cam = Camera.main;
            Vector3 feel = cam != null && (cam.transform.position - center).magnitude < SpellPlans.WorldRadius + 10f ? cam.transform.position : center;
            CameraRig.ShakeFrom(feel, 0.8f, 0.7f);
        }

        void OnDestroy() => _circle?.Release();
    }
}
