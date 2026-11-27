using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// An invisible floor that rises from the bottom of the map to its top, eating every voxel it passes (they drop away
    /// as rubble). From FIGHT (plus <see cref="startAfter"/>) it takes <see cref="riseSeconds"/> to cover the whole map.
    /// It touches nothing else: players, projectiles and everything else go straight through it.
    /// The server sends one tiny op per 0.25 m layer (WorldNet.ServerWallLayer), so every machine eats the same layers in
    /// the same order as every other voxel edit; the rubble is local, and thinned out.
    /// Put one on the arena root (Sudden Mage Encounter ▸ Hazards ▸ Add Rising Wall).
    /// </summary>
    [AddComponentMenu("Sudden Mage Encounter/Rising Wall")]
    public class RisingWall : MonoBehaviour
    {
        [Tooltip("Seconds after FIGHT that it starts rising.")]
        public float startAfter = 0f;
        [Tooltip("Seconds from starting to rise until it's over the whole map.")]
        public float riseSeconds = 180f;

        /// <summary>Most layers one frame sends (catching up after a hitch).</summary>
        const int LayersPerFrame = 4;
        /// <summary>At most this many clumps of rubble from one layer (each 1 m across at most).</summary>
        const int RubblePerLayer = 40;

        static readonly List<RisingWall> All = new();
        Arena _arena;
        int _done = int.MinValue; // the last layer (cell y) eaten; MinValue = not started

        void Awake() => _arena = GetComponentInParent<Arena>();
        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        public static void ServerTick()
        {
            foreach (var w in All) w.Tick();
        }

        void Tick()
        {
            var world = VoxelWorld.Current;
            if (world == null || _arena == null) return;
            float t = MapClock.Live(_arena) ? MapClock.Since(_arena, startAfter) : -1f;
            if (t < 0f) { _done = int.MinValue; return; } // not rising (or a practice chamber put back): start over next time
            if (!Area(world, out var lo, out var hi)) return;
            if (_done == int.MinValue) _done = lo.y - 1;
            // the layer it's reached: the bottom at 0, past the top at riseSeconds
            int target = lo.y - 1 + Mathf.FloorToInt((hi.y - lo.y + 1) * Mathf.Clamp01(t / Mathf.Max(1f, riseSeconds)));
            for (int n = 0; _done < target && n < LayersPerFrame; n++)
            {
                _done++;
                WorldNet.ServerWallLayer(_done, lo.x, hi.x, lo.z, hi.z);
            }
        }

        /// <summary>The cells this arena (or practice chamber) covers.</summary>
        bool Area(VoxelWorld world, out Vector3Int lo, out Vector3Int hi)
        {
            int slot = Practice.SlotAt(transform.position);
            if (!Practice.ChamberBounds(slot, out var b)) b = world.ArenaBounds;
            lo = world.CellOf(b.min);
            hi = world.CellOf(b.max);
            return b.size.y > 0f;
        }

        static readonly List<Vector3Int> Cells = new();
        static readonly List<VoxelWorld.RemovedCell> Removed = new();

        /// <summary>Every machine, in op order: eat layer <paramref name="y"/> inside x0..x1, z0..z1 (cells, inclusive); the voxels drop as rubble.</summary>
        public static void ApplyLayer(VoxelWorld world, int y, int x0, int x1, int z0, int z1)
        {
            Cells.Clear();
            Removed.Clear();
            int cy = y >> VoxelWorld.ChunkBits, ly = y & VoxelWorld.ChunkMask;
            foreach (var kv in world.Chunks)
            {
                var key = kv.Key;
                if (key.y != cy) continue;
                var chunk = kv.Value;
                if (chunk.Solid <= 0) continue;
                int bx = key.x << VoxelWorld.ChunkBits, bz = key.z << VoxelWorld.ChunkBits;
                if (bx + VoxelWorld.ChunkMask < x0 || bx > x1 || bz + VoxelWorld.ChunkMask < z0 || bz > z1) continue;
                for (int lz = 0; lz < VoxelWorld.ChunkSize; lz++)
                for (int lx = 0; lx < VoxelWorld.ChunkSize; lx++)
                {
                    int x = bx + lx, z = bz + lz;
                    if (x < x0 || x > x1 || z < z0 || z > z1) continue;
                    if (chunk.Cells[VoxelChunk.Index(lx, ly, lz)] != 0) Cells.Add(new Vector3Int(x, y, z));
                }
            }
            if (Cells.Count == 0) return;
            world.RemoveCells(Cells, Removed, false, null);
            if (Removed.Count == 0 || Debris.Suppressed || Application.isBatchMode) return;
            // the eaten voxels fall away, in a few big clumps
            Vector3 centre = world.CellCenter(new Vector3Int((x0 + x1) / 2, y, (z0 + z1) / 2));
            Debris.Spawn(world, new List<VoxelWorld.RemovedCell>(Removed), centre, 0.3f, Vector3.down * 1.5f, 4, null, RubblePerLayer);
        }
    }
}
