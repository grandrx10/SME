using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// A knocked-down (still alive) player: the ragdoll flops freely, but is steered onto where the player's body
    /// really is (replicated), so everyone sees it tumble along the same path.
    /// The steering is applied to EVERY part equally (a shift of the whole body's velocity), not just the pelvis —
    /// dragging only the pelvis made fast motion (being spun in a tornado) stretch the limbs out on their joints.
    /// </summary>
    public class LiveRagdoll : MonoBehaviour
    {
        public Combatant target;
        Rigidbody _pelvis, _head;
        Rigidbody[] _bodies;
        bool _aimed, _frozen;

        Vector3 _heldAt;

        /// <summary>Stasis (or Shatter) Hex: the body hangs there just as it is, until it's let go.</summary>
        public void Freeze(bool on)
        {
            if (on && !_frozen && target != null) _heldAt = target.transform.position;
            if (_frozen == on || _bodies == null) return;
            _frozen = on;
            foreach (var rb in _bodies) if (rb != null) rb.isKinematic = on;
        }

        void Start()
        {
            _pelvis = RagdollFactory.Pelvis(gameObject);
            _bodies = GetComponentsInChildren<Rigidbody>();
            var bones = Rig.MapBones(transform);
            if (bones.TryGetValue("Bone.001", out var head)) _head = head.GetComponentInParent<Rigidbody>();
            if (_head == _pelvis) _head = null;
            foreach (var j in GetComponentsInChildren<CharacterJoint>())
            {
                // keep the joints from pulling apart under hard motion
                j.enableProjection = true;
                j.projectionDistance = 0.02f;
                j.projectionAngle = 10f;
            }
            foreach (var rb in _bodies) rb.solverIterations = Mathf.Max(rb.solverIterations, 16);
        }

        void FixedUpdate()
        {
            if (target == null || _pelvis == null) return;
            Freeze(target.HeldStill);
            if (_frozen)
            {
                // moved as it is (lifted up to shatter)
                Vector3 moved = target.transform.position - _heldAt;
                _heldAt = target.transform.position;
                if (moved.sqrMagnitude > 1e-8f) foreach (var rb in _bodies) if (rb != null) rb.position += moved;
                return;
            }
            Vector3 error = target.transform.position - _pelvis.position;
            if (error.sqrMagnitude > 25f)
            {
                // way off (teleport): move the whole body there
                foreach (var rb in _bodies) rb.position += error;
                error = Vector3.zero;
            }
            Vector3 want = target.ViewVelocity + error * 12f;
            Vector3 change = (want - _pelvis.linearVelocity) * 0.6f;
            foreach (var rb in _bodies) rb.linearVelocity += change;
            HeadFirst();
        }

        /// <summary>
        /// Infectious Bite: the body flies head first. Turned to point along the flight when the lunge starts, then the
        /// head is held out in front of the hips (only the spin is changed, not where the body goes).
        /// </summary>
        void HeadFirst()
        {
            if (_head == null) return;
            Vector3 v = target.ViewVelocity;
            if (!target.LungingView || v.sqrMagnitude < 4f) { _aimed = false; return; }
            Vector3 dir = v.normalized;
            Vector3 axis = _head.position - _pelvis.position;
            float len = axis.magnitude;
            if (len < 1e-3f) return;
            if (!_aimed)
            {
                // swung round at once, about the hips
                _aimed = true;
                Quaternion turn = Quaternion.FromToRotation(axis / len, dir);
                Vector3 pivot = _pelvis.position;
                foreach (var rb in _bodies)
                {
                    rb.position = pivot + turn * (rb.position - pivot);
                    rb.rotation = turn * rb.rotation;
                    rb.angularVelocity = Vector3.zero;
                }
                return;
            }
            // kept pointing that way: the head is steered to stay out in front of the hips
            Vector3 error = _pelvis.position + dir * len - _head.position;
            _head.linearVelocity = Vector3.Lerp(_head.linearVelocity, _pelvis.linearVelocity + error * 18f, 0.5f);
        }
    }
}
