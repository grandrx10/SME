using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// A piece of ground that moves as one solid object (a rising wall, a thrown rock, a folding slab).
    /// The transform is the pivot (move/rotate it); the voxels hang off it exactly where they were taken from.
    /// It collides (kinematic) so players, projectiles and debris react to it while it moves.
    /// </summary>
    public class VoxelBody : MonoBehaviour
    {
        public int Id { get; private set; }
        public List<VoxelWorld.RemovedCell> Cells { get; private set; }
        public Vector3 HomePivot { get; private set; }
        public Bounds LocalBounds { get; private set; }
        public Transform MeshRoot { get; private set; }
        Mesh _mesh;
        Rigidbody _rb;

        public static VoxelBody Create(VoxelWorld world, int id, List<VoxelWorld.RemovedCell> cells, Vector3 pivot, bool collide = true)
        {
            var go = new GameObject("VoxelBody " + id);
            go.transform.SetParent(world.transform, true);
            go.transform.SetPositionAndRotation(pivot, Quaternion.identity);
            var b = go.AddComponent<VoxelBody>();
            b.Id = id;
            b.Cells = cells;
            b.HomePivot = pivot;

            var meshGo = new GameObject("Mesh");
            meshGo.transform.SetParent(go.transform, false);
            b.MeshRoot = meshGo.transform;
            b._mesh = new Mesh { name = "VoxelBody" };
            VoxelMesher.BuildCells(b._mesh, cells, Vector3Int.zero, world.ColorOf, VoxelWorld.Size, out var min, out var max);
            // vertices are relative to cell (0,0,0); place that corner where it is in the world
            meshGo.transform.localPosition = world.CellMin(Vector3Int.zero) - pivot;
            meshGo.AddComponent<MeshFilter>().sharedMesh = b._mesh;
            meshGo.AddComponent<MeshRenderer>().sharedMaterial = world.RenderMaterial;
            Vector3 lo = world.CellMin(min) - pivot, hi = world.CellMin(max + Vector3Int.one) - pivot;
            b.LocalBounds = new Bounds((lo + hi) * 0.5f, hi - lo);
            if (collide && b._mesh.vertexCount > 0)
            {
                var mc = meshGo.AddComponent<MeshCollider>();
                mc.sharedMesh = b._mesh;
                b._rb = go.AddComponent<Rigidbody>();
                b._rb.isKinematic = true;
                b._rb.interpolation = RigidbodyInterpolation.None;
            }
            return b;
        }

        public void SetPose(Vector3 pivot, Quaternion rotation)
        {
            transform.SetPositionAndRotation(pivot, rotation);
            if (_rb != null) { _rb.position = pivot; _rb.rotation = rotation; }
        }

        /// <summary>World-space centre of the voxels right now.</summary>
        public Vector3 Center => transform.TransformPoint(LocalBounds.center);

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
