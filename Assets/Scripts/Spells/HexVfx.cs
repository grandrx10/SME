using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    // ====================================================================== a hex on its way

    /// <summary>
    /// A hex being cast (power gathering at the caster's pointing left hand), then a ball of symbols that flies to its
    /// target wherever they go, and breaks over them.
    /// </summary>
    public class HexBallView : MonoBehaviour
    {
        class Sign { public RuneDrawing d; public Vector3 at; public float roll; }

        WorldNet.HexFx _fx;
        readonly List<Sign> _signs = new();
        Transform _core;
        TrailRenderer _trail;
        Vector3 _from, _last;
        const int Signs = 11;
        const float Ball = 0.24f, SignSize = 0.22f;

        public static void Charge(WorldNet.HexFx fx)
        {
            fx.charge = ArcaneCharge.Create(() => SpellVisuals.LeftHand(WorldNet.FindCombatant(fx.caster)), () => Casting(fx),
                SpellPlans.HexCast, SpellPlans.HexColour(fx.kind), true);
        }

        /// <summary>Still pointing (not dead, not knocked out of it, not long past when it should have gone).</summary>
        static bool Casting(WorldNet.HexFx fx)
        {
            var c = WorldNet.FindCombatant(fx.caster);
            return c != null && c.IsAlive && !WorldNet.Fizzled(fx.caster, fx.start, fx.start + SpellPlans.HexCast + 0.1) && WorldNet.Now < fx.start + SpellPlans.HexCast + 0.4;
        }

        public static void Create(WorldNet.HexFx fx)
        {
            if (fx.charge != null) { fx.charge.Release(); fx.charge = null; }
            var go = new GameObject("HexBall") { layer = Layers.Fx };
            var v = go.AddComponent<HexBallView>();
            v._fx = fx;
            v._from = v._last = SpellVisuals.LeftHand(WorldNet.FindCombatant(fx.caster));
            go.transform.position = v._from;
            Color colour = SpellPlans.HexColour(fx.kind);
            v._core = Glow.Piece(go.transform, Glow.SphereMesh, colour, "Core");
            v._core.localScale = Glow.SphereScale(0.07f);
            for (int i = 0; i < Signs; i++)
            {
                // spread evenly over the ball
                float y = 1f - 2f * (i + 0.5f) / Signs, r = Mathf.Sqrt(1f - y * y), a = i * 2.39996f;
                var host = new GameObject("Sign") { layer = Layers.Fx };
                host.transform.SetParent(go.transform, false);
                var d = new RuneDrawing(host);
                d.AddRune(RuneGlyphs.Rune(i), SignSize, 0.026f);
                d.Progress = 1f;
                d.SetColour(colour);
                v._signs.Add(new Sign { d = d, at = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r), roll = Random.Range(-40f, 40f) });
            }
            v._trail = Glow.Trail(go.transform, colour, 0.22f, 0.16f);
            v.LateUpdate();
        }

        void LateUpdate()
        {
            var t = WorldNet.FindCombatant(_fx.target);
            if (WorldNet.Instance == null || t == null) { End(false); return; }
            float u = Mathf.Clamp01((float)(WorldNet.Now - _fx.flyStart) / SpellPlans.HexFlight);
            Vector3 to = t.RagdollFocus != null ? t.RagdollFocus.position : t.transform.position + Vector3.up * 0.25f;
            float e = u * u * (2f - u); // pulls away, then rushes in
            _last = Vector3.Lerp(_from, to, e) + Vector3.up * (0.4f * Mathf.Sin(Mathf.PI * e));
            transform.position = _last;
            if (u >= 1f) { End(true); return; }

            float grow = Mathf.Clamp01(u * 5f), time = Time.time;
            Quaternion facing = RuneDrawing.FacingCamera();
            Quaternion spin = Quaternion.Euler(time * 210f, time * 320f, 0f);
            _core.localScale = Glow.SphereScale(0.07f * grow * (1f + 0.15f * Mathf.Sin(time * 40f)));
            foreach (var s in _signs)
            {
                s.d.Host.SetPositionAndRotation(transform.position + spin * s.at * (Ball * grow), facing * Quaternion.AngleAxis(s.roll, Vector3.up));
                s.d.Host.localScale = Vector3.one * grow;
            }
        }

        void End(bool landed)
        {
            if (_trail != null) Glow.DropTrail(_trail);
            if (landed)
            {
                var burst = EnergyBurst.Release;
                burst.colour = SpellPlans.HexColour(_fx.kind);
                EnergyBurst.Create(transform.position, burst);
                HexBodyFx.Flurry(WorldNet.FindCombatant(_fx.target), burst.colour);
            }
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            foreach (var s in _signs) s.d.Release();
        }
    }

    // ====================================================================== on a body

    /// <summary>
    /// Hexes that ride on a body, drawn by every client from replicated state: the bars filling up to its left, Stasis's
    /// two circles and the symbols coming off it, Mind Hex's chain to whoever has control. Whoever is Mind Hexed also sees
    /// purple symbols coming off the one who did it (nobody else does).
    /// </summary>
    public class HexBodyFx : MonoBehaviour
    {
        class Bar { public Transform root, fill; public byte kind; public double from; public float size, shown; public bool on; }

        Combatant _c;
        readonly Bar[] _bars = new Bar[HexMarks.Max];

        const float BarHeight = 1.25f, BarWidth = 0.13f, BarInk = 0.07f;
        static readonly Color BarWhite = new(0.93f, 0.93f, 0.95f, 1f);

        void Awake() => _c = GetComponent<Combatant>();

        Vector3 Body => _c.RagdollFocus != null ? _c.RagdollFocus.position : _c.transform.position;

        /// <summary>A flurry of symbols off a body.</summary>
        public static void Flurry(Combatant c, Color colour)
        {
            if (c == null || Debris.Suppressed) return;
            var s = RuneSpew.Create(() => c == null ? Vector3.zero : c.RagdollFocus != null ? c.RagdollFocus.position : c.transform.position,
                () => c != null && c.IsSpawned, 0.45f, colour, 16f, new Vector3(0.35f, 0.75f, 0.35f));
            if (s != null) s.Burst(8, 1.4f);
        }

        void LateUpdate()
        {
            if (_c == null || !_c.IsSpawned) return;
            double now = WorldNet.Now;
            var st = _c.Status.Value;
            bool seen = _c.IsAlive && (_c.Visible || _c.HasLiveRagdoll) && !st.Taken(now);
            Bars(seen, now);
            if (seen && _c.Shattering && _shatterFrom != st.ShatterFrom)
            {
                _shatterFrom = st.ShatterFrom;
                GlassCase.Create(_c, st.ShatterFrom);
            }
            Stasis(seen && _c.InStasis, st);
            Chain(seen && _c.MindHeld, st);
            Culprit(seen, now);
        }

        // ---------------- bars ----------------

        void Bars(bool seen, double now)
        {
            var marks = _c.Hexes.Value;
            var cam = Camera.main;
            int shown = 0;
            for (int i = 0; i < HexMarks.Max; i++)
            {
                var m = marks[i];
                bool on = seen && cam != null && m.Live(now);
                var bar = _bars[i];
                if (on && bar != null && (bar.kind != m.Kind || bar.from != m.From)) { Destroy(bar.root.gameObject); bar = _bars[i] = null; } // another one in its place
                if (bar == null)
                {
                    if (!on) continue;
                    bar = _bars[i] = MakeBar(m.Kind, m.From);
                }
                bar.size = Mathf.MoveTowards(bar.size, on ? 1f : 0f, Time.deltaTime / (on ? 0.16f : 0.12f));
                if (bar.size <= 0f) { Destroy(bar.root.gameObject); _bars[i] = null; continue; }
                if (on) bar.shown = Mathf.MoveTowards(bar.shown, m.Fill, Time.deltaTime * Mathf.Max(0.6f, Mathf.Abs(m.Fill - bar.shown) * 8f));
                if (cam == null) continue;
                Lay(bar, shown++, cam);
            }
            SkyBar(seen && cam != null, shown, cam);
        }

        /// <summary>Upright at the body's left as it's seen from here, several side by side (<paramref name="slot"/> 0 nearest).</summary>
        void Lay(Bar bar, int slot, Camera cam)
        {
            Vector3 right = cam.transform.right;
            right.y = 0f;
            right = right.sqrMagnitude > 1e-4f ? right.normalized : Vector3.right;
            Vector3 look = Vector3.Cross(right, Vector3.up); // the way the camera looks, level
            float grow = SpellPlans.BodyGrow(_c) * _c.transform.localScale.y;
            Vector3 at = Body - right * ((0.62f + 0.27f * slot) * grow) + Vector3.up * 0.1f;
            bar.root.SetPositionAndRotation(at, Quaternion.LookRotation(look));
            float s = bar.size * bar.size * (3f - 2f * bar.size) * grow;
            bar.root.localScale = new Vector3(s, s, 1f);
            float f = Mathf.Clamp01(bar.shown);
            bar.fill.localScale = new Vector3(BarWidth, Mathf.Max(0.001f, BarHeight * f), 0.01f);
            bar.fill.localPosition = new Vector3(0f, -BarHeight * 0.5f * (1f - f), -0.02f);
        }

        // ---------------- Skyland's fall bar ----------------

        Bar _sky;
        SkyWrap.Heat _skyHeat;
        static readonly Color SkyEmpty = new(0.2f, 0.21f, 0.26f, 1f);

        /// <summary>Every client: <paramref name="c"/> fell out of Skyland's bottom (its bar fills a third).</summary>
        public static void SkyFall(Combatant c)
        {
            if (c != null && c.TryGetComponent<HexBodyFx>(out var fx)) fx._skyHeat.Add();
        }

        /// <summary>Every client: a new round (or a reset dummy): the fall bar is empty.</summary>
        public static void SkyClear(Combatant c)
        {
            if (c != null && c.TryGetComponent<HexBodyFx>(out var fx)) fx._skyHeat.Clear();
        }

        /// <summary>Drawn like a hex's bar, after them: white filling a dark bar, there while it has anything in it.</summary>
        void SkyBar(bool seen, int slot, Camera cam)
        {
            float heat = _skyHeat.Now;
            bool on = seen && heat > 0f;
            if (_sky == null)
            {
                if (!on) return;
                _sky = MakeBar(SkyEmpty, BarWhite);
            }
            _sky.size = Mathf.MoveTowards(_sky.size, on ? 1f : 0f, Time.deltaTime / (on ? 0.16f : 0.12f));
            if (_sky.size <= 0f) { Destroy(_sky.root.gameObject); _sky = null; return; }
            _sky.shown = Mathf.MoveTowards(_sky.shown, heat, Time.deltaTime * Mathf.Max(0.6f, Mathf.Abs(heat - _sky.shown) * 8f));
            if (cam != null) Lay(_sky, slot, cam);
        }

        Bar MakeBar(byte kind, double from)
        {
            var bar = MakeBar(BarWhite, SpellPlans.HexFill(kind));
            bar.kind = kind;
            bar.from = from;
            return bar;
        }

        static Bar MakeBar(Color empty, Color fillColour)
        {
            var root = new GameObject("HexBar") { layer = Layers.Fx }.transform;
            var ink = Glow.Piece(root, Glow.CubeMesh, Glow.Ink, "Ink", false);
            ink.localScale = new Vector3(BarWidth + BarInk, BarHeight + BarInk, 0.01f);
            ink.localPosition = new Vector3(0f, 0f, 0.02f);
            var white = Glow.Piece(root, Glow.CubeMesh, empty, "Empty", false);
            white.localScale = new Vector3(BarWidth, BarHeight, 0.01f);
            var fill = Glow.Piece(root, Glow.CubeMesh, fillColour, "Fill", false);
            return new Bar { root = root, fill = fill };
        }

        // ---------------- Stasis ----------------

        Transform _circles;
        RuneDrawing _above, _below;
        double _stasisFrom, _shatterFrom;
        float _stasisSize, _stasisAge;

        void Stasis(bool on, BodyStatus st)
        {
            if (on && _stasisFrom != st.StasisFrom)
            {
                _stasisFrom = st.StasisFrom;
                ClearCircles();
                _circles = new GameObject("StasisCircles") { layer = Layers.Fx }.transform;
                _above = MakeCircle();
                _below = MakeCircle();
                _stasisSize = 1f;
                _stasisAge = 0f;
                if (!Debris.Suppressed)
                {
                    var c = _c;
                    double from = _stasisFrom;
                    RuneSpew.Create(() => c == null ? Vector3.zero : c.RagdollFocus != null ? c.RagdollFocus.position : c.transform.position,
                        () => c != null && c.IsSpawned && c.InStasis && c.Status.Value.StasisFrom == from, SpellPlans.StasisTime,
                        SpellPlans.HexTurquoise, 15f, new Vector3(0.3f, 0.75f, 0.3f));
                    var burst = EnergyBurst.Release;
                    burst.colour = SpellPlans.HexTurquoise;
                    EnergyBurst.Create(Body, burst);
                    CameraRig.ShakeFrom(Body, 0.25f, 0.25f);
                }
            }
            if (_circles == null) return;
            float dt = Time.deltaTime;
            _stasisAge += dt;
            if (!on)
            {
                if (_stasisSize >= 1f && !Debris.Suppressed && _c.IsAlive) EnergyBurst.Create(Body, BreakBurst);
                _stasisSize -= dt / 0.18f;
                if (_stasisSize <= 0f) { ClearCircles(); return; }
            }
            float grow = SpellPlans.BodyGrow(_c) * _c.transform.localScale.y, s = Mathf.Max(0.001f, _stasisSize * _stasisSize) * grow;
            float written = _stasisAge / 0.35f;
            Vector3 body = Body;
            bool down = _c.HasLiveRagdoll;
            Vector3 low = body + Vector3.down * ((down ? 0.45f : 0.88f) * grow), high = body + Vector3.up * ((down ? 0.6f : 1.25f) * grow);
            if (!on) { low = Vector3.Lerp(body, low, _stasisSize); high = Vector3.Lerp(body, high, _stasisSize); }
            Place(_below, low, _stasisAge * 40f, s, written);
            Place(_above, high, -_stasisAge * 40f, s, written);
        }

        static readonly EnergyBurst.Settings BreakBurst = new()
        {
            flashSize = 0.9f, flashTime = 0.15f, sparkCount = 12, sparkSize = 0.28f, sparkSpeed = new Vector2(4f, 8f), sparkLife = 0.3f, colour = SpellPlans.HexTurquoise,
        };

        static void Place(RuneDrawing d, Vector3 at, float turn, float scale, float written)
        {
            d.Progress = written;
            d.Host.SetPositionAndRotation(at, Quaternion.Euler(0f, turn, 0f));
            d.Host.localScale = Vector3.one * scale;
        }

        RuneDrawing MakeCircle()
        {
            var go = new GameObject("Circle") { layer = Layers.Fx };
            go.transform.SetParent(_circles, false);
            var d = new RuneDrawing(go);
            d.AddRune(JudgmentView.CircleStrokes(), SpellPlans.StasisCircle * 2f, 0.035f);
            d.Progress = 0f;
            d.SetColour(SpellPlans.HexTurquoise);
            return d;
        }

        void ClearCircles()
        {
            _above?.Release();
            _below?.Release();
            _above = _below = null;
            if (_circles != null) Destroy(_circles.gameObject);
            _circles = null;
        }

        // ---------------- Mind ----------------

        Transform _chain;
        readonly List<Transform> _links = new();
        float _chainGrow;
        Vector3 _chainFrom, _chainTo;
        const float LinkStep = 0.2f;

        /// <summary>A purple chain from whoever has control to this body (as the Empress's): slack close by, taut near where it breaks.</summary>
        void Chain(bool on, BodyStatus st)
        {
            var master = on ? WorldNet.FindCombatant(st.MindBy) : null;
            bool shown = master != null && master.IsAlive;
            if (shown)
            {
                _chainFrom = master.RagdollFocus != null ? master.RagdollFocus.position : master.transform.position + Vector3.up * 0.1f;
                _chainTo = Body + (_c.HasLiveRagdoll ? Vector3.zero : Vector3.up * 0.4f); // round the neck and shoulders
            }
            _chainGrow = Mathf.MoveTowards(_chainGrow, shown ? 1f : 0f, Time.deltaTime / (shown ? 0.2f : 0.07f));
            if (_chainGrow <= 0f)
            {
                if (_chain != null) { Destroy(_chain.gameObject); _chain = null; _links.Clear(); }
                return;
            }
            if (_chain == null) _chain = new GameObject("MindChain") { layer = Layers.Fx }.transform;

            Vector3 span = _chainTo - _chainFrom;
            float len = span.magnitude, taut = Mathf.Clamp01(len / SpellPlans.MindBreak);
            int n = Mathf.Clamp(Mathf.CeilToInt(len / LinkStep), 1, 100);
            while (_links.Count < n) _links.Add(Glow.Piece(_chain, Glow.SphereMesh, SpellPlans.HexPurple, "Link"));
            float sag = len * Mathf.Lerp(0.12f, 0.01f, taut * taut);
            Vector3 Point(float u) => _chainFrom + span * u + Vector3.down * (sag * 4f * u * (1f - u));
            for (int i = 0; i < _links.Count; i++)
            {
                var l = _links[i];
                bool use = i < n;
                if (l.gameObject.activeSelf != use) l.gameObject.SetActive(use);
                if (!use) continue;
                float u0 = i / (float)n, u1 = (i + 1) / (float)n;
                float s = shown ? (u1 <= _chainGrow + 1e-3f ? 1f : 0f) : _chainGrow;
                Vector3 a = Point(u0), b = Point(u1), d = b - a;
                if (d.sqrMagnitude < 1e-6f) d = Vector3.forward * 1e-3f;
                l.SetPositionAndRotation((a + b) * 0.5f, Quaternion.LookRotation(d) * Quaternion.Euler(0f, 0f, (i & 1) == 0 ? 0f : 90f));
                l.localScale = new Vector3(0.05f, 0.1f, d.magnitude * 1.3f) * s;
            }
        }

        // ---------------- who did it (only on the hexed player's own screen) ----------------

        ulong _culprit;

        void Culprit(bool seen, double now)
        {
            ulong who = 0;
            if (seen && _c is PlayerController p && p.IsOwner)
            {
                if (_c.MindHeld) who = _c.Status.Value.MindBy;
                else if (_c.Hexes.Value.Find(SpellPlans.MindHex, now, out var m)) who = m.By;
            }
            if (who == _culprit) return;
            _culprit = who;
            if (who == 0 || Debris.Suppressed) return;
            var c = WorldNet.FindCombatant(who);
            if (c == null) { _culprit = 0; return; }
            RuneSpew.Create(() => c == null ? Vector3.zero : c.RagdollFocus != null ? c.RagdollFocus.position : c.transform.position + Vector3.up * 0.2f,
                () => this != null && c != null && c.IsSpawned && c.IsAlive && _culprit == who, SpellPlans.HexTime + SpellPlans.MindTime + 2f,
                SpellPlans.HexPurple, 16f, new Vector3(0.3f, 0.7f, 0.3f));
        }

        void OnDestroy()
        {
            foreach (var b in _bars) if (b != null && b.root != null) Destroy(b.root.gameObject);
            if (_sky != null && _sky.root != null) Destroy(_sky.root.gameObject);
            ClearCircles();
            if (_chain != null) Destroy(_chain.gameObject);
        }
    }
}
