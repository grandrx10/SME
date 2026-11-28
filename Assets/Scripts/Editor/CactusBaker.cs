using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Writes the cactus's spikes into its model (Resources/VFX/Cactus): the one spike it came with is copied round the
    /// body exactly where the game used to put the copies, as real ProBuilder objects to move, reshape or delete.
    /// With more than one spike, the game draws the model as made (see <see cref="PotModel"/>).
    /// </summary>
    public static class CactusBaker
    {
        const string Path = "Assets/Resources/VFX/Cactus.prefab";

        [MenuItem("Sudden Mage Encounter/Bake Cactus Spikes", priority = 60)]
        static void Bake()
        {
            var root = PrefabUtility.LoadPrefabContents(Path);
            try
            {
                // ProBuilder keeps no meshes in the file: build them, so the body has a size to measure
                foreach (var pb in root.GetComponentsInChildren<ProBuilderMesh>(true)) { pb.ToMesh(); pb.Refresh(); }

                var spikes = new List<Transform>();
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                    if (mf.name.StartsWith("Spike")) spikes.Add(mf.transform);
                if (spikes.Count != 1)
                {
                    EditorUtility.DisplayDialog("Bake Cactus Spikes", spikes.Count == 0
                        ? "The cactus has no part named Spike."
                        : "The cactus already has " + spikes.Count + " spikes: it's drawn as made. Delete all but one to bake again.", "OK");
                    return;
                }
                var spike = spikes[0];

                // the body: everything but the spike (as the game measures it)
                Bounds body = default;
                bool any = false;
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r.transform == spike) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (!any) { body = r.bounds; any = true; } else body.Encapsulate(r.bounds);
                }
                if (!any) { EditorUtility.DisplayDialog("Bake Cactus Spikes", "Couldn't measure the cactus's body.", "OK"); return; }

                var places = new List<(Vector3 pos, Quaternion rot)>();
                PotModel.SpikePlaces(spike, body, places);
                if (places.Count == 0) return;
                // the original spike becomes the first, copies of it the rest
                for (int i = 1; i < places.Count; i++)
                {
                    var copy = Object.Instantiate(spike.gameObject, spike.parent);
                    copy.name = "Spike (" + i + ")";
                    copy.transform.SetPositionAndRotation(places[i].pos, places[i].rot);
                    copy.transform.localScale = spike.localScale;
                    var pb = copy.GetComponent<ProBuilderMesh>();
                    if (pb != null) { pb.MakeUnique(); pb.ToMesh(); pb.Refresh(); }
                }
                spike.SetPositionAndRotation(places[0].pos, places[0].rot);

                PrefabUtility.SaveAsPrefabAsset(root, Path);
                Debug.Log("[SME] Cactus: " + places.Count + " spikes written into " + Path + ".");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
