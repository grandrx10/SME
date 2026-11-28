using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The op stream's part for Hammerang: a hammer thrown, where the server has it as it flies (out, falling, lying on
    /// the ground, coming back), and its end (caught, or gone). Peers draw it a moment behind, between those updates.
    /// </summary>
    public partial class WorldNet
    {
        public const byte RangWindup = 0, RangFlyOut = 1, RangFalling = 2, RangResting = 3, RangBack = 4;

        public class RangFx
        {
            public int id; public ulong caster; public double start, ended = -1; public bool caught;
            public byte phase; public Vector3 dir;
            public readonly List<(double t, Vector3 p, byte phase)> snaps = new();
            public RangView view;
        }

        public readonly Dictionary<int, RangFx> Rangs = new();

        enum RangOp : byte { Throw = 162, Move = 163, End = 164 }

        static FastBufferWriter NewOp(RangOp op, int size) => NewOp((Op)(byte)op, size);

        /// <summary>How far behind the server a hammer is drawn (two of its updates: always something either side).</summary>
        const double RangDelay = 0.07;

        /// <summary>Where a hammer is drawn right now, and what it's doing.</summary>
        public static Vector3 RangPosition(RangFx fx, double now, out byte phase)
        {
            var s = fx.snaps;
            phase = fx.phase;
            if (s.Count == 0) return Vector3.zero;
            double at = now - RangDelay;
            if (fx.ended >= 0) at = System.Math.Min(at, fx.ended);
            if (at <= s[0].t) { phase = s[0].phase; return s[0].p; }
            for (int k = 1; k < s.Count; k++)
            {
                if (at > s[k].t) continue;
                float u = (float)((at - s[k - 1].t) / System.Math.Max(1e-4, s[k].t - s[k - 1].t));
                phase = s[k - 1].phase;
                return Vector3.LerpUnclamped(s[k - 1].p, s[k].p, u);
            }
            phase = s[^1].phase;
            return s[^1].p;
        }

        // ------------------------------------------------------------------ server API

        public static int ServerRangThrow(ulong caster, Vector3 from, Vector3 dir)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(RangOp.Throw, 56);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(from); w.WriteValueSafe(dir); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>Where the server's hammer is now, and what it's doing (<see cref="RangFlyOut"/> ...).</summary>
        public static void ServerRangMove(int id, Vector3 pos, byte phase)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(RangOp.Move, 32);
            w.WriteValueSafe(id); w.WriteValueSafe(pos); w.WriteValueSafe(phase); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Over: caught by its thrower (or gone, if they're not there to catch it).</summary>
        public static void ServerRangEnd(int id, Vector3 pos, bool caught)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(RangOp.End, 32);
            w.WriteValueSafe(id); w.WriteValueSafe(pos); w.WriteValueSafe(caught); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        // ------------------------------------------------------------------ apply (every peer)

        bool ApplyRang(Op opRaw, ref FastBufferReader r)
        {
            switch ((RangOp)(byte)opRaw)
            {
                case RangOp.Throw:
                {
                    var fx = new RangFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out Vector3 from); r.ReadValueSafe(out fx.dir); r.ReadValueSafe(out fx.start);
                    fx.phase = RangFlyOut;
                    fx.snaps.Add((fx.start, from, RangFlyOut));
                    Rangs[fx.id] = fx;
                    if (!Debris.Suppressed) fx.view = RangView.Create(fx);
                    return true;
                }
                case RangOp.Move:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 pos); r.ReadValueSafe(out byte phase); r.ReadValueSafe(out double t);
                    if (Rangs.TryGetValue(id, out var fx) && fx.ended < 0)
                    {
                        fx.phase = phase;
                        fx.snaps.Add((t, pos, phase));
                        while (fx.snaps.Count > 10) fx.snaps.RemoveAt(0);
                    }
                    return true;
                }
                case RangOp.End:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 pos); r.ReadValueSafe(out bool caught); r.ReadValueSafe(out double t);
                    if (Rangs.TryGetValue(id, out var fx) && fx.ended < 0)
                    {
                        fx.snaps.Add((t, pos, fx.phase));
                        fx.ended = t;
                        fx.caught = caught;
                    }
                    return true;
                }
            }
            return false;
        }

        void AnimateRang(double now)
        {
            if (Rangs.Count == 0) return;
            _done.Clear();
            foreach (var p in Rangs.Values) if (p.ended >= 0 && now - p.ended > 1.0 || now - p.start > SpellPlans.RangLife + 2.0) _done.Add(p.id);
            foreach (var id in _done) Rangs.Remove(id);
            _done.Clear();
        }

        void ClearRang()
        {
            foreach (var p in Rangs.Values) if (p.view != null) Destroy(p.view.gameObject);
            Rangs.Clear();
        }
    }
}
