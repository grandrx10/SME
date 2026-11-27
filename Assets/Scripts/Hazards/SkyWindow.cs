using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Skyland's faces are windows onto where they wrap to, the way a linked Warp Space portal shows its partner: look
    /// down past the bottom and you see the map again from above; look out past a side and you see it again from the
    /// opposite side. A camera stands where the main one would be on the other side of the wrap (shifted by the wrap),
    /// its near plane bent onto the face it comes out of so only the map shows, and renders into a texture the window
    /// samples at screen position (SME/Portal). Each one renders only while it's on screen and facing the camera, so
    /// usually one to three at once; client-side only, looks only (no collider).
    /// </summary>
    public class SkyWindow : MonoBehaviour
    {
        public enum Face { Bottom, East, West, North, South }

        static readonly int TexId = Shader.PropertyToID("_PortalTex"), OpenId = Shader.PropertyToID("_Open"), EdgeWidthId = Shader.PropertyToID("_EdgeWidth");

        SkyWrap _wrap;
        Face _face;
        Material _mat;
        Renderer _surface;
        Camera _cam;
        RenderTexture _rt;
        static readonly Plane[] Frustum = new Plane[6];
        static int _frustumFrame = -1;

        public static SkyWindow Create(SkyWrap wrap, Face face)
        {
            var go = new GameObject("SkyWindow " + face) { layer = Layers.PortalSurface };
            go.transform.SetParent(wrap.transform, true);
            var v = go.AddComponent<SkyWindow>();
            v._wrap = wrap;
            v._face = face;
            go.AddComponent<MeshFilter>().sharedMesh = PortalView.OpeningMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            v._mat = new Material(Shader.Find("SME/Portal")) { name = "Sky Window " + face };
            v._mat.SetFloat(OpenId, 1f);
            v._mat.SetFloat(EdgeWidthId, 0f); // seamless: no rim
            r.sharedMaterial = v._mat;
            r.enabled = false;
            v._surface = r;
            PortalView.ShowLayersOnMain();
            return v;
        }

        /// <summary>Where this face is (centre, facing into the area), how big, and how far the wrap through it moves you.</summary>
        void Layout(out Vector3 centre, out Vector3 inward, out Vector2 size, out Vector3 shift)
        {
            Vector3 o = _wrap.Origin;
            float b = _wrap.BottomY, t = _wrap.TopY, mid = (b + t) * 0.5f, hx = _wrap.size.x * 0.5f, hz = _wrap.size.y * 0.5f;
            switch (_face)
            {
                case Face.East:  centre = new Vector3(o.x + hx, mid, o.z); inward = Vector3.left;    size = new Vector2(_wrap.size.y, t - b); shift = Vector3.left * _wrap.size.x; break;
                case Face.West:  centre = new Vector3(o.x - hx, mid, o.z); inward = Vector3.right;   size = new Vector2(_wrap.size.y, t - b); shift = Vector3.right * _wrap.size.x; break;
                case Face.North: centre = new Vector3(o.x, mid, o.z + hz); inward = Vector3.back;    size = new Vector2(_wrap.size.x, t - b); shift = Vector3.back * _wrap.size.y; break;
                case Face.South: centre = new Vector3(o.x, mid, o.z - hz); inward = Vector3.forward; size = new Vector2(_wrap.size.x, t - b); shift = Vector3.forward * _wrap.size.y; break;
                default:         centre = new Vector3(o.x, b, o.z);        inward = Vector3.up;      size = new Vector2(_wrap.size.x, _wrap.size.y); shift = Vector3.up * (t - b); break;
            }
        }

        void LateUpdate()
        {
            if (_wrap == null || _wrap.Unset) { Hide(); return; }
            Layout(out var centre, out var inward, out var size, out var shift);
            var rot = _face == Face.Bottom ? Quaternion.LookRotation(Vector3.up, Vector3.forward) : Quaternion.LookRotation(inward, Vector3.up);
            transform.SetPositionAndRotation(centre, rot);
            transform.localScale = new Vector3(size.x, size.y, 1f);

            var main = Camera.main;
            // seen from inside the area only
            if (main == null || Vector3.Dot(main.transform.position - centre, inward) <= 0.05f) { Hide(); return; }
            if (_frustumFrame != Time.frameCount)
            {
                _frustumFrame = Time.frameCount;
                GeometryUtility.CalculateFrustumPlanes(main, Frustum);
            }
            _surface.enabled = true; // (its bounds are only right once it's on)
            if (!GeometryUtility.TestPlanesAABB(Frustum, _surface.bounds)) { Hide(); return; }
            Render(main, centre + shift, -inward, shift);
        }

        void Hide()
        {
            if (_surface != null) _surface.enabled = false;
            if (_cam != null) _cam.enabled = false;
        }

        /// <param name="clipAt">The face we come out of, on the far side of the wrap.</param>
        /// <param name="clipNormal">Pointing into the area from it: only what's that side of it shows.</param>
        void Render(Camera main, Vector3 clipAt, Vector3 clipNormal, Vector3 shift)
        {
            Ensure(main);
            _cam.transform.SetPositionAndRotation(main.transform.position + shift, main.transform.rotation);
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
            _cam.cullingMatrix = _cam.projectionMatrix * w2c;
            Vector3 cp = w2c.MultiplyPoint(clipAt), cn = w2c.MultiplyVector(clipNormal).normalized;
            float dist = -Vector3.Dot(cp, cn);
            if (Mathf.Abs(dist) > 0.05f) _cam.projectionMatrix = _cam.CalculateObliqueMatrix(new Vector4(cn.x, cn.y, cn.z, dist));
            _cam.enabled = true;
        }

        void Ensure(Camera main)
        {
            if (_cam == null)
            {
                var go = new GameObject("SkyWindowCamera " + _face);
                go.transform.SetParent(transform, true);
                _cam = go.AddComponent<Camera>();
                _cam.depth = main.depth - 1f; // drawn before the main view, which shows it
                _cam.allowHDR = true;
                _cam.allowMSAA = false;
                _cam.useOcclusionCulling = false;
                var data = _cam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = false;
                data.antialiasing = AntialiasingMode.None;
                data.renderShadows = false;
                data.requiresDepthTexture = false;
                data.requiresColorTexture = false;
                PortalView.RegisterExtraCamera(_cam);
            }
            int w = Mathf.Max(64, main.pixelWidth), h = Mathf.Max(64, main.pixelHeight);
            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                if (_rt != null) { _cam.targetTexture = null; _rt.Release(); Destroy(_rt); }
                _rt = new RenderTexture(w, h, 24, RenderTextureFormat.DefaultHDR) { name = "Sky Window " + _face };
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
    }
}
