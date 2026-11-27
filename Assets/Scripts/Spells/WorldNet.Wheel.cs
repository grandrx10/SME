using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The op stream's part for Wheel of Fortune's results, Judgement's slam and the Water of Life's burst. The server decides; every peer applies the same ops in order.</summary>
    public partial class WorldNet
    {
        enum WheelOp : byte { Blast = 133, Surface = 134, Vaporize = 135, Slam = 136 }

        static FastBufferWriter NewOp(WheelOp op, int size) => NewOp((Op)(byte)op, size);

        // ------------------------------------------------------------------ server API

        /// <summary>
        /// A blast to show (it changes nothing). <paramref name="kind"/>: 0 blown up (a roll of 0); 1 landing out of the sky
        /// (a roll of 1); 2 someone bursting with too much Water of Life; 3 the golden punch (a roll of 6).
        /// </summary>
        public static void ServerWheelBlast(byte kind, Vector3 point, float radius)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(WheelOp.Blast, 24);
            w.WriteValueSafe(kind); w.WriteValueSafe(point); w.WriteValueSafe(radius);
            i.Broadcast(Finish(ref w));
        }

        public const byte SurfaceIce = 0, SurfaceSoot = 1;

        /// <summary>
        /// Between two distances (m) from <paramref name="center"/>: every surface frosts over (one voxel deep, whichever way
        /// it faces), or the top of the ground is blackened.
        /// </summary>
        public static void ServerSurface(Vector3Int center, float from, float to, byte what)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(WheelOp.Surface, 16);
            WriteCell(ref w, center);
            w.WriteValueSafe((byte)Mathf.Clamp(Mathf.RoundToInt(from / VoxelWorld.Size), 0, 255));
            w.WriteValueSafe((byte)Mathf.Clamp(Mathf.RoundToInt(to / VoxelWorld.Size), 0, 255));
            w.WriteValueSafe(what);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Every breakable cell between two distances (in cells) from <paramref name="center"/> is gone. <paramref name="drop"/>: whatever's left hanging falls.</summary>
        public static void ServerVaporizeShell(Vector3Int center, int from, int to, bool drop)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(WheelOp.Vaporize, 16);
            WriteCell(ref w, center);
            w.WriteValueSafe((byte)Mathf.Clamp(from, 0, 255)); w.WriteValueSafe((byte)Mathf.Clamp(to, 0, 255)); w.WriteValueSafe(drop);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Judgement comes down round <paramref name="center"/> (the ground cell at its middle): everything breakable in it goes.</summary>
        public static void ServerJudgementSlam(Vector3Int center)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(WheelOp.Slam, 12);
            WriteCell(ref w, center);
            i.Broadcast(Finish(ref w));
        }

        // ------------------------------------------------------------------ apply (every peer)

        bool ApplyWheel(Op opRaw, ref FastBufferReader r, VoxelWorld world, List<VoxelWorld.RemovedCell> removed, List<List<VoxelWorld.RemovedCell>> islands)
        {
            switch ((WheelOp)(byte)opRaw)
            {
                case WheelOp.Blast:
                {
                    r.ReadValueSafe(out byte kind); r.ReadValueSafe(out Vector3 point); r.ReadValueSafe(out float radius);
                    if (Debris.Suppressed) return true;
                    if (kind == 2) WheelVfx.Bloat(point);
                    else if (kind == 3) WheelVfx.Punch(point);
                    else WheelBlastView.Create(point, radius, kind == 0 ? SpellPlans.DoomSpread : SpellPlans.SkySpread);
                    return true;
                }
                case WheelOp.Surface:
                {
                    var c = ReadCell(ref r);
                    r.ReadValueSafe(out byte from); r.ReadValueSafe(out byte to); r.ReadValueSafe(out byte what);
                    int lo = from * from, hi = to * to;
                    for (int x = -to; x <= to; x++)
                    for (int z = -to; z <= to; z++)
                    {
                        int d2 = x * x + z * z;
                        if (d2 > hi) continue;
                        if (what == SurfaceSoot)
                        {
                            if (d2 >= lo) Blacken(world, c.x + x, c.z + z, c.y + SpellPlans.SootUp, c.y - SpellPlans.SootDown);
                            continue;
                        }
                        for (int y = -to; y <= to; y++)
                            if (d2 + y * y <= hi && d2 + y * y >= lo) Frost(world, new Vector3Int(c.x + x, c.y + y, c.z + z));
                    }
                    if (what == SurfaceIce && !Debris.Suppressed) WheelVfx.FrostRing(world.CellCenter(c), to * VoxelWorld.Size);
                    return true;
                }
                case WheelOp.Vaporize:
                {
                    var c = ReadCell(ref r);
                    r.ReadValueSafe(out byte from); r.ReadValueSafe(out byte to); r.ReadValueSafe(out bool drop);
                    int lo = from * from, hi = to * to;
                    var cells = new List<Vector3Int>();
                    for (int x = -to; x <= to; x++)
                    for (int y = -to; y <= to; y++)
                    for (int z = -to; z <= to; z++)
                    {
                        int d2 = x * x + y * y + z * z;
                        if (d2 > hi || from > 0 && d2 <= lo) continue;
                        var p = new Vector3Int(c.x + x, c.y + y, c.z + z);
                        ushort v = world.Get(p);
                        if (v != 0 && !world.IsIndestructible(v)) cells.Add(p);
                    }
                    world.RemoveCells(cells, removed, drop, islands);
                    if (removed.Count > 0 && !Debris.Suppressed) VoidRise.Spawn(world, removed, FireVfx.Orange, 24);
                    return true;
                }
                case WheelOp.Slam:
                {
                    var c = ReadCell(ref r);
                    int R = SpellPlans.TarotJudgmentCells, up = SpellPlans.TarotJudgmentCellsUp, down = SpellPlans.TarotJudgmentCellsDown;
                    var cells = new List<Vector3Int>();
                    for (int x = -R; x <= R; x++)
                    for (int z = -R; z <= R; z++)
                    {
                        if (x * x + z * z > R * R) continue;
                        for (int y = 1 - down; y <= up; y++) cells.Add(new Vector3Int(c.x + x, c.y + y, c.z + z));
                    }
                    world.RemoveCells(cells, removed, true, islands);
                    Vector3 mid = world.CellCenter(c);
                    // crushed straight down
                    Debris.Spawn(world, removed, mid + Vector3.up * SpellPlans.TarotJudgmentUp, 3f, Vector3.down * 9f, 2, null, 90);
                    if (!Debris.Suppressed) CameraRig.ShakeFrom(mid, 0.8f, 0.4f);
                    return true;
                }
            }
            return false;
        }

        static readonly Vector3Int[] FrostSides = { Vector3Int.up, Vector3Int.down, Vector3Int.left, Vector3Int.right, new(0, 0, 1), new(0, 0, -1) };

        /// <summary>A cell with air on any side of it turns to ice (nothing under the surface does).</summary>
        static void Frost(VoxelWorld world, Vector3Int p)
        {
            ushort v = world.Get(p);
            if (v == 0 || world.IsIndestructible(v) || world.IsIce(v)) return;
            foreach (var d in FrostSides)
            {
                if (world.IsSolid(p + d)) continue;
                // the same shade on every peer: from the cell's position
                world.Set(p, world.IceValue((byte)(((p.x * 73856093) ^ (p.y * 19349663) ^ (p.z * 83492791)) & 1)));
                return;
            }
        }

        /// <summary>The top cell of one column (the highest in reach, with air over it) is blackened.</summary>
        static void Blacken(VoxelWorld world, int x, int z, int top, int bottom)
        {
            for (int y = top; y >= bottom; y--)
            {
                ushort v = world.Get(x, y, z);
                if (v == 0) continue;
                if (y == top && world.Get(x, y + 1, z) != 0) return; // (under something taller than that: not the ground)
                if (!world.IsIndestructible(v)) world.Set(new Vector3Int(x, y, z), world.Scorched(v));
                return;
            }
        }
    }
}
