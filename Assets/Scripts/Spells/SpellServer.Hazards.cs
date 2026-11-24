using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for the map hazards (Assets/Scripts/Hazards): drowning, lava, meteors, trains smashing through. (A
    /// train's kill is spotted by whoever it hits, as a kill barrier's is: PlayerController.CheckHazards, TestDummy.)
    /// </summary>
    public static partial class SpellServer
    {
        /// <summary>Seconds in the water this life, and how many of them have hurt.</summary>
        static readonly Dictionary<Combatant, (float time, int hurt)> Wet = new();
        static readonly Dictionary<Combatant, double> LavaNext = new();

        static void ResetHazards()
        {
            Wet.Clear();
            LavaNext.Clear();
        }

        /// <summary>Starting over in a practice chamber: as if never in the water.</summary>
        public static void DryOff(Combatant c)
        {
            Wet.Remove(c);
            LavaNext.Remove(c);
        }

        static void TickHazards(double now, float dt)
        {
            MeteorShower.ServerTick(dt);
            foreach (var t in TrainTrack.All) t.ServerSmash();
            if (Liquid.All.Count == 0) return;
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null) continue;
                if (!c.IsAlive) { Wet.Remove(c); LavaNext.Remove(c); continue; }
                if (Folded(c) || Shielded(c) || WorldNet.InsideWorld(c.transform.position)) continue;
                bool water = false, lava = false;
                foreach (var l in Liquid.All)
                {
                    if (l is RisingWater ? water : lava) continue;
                    if (!l.Touches(c)) continue;
                    if (l is RisingWater) water = true; else lava = true;
                }
                if (water) Drown(c, dt);
                if (lava && c.IsAlive) Scald(c, now);
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

        // ------------------------------------------------------------------ meteors

        /// <summary>A meteor that lands at <paramref name="at"/> (marked from now), coming down along <paramref name="dir"/>. It's a fireball in every other way.</summary>
        public static void DropMeteor(Vector3 at, Vector3 normal, Vector3 dir)
        {
            Vector3 origin = at - dir * (SpellPlans.MeteorSpeed * SpellPlans.MeteorWarn);
            int id = WorldNet.ServerMeteor(origin, dir * SpellPlans.MeteorSpeed, at, normal);
            if (id != 0) FireballsS.Add(new FireballState { id = id, start = WorldNet.Now, launched = true, lastPos = origin, meteor = true });
        }

        static void LandMeteor(FireballState f, WorldNet.FireballFx fx, Vector3 point)
        {
            var owner = WorldNet.FindCaster(fx.owner); // (whoever turned it, if anyone)
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || !c.IsAlive || Folded(c) || Shielded(c)) continue;
                Vector3 d = c.transform.position - point;
                if (d.magnitude > SpellPlans.MeteorBlast + 0.5f) continue;
                Burn(c, owner);
                Vector3 away = d.sqrMagnitude > 0.01f ? d.normalized : Vector3.up;
                Hit(c, owner, SpellPlans.MeteorDamage, c.transform.position, away * SpellPlans.MeteorKnock + Vector3.up * SpellPlans.MeteorLift, SpellPlans.MeteorRagdoll);
            }
            Scorch(point, SpellPlans.R4(SpellPlans.MeteorCrater), SpellPlans.R4(SpellPlans.MeteorSinge), 9f);
            WorldNet.ServerFlameBurst(point, 1.2f * SpellPlans.MeteorScale);
            FlameSetsOff(point, SpellPlans.MeteorBlast);
            WorldNet.ServerFireballEnd(f.id, point, false);
        }
    }
}
