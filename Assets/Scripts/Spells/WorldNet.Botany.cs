using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The op stream's part for the Botany cards: a potted plant held in someone's hands, put down on the ground, or thrown
    /// (falling as it flies); and the Sunflower's beam vaporising a layer. Where a pot is at any moment is worked out from
    /// these alike on every peer (<see cref="PotPosition"/>).
    /// </summary>
    public partial class WorldNet
    {
        public const int PotHeld = 0, PotGround = 1, PotFlying = 2;

        public class PotFx
        {
            public int id; public ulong caster; public byte kind; public double start, ended = -1;
            public int phase; public Vector3 origin, vel; public float yaw; public double t; // (ground: where; flying: from where, how fast, since when)
            public Quaternion rot = Quaternion.identity; public Vector3 spin; public bool still; // (flying: its tumble, from the server's physics)
            /// <summary>Flying: where the server's physics had it, and when (drawn a moment behind, between these).</summary>
            public readonly List<(double t, Vector3 p, Quaternion r)> snaps = new();
            public PotView view;
        }

        public readonly Dictionary<int, PotFx> Pots = new();

        enum BotanyOp : byte { Pot = 140, Drop = 141, Throw = 142, End = 143, Erase = 144, Move = 145 }

        static FastBufferWriter NewOp(BotanyOp op, int size) => NewOp((Op)(byte)op, size);

        /// <summary>Where a pot's base is right now.</summary>
        public static Vector3 PotPosition(PotFx fx, double now)
        {
            switch (fx.phase)
            {
                case PotGround: return fx.origin;
                case PotFlying:
                {
                    if (fx.still) return fx.origin;
                    return Snap(fx, now, out var p, out _) ? p : fx.origin;
                }
                default:
                {
                    var c = FindCombatant(fx.caster);
                    return c != null ? SpellPlans.PotPoint(c) : fx.origin;
                }
            }
        }

        /// <summary>Which way a pot faces right now (held: the way its holder does).</summary>
        public static float PotYaw(PotFx fx)
        {
            if (fx.phase != PotHeld) return fx.yaw;
            var c = FindCombatant(fx.caster);
            return c != null ? c.ViewYaw : fx.yaw;
        }

        /// <summary>Which way a pot is turned right now (thrown: tumbling as the server's physics has it).</summary>
        public static Quaternion PotRotation(PotFx fx, double now)
        {
            if (fx.phase != PotFlying) return Quaternion.Euler(0f, PotYaw(fx), 0f);
            if (fx.still) return fx.rot;
            return Snap(fx, now, out _, out var r) ? r : fx.rot;
        }

        /// <summary>How far behind the server's physics a flying pot is drawn (two of its updates: always something either side).</summary>
        const double PotDelay = 0.07;

        /// <summary>A flying pot, a moment ago: between the two updates either side of then (past the last: carried on briefly).</summary>
        static bool Snap(PotFx fx, double now, out Vector3 p, out Quaternion r)
        {
            p = fx.origin; r = fx.rot;
            var s = fx.snaps;
            if (s.Count == 0) return false;
            double at = now - PotDelay;
            if (at <= s[0].t) { p = s[0].p; r = s[0].r; return true; }
            for (int k = 1; k < s.Count; k++)
            {
                if (at > s[k].t) continue;
                float u = (float)((at - s[k - 1].t) / System.Math.Max(1e-4, s[k].t - s[k - 1].t));
                p = Vector3.LerpUnclamped(s[k - 1].p, s[k].p, u);
                r = Quaternion.Slerp(s[k - 1].r, s[k].r, u);
                return true;
            }
            float dt = Mathf.Clamp((float)(at - fx.t), 0f, 0.1f);
            p = fx.origin + fx.vel * dt + Vector3.down * (0.5f * SpellPlans.PotGravity * dt * dt);
            r = Quaternion.Euler(fx.spin * (dt * Mathf.Rad2Deg)) * fx.rot;
            return true;
        }

        /// <summary>The plant's head (the sunflower's face, the cactus's middle, the fly trap's mouth) right now.</summary>
        public static Vector3 PotHead(PotFx fx, double now) =>
            PotPosition(fx, now) + PotRotation(fx, now) * PotModel.Head(fx.kind);

        /// <summary>Where the fly trap that's eaten someone (pot <paramref name="id"/>) has its mouth.</summary>
        public static bool PotMouth(int id, double now, out Vector3 mouth)
        {
            mouth = default;
            var i = Instance;
            if (i == null || !i.Pots.TryGetValue(id, out var fx)) return false;
            mouth = PotHead(fx, now);
            return true;
        }

        /// <summary>This player's pot still in their hands (null: none).</summary>
        public PotFx HeldPot(ulong caster)
        {
            foreach (var p in Pots.Values) if (p.caster == caster && p.ended < 0 && p.phase == PotHeld) return p;
            return null;
        }

        // ------------------------------------------------------------------ server API

        public static int ServerPot(ulong caster, byte kind, double start)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(BotanyOp.Pot, 32);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(kind); w.WriteValueSafe(start);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>Put down (or dropped, or landed): standing on the ground here from now on.</summary>
        public static void ServerPotDrop(int id, Vector3 at, float yaw)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(BotanyOp.Drop, 40);
            w.WriteValueSafe(id); w.WriteValueSafe(at); w.WriteValueSafe(yaw); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Thrown: from here, this fast, falling.</summary>
        public static void ServerPotThrow(int id, Vector3 from, Vector3 vel, float yaw)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(BotanyOp.Throw, 52);
            w.WriteValueSafe(id); w.WriteValueSafe(from); w.WriteValueSafe(vel); w.WriteValueSafe(yaw); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>A thrown pot, as the server's physics has it now: where, turned how, how fast, spinning how.</summary>
        public static void ServerPotMove(int id, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 spin)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(BotanyOp.Move, 72);
            w.WriteValueSafe(id); w.WriteValueSafe(pos); w.WriteValueSafe(rot); w.WriteValueSafe(vel); w.WriteValueSafe(spin); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Over: the pot and plant dither away.</summary>
        public static void ServerPotEnd(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(BotanyOp.End, 16);
            w.WriteValueSafe(id); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>The Sunflower's beam vaporises these cells (white rim, floating away).</summary>
        public static void ServerFlowerErase(List<Vector3Int> cells)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return;
            var w = NewOp(BotanyOp.Erase, 8 + cells.Count * 6);
            WriteCells(ref w, cells);
            i.Broadcast(Finish(ref w));
        }

        // ------------------------------------------------------------------ apply (every peer)

        /// <summary>Applies one of these ops. False: not one of them.</summary>
        bool ApplyBotany(Op opRaw, ref FastBufferReader r, VoxelWorld world, List<VoxelWorld.RemovedCell> removed, List<List<VoxelWorld.RemovedCell>> islands)
        {
            switch ((BotanyOp)(byte)opRaw)
            {
                case BotanyOp.Pot:
                {
                    var fx = new PotFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.kind); r.ReadValueSafe(out fx.start);
                    var c = FindCombatant(fx.caster);
                    if (c != null) { fx.origin = SpellPlans.PotPoint(c); fx.yaw = c.ViewYaw; }
                    Pots[fx.id] = fx;
                    if (!Debris.Suppressed) fx.view = PotView.Create(fx);
                    return true;
                }
                case BotanyOp.Drop:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 at); r.ReadValueSafe(out float yaw); r.ReadValueSafe(out double t);
                    if (Pots.TryGetValue(id, out var fx)) { fx.phase = PotGround; fx.origin = at; fx.yaw = yaw; fx.t = t; fx.vel = Vector3.zero; }
                    return true;
                }
                case BotanyOp.Throw:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 from); r.ReadValueSafe(out Vector3 vel); r.ReadValueSafe(out float yaw); r.ReadValueSafe(out double t);
                    if (Pots.TryGetValue(id, out var fx))
                    {
                        // (where it was in the hands is the first thing to draw it from)
                        fx.rot = PotRotation(fx, t);
                        fx.snaps.Clear();
                        fx.snaps.Add((t, from, fx.rot));
                        fx.phase = PotFlying; fx.origin = from; fx.vel = vel; fx.yaw = yaw; fx.t = t; fx.spin = Vector3.zero;
                    }
                    return true;
                }
                case BotanyOp.Move:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 pos); r.ReadValueSafe(out Quaternion rot); r.ReadValueSafe(out Vector3 vel); r.ReadValueSafe(out Vector3 spin); r.ReadValueSafe(out double t);
                    if (Pots.TryGetValue(id, out var fx) && fx.ended < 0)
                    {
                        fx.phase = PotFlying; fx.origin = pos; fx.rot = rot; fx.vel = vel; fx.spin = spin; fx.t = t;
                        fx.snaps.Add((t, pos, rot));
                        while (fx.snaps.Count > 8) fx.snaps.RemoveAt(0);
                    }
                    return true;
                }
                case BotanyOp.End:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out double t);
                    if (Pots.TryGetValue(id, out var fx) && fx.ended < 0)
                    {
                        // (it ends where it was, as it was)
                        var at = PotPosition(fx, t);
                        fx.rot = PotRotation(fx, t);
                        fx.origin = at;
                        fx.yaw = PotYaw(fx);
                        if (fx.phase == PotFlying) fx.still = true;
                        else fx.phase = PotGround;
                        fx.ended = t;
                    }
                    return true;
                }
                case BotanyOp.Erase:
                {
                    var cells = ReadCells(ref r);
                    world.RemoveCells(cells, removed, true, islands);
                    if (removed.Count > 0 && !Debris.Suppressed) VoidRise.Spawn(world, removed, SpellPlans.FlowerVoid);
                    return true;
                }
            }
            return false;
        }

        void AnimateBotany(double now)
        {
            if (Pots.Count == 0) return;
            _done.Clear();
            foreach (var p in Pots.Values) if (p.ended >= 0 && now - p.ended > SpellPlans.PotVanish + 1.0) _done.Add(p.id);
            foreach (var id in _done) Pots.Remove(id);
        }

        void ClearBotany()
        {
            foreach (var p in Pots.Values) if (p.view != null) Destroy(p.view.gameObject);
            Pots.Clear();
        }
    }
}
