using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>How flame bombs look: an inked red slab with a hot orange heart, and the blast. Local-only visuals.</summary>
    public static class BombVfx
    {
        public static readonly Color Red = new(1.5f, 0.1f, 0.05f, 1f), Heart = new(3.2f, 0.9f, 0.12f, 1f), Flash = new(6f, 0.35f, 0.18f, 1f);

        /// <summary>The slab (lying flat, its up along +Y), ink included exactly its real size.</summary>
        public static (Renderer slab, Renderer heart) Build(Transform parent)
        {
            float s = SpellPlans.BombSize / ForceView.OutlineGrow, t = SpellPlans.BombThick / ForceView.OutlineGrow;
            var slab = Glow.Piece(parent, Glow.CubeMesh, Red, "Slab");
            slab.localScale = new Vector3(s, t, s);
            var heart = Glow.Piece(parent, Glow.CubeMesh, Heart, "Heart");
            heart.localPosition = Vector3.up * (t * 0.5f);
            heart.localRotation = Quaternion.Euler(0f, 45f, 0f);
            heart.localScale = new Vector3(s * 0.32f, t * 0.5f, s * 0.32f);
            return (slab.GetComponent<Renderer>(), heart.GetComponent<Renderer>());
        }

        /// <summary>It goes off: a blast out to exactly <see cref="SpellPlans.BombBlast"/>, and embers out to <see cref="SpellPlans.BombScorch"/>.</summary>
        public static void Blast(Vector3 p)
        {
            RepulsionBlast.Create(p, SpellPlans.BombBlast);
            Embers.Create(p);
        }

        /// <summary>Flame tongues flung out past the blast, over everything it scorches, shrinking away as they reach its edge.</summary>
        class Embers : MonoBehaviour
        {
            const int Count = 26;
            const float Life = 0.5f;
            readonly List<(Transform t, Vector3 dir, float len)> _bits = new();
            float _age;

            public static void Create(Vector3 p)
            {
                var go = new GameObject("BombEmbers") { layer = Layers.Fx };
                go.transform.position = p;
                var e = go.AddComponent<Embers>();
                for (int i = 0; i < Count; i++)
                {
                    float a = (i + Random.value * 0.6f) / Count * Mathf.PI * 2f;
                    var d = new Vector3(Mathf.Cos(a), Random.Range(0f, 0.9f) * (i % 2), Mathf.Sin(a)).normalized;
                    e._bits.Add((FireVfx.Tongue(go.transform, FireVfx.Random3()), d, Random.Range(0.45f, 0.75f)));
                }
                e.LateUpdate();
            }

            void LateUpdate()
            {
                _age += Time.deltaTime;
                float u = _age / Life;
                if (u >= 1f) { Destroy(gameObject); return; }
                float go = 1f - Mathf.Pow(1f - Mathf.Clamp01(u / 0.7f), 3f), shrink = 1f - u * u;
                foreach (var (t, dir, len) in _bits)
                {
                    float l = len * shrink;
                    t.localPosition = dir * Mathf.Lerp(SpellPlans.BombBlast * 0.5f, SpellPlans.BombScorch - l, go);
                    t.rotation = Quaternion.LookRotation(dir);
                    t.localScale = new Vector3(Mathf.Max(0.001f, 0.3f * shrink), Mathf.Max(0.001f, 0.3f * shrink), Mathf.Max(0.001f, l));
                }
            }
        }
    }

    /// <summary>One flame bomb: pops in, trails fire while it flies, its heart beating; set off, it flashes red and swells until it goes.</summary>
    [DefaultExecutionOrder(200)] // (after the bodies it sits on have been posed)
    public class BombView : MonoBehaviour
    {
        WorldNet.BombFx _fx;
        Renderer _slab, _heart;
        TrailRenderer _trail;
        float _age, _endT = -1f, _punch;

        public static BombView Create(WorldNet.BombFx fx)
        {
            var go = new GameObject("FlameBomb") { layer = Layers.Fx };
            var v = go.AddComponent<BombView>();
            v._fx = fx;
            (v._slab, v._heart) = BombVfx.Build(go.transform);
            if (Debris.Suppressed) v._age = 1f; // (history for a late joiner: it's been there a while)
            v.Moved();
            v.LateUpdate();
            return v;
        }

        /// <summary>It stuck to something, or came loose.</summary>
        public void Moved()
        {
            bool fly = _fx.mode == WorldNet.BombFx.Flying;
            if (fly && _trail == null)
            {
                Follow(); // (the trail starts where it is)
                _trail = Glow.Trail(transform, FireVfx.Orange, 0.18f, 0.14f);
            }
            else if (!fly && _trail != null)
            {
                Glow.DropTrail(_trail);
                _trail = null;
            }
            if (!fly) _punch = 1f;
        }

        /// <summary>It's gone (a blast covers it, or it was put out): it shrinks to nothing.</summary>
        public void End()
        {
            if (_trail != null) Glow.DropTrail(_trail);
            _trail = null;
            _endT = 0f;
        }

        void Follow()
        {
            _fx.Pose(WorldNet.Now, out var p, out var q);
            transform.SetPositionAndRotation(p, q);
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            _age += dt;
            if (_endT >= 0f)
            {
                _endT += dt;
                transform.localScale = Vector3.one * Mathf.Max(0f, 1f - _endT / 0.12f);
                if (_endT >= 0.12f) Destroy(gameObject);
                return;
            }
            Follow();
            _punch = Mathf.MoveTowards(_punch, 0f, dt * 6f);
            float size = Mathf.Min(1f, _age / 0.1f) * (1f + 0.25f * _punch * _punch);
            if (_fx.triggered >= 0)
            {
                // three hard flashes, swelling as it goes
                float u = Mathf.Clamp01((float)(WorldNet.Now - _fx.triggered) / SpellPlans.BombFuse);
                bool on = Mathf.Repeat(u * 3f, 1f) < 0.55f;
                Glow.Tint(_slab, on ? BombVfx.Flash : BombVfx.Red);
                Glow.Tint(_heart, on ? BombVfx.Flash : BombVfx.Heart);
                size *= 1f + 0.18f * u;
            }
            else
            {
                float beat = 0.75f + 0.25f * Mathf.Sin(Time.time * 5f + _fx.id);
                Glow.Tint(_heart, new Color(BombVfx.Heart.r * beat, BombVfx.Heart.g * beat, BombVfx.Heart.b * beat, 1f));
            }
            transform.localScale = Vector3.one * size;
        }
    }

    /// <summary>The bombs in a caster's hands while they place or throw them, and the spark off the raised hand of Remote Detonation.</summary>
    public class BombHandsFx : MonoBehaviour
    {
        Combatant _c;
        Transform _right, _left;
        bool _sparked;

        void Awake() => _c = GetComponent<Combatant>();

        void LateUpdate()
        {
            if (_c == null || !_c.IsSpawned) return;
            var pose = _c.ViewPose;
            float t = _c.ViewPoseTime;
            bool alive = _c.IsAlive && _c.Visible;
            bool place = alive && pose == SpellPose.BombPlace && t < SpellPlans.BombPlace;
            bool toss = alive && pose == SpellPose.BombToss && t < SpellPlans.TossTime;
            Hold(ref _right, place || toss, true);
            Hold(ref _left, toss, false);
            if (pose != SpellPose.LeftArmUp || t < SpellPlans.DetonateTime) { _sparked = false; return; }
            if (_sparked || !alive || t > SpellPlans.DetonateTime + 0.2f) return;
            _sparked = true;
            EnergyBurst.Create(SpellVisuals.LeftHand(_c), new EnergyBurst.Settings
            {
                flashSize = 0.45f, flashTime = 0.15f, sparkCount = 10, sparkSize = 0.22f, sparkSpeed = new Vector2(3f, 6f), sparkLife = 0.3f, colour = BombVfx.Flash,
            });
        }

        void Hold(ref Transform bomb, bool on, bool right)
        {
            if (!on)
            {
                if (bomb != null) Destroy(bomb.gameObject);
                bomb = null;
                return;
            }
            if (bomb == null)
            {
                bomb = new GameObject("HeldBomb") { layer = Layers.Fx }.transform;
                BombVfx.Build(bomb);
            }
            Vector3 at = right ? SpellVisuals.RightHand(_c) : SpellVisuals.LeftHand(_c);
            bomb.SetPositionAndRotation(at + _c.transform.forward * 0.08f, _c.transform.rotation * Quaternion.Euler(-90f, 0f, 0f));
        }

        void OnDestroy()
        {
            if (_right != null) Destroy(_right.gameObject);
            if (_left != null) Destroy(_left.gameObject);
        }
    }
}
