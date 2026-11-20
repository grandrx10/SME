using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SuddenMageEncounter.EditorTools
{
    /// <summary>
    /// Sudden Mage Encounter menu: one-click scene/prefab setup, spawn points, test dummies, new arenas
    /// and the arena rotation. Everything is safe to re-run.
    /// </summary>
    [InitializeOnLoad]
    public static class SMESetup
    {
        const string PrefabPath = "Assets/Prefab/NetPlayer.prefab";
        const string DummyPath = "Assets/Prefab/TestDummy.prefab";
        public const string ArenaDir = "Assets/Arenas";
        const string Menu = "Sudden Mage Encounter/";

        static SMESetup()
        {
            // before Play: make sure every arena prefab in Assets/Arenas is in the rotation
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.ExitingEditMode) EnsureRotation(false);
            };
        }

        // ------------------------------------------------------------------ setup

        [MenuItem(Menu + "Setup Scene", priority = 0)]
        public static void Setup()
        {
            EnsureLayers();
            Directory.CreateDirectory("Assets/Prefab");
            Directory.CreateDirectory(ArenaDir);
            PlayerSettings.productName = "Sudden Mage Encounter";

            var prefab = SetupPlayerPrefab();
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("Sudden Mage Encounter",
                    "Couldn't find the player prefab (" + PrefabPath + ") or a GameObject named \"Player\" in the open scene to build it from.", "OK");
                return;
            }
            if (File.Exists(DummyPath)) RebuildDummyPrefab(prefab);

            // network manager
            var nm = Object.FindAnyObjectByType<NetworkManager>();
            if (nm == null)
            {
                var go = new GameObject("NetworkManager");
                Undo.RegisterCreatedObjectUndo(go, "NetworkManager");
                nm = go.AddComponent<NetworkManager>();
            }
            var utp = nm.GetComponent<UnityTransport>();
            if (utp == null) utp = nm.gameObject.AddComponent<UnityTransport>();
            if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = utp;
            if (utp.MaxPayloadSize < GameBootstrap.MaxPayload) utp.MaxPayloadSize = GameBootstrap.MaxPayload;
            EditorUtility.SetDirty(utp);
            nm.NetworkConfig.PlayerPrefab = prefab;
            nm.NetworkConfig.TickRate = 60;
            EditorUtility.SetDirty(nm);

            // game object: bootstrap (+ match manager, world net and UI are added at runtime if missing)
            var boot = Object.FindAnyObjectByType<GameBootstrap>();
            if (boot == null)
            {
                var go = new GameObject("Game");
                Undo.RegisterCreatedObjectUndo(go, "Game");
                boot = go.AddComponent<GameBootstrap>();
            }
            if (boot.GetComponent<MatchManager>() == null) Undo.AddComponent<MatchManager>(boot.gameObject);

            // anything left pointing at deleted Gunbound scripts
            int missing = 0;
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                missing += RemoveMissingScripts(root);

            EnsureRotation(true);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog("Sudden Mage Encounter",
                "Scene set up.\n\n• Player prefab updated\n• NetworkManager + Game object ready" +
                (missing > 0 ? $"\n• Removed {missing} missing script(s) from the scene" : "") +
                "\n\nSave the scene (Ctrl+S), then press Play.\nWith no arenas yet, rounds use the built-in practice arena.", "OK");
        }

        static GameObject SetupPlayerPrefab()
        {
            if (!File.Exists(PrefabPath))
            {
                var template = GameObject.Find("Player") ?? GameObject.Find("Player (template - disabled)");
                if (template == null) return null;
                var copy = Object.Instantiate(template);
                copy.name = "NetPlayer";
                copy.SetActive(true);
                PrefabUtility.SaveAsPrefabAsset(copy, PrefabPath);
                Object.DestroyImmediate(copy);
            }

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                RemoveMissingScripts(root);
                var gun = root.transform.Find("Gun");
                if (gun != null) Object.DestroyImmediate(gun.gameObject);
                var cap = root.transform.Find("Capsule");
                if (cap != null) Object.DestroyImmediate(cap.gameObject);
                foreach (var c in root.GetComponents<Collider>()) if (!(c is CharacterController)) Object.DestroyImmediate(c);

                root.layer = LayerMask.NameToLayer("Player");
                var cc = Get<CharacterController>(root);
                cc.height = 1.95f;
                cc.radius = 0.35f;
                cc.center = new Vector3(0, -0.945f + 1.95f * 0.5f, 0);
                cc.stepOffset = 0.55f;  // walks up two voxels
                cc.skinWidth = 0.04f;
                cc.slopeLimit = 50f;
                cc.minMoveDistance = 0f;

                Get<NetworkObject>(root);
                Get<PlayerMotor>(root);
                var pc = Get<PlayerController>(root);
                pc.model = root.transform.Find("PlayerModel");
                Get<ProceduralAnimator>(root);
                Get<AbilityCaster>(root);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        }

        static void RebuildDummyPrefab(GameObject playerPrefab)
        {
            var root = Object.Instantiate(playerPrefab);
            root.name = "TestDummy";
            Object.DestroyImmediate(root.GetComponent<AbilityCaster>());
            Object.DestroyImmediate(root.GetComponent<PlayerController>());
            Object.DestroyImmediate(root.GetComponent<PlayerMotor>());
            Object.DestroyImmediate(root.GetComponent<CharacterController>());
            var dummy = Get<TestDummy>(root);
            dummy.model = root.transform.Find("PlayerModel");
            var col = Get<CapsuleCollider>(root);
            col.height = 1.95f;
            col.radius = 0.35f;
            col.center = new Vector3(0, -0.945f + 1.95f * 0.5f, 0);
            int playerLayer = LayerMask.NameToLayer("Player");
            if (playerLayer >= 0) root.layer = playerLayer;
            PrefabUtility.SaveAsPrefabAsset(root, DummyPath);
            Object.DestroyImmediate(root);
        }

        static int RemoveMissingScripts(GameObject go)
        {
            int n = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            foreach (Transform c in go.transform) n += RemoveMissingScripts(c.gameObject);
            return n;
        }

        // ------------------------------------------------------------------ placing things

        [MenuItem(Menu + "Add Spawn Point", priority = 20)]
        public static void AddSpawnPoint()
        {
            Vector3 pos = Vector3.zero;
            float yaw = 0;
            var sv = SceneView.lastActiveSceneView;
            if (sv != null)
            {
                pos = sv.pivot;
                if (sv.camera != null)
                {
                    Vector3 f = sv.camera.transform.forward; f.y = 0;
                    if (f.sqrMagnitude > 0.001f) yaw = Quaternion.LookRotation(f).eulerAngles.y;
                }
            }
            if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out var hit, 200f))
                pos = hit.point + Vector3.up * 0.97f; // the player's root sits at the hips

            // inside the selected arena (or the only one in the scene)
            Transform parent = Selection.activeTransform != null ? Selection.activeTransform.GetComponentInParent<Arena>()?.transform : null;
            if (parent == null)
            {
                var arenas = Object.FindObjectsByType<Arena>(FindObjectsSortMode.None);
                if (arenas.Length == 1) parent = arenas[0].transform;
            }
            var go = new GameObject("SpawnPoint");
            if (parent != null) go.transform.SetParent(parent, true);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
            go.AddComponent<SpawnPoint>();
            Undo.RegisterCreatedObjectUndo(go, "Add Spawn Point");
            Selection.activeGameObject = go;
        }

        [MenuItem(Menu + "Add Kill Volume", priority = 21)]
        public static void AddKillVolume()
        {
            Vector3 pos = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
            var go = new GameObject("KillVolume");
            var parent = Selection.activeTransform != null ? Selection.activeTransform.GetComponentInParent<Arena>() : null;
            if (parent != null) go.transform.SetParent(parent.transform, true);
            go.transform.position = pos;
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(10, 2, 10);
            go.AddComponent<KillVolume>();
            Undo.RegisterCreatedObjectUndo(go, "Add Kill Volume");
            Selection.activeGameObject = go;
        }

        [MenuItem(Menu + "Add Test Dummy", priority = 22)]
        public static void AddTestDummy()
        {
            var dummyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DummyPath);
            if (dummyPrefab == null)
            {
                var player = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                if (player == null)
                {
                    EditorUtility.DisplayDialog("Sudden Mage Encounter", "Run Sudden Mage Encounter > Setup Scene first.", "OK");
                    return;
                }
                RebuildDummyPrefab(player);
                dummyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DummyPath);
            }
            Vector3 pos = Vector3.zero;
            float yaw = 0;
            var sv = SceneView.lastActiveSceneView;
            if (sv != null)
            {
                pos = sv.pivot;
                Vector3 camFwd = sv.camera != null ? sv.camera.transform.forward : Vector3.forward;
                Vector3 back = new(-camFwd.x, 0, -camFwd.z);
                yaw = Quaternion.LookRotation(back.sqrMagnitude > 0.001f ? back : Vector3.back).eulerAngles.y;
            }
            if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out var hit, 200f))
                pos = hit.point + Vector3.up * 0.97f;
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(dummyPrefab);
            inst.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
            Undo.RegisterCreatedObjectUndo(inst, "Add Test Dummy");
            Selection.activeGameObject = inst;
        }

        // ------------------------------------------------------------------ arenas

        [MenuItem(Menu + "New Arena", priority = 40)]
        public static void NewArena()
        {
            var root = new GameObject("New Arena");
            Undo.RegisterCreatedObjectUndo(root, "New Arena");
            var arena = root.AddComponent<Arena>();
            arena.displayName = "";
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Ground";
            floor.transform.SetParent(root.transform, false);
            floor.transform.localScale = new Vector3(40f, 2f, 40f);
            floor.transform.localPosition = new Vector3(0f, -1f, 0f);
            var bedrock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bedrock.name = "Bedrock (indestructible)";
            bedrock.transform.SetParent(root.transform, false);
            bedrock.transform.localScale = new Vector3(40f, 0.5f, 40f);
            bedrock.transform.localPosition = new Vector3(0f, -2.25f, 0f);
            bedrock.AddComponent<Indestructible>();
            var bedMat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Bedrock" };
            bedMat.SetColor("_BaseColor", new Color(0.22f, 0.22f, 0.25f));
            Directory.CreateDirectory(ArenaDir);
            string matPath = AssetDatabase.GenerateUniqueAssetPath(ArenaDir + "/Bedrock.mat");
            AssetDatabase.CreateAsset(bedMat, matPath);
            bedrock.GetComponent<Renderer>().sharedMaterial = bedMat;
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f + Mathf.PI * 0.25f;
                var sp = new GameObject("SpawnPoint " + (i + 1)).AddComponent<SpawnPoint>();
                sp.transform.SetParent(root.transform, false);
                Vector3 p = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 12f;
                sp.transform.localPosition = p + Vector3.up * 0.97f;
                sp.transform.rotation = Quaternion.LookRotation(-p);
            }
            Selection.activeGameObject = root;
            EditorUtility.DisplayDialog("Sudden Mage Encounter",
                "New arena created.\n\n1. Build it with ProBuilder (or any meshes) as children of \"New Arena\".\n" +
                "2. Drag it into Assets/Arenas to make it a prefab.\n3. Press Bake Voxels on its Arena component.\n\n" +
                "Arena prefabs in Assets/Arenas join the round rotation automatically.", "OK");
        }

        [MenuItem(Menu + "Refresh Arena Rotation", priority = 41)]
        public static void RefreshRotation()
        {
            var mm = EnsureRotation(true);
            if (mm == null)
            {
                EditorUtility.DisplayDialog("Sudden Mage Encounter", "Run Sudden Mage Encounter > Setup Scene first.", "OK");
                return;
            }
            EditorUtility.DisplayDialog("Sudden Mage Encounter",
                mm.arenas.Count == 0
                    ? "No arena prefabs in " + ArenaDir + " yet: rounds use the built-in practice arena."
                    : $"Arena rotation ({mm.arenas.Count}):\n• " + string.Join("\n• ", mm.arenas.ConvertAll(a => a.name)) + "\n\nSave the scene.", "OK");
        }

        /// <summary>Every prefab in Assets/Arenas with an Arena component goes in the Match Manager's list.</summary>
        public static MatchManager EnsureRotation(bool force)
        {
            var boot = Object.FindAnyObjectByType<GameBootstrap>();
            var mm = boot != null ? boot.GetComponent<MatchManager>() : Object.FindAnyObjectByType<MatchManager>();
            if (mm == null) return null;
            var found = new List<GameObject>();
            if (Directory.Exists(ArenaDir))
                foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { ArenaDir }))
                {
                    var p = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                    if (p != null && p.GetComponent<Arena>() != null) found.Add(p);
                }
            found.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            mm.arenas.RemoveAll(a => a == null);
            bool missing = found.Exists(p => !mm.arenas.Contains(p));
            if (!force && !missing) return mm;
            Undo.RecordObject(mm, "Arena Rotation");
            foreach (var p in found) if (!mm.arenas.Contains(p)) mm.arenas.Add(p);
            EditorUtility.SetDirty(mm);
            EditorSceneManager.MarkSceneDirty(mm.gameObject.scene);
            return mm;
        }

        // ------------------------------------------------------------------ helpers

        static T Get<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        static void EnsureLayers()
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            string[] wanted = { Layers.PlayerName, Layers.HitboxName, Layers.RagdollName, Layers.FxName };
            int[] preferred = { 8, 9, 10, 11 };
            for (int i = 0; i < wanted.Length; i++)
            {
                if (LayerMask.NameToLayer(wanted[i]) >= 0) continue;
                int slot = -1;
                var p = layers.GetArrayElementAtIndex(preferred[i]);
                if (string.IsNullOrEmpty(p.stringValue)) slot = preferred[i];
                else
                    for (int l = 8; l < 32; l++)
                        if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(l).stringValue)) { slot = l; break; }
                if (slot < 0) continue;
                layers.GetArrayElementAtIndex(slot).stringValue = wanted[i];
                tagManager.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }
}
