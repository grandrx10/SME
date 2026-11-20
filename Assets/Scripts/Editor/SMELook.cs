using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace SuddenMageEncounter.EditorTools
{
    /// <summary>Visual-style tools: glass material, toon conversion and screen outlines.</summary>
    public static class SMELook
    {
        const string MatDir = "Assets/SME/Materials";
        const string ToonDir = "Assets/SME/Materials/Toon";
        const string GlassPath = MatDir + "/Glass.mat";
        const string OutlineMatPath = MatDir + "/Outline.mat";
        const string ToonShader = "SME/Toon";
        const string OutlineShader = "SME/ScreenOutline";
        const string FeatureName = "SME Outline";

        // ------------------------------------------------------------------ glass

        [MenuItem("Sudden Mage Encounter/Create Glass Material")]
        public static void CreateGlass()
        {
            Directory.CreateDirectory(MatDir);
            var m = AssetDatabase.LoadAssetAtPath<Material>(GlassPath);
            if (m == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                m = new Material(shader) { name = "Glass" };
                MakeTransparent(m, new Color(0.65f, 0.85f, 1f, 0.22f));
                AssetDatabase.CreateAsset(m, GlassPath);
                AssetDatabase.SaveAssets();
            }
            Selection.activeObject = m;
            EditorGUIUtility.PingObject(m);
        }

        static void MakeTransparent(Material m, Color c)
        {
            m.SetFloat("_Surface", 1f);      // Transparent
            m.SetFloat("_Blend", 0f);        // Alpha
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Smoothness", 0.95f);
            m.SetFloat("_Metallic", 0f);
            m.SetColor("_BaseColor", c);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            m.SetShaderPassEnabled("DepthOnly", false);
            m.SetShaderPassEnabled("ShadowCaster", false);
        }

        // ------------------------------------------------------------------ toon + outline

        [MenuItem("Sudden Mage Encounter/Apply Toon Look")]
        public static void ApplyToonLook()
        {
            var toon = Shader.Find(ToonShader);
            var outline = Shader.Find(OutlineShader);
            if (toon == null || outline == null)
            {
                EditorUtility.DisplayDialog("Sudden Mage Encounter", "Toon / outline shaders not found (Assets/SME/Shaders). Let Unity finish importing and try again.", "OK");
                return;
            }
            Directory.CreateDirectory(ToonDir);

            int converted = 0, replaced = 0;

            // 1) project materials we own: switch shader in place
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".mat") || path.StartsWith(ToonDir)) continue;
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!ShouldConvert(m)) continue;
                Undo.RecordObject(m, "Apply Toon Look");
                ConvertInPlace(m, toon);
                EditorUtility.SetDirty(m);
                converted++;
            }

            // 2) renderers using materials we can't edit (package / embedded in models): give them toon copies
            var scene = EditorSceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
                replaced += ReplaceReadOnly(root, toon, recordUndo: true);
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var contents = PrefabUtility.LoadPrefabContents(path);
                int n = ReplaceReadOnly(contents, toon, recordUndo: false);
                if (n > 0) PrefabUtility.SaveAsPrefabAsset(contents, path);
                PrefabUtility.UnloadPrefabContents(contents);
                replaced += n;
            }
            if (replaced > 0) EditorSceneManager.MarkSceneDirty(scene);

            // 3) outline pass on every URP renderer
            int renderers = InstallOutline(outline);

            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog("Sudden Mage Encounter",
                $"Toon look applied.\n\n• {converted} materials switched to SME/Toon\n• {replaced} read-only material slots given toon copies (in {ToonDir})\n• Outline pass on {renderers} renderer(s)\n\nTweak the outline on {OutlineMatPath}, and bands/shadow tint on any toon material. Save the scene.",
                "Nice");
        }

        static bool ShouldConvert(Material m)
        {
            if (m == null || m.shader == null) return false;
            string s = m.shader.name;
            if (s == ToonShader) return false;
            if (m.name.StartsWith("Glass", System.StringComparison.OrdinalIgnoreCase)) return false;
            if (m.renderQueue >= 2450) return false; // transparent / cutout-ish: leave alone
            if (m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f) return false;
            if (s.Contains("Particles") || s.StartsWith("Sprites") || s.StartsWith("UI/")) return false;
            bool lit = s.Contains("Lit") && !s.Contains("Unlit");
            return lit || s == "Standard" || s.StartsWith("ProBuilder");
        }

        static void ConvertInPlace(Material m, Shader toon)
        {
            Texture tex = GetTex(m);
            Color col = GetCol(m);
            Vector2 scale = m.HasProperty("_BaseMap") ? m.GetTextureScale("_BaseMap") : m.HasProperty("_MainTex") ? m.GetTextureScale("_MainTex") : Vector2.one;
            Vector2 offset = m.HasProperty("_BaseMap") ? m.GetTextureOffset("_BaseMap") : m.HasProperty("_MainTex") ? m.GetTextureOffset("_MainTex") : Vector2.zero;
            m.shader = toon;
            m.SetTexture("_BaseMap", tex);
            m.SetTextureScale("_BaseMap", scale);
            m.SetTextureOffset("_BaseMap", offset);
            m.SetColor("_BaseColor", col);
            m.renderQueue = -1;
        }

        static Texture GetTex(Material m)
        {
            if (m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null) return m.GetTexture("_BaseMap");
            if (m.HasProperty("_MainTex")) return m.GetTexture("_MainTex");
            return null;
        }

        static Color GetCol(Material m)
        {
            if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor");
            if (m.HasProperty("_Color")) return m.GetColor("_Color");
            return Color.white;
        }

        static readonly Dictionary<Material, Material> Copies = new();

        static int ReplaceReadOnly(GameObject root, Shader toon, bool recordUndo)
        {
            int count = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || !ShouldConvert(m)) continue;
                    string path = AssetDatabase.GetAssetPath(m);
                    bool editable = path.StartsWith("Assets/") && path.EndsWith(".mat");
                    if (editable) continue; // already handled in place
                    mats[i] = ToonCopy(m, toon);
                    changed = true;
                    count++;
                }
                if (changed)
                {
                    if (recordUndo) Undo.RecordObject(r, "Apply Toon Look");
                    r.sharedMaterials = mats;
                    EditorUtility.SetDirty(r);
                }
            }
            return count;
        }

        static Material ToonCopy(Material src, Shader toon)
        {
            if (Copies.TryGetValue(src, out var cached) && cached != null) return cached;
            string safe = string.Join("_", src.name.Split(Path.GetInvalidFileNameChars()));
            string path = $"{ToonDir}/{safe}_Toon.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(src);
                ConvertInPlace(m, toon);
                AssetDatabase.CreateAsset(m, path);
            }
            Copies[src] = m;
            return m;
        }

        static int InstallOutline(Shader outline)
        {
            Directory.CreateDirectory(MatDir);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(OutlineMatPath);
            if (mat == null)
            {
                mat = new Material(outline) { name = "Outline" };
                AssetDatabase.CreateAsset(mat, OutlineMatPath);
            }

            int n = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData", new[] { "Assets" }))
            {
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
                if (data == null) continue;
                bool has = false;
                foreach (var f in data.rendererFeatures)
                    if (f is FullScreenPassRendererFeature fs && fs.passMaterial == mat)
                    {
                        has = true;
                        if (fs.injectionPoint != OutlineInjection) { fs.injectionPoint = OutlineInjection; EditorUtility.SetDirty(data); }
                    }
                if (has) { n++; continue; }

                var feature = ScriptableObject.CreateInstance<FullScreenPassRendererFeature>();
                feature.name = FeatureName;
                feature.injectionPoint = OutlineInjection;
                feature.fetchColorBuffer = true;
                feature.requirements = ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal;
                feature.passMaterial = mat;
                feature.passIndex = 0;
                AssetDatabase.AddObjectToAsset(feature, data);

                var so = new SerializedObject(data);
                var list = so.FindProperty("m_RendererFeatures");
                var map = so.FindProperty("m_RendererFeatureMap");
                list.arraySize++;
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = feature;
                if (map != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId))
                {
                    map.arraySize++;
                    map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                }
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(data);
                n++;
            }
            return n;
        }

        // Outlines are drawn before transparent effects so tracers, sparks and blood render on top of them.
        const FullScreenPassRendererFeature.InjectionPoint OutlineInjection = FullScreenPassRendererFeature.InjectionPoint.BeforeRenderingTransparents;

        /// <summary>Upgrades an already-installed outline pass to the new injection point.</summary>
        [InitializeOnLoadMethod]
        static void FixOutlineOrder()
        {
            EditorApplication.delayCall += () =>
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(OutlineMatPath);
                if (mat == null) return;
                bool changed = false;
                foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData", new[] { "Assets" }))
                {
                    var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
                    if (data == null) continue;
                    foreach (var f in data.rendererFeatures)
                        if (f is FullScreenPassRendererFeature fs && fs.passMaterial == mat && fs.injectionPoint != OutlineInjection)
                        {
                            fs.injectionPoint = OutlineInjection;
                            EditorUtility.SetDirty(fs);
                            EditorUtility.SetDirty(data);
                            changed = true;
                        }
                }
                if (changed) AssetDatabase.SaveAssets();
            };
        }

        [MenuItem("Sudden Mage Encounter/Toggle Outlines")]
        public static void ToggleOutlines()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(OutlineMatPath);
            bool? state = null;
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData", new[] { "Assets" }))
            {
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
                if (data == null) continue;
                foreach (var f in data.rendererFeatures)
                    if (f is FullScreenPassRendererFeature fs && fs.passMaterial == mat)
                    {
                        state ??= !f.isActive;
                        f.SetActive(state.Value);
                        EditorUtility.SetDirty(data);
                    }
            }
            AssetDatabase.SaveAssets();
        }
    }
}
