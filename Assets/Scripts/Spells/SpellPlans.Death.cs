using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Death (reworked): a dash-punch that marks whoever it goes through; struck again while marked, they vomit black bile
    /// and burst. What's drawn and what the server does both come from these.
    /// </summary>
    public static partial class SpellPlans
    {
        // ---------------------------------------------------------------- the dash
        public const float DeathArm = SiphonArm;             // the fist drawn back, then the rush (as Mana Siphon's)
        public const float DeathDashTime = SiphonTime * 2f;  // twice Mana Siphon's dash, at its speed
        public const float DeathKnock = 11f, DeathKnockUp = 7f, DeathRagdoll = 1f; // everyone it goes through

        // ---------------------------------------------------------------- doomed (struck while marked)
        public const float BileTime = 3f;                    // vomiting black bile this long, then they burst
        public const float BileReach = 3.5f, BileAngle = 16f; // the stream out of the mouth, forward and down
        public const float BileDown = 0.65f;                 // how far it tips down from where they look
        public const float BileBlast = 3f;                   // the burst: every breakable voxel this close goes (m)...
        public const float BileSplash = 5f;                  // ...everything this close is coated in bile, and everyone this close is marked
        public const float BileCoatTime = IceCoatTime;       // bile on the ground dries off after this, as frost melts
        public const float BileSlow = 0.5f;                  // standing on it: this much of your speed (not for anyone holding Death)

        /// <summary>A doomed body's skin: nearly black.</summary>
        public static readonly Color BileBody = new(0.05f, 0.045f, 0.055f, 1f);
        /// <summary>The bile itself (drawn bits, the burst).</summary>
        public static readonly Color BileBlack = new(0.07f, 0.06f, 0.075f, 1f);

        /// <summary>Where a doomed body's bile comes from (the mouth) and the way it goes: where they look, tipped down.</summary>
        public static void BileFrom(Combatant c, out Vector3 origin, out Vector3 dir)
        {
            BreathFrom(c, out origin, out dir);
            Vector3 flat = new(dir.x, 0f, dir.z);
            if (flat.sqrMagnitude < 1e-4f) flat = c.transform.forward;
            dir = (flat.normalized + Vector3.down * BileDown).normalized;
        }
    }
}
