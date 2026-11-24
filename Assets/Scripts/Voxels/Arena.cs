using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Root of an arena prefab. Build the arena out of ProBuilder shapes (or any meshes) under this object, add
    /// SpawnPoints and KillVolumes, then press "Bake Voxels" in the inspector: every mesh is converted into voxels
    /// (colour from its material / vertex colours). At runtime the baked voxels replace the meshes.
    ///  - Add an <see cref="Indestructible"/> component to anything that must never break.
    ///  - Add a <see cref="VoxelIgnore"/> component to anything that should stay a normal mesh (decoration).
    /// Keep the root unrotated.
    /// </summary>
    [DisallowMultipleComponent]
    public class Arena : MonoBehaviour
    {
        [Tooltip("Name shown between rounds (empty = the object's name).")]
        public string displayName = "";
        [Tooltip("Baked voxels (made by Bake Voxels).")]
        public TextAsset voxelData;
        [Tooltip("Anyone falling below this height (relative to the arena root) is eliminated.")]
        public float killHeight = -25f;

        public VoxelWorld World { get; private set; }
        /// <summary>This is a practice chamber's copy; and when (server time) its hazards were set off by hand (0 = they're off).</summary>
        [System.NonSerialized] public bool isChamber;
        [System.NonSerialized] public double practiceHazards;
        /// <summary>Voxels made in code (the practice arena) instead of a baked file.</summary>
        public VoxelArenaData RuntimeData { get; set; }
        public readonly List<SpawnPoint> Spawns = new();
        /// <summary>What the camera circles while this arena builds (null = frame the whole arena).</summary>
        public BuildCameraPoint BuildCamera { get; private set; }
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? name.Replace("(Clone)", "").Trim() : displayName;
        public float KillY => transform.position.y + killHeight;

        public static Arena Current { get; private set; }

        /// <summary>Runtime: build the voxel world from the baked data and hide the source meshes.</summary>
        public void Build()
        {
            Current = this;
            GetComponentsInChildren(true, Spawns);
            BuildCamera = GetComponentInChildren<BuildCameraPoint>(true);
            var data = LoadData();
            if (data == null) return;
            HideSources();
            World = VoxelWorld.Create(transform, transform.position, data, true);
        }

        /// <summary>The arena's voxels (generated or baked), or null (with a warning) if it has none.</summary>
        public VoxelArenaData LoadData()
        {
            if (RuntimeData != null) return RuntimeData;
            if (voxelData == null)
            {
                Debug.LogWarning($"[SME] Arena '{name}' has no baked voxels: press Bake Voxels on its Arena component. Playing on the plain meshes.");
                return null;
            }
            try { return VoxelArenaData.FromBytes(voxelData.bytes); }
            catch (System.Exception e)
            {
                Debug.LogError($"[SME] Arena '{name}': couldn't read the baked voxels ({e.Message}). Re-bake it.");
                return null;
            }
        }

        /// <summary>The meshes the voxels were baked from: hidden, and no longer solid.</summary>
        public void HideSources()
        {
            foreach (var mf in VoxelSources(transform))
            {
                if (mf.TryGetComponent<Renderer>(out var r)) r.enabled = false;
                foreach (var c in mf.GetComponents<Collider>()) if (!c.isTrigger) c.enabled = false;
            }
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
        }

        /// <summary>Every mesh under the root that gets voxelized (not ignored, not a marker).</summary>
        public static List<MeshFilter> VoxelSources(Transform root)
        {
            var list = new List<MeshFilter>();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.GetComponentInParent<VoxelIgnore>(true) != null) continue;
                if (mf.GetComponentInParent<VoxelWorld>(true) != null) continue; // a preview / built world
                if (mf.GetComponent<MeshRenderer>() == null) continue;
                list.Add(mf);
            }
            return list;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.2f, 0.1f, 0.6f);
            Vector3 c = new(transform.position.x, KillY, transform.position.z);
            Gizmos.DrawWireCube(c, new Vector3(200f, 0f, 200f));
        }
    }
}
