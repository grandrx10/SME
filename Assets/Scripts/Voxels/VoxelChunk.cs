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
            c._mr.sharedMaterial = world.RenderMaterial;
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

        public void Rebuild()
        {
            if (Solid <= 0)
            {
                _mesh.Clear();
                _mf.sharedMesh = null;
                _mc.sharedMesh = null;
                _mr.enabled = false;
                return;
            }
            var origin = Key * N;
            VoxelMesher.Build(_mesh, (x, y, z) => World.Get(origin.x + x, origin.y + y, origin.z + z), N, N, N,
                v => World.ColorOf(v), VoxelWorld.Size);
            _mf.sharedMesh = _mesh;
            _mr.enabled = _mesh.vertexCount > 0;
            _mc.sharedMesh = null; // force re-cook
            _mc.sharedMesh = _mesh.vertexCount > 0 ? _mesh : null;
        }

        void OnDestroy()
        {
            if (_mesh == null) return;
            if (Application.isPlaying) Destroy(_mesh);
            else DestroyImmediate(_mesh);
        }
    }
}
