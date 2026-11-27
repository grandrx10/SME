using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The op stream's part for Hammer Spin, Wolfgang, Heavenly Slash and Parry. The server decides; every peer applies the same ops in order.</summary>
    public partial class WorldNet
    {
        /// <summary>One of Wolfgang's bullets, flying straight from <see cref="origin"/> along <see cref="dir"/> since <see cref="start"/>.</summary>
        public class WolfBulletFx
        {
            public int id; public ulong caster; public Vector3 origin, dir, end; public double start, ended = -1; public WolfBulletView view;
            public bool Alive => ended < 0;
        }

        /// <summary>Heavenly Slash's cut, leaving <see cref="origin"/> along <see cref="dir"/> at <see cref="start"/>, its line rolled <see cref="roll"/>.</summary>
        public class HeavenFx { public int id; public ulong caster; public Vector3 origin, dir; public float roll; public double start, ended = -1; public HeavenView view; }

        public readonly Dictionary<int, WolfBulletFx> WolfBullets = new();
        public readonly Dictionary<int, HeavenFx> Heavens = new();
        /// <summary>Heavenly Slash's angle for each caster still posing in the air (when it was cast, and the roll).</summary>
        public readonly Dictionary<ulong, (double start, float roll)> HeavenRolls = new();

        /// <summary>Hammer Spin: how long each caster's latest spin lasts (when it began, and its time), for the body's turning.</summary>
        public readonly Dictionary<ulong, (double start, float time)> Spins = new();

        /// <summary>How long this caster's spin lasts (a second if nobody's said yet).</summary>
        public static float SpinTimeOf(ulong caster) => Instance != null && Instance.Spins.TryGetValue(caster, out var s) ? s.time : 1f;

        /// <summary>Hammer Spin begins: it lasts <paramref name="time"/>.</summary>
        public static void ServerSpinStart(ulong caster, float time)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(ArmsOp.SpinStart, 32);
            w.WriteValueSafe(caster); w.WriteValueSafe(time); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Where a bullet is.</summary>
        public static Vector3 WolfBulletPosition(WolfBulletFx b, double now) => b.Alive
            ? b.origin + b.dir * Mathf.Min(SpellPlans.WolfSpeed * Mathf.Max(0f, (float)(now - b.start)), SpellPlans.WolfRange)
            : b.end;

        /// <summary>The cut's angle for a caster still posing in the air (false: none).</summary>
        public bool HeavenRollOf(ulong caster, out float roll)
        {
            roll = 0f;
            if (!HeavenRolls.TryGetValue(caster, out var h) || Now - h.start > SpellPlans.HeavenRelease + 0.3) return false;
            roll = h.roll;
            return true;
        }

        enum ArmsOp : byte
        {
            WolfShot = 151, WolfTurn = 152, WolfEnd = 153,
            Heaven = 154, HeavenEnd = 155, HeavenRoll = 156,
            SpinSmash = 157,
            ParryDeflect = 158, ParryStrike = 159,
            HeavenCarve = 160,
            SpinStart = 161,
        }

        static FastBufferWriter NewOp(ArmsOp op, int size) => NewOp((Op)(byte)op, size);

        // ------------------------------------------------------------------ server API

        /// <summary>Wolfgang: a bullet leaves pistol <paramref name="side"/> (1 right, -1 left).</summary>
        public static int ServerWolfShot(ulong caster, int side, Vector3 origin, Vector3 dir)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(ArmsOp.WolfShot, 64);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe((sbyte)side); w.WriteValueSafe(origin); w.WriteValueSafe(dir); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>A bullet bounced off something: from here along <paramref name="dir"/>.</summary>
        public static void ServerWolfTurn(int id, Vector3 origin, Vector3 dir)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(ArmsOp.WolfTurn, 48);
            w.WriteValueSafe(id); w.WriteValueSafe(origin); w.WriteValueSafe(dir); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>A bullet is gone at <paramref name="point"/> (<paramref name="burst"/>: it hit something; <paramref name="hole"/>: the ground, leaving a hole).</summary>
        public static void ServerWolfEnd(int id, Vector3 point, bool burst, bool hole)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(ArmsOp.WolfEnd, 40);
            w.WriteValueSafe(id); w.WriteValueSafe(point); w.WriteValueSafe(burst); w.WriteValueSafe(hole); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Heavenly Slash's cut leaves its caster.</summary>
        public static int ServerHeaven(ulong caster, Vector3 origin, Vector3 dir, float roll)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(ArmsOp.Heaven, 64);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(origin); w.WriteValueSafe(dir); w.WriteValueSafe(roll); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>Heavenly Slash's cut is stopped (a barrier, a bubble) before its full 15 m.</summary>
        public static void ServerHeavenEnd(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(ArmsOp.HeavenEnd, 24);
            w.WriteValueSafe(id); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Heavenly Slash was cast: the angle its cut will have (shown to its caster while they pose in the air).</summary>
        public static void ServerHeavenRoll(ulong caster, float roll)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(ArmsOp.HeavenRoll, 32);
            w.WriteValueSafe(caster); w.WriteValueSafe(roll); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Heavenly Slash's cut deletes these cells outright (no debris: it's a wide cut, kept cheap).</summary>
        public static void ServerHeavenCarve(List<Vector3Int> cells)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return;
            var w = NewOp(ArmsOp.HeavenCarve, 8 + cells.Count * 6);
            WriteCells(ref w, cells);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Hammer Spin smashes these cells out, flinging them on along <paramref name="dir"/> and out to the sides.</summary>
        public static void ServerSpinSmash(List<Vector3Int> cells, Vector3 dir)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return;
            var w = NewOp(ArmsOp.SpinSmash, 16 + cells.Count * 6);
            WriteCells(ref w, cells);
            w.WriteValueSafe(dir);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Parry turned a blow aside at <paramref name="point"/> (it came from <paramref name="from"/>'s way).</summary>
        public static void ServerParryDeflect(Vector3 point, Vector3 from)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(ArmsOp.ParryDeflect, 32);
            w.WriteValueSafe(point); w.WriteValueSafe(from);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Parry's stab lands at <paramref name="point"/>, thrust along <paramref name="dir"/>.</summary>
        public static void ServerParryStrike(Vector3 point, Vector3 dir)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(ArmsOp.ParryStrike, 32);
            w.WriteValueSafe(point); w.WriteValueSafe(dir);
            i.Broadcast(Finish(ref w));
        }

        // ------------------------------------------------------------------ apply (every peer)

        /// <summary>Applies one of these ops. False: not one of them.</summary>
        bool ApplyArms(Op opRaw, ref FastBufferReader r, VoxelWorld world, List<VoxelWorld.RemovedCell> removed, List<List<VoxelWorld.RemovedCell>> islands)
        {
            switch ((ArmsOp)(byte)opRaw)
            {
                case ArmsOp.WolfShot:
                {
                    var fx = new WolfBulletFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out sbyte side);
                    r.ReadValueSafe(out fx.origin); r.ReadValueSafe(out fx.dir); r.ReadValueSafe(out fx.start);
                    WolfBullets[fx.id] = fx;
                    if (!Debris.Suppressed)
                    {
                        fx.view = WolfBulletView.Create(fx);
                        MuzzleFlash.Create(fx.origin, fx.dir, 0.45f);
                        CameraRig.ShakeFrom(fx.origin, 0.12f, 0.06f);
                        var c = FindCombatant(fx.caster);
                        var gv = c != null ? c.GetComponent<WolfgangView>() : null;
                        if (gv != null) gv.Fired(side);
                    }
                    return true;
                }
                case ArmsOp.WolfTurn:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 origin); r.ReadValueSafe(out Vector3 dir); r.ReadValueSafe(out double t);
                    if (WolfBullets.TryGetValue(id, out var fx)) { fx.origin = origin; fx.dir = dir; fx.start = t; }
                    return true;
                }
                case ArmsOp.WolfEnd:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 point); r.ReadValueSafe(out bool burst); r.ReadValueSafe(out bool hole); r.ReadValueSafe(out double t);
                    Vector3 dir = Vector3.forward;
                    if (WolfBullets.TryGetValue(id, out var fx))
                    {
                        dir = fx.dir;
                        fx.ended = t;
                        fx.end = point;
                    }
                    if (burst && !Debris.Suppressed) PelletsView.Spark(point);
                    return true;
                }
                case ArmsOp.Heaven:
                {
                    var fx = new HeavenFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.origin); r.ReadValueSafe(out fx.dir);
                    r.ReadValueSafe(out fx.roll); r.ReadValueSafe(out fx.start);
                    Heavens[fx.id] = fx;
                    HeavenRolls.Remove(fx.caster);
                    if (!Debris.Suppressed) fx.view = HeavenView.Create(fx);
                    return true;
                }
                case ArmsOp.HeavenEnd:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out double t);
                    if (Heavens.TryGetValue(id, out var fx) && fx.ended < 0) fx.ended = t;
                    return true;
                }
                case ArmsOp.HeavenRoll:
                {
                    r.ReadValueSafe(out ulong caster); r.ReadValueSafe(out float roll); r.ReadValueSafe(out double t);
                    HeavenRolls[caster] = (t, roll);
                    return true;
                }
                case ArmsOp.SpinStart:
                {
                    r.ReadValueSafe(out ulong caster); r.ReadValueSafe(out float time); r.ReadValueSafe(out double t);
                    // (the spinner's own screen already knows, from the moment it let go: theirs stays, so the body doesn't jump)
                    var me = FindCaster(caster);
                    if (!(me != null && me.IsOwner && Spins.TryGetValue(caster, out var mine) && t - mine.start < 1.0)) Spins[caster] = (t, time);
                    return true;
                }
                case ArmsOp.HeavenCarve:
                    world.RemoveCells(ReadCells(ref r), null, false, null);
                    return true;
                case ArmsOp.SpinSmash:
                {
                    var cells = ReadCells(ref r);
                    r.ReadValueSafe(out Vector3 dir);
                    world.RemoveCells(cells, removed, true, islands);
                    if (removed.Count > 0)
                    {
                        Vector3 mid = Vector3.zero;
                        foreach (var rc in removed) mid += world.CellCenter(rc.cell);
                        mid /= removed.Count;
                        // smashed: flung on ahead and out to either side, tumbling
                        Debris.Spawn(world, removed, mid - dir * 0.6f, 9f, dir * 10f + Vector3.up * 5f, 2, null, 40);
                        if (!Debris.Suppressed && Random.value < 0.5f)
                            EnergyBurst.Create(mid, new EnergyBurst.Settings
                            {
                                flashSize = 0.5f, flashTime = 0.08f, sparkCount = 5, sparkSize = 0.14f, sparkSpeed = new Vector2(3f, 7f), sparkLife = 0.22f, colour = HammerVfx.Hot,
                            });
                    }
                    foreach (var island in islands)
                    {
                        if (island.Count == 0) continue;
                        Vector3 m = Vector3.zero;
                        foreach (var rc in island) m += world.CellCenter(rc.cell);
                        Debris.Spawn(world, island, m / island.Count - dir, 2f, dir * 6f, 4);
                    }
                    return true;
                }
                case ArmsOp.ParryDeflect:
                {
                    r.ReadValueSafe(out Vector3 point); r.ReadValueSafe(out Vector3 from);
                    ParryVfx.Deflect(point, from);
                    return true;
                }
                case ArmsOp.ParryStrike:
                {
                    r.ReadValueSafe(out Vector3 point); r.ReadValueSafe(out Vector3 dir);
                    ParryVfx.Strike(point, dir);
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ clean-up (every peer)

        void AnimateArms(double now)
        {
            _done.Clear();
            foreach (var b in WolfBullets.Values) if (!b.Alive && now - b.ended > 0.5 || now - b.start > 6.0) _done.Add(b.id);
            foreach (var id in _done) WolfBullets.Remove(id);
            _done.Clear();
            foreach (var h in Heavens.Values) if (now - h.start > SpellPlans.HeavenRange / SpellPlans.HeavenSpeed + 1.0) _done.Add(h.id);
            foreach (var id in _done) Heavens.Remove(id);
            _done.Clear();
        }

        void ClearArms()
        {
            foreach (var b in WolfBullets.Values) if (b.view) Destroy(b.view.gameObject);
            foreach (var h in Heavens.Values) if (h.view) Destroy(h.view.gameObject);
            WolfBullets.Clear();
            Heavens.Clear();
            HeavenRolls.Clear();
            Spins.Clear();
            BulletHole.ClearAll();
        }
    }
}
