using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SuddenMageEncounter.EditorTools
{
    /// <summary>Arena inspector: Bake Voxels, preview them in the Scene view, and add the arena to the rotation.</summary>
    [CustomEditor(typeof(Arena))]
    public class ArenaEditor : Editor
    {
        static string _lastReport = "";

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var arena = (Arena)target;
            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Bake Voxels", GUILayout.Height(32))) Bake(arena);
                EditorGUILayout.BeginHorizontal();
                bool previewing = FindPreview(arena) != null;
                if (GUILayout.Button(previewing ? "Hide Voxel Preview" : "Preview Voxels"))
                {
                    if (previewing) ClearPreview(arena);
                    else Preview(arena);
                }
                if (GUILayout.Button("Add To Rotation")) AddToRotation(arena);
                EditorGUILayout.EndHorizontal();
            }

            if (!string.IsNullOrEmpty(_lastReport)) EditorGUILayout.HelpBox(_lastReport, MessageType.Info);
            if (arena.voxelData == null)
                EditorGUILayout.HelpBox("Not baked yet. Build the arena from ProBuilder shapes (or any meshes) under this object, then Bake Voxels.\n" +
                                        "Indestructible component = can't break. Voxel Ignore component = stays a normal mesh.", MessageType.Warning);
            if (arena.transform.rotation != Quaternion.identity)
                EditorGUILayout.HelpBox("Keep the arena root unrotated (rotate its children instead).", MessageType.Warning);
        }

        // ------------------------------------------------------------------ bake

        public static void Bake(Arena arena)
        {
            ClearPreview(arena);
            var data = ArenaVoxelizer.Voxelize(arena, out var report);
            if (data.CellCount == 0)
            {
                _lastReport = "Nothing to bake: no meshes under the arena. " + report;
                return;
            }
            string path = DataPath(arena);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? SMESetup.ArenaDir);
            File.WriteAllBytes(path, data.ToBytes());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);

            Undo.RecordObject(arena, "Bake Voxels");
            arena.voxelData = asset;
            EditorUtility.SetDirty(arena);
            // a prefab instance in a scene: write it into the prefab too
            if (PrefabUtility.IsPartOfPrefabInstance(arena))
            {
                var so = new SerializedObject(arena);
                var prop = so.FindProperty(nameof(Arena.voxelData));
                PrefabUtility.ApplyPropertyOverride(prop, PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(arena), InteractionMode.UserAction);
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) EditorSceneManager.MarkSceneDirty(stage.scene);
            else if (arena.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(arena.gameObject.scene);
            _lastReport = "Baked: " + report + "\n→ " + path;
            Debug.Log("[SME] Arena '" + arena.name + "' baked: " + report + " → " + path);
        }

        /// <summary>Next to the arena's prefab if it has one, else Assets/Arenas.</summary>
        static string DataPath(Arena arena)
        {
            string dir = SMESetup.ArenaDir;
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            string prefabPath = stage != null && stage.IsPartOfPrefabContents(arena.gameObject)
                ? stage.assetPath
                : PrefabUtility.IsPartOfPrefabInstance(arena) ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(arena)
                : AssetDatabase.GetAssetPath(arena.gameObject);
            if (!string.IsNullOrEmpty(prefabPath)) dir = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/') ?? dir;
            string name = string.IsNullOrEmpty(prefabPath) ? arena.name : Path.GetFileNameWithoutExtension(prefabPath);
            return $"{dir}/{name}_Voxels.bytes";
        }

        // ------------------------------------------------------------------ preview

        const string PreviewName = "~Voxel Preview";

        static GameObject FindPreview(Arena arena)
        {
            var t = arena.transform.Find(PreviewName);
            return t != null ? t.gameObject : null;
        }

        static void Preview(Arena arena)
        {
            ClearPreview(arena);
            var data = arena.voxelData != null ? VoxelArenaData.FromBytes(arena.voxelData.bytes) : ArenaVoxelizer.Voxelize(arena, out _lastReport);
            var holder = new GameObject(PreviewName) { hideFlags = HideFlags.DontSave | HideFlags.NotEditable };
            holder.transform.SetParent(arena.transform, false);
            VoxelWorld.Create(holder.transform, arena.transform.position, data, false);
            foreach (var t in holder.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
            // show only the voxels (editor visibility only — nothing in the scene changes)
            foreach (var mf in Arena.VoxelSources(arena.transform)) SceneVisibilityManager.instance.Hide(mf.gameObject, false);
        }

        static void ClearPreview(Arena arena)
        {
            var p = FindPreview(arena);
            if (p != null) Object.DestroyImmediate(p);
            foreach (var mf in Arena.VoxelSources(arena.transform)) SceneVisibilityManager.instance.Show(mf.gameObject, false);
        }

        // ------------------------------------------------------------------ rotation

        static void AddToRotation(Arena arena)
        {
            GameObject prefab = null;
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.IsPartOfPrefabContents(arena.gameObject))
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(stage.assetPath);
            else if (PrefabUtility.IsPartOfPrefabInstance(arena))
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(arena));
            else if (PrefabUtility.IsPartOfPrefabAsset(arena)) prefab = arena.gameObject;
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("Sudden Mage Encounter", "Make the arena a prefab first (drag it into " + SMESetup.ArenaDir + ").", "OK");
                return;
            }
            var mm = Object.FindAnyObjectByType<MatchManager>();
            if (mm == null)
            {
                EditorUtility.DisplayDialog("Sudden Mage Encounter", "Open the game scene (with the Game object) and try again, or run Setup Scene.", "OK");
                return;
            }
            Undo.RecordObject(mm, "Add Arena");
            if (!mm.arenas.Contains(prefab)) mm.arenas.Add(prefab);
            EditorUtility.SetDirty(mm);
            EditorSceneManager.MarkSceneDirty(mm.gameObject.scene);
            _lastReport = $"'{prefab.name}' is in the rotation ({mm.arenas.Count} arena(s)). Save the scene.";
        }
    }
}
