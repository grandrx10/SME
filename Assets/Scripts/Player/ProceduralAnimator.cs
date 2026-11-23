using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Fully procedural animation for the "fellas" rig: velocity-driven gait in any direction, idle, airborne pose,
    /// look pitch through the spine/head, and two-bone IK arms that play the spell gestures (see <see cref="SpellPose"/>):
    /// heaving a wall up, ripping a rock out and throwing it, buckling the ground and clapping it shut.
    /// The same pose is replicated, so everyone sees the same gestures.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ProceduralAnimator : MonoBehaviour
    {
        [Header("Free arm")]
        public float freeArmSwing = 0.18f;

        [Header("Gait")]
        public float groundY = -0.92f;      // ankle height below the hips when standing (model space, metres)
        public float stanceWidth = 0.13f;
        public float maxStride = 0.42f;
        public float stepHeight = 0.12f;

        public Vector3 EyePosition { get; private set; }
        float _eyeHeight = 0.865f;

        Combatant _pc;
        Transform _model, _rigRoot, _pelvis, _chest, _head;
        Limb _armL, _armR, _legL, _legR;
        readonly List<(Transform t, Quaternion r, Vector3 p)> _bind = new();
        bool _initialised;

        float _phase, _lean, _sideLean, _airW, _moveW, _poseW, _twist, _kneelW, _crouchW;
        /// <summary>Degrees the body leans forward while crouched.</summary>
        const float CrouchLean = 14f;
        Vector3 _footL, _footR;
        SpellPose _lastPose;
        Key _lastKey;

        class Limb
        {
            public Transform root, upper, lower, end, tip;
            public float side;
        }

        // ---------------- spell gesture keyframes ----------------
        // Wrist offsets from the chest in body space (x right, y up, z forward) and the way the fingers point.
        struct Key
        {
            public float t;
            public Vector3 r, rd, l, ld;
            public float twist; // degrees the chest turns (positive = right shoulder back)
            public float drop;  // metres the hips sink (crouch)
            public float lean;  // degrees the body leans forward
            public Key(float t, Vector3 r, Vector3 rd, Vector3 l, Vector3 ld, float twist = 0f, float drop = 0f, float lean = 0f)
            { this.t = t; this.r = r; this.rd = rd; this.l = l; this.ld = ld; this.twist = twist; this.drop = drop; this.lean = lean; }
        }

        static readonly Vector3 Fwd = new(0f, 0f, 1f);

        // Nomadic Dash: arms swept back, leaning into it
        static readonly Key[] DashKeys =
        {
            new(0f, new(0.25f, -0.32f, -0.22f), new(0f, -0.6f, -1f), M(new(0.25f, -0.32f, -0.22f)), new(0f, -0.6f, -1f), 0f, 0.12f, 20f),
        };
        // Shove: palms drawn in at the chest... then thrust out
        static readonly Key[] ShoveHoldKeys =
        {
            new(0f, new(0.2f, -0.04f, 0.14f), new(0f, 0.35f, 1f), M(new(0.2f, -0.04f, 0.14f)), new(0f, 0.35f, 1f), 0f, 0.08f, -4f),
        };
        static readonly Key[] ShoveCastKeys =
        {
            new(0.00f, new(0.2f, -0.04f, 0.14f), new(0f, 0.35f, 1f), M(new(0.2f, -0.04f, 0.14f)), new(0f, 0.35f, 1f), 0f, 0.08f, -4f),
            new(0.09f, new(0.17f, 0.02f, 0.66f), new(0f, 0.6f, 1f), M(new(0.17f, 0.02f, 0.66f)), new(0f, 0.6f, 1f), 0f, 0.05f, 12f),
            new(0.35f, new(0.17f, 0.0f, 0.62f), new(0f, 0.5f, 1f), M(new(0.17f, 0.0f, 0.62f)), new(0f, 0.5f, 1f), 0f, 0.03f, 8f),
        };
        // Tornado: right hand up, stirring the air... both arms up, then flung forward
        static readonly Key[] TornadoHoldKeys =
        {
            new(0f, new(0.18f, 0.5f, 0.22f), new(0f, 1f, 0.2f), new(-0.28f, -0.2f, 0.3f), new(0f, -0.4f, 1f), -10f),
        };
        static readonly Key[] TornadoCastKeys =
        {
            new(0.00f, new(0.18f, 0.5f, 0.22f), new(0f, 1f, 0.2f), new(-0.28f, -0.2f, 0.3f), new(0f, -0.4f, 1f), -10f),
            new(0.25f, new(0.22f, 0.52f, 0.18f), new(0f, 1f, 0.1f), M(new(0.22f, 0.52f, 0.18f)), new(0f, 1f, 0.1f), 0f, 0f, -6f),
            new(0.45f, new(0.18f, 0.06f, 0.62f), Fwd, M(new(0.18f, 0.06f, 0.62f)), Fwd, 0f, 0.05f, 12f),
            new(0.60f, new(0.18f, 0.02f, 0.58f), Fwd, M(new(0.18f, 0.02f, 0.58f)), Fwd, 0f, 0.03f, 8f),
        };
        // Mana Missiles: an orb gathers in the right hand, then is flung at the target
        static readonly Key[] MissileKeys =
        {
            new(0.00f, new(0.22f, -0.05f, 0.42f), new(0f, 0.4f, 1f), new(-0.02f, -0.02f, 0.36f), new(0.6f, 0.2f, 1f)),
            new(0.75f, new(0.24f, 0.02f, 0.46f), new(0f, 0.5f, 1f), new(0.02f, 0.04f, 0.4f), new(0.6f, 0.3f, 1f)),
            new(0.88f, new(0.2f, 0.16f, 0.64f), new(0f, 0.4f, 1f), new(-0.3f, -0.2f, 0.1f), new(0f, -0.8f, 0.4f), -15f, 0f, 8f),
        };
        // Magical Barrier: drop to a crouch, right hand flat on the floor, left arm out
        static readonly Key[] BarrierKeys =
        {
            new(0.00f, new(0.22f, -0.2f, 0.35f), new(0f, -0.8f, 0.6f), new(-0.3f, -0.15f, 0.25f), new(-0.6f, -0.2f, 0.6f), 0f, 0.05f, 4f),
            new(0.20f, new(0.24f, -0.78f, 0.38f), new(0f, -1f, 0.35f), new(-0.42f, -0.08f, 0.18f), new(-1f, 0.1f, 0.4f), 0f, 0.45f, 18f),
        };
        // Mana Siphon: glowing right fist pulled back while charging... then the punch
        static readonly Key[] SiphonKeys =
        {
            new(0f, new(0.26f, -0.06f, -0.08f), Fwd, new(-0.14f, 0.04f, 0.38f), new(0.3f, 0.6f, 1f), 18f, 0.1f, 16f),
        };
        static readonly Key[] SiphonPunchKeys =
        {
            new(0.00f, new(0.26f, -0.06f, -0.08f), Fwd, new(-0.14f, 0.04f, 0.38f), new(0.3f, 0.6f, 1f), 18f, 0.1f, 16f),
            new(0.08f, new(0.1f, 0.08f, 0.7f), Fwd, new(-0.3f, -0.15f, 0.05f), new(0f, -0.7f, 0.5f), -22f, 0.08f, 14f),
            new(0.25f, new(0.1f, 0.06f, 0.66f), Fwd, new(-0.3f, -0.15f, 0.05f), new(0f, -0.7f, 0.5f), -18f, 0.06f, 10f),
        };

        static Vector3 M(Vector3 v) => new(-v.x, v.y, v.z); // mirror for the left hand

        static readonly Vector3 PalmsDown = new(0f, -0.35f, 1f);
        static readonly Key[] HoldWallKeys =
        {
            new(0f, new(0.22f, -0.15f, 0.42f), PalmsDown, M(new(0.22f, -0.15f, 0.42f)), PalmsDown),
        };
        static readonly Key[] CastWallKeys =
        {
            new(0.00f, new(0.22f, -0.15f, 0.42f), PalmsDown, M(new(0.22f, -0.15f, 0.42f)), PalmsDown),
            new(0.10f, new(0.24f, -0.4f, 0.38f), new(0f, -0.7f, 1f), M(new(0.24f, -0.4f, 0.38f)), new(0f, -0.7f, 1f)),
            new(0.30f, new(0.27f, 0.4f, 0.38f), new(0f, 1f, 0.45f), M(new(0.27f, 0.4f, 0.38f)), new(0f, 1f, 0.45f)),
            new(0.50f, new(0.27f, 0.42f, 0.36f), new(0f, 1f, 0.4f), M(new(0.27f, 0.42f, 0.36f)), new(0f, 1f, 0.4f)),
        };
        // the rock is held on the left (camera is over the right shoulder), so the LEFT hand rips it out and throws it
        static readonly Key[] GatherKeys = Mirror(new Key[]
        {
            new(0.00f, new(0.2f, -0.1f, 0.35f), new(0f, -0.3f, 1f), new(-0.25f, -0.3f, 0.2f), new(0f, -0.8f, 0.5f)),
            new(0.15f, new(0.18f, -0.58f, 0.5f), new(0f, -1f, 0.35f), new(-0.22f, -0.35f, 0.3f), new(0f, -0.8f, 0.6f)),
            new(0.30f, new(0.25f, -0.1f, 0.46f), new(0f, 0.4f, 1f), new(-0.24f, -0.3f, 0.28f), new(0f, -0.6f, 0.8f)),
            new(0.55f, new(0.3f, 0.06f, 0.42f), new(0f, 0.55f, 1f), new(-0.25f, -0.25f, 0.3f), new(0f, -0.5f, 1f)),
            new(0.66f, new(0.36f, 0.36f, -0.08f), new(0f, 1f, -0.3f), new(-0.2f, -0.15f, 0.45f), new(0.2f, 0f, 1f), 28f),
            new(0.78f, new(0.1f, 0.06f, 0.64f), new(-0.1f, -0.2f, 1f), new(-0.3f, -0.25f, 0.0f), new(0f, -0.8f, 0.2f), -22f),
            new(0.95f, new(0.15f, -0.12f, 0.5f), new(0f, -0.5f, 1f), new(-0.26f, -0.3f, 0.15f), new(0f, -0.8f, 0.4f), -8f),
        });

        /// <summary>Swaps the hands of a gesture (and turns the twist the other way).</summary>
        static Key[] Mirror(Key[] keys)
        {
            var m = new Key[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                var k = keys[i];
                m[i] = new Key(k.t, M(k.l), M(k.ld), M(k.r), M(k.rd), -k.twist, k.drop, k.lean);
            }
            return m;
        }
        static readonly Vector3 PalmsIn = new(0f, 0f, 1f);
        static readonly Key[] HoldFoldKeys =
        {
            new(0f, new(0.55f, -0.05f, 0.33f), PalmsIn, M(new(0.55f, -0.05f, 0.33f)), PalmsIn),
        };
        static readonly Key[] CastFoldKeys =
        {
            new(0.00f, new(0.55f, -0.05f, 0.33f), PalmsIn, M(new(0.55f, -0.05f, 0.33f)), PalmsIn),
            new(0.14f, new(0.6f, -0.3f, 0.3f), new(0f, -0.6f, 1f), M(new(0.6f, -0.3f, 0.3f)), new(0f, -0.6f, 1f)),
            new(0.24f, new(0.05f, 0.16f, 0.5f), new(-0.25f, 0.45f, 1f), M(new(0.05f, 0.16f, 0.5f)), new(0.25f, 0.45f, 1f)),
            new(0.40f, new(0.05f, 0.15f, 0.5f), new(-0.25f, 0.45f, 1f), M(new(0.05f, 0.15f, 0.5f)), new(0.25f, 0.45f, 1f)),
        };

        // Ball of Flame: right hand raised over the head while it forms... then hurled forward
        static readonly Key[] FlameChargeKeys =
        {
            new(0.00f, new(0.14f, 0.3f, 0.2f), new(0f, 1f, 0.1f), new(-0.26f, -0.2f, 0.3f), new(0f, -0.5f, 1f), 10f),
            // reaching past the arm's length so the IK holds it dead straight, hand pointing up
            new(0.15f, new(0.16f, 0.95f, 0.02f), new(0f, 1f, 0f), new(-0.3f, -0.1f, 0.32f), new(0.2f, 0f, 1f), 12f, 0f, -4f),
            new(0.48f, new(0.16f, 0.98f, 0.0f), new(0f, 1f, 0f), new(-0.3f, -0.1f, 0.34f), new(0.2f, 0f, 1f), 14f, 0f, -5f),
            new(0.60f, new(0.12f, 0.1f, 0.66f), new(0f, -0.2f, 1f), new(-0.3f, -0.25f, 0.0f), new(0f, -0.8f, 0.2f), -20f, 0.05f, 14f),
            new(0.80f, new(0.15f, -0.05f, 0.52f), new(0f, -0.4f, 1f), new(-0.26f, -0.3f, 0.12f), new(0f, -0.8f, 0.4f), -8f),
        };
        // Hellfire Wall: right arm sweeping low, pointing the line along the ground
        static readonly Key[] HellfireKeys =
        {
            new(0.00f, new(0.2f, -0.1f, 0.4f), new(0f, -0.4f, 1f), new(-0.26f, -0.2f, 0.25f), new(0f, -0.6f, 0.6f)),
            new(0.10f, new(0.18f, -0.3f, 0.62f), new(0f, -0.6f, 1f), new(-0.3f, -0.15f, 0.15f), new(-0.4f, -0.3f, 0.6f), -10f, 0.1f, 12f),
            new(0.50f, new(0.22f, -0.28f, 0.64f), new(0f, -0.55f, 1f), new(-0.3f, -0.15f, 0.15f), new(-0.4f, -0.3f, 0.6f), -14f, 0.1f, 12f),
        };
        // Explosive Escape: glowing hands drawn back... CLAP out front
        static readonly Key[] FlameHandsKeys =
        {
            new(0.00f, new(0.32f, 0.02f, 0.12f), new(0.3f, 0.2f, 1f), M(new(0.32f, 0.02f, 0.12f)), new(-0.3f, 0.2f, 1f), 0f, 0.08f, 4f),
            new(0.24f, new(0.42f, 0.06f, 0.18f), new(-0.4f, 0.2f, 1f), M(new(0.42f, 0.06f, 0.18f)), new(0.4f, 0.2f, 1f), 0f, 0.1f, 6f),
            new(0.32f, new(0.03f, 0.06f, 0.56f), new(-0.4f, 0.3f, 1f), M(new(0.03f, 0.06f, 0.56f)), new(0.4f, 0.3f, 1f), 0f, 0.05f, 14f),
            new(0.50f, new(0.04f, 0.04f, 0.52f), new(-0.4f, 0.3f, 1f), M(new(0.04f, 0.04f, 0.52f)), new(0.4f, 0.3f, 1f), 0f, 0.03f, 8f),
        };
        // Arcane Judgment: both hands raised to the sky... then brought down
        static readonly Key[] JudgmentHoldKeys =
        {
            new(0f, new(0.24f, 0.5f, 0.2f), new(0f, 1f, 0.3f), M(new(0.24f, 0.5f, 0.2f)), new(0f, 1f, 0.3f), 0f, 0f, -8f),
        };
        static readonly Key[] JudgmentCastKeys =
        {
            new(0.00f, new(0.24f, 0.5f, 0.2f), new(0f, 1f, 0.3f), M(new(0.24f, 0.5f, 0.2f)), new(0f, 1f, 0.3f), 0f, 0f, -8f),
            new(0.15f, new(0.2f, 0.62f, 0.12f), new(0f, 1f, 0.1f), M(new(0.2f, 0.62f, 0.12f)), new(0f, 1f, 0.1f), 0f, 0f, -12f),
            new(0.80f, new(0.2f, 0.62f, 0.12f), new(0f, 1f, 0.1f), M(new(0.2f, 0.62f, 0.12f)), new(0f, 1f, 0.1f), 0f, 0f, -12f),
            new(0.95f, new(0.22f, -0.2f, 0.5f), new(0f, -0.6f, 1f), M(new(0.22f, -0.2f, 0.5f)), new(0f, -0.6f, 1f), 0f, 0.12f, 16f),
        };
        // Tug: right arm reaching out, fingers open... then yanked back to the chest
        static readonly Key[] TugHoldKeys =
        {
            new(0f, new(0.18f, 0.08f, 0.66f), Fwd, new(-0.26f, -0.2f, 0.25f), new(0f, -0.6f, 0.6f), -12f, 0f, 6f),
        };
        static readonly Key[] TugCastKeys =
        {
            new(0.00f, new(0.18f, 0.08f, 0.66f), Fwd, new(-0.26f, -0.2f, 0.25f), new(0f, -0.6f, 0.6f), -12f, 0f, 6f),
            new(0.12f, new(0.3f, 0.02f, -0.1f), new(0f, 0.3f, 1f), new(-0.26f, -0.2f, 0.3f), new(0f, -0.6f, 0.6f), 20f, 0.06f, -10f),
            new(0.40f, new(0.28f, 0.0f, 0.0f), new(0f, 0.3f, 1f), new(-0.26f, -0.2f, 0.28f), new(0f, -0.6f, 0.6f), 14f, 0.04f, -6f),
        };
        // Summon Platform: crouch and slam both palms down, then rise with it
        static readonly Key[] PlatformKeys =
        {
            new(0.00f, new(0.22f, -0.1f, 0.4f), PalmsDown, M(new(0.22f, -0.1f, 0.4f)), PalmsDown, 0f, 0.1f, 6f),
            new(0.10f, new(0.24f, -0.55f, 0.36f), new(0f, -1f, 0.3f), M(new(0.24f, -0.55f, 0.36f)), new(0f, -1f, 0.3f), 0f, 0.35f, 18f),
            new(0.45f, new(0.3f, -0.3f, 0.2f), new(0.3f, -1f, 0.2f), M(new(0.3f, -0.3f, 0.2f)), new(-0.3f, -1f, 0.2f), 0f, 0.15f, 8f),
        };

        // The Fool: a flick of the right hand, as if showing a card
        static readonly Key[] FoolKeys =
        {
            new(0.00f, new(0.2f, -0.1f, 0.3f), new(0f, -0.3f, 1f), new(-0.26f, -0.2f, 0.25f), new(0f, -0.6f, 0.6f)),
            new(0.12f, new(0.22f, 0.18f, 0.5f), new(0f, 1f, 0.3f), new(-0.26f, -0.2f, 0.25f), new(0f, -0.6f, 0.6f), -6f),
            new(0.30f, new(0.24f, 0.2f, 0.46f), new(0f, 1f, 0.4f), new(-0.26f, -0.2f, 0.25f), new(0f, -0.6f, 0.6f), -6f),
        };
        // The Magician: one hand raised to the sky, the other pointing to the ground
        static readonly Key[] MagicianKeys =
        {
            new(0.00f, new(0.2f, 0.0f, 0.3f), new(0f, 0.5f, 1f), new(-0.24f, -0.2f, 0.25f), new(0f, -0.6f, 0.6f)),
            new(0.15f, new(0.16f, 0.95f, 0.05f), new(0f, 1f, 0f), new(-0.28f, -0.6f, 0.12f), new(0f, -1f, 0.1f), 0f, 0.05f, -4f),
            new(0.45f, new(0.16f, 0.95f, 0.05f), new(0f, 1f, 0f), new(-0.28f, -0.6f, 0.12f), new(0f, -1f, 0.1f), 0f, 0.05f, -4f),
        };
        // The High Priestess: both hands holding the sun up over the head
        static readonly Key[] SunRaiseKeys =
        {
            new(0.00f, new(0.2f, 0.1f, 0.3f), new(0f, 0.8f, 0.6f), M(new(0.2f, 0.1f, 0.3f)), new(0f, 0.8f, 0.6f)),
            new(0.20f, new(0.14f, 0.62f, 0.1f), new(-0.3f, 1f, 0f), M(new(0.14f, 0.62f, 0.1f)), new(0.3f, 1f, 0f), 0f, 0f, -8f),
            new(1.00f, new(0.14f, 0.66f, 0.08f), new(-0.3f, 1f, 0f), M(new(0.14f, 0.66f, 0.08f)), new(0.3f, 1f, 0f), 0f, 0f, -10f),
            new(1.10f, new(0.3f, 0.5f, 0.2f), new(0.6f, 1f, 0f), M(new(0.3f, 0.5f, 0.2f)), new(-0.6f, 1f, 0f), 0f, 0f, -4f),
        };
        // The Empress / The Emperor: hands clap together in front of the chest and press there (the startup),
        // then fling open as the domain bursts out
        static readonly Key[] PrayerKeys =
        {
            new(0.00f, new(0.3f, -0.05f, 0.25f), new(-0.3f, 0.4f, 1f), M(new(0.3f, -0.05f, 0.25f)), new(0.3f, 0.4f, 1f)),
            new(0.14f, new(0.03f, 0.1f, 0.3f), new(-0.6f, 1f, 0.3f), M(new(0.03f, 0.1f, 0.3f)), new(0.6f, 1f, 0.3f), 0f, 0.06f, 6f),
            new(0.48f, new(0.03f, 0.06f, 0.34f), new(-0.6f, 1f, 0.3f), M(new(0.03f, 0.06f, 0.34f)), new(0.6f, 1f, 0.3f), 0f, 0.08f, 8f),
            new(0.58f, new(0.42f, 0.0f, 0.3f), new(0.5f, 0.2f, 1f), M(new(0.42f, 0.0f, 0.3f)), new(-0.5f, 0.2f, 1f), 0f, 0.02f, -6f),
        };

        // Water of Life: hands cupped in front, holding the water... then lifted out to set it floating
        static readonly Key[] WaterHoldKeys =
        {
            new(0f, new(0.12f, -0.12f, 0.36f), new(0.3f, 0.6f, 1f), M(new(0.12f, -0.12f, 0.36f)), new(-0.3f, 0.6f, 1f), 0f, 0.03f, 4f),
        };
        static readonly Key[] WaterCastKeys =
        {
            new(0.00f, new(0.12f, -0.12f, 0.36f), new(0.3f, 0.6f, 1f), M(new(0.12f, -0.12f, 0.36f)), new(-0.3f, 0.6f, 1f), 0f, 0.03f, 4f),
            new(0.18f, new(0.16f, 0.06f, 0.62f), new(0.2f, 0.8f, 1f), M(new(0.16f, 0.06f, 0.62f)), new(-0.2f, 0.8f, 1f), 0f, 0f, 8f),
            new(0.40f, new(0.2f, 0.04f, 0.6f), new(0.3f, 0.7f, 1f), M(new(0.2f, 0.04f, 0.6f)), new(-0.3f, 0.7f, 1f), 0f, 0f, 6f),
        };
        // Bubble Away: hands come together in front of the chest, and stay cupped there while the bubble carries you
        static readonly Key[] BubbleKeys =
        {
            new(0.00f, new(0.32f, -0.05f, 0.25f), new(-0.3f, 0.4f, 1f), M(new(0.32f, -0.05f, 0.25f)), new(0.3f, 0.4f, 1f)),
            new(0.25f, new(0.05f, 0.04f, 0.34f), new(-0.5f, 0.8f, 0.6f), M(new(0.05f, 0.04f, 0.34f)), new(0.5f, 0.8f, 0.6f), 0f, 0.06f, 2f),
            new(2.30f, new(0.05f, 0.06f, 0.34f), new(-0.5f, 0.8f, 0.6f), M(new(0.05f, 0.06f, 0.34f)), new(0.5f, 0.8f, 0.6f), 0f, 0.06f, 2f),
        };
        // Tsunami: arms low and ready... drawn back behind the hips, then shoved forward
        static readonly Key[] TsunamiHoldKeys =
        {
            new(0f, new(0.3f, -0.3f, 0.18f), new(0f, -0.2f, 1f), M(new(0.3f, -0.3f, 0.18f)), new(0f, -0.2f, 1f), 0f, 0.06f, 2f),
        };
        static readonly Key[] TsunamiCastKeys =
        {
            new(0.00f, new(0.3f, -0.3f, 0.18f), new(0f, -0.2f, 1f), M(new(0.3f, -0.3f, 0.18f)), new(0f, -0.2f, 1f), 0f, 0.06f, 2f),
            new(0.25f, new(0.34f, -0.28f, -0.28f), new(0f, -0.4f, 1f), M(new(0.34f, -0.28f, -0.28f)), new(0f, -0.4f, 1f), 0f, 0.14f, -8f),
            new(0.45f, new(0.2f, 0.02f, 0.68f), new(0f, 0.5f, 1f), M(new(0.2f, 0.02f, 0.68f)), new(0f, 0.5f, 1f), 0f, 0.08f, 16f),
            new(0.65f, new(0.2f, 0.0f, 0.64f), new(0f, 0.4f, 1f), M(new(0.2f, 0.0f, 0.64f)), new(0f, 0.4f, 1f), 0f, 0.06f, 10f),
        };

        // Warp Self: hands held close together... pulled apart (the rift opens between them), then folding in on yourself
        static readonly Key[] WarpHoldKeys =
        {
            new(0f, new(0.06f, 0.02f, 0.34f), new(-0.4f, 0.6f, 1f), M(new(0.06f, 0.02f, 0.34f)), new(0.4f, 0.6f, 1f)),
        };
        static readonly Key[] WarpCastKeys =
        {
            new(0.00f, new(0.06f, 0.02f, 0.34f), new(-0.4f, 0.6f, 1f), M(new(0.06f, 0.02f, 0.34f)), new(0.4f, 0.6f, 1f)),
            new(0.12f, new(0.02f, 0.05f, 0.36f), new(-0.6f, 0.8f, 0.6f), M(new(0.02f, 0.05f, 0.36f)), new(0.6f, 0.8f, 0.6f), 0f, 0.03f, 2f),
            new(0.40f, new(0.34f, 0.05f, 0.34f), new(-1f, 0.3f, 0.4f), M(new(0.34f, 0.05f, 0.34f)), new(1f, 0.3f, 0.4f), 0f, 0.05f, 4f),
            new(0.60f, new(0.05f, 0.0f, 0.16f), new(-0.6f, 0.5f, 0.6f), M(new(0.05f, 0.0f, 0.16f)), new(0.6f, 0.5f, 0.6f), 0f, 0.2f, 12f),
            new(0.80f, new(0.3f, -0.1f, 0.3f), new(0.3f, 0.2f, 1f), M(new(0.3f, -0.1f, 0.3f)), new(-0.3f, 0.2f, 1f), 0f, 0.05f, 2f),
        };
        // Warp Space: right hand out, palm forward, framing where it'll open... a sweep, and it opens
        static readonly Key[] PortalHoldKeys =
        {
            new(0f, new(0.18f, 0.1f, 0.62f), new(0f, 1f, 0.15f), new(-0.26f, -0.2f, 0.25f), new(0f, -0.6f, 0.6f), -8f, 0f, 4f),
        };
        static readonly Key[] PortalCastKeys =
        {
            new(0.00f, new(0.18f, 0.1f, 0.62f), new(0f, 1f, 0.15f), new(-0.26f, -0.2f, 0.25f), new(0f, -0.6f, 0.6f), -8f, 0f, 4f),
            new(0.15f, new(0.42f, -0.18f, 0.55f), new(0.4f, 0.8f, 0.4f), new(-0.26f, -0.2f, 0.25f), new(0f, -0.6f, 0.6f), -14f, 0.03f, 6f),
            new(0.30f, new(0.42f, -0.16f, 0.52f), new(0.4f, 0.8f, 0.4f), new(-0.26f, -0.2f, 0.25f), new(0f, -0.6f, 0.6f), -12f, 0.02f, 4f),
        };
        // Arcane Swiftness: crouched low, gathering... then running, arms swept back
        static readonly Key[] SwiftKeys =
        {
            new(0.00f, new(0.25f, -0.45f, 0.32f), new(0f, -1f, 0.4f), M(new(0.25f, -0.45f, 0.32f)), new(0f, -1f, 0.4f), 0f, 0.35f, 22f),
            new(0.28f, new(0.25f, -0.5f, 0.3f), new(0f, -1f, 0.4f), M(new(0.25f, -0.5f, 0.3f)), new(0f, -1f, 0.4f), 0f, 0.38f, 24f),
            new(0.36f, new(0.25f, -0.32f, -0.22f), new(0f, -0.6f, -1f), M(new(0.25f, -0.32f, -0.22f)), new(0f, -0.6f, -1f), 0f, 0.12f, 24f),
            new(1.90f, new(0.25f, -0.32f, -0.22f), new(0f, -0.6f, -1f), M(new(0.25f, -0.32f, -0.22f)), new(0f, -0.6f, -1f), 0f, 0.12f, 24f),
        };

        // ---- Blade: built from BladeMotion (the same cut the sword follows), sampled densely so it flows ----
        // Severance: rushing in, both hands on the sword, held low on the left
        static readonly Key[] SeveranceDashKeys = BladeKeys(SpellPose.SeveranceDash, 0f);
        // ...then one diagonal cut, bottom left to top right, and its follow-through
        static readonly Key[] SeveranceSlashKeys = BladeKeys(SpellPose.SeveranceSlash, BladeMotion.Sweep + BladeMotion.Settle);
        // Flowing Cut: down on one knee, both hands on the sword low on the left, ready to draw
        static readonly Key[] FlowChargeKeys = BladeKeys(SpellPose.FlowCharge, 0f);
        // ...the same cut as it flashes along, then the right arm swings out, the sword pointing on to the right
        static readonly Key[] FlowStrikeKeys = BladeKeys(SpellPose.FlowStrike, BladeMotion.Sweep + BladeMotion.Settle);

        /// <summary>Keyframes every 1/60 s from 0 to <paramref name="until"/> (one key for a held pose).</summary>
        static Key[] BladeKeys(SpellPose pose, float until)
        {
            int n = Mathf.Max(1, Mathf.CeilToInt(until * 60f) + 1);
            var keys = new Key[n];
            for (int i = 0; i < n; i++)
            {
                float t = n == 1 ? 0f : until * i / (n - 1);
                var f = BladeMotion.Sample(pose, t);
                Vector3 fingers = BladeMotion.FingerDir(f.blade);
                keys[i] = new Key(t, f.hand, fingers, f.left, fingers, f.twist, f.drop, f.lean);
            }
            return keys;
        }

        // ---- Hammer: built from HammerMotion (the same swing the hammer follows), both hands on the handle ----
        // (built on first use, not in a static initializer: the hand spacing is read from the hammer prefab, and
        // Unity won't load resources while a MonoBehaviour is being constructed)
        static Key[] _hammerDrag, _hammerRise, _hammerLeap, _hammerSlam;
        // Rising Hammer: rushing in, the hammer dragged behind on the left...
        static Key[] HammerDragKeys => _hammerDrag ??= HammerKeys(SpellPose.HammerDrag, 0f);
        // ...then brought up from behind and swung upwards
        static Key[] HammerRiseKeys => _hammerRise ??= HammerKeys(SpellPose.HammerRise, HammerMotion.RiseSwing + HammerMotion.RiseSettle);
        // Descending Hammer: down on one knee, then in the air, the hammer up behind the head...
        static Key[] HammerLeapKeys => _hammerLeap ??= HammerKeys(SpellPose.HammerLeap, SpellPlans.HammerCrouch + 0.1f);
        // ...brought straight down in front on landing
        static Key[] HammerSlamKeys => _hammerSlam ??= HammerKeys(SpellPose.HammerSlam, HammerMotion.SlamSwing + HammerMotion.SlamSettle);

        /// <summary>Keyframes every 1/60 s from 0 to <paramref name="until"/> (one key for a held pose).</summary>
        /// <summary>
        /// The way a hand gripping a handle (along <paramref name="shaft"/>) points: straight on from the shoulder
        /// through the wrist, squared to the handle, so the wrist stays straight however the hammer is held.
        /// </summary>
        static Vector3 GripFingers(Vector3 hand, Vector3 shoulder, Vector3 shaft)
        {
            Vector3 d = Vector3.ProjectOnPlane(hand - shoulder, shaft);
            return d.sqrMagnitude > 1e-4f ? d.normalized : BladeMotion.FingerDir(shaft);
        }

        static Key[] HammerKeys(SpellPose pose, float until)
        {
            int n = Mathf.Max(1, Mathf.CeilToInt(until * 60f) + 1);
            var keys = new Key[n];
            for (int i = 0; i < n; i++)
            {
                float t = n == 1 ? 0f : until * i / (n - 1);
                var f = HammerMotion.Sample(pose, t);
                // each hand carries on the line of its own forearm, wrapped round the handle (no bent wrists)
                Vector3 r = GripFingers(f.Right, new Vector3(0.17f, 0.06f, -0.02f), f.shaft);
                Vector3 l = GripFingers(f.Left, new Vector3(-0.17f, 0.06f, -0.02f), f.shaft);
                keys[i] = new Key(t, f.Right, r, f.Left, l, f.twist, f.drop, f.lean);
            }
            return keys;
        }

        // ---- Attraction / Repulsion ----
        // Attraction: the left hand goes up, then is held out in front, palm guiding the ball (Repulsion: mirrored)
        static readonly Key[] AttractKeys =
        {
            new(0.00f, new(0.22f, -0.38f, 0.1f), new(0f, -1f, 0.3f), new(-0.22f, -0.2f, 0.25f), new(0f, -0.5f, 1f)),
            new(0.25f, new(0.22f, -0.38f, 0.1f), new(0f, -1f, 0.3f), new(-0.2f, 0.5f, 0.18f), new(0f, 1f, 0.2f), 10f, 0f, -4f),
            new(0.45f, new(0.22f, -0.36f, 0.12f), new(0f, -1f, 0.3f), new(-0.16f, 0.2f, 0.58f), new(0f, 0.3f, 1f), 6f, 0f, 4f),
            new(4.30f, new(0.22f, -0.36f, 0.12f), new(0f, -1f, 0.3f), new(-0.16f, 0.18f, 0.58f), new(0f, 0.3f, 1f), 6f, 0f, 4f),
        };
        static readonly Key[] RepelKeys = Mirror(AttractKeys);
        // the fused ball: both hands either side of it as it swells, then thrust out after it
        static readonly Key[] FuseKeys =
        {
            new(0.00f, new(0.2f, 0f, 0.4f), new(-0.5f, 0f, 1f), M(new(0.2f, 0f, 0.4f)), new(0.5f, 0f, 1f), 0f, 0.05f, 2f),
            new(0.30f, new(0.34f, 0.06f, 0.55f), new(-1f, 0f, 0.4f), M(new(0.34f, 0.06f, 0.55f)), new(1f, 0f, 0.4f), 0f, 0.1f, -2f),
            new(2.00f, new(0.36f, 0.08f, 0.5f), new(-1f, 0f, 0.4f), M(new(0.36f, 0.08f, 0.5f)), new(1f, 0f, 0.4f), 0f, 0.12f, -6f),
            new(2.12f, new(0.16f, 0.06f, 0.7f), Fwd, M(new(0.16f, 0.06f, 0.7f)), Fwd, 0f, 0.08f, 12f),
        };

        // ---- The Hierophant: both palms up and out, framing the wall... then pushed out as it appears ----
        static readonly Key[] HierophantHoldKeys =
        {
            new(0f, new(0.26f, 0.12f, 0.42f), new(0f, 1f, 0.25f), M(new(0.26f, 0.12f, 0.42f)), new(0f, 1f, 0.25f), 0f, 0f, 2f),
        };
        static readonly Key[] HierophantCastKeys =
        {
            new(0.00f, new(0.26f, 0.12f, 0.42f), new(0f, 1f, 0.25f), M(new(0.26f, 0.12f, 0.42f)), new(0f, 1f, 0.25f), 0f, 0f, 2f),
            new(0.18f, new(0.24f, 0.16f, 0.66f), new(0f, 1f, 0.15f), M(new(0.24f, 0.16f, 0.66f)), new(0f, 1f, 0.15f), 0f, 0.03f, 8f),
            new(0.50f, new(0.24f, 0.16f, 0.64f), new(0f, 1f, 0.15f), M(new(0.24f, 0.16f, 0.64f)), new(0f, 1f, 0.15f), 0f, 0.03f, 6f),
        };
        // ---- The Lovers: the right hand flung out and held out along the beam, the left hand on the heart ----
        static readonly Key[] LoversKeys =
        {
            new(0.00f, new(0.24f, 0.05f, 0.2f), new(0f, 0.5f, 1f), new(-0.1f, 0.12f, 0.22f), new(0.6f, 0.6f, 0.4f), -6f, 0f, 2f),
            new(0.15f, new(0.14f, 0.16f, 0.64f), Fwd, new(-0.06f, 0.1f, 0.24f), new(0.6f, 0.6f, 0.4f), 8f, 0f, 6f),
            new(0.40f, new(0.14f, 0.14f, 0.62f), Fwd, new(-0.06f, 0.1f, 0.24f), new(0.6f, 0.6f, 0.4f), 6f, 0f, 4f),
        };

        // ---- Rock Swarm: a scoop down... both hands flung up into the air as the rocks rise ----
        static readonly Key[] SwarmRaiseKeys =
        {
            new(0.00f, new(0.22f, -0.15f, 0.36f), PalmsDown, M(new(0.22f, -0.15f, 0.36f)), PalmsDown, 0f, 0.05f, 4f),
            new(0.08f, new(0.26f, -0.4f, 0.36f), new(0f, -1f, 0.4f), M(new(0.26f, -0.4f, 0.36f)), new(0f, -1f, 0.4f), 0f, 0.15f, 10f),
            new(0.28f, new(0.3f, 0.66f, 0.1f), new(0.2f, 1f, 0.05f), M(new(0.3f, 0.66f, 0.1f)), new(-0.2f, 1f, 0.05f), 0f, 0f, -10f),
            new(0.45f, new(0.3f, 0.64f, 0.12f), new(0.2f, 1f, 0.05f), M(new(0.3f, 0.64f, 0.12f)), new(-0.2f, 1f, 0.05f), 0f, 0f, -8f),
        };
        // ---- Ice Spike: down in a crouch, both palms flat on the ground... then standing back up ----
        static readonly Key[] IceCrouchKeys =
        {
            new(0.00f, new(0.22f, -0.2f, 0.4f), PalmsDown, M(new(0.22f, -0.2f, 0.4f)), PalmsDown, 0f, 0.1f, 8f),
            new(0.12f, new(0.22f, -0.82f, 0.44f), new(0f, -1f, 0.3f), M(new(0.22f, -0.82f, 0.44f)), new(0f, -1f, 0.3f), 0f, 0.5f, 22f),
            new(SpellPlans.IceCrouch, new(0.22f, -0.84f, 0.44f), new(0f, -1f, 0.3f), M(new(0.22f, -0.84f, 0.44f)), new(0f, -1f, 0.3f), 0f, 0.5f, 22f),
            new(SpellPlans.IceCrouch + 0.15f, new(0.24f, -0.2f, 0.42f), new(0f, -0.4f, 1f), M(new(0.24f, -0.2f, 0.42f)), new(0f, -0.4f, 1f), 0f, 0.08f, 6f),
        };

        // ---- The Hermit: down on one knee, both palms on the ground while the house goes up ----
        static readonly Key[] HermitKneelKeys =
        {
            new(0.00f, new(0.22f, -0.2f, 0.4f), PalmsDown, M(new(0.22f, -0.2f, 0.4f)), PalmsDown, 0f, 0.1f, 8f),
            new(0.12f, new(0.24f, -0.72f, 0.46f), new(0f, -1f, 0.3f), M(new(0.24f, -0.72f, 0.46f)), new(0f, -1f, 0.3f), 0f, 0.35f, 20f),
            new(SpellPlans.HermitKneel + SpellPlans.HermitBuild, new(0.24f, -0.74f, 0.46f), new(0f, -1f, 0.3f), M(new(0.24f, -0.74f, 0.46f)), new(0f, -1f, 0.3f), 0f, 0.35f, 20f),
        };
        // ---- Wheel of Fortune: a flick of the right hand up at the die ----
        static readonly Key[] WheelSpinKeys =
        {
            new(0.00f, new(0.22f, -0.1f, 0.3f), new(0f, 0.3f, 1f), new(-0.24f, -0.36f, 0.1f), new(0f, -1f, 0.3f)),
            new(0.10f, new(0.14f, 0.58f, 0.18f), new(0.2f, 1f, 0.2f), new(-0.24f, -0.36f, 0.1f), new(0f, -1f, 0.3f), -6f, 0f, -4f),
            new(0.30f, new(0.14f, 0.56f, 0.2f), new(0.3f, 1f, 0.3f), new(-0.24f, -0.36f, 0.1f), new(0f, -1f, 0.3f), -6f, 0f, -4f),
        };
        // ---- Justice: hands clasped together in front of the chest ----
        static readonly Key[] ClaspKeys =
        {
            new(0.00f, new(0.3f, -0.1f, 0.25f), new(-0.3f, 0.4f, 1f), M(new(0.3f, -0.1f, 0.25f)), new(0.3f, 0.4f, 1f)),
            new(0.15f, new(0.03f, 0.04f, 0.34f), new(-0.6f, 0.8f, 0.5f), M(new(0.03f, 0.04f, 0.34f)), new(0.6f, 0.8f, 0.5f), 0f, 0.04f, 4f),
            new(0.40f, new(0.03f, 0.05f, 0.34f), new(-0.6f, 0.8f, 0.5f), M(new(0.03f, 0.05f, 0.34f)), new(0.6f, 0.8f, 0.5f), 0f, 0.04f, 4f),
        };
        // ---- The Hanged Man: the left arm raised straight up ----
        static readonly Key[] LeftArmUpKeys =
        {
            new(0.00f, new(0.22f, -0.38f, 0.1f), new(0f, -1f, 0.3f), new(-0.24f, -0.1f, 0.2f), new(0f, 0.5f, 1f)),
            new(0.20f, new(0.22f, -0.38f, 0.1f), new(0f, -1f, 0.3f), new(-0.17f, 0.66f, 0.04f), Vector3.up, 6f),
            new(0.50f, new(0.22f, -0.38f, 0.1f), new(0f, -1f, 0.3f), new(-0.17f, 0.66f, 0.04f), Vector3.up, 6f),
        };
        // ---- Death: the head tipped back (arms falling open)... then forward again ----
        static readonly Key[] HeadBackKeys =
        {
            new(0.00f, new(0.24f, -0.35f, 0.1f), new(0f, -1f, 0.2f), M(new(0.24f, -0.35f, 0.1f)), new(0f, -1f, 0.2f)),
            new(0.15f, new(0.36f, -0.3f, 0.05f), new(0.4f, -1f, 0f), M(new(0.36f, -0.3f, 0.05f)), new(-0.4f, -1f, 0f), 0f, 0f, -18f),
            new(SpellPlans.DeathDelay, new(0.36f, -0.3f, 0.05f), new(0.4f, -1f, 0f), M(new(0.36f, -0.3f, 0.05f)), new(-0.4f, -1f, 0f), 0f, 0f, -18f),
            new(SpellPlans.DeathDelay + 0.15f, new(0.26f, -0.32f, 0.12f), new(0f, -1f, 0.3f), M(new(0.26f, -0.32f, 0.12f)), new(0f, -1f, 0.3f), 0f, 0.04f, 6f),
        };
        // ---- The Devil: the right hand raised ----
        static readonly Key[] RightHandUpKeys =
        {
            new(0.00f, new(0.24f, -0.1f, 0.2f), new(0f, 0.5f, 1f), new(-0.22f, -0.38f, 0.1f), new(0f, -1f, 0.3f)),
            new(0.15f, new(0.17f, 0.62f, 0.1f), new(0f, 1f, 0.2f), new(-0.22f, -0.38f, 0.1f), new(0f, -1f, 0.3f), -6f),
            new(0.40f, new(0.17f, 0.6f, 0.1f), new(0f, 1f, 0.2f), new(-0.22f, -0.38f, 0.1f), new(0f, -1f, 0.3f), -6f),
        };
        // ---- The Tower: kneeling, hands together in prayer ----
        static readonly Key[] TowerPrayKeys =
        {
            new(0.00f, new(0.2f, -0.05f, 0.25f), new(-0.3f, 0.8f, 0.5f), M(new(0.2f, -0.05f, 0.25f)), new(0.3f, 0.8f, 0.5f), 0f, 0.05f, 2f),
            new(0.15f, new(0.03f, 0.1f, 0.3f), new(-0.3f, 1f, 0.15f), M(new(0.03f, 0.1f, 0.3f)), new(0.3f, 1f, 0.15f), 0f, 0.1f, 6f),
            new(SpellPlans.TowerPray, new(0.03f, 0.12f, 0.3f), new(-0.3f, 1f, 0.15f), M(new(0.03f, 0.12f, 0.3f)), new(0.3f, 1f, 0.15f), 0f, 0.1f, 6f),
        };
        // ---- Undead Servitude / Summon Horde: crouched, hands held out over the ground ----
        static readonly Key[] NecroCrouchKeys =
        {
            new(0.00f, new(0.24f, -0.2f, 0.4f), PalmsDown, M(new(0.24f, -0.2f, 0.4f)), PalmsDown, 0f, 0.1f, 6f),
            new(0.15f, new(0.24f, -0.5f, 0.52f), new(0f, -0.7f, 1f), M(new(0.24f, -0.5f, 0.52f)), new(0f, -0.7f, 1f), 0f, 0.4f, 18f),
            new(SpellPlans.ReviveTime, new(0.26f, -0.48f, 0.54f), new(0f, -0.7f, 1f), M(new(0.26f, -0.48f, 0.54f)), new(0f, -0.7f, 1f), 0f, 0.4f, 18f),
        };
        // ---- Infectious Bite: crouched, arms back, ready to spring ----
        static readonly Key[] BiteCrouchKeys =
        {
            new(0.00f, new(0.25f, -0.3f, -0.15f), new(0f, -0.6f, -1f), M(new(0.25f, -0.3f, -0.15f)), new(0f, -0.6f, -1f), 0f, 0.35f, 24f),
        };
        // ---- Flame Thrower: both hands clasped, held straight out ----
        static readonly Key[] FlameSpewKeys =
        {
            new(0.00f, new(0.18f, 0f, 0.4f), Fwd, M(new(0.18f, 0f, 0.4f)), Fwd, 0f, 0.05f, 4f),
            new(0.12f, new(0.04f, 0.06f, 0.64f), Fwd, M(new(0.04f, 0.06f, 0.64f)), Fwd, 0f, 0.06f, 8f),
        };
        // ---- Grapple Hook: the left hand held out, pointing ----
        static readonly Key[] GrapplePointKeys =
        {
            new(0.00f, new(0.22f, -0.3f, 0.1f), new(0f, -1f, 0.3f), new(-0.2f, 0f, 0.35f), Fwd),
            new(SpellPlans.HookCast, new(0.22f, -0.3f, 0.1f), new(0f, -1f, 0.3f), new(-0.12f, 0.12f, 0.66f), Fwd, 10f, 0f, 4f),
        };

        // ---- Strength: both arms up, flexing ----
        static readonly Key[] FlexKeys =
        {
            new(0.00f, new(0.24f, -0.3f, 0.1f), new(0f, -1f, 0.2f), M(new(0.24f, -0.3f, 0.1f)), new(0f, -1f, 0.2f)),
            new(0.12f, new(0.36f, 0.22f, 0.02f), new(-0.4f, 1f, 0f), M(new(0.36f, 0.22f, 0.02f)), new(0.4f, 1f, 0f), 0f, 0.08f, -4f),
            new(SpellPlans.StrengthFlex + 0.1f, new(0.36f, 0.24f, 0.02f), new(-0.4f, 1f, 0f), M(new(0.36f, 0.24f, 0.02f)), new(0.4f, 1f, 0f), 0f, 0.08f, -4f),
        };
        // ---- Icy Breath: leaning forward, breathing out ----
        static readonly Key[] BreathLeanKeys =
        {
            new(0.00f, new(0.24f, -0.3f, 0.05f), new(0f, -1f, 0f), M(new(0.24f, -0.3f, 0.05f)), new(0f, -1f, 0f)),
            new(SpellPlans.BreathLean, new(0.3f, -0.34f, -0.1f), new(0.3f, -1f, -0.3f), M(new(0.3f, -0.34f, -0.1f)), new(-0.3f, -1f, -0.3f), 0f, 0.05f, 16f),
        };

        // ---- the guns: these give the crouch and the lean; both hands go wherever GunMotion holds the gun ----
        static readonly Vector3 GunR = new(0.14f, 0.1f, 0.38f), GunL = new(-0.02f, 0.12f, 0.55f);
        static readonly Key[] ShotgunAimKeys =
        {
            new(0.00f, GunR, Fwd, GunL, Fwd, 0f, 0.02f, 2f),
            new(SpellPlans.RachDelay, GunR, Fwd, GunL, Fwd, 0f, 0.06f, 6f),
            new(SpellPlans.RachDelay + 0.06f, GunR, Fwd, GunL, Fwd, 0f, 0.04f, -10f),
        };
        static readonly Key[] RifleKneelKeys =
        {
            new(0.00f, GunR, Fwd, GunL, Fwd, 0f, 0.05f, 4f),
            new(0.22f, GunR, Fwd, GunL, Fwd, 0f, SpellPlans.KneelDrop, 6f),
        };
        static readonly Key[] RifleFireKeys =
        {
            new(0.00f, GunR, Fwd, GunL, Fwd, 0f, SpellPlans.KneelDrop, 6f),
            new(SpellPlans.SebWindup, GunR, Fwd, GunL, Fwd, 0f, SpellPlans.KneelDrop, 8f),
            new(SpellPlans.SebWindup + 0.07f, GunR, Fwd, GunL, Fwd, 0f, SpellPlans.KneelDrop, -8f),
            new(SpellPlans.SebWindup + SpellPlans.SebRecover, GunR, Fwd, GunL, Fwd, 0f, 0f, 0f),
        };
        // ---- Undying Grasp: down on one knee, palms over the ground... then hauled up as the hands come ----
        static readonly Key[] GraspKneelKeys =
        {
            new(0.00f, new(0.24f, -0.2f, 0.4f), PalmsDown, M(new(0.24f, -0.2f, 0.4f)), PalmsDown, 0f, 0.1f, 6f),
            new(0.14f, new(0.26f, -0.62f, 0.5f), new(0f, -0.9f, 0.6f), M(new(0.26f, -0.62f, 0.5f)), new(0f, -0.9f, 0.6f), 0f, SpellPlans.KneelDrop, 18f),
            new(SpellPlans.GraspCast - 0.04f, new(0.26f, -0.64f, 0.5f), new(0f, -0.9f, 0.6f), M(new(0.26f, -0.64f, 0.5f)), new(0f, -0.9f, 0.6f), 0f, SpellPlans.KneelDrop, 18f),
            new(SpellPlans.GraspCast + 0.12f, new(0.3f, 0.12f, 0.45f), new(0f, 1f, 0.5f), M(new(0.3f, 0.12f, 0.45f)), new(0f, 1f, 0.5f), 0f, 0.08f, 2f),
        };

        // ---- Skinwalker: down on one knee over the corpse, hands held out to it ----
        static readonly Key[] SkinKneelKeys =
        {
            new(0.00f, new(0.24f, -0.15f, 0.35f), PalmsDown, M(new(0.24f, -0.15f, 0.35f)), PalmsDown, 0f, 0.1f, 6f),
            new(0.22f, new(0.2f, -0.34f, 0.55f), new(0f, -0.5f, 1f), M(new(0.2f, -0.34f, 0.55f)), new(0f, -0.5f, 1f), 0f, SpellPlans.KneelDrop, 20f),
            new(SpellPlans.SkinTime, new(0.24f, -0.24f, 0.56f), new(0f, -0.3f, 1f), M(new(0.24f, -0.24f, 0.56f)), new(0f, -0.3f, 1f), 0f, SpellPlans.KneelDrop, 16f),
        };
        // ---- Remote Flame Bomb: the right hand drawn back, then pressed out onto the spot ----
        static readonly Key[] BombPlaceKeys =
        {
            new(0.00f, new(0.24f, -0.05f, 0.25f), Fwd, new(-0.22f, -0.36f, 0.1f), new(0f, -1f, 0.3f)),
            new(0.12f, new(0.28f, 0.04f, 0.08f), Fwd, new(-0.22f, -0.36f, 0.1f), new(0f, -1f, 0.3f), 10f),
            new(0.26f, new(0.12f, 0.02f, 0.68f), new(0f, 0.5f, 1f), new(-0.24f, -0.34f, 0.05f), new(0f, -1f, 0.2f), -14f, 0.04f, 10f),
            new(0.40f, new(0.12f, 0f, 0.66f), new(0f, 0.5f, 1f), new(-0.24f, -0.34f, 0.05f), new(0f, -1f, 0.2f), -12f, 0.03f, 8f),
        };
        // ---- Flame Bomb Toss: both hands back by the hips, then thrown forward ----
        static readonly Key[] BombTossKeys =
        {
            new(0.00f, new(0.24f, -0.2f, 0.15f), new(0f, -0.4f, 1f), M(new(0.24f, -0.2f, 0.15f)), new(0f, -0.4f, 1f)),
            new(0.14f, new(0.27f, -0.2f, -0.15f), new(0f, -0.5f, -0.6f), M(new(0.27f, -0.2f, -0.15f)), new(0f, -0.5f, -0.6f), 0f, 0.1f, -6f),
            new(0.28f, new(0.2f, 0.18f, 0.6f), new(0f, 0.6f, 1f), M(new(0.2f, 0.18f, 0.6f)), new(0f, 0.6f, 1f), 0f, 0.04f, 14f),
            new(0.42f, new(0.2f, 0.16f, 0.6f), new(0f, 0.5f, 1f), M(new(0.2f, 0.16f, 0.6f)), new(0f, 0.5f, 1f), 0f, 0.03f, 10f),
        };

        /// <summary>Keyframes of a pose, and when it starts / finishes fading back to rest.</summary>
        static (Key[] keys, float fadeStart, float fadeEnd, bool shake) PoseData(SpellPose p) => p switch
        {
            SpellPose.HoldWall => (HoldWallKeys, 99f, 99f, true),
            SpellPose.CastWall => (CastWallKeys, 0.5f, 0.75f, false),
            SpellPose.Gather => (GatherKeys, 0.95f, 1.15f, false),
            SpellPose.HoldFold => (HoldFoldKeys, 99f, 99f, true),
            SpellPose.CastFold => (CastFoldKeys, 0.4f, 0.6f, false),
            SpellPose.Dash => (DashKeys, 0.3f, 0.5f, false),
            SpellPose.ShoveHold => (ShoveHoldKeys, 99f, 99f, true),
            SpellPose.ShoveCast => (ShoveCastKeys, 0.35f, 0.6f, false),
            SpellPose.TornadoHold => (TornadoHoldKeys, 99f, 99f, true),
            SpellPose.TornadoCast => (TornadoCastKeys, 0.6f, 0.9f, false),
            SpellPose.MissileCharge => (MissileKeys, 0.95f, 1.2f, false),
            SpellPose.Barrier => (BarrierKeys, 1.0f, 1.25f, false),
            SpellPose.Siphon => (SiphonKeys, 1.0f, 1.2f, false),
            SpellPose.SiphonPunch => (SiphonPunchKeys, 0.25f, 0.5f, false),
            SpellPose.FlameCharge => (FlameChargeKeys, 0.75f, 0.95f, false),
            SpellPose.HellfireDraw => (HellfireKeys, SpellPlans.HellfireDraw, SpellPlans.HellfireDraw + 0.2f, false),
            SpellPose.FlameHands => (FlameHandsKeys, 0.5f, 0.7f, false),
            SpellPose.JudgmentHold => (JudgmentHoldKeys, 99f, 99f, true),
            SpellPose.JudgmentCast => (JudgmentCastKeys, 0.95f, 1.2f, false),
            SpellPose.TugHold => (TugHoldKeys, 99f, 99f, true),
            SpellPose.TugCast => (TugCastKeys, 0.5f, 0.7f, false),
            SpellPose.Platform => (PlatformKeys, 0.5f, 0.7f, false),
            SpellPose.FoolRoll => (FoolKeys, 0.3f, 0.5f, false),
            SpellPose.Magician => (MagicianKeys, 0.5f, 0.7f, false),
            SpellPose.SunRaise => (SunRaiseKeys, SpellPlans.PriestessDelay + 0.1f, SpellPlans.PriestessDelay + 0.35f, false),
            SpellPose.Prayer => (PrayerKeys, SpellPlans.DomainDelay + 0.15f, SpellPlans.DomainDelay + 0.35f, false),
            SpellPose.WaterHold => (WaterHoldKeys, 99f, 99f, true),
            SpellPose.WaterCast => (WaterCastKeys, 0.4f, 0.6f, false),
            SpellPose.BubbleCast => (BubbleKeys, SpellPlans.BubbleWindup + SpellPlans.BubbleTime, SpellPlans.BubbleWindup + SpellPlans.BubbleTime + 0.25f, false),
            SpellPose.TsunamiHold => (TsunamiHoldKeys, 99f, 99f, true),
            SpellPose.TsunamiCast => (TsunamiCastKeys, 0.65f, 0.9f, false),
            SpellPose.WarpHold => (WarpHoldKeys, 99f, 99f, true),
            SpellPose.WarpCast => (WarpCastKeys, 0.8f, 1.0f, false),
            SpellPose.PortalHold => (PortalHoldKeys, 99f, 99f, true),
            SpellPose.PortalCast => (PortalCastKeys, 0.3f, 0.5f, false),
            SpellPose.SwiftCast => (SwiftKeys, SpellPlans.SwiftPrep + SpellPlans.SwiftTime, SpellPlans.SwiftPrep + SpellPlans.SwiftTime + 0.2f, false),
            SpellPose.SeveranceDash => (SeveranceDashKeys, SpellPlans.SeveranceTime + 0.1f, SpellPlans.SeveranceTime + 0.3f, false),
            SpellPose.SeveranceSlash => (SeveranceSlashKeys, BladeMotion.Sweep + BladeMotion.Settle, BladeMotion.Sweep + BladeMotion.Settle + 0.25f, false),
            SpellPose.FlowCharge => (FlowChargeKeys, 99f, 99f, false),
            SpellPose.AttractCast => (AttractKeys, SpellPlans.OrbRaise + SpellPlans.ForceLife, SpellPlans.OrbRaise + SpellPlans.ForceLife + 0.25f, false),
            SpellPose.RepelCast => (RepelKeys, SpellPlans.OrbRaise + SpellPlans.ForceLife, SpellPlans.OrbRaise + SpellPlans.ForceLife + 0.25f, false),
            SpellPose.FuseCast => (FuseKeys, SpellPlans.FuseCharge + 0.4f, SpellPlans.FuseCharge + 0.7f, true),
            SpellPose.FlowStrike => (FlowStrikeKeys, BladeMotion.Sweep + BladeMotion.Settle, BladeMotion.Sweep + BladeMotion.Settle + 0.25f, false),
            SpellPose.HierophantHold => (HierophantHoldKeys, 99f, 99f, true),
            SpellPose.HierophantCast => (HierophantCastKeys, SpellPlans.HierophantDelay, SpellPlans.HierophantDelay + 0.25f, false),
            SpellPose.LoversBeam => (LoversKeys, 99f, 99f, false), // held until the beam ends
            SpellPose.HammerDrag => (HammerDragKeys, SpellPlans.RisingTime + 0.1f, SpellPlans.RisingTime + 0.3f, false),
            SpellPose.HammerRise => (HammerRiseKeys, HammerMotion.RiseSwing + HammerMotion.RiseSettle, HammerMotion.RiseSwing + HammerMotion.RiseSettle + 0.25f, false),
            SpellPose.HammerLeap => (HammerLeapKeys, 99f, 99f, false), // held until it lands
            SpellPose.HammerSlam => (HammerSlamKeys, HammerMotion.SlamSwing + HammerMotion.SlamSettle, HammerMotion.SlamSwing + HammerMotion.SlamSettle + 0.3f, false),
            SpellPose.SwarmRaise => (SwarmRaiseKeys, 0.45f, 0.65f, false),
            SpellPose.IceCrouch => (IceCrouchKeys, SpellPlans.IceCrouch + 0.15f, SpellPlans.IceCrouch + 0.35f, false),
            SpellPose.HermitKneel => (HermitKneelKeys, SpellPlans.HermitKneel + SpellPlans.HermitBuild, SpellPlans.HermitKneel + SpellPlans.HermitBuild + 0.25f, false),
            SpellPose.WheelSpin => (WheelSpinKeys, 0.3f, 0.5f, false),
            SpellPose.Clasp => (ClaspKeys, SpellPlans.JusticeClasp + 0.1f, SpellPlans.JusticeClasp + 0.3f, false),
            SpellPose.LeftArmUp => (LeftArmUpKeys, 0.4f, 0.6f, false),
            SpellPose.HeadBack => (HeadBackKeys, SpellPlans.DeathDelay + 0.15f, SpellPlans.DeathDelay + 0.3f, true), // (the body twitching)
            SpellPose.RightHandUp => (RightHandUpKeys, SpellPlans.DevilRaise + 0.15f, SpellPlans.DevilRaise + 0.35f, false),
            SpellPose.TowerPray => (TowerPrayKeys, SpellPlans.TowerPray, SpellPlans.TowerPray + 0.3f, true),
            SpellPose.NecroCrouch => (NecroCrouchKeys, SpellPlans.ReviveTime, SpellPlans.ReviveTime + 0.3f, true),
            SpellPose.BiteCrouch => (BiteCrouchKeys, SpellPlans.BiteCrouch, SpellPlans.BiteCrouch + 0.1f, false),
            SpellPose.FlameSpew => (FlameSpewKeys, 99f, 99f, true), // held until it's let go
            SpellPose.GrapplePoint => (GrapplePointKeys, 99f, 99f, false), // held until the hook lets go

            SpellPose.ShotgunAim => (ShotgunAimKeys, SpellPlans.RachDelay + 0.15f, SpellPlans.RachDelay + 0.35f, false),
            SpellPose.RifleKneel => (RifleKneelKeys, 99f, 99f, false), // held until it fires, or it's let go
            SpellPose.RifleFire => (RifleFireKeys, SpellPlans.SebWindup + SpellPlans.SebRecover * 0.5f, SpellPlans.SebWindup + SpellPlans.SebRecover + 0.15f, false),
            SpellPose.GraspKneel => (GraspKneelKeys, SpellPlans.GraspCast + 0.15f, SpellPlans.GraspCast + 0.35f, true),
            SpellPose.SkinKneel => (SkinKneelKeys, SpellPlans.SkinTime, SpellPlans.SkinTime + 0.3f, true),
            SpellPose.BombPlace => (BombPlaceKeys, 0.4f, 0.6f, false),
            SpellPose.BombToss => (BombTossKeys, 0.42f, 0.6f, false),
            SpellPose.Flex => (FlexKeys, SpellPlans.StrengthFlex + 0.1f, SpellPlans.StrengthFlex + 0.3f, true),
            SpellPose.BreathLean => (BreathLeanKeys, SpellPlans.BreathLean + SpellPlans.IceBreathTime, SpellPlans.BreathLean + SpellPlans.IceBreathTime + 0.2f, false),
            _ => (null, 0f, 0f, false),
        };

        void Awake()
        {
            _pc = GetComponent<Combatant>();
            _model = _pc.model != null ? _pc.model : transform.Find("PlayerModel");
            var b = Rig.MapBones(_model);
            _rigRoot = Find(b, "both rig");
            _pelvis = Find(b, "Bone");
            _chest = Find(b, "Bone.003");
            _head = Find(b, "Bone.001");
            _armL = MakeLimb(b, "Arm L", -1);
            _armR = MakeLimb(b, "Arm R", 1);
            _legL = MakeLimb(b, "Leg L", -1);
            _legR = MakeLimb(b, "Leg R", 1);
            foreach (var t in _model.GetComponentsInChildren<Transform>(true))
                _bind.Add((t, t.localRotation, t.localPosition));
            _modelScale0 = _model.localScale; // (the model's own scale: 0.5)
            if (_head) _headScale0 = _head.localScale;
            if (_head) _eyeHeight = Vector3.Dot(_head.position + _head.rotation * Vector3.up * 0.15f - _model.position, _model.up);
            _initialised = _rigRoot && _pelvis && _chest && _head && _armL != null && _armR != null && _legL != null && _legR != null;
        }

        static Transform Find(Dictionary<string, Transform> b, string n) => b.TryGetValue(n, out var t) ? t : null;

        static Limb MakeLimb(Dictionary<string, Transform> b, string n, float side)
        {
            if (!b.TryGetValue(n, out var root) || !b.TryGetValue(n + ".001", out var up) || !b.TryGetValue(n + ".002", out var lo) ||
                !b.TryGetValue(n + ".003", out var end) || !b.TryGetValue(n + ".003_end", out var tip)) return null;
            return new Limb { root = root, upper = up, lower = lo, end = end, tip = tip, side = side };
        }

        void LateUpdate()
        {
            if (!_initialised || !_pc.IsSpawned) return;
            float dt = Time.deltaTime;

            // ---- inputs ----
            byte flags = _pc.ViewFlags;
            bool grounded = (flags & NetState.Grounded) != 0;
            float yaw = _pc.ViewYaw, pitch = _pc.ViewPitch;
            Vector3 vel = _pc.ViewVelocity;
            Quaternion yawRot = Quaternion.Euler(0, yaw, 0);
            Vector3 lv = Quaternion.Inverse(yawRot) * new Vector3(vel.x, 0, vel.z);
            float speed = lv.magnitude;
            Vector3 moveDir = speed > 0.1f ? lv / speed : Vector3.forward;

            float k = 1f - Mathf.Exp(-12f * dt);
            _airW = Mathf.Lerp(_airW, grounded ? 0 : 1, 1f - Mathf.Exp(-8f * dt));
            _moveW = Mathf.Lerp(_moveW, grounded ? Mathf.Clamp01(speed / 3f) : 0, k);
            _lean = Mathf.Lerp(_lean, grounded ? Mathf.Clamp(lv.z * 0.9f, -6f, 10f) : 0f, k);
            _sideLean = Mathf.Lerp(_sideLean, grounded ? Mathf.Clamp(-lv.x * 0.7f, -8f, 8f) : 0f, k);
            // crouched: the same speed the owner's camera comes down at
            _crouchW = Mathf.MoveTowards(_crouchW, (flags & NetState.Crouch) != 0 ? 1f : 0f, dt * 7f);

            // ---- reset to bind pose ----
            foreach (var (bt, br, bp) in _bind) { bt.localRotation = br; bt.localPosition = bp; }
            _model.localRotation = Quaternion.identity;
            _model.localScale = _modelScale0; // (back to its own size; Strength grows it again at the end, after the pose is solved)
            _head.localScale = _headScale0;

            Quaternion frame = _model.rotation;
            Vector3 origin = _model.position;
            Vector3 fUp = frame * Vector3.up, fFwd = frame * Vector3.forward, fRight = frame * Vector3.right;

            // ---- gait phase ----
            if (grounded)
            {
                float cycle = Mathf.Clamp(1.3f + speed * 0.11f, 1.3f, 3.6f);
                _phase += Mathf.PI * 2f * speed * dt / cycle;
            }
            _phase = Mathf.Repeat(_phase, Mathf.PI * 2f);
            float bob = (Mathf.Cos(_phase * 2f) * 0.5f - 0.5f) * 0.05f * _moveW;

            // ---- spell gesture state ----
            var pose = _pc.ViewPose;
            float poseT = _pc.ViewPoseTime;

            var (keys, fadeStart, fadeEnd, shake) = PoseData(pose);
            float targetW = keys == null ? 0f : poseT < fadeStart ? 1f : 1f - Mathf.Clamp01((poseT - fadeStart) / Mathf.Max(0.01f, fadeEnd - fadeStart));
            if (pose != _lastPose && keys != null && _poseW < 0.05f) _poseW = 0.05f;
            _lastPose = pose;
            _poseW = Mathf.MoveTowards(_poseW, targetW, dt * (targetW > _poseW ? 7f : 4f));
            // pose over: keep its last frame while the arms fade back to rest
            if (keys != null) _lastKey = Sample(keys, poseT);
            Key key = _lastKey;
            _twist = Mathf.Lerp(_twist, keys != null ? key.twist * _poseW : 0f, 1f - Mathf.Exp(-20f * dt));

            // ---- hips & spine ----
            // a spell that crouches you anyway (Magical Barrier, Ice Spike...) doesn't sink you twice
            float crouchDrop = PlayerMotor.CrouchDrop * _crouchW;
            float drop = Mathf.Max(key.drop * _poseW, crouchDrop);
            _rigRoot.position += fUp * (bob - drop);
            float lean = _lean + _airW * 6f + key.lean * _poseW + CrouchLean * _crouchW * (1f - _poseW);
            _pelvis.rotation = Quaternion.AngleAxis(lean, fRight) * Quaternion.AngleAxis(_sideLean, fFwd) * _pelvis.rotation;

            // (aiming Sebastian: level, down the sights, wherever the camera looks)
            _levelW = Mathf.MoveTowards(_levelW, pose == SpellPose.RifleKneel || pose == SpellPose.RifleFire ? 1f : 0f, dt * 6f);
            float p = Mathf.Clamp(pitch, -85f, 85f) * (1f - _levelW);
            _chest.rotation = Quaternion.AngleAxis(_twist, fUp) * Quaternion.AngleAxis(p * 0.3f - lean * 0.3f, fRight) * _chest.rotation;
            // Death: the head tips right back while it starts, then comes forward again
            _headBack = Mathf.MoveTowards(_headBack, pose == SpellPose.HeadBack && poseT < SpellPlans.DeathDelay ? 1f : 0f, dt * 7f);
            _head.rotation = Quaternion.AngleAxis(-_twist * 0.8f, fUp) * Quaternion.AngleAxis(p * 0.55f - lean * 0.6f - 45f * _headBack, fRight) * _head.rotation;

            EyePosition = origin + fUp * (_eyeHeight - crouchDrop) +(Quaternion.Euler(pitch, yaw, 0) * Vector3.forward) * 0.07f;

            // ---- arms ----
            Vector3 chest = _chest.position;
            if (GunMotion.Is(pose))
            {
                // both hands on the gun, wherever the aim points it
                GunMotion.Hold(_pc, pose, poseT, out var trigger, out var gunRot);
                GunMotion.Hands(GunMotion.Kind(pose), trigger, gunRot, out var gr, out var grd, out var gl, out var gld);
                Quaternion inv = Quaternion.Inverse(frame);
                key.r = inv * (gr - chest); key.rd = inv * grd;
                key.l = inv * (gl - chest); key.ld = inv * gld;
                _lastKey = key; // (what the arms fade back from)
            }
            float tremble = shake ? Mathf.Sin(Time.time * 47f) * 0.012f : 0f;
            if (pose == SpellPose.CastFold && poseT < 0.3f) tremble = Mathf.Sin(Time.time * 61f) * 0.02f; // the ground buckling
            SolveArm(_armR, chest, key.r + Vector3.up * tremble, key.rd, fRight, fUp, fFwd);
            SolveArm(_armL, chest, key.l - Vector3.up * tremble, key.ld, fRight, fUp, fFwd);

            // ---- legs ----
            // Flowing Cut's charge, Descending Hammer's crouch: down on the right knee (right foot back, left foot forward)
            bool kneel = pose == SpellPose.FlowCharge || pose == SpellPose.HammerLeap && poseT < SpellPlans.HammerCrouch
                || pose == SpellPose.HermitKneel || pose == SpellPose.TowerPray || pose == SpellPose.RifleKneel
                || pose == SpellPose.RifleFire && poseT < SpellPlans.SebWindup + 0.07f
                || pose == SpellPose.GraspKneel && poseT < SpellPlans.GraspCast
                || pose == SpellPose.SkinKneel && poseT < SpellPlans.SkinTime;
            _kneelW = Mathf.MoveTowards(_kneelW, kneel && grounded ? _poseW : 0f, dt * (pose == SpellPose.HammerLeap ? 10f : 6f));
            SolveLeg(_legL, ref _footL, 0f, dt, origin, frame, moveDir, speed, grounded);
            SolveLeg(_legR, ref _footR, Mathf.PI, dt, origin, frame, moveDir, speed, grounded);

            ApplyGetUp();
            // Strength: everything but the head twice the size, standing on the same feet
            _strongW = Mathf.MoveTowards(_strongW, _pc.Status.Value.Strong(WorldNet.Now) ? 1f : 0f, dt / SpellPlans.StrengthGrow);
            float grow = 1f + (SpellPlans.StrengthSize - 1f) * (_strongW * _strongW * (3f - 2f * _strongW));
            if (grow > 1.001f)
            {
                Vector3 feet = transform.position + Vector3.down * 0.95f;
                _model.position = feet + (_model.position - feet) * grow;
                _model.localScale = _modelScale0 * grow;
                _head.localScale = _headScale0 / grow;
            }
            // folded (Warp Self) or crushed (The World) about the body's middle
            Vector2 crush = SpellPlans.WorldFold(_pc.Status.Value, WorldNet.Now);
            var squash = new Vector2(Fold.x * crush.x, Fold.y * crush.y);
            if (squash != Vector2.one)
            {
                Vector3 s = new(squash.x, squash.y, squash.x), mid = transform.position;
                _model.position = mid + Vector3.Scale(_model.position - mid, s);
                _model.localScale = Vector3.Scale(_model.localScale, s);
            }
            if (_hitScale != grow) { _hitScale = grow; _pc.Hitboxes?.SetRadiusScale(grow); }
            _pc.Hitboxes?.Sync();
        }

        /// <summary>The body squashed about its middle (x: width, y: height), on top of everything else. Set by Warp Self.</summary>
        public Vector2 Fold { get; set; } = Vector2.one;

        float _levelW, _headBack, _strongW, _hitScale = 1f;
        Vector3 _modelScale0 = Vector3.one, _headScale0 = Vector3.one;

        /// <summary>How far grown (Strength) the body is, 0..1.</summary>
        public float StrongWeight => _strongW;

        // ---------------- getting up after a knockdown ----------------

        const float GetUpTime = 0.6f;
        readonly Dictionary<Transform, Quaternion> _getUpRot = new();
        readonly List<Quaternion> _posed = new();
        Vector3 _getUpPelvis;
        float _getUpStart = -10f;

        /// <summary>True while the body is still picking itself up.</summary>
        public bool GettingUp => Time.time - _getUpStart < GetUpTime;

        /// <summary>Remember the pose the ragdoll ended in; the body blends from it to standing.</summary>
        public void BeginGetUp(Transform ragdollRoot)
        {
            if (!_initialised || ragdollRoot == null) return;
            var bones = Rig.MapBones(ragdollRoot);
            _getUpRot.Clear();
            foreach (var (t, _, _) in _bind)
                if (bones.TryGetValue(t.name, out var r)) _getUpRot[t] = r.rotation;
            if (!bones.TryGetValue("Bone", out var pelvis)) return;
            _getUpPelvis = pelvis.position;
            _getUpStart = Time.time;
        }

        /// <summary>
        /// Blends the freshly computed standing pose with the ragdoll's last pose: the hips rise from where the
        /// body lay while every bone turns from its sprawled rotation into place (eased, quick at first).
        /// </summary>
        void ApplyGetUp()
        {
            float u = (Time.time - _getUpStart) / GetUpTime;
            if (u >= 1f || _getUpRot.Count == 0) return;
            float w = u * u * (3f - 2f * u);
            // hips: from the ground up, a touch of push forward over the feet
            Vector3 offset = (_getUpPelvis - _pelvis.position) * (1f - w);
            _rigRoot.position += offset;
            // remember the target pose before changing anything (children follow their parents)
            _posed.Clear();
            foreach (var (t, _, _) in _bind) _posed.Add(t.rotation);
            for (int i = 0; i < _bind.Count; i++)
            {
                var t = _bind[i].t;
                if (_getUpRot.TryGetValue(t, out var from)) t.rotation = Quaternion.Slerp(from, _posed[i], w);
            }
        }

        /// <summary>Linear blend between the keyframes around t (smoothstepped).</summary>
        static Key Sample(Key[] keys, float t)
        {
            if (keys.Length == 1 || t <= keys[0].t) return keys[0];
            for (int i = 1; i < keys.Length; i++)
            {
                if (t > keys[i].t) continue;
                var a = keys[i - 1];
                var b = keys[i];
                float u = (t - a.t) / Mathf.Max(0.0001f, b.t - a.t);
                // keys far apart ease in and out; densely sampled motion (the Blade cut) is already smooth: straight through
                if (b.t - a.t > 0.05f) u = Mathf.SmoothStep(0f, 1f, u);
                return new Key(t, Vector3.Lerp(a.r, b.r, u), Vector3.Slerp(a.rd.normalized, b.rd.normalized, u),
                    Vector3.Lerp(a.l, b.l, u), Vector3.Slerp(a.ld.normalized, b.ld.normalized, u), Mathf.Lerp(a.twist, b.twist, u),
                    Mathf.Lerp(a.drop, b.drop, u), Mathf.Lerp(a.lean, b.lean, u));
            }
            return keys[^1];
        }

        /// <summary>Blends the hanging/swinging rest arm with the gesture (weight _poseW) and solves the IK.</summary>
        void SolveArm(Limb arm, Vector3 chest, Vector3 poseOffset, Vector3 poseDir, Vector3 fRight, Vector3 fUp, Vector3 fFwd)
        {
            Vector3 shoulder = arm.upper.position;
            float swing = Mathf.Sin(_phase + (arm.side > 0 ? 0f : Mathf.PI)) * freeArmSwing * _moveW;
            Vector3 rest = shoulder - fUp * 0.5f + fRight * (arm.side * 0.08f) + fFwd * (0.04f + swing)
                           + (fRight * (arm.side * 0.18f) + fUp * 0.12f) * _airW;
            Vector3 restDir = (-fUp + fFwd * 0.15f).normalized;

            Vector3 target = rest, dir = restDir;
            if (_poseW > 0.001f)
            {
                Vector3 gesture = chest + fRight * poseOffset.x + fUp * poseOffset.y + fFwd * poseOffset.z;
                Vector3 gDir = (fRight * poseDir.x + fUp * poseDir.y + fFwd * poseDir.z).normalized;
                target = Vector3.Lerp(rest, gesture, _poseW);
                dir = Vector3.Slerp(restDir, gDir, _poseW).normalized;
            }

            // elbows hang down and back at rest, out and down while casting
            Vector3 restPole = shoulder - fFwd * 0.6f - fUp * 0.3f + fRight * (arm.side * 0.1f);
            Vector3 castPole = shoulder - fUp * 0.6f + fRight * (arm.side * 0.45f) - fFwd * 0.1f;
            // a hand raised above the shoulder: the elbow goes out and forward, as arms do going overhead (aimed down,
            // the forearm folded back through the body)
            float raised = Mathf.Clamp01((Vector3.Dot(target - shoulder, fUp) + 0.05f) / 0.25f);
            castPole = Vector3.Lerp(castPole, shoulder + fRight * (arm.side * 0.5f) + fFwd * 0.4f + fUp * 0.15f, raised);
            Vector3 pole = Vector3.Lerp(restPole, castPole, _poseW);
            TwoBone(arm.upper, arm.lower, arm.end, target, pole);
            Aim(arm.end, arm.tip.position, arm.end.position + dir);
        }

        void SolveLeg(Limb leg, ref Vector3 smoothed, float phaseOffset, float dt, Vector3 origin, Quaternion frame,
            Vector3 moveDir, float speed, bool grounded)
        {
            float side = leg.side;
            Vector3 fFwd = frame * Vector3.forward, fUp = frame * Vector3.up, fRight = frame * Vector3.right;

            // ground gait
            float ph = _phase + phaseOffset;
            float stride = Mathf.Clamp(speed * 0.045f, 0f, maxStride) * _moveW;
            Vector3 g = new(side * stanceWidth, groundY, side * -0.03f);
            g += moveDir * (Mathf.Sin(ph) * stride);
            g.y += Mathf.Max(0f, Mathf.Cos(ph)) * (stepHeight * Mathf.Clamp01(stride * 4f) + stride * 0.3f);

            // airborne tuck
            float vy = _pc.ViewVelocity.y;
            float tuck = Mathf.Clamp01(1f - vy / 10f) * 0.1f;
            Vector3 a = new(side * (stanceWidth + 0.03f), groundY + 0.28f - tuck, side < 0 ? 0.18f : -0.12f);

            Vector3 local = Vector3.Lerp(g, a, _airW);
            if (_kneelW > 0.001f) local += new Vector3(side * 0.02f, 0.02f, side > 0 ? -0.44f : 0.2f) * _kneelW;
            Vector3 world = origin + frame * local;

            // plant on uneven ground
            if (grounded && Physics.Raycast(world + fUp * 0.5f, -fUp, out var hit, 0.9f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
            {
                float lift = local.y - groundY;
                world = hit.point + fUp * (0.04f + Mathf.Max(0, lift));
            }

            float sk = 1f - Mathf.Exp(-30f * dt);
            if (smoothed == Vector3.zero || (smoothed - world).sqrMagnitude > 4f) smoothed = world;
            else smoothed = Vector3.Lerp(smoothed, world, sk);

            Vector3 knee = leg.upper.position + fFwd * 1f + fRight * side * 0.12f;
            TwoBone(leg.upper, leg.lower, leg.end, smoothed, knee);

            Vector3 footFwd = Vector3.ProjectOnPlane(fFwd, fUp).normalized;
            Aim(leg.end, leg.tip.position, leg.end.position + footFwd * 0.9f - fUp * 0.25f);
        }

        // ---------------- IK helpers ----------------

        /// <summary>Rotate a bone so the point 'childPoint' (attached to it) points at target.</summary>
        internal static void Aim(Transform bone, Vector3 childPoint, Vector3 target)
        {
            Vector3 from = childPoint - bone.position;
            Vector3 to = target - bone.position;
            if (from.sqrMagnitude < 1e-8f || to.sqrMagnitude < 1e-8f) return;
            bone.rotation = Quaternion.FromToRotation(from, to) * bone.rotation;
        }

        internal static void TwoBone(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
        {
            Vector3 root = upper.position;
            float a = Vector3.Distance(root, lower.position);
            float b = Vector3.Distance(lower.position, end.position);
            Vector3 toT = target - root;
            float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(a - b) + 0.001f, a + b - 0.001f);
            Vector3 dir = toT.sqrMagnitude > 1e-8f ? toT.normalized : Vector3.down;

            float cosA = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            Vector3 bend = Vector3.ProjectOnPlane(pole - root, dir);
            if (bend.sqrMagnitude < 1e-6f) bend = Vector3.ProjectOnPlane(Vector3.forward, dir);
            bend.Normalize();

            Vector3 knee = root + dir * (a * cosA) + bend * (a * sinA);
            Aim(upper, lower.position, knee);
            Aim(lower, end.position, root + dir * d);
        }
    }
}
