using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Sizes and timings for Hammerang: the user's hammer at half size, thrown from the left hand, flying straight for a
    /// second, then coming back to its thrower. What's drawn and what the server does both come from these.
    /// </summary>
    public static partial class SpellPlans
    {
        public const float RangCast = 0.3f;                  // the left hand held out, then the throw (knocked down meanwhile: nothing)
        public const float RangScale = 0.5f;                 // the user's hammer at this size
        public const float RangSpeed = 40f;                  // out, straight along the aim, head first...
        public const float RangOut = 1f;                     // ...for this long (40 m)...
        public const float RangBackSpeed = 44f;              // ...then back to the thrower at up to this (through walls)
        public const float RangBackRamp = 0.35f;             // turning round: up to full speed back over this long
        public const float RangDamage = 15f;                 // going out, and again coming back (each body once each way)
        public const float RangKnock = 10f, RangKnockUp = 5f, RangRagdoll = 1f; // thrown the way it was flying, and ragdolled (both ways)
        /// <summary>Its reach, round its middle (the hammer at <see cref="RangScale"/> flies head first: about half its length).</summary>
        public const float RangRadius = 0.5f;
        public const float RangGravity = 22f;                // knocked down (blocked, or it hit someone), it falls like this...
        public const float RangRest = 1f;                    // ...lies there this long after a block, then comes back
        public const float RangHitRest = 0.35f;              // ...or only a moment after hitting someone
        public const float RangCatch = 0.9f;                 // back within this of the thrower's chest: caught
        public const float RangLife = 10f;                   // never caught by then: gone
        public const float RangChest = 0.45f;                // the thrower's chest, above their hips

        /// <summary>Where the hammer leaves the left hand: in front of the chest, a little left (before the body's grown or shrunk).</summary>
        public static Vector3 RangFrom(Combatant c)
        {
            float grow = BodyGrow(c) * SmallScale(c);
            return c.transform.position + Quaternion.Euler(0f, c.ViewYaw, 0f) * (new Vector3(-0.3f, RangChest, 0.55f) * grow);
        }

        /// <summary>Where it's flying back to: the thrower's chest.</summary>
        public static Vector3 RangHome(Combatant c) => c.transform.position + Vector3.up * (RangChest * BodyGrow(c) * SmallScale(c));
    }
}
