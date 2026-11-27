using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The op stream's part for Compressed Shot (the bolt and the ground it breaks) and Bubble Bomb (the bubble's drift and its end).</summary>
    public partial class WorldNet
    {
        public class MineFx
        {
            public int id; public ulong caster; public Vector3 p0, v; public float damp; public double t0, start, ended = -1;
            public Vector3 endAt; public bool burst;
            public MineView view;
        }

        public readonly Dictionary<int, MineFx> Mines = new();

        enum GaleOp : byte { Shot = 146, Mine = 147, MineMove = 148, MineEnd = 149 }

        static FastBufferWriter NewOp(GaleOp op, int size) => NewOp((Op)(byte)op, size);

        /// <summary>Where a bubble is right now.</summary>
        public static Vector3 MinePosition(MineFx fx, double now) =>
            fx.ended >= 0 ? fx.endAt : SpellPlans.MineDrift(fx.p0, fx.v, fx.damp, (float)(now - fx.t0));

        // ------------------------------------------------------------------ server API

        public static void ServerWindShot(Vector3 from, Vector3 to, float charge, List<Vector3Int> cells)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(GaleOp.Shot, 40 + cells.Count * 6);
            w.WriteValueSafe(from); w.WriteValueSafe(to); w.WriteValueSafe(charge);
            WriteCells(ref w, cells);
            i.Broadcast(Finish(ref w));
        }

        public static int ServerMine(ulong caster, Vector3 at, Vector3 vel, float damp)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(GaleOp.Mine, 64);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(at); w.WriteValueSafe(vel); w.WriteValueSafe(damp); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>Bumped: drifting from here, this way, slowing like this, from now.</summary>
        public static void ServerMineMove(int id, Vector3 from, Vector3 vel, float damp)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(GaleOp.MineMove, 48);
            w.WriteValueSafe(id); w.WriteValueSafe(from); w.WriteValueSafe(vel); w.WriteValueSafe(damp); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Set off here (<paramref name="burst"/>), or just popped.</summary>
        public static void ServerMineEnd(int id, Vector3 at, bool burst)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(GaleOp.MineEnd, 32);
            w.WriteValueSafe(id); w.WriteValueSafe(at); w.WriteValueSafe(burst); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        // ------------------------------------------------------------------ apply (every peer)

        bool ApplyGale(Op opRaw, ref FastBufferReader r, VoxelWorld world, List<VoxelWorld.RemovedCell> removed, List<List<VoxelWorld.RemovedCell>> islands)
        {
            switch ((GaleOp)(byte)opRaw)
            {
                case GaleOp.Shot:
                {
                    r.ReadValueSafe(out Vector3 from); r.ReadValueSafe(out Vector3 to); r.ReadValueSafe(out float charge);
                    var cells = ReadCells(ref r);
                    world.RemoveCells(cells, removed, true, islands);
                    if (!Debris.Suppressed)
                    {
                        if (removed.Count > 0) Debris.Spawn(world, removed, to, 6f, (to - from).normalized * 6f, 1);
                        WindBolt.Create(from, to, charge);
                    }
                    return true;
                }
                case GaleOp.Mine:
                {
                    var fx = new MineFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.p0); r.ReadValueSafe(out fx.v); r.ReadValueSafe(out fx.damp); r.ReadValueSafe(out fx.t0);
                    fx.start = fx.t0;
                    Mines[fx.id] = fx;
                    if (!Debris.Suppressed) fx.view = MineView.Create(fx);
                    return true;
                }
                case GaleOp.MineMove:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 from); r.ReadValueSafe(out Vector3 vel); r.ReadValueSafe(out float damp); r.ReadValueSafe(out double t);
                    if (Mines.TryGetValue(id, out var fx)) { fx.p0 = from; fx.v = vel; fx.damp = damp; fx.t0 = t; }
                    return true;
                }
                case GaleOp.MineEnd:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 at); r.ReadValueSafe(out bool burst); r.ReadValueSafe(out double t);
                    if (Mines.TryGetValue(id, out var fx)) { fx.endAt = at; fx.burst = burst; fx.ended = t; }
                    return true;
                }
            }
            return false;
        }

        void AnimateGale(double now)
        {
            if (Mines.Count == 0) return;
            _done.Clear();
            foreach (var m in Mines.Values) if (m.ended >= 0 && now - m.ended > 1.5) _done.Add(m.id);
            foreach (var id in _done) Mines.Remove(id);
        }

        void ClearGale()
        {
            foreach (var m in Mines.Values) if (m.view != null) Destroy(m.view.gameObject);
            Mines.Clear();
        }
    }
}
