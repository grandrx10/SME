using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Greedy mesher: merges coplanar faces of the same voxel material into big quads, skipping faces hidden
    /// by neighbours (including neighbours outside the box, so chunks join seamlessly). Vertex colours carry the
    /// material colour; the voxel shader adds per-voxel shading.
    /// </summary>
    public static class VoxelMesher
    {
        static readonly List<Vector3> Verts = new();
        static readonly List<Vector3> Norms = new();
        static readonly List<Color32> Cols = new();
        static readonly List<int> Tris = new();
        static int[] _mask = new int[0];

        /// <param name="get">Voxel at local cell (may be asked for cells one outside the box).</param>
        public static void Build(Mesh mesh, Func<int, int, int, ushort> get, int sx, int sy, int sz,
            Func<ushort, Color32> color, float size, Vector3 offset = default)
        {
            Verts.Clear(); Norms.Clear(); Cols.Clear(); Tris.Clear();
            int[] dims = { sx, sy, sz };
            var x = new int[3];
            var q = new int[3];

            for (int d = 0; d < 3; d++)
            {
                int u = (d + 1) % 3, v = (d + 2) % 3;
                int du = dims[u], dv = dims[v];
                if (_mask.Length < du * dv) _mask = new int[du * dv];
                q[0] = q[1] = q[2] = 0;
                q[d] = 1;

                for (x[d] = -1; x[d] < dims[d];)
                {
                    // ---- mask of visible faces between slice x[d] and x[d]+1 ----
                    int n = 0;
                    for (x[v] = 0; x[v] < dv; x[v]++)
                    for (x[u] = 0; x[u] < du; x[u]++, n++)
                    {
                        ushort a = get(x[0], x[1], x[2]);
                        ushort b = get(x[0] + q[0], x[1] + q[1], x[2] + q[2]);
                        bool aIn = x[d] >= 0, bIn = x[d] + 1 < dims[d];
                        if (a != 0 && b == 0 && aIn) _mask[n] = a;          // face pointing +d
                        else if (a == 0 && b != 0 && bIn) _mask[n] = -b;    // face pointing -d
                        else _mask[n] = 0;
                    }
                    x[d]++;

                    // ---- greedy merge ----
                    n = 0;
                    for (int j = 0; j < dv; j++)
                    for (int i = 0; i < du;)
                    {
                        int c = _mask[n];
                        if (c == 0) { i++; n++; continue; }
                        int w = 1;
                        while (i + w < du && _mask[n + w] == c) w++;
                        int h = 1;
                        bool done = false;
                        for (; j + h < dv; h++)
                        {
                            for (int k = 0; k < w; k++)
                                if (_mask[n + k + h * du] != c) { done = true; break; }
                            if (done) break;
                        }

                        x[u] = i; x[v] = j;
                        var duv = new int[3]; duv[u] = w;
                        var dvv = new int[3]; dvv[v] = h;
                        Vector3 p0 = new(x[0], x[1], x[2]);
                        Vector3 pu = new(duv[0], duv[1], duv[2]);
                        Vector3 pv = new(dvv[0], dvv[1], dvv[2]);
                        Vector3 normal = Vector3.zero;
                        normal[d] = c > 0 ? 1 : -1;
                        Quad(p0 * size + offset, (p0 + pu) * size + offset, (p0 + pu + pv) * size + offset, (p0 + pv) * size + offset,
                            normal, color((ushort)Math.Abs(c)));

                        for (int l = 0; l < h; l++)
                        for (int k = 0; k < w; k++)
                            _mask[n + k + l * du] = 0;
                        i += w;
                        n += w;
                    }
                }
            }

            mesh.Clear();
            mesh.SetVertices(Verts);
            mesh.SetNormals(Norms);
            mesh.SetColors(Cols);
            mesh.SetTriangles(Tris, 0);
            mesh.RecalculateBounds();
        }

        static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color32 col)
        {
            int i = Verts.Count;
            Verts.Add(a); Verts.Add(b); Verts.Add(c); Verts.Add(d);
            for (int k = 0; k < 4; k++) { Norms.Add(normal); Cols.Add(col); }
            // Unity front faces: Cross(b - a, c - a) points along the normal
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) > 0f)
            {
                Tris.Add(i); Tris.Add(i + 1); Tris.Add(i + 2);
                Tris.Add(i); Tris.Add(i + 2); Tris.Add(i + 3);
            }
            else
            {
                Tris.Add(i); Tris.Add(i + 2); Tris.Add(i + 1);
                Tris.Add(i); Tris.Add(i + 3); Tris.Add(i + 2);
            }
        }

        /// <summary>Meshes a free-standing set of cells (debris, moving slabs) around a local origin cell.</summary>
        public static void BuildCells(Mesh mesh, IReadOnlyList<VoxelWorld.RemovedCell> cells, Vector3Int originCell,
            Func<ushort, Color32> color, float size, out Vector3Int min, out Vector3Int max)
        {
            min = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
            max = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
            foreach (var c in cells) { min = Vector3Int.Min(min, c.cell); max = Vector3Int.Max(max, c.cell); }
            if (cells.Count == 0) { mesh.Clear(); min = max = originCell; return; }
            var dims = max - min + Vector3Int.one;
            var grid = new ushort[dims.x * dims.y * dims.z];
            foreach (var c in cells)
            {
                var l = c.cell - min;
                grid[l.x + dims.x * (l.y + dims.y * l.z)] = c.value;
            }
            var m = min;
            Build(mesh, (x, y, z) =>
                    x < 0 || y < 0 || z < 0 || x >= dims.x || y >= dims.y || z >= dims.z ? (ushort)0 : grid[x + dims.x * (y + dims.y * z)],
                dims.x, dims.y, dims.z, color, size, (Vector3)(m - originCell) * size);
        }
    }
}
