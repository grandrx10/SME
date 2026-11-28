using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Sizes, timings and shapes for the Botany cards (Sunflower, Cactus, Fly Trap: a potted plant held in both hands) and
    /// Smallify Hex. What's drawn and what the server does both come from these.
    /// </summary>
    public static partial class SpellPlans
    {
        // ---------------------------------------------------------------- the pot
        public const byte PotSunflower = 1, PotCactus = 2, PotFlyTrap = 3, PotPine = 5; // (4 is Smallify Hex's)
        public const float PotAppear = 0.2f;                 // the pot phases into the hands (dithers in)...
        public const float PotGrow = 0.3f;                   // ...and the plant grows up out of it
        public const float PotVanish = 0.25f;                // gone: dithered away
        /// <summary>Where a held pot's base is, in front of the body (as the hands hold it), before the body's grown or shrunk.</summary>
        public static readonly Vector3 PotHeld = new(0f, -0.22f, 0.5f);
        public const float PlantLift = 0.08f;                // the plant sits this much higher in the pot than in the user's scene
        public const float EatenDrop = 0.35f;                // someone in a fly trap: their hips this far under its mouth (chest in the jaws)
        public const float PotGravity = 22f;                 // a thrown pot falls like this

        public static byte PotKind(CardId card) => card switch
        {
            CardId.Sunflower => PotSunflower,
            CardId.Cactus => PotCactus,
            CardId.FlyTrap => PotFlyTrap,
            CardId.PineTree => PotPine,
            _ => 0,
        };

        public const float PotUnderHands = 0.1f;             // a held pot's base is this far under the middle of the hands

        /// <summary>
        /// A held pot's base: in the hands, wherever the animation has them this frame (just under the middle of the two),
        /// so it moves exactly as they do. (No hands to go by: in front of the body, below the chest.)
        /// </summary>
        public static Vector3 PotPoint(Combatant c)
        {
            float grow = BodyGrow(c) * SmallScale(c);
            Vector3 fallback = c.transform.position + Quaternion.Euler(0f, c.ViewYaw, 0f) * (PotHeld * grow);
            Vector3 mid = (SpellVisuals.RightHand(c) + SpellVisuals.LeftHand(c)) * 0.5f;
            if ((mid - fallback).magnitude > 1.5f * grow) return fallback; // (the hands aren't posed yet)
            return mid + Vector3.down * (PotUnderHands * grow);
        }

        // ---------------------------------------------------------------- Sunflower
        public const float FlowerGlow = 3f;                  // grown, it glows brighter and brighter this long...
        public const float FlowerLaser = 3f;                 // ...then the beam, this long, wherever the caster aims
        public const float FlowerDps = 20f;
        public const float FlowerRange = Cards.MaxRange * 1.25f;
        public const float FlowerBeamRadius = 0.3f;          // drawn and hits at this radius
        public const float FlowerLayerEvery = 0.2f;          // what it touches is vaporised, a layer at a time
        public static float FlowerLaserStart => PotGrow + FlowerGlow;
        public static float FlowerLaserEnd => FlowerLaserStart + FlowerLaser;

        static readonly RaycastHit[] _aimHits = new RaycastHit[16];

        /// <summary>
        /// The way the beam goes from the flower's face: at whatever the caster's centre dot is on (worked out alike on every
        /// peer from the replicated view: a ray from the eyes along it, the first thing it meets that isn't the caster).
        /// </summary>
        public static Vector3 FlowerAim(Combatant c, Vector3 from)
        {
            Vector3 view = Quaternion.Euler(c.ViewPitch, c.ViewYaw, 0f) * Vector3.forward;
            Vector3 eye = c.transform.position + Vector3.up * (0.7f * BodyGrow(c) * SmallScale(c));
            const float far = 80f;
            Vector3 at = eye + view * far;
            float best = far;
            int n = Physics.RaycastNonAlloc(eye, view, _aimHits, far, Layers.BulletMask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var h = _aimHits[i];
                if (h.distance >= best || h.collider == null) continue;
                var hb = h.collider.GetComponent<Hitbox>();
                if (hb != null ? hb.Owner == c : h.collider.isTrigger) continue; // (not the caster, nor other triggers)
                best = h.distance;
                at = h.point;
            }
            Vector3 d = at - from;
            return d.sqrMagnitude > 0.04f ? d.normalized : view;
        }

        /// <summary>How far the beam gets from <paramref name="from"/>: to the first thing solid, or its full range.</summary>
        public static float FlowerReach(Vector3 from, Vector3 dir) =>
            Physics.SphereCast(from, FlowerBeamRadius * 0.5f, dir, out var hit, FlowerRange, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)
                ? hit.distance + FlowerBeamRadius * 0.5f : FlowerRange;
        /// <summary>The beam: hot sunflower yellow; the rim of what it vaporises white.</summary>
        public static readonly Color FlowerBeam = new(4.6f, 3.6f, 0.6f, 1f), FlowerCore = new(5f, 4.8f, 3.2f, 1f), FlowerVoid = new(3.4f, 3.4f, 3.2f, 1f);

        // ---------------------------------------------------------------- Cactus
        public const float CactusTime = 3f;                  // held this long once grown...
        public const float CactusGround = 3f;                // ...then put down (or dropped, if knocked over) for this long
        public const float CactusDamage = 15f;
        public const float CactusKnock = 12f, CactusKnockUp = 6f, CactusRagdoll = 1f;
        public const float CactusEvery = 2f;                 // the same person isn't pricked again sooner than this
        public const float CactusSpeed = 1.5f;               // holding it: this much faster
        public const float CactusReach = 0.55f;              // touching the plant: within this of its middle
        public const float BodyTouch = 0.85f;                // touching the caster: hips this close (sideways)
        public const int CactusSpikes = 26;                  // spikes round the body (the user's one spike, copied)

        // ---------------------------------------------------------------- Fly Trap
        public const float TrapTime = 5f;                    // held this long at most...
        public const float TrapFlight = 2f;                  // ...thrown, it lasts this long (longer if it eats someone)
        public const float TrapHold = 2f;                    // whoever it bites is inside it this long
        public const float TrapDamage = 15f;
        public const float TrapReach = 0.6f;                 // its mouth bites what comes this close
        public const float TrapThrowSpeed = 25.5f, TrapThrowUp = 4f;
        public const float TrapSpit = 6f, TrapSpitUp = 5f, TrapSpitRagdoll = 1f; // let go of: spat out

        // ---------------------------------------------------------------- Pine Tree
        public const float PineGrow = 6f;                    // grown, the sapling grows slowly this long...
        public const float PineShake = 1f;                   // ...shaking hard over the last of it...
        public const float PineBig = 6f;                     // ...then it's suddenly this many times its size (the pot grows too)
        public const float PineSaplingGrow = 1.35f;          // (the slow growth, up to this size, before it shakes)
        public const float PinePop = 0.12f;                  // the sudden growth takes this long (overshooting a little)
        public const float PineStay = 2.5f;                  // big, it stands this long, then dithers away
        public const float PineDamage = 30f;                 // anyone in the big tree when it pops (the caster too)
        public const float PineKnock = 13f, PineKnockUp = 9f, PineRagdoll = 1.5f;
        public const float PineThrowSpeed = 20f, PineThrowUp = 5f;
        /// <summary>When it pops, counted from the cast.</summary>
        public const float PineBurstAt = PotGrow + PineGrow;

        /// <summary>
        /// How big the tree is against its pot at <paramref name="t"/> s from the cast: 1 (its model's size) growing slowly
        /// to <see cref="PineSaplingGrow"/> before it pops. (The pop itself grows the pot and tree together: <see cref="PinePotScale"/>.)
        /// </summary>
        public static float PineSize(float t)
        {
            float g = t - PotGrow;
            return g <= 0f ? 1f : Mathf.Lerp(1f, PineSaplingGrow, g / PineGrow);
        }

        /// <summary>The pot (and the tree in it) when it's popped: the tree is then <see cref="PineBig"/> times its model's size.</summary>
        public const float PinePotBig = PineBig / PineSaplingGrow;

        /// <summary>
        /// How big the whole pot (and the tree in it) is at <paramref name="t"/> s from the cast: 1, then on the pop
        /// shooting out to <see cref="PinePotBig"/> (a little past it, settling back).
        /// </summary>
        public static float PinePotScale(float t)
        {
            float u = Mathf.Clamp01((t - PineBurstAt) / PinePop);
            if (u <= 0f) return 1f;
            float over = Mathf.Sin(u * Mathf.PI) * 0.12f;
            return Mathf.Lerp(1f, PinePotBig, 1f - (1f - u) * (1f - u)) + over * PinePotBig;
        }

        // thrown, it breaks the ground it hits (a bite of it, not all the way through) and bounces off
        public const float PineSmash = 0.5f;                 // the bite's radius: this times how big the pot is now...
        public const float PineSmashMax = 2.5f;              // ...up to this
        public const float PineSmashSpeed = 4f;              // hitting slower than this breaks nothing
        public const float PineSmashEvery = 0.15f;           // (at most this often)
        public const float PineBounce = 0.5f;                // how much of its speed it bounces back with

        /// <summary>How hard it shakes at <paramref name="t"/> s from the cast: 0, building to 1 just before it pops.</summary>
        public static float PineShaking(float t)
        {
            float g = t - PotGrow - (PineGrow - PineShake);
            return g <= 0f || t >= PineBurstAt ? 0f : Mathf.Clamp01(g / PineShake);
        }

        /// <summary>When a pot of this kind (not thrown) is over, counted from the cast.</summary>
        public static float PotHeldFor(byte kind) => kind switch
        {
            PotSunflower => FlowerLaserEnd,
            PotCactus => PotGrow + CactusTime,
            PotPine => PineBurstAt + PineStay,
            _ => PotGrow + TrapTime,
        };

        // ---------------------------------------------------------------- Smallify Hex
        public const byte SmallifyHex = 4;
        public const float SmallTime = 5f;                   // small this long
        public const float SmallKnockDamage = 5f;            // knocked down (ragdolled) while small: this much more
        public const float SmallSize = 0.5f;                 // ...at this size...
        public const float SmallSpeed = 0.5f;                // ...and this speed
        public const float SmallShrink = 0.25f;              // shrinking (and growing back) takes this long
        public const float SmallBumpReach = 0.75f;           // anyone this close (sideways) walking into them knocks them over
        public const float SmallBumpEvery = 1.5f;
        public const float SmallKnock = 6f, SmallKnockUp = 4f, SmallRagdoll = 1f;
        public static readonly Color HexYellow = new(3.4f, 2.9f, 0.3f, 1f);

        /// <summary>How small a body is right now: 1 its usual size .. <see cref="SmallSize"/>, eased (every peer works it out alike).</summary>
        public static float SmallScale(Combatant c)
        {
            if (c == null || !c.IsSpawned) return 1f;
            var st = c.Status.Value;
            if (st.SmallUntil <= 0) return 1f;
            double now = WorldNet.Now;
            float inT = Mathf.Clamp01((float)(now - st.SmallFrom) / SmallShrink), outT = Mathf.Clamp01((float)(st.SmallUntil - now) / SmallShrink);
            float k = Mathf.Min(inT, outT);
            k = k * k * (3f - 2f * k);
            return Mathf.Lerp(1f, SmallSize, k);
        }
    }
}
