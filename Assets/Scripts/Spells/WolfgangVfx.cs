using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    // ====================================================================== how the pistols are held

    /// <summary>
    /// Wolfgang's two pistols, shared by the arms (ProceduralAnimator), the pistols (WolfgangView) and the server (where
    /// the bullets leave), so all three agree. Both arms out at the aim, the body locked facing where it was cast; the
    /// pistols come up from the hips over <see cref="SpellPlans.WolfDraw"/>.
    /// </summary>
    public static class WolfMotion
    {
        static readonly Vector3 Fingers = new Vector3(0f, -0.55f, 0.85f).normalized;
        const float Palm = 0.08f;

        static float Ease(float u) { u = 1f - Mathf.Clamp01(u); return 1f - u * u * u; }

        /// <summary>Where pistol <paramref name="side"/> (1 right, -1 left) is held, <paramref name="t"/> seconds in (the body at its usual size).</summary>
        public static void Hold(Combatant c, float t, int side, out Vector3 trigger, out Quaternion rot)
        {
            var aim = Quaternion.Euler(c.ViewPitch, c.ViewYaw, 0f);
            var body = Quaternion.Euler(0f, c.ViewYaw, 0f);
            Vector3 hips = c.transform.position;
            float eye = SpellPlans.GunEye - ((c.ViewFlags & NetState.Crouch) != 0 ? PlayerMotor.CrouchDrop : 0f);
            Vector3 shoulder = hips + Vector3.up * (eye - 0.2f) + body * new Vector3(side * 0.17f, 0f, 0f);
            trigger = shoulder + aim * new Vector3(side * 0.05f, -0.05f, 0.5f);
            rot = aim * Quaternion.Euler(0f, 0f, -side * 12f); // canted out a little
            if (t < SpellPlans.WolfDraw)
            {
                // swung up from the hips, pointing down
                float e = Ease(t / SpellPlans.WolfDraw);
                trigger = Vector3.Lerp(hips + body * new Vector3(side * 0.26f, -0.3f, 0.08f), trigger, e);
                rot = Quaternion.Slerp(body * Quaternion.Euler(75f, 0f, 0f), rot, e);
            }
        }

        /// <summary>The wrist and the way its fingers point, holding a pistol there.</summary>
        public static void Hand(Vector3 trigger, Quaternion rot, out Vector3 wrist, out Vector3 fingers)
        {
            fingers = rot * Fingers;
            wrist = trigger - fingers * Palm;
        }

        /// <summary>Where a bullet leaves pistol <paramref name="side"/>, and the way it goes (straight along the aim).</summary>
        public static void Muzzle(Combatant c, float t, int side, out Vector3 muzzle, out Vector3 dir)
        {
            Hold(c, t, side, out var trigger, out var rot);
            muzzle = SpellPlans.GrowPoint(c, trigger + rot * GunModel.Muzzle(GunKind.Gun));
            dir = Quaternion.Euler(c.ViewPitch, c.ViewYaw, 0f) * Vector3.forward;
        }
    }

    // ====================================================================== the pistols in the hands

    /// <summary>
    /// Wolfgang's pistols in a body's hands: they fade in as they're drawn, are held out at the aim, their slides snapping
    /// back and the barrels kicking up with every shot, and fade away when it's done. Thrown off its feet meanwhile: both
    /// pistols are dropped and tumble away.
    /// </summary>
    [DefaultExecutionOrder(150)] // after the ProceduralAnimator has posed the hands this frame
    public class WolfgangView : MonoBehaviour
    {
        static readonly int FadeId = Shader.PropertyToID("_Fade");

        class Pistol
        {
            public GameObject go; public Transform slide; public Renderer[] renderers;
            public float kick; public Vector3 drift, spin;
        }

        Combatant _c;
        readonly Pistol[] _guns = new Pistol[2];
        MaterialPropertyBlock _mpb;
        float _fade;
        bool _held, _dropped;

        void Awake() => _c = GetComponent<Combatant>();

        /// <summary>A shot left pistol <paramref name="side"/> (1 right, -1 left): its slide snaps back, the barrel kicks.</summary>
        public void Fired(int side)
        {
            var g = _guns[side > 0 ? 0 : 1];
            if (g != null) g.kick = 1f;
        }

        void LateUpdate()
        {
            if (_c == null || !_c.IsSpawned) { Hide(); return; }
            var pose = _c.ViewPose;
            float t = _c.ViewPoseTime, dt = Time.deltaTime;
            bool want = _c.IsAlive && _c.Visible && !_c.HasLiveRagdoll && pose == SpellPose.WolfgangFire && t < SpellPlans.WolfDraw + SpellPlans.WolfFireTime + 0.1f;
            bool dropped = _held && !want && (_c.HasLiveRagdoll || !_c.IsAlive);
            _fade = Mathf.MoveTowards(_fade, want ? 1f : 0f, dt / (want ? SpellPlans.WolfDraw : SpellPlans.GunVanish));
            if (_fade <= 0f) { Hide(); return; }
            _mpb ??= new MaterialPropertyBlock();

            for (int i = 0; i < 2; i++)
            {
                int side = i == 0 ? 1 : -1;
                var g = _guns[i];
                if (g == null)
                {
                    var go = GunModel.Spawn(GunKind.Gun);
                    g = _guns[i] = new Pistol { go = go, slide = GunModel.Slide(go), renderers = go.GetComponentsInChildren<Renderer>(true) };
                }
                var tr = g.go.transform;
                g.kick = Mathf.MoveTowards(g.kick, 0f, dt / (SpellPlans.WolfGap * 1.6f));
                if (want)
                {
                    WolfMotion.Hold(_c, t, side, out var trigger, out var rot);
                    // the kick: the barrel flips up and the pistol jolts back
                    float k = g.kick * g.kick;
                    tr.SetPositionAndRotation(SpellPlans.GrowPoint(_c, trigger - rot * Vector3.forward * (0.05f * k)), rot * Quaternion.Euler(-16f * k, 0f, 0f));
                    tr.localScale = Vector3.one * SpellPlans.BodyGrow(_c);
                    if (g.slide != null) g.slide.localPosition = Vector3.back * (SpellPlans.WolfSlide * Mathf.Sin(g.kick * Mathf.PI * 0.5f));
                }
                else
                {
                    if (dropped && !_dropped)
                    {
                        // knocked down: both pistols fly out of the hands
                        g.drift = tr.forward * -2f + Vector3.up * 3.5f + Random.insideUnitSphere * 1.5f;
                        g.spin = new Vector3(Random.Range(-600f, 600f), Random.Range(-200f, 200f), Random.Range(-300f, 300f));
                    }
                    // put away: they fade where they are; dropped: they tumble off
                    if (dropped || _dropped)
                    {
                        g.drift += Vector3.down * 12f * dt;
                        tr.position += g.drift * dt;
                        tr.rotation *= Quaternion.Euler(g.spin * dt);
                    }
                }
                foreach (var r in g.renderers)
                {
                    if (r == null) continue;
                    r.GetPropertyBlock(_mpb);
                    _mpb.SetFloat(FadeId, _fade);
                    r.SetPropertyBlock(_mpb);
                }
            }
            if (dropped) _dropped = true;
            _held = want;
        }

        void Hide()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_guns[i] != null && _guns[i].go != null) Destroy(_guns[i].go);
                _guns[i] = null;
            }
            _fade = 0f;
            _held = _dropped = false;
        }

        void OnDestroy() => Hide();
    }

    // ====================================================================== the bullets

    /// <summary>One of Wolfgang's bullets: a hot inked slug with a short tapering trail.</summary>
    public class WolfBulletView : MonoBehaviour
    {
        WorldNet.WolfBulletFx _fx;
        Transform _slug;
        TrailRenderer _trail;

        public static WolfBulletView Create(WorldNet.WolfBulletFx fx)
        {
            var v = new GameObject("Bullet") { layer = Layers.Fx }.AddComponent<WolfBulletView>();
            v._fx = fx;
            v._slug = Glow.Piece(v.transform, Glow.PrismMesh, SpellPlans.WolfHot, "Slug");
            // the prism's half width is a quarter of its scale, and the ink adds 0.14 more: exactly the bullet's radius
            float w = SpellPlans.WolfRadius / 0.39f;
            v._slug.localScale = new Vector3(w, w, 0.35f);
            v._trail = Glow.Trail(v._slug, SpellPlans.WolfHot, 0.06f, SpellPlans.WolfRadius * 1.4f);
            v.LateUpdate();
            return v;
        }

        void LateUpdate()
        {
            if (_slug == null) return;
            double now = WorldNet.Now;
            _slug.SetPositionAndRotation(WorldNet.WolfBulletPosition(_fx, now), Quaternion.LookRotation(_fx.dir));
            if (_fx.Alive && now - _fx.start < 5.0) return;
            Glow.DropTrail(_trail);
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// A bullet hole where one of Wolfgang's bullets struck (as in Gunbound): a small dark disc, inked, flat on whatever
    /// is there once the ground it broke has gone. It stays a while, then shrinks away.
    /// </summary>
    public class BulletHole : MonoBehaviour
    {
        const int Max = 80;
        static readonly Queue<BulletHole> Holes = new();
        static Mesh _disc;
        static readonly Color Dark = new(0.06f, 0.06f, 0.08f, 1f);

        Vector3 _point, _dir;
        float _age, _size;
        bool _placed;

        /// <summary>A hole where a bullet travelling along <paramref name="dir"/> struck at <paramref name="point"/> (placed a moment later, on what's left).</summary>
        public static void Spawn(Vector3 point, Vector3 dir)
        {
            if (Debris.Suppressed) return;
            while (Holes.Count >= Max)
            {
                var old = Holes.Dequeue();
                if (old != null) Destroy(old.gameObject);
            }
            var h = new GameObject("BulletHole") { layer = Layers.Fx }.AddComponent<BulletHole>();
            h._point = point;
            h._dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
            h._size = Random.Range(0.13f, 0.17f);
            Holes.Enqueue(h);
        }

        /// <summary>A new round, a practice chamber reset: all gone.</summary>
        public static void ClearAll()
        {
            while (Holes.Count > 0)
            {
                var h = Holes.Dequeue();
                if (h != null) Destroy(h.gameObject);
            }
        }

        /// <summary>A practice chamber is being reset: its holes go (the ground is fresh).</summary>
        public static void ClearIn(float minX, float maxX)
        {
            int n = Holes.Count;
            for (int i = 0; i < n; i++)
            {
                var h = Holes.Dequeue();
                if (h == null) continue;
                float x = h._point.x;
                if (x >= minX && x < maxX) Destroy(h.gameObject);
                else Holes.Enqueue(h);
            }
        }

        static Mesh Disc => _disc ??= Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");

        void Update()
        {
            _age += Time.deltaTime;
            if (!_placed)
            {
                // the ground it broke is gone by now: the hole goes on whatever the bullet would have struck next
                if (_age < 0.06f) return;
                _placed = true;
                if (!Physics.Raycast(_point - _dir * 0.3f, _dir, out var hit, SpellPlans.WolfBreak * 2f + 0.6f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                {
                    Destroy(gameObject);
                    return;
                }
                transform.SetPositionAndRotation(hit.point + hit.normal * 0.01f, Quaternion.FromToRotation(Vector3.up, hit.normal) * Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
                var disc = Glow.Piece(transform, Disc, Dark, "Hole");
                disc.localScale = new Vector3(1f, 0.01f, 1f);
            }
            float shrink = 1f - Mathf.Clamp01((_age - SpellPlans.WolfHoleLife) / 0.3f);
            if (shrink <= 0f) { Destroy(gameObject); return; }
            float pop = Mathf.Min(1f, _age / 0.08f);
            transform.localScale = Vector3.one * (_size * pop * shrink);
        }
    }
}
