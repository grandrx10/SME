using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SuddenMageEncounter
{
    /// <summary>
    /// A Warp Space portal, on every peer: a paper-thin 2 x 3 m slab of bedrock (solid from behind, never breaks),
    /// tilted to face whoever cast it, whose front is the opening. Unlinked, the opening is an arcane swirl. Linked,
    /// it's a window onto its partner, and bodies and projectiles that enter it come out of the partner (moved by
    /// whoever simulates them).
    ///
    /// The window works like Valve's Portal: a camera stands behind the partner where the main camera would be if the
    /// two portals were one doorway, its near plane bent onto the partner's opening (oblique projection, so the wall
    /// behind it is cut away), and renders into a texture the opening samples at screen position. One level deep
    /// (portal cameras don't see portal surfaces), at the main view's resolution, and only while the opening is on screen,
    /// facing the camera and within <see cref="RenderDistance"/> m: an unseen portal costs nothing.
    /// </summary>
    [DefaultExecutionOrder(400)] // after the camera rig has placed the main camera
    public class PortalView : MonoBehaviour
    {
        // ------------------------------------------------------------------ extra cameras: no SSAO

        static readonly System.Collections.Generic.HashSet<Camera> ExtraCameras = new();
        static System.Collections.Generic.List<ScriptableRendererFeature> _ssao;
        static bool _hooked;

        /// <summary>
        /// A camera rendering into a portal or the warp window. Every renderer feature (the screen outline, SSAO) is
        /// switched off while it renders: they rebuild depth assuming an ordinary projection (wrong under a portal's
        /// oblique one) and drew flickering lines. Portal views also draw no shadows: the view is drawn plain.
        /// </summary>
        public static void RegisterExtraCamera(Camera cam)
        {
            ExtraCameras.Add(cam);
            if (_hooked) return;
            _hooked = true;
            RenderPipelineManager.beginCameraRendering += (ctx, c) =>
            {
                if (_ssao == null)
                {
                    _ssao = new System.Collections.Generic.List<ScriptableRendererFeature>();
                    foreach (var f in Resources.FindObjectsOfTypeAll<ScriptableRendererFeature>())
                        if (f.isActive) _ssao.Add(f); // every screen effect (outline, SSAO): none in the extra views
                }
                bool extra = ExtraCameras.Contains(c);
                foreach (var f in _ssao) if (f != null && f.isActive == extra) f.SetActive(!extra);
            };
            // the features are assets: never leave SSAO switched off (in the editor that would stick after play)
            Application.quitting += RestoreSsao;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged += s => { if (s == UnityEditor.PlayModeStateChange.ExitingPlayMode) RestoreSsao(); };
#endif
        }

        static void RestoreSsao()
        {
            if (_ssao == null) return;
            foreach (var f in _ssao) if (f != null && !f.isActive) f.SetActive(true);
        }

        const float RenderDistance = 60f;
        /// <summary>Shadows through the window (off: the view through a portal is drawn plain, with no shading on top).</summary>
        const bool RenderShadows = false;
        static readonly Color32 Bedrock = new(64, 60, 68, 255);
        static readonly int TexId = Shader.PropertyToID("_PortalTex"), OpenId = Shader.PropertyToID("_Open");

        WorldNet.PortalFx _fx;
        Transform _body;
        Material _mat;
        Renderer _surface;
        Camera _cam;
        RenderTexture _rt;
        static Mesh _slabMesh, _openingMesh;
        static readonly Plane[] Frustum = new Plane[6];
        static int _frustumFrame = -1;

        public static PortalView Create(WorldNet.PortalFx fx)
        {
            var go = new GameObject("Portal " + fx.id);
            go.transform.SetPositionAndRotation(fx.center, fx.Rotation);
            var v = go.AddComponent<PortalView>();
            v._fx = fx;
            ShowLayersOnMain();

            // what's seen opens and closes (the colliders stay put for its whole life)
            v._body = new GameObject("Body").transform;
            v._body.SetParent(go.transform, false);
            v._body.localScale = Vector3.zero;

            var slab = new GameObject("Bedrock");
            slab.transform.SetParent(v._body, false);
            slab.AddComponent<MeshFilter>().sharedMesh = SlabMesh;
            slab.AddComponent<MeshRenderer>().sharedMaterial = VoxelRender.Material;

            var opening = new GameObject("Opening") { layer = Layers.PortalSurface };
            opening.transform.SetParent(v._body, false);
            opening.transform.localPosition = new Vector3(0f, 0f, 0.005f);
            opening.transform.localScale = new Vector3(SpellPlans.PortalWidth, SpellPlans.PortalHeight, 1f);
            opening.AddComponent<MeshFilter>().sharedMesh = OpeningMesh;
            var r = opening.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            v._mat = new Material(Shader.Find("SME/Portal")) { name = "Portal" };
            v._mat.SetFloat(OpenId, 0f);
            r.sharedMaterial = v._mat;
            v._surface = r;

            // the slab is solid (Default layer: people, projectiles and the camera bump into it); someone in front of a
            // linked one's opening stops colliding with it on their own machine, so they can step through
            var box = go.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0f, -SpellPlans.PortalDepth * 0.5f);
            box.size = new Vector3(SpellPlans.PortalWidth, SpellPlans.PortalHeight, SpellPlans.PortalDepth);
            v.Solid = box;
            return v;
        }

        /// <summary>The slab's collider.</summary>
        public Collider Solid { get; private set; }

        /// <summary>The main camera has to draw the render-only layers the portals and warps use.</summary>
        public static void ShowLayersOnMain()
        {
            var main = Camera.main;
            if (main != null) main.cullingMask |= (1 << Layers.PortalSurface) | (1 << Layers.WarpHidden);
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start), left = (float)(_fx.End - now);
            if (left <= 0f) { Destroy(gameObject); return; }
            // grows out of a point, shrinks back into one (never fades)
            float open = Mathf.Clamp01(t / SpellPlans.PortalOpen) * Mathf.Clamp01(left / SpellPlans.PortalOpen);
            float e = 1f - (1f - open) * (1f - open) * (1f - open);
            _body.localScale = new Vector3(e, e, 1f);

            var pair = WorldNet.Instance != null ? WorldNet.Instance.LinkedTo(_fx, now) : null;

            var main = Camera.main;
            bool render = pair != null && open > 0.05f && main != null && Seen(main);
            _mat.SetFloat(OpenId, render ? 1f : 0f);
            if (render) RenderThrough(main, pair);
            else if (_cam != null) _cam.enabled = false;
        }

        /// <summary>
        /// Rubble (each machine's own) goes through too: a piece touching the opening while moving into it comes out of
        /// the partner the same distance in front, turned and moving the matching way.
        /// </summary>
        void FixedUpdate()
        {
            var net = WorldNet.Instance;
            if (net == null || Debris.All.Count == 0) return;
            var pair = net.LinkedTo(_fx, WorldNet.Now);
            if (pair == null) return;
            Quaternion qa = _fx.Rotation, qb = pair.Rotation, inv = Quaternion.Inverse(qa);
            Vector3 n = qa * Vector3.forward, nb = qb * Vector3.forward;
            const float hw = SpellPlans.PortalWidth * 0.5f, hh = SpellPlans.PortalHeight * 0.5f;
            var all = Debris.All;
            for (int i = 0; i < all.Count; i++)
            {
                var d = all[i];
                if (d == null || d.Held || d.Body == null) continue;
                var rb = d.Body;
                Vector3 c = rb.worldCenterOfMass;
                Vector3 l = inv * (c - _fx.center);
                if (Mathf.Abs(l.x) > hw || Mathf.Abs(l.y) > hh || l.z < -0.3f || l.z > 3f) continue;
                float into = -Vector3.Dot(rb.linearVelocity, n);
                if (into < 0.05f) continue;
                // how far the piece reaches towards the opening
                var col = d.GetComponent<Collider>();
                Vector3 e = col != null ? col.bounds.extents : Vector3.one * 0.15f;
                float reach = Mathf.Abs(n.x) * e.x + Mathf.Abs(n.y) * e.y + Mathf.Abs(n.z) * e.z + 0.05f;
                // (or will this step: fast rubble would otherwise bounce off the slab before we see it touch)
                if (l.z > reach + into * Time.fixedDeltaTime) continue;
                Vector3 offset = rb.position - c; // keep the piece's pivot where it was relative to its middle
                Vector3 nc = SpellPlans.ThroughPoint(c, _fx.center, qa, pair.center, qb, true);
                float outB = Vector3.Dot(nc - pair.center, nb);
                if (outB < reach) nc += nb * (reach - outB);
                Quaternion turn = qb * Quaternion.Euler(0f, 180f, 0f) * inv;
                rb.position = nc + turn * offset;
                rb.rotation = turn * rb.rotation;
                rb.linearVelocity = SpellPlans.ThroughDir(rb.linearVelocity, qa, qb);
                rb.angularVelocity = turn * rb.angularVelocity;
                d.transform.SetPositionAndRotation(rb.position, rb.rotation);
            }
        }

        /// <summary>On screen, facing the camera and near enough to be worth a render.</summary>
        bool Seen(Camera main)
        {
            Vector3 toCam = main.transform.position - _fx.center;
            if (Vector3.Dot(toCam, _fx.Normal) < 0.02f || toCam.sqrMagnitude > RenderDistance * RenderDistance) return false;
            if (_frustumFrame != Time.frameCount)
            {
                _frustumFrame = Time.frameCount;
                GeometryUtility.CalculateFrustumPlanes(main, Frustum);
            }
            return GeometryUtility.TestPlanesAABB(Frustum, _surface.bounds);
        }

        void RenderThrough(Camera main, WorldNet.PortalFx pair)
        {
            Ensure(main);
            // where the main camera would be if the partner's opening were this one's, turned round to look out of it
            var here = Matrix4x4.TRS(_fx.center, _fx.Rotation, Vector3.one);
            var there = Matrix4x4.TRS(pair.center, pair.Rotation, Vector3.one);
            var m = there * Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, 0f)) * here.inverse * main.transform.localToWorldMatrix;
            _cam.transform.SetPositionAndRotation(m.GetColumn(3), m.rotation);
            _cam.fieldOfView = main.fieldOfView;
            _cam.aspect = main.aspect;
            _cam.nearClipPlane = main.nearClipPlane;
            _cam.farClipPlane = main.farClipPlane;
            _cam.cullingMask = main.cullingMask & ~(1 << Layers.PortalSurface);
            _cam.clearFlags = main.clearFlags;
            _cam.backgroundColor = main.backgroundColor;
            _cam.ResetProjectionMatrix();
            _cam.ResetCullingMatrix();
            var w2c = _cam.worldToCameraMatrix;
            // culling and shadows use the ordinary projection: the bent one below would throw off the shadow
            // cascades (and cull things that are really in view)
            _cam.cullingMatrix = _cam.projectionMatrix * w2c;
            // near plane bent onto the partner's opening: nothing behind it (its own slab, the wall it's on) gets in the way
            Vector3 n = pair.Normal;
            Vector3 cp = w2c.MultiplyPoint(pair.center), cn = w2c.MultiplyVector(n).normalized;
            float dist = -Vector3.Dot(cp, cn);
            if (Mathf.Abs(dist) > 0.05f) _cam.projectionMatrix = _cam.CalculateObliqueMatrix(new Vector4(cn.x, cn.y, cn.z, dist));
            _cam.enabled = true;
        }

        void Ensure(Camera main)
        {
            if (_cam == null)
            {
                var go = new GameObject("PortalCamera " + _fx.id);
                go.transform.SetParent(transform, true);
                _cam = go.AddComponent<Camera>();
                _cam.depth = main.depth - 1f; // drawn before the main view, which shows it
                _cam.allowHDR = true;
                _cam.allowMSAA = false;
                _cam.useOcclusionCulling = false;
                var data = _cam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = false; // the main camera post-processes the whole picture, window included
                data.antialiasing = AntialiasingMode.None;
                data.renderShadows = RenderShadows;
                data.requiresDepthTexture = false;
                data.requiresColorTexture = false;
                RegisterExtraCamera(_cam);
            }
            // full resolution: at half, every outline and voxel line through the window came out doubled and stair-stepped
            int w = Mathf.Max(64, main.pixelWidth), h = Mathf.Max(64, main.pixelHeight);
            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                if (_rt != null) { _cam.targetTexture = null; _rt.Release(); Destroy(_rt); }
                _rt = new RenderTexture(w, h, 24, RenderTextureFormat.DefaultHDR) { name = "Portal " + _fx.id };
                _rt.Create();
                _cam.targetTexture = _rt;
                _mat.SetTexture(TexId, _rt);
            }
        }

        void OnDestroy()
        {
            if (_cam != null) { _cam.targetTexture = null; Destroy(_cam.gameObject); }
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_mat != null) Destroy(_mat);
        }

        // ------------------------------------------------------------------ meshes

        /// <summary>A unit quad (-0.5..0.5) facing +Z, uv 0..1.</summary>
        public static Mesh OpeningMesh
        {
            get
            {
                if (_openingMesh != null) return _openingMesh;
                _openingMesh = new Mesh { name = "Portal Opening" };
                _openingMesh.SetVertices(new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) });
                _openingMesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) });
                _openingMesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
                _openingMesh.RecalculateNormals();
                _openingMesh.RecalculateBounds();
                return _openingMesh;
            }
        }

        /// <summary>The bedrock slab: a box behind the opening, in voxel-grid units so the voxel shader draws its cells.</summary>
        static Mesh SlabMesh
        {
            get
            {
                if (_slabMesh != null) return _slabMesh;
                float hw = SpellPlans.PortalWidth * 0.5f, hh = SpellPlans.PortalHeight * 0.5f, d = SpellPlans.PortalDepth;
                var c = new Vector3(0f, 0f, -d * 0.5f);
                var e = new Vector3(hw, hh, d * 0.5f);
                var v = new System.Collections.Generic.List<Vector3>();
                var n = new System.Collections.Generic.List<Vector3>();
                var t = new System.Collections.Generic.List<int>();
                void Face(Vector3 normal, Vector3 a, Vector3 b)
                {
                    Vector3 fc = c + Vector3.Scale(normal, e);
                    Vector3 ea = Vector3.Scale(a, e), eb = Vector3.Scale(b, e);
                    int k = v.Count;
                    v.Add(fc - ea - eb); v.Add(fc + ea - eb); v.Add(fc + ea + eb); v.Add(fc - ea + eb);
                    for (int i = 0; i < 4; i++) n.Add(normal);
                    t.Add(k); t.Add(k + 1); t.Add(k + 2); t.Add(k); t.Add(k + 2); t.Add(k + 3);
                }
                // each face's two axes, ordered so the face points outward
                Face(Vector3.right, Vector3.up, Vector3.forward);
                Face(Vector3.left, Vector3.forward, Vector3.up);
                Face(Vector3.up, Vector3.forward, Vector3.right);
                Face(Vector3.down, Vector3.right, Vector3.forward);
                Face(Vector3.forward, Vector3.right, Vector3.up);
                Face(Vector3.back, Vector3.up, Vector3.right);
                _slabMesh = new Mesh { name = "Portal Bedrock" };
                _slabMesh.SetVertices(v);
                _slabMesh.SetNormals(n);
                var colours = new Color32[v.Count];
                for (int i = 0; i < colours.Length; i++) colours[i] = Bedrock;
                _slabMesh.SetColors(colours);
                _slabMesh.SetTriangles(t, 0);
                _slabMesh.RecalculateBounds();
                return _slabMesh;
            }
        }
    }
}
