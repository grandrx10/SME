using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server-only spell logic: turns validated casts into world ops (<see cref="WorldNet"/>), flies thrown rocks,
    /// decides what they hit, and deals damage / knockback. Clients only ever see the results.
    /// </summary>
    public static partial class SpellServer
    {
        class PendingWall { public int id; public double mergeAt; }
        class PendingFold { public int id; public double crushAt; public Bounds crush; public PlayerController caster; public Vector3 along; }
        /// <summary>A flying lump of ground: a thrown rock, or summoned ground blown off by Shove.</summary>
        class RockState
        {
            public int id; public PlayerController caster; public double start;
            public bool launched; public Vector3 lastPos; public double lastTime;
            public float damage = SpellPlans.RockDamage, radius = SpellPlans.RockRadius;
            public int envR4 = 7;
            public int lastGust;
        }

        class GustState
        {
            public int id; public PlayerController caster; public Vector3 origin, dir; public double start; public readonly HashSet<Combatant> hit = new();
            public float life = SpellPlans.GustLife;                 // how long it blows (Tug: until it reaches the caster)
            public float knock = SpellPlans.GustKnockForward, up = SpellPlans.GustKnockUp, ragdoll = SpellPlans.GustRagdoll;
        }
        class TornadoState
        {
            public int id; public PlayerController caster; public Vector3 origin, dir; public double start;
            public readonly Dictionary<Combatant, double> captured = new(), lastLump = new();
            public double nextGouge;
        }
        class DashState { public PlayerController caster; public Vector3 dir; public double until; public readonly HashSet<Combatant> hit = new(); }
        class VolleyState { public int id; public PlayerController caster; public ulong target; public double start; public bool launched; public readonly HashSet<(int, int)> redirected = new(); }
        class BarrierState { public PlayerController caster; public double start; public readonly HashSet<Combatant> hit = new(); }
        class SiphonState { public PlayerController caster; public double start; }

        static readonly List<PendingWall> Walls = new();
        static readonly List<PendingFold> Folds = new();
        static readonly List<RockState> Rocks = new();
        static readonly List<GustState> Gusts = new();
        static readonly List<TornadoState> Tornados = new();
        static readonly List<DashState> Dashes = new();
        static readonly List<VolleyState> Volleys = new();
        static readonly List<BarrierState> Barriers = new();
        static readonly List<SiphonState> Siphons = new();
        static readonly RaycastHit[] Hits = new RaycastHit[32];

        public static void Reset()
        {
            Walls.Clear();
            Folds.Clear();
            Rocks.Clear();
            Gusts.Clear();
            Tornados.Clear();
            Dashes.Clear();
            Volleys.Clear();
            Barriers.Clear();
            Siphons.Clear();
            ResetFlame();
        }

        // ------------------------------------------------------------------ barriers

        static Vector3 BarrierCenter(BarrierState b) => b.caster.transform.position + Vector3.up * 0.1f;
        static bool BarrierActive(BarrierState b, double now) =>
            b.caster != null && b.caster.IsAlive && now >= b.start + SpellPlans.BarrierWindup && now <= b.start + SpellPlans.BarrierWindup + SpellPlans.BarrierTime;

        /// <summary>Is this combatant inside its own active barrier (immune to being thrown or crushed)?</summary>
        public static bool Shielded(Combatant c)
        {
            double now = WorldNet.Now;
            foreach (var b in Barriers) if (b.caster == c && BarrierActive(b, now)) return true;
            return false;
        }

        /// <summary>Is the point inside an active barrier that doesn't belong to <paramref name="owner"/>?</summary>
        static bool BarrierBlocks(ulong owner, Vector3 p, float pad)
        {
            double now = WorldNet.Now;
            foreach (var b in Barriers)
            {
                if (!BarrierActive(b, now) || b.caster.NetworkObjectId == owner) continue;
                float r = SpellPlans.BarrierRadius + pad;
                if ((p - BarrierCenter(b)).sqrMagnitude < r * r) return true;
            }
            return false;
        }

        /// <summary>Drops cells that would appear inside an active barrier.</summary>
        static void StripBarrierCells(List<Vector3Int> cells, List<ushort> values)
        {
            var w = VoxelWorld.Current;
            double now = WorldNet.Now;
            foreach (var b in Barriers)
            {
                if (!BarrierActive(b, now) && now > b.start + SpellPlans.BarrierWindup) continue;
                Vector3 c = BarrierCenter(b);
                float r2 = SpellPlans.BarrierRadius * SpellPlans.BarrierRadius;
                for (int i = cells.Count - 1; i >= 0; i--)
                    if ((w.CellCenter(cells[i]) - c).sqrMagnitude < r2) { cells.RemoveAt(i); values.RemoveAt(i); }
            }
        }

        static void TickBarriers(double now)
        {
            var w = VoxelWorld.Current;
            for (int i = Barriers.Count - 1; i >= 0; i--)
            {
                var b = Barriers[i];
                if (b.caster == null || now > b.start + SpellPlans.BarrierWindup + SpellPlans.BarrierTime) { Barriers.RemoveAt(i); continue; }
                if (!BarrierActive(b, now)) continue;
                Vector3 c = BarrierCenter(b);
                float r = SpellPlans.BarrierRadius;
                // summoned ground inside the bubble: vaporised
                if (w != null && w.SummonedCount > 0)
                    WorldNet.ServerVaporize(w.SummonedWhere(p => (p - c).sqrMagnitude < r * r));
                // anyone touching it gets thrown off
                foreach (var o in Combatant.Registry.ToArray())
                {
                    if (o == null || o == b.caster || !o.IsAlive || b.hit.Contains(o)) continue;
                    Vector3 d = o.transform.position - c;
                    if (d.magnitude > r + 0.45f) continue;
                    b.hit.Add(o);
                    Vector3 away = new Vector3(d.x, 0f, d.z).normalized;
                    if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
                    Hit(o, b.caster, SpellPlans.BarrierDamage, o.transform.position, away * 12f + Vector3.up * 4f, 1f);
                }
            }
        }

        // ------------------------------------------------------------------ Shove

        static void TickGusts(double now)
        {
            var w = VoxelWorld.Current;
            for (int i = Gusts.Count - 1; i >= 0; i--)
            {
                var g = Gusts[i];
                if (now - g.start > g.life || g.caster == null) { Gusts.RemoveAt(i); continue; }
                Vector3 center = SpellPlans.GustCenter(g.origin, g.dir, g.start, now);
                Quaternion rot = Quaternion.LookRotation(g.dir);
                Quaternion inv = Quaternion.Inverse(rot);
                Vector3 h = SpellPlans.GustHalfSize;
                bool Inside(Vector3 p, float pad)
                {
                    Vector3 l = inv * (p - center);
                    return Mathf.Abs(l.x) <= h.x + pad && Mathf.Abs(l.y) <= h.y + pad && Mathf.Abs(l.z) <= h.z + pad;
                }

                // people: thrown, no damage
                foreach (var c in Combatant.Registry.ToArray())
                {
                    if (c == null || c == g.caster || !c.IsAlive || g.hit.Contains(c)) continue;
                    if (!Inside(c.transform.position, 0.45f) && !Inside(c.transform.position + Vector3.up * 0.7f, 0.45f)) continue;
                    g.hit.Add(c);
                    Hit(c, g.caster, 0f, c.transform.position, g.dir * g.knock + Vector3.up * g.up, g.ragdoll);
                }

                // summoned ground: blown loose and sent flying
                if (w != null && w.SummonedCount > 0)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        var cells = w.SummonedWhere(p => Inside(p, 0.1f));
                        if (cells.Count == 0) break;
                        var piece = w.SummonedPiece(cells[0], SpellPlans.PushMaxCells);
                        Vector3 pivot = Vector3.zero;
                        foreach (var pc in piece) pivot += w.CellCenter(pc.cell);
                        pivot /= piece.Count;
                        int id = WorldNet.ServerPushBody(g.caster.NetworkObjectId, piece, pivot, g.dir * SpellPlans.PushSpeed + Vector3.up * 3f);
                        if (id == 0) break;
                        float size = Mathf.Pow(piece.Count, 1f / 3f) * VoxelWorld.Size;
                        Rocks.Add(new RockState
                        {
                            id = id, caster = g.caster, start = now, launched = true, lastPos = pivot, lastTime = now,
                            damage = SpellPlans.LumpDamage(piece.Count), radius = Mathf.Clamp(size * 0.45f, 0.3f, 1.1f), envR4 = 8, lastGust = g.id,
                        });
                    }
                }

                // projectiles (anyone's, your own too): turned around
                var net = WorldNet.Instance;
                if (net == null) continue;
                foreach (var rock in Rocks)
                {
                    if (!rock.launched || rock.lastGust == g.id) continue;
                    if (!net.Rocks.TryGetValue(rock.id, out var fx)) continue;
                    Vector3 pos = WorldNet.RockPosition(fx, now);
                    if (!Inside(pos, rock.radius)) continue;
                    float speed = Mathf.Max(SpellPlans.RockSpeed * 0.8f, WorldNet.RockVelocity(fx, now).magnitude);
                    WorldNet.ServerRockLaunch(rock.id, pos, g.dir * speed + Vector3.up * 2f);
                    rock.caster = g.caster;
                    rock.lastGust = g.id;
                    rock.lastPos = pos;
                    rock.lastTime = now;
                }
                foreach (var v in Volleys)
                {
                    if (!net.Missiles.TryGetValue(v.id, out var mfx) || !mfx.launched) continue;
                    for (int m = 0; m < mfx.m.Length; m++)
                    {
                        var ms = mfx.m[m];
                        if (!ms.alive || v.redirected.Contains((g.id, m)) || !Inside(ms.pos, 0.2f)) continue;
                        v.redirected.Add((g.id, m));
                        WorldNet.ServerMissileRedirect(v.id, m, ms.pos, g.dir, g.caster.NetworkObjectId);
                    }
                }
                RedirectFireballs(g, now);
            }
        }

        // ------------------------------------------------------------------ Tornado

        static void TickTornados(WorldNet net, double now)
        {
            for (int i = Tornados.Count - 1; i >= 0; i--)
            {
                var t = Tornados[i];
                Vector3 b = SpellPlans.TornadoBase(t.origin, t.dir, t.start, now);
                if (now - t.start > SpellPlans.TornadoLife)
                {
                    // it blows itself out: everyone it carried is flung clear
                    foreach (var kv in t.captured)
                    {
                        var c = kv.Key;
                        if (c == null || !c.IsAlive) continue;
                        Vector3 out_ = c.transform.position - b;
                        out_.y = 0f;
                        c.ServerKnock((out_.sqrMagnitude > 0.01f ? out_.normalized : t.dir) * 9f + Vector3.up * 5f, 1f);
                    }
                    Tornados.RemoveAt(i);
                    continue;
                }
                // tear up the ground under it as it goes (the rubble flies up and gets sucked into the funnel)
                var w = VoxelWorld.Current;
                if (w != null && now >= t.nextGouge && now - t.start > SpellPlans.TornadoForm * 0.4f)
                {
                    t.nextGouge = now + SpellPlans.TornadoGougeEvery;
                    Vector2 off = Random.insideUnitCircle * SpellPlans.TornadoRadius * 0.75f;
                    var c0 = w.CellOf(b + new Vector3(off.x, 0f, off.y));
                    int top = w.TopSolid(c0.x, c0.z, c0.y - 8, c0.y + 6);
                    if (top != int.MinValue)
                        WorldNet.ServerRemoveSphere(new Vector3Int(c0.x, top, c0.z), SpellPlans.TornadoGouge4, 2f, Vector3.up * 7f);
                }

                // sweep up anyone in its path
                foreach (var c in Combatant.Registry.ToArray())
                {
                    if (c == null || c == t.caster || !c.IsAlive || t.captured.ContainsKey(c) || Shielded(c)) continue;
                    Vector3 rel = c.transform.position - b;
                    if (rel.y < -1.5f || rel.y > SpellPlans.TornadoHeight || new Vector2(rel.x, rel.z).magnitude > SpellPlans.TornadoRadius) continue;
                    t.captured[c] = now;
                    t.lastLump[c] = now;
                    float phase = Mathf.Atan2(rel.z, rel.x);
                    c.ServerApplyDamage(SpellPlans.TornadoDamage, t.caster != null ? t.caster.OwnerClientId : Combatant.NoAttacker, c.transform.position, Vector3.up, -1, 0f, DeathCause.Spell);
                    if (c.IsAlive) c.ServerCarry(t.origin, t.dir, t.start, now, phase);
                }
                // rubble spinning in it keeps hitting the people it carries
                int lump = net.Tornados.TryGetValue(t.id, out var fx) && fx.view != null ? fx.view.CarriedMaxCells : 0;
                if (lump > 0)
                    foreach (var c in new List<Combatant>(t.captured.Keys))
                    {
                        if (c == null || !c.IsAlive || now - t.lastLump[c] < 1.0) continue;
                        t.lastLump[c] = now;
                        c.ServerApplyDamage(SpellPlans.LumpDamage(lump), t.caster != null ? t.caster.OwnerClientId : Combatant.NoAttacker, c.transform.position, Vector3.up, -1, 0f, DeathCause.Spell);
                    }
            }
        }

        // ------------------------------------------------------------------ Nomadic Dash

        static void TickDashes(double now)
        {
            for (int i = Dashes.Count - 1; i >= 0; i--)
            {
                var d = Dashes[i];
                if (d.caster == null || !d.caster.IsAlive || now > d.until) { Dashes.RemoveAt(i); continue; }
                foreach (var c in Combatant.Registry.ToArray())
                {
                    if (c == null || c == d.caster || !c.IsAlive || d.hit.Contains(c)) continue;
                    if ((c.transform.position - d.caster.transform.position).magnitude > 1.4f) continue;
                    d.hit.Add(c);
                    Hit(c, d.caster, 0f, c.transform.position, d.dir * SpellPlans.DashKnockForward + Vector3.up * SpellPlans.DashKnockUp, 1f);
                }
            }
        }

        // ------------------------------------------------------------------ Mana Missiles

        static void TickVolleys(WorldNet net, double now)
        {
            for (int i = Volleys.Count - 1; i >= 0; i--)
            {
                var v = Volleys[i];
                if (!net.Missiles.TryGetValue(v.id, out var fx)) { Volleys.RemoveAt(i); continue; }
                if (!v.launched)
                {
                    if (v.caster == null || !v.caster.IsAlive) { Volleys.RemoveAt(i); continue; } // the orb fizzles with its caster
                    if (now < v.start + SpellPlans.MissileCharge) continue;
                    v.launched = true;
                    Vector3 origin = SpellVisuals.RightHand(v.caster) + v.caster.transform.forward * 0.15f;
                    var target = WorldNet.FindCombatant(v.target);
                    Vector3 to = target != null ? (target.transform.position + Vector3.up * 0.3f - origin) : v.caster.transform.forward;
                    to = to.sqrMagnitude > 1e-4f ? to.normalized : Vector3.forward;
                    // burst out evenly round the compass from a random start (each nudged, tilted up a little),
                    // so they never bunch up and each swings in on the target from its own side
                    var dirs = new Vector3[SpellPlans.MissileCount];
                    float start = Random.Range(0f, 360f);
                    for (int k = 0; k < dirs.Length; k++)
                    {
                        float heading = start + 360f * k / dirs.Length + Random.Range(-SpellPlans.MissileScatterJitter, SpellPlans.MissileScatterJitter);
                        float lift = Random.Range(0f, SpellPlans.MissileScatterLift);
                        dirs[k] = Quaternion.Euler(-lift, heading, 0f) * Vector3.forward;
                    }
                    WorldNet.ServerMissileLaunch(v.id, origin, dirs);
                    continue;
                }
                bool any = false;
                for (int m = 0; m < fx.m.Length; m++)
                {
                    var ms = fx.m[m];
                    if (!ms.alive) continue;
                    any = true;
                    if (BarrierBlocks(ms.owner, ms.pos, SpellPlans.MissileRadius))
                    {
                        ms.alive = false;
                        WorldNet.ServerMissileImpact(v.id, m, ms.pos, default, 0, true);
                        continue;
                    }
                    if (ms.age > SpellPlans.MissileLife)
                    {
                        ms.alive = false;
                        WorldNet.ServerMissileImpact(v.id, m, ms.pos, default, 0, false);
                        continue;
                    }
                    Vector3 step = ms.pos - ms.prev;
                    float dist = step.magnitude;
                    if (dist < 1e-5f) continue;
                    if (!SweepMissile(ms, ms.prev, step / dist, dist, out var hit, out var victim)) continue;
                    ms.alive = false;
                    if (victim != null)
                    {
                        var attacker = WorldNet.FindCaster(ms.owner);
                        Hit(victim, attacker, SpellPlans.MissileDamage, hit.point, ms.dir * 6f + Vector3.up * 3f, 0.6f);
                    }
                    // a tiny explosion: blows out whatever voxels are right there (a person's feet, a wall, the floor)
                    var w = VoxelWorld.Current;
                    Vector3 into = victim != null ? hit.point : hit.point - hit.normal * 0.1f;
                    WorldNet.ServerMissileImpact(v.id, m, hit.point, w != null ? w.CellOf(into) : default, w != null ? SpellPlans.MissileBlast4 : 0, false);
                }
                if (!any) Volleys.RemoveAt(i);
            }
        }

        static bool SweepMissile(WorldNet.Missile ms, Vector3 from, Vector3 dir, float dist, out RaycastHit best, out Combatant victim)
        {
            best = default;
            victim = null;
            int mask = Layers.EnvironmentMask | (1 << Layers.Hitbox);
            int n = Physics.SphereCastNonAlloc(from, SpellPlans.MissileRadius, dir, Hits, dist, mask, QueryTriggerInteraction.Collide);
            float bestDist = float.MaxValue;
            bool found = false;
            for (int k = 0; k < n; k++)
            {
                var h = Hits[k];
                if (h.collider == null) continue;
                Combatant owner = null;
                if (h.collider.isTrigger)
                {
                    var hb = h.collider.GetComponent<Hitbox>();
                    if (hb == null || hb.Owner == null || !hb.Owner.IsAlive || hb.Owner.NetworkObjectId == ms.owner) continue;
                    owner = hb.Owner;
                }
                float d = h.distance;
                if (d <= 0f && h.point == Vector3.zero) h.point = from;
                if (d < bestDist) { bestDist = d; best = h; victim = owner; found = true; }
            }
            return found;
        }

        // ------------------------------------------------------------------ Mana Siphon

        static void TickSiphons(double now)
        {
            for (int i = Siphons.Count - 1; i >= 0; i--)
            {
                var s = Siphons[i];
                var me = s.caster;
                float t = (float)(now - s.start);
                if (me == null || !me.IsAlive || t > SpellPlans.SiphonTime) { Siphons.RemoveAt(i); continue; }
                if (t < SpellPlans.SiphonArm) continue;
                Vector3 fwd = Quaternion.Euler(0, me.ViewYaw, 0) * Vector3.forward;
                Vector3 c = me.transform.position + fwd * SpellPlans.SiphonReach + Vector3.up * 0.2f;
                var struck = new List<Combatant>();
                foreach (var o in Combatant.Registry)
                    if (o != null && o != me && o.IsAlive && (o.transform.position - c).magnitude <= SpellPlans.SiphonHitRadius) struck.Add(o);
                if (struck.Count == 0) continue;

                // the punch lands
                float gained = 0f;
                foreach (var o in struck)
                {
                    if (o is PlayerController victim)
                    {
                        float m = victim.ManaNow;
                        float take = Mathf.Min(SpellPlans.SiphonDrain, m);
                        victim.Mana.Value = victim.Mana.Value.With(now, m - take);
                        gained += take;
                    }
                    Vector3 away = o.transform.position - me.transform.position;
                    away.y = 0f;
                    away = away.sqrMagnitude > 0.01f ? away.normalized : fwd;
                    Hit(o, me, SpellPlans.SiphonDamage, o.transform.position, away * 11f + Vector3.up * 7f, 1f);
                }
                if (gained > 0f) me.Mana.Value = me.Mana.Value.With(now, Mathf.Min(ManaState.Max, me.ManaNow + gained));
                me.ServerSiphonPunched();
                Siphons.RemoveAt(i);
            }
        }

        // ================================================================== Flame cards, Judgment, Platform

        class FireballState { public int id; public PlayerController caster; public double start; public bool launched; public Vector3 lastPos; public int lastGust; }
        class HellfireState
        {
            public int id; public PlayerController caster; public double start;
            public readonly List<Vector3> points = new();
            public double riseAt = -1;
            public readonly HashSet<Combatant> hit = new();
            public readonly HashSet<int> lit = new();
        }
        class EscapeState { public PlayerController caster; public double at; }
        class JudgmentState { public int id; public PlayerController caster; public Vector3 center; public double start, nextLayer; public readonly Dictionary<Combatant, float> owed = new(); }

        static readonly List<FireballState> FireballsS = new();
        static readonly List<HellfireState> HellfiresS = new();
        static readonly List<EscapeState> Escapes = new();
        static readonly List<JudgmentState> JudgmentsS = new();
        static readonly HashSet<int> FlamingRocks = new();

        static void ResetFlame()
        {
            FireballsS.Clear();
            HellfiresS.Clear();
            Escapes.Clear();
            JudgmentsS.Clear();
            FlamingRocks.Clear();
            ResetTarot();
        }

        static void TickFlame(WorldNet net, double now, float dt)
        {
            foreach (var c in Combatant.Registry.ToArray()) if (c != null) c.ServerTickBurn(now, dt);
            TickFireballs(net, now);
            TickHellfires(now);
            TickEscapes(now);
            TickJudgments(now, dt);
            TickTarot(now);
        }

        // ---------------- burn helper ----------------

        static void Burn(Combatant c, PlayerController attacker) =>
            c.ServerBurn(attacker != null ? attacker.OwnerClientId : Combatant.NoAttacker);

        /// <summary>Blows out a crater (rubble flung up), then blackens what's left round it.</summary>
        static void Scorch(Vector3 point, int crater4, int singe4, float push)
        {
            var w = VoxelWorld.Current;
            if (w == null) return;
            var cell = w.CellOf(point);
            if (crater4 > 0)
            {
                // scorch the whole crater first, so the rubble it throws out flies black too
                WorldNet.ServerSinge(cell, crater4 + 2, true);
                WorldNet.ServerRemoveSphere(cell, crater4, push, Vector3.up * 6f);
            }
            if (singe4 > 0) WorldNet.ServerSinge(cell, singe4);
        }

        // ---------------- Ball of Flame ----------------

        /// <summary>The caster's owner says "throw it now" (aimed along <paramref name="dir"/>).</summary>
        public static void LaunchFireball(PlayerController caster, int id, Vector3 dir)
        {
            var f = FireballsS.Find(x => x.id == id);
            if (f == null || f.launched || f.caster != caster) return;
            if (WorldNet.Now < f.start + SpellPlans.FireballCharge - 0.1) return;
            DoLaunchFireball(f, dir);
        }

        static void DoLaunchFireball(FireballState f, Vector3 dir)
        {
            var net = WorldNet.Instance;
            if (net == null || !net.Fireballs.TryGetValue(f.id, out var fx)) return;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
            Vector3 origin = WorldNet.FireballPosition(fx, WorldNet.Now);
            WorldNet.ServerFireballLaunch(f.id, origin, dir.normalized * SpellPlans.FireballSpeed, f.caster.NetworkObjectId);
            f.launched = true;
            f.lastPos = origin;
        }

        static void TickFireballs(WorldNet net, double now)
        {
            for (int i = FireballsS.Count - 1; i >= 0; i--)
            {
                var f = FireballsS[i];
                if (!net.Fireballs.TryGetValue(f.id, out var fx)) { FireballsS.RemoveAt(i); continue; }
                if (!f.launched)
                {
                    if (f.caster == null || !f.caster.IsAlive)
                    {
                        WorldNet.ServerFireballEnd(f.id, WorldNet.FireballPosition(fx, now), true);
                        FireballsS.RemoveAt(i);
                        continue;
                    }
                    // the owner never threw it: it goes where they're facing
                    if (now > f.start + SpellPlans.FireballCharge + 0.4)
                        DoLaunchFireball(f, Quaternion.Euler(f.caster.ViewPitch, f.caster.ViewYaw, 0) * Vector3.forward);
                    continue;
                }
                Vector3 pos = WorldNet.FireballPosition(fx, now);
                if (BarrierBlocks(fx.owner, pos, SpellPlans.FireballRadius))
                {
                    WorldNet.ServerFireballEnd(f.id, pos, true);
                    FireballsS.RemoveAt(i);
                    continue;
                }
                Vector3 step = pos - f.lastPos;
                float dist = step.magnitude;
                if (dist > 1e-4f && SweepBall(fx.owner, f.lastPos, step / dist, dist, SpellPlans.FireballRadius, out var hit, out var victim))
                {
                    ExplodeFireball(f, fx, hit.point - (step / dist) * 0.05f, victim);
                    FireballsS.RemoveAt(i);
                    continue;
                }
                f.lastPos = pos;
                if ((pos - fx.origin).magnitude > Cards.MaxRange || now - fx.launchTime > 3.0)
                {
                    ExplodeFireball(f, fx, pos, null); // out of range: goes off in the air
                    FireballsS.RemoveAt(i);
                }
            }
        }

        static void ExplodeFireball(FireballState f, WorldNet.FireballFx fx, Vector3 point, Combatant direct)
        {
            var owner = WorldNet.FindCaster(fx.owner);
            if (direct != null && direct.IsAlive)
                direct.ServerApplyDamage(SpellPlans.FireballHitDamage, owner != null ? owner.OwnerClientId : Combatant.NoAttacker, point, fx.velocity.normalized, -1, 0f, DeathCause.Spell);
            // the blast: everyone close (the thrower too) is set alight and thrown hard
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || !c.IsAlive || Shielded(c)) continue;
                Vector3 d = c.transform.position - point;
                if (d.magnitude > SpellPlans.FireballBlast + 0.5f) continue;
                Burn(c, owner);
                Vector3 away = d.sqrMagnitude > 0.01f ? d.normalized : Vector3.up;
                Hit(c, owner, 0f, c.transform.position, away * 15f + Vector3.up * 8f, 1.2f);
            }
            Scorch(point, SpellPlans.FireballCrater4, SpellPlans.FireballSinge4, 9f);
            WorldNet.ServerFlameBurst(point, 1.2f);
            WorldNet.ServerFireballEnd(f.id, point, false);
        }

        /// <summary>A ball sweep against the world and bodies (not <paramref name="owner"/>'s).</summary>
        static bool SweepBall(ulong owner, Vector3 from, Vector3 dir, float dist, float radius, out RaycastHit best, out Combatant victim)
        {
            best = default;
            victim = null;
            int mask = Layers.EnvironmentMask | (1 << Layers.Hitbox);
            int n = Physics.SphereCastNonAlloc(from, radius, dir, Hits, dist, mask, QueryTriggerInteraction.Collide);
            float bestDist = float.MaxValue;
            bool found = false;
            for (int k = 0; k < n; k++)
            {
                var h = Hits[k];
                if (h.collider == null) continue;
                Combatant who = null;
                if (h.collider.isTrigger)
                {
                    var hb = h.collider.GetComponent<Hitbox>();
                    if (hb == null || hb.Owner == null || !hb.Owner.IsAlive || hb.Owner.NetworkObjectId == owner) continue;
                    who = hb.Owner;
                }
                if (h.distance <= 0f && h.point == Vector3.zero) h.point = from;
                if (h.distance < bestDist) { bestDist = h.distance; best = h; victim = who; found = true; }
            }
            return found;
        }

        // ---------------- Hellfire Wall ----------------

        /// <summary>The caster's owner extends the line along the ground.</summary>
        public static void HellfirePoint(PlayerController caster, int id, Vector3 p)
        {
            var h = HellfiresS.Find(x => x.id == id);
            if (h == null || h.caster != caster || h.riseAt >= 0) return;
            if (WorldNet.Now > h.start + SpellPlans.HellfireDraw + 0.4) return;
            Vector3 last = h.points.Count > 0 ? h.points[^1] : caster.FeetPosition;
            if ((p - last).magnitude > 4f || (p - caster.FeetPosition).magnitude > SpellPlans.HellfireLineSpeed * SpellPlans.HellfireDraw + 6f) return;
            h.points.Add(p);
            WorldNet.ServerHellfirePoint(id, p);
        }

        static void TickHellfires(double now)
        {
            var net = WorldNet.Instance;
            for (int i = HellfiresS.Count - 1; i >= 0; i--)
            {
                var h = HellfiresS[i];
                if (h.riseAt < 0)
                {
                    if (now >= h.start + SpellPlans.HellfireDraw + 0.05)
                    {
                        if (h.points.Count < 2) { HellfiresS.RemoveAt(i); continue; }
                        h.riseAt = now;
                        WorldNet.ServerHellfireRise(h.id);
                    }
                    continue;
                }
                float t = (float)(now - h.riseAt);
                if (t > SpellPlans.HellfireRise + SpellPlans.HellfireStand) { HellfiresS.RemoveAt(i); continue; }
                float lit = Mathf.Clamp01(t / SpellPlans.HellfireRise); // how far along the wall has risen
                // anyone stepping in: 15 now and a burn (no ragdoll), once per wall
                foreach (var c in Combatant.Registry.ToArray())
                {
                    if (c == null || !c.IsAlive || h.hit.Contains(c) || Shielded(c)) continue;
                    if (!InWall(h, c.FeetPosition, lit, 0.4f)) continue;
                    h.hit.Add(c);
                    c.ServerApplyDamage(SpellPlans.HellfireDamage, h.caster != null ? h.caster.OwnerClientId : Combatant.NoAttacker,
                        c.transform.position, Vector3.up, -1, 0f, DeathCause.Spell);
                    Burn(c, h.caster);
                }
                // flying ground going through it catches fire
                if (net == null) continue;
                foreach (var rock in Rocks)
                {
                    if (!rock.launched || FlamingRocks.Contains(rock.id) || !net.Rocks.TryGetValue(rock.id, out var fx)) continue;
                    Vector3 p = WorldNet.RockPosition(fx, now);
                    if (!InWall(h, p + Vector3.down * 0.5f, lit, rock.radius)) continue;
                    FlamingRocks.Add(rock.id);
                    WorldNet.ServerRockIgnite(rock.id);
                }
            }
        }

        /// <summary>Is a point (feet) inside the risen part of a hellfire wall?</summary>
        static bool InWall(HellfireState h, Vector3 feet, float lit, float pad)
        {
            float total = 0f;
            for (int k = 1; k < h.points.Count; k++) total += Vector3.Distance(h.points[k - 1], h.points[k]);
            float along = 0f;
            for (int k = 1; k < h.points.Count; k++)
            {
                Vector3 a = h.points[k - 1], b = h.points[k];
                float seg = Vector3.Distance(a, b);
                if (total > 0f && along / total > lit) break;
                along += seg;
                Vector2 a2 = new(a.x, a.z), b2 = new(b.x, b.z), p2 = new(feet.x, feet.z);
                Vector2 ab = b2 - a2;
                float u = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p2 - a2, ab) / ab.sqrMagnitude) : 0f;
                if ((p2 - (a2 + ab * u)).magnitude > SpellPlans.HellfireThick * 0.5f + pad) continue;
                float ground = Mathf.Lerp(a.y, b.y, u);
                if (feet.y > ground - 0.6f && feet.y < ground + SpellPlans.HellfireHeight) return true;
            }
            return false;
        }

        // ---------------- Explosive Escape ----------------

        static void TickEscapes(double now)
        {
            for (int i = Escapes.Count - 1; i >= 0; i--)
            {
                var e = Escapes[i];
                if (now < e.at) continue;
                Escapes.RemoveAt(i);
                var me = e.caster;
                if (me == null || !me.IsAlive) continue;
                Vector3 fwd = Quaternion.Euler(0, me.ViewYaw, 0) * Vector3.forward;
                Vector3 point = me.transform.position + fwd * SpellPlans.EscapeReach + Vector3.up * 0.1f;
                foreach (var c in Combatant.Registry.ToArray())
                {
                    if (c == null || c == me || !c.IsAlive || Shielded(c)) continue;
                    Vector3 d = c.transform.position - point;
                    if (d.magnitude > SpellPlans.EscapeBlast + 0.4f) continue;
                    Burn(c, me);
                    Vector3 away = new Vector3(d.x, 0f, d.z).sqrMagnitude > 0.01f ? new Vector3(d.x, 0f, d.z).normalized : fwd;
                    Hit(c, me, 0f, c.transform.position, away * 10f + Vector3.up * 6f, 1f);
                }
                // the ground in front is blasted out and flung, and scorched round it
                Scorch(point + Vector3.down * 0.6f, SpellPlans.EscapeCrater4, SpellPlans.EscapeSinge4, 10f);
                WorldNet.ServerFlameBurst(point, 1f);
            }
        }

        // ---------------- Arcane Judgment ----------------

        static void TickJudgments(double now, float dt)
        {
            var w = VoxelWorld.Current;
            for (int i = JudgmentsS.Count - 1; i >= 0; i--)
            {
                var j = JudgmentsS[i];
                float t = (float)(now - j.start);
                if (t > SpellPlans.JudgmentLaserEnd) { JudgmentsS.RemoveAt(i); continue; }
                if (t < SpellPlans.JudgmentLaserStart) continue;
                // the beam eats down through the ground, a layer at a time
                if (w != null && now >= j.nextLayer)
                {
                    j.nextLayer += SpellPlans.JudgmentLayerEvery;
                    var c = w.CellOf(j.center);
                    int r = Mathf.RoundToInt(SpellPlans.JudgmentRadius / VoxelWorld.Size);
                    WorldNet.ServerJudgmentErase(c, r, c.y - 80, c.y + 160);
                }
                // anyone in the beam, at any height: 50 a second (no ragdoll)
                foreach (var o in Combatant.Registry.ToArray())
                {
                    if (o == null || !o.IsAlive) continue;
                    Vector3 d = o.transform.position - j.center;
                    if (new Vector2(d.x, d.z).magnitude > SpellPlans.JudgmentRadius) continue;
                    j.owed.TryGetValue(o, out float owed);
                    owed += SpellPlans.JudgmentDps * dt;
                    if (owed >= 5f)
                    {
                        o.ServerApplyDamage(owed, j.caster != null ? j.caster.OwnerClientId : Combatant.NoAttacker, o.transform.position, Vector3.down, -1, 0f, DeathCause.Spell);
                        owed = 0f;
                    }
                    j.owed[o] = owed;
                }
            }
        }

        // ---------------- Summon Platform ----------------

        static void KnockUpFromPlatform(PlayerController caster, SpellPlans.WallPlan plan)
        {
            float half = SpellPlans.PlatformSize * VoxelWorld.Size * 0.5f + 0.35f;
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || !c.IsAlive || c == caster || Shielded(c)) continue;
                Vector3 feet = c.FeetPosition;
                Vector3 d = feet - plan.center;
                if (Mathf.Abs(d.x) > half || Mathf.Abs(d.z) > half) continue;
                if (feet.y < plan.groundY - 0.6f || feet.y > plan.groundY + 2.2f) continue;
                Vector3 away = new Vector3(d.x, 0f, d.z).sqrMagnitude > 0.01f ? new Vector3(d.x, 0f, d.z).normalized : Vector3.forward;
                Hit(c, caster, 10f, feet, Vector3.up * 13f + away * 6f, 0.9f);
            }
            // the caster rides it up (their own client already jumped them; this keeps the server's view in step)
        }

        // ---------------- gusts turn fireballs too ----------------

        /// <summary>
        /// Gusts (Shove, Tug) turn fireballs — your own too. Both move fast (26 and 28 m/s), so instead of one check per
        /// frame they're compared at several moments since the last frame, or they'd pass straight through each other.
        /// </summary>
        static void RedirectFireballs(GustState g, double now)
        {
            var net = WorldNet.Instance;
            if (net == null) return;
            Quaternion inv = Quaternion.Inverse(Quaternion.LookRotation(g.dir));
            Vector3 h = SpellPlans.GustHalfSize + Vector3.one * (SpellPlans.FireballRadius + 0.2f);
            double dt = Mathf.Max(Time.deltaTime, 1f / 60f);
            foreach (var f in FireballsS)
            {
                if (!f.launched || f.lastGust == g.id || !net.Fireballs.TryGetValue(f.id, out var fx)) continue;
                for (int k = 0; k <= 6; k++)
                {
                    double t = now - dt * (k / 6.0);
                    if (t < g.start || t < fx.launchTime) break;
                    Vector3 pos = WorldNet.FireballPosition(fx, t);
                    Vector3 l = inv * (pos - SpellPlans.GustCenter(g.origin, g.dir, g.start, t));
                    if (Mathf.Abs(l.x) > h.x || Mathf.Abs(l.y) > h.y || Mathf.Abs(l.z) > h.z) continue;
                    Vector3 here = WorldNet.FireballPosition(fx, now);
                    f.lastGust = g.id;
                    f.lastPos = here;
                    WorldNet.ServerFireballLaunch(f.id, here, g.dir * SpellPlans.FireballSpeed, g.caster.NetworkObjectId);
                    break;
                }
            }
        }

        /// <summary>A caster got thrown or hurt: whatever they were doing with their body stops.</summary>
        public static void OnInterrupted(PlayerController pc, bool knocked)
        {
            Dashes.RemoveAll(d => d.caster == pc);
            if (knocked) Siphons.RemoveAll(s => s.caster == pc);
            if (knocked) DispelMagician(pc);
        }

        // ------------------------------------------------------------------ casting

        /// <summary>Carries out a cast that already passed the cost / cooldown checks. False = nothing happened (no cost).</summary>
        /// <param name="target">A point for ground spells; a direction for Shove / Nomadic Dash.</param>
        /// <param name="targetId">The enemy Mana Missiles lock on to.</param>
        public static bool Cast(PlayerController caster, CardId card, Vector3 target, float yaw, ulong targetId)
        {
            var world = VoxelWorld.Current;
            if (world == null) return false;
            Vector3 feet = caster.FeetPosition;
            double now = WorldNet.Now;
            bool groundTargeted = card == CardId.SummonWall || card == CardId.FoldingHands || card == CardId.Tornado || card == CardId.ArcaneJudgment;
            // never further than the max range (plus a little slack for lag)
            if (groundTargeted && (target - feet).magnitude > Cards.MaxRange + 1.5f) return false;

            switch (card)
            {
                case CardId.Shove:
                {
                    Vector3 dir = target.sqrMagnitude > 1e-4f ? target.normalized : Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                    Vector3 origin = caster.transform.position + Vector3.up * 0.4f + dir * (SpellPlans.GustHalfSize.z + 0.6f);
                    int id = WorldNet.ServerGust(caster.NetworkObjectId, origin, dir, SpellPlans.GustLife);
                    Gusts.Add(new GustState { id = id, caster = caster, origin = origin, dir = dir, start = now });
                    return true;
                }
                case CardId.Tornado:
                {
                    Vector3 flat = target - feet;
                    flat.y = 0f;
                    if (flat.magnitude > SpellPlans.TornadoRange + 1f) return false; // a little slack for lag
                    Vector3 dir = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                    int id = WorldNet.ServerTornado(caster.NetworkObjectId, target, dir);
                    Tornados.Add(new TornadoState { id = id, caster = caster, origin = target, dir = dir, start = now });
                    return true;
                }
                case CardId.NomadicDash:
                {
                    Vector3 dir = new(target.x, 0f, target.z);
                    if (dir.sqrMagnitude < 1e-4f) dir = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                    Dashes.RemoveAll(d => d.caster == caster);
                    Dashes.Add(new DashState { caster = caster, dir = dir.normalized, until = now + SpellPlans.DashTime + 0.1 });
                    return true;
                }
                case CardId.ManaMissiles:
                {
                    var t = WorldNet.FindCombatant(targetId);
                    if (t == null || t == caster || !t.IsAlive) return false;
                    if ((t.transform.position - caster.transform.position).magnitude > SpellPlans.MissileTargetRange + 5f) return false;
                    int id = WorldNet.ServerMissileVolley(caster.NetworkObjectId, targetId);
                    Volleys.Add(new VolleyState { id = id, caster = caster, target = targetId, start = now });
                    return true;
                }
                case CardId.MagicalBarrier:
                {
                    WorldNet.ServerBarrier(caster.NetworkObjectId);
                    Barriers.Add(new BarrierState { caster = caster, start = now });
                    return true;
                }
                case CardId.ManaSiphon:
                {
                    Siphons.RemoveAll(s => s.caster == caster);
                    Siphons.Add(new SiphonState { caster = caster, start = now });
                    return true;
                }
                case CardId.TheMagician:
                case CardId.TheHighPriestess:
                case CardId.TheEmpress:
                case CardId.TheEmperor:
                    return CastTarot(caster, card, now);
                case CardId.BallOfFlame:
                {
                    int id = WorldNet.ServerFireball(caster.NetworkObjectId);
                    FireballsS.Add(new FireballState { id = id, caster = caster, start = now });
                    return true;
                }
                case CardId.HellfireWall:
                {
                    int id = WorldNet.ServerHellfire(caster.NetworkObjectId);
                    HellfiresS.Add(new HellfireState { id = id, caster = caster, start = now });
                    return true;
                }
                case CardId.ExplosiveEscape:
                {
                    Escapes.Add(new EscapeState { caster = caster, at = now + SpellPlans.EscapeDelay });
                    return true;
                }
                case CardId.ArcaneJudgment:
                {
                    var center = target;
                    int id = WorldNet.ServerJudgment(center);
                    JudgmentsS.Add(new JudgmentState { id = id, caster = caster, center = center, start = now, nextLayer = now + SpellPlans.JudgmentLaserStart });
                    return true;
                }
                case CardId.Tug:
                {
                    // from the cursor (or 15 m out) back towards the caster, stopping when it reaches them
                    Vector3 chest = caster.transform.position + Vector3.up * 0.4f;
                    Vector3 from = target;
                    if ((from - chest).magnitude > SpellPlans.TugRange + 1f) from = chest + (from - chest).normalized * SpellPlans.TugRange;
                    Vector3 toMe = chest - from;
                    float dist = toMe.magnitude;
                    if (dist < 1.5f) return false;
                    Vector3 dir = toMe / dist;
                    float life = Mathf.Max(0.05f, (dist - 1f) / SpellPlans.GustSpeed);
                    int id = WorldNet.ServerGust(caster.NetworkObjectId, from, dir, life);
                    Gusts.Add(new GustState
                    {
                        id = id, caster = caster, origin = from, dir = dir, start = now, life = life,
                        knock = SpellPlans.TugPull, up = SpellPlans.TugUp, ragdoll = SpellPlans.TugRagdoll,
                    });
                    return true;
                }
                case CardId.SummonPlatform:
                {
                    var plan = SpellPlans.PlanPlatform(world, feet);
                    if (!plan.valid) return false;
                    StripBarrierCells(plan.cells, plan.values);
                    int id = WorldNet.ServerWall(plan, SpellPlans.PlatformHeight + 1);
                    Walls.Add(new PendingWall { id = id, mergeAt = WorldNet.Now + SpellPlans.WallRise + 0.05 });
                    KnockUpFromPlatform(caster, plan);
                    return true;
                }
                case CardId.SummonWall:
                {
                    var plan = SpellPlans.PlanWall(world, target, yaw);
                    if (!plan.valid) return false;
                    // nothing gets summoned inside someone's barrier
                    StripBarrierCells(plan.cells, plan.values);
                    int id = WorldNet.ServerWall(plan);
                    Walls.Add(new PendingWall { id = id, mergeAt = WorldNet.Now + SpellPlans.WallRise + 0.05 });
                    KnockUpFromWall(caster, plan);
                    return true;
                }
                case CardId.RockBarrage:
                {
                    Vector3 fwd = Quaternion.Euler(0, caster.ViewYaw, 0) * Vector3.forward;
                    if (!SpellPlans.PlanRock(world, caster.transform.position, feet, fwd, target, out var center)) return false;
                    int id = WorldNet.ServerRockGather(caster.NetworkObjectId, center, SpellPlans.RockRadius4);
                    if (id == 0) return false;
                    var rock = new RockState { id = id, caster = caster, start = WorldNet.Now };
                    Rocks.Add(rock);
                    return true;
                }
                case CardId.FoldingHands:
                {
                    var plan = SpellPlans.PlanFold(world, target, yaw);
                    if (!plan.valid) return false;
                    int id = WorldNet.ServerFold(plan);
                    Folds.Add(new PendingFold
                    {
                        id = id, crushAt = WorldNet.Now + SpellPlans.FoldBuckle + SpellPlans.FoldClose, crush = plan.crush, caster = caster,
                        along = plan.alongX ? Vector3.right : Vector3.forward,
                    });
                    return true;
                }
            }
            return false;
        }

        static void KnockUpFromWall(PlayerController caster, SpellPlans.WallPlan plan)
        {
            Quaternion rot = Quaternion.Euler(0, plan.yaw, 0);
            Vector3 right = rot * Vector3.right, fwd = rot * Vector3.forward;
            float halfL = SpellPlans.WallLength * VoxelWorld.Size * 0.5f + 0.35f;
            float halfT = SpellPlans.WallThick * VoxelWorld.Size * 0.5f + 0.45f;
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || !c.IsAlive || Shielded(c)) continue;
                Vector3 feet = c.FeetPosition;
                Vector3 d = feet - plan.center;
                float u = Vector3.Dot(d, right), v = Vector3.Dot(d, fwd);
                if (Mathf.Abs(u) > halfL || Mathf.Abs(v) > halfT) continue;
                if (feet.y < plan.groundY - 0.6f || feet.y > plan.groundY + 2.2f) continue;
                Vector3 away = (v >= 0f ? fwd : -fwd);
                // your own wall just carries you up onto it: no damage, no ragdoll
                if (c == caster) caster.ServerKnock(Vector3.up * 11f, 0f);
                else Hit(c, caster, 10f, feet, Vector3.up * 12f + away * 6f, 0.9f);
            }
        }

        // ------------------------------------------------------------------ rocks

        /// <summary>The caster's owner says "throw it now" (aimed along <paramref name="dir"/>).</summary>
        public static void Launch(PlayerController caster, int id, Vector3 dir)
        {
            var rock = Rocks.Find(r => r.id == id);
            if (rock == null || rock.launched || rock.caster != caster) return;
            if (WorldNet.Now < rock.start + SpellPlans.RockGather + 0.15) return;
            DoLaunch(rock, dir);
        }

        static void DoLaunch(RockState rock, Vector3 dir)
        {
            var net = WorldNet.Instance;
            if (net == null || !net.Rocks.TryGetValue(rock.id, out var fx)) return;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
            dir.Normalize();
            double now = WorldNet.Now;
            Vector3 origin = WorldNet.RockPosition(fx, now);
            WorldNet.ServerRockLaunch(rock.id, origin, dir * SpellPlans.RockSpeed);
            rock.launched = true;
            rock.lastPos = origin;
            rock.lastTime = now;
        }

        static void TickRocks(WorldNet net, double now)
        {
            for (int i = Rocks.Count - 1; i >= 0; i--)
            {
                var rock = Rocks[i];
                if (!net.Rocks.TryGetValue(rock.id, out var fx)) { Rocks.RemoveAt(i); continue; }
                if (!rock.launched)
                {
                    // the caster never threw it (lag, disconnect, died): it goes where they're facing
                    bool casterGone = rock.caster == null || !rock.caster.IsAlive;
                    if (casterGone || now > rock.start + SpellPlans.RockGather + SpellPlans.RockHold + 1.2)
                    {
                        if (casterGone) { Impact(rock, fx.body ? fx.body.Center : fx.home, null, 0, null); Rocks.RemoveAt(i); continue; }
                        DoLaunch(rock, Quaternion.Euler(rock.caster.ViewPitch, rock.caster.ViewYaw, 0) * Vector3.forward);
                    }
                    continue;
                }

                Vector3 pos = WorldNet.RockPosition(fx, now);
                // an enemy barrier: gone
                if (BarrierBlocks(rock.caster != null ? rock.caster.NetworkObjectId : ulong.MaxValue, pos, rock.radius))
                {
                    WorldNet.ServerRockImpact(rock.id, pos, default, 0, true);
                    Rocks.RemoveAt(i);
                    continue;
                }
                Vector3 step = pos - rock.lastPos;
                float dist = step.magnitude;
                if (dist > 1e-4f && SweepRock(rock, rock.lastPos, step / dist, dist, out var hit, out var victim))
                {
                    if (victim != null)
                    {
                        Vector3 v = WorldNet.RockVelocity(fx, now);
                        Hit(victim, rock.caster, rock.damage, hit.point, (v.normalized * 9f + Vector3.up * 4f) * SpellPlans.RockKnockback, 0.8f);
                        if (FlamingRocks.Contains(rock.id) && victim.IsAlive) Burn(victim, rock.caster); // set alight by a hellfire wall
                        Impact(rock, hit.point, null, 0, null);
                    }
                    else Impact(rock, hit.point, hit.normal, rock.envR4, hit.collider);
                    Rocks.RemoveAt(i);
                    continue;
                }
                rock.lastPos = pos;
                rock.lastTime = now;
                bool tooFar = (pos - fx.origin).magnitude > Cards.MaxRange + (rock.lastGust != 0 ? 10f : 0f);
                bool tooLow = Arena.Current != null && pos.y < Arena.Current.KillY;
                if (tooFar || tooLow || now - fx.launchTime > 4.0)
                {
                    // out of range: it crumbles in the air
                    Impact(rock, pos, null, 0, null);
                    Rocks.RemoveAt(i);
                }
            }
        }

        static bool SweepRock(RockState rock, Vector3 from, Vector3 dir, float dist, out RaycastHit best, out Combatant victim)
        {
            best = default;
            victim = null;
            int mask = Layers.EnvironmentMask | (1 << Layers.Hitbox);
            int n = Physics.SphereCastNonAlloc(from, rock.radius, dir, Hits, dist, mask, QueryTriggerInteraction.Collide);
            float bestDist = float.MaxValue;
            bool found = false;
            for (int k = 0; k < n; k++)
            {
                var h = Hits[k];
                if (h.collider == null) continue;
                Combatant owner = null;
                if (h.collider.isTrigger)
                {
                    var hb = h.collider.GetComponent<Hitbox>();
                    if (hb == null || hb.Owner == null || !hb.Owner.IsAlive) continue;
                    owner = hb.Owner;
                    if (owner == rock.caster) continue;
                }
                float d = h.distance;
                if (d <= 0f && h.point == Vector3.zero) { h.point = from; d = 0f; } // started inside something
                if (d < bestDist)
                {
                    bestDist = d;
                    best = h;
                    victim = owner;
                    found = true;
                }
            }
            return found;
        }

        static void Impact(RockState rock, Vector3 point, Vector3? normal, int r4, Collider col)
        {
            var world = VoxelWorld.Current;
            Vector3Int cell = default;
            if (world != null)
            {
                Vector3 inside = normal.HasValue ? point - normal.Value * 0.12f : point;
                cell = world.CellOf(inside);
            }
            WorldNet.ServerRockImpact(rock.id, point, cell, world != null ? r4 : 0);
        }

        // ------------------------------------------------------------------ ragdolls crashing through walls

        /// <summary>A ragdolled player's owner reports smashing into voxels at speed.</summary>
        public static void Crash(PlayerController pc, Vector3 point, Vector3 velocity)
        {
            var world = VoxelWorld.Current;
            if (world == null || !pc.IsAlive || !pc.ServerMayCrash) return;
            float speed = velocity.magnitude;
            if (speed < PlayerMotor.CrashSpeed - 0.5f || speed > 80f) return;
            if ((point - pc.transform.position).magnitude > 3f) return;
            int r4 = CrashRadius4(speed);
            WorldNet.ServerRemoveSphere(world.CellOf(point), r4, speed * 0.25f, velocity * 0.3f);
        }

        public static int CrashRadius4(float speed) => Mathf.Clamp(Mathf.RoundToInt(6f + (speed - PlayerMotor.CrashSpeed) * 0.5f), 6, 11);

        // ------------------------------------------------------------------ tick

        public static void Tick(WorldNet net)
        {
            double now = WorldNet.Now;
            for (int i = Walls.Count - 1; i >= 0; i--)
            {
                if (now < Walls[i].mergeAt) continue;
                WorldNet.ServerMerge(Walls[i].id);
                Walls.RemoveAt(i);
            }
            for (int i = Folds.Count - 1; i >= 0; i--)
            {
                var f = Folds[i];
                if (now < f.crushAt) continue;
                // slammed shut on someone: the slabs break on them; on nothing: they set into walls
                if (Crush(f)) WorldNet.ServerShatter(f.id);
                else WorldNet.ServerMerge(f.id);
                Folds.RemoveAt(i);
            }
            TickBarriers(now);
            TickGusts(now);
            TickRocks(net, now);
            TickTornados(net, now);
            TickDashes(now);
            TickVolleys(net, now);
            TickSiphons(now);
            TickFlame(net, now, Time.deltaTime);
        }

        /// <summary>Damages and floors everyone caught between the slabs. True if it caught anyone.</summary>
        static bool Crush(PendingFold f)
        {
            var box = f.crush;
            box.Expand(0.2f);
            bool any = false;
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || !c.IsAlive) continue;
                Vector3 p = c.transform.position;
                if (!box.Contains(p) && !box.Contains(c.FeetPosition + Vector3.up * 0.2f) && !box.Contains(p + Vector3.up * 0.8f)) continue;
                if (Shielded(c)) { any = true; continue; } // the slabs break on the barrier instead
                // crumpled where they stand (the slabs break on them rather than throwing them)
                Hit(c, f.caster, SpellPlans.FoldDamage, p, Vector3.zero, SpellPlans.FoldRagdoll);
                any = true;
            }
            return any;
        }

        // ------------------------------------------------------------------ room to stand up

        /// <summary>The cells a standing body (feet at <paramref name="feet"/>) occupies.</summary>
        public static void StandBox(VoxelWorld w, Vector3 feet, out Vector3Int min, out Vector3Int max)
        {
            min = w.CellOf(feet + new Vector3(-0.4f, 0.05f, -0.4f));
            max = w.CellOf(feet + new Vector3(0.4f, 1.95f, 0.4f));
        }

        /// <summary>Server: break whatever is in the way of someone standing up here.</summary>
        public static void ClearStandSpace(Vector3 feet)
        {
            var w = VoxelWorld.Current;
            if (w == null) return;
            StandBox(w, feet, out var min, out var max);
            WorldNet.ServerRemoveBox(min, max, 2f);
        }

        /// <summary>A knocked-down player's owner needs room to get up.</summary>
        public static void ClearStandSpaceFor(PlayerController pc, Vector3 feet)
        {
            if (!pc.IsAlive || !pc.ServerMayCrash || (feet - pc.FeetPosition).magnitude > 3f) return;
            ClearStandSpace(feet);
        }

        // ------------------------------------------------------------------ damage

        public static void Hit(Combatant victim, PlayerController attacker, float damage, Vector3 point, Vector3 knock, float ragdoll)
        {
            if (victim == null || !victim.IsAlive) return;
            ulong who = attacker != null ? attacker.OwnerClientId : Combatant.NoAttacker;
            Vector3 dir = knock.sqrMagnitude > 0.01f ? knock.normalized : Vector3.up;
            if (damage > 0f) victim.ServerApplyDamage(damage, who, point, dir, -1, knock.magnitude * 6f, DeathCause.Spell);
            if (victim.IsAlive && (ragdoll > 0f || knock.sqrMagnitude > 0f)) victim.ServerKnock(knock, ragdoll);
        }
    }
}
