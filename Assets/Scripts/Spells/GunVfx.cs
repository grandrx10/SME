using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    public enum GunKind : byte { Rachmaninoff, Sebastian, Gun } // (Gun: Wolfgang's pistols, Resources/VFX/Gun)

    // ====================================================================== the guns

    /// <summary>
    /// The guns (Resources/VFX/Rachmaninoff and Sebastian, the user's own models from Gunbound) rebuilt once into clean
    /// templates, the same way as the sword and the hammer: meshes copied out, no colliders, every part on SME/ToonFade in
    /// its own material's colours. A template's origin is the prefab's "TriggerPoint" (the right hand), the barrel points
    /// along +Z. "BarrelPoint" is the muzzle and "OffhandHold" where the left hand goes.
    /// </summary>
    public static class GunModel
    {
        static readonly GameObject[] Templates = new GameObject[3];
        static readonly bool[] Measured = new bool[3];
        static readonly Vector3[] Muzzles = { new(0f, 0.075f, 0.6f), new(0f, 0.075f, 0.6f), new(0f, 0.075f, 0.32f) };
        static readonly Vector3[] Supports = { new(0f, 0.034f, 0.307f), new(0f, 0.034f, 0.307f), new(0f, 0f, 0.1f) };

        /// <summary>The prefabs' barrels point along +X.</summary>
        static readonly Quaternion Turn = Quaternion.Euler(0f, -90f, 0f);

        /// <summary>The end of the barrel, from the trigger (z along the barrel, y up).</summary>
        public static Vector3 Muzzle(GunKind k) { Measure(k); return Muzzles[(int)k]; }
        /// <summary>Where the left hand holds it, from the trigger.</summary>
        public static Vector3 Support(GunKind k) { Measure(k); return Supports[(int)k]; }

        static GameObject Prefab(GunKind k) => Resources.Load<GameObject>("VFX/" + k);

        static Transform Deep(Transform t, string name)
        {
            foreach (var c in t.GetComponentsInChildren<Transform>(true)) if (c.name == name) return c;
            return null;
        }

        static Vector3 InGun(Transform root, Vector3 trigger, Transform marker) =>
            Turn * ((root.InverseTransformPoint(marker.position) - trigger) * SpellPlans.GunScale);

        /// <summary>Where the hands and muzzle are, from the prefab (no instantiating).</summary>
        static void Measure(GunKind k)
        {
            int i = (int)k;
            if (Measured[i]) return;
            Measured[i] = true;
            var prefab = Prefab(k);
            if (prefab == null) return;
            var root = prefab.transform;
            var trig = Deep(root, "TriggerPoint");
            if (trig == null) return;
            var muzzle = Deep(root, "BarrelPoint");
            var support = Deep(root, "OffhandHold");
            Vector3 trigger = root.InverseTransformPoint(trig.position);
            if (muzzle != null) Muzzles[i] = InGun(root, trigger, muzzle);
            if (support != null) Supports[i] = InGun(root, trigger, support);
        }

        /// <summary>Is this part of the prefab on its slide?</summary>
        static bool UnderSlide(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent) if (t.name == "Slide") return true;
            return false;
        }

        /// <summary>A spawned gun's slide (it slides back along -Z as it fires), if it has one.</summary>
        public static Transform Slide(GameObject gun) => gun != null ? gun.transform.Find("Slide") : null;

        public static GameObject Spawn(GunKind k)
        {
            var go = Object.Instantiate(Template(k));
            go.name = k.ToString();
            go.SetActive(true);
            return go;
        }

        static GameObject Template(GunKind k)
        {
            int i = (int)k;
            if (Templates[i] != null) return Templates[i];
            var t = new GameObject(k + " (template)") { layer = Layers.Fx, hideFlags = HideFlags.HideAndDontSave };
            t.SetActive(false);
            Object.DontDestroyOnLoad(t);
            Templates[i] = t;

            var prefab = Prefab(k);
            if (prefab == null)
            {
                Debug.LogWarning("[SME] Resources/VFX/" + k + " is missing: the card uses a plain stand-in gun.");
                SwordModel.Part(t.transform, Glow.CubeMesh, null, Matrix4x4.TRS(new Vector3(0f, 0.06f, 0.25f), Quaternion.identity, new Vector3(0.06f, 0.08f, 0.7f)));
                SwordModel.Part(t.transform, Glow.CubeMesh, null, Matrix4x4.TRS(new Vector3(0f, 0f, -0.2f), Quaternion.identity, new Vector3(0.05f, 0.12f, 0.3f)));
                return t;
            }

            // instantiated (active) so ProBuilder builds the meshes, copied out, then thrown away
            var src = Object.Instantiate(prefab);
            var root = src.transform;
            var trig = Deep(root, "TriggerPoint");
            Vector3 trigger = trig != null ? root.InverseTransformPoint(trig.position) : Vector3.zero;
            Matrix4x4 toGun = Matrix4x4.TRS(Vector3.zero, Turn, Vector3.one * SpellPlans.GunScale) * Matrix4x4.Translate(-trigger) * root.worldToLocalMatrix;
            // a pistol's slide (the parts under "Slide") goes on a child of its own, so it can be worked as it fires
            var slide = new GameObject("Slide") { layer = Layers.Fx }.transform;
            slide.SetParent(t.transform, false);
            foreach (var mf in src.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                SwordModel.Part(UnderSlide(mf.transform, root) ? slide : t.transform, Object.Instantiate(mf.sharedMesh), mr != null ? mr.sharedMaterials : null, toGun * mf.transform.localToWorldMatrix);
            }
            Object.Destroy(src);
            return t;
        }
    }

    // ====================================================================== how they're held

    /// <summary>
    /// The gun gestures, shared by the arms (ProceduralAnimator puts both hands on it) and the gun (GunView), so hands
    /// and gun always agree. Unlike the other gestures they follow the aim up and down.
    ///  - ShotgunAim: raised from low to the shoulder, fired (<see cref="SpellPlans.RachDelay"/>), kicking back.
    ///  - RifleKneel: raised to the eye, down on one knee.
    ///  - RifleFire: trembles as it charges up (<see cref="SpellPlans.SebWindup"/>), kicks, then comes up with the body.
    /// </summary>
    public static class GunMotion
    {
        const float Raise = 0.22f;
        /// <summary>The way the right and left fingers point, in the gun's own frame; the wrist is this far behind the palm.</summary>
        static readonly Vector3 FingersR = new Vector3(0f, -0.55f, 0.85f).normalized, FingersL = new Vector3(0.5f, -0.45f, 0.75f).normalized;
        const float Palm = 0.08f;

        public static bool Is(SpellPose p) => p == SpellPose.ShotgunAim || p == SpellPose.RifleKneel || p == SpellPose.RifleFire;
        public static GunKind Kind(SpellPose p) => p == SpellPose.ShotgunAim ? GunKind.Rachmaninoff : GunKind.Sebastian;

        static float Ease(float u) { u = 1f - Mathf.Clamp01(u); return 1f - u * u * u; }

        /// <summary>Where the trigger is and the way the gun points, on the body at its usual size.</summary>
        public static void Hold(Combatant c, SpellPose pose, float t, out Vector3 trigger, out Quaternion rot)
        {
            var aim = Quaternion.Euler(c.ViewPitch, c.ViewYaw, 0f);
            Vector3 hips = c.transform.position;
            float kick = 0f;
            if (pose == SpellPose.ShotgunAim)
            {
                float eye = SpellPlans.GunEye - ((c.ViewFlags & NetState.Crouch) != 0 ? PlayerMotor.CrouchDrop : 0f);
                trigger = hips + Vector3.up * eye + aim * new Vector3(0.11f, -0.16f, 0.36f);
                if (t > SpellPlans.RachDelay) kick = 1f - Ease((t - SpellPlans.RachDelay) / 0.3f);
            }
            else if (pose == SpellPose.RifleFire)
            {
                float after = t - SpellPlans.SebWindup;
                trigger = SpellPlans.SebastianGrip(hips, aim, Mathf.SmoothStep(0f, 1f, after / SpellPlans.SebRecover));
                if (after >= 0f) kick = 1f - Ease(after / SpellPlans.SebRecover);
                else trigger += aim * new Vector3(Mathf.Sin(Time.time * 73f), Mathf.Sin(Time.time * 91f), 0f) * (0.008f * t / SpellPlans.SebWindup);
            }
            else trigger = SpellPlans.SebastianGrip(hips, aim, 1f - Mathf.SmoothStep(0f, 1f, t / Raise));
            rot = aim;
            if (pose != SpellPose.RifleFire && t < Raise)
            {
                // swung up from low across the body
                float e = Ease(t / Raise);
                trigger = Vector3.Lerp(trigger + aim * new Vector3(0.02f, -0.28f, -0.12f), trigger, e);
                rot = Quaternion.Slerp(aim * Quaternion.Euler(55f, -15f, 10f), aim, e);
            }
            if (kick > 0f)
            {
                trigger -= aim * Vector3.forward * (0.14f * kick);
                rot = aim * Quaternion.Euler(-22f * kick, 0f, 0f);
            }
        }

        /// <summary>The two wrists, and the way their fingers point, holding it there.</summary>
        public static void Hands(GunKind kind, Vector3 trigger, Quaternion rot, out Vector3 r, out Vector3 rd, out Vector3 l, out Vector3 ld)
        {
            rd = rot * FingersR;
            ld = rot * FingersL;
            r = trigger - rd * Palm;
            l = trigger + rot * GunModel.Support(kind) - ld * Palm;
        }
    }

    /// <summary>
    /// The gun in a body's two hands while it plays a gun gesture: it manifests (dithers in), is held and aimed, and dithers
    /// away when the gesture's done (the shotgun tumbling off, as its owner is thrown). Sebastian's charge shows its caster
    /// (nobody else) the cylinder the shot will be, white once charged; everyone sees the runes spilling from the muzzle
    /// then, and the energy rushing into it before it fires.
    /// </summary>
    [DefaultExecutionOrder(150)] // after the ProceduralAnimator has posed the hands this frame
    public class GunView : MonoBehaviour
    {
        static readonly int FadeId = Shader.PropertyToID("_Fade");
        static readonly Color White = new(3.2f, 3.2f, 3.4f, 1f);

        Combatant _c;
        GameObject _gun;
        GunKind _kind;
        Renderer[] _renderers;
        MaterialPropertyBlock _mpb;
        float _fade;
        Vector3 _drift, _spin;
        bool _held, _charged;
        GunAimTube _tube;
        RuneSpew _runes;
        ArcaneCharge _windup;

        void Awake() => _c = GetComponent<Combatant>();

        /// <summary>The end of the barrel right now (false: no gun out).</summary>
        public bool TryMuzzle(out Vector3 point)
        {
            point = default;
            if (_gun == null) return false;
            point = _gun.transform.TransformPoint(GunModel.Muzzle(_kind));
            return true;
        }

        void LateUpdate()
        {
            if (_c == null || !_c.IsSpawned) { Hide(); return; }
            var pose = _c.ViewPose;
            float t = _c.ViewPoseTime, dt = Time.deltaTime;
            bool want = _c.IsAlive && _c.Visible && !_c.HasLiveRagdoll && Wanted(pose, t);
            bool winding = want && pose == SpellPose.RifleFire && t < SpellPlans.SebWindup;
            Charge(winding || want && pose == SpellPose.RifleKneel, t, winding);
            Windup(winding, want);
            if (want && _gun != null && _kind != GunMotion.Kind(pose)) Drop();
            _fade = Mathf.MoveTowards(_fade, want ? 1f : 0f, dt / (want ? SpellPlans.GunAppear : SpellPlans.GunVanish));
            if (_fade <= 0f) { Hide(); return; }

            if (_gun == null)
            {
                _kind = GunMotion.Kind(pose);
                _gun = GunModel.Spawn(_kind);
                _renderers = _gun.GetComponentsInChildren<Renderer>(true);
                _mpb ??= new MaterialPropertyBlock();
            }
            var g = _gun.transform;
            if (want)
            {
                GunMotion.Hold(_c, pose, t, out var trigger, out var rot);
                g.SetPositionAndRotation(SpellPlans.GrowPoint(_c, trigger), rot);
                g.localScale = Vector3.one * SpellPlans.BodyGrow(_c);
                _held = true;
            }
            else
            {
                if (_held)
                {
                    // let go: the shotgun is kicked out of the hands, the rifle just goes
                    _held = false;
                    bool kicked = _kind == GunKind.Rachmaninoff;
                    _drift = kicked ? g.forward * -3.5f + Vector3.up * 3f : Vector3.zero;
                    _spin = kicked ? new Vector3(-420f, 60f, 0f) : Vector3.zero;
                }
                g.position += _drift * dt;
                g.rotation *= Quaternion.Euler(_spin * dt);
            }
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetFloat(FadeId, _fade);
                r.SetPropertyBlock(_mpb);
            }
        }

        static bool Wanted(SpellPose pose, float t) => pose switch
        {
            SpellPose.ShotgunAim => t < SpellPlans.RachDelay + 0.1f,
            SpellPose.RifleKneel => true,
            SpellPose.RifleFire => t < SpellPlans.SebWindup + 0.08f,
            _ => false,
        };

        // ---------------- Sebastian's charge ----------------

        void Charge(bool on, float t, bool winding)
        {
            if (!on)
            {
                if (_tube != null) { _tube.Drop(); _tube = null; }
                if (_runes != null) { _runes.Stop(); _runes = null; }
                _charged = false;
                return;
            }
            SpellPlans.SebastianRay(_c, _c.ViewYaw, _c.ViewPitch, out var muzzle, out var dir);
            bool charged = winding || t >= SpellPlans.SebCharge;
            if (_c.IsOwner) // the line is the caster's alone
            {
                if (_tube == null) _tube = GunAimTube.Create();
                float reach = SpellPlans.SebastianReach(VoxelWorld.Current, muzzle, dir, SpellPlans.SebLength, 0.9f);
                _tube.Set(muzzle, dir, reach, charged ? 1f : Mathf.Clamp01(t / SpellPlans.SebCharge), charged ? White : GunAimTube.Waiting);
            }
            if (charged && !_charged)
            {
                EnergyBurst.Create(muzzle, new EnergyBurst.Settings
                {
                    flashSize = 0.5f, flashTime = 0.14f, sparkCount = 8, sparkSize = 0.18f, sparkSpeed = new Vector2(3f, 6f), sparkLife = 0.25f, colour = White,
                });
                _charged = true;
                _runes = RuneSpew.Create(MuzzlePoint, StillCharged, 120f, White, 18f, Vector3.one * 0.06f);
            }
            _charged = charged;
        }

        /// <summary>Let go: energy rushes into the muzzle until it fires (or is cut off).</summary>
        void Windup(bool on, bool want)
        {
            if (on && _windup == null && !Debris.Suppressed)
                _windup = ArcaneCharge.Create(MuzzlePoint, () => this != null && _c != null && _c.IsAlive && !_c.HasLiveRagdoll, SpellPlans.SebWindup, SpellPlans.SebOrange, false);
            if (on || _windup == null) return;
            if (want) _windup.Release(); // fired
            else _windup.Cancel();
            _windup = null;
        }

        Vector3 MuzzlePoint() => TryMuzzle(out var p) ? p : _c != null ? _c.transform.position : Vector3.zero;
        bool StillCharged() => this != null && _charged;

        void Hide()
        {
            Charge(false, 0f, false);
            Windup(false, false);
            Drop();
        }

        void Drop()
        {
            if (_gun != null) Destroy(_gun);
            _gun = null;
            _fade = 0f;
            _held = false;
        }

        void OnDestroy() => Hide();
    }

    /// <summary>
    /// The outline of the cylinder Sebastian's shot will be: inked lines along it and rings round it (one running out
    /// along it as the charge builds). Exactly the shot's radius, and only as long as it will reach.
    /// </summary>
    public class GunAimTube : MonoBehaviour
    {
        public static readonly Color Waiting = new(0.95f, 0.5f, 0.2f, 1f);
        const int Sides = 8, Steps = 28, Rings = 4;
        readonly List<(LineRenderer core, LineRenderer edge)> _long = new(), _rings = new();
        readonly List<Vector3> _pts = new();
        Vector3 _muzzle, _dir = Vector3.forward;
        float _reach, _charge, _shrink = 1f;
        bool _dropped;

        public static GunAimTube Create()
        {
            var t = new GameObject("SebastianAim") { layer = Layers.Fx }.AddComponent<GunAimTube>();
            for (int i = 0; i < Sides; i++) t._long.Add(ArcanaVfx.Line(t.transform, Waiting, 0.022f));
            for (int i = 0; i < Rings; i++) t._rings.Add(ArcanaVfx.Line(t.transform, Waiting, 0.035f));
            return t;
        }

        public void Set(Vector3 muzzle, Vector3 dir, float reach, float charge, Color colour)
        {
            _muzzle = muzzle; _dir = dir; _reach = reach; _charge = charge;
            foreach (var l in _long) Glow.Tint(l.core, colour);
            foreach (var l in _rings) Glow.Tint(l.core, colour);
            Draw(SpellPlans.SebRadius);
        }

        /// <summary>Let go, or fired: it closes up to nothing.</summary>
        public void Drop() => _dropped = true;

        void LateUpdate()
        {
            if (!_dropped) return;
            _shrink -= Time.deltaTime / 0.12f;
            if (_shrink <= 0f) { Destroy(gameObject); return; }
            Draw(SpellPlans.SebRadius * _shrink);
        }

        void Draw(float r)
        {
            var rot = Quaternion.LookRotation(_dir, Mathf.Abs(_dir.y) > 0.99f ? Vector3.forward : Vector3.up);
            Vector3 x = rot * Vector3.right, y = rot * Vector3.up;
            for (int i = 0; i < Sides; i++)
            {
                float a = i / (float)Sides * Mathf.PI * 2f;
                Vector3 o = (x * Mathf.Cos(a) + y * Mathf.Sin(a)) * r;
                _pts.Clear();
                _pts.Add(_muzzle + o);
                _pts.Add(_muzzle + o + _dir * _reach);
                ArcanaVfx.SetLine(_long[i], _pts);
            }
            for (int k = 0; k < Rings; k++)
            {
                // the two ends, the one that runs out with the charge, and one half way
                float at = k switch { 0 => 0f, 1 => 1f, 2 => _charge, _ => 0.5f };
                Vector3 c = _muzzle + _dir * (_reach * at);
                _pts.Clear();
                for (int s = 0; s <= Steps; s++)
                {
                    float a = s / (float)Steps * Mathf.PI * 2f;
                    _pts.Add(c + (x * Mathf.Cos(a) + y * Mathf.Sin(a)) * r);
                }
                ArcanaVfx.SetLine(_rings[k], _pts);
            }
        }
    }

    // ====================================================================== shots

    /// <summary>A cartoon muzzle flash: a hot burst and inked spikes shot out of the barrel, swelling and shrinking away.</summary>
    public class MuzzleFlash : MonoBehaviour
    {
        const float Life = 0.16f;
        static readonly Color Hot = new(4.6f, 2.6f, 0.6f, 1f), Tip = new(4.2f, 1.2f, 0.2f, 1f);
        readonly List<(Transform t, Vector3 dir, float len)> _spikes = new();
        float _age, _size;

        public static void Create(Vector3 point, Vector3 dir, float size)
        {
            var go = new GameObject("MuzzleFlash") { layer = Layers.Fx };
            go.transform.SetPositionAndRotation(point, Quaternion.LookRotation(dir));
            var m = go.AddComponent<MuzzleFlash>();
            m._size = size;
            for (int i = 0; i < 9; i++)
            {
                // one long one straight ahead, the rest fanned out round it
                Vector3 d = i == 0 ? Vector3.forward : Quaternion.Euler(0f, 0f, i * 45f + Random.Range(-12f, 12f)) * Quaternion.Euler(Random.Range(28f, 62f), 0f, 0f) * Vector3.forward;
                m._spikes.Add((Glow.Piece(go.transform, Glow.PrismMesh, i % 2 == 0 ? Hot : Tip, "Spike"), d, i == 0 ? 1.5f : Random.Range(0.6f, 1f)));
            }
            EnergyBurst.Create(point, new EnergyBurst.Settings
            {
                flashSize = 0.5f * size, flashTime = 0.12f, sparkCount = 10, sparkSize = 0.2f * size, sparkSpeed = new Vector2(4f, 10f), sparkLife = 0.25f, colour = Hot,
            });
            m.LateUpdate();
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            float u = _age / Life;
            if (u >= 1f) { Destroy(gameObject); return; }
            float s = Mathf.Sin(Mathf.Clamp01(u * 0.85f + 0.15f) * Mathf.PI);
            foreach (var (t, dir, len) in _spikes)
            {
                float l = len * s * _size;
                t.localRotation = Quaternion.LookRotation(dir);
                t.localPosition = dir * (l * 0.5f + 0.1f * u);
                float w = Mathf.Max(0.001f, 0.34f * s * _size * (1f - 0.5f * u));
                t.localScale = new Vector3(w, w, Mathf.Max(0.001f, l));
            }
        }
    }

    /// <summary>Rachmaninoff's pellets: hot inked slugs with short tapering trails.</summary>
    public class PelletsView : MonoBehaviour
    {
        static readonly Color Hot = new(4.4f, 2.5f, 0.7f, 1f);
        WorldNet.PelletsFx _fx;
        readonly List<(Transform t, TrailRenderer trail)> _shots = new();

        public static PelletsView Create(WorldNet.PelletsFx fx)
        {
            var v = new GameObject("Pellets") { layer = Layers.Fx }.AddComponent<PelletsView>();
            v._fx = fx;
            foreach (var b in fx.bits)
            {
                var t = Glow.Piece(v.transform, Glow.PrismMesh, Hot, "Pellet");
                // the prism's half width is a quarter of its scale, and the ink adds 0.14 more: exactly the pellet's radius
                float w = SpellPlans.PelletRadius / 0.39f;
                t.localScale = new Vector3(w, w, 0.45f);
                t.SetPositionAndRotation(b.origin, Quaternion.LookRotation(b.dir));
                v._shots.Add((t, Glow.Trail(t, Hot, 0.09f, SpellPlans.PelletRadius)));
            }
            return v;
        }

        public static void Spark(Vector3 p) => EnergyBurst.Create(p, new EnergyBurst.Settings
        {
            flashSize = 0.2f, flashTime = 0.08f, sparkCount = 3, sparkSize = 0.12f, sparkSpeed = new Vector2(2f, 5f), sparkLife = 0.18f, colour = Hot,
        });

        void LateUpdate()
        {
            double now = WorldNet.Now;
            bool any = false;
            for (int k = 0; k < _shots.Count; k++)
            {
                var (t, trail) = _shots[k];
                if (t == null) continue;
                var b = _fx.bits[k];
                t.SetPositionAndRotation(WorldNet.PelletPosition(b, now), Quaternion.LookRotation(b.dir));
                if (b.Alive && now - _fx.start < 20.0) { any = true; continue; }
                Glow.DropTrail(trail);
                Destroy(t.gameObject);
                _shots[k] = (null, null);
            }
            if (!any) Destroy(gameObject);
        }
    }

    /// <summary>
    /// Sebastian's shot: an inked, round-ended bar of bright orange, exactly as wide as what it hits, white-hot for its
    /// first instant, rings bursting off it along the way. It thins to nothing.
    /// </summary>
    public class SebastianBeam : MonoBehaviour
    {
        class Leg { public Vector3 a, dir; public float len; public Transform body, ink, capA, capB, inkA, inkB; public readonly List<Renderer> lit = new(); }
        class Ring { public float at; public readonly List<Transform> bits = new(); }
        readonly List<Leg> _legs = new();
        readonly List<Ring> _rings = new();
        float _age;
        const float InkRim = 0.07f;
        const int RingBits = 14;

        public static void Create(List<Vector3> points)
        {
            var b = new GameObject("SebastianShot") { layer = Layers.Fx }.AddComponent<SebastianBeam>();
            for (int i = 0; i + 1 < points.Count; i++)
            {
                Vector3 d = points[i + 1] - points[i];
                float len = d.magnitude;
                if (len < 0.02f) continue;
                d /= len;
                var leg = new Leg { a = points[i], dir = d, len = len };
                leg.body = JudgmentView.Tube(b.transform, "Shot", SpellPlans.SebOrange, false);
                leg.ink = JudgmentView.Tube(b.transform, "ShotOutline", Glow.Ink, true);
                leg.capA = Ball(b.transform, SpellPlans.SebOrange, false);
                leg.capB = Ball(b.transform, SpellPlans.SebOrange, false);
                leg.inkA = Ball(b.transform, Glow.Ink, true);
                leg.inkB = Ball(b.transform, Glow.Ink, true);
                foreach (var t in new[] { leg.body, leg.capA, leg.capB }) leg.lit.Add(t.GetComponent<Renderer>());
                b._legs.Add(leg);
                for (float at = 1.5f; at < len - 0.5f; at += 4f)
                {
                    var ring = new Ring { at = at };
                    var root = new GameObject("Ring") { layer = Layers.Fx }.transform;
                    root.SetParent(b.transform, false);
                    root.SetPositionAndRotation(leg.a + d * at, Quaternion.LookRotation(d));
                    for (int k = 0; k < RingBits; k++) ring.bits.Add(Glow.Piece(root, Glow.CubeMesh, SpellPlans.SebGold, "Bit"));
                    b._rings.Add(ring);
                }
            }
            if (points.Count > 1)
            {
                Vector3 d = (points[1] - points[0]).normalized;
                if (d.sqrMagnitude > 0.5f) MuzzleFlash.Create(points[0], d, 1.7f);
                CameraRig.ShakeFrom(points[0], 1.1f, 0.5f);
                EnergyBurst.Create(points[points.Count - 1], new EnergyBurst.Settings
                {
                    flashSize = 0.8f, flashTime = 0.18f, sparkCount = 14, sparkSize = 0.3f, sparkSpeed = new Vector2(4f, 10f), sparkLife = 0.35f, colour = SpellPlans.SebOrange,
                });
                CameraRig.ShakeFrom(points[points.Count - 1], 0.6f, 0.35f);
            }
            b.LateUpdate();
        }

        /// <summary>A round end, without its own outline (the ink one is drawn inside out, a hair bigger, as the tube's is).</summary>
        static Transform Ball(Transform parent, Color colour, bool ink)
        {
            var t = Glow.Piece(parent, Glow.SphereMesh, colour, "End", false);
            if (ink) t.GetComponent<MeshRenderer>().sharedMaterial = JudgmentView.InkMaterial;
            return t;
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            float u = _age / SpellPlans.SebBeamTime;
            if (u >= 1f) { Destroy(gameObject); return; }
            // full size for a moment, then thinning away
            float k = 1f - Mathf.Clamp01((u - 0.2f) / 0.8f);
            float r = SpellPlans.SebRadius * k * k * (3f - 2f * k), rim = r + InkRim * k;
            Color colour = Color.Lerp(SpellPlans.SebCore, SpellPlans.SebOrange, Mathf.Clamp01(u / 0.25f));
            foreach (var g in _legs)
            {
                foreach (var l in g.lit) Glow.Tint(l, colour);
                var rot = Quaternion.FromToRotation(Vector3.up, g.dir);
                // the same round-ended shape the server hits with: a ball round the muzzle, and one ending at the far end
                float len = Mathf.Max(0f, g.len - SpellPlans.SebRadius);
                Vector3 end = g.a + g.dir * len;
                Put(g.body, g.a, rot, r, len);
                Put(g.ink, g.a, rot, rim, len);
                g.capA.position = g.inkA.position = g.a;
                g.capB.position = g.inkB.position = end;
                g.capA.localScale = g.capB.localScale = Glow.SphereScale(Mathf.Max(0.001f, r));
                g.inkA.localScale = g.inkB.localScale = Glow.SphereScale(Mathf.Max(0.001f, rim));
            }
            foreach (var ring in _rings)
            {
                float t = Mathf.Clamp01((_age - ring.at * 0.004f) / (SpellPlans.SebBeamTime * 0.8f));
                float go = 1f - (1f - t) * (1f - t) * (1f - t);
                float R = SpellPlans.SebRadius * Mathf.Lerp(0.9f, 1.8f, go), w = t > 0f ? 0.09f * (1f - t) : 0f;
                float seg = 2f * Mathf.PI * R / RingBits * (1f - t * 0.3f);
                for (int i = 0; i < ring.bits.Count; i++)
                {
                    float a = (i + 0.5f) / RingBits * Mathf.PI * 2f;
                    Vector3 d = new(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    ring.bits[i].localPosition = d * R;
                    ring.bits[i].localRotation = Quaternion.LookRotation(Vector3.Cross(Vector3.forward, d), d);
                    ring.bits[i].localScale = w > 0f ? new Vector3(w, w, seg) : Vector3.zero;
                }
            }
        }

        static void Put(Transform t, Vector3 a, Quaternion rot, float r, float len)
        {
            t.SetPositionAndRotation(a, rot);
            t.localScale = new Vector3(Mathf.Max(0.001f, r), Mathf.Max(0.001f, len), Mathf.Max(0.001f, r));
        }
    }
}
