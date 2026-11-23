using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace SuddenMageEncounter
{
    /// <summary>One 16³ block of the voxel world: the cells, plus the mesh and collider built from them.</summary>
    public class VoxelChunk : MonoBehaviour
    {
        public const int N = VoxelWorld.ChunkSize;
        public readonly ushort[] Cells = new ushort[N * N * N];
        public int Solid;
        public Vector3Int Key { get; private set; }
        public VoxelWorld World { get; private set; }

        MeshFilter _mf;
        MeshRenderer _mr;
        MeshCollider _mc;
        Mesh _mesh;

        public static int Index(int x, int y, int z) => x + (z << VoxelWorld.ChunkBits) + (y << (VoxelWorld.ChunkBits * 2));

        public static VoxelChunk Create(VoxelWorld world, Vector3Int key)
        {
            var go = new GameObject($"Chunk {key.x},{key.y},{key.z}");
            go.transform.SetParent(world.transform, false);
            go.transform.position = world.Origin + (Vector3)(key * N) * VoxelWorld.Size;
            var c = go.AddComponent<VoxelChunk>();
            c.Key = key;
            c.World = world;
            c._mf = go.AddComponent<MeshFilter>();
            c._mr = go.AddComponent<MeshRenderer>();
            c._mr.sharedMaterials = world.RenderMaterials;
            c._mr.shadowCastingMode = ShadowCastingMode.On;
            c._mc = go.AddComponent<MeshCollider>();
            c._mesh = new Mesh { name = go.name, indexFormat = IndexFormat.UInt32 };
            c._mesh.MarkDynamic();
            return c;
        }

        public void RecountSolid()
        {
            int n = 0;
            foreach (var v in Cells) if (v != 0) n++;
            Solid = n;
        }

        /// <summary>Lowest local y holding a solid cell (0 if none).</summary>
        public int LowestSolidY()
        {
            for (int y = 0; y < N; y++)
            for (int z = 0; z < N; z++)
            for (int x = 0; x < N; x++)
                if (Cells[Index(x, y, z)] != 0) return y;
            return 0;
        }

        const int P = N + 2;
        static readonly ushort[] Pad = new ushort[P * P * P];
        static int PadIndex(int x, int y, int z) => (x + 1) + (z + 1) * P + (y + 1) * P * P;
        static readonly System.Func<int, int, int, ushort> PadGet = (x, y, z) => Pad[PadIndex(x, y, z)];

        /// <summary>Re-mesh and re-cook the collider now.</summary>
        public void Rebuild()
        {
            if (!RebuildMesh()) return;
            Physics.BakeMesh(_mesh.GetInstanceID(), false, _mc.cookingOptions);
            ApplyCollider();
        }

        static readonly ProfilerMarker MeshMarker = new("SME.VoxelChunk.Mesh"), CookMarker = new("SME.VoxelChunk.CookColliders");
        static readonly List<VoxelChunk> ToCook = new();

        struct CookJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int> meshes;
            public MeshColliderCookingOptions options;
            public void Execute(int i) => Physics.BakeMesh(meshes[i], false, options);
        }

        /// <summary>
        /// Re-mesh several chunks, then cook all their colliders at once on worker threads. Cooking is most of the cost
        /// of breaking voxels, and one at a time on the main thread a big break cost a frame or more.
        /// </summary>
        public static void RebuildMany(IEnumerable<VoxelChunk> chunks)
        {
            ToCook.Clear();
            using (MeshMarker.Auto())
                foreach (var c in chunks)
                    if (c != null && c.RebuildMesh()) ToCook.Add(c);
            if (ToCook.Count == 0) return;
            using (CookMarker.Auto())
            {
                if (ToCook.Count == 1) Physics.BakeMesh(ToCook[0]._mesh.GetInstanceID(), false, ToCook[0]._mc.cookingOptions);
                else
                {
                    var ids = new NativeArray<int>(ToCook.Count, Allocator.TempJob);
                    for (int i = 0; i < ToCook.Count; i++) ids[i] = ToCook[i]._mesh.GetInstanceID();
                    new CookJob { meshes = ids, options = ToCook[0]._mc.cookingOptions }.Schedule(ids.Length, 1).Complete();
                    ids.Dispose();
                }
                // (already cooked with the collider's own options, so assigning just picks the cooked data up)
                foreach (var c in ToCook) c.ApplyCollider();
            }
            ToCook.Clear();
        }

        void ApplyCollider()
        {
            _mc.sharedMesh = null;
            _mc.sharedMesh = _mesh;
        }

        /// <summary>Re-mesh; true if the collider then needs cooking (<see cref="ApplyCollider"/>), false if it was simply cleared.</summary>
        bool RebuildMesh()
        {
            if (Solid <= 0)
            {
                _mesh.Clear();
                _mf.sharedMesh = null;
                _mc.sharedMesh = null;
                _mr.enabled = false;
                return false;
            }
            // solid through and through, and buried in solid ground: nothing to see
            if (Buried())
            {
                _mesh.Clear();
                _mf.sharedMesh = null;
                _mc.sharedMesh = null;
                _mr.enabled = false;
                return false;
            }
            // copy the chunk plus a one-cell border into a flat array, so the mesher never does a dictionary lookup per voxel
            var origin = Key * N;
            for (int y = -1; y <= N; y++)
            for (int z = -1; z <= N; z++)
            for (int x = -1; x <= N; x++)
            {
                bool inside = (uint)x < N && (uint)y < N && (uint)z < N;
                Pad[PadIndex(x, y, z)] = inside ? Cells[Index(x, y, z)] : World.Get(origin.x + x, origin.y + y, origin.z + z);
            }
            VoxelMesher.Build(_mesh, PadGet, N, N, N, World.ColorOf, VoxelWorld.Size);
            _mf.sharedMesh = _mesh;
            _mr.enabled = _mesh.vertexCount > 0;
            if (_mesh.vertexCount > 0) return true;
            _mc.sharedMesh = null;
            return false;
        }

        static readonly Vector3Int[] Sides = { Vector3Int.left, Vector3Int.right, Vector3Int.down, Vector3Int.up, new(0, 0, -1), new(0, 0, 1) };

        /// <summary>Full of solid (not glass) cells, as are all six chunks round it: no face of it can show.</summary>
        bool Buried()
        {
            if (!Full()) return false;
            foreach (var d in Sides)
                if (!World.Chunks.TryGetValue(Key + d, out var n) || n == null || !n.Full()) return false;
            return true;
        }

        bool Full()
        {
            if (Solid < N * N * N) return false;
            foreach (var v in Cells) if (v == 0 || VoxelMesher.IsGlass(World.ColorOf(v))) return false;
            return true;
        }

        void OnDestroy()
        {
            if (_mesh == null) return;
            if (Application.isPlaying) Destroy(_mesh);
            else DestroyImmediate(_mesh);
        }
    }
}
