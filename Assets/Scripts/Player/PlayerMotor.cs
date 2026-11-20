using System;
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
        float _standHeight;
        Vector3 _standCenter;
        float _lastGroundedTime = -10f, _jumpPressedTime = -10f, _lastJumpTime = -10f;
        float _ragdollUntil, _lastCrash, _getUpUntil;
        /// <summary>After a knockdown you can't move until you've stood up (matches the get-up animation).</summary>
        const float GetUpSeconds = 0.5f;
        public bool GettingUp => Time.time < _getUpUntil;
        Vector3 _groundNormal = Vector3.up;
        float _fallSpeed;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _standHeight = _cc.height;
            _standCenter = _cc.center;
        }

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
            if (_ragdollUntil == float.MaxValue) _ragdollUntil = 0f;
            EndRagdoll(true);
            _cc.enabled = false;
            transform.position = pos;
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
        /// <summary>Floating in a Bubble Away right now.</summary>
        public bool Bubbled => Time.time >= _bubbleFrom && Time.time < _bubbleUntil;
        public bool Busy => Dashing || Siphoning || Rooted || _carried || Time.time < _bubbleUntil || Time.time < _swiftUntil;

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
        /// <summary>Running with Arcane Swiftness right now (after the crouch): free to move, three times as fast.</summary>
        public bool Swifting => Time.time >= _swiftFrom && Time.time < _swiftUntil;
        /// <summary>(feet, direction) — the swift run hit voxels: they come apart.</summary>
        public event Action<Vector3, Vector3> SwiftBlocked;
        float SpeedScale => Swifting ? SpellPlans.SwiftSpeedScale : 1f;

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
        public void Siphon(float seconds)
        {
            _siphonUntil = Time.time + seconds;
            _dashDir = Quaternion.Euler(0, Yaw, 0) * Vector3.forward;
            _dashUntil = 0f;
        }

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

        /// <summary>Caught in a tornado: spun along with it until thrown out (Knock).</summary>
        public void Carry(Vector3 origin, Vector3 dir, double start, double captured, float phase)
        {
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
            if (now > _carryStart + SpellPlans.TornadoLife + 0.6)
            {
                _carried = false;
                _ragdollUntil = Time.time + 0.5f;
                return;
            }
            Vector3 p = SpellPlans.TornadoCarry(_carryOrigin, _carryDir, _carryStart, _carryCaptured, now, _carryPhase);
            Velocity = (p - transform.position) / Mathf.Max(dt, 0.001f);
            _cc.enabled = false;
            transform.position = p;
            _cc.enabled = true;
        }

        /// <summary>Thrown by a spell: fly off at this velocity, limp for <paramref name="ragdollSeconds"/>.</summary>
        public void Knock(Vector3 velocity, float ragdollSeconds)
        {
            _carried = false;
            if (ragdollSeconds > 0f) { _dashUntil = _siphonUntil = 0f; }
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

        void BeginRagdoll()
        {
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
            Look();
            if (_carried) { CarryTick(dt); return; }
            Depenetrate();
            _hitThisFrame = false;
            // the bubble just ended: stop rising, fall from here
            if (_inBubble && !Bubbled)
            {
                _inBubble = false;
                if (Velocity.y > 0f) Velocity.y = 0f;
            }
            if (Ragdolled) RagdollTick(dt);
            else if (Bubbled) BubbleTick(dt);
            else if (Dashing) DashTick(dt);
            else if (Siphoning) SiphonTick(dt);
            else MoveTick(dt);
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
            _dashDir = Vector3.RotateTowards(_dashDir, aim, 100f * Mathf.Deg2Rad * dt, 0f);
            GroundCheck();
            Vector3 want = _dashDir * SpellPlans.SiphonSpeed;
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
            Yaw = Mathf.Repeat(Yaw + d.x, 360f);
            Pitch = Mathf.Clamp(Pitch - d.y, minPitch, maxPitch);
            if (!Ragdolled) transform.rotation = Quaternion.Euler(0, Yaw, 0);
        }

        // ---------------- walking ----------------

        void MoveTick(float dt)
        {
            bool frozen = GettingUp || Rooted;
            if (InputMap.Down(GameAction.Jump) && !frozen) _jumpPressedTime = Time.time;
            Vector2 input = frozen ? Vector2.zero : InputMap.Move;
            Quaternion yaw = Quaternion.Euler(0, Yaw, 0);
            Vector3 wish = yaw * new Vector3(input.x, 0, input.y);
            if (wish.sqrMagnitude > 1f) wish.Normalize();

            bool wasGrounded = IsGrounded;
            GroundCheck();
            if (IsGrounded)
            {
                if (!wasGrounded && _fallSpeed > SafeFallSpeed) HardLanding?.Invoke(_fallSpeed);
                _lastGroundedTime = Time.time;
                _fallSpeed = 0f;
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
                Vector3 target = wish * (runSpeed * SpeedScale);
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
            var flags = _cc.Move(Velocity * dt);
            if ((flags & CollisionFlags.Sides) != 0 && wish.sqrMagnitude > 0.01f)
            {
                if (Swifting) SwiftCrash(wish, before3, dt);
                TryClimbLedge(wish, before3);
            }

            // stick to the ground going down steps / slopes
            if (snap && (_cc.collisionFlags & CollisionFlags.Below) == 0 &&
                SphereDown(out var hit, _cc.stepOffset + 0.1f) && hit.normal.y > 0.6f)
                _cc.Move(Vector3.down * hit.distance);
        }

        /// <summary>
        /// Walking (or jumping) into a voxel ledge that's not too high: pop up onto it. This is what lets you climb
        /// out of craters and up broken terrain without needing perfect jumps.
        /// </summary>
        void TryClimbLedge(Vector3 wish, Vector3 from)
        {
            if (Velocity.y > 3f) return; // still rising from a jump: let it play out
            Vector3 dir = wish.normalized;
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
            }
            else
            {
                IsGrounded = _cc.isGrounded && Velocity.y <= 0f;
                _groundNormal = Vector3.up;
            }
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
            Velocity.y -= gravity * dt;
            var flags = _cc.Move(Velocity * dt);
            bool onGround = (flags & CollisionFlags.Below) != 0;
            IsGrounded = onGround;
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
            Vector3 n = hit.normal;
            float into = -Vector3.Dot(Velocity, n);
            if (!Ragdolled)
            {
                if (n.y > 0.7f) return; // floor
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
