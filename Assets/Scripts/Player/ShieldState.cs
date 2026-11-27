using System;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The arcane shield's health at a moment in time: it drains while it's held up and regenerates a while after it was
    /// last up or hit, so anyone can work out the current value. Server writes.
    /// </summary>
    public struct ShieldState : INetworkSerializable, IEquatable<ShieldState>
    {
        public float Value;
        public double Time;
        /// <summary>Regenerates from then on (while it isn't up).</summary>
        public double RegenAt;
        /// <summary>Held up.</summary>
        public bool Raised;
        /// <summary>It ran out: can't go up again before it's back to <see cref="SpellPlans.ShieldReady"/>.</summary>
        public bool Broken;
        /// <summary>Doesn't drain while it's up (a defense dummy's).</summary>
        public bool Still;
        /// <summary>Counts the times the server put it down itself (broken, knocked down...): its owner lets go too.</summary>
        public byte Forced;
        /// <summary>Its most health (0 = the usual <see cref="SpellPlans.ShieldHealth"/>; Speed Demon lowers it).</summary>
        public byte Cap;

        public float Max => Cap > 0 ? Cap : SpellPlans.ShieldHealth;

        public static ShieldState Full => new() { Value = SpellPlans.ShieldHealth };

        public float At(double now)
        {
            if (Raised && Still) return Mathf.Clamp(Value, 0f, Max);
            if (Raised) return Mathf.Clamp(Value - (float)(now - Time) * SpellPlans.ShieldDrain, 0f, Max);
            double from = Math.Max(Time, RegenAt);
            return Mathf.Clamp(Value + (float)Math.Max(0.0, now - from) * SpellPlans.ShieldRegen, 0f, Max);
        }

        /// <summary>Can go up (again).</summary>
        public bool Usable(double now, float slack = 0f) => At(now) >= (Broken && !Raised ? SpellPlans.ShieldReady - slack : 0.05f);

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Value);
            s.SerializeValue(ref Time);
            s.SerializeValue(ref RegenAt);
            s.SerializeValue(ref Raised);
            s.SerializeValue(ref Broken);
            s.SerializeValue(ref Still);
            s.SerializeValue(ref Forced);
            s.SerializeValue(ref Cap);
        }

        public bool Equals(ShieldState o) => Value == o.Value && Time == o.Time && RegenAt == o.RegenAt && Raised == o.Raised && Broken == o.Broken && Still == o.Still && Forced == o.Forced && Cap == o.Cap;
        public override bool Equals(object obj) => obj is ShieldState o && Equals(o);
        public override int GetHashCode() => Value.GetHashCode() ^ Time.GetHashCode();
    }
}
