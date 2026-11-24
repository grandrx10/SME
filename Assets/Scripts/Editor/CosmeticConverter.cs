using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SuddenMageEncounter.EditorTools
{
    /// <summary>
    /// Turns every child of the scene's "Cosmetics" object (skinned pieces of cardguy.fbx, off their animals' bones) into a
    /// plain mesh and a prefab in Resources/Cosmetics, sized and placed for the player's body. Each child becomes a stand: a
    /// copy of the player wearing it. Moving what it wears and saving the scene makes that its default spot.
    /// Running it again only converts what's new: prefabs that already exist keep their materials and spots.
    /// </summary>
    [InitializeOnLoad]
    public static class CosmeticConverter
    {
        const string RootName = "Cosmetics";
        const string MeshDir = "Assets/SME/Cosmetics/Meshes", MatDir = "Assets/SME/Cosmetics/Materials", PrefabDir = "Assets/Resources/Cosmetics";
        const string PlayerPath = "Assets/Prefab/NetPlayer.prefab", BodyMatPath = "Assets/SME/Materials/Toon/Lit_Toon.mat";
        const float Spacing = 2.2f;

        static CosmeticConverter()
        {
            EditorSceneManager.sceneSaving += (scene, path) => SaveSpots(scene);
        }

        // ------------------------------------------------------------------ where things go

        /// <summary>What part of the animal's body one is matched to on the player's, and where its origin (what it grows from) is.</summary>
        enum On { Crown, Face, Chest, Hips, Back }

        class Fit
        {
            public On on;
            public float grow = 1f;                 // times the size that matches the part's width
            public Vector3 move;                    // then moved (model units: half a metre each; +z is forward)
            public Vector3 facing;                  // which way its front faces on the animal, turned to the front first (zero: as it is)
            public int changing = -1;               // the material slot that takes the rolled colour (-1: every slot with no material of its own)
            public Fit(On on) { this.on = on; }
        }

        static readonly Dictionary<CosmeticId, Fit> Fits = new()
        {
            { CosmeticId.Shell, new Fit(On.Back) { changing = 3, grow = 0.9f } },
            { CosmeticId.TopHat, new Fit(On.Crown) { grow = 1.75f, move = new(0f, -0.16f, 0f) } },
            { CosmeticId.LongHair, new Fit(On.Crown) { grow = 1.12f } },
            { CosmeticId.SoftHat, new Fit(On.Crown) { grow = 1.3f, move = new(0f, 0f, 0.07f) } },
            { CosmeticId.RiceHat, new Fit(On.Crown) { grow = 1.1f } },
            { CosmeticId.Charm, new Fit(On.Chest) { changing = 0, grow = 0.6f, move = new(0.16f, -0.12f, -0.07f), facing = new(-0.709f, 0.522f, 0.475f) } },
            { CosmeticId.Glasses, new Fit(On.Face) { grow = 1.1f, move = new(0f, 0f, 0.08f) } },
            { CosmeticId.PuffHair, new Fit(On.Crown) },
            { CosmeticId.Goggles, new Fit(On.Face) { changing = 1, move = new(0f, 0f, 0.06f) } },
            { CosmeticId.Tie, new Fit(On.Chest) { move = new(0f, 0.12f, -0.08f) } },
            { CosmeticId.FlatHat, new Fit(On.Crown) { move = new(0f, -0.06f, 0f), grow = 1.1f } },
            { CosmeticId.Whiskers, new Fit(On.Face) { grow = 1.3f, move = new(0f, -0.1f, 0.09f) } },
            { CosmeticId.ChefHat, new Fit(On.Crown) { grow = 2f, move = new(0f, -0.16f, 0.06f) } },
            { CosmeticId.Belt, new Fit(On.Hips) { grow = 0.9f, move = new(0f, 0.05f, 0f) } },
            { CosmeticId.Hair, new Fit(On.Crown) { grow = 1.12f } },
            { CosmeticId.BunnyEars, new Fit(On.Crown) { changing = 0, grow = 1.35f, move = new(0f, -0.2f, 0f) } },
            { CosmeticId.WizardHat, new Fit(On.Crown) { changing = 0, grow = 1.12f } },
            { CosmeticId.WitchHat, new Fit(On.Crown) { changing = 0 } },
        };

        /// <summary>A body measured in its own space (+y up, +z forward): the head's and trunk's boxes, stray vertices left out.</summary>
        struct Measure
        {
            public Bounds head, trunk;
        }

        // ------------------------------------------------------------------ converting

        [MenuItem("Sudden Mage Encounter/Cosmetics/Convert", priority = 60)]
        public static void Convert()
        {
            var root = GameObject.Find(RootName);
            if (root == null) { Debug.LogError("[SME] There's no '" + RootName + "' object in the open scene."); return; }
            var player = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
            var model = player != null ? player.transform.Find("PlayerModel") : null;
            if (model == null) { Debug.LogError("[SME] No PlayerModel in " + PlayerPath); return; }
            foreach (var d in new[] { MeshDir, MatDir, PrefabDir }) Directory.CreateDirectory(d);
            AssetDatabase.Refresh();

            var todo = new List<Transform>();
            foreach (Transform c in root.transform)
                if (c.GetComponent<CosmeticStand>() == null) todo.Add(c);

            // the meshes have to be readable while we copy them
            var readable = new List<ModelImporter>();
            foreach (var c in todo)
                foreach (var m in MeshesOf(c))
                {
                    var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(m.mesh)) as ModelImporter;
                    if (imp == null || imp.isReadable || readable.Contains(imp)) continue;
                    readable.Add(imp);
                }
            foreach (var imp in readable) { imp.isReadable = true; imp.SaveAndReimport(); }

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Convert Cosmetics");
            var body = MeasurePlayer(model);
            int made = 0, kept = 0;
            var skipped = new List<string>();
            try
            {
                for (int i = 0; i < todo.Count; i++)
                {
                    var c = todo[i];
                    if (!System.Enum.TryParse<CosmeticId>(c.name, out var id) || id == CosmeticId.None)
                    {
                        skipped.Add(c.name);
                        continue;
                    }
                    string path = PrefabDir + "/" + id + ".prefab";
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) { prefab = MakePrefab(c, id, model, body, path); if (prefab != null) made++; }
                    else kept++;
                    if (prefab == null) { Debug.LogWarning("[SME] Couldn't convert '" + c.name + "'."); continue; }
                    MakeStand(root.transform, id, prefab, model, root.transform.childCount);
                    Undo.DestroyObjectImmediate(c.gameObject);
                }
            }
            finally
            {
                foreach (var imp in readable) { imp.isReadable = false; imp.SaveAndReimport(); }
            }
            Arrange(root.transform);
            Undo.RecordObject(root, "Convert Cosmetics");
            root.tag = "EditorOnly"; // (never in a build)
            RemoveLeftovers(todo.Count > 0);
            Undo.CollapseUndoOperations(group);
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(root.scene);
            if (skipped.Count > 0)
            {
                string list = string.Join(", ", skipped);
                Debug.LogWarning("[SME] Not in CosmeticId (Cosmetics.cs), left as they are: " + list);
                if (!Application.isBatchMode) EditorUtility.DisplayDialog("Cosmetics", "Not converted, because Cosmetics.cs doesn't list them by name:\n\n" + list, "OK");
            }
            Debug.Log("[SME] Cosmetics: " + made + " converted, " + kept + " already had a prefab. Move any of them on its player and save the scene to set its default spot.");
        }

        /// <summary>Scene saved: whatever the stands' cosmetics were moved to is their default spot.</summary>
        static void SaveSpots(UnityEngine.SceneManagement.Scene scene)
        {
            if (Application.isPlaying) return;
            foreach (var go in scene.GetRootGameObjects())
                foreach (var s in go.GetComponentsInChildren<CosmeticStand>(true))
                {
                    if (s.worn == null || s.prefab == null) continue;
                    var b = s.prefab.GetComponent<CosmeticBody>();
                    Transform bone = s.worn.parent;
                    while (bone != null && System.Array.IndexOf(CosmeticLook.Bones, bone.name) < 0) bone = bone.parent;
                    if (b == null || bone == null) continue;
                    Vector3 pos = bone.InverseTransformPoint(s.worn.position);
                    Quaternion rot = Quaternion.Inverse(bone.rotation) * s.worn.rotation;
                    float scale = s.worn.lossyScale.x / Mathf.Max(1e-4f, bone.lossyScale.x);
                    if (b.bone == bone.name && (b.position - pos).sqrMagnitude < 1e-8f && Quaternion.Angle(b.rotation, rot) < 0.05f && Mathf.Abs(b.scale - scale) < 1e-4f) continue;
                    b.bone = bone.name;
                    b.position = pos;
                    b.rotation = rot;
                    b.scale = scale;
                    EditorUtility.SetDirty(b);
                    PrefabUtility.SavePrefabAsset(s.prefab);
                }
        }

        struct Piece
        {
            public Mesh mesh;
            public Renderer renderer;
        }

        static List<Piece> MeshesOf(Transform t)
        {
            var list = new List<Piece>();
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = r is SkinnedMeshRenderer s ? s.sharedMesh : r.TryGetComponent<MeshFilter>(out var f) ? f.sharedMesh : null;
                if (mesh != null) list.Add(new Piece { mesh = mesh, renderer = r });
            }
            return list;
        }

        /// <summary>The model file's own objects, as it was exported: every piece is measured against its animal there.</summary>
        static Transform Find(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            return null;
        }

        static GameObject MakePrefab(Transform c, CosmeticId id, Transform model, Measure body, string path)
        {
            var pieces = MeshesOf(c);
            if (pieces.Count == 0) return null;
            var fit = Fits.TryGetValue(id, out var f) ? f : new Fit(On.Crown);

            // the animal this came off (meshes are called animal_thing), in the file it came from
            string file = AssetDatabase.GetAssetPath(pieces[0].mesh);
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(file);
            string animal = pieces[0].mesh.name.Split('_')[0];
            var from = MeasureAnimal(source, animal);

            // every piece where it is on its animal, one submesh a material slot
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var subs = new List<int[]>();
            var mats = new List<Material>();
            foreach (var p in pieces)
            {
                var t = Find(source.transform, p.mesh.name);
                var pr = t != null ? t.GetComponent<Renderer>() : null;
                if (pr == null) { Debug.LogWarning("[SME] " + p.mesh.name + " isn't in " + file); return null; }
                int at = verts.Count;
                Pose(pr, p.mesh, verts, normals);
                var uv = p.mesh.uv;
                for (int i = 0; i < p.mesh.vertexCount; i++) uvs.Add(i < uv.Length ? uv[i] : Vector2.zero);
                var own = p.renderer.sharedMaterials;
                for (int s = 0; s < p.mesh.subMeshCount; s++)
                {
                    var tris = p.mesh.GetTriangles(s);
                    if (tris.Length == 0) continue;
                    for (int k = 0; k < tris.Length; k++) tris[k] += at;
                    subs.Add(tris);
                    mats.Add(SlotMaterial(own.Length > 0 ? own[Mathf.Min(s, own.Length - 1)] : null, fit.changing >= 0 ? s == fit.changing : (bool?)null));
                }
            }

            if (fit.facing != Vector3.zero)
            {
                var q = Quaternion.FromToRotation(fit.facing, Vector3.forward);
                for (int i = 0; i < verts.Count; i++) { verts[i] = q * verts[i]; normals[i] = q * normals[i]; }
            }
            // where on the body its origin is, on the animal and on the player
            Bounds box = new(verts[0], Vector3.zero);
            foreach (var v in verts) box.Encapsulate(v);
            Vector3 a = Anchor(fit.on, from, box, true), b = Anchor(fit.on, body, box, false);
            bool onHead = fit.on == On.Crown || fit.on == On.Face;
            float s0 = (onHead ? body.head.size.x / from.head.size.x : body.trunk.size.x / from.trunk.size.x) * fit.grow;
            for (int i = 0; i < verts.Count; i++) verts[i] = (verts[i] - a) * s0;

            var mesh = new Mesh { name = id.ToString() };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) mesh.SetTriangles(subs[s], s);
            mesh.RecalculateBounds();
            string meshPath = MeshDir + "/" + id + ".asset";
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);

            string bone = onHead ? "Bone.001" : fit.on == On.Hips ? "Bone" : "Bone.003";
            var boneT = Find(model, bone);
            var go = new GameObject(id.ToString());
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();
            var spot = go.AddComponent<CosmeticBody>();
            spot.bone = bone;
            // the model's space (its own scale left out) to the bone's
            Vector3 world = model.TransformPoint(b + fit.move);
            spot.position = boneT.InverseTransformPoint(world);
            spot.rotation = Quaternion.Inverse(boneT.rotation) * model.rotation;
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }

        static Vector3 Anchor(On on, Measure m, Bounds item, bool itsOwn)
        {
            var h = m.head;
            var t = m.trunk;
            switch (on)
            {
                case On.Crown: return new Vector3(h.center.x, h.max.y, h.center.z);
                // as far up the face as it is on the animal's
                case On.Face: return itsOwn ? new Vector3(item.center.x, item.center.y, item.max.z) : new Vector3(h.center.x, Mathf.Lerp(h.min.y, h.max.y, 0.6f), h.max.z);
                case On.Chest: return itsOwn ? new Vector3(item.center.x, item.max.y, item.min.z) : new Vector3(t.center.x, Mathf.Lerp(t.min.y, t.max.y, 0.9f), t.max.z);
                case On.Hips: return itsOwn ? new Vector3(t.center.x, item.center.y, t.center.z) : new Vector3(t.center.x, Mathf.Lerp(t.min.y, t.max.y, 0.2f), t.center.z);
                default: return itsOwn ? new Vector3(t.center.x, item.center.y, t.min.z) : new Vector3(t.center.x, Mathf.Lerp(t.min.y, t.max.y, 0.5f), t.min.z);
            }
        }

        /// <summary>A slot's material: the changing colour, or a toon copy of its own.</summary>
        static Material SlotMaterial(Material own, bool? changing)
        {
            bool none = own == null || !AssetDatabase.GetAssetPath(own).StartsWith("Assets/");
            if (changing ?? none) return Changing();
            string name = none ? "Plain" : own.name;
            string path = MatDir + "/" + name.Replace('.', '_') + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(Shader.Find("SME/Toon")) { name = name };
            if (!none)
            {
                m.SetColor("_BaseColor", own.HasProperty("_BaseColor") ? own.GetColor("_BaseColor") : Color.white);
                if (own.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", own.GetTexture("_BaseMap"));
            }
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static Material Changing()
        {
            string path = MatDir + "/" + CosmeticLook.ChangingName + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(Shader.Find("SME/Toon")) { name = CosmeticLook.ChangingName };
            m.SetColor("_BaseColor", new Color(1f, 0.35f, 0.75f));
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        // ------------------------------------------------------------------ measuring

        /// <summary>Every vertex of a renderer where it's posed (world space of wherever it is), added to the lists.</summary>
        static void Pose(Renderer r, Mesh mesh, List<Vector3> verts, List<Vector3> normals)
        {
            var v = mesh.vertices;
            var n = mesh.normals;
            var smr = r as SkinnedMeshRenderer;
            var weights = smr != null ? mesh.boneWeights : null;
            Matrix4x4[] skin = null;
            if (smr != null && weights.Length == v.Length && smr.bones.Length > 0)
            {
                var binds = mesh.bindposes;
                skin = new Matrix4x4[smr.bones.Length];
                for (int i = 0; i < skin.Length; i++)
                    skin[i] = (smr.bones[i] != null ? smr.bones[i].localToWorldMatrix : r.transform.localToWorldMatrix) * (i < binds.Length ? binds[i] : Matrix4x4.identity);
            }
            for (int i = 0; i < v.Length; i++)
            {
                Matrix4x4 m = r.transform.localToWorldMatrix;
                if (skin != null)
                {
                    var w = weights[i];
                    float total = w.weight0 + w.weight1 + w.weight2 + w.weight3;
                    if (total > 1e-4f)
                    {
                        m = Matrix4x4.zero;
                        void Blend(int bone, float k)
                        {
                            if (k <= 0f) return;
                            for (int e = 0; e < 16; e++) m[e] += skin[bone][e] * (k / total);
                        }
                        Blend(w.boneIndex0, w.weight0);
                        Blend(w.boneIndex1, w.weight1);
                        Blend(w.boneIndex2, w.weight2);
                        Blend(w.boneIndex3, w.weight3);
                    }
                }
                verts.Add(m.MultiplyPoint3x4(v[i]));
                normals.Add(i < n.Length ? m.inverse.transpose.MultiplyVector(n[i]).normalized : Vector3.up);
            }
        }

        static Bounds Box(List<Vector3> pts, float trim)
        {
            var b = new Bounds();
            Vector3 min = Vector3.zero, max = Vector3.zero;
            for (int k = 0; k < 3; k++)
            {
                var one = new List<float>(pts.Count);
                foreach (var p in pts) one.Add(p[k]);
                one.Sort();
                min[k] = one[Mathf.Clamp(Mathf.FloorToInt(one.Count * trim), 0, one.Count - 1)];
                max[k] = one[Mathf.Clamp(Mathf.CeilToInt(one.Count * (1f - trim)) - 1, 0, one.Count - 1)];
            }
            b.SetMinMax(min, max);
            return b;
        }

        static Measure MeasureAnimal(GameObject source, string animal)
        {
            var m = new Measure();
            // the skin of the head: ears, snouts and the like are few vertices, so the ends are trimmed
            var head = Find(source.transform, animal + "_head");
            var pts = new List<Vector3>();
            var no = new List<Vector3>();
            if (head != null && head.TryGetComponent<SkinnedMeshRenderer>(out var hr)) Pose(hr, hr.sharedMesh, pts, no);
            m.head = pts.Count > 0 ? Box(pts, 0.04f) : new Bounds(Vector3.zero, Vector3.one);
            var trunk = Find(source.transform, animal + "_body");
            if (trunk != null && trunk.TryGetComponent<SkinnedMeshRenderer>(out var tr)) m.trunk = Trunk(tr, "Bone", "Bone.001");
            if (m.trunk.size.x < 1e-3f) m.trunk = new Bounds(m.head.center - Vector3.up * m.head.size.y, m.head.size * 0.6f);
            return m;
        }

        static Measure MeasurePlayer(Transform model)
        {
            var smr = model.GetComponentInChildren<SkinnedMeshRenderer>(true);
            var m = new Measure { head = Trunk(smr, "Bone.001"), trunk = Trunk(smr, "Bone", "Bone.003") };
            // (the model's own space)
            m.head = Local(model, m.head);
            m.trunk = Local(model, m.trunk);
            return m;
        }

        static Bounds Local(Transform model, Bounds world)
        {
            var b = new Bounds(model.InverseTransformPoint(world.center), Vector3.zero);
            b.Encapsulate(model.InverseTransformPoint(world.min));
            b.Encapsulate(model.InverseTransformPoint(world.max));
            return b;
        }

        /// <summary>The box round the vertices that mostly follow these bones.</summary>
        static Bounds Trunk(SkinnedMeshRenderer smr, params string[] bones)
        {
            var mesh = smr.sharedMesh;
            var pts = new List<Vector3>();
            var no = new List<Vector3>();
            Pose(smr, mesh, pts, no);
            var w = mesh.boneWeights;
            var part = new List<Vector3>();
            for (int i = 0; i < pts.Count && i < w.Length; i++)
            {
                float k = 0f;
                void Add(int b, float weight)
                {
                    if (b < smr.bones.Length && smr.bones[b] != null && System.Array.IndexOf(bones, smr.bones[b].name) >= 0) k += weight;
                }
                Add(w[i].boneIndex0, w[i].weight0);
                Add(w[i].boneIndex1, w[i].weight1);
                Add(w[i].boneIndex2, w[i].weight2);
                Add(w[i].boneIndex3, w[i].weight3);
                if (k > 0.5f) part.Add(pts[i]);
            }
            return part.Count > 3 ? Box(part, 0f) : new Bounds();
        }

        // ------------------------------------------------------------------ stands

        static void MakeStand(Transform root, CosmeticId id, GameObject prefab, Transform model, int index)
        {
            var go = new GameObject(id.ToString());
            Undo.RegisterCreatedObjectUndo(go, "Convert Cosmetics");
            go.transform.SetParent(root, false);
            var stand = go.AddComponent<CosmeticStand>();
            var body = Object.Instantiate(model.gameObject, go.transform, false);
            body.name = "Player";
            foreach (var mb in body.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(mb);
            foreach (var col in body.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);
            var spot = prefab.GetComponent<CosmeticBody>();
            var bone = Find(body.transform, spot.bone);
            var worn = (GameObject)PrefabUtility.InstantiatePrefab(prefab, bone != null ? bone : body.transform);
            CosmeticLook.Place(worn.transform, spot.position, spot.rotation, spot.scale);
            stand.model = body.transform;
            stand.worn = worn.transform;
            stand.prefab = prefab;
        }

        static void Arrange(Transform root)
        {
            int i = 0;
            foreach (Transform c in root)
            {
                if (c.GetComponent<CosmeticStand>() == null) continue;
                c.localPosition = new Vector3((i % 9 - 4) * Spacing, 1f, -(i / 9) * Spacing);
                i++;
            }
        }

        /// <summary>The animals' skeletons left behind in the scene, once nothing drawn is left on them.</summary>
        static void RemoveLeftovers(bool any)
        {
            if (!any) return;
            foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (go.name != "cardguy" && !go.name.StartsWith("cardguy ")) continue;
                if (go.GetComponentsInChildren<Renderer>(true).Length == 0) Undo.DestroyObjectImmediate(go);
                else Debug.Log("[SME] '" + go.name + "' still has pieces of its own in the scene: left there.");
            }
        }
    }
}
