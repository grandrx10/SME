using System;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Local player only. Reads the five ability keys and turns them into cast requests:
    /// most cards show an indicator while the key is held and cast on release; some (Rock Barrage) cast on press.
    /// Checks cost and the 1 s per-ability cooldown locally (the server checks again), predicts the mana spent,
    /// and plays the caster's arm animation.
    /// </summary>
    public partial class AbilityCaster : MonoBehaviour
    {
        PlayerController _pc;
        SpellIndicator _indicator;
        int _holding = -1;
        readonly float[] _cooldownUntil = new float[Loadout.Slots];
        readonly bool[] _foolRolling = new bool[Loadout.Slots];
        float _predictedSpend, _predictedAt;
        ManaState _manaSeen;
        int _rockId;
        double _rockLaunchAt;
        float _castUntil;

        /// <summary>Still casting the last card (<see cref="Cards.CastTime"/>): no other card until it's over.</summary>
        public bool Casting => Time.time < _castUntil;

        public SpellPose Pose { get; private set; }
        public double PoseStart { get; private set; }
        public int HoldingSlot => _holding;
        /// <summary>Mana already spent on casts the server hasn't confirmed yet.</summary>
        public float PredictedSpend => _predictedSpend;
        /// <summary>A cast was refused (slot) — the HUD flashes it.</summary>
        public static event Action<int> Denied;
        /// <summary>Mana Missiles found nobody near the centre dot.</summary>
        public static event Action NoTarget;

        /// <summary>Play a gesture now (e.g. the server says the siphon punch landed).</summary>
        public void PlayPose(SpellPose p) => SetPose(p);

        void Awake() => _pc = GetComponent<PlayerController>();

        void OnEnable()
        {
            WorldNet.RockGathered += OnRockGathered;
            WorldNet.FireballCharging += OnFireballCharging;
            WorldNet.HellfireStarted += OnHellfireStarted;
            WorldNet.BubbleEnded += OnBubbleEnded;
            WorldNet.FuseCharging += OnFuseCharging;
            WorldNet.DomainEnded += OnDomainEnded;
            WorldNet.ForceEnded += OnForceEnded;
            WorldNet.BreathEnded += OnBreathEnded;
            WorldNet.HookAttached += OnHookAttached;
            WorldNet.HookEnded += OnHookEnded;
        }

        /// <summary>Our Attraction / Repulsion ball is gone (25 m out, put out, ...): free to cast again.</summary>
        void OnForceEnded(ulong caster)
        {
            if (_pc == null || caster != _pc.NetworkObjectId) return;
            if (Pose != SpellPose.AttractCast && Pose != SpellPose.RepelCast) return; // fused: the purple ball's cast goes on
            _castUntil = 0f;
            SetPose(SpellPose.None);
        }

        int _fuseId;
        double _fuseLaunchAt;

        /// <summary>Our purple ball has started forming: throw it the moment it's done.</summary>
        void OnFuseCharging(int id, ulong caster, double start)
        {
            if (_pc == null || caster != _pc.NetworkObjectId) return;
            int over = WorldNet.Instance != null && WorldNet.Instance.Fuses.TryGetValue(id, out var fx) ? fx.over : 0;
            if (over > 0) { _wheelFuses.Add((id, start + SpellPlans.FuseThrowAt(over))); return; } // (Wheel of Fortune's)
            _fuseId = id;
            _fuseLaunchAt = start + SpellPlans.FuseCharge;
        }

        readonly System.Collections.Generic.List<(int id, double at)> _wheelFuses = new();

        void TickFuse()
        {
            // Wheel of Fortune's 7: one after another, whatever else we're up to
            for (int i = _wheelFuses.Count - 1; i >= 0; i--)
            {
                if (WorldNet.Now < _wheelFuses[i].at) continue;
                ThrowFuse(_wheelFuses[i].id);
                _wheelFuses.RemoveAt(i);
            }
            if (_fuseId == 0 || WorldNet.Now < _fuseLaunchAt) return;
            ThrowFuse(_fuseId);
            _fuseId = 0;
        }

        void ThrowFuse(int id)
        {
            var net = WorldNet.Instance;
            Vector3 from = net != null && net.Fuses.TryGetValue(id, out var fx) ? WorldNet.FusePosition(fx, WorldNet.Now) : _pc.transform.position + Vector3.up;
            // at whatever the centre dot is on (any angle, not just level)
            Vector3 d = AimPoint(true, out bool hit) - from;
            Vector3 dir = hit && d.sqrMagnitude > 4f ? d.normalized : CameraRig.AimRay.direction;
            if (_pc.IsAlive) _pc.RequestFuseLaunch(id, dir);
        }

        /// <summary>Our Empress or Emperor is over: not again for a while.</summary>
        void OnDomainEnded(ulong caster, byte kind, double at)
        {
            if (_pc == null || caster != _pc.NetworkObjectId) return;
            CardId card = kind == 0 ? CardId.TheEmpress : CardId.TheEmperor;
            for (int s = 0; s < Loadout.Slots; s++)
                if (CardIn(s) == card) _cooldownUntil[s] = Mathf.Max(_cooldownUntil[s], Time.time + SpellPlans.DomainRest - (float)(WorldNet.Now - at));
        }

        /// <summary>Our Empress (or Emperor) is up, or its hands are still clasped.</summary>
        WorldNet.DomainFx OwnDomain(CardId card) =>
            WorldNet.Instance != null && _pc != null ? WorldNet.Instance.OpenDomain(_pc.NetworkObjectId, (byte)(card == CardId.TheEmpress ? 0 : 1)) : null;

        void OnDisable()
        {
            WorldNet.FuseCharging -= OnFuseCharging;
            WorldNet.DomainEnded -= OnDomainEnded;
            WorldNet.ForceEnded -= OnForceEnded;
            WorldNet.BreathEnded -= OnBreathEnded;
            WorldNet.HookAttached -= OnHookAttached;
            WorldNet.HookEnded -= OnHookEnded;
            WorldNet.RockGathered -= OnRockGathered;
            WorldNet.FireballCharging -= OnFireballCharging;
            WorldNet.HellfireStarted -= OnHellfireStarted;
            WorldNet.BubbleEnded -= OnBubbleEnded;
            _indicator?.Hide();
        }

        /// <summary>The server says our bubble popped (or ran out): drop out of it.</summary>
        void OnBubbleEnded(ulong caster)
        {
            if (_pc == null || caster != _pc.NetworkObjectId) return;
            _pc.Motor.EndBubble();
            if (Pose == SpellPose.BubbleCast) SetPose(SpellPose.None);
        }

        void OnDestroy() => _indicator?.Destroy();

        public CardId CardIn(int slot) => _pc != null ? _pc.CardInSlot(slot) : Loadouts.Selected.Get(slot);
        /// <summary>Free to cast: a card rolled by The Fool (used once), or Warp Space's second portal.</summary>
        public bool IsFree(int slot) => _pc != null && (_pc.IsFoolCard(slot) || CardIn(slot) == CardId.WarpSpace && PortalWaiting());
        public float CooldownLeft(int slot) => CardIn(slot) == CardId.WarpSpace && PortalWaiting() ? 0f : Mathf.Max(0f, _cooldownUntil[slot] - Time.time, SwiftRestLeft(slot));
        /// <summary>Arcane Swiftness: not again until a second after the last one ended.</summary>
        /// <summary>(and Ephemerate, Submerge: not again until a second after the last one ended)</summary>
        float SwiftRestLeft(int slot)
        {
            if (_pc == null || _pc.Motor == null) return 0f;
            var card = CardIn(slot);
            if (card == CardId.ArcaneSwiftness && _pc.Motor.SwiftEndsAt > 0f) return _pc.Motor.SwiftEndsAt + SpellPlans.SwiftRest - Time.time;
            if (card == CardId.Ephemerate && _pc.Motor.EphemEndsAt > 0f) return _pc.Motor.EphemEndsAt + SpellPlans.EphemRest - Time.time;
            if (card == CardId.Submerge && _pc.IsSpawned)
            {
                var st = _pc.Status.Value;
                if (st.SubUntil > 0 && st.SubUntil <= WorldNet.Now) return (float)(st.SubUntil + SpellPlans.SubRest - WorldNet.Now);
            }
            return 0f;
        }

        /// <summary>Our first Warp Space portal is open and waiting for its partner (which is free, with no cooldown).</summary>
        bool PortalWaiting() => OwnPortals(false);

        /// <summary>A linked pair of ours is still open: no Warp Space until it closes.</summary>
        bool PortalsLinked() => OwnPortals(true);

        bool OwnPortals(bool linked)
        {
            var net = WorldNet.Instance;
            if (net == null || _pc == null) return false;
            double now = WorldNet.Now;
            foreach (var p in net.Portals.Values)
                if (p.owner == _pc.NetworkObjectId && now < p.End && (p.pair != 0) == linked) return true;
            return false;
        }
        /// <summary>A card whose effect is still running and can't be cast again yet (The Magician): 1 → 0 as it runs out.</summary>
        public float RunningLeft(int slot)
        {
            if (_pc == null || !_pc.IsSpawned) return 0f;
            if (CardIn(slot) == CardId.TheLovers) return OwnTether() != null ? 1f : 0f; // the beam's out (press again to let go)

            if (CardIn(slot) == CardId.GrappleHook) return OwnHook() != null ? 1f : 0f;       // the hook's out (press again to let go)

            if (CardIn(slot) == CardId.TheMoon) return Left(_pc.Status.Value.MoonAt, SpellPlans.MoonClasp + SpellPlans.MoonTime);
            if (CardIn(slot) == CardId.TheEmpress || CardIn(slot) == CardId.TheEmperor)
            {
                var d = OwnDomain(CardIn(slot));
                float lasts = CardIn(slot) == CardId.TheEmpress ? SpellPlans.EmpressTime : SpellPlans.EmperorTime;
                return d != null ? Left(d.start + lasts, SpellPlans.DomainDelay + lasts) : 0f;
            }
            if (CardIn(slot) == CardId.TheSun) return Left(_pc.Status.Value.SunUntil, SpellPlans.SunDelay + SpellPlans.SunTime);
            if (CardIn(slot) == CardId.Strength) return Mathf.Clamp01((float)(_pc.Status.Value.StrongUntil - WorldNet.Now) / SpellPlans.StrengthTime);
            if (CardIn(slot) != CardId.TheMagician) return 0f;
            return Mathf.Clamp01((float)(_pc.Mana.Value.FastUntil - WorldNet.Now) / SpellPlans.MagicianTime);
        }
        static float Left(double until, float of) => Mathf.Clamp01((float)(until - WorldNet.Now) / of);

        public bool Affordable(int slot) => _pc != null && (IsFree(slot) || _pc.CanAfford(Cards.Get(CardIn(slot)), _predictedSpend));

        /// <summary>What a slot's card costs right now (0 for a free one).</summary>
        public int CostIn(int slot) => IsFree(slot) || _pc == null ? 0 : _pc.CostOf(Cards.Get(CardIn(slot)));

        public void ResetForRound()
        {
            CancelAll();
            _wheelFuses.Clear();
            _starId = 0;
            for (int i = 0; i < _cooldownUntil.Length; i++) { _cooldownUntil[i] = 0f; _foolRolling[i] = false; }
            _predictedSpend = 0f;
        }

        public void CancelAll()
        {
            _castUntil = 0f;
            _holding = -1;
            _shedSlot = -1;
            _rockId = 0;
            _fireId = 0;
            _hellId = 0;
            _escapeAt = -1;
            _fuseId = 0;
            _biteAt = -1f;
            _shotAt = _graspAt = _tossAt = -1f;
            _heavenAt = -1f;
            _justiceDoneAt = -1f; // (knocked out of the kneel: not investigated after all)
            if (Pose == SpellPose.WolfgangFire && _pc != null) _pc.Motor.FaceLock(0f); // (the pistols dropped: free to turn again)
            LoseRifle();
            _flaming = false;
            if (Pose == SpellPose.GraspKneel && _pc != null) _pc.Motor.Unroot();
            if (Pose == SpellPose.SkinKneel && _pc != null) _pc.Motor.Unroot();
            if (Pose == SpellPose.IceCrouch && _pc != null) { _pc.Motor.Unroot(); _pc.Motor.FaceLock(0f); } // knocked out of the crouch
            SetPose(SpellPose.None);
            _indicator?.Hide();
        }

        void Update()
        {
            if (_pc == null || !_pc.IsSpawned) return;
            // the server's mana changed: our guess has been replaced by the real value
            if (!_pc.Mana.Value.Equals(_manaSeen)) { _manaSeen = _pc.Mana.Value; _predictedSpend = 0f; }
            if (_predictedSpend > 0f && Time.time - _predictedAt > 1.5f) _predictedSpend = 0f;
            for (int s = 0; s < Loadout.Slots; s++)
                if (_foolRolling[s] && _pc.IsFoolCard(s)) { _foolRolling[s] = false; _cooldownUntil[s] = 0f; }

            bool able = _pc.IsAlive && !_pc.Motor.Ragdolled && !_pc.Motor.GettingUp && !_pc.Motor.Busy && !_pc.Trapped && !_pc.Grasped && !_pc.Hexed && MatchManager.CastingAllowedFor(_pc); // the Empress silences
            if (!able && _holding >= 0) { if (_rifleFireAt < 0f) LoseRifle(); _holding = -1; SetPose(SpellPose.None); }
            // knocked down mid-cast: the spell stops (the server cancels its side too)
            bool knocked = !_pc.IsAlive || _pc.Motor.Ragdolled || _pc.Motor.Carried || _pc.Hexed; // (or a hex took hold: the same)
            if (knocked && (Casting || _rockId != 0 || _fireId != 0 || _hellId != 0 || _escapeAt >= 0f || _shotAt >= 0f || _graspAt >= 0f || _tossAt >= 0f || _rifleSlot >= 0 || _heavenAt >= 0f)) CancelAll();

            // the corpse got up (or went) before its skin was taken: nothing left to kneel over
            if (Pose == SpellPose.SkinKneel && Casting && WorldNet.Fizzled(_pc.NetworkObjectId, PoseStart + 0.1, WorldNet.Now)) CancelAll();

            // knocked down (or spun by a tornado): only Bubble Away and The Hanged Man (each stands you up), and The Hermit, can be cast
            bool down = _pc.Motor.Ragdolled || _pc.Motor.Carried || _pc.Motor.GettingUp;
            bool ableDown = !able && down && _pc.IsAlive && !_pc.Motor.Pinned && !_pc.Trapped && !_pc.Grasped && !_pc.Hexed && MatchManager.CastingAllowedFor(_pc);

            if (able && !InputMap.Blocked)
            {
                for (int slot = 0; slot < Loadout.Slots; slot++)
                {
                    var act = GameSettings.AbilityAction(slot);
                    if (InputMap.Down(act)) Press(slot);
                    if (_holding == slot && !GameSettings.Binds[act].IsPressed) Release(slot);
                }
            }
            else if (ableDown && !InputMap.Blocked)
            {
                for (int slot = 0; slot < Loadout.Slots; slot++)
                {
                    if (!InputMap.Down(GameSettings.AbilityAction(slot))) continue;
                    if (CardIn(slot) == CardId.BubbleAway || CardIn(slot) == CardId.TheHangedMan) Press(slot);
                    else if (CardIn(slot) == CardId.TheHermit) TryCast(slot, Cards.Get(CardId.TheHermit)); // (no preview from the floor)
                }
            }
            else if (InputMap.Blocked && _holding >= 0 && _holding == _rifleSlot) Release(_holding); // a menu opened: let go of
            else if (InputMap.Blocked && _holding >= 0)
            {
                _holding = -1;
                SetPose(SpellPose.None);
            }

            TickShield(able);

            if (_holding >= 0) ShowIndicator(_holding);
            else if (_graspAt >= 0f) ShowGrasp(); // still turning it
            else if (ShowHeaven()) { } // Heavenly Slash: the cut's angle, while posing
            else _indicator?.Hide();
            // charging Flowing Cut, Sebastian: down on one knee, not walking
            _pc.Motor.Kneeling = _holding >= 0 && (Charges(CardIn(_holding)) || CardIn(_holding) == CardId.HammerSpin); // (Hammer Spin: planted, winding up)
            // charging Compressed Shot: half speed, falling gently
            _pc.Motor.Concentrating = _holding >= 0 && CardIn(_holding) == CardId.CompressedShot;
            // Submerge: under while its key is held; let go, out you come
            if (_subSlot >= 0)
            {
                bool sunk = _pc.Status.Value.SubUntil > WorldNet.Now || WorldNet.Now - PoseStart < 0.4 && Pose == SpellPose.SubDive;
                if (!sunk) _subSlot = -1;
                else if (!_subLetGo && (InputMap.Blocked || !GameSettings.Binds[GameSettings.AbilityAction(_subSlot)].IsPressed))
                {
                    _subLetGo = true;
                    _pc.RequestCardCancel(CardId.Submerge);
                }
            }

            // timed rock throw
            if (_rockId != 0 && WorldNet.Now >= _rockLaunchAt)
            {
                if (_pc.IsAlive) _pc.RequestRockLaunch(_rockId, RockThrowDirection(_rockId));
                _rockId = 0;
            }

            TickShed(able);
            TickFlame();
            TickFuse();
            TickBite();
            TickFlameThrower();
            TickTowerAim();
            TickStars();
            TickGuns();
            TickHeaven();
            // Justice: knelt the whole time, so that body's been investigated (each only once)
            if (_justiceDoneAt >= 0f && Time.time >= _justiceDoneAt) { _justiceDoneAt = -1f; if (_justiceBody != null && _pc.IsAlive) _investigated.Add(_justiceBody); }

            // gestures end on their own
            float t = (float)(WorldNet.Now - PoseStart);
            float ends = Pose switch
            {
                SpellPose.CastWall => 0.75f, SpellPose.CastFold => 0.95f, SpellPose.Gather => 1.15f,
                SpellPose.Dash => 0.5f, SpellPose.ShoveCast => 0.6f, SpellPose.TornadoCast => 0.9f, SpellPose.MissileCharge => 1.2f,
                SpellPose.Barrier => 1.25f, SpellPose.Siphon => SpellPlans.SiphonTime + 0.1f, SpellPose.SiphonPunch => 0.5f,
                SpellPose.DeathDash => SpellPlans.DeathDashTime + 0.1f,
                SpellPose.FlameCharge => 0.95f, SpellPose.HellfireDraw => SpellPlans.HellfireDraw + 0.2f, SpellPose.FlameHands => 0.7f,
                SpellPose.JudgmentCast => 1.6f, SpellPose.TugCast => 0.7f, SpellPose.Platform => 0.7f,
                SpellPose.FoolRoll => 0.5f, SpellPose.Magician => 0.7f, SpellPose.SunRaise => SpellPlans.PriestessDelay + 0.35f, SpellPose.Prayer => SpellPlans.DomainDelay + 0.35f,
                SpellPose.WaterCast => 0.6f, SpellPose.BubbleCast => SpellPlans.BubbleWindup + SpellPlans.BubbleTime + 0.25f, SpellPose.TsunamiCast => 0.9f,
                SpellPose.WarpCast => SpellPlans.WarpDone + 0.2f, SpellPose.PortalCast => 0.5f,
                SpellPose.SwiftCast => SpellPlans.SwiftPrep + SpellPlans.SwiftTime + 0.2f,
                SpellPose.SeveranceDash => SpellPlans.SeveranceTime + 0.35f, SpellPose.SeveranceSlash => BladeMotion.Sweep + BladeMotion.Settle + 0.25f,
                SpellPose.FlowStrike => BladeMotion.Sweep + BladeMotion.Settle + 0.25f,
                SpellPose.AttractCast or SpellPose.RepelCast => SpellPlans.OrbRaise + SpellPlans.ForceLife + 0.3f,
                SpellPose.FuseCast => SpellPlans.FuseCharge + 0.75f,
                SpellPose.HierophantCast => SpellPlans.HierophantDelay + 0.25f,
                SpellPose.HammerDrag => SpellPlans.RisingTime + 0.35f,
                SpellPose.HammerRise => HammerMotion.RiseSwing + HammerMotion.RiseSettle + 0.25f,
                SpellPose.HammerSlam => HammerMotion.SlamSwing + HammerMotion.SlamSettle + 0.3f,
                SpellPose.SwarmRaise => 0.65f,
                SpellPose.IceCrouch => SpellPlans.IceCrouch + 0.35f,
                SpellPose.WheelSpin => 0.5f,
                SpellPose.Clasp => SpellPlans.MoonClasp + 0.3f,
                SpellPose.HeadBack => SpellPlans.DeathDelay + 0.3f,
                SpellPose.RightHandUp => SpellPlans.DevilRaise + 0.35f,
                SpellPose.TowerPray => SpellPlans.TowerPray + 0.3f,
                SpellPose.NecroCrouch => SpellPlans.ReviveTime + 0.3f,
                SpellPose.BiteCrouch => SpellPlans.BiteCrouch + SpellPlans.BiteMaxTime, // held through the lunge (others see it fly head first)
                SpellPose.BreathLean => SpellPlans.BreathLean + SpellPlans.IceBreathTime + 0.2f,
                SpellPose.Flex => SpellPlans.StrengthFlex + 0.3f,
                SpellPose.ShotgunAim => SpellPlans.RachDelay + 0.35f,
                SpellPose.RifleFire => SpellPlans.SebWindup + SpellPlans.SebRecover + 0.15f,
                SpellPose.GraspKneel => SpellPlans.GraspCast + 0.35f,
                SpellPose.SkinKneel => SpellPlans.SkinTime + 0.3f,
                SpellPose.BombPlace or SpellPose.BombToss or SpellPose.LeftArmUp or SpellPose.PotThrow => 0.6f,
                SpellPose.NecroRaise => SpellPlans.NecroCharge + 0.42f,
                SpellPose.HexPoint => SpellPlans.HexCast + 0.4f,
                SpellPose.GrapplePoint => OwnHook() != null ? float.MaxValue : 0.6f, // held while the hook is out
                SpellPose.PotHold => OwnPot() != null || t < 0.4f ? float.MaxValue : 0f, // held while the pot is
                SpellPose.WindShot => 0.35f, SpellPose.CloudStep => 0.4f,
                SpellPose.SubDive => SpellPlans.SubDive + 0.15f, SpellPose.SubEmerge => SpellPlans.SubSpin + 0.2f,
                SpellPose.HammerSpin => _spinTime + 0.3f, // (a moment stopped, the hammer still out, before it goes)
                SpellPose.WolfgangFire => SpellPlans.WolfDraw + SpellPlans.WolfFireTime + 0.15f,
                SpellPose.HeavenSlash => BladeMotion.Sweep + BladeMotion.Settle + 0.25f,
                SpellPose.ParryGuard => SpellPlans.ParryWindow,
                SpellPose.ParryStab => 0.5f,
                _ => float.MaxValue,
            };
            if (t > ends) SetPose(SpellPose.None);
            // the lunge is over once you're back on your feet
            if (Pose == SpellPose.BiteCrouch && _biteAt < 0f && t > SpellPlans.BiteCrouch + 0.2f && !_pc.Motor.Ragdolled) SetPose(SpellPose.None);
            // Descending Hammer: thrown out of the crouch or the leap, or it never came down: the hammer goes
            if (Pose == SpellPose.HammerLeap && !_pc.Motor.Leaping && t > SpellPlans.HammerCrouch + 0.05f) SetPose(SpellPose.None);
        }

        /// <summary>Our Lovers beam (thrown or tethered), if it's still going.</summary>
        WorldNet.LoversFx OwnTether() => WorldNet.Instance != null && _pc != null ? WorldNet.Instance.ActiveTether(_pc.NetworkObjectId) : null;

        /// <summary>The server says our charge is over: free to cast again.</summary>
        public void ChariotEnded()
        {
            _castUntil = 0f;
            if (Pose == SpellPose.ChariotRun) SetPose(SpellPose.None);
        }

        /// <summary>Our Lovers beam ended (missed, blocked, snapped, let go): the hand drops and its cooldown starts now.</summary>
        public void LoversEnded()
        {
            if (Pose == SpellPose.LoversBeam) SetPose(SpellPose.None);
            for (int s = 0; s < Loadout.Slots; s++)
                if (CardIn(s) == CardId.TheLovers) _cooldownUntil[s] = Time.time + Cards.Cooldown;
        }

        /// <summary>The server cancelled our domain while the hands were still clasped (we got hurt or thrown).</summary>
        public void DomainStartupCancelled()
        {
            if (Pose != SpellPose.Prayer) return;
            SetPose(SpellPose.None);
            _pc.Motor.Unroot();
        }

        void SetPose(SpellPose p)
        {
            Pose = p;
            PoseStart = WorldNet.Now;
        }

        // ------------------------------------------------------------------ input

        // Attraction / Repulsion: the last one cast, and when (the other within the window fuses with it)
        CardId _lastForce;
        float _lastForceAt = -10f;

        /// <summary>Casting this now would fuse it with the Attraction / Repulsion just cast (allowed mid-cast).</summary>
        bool Fuses(CardId card) => Cards.Fuses(_lastForce, card) && Time.time - _lastForceAt <= SpellPlans.FuseWindow;

        void Press(int slot)
        {
            var info = Cards.Get(CardIn(slot));
            if (info == null) return;
            // The Lovers already out: pressing it again lets go (the cooldown starts when it ends)
            if (info.id == CardId.TheLovers && OwnTether() != null) { _pc.RequestLoversCancel(); return; }
            // the grapple hook out: pressing it again lets go (free)
            if (info.id == CardId.GrappleHook && OwnHook() != null) { _pc.RequestCardCancel(CardId.GrappleHook); return; }
            // under Submerge (or diving in): no other card meanwhile (letting go of its key brings you out)
            if (_pc.Status.Value.SubUntil > WorldNet.Now)
            {
                if (info.id != CardId.Submerge) Denied?.Invoke(slot);
                return;
            }
            // holding a pot: no other card; the Fly Trap pressed again is thrown
            var pot = OwnPot();
            if (pot != null)
            {
                if (info.id == CardId.FlyTrap && pot.kind == SpellPlans.PotFlyTrap && WorldNet.Now - pot.start > 0.15)
                {
                    _pc.RequestCardCancel(CardId.FlyTrap);
                    SetPose(SpellPose.PotThrow);
                }
                else Denied?.Invoke(slot);
                return;
            }
            if (Cards.IsPassive(info.id)) { Denied?.Invoke(slot); return; } // Temperance: it works just by being there
            // charging Flowing Cut, Sebastian: no other cards until it's released
            if (_holding >= 0 && _holding != slot && (Charges(CardIn(_holding)) || CardIn(_holding) == CardId.ExplosiveEscape || CardIn(_holding) == CardId.HammerSpin)) { Denied?.Invoke(slot); return; }
            if ((Casting && !Fuses(info.id)) || CooldownLeft(slot) > 0f) { Denied?.Invoke(slot); return; }
            if (info.id == CardId.Skinwalker && _pc.Skin.Value.On && FindCorpse() == null)
            {
                // wearing a skin, and no corpse to take another from: held, it comes off
                _shedSlot = slot;
                _shedAt = Time.time + SpellPlans.SkinShed;
                RuneSpew.Create(() => _pc.transform.position + Vector3.down * 0.1f, () => this != null && _shedSlot >= 0, SpellPlans.SkinShed, SpellPlans.SkinGrey, 18f, new Vector3(0.35f, 0.5f, 0.35f));
                return;
            }
            if (info.castOnPress)
            {
                TryCast(slot, info);
                return;
            }
            // charged cards that are paid for when let go: not even started without the mana for them
            if (info.id == CardId.CompressedShot && !Affordable(slot)) { Denied?.Invoke(slot); return; }
            if (info.id == CardId.Sebastian)
            {
                // paid for now, as the charge begins (the server gives it back if it's let go too early)
                if (!Affordable(slot)) { Denied?.Invoke(slot); return; }
                _pc.RequestCast(slot, info.id, Vector3.zero, _pc.Motor.Yaw);
                if (!IsFree(slot)) { _predictedSpend += _pc.CostOf(info); _predictedAt = Time.time; }
                _cooldownUntil[slot] = Time.time + Cards.Cooldown;
                _rifleSlot = slot;
            }
            _holding = slot;
            SetPose(info.id switch
            {
                CardId.FoldingHands => SpellPose.HoldFold,
                CardId.Shove => SpellPose.ShoveHold,
                CardId.Tornado => SpellPose.TornadoHold,
                CardId.ArcaneJudgment => SpellPose.JudgmentHold,
                CardId.Judgement => SpellPose.JudgmentHold,
                CardId.Tug => SpellPose.TugHold,
                CardId.WaterOfLife => SpellPose.WaterHold,
                CardId.Tsunami => SpellPose.TsunamiHold,
                CardId.WarpSelf => SpellPose.WarpHold,
                CardId.WarpSpace => SpellPose.PortalHold,
                CardId.FlowingCut => SpellPose.FlowCharge,
                CardId.Sebastian => SpellPose.RifleKneel,
                CardId.TheHierophant => SpellPose.HierophantHold,
                CardId.RemoteFlameBomb => SpellPose.PortalHold,
                CardId.CompressedShot => SpellPose.WindCharge,
                CardId.BubbleBomb => SpellPose.WaterHold,
                CardId.TheHermit => SpellPose.None,
                CardId.HammerSpin => SpellPose.HammerSpinCharge,
                _ => SpellPose.HoldWall,
            });
            _heldSince = Time.time;
        }

        float _heldSince, _heldFor;

        /// <summary>Charged by holding its key, down on one knee.</summary>
        static bool Charges(CardId card) => card == CardId.FlowingCut || card == CardId.Sebastian;

        /// <summary>Flowing Cut charges built up so far (0 until it has been held 0.3 s).</summary>
        int FlowCharges => _holding >= 0 && CardIn(_holding) == CardId.FlowingCut ? SpellPlans.FlowCharges(Time.time - _heldSince) : 0;

        void Release(int slot)
        {
            _releasedCharges = FlowCharges;
            _heldFor = Time.time - _heldSince;
            _holding = -1;
            if (_rifleSlot == slot) { ReleaseRifle(slot); return; }
            var info = Cards.Get(CardIn(slot));
            if (info == null || !TryCast(slot, info)) SetPose(SpellPose.None);
        }

        int _releasedCharges;
        readonly System.Collections.Generic.List<Vector3Int> _swarmSpots = new();

        bool TryCast(int slot, CardInfo info)
        {
            bool fuse = Fuses(info.id);
            if ((Casting && !fuse) || CooldownLeft(slot) > 0f || !Affordable(slot)) { Denied?.Invoke(slot); return false; }
            bool free = IsFree(slot);
            var world = VoxelWorld.Current;
            Vector3 target = Vector3.zero;
            ulong targetId = 0;
            float yaw = _pc.Motor.Yaw;
            switch (info.id)
            {
                case CardId.SummonWall:
                    if (!GroundTarget(out target) || !SpellPlans.PlanWall(world, target, yaw).valid) { Denied?.Invoke(slot); return false; }
                    SetPose(SpellPose.CastWall);
                    break;
                case CardId.FoldingHands:
                    if (!GroundTarget(out target) || !SpellPlans.PlanFold(world, target, yaw).valid) { Denied?.Invoke(slot); return false; }
                    SetPose(SpellPose.CastFold);
                    break;
                case CardId.ShatterHex:
                case CardId.StasisHex:
                case CardId.MindHex:
                case CardId.SmallifyHex:
                {
                    var t = FindHexTarget(SpellPlans.HexKind(info.id));
                    if (t == null) { NoTarget?.Invoke(); return false; } // no cost, no cooldown
                    targetId = t.NetworkObjectId;
                    SetPose(SpellPose.HexPoint);
                    break;
                }
                case CardId.RockBarrage:
                case CardId.NecromaticBlast:
                {
                    // aim point nudged just inside whatever the crosshair is on
                    Vector3 p = AimPoint(false, out bool hit);
                    target = hit ? p + CameraRig.AimRay.direction * 0.15f : p;
                    if (!SpellPlans.PlanRock(world, _pc.transform.position, _pc.FeetPosition, Quaternion.Euler(0, yaw, 0) * Vector3.forward, target, out _))
                    { Denied?.Invoke(slot); return false; }
                    SetPose(info.id == CardId.NecromaticBlast ? SpellPose.NecroRaise : SpellPose.Gather); // (the throw is timed when the server's ball appears)
                    break;
                }
                case CardId.Shove:
                    target = CameraRig.AimRay.direction; // a direction
                    // the gust kicks you back the other way (stays on your feet): a way to reposition or launch yourself
                    Vector3 recoil = _pc.Motor.Velocity * 0.3f - target.normalized * SpellPlans.GustRecoil;
                    recoil.y = Mathf.Max(recoil.y, 3.5f); // always a little hop, or ground friction eats a level push
                    _pc.Motor.Knock(recoil, 0f);
                    SetPose(SpellPose.ShoveCast);
                    break;
                case CardId.Tornado:
                    if (!GroundTarget(out target, SpellPlans.TornadoRange)) { Denied?.Invoke(slot); return false; }
                    SetPose(SpellPose.TornadoCast);
                    break;
                case CardId.NomadicDash:
                {
                    target = DashDirection(); // a direction
                    _pc.Motor.Dash(target, SpellPlans.DashSpeed, SpellPlans.DashTime);
                    SetPose(SpellPose.Dash);
                    break;
                }
                case CardId.ManaMissiles:
                {
                    var t = FindMissileTarget();
                    if (t == null) { NoTarget?.Invoke(); return false; } // no cost, no cooldown
                    targetId = t.NetworkObjectId;
                    SetPose(SpellPose.MissileCharge);
                    break;
                }
                case CardId.MagicalBarrier:
                    _pc.Motor.Root(SpellPlans.BarrierWindup + SpellPlans.BarrierTime);
                    SetPose(SpellPose.Barrier);
                    break;
                case CardId.ManaSiphon:
                    _pc.Motor.Siphon(SpellPlans.SiphonTime);
                    SetPose(SpellPose.Siphon);
                    break;
                case CardId.BallOfFlame:
                    SetPose(SpellPose.FlameCharge); // the throw is timed when the server's fireball appears
                    break;
                case CardId.HellfireWall:
                    SetPose(SpellPose.HellfireDraw); // the line is drawn once the server's hellfire appears
                    break;
                case CardId.ExplosiveEscape:
                    // held: the blast grows (the server is told how long, and checks it)
                    _escapeHeld = Mathf.Clamp(_heldFor, 0f, SpellPlans.EscapeChargeMax);
                    targetId = (ulong)Mathf.RoundToInt(_escapeHeld * 1000f);
                    target = SpellPlans.EscapeSpot(_pc.transform.position, yaw); // goes off exactly where the preview showed
                    _escapeAt = Time.time + SpellPlans.EscapeDelay;
                    SetPose(SpellPose.FlameHands);
                    break;
                case CardId.ArcaneJudgment:
                case CardId.Judgement:
                    if (!GroundTarget(out target)) { Denied?.Invoke(slot); return false; }
                    SetPose(SpellPose.JudgmentCast);
                    break;
                case CardId.TheStar:
                    SetPose(SpellPose.RightHandUp);
                    break;
                case CardId.TheMoon:
                    if (_pc.Status.Value.MoonAt > WorldNet.Now) { Denied?.Invoke(slot); return false; } // one's still filling
                    SetPose(SpellPose.Clasp);
                    break;
                case CardId.TheSun:
                    if (_pc.Status.Value.SunUntil > WorldNet.Now) { Denied?.Invoke(slot); return false; } // still going
                    SetPose(SpellPose.SunRaise);
                    break;
                case CardId.TheWorld:
                {
                    if (!WorldNet.RoomForWorld(_pc.transform.position)) { Denied?.Invoke(slot); return false; } // not in (or up against) another
                    var t = FindWorldTarget();
                    if (t == null) { NoTarget?.Invoke(); return false; } // no cost, no cooldown
                    targetId = t.NetworkObjectId;
                    SetPose(SpellPose.Prayer);
                    break;
                }
                case CardId.Tug:
                    target = TugOrigin(); // where the wind starts (it blows back to you)
                    SetPose(SpellPose.TugCast);
                    break;
                case CardId.TheFool:
                    SetPose(SpellPose.FoolRoll);
                    break;
                case CardId.TheMagician:
                    if (_pc.Mana.Value.Fast(WorldNet.Now)) { Denied?.Invoke(slot); return false; } // already running
                    SetPose(SpellPose.Magician);
                    break;
                case CardId.TheHighPriestess:
                    SetPose(SpellPose.SunRaise);
                    break;
                case CardId.TheEmpress:
                case CardId.TheEmperor:
                    if (OwnDomain(info.id) != null) { Denied?.Invoke(slot); return false; } // still up
                    _pc.Motor.Root(SpellPlans.DomainDelay); // locked in place while the hands are clasped
                    SetPose(SpellPose.Prayer);
                    break;
                case CardId.SummonPlatform:
                    if (!SpellPlans.PlanPlatform(world, _pc.FeetPosition).valid) { Denied?.Invoke(slot); return false; }
                    _pc.Motor.Knock(Vector3.up * SpellPlans.PlatformLift, 0f); // ride it up (no ragdoll)
                    SetPose(SpellPose.Platform);
                    break;
                case CardId.WaterOfLife:
                    target = SpellPlans.OrbSpot(_pc.transform.position, yaw); // where the preview showed it
                    SetPose(SpellPose.WaterCast);
                    break;
                case CardId.BubbleAway:
                    if (_pc.Motor.Ragdolled || _pc.Motor.Carried || _pc.Motor.GettingUp)
                    {
                        // from the ground: the get-up and the bubble together (it forms at once)
                        _pc.Motor.StandUpNow();
                        _pc.Motor.Bubble(0f, SpellPlans.BubbleTime);
                        targetId = SpellPlans.BubbleFromRagdoll;
                        SetPose(SpellPose.BubbleCast);
                        PoseStart = WorldNet.Now - SpellPlans.BubbleWindup;
                    }
                    else
                    {
                        _pc.Motor.Bubble(SpellPlans.BubbleWindup, SpellPlans.BubbleTime);
                        SetPose(SpellPose.BubbleCast);
                    }
                    break;
                case CardId.Tsunami:
                    _pc.Motor.Root(SpellPlans.TsunamiWindup); // stand and shove it out
                    SetPose(SpellPose.TsunamiCast);
                    break;
                case CardId.WarpSelf:
                {
                    Vector3 p = AimPoint(false, out bool hit);
                    if (!SpellPlans.WarpTarget(_pc.transform.position, p, hit, CameraRig.AimRay.direction, out target)) { Denied?.Invoke(slot); return false; }
                    SetPose(SpellPose.WarpCast); // you can still move while it charges
                    break;
                }
                case CardId.WarpSpace:
                {
                    if (PortalsLinked()) { Denied?.Invoke(slot); return false; }
                    Vector3 p = AimPoint(false, out bool hit);
                    if (!SpellPlans.PlanPortal(_pc.transform.position, p, hit, CameraRig.AimRay.direction, out target, out yaw, out float pitch)) { Denied?.Invoke(slot); return false; }
                    targetId = SpellPlans.PackAngle(pitch); // the tilt rides along in the spare id
                    SetPose(SpellPose.PortalCast);
                    break;
                }
                case CardId.ArcaneSwiftness:
                    _pc.Motor.Swift(SpellPlans.SwiftPrep, SpellPlans.SwiftTime);
                    SetPose(SpellPose.SwiftCast);
                    break;
                case CardId.Attraction:
                case CardId.Repulsion:
                    if (fuse)
                    {
                        // the two together: the purple ball forms in front of you (held still, untouchable)
                        _pc.Motor.Root(SpellPlans.FuseCharge);
                        SetPose(SpellPose.FuseCast);
                        _lastForceAt = -10f;
                    }
                    else
                    {
                        SetPose(info.id == CardId.Attraction ? SpellPose.AttractCast : SpellPose.RepelCast);
                        _lastForce = info.id;
                        _lastForceAt = Time.time;
                    }
                    break;
                case CardId.Severance:
                    _pc.Motor.Siphon(SpellPlans.SeveranceTime); // the same rush as Mana Siphon
                    SetPose(SpellPose.SeveranceDash);
                    break;
                case CardId.FlowingCut:
                {
                    // not held long enough for a single charge: nothing happens (no cost, no cooldown)
                    if (_releasedCharges < 1) { Denied?.Invoke(slot); return false; }
                    target = SpellPlans.FlowDestination(world, _pc.FeetPosition, yaw); // where the feet land
                    targetId = (ulong)_releasedCharges;
                    if ((target - _pc.FeetPosition).sqrMagnitude < 0.25f) { Denied?.Invoke(slot); return false; } // a wall right in front
                    _pc.Motor.Kneeling = false;
                    _pc.Motor.Blink(target + (_pc.transform.position - _pc.FeetPosition), SpellPlans.FlowTime);
                    SetPose(SpellPose.FlowStrike);
                    break;
                }
                case CardId.TheHierophant:
                {
                    // where the preview showed it, tilted to face back along your aim
                    Vector3 p = AimPoint(false, out bool hit);
                    SpellPlans.PlanHierophant(_pc.transform.position, p, hit, CameraRig.AimRay.direction, out target, out yaw, out float pitch);
                    targetId = SpellPlans.PackAngle(pitch); // the tilt rides along in the spare id
                    SetPose(SpellPose.HierophantCast);
                    break;
                }
                case CardId.TheLovers:
                {
                    var t = FindTarget(SpellPlans.LoversTargetRange, SpellPlans.LoversTargetAngle);
                    if (t == null) { NoTarget?.Invoke(); return false; } // no cost, no cooldown
                    targetId = t.NetworkObjectId;
                    target = CameraRig.AimRay.direction; // the way it's thrown (then it homes in)
                    SetPose(SpellPose.LoversBeam);
                    break;
                }
                case CardId.TheChariot:
                {
                    Vector3 fwd = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                    _pc.Motor.Chariot(fwd);
                    SetPose(SpellPose.ChariotRun);
                    break;
                }
                case CardId.RisingHammer:
                    _pc.Motor.Siphon(SpellPlans.RisingTime); // the same rush as Mana Siphon / Severance
                    SetPose(SpellPose.HammerDrag);
                    break;
                case CardId.DescendingHammer:
                    _pc.Motor.HammerLeap(SpellPlans.HammerCrouch);
                    SetPose(SpellPose.HammerLeap);
                    break;
                case CardId.HammerSpin:
                {
                    // not wound up long enough: nothing happens (no cost, no cooldown)
                    if (_heldFor < SpellPlans.SpinMinHold) { Denied?.Invoke(slot); return false; }
                    target = SpellPlans.SpinDestination(world, _pc.FeetPosition, yaw, SpellPlans.SpinRange(_heldFor)); // where the feet end up
                    Vector3 way = target - _pc.FeetPosition;
                    way.y = 0f;
                    if (way.magnitude < 0.5f) { Denied?.Invoke(slot); return false; } // something unbreakable right in front
                    targetId = (ulong)Mathf.RoundToInt(Mathf.Min(_heldFor, 10f) * 1000f);
                    _pc.Motor.Kneeling = false;
                    _spinTime = SpellPlans.SpinTime(way.magnitude);
                    _pc.Motor.Blink(target + (_pc.transform.position - _pc.FeetPosition), _spinTime, false, SpellPlans.SpinEase);
                    if (WorldNet.Instance != null) WorldNet.Instance.Spins[_pc.NetworkObjectId] = (WorldNet.Now, _spinTime); // (our own body turns with it at once)
                    SetPose(SpellPose.HammerSpin);
                    break;
                }
                case CardId.Wolfgang:
                    // the body stays facing where it was cast (the camera looks round, the legs still walk)
                    _pc.Motor.FaceLock(SpellPlans.WolfDraw + SpellPlans.WolfFireTime, true);
                    SetPose(SpellPose.WolfgangFire);
                    break;
                case CardId.HeavenlySlash:
                    _heavenAt = Time.time + SpellPlans.HeavenCrouch;
                    _heavenStage = 0;
                    _pc.Motor.Root(SpellPlans.HeavenCrouch);
                    SetPose(SpellPose.HeavenCrouch);
                    break;
                case CardId.Parry:
                    SetPose(SpellPose.ParryGuard);
                    break;
                case CardId.Rachmaninoff:
                    _shotAt = Time.time + SpellPlans.RachDelay;
                    SetPose(SpellPose.ShotgunAim);
                    break;
                case CardId.UndyingGrasp:
                    _graspAt = Time.time + SpellPlans.GraspCast;
                    _pc.Motor.Root(SpellPlans.GraspCast); // (turning still works)
                    SetPose(SpellPose.GraspKneel);
                    break;
                case CardId.RockSwarm:
                    // needs ground all round to tear the rocks out of
                    if (!SpellPlans.PlanSwarm(world, _pc.FeetPosition, yaw, _swarmSpots)) { Denied?.Invoke(slot); return false; }
                    SetPose(SpellPose.SwarmRaise);
                    break;
                case CardId.IceSpike:
                    if (SpellPlans.IceGround(world, _pc.FeetPosition, Quaternion.Euler(0, yaw, 0) * Vector3.forward).Count == 0) { Denied?.Invoke(slot); return false; }
                    // crouched, hands on the ground: no moving or turning (the camera still looks round)
                    _pc.Motor.Root(SpellPlans.IceCrouch);
                    _pc.Motor.FaceLock(SpellPlans.IceCrouch);
                    SetPose(SpellPose.IceCrouch);
                    break;
                case CardId.TheHermit:
                    // (standing or knocked down, no gesture: the house just goes up)
                    if (!SpellPlans.PlanHouse(world, SpellPlans.HouseFeet(_pc), yaw).valid) { Denied?.Invoke(slot); return false; }
                    break;
                case CardId.WheelOfFortune:
                    SetPose(SpellPose.WheelSpin);
                    break;
                case CardId.Justice:
                {
                    var corpse = FindCorpse(true);
                    if (corpse == null) { NoTarget?.Invoke(); return false; } // no cost, no cooldown
                    targetId = corpse.NetworkObjectId;
                    _justiceBody = corpse.LastRagdoll;
                    _justiceDoneAt = Time.time + SpellPlans.JusticeCast;
                    _pc.Motor.Root(SpellPlans.JusticeCast);
                    SetPose(SpellPose.SkinKneel);
                    break;
                }
                case CardId.TheHangedMan:
                    // (from the ground, knocked down, too: the noose hauls you up)
                    _pc.Motor.StandUpNow();
                    break;
                case CardId.CompressedShot:
                    // how long it was charged (the server takes it as told, up to full)
                    targetId = (ulong)Mathf.RoundToInt(Mathf.Clamp(_heldFor, 0f, SpellPlans.ShotChargeTime) * 1000f);
                    target = AimPoint(true, out _); // what the centre dot is on
                    SetPose(SpellPose.WindShot);
                    break;
                case CardId.Ephemerate:
                    _pc.Motor.Ephemerate(SpellPlans.EphemTime);
                    break;
                case CardId.CloudStep:
                    if (!_pc.Motor.IsGrounded && _pc.Motor.AirSteps >= SpellPlans.CloudAirSteps) { Denied?.Invoke(slot); return false; } // land first
                    _pc.Motor.CloudStep();
                    SetPose(SpellPose.CloudStep);
                    break;
                case CardId.Submerge:
                    // a hop, head first into the pool; held: under; let go: out (see Update)
                    _pc.Motor.Knock(Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * SpellPlans.SubHopForward + Vector3.up * SpellPlans.SubHop, 0f);
                    _subSlot = slot;
                    _subLetGo = false;
                    SetPose(SpellPose.SubDive);
                    break;
                case CardId.BubbleBomb:
                    target = SpellPlans.MineSpot(_pc.transform.position, yaw);
                    SetPose(SpellPose.WaterCast);
                    break;
                case CardId.Sunflower:
                case CardId.Cactus:
                case CardId.FlyTrap:
                    SetPose(SpellPose.PotHold); // (held until the pot's gone: see the gestures below)
                    break;
                case CardId.Death:
                    _pc.Motor.Siphon(SpellPlans.DeathDashTime); // Mana Siphon's charge, twice as long
                    SetPose(SpellPose.DeathDash);
                    break;
                case CardId.TheDevil:
                    SetPose(SpellPose.RightHandUp);
                    break;
                case CardId.TheTower:
                    if (!GroundTarget(out target) || !SpellPlans.PlanTower(world, target, out _)) { Denied?.Invoke(slot); return false; }
                    _pc.Motor.Root(SpellPlans.TowerPray); // kneeling in prayer
                    SetPose(SpellPose.TowerPray);
                    break;
                case CardId.UndeadServitude:
                {
                    var corpse = FindCorpse();
                    if (corpse == null) { NoTarget?.Invoke(); return false; }
                    targetId = corpse.NetworkObjectId;
                    _pc.Motor.Root(SpellPlans.ReviveTime);
                    SetPose(SpellPose.NecroCrouch);
                    break;
                }
                case CardId.Skinwalker:
                {
                    var corpse = FindCorpse();
                    if (corpse == null) { NoTarget?.Invoke(); return false; }
                    targetId = corpse.NetworkObjectId;
                    _pc.Motor.Root(SpellPlans.SkinTime);
                    SetPose(SpellPose.SkinKneel);
                    break;
                }
                case CardId.RemoteFlameBomb:
                {
                    // where the preview showed it (nowhere for it: no cost, no cooldown)
                    if (!FindBombSpot(out var spot)) { Denied?.Invoke(slot); return false; }
                    if (spot.on != null)
                    {
                        // on a body: that point of that part of it, wherever it goes meanwhile
                        var part = SpellPlans.BombBone(spot.on, spot.bone);
                        if (part == null) { Denied?.Invoke(slot); return false; }
                        target = part.InverseTransformPoint(spot.point);
                        targetId = SpellPlans.PackBomb(true, spot.bone, spot.on.NetworkObjectId, Quaternion.Inverse(part.rotation) * spot.normal);
                    }
                    else
                    {
                        target = spot.point;
                        targetId = SpellPlans.PackBomb(false, 0, 0, spot.normal);
                    }
                    SetPose(SpellPose.BombPlace);
                    break;
                }
                case CardId.FlameBombToss:
                    _tossAt = Time.time + SpellPlans.TossTime;
                    SetPose(SpellPose.BombToss);
                    break;
                case CardId.RemoteDetonation:
                    if (WorldNet.Instance == null || !WorldNet.Instance.HasBomb(_pc.NetworkObjectId)) { Denied?.Invoke(slot); return false; } // none out
                    SetPose(SpellPose.LeftArmUp);
                    break;
                case CardId.InfectiousBite:
                    _pc.Motor.Root(SpellPlans.BiteCrouch);
                    _biteAt = Time.time + SpellPlans.BiteCrouch;
                    SetPose(SpellPose.BiteCrouch);
                    break;
                case CardId.SummonHorde:
                    if (SpellServer.HordeCount(_pc) >= SpellPlans.HordeMax || !SpellServer.PlanHorde(world, _pc.FeetPosition, yaw, _hordeSpots)) { Denied?.Invoke(slot); return false; }
                    _pc.Motor.Root(SpellPlans.HordeCrouch);
                    SetPose(SpellPose.NecroCrouch);
                    break;
                case CardId.FlameThrower:
                    _flaming = true;
                    _flameSlot = slot;
                    _pc.Motor.Root(0.2f);
                    SetPose(SpellPose.FlameSpew);
                    break;
                case CardId.IcyBreath:
                    SetPose(SpellPose.BreathLean);
                    break;
                case CardId.Strength:
                    if (_pc.Status.Value.StrongUntil > WorldNet.Now) { Denied?.Invoke(slot); return false; } // still going
                    SetPose(SpellPose.Flex);
                    break;
                case CardId.GrappleHook:
                {
                    // from the left hand at whatever the centre dot is on
                    Vector3 p = AimPoint(true, out _);
                    Vector3 hand = SpellVisuals.LeftHand(_pc);
                    target = (p - hand).sqrMagnitude > 0.25f ? (p - hand).normalized : CameraRig.AimRay.direction;
                    SetPose(SpellPose.GrapplePoint);
                    break;
                }
                default:
                    return false;
            }
            _pc.RequestCast(slot, info.id, target, yaw, targetId);
            _cooldownUntil[slot] = Time.time + Cards.Cooldown;
            // The Fool: the slot is held only until the rolled card arrives, then it's ready at once
            _foolRolling[slot] = info.id == CardId.TheFool && !free;
            _castUntil = Time.time + (fuse ? SpellPlans.FuseCharge + 0.3f : Cards.CastTime(info.id));
            if (info.costType == CostType.Mana && !free) _predictedSpend += _pc.CostOf(info);
            _predictedAt = Time.time;
            return true;
        }

        // ------------------------------------------------------------------ the new cards' helpers

        readonly System.Collections.Generic.List<Vector3> _hordeSpots = new();
        float _biteAt = -1f;
        bool _flaming;
        int _flameSlot;


        /// <summary>Our pot still in our hands (null: none).</summary>
        WorldNet.PotFx OwnPot() => WorldNet.Instance != null && _pc != null ? WorldNet.Instance.HeldPot(_pc.NetworkObjectId) : null;
        WorldNet.HookFx OwnHook() => WorldNet.Instance != null && _pc != null ? WorldNet.Instance.ActiveHook(_pc.NetworkObjectId) : null;

        /// <summary>Infectious Bite: the crouch is over — lunge head first at the aim (ragdolled; it bites until you're up).</summary>
        void TickBite()
        {
            if (_biteAt < 0f || Time.time < _biteAt) return;
            _biteAt = -1f;
            if (!_pc.IsAlive || _pc.Motor.Ragdolled) return;
            Vector3 aim = CameraRig.AimRay.direction;
            aim.y = Mathf.Clamp(aim.y, -0.3f, 0.6f);
            _castUntil = 0f; // (the lunge is the bite: it isn't cut short by being knocked about)
            _pc.Motor.Knock(aim.normalized * SpellPlans.BiteSpeed + Vector3.up * SpellPlans.BiteUp, SpellPlans.BiteRagdoll);
            _pc.Motor.StandOnLanding(); // limp until it comes down on the floor (biting someone or not)
        }

        float _shotAt = -1f, _graspAt = -1f, _tossAt = -1f;

        // ---------------- Hammer Spin, Heavenly Slash, Parry ----------------

        float _spinTime, _heavenAt = -1f, _justiceDoneAt = -1f;
        GameObject _justiceBody;
        readonly System.Collections.Generic.HashSet<GameObject> _investigated = new();
        int _heavenStage;

        /// <summary>Heavenly Slash: the crouch is over, the leap; up there, the pose; then the cut (the server sends it).</summary>
        void TickHeaven()
        {
            if (_heavenAt < 0f || Time.time < _heavenAt) return;
            if (!_pc.IsAlive) { _heavenAt = -1f; return; }
            switch (_heavenStage)
            {
                case 0:
                {
                    // straight up (not as high under a ceiling)
                    float rise = SpellPlans.HeavenRise;
                    if (Physics.SphereCast(_pc.transform.position, 0.3f, Vector3.up, out var hit, rise + 1f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                        rise = Mathf.Max(0f, hit.distance - 1f);
                    _pc.Motor.Blink(_pc.transform.position + Vector3.up * rise, SpellPlans.HeavenRiseTime);
                    SetPose(SpellPose.HeavenHover);
                    _heavenStage = 1;
                    _heavenAt = Time.time + SpellPlans.HeavenRiseTime;
                    break;
                }
                case 1:
                    _pc.Motor.Blink(_pc.transform.position + Vector3.up * SpellPlans.HeavenDrift, SpellPlans.HeavenHover); // drifting on up a little, slowing to a stop
                    _heavenStage = 2;
                    _heavenAt = Time.time + SpellPlans.HeavenHover;
                    break;
                default:
                    SetPose(SpellPose.HeavenSlash);
                    _heavenAt = -1f;
                    break;
            }
        }

        /// <summary>Heavenly Slash, posing in the air: the outline of the cut at its angle, along the aim (the caster's alone).</summary>
        bool ShowHeaven()
        {
            var net = WorldNet.Instance;
            if (_heavenAt < 0f || net == null || !net.HeavenRollOf(_pc.NetworkObjectId, out float roll)) return false;
            _indicator ??= new SpellIndicator();
            _indicator.ShowHeaven(_pc.transform.position + Vector3.up * 0.3f, CameraRig.AimRay.direction, roll);
            return true;
        }

        /// <summary>Parry deflected a blow: dash in (the server says where) and stab.</summary>
        public void ParryDash(Vector3 to, float seconds)
        {
            if (_pc == null || !_pc.IsAlive) return;
            Vector3 d = to - _pc.transform.position;
            if (new Vector2(d.x, d.z).sqrMagnitude > 1e-4f) _pc.Motor.FaceToward(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, seconds + 0.3f);
            _pc.Motor.Blink(to, seconds, true);
            SetPose(SpellPose.ParryStab);
            _castUntil = Time.time + seconds + 0.25f;
        }
        GunView _guns;

        /// <summary>Aiming down Sebastian's sights (the camera eases in to it).</summary>
        public bool AimingDownSights => Pose == SpellPose.RifleKneel || Pose == SpellPose.RifleFire && WorldNet.Now - PoseStart < SpellPlans.SebWindup + 0.15;

        /// <summary>Sebastian: the slot it's being charged (or about to fire) from, else -1; and when it fires.</summary>
        int _rifleSlot = -1;
        float _rifleFireAt = -1f, _rifleYaw, _riflePitch;

        /// <summary>Sebastian let go: not charged yet, the mana comes back; charged, the gun charges up and then fires.</summary>
        void ReleaseRifle(int slot)
        {
            bool charged = _heldFor >= SpellPlans.SebCharge;
            _pc.RequestSebastianRelease(charged);
            if (!charged)
            {
                _rifleSlot = -1;
                _cooldownUntil[slot] = 0f;
                Denied?.Invoke(slot);
                SetPose(SpellPose.None);
                return;
            }
            _rifleFireAt = Time.time + SpellPlans.SebWindup;
            _rifleYaw = _pc.Motor.Yaw; // this is the shot: from here the camera turns, the body and the gun don't
            _riflePitch = _pc.Motor.Pitch;
            _pc.Motor.FaceLock(SpellPlans.SebWindup + SpellPlans.SebRecover, true);
            _pc.Motor.Kneeling = false;
            _pc.Motor.Root(SpellPlans.SebWindup + SpellPlans.SebRecover);
            SetPose(SpellPose.RifleFire);
            _cooldownUntil[slot] = Time.time + SpellPlans.SebWindup + Cards.Cooldown;
            _castUntil = Time.time + SpellPlans.SebWindup + SpellPlans.SebRecover;
        }

        /// <summary>Interrupted charging Sebastian, or before it fired: the mana is gone.</summary>
        void LoseRifle()
        {
            _rifleFireAt = -1f;
            if (_rifleSlot < 0) return;
            _rifleSlot = -1;
            if (_pc != null && _pc.IsSpawned) _pc.RequestSebastianLost();
            if (Pose == SpellPose.RifleFire && _pc != null) { _pc.Motor.Unroot(); _pc.Motor.FaceLock(0f); }
        }

        /// <summary>Rachmaninoff: out, now it fires. Undying Grasp: the knee's done, the hands come.</summary>
        void TickGuns()
        {
            if (_shotAt >= 0f && Time.time >= _shotAt)
            {
                _shotAt = -1f;
                if (_pc.IsAlive)
                {
                    // from the muzzle, at whatever the centre dot is on
                    if (_guns == null) _guns = GetComponent<GunView>();
                    if (_guns == null || !_guns.TryMuzzle(out var muzzle)) muzzle = SpellVisuals.RightHand(_pc) + CameraRig.AimRay.direction * 0.4f;
                    Vector3 d = AimPoint(true, out _) - muzzle;
                    bool near = d.magnitude < 1.5f || Vector3.Dot(d, CameraRig.AimRay.direction) <= 0f;
                    _pc.RequestShotgun(muzzle, near ? CameraRig.AimRay.direction : d.normalized);
                }
            }
            if (_rifleFireAt >= 0f && Time.time >= _rifleFireAt)
            {
                _rifleFireAt = -1f;
                _rifleSlot = -1;
                if (_pc.IsAlive) _pc.RequestSebastianShot(_rifleYaw, _riflePitch);
            }
            // Flame Bomb Toss: at whatever the centre dot is on right then
            if (_tossAt >= 0f && Time.time >= _tossAt)
            {
                _tossAt = -1f;
                if (_pc.IsAlive) _pc.RequestToss(AimPoint(true, out _));
            }
            if (_graspAt >= 0f && Time.time >= _graspAt)
            {
                _graspAt = -1f;
                if (_pc.IsAlive)
                {
                    _pc.RequestGrasp(_pc.Motor.Yaw);
                    _pc.Motor.FaceLock(SpellPlans.GraspRise); // the hands are coming: the body stays facing down the line
                }
            }
        }

        /// <summary>Undying Grasp: where the hands would come up.</summary>
        void ShowGrasp()
        {
            _indicator ??= new SpellIndicator();
            Vector3 dir = Quaternion.Euler(0, _pc.Motor.Yaw, 0) * Vector3.forward;
            _indicator.ShowGraspLine(SpellPlans.GraspGround(VoxelWorld.Current, _pc.FeetPosition, dir), dir);
        }

        /// <summary>The Flame Thrower: held on the button. Rooted, hands out, nothing else cast, until it's let go.</summary>
        void TickFlameThrower()
        {
            if (!_flaming) return;
            bool held = _pc.IsAlive && !_pc.Motor.Ragdolled && !InputMap.Blocked && GameSettings.Binds[GameSettings.AbilityAction(_flameSlot)].IsPressed;
            if (!held)
            {
                _flaming = false;
                _pc.RequestBreathStop();
                _pc.Motor.Unroot();
                if (Pose == SpellPose.FlameSpew) SetPose(SpellPose.None);
                return;
            }
            _pc.Motor.Root(0.15f);
            _castUntil = Time.time + 0.15f;
            if (Pose != SpellPose.FlameSpew) SetPose(SpellPose.FlameSpew);
        }

        int _towerAimed;

        /// <summary>Our tower is about done rising: tell the server where the cursor lands (it turns to face that spot).</summary>
        void TickTowerAim()
        {
            var net = WorldNet.Instance;
            if (net == null || net.Towers.Count == 0) return;
            foreach (var t in net.Towers.Values)
            {
                if (t.caster != _pc.NetworkObjectId || t.id == _towerAimed || t.turnAt >= 0f) continue;
                if (WorldNet.Now < t.start + SpellPlans.TowerRise - 0.1) continue;
                _towerAimed = t.id;
                // what the crosshair is on, looking past the tower itself
                var ray = CameraRig.AimRay;
                Vector3 point = ray.origin + ray.direction * 60f;
                float best = float.MaxValue;
                foreach (var h in Physics.RaycastAll(ray, 300f, Layers.EnvironmentMask | (1 << Layers.Hitbox), QueryTriggerInteraction.Collide))
                {
                    if (t.body != null && h.collider.transform.IsChildOf(t.body.transform)) continue;
                    if (h.collider.isTrigger && (h.collider.GetComponent<Hitbox>() == null || h.collider.GetComponent<Hitbox>().Owner == _pc)) continue;
                    if (Vector3.Dot(h.point - _pc.transform.position, ray.direction) < 0f) continue;
                    if (h.distance < best) { best = h.distance; point = h.point; }
                }
                _pc.RequestTowerAim(point);
            }
        }

        /// <summary>The server says our breath is over (let go, out of mana, knocked down, or its time is up).</summary>
        void OnBreathEnded(ulong caster, byte kind)
        {
            if (_pc == null || caster != _pc.NetworkObjectId) return;
            if (kind == 0)
            {
                _flaming = false;
                _pc.Motor.Unroot();
                if (Pose == SpellPose.FlameSpew) SetPose(SpellPose.None);
            }
            else if (Pose == SpellPose.BreathLean) SetPose(SpellPose.None);
        }

        void OnHookAttached(int id, ulong caster, Vector3 point, ulong target)
        {
            if (_pc == null || caster != _pc.NetworkObjectId || target != 0) return;
            _pc.Motor.Reel(point); // stuck in the ground: reeled in to it
        }

        void OnHookEnded(int id, ulong caster)
        {
            if (_pc == null || caster != _pc.NetworkObjectId) return;
            _pc.Motor.EndReel();
            if (Pose == SpellPose.GrapplePoint) SetPose(SpellPose.None);
        }

        /// <summary>A hex: the enemy (not one of your Undead) nearest the centre dot, close enough and in sight (Mind Hex: not one already Mind Hexed).</summary>
        Combatant FindHexTarget(byte kind)
        {
            var ray = CameraRig.AimRay;
            Combatant best = null;
            float bestAngle = SpellPlans.HexAngle;
            foreach (var c in Combatant.Registry)
            {
                if (c == null || c == _pc || !c.IsAlive || !c.Visible || Combatant.Allies(_pc, c) || c.Status.Value.Taken(WorldNet.Now)) continue;
                if (kind == SpellPlans.MindHex && SpellServer.MindHexed(c)) continue;
                if (kind == SpellPlans.SmallifyHex && SpellServer.Smallified(c)) continue;
                if ((c.transform.position - _pc.transform.position).magnitude > SpellPlans.HexRange) continue;
                float angle = Vector3.Angle(ray.direction, c.transform.position + Vector3.up * 0.3f - ray.origin);
                if (angle < bestAngle && SpellPlans.HexSight(_pc.transform.position, c)) { bestAngle = angle; best = c; }
            }
            return best;
        }

        /// <summary>The World: the enemy (not one of your Undead) nearest the centre dot, close enough.</summary>
        Combatant FindWorldTarget()
        {
            var ray = CameraRig.AimRay;
            Combatant best = null;
            float bestAngle = SpellPlans.WorldPickAngle;
            foreach (var c in Combatant.Registry)
            {
                if (c == null || c == _pc || !c.IsAlive || !c.Visible || Combatant.Allies(_pc, c) || c.Status.Value.Taken(WorldNet.Now)) continue;
                Vector3 chest = c.transform.position + Vector3.up * 0.3f;
                if ((chest - _pc.transform.position).magnitude > SpellPlans.WorldReach) continue;
                float angle = Vector3.Angle(ray.direction, chest - ray.origin);
                if (angle < bestAngle) { bestAngle = angle; best = c; }
            }
            return best;
        }

        int _starId, _starNext;

        /// <summary>Our stars: each one, when its turn comes, goes at whatever the centre dot is on right then (knocked down or not).</summary>
        void TickStars()
        {
            var net = WorldNet.Instance;
            if (net == null || net.Stars.Count == 0 || !_pc.IsAlive) return;
            double now = WorldNet.Now;
            foreach (var fx in net.Stars.Values)
            {
                if (fx.caster != _pc.NetworkObjectId) continue;
                if (fx.id < _starId) continue; // (an older cast: the server sends those on its own if we don't)
                if (fx.id != _starId) { _starId = fx.id; _starNext = 0; }
                if (_starNext >= fx.stars.Length || now < fx.start + SpellPlans.StarEvery * (_starNext + 1)) continue;
                Vector3 from = WorldNet.StarPosition(fx, _starNext, now);
                Vector3 d = AimPoint(true, out bool hit) - from;
                Vector3 dir = hit && d.sqrMagnitude > 1f ? d.normalized : CameraRig.AimRay.direction;
                _pc.RequestStarLaunch(fx.id, _starNext++, dir);
            }
        }

        int _shedSlot = -1;
        float _shedAt;

        /// <summary>Skinwalker's key held away from any corpse: after a moment the skin comes off (let go sooner, or came up to a corpse: it doesn't).</summary>
        void TickShed(bool able)
        {
            if (_shedSlot < 0) return;
            int slot = _shedSlot;
            bool held = able && !InputMap.Blocked && CardIn(slot) == CardId.Skinwalker && _pc.Skin.Value.On
                && GameSettings.Binds[GameSettings.AbilityAction(slot)].IsPressed;
            if (!held || FindCorpse() != null) { _shedSlot = -1; return; }
            if (Time.time < _shedAt) return;
            _shedSlot = -1;
            _cooldownUntil[slot] = Time.time + Cards.Cooldown;
            _pc.RequestShedSkin();
        }

        /// <summary>Undead Servitude, Skinwalker, Justice: a corpse lying within reach, the one nearest the centre dot.</summary>
        /// <param name="justice">Leave out the bodies we've already investigated (each only once).</param>
        Combatant FindCorpse(bool justice = false)
        {
            var ray = CameraRig.AimRay;
            Combatant best = null;
            float bestAngle = 75f;
            foreach (var c in Combatant.Registry)
            {
                if (c == null || c == _pc || !c.IsSpawned || !c.Dead.Value || c.LastRagdoll == null) continue;
                if (justice && _investigated.Contains(c.LastRagdoll)) continue;
                Vector3 body = SpellServer.CorpsePoint(c);
                if (!SpellPlans.CorpseInReach(body, _pc.transform.position, 0.5f)) continue;
                float angle = Vector3.Angle(ray.direction, body - ray.origin);
                if (angle < bestAngle) { bestAngle = angle; best = c; }
            }
            return best;
        }

        // ------------------------------------------------------------------ Remote Flame Bomb

        struct BombSpot { public Vector3 point, normal; public Combatant on; public int bone; }

        /// <summary>
        /// Where a bomb would go: what the cursor is on, if that's in reach; else the nearest surface in reach towards (or
        /// close round) the cursor. False: nowhere (<paramref name="spot"/> is then as far as you can reach that way).
        /// </summary>
        bool FindBombSpot(out BombSpot spot)
        {
            var ray = CameraRig.AimRay;
            Vector3 from = _pc.transform.position + Vector3.up * 0.4f;
            bool hit = BombRay(ray.origin, ray.direction, 200f, true, out spot);
            if (hit && InBombReach(spot.point)) return true;
            Vector3 aim = (hit ? spot.point : ray.origin + ray.direction * 60f) - from;
            aim = aim.sqrMagnitude > 0.01f ? aim.normalized : ray.direction;
            var look = Quaternion.LookRotation(aim);
            for (int ring = 0; ring <= 2; ring++)
                for (int k = 0; k < (ring == 0 ? 1 : 8); k++)
                {
                    Vector3 d = look * Quaternion.Euler(0f, 0f, k * 45f) * Quaternion.Euler(ring * 6f, 0f, 0f) * Vector3.forward;
                    if (BombRay(from, d, SpellPlans.BombReach, false, out spot) && InBombReach(spot.point)) return true;
                }
            spot = new BombSpot { point = from + aim * SpellPlans.BombReach, normal = -aim };
            return false;
        }

        bool InBombReach(Vector3 p) => SpellPlans.BombDistance(_pc, p) <= SpellPlans.BombReach;

        /// <summary>The first surface (ground, or anyone else's body) along a ray.</summary>
        bool BombRay(Vector3 origin, Vector3 dir, float length, bool fromCamera, out BombSpot spot)
        {
            spot = default;
            float best = float.MaxValue;
            foreach (var h in Physics.RaycastAll(origin, dir, length, Layers.EnvironmentMask | (1 << Layers.Hitbox), QueryTriggerInteraction.Collide))
            {
                Hitbox hb = null;
                if (h.collider.isTrigger)
                {
                    hb = h.collider.GetComponent<Hitbox>();
                    if (hb == null || hb.Owner == null || hb.Owner == _pc || !hb.Owner.IsAlive) continue;
                }
                // ignore things between the camera and the player's shoulder
                if (fromCamera && Vector3.Dot(h.point - _pc.transform.position, dir) < 0f) continue;
                if (h.distance >= best) continue;
                best = h.distance;
                spot = new BombSpot
                {
                    point = h.point, normal = h.normal, on = hb != null ? hb.Owner : null,
                    bone = hb != null && hb.Start != null ? hb.SegmentIndex : SpellPlans.BombWholeBody,
                };
            }
            return best < float.MaxValue;
        }

        // ------------------------------------------------------------------ flame & tug helpers

        int _fireId, _hellId;
        double _fireLaunchAt;
        float _escapeAt = -1f, _escapeHeld, _hellNextPoint, _hellUntil;
        int _subSlot = -1;
        bool _subLetGo;
        Vector3 _hellTip, _hellDir;

        void OnFireballCharging(int id, ulong caster, double start)
        {
            if (_pc == null || caster != _pc.NetworkObjectId) return;
            _fireId = id;
            bool necro = WorldNet.Instance != null && WorldNet.Instance.Fireballs.TryGetValue(id, out var fx) && fx.necro; // Necromatic Blast
            _fireLaunchAt = start + (necro ? SpellPlans.NecroCharge : SpellPlans.FireballCharge);
        }

        void OnHellfireStarted(int id, ulong caster)
        {
            if (_pc == null || caster != _pc.NetworkObjectId) return;
            _hellId = id;
            _hellUntil = Time.time + SpellPlans.HellfireDraw;
            _hellNextPoint = 0f;
            Vector3 fwd = Quaternion.Euler(0, _pc.Motor.Yaw, 0) * Vector3.forward;
            _hellDir = fwd;
            _hellTip = OnGround(_pc.FeetPosition + fwd * 1f, _pc.FeetPosition.y) ?? _pc.FeetPosition + fwd;
            _pc.RequestHellfirePoint(id, _hellTip);
            LocalHellfire()?.local.Add(_hellTip);
        }

        void TickFlame()
        {
            // Ball of Flame: thrown the moment it's fully formed, at whatever the centre dot is on right then
            if (_fireId != 0 && WorldNet.Now >= _fireLaunchAt)
            {
                var net = WorldNet.Instance;
                Vector3 from = net != null && net.Fireballs.TryGetValue(_fireId, out var fx) ? WorldNet.FireballPosition(fx, WorldNet.Now) : _pc.transform.position + Vector3.up;
                Vector3 d = AimPoint(true, out _) - from;
                if (_pc.IsAlive) _pc.RequestFireballLaunch(_fireId, d.sqrMagnitude > 0.25f ? d.normalized : CameraRig.AimRay.direction);
                _fireId = 0;
            }

            // Hellfire Wall: the glowing line runs along the ground, steering after the cursor, for a second
            if (_hellId != 0)
            {
                if (Time.time >= _hellUntil || !_pc.IsAlive) { _hellId = 0; }
                else
                {
                    float dt = Time.deltaTime;
                    if (GroundTarget(out var want))
                    {
                        Vector3 to = want - _hellTip;
                        to.y = 0f;
                        if (to.sqrMagnitude > 0.25f) _hellDir = Vector3.RotateTowards(_hellDir, to.normalized, SpellPlans.HellfireTurn * Mathf.Deg2Rad * dt, 0f);
                    }
                    Vector3 next = _hellTip + _hellDir * (SpellPlans.HellfireLineSpeed * dt);
                    var ground = OnGround(next, _hellTip.y);
                    if (ground == null) { _hellId = 0; } // ran off an edge: the line stops there
                    else
                    {
                        _hellTip = ground.Value;
                        LocalHellfire()?.local.Add(_hellTip);
                        _hellNextPoint -= dt;
                        if (_hellNextPoint <= 0f)
                        {
                            _hellNextPoint = 0.04f;
                            _pc.RequestHellfirePoint(_hellId, _hellTip);
                        }
                    }
                }
            }

            // Explosive Escape: the clap — you're blown back and up (the server does the blast itself)
            if (_escapeAt >= 0f && Time.time >= _escapeAt)
            {
                _escapeAt = -1f;
                if (_pc.IsAlive)
                {
                    Vector3 fwd = Quaternion.Euler(0, _pc.Motor.Yaw, 0) * Vector3.forward;
                    _pc.Motor.Knock((-fwd * SpellPlans.EscapeBack + Vector3.up * SpellPlans.EscapeUp) * SpellPlans.EscapeKnockScale(_escapeHeld), 0f);
                }
            }
        }

        WorldNet.HellfireFx LocalHellfire() =>
            WorldNet.Instance != null && WorldNet.Instance.Hellfires.TryGetValue(_hellId, out var fx) ? fx : null;

        /// <summary>The ground under a point (searching a little above and below), or null if there's none.</summary>
        static Vector3? OnGround(Vector3 p, float nearY)
        {
            Vector3 from = new(p.x, nearY + 2.5f, p.z);
            if (Physics.Raycast(from, Vector3.down, out var hit, 6f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) return hit.point + Vector3.up * 0.02f;
            return null;
        }

        /// <summary>Tug: where the wind starts — under the cursor if that's within 15 m, else 15 m out along it.</summary>
        Vector3 TugOrigin()
        {
            Vector3 chest = _pc.transform.position + Vector3.up * 0.4f;
            Vector3 p = AimPoint(true, out bool hit);
            Vector3 d = p - chest;
            if (!hit || d.magnitude > SpellPlans.TugRange) p = chest + CameraRig.AimRay.direction * SpellPlans.TugRange;
            return p;
        }

        void OnRockGathered(int id, ulong caster, double start)
        {
            if (_pc == null || caster != _pc.NetworkObjectId) return;
            _rockId = id;
            _rockLaunchAt = start + SpellPlans.RockGather + SpellPlans.RockHold;
        }

        // ------------------------------------------------------------------ aiming

        /// <summary>Where the crosshair points (first thing it hits, ignoring yourself).</summary>
        public Vector3 AimPoint(bool includePlayers, out bool hitSomething)
        {
            var ray = CameraRig.AimRay;
            int mask = Layers.EnvironmentMask | (includePlayers ? 1 << Layers.Hitbox : 0);
            var hits = Physics.RaycastAll(ray, 200f, mask, includePlayers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            Vector3 point = ray.origin + ray.direction * 60f;
            hitSomething = false;
            foreach (var h in hits)
            {
                if (h.collider.isTrigger)
                {
                    var hb = h.collider.GetComponent<Hitbox>();
                    if (hb == null || hb.Owner == _pc) continue;
                }
                // ignore things between the camera and the player's shoulder
                if (Vector3.Dot(h.point - _pc.transform.position, ray.direction) < 0f) continue;
                if (h.distance < best) { best = h.distance; point = h.point; hitSomething = true; }
            }
            return point;
        }

        /// <summary>The spot on the ground a ground spell lands on: the crosshair, pulled in to max range.</summary>
        public bool GroundTarget(out Vector3 target) => GroundTarget(out target, Cards.MaxRange);

        /// <summary>The spot on the ground a ground spell lands on: the crosshair, pulled in to <paramref name="range"/>.</summary>
        public bool GroundTarget(out Vector3 target, float range)
        {
            Vector3 p = AimPoint(false, out bool hit);
            Vector3 feet = _pc.FeetPosition;
            Vector3 flat = p - feet;
            flat.y = 0f;
            if (!hit || flat.magnitude > range)
            {
                if (flat.sqrMagnitude < 1e-4f) { target = default; return false; }
                Vector3 edge = feet + flat.normalized * Mathf.Min(flat.magnitude, range);
                float top = Mathf.Max(feet.y, hit ? p.y : feet.y) + 6f;
                if (!Physics.Raycast(new Vector3(edge.x, top, edge.z), Vector3.down, out var down, 40f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                {
                    target = default;
                    return false;
                }
                p = down.point;
            }
            target = p;
            return true;
        }

        /// <summary>Nomadic Dash: the way you're pressing (8 directions, relative to the camera); forward if nothing.</summary>
        Vector3 DashDirection()
        {
            Vector2 input = InputMap.Move;
            if (input.sqrMagnitude < 0.01f) input = Vector2.up;
            float angle = Mathf.Round(Mathf.Atan2(input.x, input.y) * Mathf.Rad2Deg / 45f) * 45f;
            return Quaternion.Euler(0, _pc.Motor.Yaw + angle, 0) * Vector3.forward;
        }

        /// <summary>Mana Missiles: the enemy (player or dummy) nearest the centre dot, within range and roughly in view.</summary>
        Combatant FindMissileTarget() => FindTarget(SpellPlans.MissileTargetRange, SpellPlans.MissileTargetAngle);

        /// <summary>Whoever else (player or dummy) is nearest the centre dot, within <paramref name="range"/> and <paramref name="maxAngle"/> degrees of it.</summary>
        Combatant FindTarget(float range, float maxAngle)
        {
            var ray = CameraRig.AimRay;
            Combatant best = null;
            float bestAngle = maxAngle;
            foreach (var c in Combatant.Registry)
            {
                if (c == null || c == _pc || !c.IsAlive) continue;
                Vector3 chest = c.transform.position + Vector3.up * 0.3f;
                if ((chest - _pc.transform.position).magnitude > range) continue;
                float angle = Vector3.Angle(ray.direction, chest - ray.origin);
                if (angle < bestAngle) { bestAngle = angle; best = c; }
            }
            return best;
        }

        /// <summary>
        /// Throw direction: straight from the held rock at whatever's under the centre dot — no aiming up to make
        /// up for gravity, so it drops along the way like a real throw (and you learn to lead it).
        /// </summary>
        Vector3 RockThrowDirection(int id)
        {
            Vector3 from = _pc.transform.position + Vector3.up * 0.8f;
            var net = WorldNet.Instance;
            if (net != null && net.Rocks.TryGetValue(id, out var fx)) from = WorldNet.RockPosition(fx, WorldNet.Now);
            Vector3 d = AimPoint(true, out _) - from;
            Vector3 dir = d.sqrMagnitude > 0.25f ? d.normalized : CameraRig.AimRay.direction;
            // a fixed lift, so it's "zeroed" at RockZeroRange: dead on at that distance, a little high closer, drops further out
            return Vector3.RotateTowards(dir, Vector3.up, RockLift, 0f);
        }

        /// <summary>The throw's lift (radians): the launch angle that carries a rock level for <see cref="SpellPlans.RockZeroRange"/>.</summary>
        static float RockLift => 0.5f * Mathf.Asin(Mathf.Clamp01(SpellPlans.RockGravity * SpellPlans.RockZeroRange / (SpellPlans.RockSpeed * SpellPlans.RockSpeed)));

        // ------------------------------------------------------------------ indicator

        readonly System.Collections.Generic.List<(Vector3 c, Quaternion r, Vector3 s)> _boxes = new();

        void ShowIndicator(int slot)
        {
            _indicator ??= new SpellIndicator();
            var card = CardIn(slot);
            var world = VoxelWorld.Current;
            bool ok = GroundTarget(out var target);
            float yaw = _pc.Motor.Yaw;
            switch (card)
            {
                case CardId.SummonWall:
                {
                    var plan = ok ? SpellPlans.PlanWall(world, target, yaw) : null;
                    if (plan == null) { _indicator.Hide(); return; }
                    _indicator.ShowWall(plan);
                    break;
                }
                case CardId.FoldingHands:
                {
                    if (!ok) { _indicator.Hide(); return; }
                    var plan = SpellPlans.PlanFold(world, target, yaw);
                    _indicator.ShowFold(plan, target, yaw);
                    break;
                }
                case CardId.Shove:
                {
                    Vector3 dir = CameraRig.AimRay.direction;
                    Vector3 origin = _pc.transform.position + Vector3.up * 0.4f + dir * (SpellPlans.GustHalfSize.z + 0.6f);
                    _indicator.ShowShove(origin, dir);
                    break;
                }
                case CardId.Tornado:
                {
                    Vector3 dir = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                    if (GroundTarget(out var near, SpellPlans.TornadoRange)) _indicator.ShowTornado(near, dir);
                    else _indicator.Hide();
                    break;
                }
                case CardId.ArcaneJudgment:
                    if (ok) _indicator.ShowJudgment(target, SpellPlans.JudgmentRadius);
                    else _indicator.Hide();
                    break;
                case CardId.Judgement:
                    if (ok) _indicator.ShowJudgment(target, SpellPlans.TarotJudgmentRadius, true);
                    else _indicator.Hide();
                    break;
                case CardId.Tug:
                {
                    Vector3 from = TugOrigin();
                    Vector3 to = _pc.transform.position + Vector3.up * 0.4f;
                    if ((to - from).magnitude > 1.5f) _indicator.ShowTug(from, to);
                    else _indicator.Hide();
                    break;
                }
                case CardId.BubbleBomb:
                {
                    // where it starts, and where it'll have drifted to
                    Vector3 start = SpellPlans.MineSpot(_pc.transform.position, yaw);
                    _indicator.ShowMine(start, start + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * SpellPlans.MineGlide, SpellPlans.MineRadius);
                    break;
                }
                case CardId.ExplosiveEscape:
                {
                    // the explosion itself (the crater) in front, growing as it's held
                    float held = Mathf.Min(Time.time - _heldSince, SpellPlans.EscapeChargeMax);
                    _indicator.ShowOrb(SpellPlans.EscapeCraterCentre(SpellPlans.EscapeSpot(_pc.transform.position, yaw)), SpellPlans.EscapeCraterRadius(held));
                    break;
                }
                case CardId.WaterOfLife:
                    _indicator.ShowOrb(SpellPlans.OrbSpot(_pc.transform.position, yaw), SpellPlans.OrbRadius);
                    break;
                case CardId.Tsunami:
                    _indicator.ShowTsunami(_pc.FeetPosition, Quaternion.Euler(0, yaw, 0) * Vector3.forward);
                    break;
                case CardId.WarpSelf:
                {
                    Vector3 p = AimPoint(false, out bool hit);
                    bool can = SpellPlans.WarpTarget(_pc.transform.position, p, hit, CameraRig.AimRay.direction, out var feet);
                    _indicator.ShowWarp(feet, can);
                    break;
                }
                case CardId.WarpSpace:
                {
                    Vector3 p = AimPoint(false, out bool hit);
                    bool can = SpellPlans.PlanPortal(_pc.transform.position, p, hit, CameraRig.AimRay.direction, out var center, out float portalYaw, out float portalPitch);
                    _indicator.ShowPortal(center, portalYaw, portalPitch, can && !PortalsLinked());
                    break;
                }
                case CardId.TheHierophant:
                {
                    Vector3 p = AimPoint(false, out bool hit);
                    SpellPlans.PlanHierophant(_pc.transform.position, p, hit, CameraRig.AimRay.direction, out var center, out float wy, out float wp);
                    _indicator.ShowPane(center, SpellPlans.PortalRotation(wy, wp),
                        new Vector3(SpellPlans.HierophantWidth, SpellPlans.HierophantHeight, SpellPlans.HierophantThick));
                    break;
                }
                case CardId.IceSpike:
                {
                    Vector3 dir = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                    _indicator.ShowIceLine(SpellPlans.IceGround(world, _pc.FeetPosition, dir), dir);
                    break;
                }
                case CardId.TheHermit:
                {
                    var plan = SpellPlans.PlanHouse(world, SpellPlans.HouseFeet(_pc), yaw);
                    if (!plan.valid) { _indicator.Hide(); return; }
                    SpellPlans.HouseBoxes(world, plan, _boxes);
                    _indicator.ShowBoxes(_boxes, true);
                    break;
                }
                case CardId.TheTower:
                {
                    if (!ok) { _indicator.Hide(); return; }
                    bool can = SpellPlans.PlanTower(world, target, out var baseCell);
                    if (!can) baseCell = world != null ? world.CellOf(target) : default;
                    SpellPlans.TowerBoxes(world, baseCell, _boxes);
                    _indicator.ShowBoxes(_boxes, can);
                    break;
                }
                case CardId.RemoteFlameBomb:
                {
                    bool can = FindBombSpot(out var spot);
                    _indicator.ShowBomb(spot.point + spot.normal * (SpellPlans.BombThick * 0.5f), SpellPlans.BombRotation(spot.normal, yaw), can);
                    break;
                }
                case CardId.UndyingGrasp:
                    ShowGrasp();
                    break;
                case CardId.HammerSpin:
                {
                    // the path it'll take, growing as it's held (lit once it's wound up enough)
                    float held = Time.time - _heldSince;
                    Vector3 feet = _pc.FeetPosition;
                    _indicator.ShowSpin(feet, SpellPlans.SpinDestination(world, feet, yaw, SpellPlans.SpinRange(held)), held >= SpellPlans.SpinMinHold);
                    break;
                }
                case CardId.FlowingCut:
                {
                    // where you'll end up (lit once there's at least one charge)
                    Vector3 land = SpellPlans.FlowDestination(world, _pc.FeetPosition, yaw);
                    _indicator.ShowWarp(land, FlowCharges >= 1);
                    break;
                }
                default:
                    _indicator.Hide();
                    break;
            }
        }
    }
}
