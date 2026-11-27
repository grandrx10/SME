using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Map hazard: two walls of spikes that close in from both ends of this box (its local Z) until they meet in the
    /// middle. Anyone who touches one, spikes and all, is hurt, knocked down and launched away from it
    /// (SpellServer.Hazards). Ground that spells put in the way is smashed; the map's own they go straight through. The
    /// spikes start behind the ends of the box: put those on the faces of the room's end walls, and its sides out past
    /// anywhere a body can get to. Until they set off they're not there at all.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    [AddComponentMenu("Sudden Mage Encounter/Spike Walls")]
    public class SpikeWalls : MonoBehaviour
    {
        public static readonly List<SpikeWalls> All = new();

        [Tooltip("Seconds after FIGHT that they start closing.")]
        public float startAfter = 30f;
        [Tooltip("Seconds from then until they meet in the middle.")]
        public float closeSeconds = 150f;

        BoxCollider _box;
        Arena _home;

        void Awake()
        {
            _box = GetComponent<BoxCollider>();
            _box.isTrigger = true;
            gameObject.layer = 2; // Ignore Raycast
            _home = GetComponentInParent<Arena>();
            foreach (var c in GetComponents<Collider>()) if (c != _box) c.enabled = false;
            if (TryGetComponent<Renderer>(out var r)) r.enabled = false;
        }

        void OnEnable() => All.Add(this);

        void OnDisable()
        {
            All.Remove(this);
            foreach (var w in _walls) if (w != null && w.go != null) w.go.SetActive(false);
        }

        void OnDestroy()
        {
            foreach (var w in _walls)
            {
                if (w == null) continue;
                Solid.Remove(w.box);
                foreach (var m in w.meshes) Destroy(m);
                if (w.go != null) Destroy(w.go);
            }
        }

        Vector3 Half
        {
            get
            {
                Vector3 h = Vector3.Scale(_box.size, transform.lossyScale) * 0.5f;
                return new Vector3(Mathf.Abs(h.x), Mathf.Abs(h.y), Mathf.Abs(h.z));
            }
        }
        Vector3 Mid => transform.TransformPoint(_box.center);
        Vector3 Local(Vector3 p) => Quaternion.Inverse(transform.rotation) * (p - Mid);

        /// <summary>Are the walls out? How far from the middle their flat faces are now (the spikes stand out from those).</summary>
        bool Out(out float face)
        {
            face = 0f;
            if (!MapClock.Live(_home)) return false;
            float t = MapClock.Since(_home, startAfter);
            if (t < 0f) return false;
            // closed: a spike's length apart, the spikes of each right up against the other
            face = Mathf.Lerp(Half.z + SpellPlans.SpikeLength, SpellPlans.SpikeLength * 0.5f, t / Mathf.Max(1f, closeSeconds));
            return true;
        }

        const float BodyRadius = 0.4f;

        /// <summary>Is this body touching one of the walls? And the way that wall throws it.</summary>
        public bool Touches(Combatant c, out Vector3 away)
        {
            away = default;
            if (!Out(out float face)) return false;
            Vector3 h = Half;
            float tips = face - SpellPlans.SpikeLength - BodyRadius;
            for (int k = 0; k < (c.HasLiveRagdoll ? 1 : 3); k++)
            {
                Vector3 l = Local(c.transform.position + Vector3.up * (k == 0 ? 0f : k == 1 ? -0.6f : 0.6f));
                if (Mathf.Abs(l.x) > h.x || Mathf.Abs(l.y) > h.y || Mathf.Abs(l.z) > h.z + SpellPlans.SpikeLength || Mathf.Abs(l.z) < tips) continue;
                away = transform.rotation * (l.z < 0f ? Vector3.forward : Vector3.back);
                return true;
            }
            return false;
        }

        public Vector3 Sideways => transform.right;

        /// <summary>Server: breaks any ground spells have put where the walls (and their spikes) now are.</summary>
        public void ServerSmash()
        {
            var w = VoxelWorld.Current;
            if (w == null || w.SummonedCount == 0 || !Out(out float face)) return;
            Vector3 h = Half;
            float tips = Mathf.Max(0f, face - SpellPlans.SpikeLength);
            for (int side = -1; side <= 1; side += 2)
            {
                int s = side;
                var cells = w.SummonedWhere(p =>
                {
                    Vector3 l = Local(p);
                    return l.z * s >= tips && l.z * s <= h.z && Mathf.Abs(l.x) <= h.x && Mathf.Abs(l.y) <= h.y;
                });
                cells.RemoveAll(c => w.IsIndestructible(w.Get(c)));
                if (cells.Count > 0) WorldNet.ServerWash(cells, transform.rotation * Vector3.back * s);
            }
        }

        // ------------------------------------------------------------------ looks (and the solid walls)

        class Wall
        {
            public GameObject go; public Rigidbody rb; public BoxCollider box; public Transform mass;
            public Mesh[] meshes;
        }

        readonly Wall[] _walls = new Wall[2];
        static readonly HashSet<Collider> Solid = new();

        /// <summary>Is this one of the walls? (They throw you off: they mustn't shove you out of the way first.)</summary>
        public static bool IsWall(Collider c) => c != null && Solid.Contains(c);

        static readonly Color32 PlateA = new(74, 78, 90, 255), PlateB = new(62, 66, 78, 255), Steel = new(214, 220, 230, 255);

        bool _wasOut;

        void LateUpdate()
        {
            bool on = Out(out float face);
            if (on && _walls[0] == null)
            {
                _walls[0] = Build(false);
                _walls[1] = Build(true);
            }
            bool set = on && !_wasOut;
            _wasOut = on;
            if (_walls[0] == null) return;
            Vector3 h = Half;
            for (int i = 0; i < 2; i++)
            {
                var w = _walls[i];
                if (w.go.activeSelf != on) w.go.SetActive(on);
                if (!on) continue;
                Quaternion rot = transform.rotation * Quaternion.Euler(0f, i * 180f, 0f);
                Vector3 at = Mid - rot * Vector3.forward * face;
                w.go.transform.SetPositionAndRotation(at, rot);
                w.rb.position = at;
                w.rb.rotation = rot;
                // solid all the way back to the end it came from
                float depth = Mathf.Max(0.05f, h.z + SpellPlans.SpikeLength - face);
                w.mass.localPosition = new Vector3(0f, 0f, -depth * 0.5f - 0.02f); // (just behind the plates)
                w.mass.localScale = new Vector3(h.x * 2f, h.y * 2f, depth);
                w.box.center = new Vector3(0f, 0f, -depth * 0.5f);
                w.box.size = new Vector3(h.x * 2f, h.y * 2f, depth);
                if (set) CameraRig.ShakeFrom(at, 0.35f, 0.8f);
            }
        }

        static Material _plates, _massMat;

        static Material Plain(string name)
        {
            var m = new Material(VoxelRender.Material) { name = name };
            // (no voxels on it)
            m.SetFloat("_Variation", 0f);
            m.SetFloat("_EdgeDarken", 0f);
            return m;
        }

        Wall Build(bool other)
        {
            if (_plates == null) _plates = Plain("Spike Wall");
            if (_massMat == null)
            {
                _massMat = Plain("Spike Wall Mass");
                _massMat.SetColor("_BaseColor", (Color)PlateB);
            }
            var w = new Wall { go = new GameObject("SpikeWall") { hideFlags = HideFlags.DontSave } };
            w.go.AddComponent<VoxelIgnore>();
            w.go.transform.SetParent(_home != null ? _home.transform : transform.parent, true);
            w.rb = w.go.AddComponent<Rigidbody>();
            w.rb.isKinematic = true;
            w.box = w.go.AddComponent<BoxCollider>();
            Solid.Add(w.box);

            Vector3 h = Half;
            int nx = Mathf.Max(2, Mathf.RoundToInt(h.x * 2f / SpellPlans.SpikePitch)), ny = Mathf.Max(2, Mathf.RoundToInt(h.y * 2f / SpellPlans.SpikePitch));
            float px = h.x * 2f / nx, py = h.y * 2f / ny;

            var face = new Geo();
            for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                float x0 = -h.x + x * px, y0 = -h.y + y * py;
                face.Quad(new Vector3(x0, y0), new Vector3(x0, y0 + py), new Vector3(x0 + px, y0 + py), new Vector3(x0 + px, y0), Vector3.forward, (x / 2 + y / 2) % 2 == 0 ? PlateA : PlateB);
            }
            var ink = new Geo();
            // (one fewer each way on the other wall, so its spikes fit in between this one's)
            int cx = other ? nx - 1 : nx, cy = other ? ny - 1 : ny;
            const float e = SpellPlans.SpikeInk;
            for (int y = 0; y < cy; y++)
            for (int x = 0; x < cx; x++)
            {
                var at = new Vector3((x + 0.5f - cx * 0.5f) * px, (y + 0.5f - cy * 0.5f) * py, 0f);
                face.Foot(at, SpellPlans.SpikeBase * 0.5f + e * 1.6f); // a line of ink round its foot, flat on the plates
                face.Spike(at, SpellPlans.SpikeBase * 0.5f, SpellPlans.SpikeLength - e * 2f, Steel, false);
                ink.Spike(at, SpellPlans.SpikeBase * 0.5f + e, SpellPlans.SpikeLength, Steel, true); // (the ink's tip is the very tip)
            }

            Mesh fm = face.Make("Spike wall"), im = ink.Make("Spike wall ink");
            w.meshes = new[] { fm, im };
            Part(w.go.transform, "Plates", fm, _plates);
            Glow.Tint(Part(w.go.transform, "Ink", im, Glow.Shared), Glow.Ink);
            w.mass = Part(w.go.transform, "Mass", Glow.CubeMesh, _massMat).transform;
            return w;
        }

        static MeshRenderer Part(Transform parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return r;
        }

        /// <summary>Flat-shaded, vertex-coloured faces.</summary>
        class Geo
        {
            readonly List<Vector3> _v = new(), _n = new();
            readonly List<Color32> _c = new();
            readonly List<int> _t = new();

            /// <summary>A triangle facing <paramref name="n"/>, whichever way round its corners come.</summary>
            void Tri(Vector3 a, Vector3 b, Vector3 d, Vector3 n, Color32 c)
            {
                if (Vector3.Dot(Vector3.Cross(b - a, d - a), n) < 0f) (b, d) = (d, b);
                foreach (var p in new[] { a, b, d })
                {
                    _t.Add(_v.Count);
                    _v.Add(p);
                    _n.Add(n);
                    _c.Add(c);
                }
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 d, Vector3 f, Vector3 n, Color32 c)
            {
                Tri(a, b, d, n, c);
                Tri(a, d, f, n, c);
            }

            /// <summary>A four-sided spike standing out along +Z (inside out: an ink hull).</summary>
            public void Spike(Vector3 at, float half, float length, Color32 c, bool insideOut)
            {
                Vector3 tip = at + Vector3.forward * length;
                for (int k = 0; k < 4; k++)
                {
                    Vector3 a = at + Corner(k) * half, b = at + Corner(k + 1) * half;
                    Vector3 n = Vector3.Cross(b - a, tip - a).normalized;
                    if (Vector3.Dot(n, (a + b) * 0.5f - at) < 0f) n = -n;
                    Tri(a, b, tip, insideOut ? -n : n, c);
                }
            }

            public void Foot(Vector3 at, float half)
            {
                Vector3 o = at + Vector3.forward * 0.015f;
                Quad(o + Corner(0) * half, o + Corner(1) * half, o + Corner(2) * half, o + Corner(3) * half, Vector3.forward, (Color32)Glow.Ink);
            }

            static Vector3 Corner(int k) => (k & 3) switch { 0 => new Vector3(-1f, -1f), 1 => new Vector3(-1f, 1f), 2 => new Vector3(1f, 1f), _ => new Vector3(1f, -1f) };

            public Mesh Make(string name)
            {
                var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
                m.SetVertices(_v);
                m.SetNormals(_n);
                m.SetColors(_c);
                m.SetTriangles(_t, 0);
                m.RecalculateBounds();
                return m;
            }
        }

        void OnDrawGizmos()
        {
            var b = GetComponent<BoxCollider>();
            if (b == null) return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.85f, 0.88f, 0.95f, 0.8f);
            Gizmos.DrawWireCube(b.center, b.size);
            Gizmos.color = new Color(1f, 0.15f, 0.1f, 0.15f);
            for (int side = -1; side <= 1; side += 2)
                Gizmos.DrawCube(b.center + Vector3.forward * (side * b.size.z * 0.5f), new Vector3(b.size.x, b.size.y, 0.05f));
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
