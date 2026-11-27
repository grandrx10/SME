using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Colours and shared bits of the Blade cards' effects.</summary>
    public static class BladeVfx
    {
        /// <summary>The slashes: hot white, well above 1 so they bloom.</summary>
        public static readonly Color White = new(4.2f, 4.2f, 4.6f, 1f);
    }

    // ====================================================================== the sword

    /// <summary>
    /// The sword (Resources/VFX/Sword, the user's ProBuilder model) rebuilt once into a clean template: its meshes
    /// copied out (ProBuilder builds them on load), no colliders, every part on the SME/ToonFade shader so it can
    /// manifest and vanish in a dither. The template's origin is the prefab's "Handle" (where the hand grips it);
    /// the blade runs along +Y and its edge faces ±Z.
    /// </summary>
    public static class SwordModel
    {
        static GameObject _template;
        static readonly Dictionary<Material, Material> Faded = new();

        public static GameObject Spawn()
        {
            var t = Template();
            var go = Object.Instantiate(t);
            go.name = "Sword";
            go.SetActive(true);
            return go;
        }

        static GameObject Template()
        {
            if (_template != null) return _template;
            _template = new GameObject("Sword (template)") { layer = Layers.Fx, hideFlags = HideFlags.HideAndDontSave };
            _template.SetActive(false);
            Object.DontDestroyOnLoad(_template);

            var prefab = Resources.Load<GameObject>("VFX/Sword");
            if (prefab == null)
            {
                Debug.LogWarning("[SME] Resources/VFX/Sword is missing: the Blade cards use a plain stand-in sword.");
                Part(_template.transform, Glow.CubeMesh, null, Matrix4x4.TRS(new Vector3(0f, 0.7f, 0f), Quaternion.identity, new Vector3(0.03f, 1.15f, 0.07f)));
                Part(_template.transform, Glow.CubeMesh, null, Matrix4x4.TRS(new Vector3(0f, 0.13f, 0f), Quaternion.identity, new Vector3(0.05f, 0.03f, 0.22f)));
                Part(_template.transform, Glow.CubeMesh, null, Matrix4x4.TRS(new Vector3(0f, 0f, 0f), Quaternion.identity, new Vector3(0.05f, 0.27f, 0.05f)));
                return _template;
            }

            // instantiated (active) so ProBuilder builds the meshes, copied out, then thrown away
            var src = Object.Instantiate(prefab);
            var handle = src.transform.Find("Handle");
            Matrix4x4 toGrip = (handle != null ? handle : src.transform).worldToLocalMatrix;
            foreach (var mf in src.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                Part(_template.transform, Object.Instantiate(mf.sharedMesh), mr != null ? mr.sharedMaterials : null, toGrip * mf.transform.localToWorldMatrix);
            }
            Object.Destroy(src);
            return _template;
        }

        /// <summary>One piece of a weapon template: the mesh placed by <paramref name="m"/>, on SME/ToonFade (shared with the hammer).</summary>
        internal static void Part(Transform parent, Mesh mesh, Material[] mats, Matrix4x4 m)
        {
            var go = new GameObject("Part") { layer = Layers.Fx };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = m.GetColumn(3);
            go.transform.localRotation = m.rotation;
            go.transform.localScale = m.lossyScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (mats == null || mats.Length == 0) mats = new Material[] { null };
            var faded = new Material[mats.Length];
            for (int i = 0; i < mats.Length; i++) faded[i] = Fade(mats[i]);
            r.sharedMaterials = faded;
        }

        /// <summary>The same look on SME/ToonFade (colours and toon settings carried over).</summary>
        static Material Fade(Material from)
        {
            if (from != null && Faded.TryGetValue(from, out var m)) return m;
            var shader = Shader.Find("SME/ToonFade");
            m = shader != null ? new Material(shader) : new Material(from != null ? from : Glow.Shared);
            m.name = (from != null ? from.name : "Steel") + " (fade)";
            if (from == null) m.SetColor("_BaseColor", new Color(0.8f, 0.82f, 0.86f));
            else
            {
                foreach (var p in new[] { "_BaseColor", "_ShadowColor", "_RimColor" })
                    if (from.HasProperty(p) && m.HasProperty(p)) m.SetColor(p, from.GetColor(p));
                foreach (var p in new[] { "_Bands", "_BandSoftness", "_AmbientStrength", "_RimStrength", "_RimPower", "_SpecStrength", "_SpecSize" })
                    if (from.HasProperty(p) && m.HasProperty(p)) m.SetFloat(p, from.GetFloat(p));
                if (from.HasProperty("_BaseMap") && m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", from.GetTexture("_BaseMap"));
                Faded[from] = m;
            }
            return m;
        }
    }

    /// <summary>
    /// The sword in a body's right hand while it plays a Blade gesture: it manifests (dithers in), is held and swung
    /// the way the gesture says, flashes white as Flowing Cut charges, and dithers away when the gesture's done.
    /// </summary>
    [DefaultExecutionOrder(150)] // after the ProceduralAnimator has posed the hand this frame
    public class SwordView : MonoBehaviour
    {
        static readonly int FadeId = Shader.PropertyToID("_Fade");
        static readonly int FlashId = Shader.PropertyToID("_FlashColor");

        Combatant _c;
        GameObject _sword;
        Renderer[] _renderers;
        MaterialPropertyBlock _mpb;
        float _fade, _flash;
        int _charges;
        Quaternion _rot = Quaternion.identity;
        bool _placed;

        void Awake() => _c = GetComponent<Combatant>();

        void LateUpdate()
        {
            if (_c == null || !_c.IsSpawned) { Hide(); return; }
            var pose = _c.ViewPose;
            float t = _c.ViewPoseTime;
            bool want = _c.IsAlive && _c.Visible && Wanted(pose, t);
            float dt = Time.deltaTime;
            _fade = Mathf.MoveTowards(_fade, want ? 1f : 0f, dt / (want ? SpellPlans.SwordAppear : SpellPlans.SwordVanish));
            if (_fade <= 0f) { Hide(); return; }

            if (_sword == null)
            {
                _sword = SwordModel.Spawn();
                _renderers = _sword.GetComponentsInChildren<Renderer>(true);
                _mpb ??= new MaterialPropertyBlock();
                _placed = false;
                _charges = 0;
            }

            // Flowing Cut: a flash each time a charge builds (0.3 s, 0.6 s, 0.9 s)
            int charges = pose == SpellPose.FlowCharge ? SpellPlans.FlowCharges(t) : 0;
            if (charges > _charges)
            {
                _flash = 1f;
                EnergyBurst.Create(Tip(), new EnergyBurst.Settings
                {
                    flashSize = 0.45f + 0.15f * charges, flashTime = 0.12f, sparkCount = 5 + 2 * charges, sparkSize = 0.16f,
                    sparkSpeed = new Vector2(3f, 6f), sparkLife = 0.22f, colour = BladeVfx.White,
                });
            }
            _charges = charges;
            _flash = Mathf.MoveTowards(_flash, 0f, dt / 0.18f);

            // held in the right hand, pointed the way the gesture says; once it's over, it stays where it finished and
            // fades there (the hand goes back to rest straight away, and following it would jerk the sword across)
            if (want || !_placed)
            {
                // (Heavenly Slash: the body lies on its side in the air, the sword with it)
                Quaternion body = Quaternion.Euler(0f, _c.ViewYaw, 0f) * Quaternion.Euler(0f, 0f, SpellPlans.HeavenBodyRoll(pose, t));
                BladeAim(pose, t, out var blade, out var edge);
                Vector3 b = body * blade, e = body * edge;
                Quaternion want3 = Quaternion.LookRotation(Vector3.ProjectOnPlane(e, b).sqrMagnitude > 1e-4f ? Vector3.ProjectOnPlane(e, b) : body * Vector3.forward, b);
                _rot = _placed ? Quaternion.Slerp(_rot, want3, 1f - Mathf.Exp(-40f * dt)) : want3;
                _placed = true;
                _sword.transform.SetPositionAndRotation(SpellVisuals.RightHand(_c), _rot);
            }

            foreach (var r in _renderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetFloat(FadeId, _fade);
                _mpb.SetColor(FlashId, new Color(BladeVfx.White.r, BladeVfx.White.g, BladeVfx.White.b, _flash));
                r.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>The sword's tip in the world.</summary>
        Vector3 Tip() => _sword != null ? _sword.transform.position + _sword.transform.up * 1.25f : SpellVisuals.RightHand(_c);

        static bool Wanted(SpellPose pose, float t) => pose switch
        {
            SpellPose.SeveranceDash => t < SpellPlans.SeveranceTime + 0.2f,
            SpellPose.SeveranceSlash => t < BladeMotion.Sweep + BladeMotion.Settle,
            SpellPose.FlowCharge => true,
            SpellPose.FlowStrike => t < BladeMotion.Sweep + BladeMotion.Settle,
            SpellPose.HeavenCrouch or SpellPose.HeavenHover => true,
            SpellPose.HeavenSlash => t < BladeMotion.Sweep + BladeMotion.Settle,
            SpellPose.ParryGuard => t < SpellPlans.ParryWindow,
            SpellPose.ParryStab => t < 0.5f,
            _ => false,
        };

        /// <summary>Which way the blade points and its edge faces, in body space (x right, y up, z forward).</summary>
        static void BladeAim(SpellPose pose, float t, out Vector3 blade, out Vector3 edge)
        {
            var f = BladeMotion.Sample(pose, t);
            blade = f.blade;
            edge = f.edge;
        }

        void Hide()
        {
            if (_sword != null) Destroy(_sword);
            _sword = null;
            _fade = 0f;
            _flash = 0f;
            _charges = 0;
        }

        void OnDestroy() => Hide();
    }

    // ====================================================================== the cut itself

    /// <summary>
    /// The Blade cards' cut, shared by the arm gestures (ProceduralAnimator builds its keyframes from it) and the
    /// sword (SwordView points the blade with it), so hands and blade always agree. Body space: x right, y up, z forward.
    /// Held low on the left in both hands; then one fluid diagonal cut from bottom left to top right (eased over its
    /// whole length along a curved path, the body unwinding with it), and a short follow-through that settles.
    /// Flowing Cut does the same cut as it flashes along.
    /// </summary>
    public static class BladeMotion
    {
        public const float Sweep = 0.18f;     // the cut, bottom left to top right
        public const float Settle = 0.3f;     // the follow-through after it

        public struct Frame
        {
            public Vector3 hand, blade, edge, left; // right wrist, blade direction, edge direction, left wrist
            public float twist, drop, lean;
        }

        static readonly Vector3 LowHand = new(-0.24f, -0.36f, 0.22f), MidHand = new(0.02f, -0.02f, 0.56f), HighHand = new(0.34f, 0.36f, 0.32f);
        static readonly Vector3 LowBlade = new Vector3(-0.5f, -0.55f, -0.65f).normalized;  // trailing low behind, on the left
        static readonly Vector3 MidBlade = new Vector3(0.05f, 0.12f, 1f).normalized;       // straight out in front
        static readonly Vector3 HighBlade = new Vector3(0.5f, 0.8f, -0.15f).normalized;    // finishing high on the right

        /// <summary>The left hand on the grip, just below the right (towards the pommel).</summary>
        static Vector3 Grip(Vector3 hand, Vector3 blade) => hand - blade * 0.11f + Vector3.down * 0.02f;

        static Frame Held(bool kneeling) => new()
        {
            hand = LowHand, blade = LowBlade, edge = new Vector3(0.2f, -1f, 0.3f).normalized, left = Grip(LowHand, LowBlade),
            twist = -28f, drop = kneeling ? 0.44f : 0.14f, lean = kneeling ? 14f : 18f,
        };

        static float Smoother(float u) => u * u * u * (u * (u * 6f - 15f) + 10f);

        static Vector3 BladeAt(float e)
        {
            Vector3 a = Vector3.Slerp(LowBlade, MidBlade, e), b = Vector3.Slerp(MidBlade, HighBlade, e);
            return Vector3.Slerp(a, b, e).normalized;
        }

        /// <summary>The cut, <paramref name="t"/> seconds in (then its follow-through).</summary>
        static Frame Cut(float t, float drop0)
        {
            float e = Smoother(Mathf.Clamp01(t / Sweep));
            // a curved path through the front (a quadratic curve that passes through MidHand half way)
            Vector3 ctrl = 2f * MidHand - 0.5f * (LowHand + HighHand);
            Vector3 hand = (1f - e) * (1f - e) * LowHand + 2f * (1f - e) * e * ctrl + e * e * HighHand;
            Vector3 blade = BladeAt(e);
            Vector3 edge = BladeAt(Mathf.Min(1f, e + 0.06f)) - BladeAt(Mathf.Max(0f, e - 0.06f)); // the way it's moving
            // follow-through: drifting on a touch, easing to a stop
            float f = Mathf.Clamp01((t - Sweep) / Settle);
            float drift = Mathf.Sin(f * Mathf.PI * 0.5f);
            hand += new Vector3(0.05f, 0.04f, -0.07f) * drift;
            blade = Vector3.Slerp(blade, new Vector3(0.6f, 0.75f, -0.3f).normalized, drift * 0.5f);
            return new Frame
            {
                hand = hand, blade = blade, edge = edge.sqrMagnitude > 1e-5f ? edge.normalized : new Vector3(0.6f, 0.2f, -0.8f).normalized,
                left = Grip(hand, blade),
                twist = Mathf.Lerp(-28f, 24f, e) + 4f * drift, drop = Mathf.Lerp(drop0, 0.04f, e), lean = Mathf.Lerp(16f, 6f, e),
            };
        }

        static Frame Blend(Frame a, Frame b, float e) => new()
        {
            hand = Vector3.Lerp(a.hand, b.hand, e), blade = Vector3.Slerp(a.blade, b.blade, e), edge = Vector3.Slerp(a.edge, b.edge, e), left = Vector3.Lerp(a.left, b.left, e),
            twist = Mathf.Lerp(a.twist, b.twist, e), drop = Mathf.Lerp(a.drop, b.drop, e), lean = Mathf.Lerp(a.lean, b.lean, e),
        };

        /// <summary>Parry: on guard, the sword held in front point down, edge out, both hands on the grip at the chest.</summary>
        static Frame Guard(float t)
        {
            float e = Smoother(Mathf.Clamp01(t / 0.1f));
            Vector3 hand = new(0.1f, 0.06f, 0.4f), blade = new Vector3(0.12f, -1f, 0.3f).normalized;
            Frame from = Held(false);
            return new Frame
            {
                hand = Vector3.Lerp(from.hand, hand, e), blade = Vector3.Slerp(from.blade, blade, e), edge = Vector3.forward,
                left = Vector3.Lerp(from.left, hand + new Vector3(-0.05f, 0.08f, -0.03f), e),
                twist = Mathf.Lerp(from.twist, -6f, e), drop = 0.1f, lean = 6f,
            };
        }

        /// <summary>Parry's retaliation: lunging in, the sword thrust straight out in front.</summary>
        static Frame Stab()
        {
            Vector3 hand = new(0.12f, 0.02f, 0.62f), blade = new Vector3(-0.04f, 0.02f, 1f).normalized;
            return new Frame { hand = hand, blade = blade, edge = Vector3.down, left = new Vector3(-0.26f, -0.12f, -0.08f), twist = 22f, drop = 0.16f, lean = 22f };
        }

        public static Frame Sample(SpellPose pose, float t)
        {
            switch (pose)
            {
                case SpellPose.HeavenCrouch: return Held(true);
                case SpellPose.HeavenHover: return Blend(Held(true), Held(false), Smoother(Mathf.Clamp01(t / 0.3f))); // up out of the crouch into the sword by the side, as Severance's start (the body lies on its side)
                case SpellPose.HeavenSlash: return Cut(t, 0.04f);
                case SpellPose.ParryGuard: return Guard(t);
                case SpellPose.ParryStab: return Stab();
                case SpellPose.FlowCharge: return Held(true);
                case SpellPose.SeveranceSlash: return Cut(t, 0.14f);
                case SpellPose.FlowStrike: return Cut(t, 0.3f); // the same cut, done as it flashes along (rising out of the kneel)
                default: return Held(false); // Severance's rush
            }
        }

        /// <summary>The fingers' direction for a hand wrapped round a grip pointing along <paramref name="blade"/>.</summary>
        public static Vector3 FingerDir(Vector3 blade)
        {
            Vector3 d = Vector3.Cross(blade, Vector3.up);
            return d.sqrMagnitude > 1e-4f ? (d.normalized + Vector3.forward * 0.4f).normalized : Vector3.forward;
        }
    }

    // ====================================================================== slash frames

    /// <summary>
    /// The Last Chromatist's slash frame, in white: a thin lens-shaped streak flashed across a target along the cut,
    /// that lands full width, then stretches a little and thins away in a few held steps (drawn frames). Turned to
    /// face the camera each frame (its angle on screen is fixed when it lands), with an ink edge underneath.
    /// </summary>
    public class SlashFx : MonoBehaviour
    {
        const float Life = 0.16f, Length = 1.7f, Width = 0.2f, AngleJitter = 12f;
        const int Steps = 3;

        static Mesh _lens;
        Transform _glow, _ink;
        float _age, _angle, _scale;
        Vector3 _point;

        /// <summary>A slash across <paramref name="point"/>, cut along <paramref name="direction"/> (world).</summary>
        public static void Create(Vector3 point, Vector3 direction, float scale = 1f)
        {
            if (Debris.Suppressed) return;
            var cam = Camera.main;
            if (cam == null) return;
            var go = new GameObject("Slash") { layer = Layers.Fx };
            go.transform.position = point;
            var s = go.AddComponent<SlashFx>();
            // the angle is taken on screen: the cut has to line up with how the blade was seen to travel
            Vector3 a = cam.WorldToViewportPoint(point), b = cam.WorldToViewportPoint(point + direction.normalized);
            Vector2 sc = new((b.x - a.x) * cam.aspect, b.y - a.y);
            s._angle = (sc.sqrMagnitude > 1e-6f ? Mathf.Atan2(sc.y, sc.x) * Mathf.Rad2Deg : Random.Range(0f, 360f)) + Random.Range(-AngleJitter, AngleJitter);
            s._scale = scale;
            s._point = point;
            s._ink = Strip(go.transform, Glow.Underlay, Glow.Ink, "Ink");
            s._glow = Strip(go.transform, Glow.DoubleSided, BladeVfx.White, "Glow");
            s.LateUpdate();
        }

        static Transform Strip(Transform parent, Material m, Color c, string name)
        {
            var go = new GameObject(name) { layer = Layers.Fx };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = Lens;
            var r = go.AddComponent<MeshRenderer>();
            Glow.SetStrip(r, m);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            Glow.Tint(r, c);
            return go.transform;
        }

        /// <summary>A lens along x (-1..1): widest in the middle (half-height 1), tapering to points.</summary>
        static Mesh Lens
        {
            get
            {
                if (_lens != null) return _lens;
                const int n = 16;
                var v = new List<Vector3>();
                var t = new List<int>();
                for (int i = 0; i <= n; i++)
                {
                    float x = -1f + 2f * i / n, h = 1f - x * x;
                    v.Add(new Vector3(x, h, 0f));
                    v.Add(new Vector3(x, -h, 0f));
                    if (i == 0) continue;
                    int k = v.Count - 4;
                    t.Add(k); t.Add(k + 2); t.Add(k + 1);
                    t.Add(k + 1); t.Add(k + 2); t.Add(k + 3);
                }
                _lens = new Mesh { name = "Lens" };
                _lens.SetVertices(v);
                _lens.SetTriangles(t, 0);
                _lens.RecalculateBounds();
                return _lens;
            }
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            float p = _age / Life;
            var cam = Camera.main;
            if (p >= 1f || cam == null) { Destroy(gameObject); return; }
            // snapped between held steps: lands at full width, then stretches a little and thins away
            float k = Mathf.Floor(p * Steps) / (Steps - 1f);
            float len = Length * 0.5f * _scale * (1f + 0.35f * k);
            float wid = Width * 0.5f * _scale * (1f - 0.85f * k);
            Vector3 fwd = cam.transform.forward;
            Vector3 along = Quaternion.AngleAxis(_angle, -fwd) * cam.transform.right;
            transform.rotation = Quaternion.LookRotation(fwd, Vector3.Cross(fwd, along));
            // pulled a little towards the camera, so the body it cuts across doesn't hide it
            Vector3 toCam = cam.transform.position - _point;
            transform.position = _point + (toCam.sqrMagnitude > 1f ? toCam.normalized * 0.6f : Vector3.zero);
            _glow.localScale = new Vector3(len, wid, 1f);
            _ink.localScale = new Vector3(len + Glow.OutlineExtra, wid + Glow.OutlineExtra * 0.75f, 1f);
        }
    }

    // ====================================================================== Severance's cut

    /// <summary>
    /// Severance's cut in flight: a curved crescent, level (give or take its little tilt), that can only be seen as a
    /// ripple bending the scene behind it (SME/Distort). It pops out of the blade, flies 10 m and shrinks away there,
    /// or the moment it's spent on the voxels it cut.
    /// </summary>
    public class SeveranceView : MonoBehaviour
    {
        static readonly int VisibleId = Shader.PropertyToID("_Visible");
        static Mesh _mesh;
        static Material _mat;

        WorldNet.WaveFx _fx;
        Renderer _r;
        MaterialPropertyBlock _mpb;

        public static SeveranceView Create(WorldNet.WaveFx fx)
        {
            var go = new GameObject("Severance") { layer = Layers.Fx };
            go.AddComponent<MeshFilter>().sharedMesh = Crescent;
            var r = go.AddComponent<MeshRenderer>();
            _mat ??= new Material(Shader.Find("SME/Distort") ?? Shader.Find("Universal Render Pipeline/Unlit")) { name = "Distort" };
            r.sharedMaterial = _mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            var v = go.AddComponent<SeveranceView>();
            v._fx = fx;
            v._r = r;
            v._mpb = new MaterialPropertyBlock();
            go.transform.rotation = SpellPlans.WaveRotation(fx.dir, fx.tilt);
            go.transform.localScale = Vector3.zero;
            return v;
        }

        /// <summary>The crescent, flat in the wave's own XZ plane: its front edge is the cutting arc, thickest in the middle.</summary>
        static Mesh Crescent
        {
            get
            {
                if (_mesh != null) return _mesh;
                const int n = 28;
                const float thick = 0.9f;
                var v = new List<Vector3>();
                var uv = new List<Vector2>();
                var t = new List<int>();
                for (int i = 0; i <= n; i++)
                {
                    float s = -1f + 2f * i / n;
                    Vector3 front = SpellPlans.WaveArc(s);
                    Vector3 back = front - new Vector3(0f, 0f, thick * (1f - s * s));
                    v.Add(back); uv.Add(new Vector2((s + 1f) * 0.5f, 0f));
                    v.Add(front); uv.Add(new Vector2((s + 1f) * 0.5f, 1f));
                    if (i == 0) continue;
                    int k = v.Count - 4;
                    t.Add(k); t.Add(k + 1); t.Add(k + 2);
                    t.Add(k + 1); t.Add(k + 3); t.Add(k + 2);
                }
                _mesh = new Mesh { name = "Crescent" };
                _mesh.SetVertices(v);
                _mesh.SetUVs(0, uv);
                _mesh.SetTriangles(t, 0);
                _mesh.RecalculateBounds();
                return _mesh;
            }
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            float life = SpellPlans.SeveranceRange / SpellPlans.SeveranceSpeed;
            float end = _fx.ended >= 0 ? (float)(_fx.ended - _fx.start) : life;
            if (t > end + 0.15f) { Destroy(gameObject); return; }
            if (t < 0f) { transform.localScale = Vector3.zero; return; }
            transform.position = SpellPlans.WaveCenter(_fx.origin, _fx.dir, Mathf.Min(t, end));
            // pops out wide, then shrinks to nothing where it ends
            float grow = Mathf.Clamp01(t / 0.05f);
            float shrink = 1f - Mathf.Clamp01((t - end) / 0.15f);
            float tail = 1f - Mathf.Clamp01((t - (life - 0.08f)) / 0.08f) * 0.3f;
            float s = grow * shrink * tail;
            transform.localScale = new Vector3(s, 1f, Mathf.Max(0.05f, s));
            _r.GetPropertyBlock(_mpb);
            _mpb.SetFloat(VisibleId, s);
            _r.SetPropertyBlock(_mpb);
        }
    }

    // ====================================================================== Flowing Cut's dash

    /// <summary>
    /// Flowing Cut's passage: three level speed lines from where you knelt to where you landed (as on the card), hot
    /// white with an ink outline, thinning away from the start to the end.
    /// </summary>
    public class FlowStreak : MonoBehaviour
    {
        const float Life = 0.4f, Width = 0.08f;
        readonly List<(Transform t, float delay, float len)> _lines = new();
        float _age;

        public static void Create(Vector3 fromFeet, Vector3 toFeet)
        {
            if (Debris.Suppressed) return;
            Vector3 d = toFeet - fromFeet;
            if (d.sqrMagnitude < 0.04f) return;
            var go = new GameObject("FlowStreak") { layer = Layers.Fx };
            var s = go.AddComponent<FlowStreak>();
            Vector3 side = Vector3.Cross(Vector3.up, d.normalized);
            float[] heights = { 1.45f, 1.0f, 0.55f };
            float[] lengths = { 0.95f, 1f, 0.8f };
            for (int i = 0; i < 3; i++)
            {
                float len = d.magnitude * lengths[i];
                Vector3 a = toFeet - d.normalized * len;
                var line = Glow.Piece(go.transform, Glow.CubeMesh, BladeVfx.White, "Line");
                line.position = (a + toFeet) * 0.5f + Vector3.up * heights[i] + side * ((i - 1) * 0.12f);
                line.rotation = Quaternion.LookRotation(d.normalized);
                s._lines.Add((line, i * 0.04f, len));
            }
            s.Update();
            CameraRig.ShakeFrom(toFeet, 0.25f, 0.15f);
        }

        void Update()
        {
            _age += Time.deltaTime;
            if (_age >= Life + 0.1f) { Destroy(gameObject); return; }
            foreach (var (t, delay, len) in _lines)
            {
                float k = Mathf.Clamp01((_age - delay) / Life);
                float w = Width * (1f - k * k);
                t.localScale = new Vector3(w, w, len * (1f - 0.3f * k));
            }
        }
    }
}
