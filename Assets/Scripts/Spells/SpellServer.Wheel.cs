using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for Wheel of Fortune. A mahogany die rolls over the caster's head (no other cards meanwhile), shows its
    /// number, then (0 and 7: 4% each, the others alike):
    ///  - 0: red for 0.3 s, then they blow up: 80 to them and everyone within 10 m, thrown hard, every breakable voxel gone.
    ///  - 1: thrown into the sky, limp and tumbling (it can't break a combo); where they come down, a 10.5 m blast: two burns, thrown (them too).
    ///  - 2: two Arcane Judgments somewhere within 15 m.
    ///  - 3: every surface within 10 m frosts over, one voxel deep, over 0.3 s, for good.
    ///  - 4: four tornados somewhere within 15 m, each going its own way, sparing nobody.
    ///  - 5: twelve of the volcano's meteors within 15 m (their kills; they don't hurt the caster).
    ///  - 6: Mana Siphon's dash at twice the speed, untouchable: no mana taken, 30 damage, thrown incredibly hard.
    ///  - 7: the die becomes three fused balls (Attraction and Repulsion) over their head, thrown at their aim one after another.
    /// </summary>
    public static partial class SpellServer
    {
        class WheelState { public int id; public PlayerController caster; public double start; public byte result; }
        class SkyState { public PlayerController who; public double start, landed = -1; public bool falling; }
        class MeteorDue { public PlayerController by; public Vector3 center; public double at; }
        class IceFloorState { public Vector3Int cell; public double start; public int step; }
        class BlastState { public Vector3Int cell; public double start; public int step; }

        static readonly List<WheelState> WheelsS = new();
        static readonly List<SkyState> SkiesS = new();
        static readonly List<MeteorDue> MeteorsDue = new();
        static readonly List<IceFloorState> IceFloors = new();
        static readonly List<BlastState> BlastsS = new();

        static void ResetWheel()
        {
            WheelsS.Clear();
            SkiesS.Clear();
            MeteorsDue.Clear();
            IceFloors.Clear();
            BlastsS.Clear();
        }

        /// <summary>A practice chamber is being reset.</summary>
        static void ClearWheel(PlayerController pc, System.Func<Vector3, bool> inside)
        {
            var w = VoxelWorld.Current;
            Vector3 At(Vector3Int c) => w != null ? w.CellCenter(c) : Vector3.zero;
            WheelsS.RemoveAll(x => x.caster == pc);
            SkiesS.RemoveAll(x => x.who == pc);
            MeteorsDue.RemoveAll(x => x.by == pc || inside(x.center));
            IceFloors.RemoveAll(x => inside(At(x.cell)));
            BlastsS.RemoveAll(x => inside(At(x.cell)));
        }

        static bool CastWheel(PlayerController caster, double now)
        {
            if (WheelsS.Exists(w => w.caster == caster)) return false;
            float r = Random.value * 100f;
            byte result = r < SpellPlans.WheelRare ? (byte)0 : r < SpellPlans.WheelRare * 2f ? (byte)7 : (byte)Random.Range(1, 7);
            double start = now + SpellPlans.WheelCast;
            int id = WorldNet.ServerWheel(caster.NetworkObjectId, start, result);
            WheelsS.Add(new WheelState { id = id, caster = caster, start = start, result = result });
            return true;
        }

        static void TickWheel(double now)
        {
            for (int i = WheelsS.Count - 1; i >= 0; i--)
            {
                var wh = WheelsS[i];
                if (now < wh.start + SpellPlans.WheelRoll + SpellPlans.WheelShow) continue;
                WheelsS.RemoveAt(i);
                if (wh.caster != null && wh.caster.IsAlive) Rolled(wh.caster, wh.result, now);
            }
            TickDoom(now);
            TickSkies(now);
            TickWheelGround(now);
            for (int i = MeteorsDue.Count - 1; i >= 0; i--)
            {
                if (now < MeteorsDue[i].at) continue;
                WheelMeteor(MeteorsDue[i]);
                MeteorsDue.RemoveAt(i);
            }
        }

        static void Rolled(PlayerController me, byte result, double now)
        {
            var world = VoxelWorld.Current;
            switch (result)
            {
                case 0:
                {
                    var st = me.Status.Value;
                    st.DoomFrom = now;
                    st.DoomAt = now + SpellPlans.DoomTime;
                    me.Status.Value = st;
                    break;
                }
                case 1:
                    if (Hanging(me) || Charging(me)) { SkyBlast(me); break; } // nothing throws them: it goes off here
                    SkiesS.RemoveAll(s => s.who == me);
                    SkiesS.Add(new SkyState { who = me, start = now });
                {
                    var st = me.Status.Value;
                    st.SpinUntil = now + SpellPlans.SkyMaxTime;
                    me.Status.Value = st;
                    me.ServerFling(Vector3.up * SpellPlans.SkyFling, SpellPlans.SkyMaxTime); // (up again as they land: SkyBlast)
                    break;
                }
                case 3:
                    if (world != null && GroundBelow(me.transform.position, 0f, 40f, out var floor))
                        IceFloors.Add(new IceFloorState { cell = world.CellOf(floor + Vector3.down * 0.1f), start = now });
                    break;
                case 2:
                    for (int k = 0; k < SpellPlans.WheelCircles; k++)
                        LooseJudgment(me, WheelSpot(me, Random.insideUnitCircle * SpellPlans.WheelAround), now);
                    break;
                case 4:
                    for (int k = 0; k < SpellPlans.WheelTornados; k++)
                    {
                        Vector3 dir = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
                        Vector3 at = WheelSpot(me, Random.insideUnitCircle * SpellPlans.WheelAround);
                        int id = WorldNet.ServerTornado(me.NetworkObjectId, at, dir);
                        Tornados.Add(new TornadoState { id = id, caster = me, origin = at, dir = dir, start = now, all = true });
                    }
                    break;
                case 5:
                    for (int k = 0; k < SpellPlans.WheelMeteors; k++)
                        MeteorsDue.Add(new MeteorDue { by = me, center = me.FeetPosition, at = now + k * SpellPlans.WheelMeteorEvery });
                    break;
                case 6:
                {
                    Siphons.RemoveAll(s => s.caster == me);
                    Siphons.Add(new SiphonState { caster = me, start = now, gold = true });
                    var st = me.Status.Value;
                    st.GoldUntil = now + SpellPlans.SiphonTime + 0.1;
                    me.Status.Value = st;
                    me.ServerStartSiphon();
                    break;
                }
                case 7:
                    for (byte k = 1; k <= SpellPlans.WheelFuses; k++)
                    {
                        int id = WorldNet.ServerFuse(me.NetworkObjectId, k);
                        FusesS.Add(new FuseState { id = id, caster = me, start = now, over = k });
                    }
                    break;
            }
        }

        // ------------------------------------------------------------------ where things happen

        static bool GroundBelow(Vector3 p, float up, float down, out Vector3 at)
        {
            at = p;
            if (!Physics.Raycast(p + Vector3.up * up, Vector3.down, out var hit, up + down, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) return false;
            at = hit.point;
            return true;
        }

        /// <summary>The ground this far across from the caster (or level with their feet, over a drop).</summary>
        static Vector3 WheelSpot(PlayerController me, Vector2 across)
        {
            Vector3 p = me.FeetPosition + new Vector3(across.x, 0f, across.y);
            return GroundBelow(p, 8f, 24f, out var at) ? at : p;
        }

        /// <summary>One of the volcano's meteors, on a random spot round where the wheel was rolled.</summary>
        static void WheelMeteor(MeteorDue m)
        {
            for (int tries = 0; tries < 6; tries++)
            {
                Vector2 off = Random.insideUnitCircle * SpellPlans.WheelAround, lean = Random.insideUnitCircle * SpellPlans.MeteorLean;
                Vector3 dir = new Vector3(lean.x, -1f, lean.y).normalized;
                if (!MeteorShower.FirstTouch(m.center + new Vector3(off.x, 0f, off.y) - dir * 60f, dir, out var at, out var normal)) continue;
                DropMeteor(at, normal, dir, m.by);
                return;
            }
        }

        // ------------------------------------------------------------------ 0: blown up

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
                WorldNet.ServerWheelBlast(0, p, SpellPlans.DoomRadius);
                FlameSetsOff(p, SpellPlans.DoomRadius);
                var w = VoxelWorld.Current;
                if (w != null) BlastsS.Add(new BlastState { cell = w.CellOf(p), start = now });
                foreach (var o in Combatant.Registry.ToArray())
                {
                    if (o == null || o == c || !o.IsAlive || Room(o) != Room(c) || Folded(o) || Shielded(o)) continue;
                    Vector3 d = o.transform.position - p;
                    if (d.magnitude > SpellPlans.DoomRadius) continue;
                    Vector3 away = d.sqrMagnitude > 0.01f ? d.normalized : Vector3.up;
                    Hit(o, me, SpellPlans.DoomDamage, o.transform.position, away * SpellPlans.DoomKnock + Vector3.up * SpellPlans.DoomKnockUp, SpellPlans.DoomRagdoll);
                }
                // and the one who rolled it
                Vector2 side = Random.insideUnitCircle.normalized;
                Vector3 self = new Vector3(side.x, 0f, side.y) * (SpellPlans.DoomKnock * 0.4f) + Vector3.up * SpellPlans.DoomKnockUp * 1.4f;
                ulong mine = me != null ? me.OwnerClientId : Combatant.NoAttacker;
                c.ServerApplyDamage(SpellPlans.DoomDamage, mine, p, self.normalized, -1, self.magnitude, DeathCause.Spell);
                if (c.IsAlive) c.ServerKnock(self, SpellPlans.DoomRagdoll);
            }
        }

        // ------------------------------------------------------------------ 1: into the sky, and back down

        /// <summary>Thrown by a roll of 1 (until a moment after they've landed): lying ragdolled doesn't count towards a combo break.</summary>
        public static bool SkyFlung(PlayerController pc)
        {
            foreach (var s in SkiesS) if (s.who == pc) return true;
            return false;
        }

        static void TickSkies(double now)
        {
            for (int i = SkiesS.Count - 1; i >= 0; i--)
            {
                var s = SkiesS[i];
                var me = s.who;
                if (me == null || !me.IsAlive) { SkiesS.RemoveAt(i); continue; }
                if (s.landed >= 0)
                {
                    if (now > s.landed + SpellPlans.SkyBlastRagdoll) SkiesS.RemoveAt(i);
                    continue;
                }
                float age = (float)(now - s.start);
                if (age < 0.4f || me.Status.Value.Taken(now)) continue;
                Vector3 p = me.transform.position;
                if (me.ViewVelocity.y < -1f) s.falling = true;
                bool down = s.falling && ((me.ViewFlags & NetState.Grounded) != 0 || RisingWater.FloatLine(p, out _)
                    || Physics.Raycast(p, Vector3.down, 1f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore));
                if (!down && age < SpellPlans.SkyMaxTime) continue;
                s.landed = now;
                var st = me.Status.Value;
                st.SpinUntil = 0;
                me.Status.Value = st;
                SkyBlast(me);
            }
        }

        static void SkyBlast(PlayerController me)
        {
            Vector3 p = GroundBelow(me.transform.position, 0f, 2f, out var floor) ? floor : me.transform.position;
            WorldNet.ServerWheelBlast(1, p, SpellPlans.SkyBlast);
            var w = VoxelWorld.Current;
            if (w != null) WorldNet.ServerSurface(w.CellOf(p), 0f, SpellPlans.SkyBlast, WorldNet.SurfaceSoot);
            Scorch(p, SpellPlans.R4(SpellPlans.SkyCrater), SpellPlans.R4(SpellPlans.SkyCrater + 1.5f), 12f);
            MeltIceNear(p, SpellPlans.SkyBlast);
            FlameSetsOff(p, SpellPlans.SkyBlast);
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || c == me || !c.IsAlive || Room(c) != Room(me) || Folded(c) || Shielded(c) || Combatant.Allies(me, c)) continue;
                Vector3 d = c.transform.position - p;
                if (d.magnitude > SpellPlans.SkyBlast) continue;
                for (int b = 0; b < SpellPlans.SkyBurns; b++) Burn(c, me, p);
                d.y = 0f;
                Vector3 away = d.sqrMagnitude > 0.01f ? d.normalized : me.transform.forward;
                Hit(c, me, 0f, c.transform.position, away * SpellPlans.SkyKnock + Vector3.up * SpellPlans.SkyKnockUp, SpellPlans.SkyBlastRagdoll, from: p, card: CardId.WheelOfFortune);
            }
            // them too (straight to their machine, so this can't break a combo either)
            if (Shielded(me)) return;
            for (int b = 0; b < SpellPlans.SkyBurns; b++) Burn(me, me);
            Vector2 side = Random.insideUnitCircle.normalized;
            me.ServerFling(new Vector3(side.x, 0f, side.y) * (SpellPlans.SkyKnock * 0.4f) + Vector3.up * SpellPlans.SkyKnockUp, SpellPlans.SkyBlastRagdoll);
        }

        // ------------------------------------------------------------------ the ground: iced over, or gone

        static void TickWheelGround(double now)
        {
            for (int i = IceFloors.Count - 1; i >= 0; i--)
            {
                var f = IceFloors[i];
                float every = SpellPlans.IceFloorTime / SpellPlans.IceFloorSteps, ring = SpellPlans.IceFloorRadius / SpellPlans.IceFloorSteps;
                while (f.step < SpellPlans.IceFloorSteps && now >= f.start + every * f.step)
                {
                    f.step++;
                    WorldNet.ServerSurface(f.cell, ring * (f.step - 1), ring * f.step, WorldNet.SurfaceIce);
                }
                if (f.step < SpellPlans.IceFloorSteps) continue;
                IceFloors.RemoveAt(i);
                // frost a breath left there is ice for good now
                float r = SpellPlans.IceFloorRadius / VoxelWorld.Size + 1f;
                _ice.Clear();
                foreach (var p in Coated.Keys)
                    if ((p - f.cell).magnitude <= r) _ice.Add(p);
                foreach (var p in _ice) Coated.Remove(p);
            }
            for (int i = BlastsS.Count - 1; i >= 0; i--)
            {
                var b = BlastsS[i];
                while (b.step < SpellPlans.DoomSteps && now >= b.start + SpellPlans.DoomSpread * b.step / SpellPlans.DoomSteps)
                {
                    b.step++;
                    float r = SpellPlans.DoomRadius / VoxelWorld.Size / SpellPlans.DoomSteps;
                    WorldNet.ServerVaporizeShell(b.cell, Mathf.RoundToInt(r * (b.step - 1)), Mathf.RoundToInt(r * b.step), b.step == SpellPlans.DoomSteps);
                }
                if (b.step >= SpellPlans.DoomSteps) BlastsS.RemoveAt(i);
            }
        }
    }
}
