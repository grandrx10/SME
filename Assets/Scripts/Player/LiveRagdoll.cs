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
        Rigidbody _pelvis;
        Rigidbody[] _bodies;

        void Start()
        {
            _pelvis = RagdollFactory.Pelvis(gameObject);
            _bodies = GetComponentsInChildren<Rigidbody>();
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
        }
    }
}
