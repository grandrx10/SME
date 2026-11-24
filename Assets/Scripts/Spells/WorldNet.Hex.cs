using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The op stream's part for Necromatic Blast and the Hex cards. The server decides; every peer applies the same ops in order.</summary>
    public partial class WorldNet
    {
        /// <summary>A hex being cast (the left hand pointing), then flying at its target from <see cref="flyStart"/>.</summary>
        public class HexFx { public int id; public ulong caster, target; public byte kind; public double start, flyStart = -1; public ArcaneCharge charge; }

        public readonly Dictionary<int, HexFx> HexesCast = new();

        enum HexOp : byte { NecroBall = 124, Blight = 125, Cast = 126, Fly = 127, Shatter = 128, Proc = 129 }

        static FastBufferWriter NewOp(HexOp op, int size) => NewOp((Op)(byte)op, size);

        // ------------------------------------------------------------------ server API

        /// <summary>Necromatic Blast: this ball of ground is vaporised and gathers over the caster's head. 0: no ground there.</summary>
        public static int ServerNecroBall(ulong caster, Vector3Int center, int r4)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(HexOp.NecroBall, 40);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(Now);
            WriteCell(ref w, center);
            w.WriteValueSafe((byte)r4);
            i.Broadcast(Finish(ref w));
            return i.Fireballs.ContainsKey(id) ? id : 0;
        }

        /// <summary>Every voxel in this ball turns a sickly green.</summary>
        public static void ServerBlight(Vector3Int center, int r4)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(HexOp.Blight, 16);
            WriteCell(ref w, center);
            w.WriteValueSafe((byte)Mathf.Clamp(r4, 1, 255));
            i.Broadcast(Finish(ref w));
        }

        public static int ServerHexCast(ulong caster, ulong target, byte kind)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(HexOp.Cast, 40);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(target); w.WriteValueSafe(kind); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>The cast is done: the ball of symbols is on its way.</summary>
        public static void ServerHexFly(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(HexOp.Fly, 16);
            w.WriteValueSafe(id); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Someone shatters (look only; damage and ground are separate).</summary>
        public static void ServerHexShatter(Vector3 point, Vector3 dir)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(HexOp.Shatter, 32);
            w.WriteValueSafe(point); w.WriteValueSafe(dir);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>A flurry of symbols off a body: Mind Hex takes hold (its kind), or Necromatic Blast heals its caster (0).</summary>
        public static void ServerHexProc(byte kind, ulong who)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(HexOp.Proc, 16);
            w.WriteValueSafe(kind); w.WriteValueSafe(who);
            i.Broadcast(Finish(ref w));
        }

        // ------------------------------------------------------------------ apply (every peer)

        /// <summary>Applies one of these ops. False: not one of them.</summary>
        bool ApplyHex(Op opRaw, ref FastBufferReader r, VoxelWorld world, List<VoxelWorld.RemovedCell> removed, List<List<VoxelWorld.RemovedCell>> islands)
        {
            switch ((HexOp)(byte)opRaw)
            {
                case HexOp.NecroBall:
                {
                    var fx = new FireballFx { necro = true };
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.start);
                    var c = ReadCell(ref r);
                    r.ReadValueSafe(out byte r4);
                    world.RemoveSphere(c, r4, removed, islands);
                    if (removed.Count == 0) return true;
                    fx.owner = fx.caster;
                    Fireballs[fx.id] = fx;
                    if (!Debris.Suppressed)
                    {
                        NecroBallView.Create(fx, world, removed);
                        CameraRig.ShakeFrom(world.CellCenter(c), 0.15f, 0.2f);
                    }
                    FireballCharging?.Invoke(fx.id, fx.caster, fx.start);
                    return true;
                }
                case HexOp.Blight:
                {
                    var c = ReadCell(ref r);
                    r.ReadValueSafe(out byte r4);
                    world.Blight(c, r4);
                    return true;
                }
                case HexOp.Cast:
                {
                    var fx = new HexFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.target); r.ReadValueSafe(out fx.kind); r.ReadValueSafe(out fx.start);
                    HexesCast[fx.id] = fx;
                    if (!Debris.Suppressed) HexBallView.Charge(fx);
                    return true;
                }
                case HexOp.Fly:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out double t);
                    if (!HexesCast.TryGetValue(id, out var fx)) return true;
                    fx.flyStart = t;
                    if (!Debris.Suppressed) HexBallView.Create(fx);
                    return true;
                }
                case HexOp.Shatter:
                {
                    r.ReadValueSafe(out Vector3 point); r.ReadValueSafe(out Vector3 dir);
                    if (!Debris.Suppressed) GlassBurst.Create(point, dir);
                    return true;
                }
                case HexOp.Proc:
                {
                    r.ReadValueSafe(out byte kind); r.ReadValueSafe(out ulong who);
                    if (!Debris.Suppressed) HexBodyFx.Flurry(FindCombatant(who), kind == 0 ? SpellPlans.NecroGlow : SpellPlans.HexColour(kind));
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ clean-up (every peer)

        void AnimateHex(double now)
        {
            _done.Clear();
            foreach (var h in HexesCast.Values) if (now - h.start > 4.0) _done.Add(h.id);
            foreach (var id in _done) HexesCast.Remove(id);
            _done.Clear();
        }

        /// <summary>(their views notice, and go)</summary>
        void ClearHex() => HexesCast.Clear();
    }
}
