using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for the Water cards, and what all water does to fire: a fireball that touches water goes out,
    /// a hellfire wall touching it is put out, burning ground flying through it stops burning, and anyone burning
    /// who touches it is put out. Fire never breaks the water.
    /// </summary>
    public static partial class SpellServer
    {
        class OrbState { public int id; public PlayerController caster; public Vector3 center; public double start; }
        class BubbleState { public PlayerController caster; public double start; public float hp = SpellPlans.BubbleHp; }
        class PendingWave { public PlayerController caster; public Vector3 dir; public double at; }
        class WaveState
        {
            public int id; public PlayerController caster; public Vector3 origin, dir; public double start; public float[] ground;
            /// <summary>Lanes erased by Arcane Judgment (bit per lane).</summary>
            public int dead;
            /// <summary>How far along it had broken the ground last time (-1: not yet).</summary>
            public float broken = -1f;
            public readonly HashSet<Combatant> hit = new();
        }

        static readonly List<OrbState> OrbsS = new();
        static readonly List<BubbleState> BubblesS = new();
        static readonly List<PendingWave> PendingWaves = new();
        static readonly List<WaveState> WavesS = new();

        static void ResetWater()
        {
            OrbsS.Clear();
            BubblesS.Clear();
            PendingWaves.Clear();
            WavesS.Clear();
        }

        static bool CastWater(PlayerController caster, CardId card, Vector3 target, float yaw, ulong targetId, double now)
        {
            switch (card)
            {
                case CardId.WaterOfLife:
                {
                    // where the caster's own screen showed it (a little slack for lag); otherwise work it out here
                    Vector3 root = caster.transform.position;
                    if ((target - (root + Vector3.up * SpellPlans.OrbLift)).magnitude > SpellPlans.OrbReach + 1.5f) target = SpellPlans.OrbSpot(root, yaw);
                    int id = WorldNet.ServerOrb(caster.NetworkObjectId, target);
                    OrbsS.Add(new OrbState { id = id, caster = caster, center = target, start = now });
                    return true;
                }
                case CardId.BubbleAway:
                {
                    if (InBubble(caster)) return false;
                    Combatant.DamageScale ??= ScaleDamage;
                    // cast from the ground (ragdolled): up on your feet and in the bubble at once, no windup
                    double start = targetId == SpellPlans.BubbleFromRagdoll ? now - SpellPlans.BubbleWindup : now;
                    WorldNet.ServerBubble(caster.NetworkObjectId, start);
                    BubblesS.Add(new BubbleState { caster = caster, start = start });
                    return true;
                }
                case CardId.Tsunami:
                {
                    Vector3 dir = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                    PendingWaves.Add(new PendingWave { caster = caster, dir = dir, at = now + SpellPlans.TsunamiWindup });
                    return true;
                }
            }
            return false;
        }

        static void TickWater(WorldNet net, double now, float dt)
        {
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null) continue;
                c.ServerTickHeal(now, dt);
                // water puts out anyone burning
                if (c.IsAlive && c.BurnStacks.Value > 0 && (InWater(c.transform.position, 0.3f) || InWater(c.FeetPosition + Vector3.up * 0.3f, 0.2f)))
                {
                    c.ServerExtinguish();
                    WorldNet.ServerQuench(c.transform.position, 0.7f);
                }
            }
            TickOrbs(net, now);
            TickBubbles(now);
            TickWaves(now);
        }

        // ------------------------------------------------------------------ water vs fire

        /// <summary>Is there water here: an orb (forming or formed), a bubble, a wave?</summary>
        public static bool InWater(Vector3 p, float pad)
        {
            double now = WorldNet.Now;
            foreach (var o in OrbsS)
            {
                float r = SpellPlans.OrbRadiusAt((float)(now - o.start)) + pad;
                if ((p - o.center).sqrMagnitude < r * r) return true;
            }
            foreach (var b in BubblesS)
            {
                if (b.caster == null || !BubbleActive(b, now)) continue;
                float r = SpellPlans.BubbleRadius + pad;
                if ((p - BubbleCenterOf(b)).sqrMagnitude < r * r) return true;
            }
            if (InWaterBall(p, pad)) return true; // an Attraction ball
            if (RisingWater.InAny(p, pad)) return true; // the map's own
            foreach (var s in WavesS)
                if (InWave(s, p, now, pad)) return true;
            return false;
        }

        /// <summary>The first point between a and b that's in water (checked every 20 cm, so fast fire can't skip past it).</summary>
        static bool WaterAlong(Vector3 a, Vector3 b, float pad, out Vector3 at)
        {
            at = b;
            if (OrbsS.Count == 0 && BubblesS.Count == 0 && WavesS.Count == 0 && !AnyWaterBall() && !RisingWater.Any) return false;
            int n = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / 0.2f));
            for (int k = 0; k <= n; k++)
            {
                Vector3 p = Vector3.Lerp(a, b, k / (float)n);
                if (InWater(p, pad)) { at = p; return true; }
            }
            return false;
        }

        /// <summary>Does the risen part of a hellfire wall touch water anywhere?</summary>
        static bool WallInWater(HellfireState h, float lit)
        {
            if (OrbsS.Count == 0 && BubblesS.Count == 0 && WavesS.Count == 0 && !AnyWaterBall() && !RisingWater.Any) return false;
            float total = 0f;
            for (int k = 1; k < h.points.Count; k++) total += Vector3.Distance(h.points[k - 1], h.points[k]);
            float limit = total * lit, along = 0f;
            float pad = SpellPlans.HellfireThick * 0.5f;
            for (int k = 1; k < h.points.Count; k++)
            {
                Vector3 a = h.points[k - 1], b = h.points[k];
                float seg = Vector3.Distance(a, b);
                for (float s = 0f; s <= seg; s += 0.4f)
                {
                    if (along + s > limit) return false;
                    Vector3 p = Vector3.Lerp(a, b, seg > 0f ? s / seg : 0f);
                    if (InWater(p + Vector3.up * 0.3f, pad) || InWater(p + Vector3.up * 1.2f, pad) || InWater(p + Vector3.up * 2.2f, pad)) return true;
                }
                along += seg;
            }
            return false;
        }

        // ------------------------------------------------------------------ Water of Life

        static void TickOrbs(WorldNet net, double now)
        {
            for (int i = OrbsS.Count - 1; i >= 0; i--)
            {
                var o = OrbsS[i];
                float t = (float)(now - o.start);
                float r = SpellPlans.OrbRadiusAt(t);
                if (t < SpellPlans.OrbGrow)
                {
                    // still forming: anything barging through bursts it
                    if (OrbDisturbed(o, net, now, r))
                    {
                        OrbsS.RemoveAt(i);
                        WorldNet.ServerOrbEnd(o.id, 0, 0);
                    }
                    continue;
                }
                if (t > SpellPlans.OrbGrow + SpellPlans.OrbLife)
                {
                    OrbsS.RemoveAt(i);
                    WorldNet.ServerOrbEnd(o.id, 2, 0);
                    continue;
                }
                // formed: whoever touches it first (the caster or anyone else) gets the healing
                foreach (var c in Combatant.Registry)
                {
                    if (c == null || !c.IsAlive || !Touching(c, o.center, r)) continue;
                    c.ServerHeal(SpellPlans.OrbHeal, SpellPlans.OrbHealTime);
                    OrbsS.RemoveAt(i);
                    WorldNet.ServerOrbEnd(o.id, 1, c.NetworkObjectId);
                    break;
                }
            }
        }

        /// <summary>Is a body (feet to head) touching a ball?</summary>
        static bool Touching(Combatant c, Vector3 center, float r)
        {
            Vector3 a = c.FeetPosition + Vector3.up * 0.3f, b = c.FeetPosition + Vector3.up * 1.6f;
            Vector3 ab = b - a;
            float u = Mathf.Clamp01(Vector3.Dot(center - a, ab) / ab.sqrMagnitude);
            return (center - (a + ab * u)).magnitude <= r + 0.35f;
        }

        /// <summary>A forming orb is burst by a projectile, wind, or anyone but its caster going through it. (Fire doesn't: the water puts it out.)</summary>
        static bool OrbDisturbed(OrbState o, WorldNet net, double now, float r)
        {
            Vector3 c = o.center;
            foreach (var who in Combatant.Registry)
                if (who != null && who != o.caster && who.IsAlive && Touching(who, c, r)) return true;
            foreach (var rock in Rocks)
            {
                if (!rock.launched || !net.Rocks.TryGetValue(rock.id, out var fx)) continue;
                float reach = r + rock.radius;
                if ((WorldNet.RockPosition(fx, now) - c).sqrMagnitude < reach * reach) return true;
            }
            foreach (var v in Volleys)
            {
                if (!net.Missiles.TryGetValue(v.id, out var mfx) || !mfx.launched) continue;
                foreach (var m in mfx.m)
                    if (m.alive && (m.pos - c).magnitude < r + SpellPlans.MissileRadius) return true;
            }
            foreach (var g in Gusts)
            {
                Vector3 l = Quaternion.Inverse(Quaternion.LookRotation(g.dir)) * (c - SpellPlans.GustCenter(g.origin, g.dir, g.start, now));
                Vector3 h = SpellPlans.GustHalfSize;
                if (Mathf.Abs(l.x) <= h.x + r && Mathf.Abs(l.y) <= h.y + r && Mathf.Abs(l.z) <= h.z + r) return true;
            }
            foreach (var t in Tornados)
            {
                Vector3 rel = c - SpellPlans.TornadoBase(t.origin, t.dir, t.start, now);
                if (rel.y > -1f && rel.y < SpellPlans.TornadoHeight && new Vector2(rel.x, rel.z).magnitude < SpellPlans.TornadoRadius + r) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ Bubble Away

        static bool BubbleActive(BubbleState b, double now) =>
            now >= b.start + SpellPlans.BubbleWindup && now <= b.start + SpellPlans.BubbleWindup + SpellPlans.BubbleTime;

        static Vector3 BubbleCenterOf(BubbleState b) => SpellPlans.BubbleCenter(b.caster.transform.position);

        static BubbleState BubbleOf(Combatant c, double now, bool windupToo)
        {
            if (c == null) return null;
            foreach (var b in BubblesS)
            {
                if (b.caster != c) continue;
                if (windupToo ? now <= b.start + SpellPlans.BubbleWindup + SpellPlans.BubbleTime : BubbleActive(b, now)) return b;
            }
            return null;
        }

        /// <summary>Floating in their own bubble right now: nothing hurts or moves them; the bubble takes the hits.</summary>
        public static bool Bubbled(Combatant c) => BubbleOf(c, WorldNet.Now, false) != null;

        /// <summary>Casting or riding a Bubble Away (no other cards until it's over).</summary>
        public static bool InBubble(Combatant c) => BubbleOf(c, WorldNet.Now, true) != null;

        /// <summary>Damage meant for someone in their bubble goes into the bubble instead.</summary>
        static bool AbsorbInBubble(Combatant victim, float dmg)
        {
            var b = BubbleOf(victim, WorldNet.Now, false);
            if (b == null) return false;
            HurtBubble(b, dmg, victim.transform.position);
            return true;
        }

        static void HurtBubble(BubbleState b, float dmg, Vector3 point)
        {
            if (b.hp <= 0f || b.caster == null) return;
            b.hp -= dmg;
            if (b.hp <= 0f) EndBubble(b, true);
            else WorldNet.ServerBubbleHit(b.caster.NetworkObjectId, point);
        }

        static void EndBubble(BubbleState b, bool popped)
        {
            BubblesS.Remove(b);
            if (b.caster != null) WorldNet.ServerBubbleEnd(b.caster.NetworkObjectId, popped);
        }

        /// <summary>The active bubble (not <paramref name="owner"/>'s) a projectile at this point has run into, if any.</summary>
        static BubbleState BubbleBlocking(ulong owner, Vector3 p, float pad)
        {
            double now = WorldNet.Now;
            foreach (var b in BubblesS)
            {
                if (b.caster == null || b.caster.NetworkObjectId == owner || !BubbleActive(b, now)) continue;
                float r = SpellPlans.BubbleRadius + pad;
                if ((p - BubbleCenterOf(b)).sqrMagnitude < r * r) return b;
            }
            return null;
        }

        static void TickBubbles(double now)
        {
            for (int i = BubblesS.Count - 1; i >= 0; i--)
            {
                var b = BubblesS[i];
                if (b.caster == null || !b.caster.IsAlive) { EndBubble(b, true); continue; }
                if (now > b.start + SpellPlans.BubbleWindup + SpellPlans.BubbleTime) EndBubble(b, false);
            }
        }

        /// <summary>A bubble's owner reports it rising into voxels: break them (the owner already did on their screen).</summary>
        public static void BubbleBreak(PlayerController pc, Vector3 center)
        {
            var w = VoxelWorld.Current;
            if (w == null || !pc.IsAlive || !InBubble(pc)) return;
            if ((center - SpellPlans.BubbleCenter(pc.transform.position)).magnitude > 2.5f) return;
            SpellPlans.BubbleBreakBox(w, center, out var min, out var max);
            WorldNet.ServerRemoveBox(min, max, 3f);
        }

        /// <summary>Inside an active barrier or bubble: a wave flows round it and breaks nothing there.</summary>
        static bool Sheltered(Vector3 p, double now)
        {
            foreach (var b in Barriers)
            {
                if (!BarrierActive(b, now)) continue;
                if ((p - BarrierCenter(b)).sqrMagnitude < SpellPlans.BarrierRadius * SpellPlans.BarrierRadius) return true;
            }
            foreach (var b in BubblesS)
            {
                if (b.caster == null || !BubbleActive(b, now)) continue;
                if ((p - BubbleCenterOf(b)).sqrMagnitude < SpellPlans.BubbleRadius * SpellPlans.BubbleRadius) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ Tsunami

        static bool InWave(WaveState s, Vector3 p, double now, float pad) =>
            SpellPlans.TsunamiInside(s.origin, s.dir, s.ground, (float)(now - s.start), p, pad, out int lane) && (s.dead & (1 << lane)) == 0;

        static void TickWaves(double now)
        {
            var w = VoxelWorld.Current;
            for (int i = PendingWaves.Count - 1; i >= 0; i--)
            {
                var p = PendingWaves[i];
                if (now < p.at) continue;
                PendingWaves.RemoveAt(i);
                if (p.caster == null || !p.caster.IsAlive || w == null) continue;
                Vector3 origin = p.caster.FeetPosition;
                var ground = SpellPlans.PlanTsunamiGround(w, origin, p.dir);
                int id = WorldNet.ServerTsunami(p.caster.NetworkObjectId, origin, p.dir, ground);
                WavesS.Add(new WaveState { id = id, caster = p.caster, origin = origin, dir = p.dir, start = now, ground = ground });
            }
            for (int i = WavesS.Count - 1; i >= 0; i--)
            {
                var s = WavesS[i];
                float t = (float)(now - s.start);
                if (t > SpellPlans.TsunamiLife + SpellPlans.TsunamiCollapse) { WavesS.RemoveAt(i); continue; }
                EraseLanes(s, now, t);
                if (t > SpellPlans.TsunamiLife) continue; // collapsing: harmless
                HitWithWave(s, now);
                if (w != null) BreakWithWave(s, w, t, now);
            }
        }

        /// <summary>Arcane Judgment's beam erases the lanes of water it touches.</summary>
        static void EraseLanes(WaveState s, double now, float t)
        {
            if (JudgmentsS.Count == 0) return;
            int before = s.dead;
            float front = SpellPlans.TsunamiFront(t);
            Vector3 right = Vector3.Cross(Vector3.up, s.dir);
            foreach (var j in JudgmentsS)
            {
                float jt = (float)(now - j.start);
                if (jt < SpellPlans.JudgmentStartOf(j.kind) || jt > SpellPlans.JudgmentEndOf(j.kind)) continue;
                for (int lane = 0; lane < SpellPlans.TsunamiLanes; lane++)
                {
                    if ((s.dead & (1 << lane)) != 0) continue;
                    for (float behind = 0f; behind <= SpellPlans.TsunamiDepth; behind += 1f)
                    {
                        Vector3 d = s.origin + s.dir * (front - behind) + right * SpellPlans.LaneU(lane) - j.CenterAt(now);
                        if (new Vector2(d.x, d.z).magnitude > SpellPlans.JudgmentRadiusOf(j.kind)) continue;
                        s.dead |= 1 << lane;
                        break;
                    }
                }
            }
            if (s.dead != before) WorldNet.ServerTsunamiErase(s.id, s.dead);
        }

        static void HitWithWave(WaveState s, double now)
        {
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || c == s.caster || !c.IsAlive || s.hit.Contains(c)) continue;
                if (Shielded(c)) continue; // the water parts round a barrier or a bubble
                Vector3 f = c.FeetPosition;
                if (!InWave(s, f + Vector3.up * 0.3f, now, 0.35f) && !InWave(s, c.transform.position, now, 0.35f) && !InWave(s, f + Vector3.up * 1.6f, now, 0.3f)) continue;
                s.hit.Add(c);
                Hit(c, s.caster, SpellPlans.TsunamiDamage, c.transform.position,
                    s.dir * SpellPlans.TsunamiKnock + Vector3.up * SpellPlans.TsunamiKnockUp, SpellPlans.TsunamiRagdoll);
            }
        }

        static readonly HashSet<Vector3Int> WashSet = new();
        static readonly List<Vector3Int> WashList = new();

        /// <summary>Everything standing up in the wave's way (walls, platforms, lumps of ground) is broken and washed forward.</summary>
        static void BreakWithWave(WaveState s, VoxelWorld w, float t, double now)
        {
            if (t < SpellPlans.TsunamiRise * 0.5f) return;
            float front = SpellPlans.TsunamiFront(t);
            // break in 1 m steps (~8 per second): each step is a remesh, an island check and a debris burst on every peer
            if (s.broken >= 0f && front - s.broken < 1f && t < SpellPlans.TsunamiLife - 0.05f) return;
            float from = s.broken < 0f ? SpellPlans.TsunamiAhead : s.broken - 0.3f;
            float to = front + SpellPlans.TsunamiLip;
            s.broken = front;
            Vector3 right = Vector3.Cross(Vector3.up, s.dir);
            const float S = VoxelWorld.Size;
            const float half = SpellPlans.TsunamiWidth * 0.5f;
            WashSet.Clear();
            WashList.Clear();
            for (float a = from; a <= to; a += S * 0.5f)
            {
                float floor = SpellPlans.TsunamiFloor(s.ground, a);
                float behind = front - a;
                float top = behind < 0f ? SpellPlans.TsunamiHeight : SpellPlans.TsunamiProfile(behind);
                Vector3 col = s.origin + s.dir * a;
                for (float u = -half + S * 0.25f; u < half; u += S * 0.5f)
                {
                    if ((s.dead & (1 << SpellPlans.LaneOf(u))) != 0) continue;
                    Vector3 xz = col + right * u;
                    for (float y = floor + S * 0.5f; y <= floor + top + S * 0.5f; y += S)
                    {
                        var cell = w.CellOf(new Vector3(xz.x, y, xz.z));
                        if (WashSet.Contains(cell)) continue;
                        ushort v = w.Get(cell);
                        if (v == 0 || w.IsIndestructible(v)) continue;
                        Vector3 cc = w.CellCenter(cell);
                        if (cc.y < floor || Sheltered(cc, now)) continue;
                        WashSet.Add(cell);
                        WashList.Add(cell);
                    }
                }
            }
            if (WashList.Count > 0) WorldNet.ServerWash(WashList, s.dir);
        }
    }
}
