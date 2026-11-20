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
    public class AbilityCaster : MonoBehaviour
    {
        PlayerController _pc;
        SpellIndicator _indicator;
        int _holding = -1;
        readonly float[] _cooldownUntil = new float[Loadout.Slots];
        float _predictedSpend, _predictedAt;
        ManaState _manaSeen;
        int _rockId;
        double _rockLaunchAt;

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
        }

        void OnDisable()
        {
            WorldNet.RockGathered -= OnRockGathered;
            WorldNet.FireballCharging -= OnFireballCharging;
            WorldNet.HellfireStarted -= OnHellfireStarted;
            _indicator?.Hide();
        }

        void OnDestroy() => _indicator?.Destroy();

        public CardId CardIn(int slot) => _pc != null ? _pc.CardInSlot(slot) : Loadouts.Selected.Get(slot);
        /// <summary>A free card rolled by The Fool (costs nothing, used once).</summary>
        public bool IsFree(int slot) => _pc != null && _pc.IsFoolCard(slot);
        public float CooldownLeft(int slot) => Mathf.Max(0f, _cooldownUntil[slot] - Time.time);
        public bool Affordable(int slot) => _pc != null && (IsFree(slot) || _pc.CanAfford(Cards.Get(CardIn(slot)), _predictedSpend));

        public void ResetForRound()
        {
            CancelAll();
            for (int i = 0; i < _cooldownUntil.Length; i++) _cooldownUntil[i] = 0f;
            _predictedSpend = 0f;
        }

        public void CancelAll()
        {
            _holding = -1;
            _rockId = 0;
            _fireId = 0;
            _hellId = 0;
            _escapeAt = -1;
            SetPose(SpellPose.None);
            _indicator?.Hide();
        }

        void Update()
        {
            if (_pc == null || !_pc.IsSpawned) return;
            // the server's mana changed: our guess has been replaced by the real value
            if (!_pc.Mana.Value.Equals(_manaSeen)) { _manaSeen = _pc.Mana.Value; _predictedSpend = 0f; }
            if (_predictedSpend > 0f && Time.time - _predictedAt > 1.5f) _predictedSpend = 0f;

            bool able = _pc.IsAlive && !_pc.Motor.Ragdolled && !_pc.Motor.GettingUp && !_pc.Motor.Busy && MatchManager.CastingAllowed;
            if (!able && _holding >= 0) { _holding = -1; SetPose(SpellPose.None); }

            if (able && !InputMap.Blocked)
            {
                for (int slot = 0; slot < Loadout.Slots; slot++)
                {
                    var act = GameSettings.AbilityAction(slot);
                    if (InputMap.Down(act)) Press(slot);
                    if (_holding == slot && !GameSettings.Binds[act].IsPressed) Release(slot);
                }
            }
            else if (InputMap.Blocked && _holding >= 0)
            {
                _holding = -1;
                SetPose(SpellPose.None);
            }

            if (_holding >= 0) ShowIndicator(_holding);
            else _indicator?.Hide();

            // timed rock throw
            if (_rockId != 0 && WorldNet.Now >= _rockLaunchAt)
            {
                if (_pc.IsAlive) _pc.RequestRockLaunch(_rockId, RockThrowDirection(_rockId));
                _rockId = 0;
            }

            TickFlame();

            // gestures end on their own
            float t = (float)(WorldNet.Now - PoseStart);
            float ends = Pose switch
            {
                SpellPose.CastWall => 0.75f, SpellPose.CastFold => 0.95f, SpellPose.Gather => 1.15f,
                SpellPose.Dash => 0.5f, SpellPose.ShoveCast => 0.6f, SpellPose.TornadoCast => 0.9f, SpellPose.MissileCharge => 1.2f,
                SpellPose.Barrier => 1.25f, SpellPose.Siphon => SpellPlans.SiphonTime + 0.1f, SpellPose.SiphonPunch => 0.5f,
                SpellPose.FlameCharge => 0.95f, SpellPose.HellfireDraw => SpellPlans.HellfireDraw + 0.2f, SpellPose.FlameHands => 0.7f,
                SpellPose.JudgmentCast => 1.6f, SpellPose.TugCast => 0.7f, SpellPose.Platform => 0.7f,
                SpellPose.FoolRoll => 0.5f, SpellPose.Magician => 0.7f, SpellPose.SunRaise => SpellPlans.PriestessDelay + 0.35f, SpellPose.Prayer => 0.6f,
                _ => float.MaxValue,
            };
            if (t > ends) SetPose(SpellPose.None);
        }

        void SetPose(SpellPose p)
        {
            Pose = p;
            PoseStart = WorldNet.Now;
        }

        // ------------------------------------------------------------------ input

        void Press(int slot)
        {
            var info = Cards.Get(CardIn(slot));
            if (info == null) return;
            if (CooldownLeft(slot) > 0f) { Denied?.Invoke(slot); return; }
            if (info.castOnPress)
            {
                TryCast(slot, info);
                return;
            }
            _holding = slot;
            SetPose(info.id switch
            {
                CardId.FoldingHands => SpellPose.HoldFold,
                CardId.Shove => SpellPose.ShoveHold,
                CardId.Tornado => SpellPose.TornadoHold,
                CardId.ArcaneJudgment => SpellPose.JudgmentHold,
                CardId.Tug => SpellPose.TugHold,
                _ => SpellPose.HoldWall,
            });
        }

        void Release(int slot)
        {
            _holding = -1;
            var info = Cards.Get(CardIn(slot));
            if (info == null || !TryCast(slot, info)) SetPose(SpellPose.None);
        }

        bool TryCast(int slot, CardInfo info)
        {
            if (CooldownLeft(slot) > 0f || !Affordable(slot)) { Denied?.Invoke(slot); return false; }
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
                case CardId.RockBarrage:
                {
                    // aim point nudged just inside whatever the crosshair is on
                    Vector3 p = AimPoint(false, out bool hit);
                    target = hit ? p + CameraRig.AimRay.direction * 0.15f : p;
                    if (!SpellPlans.PlanRock(world, _pc.transform.position, _pc.FeetPosition, Quaternion.Euler(0, yaw, 0) * Vector3.forward, target, out _))
                    { Denied?.Invoke(slot); return false; }
                    SetPose(SpellPose.Gather);
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
                    _escapeAt = Time.time + SpellPlans.EscapeDelay;
                    SetPose(SpellPose.FlameHands);
                    break;
                case CardId.ArcaneJudgment:
                    if (!GroundTarget(out target)) { Denied?.Invoke(slot); return false; }
                    SetPose(SpellPose.JudgmentCast);
                    break;
                case CardId.Tug:
                    target = TugOrigin(); // where the wind starts (it blows back to you)
                    SetPose(SpellPose.TugCast);
                    break;
                case CardId.TheFool:
                    SetPose(SpellPose.FoolRoll);
                    break;
                case CardId.TheMagician:
                    SetPose(SpellPose.Magician);
                    break;
                case CardId.TheHighPriestess:
                    SetPose(SpellPose.SunRaise);
                    break;
                case CardId.TheEmpress:
                case CardId.TheEmperor:
                    SetPose(SpellPose.Prayer);
                    break;
                case CardId.SummonPlatform:
                    if (!SpellPlans.PlanPlatform(world, _pc.FeetPosition).valid) { Denied?.Invoke(slot); return false; }
                    _pc.Motor.Knock(Vector3.up * SpellPlans.PlatformLift, 0f); // ride it up (no ragdoll)
                    SetPose(SpellPose.Platform);
                    break;
                default:
                    return false;
            }
            _pc.RequestCast(slot, info.id, target, yaw, targetId);
            _cooldownUntil[slot] = Time.time + Cards.Cooldown;
            if (info.costType == CostType.Mana && !free) _predictedSpend += info.cost;
            _predictedAt = Time.time;
            return true;
        }

        // ------------------------------------------------------------------ flame & tug helpers

        int _fireId, _hellId;
        double _fireLaunchAt;
        float _escapeAt = -1f, _hellNextPoint, _hellUntil;
        Vector3 _hellTip, _hellDir;

        void OnFireballCharging(int id, ulong caster, double start)
        {
            if (_pc == null || caster != _pc.NetworkObjectId) return;
            _fireId = id;
            _fireLaunchAt = start + SpellPlans.FireballCharge;
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
                            _hellNextPoint = 0.07f;
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
                    _pc.Motor.Knock(-fwd * SpellPlans.EscapeBack + Vector3.up * SpellPlans.EscapeUp, 0f);
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
        Combatant FindMissileTarget()
        {
            var ray = CameraRig.AimRay;
            Combatant best = null;
            float bestAngle = SpellPlans.MissileTargetAngle;
            foreach (var c in Combatant.Registry)
            {
                if (c == null || c == _pc || !c.IsAlive) continue;
                Vector3 chest = c.transform.position + Vector3.up * 0.3f;
                if ((chest - _pc.transform.position).magnitude > SpellPlans.MissileTargetRange) continue;
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
                case CardId.Tug:
                {
                    Vector3 from = TugOrigin();
                    Vector3 to = _pc.transform.position + Vector3.up * 0.4f;
                    if ((to - from).magnitude > 1.5f) _indicator.ShowTug(from, to);
                    else _indicator.Hide();
                    break;
                }
                default:
                    _indicator.Hide();
                    break;
            }
        }
    }
}
