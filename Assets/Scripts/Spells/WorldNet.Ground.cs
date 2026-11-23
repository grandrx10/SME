using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The op stream's part for Rock Swarm (four voxel rocks torn out of the ground, circling their caster) and
    /// Ice Spike (spikes of ice shooting up out of the ground, which fire crumbles). Server decides, every peer applies.
    /// </summary>
    public partial class WorldNet
    {
        // ------------------------------------------------------------------ Rock Swarm state (every peer)

        public class SwarmRock
        {
            public VoxelBody body;
            public Vector3 home;                       // where it was torn out of (it rises from there)
            public bool alive = true;
            public bool flying; public Vector3 from, to; public double t0, t1; // left the ring, flying at a projectile
            public double bumpAt = -10; public Vector3 bumpDir; public float bumpSize, bumpTime = 1f;
        }

        public class SwarmFx
        {
            public int id; public ulong caster; public double start; public float phase;
            public SwarmRock[] rocks;
            /// <summary>The ring's centre as last seen (it stays there if the caster is gone).</summary>
            public Vector3 center;
        }

        public readonly Dictionary<int, SwarmFx> Swarms = new();

        static readonly Color GroundPuff = new(0.55f, 0.45f, 0.35f);
        static readonly Color IcePuff = new(0.8f, 0.94f, 1f);

        /// <summary>The middle of the ring: just above the caster's hips.</summary>
        public static Vector3 SwarmCenter(SwarmFx s)
        {
            var c = FindCombatant(s.caster);
            if (c != null) s.center = c.transform.position + Vector3.up * SpellPlans.SwarmLift;
            return s.center;
        }

        /// <summary>Where rock <paramref name="k"/> is: rising from its hole, circling, knocked aside, or flying at a projectile.</summary>
        public static Vector3 SwarmPosition(SwarmFx s, int k, double now)
        {
            var r = s.rocks[k];
            if (r.flying)
            {
                float u = r.t1 > r.t0 ? Mathf.Clamp01((float)((now - r.t0) / (r.t1 - r.t0))) : 1f;
                return Vector3.Lerp(r.from, r.to, u);
            }
            Vector3 ring = SwarmCenter(s) + SpellPlans.SwarmOrbit(s.phase, k, s.start, now);
            float g = Mathf.Clamp01((float)(now - s.start) / SpellPlans.SwarmGather);
            g = g * g * (3f - 2f * g);
            Vector3 p = Vector3.Lerp(r.home, ring, g);
            float b = (float)((now - r.bumpAt) / r.bumpTime);
            if (b >= 0f && b <= 1f) p += r.bumpDir * (r.bumpSize * Mathf.Sin(Mathf.PI * b));
            return p;
        }

        /// <summary>Is rock <paramref name="k"/> in the ring (risen, not flying off, not gone)?</summary>
        public static bool SwarmCircling(SwarmFx s, int k, double now) =>
            s.rocks[k].alive && !s.rocks[k].flying && now >= s.start + SpellPlans.SwarmGather;

        void AnimateSwarm(SwarmFx s, double now)
        {
            float t = (float)(now - s.start);
            for (int k = 0; k < s.rocks.Length; k++)
            {
                var r = s.rocks[k];
                if (!r.alive || r.body == null) continue;
                Vector3 p = SwarmPosition(s, k, now);
                // a little tremble while it's torn out, then tumbling as it goes round
                if (t < SpellPlans.SwarmGather) p += Random.insideUnitSphere * 0.03f;
                r.body.SetPose(p, Quaternion.Euler(t * 210f + k * 40f, t * 140f, t * 60f));
            }
        }

        // ------------------------------------------------------------------ server API

        /// <summary>Rock Swarm: tear a rock out of the ground round each of <paramref name="centers"/>; they rise into a ring round the caster.</summary>
        public static int ServerSwarm(ulong caster, float phase, List<Vector3Int> centers)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.Swarm, 48 + centers.Count * 6);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(Now); w.WriteValueSafe(phase);
            WriteCells(ref w, centers);
            w.WriteValueSafe((byte)SpellPlans.SwarmDigR4);
            i.Broadcast(Finish(ref w));
            return i.Swarms.ContainsKey(id) ? id : 0;
        }

        /// <summary>
        /// A rock is knocked out along <paramref name="dir"/> by <paramref name="size"/> and back over <paramref name="time"/>
        /// (bouncing off a surface, or ducking out of its caster's own projectile); <paramref name="r4"/> &gt; 0 also
        /// knocks a little off the surface round <paramref name="cell"/>.
        /// </summary>
        public static void ServerSwarmBump(int id, int k, Vector3 dir, float size, float time, Vector3Int cell, int r4)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.SwarmBump, 64);
            w.WriteValueSafe(id); w.WriteValueSafe((byte)k); w.WriteValueSafe(Now);
            w.WriteValueSafe(dir); w.WriteValueSafe(size); w.WriteValueSafe(time);
            WriteCell(ref w, cell);
            w.WriteValueSafe((byte)Mathf.Clamp(r4, 0, 255));
            i.Broadcast(Finish(ref w));
        }

        /// <summary>A rock leaves the ring and flies from <paramref name="from"/> to <paramref name="to"/>, arriving at <paramref name="t1"/>.</summary>
        public static void ServerSwarmFly(int id, int k, Vector3 from, Vector3 to, double t0, double t1)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.SwarmFly, 64);
            w.WriteValueSafe(id); w.WriteValueSafe((byte)k);
            w.WriteValueSafe(from); w.WriteValueSafe(to); w.WriteValueSafe(t0); w.WriteValueSafe(t1);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>A rock breaks at <paramref name="point"/>: into tiny voxels (<paramref name="tiny"/>), or crumbling in lumps.</summary>
        public static void ServerSwarmBreak(int id, int k, Vector3 point, bool tiny)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.SwarmBreak, 32);
            w.WriteValueSafe(id); w.WriteValueSafe((byte)k); w.WriteValueSafe(point); w.WriteValueSafe(tiny);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>
        /// One Ice Spike spike: these air cells become ice (of these shades), shooting up from <paramref name="rise"/>
        /// cells below in <see cref="SpellPlans.IceRise"/>. Set into the world with <see cref="ServerMerge"/>.
        /// </summary>
        public static int ServerIceSpike(List<Vector3Int> cells, List<byte> shades, int rise)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.IceSpike, 32 + cells.Count * 7);
            w.WriteValueSafe(id); w.WriteValueSafe(Now); w.WriteValueSafe((byte)Mathf.Clamp(rise, 1, 255));
            WriteCells(ref w, cells);
            w.WriteValueSafe(shades.Count);
            foreach (var s in shades) w.WriteValueSafe(s);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>Fire got to a spike: whichever of these cells are still ice crumble away, singed, in a hiss of steam at <paramref name="steam"/>.</summary>
        public static void ServerIceCrumble(List<Vector3Int> cells, Vector3 steam)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return;
            var w = NewOp(Op.IceCrumble, 32 + cells.Count * 6);
            WriteCells(ref w, cells);
            w.WriteValueSafe(steam);
            i.Broadcast(Finish(ref w));
        }

        // ------------------------------------------------------------------ apply (every peer)

        /// <summary>Applies a Rock Swarm / Ice Spike op. False: not one of these (the main switch handles it).</summary>
        bool ApplyGround(Op op, ref FastBufferReader r, VoxelWorld world, List<VoxelWorld.RemovedCell> removed, List<List<VoxelWorld.RemovedCell>> islands)
        {
            switch (op)
            {
                case Op.Swarm:
                {
                    var fx = new SwarmFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.start); r.ReadValueSafe(out fx.phase);
                    var centers = ReadCells(ref r);
                    r.ReadValueSafe(out byte dig);
                    fx.rocks = new SwarmRock[centers.Count];
                    var counts = new Dictionary<ushort, int>();
                    Vector3 mid = Vector3.zero;
                    for (int k = 0; k < centers.Count; k++)
                    {
                        var rock = fx.rocks[k] = new SwarmRock { home = world.CellCenter(centers[k]) };
                        mid += rock.home;
                        var torn = new List<VoxelWorld.RemovedCell>();
                        world.RemoveSphere(centers[k], dig, torn, islands);
                        if (torn.Count == 0) { rock.alive = false; continue; }
                        // a ball of whatever it was torn out of most
                        counts.Clear();
                        ushort best = torn[0].value;
                        int bestN = 0;
                        foreach (var t in torn)
                        {
                            counts.TryGetValue(t.value, out int n);
                            counts[t.value] = ++n;
                            if (n > bestN) { bestN = n; best = t.value; }
                        }
                        rock.body = VoxelBody.Create(world, fx.id * 10 + k, SpellPlans.SwarmBall(centers[k], best), rock.home, false);
                        SpellFx.Puff(rock.home + Vector3.up * 0.3f, GroundPuff, 0.7f);
                    }
                    fx.center = centers.Count > 0 ? mid / centers.Count + Vector3.up * 1f : Vector3.zero;
                    Swarms[fx.id] = fx;
                    AnimateSwarm(fx, Now);
                    return true;
                }
                case Op.SwarmBump:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out byte k); r.ReadValueSafe(out double t);
                    r.ReadValueSafe(out Vector3 dir); r.ReadValueSafe(out float size); r.ReadValueSafe(out float time);
                    var c = ReadCell(ref r);
                    r.ReadValueSafe(out byte r4);
                    if (Swarms.TryGetValue(id, out var fx) && k < fx.rocks.Length)
                    {
                        var rock = fx.rocks[k];
                        rock.bumpAt = t; rock.bumpDir = dir; rock.bumpSize = size; rock.bumpTime = Mathf.Max(0.05f, time);
                    }
                    if (r4 > 0)
                    {
                        world.RemoveSphere(c, r4, removed, islands);
                        Debris.Spawn(world, removed, world.CellCenter(c), 3f, dir * 2f, 1);
                        if (removed.Count > 0) SpellFx.Puff(world.CellCenter(c), GroundPuff, 0.5f);
                    }
                    return true;
                }
                case Op.SwarmFly:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out byte k);
                    r.ReadValueSafe(out Vector3 from); r.ReadValueSafe(out Vector3 to); r.ReadValueSafe(out double t0); r.ReadValueSafe(out double t1);
                    if (Swarms.TryGetValue(id, out var fx) && k < fx.rocks.Length)
                    {
                        var rock = fx.rocks[k];
                        rock.flying = true; rock.from = from; rock.to = to; rock.t0 = t0; rock.t1 = t1;
                    }
                    return true;
                }
                case Op.SwarmBreak:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out byte k); r.ReadValueSafe(out Vector3 point); r.ReadValueSafe(out bool tiny);
                    if (!Swarms.TryGetValue(id, out var fx) || k >= fx.rocks.Length) return true;
                    var rock = fx.rocks[k];
                    if (rock.alive)
                    {
                        rock.alive = false;
                        if (rock.body)
                        {
                            // dismantled into single voxels (it stopped a projectile, or met an enemy rock), or crumbling in lumps
                            rock.body.SetPose(point, rock.body.transform.rotation);
                            Debris.Spawn(world, rock.body.Cells, point, tiny ? 5f : 2.5f, Vector3.zero, tiny ? 1 : 2, rock.body.MeshRoot);
                            Destroy(rock.body.gameObject);
                            rock.body = null;
                        }
                        SpellFx.Puff(point, GroundPuff, tiny ? 0.8f : 1f);
                        if (tiny && !Debris.Suppressed) CameraRig.ShakeFrom(point, 0.15f, 0.15f);
                    }
                    bool any = false;
                    foreach (var o in fx.rocks) any |= o.alive;
                    if (!any) Swarms.Remove(id);
                    return true;
                }
                case Op.IceSpike:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out double start); r.ReadValueSafe(out byte rise);
                    var cells = ReadCells(ref r);
                    r.ReadValueSafe(out int n);
                    var values = new List<ushort>(cells.Count);
                    for (int s = 0; s < n; s++)
                    {
                        r.ReadValueSafe(out byte shade);
                        if (s < cells.Count) values.Add(world.IceValue(shade));
                    }
                    while (values.Count < cells.Count) values.Add(world.IceValue(0));
                    var bodyCells = Zip(cells, values);
                    Vector3 pivot = bodyCells.Count > 0 ? world.CellCenter(bodyCells[0].cell) : world.Origin;
                    var fx = new WallFx { id = id, start = start, rise = rise, cells = cells, values = values, time = SpellPlans.IceRise };
                    if (bodyCells.Count > 0) fx.body = VoxelBody.Create(world, id, bodyCells, pivot);
                    Walls[id] = fx;
                    AnimateWall(fx);
                    if (bodyCells.Count > 0 && !Debris.Suppressed)
                    {
                        // it bursts up out of the ground
                        Vector3 foot = world.CellCenter(bodyCells[0].cell);
                        SpellFx.Puff(foot + Vector3.up * 0.2f, IcePuff, 0.7f);
                        CameraRig.ShakeFrom(foot, 0.12f, 0.12f);
                    }
                    return true;
                }
                case Op.IceCrumble:
                {
                    var cells = ReadCells(ref r);
                    r.ReadValueSafe(out Vector3 steam);
                    var melt = new List<Vector3Int>();
                    foreach (var c in cells) if (world.IsIce(world.Get(c))) melt.Add(c);
                    var torn = new List<VoxelWorld.RemovedCell>();
                    world.RemoveCells(melt, torn, true, islands);
                    // singed as it crumbles: every other bit blackened (palette order is the same everywhere: this runs on every peer)
                    for (int s = 0; s < torn.Count; s += 2)
                    {
                        var t = torn[s];
                        t.value = world.Scorched(t.value);
                        torn[s] = t;
                    }
                    Debris.Spawn(world, torn, steam, 1.5f, Vector3.down, 1);
                    if (torn.Count > 0) WaterVfx.Steam(steam, 0.8f);
                    return true;
                }
            }
            return false;
        }
    }
}
