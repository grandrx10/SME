using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Necromatic Blast: the voxels torn out of the ground turn into little sickly green spheres that swarm up over the
    /// caster's head and run together into one ball (a Ball of Flame's size); thrown, it flies off dripping more of them.
    /// </summary>
    public class NecroBallView : MonoBehaviour
    {
        class Mote { public Transform t; public Vector3 from, swirl; public float delay, size; }
        class Drop { public Transform t; public Vector3 v; public float age, size; }

        WorldNet.FireballFx _fx;
        Transform _core;
        readonly List<Mote> _motes = new();
        readonly List<Drop> _drops = new();
        readonly List<Transform> _moons = new();
        TrailRenderer _trail;
        float _owed, _grown;
        int _arrived, _total;
        bool _gone;

        const int MaxMotes = 64, Moons = 5;
        const float DropLife = 0.4f;
        static readonly Color Dark = new(0.3f, 1.1f, 0.16f, 1f);

        public static void Create(WorldNet.FireballFx fx, VoxelWorld world, List<VoxelWorld.RemovedCell> cells)
        {
            var go = new GameObject("NecroBall") { layer = Layers.Fx };
            var v = go.AddComponent<NecroBallView>();
            v._fx = fx;
            go.transform.position = WorldNet.FireballPosition(fx, WorldNet.Now);
            v._core = Glow.Piece(go.transform, Glow.SphereMesh, SpellPlans.NecroGlow, "Core");
            v._core.localScale = Vector3.zero;
            for (int i = 0; i < Moons; i++) v._moons.Add(Glow.Piece(go.transform, Glow.SphereMesh, i % 2 == 0 ? Dark : SpellPlans.NecroGlow, "Moon"));
            int step = Mathf.Max(1, Mathf.CeilToInt(cells.Count / (float)MaxMotes));
            for (int i = 0; i < cells.Count; i += step)
            {
                var t = Glow.Piece(null, Glow.SphereMesh, i % 3 == 0 ? Dark : SpellPlans.NecroGlow, "Mote");
                Vector3 p = world.CellCenter(cells[i].cell);
                t.position = p;
                t.localScale = Glow.SphereScale(VoxelWorld.Size * 0.5f);
                v._motes.Add(new Mote { t = t, from = p, swirl = Random.onUnitSphere * Random.Range(0.3f, 0.9f), delay = Random.Range(0f, 0.3f), size = Random.Range(0.06f, 0.11f) });
            }
            v._total = Mathf.Max(1, v._motes.Count);
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            Drops(dt);
            if (_gone) { if (_drops.Count == 0) Destroy(gameObject); return; }
            if (_fx.done || WorldNet.Instance == null || !WorldNet.Instance.Fireballs.ContainsKey(_fx.id)) { End(); return; }
            double now = WorldNet.Now;
            transform.position = WorldNet.FireballPosition(_fx, now);

            Motes(now);
            _grown = Mathf.MoveTowards(_grown, _fx.launched ? 1f : _arrived / (float)_total, dt * 6f);
            float size = SpellPlans.FireballCore * SpellPlans.NecroScale * Mathf.Pow(_grown, 1f / 3f) * (1f + 0.07f * Mathf.Sin(Time.time * 26f));
            _core.localScale = Vector3.one * (_grown > 0f ? size : 0f);
            // lumps running round its surface: it's never quite one ball
            for (int i = 0; i < _moons.Count; i++)
            {
                float a = Time.time * (5f + i) + i * 1.9f;
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a * 0.7f + i), Mathf.Sin(a)).normalized;
                _moons[i].localPosition = dir * (size * 0.8f);
                _moons[i].localScale = Vector3.one * (size * (0.28f + 0.06f * Mathf.Sin(a * 2f)));
            }
            if (!_fx.launched) return;
            if (_trail == null)
            {
                _trail = Glow.Trail(transform, Dark, 0.22f, 0.5f);
                var burst = EnergyBurst.Release;
                burst.colour = SpellPlans.NecroGlow;
                EnergyBurst.Create(transform.position, burst);
            }
            _owed += dt * 45f;
            while (_owed >= 1f)
            {
                _owed -= 1f;
                Spill(transform.position + Random.insideUnitSphere * 0.3f, -_fx.velocity.normalized * Random.Range(1f, 4f) + Random.insideUnitSphere * 1.5f, Random.Range(0.06f, 0.15f));
            }
        }

        /// <summary>The little spheres: popped out of the ground, then pulled in over the head, each its own way round.</summary>
        void Motes(double now)
        {
            float t = (float)(now - _fx.start);
            for (int i = _motes.Count - 1; i >= 0; i--)
            {
                var m = _motes[i];
                float u = Mathf.Clamp01((t - m.delay * SpellPlans.NecroCharge * 0.8f) / (SpellPlans.NecroCharge * 0.68f));
                if (u >= 1f || _fx.launched)
                {
                    _arrived++;
                    Destroy(m.t.gameObject);
                    _motes.RemoveAt(i);
                    continue;
                }
                float e = u * u * (3f - 2f * u);
                Vector3 mid = m.from + Vector3.up * (0.5f * Mathf.Min(1f, u * 4f));
                m.t.position = Vector3.Lerp(mid, transform.position, e * e) + m.swirl * Mathf.Sin(Mathf.PI * e);
                float s = Mathf.Lerp(VoxelWorld.Size * 0.5f, m.size, Mathf.Clamp01(u * 3f));
                m.t.localScale = Glow.SphereScale(s);
            }
        }

        void Spill(Vector3 at, Vector3 v, float size)
        {
            var t = Glow.Piece(null, Glow.SphereMesh, Random.value < 0.4f ? Dark : SpellPlans.NecroGlow, "Drop");
            t.position = at;
            t.localScale = Glow.SphereScale(size);
            _drops.Add(new Drop { t = t, v = v, size = size });
        }

        void Drops(float dt)
        {
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                var d = _drops[i];
                d.age += dt;
                if (d.age >= DropLife) { Destroy(d.t.gameObject); _drops.RemoveAt(i); continue; }
                d.v += Vector3.down * (6f * dt);
                d.t.position += d.v * dt;
                d.t.localScale = Glow.SphereScale(d.size * (1f - d.age / DropLife));
            }
        }

        /// <summary>It landed (or fizzled): it bursts, its drops left to shrink away.</summary>
        void End()
        {
            _gone = true;
            if (_trail != null) Glow.DropTrail(_trail);
            foreach (var m in _motes) if (m.t != null) Destroy(m.t.gameObject);
            _motes.Clear();
            if (_fx.done)
            {
                Vector3 p = _fx.origin;
                var burst = EnergyBurst.Release;
                burst.colour = SpellPlans.NecroGlow;
                burst.flashSize = 1.5f;
                EnergyBurst.Create(p, burst);
                for (int i = 0; i < 14; i++) Spill(p, Random.onUnitSphere * Random.Range(2f, 6f) + Vector3.up * 2f, Random.Range(0.08f, 0.18f));
                CameraRig.ShakeFrom(p, 0.3f, 0.25f);
            }
            foreach (Transform t in transform) t.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            foreach (var m in _motes) if (m.t != null) Destroy(m.t.gameObject);
            foreach (var d in _drops) if (d.t != null) Destroy(d.t.gameObject);
        }
    }
}
