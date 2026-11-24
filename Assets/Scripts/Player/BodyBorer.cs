using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Bores a round tunnel straight through a dead body's ragdoll (killed by the fused Attraction + Repulsion ball):
    /// every bit of the skinned mesh inside the cylinder the ball swept is gone, its edge clipped cleanly to the
    /// cylinder. The inside of the body's shell shows through the hole as flat toon "flesh" (the kept surface drawn
    /// again, facing inwards), so it reads as a solid body with a hole in it. Bones inside the tunnel become
    /// weightless, collisionless ghosts and let go of whatever hangs off them, so the pieces either side fall apart.
    /// </summary>
    public static class BodyBorer
    {
        /// <summary>
        /// Everyone: removes the cylinder of radius <paramref name="radius"/> round the line through
        /// <paramref name="point"/> along <paramref name="axis"/> from <paramref name="body"/> (a ragdoll from
        /// RagdollFactory). Returns false if it couldn't (then the body is untouched).
        /// </summary>
        public static bool Bore(GameObject body, Vector3 point, Vector3 axis, float radius)
        {
            if (body == null || radius <= 0f || axis.sqrMagnitude < 1e-6f) return false;
            axis.Normalize();
            float Dist(Vector3 p) => Vector3.ProjectOnPlane(p - point, axis).magnitude - radius; // > 0: kept

            var renderers = body.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var remains = body.GetComponent<BoredRemains>();
            bool any = false;
            foreach (var smr in renderers)
            {
                var src = smr.sharedMesh;
                if (src == null || !src.isReadable) continue;
                var world = WorldVerts(smr);
                if (world == null) continue;
                var mesh = Cut(src, world, Dist);
                any = true;
                if (mesh == src) continue; // the tunnel missed this part
                if (mesh == null) { smr.enabled = false; continue; } // all of it was in the way
                if (remains == null) remains = body.AddComponent<BoredRemains>();
                remains.meshes.Add(mesh);

                // the inside: one more material, flat flesh, on the extra submesh
                var mats = new List<Material>(smr.sharedMaterials);
                while (mats.Count < src.subMeshCount) mats.Add(mats.Count > 0 ? mats[mats.Count - 1] : BodySlicer.FleshMaterial);
                if (mats.Count > src.subMeshCount) mats.RemoveRange(src.subMeshCount, mats.Count - src.subMeshCount);
                mats.Add(BodySlicer.FleshMaterial);
                MaterialPropertyBlock tint = null;
                if (smr.HasPropertyBlock())
                {
                    tint = new MaterialPropertyBlock();
                    smr.GetPropertyBlock(tint);
                    smr.SetPropertyBlock(null);
                }
                smr.sharedMesh = mesh;
                smr.sharedMaterials = mats.ToArray();
                if (tint != null)
                    for (int s = 0; s < src.subMeshCount; s++) smr.SetPropertyBlock(tint, s);
            }
            if (!any) return false;

            // the drawn face goes with the head, if the head's gone
            foreach (var mr in body.GetComponentsInChildren<MeshRenderer>(true))
                if (mr.name == FaceDecal.ObjectName && Dist(mr.bounds.center) < 0f) mr.enabled = false;

            // and so do the cosmetics it went through
            foreach (var mr in body.GetComponentsInChildren<MeshRenderer>(true))
                if (CosmeticLook.IsPart(mr) && Dist(mr.bounds.center) < 0f) mr.gameObject.SetActive(false);

            // bones in the tunnel: ghosts; whatever hung off one falls free
            var bodies = body.GetComponentsInChildren<Rigidbody>(true);
            var ghost = new HashSet<Rigidbody>();
            foreach (var rb in bodies)
            {
                var cap = rb.GetComponent<CapsuleCollider>();
                Vector3 centre = cap != null ? rb.transform.TransformPoint(cap.center) : rb.position;
                if (Dist(centre) >= 0f) continue;
                ghost.Add(rb);
                rb.mass = 0.2f;
                rb.linearDamping = 1f;
                rb.angularDamping = 2f;
                foreach (var col in rb.GetComponents<Collider>()) col.enabled = false;
            }
            if (ghost.Count < bodies.Length)
                foreach (var j in body.GetComponentsInChildren<Joint>(true))
                {
                    var a = j.GetComponent<Rigidbody>();
                    bool aGhost = ghost.Contains(a), bGhost = j.connectedBody != null && ghost.Contains(j.connectedBody);
                    if (aGhost != bGhost) Object.Destroy(j);
                }
            return true;
        }

        /// <summary>Where every vertex of the renderer is right now, in the world (null if it can't tell).</summary>
        internal static Vector3[] WorldVerts(SkinnedMeshRenderer smr)
        {
            var baked = new Mesh();
            smr.BakeMesh(baked, true);
            var v = baked.vertices;
            if (v.Length == 0) { Object.Destroy(baked); return null; }
            // BakeMesh only applies the renderer's own scale, not its parents' (the model is scaled down): measure
            // the baked mesh against the renderer's real bounds and correct the difference (as BodySlicer does)
            var m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (var p in v) { var w = m.MultiplyPoint3x4(p); min = Vector3.Min(min, w); max = Vector3.Max(max, w); }
            float bakedSize = (max - min).magnitude, real = smr.bounds.size.magnitude;
            float ratio = bakedSize > 1e-4f && real > 1e-4f ? real / bakedSize : 1f;
            if (ratio > 0.1f && ratio < 10f && Mathf.Abs(ratio - 1f) > 0.02f)
                m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one * ratio);
            for (int k = 0; k < v.Length; k++) v[k] = m.MultiplyPoint3x4(v[k]);
            Object.Destroy(baked);
            return v;
        }

        /// <summary>
        /// A copy of the skinned mesh with only the triangles outside the tunnel (decided where the vertices are
        /// in the world now), cut cleanly along its wall, plus an extra submesh: the kept triangles again, facing
        /// inwards (the inside of the body, seen through the hole). Null if nothing is left.
        /// </summary>
        static Mesh Cut(Mesh src, Vector3[] wp, System.Func<Vector3, float> dist)
        {
            var verts = src.vertices;
            var norms = src.normals;
            var uvs = src.uv;
            var weights = src.boneWeights;
            int n = verts.Length;
            if (wp.Length != n) return null;
            bool hasN = norms != null && norms.Length == n, hasUv = uvs != null && uvs.Length == n, hasW = weights != null && weights.Length == n;
            Vector3 N(int i) => hasN ? norms[i] : Vector3.up;
            Vector2 Uv(int i) => hasUv ? uvs[i] : Vector2.zero;
            BoneWeight W(int i) => hasW ? weights[i] : default;

            var side = new float[n];
            bool cutAny = false;
            for (int i = 0; i < n; i++) { side[i] = dist(wp[i]); if (side[i] < 0f) cutAny = true; }

            var pos = new List<Vector3>(n); var nrm = new List<Vector3>(n); var uv = new List<Vector2>(n); var bw = new List<BoneWeight>(n);
            var subs = new List<List<int>>();
            var map = new Dictionary<int, int>();
            int Add(Vector3 p, Vector3 q, Vector2 t, BoneWeight w) { pos.Add(p); nrm.Add(q); uv.Add(t); bw.Add(w); return pos.Count - 1; }
            int V(int i)
            {
                if (!map.TryGetValue(i, out var j)) { j = Add(verts[i], N(i), Uv(i), W(i)); map[i] = j; }
                return j;
            }
            // where along the edge (from kept k to dropped d) it meets the tunnel's wall: the distance to the axis
            // isn't linear along an edge, so home in on it
            int Edge(int k, int d)
            {
                float lo = 0f, hi = 1f;
                for (int it = 0; it < 10; it++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (dist(Vector3.Lerp(wp[k], wp[d], mid)) >= 0f) lo = mid; else hi = mid;
                }
                float t = lo;
                return Add(Vector3.Lerp(verts[k], verts[d], t), Vector3.Lerp(N(k), N(d), t).normalized, Vector2.Lerp(Uv(k), Uv(d), t), W(k));
            }

            if (!cutAny) return src; // untouched
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var list = new List<int>();
                subs.Add(list);
                var tris = src.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    bool ka = side[a] >= 0f, kb = side[b] >= 0f, kc = side[c] >= 0f;
                    int count = (ka ? 1 : 0) + (kb ? 1 : 0) + (kc ? 1 : 0);
                    if (count == 3) { list.Add(V(a)); list.Add(V(b)); list.Add(V(c)); continue; }
                    if (count == 0) continue;
                    // rotate so 'a' is the vertex alone on its side (keeps winding)
                    if (ka == kb) { (a, b, c) = (c, a, b); (ka, kb, kc) = (kc, ka, kb); }
                    else if (ka == kc) { (a, b, c) = (b, c, a); (ka, kb, kc) = (kb, kc, ka); }
                    if (ka)
                    {
                        list.Add(V(a)); list.Add(Edge(a, b)); list.Add(Edge(a, c));
                    }
                    else
                    {
                        int iab = Edge(b, a), iac = Edge(c, a);
                        list.Add(iab); list.Add(V(b)); list.Add(V(c));
                        list.Add(iab); list.Add(V(c)); list.Add(iac);
                    }
                }
            }
            int bodyVerts = pos.Count;
            if (bodyVerts < 3) return null;

            // the inside of the shell: every kept triangle again, reversed, with its own inward-facing vertices
            var inside = new List<int>();
            for (int i = 0; i < bodyVerts; i++) Add(pos[i], -nrm[i], Vector2.zero, bw[i]);
            foreach (var list in subs)
                for (int t = 0; t + 2 < list.Count; t += 3)
                {
                    inside.Add(list[t] + bodyVerts); inside.Add(list[t + 2] + bodyVerts); inside.Add(list[t + 1] + bodyVerts);
                }
            subs.Add(inside);

            var mesh = new Mesh { name = src.name + "_Bored" };
            if (pos.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(pos);
            mesh.SetNormals(nrm);
            mesh.SetUVs(0, uv);
            mesh.boneWeights = bw.ToArray();
            mesh.bindposes = src.bindposes;
            mesh.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) mesh.SetTriangles(subs[s], s);
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>On a bored body: frees the meshes made for it when it goes.</summary>
    public class BoredRemains : MonoBehaviour
    {
        public readonly List<Mesh> meshes = new();
        void OnDestroy()
        {
            foreach (var m in meshes) if (m != null) Destroy(m);
        }
    }
}
