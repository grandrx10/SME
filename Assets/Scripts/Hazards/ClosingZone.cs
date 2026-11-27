using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Map hazard: a red border round this point that shrinks to nothing. Being outside it hurts as the rising water does:
    /// every second out there (added up) twice as much as the last (SpellServer.Hazards). Height doesn't matter.
    /// Leave its scale at 1.
    /// </summary>
    [AddComponentMenu("Sudden Mage Encounter/Closing Zone")]
    public class ClosingZone : MonoBehaviour
    {
        static readonly List<ClosingZone> All = new();

        [Tooltip("Seconds after FIGHT that it starts closing.")]
        public float startAfter = 120f;
        [Tooltip("Seconds from then until there's nowhere left that's safe.")]
        public float closeSeconds = 60f;
        [Tooltip("How far out it starts (m): all the way round the arena.")]
        public float radius = 40f;
        [Tooltip("How far the border goes up and down from here (only a look).")]
        public float height = 40f;

        Arena _home;

        void Awake() => _home = GetComponentInParent<Arena>();
        void OnEnable() => All.Add(this);

        void OnDisable()
        {
            All.Remove(this);
            if (_wall != null) _wall.gameObject.SetActive(false);
            if (_veil != null) _veil.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (_veil != null) Destroy(_veil.gameObject);
        }

        /// <summary>Is it closing? How big the safe circle is now.</summary>
        bool Out(out float r)
        {
            r = radius;
            if (!MapClock.Live(_home)) return false;
            float t = MapClock.Since(_home, startAfter);
            if (t < 0f) return false;
            r = radius * (1f - Mathf.Clamp01(t / Mathf.Max(1f, closeSeconds)));
            return true;
        }

        bool Caught(Vector3 p)
        {
            if (!Out(out float r) || Practice.SlotAt(p) != Practice.SlotAt(transform.position)) return false; // (only its own arena)
            Vector3 d = p - transform.position;
            return d.x * d.x + d.z * d.z > r * r;
        }

        /// <summary>Out of the safe circle here?</summary>
        public static bool InAny(Vector3 p)
        {
            foreach (var z in All) if (z.Caught(p)) return true;
            return false;
        }

        public static bool Any => All.Count > 0;

        // ------------------------------------------------------------------ looks

        Transform _wall, _veil;
        static Material _mat;
        static Mesh _tube, _quad;
        const int Around = 128;

        static Material Look
        {
            get
            {
                if (_mat != null) return _mat;
                var s = Shader.Find("SME/Zone");
                if (s != null) return _mat = new Material(s) { name = "Zone" };
                Debug.LogWarning("[SME] SME/Zone shader missing; the closing zone uses a fallback shader.");
                return _mat = new Material(Shader.Find("Sprites/Default")) { name = "Zone", color = new Color(1f, 0.1f, 0.1f, 0.35f) };
            }
        }

        /// <summary>An open tube: radius 1, from y = -1 to 1.</summary>
        static Mesh Tube
        {
            get
            {
                if (_tube != null) return _tube;
                var v = new Vector3[(Around + 1) * 2];
                var n = new Vector3[v.Length];
                var t = new int[Around * 6];
                for (int i = 0; i <= Around; i++)
                {
                    float a = i * Mathf.PI * 2f / Around;
                    Vector3 d = new(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    v[i * 2] = d + Vector3.down;
                    v[i * 2 + 1] = d + Vector3.up;
                    n[i * 2] = n[i * 2 + 1] = d;
                    if (i == Around) break;
                    int k = i * 6, b = i * 2;
                    t[k] = b; t[k + 1] = b + 1; t[k + 2] = b + 3;
                    t[k + 3] = b; t[k + 4] = b + 3; t[k + 5] = b + 2;
                }
                return _tube = new Mesh { name = "Zone", vertices = v, normals = n, triangles = t };
            }
        }

        /// <summary>A red pane over the screen of anyone looking from out in the zone.</summary>
        static Mesh Pane
        {
            get
            {
                if (_quad != null) return _quad;
                Color c = new(1f, 0.04f, 0.06f, 0.22f);
                return _quad = new Mesh
                {
                    name = "Zone veil",
                    vertices = new[] { new Vector3(-0.5f, -0.5f), new Vector3(-0.5f, 0.5f), new Vector3(0.5f, 0.5f), new Vector3(0.5f, -0.5f) },
                    uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right },
                    colors = new[] { c, c, c, c },
                    triangles = new[] { 0, 1, 2, 0, 2, 3 },
                };
            }
        }

        static Transform Part(string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name) { layer = Layers.Fx, hideFlags = HideFlags.DontSave };
            go.AddComponent<VoxelIgnore>();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        void LateUpdate()
        {
            bool on = Out(out float r), wall = on && r > 0.05f;
            if (wall && _wall == null)
            {
                _wall = Part("ZoneWall", Tube, Look);
                _wall.SetParent(transform, false);
            }
            if (_wall != null)
            {
                if (_wall.gameObject.activeSelf != wall) _wall.gameObject.SetActive(wall);
                if (wall) _wall.localScale = new Vector3(r, height, r);
            }

            var cam = Camera.main;
            bool veil = on && cam != null && Caught(cam.transform.position);
            if (veil && _veil == null) _veil = Part("ZoneVeil", Pane, VoxelRender.Ghost);
            if (_veil == null) return;
            if (_veil.gameObject.activeSelf != veil) _veil.gameObject.SetActive(veil);
            if (!veil) return;
            float far = cam.nearClipPlane + 0.02f, tall = 2.2f * far * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            // (on the camera, so it's there whichever moves last)
            if (_veil.parent != cam.transform) _veil.SetParent(cam.transform, false);
            _veil.localPosition = Vector3.forward * far;
            _veil.localRotation = Quaternion.identity;
            _veil.localScale = new Vector3(tall * cam.aspect, tall, 1f);
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.1f, 0.1f, 0.9f);
            Vector3 last = transform.position + Vector3.right * radius;
            for (int i = 1; i <= 64; i++)
            {
                float a = i * Mathf.PI * 2f / 64f;
                Vector3 p = transform.position + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
                Gizmos.DrawLine(last, p);
                last = p;
            }
        }
    }
}
