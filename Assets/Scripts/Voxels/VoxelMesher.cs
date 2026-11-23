using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Greedy mesher: merges coplanar faces of the same voxel material into big quads, skipping faces hidden
    /// by neighbours (including neighbours outside the box, so chunks join seamlessly). Vertex colours carry the
    /// material colour; the voxel shader adds per-voxel shading.
    /// <para>
    /// Glass: a material whose colour alpha is under 255 (<see cref="IsGlass"/>). Glass faces go in submesh 1
    /// (drawn with SME/VoxelGlass), one quad per cell face, and only where glass meets air, so a block of glass is a
    /// single shell. Solid faces next to glass are still drawn, so you see them through it. Each glass quad's uv0
    /// runs 0..1 across the cell and uv1 flags which of its sides are edges of the glass shape, for the ink line.
    /// </para>
    /// </summary>
    public static class VoxelMesher
    {
        public const int SolidSubmesh = 0, GlassSubmesh = 1;

        static readonly List<Vector3> Verts = new();
        static readonly List<Vector3> Norms = new();
        static readonly List<Color32> Cols = new();
        static readonly List<Vector2> Uvs = new();
        static readonly List<Vector4> Edges = new();
        static readonly List<int> Tris = new();
        static readonly List<int> GlassTris = new();
        static int[] _mask = new int[0];

        public static bool IsGlass(Color32 c) => c.a < 255;

        /// <param name="get">Voxel at local cell (may be asked for cells one outside the box).</param>
        public static void Build(Mesh mesh, Func<int, int, int, ushort> get, int sx, int sy, int sz,
            Func<ushort, Color32> color, float size, Vector3 offset = default)
        {
            Verts.Clear(); Norms.Clear(); Cols.Clear(); Uvs.Clear(); Edges.Clear(); Tris.Clear(); GlassTris.Clear();
            bool Glass(ushort v) => v != 0 && IsGlass(color(v));
            bool anyGlass = false;
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
                        // solid faces only here (glass is meshed below); glass doesn't hide what's behind it
                        bool aGlass = Glass(a), bGlass = Glass(b);
                        anyGlass |= aGlass && aIn || bGlass && bIn;
                        bool aSolid = a != 0 && !aGlass, bSolid = b != 0 && !bGlass;
                        if (aSolid && !bSolid && aIn) _mask[n] = a;          // face pointing +d
                        else if (bSolid && !aSolid && bIn) _mask[n] = -b;    // face pointing -d
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
                        Vector3 p0 = new(x[0], x[1], x[2]);
                        Vector3 pu = Vector3.zero; pu[u] = w;
                        Vector3 pv = Vector3.zero; pv[v] = h;
                        Vector3 normal = Vector3.zero;
                        normal[d] = c > 0 ? 1 : -1;
                        Quad(p0 * size + offset, (p0 + pu) * size + offset, (p0 + pu + pv) * size + offset, (p0 + pv) * size + offset,
                            normal, color((ushort)Math.Abs(c)), Tris, Vector4.zero);

                        for (int l = 0; l < h; l++)
                        for (int k = 0; k < w; k++)
                            _mask[n + k + l * du] = 0;
                        i += w;
                        n += w;
                    }
                }
            }

            if (anyGlass) BuildGlass(get, sx, sy, sz, color, size, offset);

            mesh.Clear();
            mesh.SetVertices(Verts);
            mesh.SetNormals(Norms);
            mesh.SetColors(Cols);
            if (GlassTris.Count > 0)
            {
                mesh.SetUVs(0, Uvs);
                mesh.SetUVs(1, Edges);
            }
            mesh.subMeshCount = 2;
            mesh.SetTriangles(Tris, SolidSubmesh, false);
            mesh.SetTriangles(GlassTris, GlassSubmesh, false);
            mesh.RecalculateBounds();
        }

        /// <summary>One quad per glass cell face that touches air, with its edge flags.</summary>
        static void BuildGlass(Func<int, int, int, ushort> get, int sx, int sy, int sz, Func<ushort, Color32> color, float size, Vector3 offset)
        {
            bool Glass(ushort v) => v != 0 && IsGlass(color(v));
            var c = new int[3];
            for (c[2] = 0; c[2] < sz; c[2]++)
            for (c[1] = 0; c[1] < sy; c[1]++)
            for (c[0] = 0; c[0] < sx; c[0]++)
            {
                ushort v = get(c[0], c[1], c[2]);
                if (!Glass(v)) continue;
                Color32 col = color(v);
                for (int d = 0; d < 3; d++)
                for (int s = -1; s <= 1; s += 2)
                {
                    var nrm = new Vector3Int(); nrm[d] = s;
                    var cell = new Vector3Int(c[0], c[1], c[2]);
                    var out1 = cell + nrm;
                    if (get(out1.x, out1.y, out1.z) != 0) continue; // only glass against air is drawn

                    int u = (d + 1) % 3, w = (d + 2) % 3;
                    var du = new Vector3Int(); du[u] = 1;
                    var dw = new Vector3Int(); dw[w] = 1;
                    // a side is an edge of the glass shape unless the neighbour that way has the same face
                    float Edge(Vector3Int side)
                    {
                        var nb = cell + side;
                        var nbOut = nb + nrm;
                        return Glass(get(nb.x, nb.y, nb.z)) && get(nbOut.x, nbOut.y, nbOut.z) == 0 ? 0f : 1f;
                    }
                    var flags = new Vector4(Edge(-du), Edge(du), Edge(-dw), Edge(dw));

                    Vector3 p0 = cell; if (s > 0) p0[d] += 1;
                    Vector3 pu = (Vector3)du, pw = (Vector3)dw;
                    Quad(p0 * size + offset, (p0 + pu) * size + offset, (p0 + pu + pw) * size + offset, (p0 + pw) * size + offset,
                        (Vector3)nrm, col, GlassTris, flags);
                }
            }
        }

        static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color32 col, List<int> tris, Vector4 edges)
        {
            int i = Verts.Count;
            Verts.Add(a); Verts.Add(b); Verts.Add(c); Verts.Add(d);
            for (int k = 0; k < 4; k++) { Norms.Add(normal); Cols.Add(col); Edges.Add(edges); }
            Uvs.Add(new Vector2(0, 0)); Uvs.Add(new Vector2(1, 0)); Uvs.Add(new Vector2(1, 1)); Uvs.Add(new Vector2(0, 1));
            // Unity front faces: Cross(b - a, c - a) points along the normal
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) > 0f)
            {
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
            }
            else
            {
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 1);
                tris.Add(i); tris.Add(i + 3); tris.Add(i + 2);
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
