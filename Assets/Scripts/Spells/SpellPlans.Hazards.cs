using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The map hazards' numbers (the timings are on the hazards themselves, in the arena).</summary>
    public static partial class SpellPlans
    {
        // ---------------------------------------------------------------- rising water
        /// <summary>Damage for the nth second (from 0) spent in the water (or out of the closing zone), counting every dip this life.</summary>
        public static float WaterDamage(int n) => Mathf.Pow(2f, Mathf.Min(n, 10));

        // ---------------------------------------------------------------- lava
        public const float LavaEvery = 1f;               // a burn and a launch, at most this often
        public const float LavaLaunch = 15.65f;          // straight up (m/s): 25% higher than 14 m/s went
        public const float LavaCarry = 6f;               // keeping at most this much of the way they were going
        public const float LavaRagdoll = 0.78f;          // (about the top of the launch: they come down on their feet, able to act)

        // ---------------------------------------------------------------- meteors
        public const float MeteorRadius = 0.7f;          // twice a Ball of Flame
        public const float MeteorScale = MeteorRadius / FireballRadius;
        public const float MeteorSpeed = 24f;
        public const float MeteorWarn = 2f;              // marked this long before it lands
        public const float MeteorLean = 0.2f;            // how far off straight down they can come
        public const float MeteorDamage = 10f;           // to everyone in the blast (and a burn)
        public const float MeteorBlast = 3f;             // the blast, and the mark
        public const float MeteorCrater = 2f;
        public const float MeteorSinge = 3f;
        public const float MeteorKnock = 16f, MeteorLift = 9f, MeteorRagdoll = 1.4f;

        // ---------------------------------------------------------------- spike walls
        public const float SpikeDamage = 15f;
        public const float SpikeEvery = 0.8f;            // a touch hurts (and throws), at most this often
        public const float SpikeThrow = 16f, SpikeLift = 6f, SpikeRagdoll = 1f;
        public const float SpikeSlide = 4f;              // a little sideways too (off whatever they're pinned against)
        public const float SpikeLength = 1.5f, SpikeBase = 1.2f, SpikePitch = 1.5f, SpikeInk = 0.05f;

        // ---------------------------------------------------------------- trains
        public const float TrainThrow = 20f, TrainLift = 8f; // how the body's thrown
    }
}
