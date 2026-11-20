using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Kill barrier: anyone whose body enters this box is eliminated. Give it a BoxCollider (set as a trigger
    /// automatically) and size it to cover the area. Put it inside the arena prefab.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    [AddComponentMenu("Sudden Mage Encounter/Kill Volume")]
    public class KillVolume : MonoBehaviour
    {
        public static readonly List<KillVolume> All = new();
        BoxCollider _box;

        void Awake()
        {
            _box = GetComponent<BoxCollider>();
            _box.isTrigger = true;
            gameObject.layer = 2; // Ignore Raycast: spells and cameras pass through it
        }

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        public bool Contains(Vector3 p)
        {
            if (_box == null) return false;
            Vector3 local = transform.InverseTransformPoint(p) - _box.center;
            Vector3 h = _box.size * 0.5f;
            return Mathf.Abs(local.x) <= h.x && Mathf.Abs(local.y) <= h.y && Mathf.Abs(local.z) <= h.z;
        }

        public static bool AnyContains(Vector3 p)
        {
            foreach (var k in All) if (k != null && k.Contains(p)) return true;
            return false;
        }

        void OnDrawGizmos()
        {
            var b = GetComponent<BoxCollider>();
            if (b == null) return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.1f, 0.1f, 0.18f);
            Gizmos.DrawCube(b.center, b.size);
            Gizmos.color = new Color(1f, 0.1f, 0.1f, 0.8f);
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }
}
