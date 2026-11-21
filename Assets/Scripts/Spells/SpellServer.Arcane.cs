using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Server rules for Warp Self, Warp Space and Arcane Swiftness.</summary>
    public static partial class SpellServer
    {
        class WarpState { public int id; public PlayerController caster; public Vector3 dest; public double start; }
        class PortalState { public int id, pair; public PlayerController owner; public double start, linkStart = -1; }
        class SwiftState { public PlayerController caster; public double start; }

        static readonly List<WarpState> WarpsS = new();
        static readonly List<PortalState> PortalsS = new();
        static readonly List<SwiftState> SwiftsS = new();

        static void ResetArcane()
        {
            WarpsS.Clear();
            PortalsS.Clear();
            SwiftsS.Clear();
        }

        static double PortalEnd(PortalState p) => (p.linkStart >= 0 ? p.linkStart : p.start) + SpellPlans.PortalLife;

        static bool CastArcane(PlayerController caster, CardId card, Vector3 target, float yaw, ulong extra, double now)
        {
            switch (card)
            {
                case CardId.WarpSelf:
                {
                    if (WarpsS.Exists(w => w.caster == caster)) return false;
                    if ((target - caster.FeetPosition).magnitude > SpellPlans.WarpRange + 4f) return false; // a little slack for lag
                    int id = WorldNet.ServerWarp(caster.NetworkObjectId, target);
                    WarpsS.Add(new WarpState { id = id, caster = caster, dest = target, start = now });
                    return true;
                }
                case CardId.WarpSpace:
                {
                    PortalState open = null;
                    foreach (var p in PortalsS)
                    {
                        if (p.owner != caster) continue;
                        if (p.pair != 0) return false; // a linked pair is still open: not again until it closes
                        open = p;
                    }
                    if ((target - caster.transform.position).magnitude > SpellPlans.PortalRange + 4f) return false;
                    float pitch = Mathf.Clamp(SpellPlans.UnpackAngle(extra), -90f, 90f);
                    if (float.IsNaN(pitch)) pitch = 0f;
                    int id = WorldNet.ServerPortal(caster.NetworkObjectId, target, yaw, pitch);
                    var made = new PortalState { id = id, owner = caster, start = now };
                    PortalsS.Add(made);
                    if (open != null)
                    {
                        // the second one: they link, and both stay open 10 s from now
                        open.pair = id;
                        made.pair = open.id;
                        open.linkStart = made.linkStart = now;
                        WorldNet.ServerPortalLink(open.id, id);
                    }
                    return true;
                }
                case CardId.ArcaneSwiftness:
                {
                    if (SwiftsS.Exists(s => s.caster == caster)) return false;
                    Combatant.DamageScale ??= ScaleDamage;
                    WorldNet.ServerSwift(caster.NetworkObjectId);
                    SwiftsS.Add(new SwiftState { caster = caster, start = now });
                    return true;
                }
            }
            return false;
        }

        /// <summary>Warp Space's second portal is free and has no cooldown: is this player's first one waiting for it?</summary>
        public static bool PortalRecastReady(PlayerController pc)
        {
            foreach (var p in PortalsS) if (p.owner == pc && p.pair == 0) return true;
            return false;
        }

        static void TickArcane(double now)
        {
            for (int i = WarpsS.Count - 1; i >= 0; i--)
            {
                var w = WarpsS[i];
                if (w.caster == null || !w.caster.IsAlive)
                {
                    WarpsS.RemoveAt(i);
                    WorldNet.ServerWarpEnd(w.id, 0);
                    continue;
                }
                if (now > w.start + SpellPlans.WarpDone) WarpsS.RemoveAt(i);
            }
            PortalsS.RemoveAll(p => now > PortalEnd(p));
            for (int i = SwiftsS.Count - 1; i >= 0; i--)
            {
                var s = SwiftsS[i];
                if (s.caster == null || !s.caster.IsAlive)
                {
                    SwiftsS.RemoveAt(i);
                    if (s.caster != null) WorldNet.ServerSwiftEnd(s.caster.NetworkObjectId, true);
                    continue;
                }
                if (now > s.start + SpellPlans.SwiftPrep + SpellPlans.SwiftTime) SwiftsS.RemoveAt(i);
            }
        }

        /// <summary>
        /// Hurt or knocked: a warp still charging is cancelled; one unfolding finishes at once. Arcane Swiftness still
        /// being prepared is cancelled by a knock.
        /// </summary>
        static void InterruptArcane(PlayerController pc, bool knocked)
        {
            double now = WorldNet.Now;
            for (int i = WarpsS.Count - 1; i >= 0; i--)
            {
                var w = WarpsS[i];
                if (w.caster != pc) continue;
                float t = (float)(now - w.start);
                if (t < SpellPlans.WarpCharge) { WarpsS.RemoveAt(i); WorldNet.ServerWarpEnd(w.id, 0); }
                else if (t >= SpellPlans.WarpArrive) { WarpsS.RemoveAt(i); WorldNet.ServerWarpEnd(w.id, 1); }
            }
            if (!knocked) return;
            for (int i = SwiftsS.Count - 1; i >= 0; i--)
            {
                var s = SwiftsS[i];
                if (s.caster != pc || now >= s.start + SpellPlans.SwiftPrep) continue;
                SwiftsS.RemoveAt(i);
                WorldNet.ServerSwiftEnd(pc.NetworkObjectId, true);
            }
        }

        /// <summary>Folded away mid-warp: not there to be hit at all (projectiles pass through).</summary>
        public static bool Folded(Combatant c)
        {
            if (c == null) return false;
            double now = WorldNet.Now;
            foreach (var w in WarpsS)
                if (w.caster == c && now >= w.start + SpellPlans.WarpCharge && now < w.start + SpellPlans.WarpArrive) return true;
            return false;
        }

        /// <summary>Untouchable right now (folded mid-warp, or Arcane Swiftness running): no damage, knocks, burns or anything else.</summary>
        public static bool Invulnerable(Combatant c)
        {
            if (c == null) return false;
            if (Folded(c)) return true;
            double now = WorldNet.Now;
            foreach (var s in SwiftsS)
                if (s.caster == c && now >= s.start + SpellPlans.SwiftPrep && now <= s.start + SpellPlans.SwiftPrep + SpellPlans.SwiftTime + 0.1) return true;
            return false;
        }

        /// <summary>A swift dash's owner reports running into voxels: they come apart (the owner already did on their screen).</summary>
        public static void SwiftBreak(PlayerController pc, Vector3 feet, Vector3 dir)
        {
            var w = VoxelWorld.Current;
            if (w == null || !pc.IsAlive || !SwiftsS.Exists(s => s.caster == pc)) return;
            if ((feet - pc.FeetPosition).magnitude > 3f) return;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) return;
            SpellPlans.SwiftBreakBox(w, feet, dir.normalized, out var min, out var max);
            WorldNet.ServerRemoveBox(min, max, 6f);
        }

        // ------------------------------------------------------------------ projectiles through portals

        /// <summary>A thrown rock went into a linked portal: it carries on out of the other one.</summary>
        // A projectile counts as in when its ball touches the opening (the slab behind is too thin to fly into first),
        // and comes out the same distance in front of the other portal.

        static bool RockThroughPortal(RockState rock, WorldNet.RockFx fx, Vector3 pos, double now)
        {
            if (!WorldNet.PassPortal(rock.lastPos, pos, rock.radius + 0.02f, rock.radius * 0.5f, out var a, out var b)) return false;
            Vector3 np = SpellPlans.ThroughPoint(pos, a.center, a.Rotation, b.center, b.Rotation, true);
            Vector3 nv = SpellPlans.ThroughDir(WorldNet.RockVelocity(fx, now), a.Rotation, b.Rotation);
            WorldNet.ServerRockLaunch(rock.id, np, nv);
            rock.lastPos = np;
            rock.lastTime = now;
            return true;
        }

        static bool FireballThroughPortal(FireballState f, WorldNet.FireballFx fx, Vector3 pos)
        {
            if (!WorldNet.PassPortal(f.lastPos, pos, SpellPlans.FireballRadius + 0.02f, SpellPlans.FireballRadius * 0.5f, out var a, out var b)) return false;
            Vector3 np = SpellPlans.ThroughPoint(pos, a.center, a.Rotation, b.center, b.Rotation, true);
            WorldNet.ServerFireballLaunch(f.id, np, SpellPlans.ThroughDir(fx.velocity, a.Rotation, b.Rotation), fx.owner);
            f.lastPos = np;
            return true;
        }

        static bool MissileThroughPortal(int volley, int index, WorldNet.Missile ms)
        {
            if (!WorldNet.PassPortal(ms.prev, ms.pos, SpellPlans.MissileRadius + 0.02f, SpellPlans.MissileRadius, out var a, out var b)) return false;
            Vector3 np = SpellPlans.ThroughPoint(ms.pos, a.center, a.Rotation, b.center, b.Rotation, true);
            WorldNet.ServerMissileRedirect(volley, index, np, SpellPlans.ThroughDir(ms.dir, a.Rotation, b.Rotation), ms.owner);
            return true;
        }
    }
}
