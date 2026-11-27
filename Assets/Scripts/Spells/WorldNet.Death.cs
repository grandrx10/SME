using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The op stream's part for Death: black bile on the ground, and a doomed body bursting. The vomiting is a breath (kind 2).</summary>
    public partial class WorldNet
    {
        enum DeathOp : byte { BileCoat = 137, BileDry = 138, Burst = 139 }

        static FastBufferWriter NewOp(DeathOp op, int size) => NewOp((Op)(byte)op, size);

        // ------------------------------------------------------------------ server API

        /// <summary>These cells are coated in bile: whichever are still breakable ground (not ice) turn black.</summary>
        public static void ServerBileCoat(List<Vector3Int> cells)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return;
            var w = NewOp(DeathOp.BileCoat, 8 + cells.Count * 6);
            WriteCells(ref w, cells);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>The bile dries off these cells: whichever are still bile go back to what they were.</summary>
        public static void ServerBileDry(List<Vector3Int> cells, List<ushort> was)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return;
            var w = NewOp(DeathOp.BileDry, 16 + cells.Count * 8);
            WriteCells(ref w, cells);
            WriteValues(ref w, was);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>A doomed body bursts here (look only: the ground, the bile and the marks are separate).</summary>
        public static void ServerDeathBurst(Vector3 point)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(DeathOp.Burst, 16);
            w.WriteValueSafe(point);
            i.Broadcast(Finish(ref w));
        }

        // ------------------------------------------------------------------ apply (every peer)

        /// <summary>Applies one of these ops. False: not one of them.</summary>
        bool ApplyDeath(Op opRaw, ref FastBufferReader r, VoxelWorld world)
        {
            switch ((DeathOp)(byte)opRaw)
            {
                case DeathOp.BileCoat:
                {
                    var cells = ReadCells(ref r);
                    foreach (var c in cells)
                    {
                        ushort v = world.Get(c);
                        if (v == 0 || world.IsIndestructible(v) || world.IsIce(v) || world.IsBile(v)) continue;
                        // the same shade on every peer: from the cell's position
                        world.Set(c, world.BileValue((byte)(((c.x * 73856093) ^ (c.y * 19349663) ^ (c.z * 83492791)) & 1)));
                    }
                    return true;
                }
                case DeathOp.BileDry:
                {
                    var cells = ReadCells(ref r);
                    var was = ReadValues(ref r);
                    for (int k = 0; k < cells.Count && k < was.Count; k++) world.Unbile(cells[k], was[k]);
                    return true;
                }
                case DeathOp.Burst:
                {
                    r.ReadValueSafe(out Vector3 point);
                    if (!Debris.Suppressed) DeathVfx.Burst(point);
                    return true;
                }
            }
            return false;
        }
    }
}
