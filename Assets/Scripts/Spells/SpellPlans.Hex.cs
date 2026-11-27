using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Numbers and shapes for Necromatic Blast and the Hex cards.</summary>
    public static partial class SpellPlans
    {
        // ---------------------------------------------------------------- Necromatic Blast
        // (the ball is NecroScale times a Ball of Flame's: NecroRadius to the rules, FireballCore * NecroScale to the eye)
        public const float NecroCharge = 0.6f;               // the ground vaporises, gathers over the head, and is thrown
        public const float NecroSpeed = 56f;
        public const float NecroDamage = 15f, NecroHeal = 5f, NecroHealTime = 0.4f;
        public const float NecroKnock = 12f, NecroKnockUp = 5f, NecroRagdoll = 1f;
        public const float NecroBlight = 1.25f;              // voxels this close to where it lands turn green (m)
        public const float NecroAbove = 1.75f;               // it gathers this far above the hips
        public const float NecroScale = 1.25f;               // the ball is this much a Ball of Flame's size
        public static float NecroRadius => FireballRadius * NecroScale;
        /// <summary>How big a Ball of Flame's (and this one's) core is drawn, flying.</summary>
        public const float FireballCore = 0.6f;

        // ---------------------------------------------------------------- Hexes
        public const byte ShatterHex = 1, StasisHex = 2, MindHex = 3;

        public const float HexRange = 7.5f, HexAngle = 20f;    // picked like Mana Missiles: nearest the centre dot
        public const float HexCast = 0.8f;                   // the left hand pointing (knocked down meanwhile: nothing)
        public const float HexFlight = 0.45f;                // the ball of symbols always gets there in this long
        public const float HexTime = 10f;                    // it wears off after this
        public const float ShatterNeeds = 30f, StasisNeeds = 40f; // damage taken that sets it off

        public const float ShatterHold = 0.65f;              // frozen in the air this long before it goes off
        public const float ShatterLift = 0.8f, ShatterLiftTime = 0.2f; // pulled up off the ground first (m, s)
        public const float ShatterDamage = 20f;
        public const float ShatterKnock = 26f, ShatterKnockUp = 10f, ShatterRagdoll = 1.8f;
        public const float ShatterRadius = 1.9f;             // ground blown out round where they were (m)

        public const float StasisTime = 4f;
        public const float StasisGrace = 0.25f;              // the rest of the attack that set it off doesn't undo it
        public const float StasisCircle = 0.95f;             // the circles' radius

        public const float MindNear = 1.5f, MindReach = 8f;  // how far away the caster counts as close; past the reach it doesn't fill
        public const float MindFillNear = 2f, MindFillFar = 5f;
        public const float MindTime = 8f, MindBreak = 12f;   // held this long, unless they get this far apart
        public const float MindStep = 0.1f;                  // the bar is filled (and sent) this often

        public static readonly Color HexWhite = new(2.3f, 2.3f, 2.6f, 1f);
        public static readonly Color HexTurquoise = new(0.2f, 2.7f, 2.4f, 1f);
        public static readonly Color HexPurple = new(1.7f, 0.35f, 3.8f, 1f);

        public static byte HexKind(CardId card) => card switch
        {
            CardId.ShatterHex => ShatterHex,
            CardId.StasisHex => StasisHex,
            CardId.MindHex => MindHex,
            CardId.SmallifyHex => SmallifyHex,
            _ => 0,
        };

        /// <summary>How far up Shatter has pulled a body by this long after it took hold (m).</summary>
        public static float ShatterRise(float age)
        {
            float k = 1f - Mathf.Clamp01(age / ShatterLiftTime);
            return ShatterLift * (1f - k * k);
        }

        /// <summary>The colour of a hex's symbols.</summary>
        public static Color HexColour(byte kind) => kind switch { StasisHex => HexTurquoise, MindHex => HexPurple, SmallifyHex => HexYellow, _ => HexWhite };

        /// <summary>What a hex's bar fills with.</summary>
        public static Color HexFill(byte kind) => kind switch
        {
            StasisHex => new Color(0.05f, 0.8f, 0.74f, 1f),
            MindHex => new Color(0.52f, 0.12f, 0.95f, 1f),
            SmallifyHex => new Color(0.95f, 0.82f, 0.12f, 1f),
            _ => new Color(0.3f, 0.3f, 0.34f, 1f),
        };

        /// <summary>Seconds Mind Hex takes to fill with its caster this far away (0: too far, it doesn't).</summary>
        public static float MindFillTime(float distance) =>
            distance > MindReach ? 0f : Mathf.Lerp(MindFillNear, MindFillFar, Mathf.InverseLerp(MindNear, MindReach, distance));

        /// <summary>A caster (hips at <paramref name="from"/>) has a clear line to some part of the target.</summary>
        public static bool HexSight(Vector3 from, Combatant target)
        {
            Vector3 eye = from + Vector3.up * 0.75f, t = target.transform.position;
            foreach (float h in SightHeights)
                if (!Physics.Linecast(eye, t + Vector3.up * h, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) return true;
            return false;
        }

        static readonly float[] SightHeights = { 0.3f, 0.75f, -0.3f };

        // ---------------------------------------------------------------- Mind Hex: moving as the caster moves

        /// <summary>
        /// The way the caster is moving in the world (whichever way either of them faces): the hexed body goes exactly
        /// that way. 0..1 of full speed.
        /// </summary>
        public static Vector3 PuppetWish(Combatant master)
        {
            if (!CanPuppet(master)) return Vector3.zero;
            Vector3 v = master.ViewVelocity / PuppetSpeed;
            v.y = 0f;
            return v.sqrMagnitude < 0.0064f ? Vector3.zero : Vector3.ClampMagnitude(v, 1f);
        }

        public const float PuppetSpeed = 6.5f, PuppetJump = 7.6f; // a player's own run and jump

        /// <summary>The caster is going up off the ground right now (a jump).</summary>
        public static bool PuppetJumping(Combatant master) => CanPuppet(master) && master.ViewVelocity.y > 4f;

        public static bool PuppetCrouched(Combatant master) => CanPuppet(master) && (master.ViewFlags & NetState.Crouch) != 0;

        static bool CanPuppet(Combatant master) => master != null && master.IsAlive && (master.ViewFlags & NetState.Ragdoll) == 0;
    }
}
