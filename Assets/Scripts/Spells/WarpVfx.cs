using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace SuddenMageEncounter
{
    /// <summary>
    /// A view of the scene without anyone who's warping: a camera matching the main one, skipping the
    /// <see cref="Layers.WarpHidden"/> layer, rendering at half resolution. Warp rifts sample it at screen position,
    /// so through the rift you see straight through the body to whatever is behind it. Only renders on frames
    /// something asks for it.
    /// </summary>
    [DefaultExecutionOrder(400)] // after the views that ask for it, after the camera rig
    public class SeeThrough : MonoBehaviour
    {
        static SeeThrough _i;
        Camera _cam;
        RenderTexture _rt;
        int _wanted = -1;

        /// <summary>Ask for this frame's see-through picture (the returned texture gets it).</summary>
        public static RenderTexture Want()
        {
            if (_i == null)
            {
                var go = new GameObject("SeeThrough");
                DontDestroyOnLoad(go);
                _i = go.AddComponent<SeeThrough>();
            }
            _i._wanted = Time.frameCount;
            return _i.Ensure();
        }

        RenderTexture Ensure()
        {
            var main = Camera.main;
            if (_cam == null && main != null)
            {
                PortalView.ShowLayersOnMain();
                _cam = gameObject.AddComponent<Camera>();
                _cam.depth = main.depth - 2f;
                _cam.allowHDR = true;
                _cam.allowMSAA = false;
                _cam.enabled = false;
                var data = _cam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = false;
                data.antialiasing = AntialiasingMode.None;
                data.requiresDepthTexture = false;
                data.requiresColorTexture = false;
                PortalView.RegisterExtraCamera(_cam);
            }
            int w = Mathf.Max(64, Screen.width / 2), h = Mathf.Max(64, Screen.height / 2);
            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                if (_rt != null) { if (_cam != null) _cam.targetTexture = null; _rt.Release(); Destroy(_rt); }
                _rt = new RenderTexture(w, h, 24, RenderTextureFormat.DefaultHDR) { name = "SeeThrough" };
                _rt.Create();
                if (_cam != null) _cam.targetTexture = _rt;
            }
            return _rt;
        }

        void LateUpdate()
        {
            var main = Camera.main;
            bool on = _wanted == Time.frameCount && main != null && _cam != null;
            if (_cam != null) _cam.enabled = on;
            if (!on) return;
            _cam.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
            _cam.fieldOfView = main.fieldOfView;
            _cam.aspect = main.aspect;
            _cam.nearClipPlane = main.nearClipPlane;
            _cam.farClipPlane = main.farClipPlane;
            _cam.clearFlags = main.clearFlags;
            _cam.backgroundColor = main.backgroundColor;
            _cam.cullingMask = main.cullingMask & ~((1 << Layers.WarpHidden) | (1 << Layers.PortalSurface));
            _cam.targetTexture = _rt;
        }
    }

    // ====================================================================== Warp Self

    /// <summary>
    /// Warp Self, seen by everyone. Charging: the hands pull apart and the space between them is a window straight
    /// through the body (onto whatever is behind it), while arcane runes pour off the body, more and more. At the
    /// destination runes start appearing the moment it's cast. Folding: the body folds top and bottom together into
    /// its middle and vanishes in a burst of runes; then it unfolds the same way at the destination, in another.
    /// </summary>
    public class WarpView : MonoBehaviour
    {
        static readonly Vector3 BodySpread = new(0.45f, 0.95f, 0.45f);

        WorldNet.WarpFx _fx;
        Transform _hole;
        Renderer _holeR;
        Material _mat;
        RuneSpew _leaving, _arriving;
        ProceduralAnimator _animator;
        readonly List<(GameObject go, int layer)> _hidden = new();
        bool _replayed, _folded, _restored;
        Vector3 _origin;
        static readonly int TexId = Shader.PropertyToID("_PortalTex"), OpenId = Shader.PropertyToID("_Open");

        public static WarpView Create(WorldNet.WarpFx fx)
        {
            var go = new GameObject("Warp " + fx.id) { layer = Layers.Fx };
            var v = go.AddComponent<WarpView>();
            v._fx = fx;
            v._replayed = Debris.Suppressed;
            var hole = new GameObject("Window") { layer = Layers.PortalSurface };
            hole.transform.SetParent(go.transform, false);
            hole.AddComponent<MeshFilter>().sharedMesh = PortalView.OpeningMesh;
            var r = hole.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            v._mat = new Material(Shader.Find("SME/Portal")) { name = "Warp Window" };
            v._mat.SetFloat("_Shape", 1f);    // an oval...
            v._mat.SetFloat("_EdgeWidth", 0f); // ...with no rim: just a hole
            // and no depth, so the screen outline doesn't draw a ring round it
            v._mat.SetShaderPassEnabled("DepthOnly", false);
            v._mat.SetShaderPassEnabled("DepthNormals", false);
            v._mat.SetFloat(OpenId, 1f);
            r.sharedMaterial = v._mat;
            v._holeR = r;
            v._hole = hole.transform;
            var c = WorldNet.FindCombatant(fx.caster);
            if (c != null)
            {
                v._origin = c.transform.position;
                v._animator = c.Animator;
            }
            if (!v._replayed)
            {
                // runes gather where they're going from the moment it's cast
                Vector3 there = fx.dest + Vector3.up * 0.95f;
                v._arriving = RuneSpew.Create(() => there, () => v != null && !v._restored, SpellPlans.WarpArrive, Glow.Arcane, 18f, BodySpread);
            }
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            var c = WorldNet.FindCombatant(_fx.caster);
            bool cut = _fx.ended >= 0;
            if (c == null || !c.IsAlive || cut || t > SpellPlans.WarpDone || _replayed)
            {
                if (!_replayed && c != null && c.IsAlive)
                {
                    if (cut && _fx.how == 0) SpellFx.Sparkle(c.transform.position, SpellVisuals.ArcaneBlue, 0.8f); // fizzled
                    else if (cut) _arriving?.Burst(10);                                                     // unfolded at once
                }
                Restore();
                Destroy(gameObject);
                return;
            }

            Vector3 body = c.transform.position;
            float fold = 1f; // 1 standing, 0 folded away
            if (t < SpellPlans.WarpCharge)
            {
                _origin = body;
                // the window between the hands: see straight through the body
                Vector3 rh = SpellVisuals.RightHand(c), lh = SpellVisuals.LeftHand(c);
                float k = Mathf.InverseLerp(0.12f, SpellPlans.WarpCharge, t);
                float w = Mathf.Max(0.02f, (rh - lh).magnitude - 0.1f) * k, h = Mathf.Lerp(0.05f, 0.75f, k);
                var cam = Camera.main;
                Vector3 mid = (rh + lh) * 0.5f;
                Vector3 toCam = cam != null ? (cam.transform.position - mid).normalized : -c.transform.forward;
                _holeR.enabled = w > 0.03f;
                _hole.SetPositionAndRotation(mid + toCam * 0.3f, Quaternion.LookRotation(toCam, cam != null ? cam.transform.up : Vector3.up));
                _hole.localScale = new Vector3(w, h, 1f);
                _mat.SetTexture(TexId, SeeThrough.Want());
                Hide(c);
                // runes pouring off the body, more and more
                _leaving ??= RuneSpew.Create(() => _origin, () => this != null && !_restored, SpellPlans.WarpArrive, Glow.Arcane, 24f, BodySpread);
            }
            else
            {
                _holeR.enabled = false;
                Unhide();
                if (t < SpellPlans.WarpArrive)
                {
                    // folding top and bottom together into the middle
                    float u = (t - SpellPlans.WarpCharge) / SpellPlans.WarpFold;
                    fold = 1f - u * u;
                    if (u > 0.5f && _leaving != null) { _leaving.Burst(26); _leaving.Stop(); _leaving = null; }
                }
                else
                {
                    if (!_folded)
                    {
                        _folded = true;
                        if (_leaving != null) { _leaving.Burst(26); _leaving.Stop(); _leaving = null; }
                        _arriving?.Burst(26);
                        CameraRig.ShakeFrom(_fx.dest, 0.12f, 0.15f);
                    }
                    // and unfolding the same way at the destination
                    float u = (t - SpellPlans.WarpArrive) / SpellPlans.WarpUnfold;
                    fold = 1f - (1f - u) * (1f - u);
                }
            }
            Squash(fold);
        }

        /// <summary>Folds the body flat about its middle (top and bottom come together), keeping its own size and place.</summary>
        void Squash(float s)
        {
            if (_animator == null) return;
            s = Mathf.Max(0.001f, s);
            // a little wider as it flattens (the animator applies it, after it has posed the body)
            _animator.Fold = new Vector2(1f + 0.25f * (1f - s), s);
        }

        /// <summary>The body goes on the layer the see-through camera skips.</summary>
        void Hide(Combatant c)
        {
            if (_hidden.Count > 0) return;
            foreach (var r in c.GetComponentsInChildren<Renderer>(true))
            {
                var go = r.gameObject;
                if (go.layer == Layers.Fx || go.layer == Layers.PortalSurface) continue;
                _hidden.Add((go, go.layer));
                go.layer = Layers.WarpHidden;
            }
        }

        void Unhide()
        {
            foreach (var (go, layer) in _hidden) if (go != null) go.layer = layer;
            _hidden.Clear();
        }

        void Restore()
        {
            if (_restored) return;
            _restored = true;
            Unhide();
            if (_animator != null) _animator.Fold = Vector2.one;
            if (_leaving != null) _leaving.Stop();
            if (_arriving != null) _arriving.Stop();
        }

        void OnDestroy()
        {
            Restore();
            if (_mat != null) Destroy(_mat);
        }
    }

    // ====================================================================== Arcane Swiftness

    /// <summary>
    /// Arcane Swiftness: blue runes come off the caster while they crouch; then, as they run, afterimages are left
    /// behind them (The Last Chromatist's AfterimageAura): each a frozen copy of the body in the pose it had, built
    /// on a copy of its rig (so it keeps the model's exact size and shape), blue when left, paling to white as it
    /// ages while it thins out in a dithered stipple until it's gone. Copies are pooled.
    /// </summary>
    public class SwiftView : MonoBehaviour
    {
        const float Every = 0.05f, Life = 1.1f, StartOpacity = 0.85f;
        static readonly Color Blue = new(0.25f, 0.75f, 4f, 1f);
        static readonly Color White = new(2.6f, 2.8f, 3f, 1f);
        static readonly int ColourId = Shader.PropertyToID("_BaseColor"), FadeId = Shader.PropertyToID("_Fade");
        static Material _mat;

        class Ghost
        {
            public GameObject root;
            public Transform[] bones;
            public Renderer[] sources, renderers;
            public float age;
        }

        WorldNet.SwiftFx _fx;
        RuneSpew _runes;
        readonly List<Ghost> _live = new();
        readonly Stack<Ghost> _pool = new();
        readonly List<Renderer> _sourceRenderers = new();
        MaterialPropertyBlock _block;
        float _clock;
        bool _replayed;
        Color _young = Blue, _old = White;
        double _runFrom, _runUntil;
        bool _prep = true;

        public static SwiftView Create(WorldNet.SwiftFx fx)
        {
            var go = new GameObject("Swiftness") { layer = Layers.Fx };
            var v = go.AddComponent<SwiftView>();
            v._fx = fx;
            v._replayed = Debris.Suppressed;
            v._block = new MaterialPropertyBlock();
            if (_mat == null) _mat = new Material(Shader.Find("SME/Afterimage") ?? Shader.Find("SME/Glow")) { name = "Afterimage" };
            v._runFrom = fx.start + SpellPlans.SwiftPrep;
            v._runUntil = v._runFrom + SpellPlans.SwiftTime;
            return v;
        }

        /// <summary>
        /// The same trail of afterimages behind <paramref name="who"/> from <paramref name="from"/> until <paramref name="until"/>, in
        /// other colours (Wheel of Fortune's 6: golden). No runes first.
        /// </summary>
        public static SwiftView Trail(ulong who, double from, double until, Color young, Color old)
        {
            var v = Create(new WorldNet.SwiftFx { caster = who, start = from });
            v.name = "Afterimages";
            v._young = young;
            v._old = old;
            v._prep = false;
            v._runFrom = from;
            v._runUntil = until;
            return v;
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            var c = WorldNet.FindCombatant(_fx.caster);
            bool alive = c != null && c.IsAlive && c.Visible && _fx.ended < 0 && !_replayed;
            bool prepping = alive && _prep && now < _runFrom;
            bool running = alive && now >= _runFrom && now <= _runUntil;

            if (prepping && _runes == null)
                _runes = RuneSpew.Create(() => c != null ? c.transform.position : transform.position, () => this != null && _fx.ended < 0, SpellPlans.SwiftPrep, Glow.Arcane, 40f);
            if (running)
            {
                _clock += Time.deltaTime;
                if (_clock >= Every) { _clock = 0f; Snapshot(c.model); }
            }
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var g = _live[i];
                g.age += Time.deltaTime;
                float left = 1f - g.age / Life;
                if (left <= 0f)
                {
                    g.root.SetActive(false);
                    _live.RemoveAt(i);
                    _pool.Push(g);
                    continue;
                }
                Shade(g, StartOpacity * left, 1f - left);
            }
            if (!prepping && !running && _live.Count == 0) Destroy(gameObject);
        }

        void Snapshot(Transform model)
        {
            if (model == null) return;
            var source = model.GetComponentsInChildren<Transform>(true);
            _sourceRenderers.Clear();
            model.GetComponentsInChildren(true, _sourceRenderers);
            _sourceRenderers.RemoveAll(r => !(r is SkinnedMeshRenderer || r is MeshRenderer) || r.name == FaceDecal.ObjectName || CosmeticLook.IsHull(r));

            Ghost g = null;
            while (_pool.Count > 0 && g == null)
            {
                var reused = _pool.Pop();
                if (reused.root != null && reused.bones.Length == source.Length && reused.sources.Length == _sourceRenderers.Count) g = reused;
                else if (reused.root != null) Destroy(reused.root);
            }
            g ??= Build(model, source);

            // the pose as it is this frame (after the procedural animator), at the model's real size
            g.root.transform.SetPositionAndRotation(model.position, model.rotation);
            g.root.transform.localScale = model.lossyScale;
            for (int i = 1; i < source.Length; i++)
            {
                var bone = g.bones[i];
                bone.localPosition = source[i].localPosition;
                bone.localRotation = source[i].localRotation;
                bone.localScale = source[i].localScale;
                bone.gameObject.SetActive(source[i].gameObject.activeSelf);
            }
            for (int i = 0; i < g.renderers.Length; i++) g.renderers[i].enabled = g.sources[i] != null && g.sources[i].enabled;
            g.age = 0f;
            g.root.SetActive(true);
            _live.Add(g);
            Shade(g, StartOpacity, 0f);
        }

        /// <summary>A copy of the model's transforms with its renderers rebuilt on them, and nothing else.</summary>
        Ghost Build(Transform model, Transform[] source)
        {
            var copies = new Dictionary<Transform, Transform>(source.Length);
            var bones = new Transform[source.Length];
            var root = new GameObject("Afterimage") { layer = Layers.Fx };
            root.SetActive(false);
            root.transform.SetParent(transform, false);
            copies[model] = root.transform;
            bones[0] = root.transform;
            for (int i = 1; i < source.Length; i++)
            {
                var copy = new GameObject(source[i].name) { layer = Layers.Fx }.transform;
                copy.SetParent(copies[source[i].parent], false);
                copies[source[i]] = copy;
                bones[i] = copy;
            }
            var sources = _sourceRenderers.ToArray();
            var renderers = new Renderer[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                var from = sources[i];
                var host = copies[from.transform].gameObject;
                Renderer made;
                if (from is SkinnedMeshRenderer skinned)
                {
                    var to = host.AddComponent<SkinnedMeshRenderer>();
                    to.sharedMesh = skinned.sharedMesh;
                    var fromBones = skinned.bones;
                    var toBones = new Transform[fromBones.Length];
                    for (int b = 0; b < fromBones.Length; b++)
                        toBones[b] = fromBones[b] != null && copies.TryGetValue(fromBones[b], out var mapped) ? mapped : null;
                    to.bones = toBones;
                    to.rootBone = skinned.rootBone != null && copies.TryGetValue(skinned.rootBone, out var rb) ? rb : null;
                    to.localBounds = skinned.localBounds;
                    to.updateWhenOffscreen = true;
                    made = to;
                }
                else
                {
                    var filter = from.GetComponent<MeshFilter>();
                    host.AddComponent<MeshFilter>().sharedMesh = filter != null ? filter.sharedMesh : null;
                    made = host.AddComponent<MeshRenderer>();
                }
                var mats = new Material[Mathf.Max(1, from.sharedMaterials.Length)];
                for (int m = 0; m < mats.Length; m++) mats[m] = _mat;
                made.sharedMaterials = mats;
                made.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                made.receiveShadows = false;
                renderers[i] = made;
            }
            return new Ghost { root = root, bones = bones, sources = sources, renderers = renderers };
        }

        /// <summary>Blue paling to white as it ages (<paramref name="aged"/> 0 → 1), thinning out to nothing (<paramref name="strength"/> → 0).</summary>
        void Shade(Ghost g, float strength, float aged)
        {
            _block.Clear();
            _block.SetColor(ColourId, Color.Lerp(_young, _old, aged));
            _block.SetFloat(FadeId, strength);
            foreach (var r in g.renderers) r.SetPropertyBlock(_block);
        }

        void OnDestroy()
        {
            foreach (var g in _live) if (g.root != null) Destroy(g.root);
            foreach (var g in _pool) if (g.root != null) Destroy(g.root);
            if (_runes != null) _runes.Stop();
        }
    }
}
