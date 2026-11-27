using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for Death (reworked).
    ///  - The fist drawn back 0.3 s, then a charge twice as long as Mana Siphon's (steered a little by the mouse). It doesn't stop
    ///    at the first enemy: everyone it goes through is ragdolled, thrown and marked for death (for good: until they die). An
    ///    arcane shield held towards it stops it, but is used up entirely.
    ///  - Struck while marked: doomed. The body turns black and vomits black bile for 3 s (the ground it lands on is coated, as
    ///    Icy Breath's frost is, and slows anyone on it to half speed, unless they hold Death), then bursts: every breakable
    ///    voxel within 3 m goes, everything within 5 m is coated in bile, and everyone within 5 m is marked (only marked: it
    ///    never dooms).
    /// </summary>
    public static partial class SpellServer
    {
        const byte BileKind = 2; // (a breath: see BreathView)

        class DeathDashState { public PlayerController caster; public double start; public readonly HashSet<Combatant> hit = new(); }
        class BileState { public int id; public Combatant who; public PlayerController by; public double end, nextCheck; }

        static readonly List<DeathDashState> DeathDashesS = new();
        static readonly List<BileState> BilesS = new();
        /// <summary>Cells coated in bile: what they were, and when they were last coated.</summary>
        static readonly Dictionary<Vector3Int, (ushort was, double at)> Biled = new();
        static double _nextDry;
        static readonly List<Vector3Int> _bileCells = new();
        static readonly List<ushort> _dryTo = new();

        static void ResetDeath()
        {
            DeathDashesS.Clear();
            BilesS.Clear();
            Biled.Clear();
            _nextDry = 0;
        }

        static bool CastDeath(PlayerController caster, double now)
        {
            DeathDashesS.RemoveAll(d => d.caster == caster);
            DeathDashesS.Add(new DeathDashState { caster = caster, start = now });
            return true;
        }

        static void TickDeaths(double now)
        {
            TickDeathDashes(now);
            TickBiles(now);
            TickDry(now);
        }

        /// <summary>Knocked down: the charge is over.</summary>
        static void InterruptDeath(PlayerController pc, bool knocked)
        {
            if (knocked) DeathDashesS.RemoveAll(d => d.caster == pc);
        }

        /// <summary>A body died (or its chamber was reset): its charge, and its vomiting, stop (no burst).</summary>
        static void ForgetDeath(Combatant c)
        {
            DeathDashesS.RemoveAll(d => d.caster == c);
            foreach (var b in BilesS.ToArray()) if (b.who == c) EndBile(b);
        }

        // ------------------------------------------------------------------ the charge

        static void TickDeathDashes(double now)
        {
            for (int i = DeathDashesS.Count - 1; i >= 0; i--)
            {
                if (i >= DeathDashesS.Count) continue;
                var s = DeathDashesS[i];
                var me = s.caster;
                float t = (float)(now - s.start);
                if (me == null || !me.IsAlive || t > SpellPlans.DeathDashTime + 0.1f) { DeathDashesS.RemoveAt(i); continue; }
                if (t < SpellPlans.DeathArm) continue;
                // the way the charge is actually going (it steers behind the mouse), else where they're looking
                Vector3 vel = me.ViewVelocity;
                vel.y = 0f;
                Vector3 fwd = vel.magnitude > 5f ? vel.normalized : Quaternion.Euler(0, me.ViewYaw, 0) * Vector3.forward;
                Vector3 right = new(fwd.z, 0f, -fwd.x);
                // Mana Siphon's slab in front, but it goes on through
                foreach (var o in Combatant.Registry.ToArray())
                {
                    if (o == null || o == me || !o.IsAlive || s.hit.Contains(o) || Combatant.Allies(me, o) || Folded(o)) continue;
                    Vector3 rel = o.transform.position - me.transform.position;
                    float along = Vector3.Dot(rel, fwd);
                    if (along < -0.4f || along > SpellPlans.SiphonReach) continue;
                    if (Mathf.Abs(Vector3.Dot(rel, right)) > SpellPlans.SiphonHalfWidth || Mathf.Abs(rel.y) > SpellPlans.SiphonHalfHeight) continue;
                    s.hit.Add(o);
                    DeathStrike(me, o, fwd, now);
                }
            }
        }

        static void DeathStrike(PlayerController me, Combatant o, Vector3 fwd, double now)
        {
            Vector3 from = me.transform.position;
            if (Shielded(o) || Invulnerable(o)) return;
            // an arcane shield held towards it stops it, and is spent doing so
            if (Guards(o, me, from))
            {
                o.ServerAbsorb(1e6f, me);
                Rebuke(me, o);
                return;
            }
            Vector3 away = o.transform.position - from;
            away.y = 0f;
            away = away.sqrMagnitude > 0.01f ? away.normalized : fwd;
            Hit(o, me, 0f, o.transform.position, away * SpellPlans.DeathKnock + Vector3.up * SpellPlans.DeathKnockUp, SpellPlans.DeathRagdoll, from: from);
            me.ServerHitConfirm(o.NetworkObjectId, o.transform.position + Vector3.up * 1.15f, 0f, false); // (a hit marker: no damage)
            me.ServerDeathGloom();                                // both screens darken round the edges for a moment
            if (o is PlayerController victim) victim.ServerDeathGloom();
            if (!o.IsAlive) return;
            var st = o.Status.Value;
            if (st.BileAt > now) return;              // already doomed
            if (st.DeathMarked) { Doom(o, me, now); return; }
            st.DeathMarked = true;
            o.Status.Value = st;
        }

        /// <summary>Marked for death (no more than that), by whoever did it.</summary>
        static void MarkForDeath(Combatant o, PlayerController by)
        {
            if (o == null || !o.IsAlive || o.Status.Value.DeathMarked) return;
            var st = o.Status.Value;
            st.DeathMarked = true;
            o.Status.Value = st;
            o.ServerCredit(by);
        }

        // ------------------------------------------------------------------ doomed

        static void Doom(Combatant o, PlayerController by, double now)
        {
            var st = o.Status.Value;
            st.BileFrom = now;
            st.BileAt = now + SpellPlans.BileTime;
            o.Status.Value = st;
            o.ServerCredit(by);
            var b = new BileState { who = o, by = by, end = st.BileAt };
            b.id = WorldNet.ServerBreath(o.NetworkObjectId, BileKind, now);
            BilesS.Add(b);
        }

        static void EndBile(BileState b)
        {
            if (!BilesS.Remove(b)) return;
            WorldNet.ServerBreathEnd(b.id);
        }

        static void TickBiles(double now)
        {
            for (int i = BilesS.Count - 1; i >= 0; i--)
            {
                if (i >= BilesS.Count) continue;
                var b = BilesS[i];
                var c = b.who;
                if (c == null || !c.IsSpawned || !c.IsAlive) { EndBile(b); continue; }
                if (now >= b.end) { EndBile(b); Burst(b); continue; }
                if (now < b.nextCheck) continue;
                b.nextCheck = now + SpellPlans.FlameCheck;
                SpellPlans.BileFrom(c, out var origin, out var dir);
                CoatBile(origin, dir, now);
            }
        }

        /// <summary>The bile stream: a few rays in its cone, a little patch coated where each lands.</summary>
        static void CoatBile(Vector3 origin, Vector3 dir, double now)
        {
            var w = VoxelWorld.Current;
            if (w == null) return;
            _bileCells.Clear(); _seen.Clear();
            for (int k = 0; k < SpellPlans.FlameRays; k++)
            {
                Vector3 d = k == 0 ? dir : ConeRay(dir, SpellPlans.BileAngle);
                if (!Physics.Raycast(origin, d, out var hit, SpellPlans.BileReach, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                if (!VoxelWorld.IsVoxelCollider(hit.collider)) continue;
                var c0 = w.CellOf(hit.point - hit.normal * 0.1f);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx * dx + dy * dy + dz * dz > 2) continue;
                    Coat(w, new Vector3Int(c0.x + dx, c0.y + dy, c0.z + dz), now);
                }
            }
            if (_bileCells.Count > 0) WorldNet.ServerBileCoat(new List<Vector3Int>(_bileCells));
        }

        /// <summary>One cell to coat (it goes in <see cref="_bileCells"/>), unless it can't be or already is.</summary>
        static void Coat(VoxelWorld w, Vector3Int p, double now)
        {
            if (!_seen.Add(p)) return;
            ushort v = w.Get(p);
            if (v == 0 || w.IsIndestructible(v) || w.IsIce(v)) return;
            if (w.IsBile(v))
            {
                if (Biled.TryGetValue(p, out var old)) Biled[p] = (old.was, now); // coated afresh
                return;
            }
            Biled[p] = (v, now);
            _bileCells.Add(p);
        }

        /// <summary>3 s up: the body bursts. The ground round it is blown out, everything near is coated, everyone near is marked.</summary>
        static void Burst(BileState b)
        {
            var c = b.who;
            double now = WorldNet.Now;
            Vector3 p = c.transform.position;
            var w = VoxelWorld.Current;
            if (w != null) WorldNet.ServerRemoveSphere(w.CellOf(p), SpellPlans.R4(SpellPlans.BileBlast), 9f, Vector3.up * 3f);
            WorldNet.ServerDeathBurst(p);
            c.ServerCredit(b.by);
            c.ServerKillByHazard(DeathCause.Spell, p, Vector3.up * 5f, true); // in pieces
            // everything near is coated (the surface: cells with air beside them)
            if (w != null)
            {
                _bileCells.Clear(); _seen.Clear();
                var c0 = w.CellOf(p);
                int r = Mathf.CeilToInt(SpellPlans.BileSplash / VoxelWorld.Size);
                float r2 = SpellPlans.BileSplash * SpellPlans.BileSplash;
                for (int x = -r; x <= r; x++)
                for (int y = -r; y <= r; y++)
                for (int z = -r; z <= r; z++)
                {
                    var q = new Vector3Int(c0.x + x, c0.y + y, c0.z + z);
                    if ((w.CellCenter(q) - p).sqrMagnitude > r2 || w.Get(q) == 0) continue;
                    bool open = false;
                    foreach (var n in Neighbours) if (w.Get(q + n) == 0) { open = true; break; }
                    if (open) Coat(w, q, now);
                }
                if (_bileCells.Count > 0) WorldNet.ServerBileCoat(new List<Vector3Int>(_bileCells));
            }
            // everyone near: marked (only marked)
            foreach (var o in Combatant.Registry.ToArray())
            {
                if (o == null || o == c || !o.IsAlive || Folded(o)) continue;
                if (b.by != null && (o == b.by || Combatant.Allies(b.by, o))) continue;
                if ((o.transform.position - p).magnitude > SpellPlans.BileSplash) continue;
                MarkForDeath(o, b.by);
            }
        }

        /// <summary>Bile on the ground dries off as frost melts: 20 s after it was last coated.</summary>
        static void TickDry(double now)
        {
            if (Biled.Count == 0 || now < _nextDry) return;
            _nextDry = now + 0.25;
            _bileCells.Clear(); _dryTo.Clear();
            foreach (var kv in Biled)
            {
                if (now < kv.Value.at + SpellPlans.BileCoatTime) continue;
                _bileCells.Add(kv.Key);
                _dryTo.Add(kv.Value.was);
                if (_bileCells.Count >= 500) break; // (the rest next time)
            }
            if (_bileCells.Count == 0) return;
            foreach (var p in _bileCells) Biled.Remove(p);
            WorldNet.ServerBileDry(new List<Vector3Int>(_bileCells), new List<ushort>(_dryTo));
        }

        /// <summary>Server: is this player holding Death (bile doesn't slow them)?</summary>
        public static bool HoldsDeath(PlayerController pc) => pc != null && Cards.CountOf(pc.Hand, CardId.Death) > 0;
    }
}
