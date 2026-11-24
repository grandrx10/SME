using System;
using Unity.Netcode;

namespace SuddenMageEncounter
{
    /// <summary>One hex on a body, its bar filling up.</summary>
    public struct HexMark : IEquatable<HexMark>
    {
        /// <summary><see cref="SpellPlans.ShatterHex"/>, <see cref="SpellPlans.StasisHex"/> or <see cref="SpellPlans.MindHex"/> (0 = none).</summary>
        public byte Kind;
        /// <summary>Who cast it (object id).</summary>
        public ulong By;
        public double From, Until;
        /// <summary>0..1; at 1 it goes off.</summary>
        public float Fill;

        public bool Live(double now) => Kind != 0 && now < Until;

        public bool Equals(HexMark o) => Kind == o.Kind && By == o.By && From == o.From && Until == o.Until && Fill == o.Fill;
        public override bool Equals(object obj) => obj is HexMark o && Equals(o);
        public override int GetHashCode() => Kind ^ From.GetHashCode();
    }

    /// <summary>The hexes on a body, several at once. Server writes; everyone draws the bars.</summary>
    public struct HexMarks : INetworkSerializable, IEquatable<HexMarks>
    {
        public const int Max = 4;
        HexMark _a, _b, _c, _d;

        public HexMark this[int i]
        {
            get => i switch { 0 => _a, 1 => _b, 2 => _c, 3 => _d, _ => default };
            set
            {
                switch (i) { case 0: _a = value; break; case 1: _b = value; break; case 2: _c = value; break; case 3: _d = value; break; }
            }
        }

        public bool Any => (_a.Kind | _b.Kind | _c.Kind | _d.Kind) != 0;

        /// <summary>The first live hex of this kind.</summary>
        public bool Find(byte kind, double now, out HexMark mark)
        {
            for (int i = 0; i < Max; i++)
            {
                mark = this[i];
                if (mark.Kind == kind && mark.Live(now)) return true;
            }
            mark = default;
            return false;
        }

        /// <summary>Put another hex on (with no room left, the oldest goes).</summary>
        public void Add(HexMark mark, double now)
        {
            int at = 0;
            for (int i = 0; i < Max; i++)
            {
                if (!this[i].Live(now)) { at = i; break; }
                if (this[i].From < this[at].From) at = i;
            }
            this[at] = mark;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            One(s, ref _a); One(s, ref _b); One(s, ref _c); One(s, ref _d);
        }

        static void One<T>(BufferSerializer<T> s, ref HexMark m) where T : IReaderWriter
        {
            s.SerializeValue(ref m.Kind);
            if (m.Kind == 0) { if (s.IsReader) m = default; return; }
            s.SerializeValue(ref m.By); s.SerializeValue(ref m.From); s.SerializeValue(ref m.Until); s.SerializeValue(ref m.Fill);
        }

        public bool Equals(HexMarks o) => _a.Equals(o._a) && _b.Equals(o._b) && _c.Equals(o._c) && _d.Equals(o._d);
        public override bool Equals(object obj) => obj is HexMarks o && Equals(o);
        public override int GetHashCode() => _a.GetHashCode() ^ _b.GetHashCode();
    }
}
