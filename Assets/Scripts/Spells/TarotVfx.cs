using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>Tarot colours and shared shapes.</summary>
    public static class TarotVfx
    {
        public static readonly Color Mahogany = new(2.2f, 0.7f, 0.32f, 1f);
        public static readonly Color Gold = new(3f, 2.1f, 0.45f, 1f);
        /// <summary>Soft pale blue, only just over 1 so the Magician's bubbles barely bloom.</summary>
        public static readonly Color Bubble = new(0.8f, 1.25f, 1.5f, 1f);

        public static List<Vector2> Ring(float r, float startDeg, int steps)
        {
            var pts = new List<Vector2>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                float a = (startDeg + 360f * i / steps) * Mathf.Deg2Rad;
                pts.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
            }
            return pts;
        }

        /// <summary>A body's head (falls back to above its centre).</summary>
        public static Vector3 Head(Combatant c)
        {
            if (c == null) return Vector3.zero;
            var focus = c.RagdollFocus;
            if (focus != null) return focus.position + Vector3.up * 0.6f;
            if (c.Bones != null && c.Bones.TryGetValue("Bone.001_end", out var h) && h != null) return h.position;
            return c.transform.position + Vector3.up * 0.9f;
        }
    }

    // ====================================================================== The High Priestess

    /// <summary>
    /// A mahogany sun over the caster's head for a second (rays turning, pulsing faster towards the end), then a
    /// flash. If the local player has the caster on their screen during the flash, their view goes white.
    /// </summary>
    public class PriestessView : MonoBehaviour
    {
        ulong _caster;
        double _start;
        Transform _sun;
        readonly List<Transform> _rays = new();
        bool _flashed, _checked;
        float _fizzledAt = -1f;

        public static void Create(ulong caster, double start)
        {
            var go = new GameObject("PriestessSun") { layer = Layers.Fx };
            var v = go.AddComponent<PriestessView>();
            v._caster = caster;
            v._start = start;
            v._sun = Glow.Piece(go.transform, Glow.SphereMesh, TarotVfx.Mahogany, "Sun");
            for (int i = 0; i < 8; i++)
            {
                var holder = new GameObject("Ray") { layer = Layers.Fx }.transform;
                holder.SetParent(go.transform, false);
                holder.localRotation = Quaternion.Euler(0f, 0f, i * 45f) * Quaternion.Euler(-90f, 0f, 0f);
                var p = Glow.Piece(holder, Glow.PrismMesh, TarotVfx.Mahogany, "Prism");
                p.localPosition = new Vector3(0f, 0f, 0.75f);
                v._rays.Add(holder);
            }
            go.transform.localScale = Vector3.zero;
        }

        void LateUpdate()
        {
            var c = WorldNet.FindCombatant(_caster);
            float t = (float)(WorldNet.Now - _start);
            if (c == null || !c.IsAlive && !_flashed) { Destroy(gameObject); return; }

            // over the head, facing the camera
            transform.position = TarotVfx.Head(c) + Vector3.up * 0.55f;
            var cam = Camera.main;
            if (cam != null) transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);

            float delay = SpellPlans.PriestessDelay;
            // the caster was knocked down before the flash: the sun shrinks away, nobody is blinded
            if (_fizzledAt < 0f && !_flashed && WorldNet.Fizzled(_caster, _start, _start + delay)) _fizzledAt = t;
            if (_fizzledAt >= 0f)
            {
                float s = 1f - Mathf.Clamp01((t - _fizzledAt) / 0.2f);
                if (s <= 0f) { Destroy(gameObject); return; }
                transform.localScale = Vector3.one * (0.28f * s);
                return;
            }
            if (t < delay)
            {
                float grow = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / 0.2f), 3f);
                float pulse = 1f + 0.12f * Mathf.Sin(t * Mathf.Lerp(10f, 45f, t / delay));
                transform.localScale = Vector3.one * (0.28f * grow * pulse);
                _sun.localScale = Vector3.one * 0.9f;
                for (int i = 0; i < _rays.Count; i++)
                {
                    _rays[i].localRotation = Quaternion.Euler(0f, 0f, i * 45f + t * 90f) * Quaternion.Euler(-90f, 0f, 0f);
                    _rays[i].localScale = new Vector3(0.6f, 0.6f, 0.8f + 0.25f * Mathf.Sin(t * 20f + i));
                }
                return;
            }

            if (!_flashed)
            {
                _flashed = true;
                var p = transform.position;
                EnergyBurst.Create(p, new EnergyBurst.Settings
                {
                    flashSize = 3.2f, flashTime = 0.18f, sparkCount = 16, sparkSize = 0.4f,
                    sparkSpeed = new Vector2(7f, 14f), sparkLife = 0.35f, colour = new Color(4f, 3.4f, 2.6f, 1f),
                });
                CameraRig.ShakeFrom(p, 0.15f, 0.2f);
            }
            // anyone looking at the caster during the flash is blinded
            if (!_checked && t <= delay + SpellPlans.PriestessFlash) TryBlind(c);
            else _checked = true;

            // the sun bursts and shrinks away
            float u = Mathf.Clamp01((t - delay) / 0.25f);
            transform.localScale = Vector3.one * (0.28f * (1f + u) * (1f - u));
            if (u >= 1f && t > delay + SpellPlans.PriestessFlash) Destroy(gameObject);
        }

        void TryBlind(Combatant caster)
        {
            var me = PlayerController.Local;
            var cam = Camera.main;
            if (me == null || cam == null || !me.IsAlive || me.NetworkObjectId == _caster) return;
            // "on your screen": in front of the camera and inside the view (any distance)
            Vector3 vp = cam.WorldToViewportPoint(caster.transform.position + Vector3.up * 0.3f);
            if (vp.z <= 0f || vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) return;
            _checked = true;
            BlindOverlay.Blind(SpellPlans.BlindFade);
        }
    }

    /// <summary>The local player's view replaced with white, fading out.</summary>
    public class BlindOverlay : MonoBehaviour
    {
        static BlindOverlay _instance;
        Image _image;
        float _t, _time;

        public static void Blind(float seconds)
        {
            if (_instance == null)
            {
                var go = new GameObject("BlindOverlay");
                DontDestroyOnLoad(go);
                var canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 30000;
                _instance = go.AddComponent<BlindOverlay>();
                var img = new GameObject("White").AddComponent<Image>();
                img.transform.SetParent(go.transform, false);
                img.raycastTarget = false;
                var rt = img.rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                _instance._image = img;
            }
            _instance._time = Mathf.Max(0.01f, seconds);
            _instance._t = 0f;
            _instance.gameObject.SetActive(true);
            _instance.Update();
        }

        /// <summary>A new round, or the player died: see again.</summary>
        public static void Clear()
        {
            if (_instance != null) _instance.gameObject.SetActive(false);
        }

        void Update()
        {
            _t += Time.deltaTime;
            // pure white for a moment, then slowly back
            float a = 1f - Mathf.Clamp01((_t - 0.15f) / _time);
            _image.color = new Color(1f, 1f, 1f, a * a * (3f - 2f * a));
            if (_t > _time + 0.2f) gameObject.SetActive(false);
        }
    }

    // ====================================================================== The Empress / The Emperor

    /// <summary>
    /// A circle on the ground bordered by arcane symbols: mahogany for the Empress's domain (stays where it was
    /// cast), gold for the Emperor's aura (follows him). Grows out quickly, turns slowly, shrinks away at the end.
    /// </summary>
    public class DomainView : MonoBehaviour
    {
        WorldNet.DomainFx _fx;
        RuneDrawing _circle;
        Transform _circleT;
        readonly List<Transform> _motes = new();
        Color _colour;

        public static DomainView Create(WorldNet.DomainFx fx)
        {
            var go = new GameObject(fx.kind == 0 ? "EmpressDomain" : "EmperorAura") { layer = Layers.Fx };
            go.transform.position = fx.center;
            var v = go.AddComponent<DomainView>();
            v._fx = fx;
            v._colour = fx.kind == 0 ? TarotVfx.Mahogany : TarotVfx.Gold;
            var c = new GameObject("Circle") { layer = Layers.Fx };
            c.transform.SetParent(go.transform, false);
            c.transform.localPosition = Vector3.up * 0.07f;
            v._circleT = c.transform;
            v._circle = new RuneDrawing(c);
            v._circle.AddRune(Strokes(), SpellPlans.DomainRadius(fx.kind) * 2f, 0.09f);
            v._circle.Progress = 1f;
            v._circle.SetColour(v._colour);
            c.transform.localScale = Vector3.zero;
            if (WorldNet.Now < fx.start && !Debris.Suppressed)
            {
                // the startup: energy gathers into the clasped hands and symbols pour out of them
                ulong caster = fx.caster;
                float left = Mathf.Max(0.05f, (float)(fx.start - WorldNet.Now));
                System.Func<Vector3> hands = () =>
                {
                    var cc = WorldNet.FindCombatant(caster);
                    return cc != null ? (SpellVisuals.RightHand(cc) + SpellVisuals.LeftHand(cc)) * 0.5f : go.transform.position;
                };
                System.Func<bool> alive = () => { var cc = WorldNet.FindCombatant(caster); return cc != null && cc.IsAlive; };
                v._charge = ArcaneCharge.Create(hands, alive, left, v._colour, true);
                v._spew = RuneSpew.Create(hands, alive, left, v._colour);
            }
            v._startupDone = WorldNet.Now >= fx.start;
            return v;
        }

        ArcaneCharge _charge;
        RuneSpew _spew;
        bool _startupDone;

        /// <summary>The startup ends: the domain bursts out of the hands, or (hurt / thrown) it fizzles.</summary>
        void FinishStartup(bool cancelled)
        {
            _startupDone = true;
            if (cancelled)
            {
                if (_charge != null) _charge.Cancel();
                if (_spew != null) _spew.Stop();
                var me = PlayerController.Local;
                if (me != null && me.NetworkObjectId == _fx.caster && me.Caster != null) me.Caster.DomainStartupCancelled();
                return;
            }
            if (_charge != null)
            {
                CameraRig.ShakeFrom(_charge.transform.position, 0.12f, 0.2f);
                _charge.Release();
            }
            if (_spew != null) _spew.Release();
        }

        /// <summary>Double ring with a band of runes (tops outward) between, in unit coordinates.</summary>
        static List<List<Vector2>> Strokes()
        {
            var s = new List<List<Vector2>> { TarotVfx.Ring(1f, 0f, 80), TarotVfx.Ring(0.84f, 0f, 72) };
            const int n = 14;
            for (int i = 0; i < n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2f;
                Vector2 radial = new(Mathf.Cos(a), Mathf.Sin(a)), tangent = new(-radial.y, radial.x);
                foreach (var line in RuneGlyphs.Rune(i))
                {
                    var l = new List<Vector2>(line.Count);
                    foreach (var p in line) l.Add(radial * 0.92f + (tangent * -p.x + radial * p.y) * 0.055f);
                    s.Add(l);
                }
            }
            return s;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            if (!_startupDone)
            {
                if (_fx.ended >= 0 && _fx.ended < _fx.start) FinishStartup(true);
                else if (t >= 0f) FinishStartup(false);
            }
            if (_fx.kind == 1)
            {
                var c = WorldNet.FindCombatant(_fx.caster);
                if (c != null) transform.position = c.FeetPosition;
            }
            float scale;
            if (_fx.ended >= 0)
            {
                float u = Mathf.Clamp01((float)(now - _fx.ended) / 0.3f);
                if (u >= 1f || t < 0f) { Destroy(gameObject); return; }
                scale = (1f - u) * (1f - u);
            }
            else if (t < 0f) scale = 0f; // the hands are still coming together
            else scale = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / SpellPlans.DomainGrow), 3f);
            _circleT.localScale = Vector3.one * Mathf.Max(0.001f, scale);
            _circleT.localRotation = Quaternion.Euler(0f, t * (_fx.kind == 0 ? 10f : -18f), 0f);

            // motes drifting up off the border
            if (scale > 0.9f && !Debris.Suppressed && Random.value < 0.35f)
            {
                float a = Random.value * Mathf.PI * 2f;
                var m = Glow.Piece(transform, Glow.CubeMesh, _colour, "Mote");
                float rad = SpellPlans.DomainRadius(_fx.kind);
                m.localPosition = new Vector3(Mathf.Cos(a) * rad, 0.1f, Mathf.Sin(a) * rad);
                m.localScale = new Vector3(0.06f, 0.35f, 0.06f);
                _motes.Add(m);
            }
            for (int i = _motes.Count - 1; i >= 0; i--)
            {
                var m = _motes[i];
                m.localPosition += Vector3.up * (2.2f * Time.deltaTime);
                m.localScale *= Mathf.Exp(-1.6f * Time.deltaTime);
                if (m.localPosition.y > 2.2f) { Destroy(m.gameObject); _motes.RemoveAt(i); }
            }
        }

        void OnDestroy() => _circle?.Release();
    }

    // ====================================================================== on a player's body

    /// <summary>
    /// Tarot effects that ride on a player, drawn by every client from replicated state: soft bubbles while The
    /// Magician runs, mahogany shackle rings and a chain back to her while trapped by The Empress.
    /// </summary>
    public class TarotBodyFx : MonoBehaviour
    {
        Combatant _pc;
        class Bubble { public Transform t; public Vector3 offset; public float age, life, size; }
        readonly List<Bubble> _bubbles = new();
        float _owed;
        GameObject _shackles;
        RuneDrawing _ringA, _ringB;

        void Awake() => _pc = GetComponent<Combatant>();

        void LateUpdate()
        {
            if (_pc == null || !_pc.IsSpawned) return;
            bool alive = _pc.IsAlive && (_pc.Visible || _pc.HasLiveRagdoll);
            Vector3 body = _pc.RagdollFocus != null ? _pc.RagdollFocus.position : _pc.transform.position;
            float dt = Time.deltaTime;

            // The Magician: a few soft see-through bubbles drifting up round the body
            if (alive && _pc is PlayerController player && player.Mana.Value.Fast(WorldNet.Now))
            {
                _owed += 6f * dt;
                while (_owed >= 1f)
                {
                    _owed -= 1f;
                    Vector2 r = Random.insideUnitCircle.normalized * Random.Range(0.3f, 0.5f);
                    var t = MakeBubble();
                    var b = new Bubble { t = t, offset = new Vector3(r.x, Random.Range(-0.8f, 0.1f), r.y), life = Random.Range(0.8f, 1.3f), size = Random.Range(0.07f, 0.13f) };
                    t.position = body + b.offset;
                    _bubbles.Add(b);
                }
            }
            for (int i = _bubbles.Count - 1; i >= 0; i--)
            {
                var b = _bubbles[i];
                b.age += dt;
                if (b.age >= b.life + PopTime) { Destroy(b.t.gameObject); _bubbles.RemoveAt(i); continue; }
                b.offset += new Vector3(Mathf.Sin(b.age * 6f + i) * 0.15f, 0.9f, Mathf.Cos(b.age * 5f + i) * 0.1f) * dt;
                b.t.position = body + b.offset;
                // grow in, wobble a little, then pop: a quick swell and gone (no flash)
                float s = b.size * Mathf.Min(1f, b.age * 6f);
                float wob = 0.06f * Mathf.Sin(b.age * 14f + i);
                if (b.age > b.life) { float u = (b.age - b.life) / PopTime; s *= (1f + 0.5f * u) * (1f - u); }
                b.t.localScale = new Vector3(1f + wob, 1f - wob, 1f + wob) * s;
            }

            // The Empress: shackle rings round the trapped body, chained to her
            bool trapped = alive && _pc.Trapped;
            UpdateChain(trapped, body, dt);
            if (trapped && _shackles == null)
            {
                _shackles = new GameObject("Shackles") { layer = Layers.Fx };
                _ringA = MakeRing(_shackles.transform, 0.45f);
                _ringB = MakeRing(_shackles.transform, 0.55f);
            }
            if (_shackles != null)
            {
                if (!trapped) { _ringA.Release(); _ringB.Release(); Destroy(_shackles); _shackles = null; return; }
                _shackles.transform.position = body;
                float time = Time.time;
                _ringA.Host.localPosition = Vector3.up * (-0.75f + 0.05f * Mathf.Sin(time * 3f));
                _ringA.Host.localRotation = Quaternion.Euler(0f, time * 70f, 0f);
                _ringB.Host.localPosition = Vector3.up * (0.05f + 0.05f * Mathf.Sin(time * 3f + 1.5f));
                _ringB.Host.localRotation = Quaternion.Euler(0f, -time * 55f, 0f);
            }
        }

        // ---------------- the Empress's chain ----------------

        Transform _chainRoot;
        readonly List<Transform> _links = new();
        float _chainGrow, _taut;
        bool _chainShown;
        Vector3 _chainFrom, _chainTo;
        const float LinkStep = 0.2f, ChainGrowTime = 0.2f;

        /// <summary>
        /// A mahogany chain from the Empress to this trapped player: slack while they're well inside, taut at her
        /// edge (where it hauls them back). Shoots out from her when they're caught, shrinks away when they're freed.
        /// </summary>
        void UpdateChain(bool trapped, Vector3 body, float dt)
        {
            _chainShown = false;
            var net = WorldNet.Instance;
            if (trapped && net != null && net.Domains.TryGetValue(_pc.TrappedBy.Value, out var fx) && fx.ended < 0)
            {
                var c = WorldNet.FindCombatant(fx.caster);
                if (c != null && c.IsAlive)
                {
                    _chainShown = true;
                    _chainFrom = c.RagdollFocus != null ? c.RagdollFocus.position : c.transform.position + Vector3.up * 0.1f;
                    _chainTo = body;
                    Vector3 o = body - fx.center;
                    _taut = Mathf.Clamp01(new Vector2(o.x, o.z).magnitude / (SpellPlans.EmpressRadius - SpellPlans.ChainSlack));
                }
            }
            _chainGrow = Mathf.MoveTowards(_chainGrow, _chainShown ? 1f : 0f, dt / ChainGrowTime);
            if (_chainGrow <= 0f)
            {
                if (_chainRoot != null) { Destroy(_chainRoot.gameObject); _chainRoot = null; _links.Clear(); }
                return;
            }
            if (_chainRoot == null) _chainRoot = new GameObject("EmpressChain") { layer = Layers.Fx }.transform;

            Vector3 span = _chainTo - _chainFrom;
            float len = span.magnitude;
            int n = Mathf.Clamp(Mathf.CeilToInt(len / LinkStep), 1, 150);
            while (_links.Count < n) _links.Add(Glow.Piece(_chainRoot, Glow.SphereMesh, TarotVfx.Mahogany, "Link"));
            float sag = len * Mathf.Lerp(0.12f, 0.01f, _taut * _taut);
            Vector3 Point(float u) => _chainFrom + span * u + Vector3.down * (sag * 4f * u * (1f - u));
            for (int i = 0; i < _links.Count; i++)
            {
                var l = _links[i];
                bool on = i < n;
                if (l.gameObject.activeSelf != on) l.gameObject.SetActive(on);
                if (!on) continue;
                float u0 = i / (float)n, u1 = (i + 1) / (float)n;
                // growing: links appear out from her; going: they all shrink together
                float s = _chainShown ? (u1 <= _chainGrow + 1e-3f ? 1f : 0f) : _chainGrow;
                Vector3 a = Point(u0), b = Point(u1), d = b - a;
                if (d.sqrMagnitude < 1e-6f) d = Vector3.forward * 1e-3f;
                l.SetPositionAndRotation((a + b) * 0.5f, Quaternion.LookRotation(d) * Quaternion.Euler(0f, 0f, (i & 1) == 0 ? 0f : 90f));
                l.localScale = new Vector3(0.05f, 0.1f, d.magnitude * 1.3f) * s;
            }
        }

        const float PopTime = 0.07f;
        static Material _bubbleMat;

        /// <summary>A see-through toon bubble (its own ink edge; no solid hull, which would hide the see-through).</summary>
        static Transform MakeBubble()
        {
            if (_bubbleMat == null)
            {
                var s = Shader.Find("SME/Bubble");
                _bubbleMat = s != null ? new Material(s) { name = "Bubble" } : Glow.Shared;
                if (s != null) _bubbleMat.SetColor("_BaseColor", TarotVfx.Bubble);
            }
            var go = new GameObject("Bubble") { layer = Layers.Fx };
            go.AddComponent<MeshFilter>().sharedMesh = Glow.SphereMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _bubbleMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        static RuneDrawing MakeRing(Transform parent, float radius)
        {
            var go = new GameObject("Ring") { layer = Layers.Fx };
            go.transform.SetParent(parent, false);
            var d = new RuneDrawing(go);
            var strokes = new List<List<Vector2>> { TarotVfx.Ring(1f, 0f, 40) };
            // a few runes standing on the ring
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f;
                Vector2 radial = new(Mathf.Cos(a), Mathf.Sin(a)), tangent = new(-radial.y, radial.x);
                var l = new List<Vector2>();
                foreach (var p in RuneGlyphs.Rune(i)[0]) l.Add(radial * 1.15f + (tangent * -p.x + radial * p.y) * 0.12f);
                strokes.Add(l);
            }
            d.AddRune(strokes, radius * 2f, 0.05f);
            d.Progress = 1f;
            d.SetColour(TarotVfx.Mahogany);
            return d;
        }

        void OnDestroy()
        {
            foreach (var b in _bubbles) if (b.t != null) Destroy(b.t.gameObject);
            if (_shackles != null) { _ringA?.Release(); _ringB?.Release(); Destroy(_shackles); }
            if (_chainRoot != null) Destroy(_chainRoot.gameObject);
        }
    }
}
