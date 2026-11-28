using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for Hammer Spin, Wolfgang, Heavenly Slash and Parry.
    ///  - Hammer Spin: held to charge (still), at least 0.5 s: 10 m, +10 m a second, up to 40 m. Let go: a level spin
    ///    straight ahead at an even speed, the hammer stretched out. Nothing knocks the spinner down (they still take
    ///    damage). The voxels in the way are smashed just ahead of them; everyone the hammer reaches takes 25 and is
    ///    thrown hard, once.
    ///  - Wolfgang: 0.3 s to draw both pistols, the body locked facing where it was cast; then 10 shots from each, turn
    ///    about, 500 a minute each, straight along the locked aim: 3 damage, thrown and ragdolled a little, and the ground
    ///    broken 2 voxels round where one lands. Knocked down meanwhile: the rest never fire.
    ///  - Heavenly Slash: 0.3 s crouched, a leap up, posing 1 s in the air (the cut's random angle shows), then a straight
    ///    cut along the aim, longer the further it goes, out to 15 m: every breakable voxel on it is cut, and everyone it
    ///    touches is struck 4 times for 10, thrown and ragdolled (a killing strike cuts them in two).
    ///  - Parry: on guard for 1.5 s. The first damaging blow (not fire) does nothing; instead the parrier dashes straight
    ///    back at whoever dealt it (up to 20 m), untouchable, breaking the voxels in the way, and stabs them: 15, thrown.
    /// </summary>
    public static partial class SpellServer
    {
        class SpinState
        {
            public PlayerController caster; public Vector3 from, to, dir; public double start; public float time, len, carved;
            public readonly HashSet<Combatant> hit = new();
        }
        class WolfState { public PlayerController caster; public double start; public int fired; }
        class WolfBullet { public int id; public PlayerController by; public ulong owner; public Vector3 origin, dir, lastPos; public double start; }
        class HeavenCastState { public PlayerController caster; public double start; public float roll; }
        class HeavenCutState
        {
            public int id; public PlayerController caster; public Vector3 origin, dir; public Quaternion rot; public double start; public float swept, carved;
            public readonly HashSet<Vector3Int> seen = new();
            public readonly HashSet<Combatant> hit = new();
        }
        class ParryState { public PlayerController caster; public double until; }
        class ParryDashState { public PlayerController caster; public Combatant foe; public Vector3 to, dir; public double arrive; }

        static readonly List<SpinState> SpinsS = new();
        static readonly List<WolfState> WolfsS = new();
        static readonly List<WolfBullet> WolfBulletsS = new();
        static readonly List<HeavenCastState> HeavenCastsS = new();
        static readonly List<HeavenCutState> HeavenCutsS = new();
        static readonly List<ParryState> ParriesS = new();
        static readonly List<ParryDashState> ParryDashesS = new();
        static readonly List<Vector3Int> _armsCells = new();

        static void ResetArms()
        {
            SpinsS.Clear();
            WolfsS.Clear();
            WolfBulletsS.Clear();
            HeavenCastsS.Clear();
            HeavenCutsS.Clear();
            ParriesS.Clear();
            ParryDashesS.Clear();
            RangsS.Clear();
        }

        static bool CastArms(PlayerController caster, CardId card, Vector3 target, float yaw, ulong extra, double now)
        {
            switch (card)
            {
                case CardId.HammerSpin:
                {
                    // charged this long (the owner says; never less than it takes, never more than it can be)
                    float held = Mathf.Clamp(extra / 1000f, 0f, 10f);
                    if (held < SpellPlans.SpinMinHold - 0.15f || float.IsNaN(target.x + target.y + target.z)) return false;
                    Vector3 from = caster.FeetPosition;
                    Vector3 flat = target - from;
                    if (Mathf.Abs(flat.y) > 1.5f) return false;
                    flat.y = 0f;
                    float len = flat.magnitude;
                    if (len > SpellPlans.SpinRange(held) + 2f) return false; // a little slack for lag
                    Vector3 dir = len > 0.05f ? flat / len : SpellPlans.Level(yaw);
                    float time = SpellPlans.SpinTime(len);
                    SpinsS.RemoveAll(s => s.caster == caster);
                    SpinsS.Add(new SpinState
                    {
                        caster = caster, from = from, to = new Vector3(target.x, from.y, target.z), dir = dir, start = now, time = time, len = len,
                        carved = -SpellPlans.SpinRadius,
                    });
                    CastTimeOverride = time + 0.1f; // busy the whole way
                    WorldNet.ServerSpinStart(caster.NetworkObjectId, time);
                    return true;
                }
                case CardId.Wolfgang:
                    WolfsS.RemoveAll(w => w.caster == caster);
                    WolfsS.Add(new WolfState { caster = caster, start = now });
                    return true;
                case CardId.HeavenlySlash:
                {
                    float roll = Random.Range(0f, 360f);
                    HeavenCastsS.RemoveAll(h => h.caster == caster);
                    HeavenCastsS.Add(new HeavenCastState { caster = caster, start = now, roll = roll });
                    WorldNet.ServerHeavenRoll(caster.NetworkObjectId, roll);
                    return true;
                }
                case CardId.Parry:
                    Combatant.DamageScale ??= ScaleDamage; // so being untouchable mid-dash counts
                    ParriesS.RemoveAll(p => p.caster == caster);
                    ParriesS.Add(new ParryState { caster = caster, until = now + SpellPlans.ParryWindow });
                    return true;
            }
            return false;
        }

        /// <summary>Mid Hammer Spin: nothing knocks them down (they still take damage).</summary>
        public static bool Spinning(Combatant c)
        {
            if (c == null) return false;
            double now = WorldNet.Now;
            foreach (var s in SpinsS) if (s.caster == c && now <= s.start + s.time + 0.15) return true;
            return false;
        }

        /// <summary>Parry's dash back: untouchable.</summary>
        static bool ParryDashing(Combatant c)
        {
            if (c == null) return false;
            foreach (var d in ParryDashesS) if (d.caster == c) return true;
            return false;
        }

        /// <summary>Knocked down: the pistols drop (no more shots), no leap or cut, no guard.</summary>
        static void InterruptArms(PlayerController pc, bool knocked)
        {
            if (!knocked) return;
            WolfsS.RemoveAll(w => w.caster == pc);
            HeavenCastsS.RemoveAll(h => h.caster == pc);
            ParriesS.RemoveAll(p => p.caster == pc);
            InterruptRang(pc, knocked);
        }

        /// <summary>A practice chamber is being reset: what its player had going stops, and bullets in it are dropped.</summary>
        static void ClearArms(PlayerController pc, System.Func<Vector3, bool> inside)
        {
            if (pc != null)
            {
                SpinsS.RemoveAll(s => s.caster == pc);
                WolfsS.RemoveAll(w => w.caster == pc);
                HeavenCastsS.RemoveAll(h => h.caster == pc);
                HeavenCutsS.RemoveAll(h => h.caster == pc);
                ParriesS.RemoveAll(p => p.caster == pc);
                ParryDashesS.RemoveAll(p => p.caster == pc);
            }
            WolfBulletsS.RemoveAll(b => b.by == pc || inside(b.lastPos));
            ClearRang(pc, inside);
        }

        static void TickArms(double now)
        {
            TickSpins(now);
            TickWolfs(now);
            TickWolfBullets(now);
            TickHeavenCasts(now);
            TickHeavenCuts(now);
            TickParries(now);
            TickRangs(now, Time.deltaTime);
        }

        // ------------------------------------------------------------------ Hammer Spin

        static void TickSpins(double now)
        {
            var world = VoxelWorld.Current;
            for (int i = SpinsS.Count - 1; i >= 0; i--)
            {
                var s = SpinsS[i];
                var me = s.caster;
                float t = (float)(now - s.start);
                if (me == null || !me.IsAlive || t > s.time + 0.15f) { SpinsS.RemoveAt(i); continue; }
                float d = SpellPlans.SpinEase(t / s.time) * s.len; // (speeding up, then slowing down: as the owner moves)

                // the voxels just ahead are smashed out (and the round end of the sweep at the finish)
                float upto = t >= s.time ? s.len + SpellPlans.SpinRadius : Mathf.Min(s.len + SpellPlans.SpinRadius, d + SpellPlans.SpinLead);
                if (upto > s.carved + 0.2f || t >= s.time && upto > s.carved)
                {
                    SpellPlans.SpinCells(world, s.from, s.to, s.carved, upto, _armsCells);
                    s.carved = upto;
                    if (_armsCells.Count > 0) WorldNet.ServerSpinSmash(new List<Vector3Int>(_armsCells), s.dir);
                }

                // whoever the hammer reaches along the way so far (each once)
                Vector3 side = new(s.dir.z, 0f, -s.dir.x);
                foreach (var c in Combatant.Registry.ToArray())
                {
                    if (c == null || c == me || !c.IsAlive || s.hit.Contains(c) || Folded(c)) continue;
                    Vector3 rel = c.FeetPosition - s.from;
                    float along = Mathf.Clamp(Vector3.Dot(rel, s.dir), 0f, d);
                    Vector3 off = rel - s.dir * along;
                    float up = off.y;
                    off.y = 0f;
                    if (up < -1.3f || up > 1.6f || off.magnitude > SpellPlans.SpinRadius + 0.35f) continue;
                    s.hit.Add(c);
                    // thrown out to the side the hammer came round on, and on along the spin
                    Vector3 away = off.sqrMagnitude > 0.04f ? off.normalized : side;
                    Vector3 knock = (away * 0.8f + s.dir * 0.6f).normalized * SpellPlans.SpinKnock + Vector3.up * SpellPlans.SpinKnockUp;
                    Vector3 hips = s.from + s.dir * along + Vector3.up * 0.95f;
                    WorldNet.ServerHammerHit(c.transform.position, away);
                    Hit(c, me, SpellPlans.SpinDamage, c.transform.position, knock, SpellPlans.SpinRagdoll, from: hips);
                }
            }
        }

        // ------------------------------------------------------------------ Wolfgang

        static void TickWolfs(double now)
        {
            for (int i = WolfsS.Count - 1; i >= 0; i--)
            {
                var w = WolfsS[i];
                var me = w.caster;
                if (me == null || !me.IsAlive) { WolfsS.RemoveAt(i); continue; }
                float t = (float)(now - w.start);
                while (w.fired < SpellPlans.WolfShotsEach * 2 && t >= SpellPlans.WolfShotAt(w.fired))
                {
                    FireWolf(me, w.fired, SpellPlans.WolfShotAt(w.fired));
                    w.fired++;
                }
                if (w.fired >= SpellPlans.WolfShotsEach * 2) WolfsS.RemoveAt(i);
            }
        }

        static void FireWolf(PlayerController me, int k, float t)
        {
            int side = k % 2 == 0 ? 1 : -1;
            WolfMotion.Muzzle(me, t, side, out var origin, out var dir);
            // a little spread: up to WolfSpread degrees off, any way round
            dir = Quaternion.LookRotation(dir) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)) * Quaternion.Euler(Mathf.Sqrt(Random.value) * SpellPlans.WolfSpread, 0f, 0f) * Vector3.forward;
            Vector3 chest = me.transform.position + Vector3.up * 0.45f;
            // believable, and not poked through a wall
            if ((origin - chest).magnitude > 3f * SpellPlans.BodyGrow(me)) origin = chest + dir * 0.6f;
            if (Physics.Linecast(chest, origin, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) origin = chest;
            int id = WorldNet.ServerWolfShot(me.NetworkObjectId, side, origin, dir);
            if (id == 0) return;
            WolfBulletsS.Add(new WolfBullet { id = id, by = me, owner = me.NetworkObjectId, origin = origin, dir = dir, lastPos = origin, start = WorldNet.Now });
        }

        static void TickWolfBullets(double now)
        {
            var world = VoxelWorld.Current;
            for (int i = WolfBulletsS.Count - 1; i >= 0; i--)
            {
                var b = WolfBulletsS[i];
                float flown = Mathf.Min(SpellPlans.WolfSpeed * Mathf.Max(0f, (float)(now - b.start)), SpellPlans.WolfRange);
                Vector3 pos = b.origin + b.dir * flown, step = pos - b.lastPos;
                float dist = step.magnitude;
                float r = SpellPlans.WolfRadius;
                if (BarrierBlocks(b.owner, pos, r)) { WorldNet.ServerWolfEnd(b.id, pos, true, false); WolfBulletsS.RemoveAt(i); continue; }
                var bubble = BubbleBlocking(b.owner, pos, r);
                if (bubble != null)
                {
                    HurtBubble(bubble, SpellPlans.WolfDamage, pos);
                    WorldNet.ServerWolfEnd(b.id, pos, true, false);
                    WolfBulletsS.RemoveAt(i);
                    continue;
                }
                ShotSetsOff(b.lastPos, pos, r); // flame bombs on the way go off
                if (dist > 1e-4f && SweepBall(b.owner, b.lastPos, step / dist, dist, r, out var hit, out var victim) && !(victim != null && Combatant.Allies(b.by, victim)))
                {
                    bool hole = false;
                    if (victim != null)
                    {
                        Vector3 flat = new(b.dir.x, 0f, b.dir.z);
                        Vector3 knock = (flat.sqrMagnitude > 0.01f ? flat.normalized : b.dir) * SpellPlans.WolfKnock + Vector3.up * SpellPlans.WolfKnockUp;
                        Hit(victim, b.by, SpellPlans.WolfDamage, hit.point, knock, SpellPlans.WolfRagdoll, from: hit.point - b.dir);
                    }
                    else
                    {
                        hole = true;
                        if (world != null && VoxelWorld.IsVoxelCollider(hit.collider))
                        {
                            // the ground round where it struck breaks (2 voxels)
                            var cell = world.CellOf(hit.point - hit.normal * 0.1f);
                            WorldNet.ServerRemoveSphere(cell, SpellPlans.R4(SpellPlans.WolfBreak), 3f, -b.dir * 2f + Vector3.up * 2f);
                        }
                    }
                    WorldNet.ServerWolfEnd(b.id, hit.point, true, hole);
                    WolfBulletsS.RemoveAt(i);
                    continue;
                }
                b.lastPos = pos;
                if (flown >= SpellPlans.WolfRange) { WorldNet.ServerWolfEnd(b.id, pos, false, false); WolfBulletsS.RemoveAt(i); }
            }
        }

        /// <summary>The Hierophant's wall sends Wolfgang's bullets back (as its caster's own, a full flight again).</summary>
        static void ReflectWolfBullets(HierophantState h, ulong me, double now)
        {
            foreach (var b in WolfBulletsS)
            {
                if (b.owner == me) continue;
                Vector3 pos = b.origin + b.dir * Mathf.Min(SpellPlans.WolfSpeed * Mathf.Max(0f, (float)(now - b.start)), SpellPlans.WolfRange);
                if (!SpellPlans.HierophantTouch(b.lastPos, pos, SpellPlans.WolfRadius, h.center, h.rot, out var at, out var n)) continue;
                if (Vector3.Dot(b.dir, n) >= 0f) continue;
                Vector3 from = at + n * 0.05f;
                b.owner = me;
                b.by = h.caster;
                b.origin = from;
                b.dir = SpellPlans.Reflect(b.dir, n).normalized;
                b.start = now;
                b.lastPos = from;
                WorldNet.ServerWolfTurn(b.id, from, b.dir);
                WorldNet.ServerHierophantReflect(h.id, at);
            }
        }

        // ------------------------------------------------------------------ Heavenly Slash

        static void TickHeavenCasts(double now)
        {
            for (int i = HeavenCastsS.Count - 1; i >= 0; i--)
            {
                var h = HeavenCastsS[i];
                var me = h.caster;
                if (me == null || !me.IsAlive) { HeavenCastsS.RemoveAt(i); continue; }
                if (now < h.start + SpellPlans.HeavenRelease) continue;
                HeavenCastsS.RemoveAt(i);
                // the cut leaves along wherever they're looking now, at its angle
                Vector3 origin = me.transform.position + Vector3.up * 0.3f;
                Vector3 dir = ViewDir(me);
                int id = WorldNet.ServerHeaven(me.NetworkObjectId, origin, dir, h.roll);
                HeavenCutsS.Add(new HeavenCutState { id = id, caster = me, origin = origin, dir = dir, rot = SpellPlans.HeavenRotation(dir, h.roll), start = now });
            }
        }

        static void TickHeavenCuts(double now)
        {
            var world = VoxelWorld.Current;
            for (int i = HeavenCutsS.Count - 1; i >= 0; i--)
            {
                var h = HeavenCutsS[i];
                float t = (float)(now - h.start);
                float front = Mathf.Min(SpellPlans.HeavenSpeed * t, SpellPlans.HeavenRange);
                Vector3 center = h.origin + h.dir * front;
                ulong owner = h.caster != null ? h.caster.NetworkObjectId : ulong.MaxValue;

                // an enemy barrier stops it; a bubble takes the blow
                if (BarrierBlocks(owner, center, 0.5f)) { HeavenCutsS.RemoveAt(i); WorldNet.ServerHeavenEnd(h.id); continue; }
                var bubble = BubbleBlocking(owner, center, 0.5f);
                if (bubble != null)
                {
                    HurtBubble(bubble, SpellPlans.HeavenDamage * SpellPlans.HeavenStrikes, center);
                    HeavenCutsS.RemoveAt(i);
                    WorldNet.ServerHeavenEnd(h.id);
                    continue;
                }

                // every breakable voxel on its line, layer by layer as it goes
                _armsCells.Clear();
                while (world != null && h.carved + VoxelWorld.Size <= front + 1e-3f)
                {
                    h.carved += VoxelWorld.Size;
                    SpellPlans.HeavenLayer(world, h.origin, h.rot, h.carved, h.seen, _armsCells);
                }
                if (_armsCells.Count > 0) WorldNet.ServerHeavenCarve(new List<Vector3Int>(_armsCells));

                // whoever it passes through (each once): four strikes
                if (h.caster != null)
                {
                    foreach (var c in Combatant.Registry.ToArray())
                    {
                        if (c == null || c == h.caster || !c.IsAlive || h.hit.Contains(c) || Folded(c)) continue;
                        Vector3 p = c.transform.position;
                        bool touch = SpellPlans.HeavenTouches(h.origin, h.rot, h.swept, front, p, 0.4f)
                                  || SpellPlans.HeavenTouches(h.origin, h.rot, h.swept, front, p + Vector3.up * 0.65f, 0.35f)
                                  || SpellPlans.HeavenTouches(h.origin, h.rot, h.swept, front, c.FeetPosition + Vector3.up * 0.3f, 0.35f);
                        if (!touch) continue;
                        h.hit.Add(c);
                        Vector3 flat = new(h.dir.x, 0f, h.dir.z);
                        Vector3 knock = (flat.sqrMagnitude > 0.01f ? flat.normalized : Vector3.zero) * SpellPlans.HeavenKnock + Vector3.up * SpellPlans.HeavenKnockUp;
                        StartFlurry(c, h.caster, SpellPlans.HeavenDamage, 0.0, knock, SpellPlans.HeavenRagdoll, h.origin, SpellPlans.HeavenStrikes);
                    }
                }
                h.swept = front;
                if (front >= SpellPlans.HeavenRange) HeavenCutsS.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------------ Parry

        /// <summary>
        /// Every damaging hit asks this first. On guard with Parry: the blow does nothing, and the parrier dashes back at
        /// whoever dealt it. Mid-dash: it's turned aside too. True: the blow is spent.
        /// </summary>
        static bool Parries(Combatant victim, PlayerController attacker, Combatant striker, Vector3 point) =>
            ParryCatch(victim, striker != null ? striker : attacker, point);

        /// <summary>
        /// A blow or a hold (fire's first touch, a grasp, a hex taking hold, a trap, a tornado...) from <paramref name="foe"/>
        /// is about to land on <paramref name="victim"/>: on guard with Parry, it doesn't, and they dash back at the foe.
        /// Burning afterwards never asks (those ticks aren't blows). True: it's spent, skip it.
        /// </summary>
        static bool ParryCatch(Combatant victim, Combatant foe, Vector3 point)
        {
            if (victim is not PlayerController pv || ParriesS.Count == 0 && ParryDashesS.Count == 0) return false;
            Vector3 swordAt = pv.transform.position + Vector3.up * 0.2f + Quaternion.Euler(0f, pv.ViewYaw, 0f) * Vector3.forward * 0.45f;
            Vector3 fromDir = (foe != null ? foe.transform.position : point) - pv.transform.position;
            if (ParryDashing(pv))
            {
                WorldNet.ServerParryDeflect(swordAt, fromDir);
                return true;
            }
            double now = WorldNet.Now;
            var g = ParriesS.Find(p => p.caster == pv);
            if (g == null || now > g.until) return false;
            if (foe == null || foe == pv || !foe.IsAlive) return false;
            ParriesS.Remove(g);
            foreach (var f in Flurries) if (f.victim == pv) f.left = 0; // the rest of a blade's strikes are turned aside too (dropped by TickFlurries)
            WorldNet.ServerParryDeflect(swordAt, fromDir);
            StartParryDash(pv, foe);
            return true;
        }

        static void StartParryDash(PlayerController pv, Combatant foe)
        {
            double now = WorldNet.Now;
            Vector3 a = pv.transform.position, b = foe.transform.position;
            Vector3 d = b - a;
            float dist = d.magnitude;
            Vector3 dir = dist > 0.05f ? d / dist : Quaternion.Euler(0f, pv.ViewYaw, 0f) * Vector3.forward;
            float go = Mathf.Clamp(dist - SpellPlans.ParryStop, 0f, SpellPlans.ParryRange);
            var world = VoxelWorld.Current;
            Vector3 to = go > 0.05f ? SpellPlans.ParryDestination(world, a, dir, go) : a;
            float time = SpellPlans.ParryTime((to - a).magnitude);
            // the way is broken open first (the parrier is already on the move on their own screen)
            var cells = SpellPlans.ParryCells(world, a, to);
            if (cells.Count > 0) WorldNet.ServerBladeCut(cells, dir);
            pv.ServerEndCast(); // (the guard is over: free once the stab is done)
            pv.ServerParryDash(to, time);
            ParryDashesS.RemoveAll(p => p.caster == pv);
            ParryDashesS.Add(new ParryDashState { caster = pv, foe = foe, to = to, dir = dir, arrive = now + time });
        }

        static void TickParries(double now)
        {
            ParriesS.RemoveAll(p => p.caster == null || !p.caster.IsAlive || now > p.until);
            if (ParryDashesS.Count == 0) return;
            // (taken off the list first: a stab can set off the other one's own parry, which dashes back)
            var due = ParryDashesS.FindAll(d => d.caster == null || !d.caster.IsAlive || now >= d.arrive);
            ParryDashesS.RemoveAll(d => due.Contains(d));
            foreach (var d in due)
            {
                var me = d.caster;
                if (me == null || !me.IsAlive) continue;
                var foe = d.foe;
                if (foe == null || !foe.IsAlive || (foe.transform.position - d.to).magnitude > SpellPlans.ParryReach) continue; // got away
                // the stab: straight through them, thrown on the way it went
                Vector3 point = foe.transform.position + Vector3.up * 0.2f;
                Vector3 flat = new(d.dir.x, 0f, d.dir.z);
                Vector3 knock = (flat.sqrMagnitude > 0.01f ? flat.normalized : d.dir) * SpellPlans.ParryKnock + Vector3.up * SpellPlans.ParryKnockUp;
                WorldNet.ServerParryStrike(point, d.dir);
                Hit(foe, me, SpellPlans.ParryDamage, point, knock, SpellPlans.ParryRagdoll, from: me.transform.position);
            }
        }
    }
}
