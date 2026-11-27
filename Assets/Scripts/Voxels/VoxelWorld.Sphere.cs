using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The World's sphere: its lower half solid ground, its upper half hollow under a glass dome, all inside an unbreakable
    /// shell until it's over. Everything is worked out from the centre cell in integers, so every peer's is identical; the
    /// cells change at once (an op), while the meshes catch up over the next second (there are hundreds of chunks of it),
    /// which is hidden by the sphere that swells over it.
    /// </summary>
    public partial class VoxelWorld
    {
        // ------------------------------------------------------------------ materials

        /// <summary>The World's own palette entries (they never scorch, freeze or get matched by a practice chamber) → what each turns into afterwards.</summary>
        readonly Dictionary<ushort, ushort> _worldStuff = new();
        readonly Dictionary<ushort, ushort> _worldShells = new();
        ushort _worldGlass;
        static readonly Color32 WorldGlassColor = new(236, 206, 190, 72); // clear, a breath of mahogany

        /// <summary>Unbreakable dome glass. Added the first time it's needed: call it only while applying an op.</summary>
        ushort WorldGlass()
        {
            if (_worldGlass != 0) return _worldGlass;
            Palette.Add(new VoxelMaterial { color = WorldGlassColor, indestructible = true });
            _worldGlass = (ushort)(Palette.Count - 1);
            _worldStuff[_worldGlass] = 0; // (air, once the dome breaks)
            return _worldGlass;
        }

        /// <summary>An unbreakable twin of a ground material, the same colour. Call it only while applying an op.</summary>
        ushort WorldShellOf(ushort body)
        {
            if (_worldShells.TryGetValue(body, out var s)) return s;
            Palette.Add(new VoxelMaterial { color = Palette[body].color, indestructible = true });
            s = (ushort)(Palette.Count - 1);
            _worldShells[body] = s;
            _worldStuff[s] = body; // (ordinary ground again, once it's over)
            return s;
        }

        // ------------------------------------------------------------------ raising and breaking it

        /// <summary>
        /// Makes the sphere of radius <paramref name="r"/> cells round <paramref name="c"/>: ground at and below its
        /// middle, air above, shelled <paramref name="shell"/> cells thick (ground below, glass above). Whatever was in the
        /// way is gone, but for what's unbreakable. <paramref name="seconds"/>: the meshes follow the swelling sphere over this long.
        /// </summary>
        public void RaiseWorldSphere(Vector3Int c, int r, int shell, ushort body, ushort top, float seconds)
        {
            FinishAnchors();
            if (body == 0 || body >= Palette.Count) body = 1;
            if (top == 0 || top >= Palette.Count) top = body;
            ushort glass = WorldGlass(), wall = WorldShellOf(body);
            int r2 = r * r, in2 = (r - shell) * (r - shell);
            float now = Time.time, full = r;
            Vector3Int kMin = ChunkOf(c.x - r, c.y - r, c.z - r), kMax = ChunkOf(c.x + r, c.y + r, c.z + r);
            for (int ky = kMin.y; ky <= kMax.y; ky++)
            for (int kz = kMin.z; kz <= kMax.z; kz++)
            for (int kx = kMin.x; kx <= kMax.x; kx++)
            {
                var key = new Vector3Int(kx, ky, kz);
                if (!SphereReaches(key, c, r2, out int far2)) continue;
                Chunks.TryGetValue(key, out var ch);
                var o = key * ChunkSize;
                bool changed = false;
                for (int y = 0; y < ChunkSize; y++)
                for (int z = 0; z < ChunkSize; z++)
                for (int x = 0; x < ChunkSize; x++)
                {
                    int dx = o.x + x - c.x, dy = o.y + y - c.y, dz = o.z + z - c.z;
                    int d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 > r2) continue;
                    ushort want = dy > 0 ? (d2 > in2 ? glass : (ushort)0) : d2 > in2 ? wall : dy == 0 ? top : body;
                    int i = VoxelChunk.Index(x, y, z);
                    ushort old = ch != null ? ch.Cells[i] : (ushort)0;
                    if (old == want || IsIndestructible(old)) continue; // (what can't be broken stays)
                    if (ch == null) ch = GetOrCreateChunk(key);
                    ch.Cells[i] = want;
                    changed = true;
                }
                if (!changed) continue;
                ch.RecountSolid();
                // its mesh changes once the swelling sphere has gone past it
                AddLate(ch, key, now + seconds * Mathf.Min(1f, Mathf.Sqrt(far2) / full));
            }
            _summoned.RemoveWhere(p => p.y > c.y && (p - c).sqrMagnitude <= r2);
            // the very bottom holds it up (over a drop, when it's all there is)
            for (int x = -r; x <= r; x++)
            for (int z = -r; z <= r; z++)
            for (int y = -r; y <= -(r - 1); y++)
                if (x * x + y * y + z * z <= r2) _anchors.Add(new Vector3Int(c.x + x, c.y + y, c.z + z));
        }

        /// <summary>
        /// It's over: the dome's glass goes (into <paramref name="removed"/>), whatever rested on it falls
        /// (<paramref name="islands"/>), and the ground half's shell is ordinary ground.
        /// </summary>
        public void BreakWorldSphere(Vector3Int c, int r, float seconds, List<RemovedCell> removed, List<List<RemovedCell>> islands)
        {
            int r2 = r * r, outer2 = (r - 1) * (r - 1);
            float now = Time.time;
            var edge = new List<Vector3Int>();
            Vector3Int kMin = ChunkOf(c.x - r, c.y - r, c.z - r), kMax = ChunkOf(c.x + r, c.y + r, c.z + r);
            for (int ky = kMin.y; ky <= kMax.y; ky++)
            for (int kz = kMin.z; kz <= kMax.z; kz++)
            for (int kx = kMin.x; kx <= kMax.x; kx++)
            {
                var key = new Vector3Int(kx, ky, kz);
                if (!SphereReaches(key, c, r2, out _) || !Chunks.TryGetValue(key, out var ch) || ch == null) continue;
                var o = key * ChunkSize;
                bool changed = false;
                for (int i = 0; i < ch.Cells.Length; i++)
                {
                    ushort v = ch.Cells[i];
                    if (v == 0 || !_worldStuff.TryGetValue(v, out var after)) continue;
                    var p = o + new Vector3Int(i & ChunkMask, i >> (ChunkBits * 2), (i >> ChunkBits) & ChunkMask);
                    int d2 = (p - c).sqrMagnitude;
                    if (d2 > r2) continue;
                    ch.Cells[i] = after;
                    if (after != 0) continue; // (the same colour: nothing to redraw)
                    removed?.Add(new RemovedCell { cell = p, value = v });
                    if (d2 > outer2) edge.Add(p);
                    changed = true;
                }
                if (!changed) continue;
                ch.RecountSolid();
                // the top goes first
                float down = 1f - Mathf.Clamp01((o.y + ChunkSize * 0.5f - c.y) / r);
                AddLate(ch, key, now + seconds * down);
            }
            DropIslands(edge, removed, islands);
        }

        /// <summary>Does a chunk reach into the sphere? And how far (squared, cells) its furthest corner is.</summary>
        static bool SphereReaches(Vector3Int key, Vector3Int c, int r2, out int far2)
        {
            int near = 0;
            far2 = 0;
            for (int a = 0; a < 3; a++)
            {
                int lo = key[a] * ChunkSize - c[a], hi = lo + ChunkSize - 1;
                int n = lo > 0 ? lo : hi < 0 ? -hi : 0;
                int f = Mathf.Max(Mathf.Abs(lo), Mathf.Abs(hi));
                near += n * n;
                far2 += f * f;
            }
            return near <= r2;
        }

        // ------------------------------------------------------------------ meshes that catch up later

        /// <summary>Chunks whose cells have changed but whose meshes wait their turn, and from when each may be done.</summary>
        readonly Dictionary<VoxelChunk, float> _late = new();
        readonly List<VoxelChunk> _lateNow = new();
        const double LateBudgetMs = 9.0;

        /// <summary>A chunk (and the chunks touching it, whose faces may show or hide) gets its mesh rebuilt after <paramref name="at"/>.</summary>
        void AddLate(VoxelChunk ch, Vector3Int key, float at)
        {
            Put(ch, at);
            for (int k = 0; k < 6; k++)
            {
                var d = new Vector3Int(k == 0 ? 1 : k == 1 ? -1 : 0, k == 2 ? 1 : k == 3 ? -1 : 0, k == 4 ? 1 : k == 5 ? -1 : 0);
                if (Chunks.TryGetValue(key + d, out var n)) Put(n, at);
            }
            void Put(VoxelChunk c, float t)
            {
                // the latest wanted time wins (a neighbour done early would only have to be done again)
                if (c != null && (!_late.TryGetValue(c, out float had) || had < t)) _late[c] = t;
            }
        }

        void StepLate()
        {
            if (_late.Count == 0) return;
            BuildClock.Restart();
            float now = Time.time;
            while (_late.Count > 0 && BuildClock.Elapsed.TotalMilliseconds < LateBudgetMs)
            {
                // a handful at a time (their colliders cook together): those due longest first
                _lateNow.Clear();
                foreach (var kv in _late)
                    if (kv.Value <= now) _lateNow.Add(kv.Key);
                if (_lateNow.Count == 0) return;
                _lateNow.Sort((a, b) => _late[a].CompareTo(_late[b]));
                const int Batch = 4;
                if (_lateNow.Count > Batch) _lateNow.RemoveRange(Batch, _lateNow.Count - Batch);
                foreach (var c in _lateNow) _late.Remove(c);
                VoxelChunk.RebuildMany(_lateNow);
            }
        }

        /// <summary>Every mesh still waiting is done now (someone's about to stand there).</summary>
        public void FinishLate()
        {
            if (_late.Count == 0) return;
            _lateNow.Clear();
            _lateNow.AddRange(_late.Keys);
            _late.Clear();
            VoxelChunk.RebuildMany(_lateNow);
            _lateNow.Clear();
        }
    }
}
