using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for the Weapon cards (the user's hammer, Resources/VFX/Hammer).
    ///  - Rising Hammer: a rush (as Severance) that ends in an upswing — when someone comes into reach, or after
    ///    0.5 s. Everyone in reach takes 20 and is thrown straight up into the air, ragdolled.
    ///  - Descending Hammer: 0.3 s down on one knee, then a leap up and forward that the mouse steers freely (all
    ///    owner-side, like every movement). Touching anything (ground, wall, ceiling, someone) is the slam: a bowl
    ///    3 m across the surface and 4 voxels deep in the middle is smashed out, and everyone within 3 m is hit,
    ///    45 and thrown hard at the middle down to 15 and thrown lightly at the edge.
    /// </summary>
    public static partial class SpellServer
    {
        class RisingState { public PlayerController caster; public double start; }
        class RisingHitState { public PlayerController caster; public Combatant victim; public double at; }
        class LeapState { public PlayerController caster; public double start; }

        static readonly List<RisingState> RisingsS = new();
        static readonly List<RisingHitState> RisingHitsS = new();
        static readonly List<LeapState> LeapsS = new();

        static void ResetWeapon()
        {
            RisingsS.Clear();
            RisingHitsS.Clear();
            LeapsS.Clear();
        }

        static bool CastWeapon(PlayerController caster, CardId card, double now)
        {
            switch (card)
            {
                case CardId.RisingHammer:
                    RisingsS.RemoveAll(s => s.caster == caster);
                    RisingsS.Add(new RisingState { caster = caster, start = now });
                    return true;
                case CardId.DescendingHammer:
                    LeapsS.RemoveAll(s => s.caster == caster);
                    LeapsS.Add(new LeapState { caster = caster, start = now });
                    return true;
            }
            return false;
        }

        /// <summary>Thrown out of the rush, the crouch or the leap: no swing, no slam.</summary>
        static void InterruptWeapon(PlayerController pc, bool knocked)
        {
            if (!knocked) return;
            RisingsS.RemoveAll(s => s.caster == pc);
            RisingHitsS.RemoveAll(s => s.caster == pc);
            LeapsS.RemoveAll(s => s.caster == pc);
        }

        static void TickWeapon(double now)
        {
            TickRisings(now);
            TickRisingHits(now);
            // a leap that never came down (off the edge): no slam
            LeapsS.RemoveAll(l => l.caster == null || !l.caster.IsAlive || now > l.start + SpellPlans.HammerCrouch + SpellPlans.LeapMaxAir + 1.0);
        }

        // ------------------------------------------------------------------ Rising Hammer

        static void TickRisings(double now)
        {
            for (int i = RisingsS.Count - 1; i >= 0; i--)
            {
                var s = RisingsS[i];
                var me = s.caster;
                float t = (float)(now - s.start);
                if (me == null || !me.IsAlive) { RisingsS.RemoveAt(i); continue; }
                // the way the rush is actually going (it steers after the mouse), else where they're looking
                Vector3 vel = me.ViewVelocity;
                vel.y = 0f;
                Vector3 fwd = vel.magnitude > 5f ? vel.normalized : Quaternion.Euler(0, me.ViewYaw, 0) * Vector3.forward;
                var struck = t >= SpellPlans.RisingArm ? InReach(me, fwd) : new List<Combatant>();
                if (struck.Count == 0 && t < SpellPlans.RisingTime) continue;

                // the upswing: everyone in reach is struck as the head comes up through them
                RisingsS.RemoveAt(i);
                me.ServerHammerRise();
                foreach (var o in struck)
                    RisingHitsS.Add(new RisingHitState { caster = me, victim = o, at = now + SpellPlans.RisingHitAt });
            }
        }

        static void TickRisingHits(double now)
        {
            for (int i = RisingHitsS.Count - 1; i >= 0; i--)
            {
                var h = RisingHitsS[i];
                if (now < h.at) continue;
                RisingHitsS.RemoveAt(i);
                var c = h.victim;
                if (c == null || !c.IsAlive || h.caster == null || !h.caster.IsAlive) continue;
                Vector3 point = c.transform.position + Vector3.up * 0.1f;
                WorldNet.ServerHammerHit(point, Vector3.up);
                // straight up into the air
                Hit(c, h.caster, SpellPlans.RisingDamage, point, Vector3.up * SpellPlans.RisingLaunch, SpellPlans.RisingRagdoll);
            }
        }

        // ------------------------------------------------------------------ Descending Hammer

        /// <summary>The caster's owner says the leap touched down at <paramref name="point"/> (on a surface facing <paramref name="normal"/>).</summary>
        public static void HammerSlam(PlayerController caster, Vector3 point, Vector3 normal)
        {
            double now = WorldNet.Now;
            var leap = LeapsS.Find(l => l.caster == caster);
            if (leap == null || now < leap.start + SpellPlans.HammerCrouch - 0.15) return;
            LeapsS.Remove(leap);
            // believable: near where the server last saw them (they were flying fast, a little behind)
            if (float.IsNaN(point.x + point.y + point.z) || (point - caster.FeetPosition).magnitude > 3f + SpellPlans.LeapForward * 0.3f) return;
            if (float.IsNaN(normal.x + normal.y + normal.z) || normal.sqrMagnitude < 0.5f) normal = Vector3.up;
            normal.Normalize();

            var world = VoxelWorld.Current;
            var cells = SpellPlans.CraterCells(world, point, normal);
            WorldNet.ServerHammerSlam(cells, point, normal, SpellPlans.CraterRadius);

            // everyone round it: hardest in the middle, lighter towards the edge (never the caster)
            Vector3 fwd = Quaternion.Euler(0f, caster.ViewYaw, 0f) * Vector3.forward;
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || c == caster || !c.IsAlive || Folded(c) || Shielded(c)) continue;
                float k = SpellPlans.SlamReach(point, c.FeetPosition);
                if (k > 1f) continue;
                Vector3 away = c.transform.position - point;
                away = Vector3.ProjectOnPlane(away, normal);
                away = away.sqrMagnitude > 0.04f ? away.normalized : Vector3.ProjectOnPlane(fwd, normal).normalized;
                Vector3 knock = away * Mathf.Lerp(SpellPlans.SlamKnockOutMax, SpellPlans.SlamKnockOutMin, k)
                              + Vector3.up * Mathf.Lerp(SpellPlans.SlamKnockUpMax, SpellPlans.SlamKnockUpMin, k);
                Hit(c, caster, Mathf.Lerp(SpellPlans.SlamDamageMax, SpellPlans.SlamDamageMin, k), c.transform.position, knock,
                    Mathf.Lerp(SpellPlans.SlamRagdollMax, SpellPlans.SlamRagdollMin, k));
            }
        }
    }
}
