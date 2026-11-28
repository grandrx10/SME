using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for The Hermit, The Hanged Man, Death, The Devil and The Tower (Wheel of Fortune: .Wheel, Justice: .Justice).
    ///  - The Hermit: a house of ground builds round you in 0.3 s, at once (knocked down or not): the walls rise from below,
    ///    the roof comes down from above. A door on the side you face.
    ///  - The Hanged Man (10 health): a noose drops out of the sky (0.3 s; castable while knocked down, it stands you up) and
    ///    yanks you 10 m up by the neck in 0.2 s, then lets go. Nothing throws you meanwhile. The rope and the body break
    ///    whatever breakable voxels they meet.
    ///  - Death: see SpellServer.Death.
    ///  - The Devil: 20 health for 3 mana (the right hand up 0.2 s, bleeding).
    ///  - The Tower: kneeling in prayer 0.7 s (nothing moves you; hurt meanwhile: no tower), then a 5 x 5 x 15 m castle tower rises out
    ///    of the ground over 2 s, turns to face the spot under your cursor and topples that way, deleting every voxel in its way and hitting anyone
    ///    it falls on for 60. It can't be broken until it lands, then it cracks into pieces.
    /// </summary>
    public static partial class SpellServer
    {
        class DevilState { public PlayerController caster; public double at; }
        class TowerState
        {
            public int id; public PlayerController caster; public Vector3Int baseCell; public double prayUntil, start = -1;
            public float turnAt = -1f, lastAngle, turnBy; public Vector3 dir, foot;
            public bool aimed; public Vector3 aim; // where the caster's cursor landed (it turns to face it)
            public readonly HashSet<Combatant> hit = new();
        }

        static readonly List<DevilState> DevilsS = new();
        static readonly List<TowerState> TowersS = new();

        static SpellServer()
        {
            Combatant.Damaged = OnDamaged;
            Combatant.Killed = OnKilled;
            Combatant.DamageScale ??= ScaleDamage;
        }

        static void ResetArcana()
        {
            ResetJustice(); DevilsS.Clear(); TowersS.Clear();
            ResetDeath();
            ResetWheel();
            _shoved.Clear();
        }

        static bool CastArcana(PlayerController caster, CardId card, Vector3 target, float yaw, ulong targetId, VoxelWorld world, double now)
        {
            switch (card)
            {
                case CardId.TheHermit:
                    return BuildHouse(caster, yaw, world, now);
                case CardId.WheelOfFortune:
                    return CastWheel(caster, now);
                case CardId.Justice:
                    return CastJustice(caster, targetId, now);
                case CardId.TheHangedMan:
                {
                    // a noose comes down from the sky (breaking whatever's above in its way), yanks you up, and lets go
                    var st = caster.Status.Value;
                    if (now < st.HangUntil) return false; // still being yanked
                    st.HangFrom = now + SpellPlans.HangedDrop;
                    st.HangUntil = st.HangFrom + SpellPlans.HangedYank;
                    caster.Status.Value = st;
                    BreakRope(caster);
                    return true;
                }
                case CardId.Death:
                    return CastDeath(caster, now);
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
            TickWheel(now);
            TickJustice(now);
            TickBloat(now);
            TickDeaths(now);
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
                foreach (var b in BreathsS.ToArray()) if (b.caster == pc) EndBreath(b);
                foreach (var h in HooksS.ToArray()) if (h.caster == pc) EndHook(h);
                foreach (var d in Domains.ToArray()) if (d.caster == pc) End(d);
                DomainRested.Remove((pc, Empress));
                DomainRested.Remove((pc, Emperor));
                JusticesS.RemoveAll(j => j.caster == pc);
                ForgetDeath(pc);
                DevilsS.RemoveAll(d => d.caster == pc);
                RevivesS.RemoveAll(r => r.caster == pc);
                SkinsS.RemoveAll(s => s.caster == pc);
                BitesS.RemoveAll(b => b.biter == pc);
            }
            ClearWheel(pc, In);
            ClearTarot3(pc, In);
            ClearGuns(pc, In);
            ClearBombs(pc, In);
            ClearHex(pc, In);
            ClearBotany(pc, In);
            ClearGale(pc, In);
            ClearArms(pc, In);
            TowersS.RemoveAll(t => t.caster == pc || In(t.foot) || t.start < 0 && In(VoxelWorld.Current != null ? VoxelWorld.Current.CellCenter(t.baseCell) : Vector3.zero));
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

        /// <summary>Nothing moves this body: kneeling in prayer for The Tower, or charging with The Chariot (until the charge ends).</summary>
        public static bool Unmovable(Combatant c)
        {
            if (c == null || !c.IsSpawned) return false;
            double now = WorldNet.Now;
            if (Charging(c)) return true;
            if (Spinning(c)) return true; // mid Hammer Spin
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
            if (HoldingPot(pc)) return true; // a pot in both hands
            if (Submerged(pc)) return true;  // under Submerge's pool
            if (RifleOut(pc)) return true; // charging Sebastian
            return false;
        }

        /// <summary>The owner pressed a running card again: the Grapple Hook lets go (free).</summary>
        public static void CancelCard(PlayerController pc, CardId card)
        {
            if (pc == null) return;
            if (card == CardId.GrappleHook) foreach (var h in HooksS.ToArray()) if (h.caster == pc) EndHook(h);
            if (card == CardId.FlyTrap) ThrowPot(pc, SpellPlans.PotFlyTrap);
            if (card == CardId.PineTree) ThrowPot(pc, SpellPlans.PotPine);
            if (card == CardId.Submerge) CancelSubmerge(pc);
            if (card == CardId.TheHangedMan) EndHang(pc);
        }

        /// <summary>
        /// Mana drains while The Lovers' tether holds (1 a second, not refilling); the Flame Thrower takes 2 a second while it
        /// goes on refilling.
        /// </summary>
        static void RefreshFlow(PlayerController pc)
        {
            if (pc == null || !pc.IsSpawned) return;
            double now = WorldNet.Now;
            byte flow = ManaState.Refill;
            if (LoversS.Exists(l => l.caster == pc && l.tethered)) flow = ManaState.Draining;

            if (BreathsS.Exists(b => b.caster == pc && b.kind == FlameKind && b.going)) flow = ManaState.Channel;

            if (ChariotsS.Exists(c => c.caster == pc)) flow = ManaState.Held;
            if (SubsS.Exists(s => s.caster == pc)) flow = ManaState.Sunk; // under Submerge (or diving in): no regeneration
            if (pc.Mana.Value.Flow != flow) pc.Mana.Value = pc.Mana.Value.WithFlow(now, flow);
        }

        /// <summary>Knocked down mid-cast: Justice, a Devil's mana, all lost.</summary>
        static void FizzleArcana(PlayerController pc)
        {
            JusticesS.RemoveAll(j => j.caster == pc);
            DevilsS.RemoveAll(d => d.caster == pc);
            EndHang(pc);
        }

        /// <summary>
        /// Hurt while praying for The Tower: no tower. Knocked down: Death's charge ends.
        /// </summary>
        static void InterruptArcana(PlayerController pc, bool knocked)
        {
            if (TowersS.RemoveAll(t => t.caster == pc && t.start < 0) > 0) WorldNet.ServerFizzle(pc.NetworkObjectId);
            InterruptDeath(pc, knocked);
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
                    Hit(o, me as PlayerController, 0f, o.transform.position, away * SpellPlans.StrengthKnock + Vector3.up * SpellPlans.StrengthKnockUp, SpellPlans.StrengthRagdoll, from: p, card: CardId.Strength, striker: me);
                }
            }
            if (_shoved.Count > 64) _shoved.Clear();
        }

        // ------------------------------------------------------------------ The Hanged Man

        static double _nextHangCheck;

        /// <summary>The rope comes down from the sky straight through anything breakable above the caster, and the body's way up is cleared.</summary>
        static void BreakRope(Combatant c)
        {
            var w = VoxelWorld.Current;
            if (w == null) return;
            Vector3 head = c.transform.position + Vector3.up * 0.7f;
            var lo = w.CellOf(head + new Vector3(-0.2f, 0f, -0.2f));
            var hi = w.CellOf(head + new Vector3(0.2f, SpellPlans.HangedRopeUp, 0.2f));
            if (AnyBreakable(w, lo, hi)) WorldNet.ServerRemoveBox(lo, hi, 1.5f);
            Vector3 feet = c.FeetPosition;
            lo = w.CellOf(feet + new Vector3(-0.45f, 0.1f, -0.45f));
            hi = w.CellOf(feet + new Vector3(0.45f, 2f + SpellPlans.HangedLift, 0.45f));
            if (AnyBreakable(w, lo, hi)) WorldNet.ServerRemoveBox(lo, hi, 2f);
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
        /// Being yanked up: whatever the body and the rope just over it touch breaks (looking ahead: the owner's body is a
        /// moment further up than the server sees it).
        /// </summary>
        static void TickHanged(double now)
        {
            bool check = now >= _nextHangCheck;
            if (check) _nextHangCheck = now + 0.05;
            var w = VoxelWorld.Current;
            if (!check || w == null) return;
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || !c.IsAlive || !c.Status.Value.Hanging(now)) continue;
                Vector3 feet = c.FeetPosition;
                var lo = w.CellOf(feet + new Vector3(-0.45f, 0f, -0.45f));
                var hi = w.CellOf(feet + new Vector3(0.45f, 2f + SpellPlans.HangedRise * 0.1f, 0.45f));
                if (AnyBreakable(w, lo, hi)) WorldNet.ServerRemoveBox(lo, hi, 2f);
            }
        }

        // ------------------------------------------------------------------ The Hermit

        static bool BuildHouse(PlayerController caster, float yaw, VoxelWorld w, double now)
        {
            var plan = SpellPlans.PlanHouse(w, SpellPlans.HouseFeet(caster), yaw);
            if (!plan.valid) return false;
            StripBarrierCells(plan.walls, plan.wallValues);
            StripBarrierCells(plan.roof, plan.roofValues);
            int walls = WorldNet.ServerBody(plan.walls, plan.wallValues, SpellPlans.HouseHeight + 1, SpellPlans.HermitBuild);
            int roof = WorldNet.ServerBody(plan.roof, plan.roofValues, -SpellPlans.HouseRoofDrop, SpellPlans.HermitBuild);
            if (walls != 0) Walls.Add(new PendingWall { id = walls, mergeAt = now + SpellPlans.HermitBuild + 0.05 });
            if (roof != 0) Walls.Add(new PendingWall { id = roof, mergeAt = now + SpellPlans.HermitBuild + 0.05 });
            return true;
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
