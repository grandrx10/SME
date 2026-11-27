namespace SuddenMageEncounter
{
    /// <summary>The arcane shield everyone can raise.</summary>
    public static partial class SpellPlans
    {
        public const float ShieldHealth = 30f, ShieldDrain = 2f, ShieldRegen = 2f;
        public const float ShieldRegenDelay = 2f;            // after it was last up, or last hit
        public const float ShieldReady = 5f;                 // once broken: what it needs before it can go up again
        /// <summary>Blocks whatever comes from within this many degrees of the aim (more than what's drawn covers: the user asked for it).</summary>
        public const float ShieldAngle = 80f;
        /// <summary>A spell that only throws or grabs costs the shield this much for each point of mana it cost.</summary>
        public const float ShieldPushCost = 8f;
        /// <summary>Whoever's spell was blocked from this near is thrown back and ragdolled.</summary>
        public const float RebukeRange = 5f, RebukeKnock = 12f, RebukeUp = 6f, RebukeRagdoll = 1f;
        public const float RebukeEvery = 0.5f;               // (the same two: once for all the pellets of one blast)

        // practice dummies: the shield is back this long after it or its dummy was last hurt; a fireball this often
        public const float DefenseDummyBack = 2f, AttackDummyEvery = 5f;

        // looks: a circle in the middle and symbols floating round it, this far past the hand
        public const float ShieldOut = 0.28f, ShieldCore = 0.42f, ShieldRune = 0.16f, ShieldRing = 0.64f;
        public const int ShieldRunes = 6;
    }
}
