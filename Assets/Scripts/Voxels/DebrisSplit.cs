using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Breaks a clump of rubble when it hits something hard. This is its own component because Unity only
    /// reports contacts for bodies that have a collision callback. Pieces that can't split don't get one,
    /// so they cost nothing per contact.
    /// </summary>
    public class DebrisSplit : MonoBehaviour
    {
        public Debris Owner;

        void OnCollisionEnter(Collision c)
        {
            if (Owner != null) Owner.OnHit(c);
        }
    }
}
