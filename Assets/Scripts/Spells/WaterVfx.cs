using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Water: cartoon aqua shapes with ink outlines, splashes of droplets that arc down and shrink away, steam where
    /// it puts fire out. Local-only visuals.
    /// </summary>
    public static class WaterVfx
    {
        /// <summary>Water at full strength: bright aqua, a touch over 1 so it blooms a little.</summary>
        public static readonly Color Aqua = new(0.3f, 1.7f, 2.2f, 1f);
        /// <summary>The body of a big wave: deeper blue-green.</summary>
        public static readonly Color Deep = new(0.06f, 0.5f, 1.1f, 1f);
        public static readonly Color Foam = new(2.1f, 2.6f, 2.8f, 1f);
        /// <summary>A Water of Life orb still forming: pale, no colour yet.</summary>
        public static readonly Color Pale = new(0.62f, 0.85f, 0.92f, 1f);
        /// <summary>Healing: green symbols.</summary>
        public static readonly Color Heal = new(0.45f, 3.2f, 0.7f, 1f);
        /// <summary>Water erased by Arcane Judgment, floating off as void.</summary>
        public static readonly Color Void = new(0.04f, 0.03f, 0.07f, 1f);

        /// <summary>Water bursting: a flash and droplets flung out.</summary>
        public static void Splash(Vector3 p, float scale)
        {
            if (Debris.Suppressed) return;
            EnergyBurst.Create(p, new EnergyBurst.Settings
            {
                flashSize = 1.1f * scale, flashTime = 0.16f, sparkCount = 10, sparkSize = 0.24f * scale,
                sparkSpeed = new Vector2(3f, 7f) * scale, sparkLife = 0.35f, colour = Aqua,
            });
            Droplets.Create(p, scale, 12, Vector3.zero);
            CameraRig.ShakeFrom(p, 0.06f * scale, 0.12f);
        }

        /// <summary>Fire put out: a puff of steam and a few white flecks.</summary>
        public static void Steam(Vector3 p, float scale)
        {
            if (Debris.Suppressed) return;
            SpellFx.Puff(p, new Color(0.92f, 0.95f, 0.98f), scale);
            EnergyBurst.Create(p, new EnergyBurst.Settings
            {
                flashSize = 0.6f * scale, flashTime = 0.12f, sparkCount = 6, sparkSize = 0.18f * scale,
                sparkSpeed = new Vector2(2f, 5f) * scale, sparkLife = 0.3f, colour = Foam,
            });
        }
    }

    /// <summary>Droplets (outlined cubes) that fly out, arc down under gravity and shrink to nothing.</summary>
    public class Droplets : MonoBehaviour
    {
        class Drop { public Transform t; public Vector3 v; public float life, size; }
        readonly List<Drop> _drops = new();
        float _age;

        /// <param name="push">Velocity they all share (spray thrown off a moving wave).</param>
        public static void Create(Vector3 p, float scale, int count, Vector3 push)
        {
            if (Debris.Suppressed || count <= 0) return;
            var go = new GameObject("Droplets") { layer = Layers.Fx };
            go.transform.position = p;
            var s = go.AddComponent<Droplets>();
            for (int i = 0; i < count; i++)
            {
                var t = Glow.Piece(go.transform, Glow.CubeMesh, i % 3 == 0 ? WaterVfx.Foam : WaterVfx.Aqua, "Drop");
                t.position = p + Random.insideUnitSphere * 0.2f * scale;
                t.rotation = Random.rotation;
                Vector3 dir = (Random.onUnitSphere + Vector3.up * 1.2f).normalized;
                s._drops.Add(new Drop
                {
                    t = t, v = push + dir * Random.Range(2f, 5f) * scale,
                    life = Random.Range(0.4f, 0.7f), size = Random.Range(0.08f, 0.14f) * Mathf.Max(0.5f, scale),
                });
            }
            s.Update();
        }

        void Update()
        {
            float dt = Time.deltaTime;
            _age += dt;
            bool any = false;
            foreach (var d in _drops)
            {
                if (d.t == null) continue;
                float u = _age / d.life;
                if (u >= 1f) { Destroy(d.t.gameObject); d.t = null; continue; }
                any = true;
                d.v += Vector3.down * (14f * dt);
                d.t.position += d.v * dt;
                d.t.localScale = Vector3.one * (d.size * (1f - u * u));
            }
            if (!any) Destroy(gameObject);
        }
    }

    // ====================================================================== healed

    /// <summary>Green plus signs rising off someone being healed, for as long as the healing lasts.</summary>
    public class HealView : MonoBehaviour
    {
        class Plus { public Transform t; public Vector3 offset; public float age, life; }
        ulong _who;
        float _age, _owed, _time = SpellPlans.OrbHealTime;
        readonly List<Plus> _plus = new();

        public static void Create(ulong who, float seconds = SpellPlans.OrbHealTime)
        {
            if (Debris.Suppressed) return;
            var go = new GameObject("Healing") { layer = Layers.Fx };
            var v = go.AddComponent<HealView>();
            v._who = who;
            v._time = seconds;
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            _age += dt;
            var c = WorldNet.FindCombatant(_who);
            bool on = c != null && c.IsAlive && _age < _time;
            Vector3 body = c == null ? transform.position : c.RagdollFocus != null ? c.RagdollFocus.position : c.transform.position;
            transform.position = body;
            if (on)
            {
                _owed += 14f * dt;
                while (_owed >= 1f)
                {
                    _owed -= 1f;
                    Vector2 ring = Random.insideUnitCircle.normalized * Random.Range(0.25f, 0.5f);
                    _plus.Add(new Plus { t = MakePlus(transform), offset = new Vector3(ring.x, Random.Range(-0.7f, 0.5f), ring.y), life = Random.Range(0.6f, 0.9f) });
                }
            }
            var cam = Camera.main;
            for (int i = _plus.Count - 1; i >= 0; i--)
            {
                var p = _plus[i];
                p.age += dt;
                if (p.age >= p.life) { Destroy(p.t.gameObject); _plus.RemoveAt(i); continue; }
                p.offset += Vector3.up * (1.3f * dt);
                p.t.position = body + p.offset;
                if (cam != null) p.t.rotation = Quaternion.LookRotation(p.t.position - cam.transform.position);
                float u = p.age / p.life;
                float grow = Mathf.Clamp01(u / 0.15f), shrink = 1f - Mathf.InverseLerp(0.5f, 1f, u);
                p.t.localScale = Vector3.one * (0.2f * grow * shrink);
            }
            if (!on && _plus.Count == 0) Destroy(gameObject);
        }

        /// <summary>A plus sign: two outlined green bars, facing the camera (local XY).</summary>
        static Transform MakePlus(Transform parent)
        {
            var holder = new GameObject("Plus") { layer = Layers.Fx }.transform;
            holder.SetParent(parent, false);
            holder.localScale = Vector3.zero;
            Glow.Piece(holder, Glow.CubeMesh, WaterVfx.Heal, "Bar").localScale = new Vector3(1f, 0.32f, 0.32f);
            Glow.Piece(holder, Glow.CubeMesh, WaterVfx.Heal, "Bar").localScale = new Vector3(0.32f, 1f, 0.32f);
            return holder;
        }
    }

    // ====================================================================== Water of Life

    /// <summary>
    /// The orb of healing water: a pale droplet that swells over a second, then takes on its full aqua colour with
    /// a splash and starts giving off green runes. Bobs and wobbles in place; bursts when knocked, drunk or left too long.
    /// </summary>
    public class OrbView : MonoBehaviour
    {
        WorldNet.OrbFx _fx;
        Transform _ball, _shine;
        MeshRenderer _ballR;
        RuneSpew _runes;
        bool _formed, _replayed, _done;

        public static OrbView Create(WorldNet.OrbFx fx)
        {
            var go = new GameObject("WaterOfLife") { layer = Layers.Fx };
            go.transform.position = fx.center;
            var v = go.AddComponent<OrbView>();
            v._fx = fx;
            v._replayed = Debris.Suppressed; // history for a late joiner: no splashes for orbs long gone
            v._ball = Glow.Piece(go.transform, Glow.SphereMesh, WaterVfx.Pale, "Water");
            v._ballR = v._ball.GetComponent<MeshRenderer>();
            v._shine = Glow.Piece(go.transform, Glow.CubeMesh, WaterVfx.Foam, "Shine", false);
            v._ball.localScale = v._shine.localScale = Vector3.zero;
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            if (_fx.ended >= 0 || t > SpellPlans.OrbGrow + SpellPlans.OrbLife + 2f)
            {
                if (!_replayed) Burst();
                Destroy(gameObject);
                return;
            }
            float r = SpellPlans.OrbRadiusAt(t);
            if (!_formed && t >= SpellPlans.OrbGrow)
            {
                // fully formed: the colour comes in with a splash, and the green runes start
                _formed = true;
                Glow.Tint(_ballR, WaterVfx.Aqua);
                if (!_replayed)
                {
                    EnergyBurst.Create(transform.position, new EnergyBurst.Settings
                    {
                        flashSize = 1.4f, flashTime = 0.18f, sparkCount = 10, sparkSize = 0.22f, sparkSpeed = new Vector2(3f, 6f), sparkLife = 0.3f, colour = WaterVfx.Aqua,
                    });
                    _runes = RuneSpew.Create(() => transform.position, () => this != null && !_done, SpellPlans.OrbLife + 1f, WaterVfx.Heal, 6f);
                }
            }
            // bobbing in place, wobbling like a water balloon (more while it's still forming)
            transform.position = _fx.center + Vector3.up * (0.06f * Mathf.Sin(t * 2.2f));
            float wob = (_formed ? 0.05f : 0.1f) * Mathf.Sin(Time.time * (_formed ? 6f : 11f));
            _ball.localScale = new Vector3(1f + wob, 1f - wob, 1f + wob) * (2f * r);
            // a highlight on the side facing the camera, upper left
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 toCam = (cam.transform.position - transform.position).normalized;
                Vector3 dir = (toCam * 1.2f + cam.transform.up * 0.7f - cam.transform.right * 0.6f).normalized;
                _shine.SetPositionAndRotation(transform.position + dir * (r * 0.82f), Quaternion.LookRotation(-toCam, cam.transform.up) * Quaternion.Euler(0f, 0f, 35f));
                _shine.localScale = new Vector3(r * 0.42f, r * 0.13f, 0.02f);
            }
        }

        void Burst()
        {
            Vector3 p = transform.position;
            WaterVfx.Splash(p, _fx.how == 2 ? 0.7f : 0.9f);
            if (_fx.how == 1) HealView.Create(_fx.who);
        }

        void OnDestroy()
        {
            _done = true;
            if (_runes != null) _runes.Stop();
        }
    }

    // ====================================================================== Bubble Away

    /// <summary>
    /// The bubble, cartoon style: a big inked circle drawn round the caster (always facing the camera, with a small
    /// gap like a brush stroke), a white highlight arc, and the faintest tint inside so the caster stays visible.
    /// A small bubble forms between the hands first; it wobbles when hit and bursts into droplets at the end.
    /// </summary>
    public class BubbleView : MonoBehaviour
    {
        WorldNet.BubbleFx _fx;
        ToonLine _ring, _shine;
        Transform _fill;
        readonly List<Vector3> _pts = new();
        bool _formed, _replayed;
        static Material _fillMat;

        /// <summary>The faint see-through tint: URP Unlit set to alpha-blended transparent (front faces only, no depth write).</summary>
        static Material FillMaterial()
        {
            if (_fillMat != null) return _fillMat;
            var m = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "Bubble Fill" };
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetColor("_BaseColor", new Color(0.45f, 0.9f, 1f, 0.14f));
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = 2990; // before the ink ring (3001/3002), so the ring draws over it
            m.SetShaderPassEnabled("DepthOnly", false);
            m.SetShaderPassEnabled("ShadowCaster", false);
            return _fillMat = m;
        }

        public static BubbleView Create(WorldNet.BubbleFx fx)
        {
            var go = new GameObject("Bubble") { layer = Layers.Fx };
            var v = go.AddComponent<BubbleView>();
            v._fx = fx;
            v._replayed = Debris.Suppressed;
            v._ring = new ToonLine(go.transform, 0.1f, WaterVfx.Aqua);
            v._shine = new ToonLine(go.transform, 0.08f, WaterVfx.Foam);
            var fill = new GameObject("Fill") { layer = Layers.Fx };
            fill.transform.SetParent(go.transform, false);
            fill.AddComponent<MeshFilter>().sharedMesh = Glow.SphereMesh;
            var mr = fill.AddComponent<MeshRenderer>();
            mr.sharedMaterial = FillMaterial();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            v._fill = fill.transform;
            v._fill.localScale = Vector3.zero;
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start) - SpellPlans.BubbleWindup;
            var c = WorldNet.FindCombatant(_fx.caster);
            Vector3 center = c != null ? SpellPlans.BubbleCenter(c.transform.position) : transform.position;
            bool over = _fx.ended >= 0 || t > SpellPlans.BubbleTime || c == null || !c.IsAlive;
            if (over)
            {
                // it bursts (late joiners don't see bubbles that are long gone go off)
                if (!_replayed && t >= 0f && t < SpellPlans.BubbleTime + 0.5f) WaterVfx.Splash(center, _fx.popped ? 1.4f : 1.1f);
                Destroy(gameObject);
                return;
            }
            float r;
            if (t < 0f)
            {
                // a little bubble forming between the hands
                float k = 1f + t / SpellPlans.BubbleWindup;
                if (c != null) center = (SpellVisuals.RightHand(c) + SpellVisuals.LeftHand(c)) * 0.5f;
                r = Mathf.Lerp(0.05f, 0.25f, k);
            }
            else
            {
                if (!_formed)
                {
                    _formed = true;
                    if (!_replayed) WaterVfx.Splash(center, 0.8f);
                }
                float pop = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / 0.15f), 3f);
                r = SpellPlans.BubbleRadius * Mathf.Lerp(0.25f, 1f, pop);
            }
            transform.position = center;
            // wobbles a little all the time, a lot when something hits it
            float hit = Mathf.Exp(-(float)(now - _fx.lastHit) * 7f);
            float wob = 0.025f * Mathf.Sin(Time.time * 5f) + 0.1f * hit * Mathf.Sin(Time.time * 40f);
            var cam = Camera.main;
            Vector3 right = cam != null ? cam.transform.right : Vector3.right;
            Vector3 up = cam != null ? cam.transform.up : Vector3.up;
            Circle(center, right * (1f + wob), up * (1f - wob), r, Time.time * 0.8f, 340f, 56);
            _ring.Set(_pts);
            // the tint sphere: a sphere of radius r looks bigger than a flat circle of radius r up close (its outline
            // is where the view grazes it), so shrink it until its outline sits on the ink circle
            float fr = r;
            if (cam != null)
            {
                float d = Vector3.Distance(cam.transform.position, center);
                fr = r * d / Mathf.Sqrt(d * d + r * r);
            }
            // scale by the mesh's real size rather than assuming Unity's 0.5 m sphere
            float meshR = Mathf.Max(0.01f, Glow.SphereMesh.bounds.extents.x);
            _fill.localScale = new Vector3(1f + wob, 1f - wob, 1f + wob) * (fr / meshR);
            _ring.SetWidth(Mathf.Lerp(0.04f, 0.1f, r / SpellPlans.BubbleRadius));
            Circle(center, right, up, r * 0.72f, 112f * Mathf.Deg2Rad, 48f, 12);
            _shine.Set(_pts);
            _shine.SetWidth(Mathf.Lerp(0.03f, 0.08f, r / SpellPlans.BubbleRadius));
        }

        /// <summary>An arc in the plane of <paramref name="a"/> and <paramref name="b"/> (radians from, degrees long).</summary>
        void Circle(Vector3 c, Vector3 a, Vector3 b, float r, float from, float sweepDeg, int steps)
        {
            _pts.Clear();
            float sweep = sweepDeg * Mathf.Deg2Rad;
            for (int i = 0; i <= steps; i++)
            {
                float ang = from + sweep * i / steps;
                _pts.Add(c + (a * Mathf.Cos(ang) + b * Mathf.Sin(ang)) * r);
            }
        }

        void OnDestroy()
        {
            _ring?.Destroy();
            _shine?.Destroy();
        }
    }

    // ====================================================================== Tsunami

    /// <summary>
    /// The tidal wave, built out of water voxels on a lattice (lanes across, rows up, columns from the curl at the
    /// front back to the tail): deep blue water with a foam crest curling over the front, all outlined in ink. It
    /// rises out of the floor, rides the floor heights the server worked out, and collapses at the end. Wind
    /// pushes its voxels about (they spring back), it parts round barriers and bubbles, and the lanes Arcane
    /// Judgment erases turn to void and float away.
    /// </summary>
    public class TsunamiView : MonoBehaviour
    {
        const float S = VoxelWorld.Size;
        const int Lip = 3;                 // lattice columns in front of the front: the curl
        const int Lanes = SpellPlans.TsunamiLanes;
        const float InkExtra = 0.045f;     // the outline cubes are this much bigger (each side)
        static readonly int Cols = Mathf.RoundToInt(SpellPlans.TsunamiDepth / S) + Lip;
        static readonly int Rows = Mathf.CeilToInt(SpellPlans.TsunamiHeight / S) + 2;

        struct Drop
        {
            public int lane, row, col;
            public bool foam;
            public Vector3 disp, vel;       // pushed about by wind, springing back
            public float erased;            // Time.time Arcane Judgment erased it (-1 = never)
            public Vector3 erasedAt;
        }

        WorldNet.TsunamiFx _fx;
        Drop[] _d;
        int[] _grid;
        bool[] _live;
        Vector3[] _pos;
        Mesh _body, _foam, _ink, _void;
        readonly List<Vector3> _vb = new(), _vf = new(), _vi = new(), _vv = new();
        readonly List<int> _tb = new(), _tf = new(), _ti = new(), _tv = new();
        readonly List<(Vector3 c, float r)> _spheres = new();
        readonly float[] _laneDeadAt = new float[Lanes];
        int _deadSeen;
        bool _splashed, _collapsed, _replayed;
        float _sprayOwed;
        static Material _inkMat;

        public static TsunamiView Create(WorldNet.TsunamiFx fx)
        {
            var go = new GameObject("Tsunami") { layer = Layers.Fx };
            var v = go.AddComponent<TsunamiView>();
            v._fx = fx;
            v._replayed = Debris.Suppressed;
            if (_inkMat == null)
            {
                // the outline cubes show only their far faces, so they rim the water like an inverted hull
                _inkMat = new Material(Glow.Shared) { name = "Tsunami Ink" };
                if (_inkMat.HasProperty("_Cull")) _inkMat.SetFloat("_Cull", 1f);
            }
            v._body = v.Layer("Water", Glow.Shared, WaterVfx.Deep);
            v._foam = v.Layer("Foam", Glow.Shared, WaterVfx.Foam);
            v._ink = v.Layer("Outline", _inkMat, Glow.Ink);
            v._void = v.Layer("Erased", Glow.Shared, WaterVfx.Void);
            v.Build();
            return v;
        }

        Mesh Layer(string name, Material mat, Color colour)
        {
            var go = new GameObject(name) { layer = Layers.Fx };
            go.transform.SetParent(transform, false);
            var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            Glow.Tint(r, colour);
            return m;
        }

        static int Index(int lane, int row, int col) => (lane * Rows + row) * Cols + col;

        /// <summary>Lays out the wave's voxels: a tall front with a hollow under its curl, a long low tail, a jagged foam crest.</summary>
        void Build()
        {
            var list = new List<Drop>();
            _grid = new int[Lanes * Rows * Cols];
            for (int i = 0; i < _grid.Length; i++) _grid[i] = -1;
            void Add(int lane, int row, int col, bool foam)
            {
                if (row < 0 || row >= Rows || col < 0 || col >= Cols) return;
                int g = Index(lane, row, col);
                if (_grid[g] >= 0) return;
                _grid[g] = list.Count;
                list.Add(new Drop { lane = lane, row = row, col = col, foam = foam, erased = -1f });
            }
            int depth = Cols - Lip;
            for (int lane = 0; lane < Lanes; lane++)
            {
                // lower towards its ends
                int e = Mathf.Min(lane, Lanes - 1 - lane);
                float taper = e >= 3 ? 1f : 0.55f + 0.15f * e;
                int crest = Mathf.Max(1, Mathf.RoundToInt(SpellPlans.TsunamiProfile(0.5f * S) * taper / S));
                // foam is only the top 10% of the wave's height
                int foamRow = Mathf.CeilToInt(crest * 0.9f);
                for (int k = 0; k < depth; k++)
                {
                    int rows = Mathf.Max(1, Mathf.RoundToInt(SpellPlans.TsunamiProfile((k + 0.5f) * S) * taper / S));
                    for (int row = 0; row < rows; row++)
                    {
                        // the hollow under the curl
                        if (k == 0 && row >= 3 && row < rows - 4) continue;
                        if (k == 1 && row >= 4 && row < rows - 4) continue;
                        Add(lane, row, k + Lip, row >= foamRow);
                    }
                    // spikes along the crest
                    if (k <= 2 && taper >= 1f && (lane + k) % 2 == 0) Add(lane, rows, k + Lip, rows >= foamRow);
                }
                // the curl over the front, ending in ragged teeth
                for (int j = 1; j <= Lip; j++)
                {
                    if (j == Lip && lane % 3 == 1) continue;
                    for (int row = crest - 5 + j; row < crest; row++) Add(lane, row, Lip - j, row >= foamRow);
                }
            }
            _d = list.ToArray();
            _live = new bool[_d.Length];
            _pos = new Vector3[_d.Length];
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            float life = SpellPlans.TsunamiLife;
            if (t > life + SpellPlans.TsunamiCollapse || _replayed && t > life) { Destroy(gameObject); return; }
            float dt = Time.deltaTime, time = Time.time;
            Vector3 F = _fx.dir, U = Vector3.up, R = Vector3.Cross(U, F);
            float front = SpellPlans.TsunamiFront(t);
            float rise = Mathf.Clamp01(t / SpellPlans.TsunamiRise);
            float fall = Mathf.Clamp01((t - life) / SpellPlans.TsunamiCollapse);
            Vector3 mid = _fx.origin + F * front + Vector3.up * (SpellPlans.TsunamiBase(_fx.ground, front) + 1f - _fx.origin.y);

            if (!_splashed)
            {
                _splashed = true;
                if (!_replayed)
                {
                    for (int k = -1; k <= 1; k++) WaterVfx.Splash(mid + R * (k * 2f) - F * 1.5f, 1.2f);
                    CameraRig.ShakeFrom(mid, 0.3f, 0.3f);
                }
            }
            if (fall > 0f && !_collapsed)
            {
                // crashes down at the end of its run
                _collapsed = true;
                for (int k = -2; k <= 2; k++) WaterVfx.Splash(mid + R * (k * 1.3f), 1.3f);
                CameraRig.ShakeFrom(mid, 0.25f, 0.25f);
            }

            // lanes Arcane Judgment has erased
            if (_fx.dead != _deadSeen)
            {
                for (int l = 0; l < Lanes; l++)
                    if ((_fx.dead & (1 << l)) != 0 && (_deadSeen & (1 << l)) == 0) _laneDeadAt[l] = time;
                _deadSeen = _fx.dead;
            }

            CollectSpheres(now);
            for (int i = 0; i < _d.Length; i++)
            {
                ref var d = ref _d[i];
                if (d.erased >= 0f) { _live[i] = false; continue; }
                float behind = (d.col - Lip + 0.5f) * S;
                float along = front - behind + fall * 1.2f; // collapsing water spills forward
                float y = (d.row + 0.5f) * S * rise * (1f - 0.9f * fall * fall);
                if (d.foam) y += 0.06f * Mathf.Sin(time * 7f + d.lane * 0.9f + d.col * 0.6f);
                Vector3 home = _fx.origin + F * along + R * SpellPlans.LaneU(d.lane);
                home.y = SpellPlans.TsunamiBase(_fx.ground, along) + y;
                // springing back after being blown about
                d.vel += (-55f * d.disp - 8f * d.vel) * dt;
                d.disp += d.vel * dt;
                if (d.disp.sqrMagnitude > 1.44f) d.disp = d.disp.normalized * 1.2f;
                Vector3 p = home + d.disp;
                // it parts round barriers and bubbles
                foreach (var (c, r) in _spheres)
                {
                    Vector3 o = p - c;
                    if (o.sqrMagnitude < r * r) p = c + (o.sqrMagnitude > 1e-6f ? o.normalized : Vector3.up) * r;
                }
                _pos[i] = p;
                _live[i] = true;
            }
            Blow(now, dt);
            Erase(time);
            BuildMeshes(R, U, F, fall, time);
            Spray(F, dt, rise, fall);
        }

        /// <summary>Active barriers and bubbles the water flows round.</summary>
        void CollectSpheres(double now)
        {
            _spheres.Clear();
            var net = WorldNet.Instance;
            if (net == null) return;
            foreach (var b in net.Barriers)
            {
                if (now < b.start + SpellPlans.BarrierWindup || now > b.start + SpellPlans.BarrierWindup + SpellPlans.BarrierTime) continue;
                var c = WorldNet.FindCombatant(b.caster);
                if (c != null) _spheres.Add((c.transform.position + Vector3.up * 0.1f, SpellPlans.BarrierRadius + 0.2f));
            }
            foreach (var b in net.Bubbles)
            {
                if (!WorldNet.BubbleActive(b, now)) continue;
                var c = WorldNet.FindCombatant(b.caster);
                if (c != null) _spheres.Add((SpellPlans.BubbleCenter(c.transform.position), SpellPlans.BubbleRadius + 0.2f));
            }
        }

        /// <summary>Shove and Tug push the voxels they pass through (the springs bring them back).</summary>
        void Blow(double now, float dt)
        {
            var net = WorldNet.Instance;
            if (net == null || net.Gusts.Count == 0) return;
            foreach (var g in net.Gusts.Values)
            {
                float gt = (float)(now - g.start);
                if (gt < 0f || gt > g.life) continue;
                Vector3 gc = SpellPlans.GustCenter(g.origin, g.dir, g.start, now);
                Quaternion inv = Quaternion.Inverse(Quaternion.LookRotation(g.dir));
                Vector3 h = SpellPlans.GustHalfSize + Vector3.one * 0.3f;
                for (int i = 0; i < _d.Length; i++)
                {
                    if (!_live[i]) continue;
                    Vector3 l = inv * (_pos[i] - gc);
                    if (Mathf.Abs(l.x) > h.x || Mathf.Abs(l.y) > h.y || Mathf.Abs(l.z) > h.z) continue;
                    _d[i].vel += g.dir * (40f * dt);
                }
            }
        }

        /// <summary>Voxels in erased lanes turn to void as the beam reaches them (or shortly after, wherever they are).</summary>
        void Erase(float time)
        {
            if (_fx.dead == 0) return;
            var net = WorldNet.Instance;
            for (int i = 0; i < _d.Length; i++)
            {
                if (!_live[i]) continue;
                int lane = _d[i].lane;
                if ((_fx.dead & (1 << lane)) == 0) continue;
                bool erase = time - _laneDeadAt[lane] > 0.4f;
                if (!erase && net != null)
                    foreach (var j in net.Judgments.Values)
                    {
                        Vector3 o = _pos[i] - j.CenterAt(WorldNet.Now);
                        if (new Vector2(o.x, o.z).magnitude <= SpellPlans.JudgmentRadiusOf(j.kind)) { erase = true; break; }
                    }
                if (!erase) continue;
                _d[i].erased = time;
                _d[i].erasedAt = _pos[i];
                _live[i] = false;
            }
        }

        bool Live(int lane, int row, int col)
        {
            if (lane < 0 || lane >= Lanes || row < 0 || row >= Rows || col < 0 || col >= Cols) return false;
            int k = _grid[Index(lane, row, col)];
            return k >= 0 && _live[k];
        }

        void BuildMeshes(Vector3 R, Vector3 U, Vector3 F, float fall, float time)
        {
            _vb.Clear(); _tb.Clear(); _vf.Clear(); _tf.Clear(); _vi.Clear(); _ti.Clear(); _vv.Clear(); _tv.Clear();
            float half = S * 0.49f * (1f - fall); // shrinks away as it collapses
            for (int i = 0; i < _d.Length; i++)
            {
                ref var d = ref _d[i];
                if (d.erased >= 0f)
                {
                    // erased: black, floating up, shrinking to nothing
                    float age = time - d.erased;
                    if (age < 0.7f) AddCube(_vv, _tv, d.erasedAt + Vector3.up * (age * 1.5f), S * 0.5f * (1f - age / 0.7f), 63, R, U, F);
                    continue;
                }
                if (!_live[i] || half <= 0.001f) continue;
                // only the faces with no water next to them
                int mask = 0;
                if (!Live(d.lane + 1, d.row, d.col)) mask |= 1;
                if (!Live(d.lane - 1, d.row, d.col)) mask |= 2;
                if (!Live(d.lane, d.row + 1, d.col)) mask |= 4;
                if (!Live(d.lane, d.row - 1, d.col)) mask |= 8;
                if (!Live(d.lane, d.row, d.col - 1)) mask |= 16;
                if (!Live(d.lane, d.row, d.col + 1)) mask |= 32;
                if (mask == 0) continue;
                if (d.foam) AddCube(_vf, _tf, _pos[i], half, mask, R, U, F);
                else AddCube(_vb, _tb, _pos[i], half, mask, R, U, F);
                AddCube(_vi, _ti, _pos[i], half + InkExtra * (1f - fall), mask, R, U, F);
            }
            Apply(_body, _vb, _tb);
            Apply(_foam, _vf, _tf);
            Apply(_ink, _vi, _ti);
            Apply(_void, _vv, _tv);
        }

        static void Apply(Mesh m, List<Vector3> v, List<int> t)
        {
            m.Clear();
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
        }

        /// <summary>
        /// The faces of a cube in the wave's frame (R right, U up, F forward); mask bits: +R -R +U -U +F -F.
        /// Each face is wound so it faces outward (cross of its two edge axes = its normal).
        /// </summary>
        static void AddCube(List<Vector3> v, List<int> t, Vector3 c, float h, int mask, Vector3 R, Vector3 U, Vector3 F)
        {
            Vector3 r = R * h, u = U * h, f = F * h;
            if ((mask & 1) != 0) Face(v, t, c + r, u, f);
            if ((mask & 2) != 0) Face(v, t, c - r, f, u);
            if ((mask & 4) != 0) Face(v, t, c + u, f, r);
            if ((mask & 8) != 0) Face(v, t, c - u, r, f);
            if ((mask & 16) != 0) Face(v, t, c + f, r, u);
            if ((mask & 32) != 0) Face(v, t, c - f, u, r);
        }

        static void Face(List<Vector3> v, List<int> t, Vector3 c, Vector3 a, Vector3 b)
        {
            int n = v.Count;
            v.Add(c - a - b);
            v.Add(c + a - b);
            v.Add(c + a + b);
            v.Add(c - a + b);
            t.Add(n); t.Add(n + 1); t.Add(n + 2);
            t.Add(n); t.Add(n + 2); t.Add(n + 3);
        }

        /// <summary>Spray thrown forward off the crest while it runs.</summary>
        void Spray(Vector3 F, float dt, float rise, float fall)
        {
            if (_replayed || rise < 1f || fall > 0f) return;
            _sprayOwed += 30f * dt;
            while (_sprayOwed >= 1f)
            {
                _sprayOwed -= 1f;
                int i = Random.Range(0, _d.Length);
                if (!_live[i] || !_d[i].foam) continue;
                Droplets.Create(_pos[i], 0.6f, 1, F * (SpellPlans.TsunamiSpeed * 0.9f) + Vector3.up * 2f);
            }
        }

        void OnDestroy()
        {
            if (_body) Destroy(_body);
            if (_foam) Destroy(_foam);
            if (_ink) Destroy(_ink);
            if (_void) Destroy(_void);
        }
    }
}
