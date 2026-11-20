using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Works out exactly which voxels a spell uses. The client runs these for its aiming indicator; the server
    /// runs them again on its own world to decide what really happens, and sends the resulting cell lists to
    /// everyone (so every peer applies identical edits).
    /// </summary>
    public static class SpellPlans
    {
        // ---------------------------------------------------------------- Summon Wall
        public const int WallLength = 16;   // voxels (4 m) across your view
        public const int WallThick = 3;     // 0.75 m
        public const int WallHeight = 12;   // 3 m above the ground
        public const int WallDepth = 3;     // how far below the surface the wall reaches (and the trenches go)
        public const float WallRise = 0.3f; // seconds to rise

        public class WallPlan
        {
            public bool valid;
            public Vector3 center;         // on the ground
            public float yaw;
            public float groundY;          // world height of the ground surface at the centre
            public readonly List<Vector3Int> lifted = new();   // ground under the wall (moves up with it)
            public readonly List<Vector3Int> trench = new();   // ground pulled from either side
            public readonly List<Vector3Int> cells = new();    // final wall
            public readonly List<ushort> values = new();
        }

        public static WallPlan PlanWall(VoxelWorld w, Vector3 target, float yaw)
        {
            var plan = new WallPlan { yaw = yaw, center = target };
            if (w == null) return plan;
            var c = w.CellOf(target);
            int ys = w.TopSolid(c.x, c.z, c.y - 8, c.y + 4);
            if (ys == int.MinValue) return plan;
            plan.groundY = w.Origin.y + (ys + 1) * VoxelWorld.Size;
            plan.center = new Vector3(target.x, plan.groundY, target.z);

            Quaternion rot = Quaternion.Euler(0, yaw, 0);
            Vector3 right = rot * Vector3.right, fwd = rot * Vector3.forward;
            float halfL = WallLength * VoxelWorld.Size * 0.5f, halfT = WallThick * VoxelWorld.Size * 0.5f;
            float trenchOut = halfT + VoxelWorld.Size * 1.01f;
            int r = Mathf.CeilToInt((halfL + trenchOut) / VoxelWorld.Size) + 1;
            int footprint = 0, grounded = 0;
            for (int x = c.x - r; x <= c.x + r; x++)
            for (int z = c.z - r; z <= c.z + r; z++)
            {
                Vector3 cc = w.CellCenter(new Vector3Int(x, 0, z));
                float dx = cc.x - target.x, dz = cc.z - target.z;
                float u = dx * right.x + dz * right.z, v = dx * fwd.x + dz * fwd.z;
                if (Mathf.Abs(u) > halfL) continue;
                bool inWall = Mathf.Abs(v) <= halfT;
                bool inTrench = !inWall && Mathf.Abs(v) <= trenchOut;
                if (!inWall && !inTrench) continue;
                if (inWall) footprint++;
                int top = w.TopSolid(x, z, ys - 6, ys + 6);
                if (top == int.MinValue) continue;
                if (inTrench)
                {
                    for (int y = top - WallDepth + 1; y <= top; y++)
                    {
                        ushort tv = w.Get(x, y, z);
                        if (tv != 0 && !w.IsIndestructible(tv)) plan.trench.Add(new Vector3Int(x, y, z));
                    }
                    continue;
                }
                grounded++;
                ushort surface = w.Get(x, top, z);
                ushort below = w.Get(x, top - 1, z);
                ushort body = below != 0 ? below : surface;
                for (int y = top - WallDepth + 1; y <= top; y++)
                {
                    ushort lv = w.Get(x, y, z);
                    if (lv != 0 && !w.IsIndestructible(lv)) plan.lifted.Add(new Vector3Int(x, y, z));
                }
                for (int y = top - WallDepth + 1; y <= top + WallHeight; y++)
                {
                    var p = new Vector3Int(x, y, z);
                    if (w.IsIndestructible(w.Get(p))) continue;
                    plan.cells.Add(p);
                    plan.values.Add(y == top + WallHeight ? surface : body);
                }
            }
            plan.valid = footprint > 0 && grounded * 2 >= footprint;
            return plan;
        }

        // ---------------------------------------------------------------- Summon Platform
        public const int PlatformSize = 16;   // 4 m square
        public const int PlatformHeight = 16; // rises 4 m
        public const float PlatformLift = 14f; // the caster is carried up onto it (m/s up, enough to clear 4 m)

        /// <summary>A 4 x 4 x 4 m block of ground rising under <paramref name="feet"/> (same rules as the wall: its colour, a trench round it).</summary>
        public static WallPlan PlanPlatform(VoxelWorld w, Vector3 feet)
        {
            var plan = new WallPlan { yaw = 0f, center = feet };
            if (w == null) return plan;
            var c = w.CellOf(feet + Vector3.down * 0.1f);
            int ys = w.TopSolid(c.x, c.z, c.y - 8, c.y + 2);
            if (ys == int.MinValue) return plan;
            plan.groundY = w.Origin.y + (ys + 1) * VoxelWorld.Size;
            plan.center = new Vector3(feet.x, plan.groundY, feet.z);
            int half = PlatformSize / 2, footprint = 0, grounded = 0;
            for (int x = c.x - half - 1; x < c.x + half + 1; x++)
            for (int z = c.z - half - 1; z < c.z + half + 1; z++)
            {
                bool inside = x >= c.x - half && x < c.x + half && z >= c.z - half && z < c.z + half;
                int top = w.TopSolid(x, z, ys - 6, ys + 6);
                if (inside) footprint++;
                if (top == int.MinValue) continue;
                if (!inside)
                {
                    // a one-voxel trench round the edge: the ground it was pulled from
                    for (int y = top - WallDepth + 1; y <= top; y++)
                    {
                        ushort tv = w.Get(x, y, z);
                        if (tv != 0 && !w.IsIndestructible(tv)) plan.trench.Add(new Vector3Int(x, y, z));
                    }
                    continue;
                }
                grounded++;
                ushort surface = w.Get(x, top, z), below = w.Get(x, top - 1, z);
                ushort body = below != 0 ? below : surface;
                for (int y = top - WallDepth + 1; y <= top; y++)
                {
                    ushort lv = w.Get(x, y, z);
                    if (lv != 0 && !w.IsIndestructible(lv)) plan.lifted.Add(new Vector3Int(x, y, z));
                }
                // level top: every column rises to the same height above the centre's ground
                for (int y = top - WallDepth + 1; y <= ys + PlatformHeight; y++)
                {
                    var p = new Vector3Int(x, y, z);
                    if (w.IsIndestructible(w.Get(p))) continue;
                    plan.cells.Add(p);
                    plan.values.Add(y == ys + PlatformHeight ? surface : body);
                }
            }
            plan.valid = footprint > 0 && grounded * 2 >= footprint;
            return plan;
        }

        // ---------------------------------------------------------------- Rock Barrage
        public const int RockRadius4 = 10;       // radius in quarter voxels (2.5 voxels)
        public const float RockGather = 0.3f, RockHold = 0.3f;
        public const float RockSpeed = 30f, RockGravity = 14f, RockRadius = 0.32f;
        /// <summary>Throws are lifted just enough to land on what you aim at this far away (metres).</summary>
        public const float RockZeroRange = 10f;
        public const float RockDamage = 15f;
        /// <summary>Multiplier on the rock's base knockback (9 m/s along its flight + 4 m/s up).</summary>
        public const float RockKnockback = 1.3f;
        /// <summary>Aiming at ground (or a wall) this close rips the rock out of that spot.</summary>
        public const float RockPickRange = 4f;

        /// <summary>
        /// Where the rock is ripped out of: the aimed-at spot if it's within <see cref="RockPickRange"/> of the caster
        /// and solid, otherwise the ground just in front of them. <paramref name="aim"/> is a point just inside the
        /// surface the crosshair is on. Returns false if there's no ground to use.
        /// </summary>
        public static bool PlanRock(VoxelWorld w, Vector3 root, Vector3 feet, Vector3 forward, Vector3 aim, out Vector3Int center)
        {
            center = default;
            if (w == null) return false;
            if ((aim - root).magnitude <= RockPickRange + 0.3f)
            {
                var c = w.CellOf(aim);
                if (CountSphere(w, c, RockRadius4) >= 14) { center = c; return true; }
            }
            return PlanRock(w, feet, forward, out center);
        }

        /// <summary>Finds solid ground in front of the caster to rip the rock out of. Returns false if there's none.</summary>
        public static bool PlanRock(VoxelWorld w, Vector3 feet, Vector3 forward, out Vector3Int center)
        {
            center = default;
            if (w == null) return false;
            forward.y = 0;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, forward);
            float[] dists = { 2.5f, 2f, 3f, 1.6f, 3.6f };
            float[] lats = { 0f, 0.8f, -0.8f };
            var fc = w.CellOf(feet);
            foreach (float d in dists)
            foreach (float l in lats)
            {
                var c = w.CellOf(feet + forward * d + side * l);
                int top = w.TopSolid(c.x, c.z, fc.y - 10, fc.y + 3);
                if (top == int.MinValue) continue;
                var cand = new Vector3Int(c.x, top - 1, c.z);
                if (CountSphere(w, cand, RockRadius4) >= 14) { center = cand; return true; }
            }
            return false;
        }

        static int CountSphere(VoxelWorld w, Vector3Int c, int r4)
        {
            int r = (r4 + 3) / 4, lim = r4 * r4, n = 0;
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            for (int dz = -r; dz <= r; dz++)
            {
                if ((dx * dx + dy * dy + dz * dz) * 16 > lim) continue;
                ushort v = w.Get(c.x + dx, c.y + dy, c.z + dz);
                if (v != 0 && !w.IsIndestructible(v)) n++;
            }
            return n;
        }

        /// <summary>
        /// Where a held rock floats: in front of the caster on their left, following their aim — the camera sits
        /// over the right shoulder, so this keeps the centre dot clear.
        /// </summary>
        public static Vector3 RockHoldPoint(Vector3 root, float yaw, float pitch)
        {
            Quaternion look = Quaternion.Euler(Mathf.Clamp(pitch, -40f, 40f), yaw, 0);
            return root + Vector3.up * 0.75f + look * new Vector3(-0.85f, 0.15f, 1.15f);
        }

        // ---------------------------------------------------------------- Folding Hands
        public const int FoldWidth = 12;  // each slab: 3 m from the middle outwards (= how tall it stands when closed)
        public const int FoldLength = 12; // 3 m along your view
        public const int FoldThick = 2;   // 0.5 m
        public const int FoldGap = 4;     // the strip in the middle where the slabs meet (= 2 * thickness)
        public const float FoldBuckle = 0.3f, FoldClose = 0.2f;
        public const float FoldDamage = 25f, FoldRagdoll = 0.5f;

        public class FoldPlan
        {
            public bool valid;
            public bool alongX;            // the hinges run along world X (else Z)
            public Vector3Int center;      // centre cell on the surface
            public int surfaceY, bottomY;  // top and bottom cell rows of the slabs
            public readonly List<Vector3Int>[] slab = { new(), new() };
            public readonly List<ushort>[] slabValues = { new(), new() };
            public readonly List<Vector3Int>[] final = { new(), new() };
            public Vector3[] hinge = new Vector3[2];   // world point on each hinge line
            public Vector3[] outward = new Vector3[2]; // from each hinge towards its slab's far edge
            public Bounds crush;                       // the gap they close on (world)
            public Bounds[] slabBounds = new Bounds[2];
        }

        public static FoldPlan PlanFold(VoxelWorld w, Vector3 target, float yaw)
        {
            var plan = new FoldPlan();
            if (w == null) return plan;
            // snap to the nearest axis: the hinges run along your (snapped) view direction
            Vector3 f = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
            plan.alongX = Mathf.Abs(f.x) >= Mathf.Abs(f.z);
            var c = w.CellOf(target);
            int ys = w.TopSolid(c.x, c.z, c.y - 8, c.y + 4);
            if (ys == int.MinValue) return plan;
            plan.center = new Vector3Int(c.x, ys, c.z);
            plan.surfaceY = ys;
            int yb = ys - FoldThick + 1;
            plan.bottomY = yb;

            // a = along the hinge, b = across (the slabs sit at -b and +b)
            int ca = plan.alongX ? c.x : c.z, cb = plan.alongX ? c.z : c.x;
            int a0 = ca - FoldLength / 2, a1 = a0 + FoldLength;
            int half = FoldGap / 2;
            int hingeA = cb - half;  // boundary: side 0 occupies b < hingeA
            int hingeB = cb + half;  // boundary: side 1 occupies b >= hingeB
            Vector3Int Cell(int a, int y, int b) => plan.alongX ? new Vector3Int(a, y, b) : new Vector3Int(b, y, a);

            int need = FoldWidth * FoldLength * FoldThick / 2;
            for (int side = 0; side < 2; side++)
            {
                for (int a = a0; a < a1; a++)
                for (int i = 0; i < FoldWidth; i++)
                for (int j = 0; j < FoldThick; j++)
                {
                    int b = side == 0 ? hingeA - 1 - i : hingeB + i;
                    int y = yb + j;
                    var p = Cell(a, y, b);
                    ushort v = w.Get(p);
                    if (v == 0 || w.IsIndestructible(v)) continue;
                    plan.slab[side].Add(p);
                    plan.slabValues[side].Add(v);
                    // swung up about the inner bottom edge: i (distance from the hinge) becomes height
                    int fb = side == 0 ? hingeA + j : hingeB - 1 - j;
                    plan.final[side].Add(Cell(a, yb + i, fb));
                }
                if (plan.slab[side].Count < need) return plan;
            }

            float S = VoxelWorld.Size;
            Vector3 O = w.Origin;
            Vector3 axisB = plan.alongX ? Vector3.forward : Vector3.right;
            Vector3 axisA = plan.alongX ? Vector3.right : Vector3.forward;
            float aMid = (a0 + a1) * 0.5f * S, yBottom = O.y + yb * S;
            Vector3 alongOrigin = plan.alongX ? new Vector3(O.x + aMid, 0, 0) : new Vector3(0, 0, O.z + aMid);
            float baseB = plan.alongX ? O.z : O.x;
            Vector3 HingePoint(int bBoundary) =>
                alongOrigin + axisB * (baseB + bBoundary * S) + Vector3.up * yBottom;
            plan.hinge[0] = HingePoint(hingeA);
            plan.hinge[1] = HingePoint(hingeB);
            plan.outward[0] = -axisB;
            plan.outward[1] = axisB;

            float len = FoldLength * S, width = FoldWidth * S, thick = FoldThick * S;
            for (int side = 0; side < 2; side++)
            {
                Vector3 mid = plan.hinge[side] + plan.outward[side] * (width * 0.5f) + Vector3.up * (thick * 0.5f);
                plan.slabBounds[side] = new Bounds(mid, axisA * len + axisB * width + Vector3.up * thick + Vector3.zero);
                plan.slabBounds[side].size = Abs(plan.slabBounds[side].size);
            }
            Vector3 gapMid = (plan.hinge[0] + plan.hinge[1]) * 0.5f + Vector3.up * (thick + width * 0.5f);
            plan.crush = new Bounds(gapMid, Abs(axisA * len + axisB * (FoldGap * S + 0.3f) + Vector3.up * (width + 0.5f)));
            plan.valid = true;
            return plan;
        }

        static Vector3 Abs(Vector3 v) => new(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        // ---------------------------------------------------------------- Shove
        public const float GustSpeed = 26f;
        public static readonly Vector3 GustHalfSize = new(1.5f, 1.25f, 0.75f); // 3 m wide, 2.5 m tall, 1.5 m deep
        public const float GustKnockForward = 14f, GustKnockUp = 5f, GustRagdoll = 1f;
        /// <summary>The caster is blown the opposite way (m/s, no ragdoll): aim down to launch yourself up, sideways to dodge.</summary>
        public const float GustRecoil = 12f;
        public const float PushSpeed = 18f;          // how fast shoved (summoned) ground flies
        public const int PushMaxCells = 900;
        public const float PushDamageMin = 5f, PushDamageMax = 20f;

        public static Vector3 GustCenter(Vector3 origin, Vector3 dir, double start, double now) =>
            origin + dir * (GustSpeed * Mathf.Max(0f, (float)(now - start)));
        public static float GustLife => Cards.MaxRange / GustSpeed;

        /// <summary>Damage from being hit by a flying lump (shoved ground, tornado rubble): 5 for a pebble, 20 for a slab.</summary>
        public static float LumpDamage(int cells) => Mathf.Lerp(PushDamageMin, PushDamageMax, Mathf.Clamp01((cells - 1) / 400f));

        // ---------------------------------------------------------------- Tornado
        public const float TornadoForm = 1f, TornadoSpeed = 10f, TornadoTravel = 30f;
        public const float TornadoRadius = 2.2f, TornadoHeight = 6f;
        public const float TornadoDamage = 20f;
        /// <summary>Tornados form at most this far from the caster (metres).</summary>
        public const float TornadoRange = 3f;
        /// <summary>How often it rips a chunk out of the ground under it, and how big (quarter voxels).</summary>
        public const float TornadoGougeEvery = 0.12f;
        public const int TornadoGouge4 = 8;
        public static float TornadoLife => TornadoForm + TornadoTravel / TornadoSpeed;

        /// <summary>Base of the tornado at a moment: still while it forms, then barrelling forward.</summary>
        public static Vector3 TornadoBase(Vector3 origin, Vector3 dir, double start, double now)
        {
            float t = (float)(now - start) - TornadoForm;
            return origin + dir * Mathf.Clamp(t * TornadoSpeed, 0f, TornadoTravel);
        }

        /// <summary>Where someone caught in the tornado is carried: spinning up its side.</summary>
        public static Vector3 TornadoCarry(Vector3 origin, Vector3 dir, double start, double captured, double now, float phase)
        {
            Vector3 b = TornadoBase(origin, dir, start, now);
            float t = (float)(now - captured);
            float a = phase + t * 5f;
            float h = Mathf.Min(1.2f + t * 1.6f, TornadoHeight - 1.5f);
            float r = 1.3f + 0.25f * Mathf.Sin(t * 3f);
            return b + new Vector3(Mathf.Cos(a) * r, h, Mathf.Sin(a) * r);
        }

        // ---------------------------------------------------------------- Nomadic Dash
        public const float DashSpeed = 35f, DashTime = 0.16f; // ~5.6 m in a blink
        public const float DashKnockForward = 9f, DashKnockUp = 5f;

        // ---------------------------------------------------------------- Mana Missiles
        public const float MissileTargetRange = 45f, MissileTargetAngle = 20f;
        public const float MissileCharge = 0.8f, MissileSpeed = 22f, MissileLife = 3.5f;
        // flight: scatter straight out (each its own way) -> snap round onto the target -> track it, dodgeably
        public const float MissileScatterTime = 0.15f, MissileScatterSpeed = 10f;
        public const float MissileLockTime = 0.12f, MissileLockTurn = 1200f;
        public const float MissileTrackTurn = 150f;
        public const float MissileScatterJitter = 30f, MissileScatterLift = 25f;
        public const float MissileDamage = 5f, MissileRadius = 0.18f;
        public const int MissileCount = 4;
        /// <summary>Each missile goes off in a tiny explosion that blows out voxels (radius in quarter voxels: 1.75 voxels).</summary>
        public const int MissileBlast4 = 7;

        /// <summary>
        /// One step of a missile. Scatter: flies straight along its launch direction, slowly. Lock: whips round
        /// towards the target while speeding up to full. Track: keeps turning towards it, but slowly enough that
        /// someone moving sideways fast makes it miss.
        /// </summary>
        public static void StepMissile(ref Vector3 pos, ref Vector3 dir, float age, bool tracking, Vector3 target, float dt)
        {
            float speed = MissileSpeed;
            if (age < MissileScatterTime) speed = MissileScatterSpeed;
            else
            {
                bool locking = age < MissileScatterTime + MissileLockTime;
                if (locking) speed = Mathf.Lerp(MissileScatterSpeed, MissileSpeed, (age - MissileScatterTime) / MissileLockTime);
                if (tracking)
                {
                    float turn = (locking ? MissileLockTurn : MissileTrackTurn) * Mathf.Deg2Rad * dt;
                    Vector3 want = target - pos;
                    if (want.sqrMagnitude > 1e-4f) dir = Vector3.RotateTowards(dir, want.normalized, turn, 0f);
                }
            }
            pos += dir * (speed * dt);
        }

        // ---------------------------------------------------------------- Magical Barrier
        public const float BarrierWindup = 0.2f, BarrierTime = 0.8f, BarrierRadius = 3f;
        public const float BarrierDamage = 5f;

        // ---------------------------------------------------------------- Burn (the Flame cards' damage over time)
        public const float BurnDamage = 15f, BurnTime = 5f; // each application: 15 over 5 s; they stack

        // ---------------------------------------------------------------- Ball of Flame
        public const float FireballCharge = 0.5f, FireballSpeed = 28f, FireballRadius = 0.35f;
        public const float FireballHitDamage = 10f;
        public const float FireballBlast = 1.5f;          // explosion radius (m): burn, knockback, ragdoll
        public const int FireballCrater4 = 24;            // voxels blown into the air (quarter voxels: 1.5 m)
        public const int FireballSinge4 = 36;             // blackened around it (2.25 m)

        // ---------------------------------------------------------------- Hellfire Wall
        public const float HellfireDraw = 1f;             // the line is drawn for this long
        public const float HellfireLineSpeed = 21f;       // m/s, steering after the cursor
        public const float HellfireTurn = 480f;           // deg/s it can turn
        public const float HellfireRise = 0.3f, HellfireStand = 2f, HellfireFall = 0.25f;
        public const float HellfireHeight = 2.5f, HellfireThick = 0.8f;
        public const float HellfireDamage = 15f;

        // ---------------------------------------------------------------- Explosive Escape
        public const float EscapeDelay = 0.3f, EscapeReach = 1.4f, EscapeBlast = 2.2f;
        public const float EscapeBack = 14f, EscapeUp = 12f;
        public const int EscapeCrater4 = 16, EscapeSinge4 = 28;

        // ---------------------------------------------------------------- Tarot
        public const float MagicianTime = 5f;            // double mana regeneration for this long
        public const float PriestessDelay = 1f;          // the sun shows this long before the flash
        public const float PriestessFlash = 0.15f;       // looking at the caster during this window blinds you
        public const float BlindFade = 2f;               // white screen fading out
        public const float DomainDelay = 0.3f;           // hands come together
        public const float DomainGrow = 0.2f;            // expands to full size this fast
        public const float EmpressRadius = 10f, EmperorRadius = 6.8f;
        public static float DomainRadius(byte kind) => kind == 0 ? EmpressRadius : EmperorRadius;
        public const float EmpressTime = 10f, EmpressDamage = 1.25f;
        public const float EmperorTime = 6f, EmperorDamage = 0.5f;

        // ---------------------------------------------------------------- Arcane Judgment
        public const float JudgmentRadius = 5f;
        public const float JudgmentDraw = 1f, JudgmentGlow = 0.3f, JudgmentLaser = 2f, JudgmentFade = 0.5f;
        public const float JudgmentLayerEvery = 0.25f, JudgmentDps = 50f;
        public static float JudgmentLaserStart => JudgmentDraw + JudgmentGlow;
        public static float JudgmentLaserEnd => JudgmentLaserStart + JudgmentLaser;

        // ---------------------------------------------------------------- Tug
        public const float TugRange = 15f, TugPull = 13f, TugUp = 3f, TugRagdoll = 1.8f;

        // ---------------------------------------------------------------- Mana Siphon
        public const float SiphonTime = 1f, SiphonSpeed = 22f, SiphonArm = 0.3f;
        public const float SiphonReach = 1.3f, SiphonHitRadius = 1.6f;
        public const float SiphonDamage = 10f, SiphonDrain = 2f;
    }
}
