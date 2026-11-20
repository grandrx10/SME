using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Small local-only effects (dust puffs). Never affects gameplay.</summary>
    public static class SpellFx
    {
        static Material _mat;

        /// <summary>A quick burst of glittering motes (magic hitting something, things vaporising).</summary>
        public static void Sparkle(Vector3 pos, Color color, float scale)
        {
            if (Debris.Suppressed) return;
            var go = new GameObject("Sparkle") { layer = Layers.Fx };
            go.transform.position = pos;
            var ps = SpellVisuals.Particles(go.transform, "Motes", color, 0f, new Vector2(0.25f, 0.5f), new Vector2(0.06f * scale, 0.16f * scale), true);
            var main = ps.main;
            main.loop = false;
            main.duration = 0.1f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(2f * scale, 6f * scale);
            main.stopAction = ParticleSystemStopAction.Destroy;
            var em = ps.emission;
            em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)(18 * scale), (short)(30 * scale)) });
            var sh = ps.shape;
            sh.shapeType = ParticleSystemShapeType.Sphere;
            sh.radius = 0.15f * scale;
            ps.Play();
            Object.Destroy(go, 1.5f);
        }

        public static void Puff(Vector3 pos, Color color, float scale)
        {
            if (Debris.Suppressed) return;
            var go = new GameObject("Puff") { layer = Layers.Fx };
            go.transform.position = pos;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 0.2f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f * scale, 4f * scale);
            main.startSize = new ParticleSystem.MinMaxCurve(0.25f * scale, 0.6f * scale);
            main.startColor = new Color(color.r, color.g, color.b, 0.55f);
            main.gravityModifier = 0.15f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.stopAction = ParticleSystemStopAction.Destroy;
            var emission = ps.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)(14 * scale), (short)(22 * scale)) });
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.3f * scale;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = grad;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.4f));
            var r = go.GetComponent<ParticleSystemRenderer>();
            if (_mat == null)
            {
                _mat = new Material(Shader.Find("Sprites/Default")) { name = "Puff" };
                _mat.mainTexture = SoftDot();
            }
            r.sharedMaterial = _mat;
            ps.Play();
        }

        static Texture2D SoftDot()
        {
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                px[y * n + x] = new Color32(255, 255, 255, (byte)(a * a * 255));
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }
    }
}
