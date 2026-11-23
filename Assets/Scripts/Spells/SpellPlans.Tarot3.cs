using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Sizes, timings and shapes for The Star, The Moon, The Sun, Judgement, The World and Undying. What's drawn and what
    /// the server does both come from these.
    /// </summary>
    public static partial class SpellPlans
    {
        // ---------------------------------------------------------------- The Star
        public const float StarRaise = 0.3f;                 // the right hand up (knocked down meanwhile: no stars)
        public const int StarCount = 5;
        public const float StarEvery = 0.8f;                 // one is sent forward this often...
        public const float StarSpeed = 22f, StarRange = 15f; // ...this fast, this far
        public const float StarRadius = 0.28f;               // a star's points reach this far from its middle (it hits at that size)
        public const float StarDamage = 8f;
        public const float StarLight = 3f;                   // whoever it hits is seen through walls this long

        /// <summary>Star <paramref name="k"/>'s place over the caster's head: an arc of five, bobbing a little.</summary>
        public static Vector3 StarHome(Vector3 head, float yaw, int k, float t)
        {
            float a = (k - (StarCount - 1) * 0.5f) * 34f * Mathf.Deg2Rad;
            Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            return head + right * (Mathf.Sin(a) * 0.85f) + Vector3.up * (0.2f + Mathf.Cos(a) * 0.6f + 0.05f * Mathf.Sin(t * 3f + k * 1.3f));
        }

        // ---------------------------------------------------------------- The Moon
        public const float MoonClasp = 0.3f;                 // hands clasped...
        public const float MoonFill = 1.5f;                  // ...then it fills from new to full (ragdolled meanwhile: it goes)
        public const float MoonRadius = 4f, MoonSpread = 0.35f; // the pulse: this far, spreading out over this long
        public const float MoonReachUp = 2.5f;               // whoever stands within this much of its height is touched
        public const float MoonHeal = 15f, MoonHealTime = 0.3f;
        public const float MoonSize = 0.42f;                 // the moon's radius
        /// <summary>The pulse: light green (HDR).</summary>
        public static readonly Color MoonGreen = new(0.95f, 2.6f, 1.15f, 1f);

        public static float MoonRadiusAt(float t) => MoonRadius * Mathf.Clamp01(t / MoonSpread);

        // ---------------------------------------------------------------- The Sun
        public const float SunRaise = 0.3f;                  // hands up (busy casting this long)...
        public const float SunDelay = 1.5f;                  // ...this long after the cast the colours turn inside out...
        public const float SunTime = 3f;                     // ...for this long (ragdolled at any point: it's over)
        public const float SunDps = 15f;                     // a second, to anyone looking
        public const float SunBite = 0.25f;                  // dealt this often (s)
        public const float SunDummyAngle = 45f;              // a dummy "looks" at whatever is this close to straight ahead of it
        /// <summary>Dark, menacing mahogany.</summary>
        public static readonly Color SunDark = new(0.2f, 0.035f, 0.02f, 1f);
        public static readonly Color SunRim = new(1.5f, 0.28f, 0.1f, 1f);

        // ---------------------------------------------------------------- Judgement (Tarot) / Arcane Judgment
        public const byte ArcaneCircle = 0, TarotCircle = 1;
        public const float TarotJudgmentRadius = 3.375f;
        public const float TarotJudgmentDraw = 1f;           // the circle's written over a second, then burns bright for JudgmentGlow
        public const float TarotJudgmentLayerEvery = 0.8f, TarotJudgmentDps = 25f; // (a layer off each column for every 0.8 s the beam's been on it)
        public const float TarotJudgmentSlide = 6f;          // once written it slides this far forward, till the beam ends
        public const float TarotJudgmentCheck = 0.1f;        // which columns it has reached is checked this often

        public static float JudgmentRadiusOf(byte kind) => kind == TarotCircle ? TarotJudgmentRadius : JudgmentRadius;
        public static float JudgmentDrawOf(byte kind) => kind == TarotCircle ? TarotJudgmentDraw : JudgmentDraw;
        public static float JudgmentStartOf(byte kind) => JudgmentDrawOf(kind) + JudgmentGlow;
        public static float JudgmentEndOf(byte kind) => JudgmentStartOf(kind) + JudgmentLaser;
        /// <summary>Where a circle cast at <paramref name="center"/> is, <paramref name="t"/> seconds after the cast (Judgement slides along <paramref name="dir"/>).</summary>
        public static Vector3 JudgmentCenterAt(byte kind, Vector3 center, Vector3 dir, float t)
        {
            if (kind != TarotCircle) return center;
            float draw = JudgmentDrawOf(kind);
            return center + dir * (TarotJudgmentSlide * Mathf.Clamp01((t - draw) / (JudgmentEndOf(kind) - draw)));
        }
        public static float JudgmentLayerOf(byte kind) => kind == TarotCircle ? TarotJudgmentLayerEvery : JudgmentLayerEvery;
        public static float JudgmentDpsOf(byte kind) => kind == TarotCircle ? TarotJudgmentDps : JudgmentDps;

        // ---------------------------------------------------------------- The World
        public const float WorldClasp = 0.5f;                // hands clasped (knocked down meanwhile: no World)
        public const float WorldReach = 8f, WorldPickAngle = 25f; // the target: whoever is nearest the centre dot, this close
        public const float WorldWarp = 0.55f;                // both bodies warp: folded flat, then to nothing, black symbols pouring off...
        public const float WorldBarsFrom = 0.3f;             // ...and from here their screens close, shut at WorldCrush...
        public const float WorldCrush = 1f;               // both are crushed away...
        public const float WorldGrow = 1.5f;                 // ...the sphere swells from nothing to its full size...
        public const float WorldHush = 1.3f;                // ...and a moment later (the two see THE WORLD meanwhile) they're both inside it...
        public const float WorldUnfold = 0.3f;               // ...unfolding
        public const int WorldCells = 34;                    // its radius in voxels: 8.5 m
        public const float WorldLift = 3f;                   // its middle is this far above where the caster's feet were
        public const int WorldShell = 2;                     // the unbreakable shell's thickness in voxels
        public const float WorldSpotShare = 0.6f;            // the two stand this far out from the middle (share of the radius), opposite sides
        public const float WorldPush = 20f, WorldPushUp = 6f, WorldPushRagdoll = 1.2f; // anyone else it swells into
        public const float WorldGrace = 20f;                 // this long with both alive, and they start dying: 5 damage a second...
        public const float WorldTickEvery = 10f;             // ...and 5 more for every this long after that
        public const float WorldTickDamage = 5f;
        public const float WorldCallout = 5f;                // "ONLY ONE MAY REMAIN" stays up this long
        public const float WorldShatter = 0.5f;              // the dome comes down, top first, over this long
        public static float WorldRadius => WorldCells * VoxelWorld.Size;

        /// <summary>From the crush until both stand inside it.</summary>
        public const float WorldAway = WorldCrush + WorldGrow + WorldHush;

        /// <summary>How big the swelling sphere is (m), <paramref name="t"/> seconds after it starts.</summary>
        public static float WorldRadiusAt(float t) => WorldRadius * Mathf.Clamp01(t / WorldGrow);

        /// <summary>
        /// How crushed a body is while The World takes it (x: width, y: height; 1 = as normal): folded flat, then
        /// squeezed to nothing; gone while the sphere swells; unfolding again inside it.
        /// </summary>
        public static Vector2 WorldFold(in BodyStatus st, double now)
        {
            if (st.WorldUntil <= 0 || now < st.WorldFrom || now > st.WorldUntil + WorldUnfold) return Vector2.one;
            float u = Mathf.Clamp01((float)(now - st.WorldFrom) / WorldWarp); // 0 standing .. 1 gone
            if (now >= st.WorldUntil)
            {
                float left = 1f - Mathf.Clamp01((float)(now - st.WorldUntil) / WorldUnfold);
                u = left * left;
            }
            float h = 1f - u * u, w = 1f + 0.3f * Mathf.Clamp01(u / 0.6f);
            if (u > 0.6f) w *= 1f - (u - 0.6f) / 0.4f;
            return new Vector2(Mathf.Max(0.001f, w), Mathf.Max(0.001f, h));
        }

        // ---------------------------------------------------------------- Undying (passive)
        public const float UndyingTime = 1f;                 // dead this long, then up again as one of the Undead
    }
}
