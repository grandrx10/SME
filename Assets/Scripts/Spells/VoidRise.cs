using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Arcane Judgment's erased ground: the layer the beam takes turns black where it lay, then breaks into
    /// clumps that float up (slowly turning, picking up speed) and shrink away to nothing. Black voxels with a
    /// glowing arcane-blue rim. Visual only — the cells are already gone from the world.
    /// </summary>
    public class VoidRise : MonoBehaviour
    {
        static readonly Color Black = new(0.03f, 0.03f, 0.05f, 1f);
        const int ClumpCells = 4;          // clumps are up to 4x4x4 cells (1 m)
        const float Hold = 0.15f, Life = 1.4f;

        float _age, _rise, _spin, _delay;
        Vector3 _axis, _start;

        static readonly Dictionary<Vector3Int, List<Vector3Int>> Groups = new();

        public static void Spawn(VoxelWorld w, List<VoxelWorld.RemovedCell> removed) => Spawn(w, removed, Glow.Arcane);

        /// <param name="rim">The colour the clumps' edges glow.</param>
        /// <param name="most">No more clumps than this (the rest are simply gone).</param>
        public static void Spawn(VoxelWorld w, List<VoxelWorld.RemovedCell> removed, Color rim, int most = int.MaxValue)
        {
            Groups.Clear();
            foreach (var rc in removed)
            {
                var c = rc.cell;
                var key = new Vector3Int(Mathf.FloorToInt(c.x / (float)ClumpCells), Mathf.FloorToInt(c.y / (float)ClumpCells), Mathf.FloorToInt(c.z / (float)ClumpCells));
                if (!Groups.TryGetValue(key, out var list)) Groups[key] = list = new List<Vector3Int>();
                list.Add(c);
            }
            int left = Groups.Count, room = Mathf.Min(most, left);
            foreach (var g in Groups.Values)
            {
                // (an even pick of them)
                if (Random.value * left < room) { Make(w, g, rim); room--; }
                left--;
            }
        }

        static void Make(VoxelWorld w, List<Vector3Int> cells, Color rimColour)
        {
            Vector3 center = Vector3.zero;
            foreach (var c in cells) center += w.CellCenter(c);
            center /= cells.Count;

            var go = new GameObject("VoidClump") { layer = Layers.Fx };
            go.transform.position = center;
            var mesh = BuildMesh(w, cells, center);
            var t = Glow.Piece(go.transform, mesh, Black, "Clump");
            // the rim glows (arcane blue; mahogany for Judgement) instead of ink
            var rim = t.Find("Outline")?.GetComponent<MeshRenderer>();
            if (rim != null) Glow.Tint(rim, rimColour);
            var v = go.AddComponent<VoidRise>();
            v._start = center;
            v._rise = Random.Range(1.2f, 2.4f);
            v._spin = Random.Range(-60f, 60f);
            v._axis = Random.onUnitSphere;
            v._delay = Random.Range(0f, 0.12f);
            v._mesh = mesh;
        }

        Mesh _mesh;

        static readonly Vector3Int[] Dirs = { Vector3Int.right, Vector3Int.left, Vector3Int.up, Vector3Int.down, new(0, 0, 1), new(0, 0, -1) };
        static readonly HashSet<Vector3Int> Set = new();

        /// <summary>The clump's outer faces (shared faces skipped), around <paramref name="center"/>.</summary>
        static Mesh BuildMesh(VoxelWorld w, List<Vector3Int> cells, Vector3 center)
        {
            Set.Clear();
            foreach (var c in cells) Set.Add(c);
            var v = new List<Vector3>();
            var tri = new List<int>();
            float h = VoxelWorld.Size * 0.5f;
            foreach (var c in cells)
            {
                Vector3 p = w.CellCenter(c) - center;
                foreach (var d in Dirs)
                {
                    if (Set.Contains(c + d)) continue;
                    Vector3 n = d;
                    Vector3 a = Mathf.Abs(n.y) > 0.5f ? Vector3.right : Vector3.up;
                    Vector3 b = Vector3.Cross(n, a);
                    int k = v.Count;
                    Vector3 f = p + n * h;
                    v.Add(f + (-a - b) * h);
                    v.Add(f + (a - b) * h);
                    v.Add(f + (a + b) * h);
                    v.Add(f + (-a + b) * h);
                    tri.Add(k); tri.Add(k + 1); tri.Add(k + 2);
                    tri.Add(k); tri.Add(k + 2); tri.Add(k + 3);
                }
            }
            // (winding: cross(2a, 2a+2b) = 4·(a × (n × a)) = 4n, so every face already points outward)
            var m = new Mesh { name = "Void Clump", indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            m.SetVertices(v);
            m.SetTriangles(tri, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        void Update()
        {
            _age += Time.deltaTime;
            float t = _age - _delay;
            if (t >= Life) { Destroy(gameObject); return; }
            if (t <= Hold) return; // black, still lying where the ground was
            float u = t - Hold;
            // lifts off slowly, then speeds up
            transform.position = _start + Vector3.up * (_rise * u * u * 1.4f + 0.3f * u);
            transform.rotation = Quaternion.AngleAxis(_spin * u, _axis);
            float shrink = 1f - Mathf.Clamp01((t - Life * 0.45f) / (Life * 0.55f));
            transform.localScale = Vector3.one * Mathf.Max(0.001f, shrink * shrink * (3f - 2f * shrink) * 1f);
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
