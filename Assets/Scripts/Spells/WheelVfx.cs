using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Looks for Wheel of Fortune's results, Judgement's slam and too much Water of Life. Local-only, from the op stream.</summary>
    public static class WheelVfx
    {
        /// <summary>The golden punch: a huge mahogany burst.</summary>
        public static void Punch(Vector3 p)
        {
            EnergyBurst.Create(p, new EnergyBurst.Settings
            {
                flashSize = 3.4f, flashTime = 0.22f, sparkCount = 26, sparkSize = 0.5f, sparkSpeed = new Vector2(8f, 18f), sparkLife = 0.45f, colour = TarotVfx.Mahogany,
            });
            CameraRig.ShakeFrom(p, 0.7f, 0.35f);
        }

        /// <summary>Too much Water of Life: they burst.</summary>
        public static void Bloat(Vector3 p)
        {
            WaterVfx.Splash(p, 2.2f);
            EnergyBurst.Create(p, new EnergyBurst.Settings
            {
                flashSize = 2.2f, flashTime = 0.18f, sparkCount = 22, sparkSize = 0.36f, sparkSpeed = new Vector2(5f, 12f), sparkLife = 0.4f, colour = WaterVfx.Aqua,
            });
            CameraRig.ShakeFrom(p, 0.5f, 0.3f);
        }

        /// <summary>The edge of the ground freezing over.</summary>
        public static void FrostRing(Vector3 center, float radius)
        {
            for (int k = 0; k < 14; k++)
            {
                float a = (k + Random.value) / 14f * Mathf.PI * 2f;
                EnergyBurst.Create(center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (radius * 0.9f) + Vector3.up * 0.4f, new EnergyBurst.Settings
                {
                    flashSize = 0f, flashTime = 0.1f, sparkCount = 3, sparkSize = 0.24f, sparkSpeed = new Vector2(1.5f, 4f), sparkLife = 0.3f, colour = k % 2 == 0 ? ArcanaVfx.Ice : ArcanaVfx.Frost,
                });
            }
            CameraRig.ShakeFrom(center, 0.12f, 0.12f);
        }
    }

    /// <summary>
    /// A great blast: an outlined ball of fire swelling to exactly its radius, then shrinking away, flame tongues bursting
    /// out of its middle and a ring racing along the ground to its edge.
    /// </summary>
    public class WheelBlastView : MonoBehaviour
    {
        const float Away = 0.4f;
        Transform _ball, _heart;
        RuneDrawing _ring;
        Transform _ringT;
        float _age, _spread, _radius;

        public static void Create(Vector3 point, float radius, float spread)
        {
            var go = new GameObject("WheelBlast") { layer = Layers.Fx };
            go.transform.position = point;
            var v = go.AddComponent<WheelBlastView>();
            v._radius = radius;
            v._spread = spread;
            v._ball = Glow.Piece(go.transform, Glow.SphereMesh, FireVfx.Orange, "Ball");
            v._heart = Glow.Piece(go.transform, Glow.SphereMesh, FireVfx.Red, "Heart");
            var ring = new GameObject("Ring") { layer = Layers.Fx };
            ring.transform.SetParent(go.transform, false);
            ring.transform.localPosition = Vector3.up * 0.15f;
            v._ringT = ring.transform;
            v._ring = new RuneDrawing(ring);
            v._ring.AddRune(new List<List<Vector2>> { TarotVfx.Ring(1f, 0f, 96) }, radius * 2f, 0.4f);
            v._ring.Progress = 1f;
            v._ring.SetColour(FireVfx.Orange);
            FlameBurst.Create(point, radius * 0.25f); // (its tongues reach about four times this far: the radius)
            EnergyBurst.Create(point, new EnergyBurst.Settings
            {
                flashSize = 0f, flashTime = 0.1f, sparkCount = 40, sparkSize = 1.1f, sparkSpeed = new Vector2(radius * 1.2f, radius * 3f), sparkLife = 0.6f, colour = FireVfx.Orange,
            });
            CameraRig.ShakeFrom(point, 1.2f, 0.7f);
            v.LateUpdate();
        }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            float grow = 1f - Mathf.Pow(1f - Mathf.Clamp01(_age / _spread), 2f);
            float gone = Mathf.Clamp01((_age - _spread) / Away);
            if (gone >= 1f) { Destroy(gameObject); return; }
            // (ink outline included, it's never bigger than what it hits)
            float r = _radius * grow * (1f - gone) * (1f - gone);
            _ball.localScale = Glow.SphereScale(Mathf.Max(0.001f, r / ForceView.OutlineGrow));
            _heart.localScale = Glow.SphereScale(Mathf.Max(0.001f, r * 0.55f / ForceView.OutlineGrow));
            _ringT.localScale = Vector3.one * Mathf.Max(0.001f, grow * (1f - 0.02f));
            _ring.Progress = 1f - gone;
        }

        void OnDestroy() => _ring?.Release();
    }
}
