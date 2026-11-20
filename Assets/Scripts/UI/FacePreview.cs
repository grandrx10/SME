using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Live render of your character's head for the face editor: a copy of the player model on a little stage far
    /// below the map, wearing <see cref="Painter"/>'s texture, gently turning side to side. Same trick as WeaponPreview.
    /// </summary>
    public static class FacePreview
    {
        const int Layer = 31;                    // shared with WeaponPreview (only these stages use it)
        public const int TexWidth = 420, TexHeight = 560;
        const float Fov = 18f;
        const float FrameHeight = 0.75f;         // metres of character shown (head and shoulders)
        const float SwayAngle = 35f, SwaySpeed = 0.6f;

        /// <summary>The texture the editor draws into; shown on the preview head.</summary>
        public static FacePainter Painter => _painter ??= new FacePainter();
        static FacePainter _painter;

        static Transform _stage, _pivot, _model;
        static Camera _cam;
        static RenderTexture _tex;
        static bool _rendering;
        static int _tint = int.MinValue;

        public static Texture Get()
        {
            if (_cam == null) Build();
            return _cam != null ? _tex : null;
        }

        public static void SetRendering(bool on)
        {
            _rendering = on;
            if (_cam != null) _cam.enabled = on;
            if (on) ApplyTint();
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
                _stage = new GameObject("FacePreview").transform;
                _stage.position = new Vector3(-1000f, -5000f, 0f);
                _stage.gameObject.AddComponent<Sway>();
                Object.DontDestroyOnLoad(_stage.gameObject);
            }
            var main = Camera.main;
            if (main != null) main.cullingMask &= ~(1 << Layer);

            _pivot = new GameObject("Pivot").transform;
            _pivot.SetParent(_stage, false);

            var inst = Object.Instantiate(src.gameObject, _pivot); // keeps its place relative to the player root
            inst.SetActive(true);
            _model = inst.transform;
            foreach (var c in inst.GetComponentsInChildren<Collider>(true)) Object.Destroy(c);
            foreach (var m in inst.GetComponentsInChildren<MonoBehaviour>(true)) m.enabled = false;
            foreach (var a in inst.GetComponentsInChildren<Animator>(true)) a.enabled = false;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
            }
            FaceDecal.Attach(_model, _pivot, Painter.Texture);
            Layers.SetLayerRecursive(inst, Layer);
            _tint = int.MinValue;
            ApplyTint();

            // aim at the head from the front
            var bones = Rig.MapBones(_model);
            Vector3 head = _pivot.position + Vector3.up * 1.6f;
            if (bones.TryGetValue("Bone.001", out var h0))
                head = bones.TryGetValue("Bone.001_end", out var h1) ? (h0.position + h1.position) * 0.5f : h0.position;
            Vector3 look = head - Vector3.up * FrameHeight * 0.15f; // a little below the head so the shoulders show

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
            float dist = FrameHeight * 0.5f / Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad);
            camGo.transform.position = look + _pivot.forward * dist;
            camGo.transform.LookAt(look);
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = dist + 5f;

            _tex = new RenderTexture(TexWidth, TexHeight, 24, RenderTextureFormat.ARGB32) { name = "FacePreview", antiAliasing = 4 };
            _tex.Create();
            _cam.targetTexture = _tex;
            _cam.enabled = _rendering;
        }

        /// <summary>The preview wears your colour (the one in game if you're playing, else your preferred one).</summary>
        static void ApplyTint()
        {
            if (_model == null) return;
            var local = PlayerController.Local;
            int index = local != null && local.ColorIndex.Value != 255 ? local.ColorIndex.Value : GameSettings.PlayerColor;
            if (index == _tint) return;
            _tint = index;
            var mpb = new MaterialPropertyBlock();
            Color c = PlayerPalette.Get(index);
            foreach (var r in _model.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name == FaceDecal.ObjectName) continue;
                r.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", c);
                mpb.SetColor("_Color", c);
                r.SetPropertyBlock(mpb);
            }
        }

        /// <summary>The colour behind the drawing pad: your character's colour.</summary>
        public static Color SkinColor
        {
            get
            {
                var local = PlayerController.Local;
                int index = local != null && local.ColorIndex.Value != 255 ? local.ColorIndex.Value : GameSettings.PlayerColor;
                return PlayerPalette.Get(index);
            }
        }

        class Sway : MonoBehaviour
        {
            void Update()
            {
                if (!_rendering || _pivot == null) return;
                _pivot.localRotation = Quaternion.Euler(0f, Mathf.Sin(Time.unscaledTime * SwaySpeed) * SwayAngle, 0f);
            }
        }
    }
}
