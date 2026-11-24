using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Breaks a dead body's ragdoll into many solid pieces (killed by Shatter Hex): its mesh, as it's posed right now, is cut
    /// again and again by planes through its biggest piece, every cut capped with the same toon flesh as
    /// <see cref="BodySlicer"/>'s. Each piece is a rigid body of its own, flung out from the middle.
    /// </summary>
    public static class BodyShatter
    {
        const int Pieces = 24;
        const float Smallest = 0.14f; // pieces narrower than this aren't cut again (m)

        /// <summary>A bit of the body: three vertices a triangle, and which material each triangle has.</summary>
        class Chunk
        {
            public readonly List<Vector3> v = new();
            public readonly List<int> sub = new();
            public Transform t;
            public Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;

            public void Add(Vector3 a, Vector3 b, Vector3 c, int s)
            {
                v.Add(a); v.Add(b); v.Add(c);
                sub.Add(s);
                min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
                max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
            }

            public Vector3 Center => (min + max) * 0.5f;
            public float Size => (max - min).magnitude;
        }

        /// <summary>
        /// Everyone: replaces <paramref name="whole"/> (a ragdoll from RagdollFactory) with its pieces, thrown along
        /// <paramref name="velocity"/>. Null if it couldn't (then the body is untouched).
        /// </summary>
        public static GameObject Shatter(GameObject whole, Vector3 velocity)
        {
            if (whole == null) return null;
            GameObject root = null;
            SlicedRemains remains = null;
            var all = new List<Chunk>();
            foreach (var smr in whole.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var src = smr.sharedMesh;
                if (src == null || !src.isReadable || !smr.enabled) continue;
                var world = BodyBorer.WorldVerts(smr);
                if (world == null) continue;
                var chunks = Break(src, world);
                if (chunks.Count == 0) continue;
                if (root == null)
                {
                    root = new GameObject(whole.name.Replace("_Ragdoll", "") + "_Shattered") { layer = whole.layer };
                    remains = root.AddComponent<SlicedRemains>();
                }
                MaterialPropertyBlock tint = null;
                if (smr.HasPropertyBlock())
                {
                    tint = new MaterialPropertyBlock();
                    smr.GetPropertyBlock(tint);
                }
                foreach (var c in chunks) Make(c, src.subMeshCount, smr.sharedMaterials, tint, root.transform, remains.meshes, whole.layer);
                all.AddRange(chunks);
            }
            if (root == null) return null;

            Vector3 middle = Vector3.zero;
            foreach (var c in all) middle += c.Center;
            middle /= all.Count;
            Chunk biggest = null;
            foreach (var c in all)
            {
                if (biggest == null || c.Size > biggest.Size) biggest = c;
                var rb = c.t.gameObject.AddComponent<Rigidbody>();
                rb.mass = 0.6f;
                rb.maxDepenetrationVelocity = 2f; // (they start out touching)
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.linearVelocity = velocity * Random.Range(0.25f, 0.75f) + ((c.Center - middle).normalized + Random.insideUnitSphere * 0.6f) * Random.Range(2.5f, 6.5f) + Vector3.up * Random.Range(0f, 3f);
                rb.angularVelocity = Random.insideUnitSphere * 14f;
            }
            biggest.t.name = "Bone"; // what stands for the body from here on (the camera, necromancers)

            // the drawn face stays on whatever piece of the head it's on
            foreach (var mr in whole.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr.name != FaceDecal.ObjectName) continue;
                Chunk on = null;
                foreach (var c in all)
                    if (on == null || (c.Center - mr.bounds.center).sqrMagnitude < (on.Center - mr.bounds.center).sqrMagnitude) on = c;
                mr.transform.SetParent(on.t, true);
            }
            // cosmetics fall off whole
            foreach (var mr in whole.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!CosmeticLook.IsPart(mr) || !mr.gameObject.activeInHierarchy) continue;
                mr.transform.SetParent(root.transform, true);
                var mesh = mr.GetComponent<MeshFilter>().sharedMesh;
                var box = mr.gameObject.AddComponent<BoxCollider>();
                box.center = mesh.bounds.center;
                box.size = mesh.bounds.size * 0.75f;
                var rb = mr.gameObject.AddComponent<Rigidbody>();
                rb.mass = 0.3f;
                rb.maxDepenetrationVelocity = 2f;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.linearVelocity = velocity * 0.5f + (mr.bounds.center - middle).normalized * Random.Range(1.5f, 3.5f) + Vector3.up * Random.Range(1f, 3f);
                rb.angularVelocity = Random.insideUnitSphere * 8f;
            }
            whole.SetActive(false);
            Object.Destroy(whole);
            return root;
        }

        static List<Chunk> Break(Mesh src, Vector3[] world)
        {
            var whole = new Chunk();
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var tris = src.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3) whole.Add(world[tris[i]], world[tris[i + 1]], world[tris[i + 2]], s);
            }
            var list = new List<Chunk>();
            if (whole.sub.Count == 0) return list;
            list.Add(whole);
            int flesh = src.subMeshCount;
            for (int tries = 0; tries < Pieces * 4 && list.Count < Pieces; tries++)
            {
                // the biggest piece, across its longest way (roughly)
                int at = 0;
                for (int i = 1; i < list.Count; i++) if (list[i].Size > list[at].Size) at = i;
                var c = list[at];
                if (c.Size < Smallest) break;
                Vector3 d = c.max - c.min;
                Vector3 axis = d.x > d.y && d.x > d.z ? Vector3.right : d.y > d.z ? Vector3.up : Vector3.forward;
                Vector3 n = (axis + Random.insideUnitSphere * 0.8f).normalized;
                Vector3 p = c.Center + Vector3.Scale(Random.insideUnitSphere, d) * 0.12f;
                var a = new Chunk();
                var b = new Chunk();
                Cut(c, p, n, flesh, a, b);
                if (a.sub.Count == 0 || b.sub.Count == 0) continue;
                list[at] = a;
                list.Add(b);
            }
            return list;
        }

        /// <summary>Splits a chunk along a plane: <paramref name="up"/> is the side its normal points to. Both get the cut's cap.</summary>
        static void Cut(Chunk c, Vector3 point, Vector3 normal, int flesh, Chunk up, Chunk down)
        {
            var edges = new List<(Vector3 a, Vector3 b)>();
            for (int i = 0; i < c.sub.Count; i++)
            {
                Vector3 a = c.v[i * 3], b = c.v[i * 3 + 1], d = c.v[i * 3 + 2];
                float sa = Vector3.Dot(a - point, normal), sb = Vector3.Dot(b - point, normal), sd = Vector3.Dot(d - point, normal);
                bool ua = sa >= 0f, ub = sb >= 0f, ud = sd >= 0f;
                int s = c.sub[i];
                if (ua == ub && ub == ud) { (ua ? up : down).Add(a, b, d, s); continue; }
                // turned so 'a' is the one alone on its side (keeps the winding)
                if (ua == ub) { (a, b, d) = (d, a, b); (sa, sb, sd) = (sd, sa, sb); ua = ud; }
                else if (ua == ud) { (a, b, d) = (b, d, a); (sa, sb, sd) = (sb, sd, sa); ua = ub; }
                Vector3 ab = Vector3.Lerp(a, b, sa / (sa - sb)), ad = Vector3.Lerp(a, d, sa / (sa - sd));
                var alone = ua ? up : down;
                var pair = ua ? down : up;
                alone.Add(a, ab, ad, s);
                pair.Add(ab, b, d, s);
                pair.Add(ab, d, ad, s);
                edges.Add((ab, ad));
            }
            if (up.sub.Count == 0 || down.sub.Count == 0) return;
            Cap(edges, normal, flesh, up, down);
        }

        /// <summary>Fills the cut: each separate outline (a limb, the trunk) with a fan from its own middle.</summary>
        static void Cap(List<(Vector3 a, Vector3 b)> edges, Vector3 normal, int flesh, Chunk up, Chunk down)
        {
            const float q = 2000f;
            (int, int, int) Key(Vector3 p) => (Mathf.RoundToInt(p.x * q), Mathf.RoundToInt(p.y * q), Mathf.RoundToInt(p.z * q));
            var ids = new Dictionary<(int, int, int), int>();
            var parent = new List<int>();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            int Id(Vector3 p)
            {
                var k = Key(p);
                if (!ids.TryGetValue(k, out int i)) { ids[k] = i = parent.Count; parent.Add(i); }
                return i;
            }
            var of = new int[edges.Count];
            for (int i = 0; i < edges.Count; i++)
            {
                int a = Find(Id(edges[i].a)), b = Find(Id(edges[i].b));
                parent[a] = b;
                of[i] = b;
            }
            var sum = new Dictionary<int, (Vector3 p, int n)>();
            for (int i = 0; i < edges.Count; i++)
            {
                int r = of[i] = Find(of[i]);
                sum.TryGetValue(r, out var m);
                sum[r] = (m.p + edges[i].a + edges[i].b, m.n + 2);
            }
            for (int i = 0; i < edges.Count; i++)
            {
                var m = sum[of[i]];
                Vector3 mid = m.p / m.n;
                var (a, b) = edges[i];
                // facing out of the upper piece (down, into the cut); the other way round for the lower one
                if (Vector3.Dot(Vector3.Cross(a - mid, b - mid), normal) > 0f) (a, b) = (b, a);
                up.Add(mid, a, b, flesh);
                down.Add(mid, b, a, flesh);
            }
        }

        static void Make(Chunk c, int bodySubs, Material[] mats, MaterialPropertyBlock tint, Transform parent, List<Mesh> made, int layer)
        {
            Vector3 mid = c.Center;
            var go = new GameObject("Piece") { layer = layer };
            go.transform.SetParent(parent, false);
            go.transform.position = mid;
            c.t = go.transform;

            // only the materials this piece has any of
            var used = new List<int>();
            for (int s = 0; s <= bodySubs; s++) if (c.sub.Contains(s)) used.Add(s);
            var mesh = new Mesh { name = "Shattered Piece" };
            if (c.v.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            var verts = new List<Vector3>(c.v.Count);
            foreach (var p in c.v) verts.Add(p - mid);
            mesh.SetVertices(verts);
            mesh.subMeshCount = used.Count;
            for (int k = 0; k < used.Count; k++)
            {
                var tris = new List<int>();
                for (int i = 0; i < c.sub.Count; i++)
                    if (c.sub[i] == used[k]) { tris.Add(i * 3); tris.Add(i * 3 + 1); tris.Add(i * 3 + 2); }
                mesh.SetTriangles(tris, k);
            }
            mesh.RecalculateNormals(); // (no shared vertices: flat facets)
            mesh.RecalculateBounds();
            made.Add(mesh);

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            var list = new Material[used.Count];
            for (int k = 0; k < used.Count; k++)
                list[k] = used[k] < bodySubs && mats.Length > 0 ? mats[Mathf.Min(used[k], mats.Length - 1)] : BodySlicer.FleshMaterial;
            r.sharedMaterials = list;
            if (tint != null)
                for (int k = 0; k < used.Count; k++)
                    if (used[k] < bodySubs) r.SetPropertyBlock(tint, k);
            var box = go.AddComponent<BoxCollider>();
            box.center = mesh.bounds.center;
            box.size = Vector3.Max(mesh.bounds.size * 0.75f, Vector3.one * 0.03f);
        }
    }
}
