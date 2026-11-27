using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Works out exactly which voxels a spell uses. The client runs these for its aiming indicator; the server
    /// runs them again on its own world to decide what really happens, and sends the resulting cell lists to
    /// everyone (so every peer applies identical edits).
    /// </summary>
    public static partial class SpellPlans
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
        /// <summary>Players are hit this much further out than the rock itself (a bit of leeway on aim). Walls and ground use the real size.</summary>
        public const float RockHitLeeway = 0.2f;
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
        public const int FoldWidth = 17;  // each slab: 4.25 m from the middle outwards (= how tall it stands when closed)
        public const int FoldLength = 17; // 4.25 m along your view (17 x 17: twice the old 12 x 12)
        public const int FoldThick = 2;   // 0.5 m
        public const int FoldGap = 4;     // the strip in the middle where the slabs meet (= 2 * thickness)
        public const float FoldSpeed = 0.7f;                                        // (of how fast it first was)
        public const float FoldBuckle = 0.15f / FoldSpeed, FoldClose = 0.1f / FoldSpeed;
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

        // ---------------------------------------------------------------- combo break
        // Knocked down 3 times, or lying ragdolled 2 s in all, within 4 s: up at once (even in mid-air), free to act, and
        // not knocked down again (still thrown) for 3 s, wrapped in arcane (ComboBreakView).
        public const float ComboWindow = 4f;
        public const int ComboKnockdowns = 3;
        public const float ComboDownTime = 2f;
        public const float ComboBreakTime = 3f;
        public const float ComboHitMerge = 0.25f;         // knockdowns closer together than this count as one (one spell hitting twice)

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
        public const float TornadoForm = 1f, TornadoSpeed = 17f, TornadoTravel = 30f;
        public const float TornadoRadius = 2.2f, TornadoHeight = 6f;
        public const float TornadoDamage = 20f;
        /// <summary>Tornados form at most this far from the caster (metres).</summary>
        public const float TornadoRange = 3f;
        /// <summary>How often it rips a chunk out of the ground under it, and how big (quarter voxels).</summary>
        public const float TornadoGougeEvery = 0.12f;
        public const int TornadoGouge4 = 8;
        public static float TornadoLife => TornadoForm + TornadoTravel / TornadoSpeed;
        /// <summary>Caught: spun for at most this long, then flung out.</summary>
        public const float TornadoHold = 1f;
        /// <summary>Held back from the carry path this far (a wall in the way): flung out there instead of dragged through.</summary>
        public const float TornadoSlip = 1f;

        /// <summary>Thrown out of a tornado: away from its middle, and up.</summary>
        public static Vector3 TornadoFling(Vector3 body, Vector3 baseAt, Vector3 dir)
        {
            Vector3 o = body - baseAt;
            o.y = 0f;
            return (o.sqrMagnitude > 0.01f ? o.normalized : dir) * 9f + Vector3.up * 5f;
        }

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
        public const float DashSpeed = 22.75f, DashTime = 0.16f; // ~3.6 m in a blink (0.65 of the old 35 m/s)
        public const float DashKnockForward = 9f, DashKnockUp = 5f;

        // ---------------------------------------------------------------- Mana Missiles
        public const float MissileTargetRange = 45f, MissileTargetAngle = 20f;
        public const float MissileCharge = 0.8f, MissileSpeed = 22f, MissileLife = 3.5f;
        // flight: scatter straight out (each its own way) -> snap round onto the target -> track it, dodgeably
        public const float MissileScatterTime = 0.15f, MissileScatterSpeed = 10f;
        public const float MissileLockTime = 0.12f, MissileLockTurn = 1200f;
        public const float MissileTrackTurn = 90f;
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
        public const float BarrierDamage = 10f;

        // ---------------------------------------------------------------- Burn (the Flame cards' damage over time)
        public const float BurnDamage = 15f, BurnTime = 5f; // each application: 15 over 5 s; they stack

        // ---------------------------------------------------------------- Ball of Flame
        public const float FireballCharge = 0.5f, FireballSpeed = 42f, FireballRadius = 0.35f;
        public const float FireballRange = Cards.MaxRange * 1.5f; // 30 m
        public const float FireballHitDamage = 10f;
        public const float FireballBlast = 1.5f;          // explosion radius (m): burn, knockback, ragdoll
        public const int FireballCrater4 = 24;            // voxels blown into the air (quarter voxels: 1.5 m)
        public const int FireballSinge4 = 36;             // blackened around it (2.25 m)

        // ---------------------------------------------------------------- Hellfire Wall
        public const float HellfireDraw = 0.5f;           // the line is drawn for this long
        public const float HellfireLineSpeed = 42f;       // m/s, steering after the cursor
        public const float HellfireTurn = 960f;           // deg/s it can turn
        public const float HellfireRise = 0.15f, HellfireStand = 2f, HellfireFall = 0.25f;
        public const float HellfireHeight = 2.5f, HellfireThick = 0.8f;
        public const float HellfireDamage = 15f;
        public const int HellfireBurns = 1;               // burn stacks it puts on whoever steps in

        // ---------------------------------------------------------------- Explosive Escape
        public const float EscapeDelay = 0.3f, EscapeReach = 1.4f, EscapeBlast = 2.2f;
        public const float EscapeBack = 14f, EscapeUp = 12f;
        public const int EscapeCrater4 = 16, EscapeSinge4 = 28;
        public const float FlameBurstReach = 3.9f;         // how far a flame burst's tongues reach at scale 1 (FireVfx.FlameBurst: 1.7 out + 2.2 long)
        public const float EscapeChargeStep = 1f, EscapeChargeMax = 2f; // held: another blast's size every second, up to triple
        public const float EscapeKnockGrow = 1.25f;       // ...and the kick back on you grows this much every second
        /// <summary>Explosive Escape's blast (and crater) size after holding it <paramref name="held"/> s: 1, growing to 3.</summary>
        public static float EscapeSize(float held) => 1f + Mathf.Clamp(held, 0f, EscapeChargeMax) / EscapeChargeStep;
        /// <summary>How much harder the caster is kicked back after holding it <paramref name="held"/> s.</summary>
        public static float EscapeKnockScale(float held) => Mathf.Pow(EscapeKnockGrow, Mathf.Clamp(held, 0f, EscapeChargeMax) / EscapeChargeStep);
        /// <summary>Where the clap goes off: in front of the caster (body centre <paramref name="pos"/>), fixed when it's let go.</summary>
        public static Vector3 EscapeSpot(Vector3 pos, float yaw) => pos + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * EscapeReach + Vector3.up * 0.1f;
        /// <summary>The crater's centre (the explosion itself), below the clap.</summary>
        public static Vector3 EscapeCraterCentre(Vector3 spot) => spot + Vector3.down * 0.6f;
        /// <summary>The crater's radius (m) after holding it <paramref name="held"/> s.</summary>
        public static float EscapeCraterRadius(float held) => Mathf.RoundToInt(EscapeCrater4 * EscapeSize(held)) / 4f * VoxelWorld.Size;

        // ---------------------------------------------------------------- Tarot
        public const float MagicianTime = 5f;            // double mana regeneration for this long
        public const float PriestessDelay = 1f;          // the sun shows this long before the flash
        public const float PriestessFlash = 0.15f;       // looking at the caster during this window blinds you
        public const float BlindFade = 2f;               // white screen fading out
        public const float DomainDelay = 0.5f;           // startup: rooted, hands clasped, symbols pouring out (hurt / thrown = cancelled)
        public const float DomainGrow = 0.2f;            // expands to full size this fast
        public const float EmpressRadius = 11.25f, EmperorRadius = 6.8f;
        public static float DomainRadius(byte kind) => kind == 0 ? EmpressRadius : EmperorRadius;
        public const float EmpressTime = 10f, EmpressDamage = 1.25f;
        public const float EmpressTrapTime = 5f;         // caught in it: silenced and chained this long (once per domain)
        public const float ChainSlack = 0.4f;            // trapped: pulled back once this far inside the Empress's edge
        public const float ChainSpeed = 14f, ChainPull = 45f; // m/s back in, and how hard the chains accelerate you
        public const float EmperorTime = 8f, EmperorDamage = 0.3f; // damage from outside the circle, scaled
        public const float DomainRest = 3f;              // after either one ends, this long before its caster can open another

        /// <summary>
        /// The Empress's chains on a trapped body outside her domain: no further out (taut), and hauled back in.
        /// False = still inside, nothing to do. <paramref name="yank"/> is a hop back in for a body on its feet.
        /// </summary>
        public static bool ChainPullBack(Vector3 feet, Vector3 center, ref Vector3 v, float dt, out Vector3 yank)
        {
            yank = Vector3.zero;
            Vector3 o = feet - center;
            bool outside = new Vector2(o.x, o.z).magnitude > EmpressRadius - ChainSlack || o.y > 4f || o.y < -1.5f;
            if (!outside) return false;
            Vector3 dir = (center + Vector3.up * 1.2f - feet).normalized;
            float away = -Vector3.Dot(v, dir);
            if (away > 0f) v += dir * away;
            float inward = Vector3.Dot(v, dir);
            if (inward < ChainSpeed) v += dir * Mathf.Min(ChainSpeed - inward, ChainPull * dt);
            yank = dir * ChainSpeed;
            yank.y = Mathf.Max(yank.y, 4f);
            return true;
        }

        // ---------------------------------------------------------------- The Hierophant
        public const float HierophantDelay = 0.3f;       // after the cast, the wall appears
        public const float HierophantTime = 6f;          // and stands this long
        public const float HierophantWidth = 4f, HierophantHeight = 4f;
        public const float HierophantThick = 0.12f;      // the pane (projectiles bounce off either face)
        public const float HierophantRange = 3f;           // stands no further than this from you
        public const float HierophantGrow = 0.15f, HierophantShrink = 0.25f;

        static bool HierophantFits(Vector3 center, Quaternion q)
        {
            var half = new Vector3(HierophantWidth * 0.5f - 0.05f, HierophantHeight * 0.5f - 0.05f, HierophantThick * 0.5f);
            return !Physics.CheckBox(center, half, q, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Where The Hierophant's wall stands: at the spot under the crosshair (at most <see cref="HierophantRange"/> away),
        /// its face turned straight back along your aim, so it tilts with the mouse (look down at the floor and it leans
        /// back; look up and it leans over you). Lifted off (and pulled back out of) whatever it would cut into where it
        /// can be; where it can't, it stands there anyway (it's only a pane of light).
        /// </summary>
        public static void PlanHierophant(Vector3 root, Vector3 aimPoint, bool hit, Vector3 aimDir, out Vector3 center, out float yaw, out float pitch)
        {
            Vector3 chest = root + Vector3.up * 0.4f;
            aimDir = aimDir.sqrMagnitude > 1e-6f ? aimDir.normalized : Vector3.forward;
            Vector3 to = aimPoint - chest;
            if (!hit || to.magnitude > HierophantRange) aimPoint = chest + (to.sqrMagnitude > 1e-4f ? to.normalized : aimDir) * HierophantRange;
            Vector3 n = -aimDir;
            Vector3 flat = new(aimDir.x, 0f, aimDir.z);
            Vector3 upHint = Mathf.Abs(n.y) > 0.98f ? (flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward) : Vector3.up;
            Vector3 e = Quaternion.LookRotation(n, upHint).eulerAngles;
            yaw = e.y;
            pitch = e.x > 180f ? e.x - 360f : e.x;
            Quaternion q = PortalRotation(yaw, pitch);
            Vector3 top = q * Vector3.up;
            Vector3 c = aimPoint + n * (HierophantThick * 0.5f + 0.03f);
            center = c;
            for (int i = 0; i < 12; i++)
            {
                for (float lift = 0f; lift <= HierophantHeight * 0.5f + 0.3f; lift += 0.25f)
                    if (HierophantFits(c + top * lift, q)) { center = c + top * lift; return; }
                c += n * 0.25f;
                if (Vector3.Dot(c - chest, aimDir) < 1.2f) break; // came all the way back to you
            }
        }

        /// <summary>
        /// Did a ball of radius <paramref name="r"/> moving a → b touch either face of the wall? Gives where it touched
        /// and the face's outward normal (on the side it came from). Moving away from the face doesn't count.
        /// </summary>
        public static bool HierophantTouch(Vector3 a, Vector3 b, float r, Vector3 center, Quaternion q, out Vector3 at, out Vector3 normal)
        {
            at = b;
            Vector3 n = q * Vector3.forward;
            float da = Vector3.Dot(a - center, n);
            float side = da >= 0f ? 1f : -1f;
            normal = n * side;
            float reach = r + HierophantThick * 0.5f;
            float ea = da * side - reach, eb = Vector3.Dot(b - center, n) * side - reach;
            if (eb > 0f || ea < -reach * 2f) return false;       // not reaching it, or already through and out the back
            if (Vector3.Dot(b - a, normal) >= 0f && ea <= 0f) return false; // inside its reach but moving away
            float u = ea > 0f ? ea / (ea - eb) : 0f;
            at = Vector3.Lerp(a, b, u);
            Vector3 l = Quaternion.Inverse(q) * (at - center);
            return Mathf.Abs(l.x) <= HierophantWidth * 0.5f + r && Mathf.Abs(l.y) <= HierophantHeight * 0.5f + r;
        }

        /// <summary>A velocity bounced off a face with this outward normal.</summary>
        public static Vector3 Reflect(Vector3 v, Vector3 normal) => v - 2f * Vector3.Dot(v, normal) * normal;

        // ---------------------------------------------------------------- The Lovers
        public const float LoversSpeed = 26f, LoversReach = 10f; // the thrown beam: this fast, and no further than this
        public const float LoversTurn = 900f;                     // deg/s it homes in (heavily)
        public const float LoversTargetRange = 10f, LoversTargetAngle = 25f;
        public const float LoversHeal = 5f;                       // health per second, each end of the tether
        public const float LoversDrain = 1f;                      // mana per second while tethered (no regeneration)
        public const float LoversBreak = 20f;                     // stretched further than this: it snaps
        public const float LoversCatch = 0.6f;                    // the beam's head this close to the target's chest: tethered

        public static Vector3 LoversChest(Combatant c) => c.RagdollFocus != null ? c.RagdollFocus.position : c.transform.position + Vector3.up * 0.3f;

        /// <summary>The thrown beam's head: flies on, turning hard towards the target.</summary>
        public static void StepLovers(ref Vector3 pos, ref Vector3 dir, Vector3 target, float dt)
        {
            Vector3 to = target - pos;
            if (to.sqrMagnitude > 1e-6f) dir = Vector3.RotateTowards(dir, to.normalized, LoversTurn * Mathf.Deg2Rad * dt, 0f);
            pos += dir * (LoversSpeed * dt);
        }

        // ---------------------------------------------------------------- The Chariot
        public const float ChariotStartSpeed = 8f, ChariotAccel = 9f, ChariotMaxSpeed = 24f; // faster and faster
        public const float ChariotMaxTime = 8f;          // nothing stopped it by now (off the edge, round and round): it ends
        public const int ChariotTallCells = 6;           // terrain at least this many voxels high stops it
        public const float ChariotDamage = 40f;          // the crash: to whoever is stuck on the front (or the driver)
        public const float ChariotCrashKnock = 10f;      // crashing with nobody on the front: the driver is thrown back this hard...
        public const float ChariotCrashRagdoll = 1.2f;   // ...and ragdolled this long
        public const float ChariotStun = 0.3f;          // the one stuck on the front can't act this much longer
        public const float ChariotClashDamage = 25f;     // two chariots meeting: each
        public const float ChariotChunkDamage = 10f;     // terrain it knocks away, to anyone the chunks hit
        /// <summary>The charging caster is rolled up into a ball this big (radius, metres): it's drawn this size, collides
        /// with the world at this size, is hit at this size, smashes a tunnel this size and runs people over at this size.</summary>
        public const float ChariotRadius = 0.9f;
        /// <summary>The ball's centre from the hips (its bottom is at the feet).</summary>
        public static readonly Vector3 ChariotBallOffset = new(0f, ChariotRadius - 0.95f, 0f);
        public static Vector3 ChariotBallCenter(Vector3 hips) => hips + ChariotBallOffset;
        public const float ChariotBodyRadius = 0.35f;    // a person, for the ball touching them
        public const float ChariotPinAhead = ChariotRadius + ChariotBodyRadius; // the one stuck on the front: their hips this far ahead of the ball's centre
        public const float ChariotLaneDepth = 0.9f;      // how far ahead it smashes, each time it's stopped
        /// <summary>How far ahead of the feet a wall counts as in the way (tall: the crash): everything the smash would carve (its depth past the ball's front).</summary>
        public const float ChariotTallReach = ChariotLaneDepth + ChariotRadius + 0.15f;
        public const int ChariotChunkCells = 3;          // knocked-away terrain flies as lumps this many voxels across
        public const int ChariotMaxChunks = 5;           // per smash (the rest just breaks)
        public const float ChariotChunkEvery = 0.2f;     // ...and only one smash in this long throws any (the ball smashes many times a second)
        public const float ChariotReportLag = 0.6f;      // seconds of travel the server allows between where the driver says it hit and where it sees them

        public static float ChariotSpeed(float t) => Mathf.Min(ChariotStartSpeed + ChariotAccel * Mathf.Max(0f, t), ChariotMaxSpeed);

        /// <summary>The mouse steers the charge, but only at this many degrees a second (whatever the mouse sensitivity).</summary>
        public const float ChariotTurnRate = 60f;
        public static float YawOf(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        /// <summary>The way a chariot is heading: its driver's facing (which only turns at <see cref="ChariotTurnRate"/> while charging).</summary>
        public static Vector3 ChariotDir(float yaw) => Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;

        /// <summary>
        /// Carrying someone on the front: are they about to be driven into a wall (terrain <see cref="ChariotTallCells"/>
        /// high right in front of them)? That's the crash, as if the ball had hit it.
        /// </summary>
        public static bool ChariotCarryBlocked(VoxelWorld w, Vector3 feet, Vector3 dir) =>
            ChariotObstacle(w, feet + new Vector3(dir.x, 0f, dir.z).normalized * ChariotPinAhead, dir) >= ChariotTallCells;

        /// <summary>Where the one stuck on the front of a chariot is (their hips), from the driver's hips and facing.</summary>
        public static Vector3 PinPoint(Vector3 driverHips, float yaw) =>
            ChariotBallCenter(driverHips) + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * ChariotPinAhead + Vector3.up * 0.15f;

        /// <summary>Is the ball (centre <paramref name="center"/>) touching this body (hips at <paramref name="hips"/>)?</summary>
        public static bool ChariotTouches(Vector3 center, Vector3 hips)
        {
            // the body as a segment from just above the feet to the head
            Vector3 a = hips + Vector3.down * 0.6f, b = hips + Vector3.up * 0.6f;
            Vector3 ab = b - a;
            float u = Mathf.Clamp01(Vector3.Dot(center - a, ab) / ab.sqrMagnitude);
            return (a + ab * u - center).magnitude <= ChariotRadius + ChariotBodyRadius;
        }

        /// <summary>How tall (voxels, from the feet up, unbroken) the terrain right in front of a chariot is.</summary>
        public static int ChariotObstacle(VoxelWorld w, Vector3 feet, Vector3 dir, float reach = 0.9f)
        {
            dir.y = 0f;
            dir.Normalize();
            Vector3 right = new(dir.z, 0f, -dir.x);
            int best = 0;
            for (int s = -1; s <= 1; s++)
                for (float d = 0.35f; d <= reach + 1e-3f; d += VoxelWorld.Size)
                {
                    var c = w.CellOf(feet + dir * d + right * (s * 0.3f) + Vector3.up * 0.05f);
                    int k = 0;
                    while (k < ChariotTallCells + 2 && w.IsSolid(c.x, c.y + k, c.z)) k++;
                    best = Mathf.Max(best, k);
                }
            return best;
        }

        /// <summary>
        /// The breakable voxels the ball would roll through over the next <see cref="ChariotLaneDepth"/> metres: a round
        /// tunnel exactly the ball's size (not the floor it rolls on). <paramref name="hard"/>: there's terrain in it that
        /// can't be broken.
        /// </summary>
        public static List<VoxelWorld.RemovedCell> ChariotLane(VoxelWorld w, Vector3 feet, Vector3 dir, out bool hard)
        {
            hard = false;
            dir.y = 0f;
            dir.Normalize();
            Vector3 center = feet + Vector3.up * ChariotRadius;
            Vector3 far = center + dir * ChariotLaneDepth;
            var list = new List<VoxelWorld.RemovedCell>();
            float R = ChariotRadius + VoxelWorld.Size * 0.5f; // any voxel the ball's surface would cut into
            Vector3Int min = w.CellOf(Vector3.Min(center, far) - Vector3.one * R), max = w.CellOf(Vector3.Max(center, far) + Vector3.one * R);
            for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                    for (int z = min.z; z <= max.z; z++)
                    {
                        var c = new Vector3Int(x, y, z);
                        Vector3 p = w.CellCenter(c);
                        if (p.y < feet.y + 0.06f) continue;                   // the floor under it
                        float along = Vector3.Dot(p - center, dir);
                        if (along < 0f) continue;                             // only ahead of its middle
                        Vector3 axis = center + dir * Mathf.Min(along, ChariotLaneDepth);
                        if ((p - axis).magnitude > R) continue;
                        ushort v = w.Get(c);
                        if (v == 0) continue;
                        if (w.IsIndestructible(v)) { hard = true; continue; }
                        list.Add(new VoxelWorld.RemovedCell { cell = c, value = v });
                    }
            return list;
        }

        // ---------------------------------------------------------------- Arcane Judgment
        public const float JudgmentRadius = 6.6f;          // 1.75x the area of the old 5 m
        public const float JudgmentDraw = 0.6f, JudgmentGlow = 0.3f, JudgmentLaser = 2f, JudgmentFade = 0.5f;
        public const float JudgmentLayerEvery = 0.25f, JudgmentDps = 50f;
        public static float JudgmentLaserStart => JudgmentDraw + JudgmentGlow;
        public static float JudgmentLaserEnd => JudgmentLaserStart + JudgmentLaser;

        // ---------------------------------------------------------------- Tug
        public const float TugRange = 15f, TugPull = 13f, TugUp = 3f, TugRagdoll = 1.8f;

        // ---------------------------------------------------------------- Mana Siphon
        public const float SiphonTime = 0.5f, SiphonSpeed = 22f, SiphonArm = 0.3f;
        public const float SiphonTurn = 260f;              // deg/s the mouse can steer the charge (was 100)
        public const float SiphonReach = 2.9f, SiphonHalfWidth = 2f, SiphonHalfHeight = 1.4f; // hit box in front: a wide, flat slab
        public const float SiphonDamage = 15f, SiphonDrain = 1f;

        // ---------------------------------------------------------------- Water of Life
        public const float OrbReach = 1.6f;              // floats this far in front of the caster (m)
        public const float OrbLift = 0.35f;              // ... this far above their hips
        public const float OrbGrow = 4.5f;               // grows from a droplet to full size, then it's active
        public const float OrbStartRadius = 0.12f, OrbRadius = 0.35f;
        public const float OrbLife = 10f;                // once formed: pops by itself if nobody touches it
        public const float OrbHeal = 15f, OrbHealTime = 1f;
        public const int OrbSafe = 2;                    // drink more than this many in one life, and...
        public const float BloatTime = 0.5f;             // ...the body swells up this long, then bursts into pieces
        public const float BloatSize = 1.7f;             // how fat it gets

        /// <summary>Where the orb floats: in front of the caster at chest height, pulled in short of any wall.</summary>
        public static Vector3 OrbSpot(Vector3 root, float yaw)
        {
            Vector3 from = root + Vector3.up * OrbLift;
            Vector3 dir = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
            float reach = OrbReach;
            if (Physics.SphereCast(from, OrbRadius * 0.8f, dir, out var hit, OrbReach, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                reach = Mathf.Max(0.6f, hit.distance);
            return from + dir * reach;
        }

        /// <summary>The orb's radius <paramref name="t"/> seconds after it was cast.</summary>
        public static float OrbRadiusAt(float t)
        {
            if (t >= OrbGrow) return OrbRadius;
            float k = Mathf.Clamp01(t / OrbGrow);
            return Mathf.Lerp(OrbStartRadius, OrbRadius, 1f - (1f - k) * (1f - k));
        }

        // ---------------------------------------------------------------- Bubble Away
        public const float BubbleWindup = 0.3f;          // hands come together, then the bubble forms
        public const ulong BubbleFromRagdoll = 1;        // (sent as the cast's target id) cast while knocked down: it forms at once
        public const float BubbleTime = 2f, BubbleHp = 40f;
        public const float BubbleRadius = 1.2f;
        public const float BubbleRise = 1.8f;            // m/s up
        public const float BubbleMove = 3f;              // m/s steering across

        public static Vector3 BubbleCenter(Vector3 root) => root + Vector3.up * 0.05f;

        /// <summary>The cells over a rising bubble that get broken to let it through.</summary>
        public static void BubbleBreakBox(VoxelWorld w, Vector3 center, out Vector3Int min, out Vector3Int max)
        {
            min = w.CellOf(center + new Vector3(-0.95f, 0.55f, -0.95f));
            max = w.CellOf(center + new Vector3(0.95f, BubbleRadius + 0.45f, 0.95f));
        }

        /// <summary>Is there anything in the way above a bubble?</summary>
        public static bool BubbleRoofBlocked(Vector3 center)
        {
            const float lo = 0.55f, hi = BubbleRadius + 0.45f;
            return Physics.CheckBox(center + Vector3.up * ((lo + hi) * 0.5f), new Vector3(0.9f, (hi - lo) * 0.5f, 0.9f), Quaternion.identity,
                Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        // ---------------------------------------------------------------- Tsunami
        public const float TsunamiWindup = 0.5f;         // hands back... shoved forward, then the wave appears
        public const int TsunamiLanes = 24;              // columns of water across it
        public const float TsunamiWidth = TsunamiLanes * VoxelWorld.Size; // 6 m
        public const float TsunamiDepth = 3f;            // tail to front (m)
        public const float TsunamiHeight = 3f;           // the crest
        public const float TsunamiLip = 0.75f;           // the crest curls over this far in front
        public const float TsunamiAhead = 0.8f;          // its tail starts this far in front of the caster
        public const float TsunamiSpeed = 11.2f, TsunamiTravel = 16f;
        public const float TsunamiRise = 0.25f, TsunamiCollapse = 0.5f;
        public const float TsunamiDamage = 40f, TsunamiKnock = 14f, TsunamiKnockUp = 7f, TsunamiRagdoll = 1.2f;
        /// <summary>The floor under its path is sampled this often (m).</summary>
        public const float TsunamiStep = 1f;
        /// <summary>It climbs onto floor at most this many cells higher per sample (taller things are in the way: smashed), and follows it down this far.</summary>
        const int TsunamiClimb = 4, TsunamiDrop = 24;

        public static float TsunamiLife => TsunamiTravel / TsunamiSpeed;

        /// <summary>Distance from where it was cast to its front, <paramref name="t"/> seconds after it appeared.</summary>
        public static float TsunamiFront(float t) => TsunamiAhead + TsunamiDepth + TsunamiSpeed * Mathf.Clamp(t, 0f, TsunamiLife);

        /// <summary>How tall the water stands <paramref name="behind"/> metres behind the front: a tall crest, a long low tail.</summary>
        public static float TsunamiProfile(float behind)
        {
            float f = Mathf.Clamp01(behind / TsunamiDepth);
            return TsunamiHeight * (0.2f + 0.8f * Mathf.Pow(1f - f, 1.4f));
        }

        /// <summary>0 → 1 as it rises out of the ground, back to 0 as it collapses at the end.</summary>
        public static float TsunamiGrowth(float t) => Mathf.Clamp01(t / TsunamiRise) * (1f - Mathf.Clamp01((t - TsunamiLife) / TsunamiCollapse));

        /// <summary>Sideways offset of a lane's centre from the middle of the wave.</summary>
        public static float LaneU(int lane) => (lane + 0.5f) * VoxelWorld.Size - TsunamiWidth * 0.5f;
        public static int LaneOf(float u) => Mathf.Clamp(Mathf.FloorToInt((u + TsunamiWidth * 0.5f) / VoxelWorld.Size), 0, TsunamiLanes - 1);

        /// <summary>
        /// The floor the wave rides on, every <see cref="TsunamiStep"/> along its path from the caster's feet: it follows
        /// the ground down into dips and up small steps; anything taller (walls, platforms) is in its way, not floor.
        /// </summary>
        public static float[] PlanTsunamiGround(VoxelWorld w, Vector3 feet, Vector3 dir)
        {
            int n = Mathf.CeilToInt((TsunamiFront(TsunamiLife) + TsunamiLip) / TsunamiStep) + 2;
            var g = new float[n];
            var c0 = w.CellOf(feet + Vector3.up * 0.05f);
            int top = w.TopSolid(c0.x, c0.z, c0.y - 8, c0.y);
            if (top == int.MinValue) top = c0.y - 1;
            for (int i = 0; i < n; i++)
            {
                var c = w.CellOf(feet + dir * (i * TsunamiStep));
                // a solid cell with room to stand above it
                for (int y = top + TsunamiClimb; y >= top - TsunamiDrop; y--)
                    if (w.IsSolid(c.x, y, c.z) && !w.IsSolid(c.x, y + 1, c.z) && !w.IsSolid(c.x, y + 2, c.z)) { top = y; break; }
                g[i] = w.Origin.y + (top + 1) * VoxelWorld.Size;
            }
            return g;
        }

        /// <summary>Height of the floor under the wave <paramref name="along"/> metres from where it was cast (smoothed between samples).</summary>
        public static float TsunamiBase(float[] g, float along)
        {
            if (g == null || g.Length == 0) return 0f;
            if (g.Length == 1) return g[0];
            float k = Mathf.Clamp(along / TsunamiStep, 0f, g.Length - 1);
            int i = Mathf.Min((int)k, g.Length - 2);
            return Mathf.Lerp(g[i], g[i + 1], k - i);
        }

        /// <summary>The floor height the wave won't break below (the higher of the samples either side, so ledges keep their corners).</summary>
        public static float TsunamiFloor(float[] g, float along)
        {
            if (g == null || g.Length == 0) return 0f;
            int i = Mathf.Clamp((int)(along / TsunamiStep), 0, g.Length - 1);
            return Mathf.Max(g[i], g[Mathf.Min(i + 1, g.Length - 1)]);
        }

        /// <summary>Is the point inside the wave's water <paramref name="t"/> seconds after it appeared? (Also says which lane.)</summary>
        public static bool TsunamiInside(Vector3 origin, Vector3 dir, float[] ground, float t, Vector3 p, float pad, out int lane)
        {
            Vector3 d = p - origin;
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            float along = Vector3.Dot(d, dir), u = Vector3.Dot(d, right);
            lane = LaneOf(u);
            float g = TsunamiGrowth(t);
            if (g <= 0f || Mathf.Abs(u) > TsunamiWidth * 0.5f + pad) return false;
            float behind = TsunamiFront(t) - along;
            if (behind < -(TsunamiLip + pad) || behind > TsunamiDepth + pad) return false;
            float y = p.y - TsunamiBase(ground, along);
            // the lip curling over in front of it: only the top part
            if (behind < 0f) return y >= TsunamiHeight * 0.6f * g - pad && y <= TsunamiHeight * g + pad;
            return y >= -pad && y <= TsunamiProfile(behind) * g + pad;
        }

        // ---------------------------------------------------------------- Warp Self
        public const float WarpRange = 16.5f;         // no further than this from where you stand (drops included)
        public const float WarpCharge = 0.4f;            // hands together... pulled apart (you can still move)
        public const float WarpFold = 0.2f;              // folding away into the rift (untouchable)
        public const float WarpUnfold = 0.2f;            // unfolding at the destination (a hit finishes it at once)
        public static float WarpArrive => WarpCharge + WarpFold;
        public static float WarpDone => WarpCharge + WarpFold + WarpUnfold;

        static bool StandFits(Vector3 feet) =>
            !Physics.CheckCapsule(feet + Vector3.up * 0.4f, feet + Vector3.up * 1.55f, 0.33f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);

        /// <summary>
        /// Where a warp puts your feet: always onto a flat surface with room for your body, never into the air. The
        /// spot under the crosshair (or, if that's further than <see cref="WarpRange"/> or in the sky, the point at
        /// the limit along your aim) is dropped straight down onto whatever flat ground is below it; if there's none,
        /// it comes back towards you until there is. False if there's nowhere (<paramref name="feet"/> is still a
        /// sensible place to show the preview).
        /// </summary>
        public static bool WarpTarget(Vector3 root, Vector3 aimPoint, bool hit, Vector3 aimDir, out Vector3 feet)
        {
            int mask = Layers.EnvironmentMask;
            Vector3 chest = root + Vector3.up * 0.4f;
            Vector3 to = aimPoint - chest;
            Vector3 dir = hit && to.sqrMagnitude > 1e-4f ? to.normalized : aimDir.normalized;
            float far = hit ? Mathf.Min(to.magnitude - 0.3f, WarpRange) : WarpRange; // just off whatever it's on
            feet = chest + dir * Mathf.Max(0f, far);
            bool first = true;
            const float probe = 0.1f;
            for (float d = far; d > 0.8f; d -= 0.25f)
            {
                Vector3 p = chest + dir * d;
                // only drop from open air: a cast that starts touching (or inside) something ignores it, and would fall
                // straight through the floor you're aiming at onto whatever is under it
                if (Physics.CheckSphere(p, probe + 0.02f, mask, QueryTriggerInteraction.Ignore)) continue;
                // a small ball rather than a ray, so it can't slip through a seam between voxels
                if (!Physics.SphereCast(p, probe, Vector3.down, out var g, 60f, mask, QueryTriggerInteraction.Ignore)) continue;
                if (first) { feet = g.point; first = false; }
                if (g.normal.y < 0.75f || !StandFits(g.point) || (g.point - root).magnitude > WarpRange) continue;
                feet = g.point;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- Warp Space
        public const float PortalRange = 20f;
        public const float PortalWidth = 4f, PortalHeight = 4f;
        /// <summary>How thick its bedrock slab is, behind the opening.</summary>
        public const float PortalDepth = 0.08f;
        public const float PortalLife = 10f;
        public const float PortalOpen = 0.25f;           // grows out / shrinks away this fast

        /// <summary>A portal's frame: +Z is the way its opening faces, +Y its top edge. Pitch tilts it (looking up or down at it).</summary>
        public static Quaternion PortalRotation(float yaw, float pitch) => Quaternion.Euler(pitch, yaw, 0f);

        /// <summary>Pitch rides in the cast's spare id field (the yaw has its own).</summary>
        public static ulong PackAngle(float a) => unchecked((uint)System.BitConverter.SingleToInt32Bits(a));
        public static float UnpackAngle(ulong v) => System.BitConverter.Int32BitsToSingle(unchecked((int)(uint)v));

        static bool PortalFits(Vector3 center, Quaternion q)
        {
            Vector3 mid = center - q * Vector3.forward * (PortalDepth * 0.5f + 0.01f);
            var half = new Vector3(PortalWidth * 0.5f - 0.05f, PortalHeight * 0.5f - 0.05f, PortalDepth * 0.5f);
            return !Physics.CheckBox(mid, half, q, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Where a portal opens: facing straight back along your aim (look up 45 degrees and it's tilted 45 degrees; look
        /// straight down and it lies flat on the floor), at the spot under the crosshair, at most <see cref="PortalRange"/>
        /// away. Never through a surface: it's lifted (along its own top edge) off whatever it would cut into, and pulled
        /// back towards you until it fits. False if it fits nowhere.
        /// </summary>
        public static bool PlanPortal(Vector3 root, Vector3 aimPoint, bool hit, Vector3 aimDir, out Vector3 center, out float yaw, out float pitch)
        {
            Vector3 chest = root + Vector3.up * 0.4f;
            Vector3 to = aimPoint - chest;
            if (!hit || to.magnitude > PortalRange) aimPoint = chest + (to.sqrMagnitude > 1e-4f ? to.normalized : aimDir) * PortalRange;
            aimDir = aimDir.sqrMagnitude > 1e-6f ? aimDir.normalized : Vector3.forward;
            Vector3 n = -aimDir;
            // its top edge as near to "up" as it can be (lying flat: its top points away from you)
            Vector3 flat = new(aimDir.x, 0f, aimDir.z);
            Vector3 upHint = Mathf.Abs(n.y) > 0.98f ? (flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward) : Vector3.up;
            Quaternion q = Quaternion.LookRotation(n, upHint);
            Vector3 e = q.eulerAngles;
            yaw = e.y;
            pitch = e.x > 180f ? e.x - 360f : e.x;
            q = PortalRotation(yaw, pitch);
            Vector3 top = q * Vector3.up;
            Vector3 c = aimPoint + n * 0.03f;
            for (int i = 0; i < 24; i++)
            {
                for (float lift = 0f; lift <= PortalHeight * 0.5f + 0.3f; lift += 0.25f)
                {
                    Vector3 cc = c + top * lift;
                    if (PortalFits(cc, q)) { center = cc; return true; }
                }
                c += n * 0.25f;
                if (Vector3.Dot(c - chest, aimDir) < 1.2f) break; // came all the way back to you
            }
            center = c;
            return false;
        }

        /// <summary>
        /// Did a point moving from a to b pass in through the front of this portal's opening? The opening is taken
        /// <paramref name="ahead"/> metres in front of the real one (a ball of that radius touches the slab there) and
        /// shrunk by <paramref name="pad"/> at its edges.
        /// </summary>
        public static bool PortalCrossed(Vector3 a, Vector3 b, Vector3 center, Quaternion q, float ahead, float pad)
        {
            Vector3 n = q * Vector3.forward;
            float da = Vector3.Dot(a - center, n) - ahead, db = Vector3.Dot(b - center, n) - ahead;
            if (!(da > 0f && db <= 0f)) return false;
            Vector3 x = Vector3.Lerp(a, b, da / (da - db));
            Vector3 l = Quaternion.Inverse(q) * (x - center);
            return Mathf.Abs(l.x) <= PortalWidth * 0.5f - pad && Mathf.Abs(l.y) <= PortalHeight * 0.5f - pad;
        }

        /// <summary>
        /// The point that something at <paramref name="p"/> going into portal A comes out at, from portal B. Normally one
        /// continuous space (a point just behind A is just in front of B); <paramref name="sameSide"/> keeps it the same
        /// distance in front of B as it was in front of A (for things that cross <see cref="PortalCrossed"/>'s
        /// <c>ahead</c> plane, before reaching the opening).
        /// </summary>
        public static Vector3 ThroughPoint(Vector3 p, Vector3 ca, Quaternion qa, Vector3 cb, Quaternion qb, bool sameSide = false)
        {
            Vector3 l = Quaternion.Inverse(qa) * (p - ca);
            return cb + qb * new Vector3(-l.x, l.y, sameSide ? l.z : -l.z);
        }

        /// <summary>A direction (velocity, a facing) going into portal A, as it comes out of portal B.</summary>
        public static Vector3 ThroughDir(Vector3 v, Quaternion qa, Quaternion qb)
        {
            Vector3 l = Quaternion.Inverse(qa) * v;
            return qb * new Vector3(-l.x, l.y, -l.z);
        }

        // ---------------------------------------------------------------- Arcane Swiftness
        public const float SwiftPrep = 0.1f;             // crouched, gathering (a knock interrupts it)
        public const float SwiftTime = 1.6f;             // then running free, untouchable
        public const float SwiftSpeedScale = 2.3f;         // times running speed (and acceleration)
        public const float SwiftRest = 1f;                 // not cast again until this long after it ends
        public const float SwiftBumpReach = 1.4f;          // run into someone this close: they're bowled over (Nomadic Dash's knock)

        /// <summary>The cells a swift dash smashes in front of you when it runs into something.</summary>
        public static void SwiftBreakBox(VoxelWorld w, Vector3 feet, Vector3 dir, out Vector3Int min, out Vector3Int max)
        {
            Vector3 c = feet + dir * 0.6f;
            min = w.CellOf(c + new Vector3(-0.6f, 0.05f, -0.6f));
            max = w.CellOf(c + new Vector3(0.6f, 1.95f, 0.6f));
        }

        // ================================================================ Attraction / Repulsion
        public const float OrbRaise = 0.3f;               // the hand goes up, then the ball appears
        public const float ForceLife = 4f;                // the ball's life, steered all the while (the caster is casting)
        public const float ForceRadius0 = 0.5f, ForceRadius1 = 1f; // the balls grow from this to this, times ...
        public const float AttractScale = 0.7f;           // ... this for Attraction's (water) ...
        public const float FlameScale = 0.7f;             // ... and this for Repulsion's (flame)
        public const float AttractReach0 = 4f, AttractReach1 = 6f; // how far Attraction pulls from (growing; not tied to the ball's size)
        public const float AttractCore = 1.5f;            // everything within this times its radius is torn right out
        public const int AttractChunks = 3;               // chunks torn off surfaces in reach, per rip
        public const float AttractChunk = 0.75f;          // each one's radius (metres)
        public const float ForceSpeed = 10.5f;            // m/s (Attraction)
        public const float FlameSpeedScale = 3f;          // ... and moves this much faster (31.5 m/s)
        public const float ForceReachScale = 4f;          // it reaches out to four times its radius (the ball itself stays its size)
        public const float ForceDissipate = 0.3f;         // cut short (caster thrown, put out): gone in this long
        public const float ForceMoveEvery = 0.1f;         // how often the server tells everyone where it is
        public const float ForceRipEvery = 0.12f;         // how often it tears / blasts the voxels in reach
        public const float PullEvery = 0.1f;              // how often held bodies are dragged in again
        public const float PullHold = 1.2f;               // ragdolled this long after the ball lets go
        public const float RepelKnock = 21f, RepelUp = 7.5f, RepelRagdoll = 1.4f;
        public const float ForceRange = 25f;              // both balls are gone this far from where they were cast
        public const int RepelBurns = 2;                  // burn stacks Repulsion's blast puts on every enemy in it
        public const float RepelBlastRadius = 3.75f;      // Repulsion goes off on contact (or at the end of its range): this big a blast (drawn exactly this big)
        public const float RepelCrater = 1.5f;            // ... blowing out a crater this big where it touched
        public const float AttractHold = 1f;              // Attraction holds someone this long, then lets go (and never grabs them again)
        public const float AttractProjectileAccel = 90f;  // m/s² enemy projectiles in its reach are pulled in with...
        public const float AttractProjectileDrag = 1.5f;  // ...losing speed (relative to the ball) like this, so they circle it a little and spiral in
        public const float AttractProjectileEvery = 0.05f;// how often the server re-sends a pulled rock / fireball's path
        public const float AttractMissileTurn = 1.4f;     // missiles turn in this many times as hard as a circle round it would need: a short spiral in

        public static float ForceRadius(bool water, float t)
        {
            float r = Mathf.Lerp(ForceRadius0, ForceRadius1, Mathf.Clamp01(t / ForceLife));
            return r * (water ? AttractScale : FlameScale);
        }

        /// <summary>How far a ball's pull (or push) reaches, <paramref name="t"/> seconds after it came out.</summary>
        public static float ForceReach(bool water, float t) => water
            ? Mathf.Lerp(AttractReach0, AttractReach1, Mathf.Clamp01(t / ForceLife))
            : ForceRadius(false, t) * ForceReachScale;

        public const float AttractSpeedScale = 1.25f;      // Attraction moves this much faster than ForceSpeed (Repulsion keeps its own)
        public static float ForceSpeedOf(bool water) => water ? ForceSpeed * AttractSpeedScale : ForceSpeed * FlameSpeedScale;

        /// <summary>
        /// A projectile caught by Attraction, over <paramref name="dt"/>: pulled towards the ball, and slowing down
        /// relative to it, so it swings round the ball a little and spirals in.
        /// </summary>
        public static Vector3 AttractPull(Vector3 v, Vector3 toBall, Vector3 ballVel, float dt)
        {
            float d = toBall.magnitude;
            if (d > 1e-3f) v += toBall / d * (AttractProjectileAccel * dt);
            return ballVel + (v - ballVel) * Mathf.Exp(-AttractProjectileDrag * dt);
        }

        /// <summary>A missile caught by Attraction, over <paramref name="dt"/>: turned towards the ball harder than a circle round it needs.</summary>
        public static Vector3 AttractMissile(Vector3 dir, Vector3 toBall, float dt)
        {
            float d = toBall.magnitude;
            if (d < 1e-3f) return dir;
            float turn = AttractMissileTurn * MissileSpeed / Mathf.Max(d, 0.5f) * dt;
            return Vector3.RotateTowards(dir, toBall / d, turn, 0f);
        }

        /// <summary>
        /// A radius in metres as the voxel ops take it ("r4": quarter-cells, i.e. cells × 4; a cell is 0.25 m, so 1 m = 16).
        /// Always convert through this: passing metres × 4 makes everything 4× too small.
        /// </summary>
        public static int R4(float metres) => Mathf.Clamp(Mathf.RoundToInt(metres / VoxelWorld.Size * 4f), 1, 255);

        // ---------------------------------------------------------------- the fused ball
        public const float FuseWindow = 0.3f;             // both cast this close together: they fuse
        public const float FuseCharge = 2f;               // forming in front of you (you're untouchable)
        public const float FuseRadius0 = 0.25f, FuseRadius1 = 1f; // what you see is what it erases and hits
        public const float FuseRange = 75f, FuseSpeed = 45f;
        /// <summary>Wheel of Fortune's 7: the die becomes this many of the balls over the caster's head, in <see cref="WheelFuseForm"/>; then one goes every <see cref="WheelFuseGap"/>.</summary>
        public const int WheelFuses = 3;
        public const float WheelFuseForm = 0.3f, WheelFuseGap = 0.3f;
        public const float FuseDamage = 75f;
        public const float FuseKnock = 10f, FuseUp = 4f, FuseRagdoll = 1f;
        public static readonly Color FusePurple = new(2.4f, 0.35f, 4.4f, 1f);

        /// <param name="over">0: the ball in front of its caster; 1, 2, 3: one of the Wheel's, over their head.</param>
        public static float FuseRadiusAt(float t, int over = 0) =>
            Mathf.Lerp(FuseRadius0, FuseRadius1, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / (over > 0 ? WheelFuseForm : FuseCharge))));

        /// <summary>How long after it starts forming a ball is thrown.</summary>
        public static float FuseThrowAt(int over) => over > 0 ? WheelFuseForm + WheelFuseGap * (over - 1) : FuseCharge;

        /// <summary>Where one of the Wheel's balls waits: side by side over the caster's head, the middle one first to go.</summary>
        public static Vector3 FuseOverHead(Vector3 root, float yaw, int over)
        {
            float side = over == 1 ? 0f : over == 2 ? -1f : 1f;
            return root + Vector3.up * (DieAbove + FuseRadius1) + Quaternion.Euler(0f, yaw, 0f) * Vector3.right * (side * FuseRadius1 * 2.3f);
        }

        /// <summary>Where the forming ball sits: in front of the caster, far enough out that it never swallows them.</summary>
        public static Vector3 FuseHoldPoint(Vector3 root, float yaw, float radius) =>
            root + Vector3.up * 0.35f + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * (0.9f + radius);

        // ================================================================ Blade

        /// <summary>The sword manifesting in the hand / vanishing from it (dithered fade).</summary>
        public const float SwordAppear = 0.4f, SwordVanish = 0.6f; // looks only: no spell timing hangs off these
        /// <summary>Damage strikes in a flurry, and the gap between them.</summary>
        public const int FlurryStrikes = 3;
        public const float FlurryGap = 0.1f;

        // ---------------------------------------------------------------- Rising Hammer
        public const float RisingTime = 0.5f;           // the rush (as Severance's): this long, or until someone's in reach
        public const float RisingArm = 0.15f;           // rushing this long before someone in reach sets it off
        public const float RisingHitAt = 0.1f;          // after the trigger, the head comes up through them
        public const float RisingDamage = 18f;
        public const float RisingLaunch = 15f;          // thrown straight up (m/s): about 5.5 m high
        public const float RisingRagdoll = 1.4f;

        // ---------------------------------------------------------------- Descending Hammer
        public const float HammerCrouch = 0.3f;         // down on one knee, then the leap
        public const float LeapForward = 16.5f, LeapUp = 12f; // the leap's speed (m/s): about 3.6 m high, 20 m on level ground
        public const float LeapSteer = 720f;            // deg/s the mouse turns the leap (free redirect)
        public const float LeapMaxAir = 3f;             // never came down (off the edge): no slam
        public const float SlamLead = 0.14f;            // the swing down starts this long before touching down (HammerMotion.SlamSwing, about)
        public const float CraterRadius = 3f;           // what's broken and who's hit: the shockwave is drawn exactly this wide
        public const float CraterDepth = 1f;            // 4 voxels deep in the middle, nothing at the edge
        public const float SlamDamageMax = 45f, SlamDamageMin = 15f;
        public const float SlamKnockOutMax = 14f, SlamKnockOutMin = 6f;
        public const float SlamKnockUpMax = 13f, SlamKnockUpMin = 6f;
        public const float SlamRagdollMax = 1.6f, SlamRagdollMin = 0.8f;

        /// <summary>
        /// The slam's bowl: every breakable cell within <see cref="CraterRadius"/> of the impact across the surface and
        /// within <see cref="CraterDepth"/> of it along the surface normal, an ellipsoid flattened against the surface
        /// (4 deep in the middle, thinning to nothing at the rim). Server only (the cells go out in the op).
        /// </summary>
        public static List<Vector3Int> CraterCells(VoxelWorld w, Vector3 point, Vector3 normal)
        {
            var list = new List<Vector3Int>();
            if (w == null) return list;
            Vector3 n = normal.sqrMagnitude > 1e-4f ? normal.normalized : Vector3.up;
            int r = Mathf.CeilToInt(CraterRadius / VoxelWorld.Size) + 1;
            var c0 = w.CellOf(point);
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            for (int dz = -r; dz <= r; dz++)
            {
                var cell = new Vector3Int(c0.x + dx, c0.y + dy, c0.z + dz);
                ushort v = w.Get(cell);
                if (v == 0 || w.IsIndestructible(v)) continue;
                Vector3 o = w.CellCenter(cell) - point;
                float along = Vector3.Dot(o, n);
                float across = (o - n * along).magnitude;
                float a = across / CraterRadius, b = along / CraterDepth;
                if (a * a + b * b <= 1f) list.Add(cell);
            }
            return list;
        }

        /// <summary>0 at the middle of the slam, 1 at its edge, for a body standing at <paramref name="feet"/> (more than 1: missed).</summary>
        public static float SlamReach(Vector3 point, Vector3 feet)
        {
            // the nearest point of the body (feet to head) to the impact, less its own width
            Vector3 head = feet + Vector3.up * 1.9f;
            Vector3 ab = head - feet;
            float u = Mathf.Clamp01(Vector3.Dot(point - feet, ab) / ab.sqrMagnitude);
            float d = Mathf.Max(0f, (point - (feet + ab * u)).magnitude - 0.35f);
            return d / CraterRadius;
        }

        // ---------------------------------------------------------------- Severance
        public const float SeveranceTime = 0.125f;      // the rush: this long, or until someone's in reach
        public const float SeveranceArm = 0.15f;        // rushing this long before someone in reach sets it off
        public const float SeveranceSlashAt = 0.08f;    // after the trigger, the blade is in front: the cut leaves it
        public const float SeveranceDamage = 5f;        // per strike (3 strikes)
        public const float SeveranceRange = 10f;        // the cut flies this far, then fades
        public const float SeveranceSpeed = 30f;
        public const float SeveranceHalfWidth = 3.4f;   // the crescent, tip to tip / 2 (6.8 m across)
        public const float SeveranceBow = 1.4f;         // how far its tips trail behind its middle (scaled with the width: same curve)
        public const float SeveranceHalfHeight = 0.28f; // half the band's thickness
        public const float SeveranceTilt = 5f;          // random roll either way (degrees)
        public const int SeveranceMaxDepth = 4;         // voxel layers it cuts through before it's spent
        public const float SeveranceKnockOut = 9f, SeveranceKnockUp = 7f, SeveranceRagdoll = 1f;

        /// <summary>The wave's orientation: travelling along <paramref name="dir"/>, rolled by <paramref name="tilt"/> degrees.</summary>
        public static Quaternion WaveRotation(Vector3 dir, float tilt) =>
            Quaternion.LookRotation(dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward, Vector3.up) * Quaternion.Euler(0f, 0f, tilt);

        /// <summary>Where the middle of the crescent is at time <paramref name="t"/> after it left the blade.</summary>
        public static Vector3 WaveCenter(Vector3 origin, Vector3 dir, float t) => origin + dir.normalized * (SeveranceSpeed * Mathf.Max(0f, t));

        /// <summary>A point on the crescent's arc (s from -1 at the left tip to 1 at the right), in wave space.</summary>
        public static Vector3 WaveArc(float s) => new(s * SeveranceHalfWidth, 0f, -SeveranceBow * s * s);

        /// <summary>Is a ball (centre, radius) touching the crescent band, whose middle has swept from <paramref name="back"/> to <paramref name="front"/> metres along?</summary>
        public static bool WaveTouches(Vector3 origin, Quaternion rot, float back, float front, Vector3 p, float radius)
        {
            Vector3 l = Quaternion.Inverse(rot) * (p - origin);
            if (Mathf.Abs(l.x) > SeveranceHalfWidth + radius || Mathf.Abs(l.y) > SeveranceHalfHeight + radius) return false;
            float s = Mathf.Clamp(l.x / SeveranceHalfWidth, -1f, 1f);
            float arc = WaveArc(s).z;
            return l.z >= back + arc - radius - 0.15f && l.z <= front + arc + radius + 0.15f;
        }

        // ---------------------------------------------------------------- Flowing Cut
        public const float FlowStep = 0.3f;              // each 0.3 s of charge adds a charge (flash) ...
        public const int FlowMaxCharges = 3;             // ... up to three; less than one and nothing happens
        public const float FlowDistance = 15f;           // straight ahead, level
        public const float FlowTime = 0.2f;              // the teleport: untouchable for this long
        public const float FlowHalfWidth = 1.5f;         // a generous path: anyone this close to the line is cut
        public static float FlowDamage(int charges) => 5f + Mathf.Clamp(charges, 1, FlowMaxCharges); // 6 / 7 / 8 a strike
        /// <summary>Thrown on along the cut and up (with the first strike), harder the more it was charged: 9 / 14 / 19 out, 4 / 6 / 8 up.</summary>
        public static float FlowKnockOut(int charges) => 4f + 5f * Mathf.Clamp(charges, 1, FlowMaxCharges);
        public static float FlowKnockUp(int charges) => 2f + 2f * Mathf.Clamp(charges, 1, FlowMaxCharges);
        /// <summary>Ragdolled 0.6 / 0.9 / 1.2 s.</summary>
        public static float FlowRagdoll(int charges) => 0.3f + 0.3f * Mathf.Clamp(charges, 1, FlowMaxCharges);

        /// <summary>Charges built up after holding for <paramref name="held"/> seconds (0 = not enough).</summary>
        public static int FlowCharges(float held) => Mathf.Clamp(Mathf.FloorToInt(held / FlowStep + 1e-3f), 0, FlowMaxCharges);

        /// <summary>The level direction you face.</summary>
        public static Vector3 Level(float yaw) => Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;

        /// <summary>
        /// Where a Flowing Cut from <paramref name="feet"/> lands: 12 m straight ahead at the same height, through any
        /// voxels in the way (they're cut), but stopping short of anything that can't be cut (indestructible voxels,
        /// normal meshes). Returns the landing spot for the feet.
        /// </summary>
        public static Vector3 FlowDestination(VoxelWorld w, Vector3 feet, float yaw)
        {
            Vector3 dir = Level(yaw);
            Vector3 best = feet;
            const float step = 0.25f;
            for (float d = step; d <= FlowDistance + 1e-3f; d += step)
            {
                Vector3 p = feet + dir * d;
                if (FlowBlocked(w, p)) break;
                best = p;
            }
            return best;
        }

        static readonly Collider[] Probe = new Collider[24];

        /// <summary>Can a standing body not be cut through to here (indestructible voxels, or solid things that aren't voxels)?</summary>
        static bool FlowBlocked(VoxelWorld w, Vector3 feet)
        {
            Vector3 a = feet + Vector3.up * 0.45f, b = feet + Vector3.up * 1.5f;
            int n = Physics.OverlapCapsuleNonAlloc(a, b, 0.32f, Probe, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (!VoxelWorld.IsVoxelCollider(Probe[i])) return true; // a plain mesh, a portal slab...
            if (w == null) return false;
            var min = w.CellOf(feet + new Vector3(-0.3f, 0.1f, -0.3f));
            var max = w.CellOf(feet + new Vector3(0.3f, 1.85f, 0.3f));
            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
                if (w.IsIndestructible(w.Get(x, y, z))) return true;
            return false;
        }

        /// <summary>Every destructible voxel in the tunnel a Flowing Cut carves from <paramref name="from"/> to <paramref name="to"/> (feet positions).</summary>
        public static List<Vector3Int> FlowCutCells(VoxelWorld w, Vector3 from, Vector3 to)
        {
            var cells = new List<Vector3Int>();
            if (w == null) return cells;
            Vector3 d = to - from;
            d.y = 0f;
            float len = d.magnitude;
            if (len < 0.1f) return cells;
            Vector3 dir = d / len, side = new(dir.z, 0f, -dir.x);
            const float half = 0.45f;
            Vector3 lo = Vector3.Min(from, to) - new Vector3(1f, 0f, 1f), hi = Vector3.Max(from, to) + new Vector3(1f, 2f, 1f);
            var min = w.CellOf(lo);
            var max = w.CellOf(hi);
            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
            {
                ushort v = w.Get(x, y, z);
                if (v == 0 || w.IsIndestructible(v)) continue;
                Vector3 c = w.CellCenter(new Vector3Int(x, y, z)) - from;
                float along = Vector3.Dot(c, dir), across = Vector3.Dot(c, side);
                float up = c.y - (to.y - from.y) * Mathf.Clamp01(along / len);
                if (along < -0.3f || along > len + 0.4f || Mathf.Abs(across) > half || up < 0.08f || up > 1.95f) continue;
                cells.Add(new Vector3Int(x, y, z));
            }
            return cells;
        }

        // ---------------------------------------------------------------- Rock Swarm
        public const int SwarmRocks = 4;
        public const float SwarmGather = 0.3f;          // both hands up: the rocks are torn out and rise into the ring
        public const float SwarmOrbitRadius = 3f;       // they circle the caster this far out...
        public const float SwarmLift = 0.2f;            // ...this high above the hips
        public const float SwarmSpin = 75f;             // deg/s, counter-clockwise seen from above
        public const float SwarmLife = 12f;             // seconds from the cast: then every rock still going crumbles
        public const int SwarmRockR4 = 8;               // each rock is a voxel ball: every cell within 2 cells of its middle
        /// <summary>A rock's radius (0.625 m): the ball of cells is drawn this big, and it hits and is hit this big.</summary>
        public static float SwarmRockRadius => (SwarmRockR4 / 4f + 0.5f) * VoxelWorld.Size;
        public const int SwarmDigR4 = 10;               // the hole each rock is torn out of (the same as Rock Barrage's)
        public const float SwarmPickNear = 1.4f, SwarmPickFar = 2.7f; // ...dug this far round the caster
        public const float SwarmGuard = 3f;             // an enemy projectile this close to the caster: the nearest rock flies into it
        public const float SwarmLookAhead = 0.12f;      // (or about to be this close, this soon: they fly fast)
        public const float SwarmInterceptSpeed = 40f;   // m/s, flying at a projectile
        public const float SwarmDamage = 10f, SwarmKnockOut = 9f, SwarmKnockUp = 6f, SwarmRagdoll = 0.8f;
        public const float SwarmHitEvery = 0.8f;        // the same person isn't struck by the swarm again sooner than this
        public const float SwarmBumpTime = 0.35f;       // knocking against a surface: out along it and back in this long...
        public const float SwarmBumpSize = 0.6f;        // ...this far
        public const float SwarmBumpEvery = 0.3f;
        public const int SwarmScrapeR4 = 5;             // what a bump knocks off the surface (the rock itself never breaks on ground)
        public const float SwarmDodgeTime = 0.3f, SwarmDodgeSize = 1f; // getting out of the way of the caster's own projectiles

        static readonly int[] SwarmPickOrder = { 0, 3, 6, 9, 1, 4, 7, 10, 2, 5, 8, 11 }; // round the compass in quarters first

        /// <summary>
        /// Where the four rocks are torn out of: solid ground all round the caster, the spots well apart. False if
        /// there isn't enough ground about (as Rock Barrage: on a thin ledge or in the air there's nothing to use).
        /// </summary>
        public static bool PlanSwarm(VoxelWorld w, Vector3 feet, float yaw, List<Vector3Int> centers)
        {
            centers.Clear();
            if (w == null) return false;
            var fc = w.CellOf(feet + Vector3.up * 0.05f);
            float[] dists = { 2f, SwarmPickNear, SwarmPickFar };
            var spots = new List<Vector3>();
            foreach (int a in SwarmPickOrder)
            {
                if (centers.Count >= SwarmRocks) break;
                Vector3 dir = Quaternion.Euler(0f, yaw + a * 30f, 0f) * Vector3.forward;
                foreach (float d in dists)
                {
                    var c = w.CellOf(feet + dir * d);
                    int top = w.TopSolid(c.x, c.z, fc.y - 10, fc.y + 3);
                    if (top == int.MinValue) continue;
                    var cand = new Vector3Int(c.x, top - 1, c.z);
                    Vector3 cp = w.CellCenter(cand);
                    bool apart = true;
                    foreach (var s in spots) if ((s - cp).sqrMagnitude < 1.4f * 1.4f) { apart = false; break; }
                    if (!apart || CountSphere(w, cand, SwarmDigR4) < 14) continue;
                    centers.Add(cand);
                    spots.Add(cp);
                    break;
                }
            }
            return centers.Count == SwarmRocks;
        }

        /// <summary>A rock: a ball of <paramref name="value"/> cells round <paramref name="center"/> (radius <see cref="SwarmRockR4"/>).</summary>
        public static List<VoxelWorld.RemovedCell> SwarmBall(Vector3Int center, ushort value)
        {
            var list = new List<VoxelWorld.RemovedCell>();
            int r = (SwarmRockR4 + 3) / 4, lim = SwarmRockR4 * SwarmRockR4;
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            for (int dz = -r; dz <= r; dz++)
                if ((dx * dx + dy * dy + dz * dz) * 16 <= lim)
                    list.Add(new VoxelWorld.RemovedCell { cell = new Vector3Int(center.x + dx, center.y + dy, center.z + dz), value = value });
            return list;
        }

        /// <summary>Rock <paramref name="k"/>'s angle round the caster (degrees: 0 = +x, counter-clockwise seen from above).</summary>
        public static float SwarmAngle(float phase, int k, double start, double now) =>
            phase + k * 360f / SwarmRocks + SwarmSpin * (float)(now - start);

        /// <summary>Rock <paramref name="k"/>'s place in the ring, from the ring's centre.</summary>
        public static Vector3 SwarmOrbit(float phase, int k, double start, double now)
        {
            float a = SwarmAngle(phase, k, start, now) * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * SwarmOrbitRadius;
        }

        /// <summary>The way rock <paramref name="k"/> is going round (level, unit).</summary>
        public static Vector3 SwarmTangent(float phase, int k, double start, double now)
        {
            float a = SwarmAngle(phase, k, start, now) * Mathf.Deg2Rad;
            return new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));
        }

        // ---------------------------------------------------------------- Ice Spike
        public const float IceCrouch = 0.33f;           // crouched, hands on the ground: can't move or turn (the camera can)
        public const int IceCount = 12;                 // spikes along the line...
        public const float IceStart = 1.5f;             // ...the first this far in front...
        public const float IceSpacing = 1.25f;          // ...then one every this far (15.25 m in all)
        public static float IceLength => IceStart + (IceCount - 1) * IceSpacing;
        public const float IceEvery = 0.03f;            // one after another, start to end
        public const float IceRise = 0.15f;             // each shoots up this fast (it only hurts while rising)
        public const float IceMinHeight = 1.25f, IceMaxHeight = 3.25f;
        public const float IceMinRadius = 0.4f, IceMaxRadius = 0.65f; // at the base (taller spikes are thicker)
        public const float IceJitter = 0.5f;            // each a little left or right of the line
        public const float IceLean = 0.45f;             // the tip leans forward along the line by this much of the height
        public const int IceClimb = 8;                  // the line follows the ground up or down this many cells per spike (a taller step stops it)
        public const float IceDamage = 25f, IceKnockUp = 16f, IceKnockOut = 3f, IceRagdoll = 1.3f;
        public const float IceHitWidth = 2f;            // who a spike hits: this many times as wide as the spike (horizontally)
        public const float IceBumpDamage = 10f, IceBumpKnock = 8f, IceBumpUp = 4f; // walking into a standing spike: once per cast, thrown back, not down
        public const float IceMeltReach = 1.5f;         // fire this close to a spike's surface crumbles it
        public const float IceCrumbleStep = 0.08f;      // crumbling top to bottom in 3 steps this far apart

        public struct IceSlot { public int index, top; public Vector3 ground; }

        /// <summary>
        /// The ground under each spike of an Ice Spike cast from <paramref name="feet"/> along <paramref name="dir"/>:
        /// it follows the ground up and down steps, skips gaps, and stops at a wall (more than 2 m up).
        /// </summary>
        public static List<IceSlot> IceGround(VoxelWorld w, Vector3 feet, Vector3 dir)
        {
            var list = new List<IceSlot>();
            if (w == null) return list;
            dir.y = 0f;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
            int prev = w.CellOf(feet + Vector3.up * 0.05f).y - 1; // the cell the feet stand on
            for (int i = 0; i < IceCount; i++)
            {
                Vector3 p = feet + dir * (IceStart + i * IceSpacing);
                var c = w.CellOf(p);
                if (w.IsSolid(c.x, prev + IceClimb + 1, c.z) && w.IsSolid(c.x, prev + IceClimb, c.z)) break; // a wall: the line stops
                int top = w.TopSolid(c.x, c.z, prev - IceClimb, prev + IceClimb);
                if (top == int.MinValue) continue; // a gap: nothing there (the line carries on past it)
                prev = top;
                list.Add(new IceSlot { index = i, top = top, ground = new Vector3(p.x, w.Origin.y + (top + 1) * VoxelWorld.Size, p.z) });
            }
            return list;
        }

        /// <summary>A spike's base radius for its height.</summary>
        public static float IceRadius(float height) =>
            Mathf.Lerp(IceMinRadius, IceMaxRadius, Mathf.InverseLerp(IceMinHeight, IceMaxHeight, height));

        /// <summary>Where a spike's tip is (it leans forward along the line).</summary>
        public static Vector3 IceTip(Vector3 b, Vector3 fwd, float height) => b + Vector3.up * height + fwd * (IceLean * height);

        /// <summary>
        /// The air cells a spike standing on <paramref name="b"/> (a point on the ground, the surface cell row
        /// <paramref name="top"/>) fills: a cone narrowing to its leaning tip, reaching a little down into dips. Each
        /// cell's shade: 0 deep blue at the base, 1, 2 nearly white at the tip.
        /// </summary>
        public static List<Vector3Int> IceSpikeCells(VoxelWorld w, Vector3 b, int top, float height, Vector3 fwd, List<byte> shades)
        {
            var cells = new List<Vector3Int>();
            shades.Clear();
            if (w == null) return cells;
            float radius = IceRadius(height);
            int up = Mathf.CeilToInt(height / VoxelWorld.Size);
            int r = Mathf.CeilToInt((radius + IceLean * height) / VoxelWorld.Size) + 1;
            var c0 = w.CellOf(b);
            for (int y = top - 3; y <= top + up; y++)
            {
                float h = w.Origin.y + (y + 0.5f) * VoxelWorld.Size - b.y; // the cell's middle, above the ground
                if (h > height) break;
                float f = Mathf.Clamp01(h / height);
                float rad = h < 0f ? radius : Mathf.Max(radius * (1f - f), VoxelWorld.Size * 0.75f);
                Vector3 axis = b + fwd * (IceLean * height * f);
                for (int x = c0.x - r; x <= c0.x + r; x++)
                for (int z = c0.z - r; z <= c0.z + r; z++)
                {
                    Vector3 cc = w.CellCenter(new Vector3Int(x, y, z));
                    float dx = cc.x - axis.x, dz = cc.z - axis.z;
                    if (dx * dx + dz * dz > rad * rad || w.Get(x, y, z) != 0) continue;
                    cells.Add(new Vector3Int(x, y, z));
                    shades.Add((byte)(f > 0.72f ? 2 : f > 0.38f ? 1 : 0));
                }
            }
            return cells;
        }

        /// <summary>How far a point is outside a spike (its cone, roughly), for fire melting it.</summary>
        public static float IceDistance(Vector3 b, Vector3 fwd, float height, Vector3 p)
        {
            Vector3 tip = IceTip(b, fwd, height), ab = tip - b;
            float u = Mathf.Clamp01(Vector3.Dot(p - b, ab) / ab.sqrMagnitude);
            return (p - (b + ab * u)).magnitude - IceRadius(height) * (1f - u);
        }
    }
}
