using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Cosmetics as things you can see: the prefab with its changing colour put in, hung off a body's bones. Plain meshes
    /// with no colliders. The outlines drawn round a body (enemies, The Star...) go on a second renderer, the hull, which
    /// has every triangle in one submesh.
    /// </summary>
    public static class CosmeticLook
    {
        /// <summary>What's worn is called this (nothing else on a body is), and so is the start of its hull's name.</summary>
        public const string ObjectName = "Cosmetic", HullName = "CosmeticHull";
        /// <summary>Material slots with this material get the rolled colour; every other slot keeps its own.</summary>
        public const string ChangingName = "CosmeticChanging";

        /// <summary>The bones one can hang off. Sent by index: only add at the end.</summary>
        public static readonly string[] Bones =
        {
            "Bone.001", "Bone.003", "Bone",
            "Arm L", "Arm L.001", "Arm L.002", "Arm L.003", "Arm R", "Arm R.001", "Arm R.002", "Arm R.003",
            "Leg L", "Leg L.001", "Leg L.002", "Leg L.003", "Leg R", "Leg R.001", "Leg R.002", "Leg R.003",
        };

        static readonly Dictionary<CosmeticId, GameObject> Prefabs = new();
        static readonly Dictionary<(Material, int), Material> Tints = new();
        static readonly Dictionary<Mesh, Mesh> Hulls = new();

        public static GameObject Prefab(CosmeticId id)
        {
            if (!Prefabs.TryGetValue(id, out var p) || p == null)
            {
                p = Resources.Load<GameObject>("Cosmetics/" + id);
                if (p == null) Debug.LogWarning("[SME] No cosmetic prefab: Resources/Cosmetics/" + id);
                Prefabs[id] = p;
            }
            return p;
        }

        static CosmeticBody Spot(CosmeticId id)
        {
            var p = Prefab(id);
            return p != null ? p.GetComponent<CosmeticBody>() : null;
        }

        /// <summary>Puts <paramref name="w"/> where this one goes by default.</summary>
        public static void DefaultSpot(CosmeticId id, WornItem w)
        {
            var b = Spot(id);
            w.bone = (byte)(b != null ? Mathf.Max(0, System.Array.IndexOf(Bones, b.bone)) : 0);
            w.pos = b != null ? b.position : Vector3.zero;
            w.rot = b != null ? b.rotation : Quaternion.identity;
        }

        /// <summary>This one's own scale before the size it rolled.</summary>
        public static float BaseScale(CosmeticId id)
        {
            var b = Spot(id);
            return b != null ? b.scale : 1f;
        }

        /// <summary>Part of a cosmetic (keeps its own colours: body tints skip it).</summary>
        public static bool Is(Component c) => c != null && c.name.StartsWith(ObjectName, System.StringComparison.Ordinal);
        public static bool IsHull(Component c) => c != null && c.name == HullName;
        /// <summary>The drawn part, which body outlines don't go on (they go on its hull).</summary>
        public static bool IsPart(Component c) => c != null && c.name == ObjectName;

        static Material Tinted(Material m, int colour)
        {
            if (m == null || !m.name.StartsWith(ChangingName, System.StringComparison.Ordinal)) return m;
            if (Tints.TryGetValue((m, colour), out var t) && t != null) return t;
            t = new Material(m) { name = ChangingName + " " + Cosmetics.ColourName(colour) };
            t.SetColor("_BaseColor", Cosmetics.Colour(colour));
            return Tints[(m, colour)] = t;
        }

        /// <summary>One under <paramref name="parent"/>, at its own origin. Null if there's no such prefab.</summary>
        public static GameObject Make(CosmeticId id, int colour, Transform parent, bool hull = false)
        {
            var prefab = Prefab(id);
            if (prefab == null) return null;
            var go = Object.Instantiate(prefab, parent, false);
            go.name = ObjectName;
            Place(go.transform, Vector3.zero, Quaternion.identity, 1f);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) Object.Destroy(mb);
            var r = go.GetComponent<MeshRenderer>();
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++) mats[i] = Tinted(mats[i], colour);
            r.sharedMaterials = mats;
            if (hull)
            {
                var h = new GameObject(HullName);
                h.transform.SetParent(go.transform, false);
                h.AddComponent<MeshFilter>().sharedMesh = Hull(go.GetComponent<MeshFilter>().sharedMesh);
                var hr = h.AddComponent<MeshRenderer>();
                hr.sharedMaterials = System.Array.Empty<Material>();
                hr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                hr.receiveShadows = false;
            }
            Layers.SetLayerRecursive(go, parent.gameObject.layer);
            return go;
        }

        /// <summary>Hangs a whole outfit off a body's bones. Returns what was made.</summary>
        public static List<GameObject> Wear(Dictionary<string, Transform> bones, List<OutfitPiece> outfit, bool hulls)
        {
            var made = new List<GameObject>();
            foreach (var p in outfit)
            {
                if (!bones.TryGetValue(Bones[p.bone], out var bone) || bone == null) continue;
                var go = Make(p.id, p.colour, bone, hulls);
                if (go == null) continue;
                Place(go.transform, p.pos, p.rot, BaseScale(p.id) * Cosmetics.Scale(p.size));
                made.Add(go);
            }
            return made;
        }

        public static void Place(Transform t, Vector3 pos, Quaternion rot, float scale)
        {
            t.localPosition = pos;
            t.localRotation = rot;
            t.localScale = Vector3.one * scale;
        }

        /// <summary>All of a mesh's triangles in one submesh, its normals smoothed (so the outline has no gaps at hard edges).</summary>
        static Mesh Hull(Mesh src)
        {
            if (src == null || !src.isReadable) return src;
            if (Hulls.TryGetValue(src, out var m) && m != null) return m;
            var verts = src.vertices;
            var normals = src.normals;
            if (normals.Length == verts.Length)
            {
                var sum = new Dictionary<Vector3Int, Vector3>();
                for (int i = 0; i < verts.Length; i++)
                {
                    var k = Key(verts[i]);
                    sum.TryGetValue(k, out var n);
                    sum[k] = n + normals[i];
                }
                for (int i = 0; i < verts.Length; i++)
                {
                    var n = sum[Key(verts[i])];
                    if (n.sqrMagnitude > 1e-6f) normals[i] = n.normalized;
                }
            }
            var tris = new List<int>();
            for (int s = 0; s < src.subMeshCount; s++) tris.AddRange(src.GetTriangles(s));
            m = new Mesh { name = src.name + " Hull" };
            m.SetVertices(verts);
            if (normals.Length == verts.Length) m.SetNormals(normals);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return Hulls[src] = m;
        }

        static Vector3Int Key(Vector3 v) => Vector3Int.RoundToInt(v * 2000f);
    }
}
