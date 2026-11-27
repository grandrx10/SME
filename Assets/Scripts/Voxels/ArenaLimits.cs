using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// How far out the arena goes: anyone who leaves it (sideways, or over the top) is warned, then shattered (PlayerController.OutsideTime). Put one or more of
    /// these boxes inside an arena prefab to say where that is (being in any of them is fine; their bottoms don't count, the
    /// arena's kill height does). An arena without any goes as far as its voxels did when it loaded, and <see cref="Margin"/>
    /// beyond them.
    /// </summary>
    [RequireComponent(typeof(BoxCollider), typeof(VoxelIgnore))]
    [AddComponentMenu("Sudden Mage Encounter/Arena Limits")]
    public class ArenaLimits : MonoBehaviour
    {
        /// <summary>Metres past the arena's voxels that still count as in it.</summary>
        public const float Margin = 10f;

        static readonly List<ArenaLimits> All = new();
        BoxCollider _box;

        void Awake()
        {
            _box = GetComponent<BoxCollider>();
            _box.isTrigger = true;
            gameObject.layer = 2; // Ignore Raycast
            if (TryGetComponent<Renderer>(out var r)) r.enabled = false;
        }

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        bool Over(Vector3 p, bool top)
        {
            Vector3 l = transform.InverseTransformPoint(p) - _box.center, h = _box.size * 0.5f;
            return Mathf.Abs(l.x) <= Mathf.Abs(h.x) && Mathf.Abs(l.z) <= Mathf.Abs(h.z) && (!top || l.y <= Mathf.Abs(h.y));
        }

        /// <summary>Out of the arena (or the practice chamber) it's nearest?</summary>
        /// <param name="inset">Counted as out this much closer in (m).</param>
        /// <param name="top">Being over the top of the boxes counts too.</param>
        public static bool Outside(Vector3 p, float inset = 0f, bool top = true)
        {
            var w = VoxelWorld.Current;
            if (w == null) return false;
            int slot = Practice.SlotAt(p);
            if (!Practice.ChamberBounds(slot, out var b)) { slot = 0; b = w.ArenaBounds; }
            bool any = false;
            foreach (var a in All)
            {
                if (a == null || a._box == null || Practice.SlotAt(a.transform.position) != slot) continue;
                any = true;
                if (a.Over(p, top)) return false;
            }
            if (any) return true;
            if (b.size.x < 1f || b.size.z < 1f) return false;
            float m = Margin - inset;
            return p.x < b.min.x - m || p.x > b.max.x + m || p.z < b.min.z - m || p.z > b.max.z + m;
        }

        void OnDrawGizmos()
        {
            var b = GetComponent<BoxCollider>();
            if (b == null) return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.8f);
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }
}
