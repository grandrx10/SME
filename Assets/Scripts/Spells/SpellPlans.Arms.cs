using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Sizes, timings and shapes for Hammer Spin, Wolfgang, Heavenly Slash and Parry. What's drawn and what the server
    /// does both come from these.
    /// </summary>
    public static partial class SpellPlans
    {
        // ---------------------------------------------------------------- Hammer Spin
        public const float SpinMinHold = 0.5f;               // let go sooner: nothing happens (no cost)
        public const float SpinMinRange = 10f, SpinRangePerSecond = 10f, SpinMaxRange = 40f; // 10 m at 0.5 s, +10 m a second, up to 40 m
        public const float SpinSpeed = 39f;                  // zooming along, m/s on average
        public const float SpinRamp = 0.2f;                  // this share of the time speeding up at the start, and as much slowing down at the end
        public const float SpinTurns = 3.5f;                 // turns a second, the hammer stretched out
        public const float SpinRadius = 1.6f;                // from the middle of the body to the end of the hammer's head: what it hits and smashes
        public const float SpinBandLow = 0.8f, SpinBandHigh = 1.45f; // the hammer's sweep, above the feet (it smashes this band out to SpinRadius)
        public const float SpinBodyHalf = 0.5f;              // ...and the body's own tunnel, this far either side, the whole body high
        public const float SpinLead = 1.2f;                  // voxels are smashed this far ahead of the body
        public const float SpinDamage = 25f;
        public const float SpinKnock = 22f, SpinKnockUp = 9f, SpinRagdoll = 1.6f; // thrown hard: out to the side and on along the spin

        /// <summary>How far the spin goes after charging <paramref name="held"/> seconds.</summary>
        public static float SpinRange(float held) => Mathf.Clamp(SpinMinRange + SpinRangePerSecond * (held - SpinMinHold), SpinMinRange, SpinMaxRange);

        /// <summary>How long the spin takes to go <paramref name="length"/> metres.</summary>
        public static float SpinTime(float length) => Mathf.Max(0.1f, length / SpinSpeed);

        /// <summary>How far along the spin is (0..1) at <paramref name="u"/> of its time: speeding up, steady, slowing down (the same on every screen).</summary>
        public static float SpinEase(float u)
        {
            u = Mathf.Clamp01(u);
            float r = SpinRamp, k = 1f / (1f - r); // (the top speed, as a share of the average)
            if (u < r) return 0.5f * u * u / r * k;
            if (u > 1f - r) return 1f - 0.5f * (1f - u) * (1f - u) / r * k;
            return (u - 0.5f * r) * k;
        }

        /// <summary>
        /// The body's turn (degrees, counter-clockwise from above) <paramref name="t"/> seconds into a spin lasting <paramref name="time"/>:
        /// whole turns, speeding up and slowing down with the spin, so it ends facing the way it started (no snap back).
        /// </summary>
        public static float SpinAngle(float t, float time)
        {
            time = Mathf.Max(0.1f, time);
            int turns = Mathf.Max(1, Mathf.RoundToInt(SpinTurns * time));
            return -360f * turns * SpinEase(t / time);
        }

        /// <summary>
        /// Where a spin from <paramref name="feet"/> ends: <paramref name="range"/> straight ahead, level, through any voxels in
        /// the way (they're smashed), stopping short of anything that can't be broken.
        /// </summary>
        public static Vector3 SpinDestination(VoxelWorld w, Vector3 feet, float yaw, float range)
        {
            Vector3 dir = Level(yaw), best = feet;
            const float step = 0.25f;
            for (float d = step; d <= range + 1e-3f; d += step)
            {
                Vector3 p = feet + dir * d;
                if (FlowBlocked(w, p)) break;
                best = p;
            }
            return best;
        }

        /// <summary>
        /// Every breakable voxel the spin smashes between <paramref name="a"/> and <paramref name="b"/> metres along its level path
        /// <paramref name="from"/> → <paramref name="to"/> (feet): the body's tunnel and the hammer's sweep.
        /// </summary>
        public static void SpinCells(VoxelWorld w, Vector3 from, Vector3 to, float a, float b, List<Vector3Int> cells)
        {
            cells.Clear();
            if (w == null) return;
            Vector3 d = to - from;
            d.y = 0f;
            float len = d.magnitude;
            Vector3 dir = len > 1e-3f ? d / len : Vector3.forward, side = new(dir.z, 0f, -dir.x);
            a = Mathf.Max(-SpinRadius, a);
            b = Mathf.Min(len + SpinRadius, b);
            if (b <= a) return;
            Vector3 p0 = from + dir * a, p1 = from + dir * b;
            Vector3 lo = Vector3.Min(p0, p1) - new Vector3(SpinRadius, 0f, SpinRadius), hi = Vector3.Max(p0, p1) + new Vector3(SpinRadius, 2f, SpinRadius);
            var min = w.CellOf(lo);
            var max = w.CellOf(hi);
            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
            {
                ushort v = w.Get(x, y, z);
                if (v == 0 || w.IsIndestructible(v)) continue;
                Vector3 c = w.CellCenter(new Vector3Int(x, y, z)) - from;
                float along = Vector3.Dot(c, dir), across = Mathf.Abs(Vector3.Dot(c, side)), up = c.y;
                if (along < a || along > b) continue;
                // the round end of the sweep at either end of the path, the straight stretch in between
                float off = along < 0f ? Mathf.Sqrt(along * along + across * across) : along > len ? Mathf.Sqrt((along - len) * (along - len) + across * across) : across;
                bool body = off <= SpinBodyHalf && up >= 0.08f && up <= 1.95f;
                bool sweep = off <= SpinRadius && up >= SpinBandLow && up <= SpinBandHigh;
                if (body || sweep) cells.Add(new Vector3Int(x, y, z));
            }
        }

        // ---------------------------------------------------------------- Wolfgang
        public const float WolfDraw = 0.3f;                  // the two pistols come out (and fade in), then they fire
        public const int WolfShotsEach = 20;                 // from each pistol
        public const float WolfRpm = 500f;                   // each pistol's rate of fire; they take turns
        public const float WolfGap = 60f / WolfRpm * 0.5f;   // between one shot and the next (either pistol)
        public const float WolfFireTime = WolfGap * WolfShotsEach * 2f;
        public const float WolfDamage = 2f;
        public const float WolfBreak = 0.5f;                 // each bullet breaks the ground this far round where it hits (2 voxels)
        public const float WolfSpeed = 80f, WolfRange = Cards.MaxRange;
        public const float WolfSpread = 3f;                  // each bullet goes up to this many degrees off the aim, at random
        public const float WolfRadius = 0.1f;                // drawn and hit at this size
        public const float WolfKnock = 4.5f, WolfKnockUp = 2.5f, WolfRagdoll = 0.45f; // every bullet throws and ragdolls a little
        public const float WolfSlide = 0.11f;                // the slide's travel back as it fires (in the hand)
        public const float WolfHoleLife = 10f;               // bullet holes stay this long, then shrink away
        /// <summary>The bullets: hot yellow-white.</summary>
        public static readonly Color WolfHot = new(4.6f, 3.6f, 1.2f, 1f);

        /// <summary>The time pistol shot <paramref name="k"/> (0 .. 19) goes off, from the cast; even shots are the right pistol's.</summary>
        public static float WolfShotAt(int k) => WolfDraw + k * WolfGap;

        // ---------------------------------------------------------------- Heavenly Slash
        public const float HeavenCrouch = 0.3f;              // crouched, then the leap
        public const float HeavenRiseTime = 0.4f, HeavenRise = 7f; // up into the air this high (less under a ceiling)
        public const float HeavenDrift = 0.4f;               // posing, it drifts on up this much more, slowing (no dead stop)
        public const float HeavenHover = 1f;                 // held up there, posing (the angle shows), then the cut
        public const float HeavenRelease = HeavenCrouch + HeavenRiseTime + HeavenHover;
        public const float HeavenRange = 30f, HeavenSpeed = 60f;
        public const float HeavenHalf0 = 0.5f, HeavenHalf1 = 16.5f; // the cut's half length where it leaves the caster, and 30 m out (it widens twice as fast as it first did)
        public const float HeavenHalfThick = 0.3f;
        public const int HeavenStrikes = 4;
        public const float HeavenDamage = 10f;               // a strike
        public const float HeavenKnock = 10f, HeavenKnockUp = 7f, HeavenRagdoll = 1.3f;

        /// <summary>Lying on its side in the air (degrees rolled about the way it faces): rolled over as it poses, back up after the cut.</summary>
        public static float HeavenBodyRoll(SpellPose pose, float t) => pose switch
        {
            // rolled over through the whole rise (easing in and out), and back upright after the cut, just as gently
            SpellPose.HeavenHover => -90f * Smootherstep(t / (HeavenRiseTime + 0.15f)),
            SpellPose.HeavenSlash => -90f * (1f - Smootherstep((t - BladeMotion.Sweep - BladeMotion.Settle * 0.3f) / 0.4f)),
            _ => 0f,
        };

        static float Smootherstep(float u) { u = Mathf.Clamp01(u); return u * u * u * (u * (u * 6f - 15f) + 10f); }

        /// <summary>The cut's half length <paramref name="along"/> metres out from where it left.</summary>
        public static float HeavenHalf(float along) => Mathf.Lerp(HeavenHalf0, HeavenHalf1, Mathf.Clamp01(along / HeavenRange));

        /// <summary>The cut's frame: travelling along <paramref name="dir"/>, its line rolled <paramref name="roll"/> degrees (x along the line, z the way it goes).</summary>
        public static Quaternion HeavenRotation(Vector3 dir, float roll) => WaveRotation(dir, roll);

        /// <summary>Is a ball (centre, radius) in the part of the cut swept from <paramref name="back"/> to <paramref name="front"/> metres out?</summary>
        public static bool HeavenTouches(Vector3 origin, Quaternion rot, float back, float front, Vector3 p, float radius)
        {
            Vector3 l = Quaternion.Inverse(rot) * (p - origin);
            if (l.z < back - radius - 0.15f || l.z > front + radius + 0.15f) return false;
            if (Mathf.Abs(l.y) > HeavenHalfThick + radius) return false;
            return Mathf.Abs(l.x) <= HeavenHalf(l.z) + radius;
        }

        /// <summary>The voxels the cut's line covers at <paramref name="along"/> metres out (one layer).</summary>
        public static void HeavenLayer(VoxelWorld w, Vector3 origin, Quaternion rot, float along, HashSet<Vector3Int> seen, List<Vector3Int> cells)
        {
            if (w == null) return;
            float half = HeavenHalf(along);
            int n = Mathf.CeilToInt(half * 2f / (VoxelWorld.Size * 0.7f));
            for (int k = 0; k <= n; k++)
            {
                float x = -half + 2f * half * k / Mathf.Max(1, n);
                for (int j = -1; j <= 1; j++)
                {
                    var cell = w.CellOf(origin + rot * new Vector3(x, j * HeavenHalfThick * 0.8f, along));
                    if (!seen.Add(cell)) continue;
                    ushort v = w.Get(cell);
                    if (v == 0 || w.IsIndestructible(v)) continue;
                    cells.Add(cell);
                }
            }
        }

        // ---------------------------------------------------------------- Parry
        public const float ParryWindow = 1.5f;               // on guard this long
        public const float ParryRange = 20f;                 // the dash back at whoever it was, at most
        public const float ParrySpeed = 45f;
        public const float ParryStop = 0.9f;                 // the dash stops this short of them (the stab reaches)
        public const float ParryReach = 3f;                  // still this close at the end of the dash: stabbed
        public const float ParryCarve = 0.55f;               // the dash breaks every voxel this close to the body's line
        public const float ParryDamage = 15f;
        public const float ParryKnock = 14f, ParryKnockUp = 6f, ParryRagdoll = 1.2f;

        /// <summary>How long the dash takes over <paramref name="length"/> metres.</summary>
        public static float ParryTime(float length) => Mathf.Max(0.06f, length / ParrySpeed);

        /// <summary>
        /// Where Parry's dash from <paramref name="hips"/> along <paramref name="dir"/> ends: <paramref name="length"/> on,
        /// through any voxels in the way (they break), stopping short of anything that can't be broken.
        /// </summary>
        public static Vector3 ParryDestination(VoxelWorld w, Vector3 hips, Vector3 dir, float length)
        {
            Vector3 best = hips, down = Vector3.down * 0.95f;
            const float step = 0.25f;
            for (float d = step; d <= length + 1e-3f; d += step)
            {
                Vector3 p = hips + dir * d;
                if (FlowBlocked(w, p + down)) break;
                best = p;
            }
            if (length - (best - hips).magnitude < step) best = hips + dir * length; // (the last bit, short of a step)
            return best;
        }

        /// <summary>Every breakable voxel the dashing body touches, its middle going from <paramref name="a"/> to <paramref name="b"/>.</summary>
        public static List<Vector3Int> ParryCells(VoxelWorld w, Vector3 a, Vector3 b)
        {
            var cells = new List<Vector3Int>();
            if (w == null) return cells;
            // the body: from the feet to the head, all along the line
            Vector3 lo = Vector3.Min(a, b) - Vector3.one * 1.2f, hi = Vector3.Max(a, b) + Vector3.one * 1.2f;
            var min = w.CellOf(lo);
            var max = w.CellOf(hi);
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
            {
                ushort v = w.Get(x, y, z);
                if (v == 0 || w.IsIndestructible(v)) continue;
                Vector3 c = w.CellCenter(new Vector3Int(x, y, z));
                float u = len2 > 1e-4f ? Mathf.Clamp01(Vector3.Dot(c - a, ab) / len2) : 0f;
                Vector3 on = a + ab * u, off = c - on;
                // a capsule standing upright round the line: the body's height, ParryCarve round
                float up = off.y;
                off.y = 0f;
                if (up < -0.9f || up > 0.95f || off.magnitude > ParryCarve) continue;
                cells.Add(new Vector3Int(x, y, z));
            }
            return cells;
        }
    }
}
