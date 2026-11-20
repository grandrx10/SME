using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Colour + flags of one voxel material. Index 0 in a palette is always air.</summary>
    [Serializable]
    public struct VoxelMaterial
    {
        public Color32 color;
        /// <summary>Can't be broken, and anchors anything attached to it.</summary>
        public bool indestructible;
    }

    /// <summary>
    /// The destructible world of the current arena: a sparse grid of 16³ chunks of 0.25 m voxels.
    /// Cell (x,y,z) occupies Origin + (x,y,z)*Size .. +Size. Voxel value 0 = air, otherwise a palette index.
    /// Every peer holds the same grid; it only changes through <see cref="WorldNet"/> ops, applied in the same
    /// order everywhere, and every edit is integer-only so the result is identical on all machines.
    /// </summary>
    public class VoxelWorld : MonoBehaviour
    {
        public const float Size = 0.25f;
        public const int ChunkBits = 4;
        public const int ChunkSize = 1 << ChunkBits; // 16
        public const int ChunkMask = ChunkSize - 1;
        /// <summary>Flood fills give up (and call the piece anchored) after this many cells.</summary>
        const int IslandSearchLimit = 30000;

        public static VoxelWorld Current { get; private set; }

        public Vector3 Origin { get; private set; }
        /// <summary>Cells at or below this height are anchored to the world (the arena's floor).</summary>
        public int AnchorY { get; private set; }
        public readonly List<VoxelMaterial> Palette = new();
        public readonly Dictionary<Vector3Int, VoxelChunk> Chunks = new();
        public Material RenderMaterial { get; private set; }

        readonly HashSet<VoxelChunk> _dirty = new();

        // ------------------------------------------------------------------ setup

        /// <param name="live">True in play mode (becomes <see cref="Current"/>); false for an editor preview.</param>
        public static VoxelWorld Create(Transform parent, Vector3 origin, VoxelArenaData data, bool live)
        {
            var go = new GameObject("VoxelWorld");
            go.transform.SetParent(parent, false);
            go.transform.position = origin;
            go.transform.rotation = Quaternion.identity;
            var w = go.AddComponent<VoxelWorld>();
            w.Origin = origin;
            w.RenderMaterial = VoxelRender.Material;
            w.Palette.Clear();
            w.Palette.Add(default); // air
            for (int i = 1; i < data.palette.Count; i++) w.Palette.Add(data.palette[i]);
            int minY = int.MaxValue;
            foreach (var kv in data.chunks)
            {
                var c = w.GetOrCreateChunk(kv.Key);
                Array.Copy(kv.Value, c.Cells, c.Cells.Length);
                c.RecountSolid();
                if (c.Solid > 0) minY = Mathf.Min(minY, kv.Key.y * ChunkSize + c.LowestSolidY());
            }
            w.AnchorY = minY == int.MaxValue ? 0 : minY;
            w.ComputeAnchors();
            if (live) Current = w;
            w.RebuildAll();
            return w;
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
        }

        /// <summary>
        /// Every separate piece of the arena as built (floating platforms included) is held up by its own bottom
        /// layer: those cells are anchors. Break a piece so part of it no longer reaches its anchors and that part falls.
        /// </summary>
        void ComputeAnchors()
        {
            _anchors.Clear();
            var visited = new HashSet<Vector3Int>();
            var queue = new Queue<Vector3Int>();
            var comp = new List<Vector3Int>();
            foreach (var chunk in Chunks.Values)
            {
                var o = chunk.Key * ChunkSize;
                for (int i = 0; i < chunk.Cells.Length; i++)
                {
                    if (chunk.Cells[i] == 0) continue;
                    var start = o + new Vector3Int(i & ChunkMask, i >> (ChunkBits * 2), (i >> ChunkBits) & ChunkMask);
                    if (!visited.Add(start)) continue;
                    comp.Clear();
                    queue.Enqueue(start);
                    int minY = start.y;
                    while (queue.Count > 0)
                    {
                        var p = queue.Dequeue();
                        comp.Add(p);
                        if (p.y < minY) minY = p.y;
                        foreach (var d in Dirs)
                        {
                            var n = p + d;
                            if (IsSolid(n) && visited.Add(n)) queue.Enqueue(n);
                        }
                    }
                    foreach (var p in comp) if (p.y == minY) _anchors.Add(p);
                }
            }
        }

        readonly HashSet<Vector3Int> _anchors = new();
        bool IsAnchor(Vector3Int p) => p.y <= AnchorY || _anchors.Contains(p);

        // ------------------------------------------------------------------ coordinates

        public Vector3Int CellOf(Vector3 world)
        {
            Vector3 p = (world - Origin) / Size;
            return new Vector3Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));
        }

        public Vector3 CellCenter(Vector3Int c) => Origin + new Vector3(c.x + 0.5f, c.y + 0.5f, c.z + 0.5f) * Size;
        public Vector3 CellMin(Vector3Int c) => Origin + new Vector3(c.x, c.y, c.z) * Size;

        public static Vector3Int ChunkOf(int x, int y, int z) => new(x >> ChunkBits, y >> ChunkBits, z >> ChunkBits);

        // ------------------------------------------------------------------ access

        public ushort Get(int x, int y, int z)
        {
            if (!Chunks.TryGetValue(ChunkOf(x, y, z), out var c)) return 0;
            return c.Cells[VoxelChunk.Index(x & ChunkMask, y & ChunkMask, z & ChunkMask)];
        }

        public ushort Get(Vector3Int p) => Get(p.x, p.y, p.z);
        public bool IsSolid(int x, int y, int z) => Get(x, y, z) != 0;
        public bool IsSolid(Vector3Int p) => Get(p.x, p.y, p.z) != 0;

        public bool IsIndestructible(ushort v) => v != 0 && v < Palette.Count && Palette[v].indestructible;

        public Color32 ColorOf(ushort v) => v < Palette.Count ? Palette[v].color : new Color32(255, 0, 255, 255);

        /// <summary>Sets a cell and marks the chunk (and any neighbour sharing the face) for a rebuild.</summary>
        public void Set(int x, int y, int z, ushort v)
        {
            var key = ChunkOf(x, y, z);
            if (!Chunks.TryGetValue(key, out var c))
            {
                if (v == 0) return;
                c = GetOrCreateChunk(key);
            }
            int lx = x & ChunkMask, ly = y & ChunkMask, lz = z & ChunkMask;
            int i = VoxelChunk.Index(lx, ly, lz);
            ushort old = c.Cells[i];
            if (old == v) return;
            c.Cells[i] = v;
            if (v == 0 && _summoned.Count > 0) _summoned.Remove(new Vector3Int(x, y, z));
            c.Solid += (v != 0 ? 1 : 0) - (old != 0 ? 1 : 0);
            _dirty.Add(c);
            if (lx == 0) MarkDirty(key + Vector3Int.left);
            else if (lx == ChunkMask) MarkDirty(key + Vector3Int.right);
            if (ly == 0) MarkDirty(key + Vector3Int.down);
            else if (ly == ChunkMask) MarkDirty(key + Vector3Int.up);
            if (lz == 0) MarkDirty(key + new Vector3Int(0, 0, -1));
            else if (lz == ChunkMask) MarkDirty(key + new Vector3Int(0, 0, 1));
        }

        public void Set(Vector3Int p, ushort v) => Set(p.x, p.y, p.z, v);

        void MarkDirty(Vector3Int key)
        {
            if (Chunks.TryGetValue(key, out var c)) _dirty.Add(c);
        }

        VoxelChunk GetOrCreateChunk(Vector3Int key)
        {
            if (Chunks.TryGetValue(key, out var c)) return c;
            c = VoxelChunk.Create(this, key);
            Chunks[key] = c;
            return c;
        }

        /// <summary>Highest solid cell in column (x,z) between yMin and yMax, or int.MinValue.</summary>
        public int TopSolid(int x, int z, int yMin, int yMax)
        {
            for (int y = yMax; y >= yMin; y--)
                if (IsSolid(x, y, z)) return y;
            return int.MinValue;
        }

        // ------------------------------------------------------------------ rebuild

        void LateUpdate() => FlushRebuilds();

        /// <summary>Re-mesh every chunk that changed (also called before physics queries that must see edits).</summary>
        public void FlushRebuilds()
        {
            if (_dirty.Count == 0) return;
            foreach (var c in _dirty) if (c != null) c.Rebuild();
            _dirty.Clear();
        }

        void RebuildAll()
        {
            foreach (var c in Chunks.Values) c.Rebuild();
            _dirty.Clear();
        }

        // ------------------------------------------------------------------ edits

        public struct RemovedCell
        {
            public Vector3Int cell;
            public ushort value;
        }

        /// <summary>
        /// Removes every destructible cell within radius r4/4 cells of the centre cell (integer maths only),
        /// then drops anything left floating. Removed cells go to <paramref name="removed"/>, fallen pieces to <paramref name="islands"/>.
        /// </summary>
        public void RemoveSphere(Vector3Int center, int r4, List<RemovedCell> removed, List<List<RemovedCell>> islands)
        {
            int r = (r4 + 3) / 4;
            int lim = r4 * r4;
            var boundary = new List<Vector3Int>();
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            for (int dz = -r; dz <= r; dz++)
            {
                if ((dx * dx + dy * dy + dz * dz) * 16 > lim) continue;
                var p = new Vector3Int(center.x + dx, center.y + dy, center.z + dz);
                ushort v = Get(p);
                if (v == 0 || IsIndestructible(v)) continue;
                removed?.Add(new RemovedCell { cell = p, value = v });
                Set(p, 0);
                boundary.Add(p);
            }
            DropIslands(boundary, removed, islands);
        }

        /// <summary>Removes the given cells (destructible only). Returns what was actually removed.</summary>
        public void RemoveCells(IReadOnlyList<Vector3Int> cells, List<RemovedCell> removed, bool dropIslands, List<List<RemovedCell>> islands)
        {
            var boundary = dropIslands ? new List<Vector3Int>() : null;
            foreach (var p in cells)
            {
                ushort v = Get(p);
                if (v == 0 || IsIndestructible(v)) continue;
                removed?.Add(new RemovedCell { cell = p, value = v });
                Set(p, 0);
                boundary?.Add(p);
            }
            if (dropIslands) DropIslands(boundary, removed, islands);
        }

        /// <summary>Fills cells that are currently air (never overwrites existing voxels). They count as summoned ground.</summary>
        public void FillCells(IReadOnlyList<Vector3Int> cells, IReadOnlyList<ushort> values)
        {
            for (int i = 0; i < cells.Count; i++)
                if (Get(cells[i]) == 0 && values[i] != 0)
                {
                    Set(cells[i], values[i]);
                    _summoned.Add(cells[i]);
                }
        }

        // ------------------------------------------------------------------ fire

        readonly Dictionary<ushort, ushort> _soot = new();
        readonly HashSet<ushort> _isSoot = new();

        /// <summary>
        /// The scorched version of a material: nearly black, a hint of its old colour. Palette entries are added the
        /// first time each is needed — in op order, so every peer's palette stays the same.
        /// </summary>
        ushort SootOf(ushort v)
        {
            if (v == 0 || _isSoot.Contains(v)) return v;
            if (_soot.TryGetValue(v, out var s)) return s;
            var m = Palette[v];
            Color c = m.color;
            float lum = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
            Color soot = Color.Lerp(new Color(lum, lum, lum), c, 0.15f) * 0.16f + new Color(0.03f, 0.025f, 0.02f);
            soot.a = 1f;
            Palette.Add(new VoxelMaterial { color = soot, indestructible = m.indestructible });
            s = (ushort)(Palette.Count - 1);
            _soot[v] = s;
            _isSoot.Add(s);
            return s;
        }

        /// <summary>Scorches the exposed surface (cells touching air) within radius r4/4 cells: turned to soot.</summary>
        /// <remarks>With <paramref name="all"/>, every cell in the ball is scorched, not just the surface (rubble about to be blown out).</remarks>
        public void Singe(Vector3Int center, int r4, bool all = false)
        {
            int r = (r4 + 3) / 4, lim = r4 * r4;
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            for (int dz = -r; dz <= r; dz++)
            {
                if ((dx * dx + dy * dy + dz * dz) * 16 > lim) continue;
                var p = new Vector3Int(center.x + dx, center.y + dy, center.z + dz);
                ushort v = Get(p);
                if (v == 0 || _isSoot.Contains(v)) continue;
                bool exposed = all;
                if (!exposed) foreach (var d in Dirs) if (!IsSolid(p + d)) { exposed = true; break; }
                if (exposed) Set(p, SootOf(v));
            }
        }

        /// <summary>
        /// Arcane Judgment: in every column within <paramref name="radius"/> cells of (cx, cz), removes the topmost
        /// destructible voxel between yMin and yMax — one layer off the top of a cylinder.
        /// </summary>
        public void EraseTopLayer(int cx, int cz, int radius, int yMin, int yMax, List<RemovedCell> removed)
        {
            int lim = radius * radius;
            for (int x = cx - radius; x <= cx + radius; x++)
            for (int z = cz - radius; z <= cz + radius; z++)
            {
                if ((x - cx) * (x - cx) + (z - cz) * (z - cz) > lim) continue;
                for (int y = yMax; y >= yMin; y--)
                {
                    ushort v = Get(x, y, z);
                    if (v == 0) continue;
                    if (!IsIndestructible(v))
                    {
                        removed?.Add(new RemovedCell { cell = new Vector3Int(x, y, z), value = v });
                        Set(x, y, z, 0);
                    }
                    break; // only the top one (an indestructible top shields what's under it)
                }
            }
        }

        // ------------------------------------------------------------------ summoned ground

        /// <summary>Cells put there by spells (walls, folds) — as opposed to the arena's own terrain.</summary>
        readonly HashSet<Vector3Int> _summoned = new();
        public bool IsSummoned(Vector3Int p) => _summoned.Contains(p);
        public int SummonedCount => _summoned.Count;

        /// <summary>Summoned cells whose centres are inside the box / sphere (sorted, so every peer agrees on the order).</summary>
        public List<Vector3Int> SummonedWhere(System.Func<Vector3, bool> inside)
        {
            var list = new List<Vector3Int>();
            foreach (var p in _summoned) if (inside(CellCenter(p))) list.Add(p);
            list.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y != b.y ? a.y.CompareTo(b.y) : a.z.CompareTo(b.z));
            return list;
        }

        /// <summary>The connected piece of summoned ground containing <paramref name="start"/> (at most <paramref name="limit"/> cells).</summary>
        public List<RemovedCell> SummonedPiece(Vector3Int start, int limit)
        {
            var result = new List<RemovedCell>();
            if (!_summoned.Contains(start)) return result;
            var seen = new HashSet<Vector3Int> { start };
            var queue = new Queue<Vector3Int>();
            queue.Enqueue(start);
            while (queue.Count > 0 && result.Count < limit)
            {
                var p = queue.Dequeue();
                result.Add(new RemovedCell { cell = p, value = Get(p) });
                foreach (var d in Dirs)
                {
                    var n = p + d;
                    if (_summoned.Contains(n) && seen.Add(n)) queue.Enqueue(n);
                }
            }
            return result;
        }

        static readonly Vector3Int[] Dirs =
        {
            Vector3Int.left, Vector3Int.right, Vector3Int.down, Vector3Int.up, new(0, 0, -1), new(0, 0, 1),
        };

        /// <summary>
        /// After cells were removed, any piece that is no longer connected (6-neighbour) to the anchored ground
        /// falls: it's removed from the grid and handed back as an island (the caller turns it into debris).
        /// Deterministic: the same grid and the same boundary list give the same result on every peer.
        /// </summary>
        void DropIslands(List<Vector3Int> boundary, List<RemovedCell> _, List<List<RemovedCell>> islands)
        {
            if (boundary == null || boundary.Count == 0) return;
            var settled = new HashSet<Vector3Int>(); // cells already proven anchored
            var queue = new Queue<Vector3Int>();
            var comp = new List<Vector3Int>();
            var seen = new HashSet<Vector3Int>();
            foreach (var b in boundary)
            foreach (var d in Dirs)
            {
                var start = b + d;
                if (!IsSolid(start) || settled.Contains(start)) continue;
                // flood this component
                comp.Clear();
                seen.Clear();
                queue.Clear();
                queue.Enqueue(start);
                seen.Add(start);
                bool anchored = false;
                while (queue.Count > 0)
                {
                    var p = queue.Dequeue();
                    comp.Add(p);
                    if (IsAnchor(p) || settled.Contains(p) || IsIndestructible(Get(p)) || seen.Count > IslandSearchLimit)
                    {
                        anchored = true;
                        break;
                    }
                    foreach (var dd in Dirs)
                    {
                        var n = p + dd;
                        if (seen.Contains(n) || !IsSolid(n)) continue;
                        seen.Add(n);
                        queue.Enqueue(n);
                    }
                }
                if (anchored)
                {
                    foreach (var s in seen) settled.Add(s);
                    continue;
                }
                // floating: it falls
                var island = new List<RemovedCell>(comp.Count);
                foreach (var p in comp) island.Add(new RemovedCell { cell = p, value = Get(p) });
                foreach (var p in comp) Set(p, 0);
                islands?.Add(island);
            }
        }

        /// <summary>Is this collider part of the voxel world (and not an indestructible-only spot)?</summary>
        public static bool IsVoxelCollider(Collider c) => c != null && c.GetComponent<VoxelChunk>() != null;
    }
}
