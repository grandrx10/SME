using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Pictures of cosmetics in their rolled colour, rendered on a little stage far below the map (a few a frame, then
    /// kept), and one live, slowly turning picture for showing off what came out of a crate.
    /// </summary>
    public class CosmeticIcons : MonoBehaviour
    {
        const int Layer = 31; // (shared with FacePreview: only these stages use it)
        const int Size = 192, LiveSize = 512, PerFrame = 4;
        const float Fov = 20f, SpinSpeed = 55f;

        static CosmeticIcons _instance;

        readonly Dictionary<(CosmeticId, int), RenderTexture> _done = new();
        readonly Queue<(CosmeticId, int)> _queue = new();
        RenderTexture _scratch, _live;
        Camera _cam, _liveCam;
        Transform _spin;
        GameObject _shown;

        static CosmeticIcons Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject("CosmeticIcons");
                go.transform.position = new Vector3(-1000f, -5200f, 0f);
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<CosmeticIcons>();
                var main = Camera.main;
                if (main != null) main.cullingMask &= ~(1 << Layer);
                return _instance;
            }
        }

        /// <summary>The picture (it may still be blank for a frame or two).</summary>
        public static Texture Get(CosmeticId id, int colour)
        {
            var me = Instance;
            if (me._done.TryGetValue((id, colour), out var t) && t != null)
            {
                if (!t.IsCreated()) me._queue.Enqueue((id, colour)); // (lost with the graphics device)
                return t;
            }
            t = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGB32) { name = "Cosmetic " + id };
            t.Create();
            var was = RenderTexture.active;
            RenderTexture.active = t;
            GL.Clear(true, true, Color.clear);
            RenderTexture.active = was;
            me._done[(id, colour)] = t;
            me._queue.Enqueue((id, colour));
            return t;
        }

        /// <summary>Puts this one on the turntable.</summary>
        public static Texture Live(CosmeticItem item)
        {
            var me = Instance;
            if (me._liveCam == null) me.BuildLive();
            if (me._shown != null) Destroy(me._shown);
            me._spin.localRotation = Quaternion.Euler(0f, 25f, 0f);
            me._shown = CosmeticLook.Make(item.id, item.colour, me._spin);
            if (me._shown != null)
            {
                Dress(me._shown);
                // turned about its own middle
                var b = me._shown.GetComponent<MeshFilter>().sharedMesh.bounds;
                me._shown.transform.localPosition = -b.center;
                Frame(me._liveCam, me._spin.position, b.extents.magnitude, 1f);
            }
            me._liveCam.enabled = true;
            return me._live;
        }

        public static void StopLive()
        {
            if (_instance == null || _instance._liveCam == null) return;
            _instance._liveCam.enabled = false;
            if (_instance._shown != null) Destroy(_instance._shown);
            _instance._shown = null;
        }

        void Update()
        {
            for (int i = 0; i < PerFrame && _queue.Count > 0; i++)
            {
                var (id, colour) = _queue.Dequeue();
                if (_done.TryGetValue((id, colour), out var t) && t != null) Shoot(id, colour, t);
            }
            if (_liveCam != null && _liveCam.enabled) _spin.Rotate(0f, SpinSpeed * Time.unscaledDeltaTime, 0f, Space.Self);
        }

        void Shoot(CosmeticId id, int colour, RenderTexture to)
        {
            if (_cam == null)
            {
                _scratch = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { name = "CosmeticScratch", antiAliasing = 4 };
                _scratch.Create();
                _cam = Cam("IconCamera", _scratch);
                _cam.enabled = false;
            }
            var go = CosmeticLook.Make(id, colour, transform);
            if (go == null) return;
            Dress(go);
            go.transform.localRotation = Quaternion.Euler(0f, 25f, 0f);
            var b = go.GetComponent<MeshFilter>().sharedMesh.bounds;
            go.transform.localPosition = -(go.transform.localRotation * b.center);
            Frame(_cam, transform.position, b.extents.magnitude, 0.8f);
            _cam.Render();
            Graphics.Blit(_scratch, to);
            go.SetActive(false);
            Destroy(go);
        }

        void BuildLive()
        {
            var stage = new GameObject("Turntable").transform;
            stage.SetParent(transform, false);
            stage.localPosition = new Vector3(60f, 0f, 0f);
            _spin = stage;
            _live = new RenderTexture(LiveSize, LiveSize, 24, RenderTextureFormat.ARGB32) { name = "CosmeticLive", antiAliasing = 4 };
            _live.Create();
            _liveCam = Cam("LiveCamera", _live);
            gameObject.layer = stage.gameObject.layer = Layer;
        }

        Camera Cam(string name, RenderTexture target)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var c = go.AddComponent<Camera>();
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = new Color(0f, 0f, 0f, 0f);
            c.cullingMask = 1 << Layer;
            c.fieldOfView = Fov;
            c.aspect = 1f;
            c.allowHDR = false;
            c.allowMSAA = true;
            c.targetTexture = target;
            return c;
        }

        static void Frame(Camera c, Vector3 look, float radius, float room)
        {
            float dist = radius * room / Mathf.Sin(Fov * 0.5f * Mathf.Deg2Rad);
            c.transform.position = look + Quaternion.Euler(-14f, 0f, 0f) * Vector3.forward * dist;
            c.transform.LookAt(look);
            c.nearClipPlane = Mathf.Max(0.02f, dist - radius * 2f);
            c.farClipPlane = dist + radius * 2f;
        }

        static void Dress(GameObject go)
        {
            Layers.SetLayerRecursive(go, Layer);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
        }
    }
}
