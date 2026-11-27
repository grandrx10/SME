using UnityEngine;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Death: the local player's screen darkens round the edges for a moment, when they strike someone with it or are
    /// struck by it. The dark closes in fast, then draws back off the edges of the screen (it shrinks away).
    /// </summary>
    public class DeathGloom : MonoBehaviour
    {
        const float In = 0.1f, Hold = 0.45f, Out = 0.9f;
        static DeathGloom _instance;
        RawImage _image;
        float _t = 99f;

        public static void Show()
        {
            if (_instance == null)
            {
                var go = new GameObject("DeathGloom");
                DontDestroyOnLoad(go);
                var canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 29000; // (under Blind's white)
                _instance = go.AddComponent<DeathGloom>();
                var img = new GameObject("Edges").AddComponent<RawImage>();
                img.transform.SetParent(go.transform, false);
                img.raycastTarget = false;
                img.texture = MakeTexture();
                var rt = img.rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                _instance._image = img;
            }
            _instance._t = 0f;
            _instance.gameObject.SetActive(true);
            _instance.Update();
        }

        /// <summary>A new round, or the player died: gone.</summary>
        public static void Clear()
        {
            if (_instance != null) _instance.gameObject.SetActive(false);
        }

        void Update()
        {
            _t += Time.deltaTime;
            // closes in from past the edges, holds, then draws back out past them
            float k = _t < In ? _t / In : _t < In + Hold ? 1f : 1f - Mathf.Clamp01((_t - In - Hold) / Out);
            k = k * k * (3f - 2f * k);
            float s = Mathf.Lerp(1.9f, 1f, k);
            _image.rectTransform.localScale = new Vector3(s, s, 1f);
            _image.color = new Color(1f, 1f, 1f, Mathf.Lerp(0.6f, 1f, k));
            if (_t > In + Hold + Out) gameObject.SetActive(false);
        }

        /// <summary>Clear in the middle, near black at the edges and corners.</summary>
        static Texture2D MakeTexture()
        {
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                float d = Mathf.Sqrt(u * u + v * v) / 1.2f; // (about 1.18 at the corners)
                float a = Mathf.Clamp01((d - 0.45f) / 0.5f);
                a = a * a * (3f - 2f * a) * 0.92f;
                px[y * n + x] = new Color32(6, 4, 8, (byte)(a * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
