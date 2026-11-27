using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for the map hazards (Assets/Scripts/Hazards): drowning, lava, meteors, spike walls, the closing zone, trains smashing through. (A
    /// train's kill is spotted by whoever it hits, as a kill barrier's is: PlayerController.CheckHazards, TestDummy.)
    /// </summary>
    public static partial class SpellServer
    {
        /// <summary>Seconds in the water (or out of the zone) this life, and how many of them have hurt.</summary>
        static readonly Dictionary<Combatant, (float time, int hurt)> Wet = new();
        static readonly Dictionary<Combatant, double> LavaNext = new(), SpikeNext = new();
        static double _washNext;
        /// <summary>How often water and spike walls look for spells' ground to break (s).</summary>
        const double WashEvery = 0.2;

        static void ResetHazards()
        {
            Wet.Clear();
            LavaNext.Clear();
            SpikeNext.Clear();
            _washNext = 0;
        }

        /// <summary>Starting over in a practice chamber: as if never in the water.</summary>
        public static void DryOff(Combatant c)
        {
            Wet.Remove(c);
            LavaNext.Remove(c);
            SpikeNext.Remove(c);
        }

        static void TickHazards(double now, float dt)
        {
            MeteorShower.ServerTick(dt);
            RisingWall.ServerTick();
            foreach (var t in TrainTrack.All) t.ServerSmash();
            if (now >= _washNext)
            {
                _washNext = now + WashEvery;
                RisingWater.ServerWash();
                foreach (var s in SpikeWalls.All) s.ServerSmash();
            }
            if (Liquid.All.Count == 0 && SpikeWalls.All.Count == 0 && !ClosingZone.Any) return;
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null) continue;
                if (!c.IsAlive) { Wet.Remove(c); LavaNext.Remove(c); SpikeNext.Remove(c); continue; }
                if (Folded(c) || Shielded(c) || WorldNet.InsideWorld(c.transform.position)) continue;
                bool water = false, lava = false;
                foreach (var l in Liquid.All)
                {
                    if (l is RisingWater ? water : lava) continue;
                    if (!l.Touches(c)) continue;
                    if (l is RisingWater) water = true; else lava = true;
                }
                if (water || ClosingZone.InAny(c.transform.position)) Drown(c, dt);
                if (lava && c.IsAlive) Scald(c, now);
                if (c.IsAlive) Spike(c, now);
            }
        }

        static void Drown(Combatant c, float dt)
        {
            Wet.TryGetValue(c, out var w);
            w.time += dt;
            while (w.time >= w.hurt + 1 && c.IsAlive)
            {
                float damage = SpellPlans.WaterDamage(w.hurt);
                w.hurt++;
                c.ServerApplyDamage(damage, Combatant.NoAttacker, c.transform.position, Vector3.up, -1, 0f, DeathCause.Barrier);
            }
            if (c.IsAlive) Wet[c] = w; else Wet.Remove(c);
        }

        static void Scald(Combatant c, double now)
        {
            if (LavaNext.TryGetValue(c, out double next) && now < next) return;
            LavaNext[c] = now + SpellPlans.LavaEvery;
            c.ServerBurn(Combatant.NoAttacker);
            Vector3 v = c.ViewVelocity;
            v.y = 0f;
            c.ServerKnock(Vector3.ClampMagnitude(v, SpellPlans.LavaCarry) + Vector3.up * SpellPlans.LavaLaunch, SpellPlans.LavaRagdoll);
        }

        static void Spike(Combatant c, double now)
        {
            if (SpikeNext.TryGetValue(c, out double next) && now < next) return;
            foreach (var s in SpikeWalls.All)
            {
                if (!s.Touches(c, out var away)) continue;
                SpikeNext[c] = now + SpellPlans.SpikeEvery;
                Vector3 v = away * SpellPlans.SpikeThrow + Vector3.up * SpellPlans.SpikeLift + s.Sideways * Random.Range(-SpellPlans.SpikeSlide, SpellPlans.SpikeSlide);
                c.ServerApplyDamage(SpellPlans.SpikeDamage, Combatant.NoAttacker, c.transform.position, v.normalized, -1, SpellPlans.SpikeThrow, DeathCause.Barrier);
                if (c.IsAlive) c.ServerKnock(v, SpellPlans.SpikeRagdoll);
                return;
            }
        }

        // ------------------------------------------------------------------ meteors

        /// <summary>A meteor that lands at <paramref name="at"/> (marked from now), coming down along <paramref name="dir"/>. It's a fireball in every other way.</summary>
        /// <param name="size">How big it is (and its blast), against an ordinary one.</param>
        /// <param name="by">Whose it is (Wheel of Fortune's): their kills, and it doesn't hurt them; null: the map's.</param>
        public static void DropMeteor(Vector3 at, Vector3 normal, Vector3 dir, PlayerController by = null, float size = 1f)
        {
            Vector3 origin = at - dir * (SpellPlans.MeteorSpeed * SpellPlans.MeteorWarn);
            int id = WorldNet.ServerMeteor(origin, dir * SpellPlans.MeteorSpeed, at, normal, by != null ? by.NetworkObjectId : 0, size);
            if (id != 0) FireballsS.Add(new FireballState { id = id, caster = by, start = WorldNet.Now, launched = true, lastPos = origin, meteor = true, size = size });
        }

        static void LandMeteor(FireballState f, WorldNet.FireballFx fx, Vector3 point)
        {
            var owner = WorldNet.FindCaster(fx.owner); // (whoever rolled it, or turned it, if anyone)
            DamageFrom = point - fx.velocity.normalized * 60f; // (out of the sky)
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || !c.IsAlive || Folded(c) || Shielded(c) || c == owner && c == f.caster) continue;
                Vector3 d = c.transform.position - point;
                if (d.magnitude > SpellPlans.MeteorBlast * f.size + 0.5f) continue;
                Burn(c, owner, point);
                Vector3 away = d.sqrMagnitude > 0.01f ? d.normalized : Vector3.up;
                Hit(c, owner, SpellPlans.MeteorDamage, c.transform.position, away * SpellPlans.MeteorKnock + Vector3.up * SpellPlans.MeteorLift, SpellPlans.MeteorRagdoll, from: point);
            }
            DamageFrom = null;
            MeltIceNear(point, SpellPlans.MeteorSinge * f.size + SpellPlans.IceMeltReach);
            if (VoxelWorld.Current != null)
                WorldNet.ServerMeteorCrater(VoxelWorld.Current.CellOf(point), SpellPlans.R4(SpellPlans.MeteorCrater * f.size), SpellPlans.R4(SpellPlans.MeteorSinge * f.size));
            WorldNet.ServerFlameBurst(point, 1.2f * SpellPlans.MeteorScale * f.size);
            FlameSetsOff(point, SpellPlans.MeteorBlast * f.size);
            WorldNet.ServerFireballEnd(f.id, point, false);
        }
    }
}
