using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter.EditorTools
{
    /// <summary>
    /// Closes the flat holes of a mesh so the voxelizer can fill it (ProBuilder stairs have no bottom face).
    /// Only pieces seen from outside are closed, and only when all their holes are flat: planes, shells seen from
    /// inside and ragged surfaces are left open.
    /// </summary>
    static class OpenMeshCaps
    {
        public struct Cap
        {
            public Vector3 a, b, c;
            /// <summary>A triangle next to the hole (to take the material from).</summary>
            public int tri;
            /// <summary>Which hole it closes, from 1. Triangles of one cap may overlap: an even number of them is a gap.</summary>
            public int hole;
        }

        struct Edge
        {
            public int from, to, tri;
        }

        const float Weld = 1e-3f, Flat = 2e-3f;

        /// <param name="corners">Three corners per triangle, in metres.</param>
        /// <param name="mirrored">The corners were mirrored (negative scale), so the winding is flipped.</param>
        public static List<Cap> Find(List<Vector3> corners, bool mirrored)
        {
            var caps = new List<Cap>();
            var points = new List<Vector3>();
            int[] v = WeldCorners(corners, points);
            int triCount = corners.Count / 3;

            // the separate pieces of the mesh
            var piece = new int[points.Count];
            for (int i = 0; i < piece.Length; i++) piece[i] = i;
            for (int t = 0; t < triCount; t++)
            {
                Join(piece, v[t * 3], v[t * 3 + 1]);
                Join(piece, v[t * 3], v[t * 3 + 2]);
            }

            var edges = new List<Edge>();
            for (int t = 0; t < triCount; t++)
            for (int e = 0; e < 3; e++)
                edges.Add(new Edge { from = v[t * 3 + e], to = v[t * 3 + (e + 1) % 3], tri = t });
            var open = OpenOnly(edges);
            if (open.Count == 0) return caps;
            // an edge lying along several shorter ones isn't a hole: split it where they meet it
            open = OpenOnly(Split(open, points));

            var byPiece = new Dictionary<int, List<Edge>>();
            foreach (var e in open)
            {
                int p = Root(piece, e.from);
                if (!byPiece.TryGetValue(p, out var l)) byPiece[p] = l = new List<Edge>();
                l.Add(e);
            }

            int hole = 0;
            foreach (var kv in byPiece)
            {
                // the loops round its holes
                var loopOf = new Dictionary<int, int>();
                foreach (var e in kv.Value) { loopOf[e.from] = e.from; loopOf[e.to] = e.to; }
                foreach (var e in kv.Value) Join(loopOf, e.from, e.to);
                var loops = new Dictionary<int, List<Edge>>();
                foreach (var e in kv.Value)
                {
                    int r = Root(loopOf, e.from);
                    if (!loops.TryGetValue(r, out var l)) loops[r] = l = new List<Edge>();
                    l.Add(e);
                }

                var made = new List<Cap>();
                bool closed = true;
                foreach (var loop in loops.Values)
                {
                    if (!IsFlatLoop(loop, points)) { closed = false; break; }
                    hole++;
                    Vector3 apex = points[loop[0].from];
                    foreach (var e in loop) // a fan, wound against the open edges so it faces out like the rest
                        made.Add(new Cap { a = apex, b = points[e.to], c = points[e.from], tri = e.tri, hole = hole });
                }
                if (!closed) continue;

                // seen from outside: the closed piece's volume is positive (0 for a plane, negative for a shell seen from inside)
                Vector3 origin = points[kv.Key];
                double volume = 0;
                for (int t = 0; t < triCount; t++)
                    if (Root(piece, v[t * 3]) == kv.Key)
                        volume += SixVolume(points[v[t * 3]] - origin, points[v[t * 3 + 1]] - origin, points[v[t * 3 + 2]] - origin);
                foreach (var c in made) volume += SixVolume(c.a - origin, c.b - origin, c.c - origin);
                if ((mirrored ? -volume : volume) > 1e-4) caps.AddRange(made);
            }
            return caps;
        }

        static double SixVolume(Vector3 a, Vector3 b, Vector3 c) => Vector3.Dot(a, Vector3.Cross(b, c));

        /// <summary>The edges with an odd number of triangles on them (one of each).</summary>
        static List<Edge> OpenOnly(List<Edge> edges)
        {
            var odd = new Dictionary<long, Edge>();
            foreach (var e in edges)
            {
                if (e.from == e.to) continue;
                long key = e.from < e.to ? ((long)e.from << 32) | (uint)e.to : ((long)e.to << 32) | (uint)e.from;
                if (!odd.Remove(key)) odd[key] = e;
            }
            return new List<Edge>(odd.Values);
        }

        static List<Edge> Split(List<Edge> edges, List<Vector3> points)
        {
            var ends = new HashSet<int>();
            foreach (var e in edges) { ends.Add(e.from); ends.Add(e.to); }
            var result = new List<Edge>();
            var on = new List<(float t, int p)>();
            foreach (var e in edges)
            {
                Vector3 a = points[e.from], d = points[e.to] - a;
                float len = d.magnitude;
                d /= len;
                on.Clear();
                foreach (int p in ends)
                {
                    if (p == e.from || p == e.to) continue;
                    Vector3 to = points[p] - a;
                    float t = Vector3.Dot(to, d);
                    if (t > Weld && t < len - Weld && (to - d * t).sqrMagnitude < Weld * Weld) on.Add((t, p));
                }
                on.Sort((x, y) => x.t.CompareTo(y.t));
                int from = e.from;
                foreach (var (_, p) in on)
                {
                    result.Add(new Edge { from = from, to = p, tri = e.tri });
                    from = p;
                }
                result.Add(new Edge { from = from, to = e.to, tri = e.tri });
            }
            return result;
        }

        /// <summary>Closed (every corner has an even number of edges) and all in one plane.</summary>
        static bool IsFlatLoop(List<Edge> loop, List<Vector3> points)
        {
            var odd = new HashSet<int>();
            foreach (var e in loop)
            {
                if (!odd.Remove(e.from)) odd.Add(e.from);
                if (!odd.Remove(e.to)) odd.Add(e.to);
            }
            if (odd.Count > 0) return false;

            // the plane: the furthest corner, then the one furthest from that line
            Vector3 o = points[loop[0].from], far = o;
            foreach (var e in loop) if ((points[e.from] - o).sqrMagnitude > (far - o).sqrMagnitude) far = points[e.from];
            Vector3 dir = (far - o).normalized, normal = Vector3.zero;
            foreach (var e in loop)
            {
                Vector3 n = Vector3.Cross(dir, points[e.from] - o);
                if (n.sqrMagnitude > normal.sqrMagnitude) normal = n;
            }
            if (normal.magnitude < Weld) return false;
            normal.Normalize();
            foreach (var e in loop) if (Mathf.Abs(Vector3.Dot(points[e.from] - o, normal)) > Flat) return false;
            return true;
        }

        /// <summary>Point index of every corner, corners closer than <see cref="Weld"/> sharing one.</summary>
        static int[] WeldCorners(List<Vector3> corners, List<Vector3> points)
        {
            var grid = new Dictionary<Vector3Int, List<int>>();
            var index = new int[corners.Count];
            for (int i = 0; i < corners.Count; i++)
            {
                Vector3 p = corners[i];
                var cell = new Vector3Int(Mathf.FloorToInt(p.x / Weld), Mathf.FloorToInt(p.y / Weld), Mathf.FloorToInt(p.z / Weld));
                int found = -1;
                for (int x = -1; x <= 1 && found < 0; x++)
                for (int y = -1; y <= 1 && found < 0; y++)
                for (int z = -1; z <= 1 && found < 0; z++)
                {
                    if (!grid.TryGetValue(new Vector3Int(cell.x + x, cell.y + y, cell.z + z), out var near)) continue;
                    foreach (int n in near)
                        if ((points[n] - p).sqrMagnitude < Weld * Weld) { found = n; break; }
                }
                if (found < 0)
                {
                    found = points.Count;
                    points.Add(p);
                    if (!grid.TryGetValue(cell, out var l)) grid[cell] = l = new List<int>();
                    l.Add(found);
                }
                index[i] = found;
            }
            return index;
        }

        static int Root(IList<int> set, int i)
        {
            while (set[i] != i) i = set[i] = set[set[i]];
            return i;
        }

        static void Join(IList<int> set, int a, int b) => set[Root(set, a)] = Root(set, b);

        static int Root(Dictionary<int, int> set, int i)
        {
            while (set[i] != i) i = set[i] = set[set[i]];
            return i;
        }

        static void Join(Dictionary<int, int> set, int a, int b) => set[Root(set, a)] = Root(set, b);
    }
}
