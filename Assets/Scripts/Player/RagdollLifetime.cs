using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Removes a dead body at <see cref="dieAt"/>: it freezes, pops a little and shrinks to nothing (shrink, don't fade).
    /// Bodies without one stay until their owner clears them (players: the next round).
    /// </summary>
    public class RagdollLifetime : MonoBehaviour
    {
        public const float ShrinkTime = 0.35f;
        public float dieAt;

        Transform _pivot;
        float _shrinkStart;

        void Update()
        {
            if (_pivot == null && Time.time >= dieAt - ShrinkTime) BeginShrink();
            if (_pivot == null) return;
            float t = Mathf.Clamp01((Time.time - _shrinkStart) / ShrinkTime);
            // a quick swell, then down to nothing
            float s = t < 0.2f ? 1f + 0.15f * (t / 0.2f) : 1.15f * (1f - Mathf.Pow((t - 0.2f) / 0.8f, 2f));
            _pivot.localScale = Vector3.one * Mathf.Max(0f, s);
            if (t >= 1f) Destroy(_pivot.gameObject); // takes the body with it
        }

        /// <summary>Freeze the physics and hang the body off a pivot at its middle, so it shrinks in place.</summary>
        void BeginShrink()
        {
            var bodies = GetComponentsInChildren<Rigidbody>();
            Vector3 c = transform.position;
            if (bodies.Length > 0)
            {
                c = Vector3.zero;
                foreach (var rb in bodies) c += rb.position;
                c /= bodies.Length;
            }
            foreach (var rb in bodies)
            {
                rb.isKinematic = true;
                rb.interpolation = RigidbodyInterpolation.None;
            }
            _pivot = new GameObject(name + "_Shrink").transform;
            _pivot.position = c;
            transform.SetParent(_pivot, true);
            _shrinkStart = Time.time;
        }

        void OnDestroy()
        {
            // cleared early (round reset) while shrinking: don't leave the empty pivot behind
            if (_pivot != null) Destroy(_pivot.gameObject);
        }
    }
}
