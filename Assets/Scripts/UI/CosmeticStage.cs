using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Your character on a little stage far below the map, wearing what's in your wardrobe, for dressing up: rays from the
    /// picture find the cosmetic under the mouse and the spot on the body's surface (and the bone it belongs to).
    /// </summary>
    public static class CosmeticStage
    {
        const int Layer = 31; // (shared with FacePreview: only these stages use it)
        public const int TexWidth = 620, TexHeight = 800;
        const float Fov = 22f, MinHeight = 0.6f, MaxHeight = 2.3f;

        static Transform _stage, _pivot, _model;
        static Camera _cam;
        static RenderTexture _tex;
        static bool _rendering;
        static Dictionary<string, Transform> _bones;
        static FacePainter _face;

        // the body's surface as it stands here (pivot space), three vertices a triangle's worth of indices
        static Vector3[] _verts, _normals;
        static int[] _tris;
        static string[] _boneOf;

        class Worn { public int uid; public GameObject go; }
        static readonly List<Worn> Pieces = new();

        static float _yaw, _pitch, _height = MaxHeight, _lookY;

        /// <summary>A spot on the body's surface.</summary>
        public struct Spot
        {
            public Vector3 point, normal; // world
            public string bone;
        }

        public static Texture Get()
        {
            if (_cam == null) Build();
            return _cam != null ? _tex : null;
        }

        public static void SetRendering(bool on)
        {
            if (on && _cam == null) Build();
            _rendering = on;
            if (_cam != null) _cam.enabled = on;
            if (!on) return;
            Tint();
            Refresh();
        }

        public static void ResetView()
        {
            _yaw = -20f;
            _pitch = 4f;
            _height = MaxHeight;
            _lookY = 0f;
            Aim();
        }

        static void Build()
        {
            var nm = NetworkManager.Singleton;
            var player = nm != null && nm.NetworkConfig != null ? nm.NetworkConfig.PlayerPrefab : null;
            if (player == null) return;
            var pc = player.GetComponent<PlayerController>();
            Transform src = pc != null && pc.model != null ? pc.model : player.transform.Find("PlayerModel");
            if (src == null) return;

            if (_stage == null)
            {
                _stage = new GameObject("CosmeticStage").transform;
                _stage.position = new Vector3(-1000f, -5400f, 0f);
                Object.DontDestroyOnLoad(_stage.gameObject);
            }
            var main = Camera.main;
            if (main != null) main.cullingMask &= ~(1 << Layer);

            _pivot = new GameObject("Pivot").transform;
            _pivot.SetParent(_stage, false);
            var inst = Object.Instantiate(src.gameObject, _pivot);
            inst.SetActive(true);
            _model = inst.transform;
            _model.localPosition = Vector3.zero;
            _model.localRotation = Quaternion.identity;
            foreach (var c in inst.GetComponentsInChildren<Collider>(true)) Object.Destroy(c);
            foreach (var m in inst.GetComponentsInChildren<MonoBehaviour>(true)) m.enabled = false;
            foreach (var a in inst.GetComponentsInChildren<Animator>(true)) a.enabled = false;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
            }
            _bones = Rig.MapBones(_model);
            _face = new FacePainter();
            FaceDecal.Attach(_model, _pivot, _face.Texture);
            Layers.SetLayerRecursive(_stage.gameObject, Layer);
            Surface(inst.GetComponentInChildren<SkinnedMeshRenderer>(true));

            var camGo = new GameObject("Camera");
            camGo.transform.SetParent(_stage, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0, 0, 0, 0);
            _cam.cullingMask = 1 << Layer;
            _cam.fieldOfView = Fov;
            _cam.aspect = TexWidth / (float)TexHeight;
            _cam.allowHDR = false;
            _cam.allowMSAA = true;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 20f;
            _tex = new RenderTexture(TexWidth, TexHeight, 24, RenderTextureFormat.ARGB32) { name = "CosmeticStage", antiAliasing = 4 };
            _tex.Create();
            _cam.targetTexture = _tex;
            _cam.enabled = _rendering;
            ResetView();
        }

        static void Tint()
        {
            if (_model == null) return;
            var mpb = new MaterialPropertyBlock();
            Color c = FacePreview.SkinColor;
            foreach (var r in _model.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name == FaceDecal.ObjectName || CosmeticLook.Is(r)) continue;
                r.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", c);
                mpb.SetColor("_Color", c);
                r.SetPropertyBlock(mpb);
            }
            _face.Draw(FaceArt.Strokes);
        }

        /// <summary>Shows what's worn now.</summary>
        public static void Refresh()
        {
            if (_model == null) return;
            foreach (var p in Pieces)
            {
                if (p.go == null) continue;
                p.go.SetActive(false);
                Object.Destroy(p.go);
            }
            Pieces.Clear();
            foreach (var w in Wardrobe.Worn)
            {
                var item = Wardrobe.Find(w.uid);
                if (item == null || !_bones.TryGetValue(BoneName(w), out var bone)) continue;
                var go = CosmeticLook.Make(item.id, item.colour, bone);
                if (go == null) continue;
                Layers.SetLayerRecursive(go, Layer);
                Pieces.Add(new Worn { uid = w.uid, go = go });
                Move(w);
            }
        }

        static string BoneName(WornItem w) => CosmeticLook.Bones[Mathf.Clamp(w.bone, 0, CosmeticLook.Bones.Length - 1)];

        /// <summary>Puts one where it's worn (its bone may have changed).</summary>
        public static void Move(WornItem w)
        {
            var item = Wardrobe.Find(w.uid);
            foreach (var p in Pieces)
            {
                if (p.uid != w.uid || p.go == null || item == null || !_bones.TryGetValue(BoneName(w), out var bone)) continue;
                if (p.go.transform.parent != bone) p.go.transform.SetParent(bone, false);
                CosmeticLook.Place(p.go.transform, w.pos, w.rot, CosmeticLook.BaseScale(item.id) * item.Scale);
            }
        }

        // ------------------------------------------------------------------ view

        public static void Orbit(Vector2 pixels)
        {
            _yaw += pixels.x * 0.45f;
            _pitch = Mathf.Clamp(_pitch - pixels.y * 0.3f, -60f, 75f);
            Aim();
        }

        public static void Zoom(float steps)
        {
            _height = Mathf.Clamp(_height * Mathf.Pow(0.88f, steps), MinHeight, MaxHeight);
            Aim();
        }

        public static void Pan(float pixelsY, float viewHeight)
        {
            _lookY -= pixelsY / Mathf.Max(1f, viewHeight) * _height;
            Aim();
        }

        static void Aim()
        {
            if (_cam == null) return;
            float span = (MaxHeight - _height) * 0.5f + 0.15f;
            _lookY = Mathf.Clamp(_lookY, -span, span);
            float dist = _height * 0.5f / Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad);
            Vector3 look = _stage.position + Vector3.up * (_lookY + 0.06f);
            _cam.transform.position = look + Quaternion.Euler(-_pitch, 0f, 0f) * Vector3.forward * dist;
            _cam.transform.LookAt(look);
            _pivot.localRotation = Quaternion.Euler(0f, -_yaw, 0f);
        }

        /// <summary>The ray through a point of the picture (0..1 from the bottom left).</summary>
        public static Ray RayAt(Vector2 uv) => _cam.ViewportPointToRay(new Vector3(uv.x, uv.y, 0f));

        public static bool Ready => _cam != null;

        public static Vector2 ToPicture(Vector3 world) => _cam.WorldToViewportPoint(world);

        // ------------------------------------------------------------------ what's where

        /// <summary>The worn cosmetic under the ray, unless the body's in front of it (0: none).</summary>
        public static int Pick(Ray ray)
        {
            float best = OnBody(ray, out var spot) ? Vector3.Distance(ray.origin, spot.point) + 0.03f : float.MaxValue;
            int uid = 0;
            foreach (var p in Pieces)
            {
                if (p.go == null) continue;
                var mesh = p.go.GetComponent<MeshFilter>().sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                var t = p.go.transform;
                if (!p.go.GetComponent<Renderer>().bounds.IntersectRay(ray)) continue;
                Vector3 o = t.InverseTransformPoint(ray.origin), d = t.InverseTransformVector(ray.direction);
                var v = mesh.vertices;
                var tris = mesh.triangles;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                    // (the direction isn't normalised, so distances are still in world metres)
                    if (Hit(o, d, v[tris[i]], v[tris[i + 1]], v[tris[i + 2]], out float at, out _, out _) && at < best) { best = at; uid = p.uid; }
            }
            return uid;
        }

        /// <summary>Where the ray first meets the body.</summary>
        public static bool OnBody(Ray ray, out Spot spot)
        {
            spot = default;
            if (_tris == null) return false;
            Vector3 o = _pivot.InverseTransformPoint(ray.origin), d = _pivot.InverseTransformVector(ray.direction);
            float best = float.MaxValue;
            int tri = -1;
            float bu = 0f, bv = 0f;
            for (int i = 0; i + 2 < _tris.Length; i += 3)
                if (Hit(o, d, _verts[_tris[i]], _verts[_tris[i + 1]], _verts[_tris[i + 2]], out float at, out float u, out float v) && at < best)
                {
                    best = at;
                    tri = i;
                    bu = u;
                    bv = v;
                }
            if (tri < 0) return false;
            spot = At(tri, bu, bv);
            return true;
        }

        /// <summary>The nearest spot on the body to a point.</summary>
        public static Spot Nearest(Vector3 world)
        {
            Vector3 p = _pivot.InverseTransformPoint(world);
            float best = float.MaxValue;
            int tri = 0;
            float bu = 0f, bv = 0f;
            for (int i = 0; i + 2 < _tris.Length; i += 3)
            {
                Vector3 a = _verts[_tris[i]], b = _verts[_tris[i + 1]], c = _verts[_tris[i + 2]];
                Closest(p, a, b, c, out float u, out float v);
                float q = (a + (b - a) * u + (c - a) * v - p).sqrMagnitude;
                if (q >= best) continue;
                best = q;
                tri = i;
                bu = u;
                bv = v;
            }
            return At(tri, bu, bv);
        }

        static Spot At(int tri, float u, float v)
        {
            int a = _tris[tri], b = _tris[tri + 1], c = _tris[tri + 2];
            float w = 1f - u - v;
            int near = w >= u && w >= v ? a : u >= v ? b : c;
            return new Spot
            {
                point = _pivot.TransformPoint(_verts[a] * w + _verts[b] * u + _verts[c] * v),
                normal = _pivot.TransformDirection((_normals[a] * w + _normals[b] * u + _normals[c] * v).normalized),
                bone = _boneOf[near],
            };
        }

        /// <summary>A move in the character's own directions (x: to the right as you face it, y: up, z: forward; metres), in the world.</summary>
        public static Vector3 Across(Vector3 move) => _pivot.TransformVector(new Vector3(-move.x, move.y, move.z));

        public static Transform Bone(string name) => _bones != null && _bones.TryGetValue(name, out var b) ? b : null;

        static bool Hit(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t, out float u, out float v)
        {
            t = u = v = 0f;
            Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f) return false;
            Vector3 s = o - a;
            u = Vector3.Dot(s, p) / det;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            v = Vector3.Dot(d, q) / det;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, q) / det;
            return t > 1e-4f;
        }

        /// <summary>The closest point of a triangle, as weights of b (u) and c (v).</summary>
        static void Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float u, out float v)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d00 = Vector3.Dot(ab, ab), d01 = Vector3.Dot(ab, ac), d11 = Vector3.Dot(ac, ac);
            float d20 = Vector3.Dot(ap, ab), d21 = Vector3.Dot(ap, ac);
            float den = d00 * d11 - d01 * d01;
            u = v = 0f;
            if (Mathf.Abs(den) > 1e-12f)
            {
                u = (d11 * d20 - d01 * d21) / den;
                v = (d00 * d21 - d01 * d20) / den;
                if (u >= 0f && v >= 0f && u + v <= 1f) return;
            }
            // off it: the closest point of its nearest edge
            float best = float.MaxValue, bu = 0f, bv = 0f;
            void Edge(Vector3 x, Vector3 y, float ux, float vx, float uy, float vy)
            {
                Vector3 e = y - x;
                float k = Mathf.Clamp01(Vector3.Dot(p - x, e) / Mathf.Max(1e-12f, e.sqrMagnitude));
                float q = (x + e * k - p).sqrMagnitude;
                if (q >= best) return;
                best = q;
                bu = Mathf.Lerp(ux, uy, k);
                bv = Mathf.Lerp(vx, vy, k);
            }
            Edge(a, b, 0f, 0f, 1f, 0f);
            Edge(b, c, 1f, 0f, 0f, 1f);
            Edge(c, a, 0f, 1f, 0f, 0f);
            u = bu;
            v = bv;
        }

        /// <summary>The body's triangles as it's posed here, with smooth normals, and the bone each vertex follows most.</summary>
        static void Surface(SkinnedMeshRenderer smr)
        {
            _tris = null;
            if (smr == null || smr.sharedMesh == null || !smr.sharedMesh.isReadable) return;
            var mesh = smr.sharedMesh;
            var v = mesh.vertices;
            var w = mesh.boneWeights;
            var binds = mesh.bindposes;
            var bones = smr.bones;
            if (w.Length != v.Length) return;
            var toPivot = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                toPivot[i] = bones[i] != null ? _pivot.worldToLocalMatrix * bones[i].localToWorldMatrix * binds[i] : Matrix4x4.identity;
            _verts = new Vector3[v.Length];
            _boneOf = new string[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                var b = w[i];
                Vector3 p = toPivot[b.boneIndex0].MultiplyPoint3x4(v[i]) * b.weight0;
                if (b.weight1 > 0f) p += toPivot[b.boneIndex1].MultiplyPoint3x4(v[i]) * b.weight1;
                if (b.weight2 > 0f) p += toPivot[b.boneIndex2].MultiplyPoint3x4(v[i]) * b.weight2;
                if (b.weight3 > 0f) p += toPivot[b.boneIndex3].MultiplyPoint3x4(v[i]) * b.weight3;
                float total = b.weight0 + b.weight1 + b.weight2 + b.weight3;
                _verts[i] = total > 1e-4f ? p / total : v[i];
                string name = bones[b.boneIndex0] != null ? bones[b.boneIndex0].name : "Bone";
                _boneOf[i] = System.Array.IndexOf(CosmeticLook.Bones, name) >= 0 ? name : "Bone";
            }
            _tris = mesh.triangles;
            var sum = new Dictionary<Vector3Int, Vector3>();
            static Vector3Int Key(Vector3 p) => Vector3Int.RoundToInt(p * 2000f);
            for (int i = 0; i + 2 < _tris.Length; i += 3)
            {
                Vector3 a = _verts[_tris[i]], b = _verts[_tris[i + 1]], c = _verts[_tris[i + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a);
                foreach (var p in new[] { a, b, c })
                {
                    sum.TryGetValue(Key(p), out var s);
                    sum[Key(p)] = s + n;
                }
            }
            _normals = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) _normals[i] = sum.TryGetValue(Key(_verts[i]), out var s) && s.sqrMagnitude > 1e-12f ? s.normalized : Vector3.up;
        }
    }
}
