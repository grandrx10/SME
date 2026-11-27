using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Sizes and timings for Remote Flame Bomb, Remote Detonation, Flame Bomb Toss and Skinwalker. What's drawn and what
    /// the server does both come from these.
    /// </summary>
    public static partial class SpellPlans
    {
        // ---------------------------------------------------------------- flame bombs
        public const float BombSize = 0.4f, BombThick = 0.12f; // a square slab (ink included): drawn and hit at this size
        public const float BombPlace = 0.3f;                 // the right hand puts it there (knocked down meanwhile: nothing)
        public const float BombReach = 4f;                   // from the body (feet to head) to the spot
        public const float BombFuse = 0.3f;                  // set off: flashing red this long, then it goes
        public const float BombBlast = 2f;                   // everything that breaks in it goes; people in it are hurt
        public const float BombScorch = 4f;                  // blackened out to here; people in it catch fire (one burn)
        public const float BombDamage = 10f;
        public const float BombKnock = 15f, BombKnockUp = 8f, BombRagdoll = 1.2f; // people in the blast, thrown away from it (the caster too)
        public const float DetonateTime = 0.3f;              // the left hand goes up
        public const float TossTime = 0.3f;                  // both hands thrown forward
        public const int TossCount = 4;
        public const float TossSpeed = 15f, TossSpeedVary = 0.18f, TossSpread = 12f, TossMaxLift = 20f; // (degrees either way; thrown up at most this much, to reach the cursor)
        public const float BombGravity = 16f;
        public const float BombFlyRadius = 0.15f;            // in the air it's a ball this big
        public const float BombMaxFlight = 8f;

        /// <summary>The way to throw from a so it comes down on b: aimed just high enough to make up for falling, as far as <see cref="TossMaxLift"/> goes.</summary>
        public static Vector3 TossDir(Vector3 a, Vector3 b)
        {
            Vector3 to = b - a, flat = new(to.x, 0f, to.z);
            float x = flat.magnitude, v2 = TossSpeed * TossSpeed, g = BombGravity;
            if (x < 0.05f) return to.sqrMagnitude > 1e-4f ? to.normalized : Vector3.forward;
            float straight = Mathf.Atan2(to.y, x), lift = TossMaxLift * Mathf.Deg2Rad;
            float root = v2 * v2 - g * (g * x * x + 2f * to.y * v2);
            if (root >= 0f) lift = Mathf.Clamp(Mathf.Atan2(v2 - Mathf.Sqrt(root), g * x) - straight, 0f, lift);
            float angle = straight + lift;
            return flat / x * Mathf.Cos(angle) + Vector3.up * Mathf.Sin(angle);
        }

        /// <summary>The slab lying on a surface with this normal, turned by <paramref name="spin"/> on it (its own up is the normal).</summary>
        public static Quaternion BombRotation(Vector3 normal, float spin)
        {
            normal = normal.sqrMagnitude > 1e-4f ? normal.normalized : Vector3.up;
            // on walls it sits square; on floors and ceilings it faces the way it was placed
            return Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.Euler(0f, Mathf.Abs(normal.y) > 0.7f ? spin : 0f, 0f);
        }

        /// <summary>"Not a body part": stuck to the body as a whole (rolled up in the chariot's ball).</summary>
        public const int BombWholeBody = 255;

        /// <summary>
        /// The part of a body a bomb is stuck to, as it is right now: on the body, on the ragdoll standing in for it, or on
        /// the corpse. Null when there's nothing left to stick to.
        /// </summary>
        public static Transform BombBone(Combatant c, int bone)
        {
            if (c == null || !c.IsSpawned) return null;
            GameObject doll = c.Dead.Value ? c.LastRagdoll : c.LiveRagdollObject;
            if (c.Dead.Value && doll == null) return null;
            string name = bone >= 0 && bone < Rig.Segments.Length ? Rig.Segments[bone].start : null;
            if (doll != null) return FindPart(doll.transform, name ?? "Bone");
            if (name == null) return c.transform;
            return c.Bones != null && c.Bones.TryGetValue(name, out var t) ? t : c.transform;
        }

        static Transform FindPart(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform child in root)
            {
                var f = FindPart(child, name);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>Where a bomb was placed, sent in a cast's spare id: the world or a body (and which part), and the surface's normal there.</summary>
        public static ulong PackBomb(bool onBody, int bone, ulong body, Vector3 normal)
        {
            static ulong B(float f) => unchecked((ulong)(byte)(sbyte)Mathf.RoundToInt(Mathf.Clamp(f, -1f, 1f) * 127f));
            ulong v = onBody ? (ulong)(uint)(bone & 0xFF) | ((body & 0xFFFFFF) << 8) : 1UL << 63;
            return v | (B(normal.x) << 32) | (B(normal.y) << 40) | (B(normal.z) << 48);
        }

        public static void UnpackBomb(ulong v, out bool onBody, out int bone, out ulong body, out Vector3 normal)
        {
            static float F(ulong b) => unchecked((sbyte)(byte)b) / 127f;
            onBody = (v >> 63) == 0;
            bone = (int)(v & 0xFF);
            body = (v >> 8) & 0xFFFFFF;
            normal = new Vector3(F(v >> 32), F(v >> 40), F(v >> 48));
            normal = normal.sqrMagnitude > 1e-4f ? normal.normalized : Vector3.up;
        }

        /// <summary>How far a point is from a body (feet to head): what <see cref="BombReach"/> is measured by.</summary>
        public static float BombDistance(Combatant c, Vector3 p)
        {
            Vector3 a = c.FeetPosition + Vector3.up * 0.2f, ab = Vector3.up * 1.5f;
            float u = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * u)).magnitude;
        }

        /// <summary>Does a ball of radius <paramref name="r"/> going from a to b touch the slab at <paramref name="center"/>?</summary>
        public static bool BombTouched(Vector3 center, Quaternion rot, Vector3 a, Vector3 b, float r)
        {
            Quaternion inv = Quaternion.Inverse(rot);
            Vector3 p = inv * (a - center), d = inv * (b - a);
            Vector3 h = new(BombSize * 0.5f + r, BombThick * 0.5f + r, BombSize * 0.5f + r);
            float t0 = 0f, t1 = 1f;
            for (int k = 0; k < 3; k++)
            {
                if (Mathf.Abs(d[k]) < 1e-6f)
                {
                    if (Mathf.Abs(p[k]) > h[k]) return false;
                    continue;
                }
                float u = (-h[k] - p[k]) / d[k], v = (h[k] - p[k]) / d[k];
                if (u > v) (u, v) = (v, u);
                t0 = Mathf.Max(t0, u);
                t1 = Mathf.Min(t1, v);
                if (t0 > t1) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- Skinwalker
        public const float SkinShed = 1f;                    // Skinwalker held this long (away from any corpse): the skin comes off
        public const float SkinTime = 2f;                    // down on one knee over the corpse (knocked down meanwhile: nothing)
        public const float SkinMana = 10f;
        /// <summary>Its symbols: grey.</summary>
        public static readonly Color SkinGrey = new(1.15f, 1.2f, 1.3f, 1f);

        /// <summary>The moves a test dummy's body has: the starter loadout.</summary>
        public static EquippedCards BaseKit
        {
            get
            {
                var e = new EquippedCards();
                e[0] = CardId.RockBarrage;
                e[1] = CardId.SummonWall;
                e[4] = CardId.Shove;
                return e;
            }
        }

        /// <summary>
        /// The moves a skinwalker ends up with: the body's, with <paramref name="kept"/> (Skinwalker itself) still in
        /// <paramref name="slot"/>. Whatever that covers moves to an empty slot, if there is one; a Skinwalker of the body's own isn't taken.
        /// </summary>
        public static EquippedCards SkinHand(EquippedCards body, int slot, CardId kept)
        {
            for (int i = 0; i < Loadout.Slots; i++) if (body[i] == CardId.Skinwalker) body[i] = CardId.None;
            var covered = body[slot];
            body[slot] = kept;
            for (int i = 0; covered != CardId.None && i < Loadout.Slots; i++)
                if (body[i] == CardId.None) { body[i] = covered; covered = CardId.None; }
            return body;
        }
    }
}
