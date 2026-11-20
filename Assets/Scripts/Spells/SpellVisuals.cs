using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Shared bits for spell effects: colours, materials, particle helpers. Everything here is local-only.</summary>
    public static class SpellVisuals
    {
        public static readonly Color ArcaneBlue = new(0.35f, 0.62f, 1f, 1f);

        static Material _soft;
        static Texture2D _dot;

        /// <summary>Soft round sprite (puffs and sparkles).</summary>
        public static Material Soft
        {
            get
            {
                if (_soft != null) return _soft;
                _soft = new Material(Shader.Find("Sprites/Default")) { name = "SoftParticle", mainTexture = Dot };
                return _soft;
            }
        }

        static Texture2D Dot
        {
            get
            {
                if (_dot != null) return _dot;
                const int n = 64;
                _dot = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[n * n];
                for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * a * 255));
                }
                _dot.SetPixels32(px);
                _dot.Apply();
                return _dot;
            }
        }

        public static ParticleSystem Particles(Transform parent, string name, Color color, float rate, Vector2 life, Vector2 size, bool world)
        {
            var go = new GameObject(name) { layer = Layers.Fx };
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
            main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
            main.startSpeed = 0f;
            main.startColor = color;
            main.simulationSpace = world ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
            main.maxParticles = 2000;
            var em = ps.emission;
            em.rateOverTime = rate;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = Soft;
            return ps;
        }

        /// <summary>A glowing ball (HDR, blooms) with an optional point light.</summary>
        public static GameObject GlowBall(Transform parent, float size, Color color, float light)
        {
            var t = Glow.Piece(parent, Glow.SphereMesh, color.maxColorComponent <= 1f ? Glow.Arcane : color, "GlowBall");
            t.localScale = Vector3.one * size;
            if (light > 0f)
            {
                var l = t.gameObject.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = ArcaneBlue;
                l.range = light;
                l.intensity = 2.5f;
                l.shadows = LightShadows.None;
            }
            return t.gameObject;
        }

        /// <summary>A body's right hand (falls back to its chest).</summary>
        public static Vector3 RightHand(Combatant c) => Hand(c, "R");

        /// <summary>A body's left hand (falls back to its chest).</summary>
        public static Vector3 LeftHand(Combatant c) => Hand(c, "L");

        static Vector3 Hand(Combatant c, string side)
        {
            if (c == null) return Vector3.zero;
            if (c.Bones != null && c.Bones.TryGetValue("Arm " + side + ".003", out var h) && h != null)
                return c.Bones.TryGetValue("Arm " + side + ".003_end", out var tip) ? (h.position + tip.position) * 0.5f : h.position;
            return c.transform.position + Vector3.up * 0.4f;
        }
    }

    // ====================================================================== Mana Missiles

    /// <summary>
    /// Mana Missiles, chromatist-style: power gathers at the caster's hand (streaks rushing in, a pulsing core,
    /// runes written out of it), then goes off in a burst with a camera shake, and glowing prisms with tapering
    /// trails scatter out and whip round onto the target. Each one goes in a small burst, its trail left to shrink away.
    /// </summary>
    public class MissileView : MonoBehaviour
    {
        WorldNet.MissileFx _fx;
        ArcaneCharge _charge;
        readonly List<(Transform body, TrailRenderer trail)> _shots = new();
        bool _released;

        const float Length = 0.55f, Thickness = 0.3f;

        public static MissileView Create(WorldNet.MissileFx fx)
        {
            var go = new GameObject("Missiles") { layer = Layers.Fx };
            var v = go.AddComponent<MissileView>();
            v._fx = fx;
            if (!Debris.Suppressed)
            {
                Combatant Caster() => WorldNet.FindCombatant(fx.caster);
                v._charge = ArcaneCharge.Create(
                    () => { var c = Caster(); return SpellVisuals.RightHand(c) + (c != null ? c.transform.forward * 0.12f : Vector3.zero); },
                    () => { var c = Caster(); return c != null && c.IsAlive && !WorldNet.Fizzled(fx.caster, fx.start, fx.start + SpellPlans.MissileCharge); },
                    SpellPlans.MissileCharge);
            }
            return v;
        }

        void Update()
        {
            float t = (float)(WorldNet.Now - _fx.start);
            if (!_fx.launched)
            {
                if (_charge == null && t > SpellPlans.MissileCharge + 1.5f) Destroy(gameObject); // never launched (caster died)
                return;
            }
            if (!_released)
            {
                _released = true;
                if (_charge != null) _charge.Release();
                else if (_fx.m.Length > 0) EnergyBurst.Create(_fx.m[0].pos, EnergyBurst.Release);
                if (_fx.m.Length > 0) CameraRig.ShakeFrom(_fx.m[0].pos, 0.25f, 0.2f);
            }
            while (_shots.Count < _fx.m.Length)
            {
                var m = _fx.m[_shots.Count];
                var body = Glow.Piece(transform, Glow.PrismMesh, Glow.Arcane, "Missile");
                body.localScale = new Vector3(Thickness, Thickness, Length);
                body.SetPositionAndRotation(m.pos, Quaternion.LookRotation(m.dir));
                var trail = Glow.Trail(body, Glow.Arcane, 0.3f, 0.26f);
                _shots.Add((body, trail));
            }
            bool any = false;
            for (int i = 0; i < _shots.Count; i++)
            {
                var (body, trail) = _shots[i];
                if (body == null) continue;
                var m = _fx.m[i];
                if (!m.alive)
                {
                    body.position = m.pos;
                    EnergyBurst.Create(m.pos, EnergyBurst.Impact);
                    CameraRig.ShakeFrom(m.pos, 0.08f, 0.12f);
                    Glow.DropTrail(trail);
                    Destroy(body.gameObject);
                    _shots[i] = (null, null);
                    continue;
                }
                any = true;
                // pointing where it flies, spinning about that axis
                body.SetPositionAndRotation(m.pos, Quaternion.LookRotation(m.dir) * Quaternion.Euler(0f, 0f, Time.time * 540f + i * 45f));
            }
            if (!any && _shots.Count >= _fx.m.Length) Destroy(gameObject, 0.1f);
        }

        void OnDestroy()
        {
            if (_charge != null) _charge.Cancel();
        }
    }

    // ====================================================================== Magical Barrier

    /// <summary>
    /// The barrier: chromatist's hex shield (Resources/VFX/HexBarrier — a hexagon-paneled sphere on the SME/Barrier
    /// shader) in arcane blue. Panels flicker in one by one as it pops out, then it dissolves away.
    /// Follows the caster, never rotates.
    /// </summary>
    public class BarrierView : MonoBehaviour
    {
        static readonly int RevealId = Shader.PropertyToID("_Reveal");
        static readonly int FadeId = Shader.PropertyToID("_Fade");
        static readonly int ColourId = Shader.PropertyToID("_BaseColor");
        const float AppearTime = 0.2f, PopScale = 0.85f, FadeTime = 0.3f;

        WorldNet.BarrierFx _fx;
        Renderer[] _renderers;
        MaterialPropertyBlock _mpb;
        bool _burst;

        public static BarrierView Create(WorldNet.BarrierFx fx)
        {
            var prefab = Resources.Load<GameObject>("VFX/HexBarrier");
            GameObject go;
            if (prefab != null) go = Instantiate(prefab);
            else
            {
                Debug.LogWarning("[SME] Resources/VFX/HexBarrier is missing; the barrier is drawn as a plain sphere.");
                go = new GameObject("Barrier");
                Glow.Piece(go.transform, Glow.SphereMesh, Glow.Arcane);
            }
            go.name = "Barrier";
            go.layer = Layers.Fx;
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            var v = go.AddComponent<BarrierView>();
            v._fx = fx;
            v._renderers = go.GetComponentsInChildren<Renderer>(true);
            v._mpb = new MaterialPropertyBlock();
            go.transform.localScale = Vector3.zero;
            return v;
        }

        void LateUpdate()
        {
            float t = (float)(WorldNet.Now - _fx.start) - SpellPlans.BarrierWindup;
            var c = WorldNet.FindCombatant(_fx.caster);
            if (c != null) transform.SetPositionAndRotation(c.transform.position + Vector3.up * 0.1f, Quaternion.identity);
            if (t < 0f)
            {
                transform.localScale = Vector3.zero;
                // knocked down before it went up: it never does
                if (WorldNet.Fizzled(_fx.caster, _fx.start, _fx.start + SpellPlans.BarrierWindup)) Destroy(gameObject);
                return;
            }
            if (!_burst)
            {
                _burst = true;
                EnergyBurst.Create(transform.position, EnergyBurst.Release);
            }
            float life = SpellPlans.BarrierTime;
            if (t > life) { Destroy(gameObject); return; }

            float appear = Mathf.Clamp01(t / AppearTime);
            float eased = 1f - (1f - appear) * (1f - appear) * (1f - appear);
            float fade = 1f - Mathf.Clamp01((t - (life - FadeTime)) / FadeTime);
            transform.localScale = Vector3.one * (SpellPlans.BarrierRadius * Mathf.Lerp(PopScale, 1f, eased));
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetFloat(RevealId, appear);
                _mpb.SetFloat(FadeId, fade);
                _mpb.SetColor(ColourId, Glow.Arcane);
                r.SetPropertyBlock(_mpb);
            }
        }
    }

    // ====================================================================== on the caster's body

    /// <summary>Effects that ride on a body while it plays certain gestures: inked wind round a dash, a glowing siphon fist.</summary>
    public class SpellPoseFx : MonoBehaviour
    {
        Combatant _c;
        DashWind _dash;
        GameObject _glow;
        TrailRenderer _glowTrail;
        FlameAura _fireL, _fireR;

        void Awake() => _c = GetComponent<Combatant>();

        void LateUpdate()
        {
            if (_c == null || !_c.IsSpawned) return;
            var pose = _c.ViewPose;
            float t = _c.ViewPoseTime;
            bool alive = _c.IsAlive;

            bool dash = alive && pose == SpellPose.Dash && t < SpellPlans.DashTime + 0.05f;
            if (dash && _dash == null) _dash = new DashWind(null);
            if (_dash != null && !_dash.Tick(_c.transform.position, _c.ViewVelocity, dash))
            {
                _dash.Destroy();
                _dash = null;
            }

            // Explosive Escape: both hands burn until the clap
            bool fire = alive && pose == SpellPose.FlameHands && t < SpellPlans.EscapeDelay;
            if (fire && _fireR == null)
            {
                _fireR = FireVfx.Aura(null, Vector3.zero, 0.1f, 5);
                _fireL = FireVfx.Aura(null, Vector3.zero, 0.1f, 5);
            }
            if (_fireR != null)
            {
                if (!fire)
                {
                    Destroy(_fireR.gameObject);
                    Destroy(_fireL.gameObject);
                    _fireR = _fireL = null;
                }
                else
                {
                    _fireR.transform.position = SpellVisuals.RightHand(_c);
                    _fireL.transform.position = SpellVisuals.LeftHand(_c);
                }
            }

            bool glow = alive && (pose == SpellPose.Siphon && t < SpellPlans.SiphonTime + 0.1f || pose == SpellPose.SiphonPunch && t < 0.4f);
            if (glow && _glow == null)
            {
                _glow = SpellVisuals.GlowBall(null, 0.2f, Glow.Arcane, 3f);
                _glowTrail = Glow.Trail(_glow.transform, Glow.Arcane, 0.2f, 0.18f);
            }
            if (_glow != null)
            {
                if (!glow)
                {
                    EnergyBurst.Create(_glow.transform.position, EnergyBurst.Small);
                    Glow.DropTrail(_glowTrail);
                    Destroy(_glow);
                    _glow = null;
                }
                else
                {
                    _glow.transform.position = SpellVisuals.RightHand(_c);
                    _glow.transform.localScale = Vector3.one * (0.2f * (1f + 0.15f * Mathf.Sin(Time.time * 50f)));
                }
            }
        }

        void OnDestroy()
        {
            _dash?.Destroy();
            if (_glow != null) Destroy(_glow);
            if (_fireR != null) Destroy(_fireR.gameObject);
            if (_fireL != null) Destroy(_fireL.gameObject);
        }
    }
}
