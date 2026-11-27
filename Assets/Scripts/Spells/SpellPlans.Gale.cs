using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Sizes, timings and shapes for Compressed Shot, Ephemerate and Cloud Step (Wind), Submerge and Bubble Bomb (Water).
    /// What's drawn and what the server does both come from these.
    /// </summary>
    public static partial class SpellPlans
    {
        // ---------------------------------------------------------------- Compressed Shot
        public const float ShotChargeTime = 1.5f;            // held this long: full charge
        public const float ShotChargeSpeed = 0.5f;           // charging: this much of your speed...
        public const float ShotChargeGravity = 0.25f, ShotChargeFall = 3f; // ...falling this gently, never faster than this (m/s)
        public const float ShotDamage0 = 15f, ShotDamage1 = 25f;
        public const float ShotHeadshot = 1.5f;              // a hit on the head: this many times the damage
        public const float ShotKnock0 = 8f, ShotKnock1 = 22f, ShotKnockUp0 = 3f, ShotKnockUp1 = 7f, ShotRagdoll0 = 0.6f, ShotRagdoll1 = 1.4f;
        public const int ShotLayers0 = 1, ShotLayers1 = 5;   // voxel layers it breaks through
        public const float ShotRange = Cards.MaxRange;
        public const float ShotRadius = 0.22f;               // drawn and hits at this size
        public const float ShotSpeed = 180f;                 // how fast the bolt is drawn (the hit is where it lands)

        /// <summary>0 (just pressed) .. 1 (fully charged).</summary>
        public static float ShotCharge(float held) => Mathf.Clamp01(held / ShotChargeTime);

        // ---------------------------------------------------------------- Ephemerate
        public const float EphemTime = 0.75f;                // gone this long...
        public const float EphemSpeed = 45f;                 // ...flying where you aim, this fast, through anything (not out of the arena)
        public const float EphemRest = 1f;                   // not again until this long after
        public const float EphemStuck = 0.5f;                // back inside the ground this much (of the body): hurt and thrown out
        public const float EphemDamage = 20f, EphemFling = 13f, EphemRagdoll = 1.2f;
        public const float EphemPuff = 1.1f;                 // the puffs of wind it vanishes and comes back in

        // ---------------------------------------------------------------- Cloud Step
        public const float CloudJump = 3f;                   // a jump this many times as high
        public const int CloudAirSteps = 2;                  // this many in the air, then land first

        // ---------------------------------------------------------------- Submerge
        public const float SubDive = 0.5f;                   // a hop, then head first into the pool (hurt or knocked meanwhile: no dive)
        public const float SubHop = 6f, SubHopForward = 2f;  // the hop
        public const float SubMax = 2f;                      // under as long as it's held, this long at most
        public const float SubSpeed = 1.5f;                  // ...moving this much faster
        public const float SubPlunge = 0.25f, SubSink = 0.4f; // turned head down and into the pool from this long before going under, taking this long
        public const float SubHeal = 5f;                     // health a second while under
        public const float SubRest = 1f;                     // not again until this long after coming out
        public const float SubPool = 1.6f;                   // the pool's radius (drawn), the water ball's in the air
        public const float SubBall = 0.55f;
        public const float SubEmergeReach = 1.8f;            // coming out: anyone this close (sideways)...
        public const float SubEmergeDamage = 20f, SubKnock = 17f, SubKnockUp = 10f, SubRagdoll = 1.6f; // ...hurt and thrown hard
        public const float SubEmergeHop = 9f, SubSpin = 0.55f; // the caster pops up spinning

        // ---------------------------------------------------------------- Bubble Bomb
        public const float MineRadius = 0.38f;               // the bubble (drawn and bumps at this size)
        public const float MineAhead = 1.6f, MineUp = 0.25f; // where it's made, in front of the hips
        public const float MineGlide = 3f;                   // it glides this far forward...
        public const float MineDamp = 1.5f;                  // ...slowing like this (nearly stopped in two seconds)
        public const float MineBumpSpeed = 1.4f, MineBumpDamp = 0.7f; // bumped off ground: floats away like this
        public const float MineArm = 1f;                     // live this long after it's made
        public const float MineLife = 5f;                    // not set off by then: it pops, harmlessly
        public const float MineTrigger = 2f;                 // anyone this close sets it off...
        public const float MineBlast = 2f;                   // ...the blast (drawn exactly this big)
        public const float MineDamage = 15f, MineKnock = 12f, MineKnockUp = 6f, MineRagdoll = 1f;
        public const float MineCrater = 1.2f;                // ground blown out in the middle of it
        public static readonly Color MineRed = new(2.4f, 0.45f, 0.55f, 1f);

        public static Vector3 MineSpot(Vector3 hips, float yaw) => hips + Quaternion.Euler(0f, yaw, 0f) * new Vector3(0f, MineUp, MineAhead);

        /// <summary>Where a bubble drifting from <paramref name="from"/> at <paramref name="vel"/>, slowing by <paramref name="damp"/>, is <paramref name="dt"/> later.</summary>
        public static Vector3 MineDrift(Vector3 from, Vector3 vel, float damp, float dt) =>
            from + vel * ((1f - Mathf.Exp(-damp * Mathf.Max(0f, dt))) / damp);

        /// <summary>The shortest distance from a point to a body's middle line (feet to head).</summary>
        public static float BodyGap(Vector3 p, Combatant c)
        {
            Vector3 feet = c.FeetPosition, ab = Vector3.up * 1.9f * c.transform.localScale.y;
            float u = Mathf.Clamp01(Vector3.Dot(p - feet, ab) / ab.sqrMagnitude);
            return (p - (feet + ab * u)).magnitude;
        }

        // ---------------------------------------------------------------- Speed Demon (passive)
        public const float DemonTopSpeed = 1.8f;             // times as fast at 1% health (none at full; in between, in a straight line)
        public const float DemonTrailBelow = 0.3f;           // at or under this much health, a wind trail streams off behind
        public const float DemonShieldLoss = 10f;            // less shield health (each copy)

        /// <summary>A player's health, 0..1 of its most (1 for anything that isn't a living player).</summary>
        public static float HealthShare(PlayerController pc) =>
            pc == null || !pc.IsSpawned || !pc.IsAlive || pc.MaxHealth <= 0f ? 1f : Mathf.Clamp01(pc.Health.Value / pc.MaxHealth);

        /// <summary>Holding Speed Demon (the Undead lose their cards).</summary>
        public static bool HasSpeedDemon(PlayerController pc) => pc != null && pc.IsSpawned && !pc.IsUndead && Cards.CountOf(pc.Hand, CardId.SpeedDemon) > 0;

        /// <summary>How much faster Speed Demon makes them now: 1 at full health, 2 at 1%.</summary>
        public static float SpeedDemonScale(PlayerController pc)
        {
            if (!HasSpeedDemon(pc)) return 1f;
            float lost = Mathf.Clamp01((1f - HealthShare(pc)) / 0.99f);
            return 1f + (DemonTopSpeed - 1f) * lost;
        }

        /// <summary>The shield's most health for this player (0 = the usual): Speed Demon takes 10 a copy.</summary>
        public static byte ShieldCapFor(PlayerController pc)
        {
            int n = pc != null && !pc.IsUndead ? Cards.CountOf(pc.Hand, CardId.SpeedDemon) : 0;
            return n == 0 ? (byte)0 : (byte)Mathf.Max(1, Mathf.RoundToInt(ShieldHealth - DemonShieldLoss * n));
        }
    }
}
