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
    public partial class TestDummy : Combatant
    {
        const float Gravity = 20f, BallRadius0 = 0.35f, HipHeight0 = 0.95f;
        /// <summary>A Summon Horde zombie is three quarters of the size; everything else full size.</summary>
        float Scale => (IsSpawned && Status.Value.Horde ? SpellPlans.HordeScale : 1f) * SpellPlans.SmallScale(this);
        float BallRadius => BallRadius0 * Scale;
        float HipHeight => HipHeight0 * Scale;

        readonly NetworkVariable<Vector3> _netPos = new();
        readonly NetworkVariable<Vector3> _netVel = new();
        readonly NetworkVariable<bool> _netRagdoll = new();
        readonly NetworkVariable<bool> _netLunge = new();
        public override bool LungingView => _netLunge.Value && _netRagdoll.Value;

        public override Vector3 ViewVelocity => _netVel.Value;
        public override float ViewYaw => transform.eulerAngles.y;
        public override float ViewPitch => 0f;
        public override byte ViewFlags => (byte)((_grounded ? NetState.Grounded : 0) | (_netRagdoll.Value ? NetState.Ragdoll : 0));
        public override string FeedName => IsUndead ? "Undead" : _kind.Value switch { DummyKind.Defense => "Defense Dummy", DummyKind.Attack => "Attack Dummy", _ => "Test Dummy" };
        public override float MaxHealth => !IsUndead ? maxHealth : Status.Value.Horde ? SpellPlans.HordeHealth : SpellPlans.UndeadHealth;
        public override bool InPracticeChamber => PracticeSlot != 0;
        /// <summary>One of the Undead: its master's team.</summary>
        public override byte MatchTeam
        {
            get
            {
                var master = IsUndead ? WorldNet.FindCaster(Status.Value.Master) : null;
                return master != null ? master.MatchTeam : (byte)0;
            }
        }

        /// <summary>Server: the practice chamber this dummy belongs to (0 = placed in the scene).</summary>
        [System.NonSerialized] public int PracticeSlot;
        /// <summary>Server: summoned with K in practice — gone for good once it dies, instead of standing back up.</summary>
        [System.NonSerialized] public bool Temporary;

        Vector3 _homePos;
        Quaternion _homeRot;
        // server simulation
        Vector3 _vel;
        bool _ragdoll, _grounded = true;
        float _ragdollUntil, _chainYankAt;

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
                // on the front of a chariot: drawn on the front of the ball as it is on this screen
                var driver = PinnedTo;
                if (driver != null) target = SpellPlans.PinPoint(driver.transform.position, driver.ViewYaw);
                transform.position = driver != null || (transform.position - target).sqrMagnitude > 9f
                    ? target
                    : Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-20f * Time.deltaTime));
                _grounded = !_netRagdoll.Value && _netVel.Value.sqrMagnitude < 0.5f;
            }
            UpdateLiveRagdoll(_netRagdoll.Value);
            UpdateUndeadLook();
        }

        // ------------------------------------------------------------------ server

        [Tooltip("Seconds after dying before the dummy stands back up at its spot.")]
        public float respawnDelay = 3f;
        float _diedAt = -1f;

        void Simulate(float dt)
        {
            if (Dead.Value)
            {
                _outsideFor = 0f;
                if (_diedAt < 0f) _diedAt = Time.time;
                else if (Time.time - _diedAt >= (_corpse.Value ? CorpseKeep : respawnDelay))
                {
                    if (Temporary) NetworkObject.Despawn(true);
                    else if (Arena.Current != null) ServerReset();
                }
                return;
            }
            _diedAt = -1f;
            if (_dieAt >= 0f && Time.time >= _dieAt) { _dieAt = -1f; ServerKillByHazard(DeathCause.Spell, transform.position); return; }
            if (_combo.Tick(IsAlive, _ragdoll && !_lunging && !HeldStill)) ServerComboBreak(); // (a bite's own lunge doesn't count)
            // only once there's an arena to stand on (practice chamber dummies run even with no match going)
            if (!IsAlive || Arena.Current == null || (!MatchManager.Running && !InPracticeChamber)) return;
            if (MatchManager.BuildingArena && !InPracticeChamber) return; // the arena isn't up yet: wait to be reset into it
            if (Rising(dt) || TakenAway() || Frozen() || EatenNow()) return;
            Vector3 pos = transform.position;
            int mask = Layers.EnvironmentMask;
            var master = SpellServer.MindMaster(this);
            if (master == null) _puppet = false;
            if (Grasped && !_ragdoll && !_carried) _vel = new Vector3(0f, Mathf.Min(0f, _vel.y), 0f); // held by the legs
            else if (master != null && !_ragdoll && !_carried && PinnedTo == null) Puppet(master);
            else if (IsUndead && !_ragdoll && !_carried && PinnedTo == null) Think(pos);

            // trapped by the Empress and thrown out of her domain: the chains haul it back in (as for players)
            if (SpellServer.TrapCenter(this, out var trapCenter) &&
                SpellPlans.ChainPullBack(pos + Vector3.down * HipHeight, trapCenter, ref _vel, dt, out var yank) &&
                (!_ragdoll || _carried) && Time.time >= _chainYankAt)
            {
                _chainYankAt = Time.time + 0.4f;
                ServerKnock(yank, _carried ? 0.6f : 0f);
            }

            var driver = PinnedTo;
            if (driver != null)
            {
                // stuck on the front of a chariot: carried along on it, limp
                pos = SpellPlans.PinPoint(driver.transform.position, driver.ViewYaw);
                _vel = driver.ViewVelocity;
                _ragdoll = true;
                _ragdollUntil = Time.time + SpellPlans.ChariotStun;
            }
            else if (_carried)
            {
                double now = WorldNet.Now;
                // spun along its side, but walls stop it (the server flings it out once it's held back, or after a second)
                Vector3 p = SpellPlans.TornadoCarry(_carryOrigin, _carryDir, _carryStart, _carryCaptured, now, _carryPhase);
                Vector3 step = p - pos;
                float dist = step.magnitude;
                Vector3 to = dist > 1e-5f && Physics.SphereCast(pos, BallRadius, step / dist, out var block, dist, mask, QueryTriggerInteraction.Ignore)
                    ? pos + step / dist * Mathf.Max(0f, block.distance - 0.01f) : p;
                _vel = (to - pos) / Mathf.Max(dt, 0.001f);
                pos = to;
                if (now > _carryStart + SpellPlans.TornadoLife + 0.6 || now > _carryCaptured + SpellPlans.TornadoHold + 0.6) { _carried = false; _ragdollUntil = Time.time + 0.5f; }
            }
            else if (_ragdoll && _standUpAt >= 0f && Time.time >= _standUpAt)
            {
                StandNow(); // (after biting someone)
                pos = transform.position;
            }
            else if (_ragdoll)
            {
                // a ball around the hips: flies, bounces, rolls to a stop
                pos = PushOut(pos);
                bool afloat = RisingWater.FloatLine(pos, out float line);
                if (afloat) _vel.y = RisingWater.Buoy(_vel.y, pos.y, line, dt);
                else _vel.y -= Gravity * dt;
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
                bool onGround = afloat || Physics.SphereCast(pos, BallRadius * 0.95f, Vector3.down, out _, 0.08f, mask, QueryTriggerInteraction.Ignore);
                // a bite's lunge: limp until it comes down on the floor, then straight up
                if (_lunging && onGround && Time.time >= _lungeLand) { _vel = Vector3.zero; _ragdollUntil = 0f; }
                if (onGround) { float k = Mathf.Exp(-4f * dt); _vel.x *= k; _vel.z *= k; }
                if (Time.time > _ragdollUntil && onGround && _vel.sqrMagnitude < 4f)
                {
                    // stand back up right where the body lies: feet at the bottom of the ball, hips above them
                    Vector3 feet = pos + Vector3.down * BallRadius;
                    if (StandBlocked(feet)) SpellServer.ClearStandSpace(feet); // break whatever's on top
                    _ragdoll = false;
                    _lunging = false;
                    _vel = Vector3.zero;
                    pos = feet + Vector3.up * HipHeight;
                }
            }
            else if (RisingWater.FloatLine(pos, out float line2))
            {
                // afloat
                _grounded = false;
                if (!IsUndead && master == null) _vel.x = _vel.z = 0f;
                _vel.y = RisingWater.Buoy(_vel.y, pos.y, line2, dt);
                Fly(ref pos, dt);
            }
            else
            {
                // standing: feet on the ground, or falling straight down
                float down = Mathf.Max(0f, -_vel.y * dt) + 0.05f;
                if (_vel.y <= 0f && Physics.Raycast(pos, Vector3.down, out var ground, HipHeight + down, mask, QueryTriggerInteraction.Ignore))
                {
                    pos.y = ground.point.y + HipHeight;
                    if (IsUndead || master != null) { _vel.y = 0f; Walk(ref pos, dt); } // a zombie shambling along (or Mind Hexed)
                    else _vel = Vector3.zero;
                    _grounded = true;
                }
                else
                {
                    _grounded = false;
                    _vel.y -= Gravity * dt;
                    Fly(ref pos, dt);
                }
            }

            // Skyland: off a side, in at the opposite one; out the bottom, in over the top (filling its bar: full, in bits)
            bool skyOver = false;
            if (driver == null && !WorldNet.InsideWorld(pos) && SkyWrap.Near(pos) is { } sky)
            {
                if (sky.Across(pos, out var across)) { pos = across; SkyWindRpc(false); }
                else if (sky.Covers(pos) && pos.y < sky.BottomY)
                {
                    if (_skyHeat.Add()) ServerKillByHazard(DeathCause.Barrier, pos, shatter: true);
                    else { pos = sky.TopAbove(pos); SkyWindRpc(true); }
                }
                skyOver = sky.Covers(pos);
            }

            // through a linked portal: out of the other one
            // (the dummy's ball counts as in when it touches the opening, and comes out that far in front of the other one)
            if (WorldNet.PassPortal(transform.position, pos, BallRadius + 0.05f, 0f, out var portalIn, out var portalOut) && driver == null)
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
            if (_netLunge.Value != Lunging) _netLunge.Value = Lunging;

            // (out of bounds: in pieces after as long as a player gets)
            _outsideFor = !WorldNet.InsideWorld(pos) && ArenaLimits.Outside(pos) ? _outsideFor + dt : 0f;
            bool tooLong = _outsideFor >= PlayerController.OutsideTime;
            // (feet + 0.2 as for players, so lava kills a dummy the moment it touches the surface)
            if (!WorldNet.InsideWorld(pos) && (pos.y < Practice.KillYAt(pos) && !skyOver || tooLong || KillVolume.AnyContains(pos) || KillVolume.AnyContains(pos + Vector3.down * (HipHeight - 0.2f))))
                ServerKillByHazard(DeathCause.Barrier, pos, shatter: tooLong || _outsideFor > 0f && pos.y < Practice.KillYAt(pos));
            else if (!WorldNet.InsideWorld(pos) && TrainTrack.Hits(pos, out var train))
                ServerKillByHazard(DeathCause.Barrier, pos, train.normalized * SpellPlans.TrainThrow + Vector3.up * SpellPlans.TrainLift);
        }

        /// <summary>
        /// Standing, in the air (falling, or thrown on its feet: a broken combo): the whole body is swept along, sliding
        /// along whatever it meets, so a knock never puts it through a wall.
        /// </summary>
        void Fly(ref Vector3 pos, float dt)
        {
            Vector3 left = _vel * dt;
            float r = BallRadius * 0.9f;
            for (int i = 0; i < 3 && left.sqrMagnitude > 1e-8f; i++)
            {
                float dist = left.magnitude;
                Vector3 dir = left / dist;
                // (from just over the feet to just under the top of the head)
                Vector3 low = pos + Vector3.up * (r + 0.08f - HipHeight), high = pos + Vector3.up * Mathf.Max(0f, 0.95f * Scale - r);
                if (!Physics.CapsuleCast(low, high, r, dir, out var hit, dist + 0.02f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                {
                    pos += left;
                    return;
                }
                float go = Mathf.Max(0f, hit.distance - 0.02f);
                pos += dir * go;
                left -= dir * go;
                float into = -Vector3.Dot(_vel, hit.normal);
                if (into > 0f) _vel += hit.normal * into;
                into = -Vector3.Dot(left, hit.normal);
                if (into > 0f) left += hit.normal * into;
            }
        }

        double _liftOf;
        float _lifted;

        /// <summary>Server: frozen by Stasis Hex, exactly where and as it is; or by Shatter Hex, lifted off the ground first.</summary>
        bool Frozen()
        {
            if (!HeldStill) return false;
            _carried = false;
            if (_ragdollUntil > Time.time + 10f) _ragdollUntil = Time.time + 0.5f; // (it was being carried)
            _vel = Vector3.zero;
            if (_netVel.Value != Vector3.zero) _netVel.Value = Vector3.zero;
            if (Shattering)
            {
                double from = Status.Value.ShatterFrom;
                if (_liftOf != from) { _liftOf = from; _lifted = 0f; }
                float up = SpellPlans.ShatterRise((float)(WorldNet.Now - from)), step = up - _lifted;
                _lifted = up;
                // (a ceiling stops it)
                if (step > 0f && !Physics.SphereCast(transform.position, BallRadius, Vector3.up, out _, step + 0.95f * Scale - BallRadius, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                    transform.position += Vector3.up * step;
                _grounded = false;
            }
            Vector3 pos = transform.position;
            if (_netPos.Value != pos) _netPos.Value = pos;
            if (_netRagdoll.Value != _ragdoll) _netRagdoll.Value = _ragdoll;
            return true;
        }

        bool _puppet, _jumpSeen;
        float _puppetYaw, _masterYaw;

        /// <summary>Server: Mind Hexed: walks, turns and jumps as <paramref name="master"/> does.</summary>
        void Puppet(Combatant master)
        {
            if (!_puppet)
            {
                _puppet = true;
                _puppetYaw = transform.eulerAngles.y;
                _masterYaw = master.ViewYaw;
                _jumpSeen = true;
            }
            float yaw = _puppetYaw + Mathf.DeltaAngle(_masterYaw, master.ViewYaw);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 walk = SpellPlans.PuppetWish(master) * SpellPlans.PuppetSpeed;
            _vel.x = walk.x;
            _vel.z = walk.z;
            bool jumping = SpellPlans.PuppetJumping(master);
            if (jumping && !_jumpSeen && _grounded) _vel.y = SpellPlans.PuppetJump;
            _jumpSeen = jumping;
        }

        /// <summary>Server: just broke a combo (can't be knocked down).</summary>
        public bool ServerUnstoppable => _combo.Unstoppable;

        double _takenAt;

        /// <summary>Server: inside a fly trap: wherever its mouth is, still.</summary>
        bool EatenNow()
        {
            var st = Status.Value;
            double now = WorldNet.Now;
            if (!st.Eaten(now)) return false;
            if (WorldNet.PotMouth(st.EatenBy, now, out var mouth))
            {
                mouth += Vector3.down * SpellPlans.EatenDrop;
                transform.position = mouth;
                if (_netPos.Value != mouth) _netPos.Value = mouth;
            }
            _vel = Vector3.zero;
            if (_netVel.Value != Vector3.zero) _netVel.Value = Vector3.zero;
            // limp the whole time (everyone sees its ragdoll in the jaws), unless a combo break got it out
            if (!_combo.Unstoppable)
            {
                _ragdoll = true;
                _ragdollUntil = Mathf.Max(_ragdollUntil, Time.time + 0.3f);
                _standUpAt = -1f;
            }
            if (_netRagdoll.Value != _ragdoll) _netRagdoll.Value = _ragdoll;
            return true;
        }

        /// <summary>Server: taken by The World: held still while crushed, then at its spot inside until it's ready.</summary>
        bool TakenAway()
        {
            var st = Status.Value;
            double now = WorldNet.Now;
            if (!st.Taken(now)) return false;
            if (st.Vanished(now) && _takenAt != st.WorldFrom)
            {
                _takenAt = st.WorldFrom;
                ServerPlace(st.WorldSpot, st.WorldYaw);
            }
            _vel = Vector3.zero;
            if (_netVel.Value != Vector3.zero) _netVel.Value = Vector3.zero;
            return true;
        }

        /// <summary>Server: put somewhere else, standing (hips at <paramref name="hips"/>).</summary>
        public override void ServerPlace(Vector3 hips, float yaw)
        {
            if (!IsServer || !IsAlive) return;
            _vel = Vector3.zero;
            _ragdoll = _carried = _lunging = false;
            _standUpAt = -1f;
            transform.SetPositionAndRotation(hips, Quaternion.Euler(0f, yaw, 0f));
            _netPos.Value = hips;
            _netVel.Value = Vector3.zero;
            _netRagdoll.Value = false;
        }

        static bool StandBlocked(Vector3 feet)
        {
            Vector3 a = feet + Vector3.up * (0.35f + 0.08f), b = feet + Vector3.up * (1.95f - 0.35f);
            return Physics.CheckCapsule(a, b, 0.33f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        static SphereCollider _probe;
        static readonly Collider[] Overlaps = new Collider[16];

        /// <summary>If the ball has sunk into something (fast hit, voxels appearing around it), push it back out.</summary>
        Vector3 PushOut(Vector3 pos)
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

        // ---------------- combo break (as for players, so it can be tried out on a dummy)

        readonly ComboTracker _combo = new();

        void ServerComboBreak()
        {
            if (Pinned) return; // stuck on the front of a chariot: that ends on its own
            _combo.Break();
            SpellServer.ReleaseFromCarry(this);
            // up at once, even in mid-air: it drops straight down from where it is
            if (_ragdoll || _carried)
            {
                Vector3 pos = transform.position;
                Vector3 feet = pos + Vector3.down * BallRadius;
                if (StandBlocked(feet)) SpellServer.ClearStandSpace(feet);
                pos = feet + Vector3.up * HipHeight;
                transform.position = pos;
                _netPos.Value = pos;
            }
            _carried = false;
            _ragdoll = false;
            _ragdollUntil = 0f;
            _vel = new Vector3(0f, Mathf.Min(0f, _vel.y), 0f);
            _grounded = false;
            _lunging = false;
            ComboBreakRpc(SpellPlans.ComboBreakTime);
        }

        [Rpc(SendTo.Everyone)]
        void ComboBreakRpc(float seconds) => ComboBreakView.Create(this, seconds);

        public override void ServerCarry(Vector3 origin, Vector3 dir, double start, double captured, float phase)
        {
            if (!IsServer || !IsAlive || _combo.Unstoppable || Status.Value.Taken(WorldNet.Now) || SpellServer.StasisHolds(this)) return;
            if (_combo.NoteKnockdown()) { ServerComboBreak(); return; }
            SpellServer.FreeGrasp(this);
            _carried = true;
            _ragdoll = true;
            _ragdollUntil = Time.time + 999f;
            _carryOrigin = origin; _carryDir = dir; _carryStart = start; _carryCaptured = captured; _carryPhase = phase;
        }

        public override void ServerKnock(Vector3 velocity, float ragdollSeconds)
        {
            if (!IsServer || !IsAlive || Status.Value.Taken(WorldNet.Now) || SpellServer.StasisHolds(this)) return;
            if (_carried) { _carried = false; _ragdollUntil = 0f; }
            if (ragdollSeconds > 0f && !_combo.Unstoppable && !SpellServer.QuietKnock && _combo.NoteKnockdown()) ServerComboBreak();
            if (_combo.Unstoppable) ragdollSeconds = 0f; // just broke a combo: still thrown, not knocked down
            if (ragdollSeconds > 0f) SpellServer.FreeGrasp(this); // knocked out of Undying Grasp
            else if (Grasped && !_ragdoll) return;                // (nothing less gets it out)
            _vel = velocity;
            _grounded = false;
            if (ragdollSeconds > 0f)
            {
                _ragdoll = true;
                _ragdollUntil = Time.time + ragdollSeconds;
                SpellServer.SmallKnockedDown(this); // Smallify: knocked over while small hurts
            }
        }

        /// <summary>Server: new round — alive again, dropped onto the ground under where it was placed.</summary>
        // ------------------------------------------------------------------ mana (server only)

        /// <summary>The dummy sits at this much mana: drained by Mana Siphon, then refills back up to it (never past).</summary>
        public const float Mana = 5f;
        float _mana = Mana;
        double _manaTime;

        public float ManaNow => Mathf.Min(Mana, _mana + (float)(WorldNet.Now - _manaTime) * ManaState.PerSecond);

        /// <summary>Server: take up to <paramref name="amount"/> mana; returns how much was taken.</summary>
        public float ServerDrainMana(float amount)
        {
            float m = ManaNow, take = Mathf.Min(amount, m);
            _mana = m - take;
            _manaTime = WorldNet.Now;
            return take;
        }

        public void ServerReset()
        {
            _outsideFor = 0f;
            _skyHeat.Clear();
            Status.Value = default; // no longer Undead (MaxHealth goes back to normal first)
            _lunging = false;
            ServerRevive();
            TrappedBy.Value = 0;
            PinnedBy.Value = 0;
            _mana = Mana;
            _manaTime = WorldNet.Now;
            _vel = Vector3.zero;
            _ragdoll = false;
            _carried = false;
            _combo.Reset();
            Vector3 pos = _homePos;
            if (Physics.Raycast(_homePos + Vector3.up * 0.5f, Vector3.down, out var hit, 500f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                pos = hit.point + Vector3.up * HipHeight;
            transform.SetPositionAndRotation(pos, _homeRot);
            _netPos.Value = pos;
            _netVel.Value = Vector3.zero;
            _netRagdoll.Value = false;
            ResetRpc(pos);
        }

        SkyWrap.Heat _skyHeat;

        [Rpc(SendTo.Everyone)]
        void SkyWindRpc(bool fell)
        {
            SkyWrapWind.Play(this);
            if (fell) HexBodyFx.SkyFall(this);
        }

        [Rpc(SendTo.Everyone)]
        void ResetRpc(Vector3 pos)
        {
            HexBodyFx.SkyClear(this);
            ClearLiveRagdoll();
            if (LastRagdoll) Destroy(LastRagdoll);
            transform.SetPositionAndRotation(pos, _homeRot);
            SetVisible(true);
        }

        // ------------------------------------------------------------------ the Undead (server)

        bool _lunging;
        float _lungeLand;
        float _nextBite, _nextJump;
        Vector3 _riseFeet;

        /// <summary>Lunging at someone with Infectious Bite (until it's back on its feet).</summary>
        public bool Lunging => _lunging && _ragdoll;

        /// <summary>
        /// Server: a Summon Horde zombie (three quarters the size, 15 health) climbing out of the ground at
        /// <paramref name="feet"/> from <paramref name="riseFrom"/>, on <paramref name="master"/>'s side. Gone for good when it dies.
        /// </summary>
        public static TestDummy ServerSpawnZombie(Vector3 feet, float yaw, ulong master, byte practiceSlot, double riseFrom)
        {
            var prefab = Practice.DummyPrefab();
            if (prefab == null) { Debug.LogWarning("[SME] No TestDummy in the network prefab list: Summon Horde has nothing to raise."); return null; }
            var go = Instantiate(prefab, feet + Vector3.down * 2f, Quaternion.Euler(0f, yaw, 0f));
            var d = go.GetComponent<TestDummy>();
            d.PracticeSlot = practiceSlot;
            d.Temporary = true;
            go.GetComponent<NetworkObject>().Spawn(true);
            d.ServerMakeUndead(feet, yaw, master, true, riseFrom);
            return d;
        }

        /// <summary>
        /// Server: up as one of the Undead on <paramref name="master"/>'s side, standing at <paramref name="feet"/> (a horde
        /// zombie climbs out of the ground there first). It shambles at the nearest enemy and bites.
        /// </summary>
        public void ServerMakeUndead(Vector3 feet, float yaw, ulong master, bool horde, double riseFrom)
        {
            if (!IsServer || !IsSpawned) return;
            Status.Value = new BodyStatus { Undead = true, Master = master, Horde = horde, RiseFrom = horde ? riseFrom : 0 };
            _corpse.Value = false; // (a practice corpse raised: an ordinary body from now on)
            _dieAt = -1f;
            ServerRevive();
            TrappedBy.Value = 0;
            PinnedBy.Value = 0;
            _vel = Vector3.zero;
            _ragdoll = _carried = _lunging = false;
            _combo.Reset();
            _riseFeet = feet;
            _nextBite = Time.time + 1f;
            float hip = HipHeight0 * (horde ? SpellPlans.HordeScale : 1f);
            Vector3 pos = horde ? feet + Vector3.down * (1.95f * SpellPlans.HordeScale) : feet + Vector3.up * hip;
            var rot = Quaternion.Euler(0f, yaw, 0f);
            transform.SetPositionAndRotation(pos, rot);
            _netPos.Value = pos;
            _netVel.Value = Vector3.zero;
            _netRagdoll.Value = false;
            UndeadRpc(pos, yaw);
        }

        [Rpc(SendTo.Everyone)]
        void UndeadRpc(Vector3 pos, float yaw)
        {
            ClearLiveRagdoll();
            if (LastRagdoll) Destroy(LastRagdoll);
            transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            SetVisible(true);
            if (!Status.Value.Horde) NecroVfx.RiseBurst(pos);
        }

        /// <summary>Server: a horde zombie still climbing out of the ground (nothing else happens to it meanwhile).</summary>
        bool Rising(float dt)
        {
            var st = Status.Value;
            double now = WorldNet.Now;
            if (!st.Horde || now >= st.RiseFrom + SpellPlans.HordeRise) return false;
            float u = Mathf.Clamp01((float)(now - st.RiseFrom) / SpellPlans.HordeRise);
            u = 1f - (1f - u) * (1f - u);
            // from just under the surface (head and all) up to standing on it
            Vector3 pos = _riseFeet + Vector3.up * HipHeight + Vector3.down * ((1.95f * Scale + 0.05f) * (1f - u));
            transform.position = pos;
            _vel = Vector3.zero;
            if (_netPos.Value != pos) _netPos.Value = pos;
            if (_netVel.Value != Vector3.zero) _netVel.Value = Vector3.zero;
            return true;
        }

        float _standUpAt = -1f;

        /// <summary>Server: the bite landed: down a moment longer (<see cref="SpellPlans.BiteRecover"/>), then up.</summary>
        public void ServerStandUp() => _standUpAt = Time.time + SpellPlans.BiteRecover;

        void StandNow()
        {
            _standUpAt = -1f;
            _lunging = false;
            if (!_ragdoll) return;
            Vector3 feet = transform.position + Vector3.down * BallRadius;
            if (StandBlocked(feet)) SpellServer.ClearStandSpace(feet);
            Vector3 pos = feet + Vector3.up * HipHeight;
            transform.position = pos;
            _netPos.Value = pos;
            _ragdoll = false;
            _vel = new Vector3(0f, Mathf.Min(0f, _vel.y), 0f);
            _grounded = false;
        }

        /// <summary>The nearest enemy it can go for (not on its own side; in its own practice chamber, if it's in one).</summary>
        Combatant NearestEnemy(Vector3 pos)
        {
            Combatant best = null;
            float bestD = 40f;
            foreach (var c in Registry)
            {
                if (c == null || c == this || !c.IsAlive || !c.Visible || Allies(this, c) || Teammates(this, c)) continue;
                if (c.InPracticeChamber != InPracticeChamber) continue;
                float d = (c.transform.position - pos).magnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        /// <summary>Server: shamble at the nearest enemy; close enough, and it's been 3 s: lunge at them, teeth first.</summary>
        void Think(Vector3 pos)
        {
            var target = NearestEnemy(pos);
            if (target == null) { _vel.x = _vel.z = 0f; return; }
            Vector3 to = target.transform.position - pos;
            Vector3 flat = new(to.x, 0f, to.z);
            float dist = flat.magnitude;
            if (dist > 0.01f) transform.rotation = Quaternion.LookRotation(flat);
            Vector3 walk = dist > 1f ? flat / dist * SpellPlans.HordeSpeed : Vector3.zero;
            _vel.x = walk.x;
            _vel.z = walk.z;
            if (Time.time < _nextBite || to.magnitude > SpellPlans.HordeBiteRange || !_grounded) return;
            _nextBite = Time.time + SpellPlans.HordeBiteEvery;
            Vector3 aim = (target.transform.position + Vector3.up * 0.2f - pos).normalized;
            _vel = aim * SpellPlans.BiteSpeed + Vector3.up * SpellPlans.BiteUp;
            _ragdoll = true;
            _ragdollUntil = Time.time + SpellPlans.BiteRagdoll;
            _lunging = true;
            _lungeLand = Time.time + 0.15f; // (not the floor it just left)
            _grounded = false;
            SpellServer.StartBite(this);
        }

        /// <summary>Server: walking along the ground: over low steps, and a hop when a wall's in the way.</summary>
        void Walk(ref Vector3 pos, float dt)
        {
            Vector3 h = new(_vel.x, 0f, _vel.z);
            float step = h.magnitude * dt;
            if (step < 1e-5f) return;
            Vector3 dir = h / h.magnitude;
            int mask = Layers.EnvironmentMask;
            Vector3 knee = pos + Vector3.down * (HipHeight * 0.55f);
            if (Physics.SphereCast(knee, BallRadius * 0.8f, dir, out _, step + BallRadius, mask, QueryTriggerInteraction.Ignore))
            {
                bool high = Physics.SphereCast(pos + Vector3.up * 0.2f, BallRadius * 0.8f, dir, out _, step + BallRadius, mask, QueryTriggerInteraction.Ignore);
                if (!high) pos.y += 0.3f; // a step: up onto it
                else if (Time.time >= _nextJump)
                {
                    _nextJump = Time.time + 1f;
                    _vel.y = 7.5f; // a wall: try hopping it
                    _grounded = false;
                    return;
                }
                else return;
            }
            pos += dir * step;
        }

        bool _lookUndead;
        MaterialPropertyBlock _mpb;

        /// <summary>Its own colour again, over anything that painted the body (Death's black).</summary>
        public void RefreshLook()
        {
            _lookUndead = !IsUndead;
            UpdateUndeadLook();
        }

        /// <summary>Every peer: the Undead are sickly green (a horde zombie also three quarters the size).</summary>
        void UpdateUndeadLook()
        {
            float s = Scale;
            if (transform.localScale.x != s) transform.localScale = Vector3.one * s;
            bool undead = IsUndead;
            if (undead == _lookUndead) return;
            _lookUndead = undead;
            _mpb ??= new MaterialPropertyBlock();
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (!undead) { r.SetPropertyBlock(null); continue; }
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor("_BaseColor", SpellPlans.UndeadSkin);
                _mpb.SetColor("_Color", SpellPlans.UndeadSkin);
                r.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>A dead dummy's body shrinks away after this long (players' bodies stay for the round).</summary>
        public const float DummyCorpseSeconds = 2.5f;
        protected override float CorpseSeconds => _corpse.Value ? CorpseKeep : DummyCorpseSeconds;

        // ---------------- a practice corpse (I): a dummy lying dead, kept a while for raising

        /// <summary>How long a practice corpse lies there before it goes.</summary>
        public const float CorpseKeep = 60f;
        readonly NetworkVariable<bool> _corpse = new();
        float _dieAt = -1f, _outsideFor;

        /// <summary>Server: this dummy drops dead in a moment (once everyone knows its body is to stay).</summary>
        public void ServerMakeCorpse()
        {
            _corpse.Value = true;
            _dieAt = Time.time + 0.25f;
        }

        /// <summary>Server: its skin is being taken: not back at its spot until that's done.</summary>
        public void ServerHoldCorpse() { if (_diedAt >= 0f) _diedAt = Time.time; }

        public override void SetVisible(bool on)
        {
            base.SetVisible(on);
            foreach (var c in GetComponents<Collider>()) c.enabled = on;
        }
    }
}
