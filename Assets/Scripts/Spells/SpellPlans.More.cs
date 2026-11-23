using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Sizes, timings and shapes for The Hermit, Wheel of Fortune, Justice, The Hanged Man, Death, Temperance, The Devil,
    /// The Tower, the Necromancy cards, the Flame Thrower, Icy Breath and the Grapple Hook. What's drawn and what the
    /// server does both come from these.
    /// </summary>
    public static partial class SpellPlans
    {
        // ---------------------------------------------------------------- The Hermit
        public const float HermitKneel = 0.3f;      // down on one knee before anything moves
        public const float HermitBuild = 0.3f;      // the walls rise and the roof comes down in this long
        public const int HouseInner = 8;            // the room: every cell within 8 of the middle (4.25 m across)
        public const int HouseWall = 3;             // walls 3 voxels (0.75 m) thick
        public const int HouseHeight = 18;          // 4.5 m of wall above the floor
        public const int HouseRoofLayers = 9;       // the roof steps in one voxel a layer
        public const int HouseDoorHalf = 4;         // the door: 8 voxels (2 m) wide...
        public const int HouseDoorHeight = 13;      // ...and 3.25 m tall, its top corners rounded off
        public const int HouseRoofDrop = 15;        // the roof comes down from this many voxels above its place

        public class HousePlan
        {
            public bool valid;
            public Vector3Int center;                // the floor cell under the caster
            public Vector3Int door;                  // the side the door is on (a unit step along x or z)
            public readonly List<Vector3Int> walls = new(), roof = new();
            public readonly List<ushort> wallValues = new(), roofValues = new();
        }

        static int HouseOuter => HouseInner + HouseWall;

        /// <summary>
        /// A little house of ground round <paramref name="feet"/>: four walls with a door on the side you face, a
        /// stepped roof and a chimney. Only fills air (what's already there stays). Invalid without ground underfoot.
        /// </summary>
        public static HousePlan PlanHouse(VoxelWorld w, Vector3 feet, float yaw)
        {
            var plan = new HousePlan();
            if (w == null) return plan;
            var c = w.CellOf(feet + Vector3.down * 0.1f);
            int ys = w.TopSolid(c.x, c.z, c.y - 4, c.y + 1);
            if (ys == int.MinValue) return plan;
            plan.center = new Vector3Int(c.x, ys, c.z);
            Vector3 f = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            plan.door = Mathf.Abs(f.x) >= Mathf.Abs(f.z) ? new Vector3Int(f.x >= 0f ? 1 : -1, 0, 0) : new Vector3Int(0, 0, f.z >= 0f ? 1 : -1);
            ushort body = GroundValue(w, c.x, ys, c.z, false), top = GroundValue(w, c.x, ys, c.z, true);

            int o = HouseOuter;
            for (int dx = -o; dx <= o; dx++)
            for (int dz = -o; dz <= o; dz++)
            {
                if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) <= HouseInner) continue;
                for (int h = 1; h <= HouseHeight; h++)
                {
                    if (InDoor(plan.door, dx, dz, h)) continue;
                    Add(w, plan.walls, plan.wallValues, new Vector3Int(c.x + dx, ys + h, c.z + dz), body);
                }
            }
            // the roof: a stepped pyramid, two voxels thick, hanging one over the walls
            for (int k = 0; k < HouseRoofLayers; k++)
            {
                int half = o + 1 - k, y = ys + HouseHeight + 1 + k;
                bool cap = k == HouseRoofLayers - 1;
                for (int dx = -half; dx <= half; dx++)
                for (int dz = -half; dz <= half; dz++)
                {
                    if (!cap && Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) < half - 1) continue;
                    Add(w, plan.roof, plan.roofValues, new Vector3Int(c.x + dx, y, c.z + dz), top);
                }
            }
            // a chimney on the back corner, poking out of the roof
            int cx = -plan.door.x * (o - 2) + (plan.door.x == 0 ? o - 2 : 0), cz = -plan.door.z * (o - 2) + (plan.door.z == 0 ? o - 2 : 0);
            for (int h = HouseHeight + 1; h <= HouseHeight + HouseRoofLayers + 2; h++)
            for (int ax = 0; ax < 2; ax++)
            for (int az = 0; az < 2; az++)
                Add(w, plan.roof, plan.roofValues, new Vector3Int(c.x + cx - ax, ys + h, c.z + cz - az), body);
            plan.valid = plan.walls.Count > 0;
            return plan;
        }

        static bool InDoor(Vector3Int door, int dx, int dz, int h)
        {
            if (h > HouseDoorHeight) return false;
            int along = door.x != 0 ? dx : dz, across = door.x != 0 ? dz : dx;
            int side = door.x != 0 ? door.x : door.z;
            if (along * side <= HouseInner) return false;
            int lo = -HouseDoorHalf, hi = HouseDoorHalf - 1;
            if (h >= HouseDoorHeight - 1) { lo++; hi--; } // the arch
            return across >= lo && across <= hi;
        }

        static void Add(VoxelWorld w, List<Vector3Int> cells, List<ushort> values, Vector3Int p, ushort v)
        {
            if (w.Get(p) != 0 || cells.Contains(p)) return;
            cells.Add(p);
            values.Add(v);
        }

        /// <summary>
        /// The ground material at a column (its top, or what's under the top), never an indestructible one: what a summoned
        /// building is made of. Falls back to any breakable material in the palette.
        /// </summary>
        public static ushort GroundValue(VoxelWorld w, int x, int top, int z, bool surface)
        {
            ushort s = w.Get(x, top, z), b = w.Get(x, top - 1, z);
            ushort v = surface ? s : (b != 0 ? b : s);
            if (v != 0 && !w.IsIndestructible(v)) return v;
            if (s != 0 && !w.IsIndestructible(s)) return s;
            for (int i = 1; i < w.Palette.Count; i++) if (!w.Palette[i].indestructible) return (ushort)i;
            return 1;
        }

        /// <summary>The house as see-through boxes for the preview: the four walls and the roof.</summary>
        public static void HouseBoxes(VoxelWorld w, HousePlan p, List<(Vector3 c, Quaternion r, Vector3 s)> boxes)
        {
            boxes.Clear();
            if (w == null) return;
            float S = VoxelWorld.Size;
            Vector3 floor = w.CellMin(p.center) + new Vector3(0.5f, 1f, 0.5f) * S;
            float outer = (HouseOuter * 2 + 1) * S, thick = HouseWall * S, h = HouseHeight * S;
            float off = (HouseOuter * 2 + 1 - HouseWall) * 0.5f * S;
            Vector3 up = Vector3.up * (h * 0.5f);
            boxes.Add((floor + up + Vector3.right * off, Quaternion.identity, new Vector3(thick, h, outer)));
            boxes.Add((floor + up - Vector3.right * off, Quaternion.identity, new Vector3(thick, h, outer)));
            boxes.Add((floor + up + Vector3.forward * off, Quaternion.identity, new Vector3(outer, h, thick)));
            boxes.Add((floor + up - Vector3.forward * off, Quaternion.identity, new Vector3(outer, h, thick)));
            for (int k = 0; k < HouseRoofLayers; k += 2)
            {
                float side = ((HouseOuter + 1 - k) * 2 + 1) * S;
                boxes.Add((floor + Vector3.up * (h + (k + 1) * S), Quaternion.identity, new Vector3(side, 2f * S, side)));
            }
        }

        // ---------------------------------------------------------------- Wheel of Fortune
        public const float WheelCast = 0.1f, WheelRoll = 0.75f, WheelShow = 0.5f;
        /// <summary>From the cast until the die is gone and its effect happens (no other card meanwhile).</summary>
        public const float WheelBusy = WheelCast + WheelRoll + WheelShow;
        public const float DieSize = 0.6f;                   // the die's edge (m)
        public const float DieAbove = 2.1f;                  // its middle, above the hips
        public const float WheelFling = 30f, WheelFlingRagdoll = 1.6f;   // 2: thrown straight up (it crashes through what's above)
        public const float DieDamage = 30f;                  // 3: the die thrown like Rock Barrage's rock...
        public static float DieRadius => DieSize * 0.5f;     // ...hitting at its own size
        public const float SteelTime = 5f;                   // 5: nothing moves you
        public const float GoldTime = 3f;                    // 6: nothing hurts you
        public const float DoomTime = 5f;                    // 0: flashing red, then you blow up
        public const float DoomNear = 2f, DoomNearDamage = 150f, DoomFar = 5f, DoomFarDamage = 40f;
        public const float WheelSiphonScale = 2f;            // 4: Mana Siphon, with double the damage and the mana stolen
        public const float WheelSinge = 3.3f;                // 1: the ground this far round the caster is singed

        // ---------------------------------------------------------------- Justice
        public const float JusticeClasp = 0.3f;              // hands clasped before the tether sprouts
        public const float JusticeTime = 6f;                 // it holds this long...
        public const float JusticeBreak = 12f;               // ...unless the two get further apart than this (or a wall comes between them)
        public const float JusticePick = 2f;                 // the target: whoever is nearest the cursor, within this of it

        // ---------------------------------------------------------------- The Hanged Man
        public const float HangedDrop = 0.3f;                // the noose comes down from the sky this long...
        public const float HangedRise = 2.5f;                // ...then lifts you by the neck, this fast (m/s), until you let go
        public const float HangedDamage = 10f;               // a second, while hanging...
        public const float HangedTick = 0.5f;                // ...dealt this often
        public const float HangedRopeUp = 40f;               // the rope reaches this far up (what's in its way breaks)
        public const float HangedForever = 3600f;            // (hanging lasts until it's let go)
        public static readonly Color Rope = new(0.42f, 0.3f, 0.17f, 1f);

        // ---------------------------------------------------------------- Death
        public const float DeathDelay = 0.3f;                // the head tipped back
        public const float DeathRadius = 4f;
        public const float DeathGrow = 0.3f;                 // the aura spreads out to its full size this fast
        public const float DeathDamage = 2f;                 // a second in it: 2, then 4, 8, 16... (back to 2 after leaving it)
        /// <summary>Mana per second the Flame Thrower takes (no regeneration meanwhile).</summary>
        public const float ChannelDrain = 2f;
        /// <summary>Mana per second Death's aura takes (no regeneration meanwhile; the same rate as The Lovers' tether).</summary>
        public const float DeathDrain = SpellPlans.LoversDrain;

        /// <summary>Damage a second for someone who has been in Death's aura for <paramref name="seconds"/>.</summary>
        public static float DeathRate(float seconds) => DeathDamage * Mathf.Pow(2f, Mathf.Min(Mathf.Floor(seconds), 12f));

        // ---------------------------------------------------------------- Strength
        public const float StrengthFlex = 0.3f;              // flexing...
        public const float StrengthTime = 4f;                // ...then this long transformed:
        public const float StrengthSize = 2f;                // the body (not the head) twice the size,
        public const float StrengthJump = 2f;                // jumping twice as high, falling twice as fast,
        public const float StrengthDamage = 1.5f;            // and hitting half as hard again
        public const float StrengthGrow = 0.25f;             // (growing in / shrinking back takes this long)
        public const float StrengthBump = 1.15f;             // walking into an enemy (this close, middle to middle): they're thrown...
        public const float StrengthKnock = 11f, StrengthKnockUp = 6f, StrengthRagdoll = 1f;
        public const float StrengthBumpEvery = 0.8f;         // ...the same one no more often than this

        // ---------------------------------------------------------------- Temperance (passive)
        public const int TemperanceMana = 3, TemperanceHealth = 25;

        // ---------------------------------------------------------------- The Devil
        public const float DevilRaise = 0.2f;                // the right hand up
        public const float DevilMana = 3f, DevilRestore = 0.1f;

        // ---------------------------------------------------------------- The Tower
        public const float TowerPray = 0.7f;                 // kneeling in prayer (nothing moves you; hurt meanwhile: no tower)
        public const float TowerRise = 2f;                   // it rises out of the ground...
        public const float TowerTurn = 0.3f;                 // ...turns to face your aim...
        public const float TowerFall = 1.5f;                 // ...and topples over that way
        public const int TowerBase = 20;                     // voxels square (5 m)
        public const int TowerHeight = 60;                   // voxels (15 m), crenellations on top
        public const int TowerCapOver = 1;                   // the top section juts out this far each side...
        public const int TowerCapHeight = 8;                 // ...and is this tall
        public const float TowerDamage = 60f, TowerKnock = 14f, TowerRagdoll = 1.5f;
        static float TowerHalf => TowerBase * VoxelWorld.Size * 0.5f;

        static List<Vector3Int> _towerShape;

        /// <summary>
        /// The tower's cells relative to its base cell (the ground cell under its middle): hollow, its walls one voxel
        /// thick, a door on the -z side, slit windows, a wider top section with a roof and crenellations.
        /// </summary>
        public static List<Vector3Int> TowerShape()
        {
            if (_towerShape != null) return _towerShape;
            var cells = new HashSet<Vector3Int>();
            int half = TowerBase / 2;                       // x and z in [-half, half - 1]
            int capFrom = TowerHeight - TowerCapHeight;
            for (int y = 1; y <= TowerHeight; y++)
            {
                bool cap = y > capFrom;
                int lo = cap ? -half - TowerCapOver : -half, hi = cap ? half - 1 + TowerCapOver : half - 1;
                for (int x = lo; x <= hi; x++)
                for (int z = lo; z <= hi; z++)
                {
                    bool edge = x == lo || x == hi || z == lo || z == hi;
                    bool floor = y == capFrom + 1 || y == TowerHeight; // the cap's underside and the roof
                    if (!edge && !floor) continue;
                    if (!cap && Opening(x, y, z, half)) continue;
                    cells.Add(new Vector3Int(x, y, z));
                }
            }
            // crenellations round the top
            int clo = -half - TowerCapOver, chi = half - 1 + TowerCapOver;
            for (int y = TowerHeight + 1; y <= TowerHeight + 2; y++)
            for (int x = clo; x <= chi; x++)
            for (int z = clo; z <= chi; z++)
            {
                bool edge = x == clo || x == chi || z == clo || z == chi;
                if (!edge || ((x - clo + z - clo) / 2) % 2 == 1) continue;
                cells.Add(new Vector3Int(x, y, z));
            }
            _towerShape = new List<Vector3Int>(cells);
            return _towerShape;
        }

        /// <summary>The door (a round-topped arch) and the slit windows up the shaft.</summary>
        static bool Opening(int x, int y, int z, int half)
        {
            // door on the -z face
            if (z == -half && y <= 8 && x >= -2 && x <= 1 && !(y == 8 && (x == -2 || x == 1))) return true;
            // slit windows, staggered up each face
            int along = (z == -half || z == half - 1) ? x : z;
            int face = z == -half ? 0 : z == half - 1 ? 1 : x == -half ? 2 : 3;
            for (int k = 0; k < 4; k++)
            {
                int wy = 15 + k * 9 + face * 2, wa = (k % 2 == 0 ? -5 : 4) + face % 2;
                if (y >= wy && y < wy + 3 && along == wa) return true;
            }
            return false;
        }

        /// <summary>
        /// Where a tower is cast: on the ground under the aim. False with no ground there (it can't stand on nothing).
        /// </summary>
        public static bool PlanTower(VoxelWorld w, Vector3 target, out Vector3Int baseCell)
        {
            baseCell = default;
            if (w == null) return false;
            var c = w.CellOf(target);
            int ys = w.TopSolid(c.x, c.z, c.y - 8, c.y + 4);
            if (ys == int.MinValue) return false;
            baseCell = new Vector3Int(c.x, ys, c.z);
            return true;
        }

        /// <summary>The middle of the tower's foot (the pivot it rises and turns about): the top of its base cell.</summary>
        public static Vector3 TowerFoot(VoxelWorld w, Vector3Int baseCell) => w.CellMin(baseCell) + new Vector3(0f, 1f, 0f) * VoxelWorld.Size;

        /// <summary>
        /// The tower's pose at <paramref name="t"/> seconds after it started rising (its foot, and rotation). It turns
        /// (from <paramref name="turnAt"/>, by <paramref name="turnBy"/> degrees) then falls along <paramref name="fallDir"/>.
        /// </summary>
        public static void TowerPose(Vector3 foot, float t, float turnAt, float turnBy, Vector3 fallDir, out Vector3 pivot, out Quaternion rot, out float fallAngle)
        {
            fallAngle = 0f;
            float rise = Mathf.Clamp01(t / TowerRise);
            rise = 1f - (1f - rise) * (1f - rise);
            pivot = foot + Vector3.down * ((TowerHeight + 4) * VoxelWorld.Size * (1f - rise));
            rot = Quaternion.identity;
            if (turnAt < 0f || t < turnAt) return;
            float u = Mathf.Clamp01((t - turnAt) / TowerTurn);
            u = u * u * (3f - 2f * u);
            Quaternion yaw = Quaternion.Euler(0f, turnBy * u, 0f);
            rot = yaw;
            float f = Mathf.Clamp01((t - turnAt - TowerTurn) / TowerFall);
            if (f <= 0f) return;
            fallAngle = 90f * f * f; // toppling: slow at first, then crashing down
            Quaternion fall = Quaternion.AngleAxis(fallAngle, Vector3.Cross(Vector3.up, fallDir));
            Vector3 edge = foot + fallDir * TowerHalf;
            pivot = edge + fall * (foot - edge);
            rot = fall * yaw;
        }

        /// <summary>How far to turn the square tower (degrees, -45..45) so one face looks along <paramref name="aimYaw"/>.</summary>
        public static float TowerTurnBy(float aimYaw)
        {
            float d = Mathf.Repeat(aimYaw + 45f, 90f) - 45f;
            return d;
        }

        /// <summary>Seconds after the turn starts until the tower lies flat.</summary>
        public const float TowerDown = TowerTurn + TowerFall;

        /// <summary>Is a point inside the tower's walls at this pose (with <paramref name="pad"/> to spare)?</summary>
        public static bool InTower(Vector3 p, Vector3 pivot, Quaternion rot, float pad)
        {
            Vector3 l = Quaternion.Inverse(rot) * (p - pivot);
            float half = TowerHalf + TowerCapOver * VoxelWorld.Size + pad;
            float h = (TowerHeight + 3) * VoxelWorld.Size;
            return Mathf.Abs(l.x) <= half && Mathf.Abs(l.z) <= half && l.y >= -pad && l.y <= h + pad;
        }

        /// <summary>The tower's preview: its shaft and its top section.</summary>
        public static void TowerBoxes(VoxelWorld w, Vector3Int baseCell, List<(Vector3 c, Quaternion r, Vector3 s)> boxes)
        {
            boxes.Clear();
            if (w == null) return;
            Vector3 foot = TowerFoot(w, baseCell);
            float S = VoxelWorld.Size, side = TowerBase * S, capSide = (TowerBase + 2 * TowerCapOver) * S;
            float shaft = (TowerHeight - TowerCapHeight) * S, cap = (TowerCapHeight + 3) * S;
            boxes.Add((foot + Vector3.up * (shaft * 0.5f), Quaternion.identity, new Vector3(side, shaft, side)));
            boxes.Add((foot + Vector3.up * (shaft + cap * 0.5f), Quaternion.identity, new Vector3(capSide, cap, capSide)));
        }

        // ---------------------------------------------------------------- Necromancy
        public const float ReviveTime = 1f;                  // crouched over the corpse
        public const float ReviveReach = 2f;                 // the corpse must be this close
        public const float UndeadHealth = 30f;
        public const byte UndeadMana = 5;
        public const float InfectTime = 1f;                  // a bitten body turns green and gets up this long after dying
        public static readonly Color UndeadSkin = new(0.42f, 0.6f, 0.26f, 1f);
        /// <summary>Sickly green energy (HDR).</summary>
        public static readonly Color NecroGlow = new(0.9f, 2.6f, 0.35f, 1f);

        public const float BiteCrouch = 0.2f;                // crouched, then the lunge
        public const float BiteSpeed = 18.2f, BiteUp = 5.6f, BiteRagdoll = 1f;
        public const float BiteRecover = 0.5f;               // after biting someone: still down this long, then up
        public const float BiteDamage = 15f;
        public const float BiteReach = 0.85f;                // the head hits whoever comes this close to it
        public const float BiteMaxTime = 2.5f;               // a lunge that never landed is over by then

        public const float HordeCrouch = 0.4f;
        public const float HordeRise = 0.8f;                 // the zombies climb out of the ground
        public const float HordeHealth = 15f;
        public const float HordeScale = 0.75f;               // three quarters of a player's height
        public const float HordeSide = 1.6f;                 // either side of the caster
        public const float HordeBiteEvery = 3f;
        public const float HordeSpeed = 3.6f;                // shambling
        public const float HordeBiteRange = 4.5f;            // close enough to lunge
        public const int HordeMax = 6;                       // a necromancer's horde: no more than this many at once

        // ---------------------------------------------------------------- Flame Thrower
        public const float FlameReach = 9.9f;                 // the cone: this long...
        public const float FlameAngle = 22f;                 // ...this wide each side of the aim (degrees)
        public const float FlameSinge = 0.1f;                // ground in it blackens, and this long after it burns away (layer by layer)
        public const float FlameBurnEvery = 0.5f;            // a burn stack every half second someone's in it
        public const int FlameRays = 24;                     // rays cast through the cone each check
        public const float FlameCheck = 0.1f;               // (singed on one check, burnt through on the next)

        /// <summary>Is <paramref name="p"/> in a breath cone from <paramref name="origin"/> along <paramref name="dir"/>?</summary>
        public static bool InCone(Vector3 origin, Vector3 dir, float reach, float angle, Vector3 p, float pad)
        {
            Vector3 d = p - origin;
            float along = Vector3.Dot(d, dir);
            if (along < -pad || along > reach + pad) return false;
            float radius = Mathf.Max(0.3f, along * Mathf.Tan(angle * Mathf.Deg2Rad)) + pad;
            return (d - dir * along).sqrMagnitude <= radius * radius;
        }

        /// <summary>Where a breath comes out (the mouth / clasped hands) and which way, for a body facing yaw / pitch.</summary>
        public static void BreathFrom(Combatant c, out Vector3 origin, out Vector3 dir)
        {
            dir = Quaternion.Euler(Mathf.Clamp(c.ViewPitch, -60f, 70f), c.ViewYaw, 0f) * Vector3.forward;
            origin = c.transform.position + Vector3.up * 0.55f + new Vector3(dir.x, 0f, dir.z).normalized * 0.45f;
        }

        // ---------------------------------------------------------------- Icy Breath
        public const float BreathLean = 0.3f;
        public const float IceBreathTime = 2.25f;
        public const float IceBreathReach = 6.5f, IceBreathAngle = 24f;
        public const float SlowScale = 0.5f, SlowTime = 2f;   // breathed on: half speed, frosted over, until 2 s after the last of it
        public const float IceSlipSpeed = 1.5f;              // walking on ice faster than this: feet out from under you
        public const float IceSlipRagdoll = 0.9f;

        // ---------------------------------------------------------------- Grapple Hook
        public const float HookCast = 0.1f;
        public const float HookSpeed = 82.5f;
        public const float HookRange = 400f;                // (as good as unlimited)
        public const float HookPull = 85f, HookMaxSpeed = 34f; // reeling yourself in
        public const float HookArrive = 1.3f;                // this close to the hook: it lets go
        public const float HookReelTime = 1f;                // a hooked enemy is dragged in over this long
        public const float HookDamage = 15f;
        public const float HookMaxTime = 8f;                 // reeling in for longer than this: it lets go
        public const int HookBreakR4 = 8;                    // where it bites into the ground: a 2-voxel ball breaks
    }
}
