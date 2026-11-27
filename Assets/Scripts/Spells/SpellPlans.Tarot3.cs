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
        public const int StarCount = 4;                      // stars at first...
        public const int StarMax = 12;                       // ...one more each cast after one that hit someone, up to this
        public const float StarEvery = 0.1f;                 // one is sent forward this often...
        public const float StarSpeed = 66f, StarRange = 25f; // ...this fast, this far
        public const float StarRadius = 0.28f;               // a star's points reach this far from its middle (it hits at that size)
        public const float StarDamage = 5f;
        public const float StarLight = 3f;                   // whoever it hits is seen through walls this long

        /// <summary>Star <paramref name="k"/> of <paramref name="count"/>: its place over the caster's head, an arc, bobbing a little (more of them sit closer).</summary>
        public static Vector3 StarHome(Vector3 head, float yaw, int k, int count, float t)
        {
            float step = Mathf.Min(34f, 220f / Mathf.Max(1, count - 1));
            float a = (k - (count - 1) * 0.5f) * step * Mathf.Deg2Rad;
            Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            return head + right * (Mathf.Sin(a) * 0.85f) + Vector3.up * (0.2f + Mathf.Cos(a) * 0.6f + 0.05f * Mathf.Sin(t * 3f + k * 1.3f));
        }

        // ---------------------------------------------------------------- The Moon
        public const float MoonClasp = 0.3f;                 // hands clasped...
        public const float MoonTime = 15f;                  // ...then it's over the caster this long, filling as they hurt enemies...
        public const float MoonNeeds = 30f;                 // ...full after this much damage: healed. Not full in time: it goes
        public const float MoonHeal = 15f, MoonHealTime = 0.3f;
        public const float MoonSize = 0.42f;                 // the moon's radius
        /// <summary>The pulse: light green (HDR).</summary>
        public static readonly Color MoonGreen = new(0.95f, 2.6f, 1.15f, 1f);


        // ---------------------------------------------------------------- The Sun
        public const float SunRaise = 0.3f;                  // hands up (busy casting this long)...
        public const float SunDelay = 1.5f;                  // ...this long after the cast the colours turn inside out...
        public const float SunTime = 3f;                     // ...for this long (ragdolled at any point: it's over)
        public const float SunDps = 12f;                     // a second, to anyone looking
        public const float SunBite = 0.25f;                  // dealt this often (s)
        public const float SunDummyAngle = 45f;              // a dummy "looks" at whatever is this close to straight ahead of it
        /// <summary>Dark, menacing mahogany.</summary>
        public static readonly Color SunDark = new(0.2f, 0.035f, 0.02f, 1f);
        public static readonly Color SunRim = new(1.5f, 0.28f, 0.1f, 1f);

        // ---------------------------------------------------------------- Judgement (Tarot) / Arcane Judgment
        public const byte ArcaneCircle = 0, TarotCircle = 1;
        public const float TarotJudgmentRadius = 2.53f * 1.25f * 1.25f;
        public const float TarotJudgmentDraw = 0.8f;         // the circle's written in this long, then it all comes down at once
        public const float TarotJudgmentUp = 5f, TarotJudgmentDown = 0.5f; // everything from this far over it to this far under it
        public const float TarotJudgmentDamage = 30f;
        public const float TarotJudgmentSlam = 26f, TarotJudgmentRagdoll = 1.2f; // whoever's in it: thrown straight down
        public const float TarotJudgmentPress = 0.12f, TarotJudgmentLeave = 0.3f; // (looks: coming down, shrinking away)
        /// <summary>Its cells, as every peer works them out: this many columns out from the middle one, this many layers up and down.</summary>
        public static int TarotJudgmentCells => Mathf.RoundToInt(TarotJudgmentRadius / VoxelWorld.Size);
        public static int TarotJudgmentCellsUp => Mathf.RoundToInt(TarotJudgmentUp / VoxelWorld.Size);
        public static int TarotJudgmentCellsDown => Mathf.RoundToInt(TarotJudgmentDown / VoxelWorld.Size);

        public static float JudgmentRadiusOf(byte kind) => kind == TarotCircle ? TarotJudgmentRadius : JudgmentRadius;
        public static float JudgmentDrawOf(byte kind) => kind == TarotCircle ? TarotJudgmentDraw : JudgmentDraw;
        public static float JudgmentStartOf(byte kind) => kind == TarotCircle ? TarotJudgmentDraw : JudgmentDraw + JudgmentGlow;
        public static float JudgmentEndOf(byte kind) => JudgmentStartOf(kind) + (kind == TarotCircle ? TarotJudgmentPress : JudgmentLaser);

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
        public const float WorldLift = 1.5f;                 // its middle is this far above where the caster's feet were
        public const float WorldSearch = 40f, WorldSearchUp = 16f; // something unbreakable in the way: it's raised at the nearest place with room, this far off at most
        public const int WorldShell = 2;                     // the unbreakable shell's thickness in voxels
        public const float WorldSpotShare = 0.6f;            // the two stand this far out from the middle (share of the radius), opposite sides
        public const float WorldPush = 20f, WorldPushUp = 6f, WorldPushRagdoll = 1.2f; // anyone else it swells into
        public const float WorldLife = 12f;                  // both inside this long, then it shatters by itself (a countdown over the screen)
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
        public const float UndyingHealth = 10f;              // maximum health it costs
    }
}
