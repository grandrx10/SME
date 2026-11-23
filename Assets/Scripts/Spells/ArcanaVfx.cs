using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Looks for Wheel of Fortune, Justice, Death, the breaths, the Grapple Hook and the Necromancy cards: flat HDR glow
    /// with ink outlines, things shrink away rather than fade. Local-only; drawn by every peer from the op stream.
    /// </summary>
    public static class ArcanaVfx
    {
        public static readonly Color Steel = new(2.2f, 2.35f, 2.6f, 1f);           // Weapon: steel silver-white
        public static readonly Color GoldPurple = new(2.6f, 1.2f, 2.8f, 1f);       // Wheel 6
        public static readonly Color Doom = new(4f, 0.2f, 0.12f, 1f);              // Wheel 0
        public static readonly Color Pip = new(3.2f, 2.4f, 0.6f, 1f);              // the die's spots (gold on mahogany)
        public static readonly Color Ice = new(1.6f, 2.6f, 3.2f, 1f);
        public static readonly Color Frost = new(2.6f, 2.9f, 3.1f, 1f);
        public static readonly Color Blood = new(0.75f, 0.03f, 0.04f, 1f);

        /// <summary>Spots of a die face showing <paramref name="n"/> (0..6), on a unit face, in face coordinates (-0.5..0.5).</summary>
        public static IEnumerable<Vector2> Pips(int n)
        {
            const float a = 0.27f;
            switch (n)
            {
                case 1: yield return Vector2.zero; break;
                case 2: yield return new(-a, -a); yield return new(a, a); break;
                case 3: yield return new(-a, -a); yield return Vector2.zero; yield return new(a, a); break;
                case 4: yield return new(-a, -a); yield return new(a, a); yield return new(-a, a); yield return new(a, -a); break;
                case 5: yield return new(-a, -a); yield return new(a, a); yield return new(-a, a); yield return new(a, -a); yield return Vector2.zero; break;
                case 6:
                    yield return new(-a, -a); yield return new(-a, 0f); yield return new(-a, a);
                    yield return new(a, -a); yield return new(a, 0f); yield return new(a, a);
                    break;
            }
        }

        static readonly Vector3[] FaceNormals = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };

        /// <summary>A mahogany die (unit size) with <paramref name="n"/> gold spots on every face (0: a red cross on each).</summary>
        public static Transform Die(Transform parent, int n)
        {
            var root = new GameObject("Die") { layer = Layers.Fx }.transform;
            root.SetParent(parent, false);
            Glow.Piece(root, Glow.CubeMesh, TarotVfx.Mahogany, "Body");
            if (n < 0) return root;
            foreach (var normal in FaceNormals)
            {
                Vector3 u = Mathf.Abs(normal.y) > 0.5f ? Vector3.right : Vector3.up;
                Vector3 v = Vector3.Cross(normal, u);
                if (n == 0)
                {
                    // the 1-in-100: a red cross
                    for (int k = 0; k < 2; k++)
                    {
                        var bar = Glow.Piece(root, Glow.CubeMesh, Doom, "Cross", false);
                        bar.localPosition = normal * 0.5f;
                        bar.localRotation = Quaternion.LookRotation(normal, (k == 0 ? u + v : u - v).normalized);
                        bar.localScale = new Vector3(0.12f, 0.7f, 0.04f);
                    }
                    continue;
                }
                foreach (var p in Pips(n))
                {
                    var pip = Glow.Piece(root, Glow.SphereMesh, Pip, "Pip", false);
                    pip.localPosition = normal * 0.5f + u * p.x + v * p.y;
                    pip.localScale = Vector3.one * 0.17f;
                }
            }
            return root;
        }

        /// <summary>Shrinks every child away and then destroys the object (shrink, don't fade).</summary>
        public static void ShrinkAway(GameObject go, float seconds)
        {
            if (go == null) return;
            var s = go.GetComponent<Shrinker>() ?? go.AddComponent<Shrinker>();
            s.Begin(seconds);
        }

        class Shrinker : MonoBehaviour
        {
            float _t, _time = 0.2f;
            Vector3 _s0;
            bool _on;
            public void Begin(float seconds) { if (_on) return; _on = true; _time = Mathf.Max(0.01f, seconds); _s0 = transform.localScale; }
            void LateUpdate()
            {
                if (!_on) return;
                _t += Time.deltaTime;
                float u = Mathf.Clamp01(_t / _time);
                transform.localScale = _s0 * (1f - u) * (1f - u);
                if (u >= 1f) Destroy(gameObject);
            }
        }

        /// <summary>A line with an ink edge underneath (the rope, a tether's core).</summary>
        public static (LineRenderer core, LineRenderer edge) Line(Transform parent, Color colour, float width)
        {
            LineRenderer Make(string name, Material m, float w)
            {
                var go = new GameObject(name) { layer = Layers.Fx };
                go.transform.SetParent(parent, false);
                var l = go.AddComponent<LineRenderer>();
                l.sharedMaterial = m;
                l.widthMultiplier = w;
                l.numCapVertices = 2;
                l.useWorldSpace = true;
                l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                l.receiveShadows = false;
                l.alignment = LineAlignment.View;
                return l;
            }
            var core = Make("Core", Glow.DoubleSided, width);
            Glow.Tint(core, colour);
            var edge = Make("Edge", Glow.Underlay, width + Glow.OutlineExtra);
            Glow.Tint(edge, Glow.Ink);
            return (core, edge);
        }

        public static void SetLine((LineRenderer core, LineRenderer edge) l, List<Vector3> pts)
        {
            l.core.positionCount = l.edge.positionCount = pts.Count;
            for (int i = 0; i < pts.Count; i++) { l.core.SetPosition(i, pts[i]); l.edge.SetPosition(i, pts[i]); }
        }
    }

    // ====================================================================== Wheel of Fortune

    /// <summary>The die over the caster's head: it tumbles, then sits still showing the number on every side, then goes.</summary>
    public class WheelView : MonoBehaviour
    {
        WorldNet.WheelFx _fx;
        Transform _rolling, _shown;
        bool _revealed;
        Vector3 _spin;

        public static WheelView Create(WorldNet.WheelFx fx)
        {
            var go = new GameObject("WheelOfFortune") { layer = Layers.Fx };
            var v = go.AddComponent<WheelView>();
            v._fx = fx;
            v._rolling = ArcanaVfx.Die(go.transform, -1);
            v._spin = new Vector3(Random.Range(500f, 800f), Random.Range(300f, 600f), Random.Range(200f, 500f));
            go.transform.localScale = Vector3.zero;
            v.LateUpdate();
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            var c = WorldNet.FindCombatant(_fx.caster);
            if (c != null) transform.position = (c.RagdollFocus != null ? c.RagdollFocus.position : c.transform.position) + Vector3.up * SpellPlans.DieAbove;
            float end = SpellPlans.WheelRoll + SpellPlans.WheelShow;
            if (t >= end || c == null || !c.IsAlive)
            {
                // a 3: it's thrown (the flying die takes over at once); anything else: it shrinks away
                if (_fx.result == 3 && t >= end) { Destroy(gameObject); return; }
                ArcanaVfx.ShrinkAway(gameObject, 0.15f);
                enabled = false;
                return;
            }
            float pop = t < 0f ? 0f : Mathf.Min(1f, t / 0.12f);
            transform.localScale = Vector3.one * (SpellPlans.DieSize * (pop < 1f ? pop * 1.2f : 1f));
            if (t < SpellPlans.WheelRoll)
            {
                // rolling in place, slowing towards the end
                float slow = 1f - Mathf.Clamp01(t / SpellPlans.WheelRoll) * 0.6f;
                _rolling.localRotation *= Quaternion.Euler(_spin * (Time.deltaTime * slow));
                return;
            }
            if (!_revealed)
            {
                _revealed = true;
                Destroy(_rolling.gameObject);
                _shown = ArcanaVfx.Die(transform, _fx.result);
                if (!Debris.Suppressed) HierophantView.Spark(transform.position, 0.7f);
            }
            // settled, turning slowly so every side shows the number
            _shown.localRotation = Quaternion.Euler(18f, t * 120f, 0f);
            float throb = 1f + 0.08f * Mathf.Sin(t * 20f) * Mathf.Exp(-(t - SpellPlans.WheelRoll) * 4f);
            transform.localScale = Vector3.one * (SpellPlans.DieSize * throb);
        }
    }

    /// <summary>The die thrown on a roll of 3: tumbling along its flight.</summary>
    public class DieView : MonoBehaviour
    {
        WorldNet.DieFx _fx;

        public static DieView Create(WorldNet.DieFx fx)
        {
            var go = new GameObject("ThrownDie") { layer = Layers.Fx };
            var v = go.AddComponent<DieView>();
            v._fx = fx;
            ArcanaVfx.Die(go.transform, 3);
            go.transform.localScale = Vector3.one * SpellPlans.DieSize;
            Glow.Trail(go.transform, TarotVfx.Mahogany, 0.2f, 0.25f);
            v.LateUpdate();
            return v;
        }

        void LateUpdate()
        {
            if (_fx.ended >= 0)
            {
                foreach (var t in GetComponentsInChildren<TrailRenderer>()) if (t.transform.parent == transform) Glow.DropTrail(t);
                Destroy(gameObject);
                return;
            }
            double now = WorldNet.Now;
            transform.position = WorldNet.DiePosition(_fx, now);
            float a = (float)(now - _fx.t);
            transform.rotation = Quaternion.Euler(a * 720f, a * 300f, 0f);
        }
    }

    // ====================================================================== Justice

    /// <summary>A golden mahogany tether from the caster's chest to the target's: a glowing cord, sagging a little.</summary>
    public class JusticeView : MonoBehaviour
    {
        WorldNet.JusticeFx _fx;
        (LineRenderer core, LineRenderer edge) _cord;
        readonly List<Vector3> _pts = new();
        float _grow;
        static readonly Color Cord = Color.Lerp(TarotVfx.Gold, TarotVfx.Mahogany, 0.45f);

        public static JusticeView Create(WorldNet.JusticeFx fx)
        {
            var go = new GameObject("JusticeTether") { layer = Layers.Fx };
            var v = go.AddComponent<JusticeView>();
            v._fx = fx;
            v._cord = ArcanaVfx.Line(go.transform, Cord, 0.08f);
            return v;
        }

        void LateUpdate()
        {
            var a = WorldNet.FindCombatant(_fx.caster);
            var b = WorldNet.FindCombatant(_fx.target);
            bool live = _fx.ended < 0 && a != null && b != null;
            _grow = Mathf.MoveTowards(_grow, live ? 1f : 0f, Time.deltaTime / (live ? 0.18f : 0.15f));
            if (!live && _grow <= 0f)
            {
                if (_fx.ended >= 0 && !Debris.Suppressed) HierophantView.Spark(_fx.endPoint, 0.6f); // it snaps
                Destroy(gameObject);
                return;
            }
            Vector3 from = a != null ? SpellPlans.LoversChest(a) : transform.position, to = b != null ? SpellPlans.LoversChest(b) : from;
            transform.position = from;
            Vector3 span = to - from;
            float len = span.magnitude, sag = Mathf.Min(0.6f, len * 0.05f);
            _pts.Clear();
            const int n = 24;
            for (int i = 0; i <= n; i++)
            {
                float u = i / (float)n * _grow;
                _pts.Add(from + span * u + Vector3.down * (sag * 4f * u * (1f - u)) + Vector3.up * (0.04f * Mathf.Sin(Time.time * 8f + u * 12f)));
            }
            ArcanaVfx.SetLine(_cord, _pts);
        }
    }

    // ====================================================================== Death

    /// <summary>
    /// Death's aura: one black ring on the ground round the caster, spreading out to its full size, following them.
    /// Shrinks away when it ends.
    /// </summary>
    public class DeathView : MonoBehaviour
    {
        WorldNet.DeathFx _fx;
        RuneDrawing _ring;
        Transform _ringT;
        static readonly Color Black = new(0.02f, 0.02f, 0.025f, 1f);

        public static DeathView Create(WorldNet.DeathFx fx)
        {
            var go = new GameObject("DeathAura") { layer = Layers.Fx };
            var v = go.AddComponent<DeathView>();
            v._fx = fx;
            var ring = new GameObject("Ring") { layer = Layers.Fx };
            ring.transform.SetParent(go.transform, false);
            ring.transform.localPosition = Vector3.up * 0.06f;
            v._ringT = ring.transform;
            v._ring = new RuneDrawing(ring);
            v._ring.AddRune(new List<List<Vector2>> { TarotVfx.Ring(1f, 0f, 96) }, SpellPlans.DeathRadius * 2f, 0.12f);
            v._ring.Progress = 1f;
            v._ring.SetColour(Black);
            ring.transform.localScale = Vector3.zero;
            v.LateUpdate();
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            var c = WorldNet.FindCombatant(_fx.caster);
            if (c != null) transform.position = c.FeetPosition;
            float scale;
            if (_fx.ended >= 0)
            {
                float u = Mathf.Clamp01((float)(now - _fx.ended) / 0.3f);
                if (u >= 1f || t < 0f) { Destroy(gameObject); return; }
                scale = (1f - u) * (1f - u) * Mathf.Clamp01(t / SpellPlans.DeathGrow);
            }
            else scale = t < 0f ? 0f : 1f - Mathf.Pow(1f - Mathf.Clamp01(t / SpellPlans.DeathGrow), 3f);
            _ringT.localScale = Vector3.one * Mathf.Max(0.001f, scale);
        }

        void OnDestroy() => _ring?.Release();
    }

    // ====================================================================== the breaths

    /// <summary>
    /// The Flame Thrower (outlined voxel cubes of fire in yellow, orange and red, spewed in a cone from the clasped hands)
    /// and Icy Breath (pale ice cubes and little star-shaped flakes from the mouth). Each piece flies out, tumbling, and
    /// shrinks to nothing.
    /// </summary>
    public class BreathView : MonoBehaviour
    {
        WorldNet.BreathFx _fx;
        class Bit { public Transform t; public Vector3 v, spin; public float age, life, size; }
        readonly List<Bit> _bits = new();
        float _owed;

        public static BreathView Create(WorldNet.BreathFx fx)
        {
            var go = new GameObject(fx.kind == 0 ? "FlameThrower" : "IcyBreath") { layer = Layers.Fx };
            var v = go.AddComponent<BreathView>();
            v._fx = fx;
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float dt = Time.deltaTime;
            var c = WorldNet.FindCombatant(_fx.caster);
            bool going = _fx.ended < 0 && now >= _fx.start && c != null && c.IsAlive;
            bool ice = _fx.kind == 1;
            if (going)
            {
                SpellPlans.BreathFrom(c, out var origin, out var dir);
                if (!ice && c is PlayerController) origin = (SpellVisuals.RightHand(c) + SpellVisuals.LeftHand(c)) * 0.5f + dir * 0.15f;
                float reach = ice ? SpellPlans.IceBreathReach : SpellPlans.FlameReach;
                float angle = ice ? SpellPlans.IceBreathAngle : SpellPlans.FlameAngle;
                _owed += (ice ? 120f : 160f) * dt;
                while (_owed >= 1f)
                {
                    _owed -= 1f;
                    Vector2 r = Random.insideUnitCircle * Mathf.Tan(angle * Mathf.Deg2Rad);
                    Vector3 side = Vector3.Cross(dir, Vector3.up);
                    if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
                    side.Normalize();
                    Vector3 up = Vector3.Cross(side, dir);
                    Vector3 d = (dir + side * r.x + up * r.y).normalized;
                    float life = Random.Range(0.3f, 0.45f);
                    bool flake = ice && Random.value < 0.4f;
                    var t = flake ? Flake(transform) : Glow.Piece(transform, Glow.CubeMesh, ice ? (Random.value < 0.5f ? ArcanaVfx.Ice : ArcanaVfx.Frost) : FireVfx.Random3(), "Bit");
                    t.position = origin + d * Random.Range(0f, 0.3f);
                    t.rotation = Random.rotation;
                    _bits.Add(new Bit
                    {
                        t = t, v = d * (reach / life * Random.Range(0.85f, 1.05f)) + (ice ? Vector3.zero : Vector3.up * 1.5f),
                        spin = Random.insideUnitSphere * 540f, life = life, size = Random.Range(0.12f, 0.22f) * (flake ? 1.6f : 1f),
                    });
                }
            }
            for (int i = _bits.Count - 1; i >= 0; i--)
            {
                var b = _bits[i];
                b.age += dt;
                if (b.age >= b.life) { Destroy(b.t.gameObject); _bits.RemoveAt(i); continue; }
                b.v *= Mathf.Exp(-1.2f * dt);
                b.t.position += b.v * dt;
                b.t.rotation *= Quaternion.Euler(b.spin * dt);
                float u = b.age / b.life;
                // swells as it spreads, then shrinks away
                b.t.localScale = Vector3.one * (b.size * (0.6f + 1.8f * u) * (1f - u * u));
            }
            if (!going && (_fx.ended >= 0 || c == null) && _bits.Count == 0) Destroy(gameObject);
        }

        /// <summary>A little six-pointed snowflake: three crossed bars.</summary>
        static Transform Flake(Transform parent)
        {
            var root = new GameObject("Flake") { layer = Layers.Fx }.transform;
            root.SetParent(parent, false);
            for (int k = 0; k < 3; k++)
            {
                var bar = Glow.Piece(root, Glow.CubeMesh, ArcanaVfx.Frost, "Bar");
                bar.localRotation = Quaternion.Euler(0f, 0f, k * 60f);
                bar.localScale = new Vector3(0.12f, 1f, 0.12f);
            }
            return root;
        }

        void OnDestroy()
        {
            foreach (var b in _bits) if (b.t != null) Destroy(b.t.gameObject);
        }
    }

    // ====================================================================== Grapple Hook

    /// <summary>The black rope from the caster's left hand to a black hook: wavy while it flies, taut once it bites, reeled back when it lets go.</summary>
    public class HookView : MonoBehaviour
    {
        /// <summary>The rope and the hook are black.</summary>
        static readonly Color HookBlack = new(0.05f, 0.05f, 0.06f, 1f);
        WorldNet.HookFx _fx;
        (LineRenderer core, LineRenderer edge) _rope;
        Transform _head;
        readonly List<Vector3> _pts = new();
        Vector3 _tip;
        float _back;

        public static HookView Create(WorldNet.HookFx fx)
        {
            var go = new GameObject("GrappleHook") { layer = Layers.Fx };
            var v = go.AddComponent<HookView>();
            v._fx = fx;
            v._rope = ArcanaVfx.Line(go.transform, HookBlack, 0.05f);
            v._head = new GameObject("Hook") { layer = Layers.Fx }.transform;
            v._head.SetParent(go.transform, false);
            var shaft = Glow.Piece(v._head, Glow.PrismMesh, HookBlack, "Head");
            shaft.localScale = new Vector3(0.35f, 0.35f, 0.45f);
            for (int k = -1; k <= 1; k += 2)
            {
                var prong = Glow.Piece(v._head, Glow.PrismMesh, HookBlack, "Prong");
                prong.localPosition = new Vector3(0.1f * k, 0f, -0.08f);
                prong.localRotation = Quaternion.Euler(0f, 145f * k, 0f);
                prong.localScale = new Vector3(0.2f, 0.2f, 0.28f);
            }
            v._tip = fx.origin;
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            var c = WorldNet.FindCombatant(_fx.caster);
            Vector3 hand = c != null ? SpellVisuals.LeftHand(c) : _fx.origin;
            if (now < _fx.start) { _head.gameObject.SetActive(false); _rope.core.positionCount = _rope.edge.positionCount = 0; return; }
            _head.gameObject.SetActive(true);
            if (_fx.ended >= 0)
            {
                // reeled back to the hand
                _back += Time.deltaTime / 0.18f;
                _tip = Vector3.Lerp(_tip, hand, Mathf.Clamp01(_back));
                if (_back >= 1f) { Destroy(gameObject); return; }
            }
            else _tip = _fx.Tip(now);
            bool taut = _fx.attachAt >= 0 && now >= _fx.attachAt;
            Vector3 d = _tip - hand;
            float len = d.magnitude;
            Vector3 dir = len > 1e-3f ? d / len : Vector3.forward;
            Vector3 side = Vector3.Cross(dir, Vector3.up);
            if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
            side.Normalize();
            _pts.Clear();
            const int n = 20;
            float wobble = taut ? 0f : 0.18f * Mathf.Clamp01(len / 6f);
            for (int i = 0; i <= n; i++)
            {
                float u = i / (float)n, env = Mathf.Sin(u * Mathf.PI);
                _pts.Add(hand + d * u + side * (Mathf.Sin(u * 14f - Time.time * 30f) * wobble * env));
            }
            ArcanaVfx.SetLine(_rope, _pts);
            _head.SetPositionAndRotation(_tip, Quaternion.LookRotation(_fx.ended >= 0 ? -dir : (taut ? dir : _fx.dir)));
        }
    }

    // ====================================================================== Necromancy, The Devil

    public static class NecroVfx
    {
        /// <summary>Sickly green symbols pouring off the caster's hands and the corpse while it's raised (stops if they're knocked down).</summary>
        public static void Raising(ulong caster, ulong corpse, double start)
        {
            Func<bool> alive = () =>
            {
                var c = WorldNet.FindCombatant(caster);
                return c != null && c.IsAlive && !WorldNet.Fizzled(caster, start, WorldNet.Now) && WorldNet.Now < start + SpellPlans.ReviveTime;
            };
            Func<Vector3> hands = () =>
            {
                var c = WorldNet.FindCombatant(caster);
                return c != null ? (SpellVisuals.RightHand(c) + SpellVisuals.LeftHand(c)) * 0.5f : Vector3.zero;
            };
            Func<Vector3> body = () =>
            {
                var c = WorldNet.FindCombatant(corpse);
                return c != null ? SpellServer.CorpsePoint(c) : Vector3.zero;
            };
            RuneSpew.Create(hands, alive, SpellPlans.ReviveTime, SpellPlans.NecroGlow, 14f);
            RuneSpew.Create(body, alive, SpellPlans.ReviveTime, SpellPlans.NecroGlow, 18f, new Vector3(0.4f, 0.2f, 0.4f));
        }

        /// <summary>Skinwalker: grey symbols pouring off the caster and the corpse (stops if they're knocked down).</summary>
        public static void Skinning(ulong caster, ulong corpse, double start)
        {
            var dead = WorldNet.FindCombatant(corpse);
            if (dead != null) dead.HoldCorpse(SpellPlans.SkinTime + 0.6f);
            Func<bool> alive = () =>
            {
                var c = WorldNet.FindCombatant(caster);
                return c != null && c.IsAlive && !WorldNet.Fizzled(caster, start, WorldNet.Now) && WorldNet.Now < start + SpellPlans.SkinTime;
            };
            Func<Vector3> me = () =>
            {
                var c = WorldNet.FindCombatant(caster);
                return c != null ? c.transform.position + Vector3.down * 0.1f : Vector3.zero;
            };
            Func<Vector3> body = () =>
            {
                var c = WorldNet.FindCombatant(corpse);
                return c != null ? SpellServer.CorpsePoint(c) : Vector3.zero;
            };
            RuneSpew.Create(me, alive, SpellPlans.SkinTime, SpellPlans.SkinGrey, 18f, new Vector3(0.35f, 0.5f, 0.35f));
            RuneSpew.Create(body, alive, SpellPlans.SkinTime, SpellPlans.SkinGrey, 18f, new Vector3(0.4f, 0.2f, 0.4f));
        }

        /// <summary>Skinwalker's done: a grey burst off each, the corpse shrinking away as the caster comes up in its skin.</summary>
        public static void Skinned(ulong caster, ulong corpse)
        {
            var burst = new EnergyBurst.Settings
            {
                flashSize = 1.5f, flashTime = 0.2f, sparkCount = 14, sparkSize = 0.3f, sparkSpeed = new Vector2(3f, 8f), sparkLife = 0.4f, colour = SpellPlans.SkinGrey,
            };
            var dead = WorldNet.FindCombatant(corpse);
            if (dead != null)
            {
                EnergyBurst.Create(SpellServer.CorpsePoint(dead), burst);
                dead.ConsumeCorpse();
            }
            var me = WorldNet.FindCombatant(caster);
            if (me == null) return;
            EnergyBurst.Create(me.transform.position, burst);
            CameraRig.ShakeFrom(me.transform.position, 0.2f, 0.2f);
        }

        /// <summary>A body gets up as one of the Undead: a green burst.</summary>
        public static void RiseBurst(Vector3 p)
        {
            if (Debris.Suppressed) return;
            EnergyBurst.Create(p, new EnergyBurst.Settings
            {
                flashSize = 1.6f, flashTime = 0.2f, sparkCount = 14, sparkSize = 0.3f, sparkSpeed = new Vector2(3f, 8f), sparkLife = 0.4f, colour = SpellPlans.NecroGlow,
            });
            CameraRig.ShakeFrom(p, 0.12f, 0.15f);
        }

        /// <summary>The ground bursting open where a zombie claws its way out.</summary>
        public static void GroundBurst(Vector3 p)
        {
            for (int i = 0; i < 4; i++) SpellFx.Puff(p + Random.insideUnitSphere * 0.4f + Vector3.up * 0.2f, new Color(0.5f, 0.42f, 0.32f), 0.9f);
            EnergyBurst.Create(p + Vector3.up * 0.3f, new EnergyBurst.Settings
            {
                flashSize = 0.9f, flashTime = 0.15f, sparkCount = 10, sparkSize = 0.25f, sparkSpeed = new Vector2(2f, 6f), sparkLife = 0.35f, colour = SpellPlans.NecroGlow,
            });
            CameraRig.ShakeFrom(p, 0.1f, 0.2f);
        }

        /// <summary>A bite landing: a snap of green.</summary>
        public static void Chomp(Vector3 p)
        {
            EnergyBurst.Create(p, new EnergyBurst.Settings
            {
                flashSize = 0.8f, flashTime = 0.12f, sparkCount = 8, sparkSize = 0.22f, sparkSpeed = new Vector2(3f, 7f), sparkLife = 0.25f, colour = SpellPlans.NecroGlow,
            });
            CameraRig.ShakeFrom(p, 0.15f, 0.12f);
        }

        /// <summary>The Devil's price: drops of blood spilling off the body.</summary>
        public static void Bleed(ulong who)
        {
            var c = WorldNet.FindCombatant(who);
            if (c == null) return;
            var go = new GameObject("Bleed") { layer = Layers.Fx };
            go.AddComponent<BleedDrops>().Begin(c);
        }

        class BleedDrops : MonoBehaviour
        {
            class Drop { public Transform t; public Vector3 v; public float age, life, size; }
            readonly List<Drop> _drops = new();
            Combatant _c;
            float _t, _owed;

            public void Begin(Combatant c) => _c = c;

            void LateUpdate()
            {
                float dt = Time.deltaTime;
                _t += dt;
                if (_t < 0.6f && _c != null)
                {
                    _owed += 40f * dt;
                    Vector3 chest = _c.transform.position + Vector3.up * 0.3f;
                    while (_owed >= 1f)
                    {
                        _owed -= 1f;
                        var t = Glow.Piece(transform, Glow.SphereMesh, ArcanaVfx.Blood, "Drop");
                        t.position = chest + Random.insideUnitSphere * 0.25f;
                        _drops.Add(new Drop { t = t, v = Random.insideUnitSphere * 2f + Vector3.up * 1.5f, life = Random.Range(0.4f, 0.7f), size = Random.Range(0.06f, 0.12f) });
                    }
                }
                for (int i = _drops.Count - 1; i >= 0; i--)
                {
                    var d = _drops[i];
                    d.age += dt;
                    if (d.age >= d.life) { Destroy(d.t.gameObject); _drops.RemoveAt(i); continue; }
                    d.v += Vector3.down * 14f * dt;
                    d.t.position += d.v * dt;
                    float u = d.age / d.life;
                    d.t.localScale = new Vector3(1f, 1.4f, 1f) * (d.size * (1f - u * u));
                }
                if (_t > 0.6f && _drops.Count == 0) Destroy(gameObject);
            }
        }
    }

    // ====================================================================== on a body

    /// <summary>
    /// States drawn on a body from its replicated <see cref="BodyStatus"/>: a corpse turning green before it gets up as
    /// one of the Undead. (The Wheel's grey, gold and red glows are its outline: see PlayerOutlines.)
    /// </summary>
    public class StatusBodyFx : MonoBehaviour
    {
        Combatant _c;
        MaterialPropertyBlock _mpb;
        GameObject _tinted;
        Color _from = Color.white;

        void Awake() => _c = GetComponent<Combatant>();

        double _goldShown;
        bool _wasStrong;
        Transform _frost;
        readonly List<Transform> _shards = new();
        float _frostSize;

        static readonly Color GoldYoung = new(3.4f, 2.3f, 0.5f, 1f), GoldOld = new(3f, 2.8f, 1.8f, 1f);

        void LateUpdate()
        {
            if (_c == null || !_c.IsSpawned) return;
            var st = _c.Status.Value;
            double now = WorldNet.Now;
            // Wheel of Fortune's 6: a trail of golden afterimages while nothing can hurt you
            if (st.Gold(now) && _goldShown < st.GoldUntil && !Debris.Suppressed)
            {
                _goldShown = st.GoldUntil;
                SwiftView.Trail(_c.NetworkObjectId, now, st.GoldUntil, GoldYoung, GoldOld);
            }
            Frost(st.Slowed(now) && _c.IsAlive && (_c.Visible || _c.HasLiveRagdoll));
            Noose(st, now);
            // Strength: a burst as the body swells
            bool strong = st.Strong(now) && _c.IsAlive;
            if (strong && !_wasStrong && !Debris.Suppressed) { HierophantView.Spark(_c.transform.position + Vector3.up * 0.5f, 1.3f); CameraRig.ShakeFrom(_c.transform.position, 0.2f, 0.2f); }
            _wasStrong = strong;
            if (!_c.Dead.Value || st.InfectFrom <= 0 || _c.LastRagdoll == null) { _tinted = null; return; }
            // bitten to death: the body turns green before it gets up
            var body = _c.LastRagdoll;
            _mpb ??= new MaterialPropertyBlock();
            if (_tinted != body)
            {
                _tinted = body;
                _from = _c is PlayerController p ? p.PlayerColor : Color.white;
            }
            float u = Mathf.Clamp01((float)(now - st.InfectFrom) / SpellPlans.InfectTime);
            Color col = Color.Lerp(_from, SpellPlans.UndeadSkin, u);
            foreach (var r in body.GetComponentsInChildren<Renderer>())
            {
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor("_BaseColor", col);
                _mpb.SetColor("_Color", col);
                r.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>
        /// Slowed by Icy Breath: frosted over, with outlined shards of ice stuck all over the body (they grow in, and
        /// shrink away once it wears off, 2 s after the breath last touched them).
        /// </summary>
        void Frost(bool on)
        {
            _frostSize = Mathf.MoveTowards(_frostSize, on ? 1f : 0f, Time.deltaTime / (on ? 0.15f : 0.3f));
            if (_frostSize <= 0f)
            {
                if (_frost != null) { Destroy(_frost.gameObject); _frost = null; _shards.Clear(); }
                return;
            }
            if (_frost == null)
            {
                _frost = new GameObject("Frost") { layer = Layers.Fx }.transform;
                for (int i = 0; i < 14; i++)
                {
                    var s = Glow.Piece(_frost, i % 3 == 0 ? Glow.PrismMesh : Glow.CubeMesh, i % 2 == 0 ? ArcanaVfx.Ice : ArcanaVfx.Frost, "Shard");
                    Vector3 d = Random.onUnitSphere;
                    s.localPosition = new Vector3(d.x * 0.32f, Random.Range(-0.85f, 0.75f), d.z * 0.32f);
                    s.localRotation = Quaternion.LookRotation(new Vector3(d.x, Random.Range(-0.3f, 0.6f), d.z));
                    _shards.Add(s);
                }
            }
            Vector3 body = _c.RagdollFocus != null ? _c.RagdollFocus.position : _c.transform.position;
            _frost.SetPositionAndRotation(body, Quaternion.Euler(0f, _c.ViewYaw, 0f));
            _frost.localScale = Vector3.one * _c.transform.localScale.y;
            float s0 = _frostSize * _frostSize * (3f - 2f * _frostSize);
            for (int i = 0; i < _shards.Count; i++)
            {
                float k = 0.09f + 0.05f * ((i * 7) % 3);
                _shards[i].localScale = (i % 3 == 0 ? new Vector3(1f, 1f, 2.2f) : Vector3.one) * (k * s0);
            }
        }

        // ---------------- The Hanged Man's noose ----------------

        GameObject _noose;
        (LineRenderer core, LineRenderer edge) _rope, _loop;
        readonly List<Vector3> _ropePts = new(), _loopPts = new();
        float _ropeDown; // 0: all the way up in the sky, 1: down at the neck
        Vector3 _ropeTop;

        /// <summary>
        /// A rope coming down out of the sky to a loop round the neck (while the noose drops, then while hanging), and
        /// hauled back up into the sky when it's let go.
        /// </summary>
        void Noose(BodyStatus st, double now)
        {
            bool on = _c.IsAlive && st.HangUntil > now && now >= st.HangFrom - SpellPlans.HangedDrop;
            if (!on && _noose == null) return;
            Vector3 neck = SpellPlans.LoversChest(_c) + Vector3.up * 0.38f * _c.transform.localScale.y;
            if (on)
            {
                _ropeDown = Mathf.Clamp01(1f - (float)(st.HangFrom - now) / SpellPlans.HangedDrop);
                _ropeTop = neck + Vector3.up * SpellPlans.HangedRopeUp;
            }
            else _ropeDown = Mathf.MoveTowards(_ropeDown, 0f, Time.deltaTime / 0.3f);
            if (!on && _ropeDown <= 0f) { Destroy(_noose); _noose = null; return; }
            if (_noose == null)
            {
                _noose = new GameObject("Noose") { layer = Layers.Fx };
                _rope = ArcanaVfx.Line(_noose.transform, SpellPlans.Rope, 0.07f);
                _loop = ArcanaVfx.Line(_noose.transform, SpellPlans.Rope, 0.06f);
            }
            // (held on the neck while hanging; let go, it slides back up from where the neck was)
            Vector3 bottom = Vector3.Lerp(_ropeTop, on ? neck : _ropeTop + Vector3.down * SpellPlans.HangedRopeUp, _ropeDown);
            _ropePts.Clear();
            _ropePts.Add(_ropeTop);
            _ropePts.Add(bottom);
            ArcanaVfx.SetLine(_rope, _ropePts);
            // the loop round the neck, once it's there
            _loopPts.Clear();
            if (on && _ropeDown >= 1f)
                for (int i = 0; i <= 14; i++)
                {
                    float a = i / 14f * Mathf.PI * 2f;
                    _loopPts.Add(neck + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.14f + Vector3.up * (0.05f * Mathf.Sin(a)));
                }
            ArcanaVfx.SetLine(_loop, _loopPts);
        }

        void OnDestroy()
        {
            if (_frost != null) Destroy(_frost.gameObject);
            if (_noose != null) Destroy(_noose);
        }
    }
}
