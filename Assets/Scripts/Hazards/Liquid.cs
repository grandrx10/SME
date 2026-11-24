using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// A box of liquid with a level that can rise (<see cref="RisingWater"/>, <see cref="LavaFlood"/>). The object's Box
    /// Collider says where: its sides and bottom are the liquid's, and its top is the level it starts at. The object's
    /// own mesh (if any) is only a stand-in while editing: in play the liquid is drawn instead. Rotate it about Y only.
    /// </summary>
    [RequireComponent(typeof(BoxCollider), typeof(VoxelIgnore))]
    public abstract class Liquid : MonoBehaviour
    {
        public static readonly List<Liquid> All = new();

        BoxCollider _box;
        Transform _surface;
        readonly HashSet<Combatant> _in = new();
        protected Arena Home { get; private set; }

        /// <summary>Seconds into the round that it sets off, and the world height of its surface this long after that (negative: not yet).</summary>
        protected abstract float Wait { get; }
        protected abstract float LevelAt(float t);
        protected abstract Material Look { get; }
        /// <summary>Someone just went in (local look).</summary>
        protected abstract void Entered(Vector3 at, float speed);
        protected abstract Color GizmoColour { get; }

        protected virtual void Awake()
        {
            _box = GetComponent<BoxCollider>();
            Home = GetComponentInParent<Arena>();
            foreach (var c in GetComponents<Collider>()) if (c != _box) c.enabled = false;
            _box.isTrigger = true;
            gameObject.layer = 2; // Ignore Raycast: spells and cameras pass through it
            if (TryGetComponent<Renderer>(out var r)) r.enabled = false;
        }

        protected virtual void OnEnable() => All.Add(this);

        protected virtual void OnDisable()
        {
            All.Remove(this);
            _in.Clear();
        }

        Vector3 Half => new(Mathf.Abs(_box.size.x) * 0.5f, Mathf.Abs(_box.size.y) * 0.5f, Mathf.Abs(_box.size.z) * 0.5f);
        /// <summary>World height it starts at (the box's top), and of its bottom.</summary>
        public float BaseY => transform.TransformPoint(_box.center + Vector3.up * Half.y).y;
        public float BottomY => transform.TransformPoint(_box.center - Vector3.up * Half.y).y;
        /// <summary>World height of the surface right now.</summary>
        public float Level => MapClock.Live(Home) ? Mathf.Max(BaseY, LevelAt(MapClock.Since(Home, Wait))) : BaseY;

        public bool Over(Vector3 p)
        {
            Vector3 l = transform.InverseTransformPoint(p) - _box.center, h = Half;
            return Mathf.Abs(l.x) <= h.x && Mathf.Abs(l.z) <= h.z;
        }

        public bool Contains(Vector3 p, float pad) => p.y >= BottomY - pad && p.y <= Level + pad && Over(p);

        /// <summary>Is this body touching it (its feet, or its middle when it's lying limp)?</summary>
        public bool Touches(Combatant c)
        {
            Vector3 hips = c.transform.position;
            Vector3 low = c.HasLiveRagdoll ? hips + Vector3.down * 0.35f : c.FeetPosition;
            return Contains(low + Vector3.up * 0.02f, 0.03f) || Contains(hips, 0f);
        }

        /// <summary>Height of the liquid's surface here, if <paramref name="p"/> is in (or just over) some.</summary>
        public static bool SurfaceAt(Vector3 p, out float surface)
        {
            foreach (var l in All)
                if (l.Contains(p, 1f)) { surface = l.Level; return true; }
            surface = 0f;
            return false;
        }

        // ------------------------------------------------------------------ looks

        protected virtual void LateUpdate()
        {
            if (_surface == null) Build();
            float level = Level;
            Vector3 mid = transform.TransformPoint(_box.center);
            mid.y = level;
            _surface.SetPositionAndRotation(mid, Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
            Vector3 s = transform.lossyScale, h = Half;
            float sy = Mathf.Max(1e-4f, Mathf.Abs(s.y));
            _surface.localScale = new Vector3(h.x * 2f, Mathf.Max(0.01f, level - BottomY) / sy, h.z * 2f);

            foreach (var c in Combatant.Registry)
            {
                if (c == null) continue;
                bool touching = c.IsAlive && c.Visible && Touches(c);
                if (!touching) { _in.Remove(c); continue; }
                if (!_in.Add(c)) continue;
                Vector3 at = c.transform.position;
                at.y = level;
                Entered(at, Mathf.Abs(c.ViewVelocity.y));
            }
            _in.RemoveWhere(c => c == null);
        }

        void Build()
        {
            var go = new GameObject("Surface") { layer = 2, hideFlags = HideFlags.DontSave };
            go.AddComponent<VoxelIgnore>();
            _surface = go.transform;
            _surface.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = Block;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Look;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        static Mesh _block;

        /// <summary>A 1×1×1 block hanging down from y = 0: its top and its four sides.</summary>
        static Mesh Block
        {
            get
            {
                if (_block != null) return _block;
                var v = new List<Vector3>();
                var n = new List<Vector3>();
                var t = new List<int>();
                // corners clockwise as seen from outside (bottom left, top left, top right, bottom right)
                void Face(Vector3 c, Vector3 right, Vector3 up, Vector3 normal)
                {
                    int k = v.Count;
                    v.Add(c - right - up); v.Add(c - right + up); v.Add(c + right + up); v.Add(c + right - up);
                    for (int i = 0; i < 4; i++) n.Add(normal);
                    t.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3 });
                }
                Face(Vector3.zero, Vector3.right * 0.5f, Vector3.forward * 0.5f, Vector3.up);
                foreach (var d in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
                    Face(d * 0.5f + Vector3.down * 0.5f, Vector3.Cross(Vector3.up, -d) * 0.5f, Vector3.up * 0.5f, d);
                _block = new Mesh { name = "Liquid" };
                _block.SetVertices(v);
                _block.SetNormals(n);
                _block.SetTriangles(t, 0);
                _block.RecalculateBounds();
                return _block;
            }
        }

        protected virtual void OnDrawGizmos()
        {
            var b = GetComponent<BoxCollider>();
            if (b == null) return;
            Color c = GizmoColour;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(c.r, c.g, c.b, 0.12f);
            Gizmos.DrawCube(b.center, b.size);
            Gizmos.color = new Color(c.r, c.g, c.b, 0.8f);
            Gizmos.DrawWireCube(b.center, b.size);
            Gizmos.DrawCube(b.center + Vector3.up * (Mathf.Abs(b.size.y) * 0.5f), new Vector3(b.size.x, 0.01f, b.size.z));
            Gizmos.matrix = Matrix4x4.identity;
        }

        /// <summary>The level it rises to, drawn in the editor.</summary>
        protected void DrawLevel(float y)
        {
            var b = GetComponent<BoxCollider>();
            if (b == null) return;
            Vector3 mid = transform.TransformPoint(b.center);
            mid.y = y;
            Gizmos.matrix = Matrix4x4.TRS(mid, Quaternion.Euler(0f, transform.eulerAngles.y, 0f), Vector3.one);
            Vector3 s = transform.lossyScale;
            Gizmos.color = GizmoColour;
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(Mathf.Abs(b.size.x * s.x), 0.01f, Mathf.Abs(b.size.z * s.z)));
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
