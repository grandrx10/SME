using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// A target that stands still, for trying spells on. It falls with gravity (also through a floor you break),
    /// gets knocked back and ragdolls like a player, dies at 0 HP, and is back at its spot (dropped onto the
    /// ground) at the start of every round. Dummies never decide who wins.
    /// Simulated on the server, replicated to everyone. Add one via Sudden Mage Encounter > Add Test Dummy.
    /// </summary>
    public class TestDummy : Combatant
    {
        const float Gravity = 20f, BallRadius = 0.35f, HipHeight = 0.95f;

        readonly NetworkVariable<Vector3> _netPos = new();
        readonly NetworkVariable<Vector3> _netVel = new();
        readonly NetworkVariable<bool> _netRagdoll = new();

        public override Vector3 ViewVelocity => _netVel.Value;
        public override float ViewYaw => transform.eulerAngles.y;
        public override float ViewPitch => 0f;
        public override byte ViewFlags => (byte)((_grounded ? NetState.Grounded : 0) | (_netRagdoll.Value ? NetState.Ragdoll : 0));
        public override string FeedName => "Test Dummy";

        Vector3 _homePos;
        Quaternion _homeRot;
        // server simulation
        Vector3 _vel;
        bool _ragdoll, _grounded = true;
        float _ragdollUntil;

        protected override void Awake()
        {
            base.Awake();
            _homePos = transform.position;
            _homeRot = transform.rotation;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (IsServer) _netPos.Value = transform.position;
            else transform.position = _netPos.Value;
            SetVisible(!Dead.Value);
        }

        void Update()
        {
            if (!IsSpawned) return;
            if (IsServer) Simulate(Mathf.Min(Time.deltaTime, 0.05f));
            else
            {
                Vector3 target = _netPos.Value;
                transform.position = (transform.position - target).sqrMagnitude > 9f
                    ? target
                    : Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-20f * Time.deltaTime));
                _grounded = !_netRagdoll.Value && _netVel.Value.sqrMagnitude < 0.5f;
            }
            UpdateLiveRagdoll(_netRagdoll.Value);
        }

        // ------------------------------------------------------------------ server

        [Tooltip("Seconds after dying before the dummy stands back up at its spot.")]
        public float respawnDelay = 3f;
        float _diedAt = -1f;

        void Simulate(float dt)
        {
            if (Dead.Value)
            {
                if (_diedAt < 0f) _diedAt = Time.time;
                else if (Time.time - _diedAt >= respawnDelay && Arena.Current != null) ServerReset();
                return;
            }
            _diedAt = -1f;
            // only once there's an arena to stand on
            if (!IsAlive || Arena.Current == null || !MatchManager.Running) return;
            Vector3 pos = transform.position;
            int mask = Layers.EnvironmentMask;

            if (_carried)
            {
                double now = WorldNet.Now;
                Vector3 p = SpellPlans.TornadoCarry(_carryOrigin, _carryDir, _carryStart, _carryCaptured, now, _carryPhase);
                _vel = (p - pos) / Mathf.Max(dt, 0.001f);
                pos = p;
                if (now > _carryStart + SpellPlans.TornadoLife + 0.6) { _carried = false; _ragdollUntil = Time.time + 0.5f; }
            }
            else if (_ragdoll)
            {
                // a ball around the hips: flies, bounces, rolls to a stop
                pos = PushOut(pos);
                _vel.y -= Gravity * dt;
                Vector3 step = _vel * dt;
                float dist = step.magnitude;
                if (dist > 1e-5f && Physics.SphereCast(pos, BallRadius, step / dist, out var hit, dist, mask, QueryTriggerInteraction.Ignore))
                {
                    pos += step / dist * Mathf.Max(0f, hit.distance - 0.01f);
                    float into = -Vector3.Dot(_vel, hit.normal);
                    if (into > 0f) _vel += hit.normal * into * 1.3f;
                    if (hit.normal.y > 0.6f) { _vel.x *= 0.85f; _vel.z *= 0.85f; }
                }
                else pos += step;
                bool onGround = Physics.SphereCast(pos, BallRadius * 0.95f, Vector3.down, out _, 0.08f, mask, QueryTriggerInteraction.Ignore);
                if (onGround) { float k = Mathf.Exp(-4f * dt); _vel.x *= k; _vel.z *= k; }
                if (Time.time > _ragdollUntil && onGround && _vel.sqrMagnitude < 4f)
                {
                    // stand back up right where the body lies: feet at the bottom of the ball, hips above them
                    Vector3 feet = pos + Vector3.down * BallRadius;
                    if (StandBlocked(feet)) SpellServer.ClearStandSpace(feet); // break whatever's on top
                    _ragdoll = false;
                    _vel = Vector3.zero;
                    pos = feet + Vector3.up * HipHeight;
                }
            }
            else
            {
                // standing: feet on the ground, or falling straight down
                float down = Mathf.Max(0f, -_vel.y * dt) + 0.05f;
                if (_vel.y <= 0f && Physics.Raycast(pos, Vector3.down, out var ground, HipHeight + down, mask, QueryTriggerInteraction.Ignore))
                {
                    pos.y = ground.point.y + HipHeight;
                    _vel = Vector3.zero;
                    _grounded = true;
                }
                else
                {
                    _grounded = false;
                    _vel.y -= Gravity * dt;
                    pos += _vel * dt;
                }
            }

            // through a linked portal: out of the other one
            // (the dummy's ball counts as in when it touches the opening, and comes out that far in front of the other one)
            if (WorldNet.PassPortal(transform.position, pos, BallRadius + 0.05f, 0f, out var portalIn, out var portalOut))
            {
                pos = SpellPlans.ThroughPoint(pos, portalIn.center, portalIn.Rotation, portalOut.center, portalOut.Rotation, true);
                _vel = SpellPlans.ThroughDir(_vel, portalIn.Rotation, portalOut.Rotation);
                Vector3 facing = SpellPlans.ThroughDir(transform.forward, portalIn.Rotation, portalOut.Rotation);
                facing.y = 0f;
                if (facing.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(facing);
            }
            transform.position = pos;
            if (_netPos.Value != pos) _netPos.Value = pos;
            if (_netVel.Value != _vel) _netVel.Value = _vel;
            if (_netRagdoll.Value != _ragdoll) _netRagdoll.Value = _ragdoll;

            if (pos.y < Arena.Current.KillY || KillVolume.AnyContains(pos)) ServerKillByHazard(DeathCause.Barrier, pos);
        }

        static bool StandBlocked(Vector3 feet)
        {
            Vector3 a = feet + Vector3.up * (0.35f + 0.08f), b = feet + Vector3.up * (1.95f - 0.35f);
            return Physics.CheckCapsule(a, b, 0.33f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        static SphereCollider _probe;
        static readonly Collider[] Overlaps = new Collider[16];

        /// <summary>If the ball has sunk into something (fast hit, voxels appearing around it), push it back out.</summary>
        static Vector3 PushOut(Vector3 pos)
        {
            if (_probe == null)
            {
                var go = new GameObject("DummyProbe") { layer = Layers.Fx, hideFlags = HideFlags.HideAndDontSave };
                go.transform.position = new Vector3(0, -10000, 0);
                _probe = go.AddComponent<SphereCollider>();
                _probe.isTrigger = true;
                _probe.radius = BallRadius;
            }
            for (int iter = 0; iter < 4; iter++)
            {
                int n = Physics.OverlapSphereNonAlloc(pos, BallRadius, Overlaps, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
                Vector3 push = Vector3.zero;
                for (int i = 0; i < n; i++)
                {
                    var o = Overlaps[i];
                    if (Physics.ComputePenetration(_probe, pos, Quaternion.identity, o, o.transform.position, o.transform.rotation, out var dir, out float d)
                        && d > push.magnitude)
                        push = dir * (d + 0.01f);
                }
                if (push == Vector3.zero) break;
                pos += push;
            }
            return pos;
        }

        bool _carried;
        Vector3 _carryOrigin, _carryDir;
        double _carryStart, _carryCaptured;
        float _carryPhase;

        public override void ServerCarry(Vector3 origin, Vector3 dir, double start, double captured, float phase)
        {
            if (!IsServer || !IsAlive) return;
            _carried = true;
            _ragdoll = true;
            _ragdollUntil = Time.time + 999f;
            _carryOrigin = origin; _carryDir = dir; _carryStart = start; _carryCaptured = captured; _carryPhase = phase;
        }

        public override void ServerKnock(Vector3 velocity, float ragdollSeconds)
        {
            if (!IsServer || !IsAlive) return;
            if (_carried) { _carried = false; _ragdollUntil = 0f; }
            _vel = velocity;
            _grounded = false;
            if (ragdollSeconds > 0f)
            {
                _ragdoll = true;
                _ragdollUntil = Time.time + ragdollSeconds;
            }
        }

        /// <summary>Server: new round — alive again, dropped onto the ground under where it was placed.</summary>
        public void ServerReset()
        {
            ServerRevive();
            _vel = Vector3.zero;
            _ragdoll = false;
            _carried = false;
            Vector3 pos = _homePos;
            if (Physics.Raycast(_homePos + Vector3.up * 0.5f, Vector3.down, out var hit, 500f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                pos = hit.point + Vector3.up * HipHeight;
            transform.SetPositionAndRotation(pos, _homeRot);
            _netPos.Value = pos;
            _netVel.Value = Vector3.zero;
            _netRagdoll.Value = false;
            ResetRpc(pos);
        }

        [Rpc(SendTo.Everyone)]
        void ResetRpc(Vector3 pos)
        {
            ClearLiveRagdoll();
            if (LastRagdoll) Destroy(LastRagdoll);
            transform.SetPositionAndRotation(pos, _homeRot);
            SetVisible(true);
        }

        public override void SetVisible(bool on)
        {
            base.SetVisible(on);
            foreach (var c in GetComponents<Collider>()) c.enabled = on;
        }
    }
}
