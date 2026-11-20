using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Removes a dead body's ragdoll after a while (taken off ragdolls that are still in use).</summary>
    public class RagdollLifetime : MonoBehaviour
    {
        public float dieAt;
        void Update() { if (Time.time >= dieAt) Destroy(gameObject); }
    }
}
