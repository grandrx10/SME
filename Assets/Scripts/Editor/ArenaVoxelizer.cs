using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter.EditorTools
{
    /// <summary>
    /// Turns the meshes of an arena (ProBuilder shapes or any MeshRenderer) into 0.25 m voxels.
    /// For every column of cells it casts a vertical ray through the triangles and fills between entry and exit
    /// hits (so closed shapes come out solid); a lone surface (a plane) becomes a one-voxel-thick sheet.
    /// Colour comes from the material (base colour × average texture colour) and vertex colours, if any.
    /// </summary>
    public static class ArenaVoxelizer
    {
        struct Tri
        {
            public Vector3 a, b, c;
            public ushort material;
        }

        struct Hit
        {
            public float y;
            public ushort material;
        }

        public static VoxelArenaData Voxelize(Arena arena, out string report)
        {
            var data = new VoxelArenaData();
            Vector3 origin = arena.transform.position;
            float S = VoxelWorld.Size;
            var texCache = new Dictionary<Material, Color>();
            int meshes = 0, tris = 0;

            foreach (var mf in Arena.VoxelSources(arena.transform))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || !mesh.isReadable && !Application.isEditor) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                bool indestructible = mf.GetComponentInParent<Indestructible>(true) != null;
                var mats = mr.sharedMaterials;
                Matrix4x4 m = mf.transform.localToWorldMatrix;
                var verts = mesh.vertices;
                var colors = mesh.colors;
                bool hasVertexColors = colors != null && colors.Length == verts.Length;
                for (int i = 0; i < verts.Length; i++) verts[i] = m.MultiplyPoint3x4(verts[i]);

                var list = new List<Tri>();
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                    Color baseCol = MaterialColor(sub < mats.Length ? mats[sub] : null, texCache);
                    var idx = mesh.GetTriangles(sub);
                    for (int t = 0; t + 2 < idx.Length; t += 3)
                    {
                        Color col = baseCol;
                        if (hasVertexColors)
                        {
                            Color vc = (colors[idx[t]] + colors[idx[t + 1]] + colors[idx[t + 2]]) / 3f;
                            col.r *= vc.r; col.g *= vc.g; col.b *= vc.b; // alpha stays the material's (glass or not)
                        }
                        list.Add(new Tri { a = verts[idx[t]], b = verts[idx[t + 1]], c = verts[idx[t + 2]], material = data.AddMaterial(Quantize(col), indestructible) });
                    }
                }
                if (list.Count == 0) continue;
                meshes++;
                tris += list.Count;
                FillMesh(data, list, origin, S);
            }

            report = $"{meshes} mesh(es), {tris} triangles → {data.CellCount:N0} voxels, {data.palette.Count - 1} colour(s), {data.chunks.Count} chunk(s)";
            return data;
        }

        static void FillMesh(VoxelArenaData data, List<Tri> tris, Vector3 origin, float S)
        {
            // bin the triangles by the cell columns their XZ footprint covers
            var bins = new Dictionary<long, List<int>>();
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                float minX = Mathf.Min(t.a.x, Mathf.Min(t.b.x, t.c.x)), maxX = Mathf.Max(t.a.x, Mathf.Max(t.b.x, t.c.x));
                float minZ = Mathf.Min(t.a.z, Mathf.Min(t.b.z, t.c.z)), maxZ = Mathf.Max(t.a.z, Mathf.Max(t.b.z, t.c.z));
                int x0 = Mathf.CeilToInt((minX - origin.x) / S - 0.5f), x1 = Mathf.FloorToInt((maxX - origin.x) / S - 0.5f);
                int z0 = Mathf.CeilToInt((minZ - origin.z) / S - 0.5f), z1 = Mathf.FloorToInt((maxZ - origin.z) / S - 0.5f);
                for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    long key = Key(x, z);
                    if (!bins.TryGetValue(key, out var l)) bins[key] = l = new List<int>();
                    l.Add(i);
                }
            }

            var hits = new List<Hit>();
            foreach (var kv in bins)
            {
                int cx = (int)(kv.Key >> 32), cz = (int)(kv.Key & 0xffffffff);
                // nudge off exact edges so shared edges don't count twice
                float px = origin.x + (cx + 0.5f) * S + 1.37e-4f, pz = origin.z + (cz + 0.5f) * S + 2.91e-4f;
                hits.Clear();
                foreach (int i in kv.Value)
                {
                    var t = tris[i];
                    if (VerticalHit(t, px, pz, out float y)) hits.Add(new Hit { y = y, material = t.material });
                }
                if (hits.Count == 0) continue;
                hits.Sort((a, b) => b.y.CompareTo(a.y)); // top first
                // drop near-duplicate hits (coincident faces)
                for (int i = hits.Count - 1; i > 0; i--)
                    if (Mathf.Abs(hits[i].y - hits[i - 1].y) < 1e-4f) hits.RemoveAt(i);

                int k = 0;
                for (; k + 1 < hits.Count; k += 2) FillSpan(data, cx, cz, hits[k + 1].y, hits[k].y, hits[k].material, origin.y, S);
                if (k < hits.Count) FillSpan(data, cx, cz, hits[k].y, hits[k].y, hits[k].material, origin.y, S); // open surface
            }
        }

        static void FillSpan(VoxelArenaData data, int x, int z, float bottom, float top, ushort material, float oy, float S)
        {
            int y0 = Mathf.CeilToInt((bottom - oy) / S - 0.5f), y1 = Mathf.FloorToInt((top - oy) / S - 0.5f);
            if (y1 < y0) y0 = y1 = Mathf.FloorToInt(((bottom + top) * 0.5f - oy) / S); // thinner than a voxel: one cell
            for (int y = y0; y <= y1; y++)
                if (data.Get(x, y, z) == 0) data.Set(x, y, z, material);
        }

        /// <summary>Where a vertical line at (px, pz) crosses the triangle, if it does.</summary>
        static bool VerticalHit(Tri t, float px, float pz, out float y)
        {
            y = 0;
            float d = (t.b.z - t.c.z) * (t.a.x - t.c.x) + (t.c.x - t.b.x) * (t.a.z - t.c.z);
            if (Mathf.Abs(d) < 1e-9f) return false; // vertical triangle: no area seen from above
            float u = ((t.b.z - t.c.z) * (px - t.c.x) + (t.c.x - t.b.x) * (pz - t.c.z)) / d;
            float v = ((t.c.z - t.a.z) * (px - t.c.x) + (t.a.x - t.c.x) * (pz - t.c.z)) / d;
            float w = 1f - u - v;
            if (u < 0f || v < 0f || w < 0f) return false;
            y = u * t.a.y + v * t.b.y + w * t.c.y;
            return true;
        }

        static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        /// <summary>Alpha 255 = solid; anything less is a glass voxel (see <see cref="VoxelMesher.IsGlass"/>).</summary>
        static Color32 Quantize(Color c)
        {
            byte Q(float f) => (byte)(Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(f) * 255f / 4f) * 4, 0, 255));
            byte a = c.a >= 1f ? (byte)255 : (byte)Mathf.Min(Q(c.a), 252);
            return new Color32(Q(c.r), Q(c.g), Q(c.b), a);
        }

        /// <summary>Glass face opacity when the material doesn't give one (an opaque-alpha glass shader).</summary>
        const float DefaultGlassAlpha = 0.25f;

        /// <summary>
        /// Material colour × the average colour of its main texture. Alpha is 1 for solid materials; for glass (a shader
        /// with "Glass" in its name, a URP Transparent surface, or a transparent render queue) it is the face opacity, under 1.
        /// </summary>
        static Color MaterialColor(Material mat, Dictionary<Material, Color> cache)
        {
            if (mat == null) return new Color(0.75f, 0.75f, 0.75f);
            if (cache.TryGetValue(mat, out var c)) return c;
            Color col = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
            float alpha = col.a;
            Texture tex = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            if (tex != null) col *= AverageColor(tex);
            bool glass = mat.shader != null && mat.shader.name.IndexOf("glass", System.StringComparison.OrdinalIgnoreCase) >= 0
                         || mat.HasProperty("_Surface") && mat.GetFloat("_Surface") > 0.5f
                         || mat.renderQueue > 2500;
            col.a = glass ? Mathf.Clamp(alpha < 0.99f ? alpha : DefaultGlassAlpha, 0.05f, 0.95f) : 1f;
            cache[mat] = col;
            return col;
        }

        static Color AverageColor(Texture tex)
        {
            var rt = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var read = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, 16, 16), 0, 0);
            read.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Color sum = Color.black;
            foreach (var p in read.GetPixels()) sum += p;
            Object.DestroyImmediate(read);
            return sum / 256f;
        }
    }
}
