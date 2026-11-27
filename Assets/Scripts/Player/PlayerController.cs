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
    public partial class PlayerController : Combatant
    {
        public static PlayerController Local { get; private set; }
        public static readonly List<PlayerController> All = new();
        public static event System.Action<PlayerController> LocalSpawned, LocalDespawned;

        [Tooltip("Remote players are rendered this far in the past for smooth interpolation.")]
        public float interpolationDelay = 0.1f;

        public readonly NetworkVariable<FixedString32Bytes> PlayerName = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        /// <summary>Palette index assigned by the host (unique per lobby). 255 = not assigned yet.</summary>
        public readonly NetworkVariable<byte> ColorIndex = new(255);
        /// <summary>Team Deathmatch: the team they're on and its colour (default = not in one).</summary>
        public readonly NetworkVariable<TeamState> Side = new();
        public override byte MatchTeam => IsSpawned ? Side.Value.Team : (byte)0;
        /// <summary>The palette index they're coloured: their team's in a Team Deathmatch, else their own (255 = none yet).</summary>
        public byte BodyColor => MatchTeam != 0 ? Side.Value.Color : ColorIndex.Value;
        public readonly NetworkVariable<FixedString4096Bytes> Face = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        /// <summary>The cosmetics worn (<see cref="OutfitPiece"/>).</summary>
        public readonly NetworkVariable<FixedString512Bytes> Outfit = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public readonly NetworkVariable<int> Wins = new();
        public readonly NetworkVariable<int> Kills = new();
        public readonly NetworkVariable<ManaState> Mana = new();
        /// <summary>The cards this player brought into the round (owner writes it when the round starts).</summary>
        public readonly NetworkVariable<EquippedCards> Equipped = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        /// <summary>The skin this player is wearing (Skinwalker), until the next round.</summary>
        public readonly NetworkVariable<SkinState> Skin = new();
        /// <summary>The cards this player has right now: the ones they brought, or those of the body whose skin they took.</summary>
        public EquippedCards Hand => IsSpawned && Skin.Value.On ? Skin.Value.Hand : Equipped.Value;
        /// <summary>The Fool: the free single-use card it rolled, per slot (None = The Fool is showing).</summary>
        public readonly NetworkVariable<EquippedCards> FoolCards = new();
        /// <summary>The practice chamber this player is in (0 = in the match). See <see cref="Practice"/>.</summary>
        public readonly NetworkVariable<byte> PracticeSlot = new();
        public bool InPractice => IsSpawned && PracticeSlot.Value != 0;
        /// <summary>While in the practice range: be pulled into the next round when it starts (H toggles it; on by default).</summary>
        public readonly NetworkVariable<bool> JoinRounds = new(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public override bool InPracticeChamber => InPractice;

        /// <summary>The card a slot actually casts right now (a Fool slot holding a rolled card casts that).</summary>
        public CardId CardInSlot(int slot)
        {
            // one of the Undead: nothing but a bite
            if (IsUndead) return slot == 0 ? CardId.InfectiousBite : CardId.None;
            var c = IsSpawned ? Hand[slot] : Loadouts.Selected.Get(slot);
            if (c == CardId.TheFool && IsSpawned && FoolCards.Value[slot] != CardId.None) return FoolCards.Value[slot];
            return c;
        }

        /// <summary>How many Temperance cards this player brought (each: more mana, less health).</summary>
        public int Temperance => IsUndead ? 0 : Cards.TemperanceCount(IsSpawned ? Hand : EquippedCards.From(Loadouts.Selected));

        /// <summary>What this card costs this player.</summary>
        public int CostOf(CardInfo info) => info == null ? 0 : info.cost;

        /// <summary>The most mana this player can hold: 10, +3 per Temperance; the Undead only 5.</summary>
        public byte ManaCap => IsUndead ? SpellPlans.UndeadMana : (byte)(ManaState.Max + SpellPlans.TemperanceMana * Temperance);

        public override float MaxHealth => IsUndead ? SpellPlans.UndeadHealth : maxHealth + Cards.HealthBonus(IsSpawned ? Hand : EquippedCards.From(Loadouts.Selected));

        /// <summary>Is this slot showing a free card rolled by The Fool?</summary>
        public bool IsFoolCard(int slot) => IsSpawned && Hand[slot] == CardId.TheFool && FoolCards.Value[slot] != CardId.None;

        public override string FeedName => DisplayName;
        public override int FeedColor => BodyColor == 255 ? -1 : BodyColor;
        /// <summary>The colour the body is (a skinwalker's: the one of the skin they took).</summary>
        public Color PlayerColor
        {
            get
            {
                int index = BodyColor;
                if (IsSpawned && Skin.Value.On)
                {
                    if (Skin.Value.Kind == SkinState.Undead) return SpellPlans.UndeadSkin;
                    if (Skin.Value.Kind == SkinState.Dummy) return DummyColor;
                    index = Skin.Value.Color;
                }
                return PlayerPalette.Get(index == 255 ? -1 : index);
            }
        }

        /// <summary>A test dummy's own colour.</summary>
        static Color DummyColor
        {
            get
            {
                var prefab = Practice.DummyPrefab();
                var r = prefab != null ? prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
                var m = r != null ? r.sharedMaterial : null;
                return m != null && m.HasProperty(BaseColorId) ? m.GetColor(BaseColorId) : Color.white;
            }
        }

        /// <summary>The name over the head (a skinwalker's: that of the body whose skin they took).</summary>
        public string TagName => IsSpawned && Skin.Value.On ? Skin.Value.Name.ToString() : DisplayName;

        /// <summary>The face drawing on the head (a skinwalker's: that of the body whose skin they took).</summary>
        public string ShownFace
        {
            get
            {
                if (!IsSpawned || !Skin.Value.On) return Face.Value.ToString();
                var from = Skin.Value.Face == 0 ? null : WorldNet.FindCaster(Skin.Value.Face);
                return from != null ? from.Face.Value.ToString() : "";
            }
        }

        /// <summary>The cosmetics on the body (a skinwalker's: those of the body whose skin they took).</summary>
        public string ShownOutfit
        {
            get
            {
                if (!IsSpawned || !Skin.Value.On) return Outfit.Value.ToString();
                var from = Skin.Value.Face == 0 ? null : WorldNet.FindCaster(Skin.Value.Face);
                return from != null ? from.Outfit.Value.ToString() : "";
            }
        }
        public string ColorHex => BodyColor == 255 ? "FFFFFF" : PlayerPalette.HexString(BodyColor);

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
        public override bool LungingView => IsRagdolled && _viewPose == SpellPose.BiteCrouch && ViewPoseTime >= SpellPlans.BiteCrouch - 0.05f;

        FacePainter _facePainter;
        MeshRenderer _faceRenderer;
        bool _hasFace;

        /// <summary>A received state, at the time its owner took it (their clock).</summary>
        struct Snap { public float time; public NetState s; }
        /// <summary>Our clock minus the owner's, as seen by the quickest recent snapshot (so packets that arrive late or in
        /// bunches are still played back evenly, <see cref="interpolationDelay"/> behind).</summary>
        float _clockOffset;
        bool _haveOffset;

        /// <summary>The clock snapshots are stamped with (real time: unaffected by hitches and time scale).</summary>
        static float SnapClock => (float)Time.realtimeSinceStartupAsDouble;
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
            if (GetComponent<SwordView>() == null) gameObject.AddComponent<SwordView>();
            if (GetComponent<HammerView>() == null) gameObject.AddComponent<HammerView>();
            if (GetComponent<GunView>() == null) gameObject.AddComponent<GunView>();
            if (GetComponent<WolfgangView>() == null) gameObject.AddComponent<WolfgangView>();
            if (GetComponent<SpeedDemonView>() == null) gameObject.AddComponent<SpeedDemonView>();
            if (GetComponent<BombHandsFx>() == null) gameObject.AddComponent<BombHandsFx>();
            Motor.enabled = false;
            Caster.enabled = false;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            All.Add(this);
            gameObject.layer = Layers.Player;
            ColorIndex.OnValueChanged += OnColorChanged;
            Side.OnValueChanged += OnSideChanged;
            Status.OnValueChanged += OnStatusChanged;
            Equipped.OnValueChanged += OnEquippedChanged;
            Skin.OnValueChanged += OnSkinChanged;
            ApplyColor();
            _facePainter = new FacePainter();
            _faceRenderer = FaceDecal.Attach(model, transform, _facePainter.Texture);
            Face.OnValueChanged += OnFaceChanged;
            ApplyFace();
            Outfit.OnValueChanged += OnOutfitChanged;
            ApplyOutfit();

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
                Motor.ChariotBlocked += OnChariotBlocked;
                Motor.HammerSwing += OnHammerSwing;
                Motor.HammerLanded += OnHammerLanded;
                WorldNet.ChariotEnded += OnChariotEnded;
                WorldNet.LoversEnded += OnLoversEnded;
                Motor.Moved += OnMoved;
                WorldNet.WarpEnded += OnWarpEnded;
                WorldNet.SwiftEnded += OnSwiftEnded;
                Health.OnValueChanged += OnOwnHealthChanged;
                PushName();
                PushFace();
                PushOutfit();
                PushLoadout();
                GameSettings.NameChanged += PushName;
                FaceArt.Changed += PushFace;
                Wardrobe.Changed += PushOutfit;
                Loadouts.Changed += OnLoadoutsChanged;
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
            Wardrobe.Changed -= PushOutfit;
            Outfit.OnValueChanged -= OnOutfitChanged;
            Loadouts.Changed -= OnLoadoutsChanged;
            Face.OnValueChanged -= OnFaceChanged;
            ColorIndex.OnValueChanged -= OnColorChanged;
            Side.OnValueChanged -= OnSideChanged;
            Status.OnValueChanged -= OnStatusChanged;
            Equipped.OnValueChanged -= OnEquippedChanged;
            Skin.OnValueChanged -= OnSkinChanged;
            if (Motor != null)
            {
                Motor.Crashed -= OnCrashed; Motor.HardLanding -= OnHardLanding; Motor.StandBlocked -= OnStandBlocked; Motor.BubbleBlocked -= OnBubbleBlocked;
                Motor.SwiftBlocked -= OnSwiftBlocked; Motor.Moved -= OnMoved;
                Motor.ChariotBlocked -= OnChariotBlocked;
                Motor.HammerSwing -= OnHammerSwing;
                Motor.HammerLanded -= OnHammerLanded;
            }
            WorldNet.WarpEnded -= OnWarpEnded;
            WorldNet.SwiftEnded -= OnSwiftEnded;
            WorldNet.ChariotEnded -= OnChariotEnded;
            WorldNet.LoversEnded -= OnLoversEnded;
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
            if (IsServer) TickComboBreak();
            if (IsOwner)
            {
                if (!Dead.Value)
                {
                    if (Trapped && !HeldStill) ChainTick(); // silenced by the Empress: free to move, but her chains keep you inside
                    // stuck on the front of someone's chariot: carried along in front of them
                    var driver = PinnedTo;
                    if (driver != null) Motor.PinTo(driver);
                    else Motor.Unpin();
                    Motor.ChariotCarrying = Motor.Charioting && CarryingSomeone();
                    TickOwnWarp();
                    TickTaken();
                    WriteState();
                    CheckHazards();
                }
                _viewVelocity = Motor.Velocity;
                _viewYaw = Motor.BodyYaw; // (the way the body faces, as others see it, not the camera)
                _viewPitch = Motor.BodyPitch;
                _viewFlags = BuildFlags();
                _viewPose = Caster.Pose;
                _viewPoseStart = Caster.PoseStart;
            }
            else Interpolate();
            SetBalled(ChargingNow());
            if (!IsOwner) ShapeRemoteBody();
            UpdateLiveRagdoll();
        }

        // ---------------- the Empress's chains ----------------

        float _chainYankAt;

        /// <summary>
        /// Owner: trapped and outside the domain that holds us (walked, thrown or carried out): the chains go taut
        /// (no further out) and haul us back in, whoever threw us.
        /// </summary>
        void ChainTick()
        {
            var net = WorldNet.Instance;
            if (net == null || !net.Domains.TryGetValue(TrappedBy.Value, out var fx) || fx.ended >= 0) return;
            Vector3 v = Motor.Velocity;
            if (!Motor.Ragdolled && !Motor.Carried)
            {
                // walking: the edge of her domain is a wall (a big step past it, e.g. a warp, still gets yanked back)
                Vector3 o = FeetPosition - fx.center;
                float d = new Vector2(o.x, o.z).magnitude, edge = SpellPlans.EmpressRadius - SpellPlans.ChainSlack;
                if (d > edge && d < edge + 1f)
                {
                    Motor.Nudge(-new Vector3(o.x, 0f, o.z) / d * (d - edge));
                    return;
                }
            }
            if (!SpellPlans.ChainPullBack(FeetPosition, fx.center, ref v, Time.deltaTime, out var yank)) return;
            if (Motor.Ragdolled && !Motor.Carried) { Motor.Velocity = v; return; }
            // on our feet (or spun by a tornado): yanked off them, back in
            if (Time.time < _chainYankAt) return;
            _chainYankAt = Time.time + 0.4f;
            Motor.Knock(yank, Motor.Carried ? 0.6f : 0f);
        }

        // ---------------- replication ----------------

        byte BuildFlags()
        {
            byte f = 0;
            if (Motor.IsGrounded) f |= NetState.Grounded;
            if (Motor.Ragdolled) f |= NetState.Ragdoll;
            if (Motor.Crouching) f |= NetState.Crouch;
            return f;
        }

        void WriteState()
        {
            _state.Value = new NetState
            {
                Position = transform.position,
                Velocity = Motor.Velocity,
                Yaw = Motor.BodyYaw, // (face-locked: others see the body hold still while our camera turns)
                Pitch = Motor.BodyPitch,
                Flags = BuildFlags(),
                Teleport = _teleportCounter,
                Pose = (byte)Caster.Pose,
                PoseStart = Caster.PoseStart,
                Stamp = SnapClock,
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
            float now = SnapClock, offset = now - s.Stamp;
            // the lowest offset seen is the quickest delivery; it creeps up slowly in case the route got slower
            if (!_haveOffset || offset < _clockOffset) { _clockOffset = offset; _haveOffset = true; }
            else _clockOffset += (offset - _clockOffset) * 0.01f;
            // (sequenced, so in order; the same moment twice, e.g. the value we already had on spawn, replaces it)
            if (_snaps.Count > 0 && s.Stamp <= _snaps[^1].time) _snaps[^1] = new Snap { time = _snaps[^1].time, s = s };
            else _snaps.Add(new Snap { time = s.Stamp, s = s });
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
            float t = SnapClock - _clockOffset - interpolationDelay; // on the owner's clock
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
            // stuck on the front of a chariot: drawn on the front of the ball as it is on this screen (not where its
            // owner's last report put it, a moment behind a ball doing 24 m/s)
            var driver = PinnedTo;
            if (driver != null) s.Position = SpellPlans.PinPoint(driver.transform.position, driver.ViewYaw);
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
                CostType.Passive => false, // never cast
                _ => Mathf.Floor(ManaNow - predictedSpend + 0.001f) >= CostOf(card),
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
            if (IsSpawned && Status.Value.Taken(WorldNet.Now)) return; // gone into The World: nothing until it's ready
            if (!IsAlive || slot >= Loadout.Slots || !MatchManager.CastingAllowedFor(this) || Trapped || Grasped || Hexed || Pinned || SpellServer.InBubble(this)) return;
            var card = (CardId)cardByte;
            bool free = IsFoolCard(slot) && FoolCards.Value[slot] == card;
            if (IsUndead ? CardInSlot(slot) != card : !free && Hand[slot] != card) return;
            if (Cards.IsPassive(card)) return; // Temperance works just by being there
            // the die over your head, a breath still going: nothing else meanwhile
            if (SpellServer.BusyChanneling(this)) return;
            if (!free && card == CardId.TheFool && FoolCards.Value[slot] != CardId.None) return; // already holding a rolled card
            var info = Cards.Get(card);
            if (info == null) return;
            double now = WorldNet.Now;
            // Warp Space's second portal: free, no cooldown
            bool secondPortal = card == CardId.WarpSpace && SpellServer.PortalRecastReady(this);
            if (secondPortal) free = true;
            if (now - _serverLastCast[slot] < Cards.Cooldown - 0.15 && !(free && card == CardId.WarpSpace)) return;
            // one card at a time (a little slack for lag) -- except Attraction and Repulsion cast together, which fuse
            if (now < _serverCastingUntil - 0.15 && !SpellServer.FuseReady(this, card)) return;
            // cost (a little slack for clock differences); a Fool card is free
            float mana = Mana.Value.At(now + 0.12);
            int cost = CostOf(info);
            if (!free && info.costType == CostType.Mana && Mathf.Floor(mana + 0.001f) < cost) return;
            if (!free && info.costType == CostType.Health && Health.Value <= info.cost) return;
            if (card == CardId.TheFool && !free)
            {
                var rolled = FoolCards.Value;
                rolled[slot] = SpellServer.RollFoolCard();
                FoolCards.Value = rolled;
                // no cooldown: the rolled card can be cast straight away
            }
            else if (!SpellServer.Cast(this, card, target, yaw, targetId)) return;
            else _serverLastCast[slot] = now;
            _serverCastingUntil = now + (SpellServer.CastTimeOverride >= 0f ? SpellServer.CastTimeOverride : Cards.CastTime(card));
            SpellServer.CastTimeOverride = -1f;
            ServerLowerShield(false); // (its owner lowers it too)
            if (card == CardId.Skinwalker) SpellServer.SkinwalkBegan(this, slot);
            if (card == CardId.Justice) SpellServer.JusticeBegan(this, free ? 0 : cost);
            if (card == CardId.Sebastian) SpellServer.SebastianBegan(this, slot, free ? 0 : cost, free);
            if (free && card == CardId.Sebastian) { } // a rolled Sebastian is used up when it fires (or is lost), not when the charge begins
            else if (free && IsFoolCard(slot) && card == CardId.WarpSpace && !secondPortal)
            {
                // a rolled Warp Space is two casts: it stays until the second portal (or the first closes unpaired)
                SpellServer.MarkFoolPortal(this, slot);
            }
            else if (free && IsFoolCard(slot))
            {
                // used once: back to The Fool
                var rolled = FoolCards.Value;
                rolled[slot] = CardId.None;
                FoolCards.Value = rolled;
            }
            else if (free) { } // Warp Space's second portal
            else if (info.costType == CostType.Mana)
                Mana.Value = Mana.Value.With(now, Mathf.Max(0f, Mana.Value.At(now) - cost));
            else
                ServerApplyDamage(info.cost, OwnerClientId, transform.position, Vector3.up, -1, 0f, DeathCause.Spell);
        }

        /// <summary>Server: a card The Fool rolled into <paramref name="slot"/> is spent (if it's still <paramref name="card"/>).</summary>
        public void ServerDropFoolCard(int slot, CardId card)
        {
            if (!IsSpawned || slot < 0 || slot >= Loadout.Slots || FoolCards.Value[slot] != card) return;
            var rolled = FoolCards.Value;
            rolled[slot] = CardId.None;
            FoolCards.Value = rolled;
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

        public void RequestFuseLaunch(int id, Vector3 dir) => FuseLaunchRpc(id, dir);

        [Rpc(SendTo.Server)]
        void FuseLaunchRpc(int id, Vector3 dir)
        {
            if (IsAlive) SpellServer.LaunchFuse(this, id, dir);
        }

        /// <summary>Owner: Rachmaninoff fires, from its muzzle.</summary>
        public void RequestShotgun(Vector3 muzzle, Vector3 dir) => ShotgunRpc(muzzle, dir);

        [Rpc(SendTo.Server)]
        void ShotgunRpc(Vector3 muzzle, Vector3 dir)
        {
            if (IsAlive) SpellServer.FireShotgun(this, muzzle, dir);
        }

        /// <summary>Owner: Sebastian was let go, charged or not yet.</summary>
        public void RequestSebastianRelease(bool charged) => SebastianReleaseRpc(charged);

        [Rpc(SendTo.Server)]
        void SebastianReleaseRpc(bool charged) => SpellServer.SebastianReleased(this, charged);

        /// <summary>Owner: interrupted charging Sebastian (the mana is gone).</summary>
        public void RequestSebastianLost() => SebastianLostRpc();

        [Rpc(SendTo.Server)]
        void SebastianLostRpc() => SpellServer.SebastianLost(this);

        /// <summary>Owner: Sebastian has charged up: it fires now, this way.</summary>
        public void RequestSebastianShot(float yaw, float pitch) => SebastianShotRpc(yaw, pitch);

        [Rpc(SendTo.Server)]
        void SebastianShotRpc(float yaw, float pitch)
        {
            if (IsAlive) SpellServer.SebastianShot(this, yaw, pitch);
        }

        /// <summary>Owner: held Skinwalker's button long enough, away from any corpse: the skin comes off.</summary>
        public void RequestShedSkin() => ShedSkinRpc();

        [Rpc(SendTo.Server)]
        void ShedSkinRpc()
        {
            if (!IsAlive || !Skin.Value.On || Status.Value.Taken(WorldNet.Now)) return;
            SpellServer.ShedSkin(this);
        }

        /// <summary>Server: this slot can be cast again straight away.</summary>
        public void ServerClearCooldown(int slot)
        {
            if (slot >= 0 && slot < _serverLastCast.Length) _serverLastCast[slot] = -10;
        }

        /// <summary>Owner: Undying Grasp's cast is done, facing this way.</summary>
        public void RequestGrasp(float yaw) => GraspRpc(yaw);

        [Rpc(SendTo.Server)]
        void GraspRpc(float yaw)
        {
            if (IsAlive) SpellServer.ReleaseGrasp(this, yaw);
        }

        /// <summary>Owner: Flame Bomb Toss: thrown now, at where the cursor is.</summary>
        public void RequestToss(Vector3 point) => TossRpc(point);

        [Rpc(SendTo.Server)]
        void TossRpc(Vector3 point)
        {
            if (IsAlive) SpellServer.ReleaseToss(this, point);
        }

        public void RequestHellfirePoint(int id, Vector3 p) => HellfirePointRpc(id, p);

        [Rpc(SendTo.Server)]
        void HellfirePointRpc(int id, Vector3 p)
        {
            if (IsAlive) SpellServer.HellfirePoint(this, id, p);
        }

        // ---------------- hit confirmation ----------------

        /// <summary>Local player: (victim object id, where, damage, killed) — one of your spells just hurt someone.</summary>
        public static event System.Action<ulong, Vector3, float, bool> LocalHitConfirmed;

        public void ServerHitConfirm(ulong victim, Vector3 where, float damage, bool killed) => HitConfirmRpc(victim, where, damage, killed);

        [Rpc(SendTo.Owner)]
        void HitConfirmRpc(ulong victim, Vector3 where, float damage, bool killed) => LocalHitConfirmed?.Invoke(victim, where, damage, killed);

        /// <summary>Server: this player struck someone with Death, or was struck by it: their screen darkens round the edges.</summary>
        public void ServerDeathGloom() => DeathGloomRpc();

        [Rpc(SendTo.Owner)]
        void DeathGloomRpc() => DeathGloom.Show();

        // ---------------- knockback / crashes / falls ----------------

        float _serverRagdollUntil;
        /// <summary>Server: was this player thrown recently (so a crash report is believable)?</summary>
        public bool ServerMayCrash => Time.time < _serverRagdollUntil + 3f;

        // ---------------- combo break (server) ----------------

        readonly ComboTracker _combo = new();

        /// <summary>Server: just broke a combo — can't be knocked down (ragdolled, carried) until this runs out.</summary>
        public bool ServerUnstoppable => _combo.Unstoppable;

        void ResetComboBreak() => _combo.Reset();

        /// <summary>Server: a hit is about to knock this player down. The third in the window breaks the combo instead.</summary>
        void NoteKnockdown() { if (_combo.NoteKnockdown()) ServerComboBreak(); }

        /// <summary>Server, every frame: lying ragdolled too long within the window breaks the combo.</summary>
        void TickComboBreak() { if (_combo.Tick(IsAlive, IsRagdolled && !LungingView && !HeldStill && !SpellServer.SkyFlung(this))) ServerComboBreak(); } // (a bite's own lunge, or the Wheel's throw, doesn't count)

        /// <summary>Server: thrown by the kick of our own shotgun. Interrupts nothing, but counts for combo breaks like any knockdown.</summary>
        public void ServerRecoil(Vector3 velocity, float ragdollSeconds)
        {
            if (!IsServer || !IsAlive) return;
            if (!ServerUnstoppable) NoteKnockdown();
            if (ServerUnstoppable) ragdollSeconds = 0f; // (that broke the combo, or one just did: thrown, on our feet)
            _serverRagdollUntil = Mathf.Max(_serverRagdollUntil, Time.time + ragdollSeconds);
            SpellServer.FreeGrasp(this);
            SpellServer.DispelMagician(this);
            KnockRpc(velocity, ragdollSeconds);
        }

        void ServerComboBreak()
        {
            if (Pinned) return; // stuck on the front of a chariot: that ends on its own
            _combo.Break();
            SpellServer.ReleaseFromCarry(this);
            ComboBreakRpc(SpellPlans.ComboBreakTime);
        }

        [Rpc(SendTo.Everyone)]
        void ComboBreakRpc(float seconds)
        {
            if (IsOwner && Motor != null && !Dead.Value) Motor.BreakFree(seconds);
            ComboBreakView.Create(this, seconds);
        }

        public override void ServerKnock(Vector3 velocity, float ragdollSeconds)
        {
            if (!IsServer) return;
            if (SpellServer.Bubbled(this) || SpellServer.Invulnerable(this)) return; // in your bubble, mid-warp, swift: nothing moves you
            if (SpellServer.Unmovable(this)) return; // praying for The Tower, charging with The Chariot: nothing moves you
            if (SpellServer.Hanging(this)) return; // hanging from The Hanged Man's noose: nothing moves you
            if (ragdollSeconds > 0f && SpellServer.Shielded(this)) return; // inside your own barrier: nothing moves you
            if (SpellServer.StasisHolds(this)) return; // frozen by this very attack (a later one thaws them, and throws them)
            // combo break: knocked down too much lately, they get up and can't be knocked down for a while (still thrown)
            if (ragdollSeconds > 0f && !ServerUnstoppable && !SpellServer.QuietKnock) NoteKnockdown();
            if (ServerUnstoppable) ragdollSeconds = 0f;
            _serverRagdollUntil = Mathf.Max(_serverRagdollUntil, Time.time + ragdollSeconds);
            if (ragdollSeconds > 0f) SpellServer.OnInterrupted(this, true);
            KnockRpc(velocity, ragdollSeconds);
            if (ragdollSeconds > 0f) SpellServer.SmallKnockedDown(this); // Smallify: knocked over while small hurts
        }

        protected override void OnServerDamaged(float amount) => SpellServer.OnInterrupted(this, false);

        public override void ServerCarry(Vector3 origin, Vector3 dir, double start, double captured, float phase)
        {
            if (SpellServer.Invulnerable(this) || ServerUnstoppable || SpellServer.Unmovable(this) || SpellServer.Hanging(this)) return;
            if (SpellServer.StasisHolds(this)) return;
            NoteKnockdown();
            if (ServerUnstoppable) return; // (that broke the combo)
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
        /// <summary>Server: out of Submerge's pool: the owner pops up, spinning.</summary>
        public void ServerEmerged() => EmergedRpc();

        [Rpc(SendTo.Owner)]
        void EmergedRpc()
        {
            if (Motor == null || Dead.Value) return;
            Motor.Knock(Vector3.up * SpellPlans.SubEmergeHop, 0f);
            if (Caster != null) Caster.PlayPose(SpellPose.SubEmerge);
        }

        public void ServerSiphonPunched() => SiphonPunchRpc();

        [Rpc(SendTo.Owner)]
        void SiphonPunchRpc()
        {
            if (Motor != null) Motor.EndSiphon();
            if (Caster != null) Caster.PlayPose(SpellPose.SiphonPunch);
        }

        /// <summary>Server: Severance's rush ends in the cut — stop and swing the sword across.</summary>
        public void ServerSeveranceSlash() => SeveranceSlashRpc();

        [Rpc(SendTo.Owner)]
        void SeveranceSlashRpc()
        {
            if (Motor != null) Motor.EndSiphon();
            if (Caster != null) Caster.PlayPose(SpellPose.SeveranceSlash);
        }

        /// <summary>Server: Parry deflected a blow — dash to <paramref name="to"/> (the hips) in <paramref name="seconds"/> and stab.</summary>
        public void ServerParryDash(Vector3 to, float seconds) => ParryDashRpc(to, seconds);

        [Rpc(SendTo.Owner)]
        void ParryDashRpc(Vector3 to, float seconds)
        {
            if (Caster != null) Caster.ParryDash(to, seconds);
        }

        /// <summary>Server: Rising Hammer's rush ends — stop and swing the hammer up.</summary>
        public void ServerHammerRise() => HammerRiseRpc();

        [Rpc(SendTo.Owner)]
        void HammerRiseRpc()
        {
            if (Motor != null) Motor.EndSiphon();
            if (Caster != null) Caster.PlayPose(SpellPose.HammerRise);
        }

        /// <summary>Owner: Descending Hammer is about to touch down — bring the hammer down.</summary>
        void OnHammerSwing()
        {
            if (Caster != null && Caster.Pose == SpellPose.HammerLeap) Caster.PlayPose(SpellPose.HammerSlam);
        }

        /// <summary>Owner: Descending Hammer touched down — the server smashes the ground and hits everyone round it.</summary>
        void OnHammerLanded(Vector3 point, Vector3 normal)
        {
            if (Caster != null && Caster.Pose == SpellPose.HammerLeap) Caster.PlayPose(SpellPose.HammerSlam);
            HammerSlamRpc(point, normal);
        }

        [Rpc(SendTo.Server)]
        void HammerSlamRpc(Vector3 point, Vector3 normal)
        {
            if (IsAlive) SpellServer.HammerSlam(this, point, normal);
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

        double _takenAt;
        bool _placed;

        /// <summary>
        /// Taken by The World: held still while we're crushed, then put where we'll stand inside it (and held there, the
        /// ground under it isn't up yet) until it's ready.
        /// </summary>
        void TickTaken()
        {
            var st = Status.Value;
            double now = WorldNet.Now;
            bool taken = st.Taken(now);
            if (taken && _takenAt != st.WorldFrom)
            {
                _takenAt = st.WorldFrom;
                _placed = false;
                Caster.CancelAll();
                Motor.StandUpNow();
            }
            if (taken && !_placed && st.Vanished(now))
            {
                _placed = true;
                _teleportCounter++;
                Motor.Teleport(st.WorldSpot, st.WorldYaw);
            }
            if (Motor.Held && !taken && VoxelWorld.Current != null)
            {
                // about to stand on it
                VoxelWorld.Current.FinishLate();
                Physics.SyncTransforms();
            }
            Motor.Held = taken;
        }

        /// <summary>Server: The World puts us somewhere (back inside it, or out of its way), on our feet.</summary>
        public override void ServerPlace(Vector3 hips, float yaw)
        {
            if (IsServer && IsAlive) PlaceRpc(hips, yaw);
        }

        [Rpc(SendTo.Owner)]
        void PlaceRpc(Vector3 hips, float yaw)
        {
            if (Motor == null || Dead.Value) return;
            _teleportCounter++;
            Motor.Teleport(hips, yaw);
        }

        // ---------------- The Star, The Sun (owner) ----------------

        /// <summary>Owner: our next star goes now, along <paramref name="dir"/>.</summary>
        public void RequestStarLaunch(int id, int index, Vector3 dir) => StarLaunchRpc(id, (byte)index, dir);

        [Rpc(SendTo.Server)]
        void StarLaunchRpc(int id, byte index, Vector3 dir) => SpellServer.LaunchStar(this, id, index, dir);

        /// <summary>Owner: someone's Sun is on our screen now (or no longer).</summary>
        public void ReportSunGaze(ulong sun, bool looking) => SunGazeRpc(sun, looking);

        [Rpc(SendTo.Server)]
        void SunGazeRpc(ulong sun, bool looking) => SpellServer.SunGaze(this, sun, looking);

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

        /// <summary>
        /// Rolled up into the chariot's ball right now? The owner goes by its own charge (it starts and stops on their
        /// screen first); everyone else by the server's.
        /// </summary>
        float _remoteStandRadius = -1f, _remoteStandHeight;
        Vector3 _remoteStandCenter;
        int _remoteShape; // 0 standing, 1 the chariot's ball, 2 crouched

        /// <summary>Someone else's body, as others bump into it: the chariot's ball while they charge, crouched, or standing.</summary>
        void ShapeRemoteBody()
        {
            if (CC == null) return;
            int shape = Balled ? 1 : (_viewFlags & NetState.Crouch) != 0 ? 2 : 0;
            if (_remoteShape == shape) return;
            if (_remoteStandRadius < 0f) { _remoteStandRadius = CC.radius; _remoteStandHeight = CC.height; _remoteStandCenter = CC.center; }
            _remoteShape = shape;
            float r = SpellPlans.ChariotRadius;
            PlayerMotor.CrouchShape(_remoteStandHeight, _remoteStandCenter, out float ch, out Vector3 cc);
            CC.radius = shape == 1 ? r : _remoteStandRadius;
            CC.height = shape == 1 ? r * 2f : shape == 2 ? ch : _remoteStandHeight;
            CC.center = shape == 1 ? SpellPlans.ChariotBallOffset : shape == 2 ? cc : _remoteStandCenter;
        }

        bool ChargingNow()
        {
            if (!IsAlive) return false;
            if (IsOwner) return Motor != null && Motor.Charioting;
            var net = WorldNet.Instance;
            if (net == null) return false;
            foreach (var c in net.Chariots) if (c.caster == NetworkObjectId && c.ended < 0) return true;
            return false;
        }

        // ---------------- The Chariot / The Lovers (owner) ----------------

        /// <summary>Someone is stuck on the front of our chariot.</summary>
        bool CarryingSomeone()
        {
            foreach (var o in Registry)
                if (o != null && o != this && o.IsSpawned && o.PinnedBy.Value == NetworkObjectId) return true;
            return false;
        }

        /// <summary>
        /// The charge ran into terrain. Under 6 voxels high: it's knocked away here at once so the charge carries on (the
        /// server does the same for everyone, and throws the chunks). 6 or more, or nothing that breaks: the crash.
        /// </summary>
        void OnChariotBlocked(Vector3 feet, Vector3 dir)
        {
            var w = VoxelWorld.Current;
            if (w == null) return;
            // (looking as far ahead as the smash would carve: a tall wall just past a low bump is still a wall)
            bool tall = SpellPlans.ChariotObstacle(w, feet, dir, SpellPlans.ChariotTallReach) >= SpellPlans.ChariotTallCells;
            if (!tall)
            {
                var lane = SpellPlans.ChariotLane(w, feet, dir, out _);
                if (lane.Count > 0)
                {
                    // (the host's world is the server's: it must be left for the server to break and send to everyone)
                    if (!IsServer)
                    {
                        var cells = new List<Vector3Int>(lane.Count);
                        foreach (var rc in lane) cells.Add(rc.cell);
                        w.RemoveCells(cells, null, true, null);
                        w.FlushRebuilds();
                    }
                    ChariotSmashRpc(feet, dir);
                    return;
                }
            }
            Motor.EndChariot(true);
            ChariotCrashRpc(feet, dir);
        }

        [Rpc(SendTo.Server)]
        void ChariotSmashRpc(Vector3 feet, Vector3 dir) => SpellServer.ChariotSmash(this, feet, dir);

        [Rpc(SendTo.Server)]
        void ChariotCrashRpc(Vector3 feet, Vector3 dir) => SpellServer.ChariotCrash(this, feet, dir);

        void OnChariotEnded(ulong caster, bool crashed)
        {
            if (caster != NetworkObjectId || Motor == null) return;
            Motor.EndChariot(crashed);
            if (Caster != null) Caster.ChariotEnded();
        }

        void OnLoversEnded(ulong caster)
        {
            if (caster == NetworkObjectId && Caster != null) Caster.LoversEnded();
        }

        /// <summary>Owner: let go of the Lovers beam (pressing its card again).</summary>
        public void RequestLoversCancel() => LoversCancelRpc();

        [Rpc(SendTo.Server)]
        void LoversCancelRpc() => SpellServer.CancelLovers(this);

        /// <summary>Server: this card's cooldown starts now (The Lovers: when its beam breaks, not when it was cast).</summary>
        public void ServerRestartCooldown(CardId card)
        {
            double now = WorldNet.Now;
            for (int i = 0; i < Loadout.Slots; i++)
                if (CardInSlot(i) == card) _serverLastCast[i] = now;
        }

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
        float _outsideFor;
        /// <summary>Out of the arena for this long (s): shattered. Warned about it once it's been <see cref="OutsideWarn"/> (a new round's arena, loading round us before we're moved into it, isn't worth one).</summary>
        public const float OutsideTime = 3f, OutsideWarn = 0.4f;
        /// <summary>Owner: out of bounds, with this long left to get back in (below 0: not out).</summary>
        public float OutOfBoundsLeft => IsAlive && !_barrierReported && _outsideFor >= OutsideWarn ? Mathf.Max(0f, OutsideTime - _outsideFor) : -1f;

        void CheckHazards()
        {
            if (_barrierReported) return;
            // inside a World: whatever its sphere swallowed (lava, the drop under the arena) can't reach us
            if (Motor.Held || WorldNet.InsideWorld(transform.position)) { _outsideFor = 0f; return; }
            _outsideFor = ArenaLimits.Outside(transform.position) ? _outsideFor + Time.deltaTime : 0f;
            bool below = transform.position.y < Practice.KillYAt(transform.position);
            if (SkyWrap.Near(transform.position) is { } sky)
            {
                // off a side of Skyland's area: in at the opposite side (free)
                if (sky.Across(transform.position, out var across)) { SkyAcross(across); return; }
                // over it: the bottom wraps instead of the kill height
                if (transform.position.y < sky.BottomY) { SkyWrapOwn(sky); return; }
                below = false;
            }
            // (out of the arena altogether: in pieces, if they don't get back in)
            bool tooLong = _outsideFor >= OutsideTime;
            if (below || tooLong || KillVolume.AnyContains(transform.position) || KillVolume.AnyContains(FeetPosition + Vector3.up * 0.2f))
            {
                _barrierReported = true;
                BarrierRpc(tooLong || below && _outsideFor > 0f); // (fell off outside the limits: in pieces too)
            }
            else if (TrainTrack.Hits(transform.position, out var train))
            {
                _barrierReported = true;
                TrainRpc(train.normalized);
            }
        }

        [Rpc(SendTo.Server)]
        void TrainRpc(Vector3 dir)
        {
            if (IsAlive) ServerKillByHazard(DeathCause.Barrier, transform.position, Vector3.ClampMagnitude(dir, 1f) * SpellPlans.TrainThrow + Vector3.up * SpellPlans.TrainLift);
        }

        SkyWrap.Heat _skyHeat;
        /// <summary>Owner: how full Skyland's fall bar is (0..1+), draining as it goes.</summary>
        public float SkyHeat => _skyHeat.Now;

        /// <summary>Owner: fell out of Skyland's bottom: back in over the top, still falling (or, if that fills the bar, in bits).</summary>
        void SkyWrapOwn(SkyWrap sky)
        {
            if (_skyHeat.Add())
            {
                _barrierReported = true;
                BarrierRpc(true);
                return;
            }
            Motor.MoveTo(sky.TopAbove(transform.position), Motor.Yaw, Motor.Velocity); // (Moved: others snap)
            WriteState();
            SkyWrapRpc(true);
        }

        [Rpc(SendTo.Everyone)]
        void SkyWrapRpc(bool fell)
        {
            SkyWrapWind.Play(this);
            if (fell) HexBodyFx.SkyFall(this); // (everyone fills the bar beside us)
        }

        /// <summary>Owner: off a side of Skyland: in at the opposite side, same height, still moving. Doesn't fill the bar.</summary>
        void SkyAcross(Vector3 to)
        {
            Motor.MoveTo(to, Motor.Yaw, Motor.Velocity);
            WriteState();
            SkyWrapRpc(false);
        }

        [Rpc(SendTo.Server)]
        void BarrierRpc(bool shatter)
        {
            if (IsAlive) ServerKillByHazard(DeathCause.Barrier, transform.position, shatter: shatter);
        }

        // ---------------- rounds ----------------

        /// <summary>Server: start of a round — alive, full health, at this spawn, with no mana until <paramref name="manaFrom"/> (the end of the countdown).</summary>
        public void ServerBeginRound(Vector3 pos, float yaw, double manaFrom) => ServerSpawnAt(pos, yaw, 0f, manaFrom);

        /// <summary>Server: (re)spawn in a practice chamber — alive, full health and full mana.</summary>
        public void ServerPracticeSpawn(Vector3 pos, float yaw)
        {
            if (IsServer) Side.Value = default; // off the team while practising
            ServerSpawnAt(pos, yaw, ManaState.Max, WorldNet.Now);
        }

        void ServerSpawnAt(Vector3 pos, float yaw, float mana, double manaFrom)
        {
            if (!IsServer) return;
            Status.Value = default; // a new round: nobody's Undead any more
            Skin.Value = default;   // and everyone in their own skin
            ServerRevive();
            ServerRefillShield(true);
            // mana starts counting at manaFrom (before then it reads as this value: 0 during the countdown)
            Mana.Value = new ManaState { Value = Mathf.Min(mana, ManaCap), Time = manaFrom, Cap = ManaCap };
            FoolCards.Value = default;
            TrappedBy.Value = 0;
            PinnedBy.Value = 0;
            for (int i = 0; i < _serverLastCast.Length; i++) _serverLastCast[i] = -10;
            _serverRagdollUntil = 0f;
            ResetComboBreak();
            RoundStartRpc(pos, yaw);
        }

        /// <summary>
        /// Server: raised from the dead as one of the Undead, on <paramref name="master"/>'s side, standing at
        /// <paramref name="feet"/>: sickly green, 30 health, 5 mana at most, and nothing but Infectious Bite.
        /// </summary>
        public void ServerReviveUndead(Vector3 feet, float yaw, ulong master)
        {
            if (!IsServer || !IsSpawned || !Dead.Value) return;
            Status.Value = new BodyStatus { Undead = true, Master = master };
            if (master != NetworkObjectId && !InPractice)
            {
                // on the master's team now (Team Deathmatch), though its win won't pay them
                var m = WorldNet.FindCaster(master);
                if (m != null) Side.Value = m.Side.Value;
                MatchManager.ServerLoseShare(this);
            }
            ServerRevive();
            ServerRefillShield(true);
            Mana.Value = new ManaState { Value = 0f, Time = WorldNet.Now, Cap = SpellPlans.UndeadMana };
            FoolCards.Value = default;
            TrappedBy.Value = 0;
            PinnedBy.Value = 0;
            for (int i = 0; i < _serverLastCast.Length; i++) _serverLastCast[i] = -10;
            _serverRagdollUntil = 0f;
            ResetComboBreak();
            UndeadRiseRpc(feet + Vector3.up * 0.95f, yaw);
        }

        [Rpc(SendTo.Everyone)]
        void UndeadRiseRpc(Vector3 pos, float yaw)
        {
            HexBodyFx.SkyClear(this);
            ClearLiveRagdoll();
            if (LastRagdoll) Destroy(LastRagdoll);
            if (IsOwner)
            {
                BlindOverlay.Clear(); DeathGloom.Clear();
                _teleportCounter++;
                _barrierReported = false;
                _outsideFor = 0f;
                _skyHeat.Clear();
                Motor.Teleport(pos, yaw);
                Motor.UnstoppableUntil = 0f;
                Motor.enabled = true;
                Caster.ResetForRound();
                WriteState();
            }
            else
            {
                transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
                _hiddenUntilTeleport = false;
            }
            SetVisible(true);
            ApplyColor();
            NecroVfx.RiseBurst(pos);
        }

        /// <summary>Server: the loadout changed (a new round, or a change in practice): Temperance's health and mana follow it.</summary>
        void OnEquippedChanged(EquippedCards prev, EquippedCards next)
        {
            if (!IsServer || !IsSpawned) return;
            if (Skin.Value.On)
            {
                // wearing someone's skin: their moves, not these (in practice, changing your loadout takes it off)
                if (!InPractice) return;
                Skin.Value = default;
                FoolCards.Value = default;
            }
            float oldMax = maxHealth + Cards.HealthBonus(prev);
            if (!IsUndead && IsAlive && MaxHealth > oldMax) Health.Value = Mathf.Min(MaxHealth, Health.Value + (MaxHealth - oldMax));
            else if (IsAlive) Health.Value = Mathf.Min(Health.Value, MaxHealth);
            if (Mana.Value.Cap != ManaCap)
            {
                var m = Mana.Value;
                double t = WorldNet.Now;
                // (before mana starts flowing at the end of the countdown, the value is kept as it is)
                if (t >= m.Time) m = m.With(t, Mathf.Min(m.At(t), ManaCap));
                m.Cap = ManaCap;
                Mana.Value = m;
            }
            // Speed Demon: less shield
            byte shieldCap = SpellPlans.ShieldCapFor(this);
            if (Shield.Value.Cap != shieldCap)
            {
                var sh = Shield.Value;
                double t = WorldNet.Now;
                sh.Value = Mathf.Min(sh.At(t), shieldCap > 0 ? shieldCap : SpellPlans.ShieldHealth);
                sh.Time = t;
                sh.Cap = shieldCap;
                Shield.Value = sh;
            }
        }

        void OnSkinChanged(SkinState prev, SkinState next)
        {
            ApplyColor();
            ApplyFace();
            ApplyOutfit();
        }

        /// <summary>Server: Skinwalker: in this skin from now on, with its moves, full health and 10 mana.</summary>
        public void ServerTakeSkin(SkinState skin)
        {
            if (!IsServer || !IsAlive) return;
            Skin.Value = skin;
            FoolCards.Value = default;
            Health.Value = MaxHealth;
            var m = Mana.Value.With(WorldNet.Now, Mathf.Min(SpellPlans.SkinMana, ManaCap));
            m.Cap = ManaCap;
            Mana.Value = m;
            for (int i = 0; i < _serverLastCast.Length; i++) _serverLastCast[i] = -10;
        }

        /// <summary>Server: the skin comes off: their own colour, face and moves again (health and mana stay as they are, within their own limits).</summary>
        public void ServerShedSkin()
        {
            if (!IsServer || !IsAlive) return;
            Skin.Value = default;
            FoolCards.Value = default;
            Health.Value = Mathf.Min(Health.Value, MaxHealth);
            double now = WorldNet.Now;
            var m = Mana.Value.With(now, Mathf.Min(ManaNow, ManaCap));
            m.Cap = ManaCap;
            Mana.Value = m;
        }

        void OnStatusChanged(BodyStatus prev, BodyStatus next)
        {
            if (prev.Undead != next.Undead) ApplyColor();
        }

        /// <summary>
        /// Server: Wheel of Fortune rolled a 1 (or its blast landed): thrown, ragdolled (crashing through what's above). Sent straight
        /// to the owner (the card's own effect, so nothing that would stop a hit stops it).
        /// </summary>
        public void ServerFling(Vector3 velocity, float ragdollSeconds)
        {
            if (!IsServer || !IsAlive || SpellServer.Hanging(this) || SpellServer.Charging(this)) return; // (the noose holds you; nothing throws The Chariot)
            _serverRagdollUntil = Mathf.Max(_serverRagdollUntil, Time.time + ragdollSeconds); // (so its crashes are believed)
            SpellServer.OnInterrupted(this, true);
            FlingRpc(velocity, ragdollSeconds);
        }

        [Rpc(SendTo.Owner)]
        void FlingRpc(Vector3 velocity, float ragdollSeconds)
        {
            if (Dead.Value || Motor == null) return;
            Motor.StandUpNow(); // (out of anything holding it down first)
            // (limp even while a combo break would keep them on their feet)
            float unstoppable = Motor.UnstoppableUntil;
            Motor.UnstoppableUntil = 0f;
            Motor.Knock(velocity, ragdollSeconds);
            Motor.UnstoppableUntil = unstoppable;
        }

        /// <summary>Owner: Wheel of Fortune rolled a 6: the Mana Siphon charge, twice as fast (off the ground first, if need be).</summary>
        public void ServerStartSiphon() => StartSiphonRpc();

        [Rpc(SendTo.Owner)]
        void StartSiphonRpc()
        {
            if (Motor == null || Dead.Value) return;
            Motor.StandUpNow();
            Motor.Siphon(SpellPlans.SiphonTime, SpellPlans.GoldSpeed);
            if (Caster != null) Caster.PlayPose(SpellPose.Siphon);
        }

        /// <summary>Owner: the bite landed (or a hooked surface was reached): back on your feet at once.</summary>
        public void ServerStandUp() => StandUpRpc();

        [Rpc(SendTo.Owner)]
        void StandUpRpc()
        {
            if (Motor != null && !Dead.Value) Motor.StandUpIn(SpellPlans.BiteRecover); // down a moment longer, then up
        }

        /// <summary>Owner: where our cursor landed as our tower finished rising (it turns to face it).</summary>
        public void RequestTowerAim(Vector3 point) => TowerAimRpc(point);

        [Rpc(SendTo.Server)]
        void TowerAimRpc(Vector3 point) => SpellServer.TowerAim(this, point);

        /// <summary>Owner: let go of the Flame Thrower's button.</summary>
        public void RequestBreathStop() => BreathStopRpc();

        [Rpc(SendTo.Server)]
        void BreathStopRpc() => SpellServer.StopBreath(this);

        /// <summary>Owner: pressed a running card again (Death's aura, the Grapple Hook): it ends, free.</summary>
        public void RequestCardCancel(CardId card) => CardCancelRpc((byte)card);

        [Rpc(SendTo.Server)]
        void CardCancelRpc(byte card) => SpellServer.CancelCard(this, (CardId)card);

        /// <summary>Server: joined mid-round — watch until the next one.</summary>
        public void ServerMakeSpectator()
        {
            if (!IsServer) return;
            Health.Value = 0f;
            Dead.Value = true;
        }

        // ---------------- practice ----------------

        public enum PracticeAction : byte { Enter, Reset, Summon, Leave, Corpse, NextMap, Hazards }

        /// <summary>Owner: ask the server to enter / reset / leave your practice chamber, or drop a dummy at <paramref name="point"/>.</summary>
        /// <param name="kind">Which dummy (<see cref="TestDummy.DummyKind"/>).</param>
        public void RequestPractice(PracticeAction action, Vector3 point = default, TestDummy.DummyKind kind = TestDummy.DummyKind.Plain) => PracticeRpc((byte)action, point, (byte)kind);

        [Rpc(SendTo.Server)]
        void PracticeRpc(byte action, Vector3 point, byte kind)
        {
            switch ((PracticeAction)action)
            {
                case PracticeAction.Enter: Practice.ServerEnter(this); break;
                case PracticeAction.Reset: Practice.ServerReset(this); break;
                case PracticeAction.Summon: Practice.ServerSummon(this, point, false, (TestDummy.DummyKind)(kind % TestDummy.DummyKinds)); break;
                case PracticeAction.Corpse: Practice.ServerSummon(this, point, true); break;
                case PracticeAction.Leave: Practice.ServerLeave(this); break;
                case PracticeAction.NextMap: Practice.ServerNextMap(this); break;
                case PracticeAction.Hazards: Practice.ServerHazards(this); break;
            }
        }

        /// <summary>Server: out of the chamber and watching the match (the hidden body waits in the arena).</summary>
        public void ServerLeavePractice(Vector3 arenaPos)
        {
            if (!IsServer) return;
            ServerMakeSpectator();
            FoolCards.Value = default;
            TrappedBy.Value = 0;
            PinnedBy.Value = 0;
            LeavePracticeRpc(arenaPos);
        }

        [Rpc(SendTo.Everyone)]
        void LeavePracticeRpc(Vector3 arenaPos)
        {
            ClearLiveRagdoll();
            if (LastRagdoll) Destroy(LastRagdoll);
            if (IsOwner)
            {
                BlindOverlay.Clear(); DeathGloom.Clear();
                _teleportCounter++;
                Caster.CancelAll();
                Motor.Teleport(arenaPos, Motor.Yaw);
                Motor.enabled = false;
                WriteState();
            }
            else transform.position = arenaPos;
            SetVisible(false); // also turns the character controller back off (Teleport switched it on)
        }

        /// <summary>Owner: in practice, a loadout change takes effect straight away.</summary>
        void OnLoadoutsChanged()
        {
            if (InPractice) PushLoadout();
        }

        /// <summary>A dead player's body stays where it fell until the next round (RoundStartRpc clears it).</summary>
        protected override float CorpseSeconds => 0f;

        [Rpc(SendTo.Everyone)]
        void RoundStartRpc(Vector3 pos, float yaw)
        {
            HexBodyFx.SkyClear(this);
            ClearLiveRagdoll();
            if (LastRagdoll) Destroy(LastRagdoll);
            if (IsOwner)
            {
                BlindOverlay.Clear(); DeathGloom.Clear();
                _teleportCounter++;
                _barrierReported = false;
                _outsideFor = 0f;
                _skyHeat.Clear();
                PushLoadout(); // cards picked between rounds come into play now
                if (VoxelWorld.Current != null) VoxelWorld.Current.FinishBuild(); // about to stand on it
                Motor.Teleport(pos, yaw);
                Motor.UnstoppableUntil = 0f;
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
            TrappedBy.Value = 0;
            PinnedBy.Value = 0;
            Mana.Value = new ManaState { Value = Mana.Value.At(WorldNet.Now), Time = WorldNet.Now, Cap = Mana.Value.Cap }; // the Magician's boost ends
            var st = Status.Value; // floating, gold, chilled, doomed: all over (being Undead stays until raised again)
            st.HangUntil = st.GoldUntil = st.SlowUntil = st.DoomAt = st.StrongUntil = st.GraspUntil = st.BloatAt = st.SpinUntil = 0;
            st.LitUntil = st.MoonAt = st.SunUntil = st.WorldUntil = st.StasisUntil = st.ShatterUntil = st.MindUntil = 0;
            st.JusticeOn = 0;
            st.DeathMarked = false; st.BileFrom = st.BileAt = 0;
            st.SmallUntil = st.EatenUntil = st.EphemUntil = st.SubUntil = 0;
            Status.Value = st;
            if (!InPractice) ServerGrantGold(Rewards.Death); // dying in a round still pays a little
            if (attackerClientId == OwnerClientId || attackerClientId == NoAttacker || InPractice) return;
            var killer = ServerFind(attackerClientId);
            if (killer != null && !Teammates(killer, this)) // (nothing for killing a teammate)
            {
                killer.Kills.Value++;
                killer.ServerRefillShield(false);
                killer.ServerGrantGold(Rewards.Kill);
            }
        }

        /// <summary>Server: pay this player gold (it's saved on their own PC).</summary>
        public void ServerGrantGold(int amount) => GoldRpc(amount);

        [Rpc(SendTo.Owner)]
        void GoldRpc(int amount) => Wallet.Earn(amount);

        /// <summary>Server: a round this player was in just ended (their first one ever pays a bonus, on their own PC).</summary>
        public void ServerRoundPlayed() => RoundPlayedRpc();

        [Rpc(SendTo.Owner)]
        void RoundPlayedRpc() => Wallet.RoundPlayed();

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

        void PushOutfit()
        {
            if (!IsOwner || !IsSpawned) return;
            var fs = new FixedString512Bytes();
            fs.CopyFromTruncated(Wardrobe.Encoded());
            if (!Outfit.Value.Equals(fs)) Outfit.Value = fs;
        }

        string _wornOutfit = "";

        void OnOutfitChanged(FixedString512Bytes prev, FixedString512Bytes next) => ApplyOutfit();

        void ApplyOutfit()
        {
            string shown = ShownOutfit;
            if (shown == _wornOutfit) return;
            _wornOutfit = shown;
            Wear(OutfitPiece.Decode(shown));
        }

        void OnFaceChanged(FixedString4096Bytes prev, FixedString4096Bytes next) => ApplyFace();

        void ApplyFace()
        {
            if (_facePainter == null) return;
            var strokes = FaceArt.Decode(ShownFace);
            _hasFace = strokes.Count > 0;
            _facePainter.Draw(strokes);
            UpdateFaceVisible();
        }

        void UpdateFaceVisible()
        {
            if (_faceRenderer != null) _faceRenderer.enabled = _hasFace && Visible && !HasLiveRagdoll && !Balled;
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
        void OnSideChanged(TeamState prev, TeamState next) => ApplyColor();

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        MaterialPropertyBlock _mpb;

        /// <summary>Their own colour again, over anything that painted the body (Death's black).</summary>
        public void RefreshColor() => ApplyColor();

        void ApplyColor()
        {
            if (ColorIndex.Value == 255 || model == null) return;
            _mpb ??= new MaterialPropertyBlock();
            Color c = IsSpawned && IsUndead ? SpellPlans.UndeadSkin : PlayerColor; // the Undead: sickly green all over
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name == FaceDecal.ObjectName || CosmeticLook.Is(r)) continue; // the face and cosmetics keep their own colours
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(BaseColorId, c);
                _mpb.SetColor(ColorId, c);
                r.SetPropertyBlock(_mpb);
            }
        }
    }
}
