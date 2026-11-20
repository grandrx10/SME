using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Networked player: owner-authoritative movement (replicated + interpolated), mana, the cards brought into
    /// the round, casting requests (validated by the server), knockback / ragdoll, falls and kill barriers.
    /// Health, death and the death ragdoll live in Combatant.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class PlayerController : Combatant
    {
        public static PlayerController Local { get; private set; }
        public static readonly List<PlayerController> All = new();
        public static event System.Action<PlayerController> LocalSpawned, LocalDespawned;

        [Tooltip("Remote players are rendered this far in the past for smooth interpolation.")]
        public float interpolationDelay = 0.1f;

        public readonly NetworkVariable<FixedString32Bytes> PlayerName = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        /// <summary>Palette index assigned by the host (unique per lobby). 255 = not assigned yet.</summary>
        public readonly NetworkVariable<byte> ColorIndex = new(255);
        public readonly NetworkVariable<FixedString4096Bytes> Face = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public readonly NetworkVariable<int> Wins = new();
        public readonly NetworkVariable<int> Kills = new();
        public readonly NetworkVariable<ManaState> Mana = new();
        /// <summary>The cards this player brought into the round (owner writes it when the round starts).</summary>
        public readonly NetworkVariable<EquippedCards> Equipped = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        /// <summary>The Fool: the free single-use card it rolled, per slot (None = The Fool is showing).</summary>
        public readonly NetworkVariable<EquippedCards> FoolCards = new();
        /// <summary>Caught in an Empress domain: can't move or cast (spells can still push you about).</summary>
        public readonly NetworkVariable<bool> Trapped = new();

        /// <summary>The card a slot actually casts right now (a Fool slot holding a rolled card casts that).</summary>
        public CardId CardInSlot(int slot)
        {
            var c = IsSpawned ? Equipped.Value[slot] : Loadouts.Selected.Get(slot);
            if (c == CardId.TheFool && IsSpawned && FoolCards.Value[slot] != CardId.None) return FoolCards.Value[slot];
            return c;
        }

        /// <summary>Is this slot showing a free card rolled by The Fool?</summary>
        public bool IsFoolCard(int slot) => IsSpawned && Equipped.Value[slot] == CardId.TheFool && FoolCards.Value[slot] != CardId.None;

        public override string FeedName => DisplayName;
        public override int FeedColor => ColorIndex.Value == 255 ? -1 : ColorIndex.Value;
        public Color PlayerColor => PlayerPalette.Get(ColorIndex.Value == 255 ? -1 : ColorIndex.Value);
        public string ColorHex => ColorIndex.Value == 255 ? "FFFFFF" : PlayerPalette.HexString(ColorIndex.Value);

        public static PlayerController FindByClient(ulong clientId)
        {
            foreach (var p in All) if (p != null && p.OwnerClientId == clientId) return p;
            return null;
        }
        public static PlayerController ServerFind(ulong clientId) => FindByClient(clientId);

        public string DisplayName
        {
            get
            {
                string n = PlayerName.Value.ToString();
                return string.IsNullOrEmpty(n) ? "Player " + (OwnerClientId + 1) : n;
            }
        }

        readonly NetworkVariable<NetState> _state = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public PlayerMotor Motor { get; private set; }
        public CharacterController CC { get; private set; }
        public AbilityCaster Caster { get; private set; }

        Vector3 _viewVelocity;
        float _viewYaw, _viewPitch;
        byte _viewFlags;
        SpellPose _viewPose;
        double _viewPoseStart;
        public override Vector3 ViewVelocity => _viewVelocity;
        public override float ViewYaw => _viewYaw;
        public override float ViewPitch => _viewPitch;
        public override byte ViewFlags => _viewFlags;
        public override SpellPose ViewPose => _viewPose;
        public override float ViewPoseTime => (float)(WorldNet.Now - _viewPoseStart);
        public bool IsRagdolled => (_viewFlags & NetState.Ragdoll) != 0;

        FacePainter _facePainter;
        MeshRenderer _faceRenderer;
        bool _hasFace;

        struct Snap { public float time; public NetState s; }
        readonly List<Snap> _snaps = new();
        byte _teleportCounter, _lastSeenTeleport;
        bool _hiddenUntilTeleport;

        protected override void Awake()
        {
            base.Awake();
            Motor = GetComponent<PlayerMotor>();
            CC = GetComponent<CharacterController>();
            Caster = GetComponent<AbilityCaster>();
            if (Caster == null) Caster = gameObject.AddComponent<AbilityCaster>();
            if (GetComponent<SpellPoseFx>() == null) gameObject.AddComponent<SpellPoseFx>();
            Motor.enabled = false;
            Caster.enabled = false;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            All.Add(this);
            gameObject.layer = Layers.Player;
            ColorIndex.OnValueChanged += OnColorChanged;
            ApplyColor();
            _facePainter = new FacePainter();
            _faceRenderer = FaceDecal.Attach(model, transform, _facePainter.Texture);
            Face.OnValueChanged += OnFaceChanged;
            ApplyFace();

            if (IsOwner)
            {
                Local = this;
                Motor.enabled = true;
                Caster.enabled = true;
                Motor.Crashed += OnCrashed;
                Motor.HardLanding += OnHardLanding;
                Motor.StandBlocked += OnStandBlocked;
                Motor.BubbleBlocked += OnBubbleBlocked;
                Motor.SwiftBlocked += OnSwiftBlocked;
                Motor.Moved += OnMoved;
                WorldNet.WarpEnded += OnWarpEnded;
                WorldNet.SwiftEnded += OnSwiftEnded;
                Health.OnValueChanged += OnOwnHealthChanged;
                PushName();
                PushFace();
                PushLoadout();
                GameSettings.NameChanged += PushName;
                FaceArt.Changed += PushFace;
                RequestColorRpc(-1);
                SpawnPoint.Pick(out var pos, out var yaw);
                Motor.Teleport(pos, yaw);
                WriteState();
                LocalSpawned?.Invoke(this);
            }
            else
            {
                Motor.enabled = false;
                _state.OnValueChanged += OnStateChanged;
                _lastSeenTeleport = _state.Value.Teleport;
                PushSnap(_state.Value);
                ApplySnapImmediate(_state.Value);
            }
            SetVisible(!Dead.Value);
            if (IsOwner && Dead.Value) Motor.enabled = false; // joined mid-round: watching

            if (IsServer) MatchManager.ServerOnPlayerJoined(this);
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            All.Remove(this);
            _state.OnValueChanged -= OnStateChanged;
            GameSettings.NameChanged -= PushName;
            FaceArt.Changed -= PushFace;
            Face.OnValueChanged -= OnFaceChanged;
            ColorIndex.OnValueChanged -= OnColorChanged;
            if (Motor != null)
            {
                Motor.Crashed -= OnCrashed; Motor.HardLanding -= OnHardLanding; Motor.StandBlocked -= OnStandBlocked; Motor.BubbleBlocked -= OnBubbleBlocked;
                Motor.SwiftBlocked -= OnSwiftBlocked; Motor.Moved -= OnMoved;
            }
            WorldNet.WarpEnded -= OnWarpEnded;
            WorldNet.SwiftEnded -= OnSwiftEnded;
            Health.OnValueChanged -= OnOwnHealthChanged;
            if (Local == this)
            {
                Local = null;
                LocalDespawned?.Invoke(this);
            }
        }

        void Update()
        {
            if (!IsSpawned) return;
            if (IsOwner)
            {
                if (!Dead.Value)
                {
                    if (Trapped.Value) Motor.Root(0.15f); // held in place by the Empress (knocks still move you)
                    TickOwnWarp();
                    WriteState();
                    CheckHazards();
                }
                _viewVelocity = Motor.Velocity;
                _viewYaw = Motor.Yaw;
                _viewPitch = Motor.Pitch;
                _viewFlags = BuildFlags();
                _viewPose = Caster.Pose;
                _viewPoseStart = Caster.PoseStart;
            }
            else Interpolate();
            UpdateLiveRagdoll();
        }

        // ---------------- replication ----------------

        byte BuildFlags()
        {
            byte f = 0;
            if (Motor.IsGrounded) f |= NetState.Grounded;
            if (Motor.Ragdolled) f |= NetState.Ragdoll;
            return f;
        }

        void WriteState()
        {
            _state.Value = new NetState
            {
                Position = transform.position,
                Velocity = Motor.Velocity,
                Yaw = Motor.Yaw,
                Pitch = Motor.Pitch,
                Flags = BuildFlags(),
                Teleport = _teleportCounter,
                Pose = (byte)Caster.Pose,
                PoseStart = Caster.PoseStart,
            };
        }

        void OnStateChanged(NetState prev, NetState next) => PushSnap(next);

        void PushSnap(NetState s)
        {
            if (s.Teleport != _lastSeenTeleport)
            {
                _lastSeenTeleport = s.Teleport;
                _snaps.Clear();
                _hiddenUntilTeleport = false;
                ApplySnapImmediate(s);
                SetVisible(!Dead.Value);
            }
            _snaps.Add(new Snap { time = Time.time, s = s });
            if (_snaps.Count > 30) _snaps.RemoveAt(0);
        }

        void ApplySnapImmediate(NetState s)
        {
            transform.SetPositionAndRotation(s.Position, Quaternion.Euler(0, s.Yaw, 0));
            _viewVelocity = s.Velocity;
            _viewYaw = s.Yaw;
            _viewPitch = s.Pitch;
            _viewFlags = s.Flags;
            _viewPose = (SpellPose)s.Pose;
            _viewPoseStart = s.PoseStart;
        }

        void Interpolate()
        {
            if (_snaps.Count == 0) return;
            float t = Time.time - interpolationDelay;
            var newest = _snaps[^1];
            NetState s;
            if (t >= newest.time)
            {
                s = newest.s;
                s.Position += newest.s.Velocity * Mathf.Min(t - newest.time, 0.15f);
            }
            else
            {
                int i = _snaps.Count - 1;
                while (i > 0 && _snaps[i - 1].time > t) i--;
                if (i == 0) s = _snaps[0].s;
                else
                {
                    var a = _snaps[i - 1];
                    var b = _snaps[i];
                    float k = Mathf.Clamp01((t - a.time) / Mathf.Max(0.0001f, b.time - a.time));
                    s = b.s;
                    s.Position = Vector3.Lerp(a.s.Position, b.s.Position, k);
                    s.Velocity = Vector3.Lerp(a.s.Velocity, b.s.Velocity, k);
                    s.Yaw = Mathf.LerpAngle(a.s.Yaw, b.s.Yaw, k);
                    s.Pitch = Mathf.Lerp(a.s.Pitch, b.s.Pitch, k);
                }
            }
            while (_snaps.Count > 2 && _snaps[1].time < t - 0.5f) _snaps.RemoveAt(0);
            ApplySnapImmediate(s);
        }

        // ---------------- live ragdoll (knocked down) ----------------

        void UpdateLiveRagdoll()
        {
            // other players' standing capsule would block people while they're really a heap on the floor
            if (!IsOwner && CC != null && CC.enabled != (Visible && !IsRagdolled)) CC.enabled = Visible && !IsRagdolled;
            UpdateLiveRagdoll(IsRagdolled);
        }

        protected override void OnLiveRagdollChanged() => UpdateFaceVisible();

        // ---------------- mana ----------------

        /// <summary>Mana right now (fractional; only whole points can be spent).</summary>
        public float ManaNow => Mana.Value.At(WorldNet.Now);

        public bool CanAfford(CardInfo card, float predictedSpend = 0f)
        {
            if (card == null) return false;
            return card.costType switch
            {
                CostType.Health => Health.Value - predictedSpend > card.cost,
                _ => Mathf.Floor(ManaNow - predictedSpend + 0.001f) >= card.cost,
            };
        }

        // ---------------- casting (owner -> server) ----------------

        readonly double[] _serverLastCast = { -10, -10, -10, -10, -10 };
        double _serverCastingUntil = -10;

        /// <summary>Server: still in the middle of casting a card (see <see cref="Cards.CastTime"/>).</summary>
        public bool ServerCasting => WorldNet.Now < _serverCastingUntil;

        /// <summary>Server: knocked out of a cast: free to cast again once back up.</summary>
        public void ServerEndCast() => _serverCastingUntil = -10;

        public void RequestCast(int slot, CardId card, Vector3 target, float yaw, ulong targetId = 0) => CastRpc((byte)slot, (byte)card, target, yaw, targetId);
        public void RequestRockLaunch(int id, Vector3 dir) => RockLaunchRpc(id, dir);

        [Rpc(SendTo.Server)]
        void CastRpc(byte slot, byte cardByte, Vector3 target, float yaw, ulong targetId)
        {
            if (!IsAlive || slot >= Loadout.Slots || !MatchManager.CastingAllowed || Trapped.Value || SpellServer.InBubble(this)) return;
            var card = (CardId)cardByte;
            bool free = IsFoolCard(slot) && FoolCards.Value[slot] == card;
            if (!free && Equipped.Value[slot] != card) return;
            if (!free && card == CardId.TheFool && FoolCards.Value[slot] != CardId.None) return; // already holding a rolled card
            var info = Cards.Get(card);
            if (info == null) return;
            double now = WorldNet.Now;
            // Warp Space's second portal: free, no cooldown
            if (card == CardId.WarpSpace && SpellServer.PortalRecastReady(this)) free = true;
            if (now - _serverLastCast[slot] < Cards.Cooldown - 0.15 && !(free && card == CardId.WarpSpace)) return;
            if (now < _serverCastingUntil - 0.15) return; // one card at a time (a little slack for lag)
            // cost (a little slack for clock differences); a Fool card is free
            float mana = Mana.Value.At(now + 0.12);
            if (!free && info.costType == CostType.Mana && Mathf.Floor(mana + 0.001f) < info.cost) return;
            if (!free && info.costType == CostType.Health && Health.Value <= info.cost) return;
            if (card == CardId.TheFool && !free)
            {
                var rolled = FoolCards.Value;
                rolled[slot] = SpellServer.RollFoolCard();
                FoolCards.Value = rolled;
            }
            else if (!SpellServer.Cast(this, card, target, yaw, targetId)) return;
            _serverLastCast[slot] = now;
            _serverCastingUntil = now + Cards.CastTime(card);
            if (free && IsFoolCard(slot))
            {
                // used once: back to The Fool
                var rolled = FoolCards.Value;
                rolled[slot] = CardId.None;
                FoolCards.Value = rolled;
            }
            else if (free) { } // Warp Space's second portal
            else if (info.costType == CostType.Mana)
                Mana.Value = Mana.Value.With(now, Mathf.Max(0f, Mana.Value.At(now) - info.cost));
            else
                ServerApplyDamage(info.cost, OwnerClientId, transform.position, Vector3.up, -1, 0f, DeathCause.Spell);
        }

        [Rpc(SendTo.Server)]
        void RockLaunchRpc(int id, Vector3 dir)
        {
            if (IsAlive) SpellServer.Launch(this, id, dir);
        }

        public void RequestFireballLaunch(int id, Vector3 dir) => FireballLaunchRpc(id, dir);

        [Rpc(SendTo.Server)]
        void FireballLaunchRpc(int id, Vector3 dir)
        {
            if (IsAlive) SpellServer.LaunchFireball(this, id, dir);
        }

        public void RequestHellfirePoint(int id, Vector3 p) => HellfirePointRpc(id, p);

        [Rpc(SendTo.Server)]
        void HellfirePointRpc(int id, Vector3 p)
        {
            if (IsAlive) SpellServer.HellfirePoint(this, id, p);
        }

        // ---------------- hit confirmation ----------------

        /// <summary>Local player: (where, damage, killed) — one of your spells just hurt someone.</summary>
        public static event System.Action<Vector3, float, bool> LocalHitConfirmed;

        public void ServerHitConfirm(Vector3 where, float damage, bool killed) => HitConfirmRpc(where, damage, killed);

        [Rpc(SendTo.Owner)]
        void HitConfirmRpc(Vector3 where, float damage, bool killed) => LocalHitConfirmed?.Invoke(where, damage, killed);

        // ---------------- knockback / crashes / falls ----------------

        float _serverRagdollUntil;
        /// <summary>Server: was this player thrown recently (so a crash report is believable)?</summary>
        public bool ServerMayCrash => Time.time < _serverRagdollUntil + 3f;

        public override void ServerKnock(Vector3 velocity, float ragdollSeconds)
        {
            if (!IsServer) return;
            if (SpellServer.Bubbled(this) || SpellServer.Invulnerable(this)) return; // in your bubble, mid-warp, swift: nothing moves you
            if (ragdollSeconds > 0f && SpellServer.Shielded(this)) return; // inside your own barrier: nothing moves you
            _serverRagdollUntil = Mathf.Max(_serverRagdollUntil, Time.time + ragdollSeconds);
            if (ragdollSeconds > 0f) SpellServer.OnInterrupted(this, true);
            KnockRpc(velocity, ragdollSeconds);
        }

        protected override void OnServerDamaged(float amount) => SpellServer.OnInterrupted(this, false);

        public override void ServerCarry(Vector3 origin, Vector3 dir, double start, double captured, float phase)
        {
            if (SpellServer.Invulnerable(this)) return;
            _serverRagdollUntil = Mathf.Max(_serverRagdollUntil, Time.time + SpellPlans.TornadoLife + 1f);
            SpellServer.OnInterrupted(this, true);
            CarryRpc(origin, dir, start, captured, phase);
        }

        [Rpc(SendTo.Owner)]
        void CarryRpc(Vector3 origin, Vector3 dir, double start, double captured, float phase)
        {
            if (!Dead.Value && Motor != null) Motor.Carry(origin, dir, start, captured, phase);
        }

        /// <summary>Server: the siphon punch landed — stop the dash and swing.</summary>
        public void ServerSiphonPunched() => SiphonPunchRpc();

        [Rpc(SendTo.Owner)]
        void SiphonPunchRpc()
        {
            if (Motor != null) Motor.EndSiphon();
            if (Caster != null) Caster.PlayPose(SpellPose.SiphonPunch);
        }

        [Rpc(SendTo.Owner)]
        void KnockRpc(Vector3 velocity, float ragdollSeconds)
        {
            if (!Dead.Value && Motor != null) Motor.Knock(velocity, ragdollSeconds);
        }

        void OnCrashed(Vector3 point, Vector3 velocity)
        {
            // break the wall here right away (so we fly through it), the server confirms it for everyone
            var w = VoxelWorld.Current;
            if (w != null)
            {
                var removed = new List<VoxelWorld.RemovedCell>();
                var islands = new List<List<VoxelWorld.RemovedCell>>();
                w.RemoveSphere(w.CellOf(point), SpellServer.CrashRadius4(velocity.magnitude), removed, islands);
                Debris.Spawn(w, removed, point, velocity.magnitude * 0.25f, velocity * 0.3f, 2);
                foreach (var island in islands) Debris.Spawn(w, island, point, 0.8f, Vector3.zero, 4);
                w.FlushRebuilds();
            }
            CrashRpc(point, velocity);
        }

        /// <summary>Owner: getting hurt mid-dash stops the dash.</summary>
        void OnOwnHealthChanged(float prev, float next)
        {
            if (next < prev - 0.01f && Motor != null) Motor.CancelDash();
        }

        void OnStandBlocked(Vector3 feet)
        {
            // break it here straight away so we can get up now; the server does the same for everyone
            var w = VoxelWorld.Current;
            if (w == null) return;
            SpellServer.StandBox(w, feet, out var min, out var max);
            var removed = new List<VoxelWorld.RemovedCell>();
            var islands = new List<List<VoxelWorld.RemovedCell>>();
            w.RemoveCells(WorldNet.BoxCells(min, max), removed, true, islands);
            if (removed.Count == 0) return; // nothing breakable in the way
            Debris.Spawn(w, removed, feet + Vector3.up, 2f, Vector3.zero, 2);
            foreach (var island in islands) Debris.Spawn(w, island, feet + Vector3.up, 0.8f, Vector3.zero, 4);
            w.FlushRebuilds();
            StandSpaceRpc(feet);
        }

        [Rpc(SendTo.Server)]
        void StandSpaceRpc(Vector3 feet) => SpellServer.ClearStandSpaceFor(this, feet);

        void OnBubbleBlocked(Vector3 center)
        {
            // break it here straight away so the bubble keeps rising; the server does the same for everyone
            var w = VoxelWorld.Current;
            if (w == null) return;
            SpellPlans.BubbleBreakBox(w, center, out var min, out var max);
            var removed = new List<VoxelWorld.RemovedCell>();
            var islands = new List<List<VoxelWorld.RemovedCell>>();
            w.RemoveCells(WorldNet.BoxCells(min, max), removed, true, islands);
            if (removed.Count == 0) return; // nothing breakable in the way
            Debris.Spawn(w, removed, center, 3f, Vector3.up * 2f, 2);
            foreach (var island in islands) Debris.Spawn(w, island, center, 0.8f, Vector3.zero, 4);
            w.FlushRebuilds();
            BubbleBreakRpc(center);
        }

        [Rpc(SendTo.Server)]
        void BubbleBreakRpc(Vector3 center) => SpellServer.BubbleBreak(this, center);

        // ---------------- Warp Self / portals / Arcane Swiftness (owner) ----------------

        /// <summary>Moved in one go (warp, portal): everyone else snaps to the new spot instead of sliding there.</summary>
        void OnMoved(Vector3 pos) => _teleportCounter++;

        int _warpDone;

        /// <summary>
        /// Our warp, timed by the server's clock: held still while folding and unfolding, and moved to the
        /// destination the moment the fold ends (unless the server cancelled it while it charged).
        /// </summary>
        void TickOwnWarp()
        {
            var net = WorldNet.Instance;
            if (net == null || net.Warps.Count == 0) return;
            double now = WorldNet.Now;
            foreach (var w in net.Warps.Values)
            {
                if (w.caster != NetworkObjectId || w.ended >= 0 && w.how == 0) continue;
                float t = (float)(now - w.start);
                if (t < SpellPlans.WarpCharge || t > SpellPlans.WarpDone) continue;
                if (w.ended < 0) Motor.Root(0.1f); // folding / unfolding: stand still
                if (t >= SpellPlans.WarpArrive && _warpDone != w.id)
                {
                    _warpDone = w.id;
                    Motor.MoveTo(w.dest + Vector3.up * 0.95f, Motor.Yaw, Vector3.zero);
                }
            }
        }

        void OnWarpEnded(int id, ulong caster, byte how)
        {
            if (caster != NetworkObjectId) return;
            if (how == 0) _warpDone = id; // cancelled while charging: never goes
            if (Caster != null && (Caster.Pose == SpellPose.WarpCast)) Caster.PlayPose(SpellPose.None);
        }

        void OnSwiftEnded(ulong caster, bool cancelled)
        {
            if (caster != NetworkObjectId || Motor == null) return;
            Motor.EndSwift();
            if (Caster != null && Caster.Pose == SpellPose.SwiftCast) Caster.PlayPose(SpellPose.None);
        }

        void OnSwiftBlocked(Vector3 feet, Vector3 dir)
        {
            // it comes apart here straight away so the run carries on; the server does the same for everyone
            var w = VoxelWorld.Current;
            if (w == null) return;
            SpellPlans.SwiftBreakBox(w, feet, dir, out var min, out var max);
            var removed = new List<VoxelWorld.RemovedCell>();
            var islands = new List<List<VoxelWorld.RemovedCell>>();
            w.RemoveCells(WorldNet.BoxCells(min, max), removed, true, islands);
            if (removed.Count == 0) return;
            Debris.Spawn(w, removed, feet + Vector3.up - dir, 6f, dir * 6f, 1);
            foreach (var island in islands) Debris.Spawn(w, island, feet + Vector3.up, 0.8f, Vector3.zero, 4);
            w.FlushRebuilds();
            SwiftBreakRpc(feet, dir);
        }

        [Rpc(SendTo.Server)]
        void SwiftBreakRpc(Vector3 feet, Vector3 dir) => SpellServer.SwiftBreak(this, feet, dir);

        [Rpc(SendTo.Server)]
        void CrashRpc(Vector3 point, Vector3 velocity) => SpellServer.Crash(this, point, velocity);

        void OnHardLanding(float speed)
        {
            // no fall damage: kill barriers handle falling out of the arena
        }

        [Rpc(SendTo.Server)]
        void FallRpc(float damage)
        {
            if (IsAlive) ServerApplyDamage(Mathf.Clamp(damage, 0f, 200f), Combatant.NoAttacker, FeetPosition, Vector3.up, -1, 0f, DeathCause.Fall);
        }

        bool _barrierReported;

        void CheckHazards()
        {
            if (_barrierReported) return;
            var arena = Arena.Current;
            bool below = arena != null && transform.position.y < arena.KillY;
            if (below || KillVolume.AnyContains(transform.position) || KillVolume.AnyContains(FeetPosition + Vector3.up * 0.2f))
            {
                _barrierReported = true;
                BarrierRpc();
            }
        }

        [Rpc(SendTo.Server)]
        void BarrierRpc()
        {
            if (IsAlive) ServerKillByHazard(DeathCause.Barrier, transform.position);
        }

        // ---------------- rounds ----------------

        /// <summary>Server: start of a round — alive, full health, no mana, at this spawn.</summary>
        public void ServerBeginRound(Vector3 pos, float yaw)
        {
            if (!IsServer) return;
            ServerRevive();
            Mana.Value = new ManaState { Value = 0f, Time = WorldNet.Now };
            FoolCards.Value = default;
            Trapped.Value = false;
            for (int i = 0; i < _serverLastCast.Length; i++) _serverLastCast[i] = -10;
            _serverRagdollUntil = 0f;
            RoundStartRpc(pos, yaw);
        }

        /// <summary>Server: joined mid-round — watch until the next one.</summary>
        public void ServerMakeSpectator()
        {
            if (!IsServer) return;
            Health.Value = 0f;
            Dead.Value = true;
        }

        [Rpc(SendTo.Everyone)]
        void RoundStartRpc(Vector3 pos, float yaw)
        {
            ClearLiveRagdoll();
            if (LastRagdoll) Destroy(LastRagdoll);
            if (IsOwner)
            {
                BlindOverlay.Clear();
                _teleportCounter++;
                _barrierReported = false;
                PushLoadout(); // cards picked between rounds come into play now
                Motor.Teleport(pos, yaw);
                Motor.enabled = true;
                Caster.ResetForRound();
                WriteState();
                SetVisible(true);
            }
            else
            {
                transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
                _hiddenUntilTeleport = false;
                SetVisible(true);
            }
        }

        protected override void OnServerKilled(ulong attackerClientId)
        {
            FoolCards.Value = default; // a rolled card is lost on death
            Trapped.Value = false;
            Mana.Value = new ManaState { Value = Mana.Value.At(WorldNet.Now), Time = WorldNet.Now }; // the Magician's boost ends
            if (attackerClientId == OwnerClientId || attackerClientId == NoAttacker) return;
            var killer = ServerFind(attackerClientId);
            if (killer != null) killer.Kills.Value++;
        }

        protected override void OnDiedLocal()
        {
            _hiddenUntilTeleport = !IsOwner;
            if (IsOwner)
            {
                Motor.enabled = false;
                Motor.Velocity = Vector3.zero;
                Caster.CancelAll();
            }
        }

        protected override void OnDeadChanged(bool prev, bool dead)
        {
            if (IsOwner && dead && Motor != null) { Motor.enabled = false; Caster.CancelAll(); }
            if (dead) SetVisible(false);
            else if (IsOwner || !_hiddenUntilTeleport) SetVisible(true);
        }

        public override void SetVisible(bool on)
        {
            base.SetVisible(on);
            UpdateFaceVisible();
            if (CC) CC.enabled = on;
        }

        // ---------------- loadout / name / face / colour ----------------

        void PushLoadout()
        {
            if (!IsOwner || !IsSpawned) return;
            Equipped.Value = EquippedCards.From(Loadouts.Selected);
        }

        void PushName()
        {
            if (!IsOwner || !IsSpawned) return;
            var fs = new FixedString32Bytes();
            fs.CopyFromTruncated(GameSettings.PlayerName ?? "");
            PlayerName.Value = fs;
        }

        void PushFace()
        {
            if (!IsOwner || !IsSpawned) return;
            var fs = new FixedString4096Bytes();
            fs.CopyFromTruncated(FaceArt.Encoded ?? "");
            if (Face.Value.Equals(fs)) return;
            Face.Value = fs;
            ApplyFace();
        }

        void OnFaceChanged(FixedString4096Bytes prev, FixedString4096Bytes next) => ApplyFace();

        void ApplyFace()
        {
            if (_facePainter == null) return;
            var strokes = FaceArt.Decode(Face.Value.ToString());
            _hasFace = strokes.Count > 0;
            _facePainter.Draw(strokes);
            UpdateFaceVisible();
        }

        void UpdateFaceVisible()
        {
            if (_faceRenderer != null) _faceRenderer.enabled = _hasFace && Visible && !HasLiveRagdoll;
        }

        [Rpc(SendTo.Server)]
        void RequestColorRpc(sbyte wanted)
        {
            int n = PlayerPalette.Count;
            bool Taken(int i)
            {
                foreach (var p in All)
                    if (p != null && p != this && p.ColorIndex.Value == i) return true;
                return false;
            }
            int start = wanted >= 0 && wanted < n ? wanted : Random.Range(0, n);
            for (int k = 0; k < n; k++)
            {
                int i = (start + k) % n;
                if (!Taken(i)) { ColorIndex.Value = (byte)i; return; }
            }
            ColorIndex.Value = (byte)start;
        }

        void OnColorChanged(byte prev, byte next) => ApplyColor();

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        MaterialPropertyBlock _mpb;

        void ApplyColor()
        {
            if (ColorIndex.Value == 255 || model == null) return;
            _mpb ??= new MaterialPropertyBlock();
            Color c = PlayerColor;
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name == FaceDecal.ObjectName) continue; // the face keeps its own colours
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(BaseColorId, c);
                _mpb.SetColor(ColorId, c);
                r.SetPropertyBlock(_mpb);
            }
        }
    }
}
