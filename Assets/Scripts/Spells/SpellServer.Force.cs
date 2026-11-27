using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for Attraction (Water) and Repulsion (Flame), and the purple ball they make together.
    ///  - Either one: a hand goes up for 0.3 s, then a ball comes out and is steered away along the caster's aim
    ///    for 4 s, growing (the caster can walk about but casts nothing else). It reaches out to twice its radius:
    ///    Attraction tears voxels out and sucks them in, pulls enemy projectiles round and into it, and grabs people
    ///    (ragdolled, dragged along for 1 s, then let go; never the same person twice); Repulsion flies until it touches
    ///    the ground, a flying rock or anyone, or reaches the end of its range, then goes off in a blast
    ///    (3.75 m) that blows a crater, scorches everything in it, and throws every enemy in it away, double burned. Throwing the caster down
    ///    cuts it short (it dissipates in 0.3 s). Water rules: Attraction puts fire out; Repulsion is put out by water.
    ///  - Both cast within 0.3 s of each other (the one special case where a second card may be cast mid-cast): they
    ///    fuse. A purple ball grows in front of the caster for 2 s (the caster is untouchable), then flies 40 m along
    ///    their aim, erasing every voxel and projectile in its way and dealing 75 to anyone it hits.
    /// </summary>
    public static partial class SpellServer
    {
        class ForceState
        {
            public int id; public PlayerController caster; public bool water; public double start;
            public bool out_; public Vector3 pos, vel, castAt;
            public double nextMove, nextRip, nextPull, lastProjectilePull = -1;
            public readonly Dictionary<Combatant, double> held = new();   // Attraction: grabbed (when)
            public readonly HashSet<Combatant> released = new();          // Attraction: held once already (never grabbed again)
            public readonly HashSet<int> pulledRocks = new(), pulledFireballs = new(); // Attraction: projectiles caught
        }
        class FuseState
        {
            public int id; public PlayerController caster; public double start;
            public bool launched; public Vector3 origin, dir; public double launchTime; public float erased, swept;
            public byte over; // 0, or which of Wheel of Fortune's (over its caster's head) it is
            public readonly HashSet<Combatant> hit = new();
        }

        static readonly List<ForceState> ForcesS = new();
        static readonly List<FuseState> FusesS = new();

        /// <summary>Set by a cast that needs a different cast lock than its card's usual one (the fused ball). -1 = none.</summary>
        public static float CastTimeOverride = -1f;

        static void ResetForce()
        {
            ForcesS.Clear();
            FusesS.Clear();
        }

        /// <summary>The other half of a fusion: this caster's partner ball, cast within the window and not out of the hand yet.</summary>
        static ForceState FusePartner(PlayerController pc, CardId card, double now)
        {
            if (card != CardId.Attraction && card != CardId.Repulsion) return null;
            bool partnerWater = card == CardId.Repulsion;
            foreach (var f in ForcesS)
                if (f.caster == pc && f.water == partnerWater && !f.out_ && now - f.start <= SpellPlans.FuseWindow + 0.15) return f;
            return null;
        }

        /// <summary>May this cast go ahead mid-cast? Only to fuse Attraction and Repulsion.</summary>
        public static bool FuseReady(PlayerController pc, CardId card) => FusePartner(pc, card, WorldNet.Now) != null;

        static bool CastForce(PlayerController caster, CardId card, double now)
        {
            var partner = FusePartner(caster, card, now);
            if (partner != null)
            {
                // the two fuse: the first ball never comes out; the purple one forms instead
                ForcesS.Remove(partner);
                WorldNet.ServerForceEnd(partner.id, true);
                Combatant.DamageScale ??= ScaleDamage; // untouchable while it forms
                int fid = WorldNet.ServerFuse(caster.NetworkObjectId);
                FusesS.Add(new FuseState { id = fid, caster = caster, start = now });
                CastTimeOverride = SpellPlans.FuseCharge + 0.3f;
                return true;
            }
            bool water = card == CardId.Attraction;
            int id = WorldNet.ServerForce(caster.NetworkObjectId, water);
            ForcesS.Add(new ForceState { id = id, caster = caster, water = water, start = now });
            return true;
        }

        /// <summary>Forming the purple ball: untouchable.</summary>
        static bool Fusing(Combatant c)
        {
            foreach (var f in FusesS) if (f.caster == c && !f.launched && f.over == 0) return true;
            return false;
        }

        /// <summary>Thrown down mid-cast: the ball (or the hand still raising it) dissipates.</summary>
        static void InterruptForce(PlayerController pc, bool knocked)
        {
            if (!knocked) return;
            for (int i = ForcesS.Count - 1; i >= 0; i--)
                if (ForcesS[i].caster == pc) EndForce(i, true);
        }

        /// <summary>Is there an Attraction ball (water) here?</summary>
        static bool InWaterBall(Vector3 p, float pad)
        {
            double now = WorldNet.Now;
            foreach (var f in ForcesS)
            {
                if (!f.water || !f.out_) continue;
                float r = SpellPlans.ForceRadius(true, (float)(now - f.start) - SpellPlans.OrbRaise) + pad;
                if ((p - f.pos).sqrMagnitude < r * r) return true;
            }
            return false;
        }

        static bool AnyWaterBall()
        {
            foreach (var f in ForcesS) if (f.water && f.out_) return true;
            return false;
        }

        static void TickForce(double now, float dt)
        {
            TickForces(now, dt);
            TickFuses(now);
        }

        // ------------------------------------------------------------------ Attraction / Repulsion

        static void TickForces(double now, float dt)
        {
            var world = VoxelWorld.Current;
            for (int i = ForcesS.Count - 1; i >= 0; i--)
            {
                var f = ForcesS[i];
                var me = f.caster;
                if (me == null || !me.IsAlive) { EndForce(i, true); continue; }
                float t = (float)(now - f.start);
                if (t < SpellPlans.OrbRaise) continue;
                Vector3 aim = Quaternion.Euler(me.ViewPitch, me.ViewYaw, 0f) * Vector3.forward;
                if (!f.out_)
                {
                    // out of the raised hand (left for water, right for flame)
                    f.out_ = true;
                    Vector3 side = Quaternion.Euler(0f, me.ViewYaw, 0f) * Vector3.right * (f.water ? -0.3f : 0.3f);
                    f.pos = me.transform.position + Vector3.up * 0.45f + side + aim * 0.9f;
                    f.castAt = me.transform.position;
                    f.nextMove = f.nextRip = f.nextPull = now;
                }
                float life = t - SpellPlans.OrbRaise;
                // it runs its course after 4 s, or once it's 25 m from where it was cast
                // (Repulsion goes off there)
                if (life >= SpellPlans.ForceLife || (f.pos - f.castAt).magnitude >= SpellPlans.ForceRange)
                {
                    if (f.water) EndForce(i, false);
                    else ExplodeRepulsion(i, f.pos);
                    continue;
                }
                float r = SpellPlans.ForceRadius(f.water, life), reach = SpellPlans.ForceReach(f.water, life);

                // steered along the caster's aim at full speed the whole time, but only ever away from them: whatever
                // part of the aim points back at the caster is dropped (aiming straight back, it keeps its last heading)
                Vector3 fromMe = f.pos - me.transform.position;
                Vector3 dir = aim;
                if (fromMe.sqrMagnitude > 1e-4f && Vector3.Dot(dir, fromMe) < 0f)
                {
                    dir = Vector3.ProjectOnPlane(dir, fromMe.normalized);
                    if (dir.sqrMagnitude < 0.04f) dir = f.vel.sqrMagnitude > 1e-4f ? f.vel : fromMe;
                }
                f.vel = dir.normalized * SpellPlans.ForceSpeedOf(f.water);
                Vector3 from = f.pos;
                f.pos += f.vel * dt;
                if (now >= f.nextMove)
                {
                    f.nextMove = now + SpellPlans.ForceMoveEvery;
                    WorldNet.ServerForceMove(f.id, f.pos, f.vel);
                }

                // fire never beats water: a flame ball that touches water goes out
                if (!f.water && (InWater(f.pos, r) || InWaterBall(f.pos, r)))
                {
                    WorldNet.ServerQuench(f.pos, 1f);
                    EndForce(i, true);
                    continue;
                }

                // Repulsion: the moment it touches the ground, a flying lump of it, or anyone, it goes off
                if (!f.water)
                {
                    if (RepelContact(world, f, from, r, out Vector3 at)) ExplodeRepulsion(i, at);
                    continue; // (it does nothing else in flight)
                }

                // Attraction: the voxels in reach are torn out and sucked in
                if (world != null && now >= f.nextRip)
                {
                    f.nextRip = now + SpellPlans.ForceRipEvery;
                    TearTowards(world, f, r, reach);
                }

                // people (and dummies) in reach are grabbed (once each)
                foreach (var c in Combatant.Registry.ToArray())
                {
                    if (c == null || c == me || !c.IsAlive || Folded(c)) continue;
                    if (f.held.ContainsKey(c) || f.released.Contains(c) || !Touching(c, f.pos, reach)) continue;
                    if (ShieldStops(c, me, f.pos, CardId.Attraction)) f.released.Add(c); // (held off with a shield)
                    else if (ParryCatch(c, me, f.pos)) f.released.Add(c); // (Parry: not held)
                    else f.held[c] = now;
                }

                // enemy projectiles in reach are pulled in
                PullProjectiles(f, r, reach, now);

                // Attraction: everyone it holds is dragged back in, limp, and carried along with it (for a second, then let go)
                if (now >= f.nextPull)
                {
                    f.nextPull = now + SpellPlans.PullEvery;
                    foreach (var c in new List<Combatant>(f.held.Keys))
                    {
                        if (c == null || !c.IsAlive || (c.transform.position - f.pos).magnitude > 12f) { f.held.Remove(c); if (c != null) f.released.Add(c); continue; }
                        if (now - f.held[c] >= SpellPlans.AttractHold)
                        {
                            f.held.Remove(c);
                            f.released.Add(c);
                            c.ServerCredit(f.caster);
                            c.ServerKnock(f.vel * 0.5f + Vector3.up * 2f, SpellPlans.PullHold);
                            continue;
                        }
                        Vector3 to = f.pos - c.transform.position;
                        Vector3 v = Vector3.ClampMagnitude(to * 7f, 14f) + f.vel + Vector3.up * 1.5f; // (a lift against gravity between pulls)
                        c.ServerCredit(f.caster);
                        c.ServerKnock(v, 0.6f);
                    }
                }
            }
        }

        /// <summary>
        /// Attraction catches enemy projectiles in its reach and pulls them in (an acceleration, so they swing round it
        /// a little before reaching the middle), where they're swallowed. Rocks and fireballs fly on paths every peer
        /// works out from their last launch, so the server re-launches them along the bent path every
        /// <see cref="SpellPlans.AttractProjectileEvery"/>; missiles are steered by every peer (<see cref="WorldNet"/>'s
        /// StepMissiles). Fire meeting the water ball goes out as usual.
        /// </summary>
        static void PullProjectiles(ForceState f, float r, float reach, double now)
        {
            var net = WorldNet.Instance;
            if (net == null) return;
            ulong mine = f.caster != null ? f.caster.NetworkObjectId : ulong.MaxValue;
            // (keeps hold of what it caught a little past its reach, so they swing back in)
            float keep = reach * 2f;
            if (f.lastProjectilePull < 0) f.lastProjectilePull = now;
            float dt = (float)(now - f.lastProjectilePull);
            bool step = dt >= SpellPlans.AttractProjectileEvery;
            if (step) f.lastProjectilePull = now;

            for (int k = Rocks.Count - 1; k >= 0; k--)
            {
                var rock = Rocks[k];
                if (!rock.launched || rock.caster == f.caster || !net.Rocks.TryGetValue(rock.id, out var fx)) continue;
                Vector3 p = WorldNet.RockPosition(fx, now);
                Vector3 to = f.pos - p;
                float d = to.magnitude;
                bool caught = f.pulledRocks.Contains(rock.id);
                if (d > (caught ? keep : reach) + rock.radius) { f.pulledRocks.Remove(rock.id); continue; }
                f.pulledRocks.Add(rock.id);
                if (d <= r + rock.radius)
                {
                    // swallowed: it falls apart in the middle
                    Impact(rock, p, null, 0, null);
                    Rocks.RemoveAt(k);
                    continue;
                }
                if (!step) continue;
                Vector3 v = SpellPlans.AttractPull(WorldNet.RockVelocity(fx, now), to, f.vel, dt);
                v.y += SpellPlans.RockGravity * dt; // held up against gravity while it's caught
                WorldNet.ServerRockLaunch(rock.id, p, v);
                rock.lastPos = p;
                rock.lastTime = now;
            }

            for (int k = FireballsS.Count - 1; k >= 0; k--)
            {
                var fb = FireballsS[k];
                if (!fb.launched || !net.Fireballs.TryGetValue(fb.id, out var fx) || fx.owner == mine) continue;
                Vector3 p = WorldNet.FireballPosition(fx, now);
                Vector3 to = f.pos - p;
                if (fb.necro && to.magnitude <= r + SpellPlans.NecroRadius)
                {
                    // Necromatic Blast: swallowed in the middle, as a rock is (water doesn't put it out)
                    WorldNet.ServerFireballEnd(fb.id, p, false);
                    FireballsS.RemoveAt(k);
                    continue;
                }
                bool caught = f.pulledFireballs.Contains(fb.id);
                if (to.magnitude > (caught ? keep : reach) + fb.Radius) { f.pulledFireballs.Remove(fb.id); continue; }
                f.pulledFireballs.Add(fb.id);
                if (!step) continue; // (it goes out when it reaches the water: TickFireballs)
                WorldNet.ServerFireballLaunch(fb.id, p, SpellPlans.AttractPull(fx.velocity, to, f.vel, dt), fx.owner);
                fb.lastPos = p;
            }

            foreach (var v in Volleys)
            {
                if (!net.Missiles.TryGetValue(v.id, out var mfx) || !mfx.launched) continue;
                for (int m = 0; m < mfx.m.Length; m++)
                {
                    var ms = mfx.m[m];
                    if (!ms.alive || ms.owner == mine || (ms.pos - f.pos).magnitude > r + SpellPlans.MissileRadius) continue;
                    ms.alive = false;
                    WorldNet.ServerMissileImpact(v.id, m, ms.pos, default, 0, false);
                }
            }
        }

        /// <summary>
        /// Repulsion's ball, moved from <paramref name="from"/> to where it is now: did it touch the ground (any
        /// solid voxel), a flying lump of ground (a thrown rock), or anyone (not its caster) on the way? Swept in
        /// small steps, so it can't skip through anything at its speed. <paramref name="at"/>: where it touched.
        /// </summary>
        static bool RepelContact(VoxelWorld world, ForceState f, Vector3 from, float r, out Vector3 at)
        {
            Vector3 to = f.pos;
            float len = (to - from).magnitude;
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.05f, r * 0.5f)));
            var net = WorldNet.Instance;
            double now = WorldNet.Now;
            for (int s = 1; s <= steps; s++)
            {
                at = Vector3.Lerp(from, to, s / (float)steps);
                if (world != null && AnySolid(world, at, r)) return true;
                foreach (var c in Combatant.Registry)
                    if (c != null && c != f.caster && c.IsAlive && !Folded(c) && Touching(c, at, r)) return true;
                if (net != null)
                    foreach (var rock in Rocks)
                        if (rock.launched && net.Rocks.TryGetValue(rock.id, out var fx)
                            && (WorldNet.RockPosition(fx, now) - at).magnitude <= r + rock.radius) return true;
            }
            at = to;
            return false;
        }

        /// <summary>Is there any solid voxel (breakable or not) within <paramref name="radius"/> of <paramref name="p"/>?</summary>
        static bool AnySolid(VoxelWorld w, Vector3 p, float radius)
        {
            var c = w.CellOf(p);
            int n = Mathf.CeilToInt(radius / VoxelWorld.Size);
            float lim = (radius / VoxelWorld.Size) * (radius / VoxelWorld.Size);
            for (int x = -n; x <= n; x++)
            for (int y = -n; y <= n; y++)
            for (int z = -n; z <= n; z++)
                if (x * x + y * y + z * z <= lim && w.Get(c.x + x, c.y + y, c.z + z) != 0) return true;
            return false;
        }

        /// <summary>
        /// Repulsion goes off at <paramref name="at"/>: a big blast (<see cref="SpellPlans.RepelBlastRadius"/>) that
        /// blows a crater, scorches everything in it black, shatters flying rocks, and throws every enemy in it
        /// away, ragdolled and set alight twice over.
        /// </summary>
        static void ExplodeRepulsion(int index, Vector3 at)
        {
            var f = ForcesS[index];
            var me = f.caster;
            float R = SpellPlans.RepelBlastRadius;
            WorldNet.ServerRepelBlast(at, R);
            MeltIceNear(at, R + SpellPlans.IceMeltReach);
            FlameSetsOff(at, R);
            // the ground: a crater blown out, then everything in the blast blackened
            var w = VoxelWorld.Current;
            if (w != null)
            {
                var cell = w.CellOf(at);
                WorldNet.ServerSinge(cell, SpellPlans.R4(SpellPlans.RepelCrater) + 2, true);
                if (AnyBreakable(w, at, SpellPlans.RepelCrater))
                    WorldNet.ServerPullSphere(cell, SpellPlans.R4(SpellPlans.RepelCrater), 14f, Vector3.up * 4f);
                WorldNet.ServerSinge(cell, SpellPlans.R4(R));
            }
            // flying rocks in it fall apart
            var net = WorldNet.Instance;
            double now = WorldNet.Now;
            if (net != null)
                for (int k = Rocks.Count - 1; k >= 0; k--)
                {
                    var rock = Rocks[k];
                    if (!rock.launched || !net.Rocks.TryGetValue(rock.id, out var fx)) continue;
                    Vector3 p = WorldNet.RockPosition(fx, now);
                    if ((p - at).magnitude > R + rock.radius) continue;
                    Impact(rock, p, null, 0, null);
                    Rocks.RemoveAt(k);
                }
            // every enemy in it: thrown away and set alight (double burn)
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || c == me || !c.IsAlive || Folded(c) || Shielded(c) || !Touching(c, at, R)) continue;
                Vector3 away = c.transform.position - at;
                away = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.up;
                for (int k = 0; k < SpellPlans.RepelBurns; k++) Burn(c, me, at); // (first: a shield that takes them stops the throw too)
                Hit(c, me, 0f, c.transform.position, away * SpellPlans.RepelKnock + Vector3.up * SpellPlans.RepelUp, SpellPlans.RepelRagdoll, from: at, card: CardId.Repulsion);
            }
            EndForce(index, true);
        }

        /// <summary>
        /// Attraction tearing at the world: everything right round the ball is ripped out, and chunks are torn off
        /// whatever surfaces are within its reach (the ground under it, walls beside it) and flung at it. On every
        /// screen the ball then catches the rubble and swirls it round itself (<see cref="ForceView"/>).
        /// </summary>
        static void TearTowards(VoxelWorld world, ForceState f, float r, float reach)
        {
            float core = r * SpellPlans.AttractCore;
            if (AnyBreakable(world, f.pos, core))
                WorldNet.ServerPullSphere(world.CellOf(f.pos), SpellPlans.R4(Mathf.Max(0.3f, core)), 0f, f.vel);
            for (int k = 0; k < SpellPlans.AttractChunks; k++)
            {
                // mostly downwards and out to the sides: that's where the ground and walls are
                Vector3 dir = Random.onUnitSphere;
                dir.y = -Mathf.Abs(dir.y) * 1.5f + 0.2f;
                dir.Normalize();
                if (!Physics.Raycast(f.pos, dir, out var hit, reach, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                if (!VoxelWorld.IsVoxelCollider(hit.collider)) continue;
                var cell = world.CellOf(hit.point - hit.normal * 0.15f);
                ushort v = world.Get(cell);
                if (v == 0 || world.IsIndestructible(v)) continue;
                // ripped up and thrown at the ball (the ball's own pull takes over from there)
                Vector3 fling = (f.pos - hit.point).normalized * 9f + Vector3.up * 2f + f.vel * 0.5f;
                WorldNet.ServerPullSphere(cell, SpellPlans.R4(SpellPlans.AttractChunk), 0f, fling);
            }
        }

        /// <summary>Is there a destructible voxel within <paramref name="radius"/> of <paramref name="p"/>?</summary>
        static bool AnyBreakable(VoxelWorld w, Vector3 p, float radius)
        {
            var c = w.CellOf(p);
            int n = Mathf.CeilToInt(radius / VoxelWorld.Size);
            float lim = (radius / VoxelWorld.Size) * (radius / VoxelWorld.Size);
            for (int x = -n; x <= n; x++)
            for (int y = -n; y <= n; y++)
            for (int z = -n; z <= n; z++)
            {
                if (x * x + y * y + z * z > lim) continue;
                ushort v = w.Get(c.x + x, c.y + y, c.z + z);
                if (v != 0 && !w.IsIndestructible(v)) return true;
            }
            return false;
        }

        static void EndForce(int index, bool cut)
        {
            var f = ForcesS[index];
            ForcesS.RemoveAt(index);
            WorldNet.ServerForceEnd(f.id, cut);
            if (f.caster != null) f.caster.ServerEndCast(); // the ball's gone: the caster is free to cast again
            // whoever it was holding is let go, still limp for a while
            foreach (var c in f.held.Keys)
                if (c != null && c.IsAlive) { c.ServerCredit(f.caster); c.ServerKnock(f.vel * 0.5f + Vector3.up * 2f, SpellPlans.PullHold); }
        }

        // ------------------------------------------------------------------ the fused ball

        /// <summary>The caster's owner throws the purple ball (along <paramref name="dir"/>, their aim) once it has formed.</summary>
        public static void LaunchFuse(PlayerController pc, int id, Vector3 dir)
        {
            var f = FusesS.Find(x => x.id == id);
            if (f == null || f.caster != pc || f.launched) return;
            if (WorldNet.Now < f.start + SpellPlans.FuseThrowAt(f.over) - 0.15) return;
            DoLaunchFuse(f, dir);
        }

        static void DoLaunchFuse(FuseState f, Vector3 dir)
        {
            var net = WorldNet.Instance;
            if (dir.sqrMagnitude < 1e-4f) dir = f.caster.transform.forward;
            dir.Normalize();
            double now = WorldNet.Now;
            Vector3 origin = net != null && net.Fuses.TryGetValue(f.id, out var fx) ? WorldNet.FusePosition(fx, now)
                : SpellPlans.FuseHoldPoint(f.caster.transform.position, f.caster.ViewYaw, SpellPlans.FuseRadius1);
            f.launched = true;
            f.origin = origin;
            f.dir = dir;
            f.launchTime = now;
            WorldNet.ServerFuseLaunch(f.id, origin, dir);
        }

        static void TickFuses(double now)
        {
            var world = VoxelWorld.Current;
            for (int i = FusesS.Count - 1; i >= 0; i--)
            {
                var f = FusesS[i];
                if (!f.launched)
                {
                    // the caster died forming it: it bursts there and then
                    if (f.caster == null || !f.caster.IsAlive) { FusesS.RemoveAt(i); WorldNet.ServerFuseEnd(f.id); continue; }
                    // their owner never threw it (lag): it goes where they're looking
                    if (now > f.start + SpellPlans.FuseThrowAt(f.over) + 0.4)
                        DoLaunchFuse(f, Quaternion.Euler(f.caster.ViewPitch, f.caster.ViewYaw, 0f) * Vector3.forward);
                    continue;
                }
                float travelled = Mathf.Min(SpellPlans.FuseSpeed * (float)(now - f.launchTime), SpellPlans.FuseRange);
                float R = SpellPlans.FuseRadius1;
                Vector3 a = f.origin + f.dir * f.swept, b = f.origin + f.dir * travelled;

                // every voxel in its way is erased (a ball at each metre along the path)
                while (world != null && f.erased <= travelled)
                {
                    Vector3 p = f.origin + f.dir * f.erased;
                    // (a touch past its edge: the lightning crackling over the surface takes what it touches too)
                    if (AnyBreakable(world, p, R * 1.15f)) WorldNet.ServerErase(world.CellOf(p), SpellPlans.R4(R * 1.15f));
                    f.erased += 0.4f; // close enough together that the tunnel is a smooth tube
                }
                // everyone it passes through: 75, and thrown along with it
                foreach (var c in Combatant.Registry.ToArray())
                {
                    if (c == null || c == f.caster || !c.IsAlive || f.hit.Contains(c) || Folded(c)) continue;
                    float d = Mathf.Min(DistToSegment(c.transform.position, a, b), DistToSegment(c.FeetPosition + Vector3.up * 0.3f, a, b));
                    if (d > R + 0.4f) continue;
                    f.hit.Add(c);
                    // killed by it: the body is missing the round tunnel the ball bored through it
                    Vector3 onPath = f.origin + f.dir * Vector3.Dot(c.transform.position - f.origin, f.dir);
                    Hit(c, f.caster, SpellPlans.FuseDamage, onPath, f.dir * SpellPlans.FuseKnock + Vector3.up * SpellPlans.FuseUp, SpellPlans.FuseRagdoll,
                        bore: f.dir * R);
                }
                // projectiles and other balls in its way are erased too
                SlashProjectiles(a, b, R, f.caster, false);
                for (int k = ForcesS.Count - 1; k >= 0; k--)
                {
                    var o = ForcesS[k];
                    if (o.out_ && DistToSegment(o.pos, a, b) <= R + SpellPlans.ForceRadius1) EndForce(k, true);
                }
                f.swept = travelled;
                if (travelled >= SpellPlans.FuseRange) { FusesS.RemoveAt(i); WorldNet.ServerFuseEnd(f.id); }
            }
        }
    }
}
