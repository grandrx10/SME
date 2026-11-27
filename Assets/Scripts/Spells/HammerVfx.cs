using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Colours of the hammer's effects.</summary>
    public static class HammerVfx
    {
        /// <summary>Impacts, sparks and the shockwave: hot white-gold, well above 1 so it blooms.</summary>
        public static readonly Color Hot = new(4.6f, 4.0f, 2.8f, 1f);
    }

    // ====================================================================== the hammer

    /// <summary>
    /// The hammer (Resources/VFX/Hammer, the user's ProBuilder model) rebuilt once into a clean template, the same
    /// way as the sword: meshes copied out, no colliders, every part on SME/ToonFade so it manifests and vanishes in
    /// a dither. The template's origin is half way between its "HandleR" and "HandleL" (where the two hands grip
    /// it); +Y runs up the handle to the head, +X is the way the head's striking face points.
    /// </summary>
    public static class HammerModel
    {
        static GameObject _template;
        static bool _measured;
        static float _halfSpacing = 0.205f, _headReach = 1.375f;

        /// <summary>Half the gap between the two hands on the handle (HandleR / HandleL in the prefab).</summary>
        public static float HalfSpacing { get { Measure(); return _halfSpacing; } }
        /// <summary>From the middle of the grip up the handle to the middle of the head.</summary>
        public static float HeadReach { get { Measure(); return _headReach; } }

        public static GameObject Spawn()
        {
            var go = Object.Instantiate(Template());
            go.name = "Hammer";
            go.SetActive(true);
            return go;
        }

        /// <summary>Where the hands go, from the prefab (no instantiating: the animator asks for this while it builds its keys).</summary>
        static void Measure()
        {
            if (_measured) return;
            _measured = true;
            var prefab = Resources.Load<GameObject>("VFX/Hammer");
            if (prefab == null) return;
            var root = prefab.transform;
            var r = root.Find("HandleR");
            var l = root.Find("HandleL");
            if (r == null || l == null) return;
            Grip(root.InverseTransformPoint(r.position), root.InverseTransformPoint(l.position), out var mid, out var axis);
            _halfSpacing = Vector3.Distance(r.position, l.position) * 0.5f;
            _headReach = Vector3.Dot(-mid, axis); // the head sits at the prefab's origin
        }

        /// <summary>The middle of the grip and the way up the handle (the right hand is the one nearer the head).</summary>
        static void Grip(Vector3 right, Vector3 left, out Vector3 mid, out Vector3 axis)
        {
            mid = (right + left) * 0.5f;
            axis = (right - left).sqrMagnitude > 1e-6f ? (right - left).normalized : Vector3.up;
        }

        static GameObject Template()
        {
            if (_template != null) return _template;
            _template = new GameObject("Hammer (template)") { layer = Layers.Fx, hideFlags = HideFlags.HideAndDontSave };
            _template.SetActive(false);
            Object.DontDestroyOnLoad(_template);

            var prefab = Resources.Load<GameObject>("VFX/Hammer");
            if (prefab == null)
            {
                Debug.LogWarning("[SME] Resources/VFX/Hammer is missing: the Weapon cards use a plain stand-in hammer.");
                SwordModel.Part(_template.transform, Glow.CubeMesh, null, Matrix4x4.TRS(new Vector3(0f, 0.4f, 0f), Quaternion.identity, new Vector3(0.1f, 1.9f, 0.1f)));
                SwordModel.Part(_template.transform, Glow.CubeMesh, null, Matrix4x4.TRS(new Vector3(0f, 1.375f, 0f), Quaternion.identity, new Vector3(0.7f, 0.55f, 0.5f)));
                return _template;
            }

            // instantiated (active) so ProBuilder builds the meshes, copied out, then thrown away
            var src = Object.Instantiate(prefab);
            var root = src.transform;
            var r = root.Find("HandleR");
            var l = root.Find("HandleL");
            Vector3 right = r != null ? root.InverseTransformPoint(r.position) : new Vector3(0f, -1.17f, 0f);
            Vector3 left = l != null ? root.InverseTransformPoint(l.position) : new Vector3(0f, -1.58f, 0f);
            Grip(right, left, out var mid, out var axis);
            Vector3 face = Vector3.ProjectOnPlane(Vector3.right, axis);
            face = face.sqrMagnitude > 1e-4f ? face.normalized : Vector3.forward;
            // the grip frame, in the prefab's own space: Y up the handle, X out of the striking face
            var frame = Matrix4x4.TRS(mid, Quaternion.LookRotation(Vector3.Cross(face, axis), axis), Vector3.one);
            Matrix4x4 toGrip = frame.inverse * root.worldToLocalMatrix;
            foreach (var mf in src.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                SwordModel.Part(_template.transform, Object.Instantiate(mf.sharedMesh), mr != null ? mr.sharedMaterials : null, toGrip * mf.transform.localToWorldMatrix);
            }
            if (r != null && l != null)
            {
                _halfSpacing = Vector3.Distance(r.position, l.position) * 0.5f;
                _headReach = Vector3.Dot(-mid, axis);
                _measured = true;
            }
            Object.Destroy(src);
            return _template;
        }
    }

    // ====================================================================== how it's swung

    /// <summary>
    /// The hammer gestures, shared by the arms (ProceduralAnimator builds its keyframes from it) and the hammer
    /// (HammerView points it with it), so hands and hammer always agree. Body space, from the chest: x right, y up,
    /// z forward. Both hands are always on the handle, the right one nearer the head.
    ///  - Drag: rushing in, the hammer trailing behind on the left, its head on the ground.
    ///  - Rise: brought up from behind, under and through the front, and on up (an uppercut).
    ///  - Leap: down on one knee, then in the air, the hammer held up behind the head.
    ///  - Slam: brought over the top and straight down in front.
    /// </summary>
    public static class HammerMotion
    {
        public const float RiseSwing = 0.16f, RiseSettle = 0.3f;
        public const float SlamSwing = 0.16f, SlamSettle = 0.35f; // (three quarters of a turn, over the top)

        public struct Frame
        {
            public Vector3 grip, shaft, face; // middle of the hands, the way up the handle, the way the striking face points
            public float twist, drop, lean;
            public Vector3 Right => grip + shaft * HammerModel.HalfSpacing;
            public Vector3 Left => grip - shaft * HammerModel.HalfSpacing;
        }

        // the handle's direction: an angle in the body's side-on plane (0 = straight ahead, 90 = straight up), leaning across by x
        static Vector3 Shaft(float deg, float x)
        {
            float a = deg * Mathf.Deg2Rad;
            return new Vector3(x, Mathf.Sin(a), Mathf.Cos(a)).normalized;
        }

        /// <summary>The way the head is moving as the angle grows (<paramref name="sign"/> 1) or shrinks (-1): where its face leads.</summary>
        static Vector3 Lead(float deg, float x, float sign)
        {
            Vector3 d = Shaft(deg + 5f * sign, x) - Shaft(deg - 5f * sign, x);
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
        }

        static float Smoother(float u) => u * u * u * (u * (u * 6f - 15f) + 10f);

        static Vector3 Curve(Vector3 a, Vector3 m, Vector3 b, float e)
        {
            Vector3 c = 2f * m - 0.5f * (a + b); // passes through m half way
            return (1f - e) * (1f - e) * a + 2f * (1f - e) * e * c + e * e * b;
        }

        // Rising Hammer: trailing behind on the left, head down on the ground...
        // (held up at the hip and angled out to the left, so the handle runs clear of the body; the head still reaches the ground)
        static readonly Vector3 DragGrip = new(-0.3f, -0.24f, 0.1f);
        const float DragAngle = -150f, DragX = -0.75f;
        // ...swung under and up through the front, finishing high in front
        static readonly Vector3 RiseMid = new(-0.08f, -0.1f, 0.3f), RiseEnd = new(0.02f, 0.42f, 0.3f);
        const float RiseAngle = 75f, RiseX = -0.05f;

        // Descending Hammer: held up behind the head...
        // (wound right back: arms up, the left hand above the head, the right by the right ear; the hammer hangs down
        // behind the back over the right shoulder, its head down by the hips, the handle clear of head and body)
        static readonly Vector3 RaisedGrip = new(0.15f, 0.5f, 0.02f);
        const float RaisedAngle = 235f, RaisedX = 0f;
        // ...brought forward over the top and straight down in front
        static readonly Vector3 SlamMid = new(0.06f, 0.5f, 0.2f), SlamEnd = new(0f, -0.28f, 0.45f);
        const float SlamAngle = -35f;

        static Frame Drag() => new()
        {
            grip = DragGrip, shaft = Shaft(DragAngle, DragX), face = Lead(DragAngle, DragX, 1f), // face down, on the ground
            twist = -25f, drop = 0.14f, lean = 18f,
        };

        static Frame Rise(float t)
        {
            float e = Smoother(Mathf.Clamp01(t / RiseSwing));
            float drift = Mathf.Sin(Mathf.Clamp01((t - RiseSwing) / RiseSettle) * Mathf.PI * 0.5f);
            float angle = Mathf.Lerp(DragAngle, RiseAngle, e) + 10f * drift, x = Mathf.Lerp(DragX, RiseX, e);
            return new Frame
            {
                grip = Curve(DragGrip, RiseMid, RiseEnd, e) + new Vector3(0f, 0.04f, -0.04f) * drift,
                shaft = Shaft(angle, x), face = Lead(angle, x, 1f),
                twist = Mathf.Lerp(-25f, 15f, e), drop = Mathf.Lerp(0.14f, 0.02f, e), lean = Mathf.Lerp(18f, -6f, e),
            };
        }

        static Frame Leap(float t)
        {
            // down on one knee for the crouch, then up into the air, arched back
            float air = Mathf.Clamp01((t - SpellPlans.HammerCrouch) / 0.1f);
            return new Frame
            {
                grip = RaisedGrip, shaft = Shaft(RaisedAngle, RaisedX), face = Lead(RaisedAngle, RaisedX, -1f),
                twist = 0f, drop = Mathf.Lerp(0.42f, 0f, air), lean = Mathf.Lerp(12f, -8f, air),
            };
        }

        static Frame Slam(float t)
        {
            float e = Smoother(Mathf.Clamp01(t / SlamSwing));
            float drift = Mathf.Sin(Mathf.Clamp01((t - SlamSwing) / SlamSettle) * Mathf.PI * 0.5f);
            float angle = Mathf.Lerp(RaisedAngle, SlamAngle, e) - 5f * drift, x = Mathf.Lerp(RaisedX, 0f, e);
            return new Frame
            {
                grip = Curve(RaisedGrip, SlamMid, SlamEnd, e), shaft = Shaft(angle, x), face = Lead(angle, x, -1f),
                twist = 0f, drop = Mathf.Lerp(0f, 0.3f, e), lean = Mathf.Lerp(-8f, 30f, e),
            };
        }

        // Hammer Spin: wound right back, the hammer out behind on the right, low and level...
        static readonly Vector3 WoundGrip = new(0.3f, -0.18f, -0.02f);
        const float WoundAngle = 190f, WoundX = 1.6f;
        // ...then stretched straight out to the right, level, at the hips, the face leading round the spin
        static readonly Vector3 SpinGrip = new(0.42f, -0.12f, 0.12f);
        const float SpinAngle = 0f, SpinX = 5f;

        static Frame Wound() => new()
        {
            grip = WoundGrip, shaft = Shaft(WoundAngle, WoundX), face = Vector3.down,
            twist = 38f, drop = 0.2f, lean = 12f,
        };

        static Frame Spin(float t)
        {
            float e = Smoother(Mathf.Clamp01(t / 0.12f)); // swung round from behind into the spin
            float angle = Mathf.Lerp(WoundAngle, 360f + SpinAngle, e), x = Mathf.Lerp(WoundX, SpinX, e);
            return new Frame
            {
                grip = Vector3.Lerp(WoundGrip, SpinGrip, e), shaft = Shaft(angle, x), face = Vector3.forward,
                twist = Mathf.Lerp(38f, 0f, e), drop = Mathf.Lerp(0.2f, 0.12f, e), lean = Mathf.Lerp(12f, -4f, e),
            };
        }

        public static Frame Sample(SpellPose pose, float t) => pose switch
        {
            SpellPose.HammerSpinCharge => Wound(),
            SpellPose.HammerSpin => Spin(t),
            SpellPose.HammerRise => Rise(t),
            SpellPose.HammerLeap => Leap(t),
            SpellPose.HammerSlam => Slam(t),
            _ => Drag(),
        };
    }

    /// <summary>
    /// The hammer in a body's two hands while it plays a hammer gesture: it manifests (dithers in), is held and
    /// swung the way the gesture says, sparks where its head drags along the ground, leaves a hot streak behind its
    /// head through a swing, and dithers away when the gesture's done.
    /// </summary>
    [DefaultExecutionOrder(150)] // after the ProceduralAnimator has posed the hands this frame
    public class HammerView : MonoBehaviour
    {
        static readonly int FadeId = Shader.PropertyToID("_Fade");

        Combatant _c;
        GameObject _hammer;
        Transform _head;
        TrailRenderer _trail;
        Renderer[] _renderers;
        MaterialPropertyBlock _mpb;
        float _fade, _nextSpark;
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

            if (_hammer == null)
            {
                _hammer = HammerModel.Spawn();
                _renderers = _hammer.GetComponentsInChildren<Renderer>(true);
                _head = new GameObject("Head") { layer = Layers.Fx }.transform;
                _head.SetParent(_hammer.transform, false);
                _head.localPosition = Vector3.up * HammerModel.HeadReach;
                _mpb ??= new MaterialPropertyBlock();
                _placed = false;
            }

            // in both hands, pointed the way the gesture says; once it's over, it stays where it finished and fades there
            // (the hands go back to rest straight away, and following them would jerk it across)
            if (want || !_placed)
            {
                // (Hammer Spin: the whole body turns round and round, the hammer with it)
                Quaternion body = Quaternion.Euler(0f, _c.ViewYaw + (pose == SpellPose.HammerSpin ? SpellPlans.SpinAngle(t, WorldNet.SpinTimeOf(_c.NetworkObjectId)) : 0f), 0f);
                var f = HammerMotion.Sample(pose, t);
                Vector3 shaft = body * f.shaft;
                Vector3 face = Vector3.ProjectOnPlane(body * f.face, shaft);
                if (face.sqrMagnitude < 1e-4f) face = Vector3.ProjectOnPlane(body * Vector3.forward, shaft);
                Quaternion aim = Quaternion.LookRotation(Vector3.Cross(face.normalized, shaft), shaft);
                _rot = _placed && pose != SpellPose.HammerSpin ? Quaternion.Slerp(_rot, aim, 1f - Mathf.Exp(-40f * dt)) : aim;
                _placed = true;
                Vector3 grip = (SpellVisuals.RightHand(_c) + SpellVisuals.LeftHand(_c)) * 0.5f;
                _hammer.transform.SetPositionAndRotation(grip, _rot);
            }

            // a hot streak behind the head through the swing
            bool swinging = want && (pose == SpellPose.HammerRise && t < HammerMotion.RiseSwing + 0.08f || pose == SpellPose.HammerSlam && t < HammerMotion.SlamSwing + 0.06f
                || pose == SpellPose.HammerSpin && t < WorldNet.SpinTimeOf(_c.NetworkObjectId) - 0.05f);
            if (swinging && _trail == null) _trail = Glow.Trail(_head, HammerVfx.Hot, 0.14f, 0.3f);
            else if (!swinging && _trail != null) { Glow.DropTrail(_trail); _trail = null; }

            // sparks where the dragged head scrapes along the ground
            if (want && pose == SpellPose.HammerDrag && Time.time >= _nextSpark &&
                Physics.Raycast(_head.position + Vector3.up * 0.3f, Vector3.down, out var hit, 0.9f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
            {
                _nextSpark = Time.time + 0.06f;
                EnergyBurst.Create(hit.point + Vector3.up * 0.05f, new EnergyBurst.Settings
                {
                    flashSize = 0.18f, flashTime = 0.06f, sparkCount = 4, sparkSize = 0.09f, sparkSpeed = new Vector2(2f, 5f), sparkLife = 0.2f, colour = HammerVfx.Hot,
                });
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
            SpellPose.HammerDrag => t < SpellPlans.RisingTime + 0.2f,
            SpellPose.HammerRise => t < HammerMotion.RiseSwing + HammerMotion.RiseSettle,
            SpellPose.HammerLeap => true,
            SpellPose.HammerSlam => t < HammerMotion.SlamSwing + HammerMotion.SlamSettle,
            SpellPose.HammerSpinCharge => true,
            SpellPose.HammerSpin => true,
            _ => false,
        };

        void Hide()
        {
            if (_trail != null) { Glow.DropTrail(_trail); _trail = null; }
            if (_hammer != null) Destroy(_hammer);
            _hammer = null;
            _head = null;
            _fade = 0f;
        }

        void OnDestroy() => Hide();
    }

    // ====================================================================== Descending Hammer's slam

    /// <summary>
    /// The slam's shockwave, flat against the surface it hit: a hot flash, two inked rings racing out across the
    /// surface (the outer one stops exactly at the crater's radius: what's drawn is what it hits), and cartoon
    /// shock lines shooting out from the middle. Everything thins and shrinks away; nothing fades.
    /// </summary>
    public class HammerShock : MonoBehaviour
    {
        const float Life = 0.55f;
        const int RingPieces = 40, Lines = 12;

        readonly List<Transform> _outer = new(), _inner = new();
        readonly List<(Transform t, Vector3 dir, float delay)> _lines = new();
        float _radius, _age;

        public static HammerShock Create(Vector3 point, Vector3 normal, float radius)
        {
            var go = new GameObject("HammerShock") { layer = Layers.Fx };
            go.transform.SetPositionAndRotation(point, Quaternion.FromToRotation(Vector3.up, normal.sqrMagnitude > 1e-4f ? normal.normalized : Vector3.up));
            var s = go.AddComponent<HammerShock>();
            s._radius = radius;
            for (int i = 0; i < RingPieces; i++)
            {
                s._outer.Add(Glow.Piece(go.transform, Glow.CubeMesh, HammerVfx.Hot, "Ring"));
                s._inner.Add(Glow.Piece(go.transform, Glow.CubeMesh, HammerVfx.Hot, "Ring"));
            }
            for (int i = 0; i < Lines; i++)
            {
                float a = (i + Random.Range(-0.25f, 0.25f)) / Lines * Mathf.PI * 2f;
                s._lines.Add((Glow.Piece(go.transform, Glow.CubeMesh, HammerVfx.Hot, "Line"), new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)), Random.Range(0f, 0.04f)));
            }
            EnergyBurst.Create(point + normal * 0.3f, new EnergyBurst.Settings
            {
                flashSize = 1.6f, flashTime = 0.18f, sparkCount = 22, sparkSize = 0.4f, sparkSpeed = new Vector2(radius * 2f, radius * 4f), sparkLife = 0.35f, colour = HammerVfx.Hot,
            });
            CameraRig.ShakeFrom(point, 1.4f, 0.5f);
            s.LateUpdate();
            return s;
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            float u = _age / Life;
            if (u >= 1f) { Destroy(gameObject); return; }
            float R = _radius;
            Ring(_outer, u, R, R * 0.07f, 0f);
            Ring(_inner, u, R * 0.62f, R * 0.05f, 0.12f);

            // shock lines: shooting out from the middle, their tips stopping at the rim, then shrinking away
            foreach (var (t, dir, delay) in _lines)
            {
                float k = Mathf.Clamp01((u - delay) / 0.35f);
                float go = 1f - Mathf.Pow(1f - k, 3f);
                float shrink = 1f - Mathf.Clamp01((u - 0.35f) / 0.65f);
                float len = R * 0.35f * shrink * Mathf.Clamp01(k * 3f);
                float tip = Mathf.Lerp(R * 0.2f, R, go);
                float w = R * 0.025f * shrink;
                t.localPosition = dir * (tip - len * 0.5f) + Vector3.up * 0.12f;
                t.localRotation = Quaternion.LookRotation(dir);
                t.localScale = new Vector3(Mathf.Max(0.001f, w), Mathf.Max(0.001f, w), Mathf.Max(0.001f, len)) / 1.14f;
            }
        }

        /// <summary>A ring of inked segments racing out to <paramref name="reach"/> (its outside edge exactly there), thinning as it goes.</summary>
        static void Ring(List<Transform> pieces, float u, float reach, float thickness, float delay)
        {
            float k = Mathf.Clamp01((u - delay) / 0.45f);
            float out_ = 1f - Mathf.Pow(1f - k, 3f);
            float shrink = 1f - Mathf.Clamp01((u - delay - 0.45f) / 0.4f);
            float ringR = Mathf.Lerp(reach * 0.15f, reach, out_);
            float thick = thickness * shrink * (k > 0f ? 1f : 0f);
            float seg = 2f * Mathf.PI * ringR / pieces.Count * 1.05f;
            for (int i = 0; i < pieces.Count; i++)
            {
                float a = (i + 0.5f) / pieces.Count * Mathf.PI * 2f;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                pieces[i].localPosition = d * (ringR - thick * 0.65f) + Vector3.up * 0.1f;
                pieces[i].localRotation = Quaternion.LookRotation(Vector3.Cross(Vector3.up, d));
                pieces[i].localScale = new Vector3(Mathf.Max(0.001f, thick), Mathf.Max(0.001f, thick * 0.6f), Mathf.Max(0.001f, seg)) / 1.14f;
            }
        }
    }

    // ====================================================================== Rising Hammer connecting

    /// <summary>The uppercut landing: a hot flash and sparks on the body, and inked speed lines shooting off the way it was thrown (up).</summary>
    public class HammerImpact : MonoBehaviour
    {
        const float Life = 0.35f;
        readonly List<(Transform t, Vector3 offset, float delay)> _lines = new();
        Vector3 _dir;
        float _age;

        public static HammerImpact Create(Vector3 point, Vector3 dir)
        {
            var go = new GameObject("HammerImpact") { layer = Layers.Fx };
            go.transform.position = point;
            var s = go.AddComponent<HammerImpact>();
            s._dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.up;
            Vector3 side = Vector3.Cross(s._dir, Mathf.Abs(s._dir.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 side2 = Vector3.Cross(s._dir, side);
            for (int i = 0; i < 4; i++)
            {
                float a = (i + 0.5f) / 4f * Mathf.PI * 2f;
                Vector3 off = (side * Mathf.Cos(a) + side2 * Mathf.Sin(a)) * 0.35f;
                s._lines.Add((Glow.Piece(go.transform, Glow.CubeMesh, HammerVfx.Hot, "Line"), off, i * 0.02f));
            }
            EnergyBurst.Create(point, new EnergyBurst.Settings
            {
                flashSize = 0.9f, flashTime = 0.14f, sparkCount = 12, sparkSize = 0.28f, sparkSpeed = new Vector2(4f, 9f), sparkLife = 0.3f, colour = HammerVfx.Hot,
            });
            CameraRig.ShakeFrom(point, 0.45f, 0.25f);
            s.LateUpdate();
            return s;
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            if (_age >= Life + 0.1f) { Destroy(gameObject); return; }
            foreach (var (t, offset, delay) in _lines)
            {
                float k = Mathf.Clamp01((_age - delay) / Life);
                float len = 1.6f * Mathf.Sin(k * Mathf.PI);
                float w = 0.07f * (1f - k);
                t.position = transform.position + offset + _dir * (0.3f + 2.2f * k + len * 0.5f);
                t.rotation = Quaternion.LookRotation(_dir);
                t.localScale = new Vector3(Mathf.Max(0.001f, w), Mathf.Max(0.001f, w), Mathf.Max(0.001f, len));
            }
        }
    }
}
