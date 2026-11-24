using UnityEditor;
using UnityEngine;

namespace SuddenMageEncounter.EditorTools
{
    /// <summary>
    /// Sudden Mage Encounter > Hazards: puts a working example of each map hazard in the selected arena (or the only one
    /// in the scene / open prefab), sized to fit it. Move and resize what it makes; nothing needs a re-bake but signals.
    /// </summary>
    public static class SMEHazards
    {
        const string Menu = "Sudden Mage Encounter/Hazards/";

        [MenuItem(Menu + "Add Rising Water", priority = 40)]
        public static void AddWater()
        {
            if (!Target(out var arena, out var b)) return;
            float s = Mathf.Max(b.size.x, b.size.z) + 60f, depth = 4f;
            // its surface starts just under the arena
            var go = Box("Rising Water", arena, new Vector3(b.center.x, b.min.y - 1f, b.center.z), new Vector3(s, depth, s), Vector3.down * (depth * 0.5f));
            var w = go.AddComponent<RisingWater>();
            w.riseTo = Marker("Water Rises To", go.transform.parent, new Vector3(b.center.x, b.max.y, b.center.z));
            Done(go, "Add Rising Water");
        }

        /// <summary>A lava flood; instant-kill lava that's already in the arena (a Kill Volume with lava on) becomes it.</summary>
        [MenuItem(Menu + "Add Lava Flood", priority = 41)]
        public static void AddLava()
        {
            if (!Target(out var arena, out var b)) return;
            KillVolume old = null;
            foreach (var k in arena.GetComponentsInChildren<KillVolume>(true)) if (k.lava) { old = k; break; }
            GameObject go;
            if (old != null)
            {
                go = old.gameObject;
                Undo.DestroyObjectImmediate(old);
                if (go.TryGetComponent<LavaFlood>(out _)) { Selection.activeGameObject = go; return; }
            }
            else
            {
                Vector3 size = new(b.size.x, 4f, b.size.z);
                go = Box("Lava Flood", arena, new Vector3(b.center.x, b.min.y + 1f, b.center.z), size, Vector3.down * 2f);
                Undo.RegisterCreatedObjectUndo(go, "Add Lava Flood");
            }
            var lava = Undo.AddComponent<LavaFlood>(go);
            var box = go.GetComponent<BoxCollider>();
            Vector3 top = go.transform.TransformPoint(box.center + Vector3.up * (Mathf.Abs(box.size.y) * 0.5f));
            lava.riseTo = Marker("Lava Rises To", go.transform.parent, top + Vector3.up * Mathf.Max(2f, (b.max.y - top.y) * 0.35f));
            lava.fallPoint = Marker("Lava Fall", go.transform.parent, new Vector3(top.x, b.max.y + 25f, top.z));
            Undo.RegisterCreatedObjectUndo(lava.riseTo.gameObject, "Add Lava Flood");
            Undo.RegisterCreatedObjectUndo(lava.fallPoint.gameObject, "Add Lava Flood");
            EditorUtility.SetDirty(lava);
            Selection.activeGameObject = go;
        }

        [MenuItem(Menu + "Add Meteor Shower", priority = 42)]
        public static void AddMeteors()
        {
            if (!Target(out var arena, out var b)) return;
            var go = Box("Meteor Shower", arena, b.center, new Vector3(b.size.x, b.size.y + 4f, b.size.z), Vector3.zero);
            go.AddComponent<MeteorShower>();
            Done(go, "Add Meteor Shower");
        }

        [MenuItem(Menu + "Add Train Track", priority = 43)]
        public static void AddTrack()
        {
            if (!Target(out var arena, out var b)) return;
            var sv = SceneView.lastActiveSceneView;
            Vector3 at = sv != null ? sv.pivot : b.center;
            bool alongX = b.size.x >= b.size.z;
            Vector3 f = alongX ? Vector3.right : Vector3.forward;
            float half = (alongX ? b.size.x : b.size.z) * 0.5f + 2f;
            Vector3 mid = alongX ? new Vector3(b.center.x, at.y, at.z) : new Vector3(at.x, at.y, b.center.z);
            var go = new GameObject("Train Track");
            go.transform.SetParent(arena.transform, true);
            go.transform.position = mid;
            var t = go.AddComponent<TrainTrack>();
            t.frontPrefab = SMETrain.Front; // (not made yet: the built-in ones)
            t.carPrefab = SMETrain.Middle;
            t.backPrefab = SMETrain.Back;
            t.from = Marker("From", go.transform, mid - f * half);
            t.to = Marker("To", go.transform, mid + f * half);
            Done(go, "Add Train Track");
        }

        /// <summary>An example signal (post and lamp) beside the selected track, wired to it. Bake the arena after.</summary>
        [MenuItem(Menu + "Add Train Signal", priority = 44)]
        public static void AddSignal()
        {
            if (!Target(out var arena, out var b)) return;
            var track = Selection.activeTransform != null ? Selection.activeTransform.GetComponentInParent<TrainTrack>() : null;
            if (track == null) track = arena.GetComponentInChildren<TrainTrack>(true);
            var sv = SceneView.lastActiveSceneView;
            Vector3 at = sv != null ? sv.pivot : b.center;
            const float S = VoxelWorld.Size;
            // (on the voxel grid, so it bakes cleanly)
            Vector3 o = arena.transform.position;
            at = o + new Vector3(Mathf.Round((at.x - o.x) / S), Mathf.Round((at.y - o.y) / S), Mathf.Round((at.z - o.z) / S)) * S;

            var go = new GameObject("Train Signal");
            go.transform.SetParent(arena.transform, true);
            go.transform.position = at;
            Cube("Post", go.transform, new Vector3(0f, 5f * S, 0f), new Vector3(2f, 10f, 2f) * S, "Assets/SME/Black.mat");
            var lamp = Cube("Lamp", go.transform, new Vector3(0f, 12f * S, 0f), new Vector3(4f, 4f, 4f) * S, "Assets/SME/Red.mat");
            lamp.AddComponent<TrainSignal>().track = track;
            Done(go, "Add Train Signal");
        }

        // ------------------------------------------------------------------

        static GameObject Cube(string name, Transform parent, Vector3 at, Vector3 size, string material)
        {
            var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
            c.name = name;
            c.transform.SetParent(parent, false);
            c.transform.localPosition = at;
            c.transform.localScale = size;
            var m = AssetDatabase.LoadAssetAtPath<Material>(material);
            if (m != null) c.GetComponent<MeshRenderer>().sharedMaterial = m;
            return c;
        }

        static GameObject Box(string name, Arena arena, Vector3 at, Vector3 size, Vector3 center)
        {
            var go = new GameObject(name);
            go.transform.SetParent(arena.transform, true);
            go.transform.position = at;
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = size;
            box.center = center;
            return go;
        }

        static Transform Marker(string name, Transform parent, Vector3 at)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, true);
            go.transform.position = at;
            return go.transform;
        }

        static void Done(GameObject go, string what)
        {
            Undo.RegisterCreatedObjectUndo(go, what);
            Selection.activeGameObject = go;
            EditorUtility.SetDirty(go);
        }

        /// <summary>The arena to add to, and how big it is.</summary>
        static bool Target(out Arena arena, out Bounds bounds)
        {
            arena = Selection.activeTransform != null ? Selection.activeTransform.GetComponentInParent<Arena>() : null;
            if (arena == null)
            {
                var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null) arena = stage.prefabContentsRoot.GetComponentInChildren<Arena>(true);
            }
            if (arena == null)
            {
                var all = Object.FindObjectsByType<Arena>(FindObjectsSortMode.None);
                if (all.Length == 1) arena = all[0];
            }
            bounds = default;
            if (arena == null)
            {
                EditorUtility.DisplayDialog("Sudden Mage Encounter", "Select an arena (or anything in it) first.", "OK");
                return false;
            }
            bool any = false;
            foreach (var mf in Arena.VoxelSources(arena.transform))
            {
                var r = mf.GetComponent<MeshRenderer>();
                if (any) bounds.Encapsulate(r.bounds); else bounds = r.bounds;
                any = true;
            }
            if (!any) bounds = new Bounds(arena.transform.position, new Vector3(40f, 10f, 40f));
            return true;
        }
    }
}
