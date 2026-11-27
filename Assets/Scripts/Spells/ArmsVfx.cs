using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    // ====================================================================== Heavenly Slash's cut

    /// <summary>
    /// Heavenly Slash's cut in flight: a straight blade of air, seen only as a ripple bending the scene behind it
    /// (SME/Distort, as Severance's crescent), growing longer the further it gets (exactly as long as what it cuts), and
    /// shrinking away at 15 m or wherever it's stopped.
    /// </summary>
    public class HeavenView : MonoBehaviour
    {
        static readonly int VisibleId = Shader.PropertyToID("_Visible");
        static Mesh _mesh;
        static Material _mat;

        WorldNet.HeavenFx _fx;
        Renderer _r;
        MaterialPropertyBlock _mpb;

        public static HeavenView Create(WorldNet.HeavenFx fx)
        {
            var go = new GameObject("HeavenlySlash") { layer = Layers.Fx };
            go.AddComponent<MeshFilter>().sharedMesh = Blade;
            var r = go.AddComponent<MeshRenderer>();
            _mat ??= new Material(Shader.Find("SME/Distort") ?? Shader.Find("Universal Render Pipeline/Unlit")) { name = "Distort" };
            r.sharedMaterial = _mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            var v = go.AddComponent<HeavenView>();
            v._fx = fx;
            v._r = r;
            v._mpb = new MaterialPropertyBlock();
            go.transform.rotation = SpellPlans.HeavenRotation(fx.dir, fx.roll);
            go.transform.localScale = Vector3.zero;
            // the sword's sweep, a white slash across the caster along the cut's line
            SlashFx.Create(fx.origin, go.transform.right, 2.2f);
            CameraRig.ShakeFrom(fx.origin, 0.8f, 0.3f);
            return v;
        }

        /// <summary>A straight lens flat in its own XZ plane: x from -1 to 1 along the cut, thickest in the middle.</summary>
        static Mesh Blade
        {
            get
            {
                if (_mesh != null) return _mesh;
                const int n = 24;
                const float thick = 0.9f;
                var v = new List<Vector3>();
                var uv = new List<Vector2>();
                var t = new List<int>();
                for (int i = 0; i <= n; i++)
                {
                    float s = -1f + 2f * i / n;
                    float h = thick * (1f - s * s);
                    v.Add(new Vector3(s, 0f, -h)); uv.Add(new Vector2((s + 1f) * 0.5f, 0f));
                    v.Add(new Vector3(s, 0f, 0f)); uv.Add(new Vector2((s + 1f) * 0.5f, 1f));
                    if (i == 0) continue;
                    int k = v.Count - 4;
                    t.Add(k); t.Add(k + 1); t.Add(k + 2);
                    t.Add(k + 1); t.Add(k + 3); t.Add(k + 2);
                }
                _mesh = new Mesh { name = "HeavenBlade" };
                _mesh.SetVertices(v);
                _mesh.SetUVs(0, uv);
                _mesh.SetTriangles(t, 0);
                _mesh.RecalculateBounds();
                return _mesh;
            }
        }

        void LateUpdate()
        {
            double now = WorldNet.Now;
            float t = (float)(now - _fx.start);
            float life = SpellPlans.HeavenRange / SpellPlans.HeavenSpeed;
            float end = _fx.ended >= 0 ? (float)(_fx.ended - _fx.start) : life;
            if (t > end + 0.15f) { Destroy(gameObject); return; }
            if (t < 0f) { transform.localScale = Vector3.zero; return; }
            float along = SpellPlans.HeavenSpeed * Mathf.Min(t, end);
            transform.position = _fx.origin + _fx.dir * along;
            float grow = Mathf.Clamp01(t / 0.04f);
            float shrink = 1f - Mathf.Clamp01((t - end) / 0.15f);
            float s = grow * shrink;
            transform.localScale = new Vector3(SpellPlans.HeavenHalf(along) * s, 1f, Mathf.Max(0.05f, s));
            _r.GetPropertyBlock(_mpb);
            _mpb.SetFloat(VisibleId, s);
            _r.SetPropertyBlock(_mpb);
        }
    }

    // ====================================================================== Parry

    /// <summary>Parry's looks: the ring of a deflected blow, and the stab landing.</summary>
    public static class ParryVfx
    {
        /// <summary>A blow turned aside at <paramref name="point"/>: a hot white flash, inked spikes flung off, a jolt.</summary>
        public static void Deflect(Vector3 point, Vector3 from)
        {
            if (Debris.Suppressed) return;
            Vector3 back = from.sqrMagnitude > 1e-4f ? from.normalized : Vector3.up;
            EnergyBurst.Create(point, new EnergyBurst.Settings
            {
                flashSize = 0.9f, flashTime = 0.12f, sparkCount = 14, sparkSize = 0.2f, sparkSpeed = new Vector2(5f, 11f), sparkLife = 0.25f, colour = BladeVfx.White,
            });
            SlashFx.Create(point, Vector3.Cross(back, Vector3.up).sqrMagnitude > 1e-4f ? Vector3.Cross(back, Vector3.up) : Vector3.right, 1.1f);
            CameraRig.ShakeFrom(point, 0.5f, 0.2f);
        }

        /// <summary>The stab landing at <paramref name="point"/>, thrust along <paramref name="dir"/>.</summary>
        public static void Strike(Vector3 point, Vector3 dir)
        {
            if (Debris.Suppressed) return;
            EnergyBurst.Create(point, new EnergyBurst.Settings
            {
                flashSize = 1.1f, flashTime = 0.14f, sparkCount = 16, sparkSize = 0.24f, sparkSpeed = new Vector2(5f, 12f), sparkLife = 0.3f, colour = BladeVfx.White,
            });
            SlashFx.Create(point, dir, 1.6f);
            CameraRig.ShakeFrom(point, 0.6f, 0.25f);
        }
    }
}
