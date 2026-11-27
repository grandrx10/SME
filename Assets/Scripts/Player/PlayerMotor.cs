using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Owner-side movement: WASD to run, Space to jump, steps and voxel ledges climbed automatically.
    /// When knocked away the body goes ragdoll: it flies ballistically, bounces, and smashes through
    /// breakable voxels it hits hard enough. The result is replicated by PlayerController.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerMotor : MonoBehaviour
    {
        /// <summary>Speed into a surface (m/s) at which a ragdolled body breaks through voxels.</summary>
        public const float CrashSpeed = 9f;
        /// <summary>Landing faster than this (m/s, downwards) hurts.</summary>
        public const float SafeFallSpeed = 14f;

        [Header("Movement")]
        public float runSpeed = 6.5f;
        public float groundAccel = 60f;
        public float groundDecel = 50f;
        public float airControl = 18f;
        public float gravity = 20f;
        public float jumpUpSpeed = 7.6f;
        public float jumpBuffer = 0.15f;
        public float coyoteTime = 0.12f;
        [Tooltip("Ledges up to this high (above the feet) are climbed by walking/jumping into them.")]
        public float ledgeClimbHeight = 0.8f;

        [Header("Look")]
        public float minPitch = -80f, maxPitch = 80f;

        // ---- public state ----
        public Vector3 Velocity;
        public bool IsGrounded { get; private set; }
        public float Yaw, Pitch;
        public Quaternion ViewRotation => Quaternion.Euler(Pitch, Yaw, 0);
        public Vector3 AimDirection => ViewRotation * Vector3.forward;
        public bool Ragdolled { get; private set; }
        public float HorizontalSpeed => new Vector2(Velocity.x, Velocity.z).magnitude;

        /// <summary>(contact point, velocity) — a ragdolled body just smashed into breakable voxels.</summary>
        public event Action<Vector3, Vector3> Crashed;
        /// <summary>Downward speed (m/s) of a hard landing (only above <see cref="SafeFallSpeed"/>).</summary>
        public event Action<float> HardLanding;
        /// <summary>(feet position) — trying to stand up but something is in the way: clear it.</summary>
        public event Action<Vector3> StandBlocked;

        CharacterController _cc;
        float _standHeight, _standRadius;
        Vector3 _standCenter;
        float _lastGroundedTime = -10f, _jumpPressedTime = -10f, _lastJumpTime = -10f;
        float _ragdollUntil, _lastCrash, _getUpUntil;
        /// <summary>After a knockdown you can't move until you've stood up (matches the get-up animation).</summary>
        const float GetUpSeconds = 0.5f;
        public bool GettingUp => Time.time < _getUpUntil;
        Vector3 _groundNormal = Vector3.up;
        float _fallSpeed;

        PlayerController _pc;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _pc = GetComponent<PlayerController>();
            _standHeight = _cc.height;
            _standCenter = _cc.center;
            _standRadius = _cc.radius;
            _gravity0 = gravity;
            _jump0 = jumpUpSpeed;
        }

        float _gravity0, _jump0;

        public void SetLook(float yaw, float pitch)
        {
            Yaw = yaw;
            Pitch = pitch;
            transform.rotation = Quaternion.Euler(0, Yaw, 0);
        }

        public void Teleport(Vector3 pos, float yaw)
        {
            _carried = false;
            _dashUntil = _siphonUntil = _rootUntil = 0f;
            _bubbleFrom = _bubbleUntil = -1f;
            _inBubble = false;
            _swiftFrom = _swiftUntil = -1f;
            _blinkStart = -1f;
            EndLeap();
            StopCharging();
            _reeling = false;
            _pinnedTo = null;
            if (_ragdollUntil == float.MaxValue) _ragdollUntil = 0f;
            EndRagdoll(true);
            _cc.enabled = false;
            transform.position = pos;
            Crouching = false; // respawned standing
            CrouchBlend = 0f;
            _cc.height = _standHeight;
            _cc.center = _standCenter;
            _cc.enabled = true;
            Velocity = Vector3.zero;
            _fallSpeed = 0f;
            SetLook(yaw, 0);
        }

        // ---------------- spell-driven movement ----------------

        Vector3 _dashDir;
        float _dashUntil, _siphonUntil, _rootUntil;
        bool _carried;
        Vector3 _carryOrigin, _carryDir;
        double _carryStart, _carryCaptured;
        float _carryPhase;

        public bool Dashing => Time.time < _dashUntil;
        public bool Siphoning => Time.time < _siphonUntil;
        public bool Rooted => Time.time < _rootUntil;
        /// <summary>The body stays facing one way (Ice Spike's crouch) while the camera still looks around.</summary>
        public bool FaceLocked => Time.time < _faceLockUntil && !Ragdolled;
        /// <summary>The way the body faces: <see cref="Yaw"/> (the camera's), unless it's face-locked.</summary>
        public float BodyYaw => _frozen ? _heldYaw : _puppet ? PuppetYaw() : FaceLocked ? _faceYaw : Yaw;
        /// <summary>The way the head looks up and down: <see cref="Pitch"/> (the camera's), unless a hex has hold of the body.</summary>
        public float BodyPitch => _frozen || _puppet ? _heldPitch : FaceLocked && _pitchLocked ? _facePitch : Pitch;
        float _faceLockUntil = -1f, _faceYaw, _facePitch;
        bool _pitchLocked;

        /// <summary>Keep the body facing the way it faces now for <paramref name="seconds"/> (the camera stays free).</summary>
        /// <param name="pitch">Up and down too (Sebastian, about to fire).</param>
        public void FaceLock(float seconds, bool pitch = false)
        {
            _faceYaw = Yaw;
            _facePitch = Pitch;
            _pitchLocked = pitch;
            _faceLockUntil = Time.time + seconds;
        }

        /// <summary>Keep the body facing <paramref name="yaw"/> for <paramref name="seconds"/> (Parry's dash: at whoever it's going for).</summary>
        public void FaceToward(float yaw, float seconds)
        {
            _faceYaw = yaw;
            _facePitch = Pitch;
            _pitchLocked = false;
            _faceLockUntil = Time.time + seconds;
        }
        /// <summary>Floating in a Bubble Away right now.</summary>
        public bool Bubbled => Time.time >= _bubbleFrom && Time.time < _bubbleUntil;
        /// <summary>Taken by The World: held exactly where it is (no gravity, no moving; looking round still works).</summary>
        public bool Held;
        public bool Busy => Held || Stasis || Mind || Dashing || Siphoning || Rooted || _carried || Time.time < _bubbleUntil || Time.time < _swiftUntil || Blinking || Charioting || Pinned || Leaping;

        // ---------------- Stasis Hex, Mind Hex ----------------

        bool _frozen, _puppet, _jumpSeen, _jumpWanted;
        float _heldYaw, _heldPitch, _puppetYaw, _masterYaw;
        double _thawed, _broken, _liftOf;
        float _lifted;

        /// <summary>Frozen by Stasis Hex, or held up by Shatter Hex (not any more once something has moved us out of it: the server's word on that is on its way).</summary>
        public bool Stasis => _pc != null && (_pc.InStasis && _pc.Status.Value.StasisFrom != _thawed || Shattering);
        bool Shattering => _pc != null && _pc.Shattering && _pc.Status.Value.ShatterFrom != _broken;

        /// <summary>The server has thrown, carried or picked us up: out of Stasis (or Shatter's hold).</summary>
        void Thaw()
        {
            if (_pc == null) return;
            if (_pc.InStasis) _thawed = _pc.Status.Value.StasisFrom;
            if (_pc.Shattering) _broken = _pc.Status.Value.ShatterFrom;
        }

        /// <summary>Held by a hex: nothing moves, but for Shatter's lift, and being pushed out of ground that closes on us.</summary>
        void FrozenTick()
        {
            Velocity = Vector3.zero;
            _fallSpeed = 0f;
            if (!Shattering) { Depenetrate(); return; }
            double from = _pc.Status.Value.ShatterFrom;
            if (_liftOf != from) { _liftOf = from; _lifted = 0f; }
            float up = SpellPlans.ShatterRise((float)(WorldNet.Now - from));
            if (up > _lifted) _cc.Move(Vector3.up * (up - _lifted));
            _lifted = up;
            IsGrounded = false;
        }
        /// <summary>Under someone's control (Mind Hex): our feet do what theirs do.</summary>
        public bool Mind => _pc != null && _pc.MindHeld;
        Combatant Master => _pc != null && _pc.MindHeld ? WorldNet.FindCombatant(_pc.Status.Value.MindBy) : null;

        /// <summary>The body turns as theirs does, from the way it faced when they took hold of it.</summary>
        float PuppetYaw()
        {
            var m = Master;
            return m != null ? Mathf.Repeat(_puppetYaw + Mathf.DeltaAngle(_masterYaw, m.ViewYaw), 360f) : _puppetYaw;
        }

        void HexTick()
        {
            bool stasis = Stasis, mind = Mind;
            if (stasis && !_frozen)
            {
                _heldYaw = BodyYaw;
                _heldPitch = BodyPitch;
                StopEverything();
                _carried = false;
                if (_ragdollUntil == float.MaxValue) _ragdollUntil = Time.time + 0.5f;
            }
            if (mind && !_puppet)
            {
                var m = Master;
                _puppetYaw = BodyYaw;
                _heldPitch = BodyPitch;
                _masterYaw = m != null ? m.ViewYaw : 0f;
                _jumpSeen = true; // (already in the air when it takes hold: not a jump)
                StopEverything();
            }
            _frozen = stasis;
            _puppet = mind;
            if (!mind || Ragdolled) { _jumpWanted = false; _jumpSeen = true; return; }
            bool jumping = SpellPlans.PuppetJumping(Master);
            if (jumping && !_jumpSeen) _jumpWanted = true;
            _jumpSeen = jumping;
        }

        void StopEverything()
        {
            _dashUntil = _siphonUntil = _rootUntil = 0f;
            _faceLockUntil = -1f;
            _blinkStart = -1f;
            _reeling = false;
            EndLeap();
            EndBubble();
            if (Charioting) StopCharging();
        }

        // ---------------- Descending Hammer ----------------

        float _leapPrepUntil = -1f, _leapStart = -1f;
        bool _leapSwung;
        Vector3 _lastHitPoint, _lastHitNormal = Vector3.up;
        Collider _lastHitCollider;
        /// <summary>Crouched for Descending Hammer, or in the air on its leap (until it touches down).</summary>
        public bool Leaping => _leapPrepUntil >= 0f || _leapStart >= 0f;
        /// <summary>The leap is about to touch down: bring the hammer down now.</summary>
        public event Action HammerSwing;
        /// <summary>(point, surface normal) — the leap touched down (ground, a wall, a ceiling or someone): the slam.</summary>
        public event Action<Vector3, Vector3> HammerLanded;

        /// <summary>Descending Hammer: down on one knee for <paramref name="crouch"/>, then leap up and forward the way you face.</summary>
        public void HammerLeap(float crouch)
        {
            _leapPrepUntil = Time.time + crouch;
            _leapStart = -1f;
            _leapSwung = false;
            _dashUntil = _siphonUntil = _rootUntil = 0f;
        }

        /// <summary>Knocked out of it, or it never came down: no slam.</summary>
        public void EndLeap()
        {
            _leapPrepUntil = _leapStart = -1f;
        }

        void LeapPrepTick(float dt)
        {
            GroundCheck();
            Velocity = new Vector3(0f, IsGrounded ? -2f : Velocity.y - gravity * dt, 0f);
            _cc.Move(Velocity * dt);
            if (Time.time < _leapPrepUntil) return;
            // off: up and forward the way you face
            _leapPrepUntil = -1f;
            _leapStart = Time.time;
            Velocity = Quaternion.Euler(0f, Yaw, 0f) * Vector3.forward * SpellPlans.LeapForward + Vector3.up * SpellPlans.LeapUp;
            _lastJumpTime = Time.time;
            IsGrounded = false;
            _fallSpeed = 0f;
        }

        void LeapTick(float dt)
        {
            float t = Time.time - _leapStart;
            if (t > SpellPlans.LeapMaxAir) { EndLeap(); return; } // never came down
            // the mouse redirects it freely: the way across turns to wherever you face
            Vector3 h = new(Velocity.x, 0f, Velocity.z);
            Vector3 want = Quaternion.Euler(0f, Yaw, 0f) * Vector3.forward;
            if (h.sqrMagnitude > 1e-4f) h = Vector3.RotateTowards(h, want * h.magnitude, SpellPlans.LeapSteer * Mathf.Deg2Rad * dt, 0f);
            Velocity = new Vector3(h.x, Velocity.y - gravity * dt, h.z);

            // the swing down starts just before it lands
            if (!_leapSwung && t > 0.1f)
            {
                float lead = Velocity.magnitude * SpellPlans.SlamLead;
                Vector3 c = transform.position + _cc.center;
                float half = Mathf.Max(0f, _cc.height * 0.5f - _cc.radius);
                if (lead > 0.01f && Physics.CapsuleCast(c + Vector3.down * half, c + Vector3.up * half, _cc.radius * 0.95f, Velocity.normalized, lead,
                        Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                {
                    _leapSwung = true;
                    HammerSwing?.Invoke();
                }
            }

            _lastHitCollider = null;
            var flags = _cc.Move(Velocity * dt);
            if (t < 0.08f && (flags & CollisionFlags.Below) != 0) flags &= ~CollisionFlags.Below; // still leaving the ground
            Vector3 feet = transform.position + _cc.center + Vector3.down * (_cc.height * 0.5f);
            Combatant on = SomeoneTouching();
            if (flags == CollisionFlags.None && on == null) return;

            // touched down (anything at all): the slam, right here
            Vector3 point = feet, normal = Vector3.up;
            bool body = on != null || (_lastHitCollider != null && _lastHitCollider.GetComponentInParent<Combatant>() != null);
            if (!body && (flags & CollisionFlags.Below) == 0 && _lastHitCollider != null)
            {
                point = _lastHitPoint;
                normal = _lastHitNormal;
            }
            EndLeap();
            if (!_leapSwung) HammerSwing?.Invoke();
            Velocity = Vector3.zero;
            Root(0.25f); // a beat to recover from the blow
            HammerLanded?.Invoke(point, normal);
        }

        /// <summary>Someone else's body we've come down on (their capsules aren't always solid to us).</summary>
        Combatant SomeoneTouching()
        {
            Vector3 c = transform.position + _cc.center;
            foreach (var o in Combatant.Registry)
            {
                if (o == null || o.gameObject == gameObject || !o.IsAlive || !o.Visible) continue;
                Vector3 d = o.transform.position - c;
                if (Mathf.Abs(d.y) > 1.5f) continue;
                d.y = 0f;
                if (d.magnitude < _cc.radius + 0.4f) return o;
            }
            return null;
        }

        // ---------------- The Chariot ----------------

        Vector3 _chariotDir;
        float _chariotStart = -1f, _lastChariotBlock;
        /// <summary>Other people's bodies the ball rolls through while charging (the server picks them up or bowls them aside).</summary>
        readonly List<Collider> _chariotIgnored = new();
        /// <summary>Someone's stuck on the front of our charge (set each frame by the owner): they hitting a wall is the crash.</summary>
        public bool ChariotCarrying { get; set; }
        /// <summary>Charging with The Chariot right now: running on its own, faster and faster, the way it set off.</summary>
        public bool Charioting => _chariotStart >= 0f;
        /// <summary>(feet, direction) — the charge ran into terrain (the owner decides: smash through it, or crash).</summary>
        public event Action<Vector3, Vector3> ChariotBlocked;

        /// <summary>The Chariot: charge along <paramref name="dir"/> (level) until told to stop.</summary>
        public void Chariot(Vector3 dir)
        {
            dir.y = 0f;
            _chariotDir = dir.sqrMagnitude > 1e-4f ? dir.normalized : transform.forward;
            Yaw = SpellPlans.YawOf(_chariotDir);
            _chariotStart = Time.time;
            _nextIgnore = 0f;
            Crouching = false; // (rolled up into the ball; it unrolls standing)
            SetBallShape(true);
            _dashUntil = _siphonUntil = _rootUntil = 0f;
        }

        /// <summary>The charge is over: it stops dead (a crash bounces you back a little).</summary>
        public void EndChariot(bool crashed)
        {
            if (!Charioting) return;
            StopCharging();
            Velocity = crashed ? -_chariotDir * 4f + Vector3.up * 3f : _chariotDir * 3f;
            if (crashed) { _lastJumpTime = Time.time; IsGrounded = false; }
        }

        void StopCharging()
        {
            if (_chariotStart < 0f) return;
            _chariotStart = -1f;
            ChariotCarrying = false;
            SetBallShape(false);
            foreach (var col in _chariotIgnored) if (col != null) Physics.IgnoreCollision(_cc, col, false);
            _chariotIgnored.Clear();
        }

        /// <summary>The ball doesn't stop at people: it rolls into them (the server sticks them on the front, or bowls them aside).</summary>
        static readonly List<Collider> _bodyCols = new();
        float _nextIgnore;

        void IgnoreBodies()
        {
            // (a few times a second is plenty: only someone joining mid-charge is new)
            if (Time.time < _nextIgnore) return;
            _nextIgnore = Time.time + 0.25f;
            foreach (var o in Combatant.Registry)
            {
                if (o == null || o.gameObject == gameObject) continue;
                o.GetComponents(_bodyCols);
                foreach (var col in _bodyCols)
                {
                    if (col == null || col.isTrigger || _chariotIgnored.Contains(col) || Physics.GetIgnoreCollision(_cc, col)) continue;
                    Physics.IgnoreCollision(_cc, col, true);
                    _chariotIgnored.Add(col);
                }
            }
        }

        /// <summary>
        /// Charging, the body is the chariot's ball, so it collides with the world as the ball it's drawn as (bottom at
        /// the feet, <see cref="SpellPlans.ChariotRadius"/> round). Back to a standing capsule after.
        /// </summary>
        void SetBallShape(bool ball)
        {
            if (Ragdolled) { _cc.radius = _standRadius; return; } // (the ragdoll keeps its own ball round the hips)
            _cc.enabled = false;
            float r = ball ? SpellPlans.ChariotRadius : _standRadius;
            if (ball)
            {
                _cc.radius = r;
                _cc.height = r * 2f;
                _cc.center = SpellPlans.ChariotBallOffset;
            }
            else
            {
                _cc.height = _standHeight;
                _cc.center = _standCenter;
                _cc.radius = r;
            }
            _cc.enabled = true;
        }

        void ChariotTick(float dt)
        {
            float t = Time.time - _chariotStart;
            // the server ends it (max time); if that got lost, stop
            if (t > SpellPlans.ChariotMaxTime + 1.5f) { StopCharging(); return; }
            float speed = SpellPlans.ChariotSpeed(t);
            _chariotDir = Quaternion.Euler(0f, Yaw, 0f) * Vector3.forward; // steered (a little) with the mouse
            IgnoreBodies();
            // carrying someone: they meet the wall first, and that's the crash
            if (ChariotCarrying && Time.time - _lastChariotBlock > 0.05f)
            {
                var w = VoxelWorld.Current;
                Vector3 feet = transform.position + _cc.center + Vector3.down * (_cc.height * 0.5f);
                if (w != null && SpellPlans.ChariotCarryBlocked(w, feet, _chariotDir))
                {
                    _lastChariotBlock = Time.time;
                    ChariotBlocked?.Invoke(feet + _chariotDir * SpellPlans.ChariotPinAhead, _chariotDir); // (where they are: the wall is tall there)
                    if (!Charioting) return;
                }
            }
            GroundCheck();
            Velocity = new Vector3(_chariotDir.x * speed, IsGrounded ? -2f : Velocity.y - gravity * dt, _chariotDir.z * speed);
            if (!IsGrounded) _fallSpeed = Mathf.Max(_fallSpeed, -Velocity.y);
            Vector3 before = transform.position;
            var flags = _cc.Move(Velocity * dt);
            Vector3 moved = transform.position - before;
            moved.y = 0f;
            // stopped by something in front (not just slowed on a step): smash it or crash into it. Only the progress the
            // way it's heading counts: glancing off a wall, the body slides along it, but the ball still ran into it
            if ((flags & CollisionFlags.Sides) != 0 && Vector3.Dot(moved, _chariotDir) < speed * dt * 0.5f && Time.time - _lastChariotBlock > 0.05f)
            {
                _lastChariotBlock = Time.time;
                ChariotBlocked?.Invoke(transform.position + _cc.center + Vector3.down * (_cc.height * 0.5f), _chariotDir);
            }
        }

        // ---------------- stuck on the front of someone's Chariot ----------------

        Combatant _pinnedTo, _pinRefused;
        float _pinRefusedUntil;
        /// <summary>Carried along on the front of someone's Chariot (limp, can't act).</summary>
        public bool Pinned => _pinnedTo != null;

        /// <summary>Owner, every frame while stuck on <paramref name="driver"/>'s chariot.</summary>
        public void PinTo(Combatant driver)
        {
            if (_pinnedTo == driver || driver == null) return;
            if (driver == _pinRefused && Time.time < _pinRefusedUntil) return; // just thrown off it: the server's release is on its way
            Thaw();
            _pinnedTo = driver;
            _dashUntil = _siphonUntil = _rootUntil = 0f;
            StopCharging();
            _carried = false;
            if (!Ragdolled) BeginRagdoll();
            _ragdollUntil = float.MaxValue;
        }

        /// <summary>Let go of the front of the chariot: limp a moment more, then up.</summary>
        public void Unpin()
        {
            if (_pinnedTo == null) return;
            _pinnedTo = null;
            if (_ragdollUntil == float.MaxValue) _ragdollUntil = Time.time + SpellPlans.ChariotStun;
        }

        void PinTick(float dt)
        {
            if (_pinnedTo == null || !_pinnedTo.IsSpawned) { Unpin(); return; }
            Vector3 p = SpellPlans.PinPoint(_pinnedTo.transform.position, _pinnedTo.ViewYaw);
            Velocity = _pinnedTo.ViewVelocity;
            _cc.enabled = false;
            transform.position = p;
            _cc.enabled = true;
            IsGrounded = false;
            _fallSpeed = 0f;
        }

        // ---------------- crouching ----------------

        /// <summary>
        /// Metres the hips sink when crouched. The drawn body (<see cref="ProceduralAnimator"/>), the camera, and the
        /// top of the body's capsule all come down by this much, so what you see is what can be hit and what fits.
        /// </summary>
        public const float CrouchDrop = 0.45f;
        /// <summary>Walking speed while crouched, as a share of running.</summary>
        public const float CrouchSpeedScale = 0.5f;
        /// <summary>Holding the Crouch key (and not stuck under something when you let go).</summary>
        public bool Crouching { get; private set; }
        /// <summary>0 standing .. 1 crouched, eased (how far the camera has come down).</summary>
        public float CrouchBlend { get; private set; }

        /// <summary>The crouched capsule: <see cref="CrouchDrop"/> off the top, the feet where they were.</summary>
        public static void CrouchShape(float standHeight, Vector3 standCenter, out float height, out Vector3 center)
        {
            height = standHeight - CrouchDrop;
            center = standCenter + Vector3.down * (CrouchDrop * 0.5f);
        }

        /// <summary>Crouch, or stand back up if there's head room (under a low roof you stay down).</summary>
        void SetCrouch(bool on)
        {
            if (Crouching == on || Ragdolled || Charioting) return; // (those shape the body themselves)
            if (!on && !RoomToStand()) return;
            Crouching = on;
            _cc.enabled = false;
            if (on)
            {
                CrouchShape(_standHeight, _standCenter, out float h, out Vector3 c);
                _cc.height = h;
                _cc.center = c;
            }
            else
            {
                _cc.height = _standHeight;
                _cc.center = _standCenter;
            }
            _cc.enabled = true;
        }

        bool RoomToStand()
        {
            Vector3 c = transform.position + _standCenter;
            float half = _standHeight * 0.5f - _cc.radius;
            return !Physics.CheckCapsule(c + Vector3.down * half + Vector3.up * 0.05f, c + Vector3.up * half, _cc.radius * 0.95f,
                Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>Down on one knee charging Flowing Cut: no walking or jumping (but still free to cast). Set every frame.</summary>
        public bool Kneeling { get; set; }

        // ---------------- Flowing Cut: the teleport ----------------

        Vector3 _blinkFrom, _blinkTo;
        float _blinkStart = -1f, _blinkTime, _blinkArrived = -10f;
        /// <summary>Flashing along a Flowing Cut right now (through anything: the server cuts the way open).</summary>
        public bool Blinking => _blinkStart >= 0f && Time.time < _blinkStart + _blinkTime;

        /// <summary>Flowing Cut: cross to <paramref name="to"/> (where the hips end up) in <paramref name="seconds"/>, passing through whatever's in between.</summary>
        /// <param name="steady">At an even speed all the way (Hammer Spin), not fast out of the stance and easing in.</param>
        /// <param name="curve">Or how far along it is at each share of the time (Hammer Spin: speeding up and slowing down).</param>
        public void Blink(Vector3 to, float seconds, bool steady = false, Func<float, float> curve = null)
        {
            _blinkSteady = steady;
            _blinkCurve = curve;
            _blinkFrom = transform.position;
            _blinkTo = to;
            _blinkTime = Mathf.Max(0.01f, seconds);
            _blinkStart = Time.time;
            _dashUntil = _siphonUntil = _rootUntil = 0f;
        }

        bool _blinkSteady;
        Func<float, float> _blinkCurve;

        void BlinkTick()
        {
            float u = Mathf.Clamp01((Time.time - _blinkStart) / _blinkTime);
            float e = _blinkCurve != null ? _blinkCurve(u) : _blinkSteady ? u : 1f - (1f - u) * (1f - u); // fast out of the stance, easing in (or steady)
            Vector3 p = Vector3.Lerp(_blinkFrom, _blinkTo, e);
            Velocity = (_blinkTo - _blinkFrom) / _blinkTime;
            _cc.enabled = false;
            transform.position = p;
            _cc.enabled = true;
            IsGrounded = false;
            if (u >= 1f)
            {
                _blinkStart = -1f;
                _blinkArrived = Time.time;
                Velocity = (_blinkTo - _blinkFrom).normalized * 2f; // a little carry, not the full speed
                _fallSpeed = 0f;
            }
        }

        // ---------------- moved by a spell (warp, portal) ----------------

        /// <summary>(new position) — moved somewhere else in one go (a warp, a portal): others should snap, not slide.</summary>
        public event Action<Vector3> Moved;

        /// <summary>Put the body somewhere else without stopping what it's doing (ragdoll, dash, bubble keep going).</summary>
        public void MoveTo(Vector3 pos, float yaw, Vector3 velocity, float? pitch = null)
        {
            _cc.enabled = false;
            transform.position = pos;
            _cc.enabled = true;
            Velocity = velocity;
            Yaw = Mathf.Repeat(yaw, 360f);
            if (pitch.HasValue) Pitch = Mathf.Clamp(pitch.Value, minPitch, maxPitch);
            if (!Ragdolled) transform.rotation = Quaternion.Euler(0, Yaw, 0);
            _dashDir = Quaternion.Euler(0, Yaw, 0) * Vector3.forward;
            Moved?.Invoke(pos);
        }

        int _portalSkip;

        /// <summary>
        /// Going into a linked portal: the moment the body touches its opening (walking into it, falling or jumping
        /// in, or standing on one that faces up), it comes out of the other one, the same distance in front of it,
        /// facing and moving the matching way. The opening is flush against whatever it was cast on, so we can't
        /// wait for the body to pass through it. The portal we came out of is ignored until we're clear of it.
        /// </summary>
        void ThroughPortals()
        {
            var net = WorldNet.Instance;
            if (net == null || net.Portals.Count < 2) return;
            double now = WorldNet.Now;
            float r = _cc.radius, half = Mathf.Max(0f, _cc.height * 0.5f - _cc.radius), slack = _cc.skinWidth + 0.06f;
            const float hw = SpellPlans.PortalWidth * 0.5f, hh = SpellPlans.PortalHeight * 0.5f;
            Vector3 c = transform.position + _cc.center;
            foreach (var a in net.Portals.Values)
            {
                var b = net.LinkedTo(a, now);
                if (b == null) continue;
                Quaternion qa = a.Rotation;
                Vector3 n = qa * Vector3.forward;
                Vector3 l = Quaternion.Inverse(qa) * (c - a.center);
                // how far the body reaches towards the opening (a capsule standing up: further for a floor or ceiling portal)
                float reach = r + half * Mathf.Abs(n.y) + slack;
                bool over = Mathf.Abs(l.x) <= hw && Mathf.Abs(l.y) <= hh;
                if (a.id == _portalSkip)
                {
                    if (!over || l.z > reach + 0.3f) _portalSkip = 0;
                    continue;
                }
                if (!over || l.z > reach || l.z < -0.5f) continue;
                // moving into it (as we were before the slab stopped us), or standing on a floor portal
                Vector3 moving = _hitThisFrame ? _intendedVelocity : Velocity;
                bool going = Vector3.Dot(moving, n) < -0.1f || n.y > 0.7f;
                if (!going) continue;

                Quaternion qb = b.Rotation;
                Vector3 nb = qb * Vector3.forward;
                Vector3 p = SpellPlans.ThroughPoint(c, a.center, qa, b.center, qb, true);
                // far enough out of the other one for the body (its reach differs if one portal is flat and the other upright)
                float reachB = r + half * Mathf.Abs(nb.y) + slack;
                float outB = Vector3.Dot(p - b.center, nb);
                if (outB < reachB) p += nb * (reachB - outB);
                Vector3 look = SpellPlans.ThroughDir(ViewRotation * Vector3.forward, qa, qb);
                float yaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
                float pitch = -Mathf.Asin(Mathf.Clamp(look.y, -1f, 1f)) * Mathf.Rad2Deg;
                Vector3 v = SpellPlans.ThroughDir(moving, qa, qb);
                // always leave moving out of it, at least a little
                float away = Vector3.Dot(v, nb);
                if (away < 2f) v += nb * (2f - away);
                _portalSkip = b.id;
                IsGrounded = false;
                _lastJumpTime = Time.time;
                MoveTo(p - _cc.center, yaw, v, pitch);
                return;
            }
        }

        // ---------------- Arcane Swiftness ----------------

        float _swiftFrom = -1f, _swiftUntil = -1f, _lastSwiftBreak;
        /// <summary>When the last Arcane Swiftness ended, or will (-1: never cast).</summary>
        public float SwiftEndsAt => _swiftUntil;
        /// <summary>Running with Arcane Swiftness right now (after the crouch): free to move, three times as fast.</summary>
        public bool Swifting => Time.time >= _swiftFrom && Time.time < _swiftUntil;
        /// <summary>(feet, direction) — the swift run hit voxels: they come apart.</summary>
        public event Action<Vector3, Vector3> SwiftBlocked;
        float SpeedScale => (Swifting ? SpellPlans.SwiftSpeedScale : 1f) * (Chilled ? SpellPlans.SlowScale : 1f) * (Strong ? SpellPlans.StrengthSpeed : 1f) * (InBile ? SpellPlans.BileSlow : 1f)
            * (Small ? SpellPlans.SmallSpeed : 1f) * SpellServer.PotSpeed(_pc) * (Concentrating ? SpellPlans.ShotChargeSpeed : 1f)
            * (_pc != null && _pc.IsSpawned && _pc.Status.Value.Under(WorldNet.Now) ? SpellPlans.SubSpeed : 1f)
            * SpellPlans.SpeedDemonScale(_pc);
        /// <summary>Shrunk by Smallify Hex: half speed.</summary>
        bool Small => _pc != null && _pc.IsSpawned && _pc.Status.Value.Small(WorldNet.Now);
        /// <summary>Standing in Death's black bile (anyone holding Death wades through it).</summary>
        bool InBile => IsGrounded && OnBile() && !SpellServer.HoldsDeath(_pc);
        /// <summary>Transformed by Strength.</summary>
        bool Strong => _pc != null && _pc.IsSpawned && _pc.Status.Value.Strong(WorldNet.Now);
        /// <summary>Breathed on by Icy Breath: half speed for a moment.</summary>
        bool Chilled => _pc != null && _pc.IsSpawned && _pc.Status.Value.Slowed(WorldNet.Now);

        /// <summary>Arcane Swiftness: crouched for <paramref name="prep"/>, then <paramref name="seconds"/> of running at three times the speed.</summary>
        public void Swift(float prep, float seconds)
        {
            Root(prep);
            _swiftFrom = Time.time + prep;
            _swiftUntil = _swiftFrom + seconds;
        }

        /// <summary>Knocked out of the crouch: it never happens.</summary>
        public void EndSwift()
        {
            if (Time.time >= _swiftUntil) return;
            if (Time.time < _swiftFrom) _rootUntil = 0f;
            _swiftUntil = Time.time;
        }

        /// <summary>Running swift into something: it comes apart in front of you (small steps you just run up).</summary>
        void SwiftCrash(Vector3 wish, Vector3 before, float dt)
        {
            Vector3 moved = transform.position - before;
            moved.y = 0f;
            float speed = new Vector2(Velocity.x, Velocity.z).magnitude;
            if (moved.magnitude > Mathf.Max(speed, runSpeed) * dt * 0.6f || Time.time - _lastSwiftBreak < 0.08f) return;
            _lastSwiftBreak = Time.time;
            SwiftBlocked?.Invoke(transform.position + _cc.center + Vector3.down * (_cc.height * 0.5f), wish.normalized);
        }
        /// <summary>Caught up in a tornado right now.</summary>
        public bool Carried => _carried;

        // ---------------- Bubble Away ----------------

        float _bubbleFrom = -1f, _bubbleUntil = -1f, _lastBubbleBreak;
        bool _inBubble;
        /// <summary>(bubble centre) — the rising bubble ran into voxels above it: break them.</summary>
        public event Action<Vector3> BubbleBlocked;

        /// <summary>Bubble Away: held still while it forms, then floating up for <paramref name="seconds"/>, steering across.</summary>
        public void Bubble(float windup, float seconds)
        {
            Root(windup);
            _bubbleFrom = Time.time + windup;
            _bubbleUntil = _bubbleFrom + seconds;
        }

        /// <summary>The bubble popped: drop out of it (you just fall from there).</summary>
        public void EndBubble()
        {
            if (Time.time >= _bubbleUntil) return;
            _bubbleUntil = Time.time;
            if (Time.time < _bubbleFrom) _rootUntil = 0f;
        }

        void BubbleTick(float dt)
        {
            _inBubble = true;
            Vector2 input = InputMap.Move;
            Vector3 wish = Quaternion.Euler(0, Yaw, 0) * new Vector3(input.x, 0, input.y);
            if (wish.sqrMagnitude > 1f) wish.Normalize();
            Vector3 h = Vector3.MoveTowards(new Vector3(Velocity.x, 0f, Velocity.z), wish * SpellPlans.BubbleMove, 10f * dt);
            Velocity = new Vector3(h.x, SpellPlans.BubbleRise, h.z);
            IsGrounded = false;
            Vector3 c = SpellPlans.BubbleCenter(transform.position);
            bool ready = Time.time - _lastBubbleBreak > 0.1f;
            if (ready && SpellPlans.BubbleRoofBlocked(c))
            {
                _lastBubbleBreak = Time.time;
                ready = false;
                BubbleBlocked?.Invoke(c);
            }
            var flags = _cc.Move(Velocity * dt);
            if (ready && (flags & CollisionFlags.Above) != 0)
            {
                _lastBubbleBreak = Time.time;
                BubbleBlocked?.Invoke(c);
            }
        }

        /// <summary>Nomadic Dash: rush along <paramref name="dir"/> (horizontal).</summary>
        public void Dash(Vector3 dir, float speed, float seconds)
        {
            dir.y = 0f;
            _dashDir = dir.sqrMagnitude > 1e-4f ? dir.normalized : transform.forward;
            _dashSpeed = speed;
            _dashUntil = Time.time + seconds;
            _siphonUntil = 0f;
        }
        float _dashSpeed;

        public void CancelDash()
        {
            if (!Dashing) return;
            _dashUntil = 0f;
            Velocity = new Vector3(_dashDir.x, 0f, _dashDir.z) * runSpeed + Vector3.up * Velocity.y;
        }

        /// <summary>Mana Siphon: charge forward, steering a little with the mouse.</summary>
        /// <param name="speed">Times its usual speed (Wheel of Fortune's 6).</param>
        public void Siphon(float seconds, float speed = 1f)
        {
            _siphonUntil = Time.time + seconds;
            _siphonSpeed = SpellPlans.SiphonSpeed * speed;
            _dashDir = Quaternion.Euler(0, Yaw, 0) * Vector3.forward;
            _dashUntil = 0f;
        }

        float _siphonSpeed = SpellPlans.SiphonSpeed;

        public void EndSiphon()
        {
            if (!Siphoning) return;
            _siphonUntil = 0f;
            Velocity = new Vector3(_dashDir.x, 0f, _dashDir.z) * 2f;
        }

        /// <summary>Magical Barrier: can't move (gravity still pulls).</summary>
        public void Root(float seconds)
        {
            _rootUntil = Time.time + seconds;
            _dashUntil = _siphonUntil = 0f;
        }

        public void Unroot() => _rootUntil = 0f;

        /// <summary>Slide by this much (walls still stop it).</summary>
        public void Nudge(Vector3 delta) { if (_cc.enabled) _cc.Move(delta); }

        /// <summary>Caught in a tornado:spun along with it until thrown out (Knock).</summary>
        public void Carry(Vector3 origin, Vector3 dir, double start, double captured, float phase)
        {
            if (Unstoppable || Held) return; // combo broken: can't be knocked down for a while
            Thaw();
            _carryOrigin = origin;
            _carryDir = dir;
            _carryStart = start;
            _carryCaptured = captured;
            _carryPhase = phase;
            _carried = true;
            _dashUntil = _siphonUntil = _rootUntil = 0f;
            if (!Ragdolled) BeginRagdoll();
            _ragdollUntil = float.MaxValue;
        }

        void CarryTick(float dt)
        {
            double now = WorldNet.Now;
            // the tornado should have thrown us out by now; if that got lost, let go
            if (now > _carryStart + SpellPlans.TornadoLife + 0.6 || now > _carryCaptured + SpellPlans.TornadoHold + 0.6)
            {
                _carried = false;
                _ragdollUntil = Time.time + 0.5f;
                return;
            }
            // spun along its side, but walls stop us (never dragged through one): held back too far, we're flung out there
            Vector3 p = SpellPlans.TornadoCarry(_carryOrigin, _carryDir, _carryStart, _carryCaptured, now, _carryPhase);
            Vector3 from = transform.position;
            _cc.Move(p - from);
            Velocity = (transform.position - from) / Mathf.Max(dt, 0.001f);
            if ((transform.position - p).magnitude > SpellPlans.TornadoSlip)
                Knock(SpellPlans.TornadoFling(transform.position, SpellPlans.TornadoBase(_carryOrigin, _carryDir, _carryStart, now), _carryDir), 1f);
        }

        /// <summary>Thrown by a spell: fly off at this velocity, limp for <paramref name="ragdollSeconds"/>.</summary>
        public void Knock(Vector3 velocity, float ragdollSeconds)
        {
            if (Hanging || Held) return; // the noose (or The World) holds you where you are
            if (Grasped && (Unstoppable || ragdollSeconds <= 0f) && !Ragdolled) return; // held by the legs: only a knockdown gets you out
            if (Unstoppable) ragdollSeconds = 0f; // combo broken: still thrown, but stays on their feet
            Thaw(); // hit: out of Stasis
            _carried = false;
            if (_pinnedTo != null)
            {
                // thrown off the front of a chariot (the crash, or a hit): don't climb straight back on before the server lets go
                _pinRefused = _pinnedTo;
                _pinRefusedUntil = Time.time + 0.6f;
                _pinnedTo = null;
            }
            if (ragdollSeconds > 0f) { _dashUntil = _siphonUntil = 0f; StopCharging(); _standAt = -1f; _standOnLanding = false; _blinkStart = -1f; } // (knocked out of a hover too)
            EndLeap(); // thrown out of the crouch or the leap: no slam
            if (_ragdollUntil == float.MaxValue) _ragdollUntil = 0f;
            Velocity = velocity;
            _lastJumpTime = Time.time; // leave the ground
            IsGrounded = false;
            if (ragdollSeconds > 0f)
            {
                if (!Ragdolled) BeginRagdoll();
                _ragdollUntil = Mathf.Max(_ragdollUntil, Time.time + ragdollSeconds);
            }
        }

        /// <summary>Combo break: can't be knocked down (ragdolled or carried) until then.</summary>
        public float UnstoppableUntil;
        public bool Unstoppable => Time.time < UnstoppableUntil;

        /// <summary>
        /// Up on your feet right now, even in mid-air (the get-up animation plays from wherever the body lay), and free
        /// to act at once. Not while stuck on the front of a chariot (the server holds that).
        /// </summary>
        public void StandUpNow()
        {
            if (_pinnedTo != null) return;
            if (_carried) { _carried = false; Velocity = Vector3.zero; }
            _getUpUntil = 0f;
            if (!Ragdolled) return;
            _ragdollUntil = 0f;
            EndRagdoll(true);
            Velocity = new Vector3(Velocity.x * 0.2f, Mathf.Min(Velocity.y, 0f) * 0.2f, Velocity.z * 0.2f);
        }

        float _standAt = -1f;
        bool _standOnLanding;
        float _landArmed;

        /// <summary>Infectious Bite: limp until the body comes down on the floor, then straight up.</summary>
        public void StandOnLanding()
        {
            _standOnLanding = true;
            _landArmed = Time.time + 0.15f; // (not the floor it just left)
        }

        /// <summary>Stay down <paramref name="seconds"/> more, then up (Infectious Bite: after the bite lands).</summary>
        public void StandUpIn(float seconds) => _standAt = Time.time + Mathf.Max(0f, seconds);

        /// <summary>Combo break: stand up now and can't be knocked down for <paramref name="seconds"/>.</summary>
        public void BreakFree(float seconds)
        {
            UnstoppableUntil = Time.time + seconds;
            bool down = Ragdolled;
            StandUpNow();
            if (!down || Ragdolled || RoomToStand()) return;
            // up off the floor where a standing body doesn't fit: break what's in the way; what won't break, crouch under
            // (being shoved out of it sideways could put you through a wall)
            StandBlocked?.Invoke(transform.position + _standCenter + Vector3.down * (_standHeight * 0.5f));
            Physics.SyncTransforms();
            if (!RoomToStand()) SetCrouch(true);
        }

        /// <summary>
        /// Combo broken: hits still throw us, on our feet, but never through anything. Whatever moved the body this frame
        /// (a hard knock, being pushed out of something it overlapped), if its middle went through a surface it goes
        /// back, and stops against it.
        /// </summary>
        void StayThisSide(Vector3 was)
        {
            Vector3 at = transform.position, d = at - was;
            float dist = d.magnitude;
            if (dist < 1e-4f || !Physics.Raycast(was, d / dist, out var hit, dist, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) return;
            if (Physics.GetIgnoreCollision(_cc, hit.collider)) return; // (passing into a portal)
            _cc.enabled = false;
            transform.position = was;
            _cc.enabled = true;
            float into = -Vector3.Dot(Velocity, hit.normal);
            if (into > 0f) Velocity += hit.normal * into;
        }

        void BeginRagdoll()
        {
            Crouching = false; // (the ball replaces the crouched shape; getting up is standing)
            Ragdolled = true;
            // the body becomes a ball around the hips
            _cc.height = _cc.radius * 2f;
            _cc.center = Vector3.zero;
        }

        /// <summary>Stand back up (needs head room; <paramref name="force"/> skips the check).</summary>
        bool EndRagdoll(bool force)
        {
            if (!Ragdolled) return true;
            float lift = -(_standCenter.y - _standHeight * 0.5f) - _cc.radius; // ball bottom -> feet
            Vector3 up = transform.position + Vector3.up * Mathf.Max(0f, lift);
            if (!force)
            {
                Vector3 a = up + _standCenter + Vector3.down * (_standHeight * 0.5f - _cc.radius) + Vector3.up * 0.05f;
                Vector3 b = up + _standCenter + Vector3.up * (_standHeight * 0.5f - _cc.radius);
                bool Blocked() => Physics.CheckCapsule(a, b, _cc.radius * 0.95f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
                if (Blocked())
                {
                    // something's on top of us: break it to make room (can't break indestructible voxels: wait)
                    Vector3 feet = up + _standCenter + Vector3.down * (_standHeight * 0.5f);
                    StandBlocked?.Invoke(feet);
                    Physics.SyncTransforms();
                    if (Blocked()) return false;
                }
            }
            Ragdolled = false;
            if (!force) _getUpUntil = Time.time + GetUpSeconds;
            _cc.enabled = false;
            _cc.height = _standHeight;
            _cc.center = _standCenter;
            transform.position = up;
            _cc.enabled = true;
            _fallSpeed = 0f;
            return true;
        }

        void Update()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            // Strength: falling twice as fast, jumping twice as high (twice the speed against twice the gravity)
            float strong = Strong ? SpellPlans.StrengthJump : 1f;
            gravity = _gravity0 * strong;
            jumpUpSpeed = _jump0 * strong;
            if (Concentrating) gravity *= SpellPlans.ShotChargeGravity; // Compressed Shot charging: falling gently
            HexTick();
            Look();
            if (Held) { Velocity = Vector3.zero; _fallSpeed = 0f; IsGrounded = true; return; }
            if (_pc != null && _pc.IsSpawned && _pc.Status.Value.Eaten(WorldNet.Now)) { EatenTick(); return; }
            if (Ephemeral) { EphemTick(dt); return; }
            if (_wasEphem)
            {
                // back: solid again (not shoved out of what we came back inside: the server decides that), drifting on a little
                _wasEphem = false;
                _cc.enabled = true;
                _blinkArrived = Time.time + 0.2f;
                Velocity *= 0.25f;
            }
            if (_frozen) { FrozenTick(); return; } // Stasis Hex: exactly where and as we are
            if (_pinnedTo != null) { PinTick(dt); return; }
            if (Hanging && (Ragdolled || _carried)) StandUpNow(); // caught by the noose: up off the floor (it can be cast while down)
            if (_carried) { CarryTick(dt); return; }
            if (_blinkStart >= 0f && !Ragdolled) { BlinkTick(); return; } // passing through: no pushing out of voxels yet
            Vector3 was = transform.position;
            // just arrived: the server's cut may still be on its way here, so don't get shoved out of the tunnel's walls
            if (Time.time > _blinkArrived + 0.35f) Depenetrate();
            _hitThisFrame = false;
            // the bubble just ended: stop rising, fall from here
            if (_inBubble && !Bubbled)
            {
                _inBubble = false;
                if (Velocity.y > 0f) Velocity.y = 0f;
            }
            // crouch only while walking about; dashing, leaping, floating and so on stand you up (when there's room)
            bool walking = !Ragdolled && !Charioting && !Leaping && !Bubbled && !Dashing && !Siphoning;
            SetCrouch(walking && (_puppet ? SpellPlans.PuppetCrouched(Master) : InputMap.Held(GameAction.Crouch)) && !GettingUp);
            CrouchBlend = Mathf.MoveTowards(CrouchBlend, Crouching ? 1f : 0f, dt * 7f);
            bool hanging = Hanging;
            // the noose lets go at the top: falling from there, not flying on up
            if (_wasHanging && !hanging && Velocity.y > 0f) Velocity.y = 0f;
            _wasHanging = hanging;
            if (hanging) HangTick(dt);
            else if (Ragdolled) RagdollTick(dt);
            else if (Charioting) ChariotTick(dt);
            else if (Grasped) GraspTick(dt);
            else if (_leapPrepUntil >= 0f) LeapPrepTick(dt);
            else if (_leapStart >= 0f) LeapTick(dt);
            else if (Bubbled) BubbleTick(dt);
            else if (Dashing) DashTick(dt);
            else if (Siphoning) SiphonTick(dt);
            else if (Reeling) ReelTick(dt);
            else MoveTick(dt);
            if (Unstoppable && !Ragdolled) StayThisSide(was);
            if (Concentrating && Velocity.y < -SpellPlans.ShotChargeFall) Velocity.y = -SpellPlans.ShotChargeFall;
            if (IsGrounded) AirSteps = 0;
            ThroughPortals();
        }

        void DashTick(float dt)
        {
            Vector3 want = _dashDir * _dashSpeed;
            Velocity = new Vector3(want.x, IsGrounded ? -2f : Velocity.y - gravity * 0.5f * dt, want.z);
            Vector3 before = transform.position;
            var flags = _cc.Move(Velocity * dt);
            if ((flags & CollisionFlags.Sides) != 0) TryClimbLedge(_dashDir, before); // small bumps don't stop you
            Vector3 moved = transform.position - before;
            moved.y = 0f;
            // ran into a wall: the dash ends there
            if ((flags & CollisionFlags.Sides) != 0 && moved.magnitude < _dashSpeed * dt * 0.4f) CancelDash();
            GroundCheck();
        }

        void SiphonTick(float dt)
        {
            // the mouse steers it a little
            Vector3 aim = Quaternion.Euler(0, Yaw, 0) * Vector3.forward;
            _dashDir = Vector3.RotateTowards(_dashDir, aim, SpellPlans.SiphonTurn * Mathf.Deg2Rad * dt, 0f);
            GroundCheck();
            Vector3 want = _dashDir * _siphonSpeed;
            Velocity = new Vector3(want.x, IsGrounded ? -2f : Velocity.y - gravity * dt, want.z);
            Vector3 before = transform.position;
            var flags = _cc.Move(Velocity * dt);
            if ((flags & CollisionFlags.Sides) != 0) TryClimbLedge(_dashDir, before);
        }

        // ---------------- getting out of voxels that appeared around us ----------------

        static CapsuleCollider _probe;
        static readonly Collider[] Overlaps = new Collider[16];

        /// <summary>
        /// Walls rise and slabs slam shut around players; if new voxels end up inside the body, step out of them
        /// (the shortest way out, or upwards if we're deep inside).
        /// </summary>
        void Depenetrate()
        {
            if (!_cc.enabled) return;
            if (_probe == null)
            {
                var go = new GameObject("MotorProbe") { layer = Layers.Fx, hideFlags = HideFlags.HideAndDontSave };
                go.transform.position = new Vector3(0, -10000, 0);
                _probe = go.AddComponent<CapsuleCollider>();
                _probe.isTrigger = true;
                _probe.direction = 1;
            }
            _probe.center = _cc.center;
            _probe.radius = _cc.radius - 0.02f;
            _probe.height = Mathf.Max(_cc.height - 0.04f, _probe.radius * 2f);
            for (int iter = 0; iter < 4; iter++)
            {
                Vector3 c = transform.position + _cc.center;
                float half = Mathf.Max(0f, _probe.height * 0.5f - _probe.radius);
                int n = Physics.OverlapCapsuleNonAlloc(c + Vector3.down * half, c + Vector3.up * half, _probe.radius, Overlaps,
                    Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
                Vector3 push = Vector3.zero;
                for (int i = 0; i < n; i++)
                {
                    var o = Overlaps[i];
                    if (o == null || o == _cc || Physics.GetIgnoreCollision(_cc, o)) continue; // (passing into a portal)
                    if (TrainCar.Of(o) != null || SpikeWalls.IsWall(o)) continue; // (a train kills; it mustn't shove you up onto its roof first)
                    if (Physics.ComputePenetration(_probe, transform.position, Quaternion.identity, o, o.transform.position, o.transform.rotation,
                            out var dir, out float dist) && dist > push.magnitude)
                        push = dir * (dist + 0.02f);
                }
                if (push == Vector3.zero) return;
                // buried deep (e.g. inside a freshly closed fold): go up instead of sideways into more ground
                if (push.magnitude > 0.3f && push.y < 0.5f) push = Vector3.up * Mathf.Max(0.3f, push.magnitude);
                _cc.enabled = false;
                transform.position += push;
                _cc.enabled = true;
                if (Vector3.Dot(Velocity, push) < 0f) Velocity -= Vector3.Project(Velocity, push.normalized);
            }
        }

        void Look()
        {
            Vector2 d = InputMap.LookDelta;
            // charging: the mouse only says which way to turn; it turns at a fixed rate whatever the sensitivity
            float turn = Charioting
                ? (Mathf.Abs(d.x) > 0.01f ? Mathf.Sign(d.x) * SpellPlans.ChariotTurnRate * Time.deltaTime : 0f)
                : d.x;
            Yaw = Mathf.Repeat(Yaw + turn, 360f);
            Pitch = Mathf.Clamp(Pitch - d.y, minPitch, maxPitch);
            if (!Ragdolled) transform.rotation = Quaternion.Euler(0, BodyYaw, 0);
        }

        // ---------------- walking ----------------

        void MoveTick(float dt)
        {
            bool frozen = GettingUp || Rooted || Kneeling || MatchManager.MovementLockedFor(_pc); // the 3-2-1: look around, but stay put
            // Mind Hex: it's the caster who walks and jumps us about
            if ((_puppet ? _jumpWanted : InputMap.Down(GameAction.Jump)) && !frozen) _jumpPressedTime = Time.time;
            _jumpWanted = false;
            Vector2 input = frozen || _puppet ? Vector2.zero : InputMap.Move;
            Quaternion yaw = Quaternion.Euler(0, Yaw, 0);
            Vector3 wish = yaw * new Vector3(input.x, 0, input.y);
            if (_puppet && !frozen) wish = SpellPlans.PuppetWish(Master);
            if (wish.sqrMagnitude > 1f) wish.Normalize();

            bool wasGrounded = IsGrounded;
            GroundCheck();
            if (RisingWater.FloatLine(transform.position, out float floatAt)) { SwimTick(dt, wish, floatAt); return; }
            RideTrain(dt);
            if (IsGrounded)
            {
                if (!wasGrounded && _fallSpeed > SafeFallSpeed) HardLanding?.Invoke(_fallSpeed);
                _lastGroundedTime = Time.time;
                _fallSpeed = 0f;
                // walking on ice (Icy Breath's): your feet go out from under you
                if (HorizontalSpeed > SpellPlans.IceSlipSpeed && Time.time >= _iceGraceUntil && OnIce())
                {
                    _iceGraceUntil = Time.time + SpellPlans.IceSlipRagdoll + GetUpSeconds + 1.2f; // time to get off it
                    Knock(new Vector3(Velocity.x * 1.3f, 3f, Velocity.z * 1.3f), SpellPlans.IceSlipRagdoll);
                    return;
                }
            }

            bool jumpQueued = Time.time - _jumpPressedTime <= jumpBuffer;
            bool canJump = Time.time - _lastGroundedTime <= coyoteTime && Time.time - _lastJumpTime > 0.25f;
            Vector3 h = new(Velocity.x, 0, Velocity.z);
            if (jumpQueued && canJump)
            {
                Velocity.y = jumpUpSpeed;
                _lastJumpTime = Time.time;
                _jumpPressedTime = -10f;
                IsGrounded = false;
            }
            if (IsGrounded && Time.time - _lastJumpTime > 0.1f)
            {
                Vector3 target = wish * (runSpeed * SpeedScale * (Crouching ? CrouchSpeedScale : 1f));
                float rate = (wish.sqrMagnitude > 0.01f ? groundAccel : groundDecel) * SpeedScale;
                h = Vector3.MoveTowards(h, target, rate * dt);
                Velocity = new Vector3(h.x, -2f, h.z);
            }
            else
            {
                // air: steer, but never faster than running (or what you already had)
                float before = h.magnitude;
                h += wish * (airControl * SpeedScale * dt);
                float cap = Mathf.Max(before, runSpeed * SpeedScale);
                if (h.magnitude > cap) h = h.normalized * cap;
                Velocity.x = h.x;
                Velocity.z = h.z;
                Velocity.y -= gravity * dt;
                _fallSpeed = Mathf.Max(_fallSpeed, -Velocity.y);
            }

            bool snap = IsGrounded && Time.time - _lastJumpTime > 0.1f;
            Vector3 before3 = transform.position;
            Vector3 meant = new(Velocity.x, 0f, Velocity.z);
            var flags = _cc.Move(Velocity * dt);
            // walking, but held back (voxel stairs catch the capsule on their edges, which doesn't always count as
            // hitting a side): step up, and keep the speed the edge took away
            bool stepped = false;
            if (snap && wish.sqrMagnitude > 0.01f && !Swifting)
            {
                Vector3 moved = transform.position - before3;
                float want = Vector3.Dot(meant, wish.normalized) * dt;
                float got = Vector3.Dot(new Vector3(moved.x, 0f, moved.z), wish.normalized);
                if ((want <= 1e-4f || got < want * 0.6f) && TryStepUp(wish))
                {
                    stepped = true;
                    Vector3 keep = wish.normalized * Mathf.Max(meant.magnitude, runSpeed * SpeedScale * 0.6f);
                    Velocity.x = keep.x;
                    Velocity.z = keep.z;
                }
            }
            if (!stepped && (flags & CollisionFlags.Sides) != 0 && wish.sqrMagnitude > 0.01f)
            {
                if (Swifting) SwiftCrash(wish, before3, dt);
                TryClimbLedge(wish, before3);
            }

            // stick to the ground going down steps / slopes
            if (snap && (_cc.collisionFlags & CollisionFlags.Below) == 0 &&
                SphereDown(out var hit, _cc.stepOffset + 0.1f) && hit.normal.y > 0.6f)
                _cc.Move(Vector3.down * hit.distance);
        }

        // ---------------- map hazards: water, trains ----------------

        /// <summary>Afloat in rising water: bobbing at the surface, swimming slowly, able to jump out (but not to dive).</summary>
        void SwimTick(float dt, Vector3 wish, float line)
        {
            IsGrounded = false;
            _fallSpeed = 0f;
            Vector3 h = Vector3.MoveTowards(new Vector3(Velocity.x, 0f, Velocity.z), wish * (runSpeed * SpeedScale * RisingWater.SwimSpeed), 25f * dt);
            float y = transform.position.y;
            float vy = RisingWater.Buoy(Velocity.y, y, line, dt);
            if (Time.time - _jumpPressedTime <= jumpBuffer && Time.time - _lastJumpTime > 0.4f && y > line - 0.5f)
            {
                vy = jumpUpSpeed;
                _lastJumpTime = Time.time;
                _jumpPressedTime = -10f;
            }
            Velocity = new Vector3(h.x, vy, h.z);
            Vector3 before = transform.position;
            var flags = _cc.Move(Velocity * dt);
            if ((flags & CollisionFlags.Sides) != 0 && wish.sqrMagnitude > 0.01f) TryClimbLedge(wish, before);
        }

        Collider _groundCollider;
        Vector3 _trainVelocity;

        /// <summary>Standing on a train's roof: carried along with it (and off it, at its speed, when you jump).</summary>
        void RideTrain(float dt)
        {
            var car = IsGrounded ? TrainCar.Of(_groundCollider) : null;
            if (car != null)
            {
                _trainVelocity = car.Velocity;
                _cc.Move(_trainVelocity * dt);
            }
            else
            {
                if (_trainVelocity != Vector3.zero && !IsGrounded) Velocity += _trainVelocity;
                _trainVelocity = Vector3.zero;
            }
        }

        // ---------------- ice ----------------

        float _iceGraceUntil;

        // ---------------- Compressed Shot, Ephemerate, Cloud Step ----------------

        /// <summary>Charging Compressed Shot (set by the owner's caster): half speed, falling gently.</summary>
        public bool Concentrating;
        float _ephemUntil = -1f;
        bool _wasEphem;
        /// <summary>Gone with Ephemerate: flying where we aim, through anything (not out of the arena).</summary>
        public bool Ephemeral => Time.time < _ephemUntil;
        /// <summary>When the last Ephemerate ended, or will (-1: never cast).</summary>
        public float EphemEndsAt => _ephemUntil;
        /// <summary>Cloud Steps taken since we last stood on something.</summary>
        public int AirSteps;

        public void Ephemerate(float seconds)
        {
            if (Ragdolled) StandUpNow();
            _ephemUntil = Time.time + seconds;
            _wasEphem = true;
            _ephemMirror = Vector3.one;
        }

        Vector3 _ephemMirror = Vector3.one;

        /// <summary>Somewhere Ephemerate can't go: out of the arena's sides or top, or down near where we'd fall out.</summary>
        static bool EphemOut(Vector3 p) => ArenaLimits.Outside(p, 0.5f) || p.y < Practice.KillYAt(p) + 2f;

        void EphemTick(float dt)
        {
            _cc.enabled = false;
            // where we aim, mirrored on each side of the arena we've bounced off this time
            Vector3 dir = Vector3.Scale(ViewRotation * Vector3.forward, _ephemMirror);
            Vector3 here = transform.position, next = here + dir * (SpellPlans.EphemSpeed * dt);
            if (EphemOut(next))
            {
                // at the edge: bounce off it (that way reversed from now on), each way on its own
                Vector3 step = next - here, p = here;
                for (int a = 0; a < 3; a++)
                {
                    Vector3 axis = Vector3.zero; axis[a] = 1f;
                    Vector3 q = p + Vector3.Scale(step, axis);
                    if (!EphemOut(q)) { p = q; continue; }
                    _ephemMirror[a] = -_ephemMirror[a];
                    dir[a] = -dir[a];
                    q = p - Vector3.Scale(step, axis);
                    if (!EphemOut(q)) p = q;
                }
                next = p;
            }
            transform.position = next;
            Velocity = dir * SpellPlans.EphemSpeed;
            IsGrounded = false;
            _fallSpeed = 0f;
        }

        /// <summary>Cloud Step: a jump three times as high, from wherever we are (counted, in the air).</summary>
        public void CloudStep()
        {
            if (!IsGrounded) AirSteps++;
            Knock(new Vector3(Velocity.x, jumpUpSpeed * Mathf.Sqrt(SpellPlans.CloudJump), Velocity.z), 0f);
        }

        /// <summary>Inside a fly trap: wherever its mouth is (held, on the ground or thrown), up off the floor.</summary>
        void EatenTick()
        {
            // limp the whole time (the ragdoll is steered to where we're held), unless a combo break just got us out
            if (!Unstoppable)
            {
                if (!Ragdolled) BeginRagdoll();
                _ragdollUntil = Mathf.Max(_ragdollUntil, Time.time + 0.3f);
            }
            var st = _pc.Status.Value;
            // (the body's own collider is off meanwhile: see EatenBodyFx; it would shove its eater about)
            if (WorldNet.PotMouth(st.EatenBy, WorldNet.Now, out var mouth)) transform.position = mouth + Vector3.down * SpellPlans.EatenDrop;
            Velocity = Vector3.zero;
            IsGrounded = false;
            _fallSpeed = 0f;
        }

        /// <summary>Standing on Death's black bile?</summary>
        bool OnBile()
        {
            var w = VoxelWorld.Current;
            if (w == null || _cc == null) return false;
            Vector3 feet = transform.position + _cc.center + Vector3.down * (_cc.height * 0.5f);
            return w.IsBile(w.Get(w.CellOf(feet + Vector3.down * 0.1f)));
        }

        /// <summary>Standing on ice?</summary>
        bool OnIce()
        {
            var w = VoxelWorld.Current;
            if (w == null) return false;
            Vector3 feet = transform.position + _cc.center + Vector3.down * (_cc.height * 0.5f);
            return w.IsIce(w.Get(w.CellOf(feet + Vector3.down * 0.1f)));
        }

        // ---------------- The Hanged Man ----------------

        /// <summary>Yanked up by The Hanged Man's noose: no walking (turning and casting still work).</summary>
        public bool Hanging => _pc != null && _pc.IsSpawned && _pc.Status.Value.Hanging(WorldNet.Now);
        bool _wasHanging;

        void HangTick(float dt)
        {
            SetCrouch(false);
            Velocity = new Vector3(0f, SpellPlans.HangedRise, 0f);
            _cc.Move(Velocity * dt); // (what the body runs into breaks: the server clears it, see SpellServer.TickHanged)
            IsGrounded = false;
            _fallSpeed = 0f;
            _lastGroundedTime = Time.time; // (a jump straight after letting go still works)
        }

        // ---------------- Undying Grasp ----------------

        /// <summary>Held by the legs by Undying Grasp.</summary>
        public bool Grasped => _pc != null && _pc.Grasped;

        void GraspTick(float dt)
        {
            // whatever you were in the middle of stops there
            _dashUntil = _siphonUntil = 0f;
            _reeling = false;
            EndLeap();
            GroundCheck();
            Velocity = new Vector3(0f, IsGrounded ? -2f : Mathf.Min(Velocity.y, 0f) - gravity * dt, 0f);
            _cc.Move(Velocity * dt);
        }

        // ---------------- Grapple Hook ----------------

        bool _reeling;
        Vector3 _reelTo;
        float _reelStart;

        /// <summary>Reeled in towards a grapple hook stuck in the ground.</summary>
        public bool Reeling => _reeling;

        public void Reel(Vector3 anchor)
        {
            _reeling = true;
            _reelTo = anchor;
            _reelStart = Time.time;
            _dashUntil = _siphonUntil = 0f;
        }

        public void EndReel() => _reeling = false;

        /// <summary>The rope's pull (eased off right at the hook so you don't jitter about on it).</summary>
        void ReelPull(float dt)
        {
            Vector3 to = _reelTo - transform.position;
            float dist = to.magnitude;
            if (dist <= SpellPlans.HookArrive || Time.time - _reelStart > SpellPlans.HookMaxTime) { _reeling = false; return; }
            Velocity += to / dist * (SpellPlans.HookPull * Mathf.Clamp01((dist - 0.8f) / 2f) * dt);
            if (Velocity.magnitude > SpellPlans.HookMaxSpeed) Velocity = Velocity.normalized * SpellPlans.HookMaxSpeed;
        }

        void ReelTick(float dt)
        {
            Vector2 input = InputMap.Move;
            Vector3 wish = Quaternion.Euler(0, Yaw, 0) * new Vector3(input.x, 0, input.y);
            Velocity.y -= gravity * dt;
            Velocity += wish * (airControl * 0.6f * dt); // a little steering on the way
            ReelPull(dt);
            Vector3 before = transform.position;
            var flags = _cc.Move(Velocity * dt);
            // pulled into a wall short of the hook (it's in the side of it): slide up it rather than sticking
            if ((flags & CollisionFlags.Sides) != 0 && (transform.position - before).magnitude < Velocity.magnitude * dt * 0.3f) Velocity.y = Mathf.Max(Velocity.y, 4f);
            IsGrounded = false;
            _fallSpeed = 0f;
            if (!_reeling) _lastGroundedTime = Time.time; // arrived: a jump off it still works
        }

        /// <summary>Steps walked straight up, without slowing: one voxel, or two.</summary>
        static readonly float[] StepHeights = { VoxelWorld.Size + 0.02f, VoxelWorld.Size * 2f + 0.02f };

        /// <summary>
        /// On the ground, walking into a step one or two voxels high: up onto it. Only looks a hand's width ahead, so
        /// steep stairs work too: steps shallower than the body is wide (where the character controller's own
        /// stepping, and the ledge climb below, hit the next step up and give up). Climbs one step a frame.
        /// </summary>
        bool TryStepUp(Vector3 dir)
        {
            if (!IsGrounded) return false;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) return false;
            dir.Normalize();
            float r = _cc.radius, half = Mathf.Max(0f, _cc.height * 0.5f - r);
            Vector3 feet = transform.position + _cc.center + Vector3.down * (_cc.height * 0.5f);
            foreach (float h in StepHeights)
            {
                Vector3 pos = transform.position + Vector3.up * h + dir * 0.12f;
                Vector3 c = pos + _cc.center;
                // room for the whole body there
                if (Physics.CheckCapsule(c + Vector3.down * half + Vector3.up * 0.02f, c + Vector3.up * half, r * 0.95f,
                        Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                // and a step to stand on (not a gap, or an overhang we're under)
                Vector3 from = c + Vector3.down * half;
                if (!Physics.SphereCast(from, r * 0.9f, Vector3.down, out var hit, h + r, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                float rise = hit.point.y - feet.y;
                // (the cast often meets the step on its edge, so the normal comes out slanted: the room check already rules out walls)
                if (hit.normal.y < 0.2f || rise < 0.05f || rise > h + 0.01f) continue;
                _cc.enabled = false;
                transform.position = pos + Vector3.down * Mathf.Max(0f, h - rise - 0.01f);
                _cc.enabled = true;
                Velocity.y = Mathf.Max(0f, Velocity.y);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Walking (or jumping) into a voxel ledge that's not too high: pop up onto it. This is what lets you climb
        /// out of craters and up broken terrain without needing perfect jumps.
        /// </summary>
        void TryClimbLedge(Vector3 wish, Vector3 from)
        {
            if (Velocity.y > 3f) return; // still rising from a jump: let it play out
            Vector3 dir = wish.normalized;
            if (TryStepUp(dir)) return;
            Vector3 feet = transform.position + _cc.center + Vector3.down * (_cc.height * 0.5f);
            // something solid right in front, low down?
            if (!Physics.Raycast(feet + Vector3.up * 0.1f, dir, _cc.radius + 0.3f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                return;
            // find the top of it
            Vector3 probe = feet + dir * (_cc.radius + 0.2f) + Vector3.up * (ledgeClimbHeight + 0.05f);
            if (!Physics.Raycast(probe, Vector3.down, out var top, ledgeClimbHeight + 0.05f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                return;
            if (top.normal.y < 0.7f) return;
            float rise = top.point.y - feet.y;
            if (rise <= 0.02f || rise > ledgeClimbHeight) return;
            // room to stand up there?
            Vector3 standPos = transform.position + Vector3.up * (rise + 0.02f) + dir * 0.15f;
            Vector3 a = standPos + _cc.center + Vector3.down * (_cc.height * 0.5f - _cc.radius) + Vector3.up * 0.04f;
            Vector3 b = standPos + _cc.center + Vector3.up * (_cc.height * 0.5f - _cc.radius);
            if (Physics.CheckCapsule(a, b, _cc.radius * 0.95f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) return;
            _cc.enabled = false;
            transform.position = standPos;
            _cc.enabled = true;
            Velocity.y = Mathf.Max(0f, Velocity.y);
        }

        void GroundCheck()
        {
            if (Velocity.y > 0.5f && Time.time - _lastJumpTime < 0.25f) { IsGrounded = false; return; }
            if (SphereDown(out var hit, 0.12f) && hit.normal.y >= Mathf.Cos(_cc.slopeLimit * Mathf.Deg2Rad))
            {
                IsGrounded = true;
                _groundNormal = hit.normal;
                _groundCollider = hit.collider;
            }
            else if (hit.collider != null && OnStepEdge(hit))
            {
                IsGrounded = true;
                _groundNormal = Vector3.up;
                _groundCollider = hit.collider;
            }
            else
            {
                IsGrounded = _cc.isGrounded && Velocity.y <= 0f;
                if (hit.collider != null || !IsGrounded) _groundCollider = hit.collider;
                _groundNormal = Vector3.up;
            }
        }

        /// <summary>
        /// Standing on stairs: the round bottom of the body rests on a step's edge (so the ground looks steep), with a
        /// tread a step or so below the middle of the feet. That's still standing, not sliding off a cliff.
        /// </summary>
        bool OnStepEdge(RaycastHit hit)
        {
            if (hit.normal.y < 0.2f) return false;
            Vector3 feet = transform.position + _cc.center + Vector3.down * (_cc.height * 0.5f);
            if (hit.point.y < feet.y - 0.2f) return false;
            return Physics.Raycast(feet + Vector3.up * 0.1f, Vector3.down, out var below, 0.1f + _cc.stepOffset,
                Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) && below.normal.y > 0.7f;
        }

        bool SphereDown(out RaycastHit hit, float distance)
        {
            float r = _cc.radius * 0.95f;
            Vector3 bottomSphere = transform.position + _cc.center + Vector3.down * (_cc.height * 0.5f - _cc.radius);
            Vector3 origin = bottomSphere + Vector3.up * 0.05f;
            return Physics.SphereCast(origin, r, Vector3.down, out hit, distance + 0.05f + _cc.skinWidth,
                Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        // ---------------- ragdoll ----------------

        void RagdollTick(float dt)
        {
            bool afloat = RisingWater.FloatLine(transform.position, out float line);
            if (afloat)
            {
                Velocity.y = RisingWater.Buoy(Velocity.y, transform.position.y, line, dt);
                _fallSpeed = 0f;
            }
            else Velocity.y -= gravity * dt;
            // thrown up off lava: back on your feet in the air, so there's a chance to get clear of it
            if (Time.time >= _ragdollUntil && _standAt < 0f && !_standOnLanding && LavaFlood.Below(transform.position) && EndRagdoll(false))
            {
                _getUpUntil = 0f;
                return;
            }
            if (Reeling) ReelPull(dt); // a grapple hook already stuck keeps reeling you in, limp or not
            if (_standAt >= 0f && Time.time >= _standAt) { _standAt = -1f; StandUpNow(); return; }
            var flags = _cc.Move(Velocity * dt);
            bool onGround = afloat || (flags & CollisionFlags.Below) != 0;
            IsGrounded = onGround;
            if (_standOnLanding && onGround && Time.time >= _landArmed) { _standOnLanding = false; StandUpNow(); return; }
            if (onGround)
            {
                // tumble to a stop
                float k = Mathf.Exp(-4f * dt);
                Velocity.x *= k;
                Velocity.z *= k;
            }
            bool slow = Velocity.sqrMagnitude < 4f;
            if (Time.time >= _ragdollUntil && (slow || Time.time > _ragdollUntil + 1.5f) && (onGround || Time.time > _ragdollUntil + 3f))
                EndRagdoll(Time.time > _ragdollUntil + 4f);
        }

        // The velocity a move was made with, before the first thing it hit changed it (sliding along walls, bouncing):
        // walking into a portal's slab zeroes the part going into it, but it still counts as going in.
        Vector3 _intendedVelocity;
        bool _hitThisFrame;

        void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (!_hitThisFrame) { _hitThisFrame = true; _intendedVelocity = Velocity; }
            if (_lastHitCollider == null) { _lastHitCollider = hit.collider; _lastHitPoint = hit.point; _lastHitNormal = hit.normal; }
            Vector3 n = hit.normal;
            float into = -Vector3.Dot(Velocity, n);
            if (!Ragdolled)
            {
                if (n.y > 0.7f) return; // floor
                // the edge of a step at your feet, walking: step over it rather than losing speed to it
                if (n.y > 0.1f && IsGrounded &&
                    hit.point.y < transform.position.y + _cc.center.y - _cc.height * 0.5f + _cc.stepOffset) return;
                if (into > 0f) Velocity += n * into; // slide along walls
                if (n.y < -0.5f && Velocity.y > 0f) Velocity.y = 0f; // ceiling
                return;
            }
            if (into <= 0f) return;
            // smashing into breakable voxels: go through them (floors only at very high speed)
            bool floor = n.y > 0.6f;
            float needed = floor ? CrashSpeed * 2f : CrashSpeed;
            if (into >= needed && Time.time - _lastCrash > 0.08f && IsBreakable(hit))
            {
                _lastCrash = Time.time;
                Crashed?.Invoke(hit.point - n * 0.1f, Velocity);
                Velocity *= 0.7f;
                return;
            }
            // otherwise bounce a little
            Velocity += n * into * 1.25f;
        }

        static bool IsBreakable(ControllerColliderHit hit)
        {
            var w = VoxelWorld.Current;
            if (w == null || !VoxelWorld.IsVoxelCollider(hit.collider)) return false;
            ushort v = w.Get(w.CellOf(hit.point - hit.normal * 0.1f));
            return v != 0 && !w.IsIndestructible(v);
        }
    }
}
