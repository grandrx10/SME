using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for Hammerang: 0.3 s with the left hand out (knocked down meanwhile: nothing), then the hammer flies
    /// straight along the aim for 1 s and comes back to its thrower (through walls), 15 to anyone it meets going out and
    /// again coming back (each once each way). Going out, anything that stops it (ground, a barrier, a bubble, a
    /// Hierophant wall, someone's body) knocks it down: it falls, lies there (1 s, or only a moment after hitting someone),
    /// then comes back. Once thrown it always comes back, whatever happens to its thrower meanwhile (unless they die).
    /// </summary>
    public static partial class SpellServer
    {
        class RangState
        {
            public int id; public PlayerController caster; public ulong owner; public double start, phaseAt, restUntil, nextSend;
            public byte phase; public Vector3 pos, vel, dir; public float rest;
            public readonly HashSet<Combatant> hitOut = new(), hitBack = new();
        }

        static readonly List<RangState> RangsS = new();
        static readonly Collider[] _rangBodies = new Collider[16];

        static bool CastRang(PlayerController caster, double now)
        {
            RangsS.RemoveAll(r => r.caster == caster && r.phase == WorldNet.RangWindup);
            RangsS.Add(new RangState { caster = caster, owner = caster.NetworkObjectId, start = now, phase = WorldNet.RangWindup });
            return true;
        }

        /// <summary>Knocked down with the hand still out: no throw. (Once it's thrown, nothing stops it coming back.)</summary>
        static void InterruptRang(PlayerController pc, bool knocked)
        {
            if (knocked) RangsS.RemoveAll(r => r.caster == pc && r.phase == WorldNet.RangWindup);
        }

        static void ClearRang(PlayerController pc, System.Func<Vector3, bool> inside)
        {
            foreach (var r in RangsS.ToArray())
                if (r.caster == pc || inside(r.pos)) EndRang(r, false);
        }

        static void EndRang(RangState r, bool caught)
        {
            if (!RangsS.Remove(r)) return;
            if (r.id != 0) WorldNet.ServerRangEnd(r.id, r.pos, caught);
        }

        static void TickRangs(double now, float dt)
        {
            for (int i = RangsS.Count - 1; i >= 0; i--)
            {
                if (i >= RangsS.Count) continue;
                var r = RangsS[i];
                var me = r.caster;
                if (me == null || !me.IsAlive) { EndRang(r, false); continue; }
                if (r.phase == WorldNet.RangWindup)
                {
                    if (now >= r.start + SpellPlans.RangCast) ThrowRang(r, now);
                    continue;
                }
                if (now - r.start > SpellPlans.RangLife) { EndRang(r, false); continue; }
                byte was = r.phase;
                switch (r.phase)
                {
                    case WorldNet.RangFlyOut: RangOut(r, now, dt); break;
                    case WorldNet.RangFalling: RangFall(r, now, dt); break;
                    case WorldNet.RangResting: if (now >= r.restUntil) ComeBack(r, now); break;
                    case WorldNet.RangBack: RangBack(r, now, dt); break;
                }
                if (!RangsS.Contains(r)) continue;
                if (r.phase != was || now >= r.nextSend)
                {
                    r.nextSend = now + 1.0 / 30.0;
                    WorldNet.ServerRangMove(r.id, r.pos, r.phase);
                }
            }
        }

        static void ThrowRang(RangState r, double now)
        {
            var me = r.caster;
            Vector3 chest = SpellPlans.RangHome(me), from = SpellPlans.RangFrom(me);
            if (Physics.Linecast(chest, from, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) from = chest; // (not poked through a wall)
            r.dir = ViewDir(me);
            r.pos = from;
            r.phase = WorldNet.RangFlyOut;
            r.phaseAt = now;
            r.id = WorldNet.ServerRangThrow(r.owner, from, r.dir);
            if (r.id == 0) RangsS.Remove(r);
            r.nextSend = now + 1.0 / 30.0;
        }

        /// <summary>Going out: straight on, until it's been out a second or something stops it.</summary>
        static void RangOut(RangState r, double now, float dt)
        {
            float rad = SpellPlans.RangRadius;
            Vector3 next = r.pos + r.dir * (SpellPlans.RangSpeed * dt);
            if (RangBlocked(r, r.pos, next)) return;
            ShotSetsOff(r.pos, next, rad);
            // someone in the way: hurt, and it drops
            var victim = RangBody(r, next, r.hitOut);
            if (victim != null)
            {
                r.hitOut.Add(victim);
                Vector3 at = victim.transform.position;
                WorldNet.ServerHammerHit(at, r.dir);
                Hit(victim, r.caster, SpellPlans.RangDamage, at, RangKnockFor(r.dir), SpellPlans.RangRagdoll, from: r.pos);
                KnockDown(r, now, SpellPlans.RangHitRest);
                return;
            }
            // ground in the way (a little smaller than the hammer, so the floor it skims doesn't stop it)
            Vector3 step = next - r.pos;
            if (Physics.SphereCast(r.pos, rad * 0.5f, r.dir, out var hit, step.magnitude, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
            {
                r.pos = hit.point + hit.normal * (rad * 0.5f);
                WorldNet.ServerHammerHit(hit.point, r.dir);
                KnockDown(r, now, SpellPlans.RangRest);
                return;
            }
            r.pos = next;
            if (now - r.phaseAt >= SpellPlans.RangOut) ComeBack(r, now);
        }

        /// <summary>A barrier, a bubble or a Hierophant wall in the way: it's knocked down. True if so.</summary>
        static bool RangBlocked(RangState r, Vector3 from, Vector3 to)
        {
            float rad = SpellPlans.RangRadius;
            bool blocked = BarrierBlocks(r.owner, to, rad);
            if (!blocked)
            {
                var bubble = BubbleBlocking(r.owner, to, rad);
                if (bubble != null) { HurtBubble(bubble, SpellPlans.RangDamage, to); blocked = true; }
            }
            if (!blocked)
            {
                double now = WorldNet.Now;
                foreach (var h in HierophantsS)
                {
                    if (h.caster == null || h.caster == r.caster || now < h.Up || now > h.End) continue;
                    if (!SpellPlans.HierophantTouch(from, to, rad, h.center, h.rot, out var at, out _)) continue;
                    WorldNet.ServerHierophantReflect(h.id, at);
                    blocked = true;
                    break;
                }
            }
            if (!blocked) return false;
            WorldNet.ServerHammerHit(to, -(to - from).normalized);
            KnockDown(r, WorldNet.Now, SpellPlans.RangRest);
            return true;
        }

        /// <summary>The nearest body the hammer is touching at <paramref name="p"/> that it may hurt (not its thrower, not one already hit).</summary>
        static Combatant RangBody(RangState r, Vector3 p, HashSet<Combatant> already)
        {
            int n = Physics.OverlapSphereNonAlloc(p, SpellPlans.RangRadius, _rangBodies, 1 << Layers.Hitbox, QueryTriggerInteraction.Collide);
            Combatant best = null;
            float bestD = float.MaxValue;
            for (int k = 0; k < n; k++)
            {
                var hb = _rangBodies[k] != null ? _rangBodies[k].GetComponent<Hitbox>() : null;
                var c = hb != null ? hb.Owner : null;
                if (c == null || c == r.caster || !c.IsAlive || already.Contains(c) || Folded(c) || Combatant.Allies(r.caster, c)) continue;
                float d = (c.transform.position - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        /// <summary>Whoever it hits is thrown the way it was flying (level), and up a little.</summary>
        static Vector3 RangKnockFor(Vector3 dir)
        {
            Vector3 flat = new(dir.x, 0f, dir.z);
            return (flat.sqrMagnitude > 0.01f ? flat.normalized : Vector3.zero) * SpellPlans.RangKnock + Vector3.up * SpellPlans.RangKnockUp;
        }

        /// <summary>Stopped: it bounces back a little and falls, then lies there <paramref name="rest"/> s before coming back.</summary>
        static void KnockDown(RangState r, double now, float rest)
        {
            r.phase = WorldNet.RangFalling;
            r.phaseAt = now;
            r.rest = rest;
            r.vel = -r.dir * 2.5f + Vector3.up * 3f;
        }

        static void RangFall(RangState r, double now, float dt)
        {
            r.vel += Vector3.down * (SpellPlans.RangGravity * dt);
            Vector3 step = r.vel * dt;
            const float lie = 0.18f; // (lying on its side: its head this high off the ground)
            if (Physics.SphereCast(r.pos, lie, step.normalized, out var hit, step.magnitude, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
            {
                r.pos = hit.point + hit.normal * lie;
                r.phase = WorldNet.RangResting;
                r.restUntil = now + r.rest;
                return;
            }
            r.pos += step;
            // nothing under it (off the edge of the map): straight back
            if (now - r.phaseAt > 2.0 || r.pos.y < Practice.KillYAt(r.pos)) ComeBack(r, now);
        }

        static void ComeBack(RangState r, double now)
        {
            r.phase = WorldNet.RangBack;
            r.phaseAt = now;
            r.hitBack.Clear();
        }

        /// <summary>Coming back: straight at its thrower's chest (through walls), speeding up; caught when it gets there.</summary>
        static void RangBack(RangState r, double now, float dt)
        {
            var me = r.caster;
            Vector3 to = SpellPlans.RangHome(me) - r.pos;
            float d = to.magnitude;
            if (d <= SpellPlans.RangCatch) { EndRang(r, true); return; }
            float speed = SpellPlans.RangBackSpeed * Mathf.Clamp(((float)(now - r.phaseAt)) / SpellPlans.RangBackRamp, 0.1f, 1f);
            Vector3 dir = to / d;
            Vector3 next = r.pos + dir * Mathf.Min(speed * dt, d);
            r.dir = dir;
            if (RangBlocked(r, r.pos, next)) return;
            // anyone on the way back: hurt (it carries on)
            for (var victim = RangBody(r, next, r.hitBack); victim != null; victim = RangBody(r, next, r.hitBack))
            {
                r.hitBack.Add(victim);
                WorldNet.ServerHammerHit(victim.transform.position, dir);
                Hit(victim, me, SpellPlans.RangDamage, victim.transform.position, RangKnockFor(dir), SpellPlans.RangRagdoll, from: r.pos);
            }
            r.pos = next;
        }
    }
}
