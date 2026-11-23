using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace SuddenMageEncounter
{
    /// <summary>
    /// What the two people caught by The World see while it's being made: the caster, facing them, behind a little world,
    /// their arms closing over it to hold it, THE WORLD across the middle. Only a picture: a copy of the caster's model on a
    /// stage far below the map, filmed by its own camera (as <see cref="FacePreview"/> does).
    /// </summary>
    public class WorldCinematic : MonoBehaviour
    {
        const int Layer = 31;                       // (shared with FacePreview: only these stages use it)
        const float Fov = 26f, FrameHeight = 1.8f; // metres of them in shot
        const float GlobeRadius = 0.34f;
        // under the map, but not far off, where depth is still precise
        static readonly Vector3 StagePos = new(0f, -150f, 0f);
        const float LightSide = 38f;                // they face the sun, turned this far off it (degrees), so it models them
        // all in black and white: a black figure and black symbols on white, a white world with black continents (as on the card)
        static readonly Color Backdrop = new(0.93f, 0.93f, 0.93f, 1f), Body = new(0.07f, 0.07f, 0.07f, 1f);
        static readonly Color Sea = new(0.95f, 0.95f, 0.95f, 1f), Black = new(0.004f, 0.004f, 0.004f, 1f);

        class Arm { public Transform upper, lower, end, tip; public float side; }

        Transform _stage, _pivot, _globe, _spin, _behind, _orbit, _chest, _head, _cam;
        readonly Arm[] _arms = new Arm[2];
        readonly List<(Transform t, Quaternion r)> _bind = new();
        RuneDrawing _behindRunes, _orbitRunes;
        Mesh _land;
        Material _seaMat, _greyMat;
        FacePainter _face;
        RenderTexture _tex;
        Text _title;
        float _dist;
        bool _slammed;
        float _kick;
        Vector3 _look;

        /// <summary>Null when there's nobody to show (the caster has gone).</summary>
        public static WorldCinematic Create(PlayerController caster, RectTransform layer)
        {
            var nm = NetworkManager.Singleton;
            var prefab = nm != null && nm.NetworkConfig != null ? nm.NetworkConfig.PlayerPrefab : null;
            if (caster == null || prefab == null) return null;
            var pc = prefab.GetComponent<PlayerController>();
            Transform src = pc != null && pc.model != null ? pc.model : prefab.transform.Find("PlayerModel");
            if (src == null) return null;
            var go = new GameObject("WorldCinematic");
            go.transform.position = StagePos;
            var c = go.AddComponent<WorldCinematic>();
            c._stage = go.transform;
            if (!c.Stage(src, caster.ShownFace)) { Destroy(go); return null; }
            c.Show(layer);
            return c;
        }

        // ------------------------------------------------------------------ the stage

        bool Stage(Transform src, string face)
        {
            _pivot = new GameObject("Pivot").transform;
            _pivot.SetParent(_stage, false);
            // facing into the sun, wherever it is, so the front of them (what's filmed) is the lit side
            Vector3 sun = SunDirection();
            sun.y = 0f;
            if (sun.sqrMagnitude > 1e-4f) _pivot.rotation = Quaternion.LookRotation(-sun) * Quaternion.Euler(0f, LightSide, 0f);
            var inst = Instantiate(src.gameObject, _pivot); // keeps its place relative to the player root
            inst.SetActive(true);
            foreach (var c in inst.GetComponentsInChildren<Collider>(true)) Destroy(c);
            foreach (var m in inst.GetComponentsInChildren<MonoBehaviour>(true)) m.enabled = false;
            foreach (var a in inst.GetComponentsInChildren<Animator>(true)) a.enabled = false;
            var mpb = new MaterialPropertyBlock();
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = true;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
                r.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", Body);
                mpb.SetColor("_Color", Body);
                r.SetPropertyBlock(mpb);
            }
            // their face on it
            var strokes = FaceArt.Decode(face);
            if (strokes.Count > 0)
            {
                _face = new FacePainter();
                _face.Draw(strokes);
                FaceDecal.Attach(inst.transform, _pivot, _face.Texture);
            }

            var bones = Rig.MapBones(inst.transform);
            bones.TryGetValue("Bone.003", out _chest);
            bones.TryGetValue("Bone.001", out _head);
            if (_chest == null || _head == null) return false;
            for (int i = 0; i < 2; i++)
            {
                string n = i == 0 ? "Arm L" : "Arm R";
                if (!bones.TryGetValue(n + ".001", out var up) || !bones.TryGetValue(n + ".002", out var lo) ||
                    !bones.TryGetValue(n + ".003", out var end) || !bones.TryGetValue(n + ".003_end", out var tip)) return false;
                _arms[i] = new Arm { upper = up, lower = lo, end = end, tip = tip, side = i == 0 ? -1f : 1f };
            }
            foreach (var t in inst.GetComponentsInChildren<Transform>(true)) _bind.Add((t, t.localRotation));

            // the little world, in front of the chest
            _globe = new GameObject("Globe").transform;
            _globe.SetParent(_pivot, false);
            _globe.position = _chest.position + _pivot.forward * 0.42f + Vector3.down * 0.2f;
            _globe.localScale = Vector3.zero;
            _spin = new GameObject("Spin").transform;
            _spin.SetParent(_globe, false);
            var sea = Glow.Piece(_spin, Glow.SphereMesh, Sea, "Sea");
            sea.localScale = Glow.SphereScale(1f);
            // lit like a body (and, like one, written to the depth the screen outline reads: or theirs would show through it)
            var toon = Shader.Find("SME/Toon");
            if (toon != null) sea.GetComponent<MeshRenderer>().sharedMaterial = _seaMat = new Material(toon) { name = "Globe" };
            else Glow.Tint(sea.GetComponent<MeshRenderer>(), Sea * 0.4f);
            // (a big ball: a thinner ink edge than the usual share of its size, so the hands sit on the world, not its outline)
            var ink = sea.Find("Outline")?.GetComponent<MeshRenderer>();
            if (ink != null)
            {
                ink.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", Black);
                mpb.SetFloat("_Width", 0.06f);
                ink.SetPropertyBlock(mpb);
            }
            _land = LandMesh();
            Glow.Piece(_spin, _land, Black, "Land", false);
            // a band of runes turning round it
            var orbit = new GameObject("Orbit");
            orbit.transform.SetParent(_globe, false);
            _orbit = orbit.transform;
            _orbitRunes = new RuneDrawing(orbit);
            _orbitRunes.AddRune(DomainView.Strokes(), 2f * 1.6f, 0.05f);
            _orbitRunes.Progress = 1f;
            Blacken(_orbitRunes);

            // a great rune circle behind them
            var behind = new GameObject("Behind");
            behind.transform.SetParent(_pivot, false);
            _behind = behind.transform;
            _behind.position = _chest.position - _pivot.forward * 1.1f + Vector3.up * 0.1f;
            _behindRunes = new RuneDrawing(behind);
            _behindRunes.AddRune(DomainView.Strokes(), 2.5f, 0.03f);
            _behindRunes.Progress = 0f;
            Blacken(_behindRunes);

            Layers.SetLayerRecursive(_stage.gameObject, Layer);

            // (the title runs across the middle: over the world, clear of the head and the hands)
            _look = _globe.position + Vector3.up * 0.1f;
            _dist = FrameHeight * 0.5f / Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad);
            var camGo = new GameObject("Camera");
            camGo.transform.SetParent(_stage, false);
            _cam = camGo.transform;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Backdrop;
            cam.cullingMask = 1 << Layer;
            cam.fieldOfView = Fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = Practice.ViewDistance; // (as the game's own: the screen outline paints over any empty background nearer than its fade)
            cam.allowHDR = true;
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true; // (it glows like the rest of the game)
            var main = Camera.main;
            if (main != null)
            {
                main.cullingMask &= ~(1 << Layer); // (the game's own view never shows the stages)
                data.volumeLayerMask = main.GetUniversalAdditionalCameraData().volumeLayerMask;
                cam.depth = main.depth - 3f;
            }
            int w = Mathf.Max(64, Screen.width), h = Mathf.Max(64, Screen.height);
            _tex = new RenderTexture(w, h, 24, RenderTextureFormat.Default) { name = "WorldCinematic" };
            _tex.Create();
            cam.targetTexture = _tex;
            cam.aspect = w / (float)h;
            return true;
        }

        /// <summary>A drawing in plain black (no ink edge: black round black would only blot it).</summary>
        static void Blacken(RuneDrawing d)
        {
            d.SetColour(Black);
            var ink = d.Host.Find("Outline");
            if (ink != null) ink.gameObject.SetActive(false);
        }

        /// <summary>The way the scene's sunlight travels.</summary>
        static Vector3 SunDirection()
        {
            if (RenderSettings.sun != null) return RenderSettings.sun.transform.forward;
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && l.enabled) return l.transform.forward;
            return Vector3.forward;
        }

        RectTransform _picture;

        void Show(RectTransform layer)
        {
            var img = UIFactory.Rect("Picture", layer).gameObject.AddComponent<RawImage>();
            img.texture = _tex;
            var grey = Shader.Find("SME/GreyPicture");
            if (grey != null) img.material = _greyMat = new Material(grey) { name = "Grey" };
            img.raycastTarget = false;
            _picture = UIFactory.Stretch(img.rectTransform);
            SetShown(false); // (until the screen has shut: they still see where they were)
            _title = UIFactory.Text("Title", _picture, "THE WORLD", 150, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            UIFactory.Place(_title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1800f, 260f));
            WorldScreen.Ink(_title, 8f);
            _title.rectTransform.localScale = Vector3.zero;
            Tick(0f, 1f);
        }

        /// <summary>The land on the globe: a thin skin just over the sea, wherever the (made-up) continents are.</summary>
        static Mesh LandMesh()
        {
            // continents: a direction, and how far (radians) each reaches from it
            var blobs = new (Vector3 dir, float reach)[]
            {
                (new Vector3(-0.35f, 0.45f, 0.82f), 0.5f), (new Vector3(0.1f, 0.62f, 0.78f), 0.34f), (new Vector3(-0.2f, 0f, 0.98f), 0.24f),
                (new Vector3(0.28f, -0.6f, 0.75f), 0.4f), (new Vector3(0.88f, 0.3f, 0.36f), 0.42f), (new Vector3(0.95f, -0.2f, -0.2f), 0.3f),
                (new Vector3(0.3f, 0.35f, -0.88f), 0.5f), (new Vector3(-0.4f, -0.35f, -0.85f), 0.38f), (new Vector3(-0.95f, 0.05f, -0.3f), 0.4f),
            };
            bool IsLand(Vector3 d)
            {
                float wob = 0.09f * Mathf.Sin(d.x * 9f + d.y * 5f) + 0.07f * Mathf.Sin(d.z * 12f - d.x * 7f);
                foreach (var b in blobs)
                    if (Mathf.Acos(Mathf.Clamp(Vector3.Dot(d, b.dir.normalized), -1f, 1f)) < b.reach + wob) return true;
                return false;
            }
            const int Lon = 220, Lat = 110;
            var v = new List<Vector3>();
            var on = new List<bool>();
            for (int y = 0; y <= Lat; y++)
            for (int x = 0; x <= Lon; x++)
            {
                float a = x / (float)Lon * Mathf.PI * 2f, p = (y / (float)Lat - 0.5f) * Mathf.PI;
                var d = new Vector3(Mathf.Cos(p) * Mathf.Sin(a), Mathf.Sin(p), Mathf.Cos(p) * Mathf.Cos(a));
                v.Add(d * 1.02f);
                on.Add(IsLand(d));
            }
            var tri = new List<int>();
            void Face(int a, int b, int c)
            {
                if (!on[a] || !on[b] || !on[c]) return;
                // (facing outward)
                if (Vector3.Dot(Vector3.Cross(v[b] - v[a], v[c] - v[a]), v[a]) < 0f) (b, c) = (c, b);
                tri.Add(a); tri.Add(b); tri.Add(c);
            }
            for (int y = 0; y < Lat; y++)
            for (int x = 0; x < Lon; x++)
            {
                int k = y * (Lon + 1) + x;
                Face(k, k + 1, k + Lon + 1);
                Face(k + 1, k + Lon + 2, k + Lon + 1);
            }
            var m = new Mesh { name = "Land", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(v);
            m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------ the shot

        /// <summary>On screen (and being filmed), or not yet.</summary>
        public void SetShown(bool on)
        {
            if (_picture != null && _picture.gameObject.activeSelf != on) _picture.gameObject.SetActive(on);
            if (_cam != null) _cam.GetComponent<Camera>().enabled = on;
        }

        /// <summary>The picture <paramref name="t"/> seconds in (it starts before 0: the screen is still shut), lasting <paramref name="length"/>.</summary>
        public void Tick(float t, float length)
        {
            if (_stage == null) return;
            float dt = Time.deltaTime;
            // the world swells out of nothing between their hands, which close over it
            float grow = Mathf.Clamp01((t - 0.1f) / 0.45f);
            float size = grow <= 0f ? 0f : Mathf.Max(0f, RuneSparks.EaseOutBack01(grow));
            float beat = 1f + 0.025f * Mathf.Sin(t * 9f);
            _globe.localScale = Vector3.one * Mathf.Max(0.001f, GlobeRadius * size * beat);
            _spin.localRotation = Quaternion.Euler(0f, 0f, 18f) * Quaternion.Euler(0f, -t * 55f, 0f);
            _orbit.localRotation = Quaternion.Euler(14f, 0f, -12f) * Quaternion.Euler(0f, t * 70f, 0f);
            // (drawn flat in its own XZ: stood up to face the camera)
            _behind.localRotation = Quaternion.LookRotation(Vector3.up, Vector3.forward) * Quaternion.Euler(0f, t * 22f, 0f);
            _behindRunes.Progress = Mathf.Clamp01(t / 0.6f);

            Pose(t, grow);

            // the title slams in
            float titleAt = 0.45f;
            if (t >= titleAt && !_slammed) { _slammed = true; _kick = 1f; }
            float k = Mathf.Clamp01((t - titleAt) / 0.14f), over = Mathf.Max(0f, t - titleAt);
            _title.rectTransform.localScale = t < titleAt ? Vector3.zero : Vector3.one * (Mathf.Lerp(2.6f, 1f, k) * (1f + 0.02f * over));
            _kick = Mathf.MoveTowards(_kick, 0f, dt * 2.2f);
            float trauma = _kick * _kick;
            _title.rectTransform.anchoredPosition = Random.insideUnitCircle * (trauma * 26f + 1.5f);

            // the camera: low, pushing slowly in, jolted as the title lands
            float u = Mathf.Clamp01(t / Mathf.Max(0.1f, length));
            float dist = _dist * Mathf.Lerp(1.18f, 0.94f, 1f - (1f - u) * (1f - u));
            Vector3 eye = _look + _pivot.forward * dist + Vector3.down * 0.3f + Random.insideUnitSphere * (0.06f * trauma + 0.004f);
            _cam.position = eye;
            _cam.rotation = Quaternion.LookRotation(_look - eye) * Quaternion.Euler(0f, 0f, Mathf.Lerp(-3f, 1.5f, u));
        }

        /// <summary>Head bowed over it; both arms brought in from wide, hands over the top of the world.</summary>
        void Pose(float t, float grow)
        {
            foreach (var (b, r) in _bind) b.localRotation = r;
            Vector3 right = _pivot.right, fwd = _pivot.forward;
            float bow = Mathf.Lerp(-6f, 14f, grow);
            _chest.rotation = Quaternion.AngleAxis(6f * grow, right) * _chest.rotation;
            _head.rotation = Quaternion.AngleAxis(bow, right) * _head.rotation;
            float hold = grow * grow * (3f - 2f * grow);
            Vector3 chest = _chest.position;
            foreach (var a in _arms)
            {
                Vector3 wide = chest + right * (a.side * 0.55f) + Vector3.up * 0.28f + fwd * 0.22f;
                Vector3 over = _globe.position + (right * (a.side * 0.62f) + Vector3.up * 0.72f + fwd * 0.3f).normalized * (GlobeRadius * 1.12f);
                over += Vector3.up * (0.012f * Mathf.Sin(t * 5f + a.side)) + Random.insideUnitSphere * 0.003f;
                Vector3 hand = Vector3.Lerp(wide, over, hold);
                Vector3 shoulder = a.upper.position;
                Vector3 pole = shoulder + right * (a.side * 0.6f) + Vector3.up * 0.05f - fwd * 0.05f; // elbows out
                ProceduralAnimator.TwoBone(a.upper, a.lower, a.end, hand, pole);
                // fingers curling down over the front of it
                Vector3 fingers = Vector3.Slerp((fwd + Vector3.up).normalized, (right * (-a.side * 0.75f) + fwd * 0.7f + Vector3.down * 0.35f).normalized, hold);
                ProceduralAnimator.Aim(a.end, a.tip.position, a.end.position + fingers);
            }
        }

        public void Remove()
        {
            if (_picture != null) Destroy(_picture.gameObject);
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (_picture != null) Destroy(_picture.gameObject);
            _behindRunes?.Release();
            _orbitRunes?.Release();
            if (_land != null) Destroy(_land);
            if (_seaMat != null) Destroy(_seaMat);
            if (_greyMat != null) Destroy(_greyMat);
            if (_face != null) Destroy(_face.Texture);
            if (_tex != null) { _tex.Release(); Destroy(_tex); }
        }
    }
}
