using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SuddenMageEncounter.EditorTools
{
    /// <summary>
    /// Sudden Mage Encounter > Hazards > Make Train Prefabs: the built-in subway car as prefabs (front, middle, back), and
    /// a whole train of them to look at (SubwayTrain). Tracks with no cars of their own get them.
    /// </summary>
    public static class SMETrain
    {
        public const string Dir = "Assets/Prefab/Subway";

        public static GameObject Front => Load("SubwayFront");
        public static GameObject Middle => Load("SubwayMiddle");
        public static GameObject Back => Load("SubwayBack");
        static GameObject Load(string name) => AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "/" + name + ".prefab");

        [MenuItem("Sudden Mage Encounter/Hazards/Make Train Prefabs", priority = 60)]
        public static void Make()
        {
            if (Middle != null && !EditorUtility.DisplayDialog("Sudden Mage Encounter",
                    "Make the subway prefabs fresh? Any changes to the ones in " + Dir + " are lost.", "Make them", "Cancel")) return;
            System.IO.Directory.CreateDirectory(Dir);
            AssetDatabase.Refresh();
            var size = TrainTrack.CarSize;
            const float gap = TrainTrack.CarGap;
            var front = Car("SubwayFront", size, true, false);
            var middle = Car("SubwayMiddle", size, false, false);
            var back = Car("SubwayBack", size, false, true);

            var train = new GameObject("SubwayTrain");
            var cars = new[] { front, middle, middle, back };
            for (int i = 0; i < cars.Length; i++)
            {
                var c = (GameObject)PrefabUtility.InstantiatePrefab(cars[i], train.transform);
                c.transform.localPosition = Vector3.back * (i * (size.z + gap));
            }
            var saved = PrefabUtility.SaveAsPrefabAsset(train, Dir + "/SubwayTrain.prefab");
            Object.DestroyImmediate(train);
            AssetDatabase.SaveAssets();

            foreach (var t in Object.FindObjectsByType<TrainTrack>(FindObjectsSortMode.None))
            {
                if (t.carPrefab != null || t.frontPrefab != null || t.backPrefab != null) continue;
                Undo.RecordObject(t, "Make Train Prefabs");
                t.frontPrefab = front;
                t.carPrefab = middle;
                t.backPrefab = back;
                EditorUtility.SetDirty(t);
            }
            Selection.activeObject = saved;
            EditorGUIUtility.PingObject(saved);
        }

        static GameObject Car(string name, Vector3 size, bool first, bool last)
        {
            var go = new GameObject(name);
            TrainCar.BuildVoxels(go.transform, size, first, last);
            // everything it's made of was made in code: saved as assets, so the prefab keeps it
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                if (Unsaved(mf.sharedMesh)) mf.sharedMesh = Keep(Object.Instantiate(mf.sharedMesh), name);
            var block = new MaterialPropertyBlock();
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
            {
                var mats = r.sharedMaterials;
                bool tinted = r.HasPropertyBlock();
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || !Unsaved(mats[i])) continue;
                    var m = new Material(mats[i]);
                    string called = mats[i].name;
                    if (tinted)
                    {
                        // (a lamp's colour is a property block, which a prefab doesn't save)
                        r.GetPropertyBlock(block);
                        m.SetColor("_BaseColor", block.GetColor("_BaseColor"));
                        called = first ? "Headlight" : "Taillight";
                    }
                    mats[i] = Keep(m, called);
                }
                r.SetPropertyBlock(null);
                r.sharedMaterials = mats;
            }
            var saved = PrefabUtility.SaveAsPrefabAsset(go, Dir + "/" + name + ".prefab");
            Object.DestroyImmediate(go);
            return saved;
        }

        /// <summary>Everything a voxel mesh has (VoxelMesher), into a saved one.</summary>
        static void Refill(Mesh from, Mesh to)
        {
            to.Clear();
            to.indexFormat = from.indexFormat;
            to.SetVertices(from.vertices);
            to.SetNormals(from.normals);
            to.SetColors(from.colors32);
            if (from.uv.Length > 0) to.SetUVs(0, from.uv);
            var edges = new List<Vector4>();
            from.GetUVs(1, edges);
            if (edges.Count > 0) to.SetUVs(1, edges);
            to.subMeshCount = from.subMeshCount;
            for (int i = 0; i < from.subMeshCount; i++) to.SetTriangles(from.GetTriangles(i), i);
            to.RecalculateBounds();
            to.UploadMeshData(false);
        }

        static bool Unsaved(Object o) => string.IsNullOrEmpty(AssetDatabase.GetAssetPath(o));

        /// <summary>Saved beside the prefabs, over the one already there (so whatever uses it keeps it).</summary>
        static T Keep<T>(T made, string name) where T : Object
        {
            string path = Dir + "/" + name + (made is Mesh ? ".asset" : ".mat");
            made.name = name;
            var old = AssetDatabase.LoadAssetAtPath<T>(path);
            if (old == null)
            {
                AssetDatabase.CreateAsset(made, path);
                return made;
            }
            // (a mesh copied as an asset keeps being drawn as it was: it has to be filled in again)
            if (made is Mesh from && old is Mesh to) Refill(from, to);
            else EditorUtility.CopySerialized(made, old);
            old.name = name;
            EditorUtility.SetDirty(old);
            Object.DestroyImmediate(made);
            return old;
        }
    }
}
