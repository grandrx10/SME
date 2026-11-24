using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// One car of a train (made and placed by <see cref="TrainTrack"/>): a solid block (spells stop on it, its roof can be
    /// stood on) with a voxel subway car on it, or the track's own car model.
    /// </summary>
    public class TrainCar : MonoBehaviour
    {
        TrainTrack _track;
        Rigidbody _rb;
        BoxCollider _box;

        public Vector3 Velocity => _track != null ? _track.Velocity : Vector3.zero;

        /// <summary>The train car this collider belongs to (null: it isn't one).</summary>
        public static TrainCar Of(Collider c) => c != null && c.attachedRigidbody != null ? c.attachedRigidbody.GetComponent<TrainCar>() : null;

        public static TrainCar Create(TrainTrack track, bool first, bool last)
        {
            var go = new GameObject("TrainCar");
            go.transform.SetParent(track.transform.parent, true);
            var c = go.AddComponent<TrainCar>();
            c._track = track;
            c._rb = go.AddComponent<Rigidbody>();
            c._rb.isKinematic = true;
            c._box = go.AddComponent<BoxCollider>();
            Vector3 s = track.carSize;
            c._box.size = s;
            c._box.center = Vector3.up * (s.y * 0.5f);
            var prefab = first && track.frontPrefab != null ? track.frontPrefab : last && track.backPrefab != null ? track.backPrefab : track.carPrefab;
            if (prefab != null)
            {
                var m = Instantiate(prefab, go.transform, false);
                foreach (var col in m.GetComponentsInChildren<Collider>()) Destroy(col); // (the block is what's solid)
            }
            else BuildVoxels(go.transform, s, first, last);
            go.SetActive(false);
            return c;
        }

        /// <param name="shown">How much of its length is on the track (0..1).</param>
        public void Place(Vector3 at, Quaternion rot, float shown)
        {
            if (gameObject.activeSelf != shown > 0.01f) gameObject.SetActive(shown > 0.01f);
            if (shown <= 0.01f) return;
            transform.SetPositionAndRotation(at, rot);
            transform.localScale = new Vector3(1f, 1f, shown);
            _rb.position = at;
            _rb.rotation = rot;
        }

        // ------------------------------------------------------------------ the voxel subway car: blue and grey, nothing else

        const byte Grey = 1, Blue = 2;

        static readonly Color32[] Palette =
        {
            default,
            new(158, 165, 176, 255), // grey
            new(38, 78, 160, 255),   // blue
        };

        /// <summary>The blocks it's laid out in (m): two voxels.</summary>
        const float Block = VoxelWorld.Size * 2f;
        static readonly Color HeadLight = new(2.4f, 2.5f, 2.7f), TailLight = new(0.35f, 0.85f, 3.6f);

        static readonly Dictionary<(Vector3Int, bool, bool), Mesh> Meshes = new();

        /// <summary>The built-in car under <paramref name="root"/> (also what the editor makes prefabs of).</summary>
        public static void BuildVoxels(Transform root, Vector3 size, bool first, bool last)
        {
            var n = new Vector3Int(Mathf.Max(10, Mathf.RoundToInt(size.x / Block)), Mathf.Max(14, Mathf.RoundToInt(size.y / Block)), Mathf.Max(16, Mathf.RoundToInt(size.z / Block)));
            if (!Meshes.TryGetValue((n, first, last), out var mesh) || mesh == null)
            {
                mesh = new Mesh { name = "TrainCar" };
                var cells = new List<VoxelWorld.RemovedCell>();
                for (int x = 0; x < n.x; x++)
                for (int y = 0; y < n.y; y++)
                for (int z = 0; z < n.z; z++)
                {
                    byte v = Cell(x, y, z, n, first, last);
                    if (v != 0) cells.Add(new VoxelWorld.RemovedCell { cell = new Vector3Int(x, y, z), value = v });
                }
                VoxelMesher.BuildCells(mesh, cells, Vector3Int.zero, v => Palette[Mathf.Min(v, Palette.Length - 1)], Block, out _, out _);
                Meshes[(n, first, last)] = mesh;
            }
            var go = new GameObject("Mesh");
            go.transform.SetParent(root, false);
            // exactly the size of the block that kills (a car size that isn't whole blocks is stretched to fit)
            go.transform.localScale = new Vector3(size.x / (n.x * Block), size.y / (n.y * Block), size.z / (n.z * Block));
            go.transform.localPosition = new Vector3(-size.x * 0.5f, 0f, -size.z * 0.5f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = VoxelRender.Materials;

            // headlights, tail lights (nearly flush with the ends)
            if (first) Lamps(root, size, n, 1f, HeadLight);
            if (last) Lamps(root, size, n, -1f, TailLight);
        }

        static void Lamps(Transform root, Vector3 size, Vector3Int n, float end, Color colour)
        {
            // in the middle of the two 3×3 surrounds (see Cell), about one block big
            Vector3 b = new(size.x / n.x, size.y / n.y, size.z / n.z);
            for (int k = -1; k <= 1; k += 2)
            {
                var p = Glow.Piece(root, Glow.CubeMesh, colour, "Lamp");
                p.localPosition = new Vector3(k * (size.x * 0.5f - 2.5f * b.x), 3.5f * b.y, end * (size.z * 0.5f - 0.08f));
                p.localScale = new Vector3(b.x * 1.2f, b.y * 1.2f, 0.24f);
            }
        }

        static byte Cell(int x, int y, int z, Vector3Int n, bool first, bool last)
        {
            bool side = x == 0 || x == n.x - 1, nose = first && z == n.z - 1, tail = last && z == 0;
            bool end = z == 0 || z == n.z - 1;
            int top = n.y - 1, across = Mathf.Min(x, n.x - 1 - x); // blocks in from the nearer side
            int low = n.y / 2, high = top - 5, band = n.y / 4;
            bool glass = y >= low && y <= high, stripe = y == band || y == band + 1;

            // underneath: set in, with gaps between the wheels
            if (y < 2)
            {
                if (across < 1) return 0;
                int wheel = z % Mathf.Max(8, n.z / 2);
                return y == 1 || (wheel >= 2 && wheel <= 5) ? Blue : (byte)0;
            }
            // the roof: rounded along its edges
            if (y == top && across < 2) return 0;
            if (y == top - 1 && across < 1) return 0;
            if (y >= top - 2) return Blue;
            if (!side && !end) return Grey; // (hidden inside)

            if (nose || tail)
            {
                if (across >= 1 && across <= 3 && y >= 2 && y <= 4) return across == 2 && y == 3 ? Grey : Blue; // round the lamps
                if (y == top - 4 && across >= n.x / 3) return Blue; // the destination sign
                if (glass && y < high && across >= 2) return across >= (n.x - 1) / 2 ? Grey : Blue; // the windscreen, split down the middle
                return stripe ? Blue : Grey;
            }
            if (end) return Grey;

            // the sides: a row of windows, and the stripe running along under them
            int bay = Mathf.Max(4, n.z / 4), m = z % bay;
            return stripe || (glass && m >= 1 && m <= bay - 2) ? Blue : Grey;
        }
    }
}
