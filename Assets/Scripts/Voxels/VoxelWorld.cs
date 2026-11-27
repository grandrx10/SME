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
    public partial class VoxelWorld : MonoBehaviour
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
        /// <summary>Materials for voxel meshes, one per <see cref="VoxelMesher"/> submesh (solid, glass).</summary>
        public Material[] RenderMaterials { get; private set; }

        readonly HashSet<VoxelChunk> _dirty = new();

        // ------------------------------------------------------------------ setup

        /// <param name="live">
        /// True in play mode (becomes <see cref="Current"/>, and builds itself over the next few seconds instead of
        /// freezing the game: see <see cref="Building"/>); false for an editor preview (built at once).
        /// </param>
        public static VoxelWorld Create(Transform parent, Vector3 origin, VoxelArenaData data, bool live)
        {
            var go = new GameObject("VoxelWorld");
            go.transform.SetParent(parent, false);
            go.transform.position = origin;
            go.transform.rotation = Quaternion.identity;
            var w = go.AddComponent<VoxelWorld>();
            w.Origin = origin;
            w.RenderMaterials = VoxelRender.Materials;
            w.Palette.Clear();
            w.Palette.Add(default); // air
            for (int i = 1; i < data.palette.Count; i++) w.Palette.Add(data.palette[i]);
            int minY = int.MaxValue;
            Vector3Int kMin = new(int.MaxValue, int.MaxValue, int.MaxValue), kMax = new(int.MinValue, int.MinValue, int.MinValue);
            foreach (var kv in data.chunks)
            {
                var c = w.GetOrCreateChunk(kv.Key);
                Array.Copy(kv.Value, c.Cells, c.Cells.Length);
                c.RecountSolid();
                if (c.Solid <= 0) continue;
                minY = Mathf.Min(minY, kv.Key.y * ChunkSize + c.LowestSolidY());
                kMin = Vector3Int.Min(kMin, kv.Key);
                kMax = Vector3Int.Max(kMax, kv.Key);
            }
            w.AnchorY = minY == int.MaxValue ? 0 : minY;
            if (minY == int.MaxValue) kMin = kMax = Vector3Int.zero;
            Vector3 lo = origin + (Vector3)(kMin * ChunkSize) * Size, hi = origin + (Vector3)((kMax + Vector3Int.one) * ChunkSize) * Size;
            w.ArenaBounds = new Bounds((lo + hi) * 0.5f, hi - lo);
            if (live)
            {
                Current = w;
                w.StartBuild(kMin, kMax);
                return w;
            }
            w._anchors.Clear();
            w.ComputeAnchors(w.Chunks.Values);
            w.RebuildAll();
            return w;
        }

        /// <summary>Where the arena's voxels were when it loaded (for the camera that circles it while it builds).</summary>
        public Bounds ArenaBounds { get; private set; }

        // ------------------------------------------------------------------ building over time

        /// <summary>Roughly how long the arena takes to rise (well inside the match's build phase).</summary>
        public const float BuildSeconds = 3.5f;
        /// <summary>Per-frame budgets (ms) for meshing chunks and for the anchor flood fill.</summary>
        const double MeshBudgetMs = 9.0, AnchorBudgetMs = 5.0;

        List<VoxelChunk> _buildQueue;
        int _buildNext;
        float _buildStart;
        IEnumerator<bool> _anchorJob;
        Vector3Int _jobMin, _jobMax;
        static readonly System.Diagnostics.Stopwatch BuildClock = new();

        /// <summary>Still meshing chunks or working out which cells hold the arena up.</summary>
        public bool Building => _buildQueue != null || _anchorJob != null;
        /// <summary>0..1: how much of the arena has been put up.</summary>
        public float BuildProgress => _buildQueue == null ? 1f : _buildQueue.Count == 0 ? 1f : (float)_buildNext / _buildQueue.Count;

        /// <summary>
        /// Instead of meshing every chunk (and cooking every collider) in one frame, the arena rises layer by layer
        /// over <see cref="BuildSeconds"/>, and the anchor flood fill runs a few milliseconds a frame alongside it.
        /// The grid itself is complete from the start, so world ops can be applied at any time; an op that edits the
        /// arena before its anchors are known finishes them first (<see cref="Set"/>), so every peer agrees.
        /// </summary>
        void StartBuild(Vector3Int kMin, Vector3Int kMax)
        {
            var center = (Vector3)(kMin + kMax) * 0.5f;
            _buildQueue = new List<VoxelChunk>(Chunks.Values);
            _buildQueue.Sort((a, b) =>
            {
                if (a.Key.y != b.Key.y) return a.Key.y.CompareTo(b.Key.y); // bottom up
                float da = new Vector2(a.Key.x - center.x, a.Key.z - center.z).sqrMagnitude;
                float db = new Vector2(b.Key.x - center.x, b.Key.z - center.z).sqrMagnitude;
                return da.CompareTo(db);                                    // each layer spreading out from the middle
            });
            _buildNext = 0;
            _buildStart = Time.unscaledTime;
            _anchors.Clear();
            _jobMin = kMin - Vector3Int.one;
            _jobMax = kMax + Vector3Int.one;
            _anchorJob = AnchorSteps(new List<VoxelChunk>(_buildQueue));
        }

        void StepBuild()
        {
            BuildClock.Restart();
            if (_anchorJob != null)
                while (BuildClock.Elapsed.TotalMilliseconds < AnchorBudgetMs)
                    if (!_anchorJob.MoveNext()) { _anchorJob = null; break; }
            if (_buildQueue == null) return;
            BuildClock.Restart();
            float k = Mathf.Clamp01((Time.unscaledTime - _buildStart) / BuildSeconds);
            int target = Mathf.CeilToInt(_buildQueue.Count * k);
            while (_buildNext < target && BuildClock.Elapsed.TotalMilliseconds < MeshBudgetMs) BuildNext();
            if (_buildNext >= _buildQueue.Count) _buildQueue = null;
        }

        void BuildNext()
        {
            var c = _buildQueue[_buildNext++];
            if (c != null && !_dirty.Contains(c)) c.Rebuild(); // a dirty one gets built by the flush anyway
        }

        /// <summary>Put up whatever's left of the arena right now (someone's about to stand on it).</summary>
        public void FinishBuild()
        {
            FinishAnchors();
            if (_buildQueue == null) return;
            while (_buildNext < _buildQueue.Count) BuildNext();
            _buildQueue = null;
        }

        void FinishAnchors()
        {
            if (_anchorJob == null) return;
            var job = _anchorJob;
            _anchorJob = null; // first, so the flood fill can't re-enter through Set
            while (job.MoveNext()) { }
        }

        /// <summary>Called on every edit: the arena's anchors must be known before its cells change.</summary>
        void EnsureAnchorsFor(Vector3Int chunkKey)
        {
            if (chunkKey.x >= _jobMin.x && chunkKey.x <= _jobMax.x && chunkKey.y >= _jobMin.y && chunkKey.y <= _jobMax.y &&
                chunkKey.z >= _jobMin.z && chunkKey.z <= _jobMax.z)
                FinishAnchors();
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
        }

        /// <summary>
        /// Every separate piece of the arena as built (floating platforms included) is held up by its own bottom
        /// layer: those cells are anchors. Break a piece so part of it no longer reaches its anchors and that part falls.
        /// </summary>
        /// <summary>Adds the anchors of every piece that has a cell in these chunks (all at once).</summary>
        void ComputeAnchors(IEnumerable<VoxelChunk> chunks)
        {
            var job = AnchorSteps(new List<VoxelChunk>(chunks));
            while (job.MoveNext()) { }
        }

        /// <summary>
        /// The anchor flood fill, in steps (it yields every couple of thousand cells so it can be spread over frames).
        /// The result only depends on the grid, which mustn't change in these chunks until it's done.
        /// </summary>
        IEnumerator<bool> AnchorSteps(List<VoxelChunk> chunks)
        {
            // Visited cells are one bit per cell, per chunk, and neighbours inside the same chunk are plain array
            // offsets: arenas carry millions of solid cells (the volcano: 3 million), so no hashing per cell.
            var visited = new Dictionary<VoxelChunk, ulong[]>();
            var queue = new Queue<(VoxelChunk c, int i)>();
            var lowest = new List<Vector3Int>();
            const int Batch = 4096;
            int work = 0;
            foreach (var chunk in chunks)
            {
                if (chunk == null) continue;
                var cells = chunk.Cells;
                var seen = VisitedBits(visited, chunk);
                for (int i = 0; i < cells.Length; i++)
                {
                    if ((i & 63) == 0 && ++work >= Batch) { work = 0; yield return true; }
                    if (cells[i] == 0 || (seen[i >> 6] & (1UL << i)) != 0) continue;
                    seen[i >> 6] |= 1UL << i;
                    queue.Enqueue((chunk, i));
                    lowest.Clear();
                    int minY = int.MaxValue;
                    while (queue.Count > 0)
                    {
                        if (++work >= Batch) { work = 0; yield return true; }
                        var (c, idx) = queue.Dequeue();
                        var bits = VisitedBits(visited, c);
                        int lx = idx & ChunkMask, lz = (idx >> ChunkBits) & ChunkMask, ly = idx >> (ChunkBits * 2);
                        int y = c.Key.y * ChunkSize + ly;
                        if (y < minY) { minY = y; lowest.Clear(); }
                        if (y == minY) lowest.Add(c.Key * ChunkSize + new Vector3Int(lx, ly, lz));
                        const int SZ = 1 << ChunkBits, SY = 1 << (ChunkBits * 2);
                        FloodTo(c, bits, lx > 0, idx - 1, new Vector3Int(-1, 0, 0), idx + ChunkMask, visited, queue);
                        FloodTo(c, bits, lx < ChunkMask, idx + 1, new Vector3Int(1, 0, 0), idx - ChunkMask, visited, queue);
                        FloodTo(c, bits, ly > 0, idx - SY, new Vector3Int(0, -1, 0), idx + ChunkMask * SY, visited, queue);
                        FloodTo(c, bits, ly < ChunkMask, idx + SY, new Vector3Int(0, 1, 0), idx - ChunkMask * SY, visited, queue);
                        FloodTo(c, bits, lz > 0, idx - SZ, new Vector3Int(0, 0, -1), idx + ChunkMask * SZ, visited, queue);
                        FloodTo(c, bits, lz < ChunkMask, idx + SZ, new Vector3Int(0, 0, 1), idx - ChunkMask * SZ, visited, queue);
                    }
                    // a piece standing on the floor is held up by it already (IsAnchor)
                    if (minY > AnchorY) foreach (var p in lowest) _anchors.Add(p);
                }
            }
        }

        static ulong[] VisitedBits(Dictionary<VoxelChunk, ulong[]> visited, VoxelChunk c)
        {
            if (!visited.TryGetValue(c, out var bits)) visited[c] = bits = new ulong[ChunkSize * ChunkSize * ChunkSize / 64];
            return bits;
        }

        /// <summary>One step of the anchor flood fill: to the neighbour in this chunk, or across into the next one.</summary>
        void FloodTo(VoxelChunk c, ulong[] bits, bool inside, int inIdx, Vector3Int dk, int outIdx,
            Dictionary<VoxelChunk, ulong[]> visited, Queue<(VoxelChunk, int)> queue)
        {
            if (!inside)
            {
                if (!Chunks.TryGetValue(c.Key + dk, out c) || c == null) return;
                inIdx = outIdx;
                if (c.Cells[inIdx] == 0) return;
                bits = VisitedBits(visited, c);
            }
            else if (c.Cells[inIdx] == 0) return;
            ulong m = 1UL << inIdx;
            if ((bits[inIdx >> 6] & m) != 0) return;
            bits[inIdx >> 6] |= m;
            queue.Enqueue((c, inIdx));
        }

        readonly HashSet<Vector3Int> _anchors = new();
        bool IsAnchor(Vector3Int p) => p.y <= AnchorY || _anchors.Contains(p);

        // ------------------------------------------------------------------ regions (practice chambers)

        /// <summary>
        /// Copies a whole arena into the grid, shifted by <paramref name="offset"/> cells (a multiple of the chunk size),
        /// for a practice chamber living off to the side of the main arena. Its materials are matched to (or added to)
        /// this world's palette in order, so every peer that applies the same stamps ends up with the same palette.
        /// </summary>
        public void Stamp(VoxelArenaData data, Vector3Int offset)
        {
            var map = new ushort[data.palette.Count];
            for (int i = 1; i < data.palette.Count; i++)
            {
                var m = data.palette[i];
                int found = -1;
                for (int j = 1; j < Palette.Count && found < 0; j++)
                {
                    var p = Palette[j];
                    if (!_isSoot.Contains((ushort)j) && !_isBlight.Contains((ushort)j) && !_isIce.Contains((ushort)j) && !_worldStuff.ContainsKey((ushort)j) && p.indestructible == m.indestructible && p.color.Equals(m.color)) found = j;
                }
                if (found < 0) { Palette.Add(m); found = Palette.Count - 1; }
                map[i] = (ushort)found;
            }
            var chunkOffset = new Vector3Int(offset.x >> ChunkBits, offset.y >> ChunkBits, offset.z >> ChunkBits);
            var stamped = new List<VoxelChunk>();
            foreach (var kv in data.chunks)
            {
                var c = GetOrCreateChunk(kv.Key + chunkOffset);
                var src = kv.Value;
                for (int i = 0; i < src.Length; i++) c.Cells[i] = src[i] < map.Length ? map[src[i]] : (ushort)0;
                c.RecountSolid();
                _dirty.Add(c);
                stamped.Add(c);
            }
            ComputeAnchors(stamped);
            FlushRebuilds();
        }

        /// <summary>Empties every chunk whose x lies in [minCellX, maxCellX) (a practice chamber closing or resetting).</summary>
        public void ClearRegion(int minCellX, int maxCellX)
        {
            var gone = new List<Vector3Int>();
            foreach (var key in Chunks.Keys)
            {
                int x = key.x * ChunkSize;
                if (x >= minCellX && x < maxCellX) gone.Add(key);
            }
            foreach (var key in gone)
            {
                var c = Chunks[key];
                Chunks.Remove(key);
                _dirty.Remove(c);
                if (c != null) { c.gameObject.SetActive(false); Destroy(c.gameObject); } // its collider is gone this frame
            }
            _anchors.RemoveWhere(p => p.x >= minCellX && p.x < maxCellX);
            _summoned.RemoveWhere(p => p.x >= minCellX && p.x < maxCellX);
        }

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
            if (_anchorJob != null) EnsureAnchorsFor(key);
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

        void LateUpdate()
        {
            if (Building) StepBuild();
            StepLate();
            FlushRebuilds();
        }

        /// <summary>Re-mesh every chunk that changed (also called before physics queries that must see edits).</summary>
        public void FlushRebuilds()
        {
            if (_dirty.Count == 0) return;
            if (_late.Count > 0) foreach (var c in _dirty) _late.Remove(c); // (up to date now: no need to do it again later)
            VoxelChunk.RebuildMany(_dirty);
            _dirty.Clear();
        }

        void RebuildAll()
        {
            VoxelChunk.RebuildMany(Chunks.Values);
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
            if (v == 0 || _isSoot.Contains(v) || _worldStuff.ContainsKey(v)) return v; // (The World's shell doesn't scorch)
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
            if (_isIce.Contains(v)) _isIce.Add(s); // singed ice still crumbles as ice
            return s;
        }

        // ------------------------------------------------------------------ blight (Necromatic Blast)

        readonly Dictionary<ushort, ushort> _blight = new();
        readonly HashSet<ushort> _isBlight = new();

        /// <summary>The sickly green version of a material, light where it was light. Added the first time it's needed, in op order, as soot is.</summary>
        ushort BlightOf(ushort v)
        {
            if (v == 0 || _isBlight.Contains(v) || _worldStuff.ContainsKey(v)) return v;
            if (_blight.TryGetValue(v, out var b)) return b;
            var m = Palette[v];
            Color c = m.color;
            float lum = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
            Color green = new Color(0.5f, 0.78f, 0.22f) * (0.4f + 0.6f * lum);
            green.a = 1f;
            Palette.Add(new VoxelMaterial { color = green, indestructible = m.indestructible });
            b = (ushort)(Palette.Count - 1);
            _blight[v] = b;
            _isBlight.Add(b);
            if (_isIce.Contains(v)) _isIce.Add(b);
            return b;
        }

        /// <summary>Every cell within radius r4/4 cells turns a sickly green. Call it only while applying an op.</summary>
        public void Blight(Vector3Int center, int r4)
        {
            int r = (r4 + 3) / 4, lim = r4 * r4;
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            for (int dz = -r; dz <= r; dz++)
            {
                if ((dx * dx + dy * dy + dz * dz) * 16 > lim) continue;
                var p = new Vector3Int(center.x + dx, center.y + dy, center.z + dz);
                ushort v = Get(p);
                if (v == 0) continue;
                ushort b = BlightOf(v);
                if (b != v) Set(p, b);
            }
        }

        /// <summary>The scorched version of a material (see <see cref="SootOf"/>). Adds a palette entry: call it only while applying an op.</summary>
        public ushort Scorched(ushort v) => SootOf(v);

        // ------------------------------------------------------------------ ice

        readonly Dictionary<byte, ushort> _ice = new();
        readonly HashSet<ushort> _isIce = new();
        /// <summary>Ice Spike's shades, base to tip: a clear blue, paler, nearly white.</summary>
        static readonly Color32[] IceShades = { new(120, 200, 238, 255), new(172, 228, 248, 255), new(228, 248, 255, 255) };

        /// <summary>
        /// The palette entry for ice of this shade (0..2), added the first time it's needed. Call it only while
        /// applying an op, so every peer adds it at the same point and the palettes stay the same.
        /// </summary>
        public ushort IceValue(byte shade)
        {
            shade = (byte)Mathf.Min(shade, IceShades.Length - 1);
            if (_ice.TryGetValue(shade, out var v)) return v;
            Palette.Add(new VoxelMaterial { color = IceShades[shade] });
            v = (ushort)(Palette.Count - 1);
            _ice[shade] = v;
            _isIce.Add(v);
            return v;
        }

        /// <summary>Is this ice (an Ice Spike's), singed or not?</summary>
        public bool IsIce(ushort v) => v != 0 && _isIce.Contains(v);

        // ------------------------------------------------------------------ black bile (Death)

        readonly Dictionary<byte, ushort> _bile = new();
        readonly HashSet<ushort> _isBile = new();
        static readonly Color32[] BileShades = { new(30, 29, 32, 255), new(39, 38, 41, 255) }; // black and a dark grey, close (no checkerboard)

        /// <summary>The palette entry for bile of this shade (0..1), added the first time it's needed. Call it only while applying an op.</summary>
        public ushort BileValue(byte shade)
        {
            shade = (byte)Mathf.Min(shade, BileShades.Length - 1);
            if (_bile.TryGetValue(shade, out var v)) return v;
            Palette.Add(new VoxelMaterial { color = BileShades[shade] });
            v = (ushort)(Palette.Count - 1);
            _bile[shade] = v;
            _isBile.Add(v);
            return v;
        }

        /// <summary>Is this Death's black bile?</summary>
        public bool IsBile(ushort v) => v != 0 && _isBile.Contains(v);

        /// <summary>Bile drying off a cell: back to what it was. Call it only while applying an op.</summary>
        public void Unbile(Vector3Int p, ushort was)
        {
            if (!IsBile(Get(p)) || was == 0) return;
            Set(p, was);
        }

        /// <summary>Ice melting off a cell: back to what it was (singed or blighted if the ice was). Call it only while applying an op.</summary>
        public void Thaw(Vector3Int p, ushort was)
        {
            ushort v = Get(p);
            if (!IsIce(v) || was == 0) return;
            Set(p, _isSoot.Contains(v) ? SootOf(was) : _isBlight.Contains(v) ? BlightOf(was) : was);
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
                EraseTop(x, z, yMin, yMax, removed);
            }
        }

        /// <summary>Removes the topmost voxel of one column between yMin and yMax (an indestructible top shields what's under it).</summary>
        public void EraseTop(int x, int z, int yMin, int yMax, List<RemovedCell> removed)
        {
            for (int y = yMax; y >= yMin; y--)
            {
                ushort v = Get(x, y, z);
                if (v == 0) continue;
                if (!IsIndestructible(v))
                {
                    removed?.Add(new RemovedCell { cell = new Vector3Int(x, y, z), value = v });
                    Set(x, y, z, 0);
                }
                return;
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
        /// <para>
        /// The search always grows from its lowest cell first (anchors are at the bottom of things), so in solid ground
        /// it runs more or less straight down to them instead of flooding a ball of terrain around the hole, which
        /// could be tens of thousands of cells for every break high up on a mountain.
        /// </para>
        /// </summary>
        void DropIslands(List<Vector3Int> boundary, List<RemovedCell> _, List<List<RemovedCell>> islands)
        {
            if (boundary == null || boundary.Count == 0) return;
            using var marker = IslandsMarker.Auto();
            var settled = _settled; // cells already proven anchored
            var comp = _comp;
            var seen = _seen;
            settled.Clear();
            foreach (var b in boundary)
            foreach (var d in Dirs)
            {
                var start = b + d;
                if (!IsSolid(start) || settled.Contains(start)) continue;
                // search this piece
                comp.Clear();
                seen.Clear();
                _heap.Clear();
                _heapSeq = 0;
                HeapPush(start);
                seen.Add(start);
                bool anchored = false;
                while (_heap.Count > 0)
                {
                    var p = HeapPop();
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
                        HeapPush(n);
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
            settled.Clear();
            seen.Clear();
            comp.Clear();
            _heap.Clear();
        }

        static readonly Unity.Profiling.ProfilerMarker IslandsMarker = new("SME.VoxelWorld.DropIslands");
        readonly HashSet<Vector3Int> _settled = new(), _seen = new();
        readonly List<Vector3Int> _comp = new();

        // min-heap on (y, then order pushed): lowest first, ties in a fixed order so every peer searches the same way
        readonly List<(int y, int seq, Vector3Int p)> _heap = new();
        int _heapSeq;

        static bool Less((int y, int seq, Vector3Int p) a, (int y, int seq, Vector3Int p) b) => a.y < b.y || (a.y == b.y && a.seq < b.seq);

        void HeapPush(Vector3Int p)
        {
            var item = (p.y, _heapSeq++, p);
            int i = _heap.Count;
            _heap.Add(item);
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (!Less(item, _heap[parent])) break;
                _heap[i] = _heap[parent];
                i = parent;
            }
            _heap[i] = item;
        }

        Vector3Int HeapPop()
        {
            var top = _heap[0].p;
            int last = _heap.Count - 1;
            var item = _heap[last];
            _heap.RemoveAt(last);
            if (last == 0) return top;
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1;
                if (l >= last) break;
                int r = l + 1, m = r < last && Less(_heap[r], _heap[l]) ? r : l;
                if (!Less(_heap[m], item)) break;
                _heap[i] = _heap[m];
                i = m;
            }
            _heap[i] = item;
            return top;
        }

        /// <summary>Is this collider part of the voxel world (and not an indestructible-only spot)?</summary>
        public static bool IsVoxelCollider(Collider c) => c != null && c.GetComponent<VoxelChunk>() != null;
    }
}
