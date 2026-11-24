using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Lava voxels pouring down: all of them in three meshes, no physics, shrinking away soon after they land.</summary>
    public class LavaFallView : MonoBehaviour
    {
        struct Drop { public Vector3 p; public float vy, ground, half, landed; public bool hot; }
        const float Gravity = 20f, Linger = 0.6f, Ink = 0.04f;

        readonly List<Drop> _d = new();
        readonly List<Vector3> _vo = new(), _vh = new(), _vi = new();
        readonly List<int> _to = new(), _th = new(), _ti = new();
        Mesh _orange, _hot, _ink;
        float _idle;

        public static LavaFallView Create(Transform parent)
        {
            var go = new GameObject("LavaFall") { layer = Layers.Fx };
            go.transform.SetParent(parent, true);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var v = go.AddComponent<LavaFallView>();
            v._ink = VoxelOverlay.Layer(go.transform, "Outline", VoxelOverlay.InkMaterial, Glow.Ink);
            v._orange = VoxelOverlay.Layer(go.transform, "Lava", Glow.Shared, FireVfx.Orange);
            v._hot = VoxelOverlay.Layer(go.transform, "Hot", Glow.Shared, FireVfx.Yellow);
            return v;
        }

        public void Add(Vector3 p, float ground)
        {
            _idle = 0f;
            _d.Add(new Drop { p = p, vy = -Random.Range(6f, 10f), ground = ground, half = Random.value < 0.3f ? 0.25f : 0.125f, landed = -1f, hot = Random.value < 0.35f });
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime, now = Time.time;
            _vo.Clear(); _to.Clear(); _vh.Clear(); _th.Clear(); _vi.Clear(); _ti.Clear();
            for (int i = _d.Count - 1; i >= 0; i--)
            {
                var d = _d[i];
                if (d.landed < 0f)
                {
                    d.vy -= Gravity * dt;
                    d.p.y += d.vy * dt;
                    if (d.p.y <= d.ground + d.half) { d.p.y = d.ground + d.half; d.landed = now; }
                    _d[i] = d;
                }
                float left = d.landed < 0f ? 1f : 1f - (now - d.landed) / Linger;
                if (left <= 0f) { _d.RemoveAt(i); continue; }
                float h = d.half * left;
                if (d.hot) VoxelOverlay.AddCube(_vh, _th, d.p, h, 63); else VoxelOverlay.AddCube(_vo, _to, d.p, h, 63);
                VoxelOverlay.AddCube(_vi, _ti, d.p, h + Ink * left, 63);
            }
            VoxelOverlay.Apply(_orange, _vo, _to);
            VoxelOverlay.Apply(_hot, _vh, _th);
            VoxelOverlay.Apply(_ink, _vi, _ti);
            if (_d.Count > 0) return;
            _idle += dt;
            if (_idle > 2f) Destroy(gameObject);
        }

        void OnDestroy()
        {
            Destroy(_orange);
            Destroy(_hot);
            Destroy(_ink);
        }
    }

    /// <summary>Building glowing, inked voxels into one mesh (lava drops, lit signals).</summary>
    public static class VoxelOverlay
    {
        static Material _ink;

        /// <summary>Ink cubes showing only their far faces, so they rim what's inside them.</summary>
        public static Material InkMaterial
        {
            get
            {
                if (_ink != null) return _ink;
                _ink = new Material(Glow.Shared) { name = "Voxel Ink" };
                if (_ink.HasProperty("_Cull")) _ink.SetFloat("_Cull", 1f);
                return _ink;
            }
        }

        public static Mesh Layer(Transform parent, string name, Material mat, Color colour)
        {
            var go = new GameObject(name) { layer = Layers.Fx };
            go.transform.SetParent(parent, false);
            var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            Glow.Tint(r, colour);
            return m;
        }

        /// <summary>The faces of a cube; mask bits: +X -X +Y -Y +Z -Z.</summary>
        public static void AddCube(List<Vector3> v, List<int> t, Vector3 c, float h, int mask)
        {
            Vector3 x = Vector3.right * h, y = Vector3.up * h, z = Vector3.forward * h;
            if ((mask & 1) != 0) Face(v, t, c + x, y, z);
            if ((mask & 2) != 0) Face(v, t, c - x, z, y);
            if ((mask & 4) != 0) Face(v, t, c + y, z, x);
            if ((mask & 8) != 0) Face(v, t, c - y, x, z);
            if ((mask & 16) != 0) Face(v, t, c + z, x, y);
            if ((mask & 32) != 0) Face(v, t, c - z, y, x);
        }

        static void Face(List<Vector3> v, List<int> t, Vector3 c, Vector3 a, Vector3 b)
        {
            int n = v.Count;
            v.Add(c - a - b); v.Add(c + a - b); v.Add(c + a + b); v.Add(c - a + b);
            t.Add(n); t.Add(n + 1); t.Add(n + 2);
            t.Add(n); t.Add(n + 2); t.Add(n + 3);
        }

        public static void Apply(Mesh m, List<Vector3> v, List<int> t)
        {
            m.Clear();
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
        }
    }
}
