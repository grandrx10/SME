using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for Rock Swarm and Ice Spike.
    ///  - Rock Swarm: hands up for 0.3 s while four balls of rock are torn out of the ground round the caster; then
    ///    they circle the caster (counter-clockwise, 3 m out) until they all break, a new swarm is cast, or 6 s are up. An enemy
    ///    projectile (thrown rock, fireball, missile) coming within 3 m sends the nearest rock flying into it: both
    ///    break, the rock into tiny voxels. An enemy touching a rock takes 10, thrown and ragdolled, and the rock breaks. A rock knocking
    ///    against the ground bounces off it and chips the surface (it never breaks on ground); two enemies' rocks
    ///    meeting both break. The caster's own projectiles pass through: the rocks duck out of their way.
    ///  - Ice Spike: 0.5 s crouched (rooted, facing fixed; knocked down = no spikes), then spikes of ice shoot up one
    ///    after another along a straight line in front, a little left or right of it, of random heights. A rising
    ///    spike throws an enemy up (25, ragdolled), once per cast; an enemy walking into a risen one takes 10 and is
    ///    pushed off it (not knocked down), once per cast, unless the spikes already threw them. Fire near one (a fireball,
    ///    a hellfire wall, a burning person, burning ground, a blast) crumbles it away, singed.
    /// </summary>
    public static partial class SpellServer
    {
        // ================================================================== Rock Swarm

        /// <summary>A projectile a rock can fly at (or duck out of the way of).</summary>
        enum ShotKind : byte { Rock, Fireball, Missile }
        struct Shot { public ShotKind kind; public int id, sub; public ulong owner; public float radius; }
        class Catch { public int k; public double at; public Vector3 point; public Shot shot; }

        class SwarmState
        {
            public int id; public PlayerController caster; public double start;
            public readonly double[] nextBump = new double[SpellPlans.SwarmRocks];
            public readonly double[] nextDodge = new double[SpellPlans.SwarmRocks];
            public readonly Dictionary<Combatant, double> struck = new();
            public readonly List<Catch> catches = new();
        }

        static readonly List<SwarmState> SwarmsS = new();
        static readonly List<Shot> Shots = new();
        /// <summary>Projectiles a rock is already flying at.</summary>
        static readonly HashSet<(ShotKind, int, int)> Chased = new();

        static (ShotKind, int, int) Key(Shot s) => (s.kind, s.id, s.sub);

        static bool CastSwarm(PlayerController caster, VoxelWorld world, double now)
        {
            var centers = new List<Vector3Int>();
            Vector3 feet = caster.FeetPosition;
            if (!SpellPlans.PlanSwarm(world, feet, caster.ViewYaw, centers)) return false;
            // round the ring in order (counter-clockwise from the first), so each rock rises into the slot nearest its hole
            float Angle(Vector3Int c)
            {
                Vector3 d = world.CellCenter(c) - feet;
                return Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
            }
            float phase = Angle(centers[0]);
            centers.Sort((a, b) => Mathf.Repeat(Angle(a) - phase, 360f).CompareTo(Mathf.Repeat(Angle(b) - phase, 360f)));
            // a new swarm replaces the old one
            for (int i = SwarmsS.Count - 1; i >= 0; i--)
                if (SwarmsS[i].caster == caster) { EndSwarm(SwarmsS[i]); SwarmsS.RemoveAt(i); }
            int id = WorldNet.ServerSwarm(caster.NetworkObjectId, phase, centers);
            if (id == 0) return false;
            SwarmsS.Add(new SwarmState { id = id, caster = caster, start = now });
            return true;
        }

        /// <summary>Every rock still going crumbles where it is.</summary>
        static void EndSwarm(SwarmState s)
        {
            foreach (var c in s.catches) Chased.Remove(Key(c.shot));
            s.catches.Clear();
            var net = WorldNet.Instance;
            if (net == null || !net.Swarms.TryGetValue(s.id, out var fx)) return;
            double now = WorldNet.Now;
            for (int k = 0; k < fx.rocks.Length; k++)
                if (fx.rocks[k].alive) WorldNet.ServerSwarmBreak(s.id, k, WorldNet.SwarmPosition(fx, k, now), false);
        }

        static void TickSwarms(WorldNet net, double now)
        {
            if (SwarmsS.Count == 0) return;
            GatherShots(net, now);
            float R = SpellPlans.SwarmRockRadius;
            for (int i = SwarmsS.Count - 1; i >= 0; i--)
            {
                var s = SwarmsS[i];
                if (!net.Swarms.TryGetValue(s.id, out var fx))
                {
                    foreach (var c in s.catches) Chased.Remove(Key(c.shot));
                    SwarmsS.RemoveAt(i);
                    continue;
                }
                if (s.caster == null || !s.caster.IsAlive || AllBroken(fx) || now > s.start + SpellPlans.SwarmLife)
                {
                    EndSwarm(s);
                    SwarmsS.RemoveAt(i);
                    continue;
                }
                ResolveCatches(s, now);
                if (now < s.start + SpellPlans.SwarmGather) continue;
                ulong me = s.caster.NetworkObjectId;
                for (int k = 0; k < fx.rocks.Length; k++)
                {
                    if (!WorldNet.SwarmCircling(fx, k, now)) continue;
                    Vector3 p = WorldNet.SwarmPosition(fx, k, now);
                    // an enemy's barrier: it breaks on it
                    if (BarrierBlocks(me, p, R)) { WorldNet.ServerSwarmBreak(s.id, k, p, true); continue; }
                    StrikeWithRock(s, fx, k, p, now);
                    ScrapeRock(s, k, p, now);
                }
                GuardSwarm(s, fx, now);
            }
            ClashSwarms(net, now);
        }

        static bool AllBroken(WorldNet.SwarmFx fx)
        {
            foreach (var r in fx.rocks) if (r.alive) return false;
            return true;
        }

        /// <summary>An enemy touching a circling rock: 10, thrown on round the ring and outwards, ragdolled; the rock breaks into tiny voxels.</summary>
        static void StrikeWithRock(SwarmState s, WorldNet.SwarmFx fx, int k, Vector3 p, double now)
        {
            float reach = SpellPlans.SwarmRockRadius + 0.35f;
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || c == s.caster || !c.IsAlive || Folded(c) || Shielded(c)) continue;
                if (s.struck.TryGetValue(c, out double at) && now < at + SpellPlans.SwarmHitEvery) continue;
                // the nearest point of the body (feet to head)
                Vector3 feet = c.FeetPosition, ab = Vector3.up * 1.9f;
                float u = Mathf.Clamp01(Vector3.Dot(p - feet, ab) / ab.sqrMagnitude);
                if ((p - (feet + ab * u)).magnitude > reach) continue;
                s.struck[c] = now;
                Vector3 along = SpellPlans.SwarmTangent(fx.phase, k, fx.start, now);
                Vector3 outward = c.transform.position - WorldNet.SwarmCenter(fx);
                outward.y = 0f;
                outward = outward.sqrMagnitude > 0.01f ? outward.normalized : along;
                Vector3 knock = (along + outward).normalized * SpellPlans.SwarmKnockOut + Vector3.up * SpellPlans.SwarmKnockUp;
                Hit(c, s.caster, SpellPlans.SwarmDamage, p, knock, SpellPlans.SwarmRagdoll, from: p);
                // the rock is spent: it falls apart on them
                WorldNet.ServerSwarmBreak(s.id, k, p, true);
                return;
            }
        }

        /// <summary>A rock knocking into the ground or a wall: it bounces off, chipping the surface (it never breaks on it).</summary>
        static void ScrapeRock(SwarmState s, int k, Vector3 p, double now)
        {
            var w = VoxelWorld.Current;
            if (w == null || now < s.nextBump[k]) return;
            float R = SpellPlans.SwarmRockRadius;
            var c0 = w.CellOf(p);
            int r = Mathf.CeilToInt(R / VoxelWorld.Size);
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            for (int dz = -r; dz <= r; dz++)
            {
                var cell = new Vector3Int(c0.x + dx, c0.y + dy, c0.z + dz);
                if (!w.IsSolid(cell)) continue;
                Vector3 cc = w.CellCenter(cell);
                if ((cc - p).sqrMagnitude > R * R) continue;
                sum += cc;
                n++;
            }
            if (n < 2) return;
            Vector3 normal = p - sum / n;
            normal = normal.sqrMagnitude > 1e-4f ? normal.normalized : Vector3.up;
            s.nextBump[k] = now + SpellPlans.SwarmBumpEvery;
            Vector3 contact = p - normal * (R * 0.7f);
            WorldNet.ServerSwarmBump(s.id, k, normal, SpellPlans.SwarmBumpSize, SpellPlans.SwarmBumpTime, w.CellOf(contact), SpellPlans.SwarmScrapeR4);
        }

        /// <summary>
        /// Enemy projectiles coming within <see cref="SpellPlans.SwarmGuard"/> of the caster: the nearest circling rock
        /// flies to meet each one. The caster's own: any rock in the way ducks aside.
        /// </summary>
        static void GuardSwarm(SwarmState s, WorldNet.SwarmFx fx, double now)
        {
            ulong me = s.caster.NetworkObjectId;
            Vector3 center = WorldNet.SwarmCenter(fx);
            float R = SpellPlans.SwarmRockRadius;
            foreach (var shot in Shots)
            {
                if (shot.owner == me) { Dodge(s, fx, shot, now); continue; }
                if (Chased.Contains(Key(shot))) continue;
                Vector3 a = ShotAt(shot, now, now), b = ShotAt(shot, now + SpellPlans.SwarmLookAhead, now);
                if (float.IsInfinity(a.x) || float.IsInfinity(b.x)) continue;
                if (DistanceToSegment(center, a, b) > SpellPlans.SwarmGuard + shot.radius) continue;
                // the nearest rock in the ring
                int best = -1;
                float bestD = float.MaxValue;
                Vector3 from = default;
                for (int k = 0; k < fx.rocks.Length; k++)
                {
                    if (!WorldNet.SwarmCircling(fx, k, now)) continue;
                    Vector3 p = WorldNet.SwarmPosition(fx, k, now);
                    float d = (p - a).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = k; from = p; }
                }
                if (best < 0) continue; // all out of the ring already
                // where (and when) it can meet it
                double meet = -1;
                Vector3 point = default;
                for (int step = 1; step <= 30; step++)
                {
                    float dt = step * 0.01f;
                    Vector3 q = ShotAt(shot, now + dt, now);
                    if ((q - from).magnitude > SpellPlans.SwarmInterceptSpeed * dt + R + shot.radius) continue;
                    meet = now + dt;
                    point = q;
                    break;
                }
                if (meet < 0) continue;
                WorldNet.ServerSwarmFly(s.id, best, from, point, now, meet);
                s.catches.Add(new Catch { k = best, at = meet, point = point, shot = shot });
                Chased.Add(Key(shot));
            }
        }

        /// <summary>The caster's own projectile about to pass through a rock: the rock ducks out of its way.</summary>
        static void Dodge(SwarmState s, WorldNet.SwarmFx fx, Shot shot, double now)
        {
            float R = SpellPlans.SwarmRockRadius;
            for (int k = 0; k < fx.rocks.Length; k++)
            {
                if (!WorldNet.SwarmCircling(fx, k, now) || now < s.nextDodge[k]) continue;
                for (int step = 0; step <= 4; step++)
                {
                    double t = now + step * 0.03;
                    Vector3 q = ShotAt(shot, t, now), p = WorldNet.SwarmPosition(fx, k, t);
                    if ((q - p).magnitude > R + shot.radius + 0.25f) continue;
                    Vector3 vel = ShotAt(shot, t + 0.02, now) - q;
                    Vector3 away = vel.sqrMagnitude > 1e-6f ? Vector3.ProjectOnPlane(p - q, vel.normalized) : p - q;
                    away = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.up;
                    WorldNet.ServerSwarmBump(s.id, k, away, SpellPlans.SwarmDodgeSize, SpellPlans.SwarmDodgeTime, default, 0);
                    s.nextDodge[k] = now + SpellPlans.SwarmDodgeTime;
                    s.nextBump[k] = System.Math.Max(s.nextBump[k], now + SpellPlans.SwarmDodgeTime);
                    break;
                }
            }
        }

        /// <summary>Rocks that reached what they flew at: it's stopped (if it's still there), and the rock falls apart into tiny voxels.</summary>
        static void ResolveCatches(SwarmState s, double now)
        {
            for (int c = s.catches.Count - 1; c >= 0; c--)
            {
                var ct = s.catches[c];
                if (now < ct.at) continue;
                s.catches.RemoveAt(c);
                Chased.Remove(Key(ct.shot));
                float near = SpellPlans.SwarmRockRadius + ct.shot.radius + 0.8f;
                if (ShotLive(ct.shot, now, out var at) && Mathf.Min((at - ct.point).magnitude, (ShotAt(ct.shot, ct.at, now) - ct.point).magnitude) <= near)
                    StopShot(ct.shot, at);
                WorldNet.ServerSwarmBreak(s.id, ct.k, ct.point, true);
            }
        }

        /// <summary>Two enemies' rocks meeting: both break.</summary>
        static void ClashSwarms(WorldNet net, double now)
        {
            if (SwarmsS.Count < 2) return;
            float touch = SpellPlans.SwarmRockRadius * 2f;
            for (int i = 0; i < SwarmsS.Count; i++)
            for (int j = i + 1; j < SwarmsS.Count; j++)
            {
                var a = SwarmsS[i];
                var b = SwarmsS[j];
                if (a.caster == b.caster || now < a.start + SpellPlans.SwarmGather || now < b.start + SpellPlans.SwarmGather) continue;
                if (!net.Swarms.TryGetValue(a.id, out var fa) || !net.Swarms.TryGetValue(b.id, out var fb)) continue;
                for (int ka = 0; ka < fa.rocks.Length; ka++)
                for (int kb = 0; kb < fb.rocks.Length; kb++)
                {
                    if (!fa.rocks[ka].alive || !fb.rocks[kb].alive) continue;
                    Vector3 pa = WorldNet.SwarmPosition(fa, ka, now), pb = WorldNet.SwarmPosition(fb, kb, now);
                    if ((pa - pb).sqrMagnitude > touch * touch) continue;
                    Vector3 mid = (pa + pb) * 0.5f;
                    WorldNet.ServerSwarmBreak(a.id, ka, mid, true);
                    WorldNet.ServerSwarmBreak(b.id, kb, mid, true);
                }
            }
        }

        // ---------------- projectiles ----------------

        /// <summary>Every projectile in flight right now: thrown / blown rocks, fireballs, missiles.</summary>
        static void GatherShots(WorldNet net, double now)
        {
            Shots.Clear();
            foreach (var r in Rocks)
                if (r.launched && net.Rocks.ContainsKey(r.id))
                    Shots.Add(new Shot { kind = ShotKind.Rock, id = r.id, owner = r.caster != null ? r.caster.NetworkObjectId : ulong.MaxValue, radius = r.radius });
            foreach (var f in FireballsS)
                if (f.launched && net.Fireballs.TryGetValue(f.id, out var fx))
                    Shots.Add(new Shot { kind = ShotKind.Fireball, id = f.id, owner = fx.owner, radius = f.Radius });
            foreach (var v in Volleys)
            {
                if (!net.Missiles.TryGetValue(v.id, out var mfx) || !mfx.launched) continue;
                for (int m = 0; m < mfx.m.Length; m++)
                    if (mfx.m[m].alive)
                        Shots.Add(new Shot { kind = ShotKind.Missile, id = v.id, sub = m, owner = mfx.m[m].owner, radius = SpellPlans.MissileRadius });
            }
        }

        /// <summary>Where a projectile will be at <paramref name="t"/> (rocks and fireballs fly a set path; missiles: straight on from now).</summary>
        static Vector3 ShotAt(Shot s, double t, double now)
        {
            var net = WorldNet.Instance;
            switch (s.kind)
            {
                case ShotKind.Rock:
                    return net.Rocks.TryGetValue(s.id, out var r) ? WorldNet.RockPosition(r, t) : Vector3.positiveInfinity;
                case ShotKind.Fireball:
                    return net.Fireballs.TryGetValue(s.id, out var f) ? WorldNet.FireballPosition(f, t) : Vector3.positiveInfinity;
                default:
                    if (!net.Missiles.TryGetValue(s.id, out var m) || s.sub >= m.m.Length) return Vector3.positiveInfinity;
                    var ms = m.m[s.sub];
                    return ms.pos + ms.dir.normalized * (SpellPlans.MissileSpeed * (float)(t - now));
            }
        }

        /// <summary>Is the projectile still flying (and where is it now)?</summary>
        static bool ShotLive(Shot s, double now, out Vector3 at)
        {
            at = default;
            var net = WorldNet.Instance;
            if (net == null) return false;
            switch (s.kind)
            {
                case ShotKind.Rock:
                {
                    var rock = Rocks.Find(x => x.id == s.id);
                    if (rock == null || !rock.launched || !net.Rocks.TryGetValue(s.id, out var fx)) return false;
                    at = WorldNet.RockPosition(fx, now);
                    return true;
                }
                case ShotKind.Fireball:
                {
                    var f = FireballsS.Find(x => x.id == s.id);
                    if (f == null || !f.launched || !net.Fireballs.TryGetValue(s.id, out var fx)) return false;
                    at = WorldNet.FireballPosition(fx, now);
                    return true;
                }
                default:
                {
                    if (!net.Missiles.TryGetValue(s.id, out var fx) || s.sub >= fx.m.Length || !fx.m[s.sub].alive) return false;
                    at = fx.m[s.sub].pos;
                    return true;
                }
            }
        }

        /// <summary>A rock flew into it: a thrown rock bursts, a fireball goes off there, a missile pops.</summary>
        static void StopShot(Shot s, Vector3 at)
        {
            var net = WorldNet.Instance;
            switch (s.kind)
            {
                case ShotKind.Rock:
                {
                    var rock = Rocks.Find(x => x.id == s.id);
                    if (rock == null) return;
                    Impact(rock, at, null, 0, null);
                    Rocks.Remove(rock);
                    return;
                }
                case ShotKind.Fireball:
                {
                    var f = FireballsS.Find(x => x.id == s.id);
                    if (f == null || !net.Fireballs.TryGetValue(s.id, out var fx)) return;
                    ExplodeFireball(f, fx, at, null);
                    FireballsS.Remove(f);
                    return;
                }
                default:
                {
                    if (!net.Missiles.TryGetValue(s.id, out var fx) || s.sub >= fx.m.Length || !fx.m[s.sub].alive) return;
                    fx.m[s.sub].alive = false;
                    WorldNet.ServerMissileImpact(s.id, s.sub, fx.m[s.sub].pos, default, 0, false);
                    return;
                }
            }
        }

        static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float u = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (p - (a + ab * u)).magnitude;
        }

        // ================================================================== Ice Spike

        class IceCastState { public PlayerController caster; public Vector3 dir; public double at; }
        class IceLineState
        {
            public PlayerController caster; public Vector3 dir, side; public double start;
            public List<SpellPlans.IceSlot> slots; public float[] heights, jitter; public int next;
            public readonly HashSet<Combatant> hit = new(); // nobody is struck by more than one spike
            public readonly HashSet<Combatant> bumped = new(); // walked into a standing spike (once per cast)
        }
        class IceSpikeState
        {
            public int id; public IceLineState line; public Vector3 b; public float height; public double start;
            public List<Vector3Int> cells;
            public bool melting; public int meltStep; public double nextMelt;
        }

        static readonly List<IceCastState> IceCastsS = new();
        static readonly List<IceLineState> IceLinesS = new();
        static readonly List<IceSpikeState> IceSpikesS = new();
        static double _nextIceFireCheck;

        static void ResetGround()
        {
            SwarmsS.Clear();
            Shots.Clear();
            Chased.Clear();
            IceCastsS.Clear();
            IceLinesS.Clear();
            IceSpikesS.Clear();
            _nextIceFireCheck = 0;
        }

        static bool CastGround(PlayerController caster, CardId card, float yaw, VoxelWorld world, double now)
        {
            switch (card)
            {
                case CardId.RockSwarm:
                    return CastSwarm(caster, world, now);
                case CardId.IceSpike:
                {
                    Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                    if (SpellPlans.IceGround(world, caster.FeetPosition, dir).Count == 0) return false;
                    IceCastsS.RemoveAll(c => c.caster == caster);
                    IceCastsS.Add(new IceCastState { caster = caster, dir = dir, at = now + SpellPlans.IceCrouch });
                    return true;
                }
            }
            return false;
        }

        /// <summary>Knocked down: the swarm still being torn out drops; a crouch that hadn't finished raises no spikes.</summary>
        static void FizzleGround(PlayerController pc, double now)
        {
            IceCastsS.RemoveAll(c => c.caster == pc);
            for (int i = SwarmsS.Count - 1; i >= 0; i--)
            {
                var s = SwarmsS[i];
                if (s.caster != pc || now >= s.start + SpellPlans.SwarmGather) continue;
                EndSwarm(s);
                SwarmsS.RemoveAt(i);
            }
        }

        static void TickGround(WorldNet net, double now)
        {
            TickSwarms(net, now);
            TickIceCasts(now);
            TickIceLines(now);
            TickIceSpikes(now);
        }

        static void TickIceCasts(double now)
        {
            var w = VoxelWorld.Current;
            for (int i = IceCastsS.Count - 1; i >= 0; i--)
            {
                var c = IceCastsS[i];
                if (now < c.at) continue;
                IceCastsS.RemoveAt(i);
                if (c.caster == null || !c.caster.IsAlive || w == null) continue;
                var slots = SpellPlans.IceGround(w, c.caster.FeetPosition, c.dir);
                if (slots.Count == 0) continue;
                var line = new IceLineState
                {
                    caster = c.caster, dir = c.dir, side = new Vector3(c.dir.z, 0f, -c.dir.x), start = now, slots = slots,
                    heights = new float[slots.Count], jitter = new float[slots.Count],
                };
                for (int k = 0; k < slots.Count; k++)
                {
                    line.heights[k] = Random.Range(SpellPlans.IceMinHeight, SpellPlans.IceMaxHeight);
                    line.jitter[k] = Random.Range(-SpellPlans.IceJitter, SpellPlans.IceJitter);
                }
                IceLinesS.Add(line);
            }
        }

        /// <summary>The spikes shoot up one at a time, start to end.</summary>
        static void TickIceLines(double now)
        {
            var w = VoxelWorld.Current;
            var shades = new List<byte>();
            for (int i = IceLinesS.Count - 1; i >= 0; i--)
            {
                var line = IceLinesS[i];
                while (line.next < line.slots.Count && now >= line.start + line.slots[line.next].index * SpellPlans.IceEvery)
                {
                    int k = line.next++;
                    if (w == null) continue;
                    var slot = line.slots[k];
                    float h = line.heights[k];
                    Vector3 b = slot.ground + line.side * line.jitter[k];
                    var cells = SpellPlans.IceSpikeCells(w, b, slot.top, h, line.dir, shades);
                    if (cells.Count == 0) continue;
                    int rise = Mathf.CeilToInt(h / VoxelWorld.Size) + 4; // from just under the ground
                    int id = WorldNet.ServerIceSpike(cells, shades, rise);
                    if (id == 0) continue;
                    Walls.Add(new PendingWall { id = id, mergeAt = now + SpellPlans.IceRise + 0.03 });
                    IceSpikesS.Add(new IceSpikeState { id = id, line = line, b = b, height = h, start = now, cells = cells });
                }
                if (line.next >= line.slots.Count) IceLinesS.RemoveAt(i);
            }
        }

        static bool IceRising(IceSpikeState s, double now) => now <= s.start + SpellPlans.IceRise + 0.05;

        static void TickIceSpikes(double now)
        {
            // rising spikes throw whoever they come up under
            foreach (var s in IceSpikesS)
            {
                if (IceRising(s, now)) HitWithSpike(s);
                else if (!s.melting) BumpSpike(s);
            }

            // fire near a standing spike: it crumbles
            if (now >= _nextIceFireCheck && IceSpikesS.Count > 0)
            {
                _nextIceFireCheck = now + 0.1;
                CheckIceFire(now);
            }
            for (int i = IceSpikesS.Count - 1; i >= 0; i--)
            {
                var s = IceSpikesS[i];
                if (!s.melting || now < s.nextMelt) continue;
                CrumbleStep(s);
                s.nextMelt = now + SpellPlans.IceCrumbleStep;
                if (++s.meltStep >= 3) IceSpikesS.RemoveAt(i);
            }
        }

        /// <summary>
        /// Is this body inside a spike's hitbox (<see cref="SpellPlans.IceHitWidth"/> times as wide as the spike)?
        /// <paramref name="axis"/>: the spike's middle at the body's height.
        /// </summary>
        static bool InSpike(IceSpikeState s, Combatant c, out Vector3 axis)
        {
            axis = default;
            Vector3 feet = c.FeetPosition;
            float h = feet.y - s.b.y;
            if (h < -0.6f || h > s.height) return false;
            float f = Mathf.Clamp01((h + 0.4f) / s.height);
            axis = s.b + s.line.dir * (SpellPlans.IceLean * s.height * f) + Vector3.up * Mathf.Max(0f, h + 0.4f);
            Vector2 off = new(c.transform.position.x - axis.x, c.transform.position.z - axis.z);
            return off.magnitude <= (SpellPlans.IceRadius(s.height) * (1f - f) + 0.45f) * SpellPlans.IceHitWidth;
        }

        /// <summary>
        /// An enemy walking (or thrown) into a standing spike: 10 and pushed back off it, not knocked down; once per
        /// cast, and never someone the spikes already threw.
        /// </summary>
        static void BumpSpike(IceSpikeState s)
        {
            var line = s.line;
            var w = VoxelWorld.Current;
            foreach (var c in Combatant.Registry)
            {
                if (c == null || c == line.caster || !c.IsAlive || line.hit.Contains(c) || line.bumped.Contains(c) || Folded(c) || Shielded(c)) continue;
                if (!InSpike(s, c, out var axis)) continue;
                // (still standing there: not broken off by something else)
                if (w != null && !w.IsSolid(w.CellOf(axis))) continue;
                line.bumped.Add(c);
                Vector3 away = c.transform.position - axis;
                away.y = 0f;
                away = away.sqrMagnitude > 0.01f ? away.normalized : -line.dir;
                Hit(c, line.caster, SpellPlans.IceBumpDamage, c.transform.position, away * SpellPlans.IceBumpKnock + Vector3.up * SpellPlans.IceBumpUp, 0f);
            }
        }

        static void HitWithSpike(IceSpikeState s)
        {
            var line = s.line;
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || c == line.caster || !c.IsAlive || line.hit.Contains(c) || Folded(c) || Shielded(c)) continue;
                Vector3 feet = c.FeetPosition;
                if (!InSpike(s, c, out _)) continue;
                line.hit.Add(c);
                // it's water: whoever was burning isn't any more
                if (c.BurnStacks.Value > 0)
                {
                    c.ServerExtinguish();
                    WorldNet.ServerQuench(c.transform.position, 0.7f);
                }
                Hit(c, line.caster, SpellPlans.IceDamage, feet + Vector3.up * 0.5f,
                    Vector3.up * SpellPlans.IceKnockUp + line.dir * SpellPlans.IceKnockOut, SpellPlans.IceRagdoll);
            }
        }

        static readonly List<Vector3> FirePoints = new();

        /// <summary>Anything burning near a standing spike crumbles it.</summary>
        static void CheckIceFire(double now)
        {
            var net = WorldNet.Instance;
            FirePoints.Clear();
            foreach (var c in Combatant.Registry)
                if (c != null && c.IsAlive && c.BurnStacks.Value > 0) { FirePoints.Add(c.transform.position); FirePoints.Add(c.FeetPosition + Vector3.up * 0.2f); }
            if (net != null)
            {
                foreach (var f in FireballsS)
                    if (!f.necro && net.Fireballs.TryGetValue(f.id, out var fx)) FirePoints.Add(WorldNet.FireballPosition(fx, now));
                foreach (int id in FlamingRocks)
                    if (net.Rocks.TryGetValue(id, out var rx)) FirePoints.Add(WorldNet.RockPosition(rx, now));
            }
            foreach (var f in ForcesS)
                if (!f.water) FirePoints.Add(f.pos);

            foreach (var s in IceSpikesS)
            {
                if (s.melting || IceRising(s, now)) continue;
                bool fire = false;
                foreach (var p in FirePoints)
                    if (SpellPlans.IceDistance(s.b, s.line.dir, s.height, p) <= SpellPlans.IceMeltReach) { fire = true; break; }
                if (!fire)
                    foreach (var h in HellfiresS)
                    {
                        if (h.riseAt < 0) continue;
                        float lit = Mathf.Clamp01((float)(now - h.riseAt) / SpellPlans.HellfireRise);
                        if (InWall(h, s.b, lit, SpellPlans.IceRadius(s.height) + SpellPlans.IceMeltReach * 0.5f)) { fire = true; break; }
                    }
                if (fire) StartMelt(s, now);
            }
        }

        /// <summary>A blast of fire at <paramref name="point"/>: every standing spike within <paramref name="reach"/> crumbles.</summary>
        static void MeltIceNear(Vector3 point, float reach)
        {
            double now = WorldNet.Now;
            foreach (var s in IceSpikesS)
                if (!s.melting && SpellPlans.IceDistance(s.b, s.line.dir, s.height, point) <= reach) StartMelt(s, now);
        }

        static void StartMelt(IceSpikeState s, double now)
        {
            s.melting = true;
            s.nextMelt = System.Math.Max(now, s.start + SpellPlans.IceRise + 0.05); // (set into the world first)
            // top first
            s.cells.Sort((a, b) => b.y.CompareTo(a.y));
        }

        /// <summary>A third of the spike (top down) crumbles, singed, in a hiss of steam.</summary>
        static void CrumbleStep(IceSpikeState s)
        {
            int n = s.cells.Count, from = n * s.meltStep / 3, to = n * (s.meltStep + 1) / 3;
            if (to <= from) return;
            var slice = s.cells.GetRange(from, to - from);
            var w = VoxelWorld.Current;
            Vector3 steam = w != null ? w.CellCenter(slice[slice.Count / 2]) : s.b;
            WorldNet.ServerIceCrumble(slice, steam);
        }
    }
}
