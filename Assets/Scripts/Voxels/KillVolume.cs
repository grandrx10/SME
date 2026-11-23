using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Kill barrier: anyone whose body enters this box is eliminated. Give it a BoxCollider (set as a trigger
    /// automatically) and size it to cover the area. Put it inside the arena prefab. Keep it upright (rotate it about Y only).
    /// <para>
    /// With <see cref="lava"/> on, the top of the box is a glowing lava surface (SME/Lava): touching it kills, and dead
    /// bodies float on it (<see cref="LavaFloat"/>). Make the box's top the lava level and sink the rest into the pit.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    [AddComponentMenu("Sudden Mage Encounter/Kill Volume")]
    public class KillVolume : MonoBehaviour
    {
        public static readonly List<KillVolume> All = new();
        static int _lavaCount;

        [Tooltip("Draw a lava surface along the top of the box. Touching it kills; dead bodies float on it.")]
        public bool lava;

        /// <summary>
        /// How far above a lava surface still counts as touching it. Players (and dummies) are checked at feet + 0.2 m,
        /// so this kills the moment their feet reach the surface that's drawn.
        /// </summary>
        public const float LavaReach = 0.2f;

        BoxCollider _box;
        GameObject _surface;
        bool _countedLava;

        public static bool AnyLava => _lavaCount > 0;

        void Awake()
        {
            _box = GetComponent<BoxCollider>();
            _box.isTrigger = true;
            gameObject.layer = 2; // Ignore Raycast: spells and cameras pass through it
        }

        void OnEnable()
        {
            All.Add(this);
            _countedLava = lava;
            if (lava)
            {
                _lavaCount++;
                if (Application.isPlaying) BuildSurface();
            }
        }

        void OnDisable()
        {
            All.Remove(this);
            if (_countedLava) _lavaCount--;
            _countedLava = false;
            if (_surface != null) Destroy(_surface);
        }

        public bool Contains(Vector3 p)
        {
            if (_box == null) return false;
            Vector3 local = transform.InverseTransformPoint(p) - _box.center;
            Vector3 h = _box.size * 0.5f;
            float top = h.y + (lava ? LavaReach / Mathf.Max(1e-4f, transform.lossyScale.y) : 0f);
            return Mathf.Abs(local.x) <= h.x && local.y <= top && local.y >= -h.y && Mathf.Abs(local.z) <= h.z;
        }

        public static bool AnyContains(Vector3 p)
        {
            foreach (var k in All) if (k != null && k.Contains(p)) return true;
            return false;
        }

        /// <summary>World height of this box's top (the lava surface).</summary>
        public float TopY => _box == null ? transform.position.y : transform.TransformPoint(_box.center + Vector3.up * _box.size.y * 0.5f).y;

        /// <summary>If <paramref name="p"/> is in (or just over) a lava volume, the height of its surface.</summary>
        public static bool LavaSurfaceAt(Vector3 p, out float surfaceY)
        {
            foreach (var k in All)
            {
                if (k == null || !k.lava || k._box == null) continue;
                Vector3 local = k.transform.InverseTransformPoint(p) - k._box.center;
                Vector3 h = k._box.size * 0.5f;
                if (Mathf.Abs(local.x) > h.x || Mathf.Abs(local.z) > h.z || local.y < -h.y) continue;
                surfaceY = k.TopY;
                if (p.y > surfaceY + 1f) continue;
                return true;
            }
            surfaceY = 0f;
            return false;
        }

        // ------------------------------------------------------------------ lava surface

        static Mesh _quad;
        static Material _lavaMat;

        /// <summary>A flat 1×1 quad on XZ, facing up.</summary>
        static Mesh Quad
        {
            get
            {
                if (_quad != null) return _quad;
                _quad = new Mesh { name = "LavaQuad" };
                _quad.SetVertices(new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(0.5f, 0, -0.5f) });
                _quad.SetNormals(new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
                _quad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
                _quad.RecalculateBounds();
                return _quad;
            }
        }

        static Material LavaMaterial
        {
            get
            {
                if (_lavaMat != null) return _lavaMat;
                var s = Shader.Find("SME/Lava");
                if (s == null)
                {
                    Debug.LogWarning("[SME] SME/Lava shader missing; lava uses a fallback shader.");
                    s = Shader.Find("Universal Render Pipeline/Unlit");
                }
                _lavaMat = new Material(s) { name = "Lava" };
                return _lavaMat;
            }
        }

        void BuildSurface()
        {
            if (_surface != null || _box == null) return;
            _surface = new GameObject("LavaSurface") { layer = 2, hideFlags = HideFlags.DontSave };
            _surface.AddComponent<VoxelIgnore>();
            _surface.transform.SetParent(transform, false);
            _surface.transform.localPosition = _box.center + Vector3.up * _box.size.y * 0.5f;
            _surface.transform.localRotation = Quaternion.identity;
            _surface.transform.localScale = new Vector3(_box.size.x, 1f, _box.size.z);
            _surface.AddComponent<MeshFilter>().sharedMesh = Quad;
            var mr = _surface.AddComponent<MeshRenderer>();
            mr.sharedMaterial = LavaMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        void OnDrawGizmos()
        {
            var b = GetComponent<BoxCollider>();
            if (b == null) return;
            Gizmos.matrix = transform.localToWorldMatrix;
            if (lava)
            {
                // the surface, solid, where it will be drawn in play
                Vector3 top = b.center + Vector3.up * b.size.y * 0.5f;
                Gizmos.color = new Color(1f, 0.45f, 0.05f, 0.85f);
                Gizmos.DrawCube(top, new Vector3(b.size.x, 0.01f, b.size.z));
                Gizmos.color = new Color(1f, 0.3f, 0.05f, 0.12f);
                Gizmos.DrawCube(b.center, b.size);
                Gizmos.color = new Color(1f, 0.3f, 0.05f, 0.8f);
                Gizmos.DrawWireCube(b.center, b.size);
                return;
            }
            Gizmos.color = new Color(1f, 0.1f, 0.1f, 0.18f);
            Gizmos.DrawCube(b.center, b.size);
            Gizmos.color = new Color(1f, 0.1f, 0.1f, 0.8f);
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }
}
