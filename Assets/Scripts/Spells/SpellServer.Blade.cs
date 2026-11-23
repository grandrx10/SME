using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for the Blade cards.
    ///  - Severance: a rush (as Mana Siphon) that ends in a cut — when someone comes into reach (they're hit at once)
    ///    or after a second. The cut flies 10 m as a crescent, cutting through up to 4 voxels deep, and everyone it
    ///    touches (once each) is thrown, ragdolled and slashed three times.
    ///  - Flowing Cut: charged (1-3 charges), then a 12 m teleport straight ahead, untouchable for its 0.2 s,
    ///    cutting the voxels in the way, the projectiles along it, and everyone near the line (three slashes each).
    /// </summary>
    public static partial class SpellServer
    {
        class SeveranceState { public PlayerController caster; public double start; }
        class CutState
        {
            public int id; public PlayerController caster; public Vector3 origin, dir; public Quaternion rot; public double start;
            public float swept;   // metres of its path already checked for people
            public float carved;  // metres of its path already cut through voxels
            public int depth;     // voxel layers it has cut so far
            public readonly HashSet<Combatant> hit = new();
        }
        class FlurryState
        {
            public Combatant victim; public PlayerController attacker; public float damage; public int left; public double next;
            public Vector3 knock; public float ragdoll; public float yaw;
        }
        class FlowState { public PlayerController caster; public Vector3 from, to; public double start; public float damage; public int charges; public readonly HashSet<Combatant> hit = new(); }

        static readonly List<SeveranceState> SeverancesS = new();
        static readonly List<CutState> CutsS = new();
        static readonly List<FlurryState> Flurries = new();
        static readonly List<FlowState> FlowsS = new();

        static void ResetBlade()
        {
            SeverancesS.Clear();
            CutsS.Clear();
            Flurries.Clear();
            FlowsS.Clear();
        }

        static bool CastBlade(PlayerController caster, CardId card, Vector3 target, ulong extra, double now)
        {
            switch (card)
            {
                case CardId.Severance:
                    SeverancesS.RemoveAll(s => s.caster == caster);
                    SeverancesS.Add(new SeveranceState { caster = caster, start = now });
                    return true;
                case CardId.FlowingCut:
                {
                    Vector3 from = caster.FeetPosition;
                    Vector3 flat = target - from;
                    if (Mathf.Abs(flat.y) > 1.5f) return false;
                    flat.y = 0f;
                    if (flat.magnitude > SpellPlans.FlowDistance + 2f) return false; // a little slack for lag
                    Vector3 to = new(target.x, from.y, target.z);
                    int charges = Mathf.Clamp((int)extra, 1, SpellPlans.FlowMaxCharges);
                    Combatant.DamageScale ??= ScaleDamage; // so being untouchable mid-teleport counts
                    // the way is cut open first (the caster is already on the move on their own screen)
                    var w = VoxelWorld.Current;
                    var cells = SpellPlans.FlowCutCells(w, from, to);
                    if (cells.Count > 0) WorldNet.ServerBladeCut(cells, flat.sqrMagnitude > 1e-4f ? flat.normalized : caster.transform.forward);
                    WorldNet.ServerFlowCut(caster.NetworkObjectId, from, to);
                    FlowsS.RemoveAll(f => f.caster == caster);
                    FlowsS.Add(new FlowState { caster = caster, from = from, to = to, start = now, damage = SpellPlans.FlowDamage(charges), charges = charges });
                    return true;
                }
            }
            return false;
        }

        /// <summary>Flowing Cut's teleport is under way: untouchable.</summary>
        static bool Flowing(Combatant c, double now)
        {
            foreach (var f in FlowsS)
                if (f.caster == c && now >= f.start && now <= f.start + SpellPlans.FlowTime + 0.05) return true;
            return false;
        }

        /// <summary>Thrown out of the rush: no cut.</summary>
        static void InterruptBlade(PlayerController pc, bool knocked)
        {
            if (knocked) SeverancesS.RemoveAll(s => s.caster == pc);
        }

        static void TickBlade(double now)
        {
            TickSeverances(now);
            TickCuts(now);
            TickFlows(now);
            TickFlurries(now);
        }

        // ------------------------------------------------------------------ Severance: the rush

        static void TickSeverances(double now)
        {
            for (int i = SeverancesS.Count - 1; i >= 0; i--)
            {
                var s = SeverancesS[i];
                var me = s.caster;
                float t = (float)(now - s.start);
                if (me == null || !me.IsAlive) { SeverancesS.RemoveAt(i); continue; }
                // the way the rush is actually going (it steers after the mouse), else where they're looking
                Vector3 vel = me.ViewVelocity;
                vel.y = 0f;
                Vector3 fwd = vel.magnitude > 5f ? vel.normalized : Quaternion.Euler(0, me.ViewYaw, 0) * Vector3.forward;
                var struck = t >= SpellPlans.SeveranceArm ? InReach(me, fwd) : new List<Combatant>();
                if (struck.Count == 0 && t < SpellPlans.SeveranceTime) continue;

                // the cut: the sword is swept across, and the crescent leaves it
                SeverancesS.RemoveAt(i);
                me.ServerSeveranceSlash();
                double leave = now + SpellPlans.SeveranceSlashAt;
                Vector3 origin = me.transform.position + Vector3.up * 0.35f + fwd * 0.7f;
                float tilt = Random.Range(-SpellPlans.SeveranceTilt, SpellPlans.SeveranceTilt);
                int id = WorldNet.ServerWave(me.NetworkObjectId, origin, fwd, tilt, leave);
                var wave = new CutState { id = id, caster = me, origin = origin, dir = fwd, rot = SpellPlans.WaveRotation(fwd, tilt), start = leave };
                CutsS.Add(wave);
                // whoever walked into reach and set it off is hit by it there and then
                foreach (var o in struck)
                {
                    wave.hit.Add(o);
                    StrikeWithWave(o, wave);
                }
            }
        }

        /// <summary>Everyone in the slab in front of a rushing caster (as Mana Siphon's).</summary>
        static List<Combatant> InReach(PlayerController me, Vector3 fwd)
        {
            var list = new List<Combatant>();
            Vector3 right = new(fwd.z, 0f, -fwd.x);
            foreach (var o in Combatant.Registry)
            {
                if (o == null || o == me || !o.IsAlive || Folded(o)) continue;
                Vector3 rel = o.transform.position - me.transform.position;
                float along = Vector3.Dot(rel, fwd);
                if (along < -0.4f || along > SpellPlans.SiphonReach) continue;
                if (Mathf.Abs(Vector3.Dot(rel, right)) > SpellPlans.SiphonHalfWidth || Mathf.Abs(rel.y) > SpellPlans.SiphonHalfHeight) continue;
                list.Add(o);
            }
            return list;
        }

        static void StrikeWithWave(Combatant o, CutState w)
        {
            Vector3 away = o.transform.position - w.origin;
            away.y = 0f;
            away = away.sqrMagnitude > 0.01f ? Vector3.Lerp(w.dir, away.normalized, 0.4f).normalized : w.dir;
            StartFlurry(o, w.caster, SpellPlans.SeveranceDamage, 0.0,
                away * SpellPlans.SeveranceKnockOut + Vector3.up * SpellPlans.SeveranceKnockUp, SpellPlans.SeveranceRagdoll);
        }

        // ------------------------------------------------------------------ Severance: the cut in flight

        static void TickCuts(double now)
        {
            var world = VoxelWorld.Current;
            for (int i = CutsS.Count - 1; i >= 0; i--)
            {
                var w = CutsS[i];
                float t = (float)(now - w.start);
                if (t < 0f) continue;
                float front = Mathf.Min(SpellPlans.SeveranceSpeed * t, SpellPlans.SeveranceRange);
                Vector3 center = w.origin + w.dir * front;
                ulong owner = w.caster != null ? w.caster.NetworkObjectId : ulong.MaxValue;

                // an enemy barrier: it breaks on it; a bubble takes the hit
                if (BarrierBlocks(owner, center, 0.5f)) { EndCut(i, w); continue; }
                var bubble = BubbleBlocking(owner, center, 0.5f);
                if (bubble != null)
                {
                    HurtBubble(bubble, SpellPlans.SeveranceDamage * SpellPlans.FlurryStrikes, center);
                    EndCut(i, w);
                    continue;
                }

                // voxels, layer by layer (0.25 m at a time) along its path, before it reaches anyone behind them
                bool spent = false;
                var cut = new List<Vector3Int>();
                while (world != null && w.carved + VoxelWorld.Size <= front + 1e-3f)
                {
                    w.carved += VoxelWorld.Size;
                    var layer = WaveLayer(world, w, w.carved, out bool hard);
                    if (layer.Count == 0 && !hard) continue;
                    w.depth++;
                    if (w.depth > SpellPlans.SeveranceMaxDepth) { spent = true; break; }
                    cut.AddRange(layer);
                    if (hard) { spent = true; break; } // something that can't be cut: it breaks on it
                }
                if (cut.Count > 0) WorldNet.ServerBladeCut(cut, w.dir);
                float reach = spent ? Mathf.Min(front, w.carved) : front;

                // people it sweeps through (each only once)
                foreach (var c in Combatant.Registry.ToArray())
                {
                    if (c == null || c == w.caster || !c.IsAlive || w.hit.Contains(c) || Folded(c)) continue;
                    Vector3 p = c.transform.position;
                    bool touch = SpellPlans.WaveTouches(w.origin, w.rot, w.swept, reach, p, 0.4f)
                              || SpellPlans.WaveTouches(w.origin, w.rot, w.swept, reach, p + Vector3.up * 0.65f, 0.35f)
                              || SpellPlans.WaveTouches(w.origin, w.rot, w.swept, reach, c.FeetPosition + Vector3.up * 0.3f, 0.35f);
                    if (!touch) continue;
                    w.hit.Add(c);
                    StrikeWithWave(c, w);
                }
                w.swept = reach;

                // other blades' cuts it crosses are cut too; then it's gone where it was spent, or at 10 m
                if (spent) { EndCut(i, w); continue; }
                if (front >= SpellPlans.SeveranceRange) CutsS.RemoveAt(i);
            }
        }

        static void EndCut(int index, CutState w)
        {
            CutsS.RemoveAt(index);
            WorldNet.ServerWaveEnd(w.id);
        }

        /// <summary>
        /// The voxels the crescent's band covers with its middle <paramref name="d"/> metres along (a curved sheet one cell
        /// deep). <paramref name="hard"/>: it met something it can't cut.
        /// </summary>
        static List<Vector3Int> WaveLayer(VoxelWorld world, CutState w, float d, out bool hard)
        {
            hard = false;
            var set = new HashSet<Vector3Int>();
            var list = new List<Vector3Int>();
            int across = Mathf.CeilToInt(SpellPlans.SeveranceHalfWidth * 2f / (VoxelWorld.Size * 0.7f));
            for (int k = 0; k <= across; k++)
            {
                float s = -1f + 2f * k / across;
                for (int j = -1; j <= 1; j++)
                {
                    Vector3 local = SpellPlans.WaveArc(s) + new Vector3(0f, j * SpellPlans.SeveranceHalfHeight * 0.8f, d);
                    var cell = world.CellOf(w.origin + w.rot * local);
                    if (!set.Add(cell)) continue;
                    ushort v = world.Get(cell);
                    if (v == 0) continue;
                    if (world.IsIndestructible(v)) { hard = true; continue; }
                    list.Add(cell);
                }
            }
            return list;
        }

        // ------------------------------------------------------------------ Flowing Cut

        static void TickFlows(double now)
        {
            for (int i = FlowsS.Count - 1; i >= 0; i--)
            {
                var f = FlowsS[i];
                float t = (float)(now - f.start);
                if (f.caster == null || t > SpellPlans.FlowTime + 0.05f) { FlowsS.RemoveAt(i); continue; }
                Vector3 a = f.from, b = f.to;
                // everyone near the line (generous), struck as the blade arrives at the far end
                foreach (var c in Combatant.Registry.ToArray())
                {
                    if (c == null || c == f.caster || !c.IsAlive || f.hit.Contains(c) || Folded(c)) continue;
                    if (!NearPath(a, b, c.FeetPosition, SpellPlans.FlowHalfWidth)) continue;
                    f.hit.Add(c);
                    // thrown on the way the cut went (and a little out from its line), up, and ragdolled: harder with more charge
                    Vector3 along = b - a;
                    along.y = 0f;
                    along = along.sqrMagnitude > 1e-4f ? along.normalized : f.caster.transform.forward;
                    Vector3 side = c.transform.position - a;
                    side = Vector3.ProjectOnPlane(new Vector3(side.x, 0f, side.z), along);
                    Vector3 dir = (along + (side.sqrMagnitude > 1e-4f ? side.normalized * 0.35f : Vector3.zero)).normalized;
                    Vector3 knock = dir * SpellPlans.FlowKnockOut(f.charges) + Vector3.up * SpellPlans.FlowKnockUp(f.charges);
                    StartFlurry(c, f.caster, f.damage, f.start + SpellPlans.FlowTime - now, knock, SpellPlans.FlowRagdoll(f.charges));
                }
                // projectiles crossing the path while the blade passes: cut out of the air
                SlashProjectiles(a + Vector3.up * 1f, b + Vector3.up * 1f, SpellPlans.FlowHalfWidth, f.caster);
            }
        }

        /// <summary>Is a body (feet at <paramref name="feet"/>) within <paramref name="half"/> of the level path a → b (feet), at about its height?</summary>
        static bool NearPath(Vector3 a, Vector3 b, Vector3 feet, float half)
        {
            Vector3 ab = b - a;
            ab.y = 0f;
            Vector3 ap = feet - a;
            float len2 = ab.sqrMagnitude;
            float u = len2 > 1e-4f ? Mathf.Clamp01(Vector3.Dot(new Vector3(ap.x, 0f, ap.z), ab) / len2) : 0f;
            Vector3 closest = a + ab * u;
            Vector3 d = feet - closest;
            if (d.y < -2f || d.y > 2.2f) return false;
            d.y = 0f;
            return d.magnitude <= half;
        }

        static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float u = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (p - (a + ab * u)).magnitude;
        }

        /// <summary>Cuts down everyone else's projectiles near the segment (rocks, fireballs, missiles, Severance cuts).</summary>
        static void SlashProjectiles(Vector3 a, Vector3 b, float r, PlayerController by, bool slash = true)
        {
            var net = WorldNet.Instance;
            if (net == null) return;
            double now = WorldNet.Now;
            Vector3 dir = (b - a).sqrMagnitude > 1e-4f ? (b - a).normalized : Vector3.forward;
            Vector3 across = Vector3.Cross(Vector3.up, dir);
            for (int i = Rocks.Count - 1; i >= 0; i--)
            {
                var rock = Rocks[i];
                if (!rock.launched || rock.caster == by || !net.Rocks.TryGetValue(rock.id, out var fx)) continue;
                Vector3 p = WorldNet.RockPosition(fx, now);
                if (DistToSegment(p, a, b) > r + rock.radius) continue;
                Impact(rock, p, null, 0, null); // it falls apart in the air
                Rocks.RemoveAt(i);
                if (slash) WorldNet.ServerSlash(p, across, 1f);
            }
            for (int i = FireballsS.Count - 1; i >= 0; i--)
            {
                var f = FireballsS[i];
                if (!f.launched || !net.Fireballs.TryGetValue(f.id, out var fx) || fx.owner == by.NetworkObjectId) continue;
                Vector3 p = WorldNet.FireballPosition(fx, now);
                if (DistToSegment(p, a, b) > r + SpellPlans.FireballRadius) continue;
                WorldNet.ServerFireballEnd(f.id, p, true);
                FireballsS.RemoveAt(i);
                if (slash) WorldNet.ServerSlash(p, across, 1f);
            }
            foreach (var v in Volleys)
            {
                if (!v.launched || !net.Missiles.TryGetValue(v.id, out var mfx)) continue;
                for (int m = 0; m < mfx.m.Length; m++)
                {
                    var ms = mfx.m[m];
                    if (!ms.alive || ms.owner == by.NetworkObjectId || DistToSegment(ms.pos, a, b) > r + SpellPlans.MissileRadius) continue;
                    ms.alive = false;
                    WorldNet.ServerMissileImpact(v.id, m, ms.pos, default, 0, true);
                }
            }
            double t0 = now;
            for (int i = CutsS.Count - 1; i >= 0; i--)
            {
                var w = CutsS[i];
                if (w.caster == by || t0 < w.start) continue;
                Vector3 c = w.origin + w.dir * Mathf.Min(SpellPlans.SeveranceSpeed * (float)(t0 - w.start), SpellPlans.SeveranceRange);
                if (DistToSegment(c, a, b) > r + SpellPlans.SeveranceHalfWidth * 0.5f) continue;
                EndCut(i, w);
                if (slash) WorldNet.ServerSlash(c, across, 1.2f);
            }
        }

        // ------------------------------------------------------------------ the strikes

        /// <summary>Three slashes on <paramref name="victim"/>, <see cref="SpellPlans.FlurryGap"/> apart, starting in <paramref name="delay"/> s. The first one carries the knock.</summary>
        static void StartFlurry(Combatant victim, PlayerController attacker, float damage, double delay, Vector3 knock, float ragdoll)
        {
            Flurries.Add(new FlurryState
            {
                victim = victim, attacker = attacker, damage = damage, left = SpellPlans.FlurryStrikes, next = WorldNet.Now + System.Math.Max(0.0, delay),
                knock = knock, ragdoll = ragdoll, yaw = Random.Range(0f, 360f),
            });
        }

        static void TickFlurries(double now)
        {
            for (int i = Flurries.Count - 1; i >= 0; i--)
            {
                var f = Flurries[i];
                if (f.victim == null || !f.victim.IsAlive) { Flurries.RemoveAt(i); continue; }
                if (now < f.next) continue;
                bool first = f.left == SpellPlans.FlurryStrikes;
                Vector3 point = f.victim.transform.position + Vector3.up * 0.25f + Random.insideUnitSphere * 0.25f;
                // spread over half a turn, so the slashes cross rather than stack (as in The Last Chromatist)
                f.yaw += 180f / SpellPlans.FlurryStrikes + Random.Range(-15f, 15f);
                Vector3 dir = Quaternion.Euler(Random.Range(-35f, 35f), f.yaw, 0f) * Vector3.forward;
                WorldNet.ServerSlash(point, dir, 1f);
                // a killing slash cuts them in two along it: the plane holds the slash line, tipped as near level as it allows
                Vector3 cut = Vector3.ProjectOnPlane(Vector3.up, dir);
                if (cut.sqrMagnitude < 1e-4f) cut = Vector3.Cross(dir, Vector3.right);
                Hit(f.victim, f.attacker, f.damage, point, first ? f.knock : Vector3.zero, first ? f.ragdoll : 0f, cut.normalized);
                f.left--;
                f.next += SpellPlans.FlurryGap;
                if (f.left <= 0) Flurries.RemoveAt(i);
            }
        }
    }
}
