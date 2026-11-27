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
            // where each part is on the body, as it was made (from the body as it stood: its own frame is upright)
            _rest0 = transform.rotation;
            _rest = new Vector3[_bodies.Length];
            if (_pelvis != null)
                for (int i = 0; i < _bodies.Length; i++)
                    if (_bodies[i] != null) _rest[i] = Quaternion.Inverse(_rest0) * (_bodies[i].position - _pelvis.position);
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
            if (InJaws()) return;
            _laid = false;
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
            Tumble();
        }

        bool _laid;
        Vector3[] _rest;   // each body's offset from the hips when it was made, in the body's own frame (x: side, y: up, z: front)
        Quaternion _rest0;

        /// <summary>
        /// Inside a fly trap: lying flat in its jaws, head out in front (the way the trap faces), face down. The hips, the
        /// spine, the head and the legs are held in that line; the arms are left to dangle.
        /// </summary>
        bool InJaws()
        {
            if (!target.IsSpawned) return false;
            var st = target.Status.Value;
            double now = WorldNet.Now;
            var net = WorldNet.Instance;
            if (!st.Eaten(now) || net == null || !net.Pots.TryGetValue(st.EatenBy, out var fx)) return false;
            if (_rest == null) return false;
            Vector3 mouth = WorldNet.PotHead(fx, now);
            Vector3 ahead = Quaternion.Euler(0f, WorldNet.PotYaw(fx), 0f) * Vector3.forward;
            // body up -> ahead, body front -> down
            Quaternion lay = Quaternion.LookRotation(Vector3.down, ahead);
            Quaternion turn = lay * Quaternion.Inverse(_rest0);
            Vector3 hips = mouth - ahead * SpellPlans.EatenDrop; // the chest in its jaws
            if (!_laid)
            {
                // swung flat at once, about the hips, and put there
                _laid = true;
                Vector3 pivot = _pelvis.position;
                foreach (var rb in _bodies)
                {
                    if (rb == null) continue;
                    rb.position = hips + turn * (rb.position - pivot);
                    rb.rotation = turn * rb.rotation;
                    rb.linearVelocity = rb.angularVelocity = Vector3.zero;
                }
                return true;
            }
            Vector3 carry = target.ViewVelocity;
            for (int i = 0; i < _bodies.Length; i++)
            {
                var rb = _bodies[i];
                if (rb == null) continue;
                Vector3 r = _rest[i];
                if (rb != _pelvis && Mathf.Abs(r.x) > 0.18f) continue; // an arm: limp
                Vector3 want = hips + lay * r;
                rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, carry + (want - rb.position) * 15f, 0.5f);
                rb.angularVelocity *= 0.8f;
            }
            return true;
        }

        /// <summary>
        /// Wheel of Fortune's throw into the sky: the whole body turns over and over about the hips, on an axis that itself
        /// swings round (the limbs still flop).
        /// </summary>
        void Tumble()
        {
            if (!target.IsSpawned || WorldNet.Now >= target.Status.Value.SpinUntil) return;
            float t = Time.fixedTime;
            Vector3 spin = new Vector3(Mathf.Cos(t * 1.1f), 0.5f * Mathf.Sin(t * 1.7f), Mathf.Sin(t * 1.1f)).normalized * SpellPlans.SkySpin;
            Vector3 hips = _pelvis.position, along = _pelvis.linearVelocity;
            foreach (var rb in _bodies)
            {
                if (rb == null) continue;
                rb.maxAngularVelocity = SpellPlans.SkySpin * 2f;
                rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, spin, 0.4f);
                rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, along + Vector3.Cross(spin, rb.worldCenterOfMass - hips), 0.4f);
            }
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
