using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Attraction / Repulsion ball, as on the cards. Water: an inked aqua ball with a highlight, short streaks
    /// rushing in towards it from its reach. Flame: a hot core in a spiky sun of yellow/orange/red tongues pointing
    /// out, streaks flung away from it. Both swell as they go; cut short, they shrink away in 0.3 s.
    /// </summary>
    public class ForceView : MonoBehaviour
    {
        /// <summary>
        /// The ink hull (SME/Outline) sticks out 14% of the mesh's scale all round, so a sphere scaled to diameter d
        /// shows as d × 1.28 across. Divide by this to make the outlined ball exactly the size it acts at.
        /// </summary>
        public const float OutlineGrow = 1.28f;

        class Streak { public Transform t; public Vector3 dir; public float age, life; }
        class Spike { public Transform t; public Vector3 dir; public float len, phase, speed; }

        WorldNet.ForceFx _fx;
        Transform _ball, _shine;
        readonly List<Streak> _streaks = new();
        readonly List<Spike> _spikes = new();
        float _owed;
        Vector3 _shown;
        bool _placed, _burst;

        public static ForceView Create(WorldNet.ForceFx fx)
        {
            var go = new GameObject(fx.water ? "Attraction" : "Repulsion") { layer = Layers.Fx };
            var v = go.AddComponent<ForceView>();
            v._fx = fx;
            v._ball = Glow.Piece(go.transform, Glow.SphereMesh, fx.water ? WaterVfx.Aqua : FireVfx.Orange, "Ball");
            if (fx.water) v._shine = Glow.Piece(go.transform, Glow.CubeMesh, WaterVfx.Foam, "Shine", false);
            else
            {
                // the sun's rays: flame tongues standing out of the ball all round
                for (int i = 0; i < 14; i++)
                {
                    var t = FireVfx.Tongue(go.transform, i % 3 == 0 ? FireVfx.Yellow : i % 3 == 1 ? FireVfx.Orange : FireVfx.Red);
                    v._spikes.Add(new Spike { t = t, dir = Random.onUnitSphere, len = Random.Range(0.7f, 1.2f), phase = Random.value * 10f, speed = Random.Range(10f, 16f) });
                }
            }
            go.transform.localScale = Vector3.zero;
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            var caster = WorldNet.FindCombatant(_fx.caster);
            float dt = Time.deltaTime;

            // where it is: in the raised hand while it gathers, then where the server says, carried on smoothly
            Vector3 hand = caster != null ? (_fx.water ? SpellVisuals.LeftHand(caster) : SpellVisuals.RightHand(caster)) : transform.position;
            Vector3 target = t < SpellPlans.OrbRaise || !_fx.moved ? hand + Vector3.up * 0.15f : _fx.PositionAt(now);
            // carried along at its own speed (no lag behind where it really is), nudged onto each update from the server
            Vector3 moving = _fx.moved && t >= SpellPlans.OrbRaise ? _fx.vel * dt : Vector3.zero;
            _shown = _placed && (target - _shown).sqrMagnitude < 9f ? Vector3.Lerp(_shown + moving, target, 1f - Mathf.Exp(-25f * dt)) : target;
            _placed = true;
            transform.position = _shown;

            float life = Mathf.Max(0f, t - SpellPlans.OrbRaise);
            float r = t < SpellPlans.OrbRaise
                ? SpellPlans.ForceRadius(_fx.water, 0f) * Mathf.Clamp01(t / SpellPlans.OrbRaise) * 0.6f // gathering in the hand
                : SpellPlans.ForceRadius(_fx.water, life);
            // ended: cut short shrinks away fast; ran its course pops
            float shrink = 1f;
            if (_fx.ended >= 0)
            {
                float k = (float)(now - _fx.ended) / SpellPlans.ForceDissipate;
                if (!_burst && !_fx.cut && !Debris.Suppressed)
                {
                    _burst = true;
                    if (_fx.water) WaterVfx.Splash(_shown, 1f); else FireVfx.Burst(_shown, 0.9f);
                }
                if (k >= 1f || !_fx.cut) { Finish(); return; }
                shrink = 1f - k;
            }
            transform.localScale = Vector3.one;
            float wob = 0.06f * Mathf.Sin(Time.time * (_fx.water ? 7f : 13f));
            _ball.localScale = new Vector3(1f + wob, 1f - wob, 1f + wob) * (2f * r * shrink / OutlineGrow); // outline included, it's radius r

            var cam = Camera.main;
            if (_shine != null && cam != null)
            {
                Vector3 toCam = (cam.transform.position - _shown).normalized;
                Vector3 dir = (toCam * 1.2f + cam.transform.up * 0.7f - cam.transform.right * 0.6f).normalized;
                _shine.SetPositionAndRotation(_shown + dir * (r * shrink * 0.82f), Quaternion.LookRotation(-toCam, cam.transform.up) * Quaternion.Euler(0f, 0f, 35f));
                _shine.localScale = new Vector3(r * 0.42f, r * 0.13f, 0.02f) * shrink;
            }

            // the sun's flickering rays
            foreach (var s in _spikes)
            {
                float flick = 0.7f + 0.3f * Mathf.Sin(Time.time * s.speed + s.phase);
                s.t.localPosition = s.dir * (r * 0.85f * shrink);
                s.t.rotation = Quaternion.LookRotation(s.dir);
                float len = s.len * r * 1.4f * flick * shrink, w = r * 0.45f * shrink;
                s.t.localScale = new Vector3(w, w, Mathf.Max(0.001f, len));
            }

            float reachNow = SpellPlans.ForceReach(_fx.water, life);
            if (_fx.water && t >= SpellPlans.OrbRaise && _fx.ended < 0) Rubble(reachNow, r, dt);

            // streaks: rushing in from the edge of its reach (water), or flung out to it (flame)
            if (t >= SpellPlans.OrbRaise && _fx.ended < 0)
            {
                _owed += dt * 26f;
                while (_owed >= 1f)
                {
                    _owed -= 1f;
                    var st = Glow.Piece(transform, Glow.CubeMesh, _fx.water ? WaterVfx.Aqua : FireVfx.Random3(), "Streak");
                    _streaks.Add(new Streak { t = st, dir = Random.onUnitSphere, life = Random.Range(0.25f, 0.4f) });
                }
            }
            float reach = SpellPlans.ForceReach(_fx.water, life);
            for (int i = _streaks.Count - 1; i >= 0; i--)
            {
                var s = _streaks[i];
                s.age += dt;
                float u = s.age / s.life;
                if (u >= 1f || s.t == null) { if (s.t != null) Destroy(s.t.gameObject); _streaks.RemoveAt(i); continue; }
                // (flame: just spitting off it -- it pushes nothing until it goes off)
                float along = _fx.water ? Mathf.Lerp(reach * 1.25f, r, u * u) : Mathf.Lerp(r, r * 2.2f, Mathf.Sqrt(u));
                s.t.localPosition = s.dir * along;
                s.t.rotation = Quaternion.LookRotation(s.dir);
                float w = 0.06f * (1f - u) * shrink;
                s.t.localScale = new Vector3(w, w, 0.45f * (1f - u * 0.5f) * shrink);
            }
        }

        // ---------------- rubble ----------------

        class Caught { public Debris d; public Vector3 axis; public float phase, radius, ring, height; }
        readonly List<Caught> _caught = new();
        const int MaxCaught = 90;

        /// <summary>
        /// Attraction: loose rubble in reach is caught and pulled in to whirl round the ball (each piece on its own
        /// tilted orbit, drawn in closer over time), carried along with it. Repulsion: rubble in reach is shoved away.
        /// </summary>
        void Rubble(float reach, float r, float dt)
        {
            if (Debris.All.Count == 0 && _caught.Count == 0) return;
            if (_fx.water)
            {
                if (_caught.Count < MaxCaught)
                    foreach (var d in Debris.All)
                    {
                        if (d == null || d.Body == null || d.Held) continue;
                        if ((d.Body.position - _shown).sqrMagnitude > reach * reach) continue;
                        d.Held = true;
                        // caught: no longer simulated (no collisions, no gravity), just carried round; physics again once thrown out
                        d.Body.isKinematic = true;
                        d.Body.detectCollisions = false;
                        _caught.Add(new Caught
                        {
                            d = d, axis = (Vector3.up + Random.insideUnitSphere * 0.8f).normalized, phase = Random.value * Mathf.PI * 2f,
                            radius = Mathf.Max(r * 1.3f, (d.Body.position - _shown).magnitude), ring = Random.Range(1.25f, 2.1f), height = Random.Range(-0.4f, 0.4f),
                        });
                        if (_caught.Count >= MaxCaught) break;
                    }
                float time = Time.time;
                for (int i = _caught.Count - 1; i >= 0; i--)
                {
                    var c = _caught[i];
                    if (c.d == null || c.d.Body == null) { _caught.RemoveAt(i); continue; }
                    // spiralling in to a tight whirl just outside the ball
                    c.radius = Mathf.MoveTowards(c.radius, r * c.ring, dt * 5f);
                    Vector3 side = Vector3.Cross(c.axis, Vector3.right);
                    if (side.sqrMagnitude < 1e-3f) side = Vector3.Cross(c.axis, Vector3.forward);
                    side.Normalize();
                    Vector3 other = Vector3.Cross(c.axis, side);
                    float a = c.phase + time * 6f;
                    Vector3 target = _shown + (side * Mathf.Cos(a) + other * Mathf.Sin(a)) * c.radius + c.axis * (c.height * r);
                    c.d.KeepAlive(1.5f);
                    Vector3 p = c.d.Body.position;
                    c.d.Body.MovePosition(Vector3.Lerp(p, target, 1f - Mathf.Exp(-9f * dt)) + _fx.vel * dt);
                    c.d.Body.MoveRotation(c.d.Body.rotation * Quaternion.Euler(c.axis * (240f * dt)));
                }
            }
            else
            {
                foreach (var d in Debris.All)
                {
                    if (d == null || d.Body == null || d.Held) continue;
                    Vector3 o = d.Body.position - _shown;
                    float dist = o.magnitude;
                    if (dist > reach || dist < 1e-3f) continue;
                    d.Body.linearVelocity += o / dist * (90f * dt);
                }
            }
        }

        /// <summary>The ball's gone: what it held flies off (the way it was going), under gravity again.</summary>
        void ReleaseRubble()
        {
            foreach (var c in _caught)
            {
                if (c.d == null || c.d.Body == null) continue;
                Vector3 o = c.d.Body.position - _shown;
                // simulated again from here
                c.d.Body.isKinematic = false;
                c.d.Body.detectCollisions = true;
                c.d.Body.useGravity = true;
                c.d.Body.linearVelocity = _fx.vel * 0.6f + (o.sqrMagnitude > 1e-4f ? o.normalized : Random.onUnitSphere) * Random.Range(3f, 7f) + Vector3.up * 2f;
                c.d.Body.angularVelocity = Random.insideUnitSphere * 6f;
                c.d.Held = false;
            }
            _caught.Clear();
        }

        void Finish()
        {
            Destroy(gameObject);
        }

        void OnDestroy() => ReleaseRubble();
    }

    // ====================================================================== Repulsion going off

    /// <summary>
    /// Repulsion's blast: a white-hot core that swells and collapses, a shell of outlined flame tongues flung out
    /// in every direction, and an inked ring of fire racing out along the ground. Tongue tips and the ring stop
    /// exactly at the blast's radius (what's drawn is what it hits), then everything shrinks away to nothing.
    /// </summary>
    public class RepulsionBlast : MonoBehaviour
    {
        const float Life = 0.6f;
        const int ShellTongues = 40, RingPieces = 36, GroundTongues = 18;

        class Piece { public Transform t; public Vector3 dir; public float len, width, delay; }

        readonly List<Piece> _shell = new();
        readonly List<Piece> _ground = new();
        readonly List<Transform> _ring = new();
        Transform _core, _heart;
        float _radius, _age;

        public static RepulsionBlast Create(Vector3 point, float radius)
        {
            var go = new GameObject("RepulsionBlast") { layer = Layers.Fx };
            go.transform.position = point;
            var b = go.AddComponent<RepulsionBlast>();
            b._radius = radius;
            b._core = Glow.Piece(go.transform, Glow.SphereMesh, FireVfx.Orange, "Core");
            b._heart = Glow.Piece(go.transform, Glow.SphereMesh, new Color(5f, 4.2f, 2.2f, 1f), "Heart", false);
            // the shell: evenly spread (golden spiral), each tongue pointing straight out
            for (int i = 0; i < ShellTongues; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / ShellTongues, rr = Mathf.Sqrt(1f - y * y), a = i * 2.39996f;
                var d = new Vector3(Mathf.Cos(a) * rr, y, Mathf.Sin(a) * rr);
                b._shell.Add(new Piece
                {
                    t = FireVfx.Tongue(go.transform, i % 3 == 0 ? FireVfx.Yellow : i % 3 == 1 ? FireVfx.Orange : FireVfx.Red),
                    dir = d, len = Random.Range(0.22f, 0.32f), width = Random.Range(0.08f, 0.12f), delay = Random.Range(0f, 0.05f),
                });
            }
            // low tongues licking out along the ground
            for (int i = 0; i < GroundTongues; i++)
            {
                float a = (i + Random.value * 0.5f) / GroundTongues * Mathf.PI * 2f;
                b._ground.Add(new Piece
                {
                    t = FireVfx.Tongue(go.transform, i % 2 == 0 ? FireVfx.Red : FireVfx.Orange),
                    dir = new Vector3(Mathf.Cos(a), 0.12f, Mathf.Sin(a)).normalized, len = Random.Range(0.18f, 0.26f), width = Random.Range(0.1f, 0.14f),
                });
            }
            // the shockwave: a ring of hot segments, each inked
            for (int i = 0; i < RingPieces; i++) b._ring.Add(Glow.Piece(go.transform, Glow.CubeMesh, FireVfx.Yellow, "Ring"));

            EnergyBurst.Create(point, new EnergyBurst.Settings
            {
                flashSize = radius * 0.8f, flashTime = 0.2f, sparkCount = 26, sparkSize = 0.5f, sparkSpeed = new Vector2(radius * 2f, radius * 4f), sparkLife = 0.4f, colour = FireVfx.Orange,
            });
            CameraRig.ShakeFrom(point, 1.1f, 0.45f);
            b.LateUpdate();
            return b;
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            float u = _age / Life;
            if (u >= 1f) { Destroy(gameObject); return; }
            float R = _radius;

            // the core: punches out fast, then collapses
            float coreU = Mathf.Clamp01(_age / 0.3f);
            float core = coreU < 0.25f ? coreU / 0.25f : 1f - (coreU - 0.25f) / 0.75f;
            core = Mathf.Max(0f, core);
            _core.localScale = Vector3.one * (2f * R * 0.42f * core / ForceView.OutlineGrow);
            _heart.localScale = Vector3.one * (2f * R * 0.3f * core);

            // the shell flies out (fast, then slowing) until the tips just reach the radius, then shrinks away
            foreach (var p in _shell) Tongue(p, u, R, 1f);
            foreach (var p in _ground) Tongue(p, u, R, 0.9f);

            // the ring races out along the ground to the radius, thinning as it goes
            float out_ = 1f - Mathf.Pow(1f - Mathf.Clamp01(u / 0.45f), 3f);
            float ringShrink = 1f - Mathf.Clamp01((u - 0.45f) / 0.55f);
            float ringR = Mathf.Lerp(R * 0.25f, R, out_);
            float thick = R * 0.07f * ringShrink;
            float seg = 2f * Mathf.PI * ringR / RingPieces * 1.05f;
            for (int i = 0; i < _ring.Count; i++)
            {
                float a = (i + 0.5f) / RingPieces * Mathf.PI * 2f;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                // (inside the radius by its own half-thickness and outline, so its outside edge is the blast's edge)
                _ring[i].localPosition = d * (ringR - thick * 0.65f) + Vector3.up * 0.1f;
                _ring[i].localRotation = Quaternion.LookRotation(Vector3.Cross(Vector3.up, d));
                _ring[i].localScale = new Vector3(Mathf.Max(0.001f, thick), Mathf.Max(0.001f, thick * 0.6f), Mathf.Max(0.001f, seg)) / 1.14f;
            }
        }

        /// <summary>One tongue flying out along its direction, its tip ending exactly at <paramref name="reach"/> × the radius.</summary>
        void Tongue(Piece p, float u, float R, float reach)
        {
            float k = Mathf.Clamp01((u - p.delay) / 0.4f);
            float go = 1f - Mathf.Pow(1f - k, 3f);
            float shrink = 1f - Mathf.Clamp01((u - 0.4f) / 0.6f);
            float len = p.len * R * shrink * Mathf.Clamp01(k * 3f);
            float baseAt = Mathf.Lerp(0.1f * R, reach * R - len, go);
            p.t.localPosition = p.dir * baseAt;
            p.t.rotation = Quaternion.LookRotation(p.dir);
            float w = p.width * R * shrink;
            p.t.localScale = new Vector3(Mathf.Max(0.001f, w), Mathf.Max(0.001f, w), Mathf.Max(0.001f, len));
        }
    }

    // ====================================================================== the fused ball

    /// <summary>
    /// The purple ball: a deep violet core with an ink outline that wobbles and jitters, unstable, while black
    /// lightning crackles off it, reshaped every few hundredths of a second (The Last Chromatist's witch blast).
    /// It swells in front of the caster for 2 s, goes off in a burst as it's thrown, flies straight and bursts at
    /// the end of its 40 m.
    /// </summary>
    public class FuseView : MonoBehaviour
    {
        const int Bolts = 7, BoltPoints = 7;
        const float Flicker = 0.04f;

        WorldNet.FuseFx _fx;
        Transform _core, _inner;
        readonly List<LineRenderer> _bolts = new();
        readonly List<float> _angles = new();
        float _nextShape, _owed;
        bool _launched, _done;

        public static FuseView Create(WorldNet.FuseFx fx)
        {
            var go = new GameObject("FusedBall") { layer = Layers.Fx };
            var v = go.AddComponent<FuseView>();
            v._fx = fx;
            v._core = Glow.Piece(go.transform, Glow.SphereMesh, SpellPlans.FusePurple, "Core");
            // a hotter heart inside, seen flickering through
            v._inner = Glow.Piece(go.transform, Glow.SphereMesh, new Color(4.5f, 2.6f, 5.5f, 1f), "Heart", false);
            for (int i = 0; i < Bolts; i++)
            {
                var b = new GameObject("Bolt") { layer = Layers.Fx }.AddComponent<LineRenderer>();
                b.transform.SetParent(go.transform, false);
                b.useWorldSpace = false;
                b.positionCount = BoltPoints;
                b.numCapVertices = 0;
                b.numCornerVertices = 0;
                b.alignment = LineAlignment.View;
                b.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                b.receiveShadows = false;
                b.sharedMaterial = Glow.DoubleSided;
                Glow.Tint(b, Color.black); // black lightning, as the witch's blast
                v._bolts.Add(b);
                v._angles.Add(360f * i / Bolts);
            }
            go.transform.position = WorldNet.FusePosition(fx, WorldNet.Now);
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            float dt = Time.deltaTime;
            transform.position = WorldNet.FusePosition(_fx, now);
            float r = SpellPlans.FuseRadiusAt(t);

            if (_fx.Launched && !_launched)
            {
                _launched = true;
                if (!Debris.Suppressed)
                {
                    EnergyBurst.Create(transform.position, new EnergyBurst.Settings
                    {
                        flashSize = 5f, flashTime = 0.22f, sparkCount = 18, sparkSize = 0.45f, sparkSpeed = new Vector2(6f, 13f), sparkLife = 0.45f, colour = SpellPlans.FusePurple,
                    });
                    CameraRig.ShakeFrom(transform.position, 0.6f, 0.3f);
                }
            }
            float shrink = 1f;
            if (_fx.ended >= 0)
            {
                if (!_done && !Debris.Suppressed)
                {
                    _done = true;
                    EnergyBurst.Create(transform.position, new EnergyBurst.Settings
                    {
                        flashSize = 6f, flashTime = 0.25f, sparkCount = 22, sparkSize = 0.5f, sparkSpeed = new Vector2(6f, 14f), sparkLife = 0.5f, colour = SpellPlans.FusePurple,
                    });
                    CameraRig.ShakeFrom(transform.position, 0.5f, 0.3f);
                }
                float k = (float)(now - _fx.ended) / 0.2f;
                if (k >= 1f) { Destroy(gameObject); return; }
                shrink = 1f - k;
            }

            // volatile: it never sits still -- jittering and squashing out of step, but only ever inwards, so its
            // outside edge (ink outline included) never goes past the radius it erases
            float jit = 0.05f + (_fx.Launched ? 0.03f : 0.06f * Mathf.Clamp01(t / SpellPlans.FuseCharge));
            Vector3 wob = new(1f - jit * (0.5f + 0.5f * Mathf.Sin(Time.time * 37f)), 1f - jit * (0.5f + 0.5f * Mathf.Sin(Time.time * 29f + 1.3f)),
                1f - jit * (0.5f + 0.5f * Mathf.Sin(Time.time * 43f + 2.1f)));
            _core.localScale = wob * (2f * r * shrink / ForceView.OutlineGrow);
            _core.localRotation = Random.rotation;
            _inner.localScale = Vector3.one * (2f * r * shrink * (0.55f + 0.15f * Mathf.Sin(Time.time * 60f)));

            // the lightning: drawn again in a new jagged shape every few hundredths of a second
            foreach (var b in _bolts) b.widthMultiplier = Mathf.Max(0.02f, r * 0.07f) * shrink;
            if (Time.time >= _nextShape)
            {
                _nextShape = Time.time + Flicker;
                Shape(r * shrink);
            }

            // purple sparks spat off it now and then
            _owed += dt * (_fx.Launched ? 10f : 6f);
            if (_owed >= 1f && !Debris.Suppressed && shrink > 0.5f)
            {
                _owed -= 1f;
                EnergyBurst.Create(transform.position + Random.onUnitSphere * r, new EnergyBurst.Settings
                {
                    flashSize = 0f, flashTime = 0.1f, sparkCount = 3, sparkSize = 0.18f + r * 0.06f, sparkSpeed = new Vector2(3f, 7f), sparkLife = 0.25f, colour = SpellPlans.FusePurple,
                });
            }
        }

        /// <summary>Each bolt starts on the ball and crackles out from it, wandering round its own side.</summary>
        void Shape(float r)
        {
            for (int b = 0; b < _bolts.Count; b++)
            {
                var bolt = _bolts[b];
                _angles[b] += Random.Range(-50f, 50f);
                Vector3 axis = Quaternion.Euler(Random.Range(-70f, 70f), _angles[b], 0f) * Vector3.forward;
                Vector3 side = Vector3.Cross(axis, Random.onUnitSphere).normalized;
                for (int i = 0; i < BoltPoints; i++)
                {
                    float u = i / (BoltPoints - 1f);
                    float dist = r * Mathf.Lerp(0.75f, Random.Range(1.05f, 1.3f), u); // crackling right over its surface, not out past it
                    Vector3 jag = side * Random.Range(-0.2f, 0.2f) * r * u + Random.insideUnitSphere * 0.06f * r * u;
                    bolt.SetPosition(i, Quaternion.AngleAxis(u * Random.Range(-40f, 40f), side) * axis * dist + jag);
                }
            }
        }
    }
}
