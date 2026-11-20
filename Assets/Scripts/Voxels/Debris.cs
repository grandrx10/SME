using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Client-side rubble. Broken-off voxels fly as clumps (chunks of voxels that stay together) and only split
    /// into smaller clumps when they hit something hard enough, down to single voxels, which then fade away.
    /// Purely visual: debris never changes the world or hurts anyone, so every client can simulate its own.
    /// </summary>
    public class Debris : MonoBehaviour
    {
        const int MaxPieces = 450;
        const float SplitSpeed = 6f;
        static readonly List<Debris> Live = new();
        public static bool Suppressed; // replaying history for a late joiner: no rubble

        /// <summary>Every piece of rubble flying around right now.</summary>
        public static IReadOnlyList<Debris> All => Live;
        public Rigidbody Body => _rb;
        public int CellCount => _cells != null ? _cells.Count : 0;
        /// <summary>Caught by something (a tornado) that steers it.</summary>
        public bool Held { get; set; }

        /// <summary>Don't fade away for at least this long.</summary>
        public void KeepAlive(float seconds)
        {
            _dieAt = Mathf.Max(_dieAt, Time.time + seconds);
            transform.localScale = _scale0;
        }

        List<VoxelWorld.RemovedCell> _cells;
        int _block;
        float _dieAt, _born;
        Rigidbody _rb;
        Mesh _mesh;
        Vector3 _scale0;
        bool _split;

        /// <summary>Throws the removed cells as clumps of <paramref name="block"/>³ voxels, pushed away from <paramref name="center"/>.</summary>
        /// <param name="frame">Cells are laid out in this transform's space (a moving body's mesh root, cell (0,0,0) at its origin);
        /// null = where they sit in the world grid.</param>
        public static void Spawn(VoxelWorld world, List<VoxelWorld.RemovedCell> cells, Vector3 center, float push, Vector3 extraVelocity, int block, Transform frame = null)
        {
            if (Suppressed || world == null || cells == null || cells.Count == 0) return;
            // too much rubble at once: thin it out (keep the clumps, drop a random share)
            float keep = Mathf.Clamp01(1500f / cells.Count);
            var groups = Group(cells, block);
            foreach (var g in groups)
            {
                if (keep < 1f && Random.value > keep) continue;
                var min = Min(g);
                Vector3 pos = frame != null ? frame.TransformPoint((Vector3)min * VoxelWorld.Size) : world.CellMin(min);
                Quaternion rot = frame != null ? frame.rotation : Quaternion.identity;
                Vector3 mid = pos + rot * ((Vector3)(Max(g) - min + Vector3Int.one) * (VoxelWorld.Size * 0.5f));
                Vector3 away = mid - center;
                float dist = Mathf.Max(0.3f, away.magnitude);
                Vector3 v = away / dist * (push / Mathf.Sqrt(dist)) + Random.insideUnitSphere * push * 0.25f + Vector3.up * push * 0.35f + extraVelocity;
                Make(world, g, min, pos, rot, v, Random.insideUnitSphere * 4f, block);
            }
        }

        static Debris Make(VoxelWorld world, List<VoxelWorld.RemovedCell> cells, Vector3Int min, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel, int block)
        {
            while (Live.Count >= MaxPieces)
            {
                var old = Live[0];
                Live.RemoveAt(0);
                if (old != null) Destroy(old.gameObject);
            }
            var go = new GameObject("Debris") { layer = Layers.Fx };
            go.transform.SetPositionAndRotation(pos, rot);
            var d = go.AddComponent<Debris>();
            d._cells = cells;
            d._block = block;
            d._mesh = new Mesh { name = "Debris" };
            VoxelMesher.BuildCells(d._mesh, cells, min, world.ColorOf, VoxelWorld.Size, out var cmin, out var cmax);
            go.AddComponent<MeshFilter>().sharedMesh = d._mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = world.RenderMaterial;
            var box = go.AddComponent<BoxCollider>();
            Vector3 size = (Vector3)(cmax - cmin + Vector3Int.one) * VoxelWorld.Size;
            box.center = (Vector3)(cmin - min) * VoxelWorld.Size + size * 0.5f;
            box.size = size * 0.96f;
            d._rb = go.AddComponent<Rigidbody>();
            d._rb.mass = Mathf.Max(0.2f, cells.Count * 0.15f);
            d._rb.linearVelocity = vel;
            d._rb.angularVelocity = angVel;
            d._rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            d._rb.interpolation = RigidbodyInterpolation.Interpolate;
            d._born = Time.time;
            d._dieAt = Time.time + Random.Range(4f, 6.5f) * (cells.Count > 1 ? 1f : 0.6f);
            d._scale0 = Vector3.one;
            d._world = world;
            Live.Add(d);
            return d;
        }

        VoxelWorld _world;

        void Update()
        {
            float left = _dieAt - Time.time;
            if (left < 0.5f)
            {
                transform.localScale = _scale0 * Mathf.Max(0.01f, left / 0.5f);
                if (left <= 0f) Destroy(gameObject);
            }
        }

        void OnCollisionEnter(Collision c)
        {
            if (_split || Held || _cells.Count <= 1 || Time.time - _born < 0.06f) return;
            if (c.relativeVelocity.magnitude < SplitSpeed) return;
            Split();
        }

        /// <summary>Breaks into clumps half the size (or single voxels).</summary>
        void Split()
        {
            if (_world == null) return;
            _split = true;
            int block = Mathf.Max(1, _block / 2);
            var parentMin = Min(_cells);
            Vector3 v = _rb.linearVelocity, w = _rb.angularVelocity;
            foreach (var g in Group(_cells, block))
            {
                var min = Min(g);
                Vector3 local = (Vector3)(min - parentMin) * VoxelWorld.Size;
                Vector3 pos = transform.TransformPoint(local);
                Make(_world, g, min, pos, transform.rotation, v * 0.7f + Random.insideUnitSphere * 2.5f, w + Random.insideUnitSphere * 3f, block);
            }
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            Live.Remove(this);
            if (_mesh != null) Destroy(_mesh);
        }

        // ------------------------------------------------------------------ helpers

        static List<List<VoxelWorld.RemovedCell>> Group(List<VoxelWorld.RemovedCell> cells, int block)
        {
            var map = new Dictionary<Vector3Int, List<VoxelWorld.RemovedCell>>();
            var order = new List<List<VoxelWorld.RemovedCell>>();
            foreach (var c in cells)
            {
                var key = new Vector3Int(FloorDiv(c.cell.x, block), FloorDiv(c.cell.y, block), FloorDiv(c.cell.z, block));
                if (!map.TryGetValue(key, out var list))
                {
                    map[key] = list = new List<VoxelWorld.RemovedCell>();
                    order.Add(list);
                }
                list.Add(c);
            }
            return order;
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

        static Vector3Int Min(List<VoxelWorld.RemovedCell> cells)
        {
            var m = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
            foreach (var c in cells) m = Vector3Int.Min(m, c.cell);
            return m;
        }

        static Vector3Int Max(List<VoxelWorld.RemovedCell> cells)
        {
            var m = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
            foreach (var c in cells) m = Vector3Int.Max(m, c.cell);
            return m;
        }

        public static void ClearAll()
        {
            foreach (var d in Live.ToArray()) if (d != null) Destroy(d.gameObject);
            Live.Clear();
        }
    }
}
