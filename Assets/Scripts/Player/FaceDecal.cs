using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Puts a face drawing on a character's head. Takes the front of the real head mesh (the triangles skinned to the
    /// head bone that face forward), lifts them a hair off the skin, projects the drawing onto them straight from the
    /// front and parents the result to the head bone — so it follows the head exactly, ragdolls included.
    /// The mesh is built once per character mesh and shared.
    /// </summary>
    public static class FaceDecal
    {
        public const string ObjectName = "FaceDecal";
        const string HeadBone = "Bone.001";
        /// <summary>How much of the head's width the drawing covers.</summary>
        const float FaceWidth = 0.95f;
        /// <summary>Gap between skin and drawing, as a fraction of the face width.</summary>
        const float Lift = 0.012f;
        /// <summary>Triangles have to face at least this much forward (dot with forward) to get the drawing.</summary>
        const float MinFacing = 0.15f;

        static readonly Dictionary<Mesh, Mesh> Cache = new();
        static Shader _shader;

        /// <summary>
        /// Adds (or finds) the face object on a character model. <paramref name="characterRoot"/> gives the way the
        /// character faces (its forward/up). Returns null if the model has no head.
        /// </summary>
        public static MeshRenderer Attach(Transform model, Transform characterRoot, Texture texture)
        {
            if (model == null) return null;
            SkinnedMeshRenderer smr = null;
            int headIndex = -1;
            foreach (var r in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bones = r.bones;
                for (int i = 0; i < bones.Length; i++)
                    if (bones[i] != null && bones[i].name == HeadBone) { smr = r; headIndex = i; break; }
                if (smr != null) break;
            }
            Transform head = smr != null ? smr.bones[headIndex] : FindDeep(model, HeadBone);
            if (head == null) return null;

            var existing = head.Find(ObjectName);
            if (existing != null && existing.TryGetComponent<MeshRenderer>(out var had))
            {
                had.sharedMaterial.mainTexture = texture;
                return had;
            }

            Mesh mesh = null;
            if (smr != null && smr.sharedMesh != null && smr.sharedMesh.isReadable && !Cache.TryGetValue(smr.sharedMesh, out mesh))
            {
                mesh = Build(smr, headIndex, characterRoot);
                Cache[smr.sharedMesh] = mesh;
            }

            var go = new GameObject(ObjectName);
            go.layer = head.gameObject.layer;
            go.transform.SetParent(head, false);
            if (mesh == null)
            {
                // no usable head mesh: a flat card in front of the head bone instead
                mesh = Quad();
                go.transform.position = head.position + characterRoot.forward * 0.17f + characterRoot.up * 0.1f;
                go.transform.rotation = Quaternion.LookRotation(-characterRoot.forward, characterRoot.up);
                go.transform.localScale = Vector3.one * (0.3f / Mathf.Max(1e-4f, head.lossyScale.x));
            }
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            if (_shader == null) _shader = Shader.Find("Sprites/Default");
            mr.sharedMaterial = new Material(_shader) { name = "Face", mainTexture = texture, renderQueue = 3000 };
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return mr;
        }

        static Mesh Build(SkinnedMeshRenderer smr, int headIndex, Transform root)
        {
            var src = smr.sharedMesh;
            var verts = src.vertices;
            var normals = src.normals;
            var weights = src.boneWeights;
            var bindposes = src.bindposes;
            if (weights.Length != verts.Length || headIndex >= bindposes.Length) return null;
            if (normals.Length != verts.Length) normals = null;

            // the character's facing, in mesh space
            Vector3 f = smr.transform.InverseTransformDirection(root.forward).normalized;
            Vector3 upGuess = smr.transform.InverseTransformDirection(root.up);
            Vector3 right = Vector3.Cross(upGuess, f).normalized;
            Vector3 up = Vector3.Cross(f, right);

            // head vertices = mostly skinned to the head bone
            var isHead = new bool[verts.Length];
            int count = 0;
            float minR = float.MaxValue, maxR = float.MinValue, minU = float.MaxValue, maxU = float.MinValue;
            for (int i = 0; i < verts.Length; i++)
            {
                if (Dominant(weights[i]) != headIndex) continue;
                isHead[i] = true;
                count++;
                float r = Vector3.Dot(verts[i], right), u = Vector3.Dot(verts[i], up);
                minR = Mathf.Min(minR, r); maxR = Mathf.Max(maxR, r);
                minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u);
            }
            if (count < 12) return null;

            float size = (maxR - minR) * FaceWidth;
            float rc = (minR + maxR) * 0.5f, uc = (minU + maxU) * 0.5f;
            float lift = size * Lift;
            Matrix4x4 toBone = bindposes[headIndex];

            var outV = new List<Vector3>();
            var outN = new List<Vector3>();
            var outUv = new List<Vector2>();
            var outT = new List<int>();
            for (int sm = 0; sm < src.subMeshCount; sm++)
            {
                if (src.GetTopology(sm) != MeshTopology.Triangles) continue;
                var tris = src.GetTriangles(sm);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    if (!isHead[a] || !isHead[b] || !isHead[c]) continue;
                    Vector3 n = normals != null
                        ? (normals[a] + normals[b] + normals[c]).normalized
                        : Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]).normalized;
                    if (Vector3.Dot(n, f) < MinFacing) continue;

                    // drawing coordinates, as seen by someone looking at the face (their right = the character's left)
                    Vector2 Uv(int v) => new(0.5f - (Vector3.Dot(verts[v], right) - rc) / size,
                                             0.5f + (Vector3.Dot(verts[v], up) - uc) / size);
                    Vector2 ua = Uv(a), ub = Uv(b), uv = Uv(c);
                    if (Mathf.Max(ua.x, ub.x, uv.x) < 0f || Mathf.Min(ua.x, ub.x, uv.x) > 1f ||
                        Mathf.Max(ua.y, ub.y, uv.y) < 0f || Mathf.Min(ua.y, ub.y, uv.y) > 1f) continue;

                    foreach (int v in new[] { a, b, c })
                    {
                        Vector3 vn = normals != null ? normals[v] : n;
                        outT.Add(outV.Count);
                        outV.Add(toBone.MultiplyPoint3x4(verts[v] + vn * lift));
                        outN.Add(toBone.MultiplyVector(vn).normalized);
                        outUv.Add(Uv(v));
                    }
                }
            }
            if (outT.Count == 0) return null;

            var mesh = new Mesh { name = "FaceDecal" };
            if (outV.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(outV);
            mesh.SetNormals(outN);
            mesh.SetUVs(0, outUv);
            mesh.SetTriangles(outT, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static int Dominant(BoneWeight w)
        {
            int best = w.boneIndex0;
            float bw = w.weight0;
            if (w.weight1 > bw) { best = w.boneIndex1; bw = w.weight1; }
            if (w.weight2 > bw) { best = w.boneIndex2; bw = w.weight2; }
            if (w.weight3 > bw) { best = w.boneIndex3; }
            return best;
        }

        static Mesh _quad;
        static Mesh Quad()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "FaceQuad" };
            _quad.SetVertices(new List<Vector3> { new(-0.5f, -0.5f, 0), new(0.5f, -0.5f, 0), new(-0.5f, 0.5f, 0), new(0.5f, 0.5f, 0) });
            // the card's +Z points into the head, so someone looking at the face looks along +Z: +X is their right
            _quad.SetUVs(0, new List<Vector2> { new(0, 0), new(1, 0), new(0, 1), new(1, 1) });
            _quad.SetTriangles(new List<int> { 0, 2, 1, 2, 3, 1 }, 0);
            _quad.RecalculateNormals();
            _quad.RecalculateBounds();
            return _quad;
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform c in root)
            {
                var f = FindDeep(c, name);
                if (f != null) return f;
            }
            return null;
        }
    }
}
