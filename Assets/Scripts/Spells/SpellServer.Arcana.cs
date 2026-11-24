using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for The Hermit, Wheel of Fortune, Justice, The Hanged Man, Death, The Devil and The Tower.
    ///  - The Hermit: down on one knee 0.3 s (rooted), then a house of ground builds round you in 0.3 s: the walls rise from
    ///    below, the roof comes down from above. A door on the side you face. Knocked down while kneeling: no house.
    ///  - Wheel of Fortune: a mahogany die rolls over your head (no other cards meanwhile), shows its number, then:
    ///    1 everyone burns, the ground round you singed; 2 you're flung straight up; 3 the die is thrown like Rock Barrage's rock (30); 4 Mana Siphon at
    ///    double strength; 5 metallic grey, unmovable for 5 s; 6 golden purple, unhurtable for 3 s, leaving golden afterimages; 0 (1%) you flash red
    ///    faster and faster for 5 s, then blow up: dead, 150 to anyone within 2 m, 40 within 5 m.
    ///  - Justice: hands clasped 0.3 s, then a golden mahogany tether to the target for 6 s (snaps past 20 m, or when a wall comes between them). Healing the
    ///    target gets is given to the caster too; damage the caster takes, the target takes too.
    ///  - The Hanged Man: a noose drops out of the sky (0.3 s; castable while knocked down) and lifts you by the neck, steadily,
    ///    until you press it again: 10 damage a second, no walking or being thrown (turning and casting still work). The
    ///    rope and the body break whatever breakable voxels they meet.
    ///  - Death: the head tipped back 0.3 s, then a 4 m aura follows the caster. Others in it are hit once a second: 2, then
    ///    4, 8, 16... while they stay (back to 2 once they leave). Drains 1 mana a second; ends when the caster is knocked down,
    ///    out of mana, or presses it again.
    ///  - The Devil: 25 health for 3 mana (the right hand up 0.2 s, bleeding).
    ///  - The Tower: kneeling in prayer 0.7 s (nothing moves you; hurt meanwhile: no tower), then a 5 x 5 x 15 m castle tower rises out
    ///    of the ground over 2 s, turns to face the spot under your cursor and topples that way, deleting every voxel in its way and hitting anyone
    ///    it falls on for 60. It can't be broken until it lands, then it cracks into pieces.
    /// </summary>
    public static partial class SpellServer
    {
        class HermitState { public PlayerController caster; public Vector3 feet; public float yaw; public double buildAt; }
        class WheelState { public int id; public PlayerController caster; public double start; public byte result; }
        class DieState { public int id; public PlayerController caster; public Vector3 lastPos; public double launched; }
        class JusticeState { public int id; public PlayerController caster; public Combatant target; public double start; public bool tethered; }
        class DeathState
        {
            public int id; public PlayerController caster; public double formAt; public bool formed;
            public readonly Dictionary<Combatant, double> since = new();
            public readonly Dictionary<Combatant, int> ticks = new(); // how many times it has hit each one since they came in
        }
        class DevilState { public PlayerController caster; public double at; }
        class TowerState
        {
            public int id; public PlayerController caster; public Vector3Int baseCell; public double prayUntil, start = -1;
            public float turnAt = -1f, lastAngle, turnBy; public Vector3 dir, foot;
            public bool aimed; public Vector3 aim; // where the caster's cursor landed (it turns to face it)
            public readonly HashSet<Combatant> hit = new();
        }

        static readonly List<HermitState> HermitsS = new();
        static readonly List<WheelState> WheelsS = new();
        static readonly List<DieState> DiceS = new();
        static readonly List<JusticeState> JusticesS = new();
        static readonly List<DeathState> DeathsS = new();
        static readonly List<DevilState> DevilsS = new();
        static readonly List<TowerState> TowersS = new();
        static bool _mirroring;

        static SpellServer()
        {
            Combatant.Damaged = OnDamaged;
            Combatant.Healed = OnHealed;
            Combatant.Killed = OnKilled;
            Combatant.DamageScale ??= ScaleDamage;
        }

        static void ResetArcana()
        {
            HermitsS.Clear(); WheelsS.Clear(); DiceS.Clear(); JusticesS.Clear(); DeathsS.Clear(); DevilsS.Clear(); TowersS.Clear();
            _mirroring = false;
            _hangOwed.Clear();
            _shoved.Clear();
        }

        static bool CastArcana(PlayerController caster, CardId card, Vector3 target, float yaw, ulong targetId, VoxelWorld world, double now)
        {
            switch (card)
            {
                case CardId.TheHermit:
                {
                    if (!SpellPlans.PlanHouse(world, caster.FeetPosition, yaw).valid) return false;
                    HermitsS.RemoveAll(h => h.caster == caster);
                    HermitsS.Add(new HermitState { caster = caster, feet = caster.FeetPosition, yaw = yaw, buildAt = now + SpellPlans.HermitKneel });
                    return true;
                }
                case CardId.WheelOfFortune:
                {
                    if (WheelsS.Exists(w => w.caster == caster)) return false;
                    float r = Random.value * 100f;
                    byte result = r < 1f ? (byte)0 : (byte)Mathf.Clamp(1 + (int)((r - 1f) / 16.5f), 1, 6);
                    double start = now + SpellPlans.WheelCast;
                    int id = WorldNet.ServerWheel(caster.NetworkObjectId, start, result);
                    WheelsS.Add(new WheelState { id = id, caster = caster, start = start, result = result });
                    return true;
                }
                case CardId.Justice:
                {
                    var t = WorldNet.FindCombatant(targetId);
                    if (t == null || t == caster || !t.IsAlive) return false;
                    if ((t.transform.position - caster.transform.position).magnitude > Cards.MaxRange + SpellPlans.JusticePick + 4f) return false;
                    JusticesS.RemoveAll(j => j.caster == caster && !j.tethered);
                    JusticesS.Add(new JusticeState { caster = caster, target = t, start = now + SpellPlans.JusticeClasp });
                    return true;
                }
                case CardId.TheHangedMan:
                {
                    // a noose comes down from the sky (breaking whatever's above in its way), and hangs you until you let go
                    var st = caster.Status.Value;
                    if (now < st.HangUntil) return false; // already hanging (pressing it again lets go instead)
                    st.HangFrom = now + SpellPlans.HangedDrop;
                    st.HangUntil = now + SpellPlans.HangedForever;
                    caster.Status.Value = st;
                    BreakRope(caster);
                    _hangOwed.Remove(caster);
                    return true;
                }
                case CardId.Death:
                {
                    if (DeathsS.Exists(d => d.caster == caster)) return false;
                    var d = new DeathState { caster = caster, formAt = now + SpellPlans.DeathDelay };
                    d.id = WorldNet.ServerDeathAura(caster.NetworkObjectId, d.formAt);
                    DeathsS.Add(d);
                    return true;
                }
                case CardId.TheDevil:
                    DevilsS.Add(new DevilState { caster = caster, at = now + SpellPlans.DevilRaise });
                    WorldNet.ServerNecroFx(2, caster.NetworkObjectId, 0, caster.transform.position, now);
                    return true;
                case CardId.Strength:
                {
                    // flexing, then transformed (not again while it's still going, flex included)
                    var st = caster.Status.Value;
                    if (now < st.StrongUntil) return false;
                    st.StrongFrom = now + SpellPlans.StrengthFlex;
                    st.StrongUntil = st.StrongFrom + SpellPlans.StrengthTime;
                    caster.Status.Value = st;
                    return true;
                }
                case CardId.TheTower:
                {
                    if (!SpellPlans.PlanTower(world, target, out var baseCell)) return false;
                    if (TowersS.Exists(t => t.caster == caster && t.start < 0)) return false;
                    TowersS.Add(new TowerState { caster = caster, baseCell = baseCell, prayUntil = now + SpellPlans.TowerPray });
                    return true;
                }
            }
            return false;
        }

        static void TickArcana(double now, float dt)
        {
            TickHermits(now);
            TickWheels(now);
            TickDice(now);
            TickDoom(now);
            TickJustice(now);
            TickDeaths(now, dt);
            TickDevils(now);
            TickTowers(now);
            TickHanged(now);
            TickStrong(now);
        }

        // ------------------------------------------------------------------ shared

        /// <summary>
        /// Server: a practice chamber is being reset. Whatever its player still has going stops (an aura, a breath, a hook,
        /// a tether, a die, a float), and towers and rock swarms in it are dropped (their ops would land in the fresh chamber).
        /// </summary>
        public static void ServerClearPractice(PlayerController pc, int slot, float minX, float maxX)
        {
            bool In(Vector3 p) => p.x >= minX && p.x < maxX;
            if (pc != null)
            {
                foreach (var d in DeathsS.ToArray()) if (d.caster == pc) EndDeath(d);
                foreach (var b in BreathsS.ToArray()) if (b.caster == pc) EndBreath(b);
                foreach (var h in HooksS.ToArray()) if (h.caster == pc) EndHook(h);
                foreach (var j in JusticesS.ToArray())
                    if (j.caster == pc || j.target == pc)
                    {
                        JusticesS.Remove(j);
                        if (j.tethered) WorldNet.ServerJusticeEnd(j.id, pc.transform.position);
                    }
                WheelsS.RemoveAll(w => w.caster == pc);
                HermitsS.RemoveAll(h => h.caster == pc);
                DevilsS.RemoveAll(d => d.caster == pc);
                RevivesS.RemoveAll(r => r.caster == pc);
                SkinsS.RemoveAll(s => s.caster == pc);
                BitesS.RemoveAll(b => b.biter == pc);
            }
            ClearTarot3(pc, In);
            ClearGuns(pc, In);
            ClearBombs(pc, In);
            ClearHex(pc, In);
            TowersS.RemoveAll(t => t.caster == pc || In(t.foot) || t.start < 0 && In(VoxelWorld.Current != null ? VoxelWorld.Current.CellCenter(t.baseCell) : Vector3.zero));
            DiceS.RemoveAll(d => d.caster == pc);
            var net = WorldNet.Instance;
            for (int i = SwarmsS.Count - 1; i >= 0; i--)
            {
                var s = SwarmsS[i];
                if (s.caster != pc && !(net != null && net.Swarms.TryGetValue(s.id, out var fx) && In(fx.center))) continue;
                foreach (var c in s.catches) Chased.Remove(Key(c.shot));
                SwarmsS.RemoveAt(i);
            }
            InfectsS.RemoveAll(f => f.victim is TestDummy d && d.PracticeSlot == slot);
        }

        /// <summary>The caster's owner says where their cursor landed, for their tower to turn and face.</summary>
        public static void TowerAim(PlayerController pc, Vector3 point)
        {
            foreach (var t in TowersS)
                if (t.caster == pc && t.turnAt < 0f && (point - t.foot).magnitude < 400f) { t.aim = point; t.aimed = true; }
        }

        /// <summary>The practice chamber a body is in (0: the match).</summary>
        static int Room(Combatant c) => c switch
        {
            PlayerController p => p.PracticeSlot.Value,
            TestDummy d => d.PracticeSlot,
            _ => 0,
        };

        /// <summary>Nothing moves this body: turned to steel by the Wheel, kneeling in prayer for The Tower, or charging with The Chariot (until the charge ends).</summary>
        public static bool Unmovable(Combatant c)
        {
            if (c == null || !c.IsSpawned) return false;
            double now = WorldNet.Now;
            if (c.Status.Value.Steel(now)) return true;
            if (Charging(c)) return true;
            foreach (var t in TowersS) if (t.caster == c && now < t.prayUntil) return true;
            return false;
        }

        /// <summary>Is this body hanging from The Hanged Man's noose right now (nothing moves it)?</summary>
        public static bool Hanging(Combatant c) => c != null && c.IsSpawned && c.Status.Value.Hanging(WorldNet.Now);

        /// <summary>Server: The Hanged Man is let go (pressed again, or the noose never caught): it's over.</summary>
        public static void EndHang(Combatant c)
        {
            if (c == null || !c.IsSpawned) return;
            var st = c.Status.Value;
            double now = WorldNet.Now;
            if (now >= st.HangUntil) return;
            st.HangFrom = st.HangUntil = now;
            c.Status.Value = st;
        }

        /// <summary>The die is over their head, or the Flame Thrower is going: no other card until it's done.</summary>
        public static bool BusyChanneling(PlayerController pc)
        {
            double now = WorldNet.Now;
            foreach (var w in WheelsS) if (w.caster == pc && now < w.start + SpellPlans.WheelRoll + SpellPlans.WheelShow) return true;
            foreach (var b in BreathsS) if (b.caster == pc && b.kind == FlameKind) return true;
            if (RifleOut(pc)) return true; // charging Sebastian
            return false;
        }

        /// <summary>The owner pressed a running card again: Death's aura ends, the Grapple Hook lets go (free).</summary>
        public static void CancelCard(PlayerController pc, CardId card)
        {
            if (pc == null) return;
            if (card == CardId.Death) foreach (var d in DeathsS.ToArray()) if (d.caster == pc) EndDeath(d);
            if (card == CardId.GrappleHook) foreach (var h in HooksS.ToArray()) if (h.caster == pc) EndHook(h);
            if (card == CardId.TheHangedMan) EndHang(pc);
        }

        /// <summary>
        /// Mana drains while Death's aura or the Flame Thrower runs (2 a second), or The Lovers' tether holds (1); otherwise
        /// it refills.
        /// </summary>
        static void RefreshFlow(PlayerController pc)
        {
            if (pc == null || !pc.IsSpawned) return;
            double now = WorldNet.Now;
            byte flow = ManaState.Refill;
            if (LoversS.Exists(l => l.caster == pc && l.tethered)) flow = ManaState.Draining;
            if (DeathsS.Exists(d => d.caster == pc && d.formed)) flow = ManaState.Draining;          // Death: 1 a second
            if (BreathsS.Exists(b => b.caster == pc && b.kind == FlameKind)) flow = ManaState.DrainingFast; // the Flame Thrower: 2
            if (ChariotsS.Exists(c => c.caster == pc)) flow = ManaState.Held;
            if (pc.Mana.Value.Flow != flow) pc.Mana.Value = pc.Mana.Value.WithFlow(now, flow);
        }

        /// <summary>Knocked down mid-cast: a house still to be built, a tether still to sprout, a Devil's mana, all lost.</summary>
        static void FizzleArcana(PlayerController pc)
        {
            HermitsS.RemoveAll(h => h.caster == pc);
            JusticesS.RemoveAll(j => j.caster == pc && !j.tethered);
            DevilsS.RemoveAll(d => d.caster == pc);
            EndHang(pc);
        }

        /// <summary>
        /// Hurt while praying for The Tower: no tower. Knocked down: Death's aura ends (also while the head is still tipped back).
        /// </summary>
        static void InterruptArcana(PlayerController pc, bool knocked)
        {
            if (TowersS.RemoveAll(t => t.caster == pc && t.start < 0) > 0) WorldNet.ServerFizzle(pc.NetworkObjectId);
            if (!knocked) return;
            foreach (var d in DeathsS.ToArray()) if (d.caster == pc) EndDeath(d);
        }

        // ------------------------------------------------------------------ Strength

        static readonly Dictionary<(Combatant, Combatant), double> _shoved = new();

        /// <summary>Transformed: any enemy the grown body walks into is knocked away and ragdolled (each one at most every so often).</summary>
        static void TickStrong(double now)
        {
            foreach (var me in Combatant.Registry.ToArray())
            {
                if (me == null || !me.IsAlive || !me.Status.Value.Strong(now)) continue;
                Vector3 p = me.transform.position;
                float reach = SpellPlans.StrengthBump * me.transform.localScale.y;
                foreach (var o in Combatant.Registry.ToArray())
                {
                    if (o == null || o == me || !o.IsAlive || Combatant.Allies(me, o) || Shielded(o) || Folded(o)) continue;
                    Vector3 d = o.transform.position - p;
                    if (d.y < -2.2f || d.y > 2.4f || new Vector2(d.x, d.z).magnitude > reach) continue;
                    if (_shoved.TryGetValue((me, o), out double at) && now < at + SpellPlans.StrengthBumpEvery) continue;
                    _shoved[(me, o)] = now;
                    Vector3 away = new Vector3(d.x, 0f, d.z);
                    away = away.sqrMagnitude > 0.01f ? away.normalized : me.transform.forward;
                    Hit(o, me as PlayerController, 0f, o.transform.position, away * SpellPlans.StrengthKnock + Vector3.up * SpellPlans.StrengthKnockUp, SpellPlans.StrengthRagdoll);
                }
            }
            if (_shoved.Count > 64) _shoved.Clear();
        }

        // ------------------------------------------------------------------ The Hanged Man

        static readonly Dictionary<Combatant, float> _hangOwed = new();
        static double _nextHangCheck;

        /// <summary>The rope comes down from the sky straight through anything breakable above the caster.</summary>
        static void BreakRope(Combatant c)
        {
            var w = VoxelWorld.Current;
            if (w == null) return;
            Vector3 head = c.transform.position + Vector3.up * 0.7f;
            var lo = w.CellOf(head + new Vector3(-0.2f, 0f, -0.2f));
            var hi = w.CellOf(head + new Vector3(0.2f, SpellPlans.HangedRopeUp, 0.2f));
            if (AnyBreakable(w, lo, hi)) WorldNet.ServerRemoveBox(lo, hi, 1.5f);
        }

        static bool AnyBreakable(VoxelWorld w, Vector3Int lo, Vector3Int hi)
        {
            for (int x = lo.x; x <= hi.x; x++)
            for (int y = lo.y; y <= hi.y; y++)
            for (int z = lo.z; z <= hi.z; z++)
            {
                ushort v = w.Get(x, y, z);
                if (v != 0 && !w.IsIndestructible(v)) return true;
            }
            return false;
        }

        /// <summary>
        /// Hanging: 10 damage a second, and whatever the body and the rope just over it touch breaks (looking a little
        /// ahead: the owner's body is a moment further up than the server sees it).
        /// </summary>
        static void TickHanged(double now)
        {
            bool check = now >= _nextHangCheck;
            if (check) _nextHangCheck = now + 0.1;
            var w = VoxelWorld.Current;
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || !c.IsAlive || !c.Status.Value.Hanging(now)) { if (c != null) _hangOwed.Remove(c); continue; }
                _hangOwed.TryGetValue(c, out float owed);
                owed += SpellPlans.HangedDamage * Time.deltaTime;
                if (owed >= SpellPlans.HangedDamage * SpellPlans.HangedTick)
                {
                    ulong self = c is PlayerController p ? p.OwnerClientId : Combatant.NoAttacker;
                    c.ServerApplyDamage(owed, self, c.transform.position + Vector3.up * 0.7f, Vector3.up, -1, 0f, DeathCause.Spell);
                    owed = 0f;
                }
                _hangOwed[c] = owed;
                if (!check || w == null || !c.IsAlive) continue;
                Vector3 feet = c.FeetPosition;
                var lo = w.CellOf(feet + new Vector3(-0.45f, 0f, -0.45f));
                var hi = w.CellOf(feet + new Vector3(0.45f, 2f + SpellPlans.HangedRise * 0.6f, 0.45f));
                if (AnyBreakable(w, lo, hi)) WorldNet.ServerRemoveBox(lo, hi, 2f);
            }
        }

        // ------------------------------------------------------------------ Justice: shared damage and healing

        static void OnDamaged(Combatant victim, ulong attacker, float dmg)
        {
            if (_mirroring || dmg <= 0f || JusticesS.Count == 0) return;
            foreach (var j in JusticesS.ToArray())
            {
                if (!j.tethered || j.caster != victim || j.target == null || !j.target.IsAlive) continue;
                _mirroring = true;
                // tethered to a teammate: half (a hit by a teammate of theirs is halved as it lands)
                float share = dmg;
                if (Combatant.Teammates(j.caster, j.target) && !Combatant.Teammates(PlayerController.ServerFind(attacker), j.target)) share *= SpellPlans.TeamDamage;
                try { j.target.ServerApplyDamage(share, attacker, j.target.transform.position, Vector3.up, -1, 0f, DeathCause.Spell); }
                finally { _mirroring = false; }
            }
        }

        static void OnHealed(Combatant who, float amount, float seconds)
        {
            if (_mirroring || JusticesS.Count == 0) return;
            foreach (var j in JusticesS.ToArray())
            {
                if (!j.tethered || j.target != who || j.caster == null || !j.caster.IsAlive) continue;
                _mirroring = true;
                try { j.caster.ServerHeal(amount, seconds); }
                finally { _mirroring = false; }
            }
        }

        // ------------------------------------------------------------------ The Hermit

        static void TickHermits(double now)
        {
            var w = VoxelWorld.Current;
            for (int i = HermitsS.Count - 1; i >= 0; i--)
            {
                var h = HermitsS[i];
                if (now < h.buildAt) continue;
                HermitsS.RemoveAt(i);
                if (h.caster == null || !h.caster.IsAlive || w == null) continue;
                var plan = SpellPlans.PlanHouse(w, h.feet, h.yaw);
                if (!plan.valid) continue;
                StripBarrierCells(plan.walls, plan.wallValues);
                StripBarrierCells(plan.roof, plan.roofValues);
                int walls = WorldNet.ServerBody(plan.walls, plan.wallValues, SpellPlans.HouseHeight + 1, SpellPlans.HermitBuild);
                int roof = WorldNet.ServerBody(plan.roof, plan.roofValues, -SpellPlans.HouseRoofDrop, SpellPlans.HermitBuild);
                if (walls != 0) Walls.Add(new PendingWall { id = walls, mergeAt = now + SpellPlans.HermitBuild + 0.05 });
                if (roof != 0) Walls.Add(new PendingWall { id = roof, mergeAt = now + SpellPlans.HermitBuild + 0.05 });
            }
        }

        // ------------------------------------------------------------------ Wheel of Fortune

        static void TickWheels(double now)
        {
            for (int i = WheelsS.Count - 1; i >= 0; i--)
            {
                var wh = WheelsS[i];
                if (now < wh.start + SpellPlans.WheelRoll + SpellPlans.WheelShow) continue;
                WheelsS.RemoveAt(i);
                var me = wh.caster;
                if (me == null || !me.IsAlive) continue;
                Vector3 die = me.transform.position + Vector3.up * SpellPlans.DieAbove;
                switch (wh.result)
                {
                    case 1: // everyone set alight (everyone in the same match, or the same practice chamber)
                        foreach (var c in Combatant.Registry.ToArray())
                            if (c != null && c.IsAlive && Room(c) == Room(me)) Burn(c, me);
                        // a burst of flame round the caster (three quarters the size it was), the ground round them singed
                        Scorch(me.FeetPosition + Vector3.up * 0.1f, 0, SpellPlans.R4(SpellPlans.WheelSinge), 0f);
                        WorldNet.ServerFlameBurst(me.transform.position, 0.9f);
                        FlameSetsOff(me.transform.position, SpellPlans.WheelSinge);
                        break;
                    case 2: // flung straight up (smashing through whatever's above)
                        me.ServerFling(Vector3.up * SpellPlans.WheelFling, SpellPlans.WheelFlingRagdoll);
                        break;
                    case 3: // the die is thrown
                    {
                        Vector3 aim = Quaternion.Euler(me.ViewPitch, me.ViewYaw, 0f) * Vector3.forward;
                        float lift = 0.5f * Mathf.Asin(Mathf.Clamp01(SpellPlans.RockGravity * SpellPlans.RockZeroRange / (SpellPlans.RockSpeed * SpellPlans.RockSpeed)));
                        Vector3 dir = Vector3.RotateTowards(aim, Vector3.up, lift, 0f);
                        int id = WorldNet.ServerDieThrow(die, dir * SpellPlans.RockSpeed);
                        DiceS.Add(new DieState { id = id, caster = me, lastPos = die, launched = now });
                        break;
                    }
                    case 4: // Mana Siphon, twice as strong
                        Siphons.RemoveAll(s => s.caster == me);
                        Siphons.Add(new SiphonState { caster = me, start = now, mult = SpellPlans.WheelSiphonScale });
                        me.ServerStartSiphon();
                        break;
                    case 5:
                    {
                        var st = me.Status.Value;
                        st.SteelUntil = now + SpellPlans.SteelTime;
                        me.Status.Value = st;
                        break;
                    }
                    case 6:
                    {
                        var st = me.Status.Value;
                        st.GoldUntil = now + SpellPlans.GoldTime;
                        me.Status.Value = st;
                        break;
                    }
                    default: // 0: doomed
                    {
                        var st = me.Status.Value;
                        st.DoomFrom = now;
                        st.DoomAt = now + SpellPlans.DoomTime;
                        me.Status.Value = st;
                        break;
                    }
                }
            }
        }

        static void TickDice(double now)
        {
            var net = WorldNet.Instance;
            if (net == null) return;
            for (int i = DiceS.Count - 1; i >= 0; i--)
            {
                var d = DiceS[i];
                if (!net.Dice.TryGetValue(d.id, out var fx)) { DiceS.RemoveAt(i); continue; }
                Vector3 pos = WorldNet.DiePosition(fx, now);
                ulong owner = d.caster != null ? d.caster.NetworkObjectId : ulong.MaxValue;
                Vector3 step = pos - d.lastPos;
                float dist = step.magnitude;
                var w = VoxelWorld.Current;
                if (BarrierBlocks(owner, pos, SpellPlans.DieRadius) || BubbleBlocking(owner, pos, SpellPlans.DieRadius) != null)
                {
                    WorldNet.ServerDieEnd(d.id, pos, default, 0);
                    DiceS.RemoveAt(i);
                    continue;
                }
                if (dist > 1e-4f && SweepBall(owner, d.lastPos, step / dist, dist, SpellPlans.DieRadius, out var hit, out var victim))
                {
                    if (victim != null)
                    {
                        Vector3 v = fx.velocity + Vector3.down * SpellPlans.RockGravity * (float)(now - fx.t);
                        Hit(victim, d.caster, SpellPlans.DieDamage, hit.point, (v.normalized * 9f + Vector3.up * 4f) * SpellPlans.RockKnockback, 0.8f);
                        WorldNet.ServerDieEnd(d.id, hit.point, default, 0);
                    }
                    else WorldNet.ServerDieEnd(d.id, hit.point, w != null ? w.CellOf(hit.point - hit.normal * 0.12f) : default, w != null ? 7 : 0);
                    DiceS.RemoveAt(i);
                    continue;
                }
                d.lastPos = pos;
                if ((pos - fx.origin).magnitude > Cards.MaxRange || pos.y < Practice.KillYAt(pos) || now - fx.t > 4.0)
                {
                    WorldNet.ServerDieEnd(d.id, pos, default, 0);
                    DiceS.RemoveAt(i);
                }
            }
        }

        /// <summary>A roll of 0: once the red flashing runs out, the caster blows up.</summary>
        static void TickDoom(double now)
        {
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || !c.IsSpawned) continue;
                var st = c.Status.Value;
                if (!st.Doomed || now < st.DoomAt) continue;
                st.DoomAt = st.DoomFrom = 0;
                c.Status.Value = st;
                if (!c.IsAlive) continue;
                Vector3 p = c.transform.position;
                var me = c as PlayerController;
                foreach (var o in Combatant.Registry.ToArray())
                {
                    if (o == null || o == c || !o.IsAlive) continue;
                    Vector3 d = o.transform.position - p;
                    float dist = d.magnitude;
                    if (dist > SpellPlans.DoomFar) continue;
                    float dmg = dist <= SpellPlans.DoomNear ? SpellPlans.DoomNearDamage : SpellPlans.DoomFarDamage;
                    Vector3 away = d.sqrMagnitude > 0.01f ? d.normalized : Vector3.up;
                    Hit(o, me, dmg, o.transform.position, away * 14f + Vector3.up * 7f, 1.2f);
                }
                Scorch(p, SpellPlans.R4(1.5f), SpellPlans.R4(3f), 10f);
                WorldNet.ServerFlameBurst(p, 2.2f);
                FlameSetsOff(p, SpellPlans.DoomFar);
                c.ServerKillByHazard(DeathCause.Spell, p);
            }
        }

        // ------------------------------------------------------------------ Justice

        static void TickJustice(double now)
        {
            for (int i = JusticesS.Count - 1; i >= 0; i--)
            {
                var j = JusticesS[i];
                bool ok = j.caster != null && j.caster.IsAlive && j.target != null && j.target.IsAlive;
                if (!j.tethered)
                {
                    if (now < j.start) continue;
                    if (!ok || (j.target.transform.position - j.caster.transform.position).magnitude > SpellPlans.JusticeBreak
                        || Physics.Linecast(SpellPlans.LoversChest(j.caster), SpellPlans.LoversChest(j.target), Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                    { JusticesS.RemoveAt(i); continue; }
                    j.tethered = true;
                    j.id = WorldNet.ServerJustice(j.caster.NetworkObjectId, j.target.NetworkObjectId, now);
                    continue;
                }
                Vector3 a = ok ? SpellPlans.LoversChest(j.caster) : Vector3.zero, b = ok ? SpellPlans.LoversChest(j.target) : Vector3.zero;
                // snaps when it runs out, past 20 m, or when a wall comes between the two
                if (!ok || now > j.start + SpellPlans.JusticeTime || (b - a).magnitude > SpellPlans.JusticeBreak
                    || Physics.Linecast(a, b, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                {
                    JusticesS.RemoveAt(i);
                    WorldNet.ServerJusticeEnd(j.id, ok ? Vector3.Lerp(a, b, 0.5f) : (j.caster != null ? j.caster.transform.position : Vector3.zero));
                }
            }
        }

        // ------------------------------------------------------------------ Death

        static void TickDeaths(double now, float dt)
        {
            for (int i = DeathsS.Count - 1; i >= 0; i--)
            {
                if (i >= DeathsS.Count) continue;
                var d = DeathsS[i];
                var me = d.caster;
                if (me == null || !me.IsAlive) { EndDeath(d); continue; }
                if (now < d.formAt) continue;
                if (!d.formed)
                {
                    d.formed = true;
                    RefreshFlow(me);
                }
                if (me.ManaNow <= 0.001f) { EndDeath(d); continue; }
                float r = SpellPlans.DeathRadius * Mathf.Clamp01((float)(now - d.formAt) / SpellPlans.DeathGrow);
                Vector3 c = me.FeetPosition;
                foreach (var o in Combatant.Registry.ToArray())
                {
                    if (o == null || o == me || !o.IsAlive) continue;
                    Vector3 rel = o.FeetPosition - c;
                    bool inside = rel.y > -2.5f && rel.y < 4f && new Vector2(rel.x, rel.z).magnitude <= r && !Combatant.Allies(me, o);
                    if (!inside)
                    {
                        // out of it: the doubling starts over
                        d.since.Remove(o);
                        d.ticks.Remove(o);
                        continue;
                    }
                    // one hit a second while they stay: 2, then 4, 8, 16...
                    if (!d.since.TryGetValue(o, out double since)) d.since[o] = since = now;
                    d.ticks.TryGetValue(o, out int done);
                    if (now - since < done + 1) continue;
                    d.ticks[o] = done + 1;
                    o.ServerApplyDamage(SpellPlans.DeathRate(done), me.OwnerClientId, o.transform.position, Vector3.up, -1, 0f, DeathCause.Spell);
                }
            }
        }

        static void EndDeath(DeathState d)
        {
            if (!DeathsS.Remove(d)) return;
            WorldNet.ServerDeathAuraEnd(d.id);
            RefreshFlow(d.caster);
        }

        // ------------------------------------------------------------------ The Devil

        static void TickDevils(double now)
        {
            for (int i = DevilsS.Count - 1; i >= 0; i--)
            {
                var d = DevilsS[i];
                if (now < d.at) continue;
                DevilsS.RemoveAt(i);
                var me = d.caster;
                if (me == null || !me.IsAlive) continue;
                me.Mana.Value = me.Mana.Value.With(now, Mathf.Min(me.Mana.Value.Limit, me.ManaNow + SpellPlans.DevilMana));
            }
        }

        // ------------------------------------------------------------------ The Tower

        static readonly HashSet<Vector3Int> _carve = new();

        static void TickTowers(double now)
        {
            var w = VoxelWorld.Current;
            for (int i = TowersS.Count - 1; i >= 0; i--)
            {
                var t = TowersS[i];
                if (t.start < 0)
                {
                    if (now < t.prayUntil) continue;
                    if (t.caster == null || !t.caster.IsAlive || w == null) { TowersS.RemoveAt(i); continue; }
                    var b = t.baseCell;
                    ushort wall = SpellPlans.GroundValue(w, b.x, b.y, b.z, false), trim = SpellPlans.GroundValue(w, b.x, b.y, b.z, true);
                    t.start = now;
                    t.foot = SpellPlans.TowerFoot(w, b);
                    t.id = WorldNet.ServerTower(t.caster.NetworkObjectId, b, now, wall, trim);
                    continue;
                }
                float age = (float)(now - t.start);
                if (t.turnAt < 0f)
                {
                    if (age < SpellPlans.TowerRise) continue;
                    // risen: it turns to face the spot its caster's cursor is on, and topples that way (a moment's wait for
                    // that spot to arrive; without it, the way they're looking)
                    Vector3 flat = t.aim - t.foot;
                    flat.y = 0f;
                    if (!t.aimed && age < SpellPlans.TowerRise + 0.3f) continue;
                    float aim = t.aimed && flat.sqrMagnitude > 0.25f ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg
                        : t.caster != null && t.caster.IsAlive ? t.caster.ViewYaw : 0f;
                    t.turnAt = age;
                    t.turnBy = SpellPlans.TowerTurnBy(aim);
                    t.dir = Quaternion.Euler(0f, aim, 0f) * Vector3.forward;
                    WorldNet.ServerTowerTurn(t.id, t.turnAt, t.turnBy, t.dir);
                    continue;
                }
                SpellPlans.TowerPose(t.foot, age, t.turnAt, t.turnBy, t.dir, out var pivot, out var rot, out float angle);
                if (angle > t.lastAngle)
                {
                    CarveTower(t, t.lastAngle, angle, w);
                    t.lastAngle = angle;
                }
                // whoever it comes down on
                if (angle > 1f)
                    foreach (var c in Combatant.Registry.ToArray())
                    {
                        if (c == null || !c.IsAlive || c == t.caster || t.hit.Contains(c) || Combatant.Allies(t.caster, c)) continue;
                        if (!SpellPlans.InTower(c.transform.position, pivot, rot, 0.4f) && !SpellPlans.InTower(c.transform.position + Vector3.up * 0.8f, pivot, rot, 0.4f)) continue;
                        t.hit.Add(c);
                        Hit(c, t.caster, SpellPlans.TowerDamage, c.transform.position, t.dir * SpellPlans.TowerKnock + Vector3.down * 4f, SpellPlans.TowerRagdoll);
                    }
                if (age >= t.turnAt + SpellPlans.TowerDown)
                {
                    WorldNet.ServerTowerBreak(t.id);
                    TowersS.RemoveAt(i);
                }
            }
        }

        /// <summary>Everything the tower's leading face swept through between two fall angles is deleted.</summary>
        static void CarveTower(TowerState t, float from, float to, VoxelWorld w)
        {
            if (w == null) return;
            _carve.Clear();
            float S = VoxelWorld.Size;
            Vector3 right = Vector3.Cross(Vector3.up, t.dir).normalized;
            Vector3 edge = t.foot + t.dir * (SpellPlans.TowerBase * S * 0.5f);
            int steps = Mathf.Clamp(Mathf.CeilToInt((to - from) / 0.5f), 1, 12);
            int half = SpellPlans.TowerBase / 2 + SpellPlans.TowerCapOver;
            int top = SpellPlans.TowerHeight + 3, capFrom = SpellPlans.TowerHeight - SpellPlans.TowerCapHeight;
            for (int s = 1; s <= steps; s++)
            {
                float angle = Mathf.Lerp(from, to, s / (float)steps);
                Quaternion fall = Quaternion.AngleAxis(angle, right);
                for (int h = 0; h <= top; h++)
                {
                    bool cap = h > capFrom;
                    float out0 = cap ? SpellPlans.TowerCapOver * S : 0f;
                    int width = cap ? half : SpellPlans.TowerBase / 2;
                    for (int a = -width; a <= width; a++)
                    {
                        Vector3 p0 = edge + t.dir * out0 + right * (a * S) + Vector3.up * (h * S + S * 0.5f);
                        Vector3 p = edge + fall * (p0 - edge);
                        var cell = w.CellOf(p);
                        if (_carve.Contains(cell)) continue;
                        ushort v = w.Get(cell);
                        if (v == 0 || w.IsIndestructible(v)) continue;
                        _carve.Add(cell);
                    }
                }
            }
            if (_carve.Count > 0) WorldNet.ServerTowerCarve(new List<Vector3Int>(_carve));
        }
    }
}
