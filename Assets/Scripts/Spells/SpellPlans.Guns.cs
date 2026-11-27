using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Sizes, timings and shapes for Rachmaninoff, Sebastian and Undying Grasp. What's drawn and what the server does both
    /// come from these.
    /// </summary>
    public static partial class SpellPlans
    {
        // ---------------------------------------------------------------- both guns
        public const float GunScale = 0.4f;                  // the prefabs are built this much bigger than they're held (as in Gunbound)
        public const float GunAppear = 0.2f, GunVanish = 0.3f; // dithering in and out
        /// <summary>The eyes above the hips, standing; and how far the hips sink down on one knee.</summary>
        public const float GunEye = 0.865f, KneelDrop = 0.42f;

        // ---------------------------------------------------------------- Rachmaninoff
        public const float RachDelay = 0.3f;                 // pulled out, then it fires (knocked down meanwhile: it doesn't)
        public const int RachPellets = 12;
        public const float RachDamage = 3f;                  // each
        public const float RachCone = 15f;                   // degrees across, edge to edge
        public const float RachSpeed = 55f, RachRange = Cards.MaxRange;
        public const float PelletRadius = 0.18f;             // drawn and hit at this size
        public const float RachKnock = 12f, RachKnockUp = 4f, RachRagdoll = 1f; // the first pellet to hit someone; the rest only hurt
        public const float RecoilBack = 21f, RecoilUp = 4f, RecoilRagdoll = 1.1f; // the caster, straight back from the blast

        /// <summary>The way pellet <paramref name="k"/> goes: spread evenly over the cone (no two in one place), turned by <paramref name="turn"/>.</summary>
        public static Vector3 PelletDir(Vector3 dir, int k, float turn)
        {
            float off = RachCone * 0.5f * Mathf.Sqrt((k + 0.5f) / RachPellets);
            return Quaternion.LookRotation(dir) * Quaternion.Euler(0f, 0f, turn + k * 137.5f) * Quaternion.Euler(off, 0f, 0f) * Vector3.forward;
        }

        // ---------------------------------------------------------------- Sebastian
        public const float SebCharge = 0.75f;                // held this long before it will fire
        public const float SebLength = 75f, SebRadius = 0.5f; // the shot: a round-ended cylinder
        public const float SebDamage = 50f;
        public const float SebKnock = 26f, SebKnockUp = 7f, SebRagdoll = 1.8f; // thrown hard along the shot
        public const float SebWindup = 0.3f;                 // let go: the gun charges up this long, then fires (ragdolled meanwhile: it doesn't)
        public const float SebRecover = 0.3f;                // standing up, the gun fading, before moving again
        public const float SebBeamTime = 0.45f;              // the shot hangs in the air this long, thinning away
        /// <summary>The shot: bright orange; its first instant hotter; the rim of what it vaporises golden.</summary>
        public static readonly Color SebOrange = new(4.2f, 1.45f, 0.18f, 1f), SebCore = new(5f, 3.6f, 1.6f, 1f), SebGold = new(3.6f, 2.3f, 0.35f, 1f);

        /// <summary>How much a body is grown (Strength): 1 = its usual size, about the feet.</summary>
        public static float BodyGrow(Combatant c)
        {
            float w = c != null && c.Animator != null ? c.Animator.StrongWeight : 0f;
            return 1f + (StrengthSize - 1f) * (w * w * (3f - 2f * w));
        }

        /// <summary>A point of a body at its usual size, where it is on the grown body.</summary>
        public static Vector3 GrowPoint(Combatant c, Vector3 p)
        {
            float grow = BodyGrow(c);
            if (grow <= 1.001f) return p;
            Vector3 feet = c.FeetPosition;
            return feet + (p - feet) * grow;
        }

        /// <summary>Where the rifle's trigger is held, aimed down its sights: <paramref name="stand"/> 0 down on one knee, 1 standing (body at its usual size).</summary>
        public static Vector3 SebastianGrip(Vector3 hips, Quaternion aim, float stand) =>
            hips + Vector3.up * Mathf.Lerp(GunEye - KneelDrop, GunEye, stand) + aim * new Vector3(0.07f, -0.12f, 0.32f);

        /// <summary>The line of the shot, straight out of the barrel as it's held down on one knee.</summary>
        public static void SebastianRay(Combatant c, float yaw, float pitch, out Vector3 muzzle, out Vector3 dir)
        {
            var aim = Quaternion.Euler(pitch, yaw, 0f);
            dir = aim * Vector3.forward;
            muzzle = GrowPoint(c, SebastianGrip(c.transform.position, aim, 0f) + aim * GunModel.Muzzle(GunKind.Sebastian));
        }

        static readonly RaycastHit[] _sebHits = new RaycastHit[32];

        /// <summary>
        /// How far along the shot gets (up to <paramref name="length"/>): it goes through any ground that breaks, and stops at
        /// ground that doesn't, or a portal. <paramref name="back"/>: also looked for this far behind the start (a barrel
        /// poked through something).
        /// </summary>
        public static float SebastianReach(VoxelWorld w, Vector3 from, Vector3 dir, float length, float back)
        {
            float reach = length;
            if (w != null)
                for (float d = -back; d < length; d += VoxelWorld.Size * 0.4f)
                    if (w.IsIndestructible(w.Get(w.CellOf(from + dir * d)))) { reach = Mathf.Max(0f, d); break; }
            int n = Physics.RaycastNonAlloc(from - dir * back, dir, _sebHits, length + back, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (_sebHits[i].collider != null && _sebHits[i].distance - back < reach && _sebHits[i].collider.GetComponentInParent<PortalView>() != null)
                    reach = Mathf.Max(0f, _sebHits[i].distance - back);
            return reach;
        }

        /// <summary>Every breakable cell in the cylinder from a to b.</summary>
        public static void SebastianCells(VoxelWorld w, Vector3 a, Vector3 b, HashSet<Vector3Int> cells)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (w == null || len < 0.01f) return;
            d /= len;
            int r = Mathf.CeilToInt(SebRadius / VoxelWorld.Size) + 1;
            for (float s = 0f; s <= len; s += VoxelWorld.Size * 0.9f)
            {
                var c0 = w.CellOf(a + d * s);
                for (int x = -r; x <= r; x++)
                for (int y = -r; y <= r; y++)
                for (int z = -r; z <= r; z++)
                {
                    var c = new Vector3Int(c0.x + x, c0.y + y, c0.z + z);
                    ushort v = w.Get(c);
                    if (v == 0 || w.IsIndestructible(v)) continue;
                    Vector3 p = w.CellCenter(c) - a;
                    float along = Vector3.Dot(p, d);
                    if (along < 0f || along > len || (p - d * along).sqrMagnitude > SebRadius * SebRadius) continue;
                    cells.Add(c);
                }
            }
        }

        // ---------------------------------------------------------------- Undying Grasp
        public const float GraspCast = 0.5f;                 // down on one knee, turning with the camera (knocked down meanwhile: nothing)
        public const float GraspRise = 0.4f;                 // the hands shoot out; whoever is in reach meanwhile is grabbed
        public const float GraspHold = 3f;
        public const float GraspBreak = 30f;                 // damage taken while held that breaks free (ragdolled breaks free too)
        public const int GraspCount = 15;                    // hands along the line, 1 m apart: 15 m
        public const float GraspSpacing = 1f;
        public const float GraspSide = 0.28f;                // left and right of the line in turn
        public const float GraspReach = 0.75f;               // a body's middle this close to the line (a body's half width past the hands)
        public const float GraspHeight = 1.1f;               // a hand's height: feet above that aren't reached
        public const int GraspClimb = 8;                     // the line follows the ground up or down this many cells per hand

        /// <summary>One hand: the ground it comes out of, and the ground at the middle of the line there.</summary>
        public struct GraspSlot { public Vector3 ground, middle; }

        /// <summary>
        /// The ground under each hand: along the line from <paramref name="feet"/>, following the ground up and down; no
        /// ground, no hand there; a wall more than 2 m up stops it.
        /// </summary>
        public static List<GraspSlot> GraspGround(VoxelWorld w, Vector3 feet, Vector3 dir)
        {
            var list = new List<GraspSlot>();
            if (w == null) return list;
            dir.y = 0f;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
            Vector3 right = new(dir.z, 0f, -dir.x);
            int prev = w.CellOf(feet + Vector3.up * 0.05f).y - 1;
            for (int i = 0; i < GraspCount; i++)
            {
                Vector3 mid = feet + dir * ((i + 1) * GraspSpacing);
                var c = w.CellOf(mid);
                if (w.IsSolid(c.x, prev + GraspClimb + 1, c.z) && w.IsSolid(c.x, prev + GraspClimb, c.z)) break;
                int top = w.TopSolid(c.x, c.z, prev - GraspClimb, prev + GraspClimb);
                if (top == int.MinValue) continue;
                prev = top;
                float y = w.Origin.y + (top + 1) * VoxelWorld.Size;
                // the hand itself, a little to one side (on the ground there, if that's about level)
                Vector3 p = mid + right * (i % 2 == 0 ? GraspSide : -GraspSide);
                var pc = w.CellOf(p);
                int side = w.TopSolid(pc.x, pc.z, top - 2, top + 2);
                if (side == int.MinValue) p = mid;
                float py = side == int.MinValue ? y : w.Origin.y + (side + 1) * VoxelWorld.Size;
                list.Add(new GraspSlot { ground = new Vector3(p.x, py, p.z), middle = new Vector3(mid.x, y, mid.z) });
            }
            return list;
        }

        /// <summary>Does the hand at <paramref name="slot"/> (the ground at the middle of the line there) reach a body standing at <paramref name="feet"/>?</summary>
        public static bool GraspTouches(Vector3 slot, Vector3 dir, Vector3 feet)
        {
            Vector3 d = feet - slot;
            if (d.y < -0.6f || d.y > GraspHeight) return false;
            float along = d.x * dir.x + d.z * dir.z, across = d.x * dir.z - d.z * dir.x;
            return Mathf.Abs(along) <= GraspSpacing * 0.5f + 0.2f && Mathf.Abs(across) <= GraspReach;
        }
    }
}
