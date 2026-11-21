using System;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Arm/body animation a caster is playing (sent so everyone sees the same gestures).</summary>
    public enum SpellPose : byte
    {
        None = 0,
        HoldWall = 1,   // palms down, ready to lift
        CastWall = 2,   // heave upwards
        Gather = 3,     // rip the rock out, hold it, throw it
        HoldFold = 4,   // arms out wide, palms facing in
        CastFold = 5,   // buckle... clap
        Dash = 6,
        ShoveHold = 7,
        ShoveCast = 8,
        TornadoHold = 9,
        TornadoCast = 10,
        MissileCharge = 11,
        Barrier = 12,
        Siphon = 13,
        SiphonPunch = 14,
        FlameCharge = 15,    // hand above the head, then throw
        HellfireDraw = 16,   // pointing the line along the ground
        FlameHands = 17,     // glowing hands... clap
        JudgmentHold = 18,
        JudgmentCast = 19,
        TugHold = 20,
        TugCast = 21,
        Platform = 22,       // stamp the ground up
        FoolRoll = 23,       // a flick of the hand: a card appears
        Magician = 24,       // wand hand up, other hand down
        SunRaise = 25,       // holding the sun up... flash
        Prayer = 26,         // hands come together (Empress, Emperor)
        WaterHold = 27,      // hands cupped in front, holding the water
        WaterCast = 28,      // lifted out: the orb floats off them
        BubbleCast = 29,     // hands come together, and stay together while the bubble carries you
        TsunamiHold = 30,    // arms low and ready
        TsunamiCast = 31,    // drawn back behind the hips... shoved forward
        WarpHold = 32,       // hands close together in front of the chest
        WarpCast = 33,       // hands together... pulled apart, opening the rift
        PortalHold = 34,     // right hand out, palm forward, framing the portal
        PortalCast = 35,     // a sweep of the hand: the portal opens
        SwiftCast = 36,      // crouched, gathering... then the forced run, leaning in
    }

    /// <summary>Owner-authoritative movement snapshot replicated to everyone.</summary>
    public struct NetState : INetworkSerializable, IEquatable<NetState>
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Yaw;
        public float Pitch;
        public byte Flags;
        public byte Teleport;   // increments on respawn so remotes snap instead of interpolating
        public byte Pose;       // SpellPose
        public double PoseStart; // server time the pose started

        public const byte Grounded = 1, Ragdoll = 2;

        public bool Has(byte f) => (Flags & f) != 0;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Position);
            s.SerializeValue(ref Velocity);
            s.SerializeValue(ref Yaw);
            s.SerializeValue(ref Pitch);
            s.SerializeValue(ref Flags);
            s.SerializeValue(ref Teleport);
            s.SerializeValue(ref Pose);
            s.SerializeValue(ref PoseStart);
        }

        public bool Equals(NetState o) =>
            Position == o.Position && Velocity == o.Velocity && Mathf.Approximately(Yaw, o.Yaw) &&
            Mathf.Approximately(Pitch, o.Pitch) && Flags == o.Flags && Teleport == o.Teleport && Pose == o.Pose && PoseStart == o.PoseStart;

        public override bool Equals(object obj) => obj is NetState o && Equals(o);
        public override int GetHashCode() => Position.GetHashCode() ^ (Flags << 8);
    }

    /// <summary>Mana at a moment in time; it refills at 1 per second up to 10, so anyone can work out the current value.</summary>
    public struct ManaState : INetworkSerializable, IEquatable<ManaState>
    {
        public const float Max = 10f, PerSecond = 1f;
        public float Value;
        public double Time;
        /// <summary>The Magician: regeneration runs at double speed until this time.</summary>
        public double FastUntil;

        public float At(double now)
        {
            float extra = (float)System.Math.Max(0.0, System.Math.Min(now, FastUntil) - Time);
            return Mathf.Clamp(Value + ((float)(now - Time) + extra) * PerSecond, 0f, Max);
        }

        public bool Fast(double now) => now < FastUntil;

        /// <summary>A new mana amount from now on (keeps a running Magician boost).</summary>
        public ManaState With(double now, float value) => new() { Value = value, Time = now, FastUntil = FastUntil };

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Value);
            s.SerializeValue(ref Time);
            s.SerializeValue(ref FastUntil);
        }

        public bool Equals(ManaState o) => Value == o.Value && Time == o.Time && FastUntil == o.FastUntil;
        public override bool Equals(object obj) => obj is ManaState o && Equals(o);
        public override int GetHashCode() => Value.GetHashCode() ^ Time.GetHashCode();
    }

    /// <summary>The 5 cards a player brought into the round (what the server lets them cast).</summary>
    public struct EquippedCards : INetworkSerializable, IEquatable<EquippedCards>
    {
        public byte C0, C1, C2, C3, C4;

        public CardId this[int i]
        {
            get => (CardId)(i switch { 0 => C0, 1 => C1, 2 => C2, 3 => C3, 4 => C4, _ => 0 });
            set
            {
                byte b = (byte)value;
                switch (i) { case 0: C0 = b; break; case 1: C1 = b; break; case 2: C2 = b; break; case 3: C3 = b; break; case 4: C4 = b; break; }
            }
        }

        public static EquippedCards From(Loadout l)
        {
            var e = new EquippedCards();
            for (int i = 0; i < Loadout.Slots; i++) e[i] = l.Get(i);
            return e;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref C0); s.SerializeValue(ref C1); s.SerializeValue(ref C2); s.SerializeValue(ref C3); s.SerializeValue(ref C4);
        }

        public bool Equals(EquippedCards o) => C0 == o.C0 && C1 == o.C1 && C2 == o.C2 && C3 == o.C3 && C4 == o.C4;
        public override bool Equals(object obj) => obj is EquippedCards o && Equals(o);
        public override int GetHashCode() => C0 | (C1 << 8) | (C2 << 16) | (C3 << 24) ^ C4;
    }
}
