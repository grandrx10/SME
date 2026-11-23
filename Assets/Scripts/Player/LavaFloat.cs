using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// On a dead body: any part that sinks into lava (a <see cref="KillVolume"/> with lava on) is pushed back up and
    /// slowed down, as if the lava were thick, so the body ends up floating on the surface, bobbing gently.
    /// Client-side only, like the ragdoll itself.
    /// </summary>
    public class LavaFloat : MonoBehaviour
    {
        /// <summary>Each part settles this far below the surface (m): about half a limb.</summary>
        const float FloatDepth = 0.1f;
        /// <summary>Buoyancy never pushes harder than this many times gravity (a fast fall still sinks a bit first).</summary>
        const float MaxLift = 3f;
        const float Drag = 4f, AngularDrag = 3f;
        const float BobHeight = 0.04f, BobSpeed = 1.6f;

        Rigidbody[] _bodies;
        float _seed;

        void Start()
        {
            _bodies = GetComponentsInChildren<Rigidbody>();
            _seed = Random.value * 10f;
        }

        void FixedUpdate()
        {
            if (_bodies == null || !KillVolume.AnyLava) return;
            float dt = Time.fixedDeltaTime;
            foreach (var rb in _bodies)
            {
                if (rb == null || rb.isKinematic) continue;
                Vector3 p = rb.worldCenterOfMass;
                if (!KillVolume.LavaSurfaceAt(p, out float top)) continue;
                top += Mathf.Sin(Time.time * BobSpeed + _seed + p.x * 0.7f + p.z * 0.4f) * BobHeight;
                float depth = top - p.y;
                if (depth <= 0f) continue;
                rb.AddForce(-Physics.gravity * Mathf.Min(depth / FloatDepth, MaxLift), ForceMode.Acceleration);
                rb.linearVelocity *= 1f - Mathf.Clamp01(Drag * dt);
                rb.angularVelocity *= 1f - Mathf.Clamp01(AngularDrag * dt);
            }
        }
    }
}
