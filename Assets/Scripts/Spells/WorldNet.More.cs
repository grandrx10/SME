using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The op stream's part for The Hermit, Wheel of Fortune, Justice, Death, The Tower, the breaths (Flame Thrower, Icy
    /// Breath), the Grapple Hook and the Necromancy effects. The server decides; every peer applies the same ops in order.
    /// </summary>
    public partial class WorldNet
    {
        // ------------------------------------------------------------------ live effects (every peer)

        /// <summary>A Wheel of Fortune die over <see cref="caster"/>'s head, rolled at <see cref="start"/>, landing on <see cref="result"/>.</summary>
        public class WheelFx { public int id; public ulong caster; public double start; public byte result; public WheelView view; }
        /// <summary>The die thrown (a roll of 3): flies like a thrown rock from <see cref="origin"/> at <see cref="t"/>.</summary>
        public class DieFx { public int id; public Vector3 origin, velocity, end; public double t, ended = -1; public DieView view; }
        /// <summary>Justice: a tether from <see cref="caster"/> to <see cref="target"/>.</summary>
        public class JusticeFx { public int id; public ulong caster, target; public double start, ended = -1; public Vector3 endPoint; public JusticeView view; }
        /// <summary>Death's aura round <see cref="caster"/>, formed at <see cref="start"/>.</summary>
        public class DeathFx { public int id; public ulong caster; public double start, ended = -1; public DeathView view; }
        /// <summary>The Tower: rising from <see cref="start"/>, turning (by <see cref="turnBy"/>) from <see cref="turnAt"/> s, then toppling along <see cref="fallDir"/>.</summary>
        public class TowerFx
        {
            public int id; public ulong caster; public Vector3Int baseCell; public Vector3 foot; public double start;
            public float turnAt = -1f, turnBy; public Vector3 fallDir = Vector3.forward;
            public VoxelBody body;
            public void Pose(double now, out Vector3 pivot, out Quaternion rot, out float fallAngle) =>
                SpellPlans.TowerPose(foot, (float)(now - start), turnAt, turnBy, fallDir, out pivot, out rot, out fallAngle);
        }
        /// <summary>A breath: <see cref="kind"/> 0 the Flame Thrower (from the clasped hands), 1 Icy Breath (from the mouth).</summary>
        public class BreathFx { public int id; public ulong caster; public byte kind; public double start, ended = -1; public BreathView view; }
        /// <summary>
        /// The Grapple Hook: thrown from <see cref="origin"/> along <see cref="dir"/> at <see cref="start"/>; stuck at
        /// <see cref="point"/> (or in <see cref="target"/>) from <see cref="attachAt"/>.
        /// </summary>
        public class HookFx
        {
            public int id; public ulong caster, target; public Vector3 origin, dir, point; public double start, attachAt = -1, ended = -1;
            public HookView view;
            public Vector3 Tip(double now)
            {
                if (attachAt >= 0 && now >= attachAt)
                {
                    var t = target != 0 ? FindCombatant(target) : null;
                    return t != null ? SpellPlans.LoversChest(t) : point;
                }
                return origin + dir * (SpellPlans.HookSpeed * Mathf.Max(0f, (float)(now - start)));
            }
        }

        public readonly Dictionary<int, WheelFx> Wheels = new();
        public readonly Dictionary<int, DieFx> Dice = new();
        public readonly Dictionary<int, JusticeFx> Justices = new();
        public readonly Dictionary<int, DeathFx> DeathAuras = new();
        public readonly Dictionary<int, TowerFx> Towers = new();
        public readonly Dictionary<int, BreathFx> Breaths = new();
        public readonly Dictionary<int, HookFx> Hooks = new();

        /// <summary>(caster object id, kind) — a breath ended (let go, out of mana, knocked down): its owner stops.</summary>
        public static event Action<ulong, byte> BreathEnded;
        /// <summary>(hook id, caster object id, point, target object id or 0) — a hook stuck: its owner is reeled in (a surface).</summary>
        public static event Action<int, ulong, Vector3, ulong> HookAttached;
        /// <summary>(hook id, caster object id) — a hook let go.</summary>
        public static event Action<int, ulong> HookEnded;

        /// <summary>Where a thrown die is (it flies like Rock Barrage's rock).</summary>
        public static Vector3 DiePosition(DieFx d, double now)
        {
            float t = Mathf.Max(0f, (float)(now - d.t));
            return d.origin + d.velocity * t + 0.5f * Vector3.down * SpellPlans.RockGravity * t * t;
        }

        /// <summary>This player's breath still going, if any.</summary>
        public BreathFx ActiveBreath(ulong caster)
        {
            foreach (var b in Breaths.Values) if (b.caster == caster && b.ended < 0) return b;
            return null;
        }

        /// <summary>This player's grapple hook still out, if any.</summary>
        public HookFx ActiveHook(ulong caster)
        {
            foreach (var h in Hooks.Values) if (h.caster == caster && h.ended < 0) return h;
            return null;
        }

        /// <summary>This player's Death aura still going, if any.</summary>
        public DeathFx ActiveDeath(ulong caster)
        {
            foreach (var d in DeathAuras.Values) if (d.caster == caster && d.ended < 0) return d;
            return null;
        }

        /// <summary>This player's Wheel of Fortune die still over their head.</summary>
        public bool DieOverHead(ulong caster, double now)
        {
            foreach (var w in Wheels.Values) if (w.caster == caster && now < w.start + SpellPlans.WheelBusy) return true;
            return false;
        }

        enum MoreOp : byte
        {
            Body = 85, Wheel = 86, DieThrow = 87, DieEnd = 88, Justice = 89, JusticeEnd = 90, DeathAura = 91, DeathAuraEnd = 92,
            Tower = 93, TowerTurn = 94, TowerCarve = 95, TowerBreak = 96, Breath = 97, BreathEnd = 98,
            CellsSinge = 99, CellsBurn = 100, IceCoat = 101, Hook = 102, HookAttach = 103, HookEnd = 104, NecroFx = 105, IceThaw = 130,
        }

        static FastBufferWriter NewOp(MoreOp op, int size) => NewOp((Op)(byte)op, size);

        // ------------------------------------------------------------------ server API

        /// <summary>
        /// A body of ground moving into place over <paramref name="time"/>: from <paramref name="rise"/> cells below it
        /// (negative: coming down from above). Set into the world with <see cref="ServerMerge"/>.
        /// </summary>
        public static int ServerBody(List<Vector3Int> cells, List<ushort> values, int rise, float time)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return 0;
            int id = i._nextId++;
            var w = NewOp(MoreOp.Body, 32 + cells.Count * 8);
            w.WriteValueSafe(id); w.WriteValueSafe(Now); w.WriteValueSafe((short)rise); w.WriteValueSafe(time);
            WriteCells(ref w, cells);
            WriteValues(ref w, values);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static int ServerWheel(ulong caster, double start, byte result)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(MoreOp.Wheel, 32);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(start); w.WriteValueSafe(result);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static int ServerDieThrow(Vector3 origin, Vector3 velocity)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(MoreOp.DieThrow, 48);
            w.WriteValueSafe(id); w.WriteValueSafe(origin); w.WriteValueSafe(velocity); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>The thrown die breaks at <paramref name="point"/>, knocking out <paramref name="r4"/> round <paramref name="cell"/>.</summary>
        public static void ServerDieEnd(int id, Vector3 point, Vector3Int cell, int r4)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(MoreOp.DieEnd, 40);
            w.WriteValueSafe(id); w.WriteValueSafe(point); WriteCell(ref w, cell); w.WriteValueSafe((byte)Mathf.Clamp(r4, 0, 255));
            i.Broadcast(Finish(ref w));
        }

        public static int ServerJustice(ulong caster, ulong target, double start)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(MoreOp.Justice, 40);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(target); w.WriteValueSafe(start);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static void ServerJusticeEnd(int id, Vector3 point)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(MoreOp.JusticeEnd, 24);
            w.WriteValueSafe(id); w.WriteValueSafe(point); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        public static int ServerDeathAura(ulong caster, double start)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(MoreOp.DeathAura, 32);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(start);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static void ServerDeathAuraEnd(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(MoreOp.DeathAuraEnd, 16);
            w.WriteValueSafe(id); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>The Tower rises out of the ground at <paramref name="baseCell"/> (every peer builds the same shape).</summary>
        public static int ServerTower(ulong caster, Vector3Int baseCell, double start, ushort wall, ushort trim)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(MoreOp.Tower, 40);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); WriteCell(ref w, baseCell); w.WriteValueSafe(start);
            w.WriteValueSafe(wall); w.WriteValueSafe(trim);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static void ServerTowerTurn(int id, float turnAt, float turnBy, Vector3 fallDir)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(MoreOp.TowerTurn, 32);
            w.WriteValueSafe(id); w.WriteValueSafe(turnAt); w.WriteValueSafe(turnBy); w.WriteValueSafe(fallDir);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>The falling tower deletes these cells.</summary>
        public static void ServerTowerCarve(List<Vector3Int> cells)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return;
            var w = NewOp(MoreOp.TowerCarve, 24 + cells.Count * 6);
            WriteCells(ref w, cells);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerTowerBreak(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(MoreOp.TowerBreak, 8);
            w.WriteValueSafe(id);
            i.Broadcast(Finish(ref w));
        }

        public static int ServerBreath(ulong caster, byte kind, double start)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(MoreOp.Breath, 32);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(kind); w.WriteValueSafe(start);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static void ServerBreathEnd(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(MoreOp.BreathEnd, 16);
            w.WriteValueSafe(id); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>The Flame Thrower blackens these cells.</summary>
        public static void ServerCellsSinge(List<Vector3Int> cells)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return;
            var w = NewOp(MoreOp.CellsSinge, 8 + cells.Count * 6);
            WriteCells(ref w, cells);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>The Flame Thrower burns these cells away (blown on along <paramref name="dir"/>).</summary>
        public static void ServerCellsBurn(List<Vector3Int> cells, Vector3 dir)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return;
            var w = NewOp(MoreOp.CellsBurn, 20 + cells.Count * 6);
            WriteCells(ref w, cells);
            w.WriteValueSafe(dir);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Icy Breath frosts these cells over: whichever are still breakable ground become ice.</summary>
        public static void ServerIceCoat(List<Vector3Int> cells)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return;
            var w = NewOp(MoreOp.IceCoat, 8 + cells.Count * 6);
            WriteCells(ref w, cells);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>The frost melts off these cells: whichever are still ice go back to what they were.</summary>
        public static void ServerIceThaw(List<Vector3Int> cells, List<ushort> was)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return;
            var w = NewOp(MoreOp.IceThaw, 16 + cells.Count * 8);
            WriteCells(ref w, cells);
            WriteValues(ref w, was);
            i.Broadcast(Finish(ref w));
        }

        public static int ServerHook(ulong caster, Vector3 origin, Vector3 dir, double start)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(MoreOp.Hook, 48);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(origin); w.WriteValueSafe(dir); w.WriteValueSafe(start);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>The hook stuck in a surface at <paramref name="point"/>, or in <paramref name="target"/> (object id; 0 = a surface).</summary>
        public static void ServerHookAttach(int id, Vector3 point, ulong target)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(MoreOp.HookAttach, 40);
            w.WriteValueSafe(id); w.WriteValueSafe(point); w.WriteValueSafe(target); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerHookEnd(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(MoreOp.HookEnd, 16);
            w.WriteValueSafe(id); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>
        /// A Necromancy (or Devil) effect to show. <paramref name="kind"/>: 0 green symbols from <paramref name="a"/>'s
        /// hands and <paramref name="b"/>'s corpse while raising it; 1 the ground bursting at <paramref name="point"/> as a
        /// zombie climbs out; 2 The Devil's bleeding on <paramref name="a"/>; 3 a bite landing at <paramref name="point"/>;
        /// 4 grey symbols from <paramref name="a"/> and <paramref name="b"/>'s corpse while its skin is taken; 5 it has been (the corpse is gone).
        /// </summary>
        public static void ServerNecroFx(byte kind, ulong a, ulong b, Vector3 point, double start)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(MoreOp.NecroFx, 48);
            w.WriteValueSafe(kind); w.WriteValueSafe(a); w.WriteValueSafe(b); w.WriteValueSafe(point); w.WriteValueSafe(start);
            i.Broadcast(Finish(ref w));
        }

        // ------------------------------------------------------------------ apply (every peer)

        /// <summary>Applies one of these ops. False: not one of them (the main switch handles it).</summary>
        bool ApplyMore(Op opRaw, ref FastBufferReader r, VoxelWorld world, List<VoxelWorld.RemovedCell> removed, List<List<VoxelWorld.RemovedCell>> islands)
        {
            switch ((MoreOp)(byte)opRaw)
            {
                case MoreOp.Body:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out double start); r.ReadValueSafe(out short rise); r.ReadValueSafe(out float time);
                    var cells = ReadCells(ref r);
                    var values = ReadValues(ref r);
                    var bodyCells = Zip(cells, values);
                    Vector3 pivot = bodyCells.Count > 0 ? world.CellCenter(bodyCells[0].cell) : world.Origin;
                    var fx = new WallFx { id = id, start = start, rise = rise, cells = cells, values = values, time = Mathf.Max(0.05f, time) };
                    if (bodyCells.Count > 0) fx.body = VoxelBody.Create(world, id, bodyCells, pivot);
                    Walls[id] = fx;
                    AnimateWall(fx);
                    return true;
                }
                case MoreOp.Wheel:
                {
                    var fx = new WheelFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.start); r.ReadValueSafe(out fx.result);
                    if (!Debris.Suppressed) fx.view = WheelView.Create(fx);
                    Wheels[fx.id] = fx;
                    return true;
                }
                case MoreOp.DieThrow:
                {
                    var fx = new DieFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.origin); r.ReadValueSafe(out fx.velocity); r.ReadValueSafe(out fx.t);
                    if (!Debris.Suppressed) fx.view = DieView.Create(fx);
                    Dice[fx.id] = fx;
                    return true;
                }
                case MoreOp.DieEnd:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 point);
                    var c = ReadCell(ref r);
                    r.ReadValueSafe(out byte r4);
                    if (Dice.TryGetValue(id, out var fx)) { fx.ended = Now; fx.end = point; }
                    if (r4 > 0)
                    {
                        world.RemoveSphere(c, r4, removed, islands);
                        Debris.Spawn(world, removed, point, 6f, Vector3.zero, 2);
                    }
                    if (!Debris.Suppressed)
                    {
                        SpellFx.Puff(point, new Color(0.55f, 0.45f, 0.35f), 1f);
                        HierophantView.Spark(point, 1f);
                        CameraRig.ShakeFrom(point, 0.25f, 0.2f);
                    }
                    return true;
                }
                case MoreOp.Justice:
                {
                    var fx = new JusticeFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.target); r.ReadValueSafe(out fx.start);
                    if (!Debris.Suppressed) fx.view = JusticeView.Create(fx);
                    Justices[fx.id] = fx;
                    return true;
                }
                case MoreOp.JusticeEnd:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 point); r.ReadValueSafe(out double t);
                    if (Justices.TryGetValue(id, out var fx)) { fx.ended = t; fx.endPoint = point; }
                    return true;
                }
                case MoreOp.DeathAura:
                {
                    var fx = new DeathFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.start);
                    if (!Debris.Suppressed) fx.view = DeathView.Create(fx);
                    DeathAuras[fx.id] = fx;
                    return true;
                }
                case MoreOp.DeathAuraEnd:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out double t);
                    if (DeathAuras.TryGetValue(id, out var fx)) fx.ended = t;
                    return true;
                }
                case MoreOp.Tower:
                {
                    var fx = new TowerFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster);
                    fx.baseCell = ReadCell(ref r);
                    r.ReadValueSafe(out fx.start);
                    r.ReadValueSafe(out ushort wall); r.ReadValueSafe(out ushort trim);
                    fx.foot = SpellPlans.TowerFoot(world, fx.baseCell);
                    // whatever stands where it will rise is smashed out of the way first
                    var clear = TowerClearCells(world, fx.baseCell);
                    world.RemoveCells(clear, removed, true, islands);
                    Debris.Spawn(world, removed, fx.foot + Vector3.up * 2f, 4f, Vector3.up * 3f, 2, null, 120);
                    var shape = SpellPlans.TowerShape();
                    var cells = new List<VoxelWorld.RemovedCell>(shape.Count);
                    int capFrom = SpellPlans.TowerHeight - SpellPlans.TowerCapHeight;
                    foreach (var o in shape)
                        cells.Add(new VoxelWorld.RemovedCell { cell = fx.baseCell + o, value = o.y > capFrom || o.y <= 2 ? trim : wall });
                    fx.body = VoxelBody.Create(world, fx.id, cells, fx.foot);
                    Towers[fx.id] = fx;
                    AnimateTower(fx, Now);
                    if (!Debris.Suppressed) CameraRig.ShakeFrom(fx.foot, 0.5f, SpellPlans.TowerRise);
                    return true;
                }
                case MoreOp.TowerTurn:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out float turnAt); r.ReadValueSafe(out float turnBy); r.ReadValueSafe(out Vector3 dir);
                    if (Towers.TryGetValue(id, out var fx)) { fx.turnAt = turnAt; fx.turnBy = turnBy; fx.fallDir = dir; }
                    return true;
                }
                case MoreOp.TowerCarve:
                {
                    // what it falls through is simply gone: no debris (and no scraps cut loose); only big pieces left hanging fall
                    world.RemoveCells(ReadCells(ref r), null, true, islands);
                    islands.RemoveAll(i => i.Count < SpellPlans.TowerScrap);
                    return true;
                }
                case MoreOp.TowerBreak:
                {
                    r.ReadValueSafe(out int id);
                    if (!Towers.TryGetValue(id, out var fx)) return true;
                    Towers.Remove(id);
                    if (fx.body == null) return true;
                    AnimateTower(fx, fx.start + SpellPlans.TowerRise + 10.0); // lying flat
                    BreakTower(world, fx);
                    Destroy(fx.body.gameObject);
                    return true;
                }
                case MoreOp.Breath:
                {
                    var fx = new BreathFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.kind); r.ReadValueSafe(out fx.start);
                    if (!Debris.Suppressed) fx.view = BreathView.Create(fx);
                    Breaths[fx.id] = fx;
                    return true;
                }
                case MoreOp.BreathEnd:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out double t);
                    if (Breaths.TryGetValue(id, out var fx) && fx.ended < 0)
                    {
                        fx.ended = t;
                        BreathEnded?.Invoke(fx.caster, fx.kind);
                    }
                    return true;
                }
                case MoreOp.CellsSinge:
                {
                    var cells = ReadCells(ref r);
                    foreach (var c in cells)
                    {
                        ushort v = world.Get(c);
                        if (v != 0 && !world.IsIndestructible(v)) world.Set(c, world.Scorched(v));
                    }
                    return true;
                }
                case MoreOp.CellsBurn:
                {
                    var cells = ReadCells(ref r);
                    r.ReadValueSafe(out Vector3 dir);
                    world.RemoveCells(cells, removed, true, islands);
                    if (removed.Count > 0)
                    {
                        // it burns away black
                        for (int s = 0; s < removed.Count; s++)
                        {
                            var t = removed[s];
                            t.value = world.Scorched(t.value);
                            removed[s] = t;
                        }
                        Vector3 mid = world.CellCenter(removed[0].cell);
                        Debris.Spawn(world, removed, mid - dir * 0.3f, 2f, dir * 3f + Vector3.up * 1.5f, 1, null, 8);
                    }
                    return true;
                }
                case MoreOp.IceCoat:
                {
                    var cells = ReadCells(ref r);
                    foreach (var c in cells)
                    {
                        ushort v = world.Get(c);
                        if (v == 0 || world.IsIndestructible(v) || world.IsIce(v)) continue;
                        // the same shade on every peer: from the cell's position
                        world.Set(c, world.IceValue((byte)(((c.x * 73856093) ^ (c.y * 19349663) ^ (c.z * 83492791)) & 1)));
                    }
                    return true;
                }
                case MoreOp.IceThaw:
                {
                    var cells = ReadCells(ref r);
                    var was = ReadValues(ref r);
                    for (int k = 0; k < cells.Count && k < was.Count; k++) world.Thaw(cells[k], was[k]);
                    return true;
                }
                case MoreOp.Hook:
                {
                    var fx = new HookFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.origin); r.ReadValueSafe(out fx.dir); r.ReadValueSafe(out fx.start);
                    if (!Debris.Suppressed) fx.view = HookView.Create(fx);
                    Hooks[fx.id] = fx;
                    return true;
                }
                case MoreOp.HookAttach:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 point); r.ReadValueSafe(out ulong target); r.ReadValueSafe(out double t);
                    if (Hooks.TryGetValue(id, out var fx))
                    {
                        fx.point = point; fx.target = target; fx.attachAt = t;
                        HookAttached?.Invoke(id, fx.caster, point, target);
                    }
                    return true;
                }
                case MoreOp.HookEnd:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out double t);
                    if (Hooks.TryGetValue(id, out var fx) && fx.ended < 0)
                    {
                        fx.ended = t;
                        HookEnded?.Invoke(id, fx.caster);
                    }
                    return true;
                }
                case MoreOp.NecroFx:
                {
                    r.ReadValueSafe(out byte kind); r.ReadValueSafe(out ulong a); r.ReadValueSafe(out ulong b);
                    r.ReadValueSafe(out Vector3 point); r.ReadValueSafe(out double start);
                    if (Debris.Suppressed) return true;
                    if (kind == 0) NecroVfx.Raising(a, b, start);
                    else if (kind == 1) NecroVfx.GroundBurst(point);
                    else if (kind == 2) NecroVfx.Bleed(a);
                    else if (kind == 3) NecroVfx.Chomp(point);
                    else if (kind == 4) NecroVfx.Skinning(a, b, start);
                    else if (kind == 5) NecroVfx.Skinned(a, b);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Breakable cells where the tower will stand (above the ground), smashed out before it rises.</summary>
        static List<Vector3Int> TowerClearCells(VoxelWorld world, Vector3Int b)
        {
            var list = new List<Vector3Int>();
            int half = SpellPlans.TowerBase / 2 + SpellPlans.TowerCapOver;
            int top = SpellPlans.TowerHeight + 3;
            for (int x = -half; x < half; x++)
            for (int z = -half; z < half; z++)
            for (int y = 1; y <= top; y++)
            {
                var p = new Vector3Int(b.x + x, b.y + y, b.z + z);
                ushort v = world.Get(p);
                if (v != 0 && !world.IsIndestructible(v)) list.Add(p);
            }
            return list;
        }

        /// <summary>The fallen tower cracks into several big pieces that tumble on.</summary>
        static void BreakTower(VoxelWorld world, TowerFx fx)
        {
            if (Debris.Suppressed) return;
            const int block = 16;
            var groups = new Dictionary<Vector3Int, List<VoxelWorld.RemovedCell>>();
            foreach (var c in fx.body.Cells)
            {
                var k = new Vector3Int(Mathf.FloorToInt(c.cell.x / (float)block), Mathf.FloorToInt(c.cell.y / (float)block), Mathf.FloorToInt(c.cell.z / (float)block));
                if (!groups.TryGetValue(k, out var g)) groups[k] = g = new List<VoxelWorld.RemovedCell>();
                g.Add(c);
            }
            Vector3 mid = fx.body.Center;
            foreach (var g in groups.Values)
                Debris.Spawn(world, g, mid + Vector3.down * 3f, 3f, fx.fallDir * 4f, block, fx.body.MeshRoot);
            SpellFx.Puff(mid, new Color(0.6f, 0.5f, 0.4f), 3f);
            CameraRig.ShakeFrom(mid, 1f, 0.8f);
        }

        void AnimateTower(TowerFx fx, double now)
        {
            if (fx.body == null) return;
            fx.Pose(now, out var pivot, out var rot, out _);
            fx.body.SetPose(pivot, rot);
        }

        // ------------------------------------------------------------------ animation and clean-up (every peer)

        void AnimateMore(double now)
        {
            foreach (var t in Towers.Values) AnimateTower(t, now);
            _done.Clear();
            foreach (var w in Wheels.Values) if (now - w.start > SpellPlans.WheelBusy + 2.0) _done.Add(w.id);
            foreach (var id in _done) Wheels.Remove(id);
            _done.Clear();
            foreach (var d in Dice.Values) if (d.ended >= 0 && now - d.ended > 1.0 || now - d.t > 8.0) _done.Add(d.id);
            foreach (var id in _done) Dice.Remove(id);
            _done.Clear();
            foreach (var j in Justices.Values) if (j.ended >= 0 && now - j.ended > 1.0 || now - j.start > SpellPlans.JusticeTime + 5.0) _done.Add(j.id);
            foreach (var id in _done) Justices.Remove(id);
            _done.Clear();
            foreach (var d in DeathAuras.Values) if (d.ended >= 0 && now - d.ended > 1.0) _done.Add(d.id);
            foreach (var id in _done) DeathAuras.Remove(id);
            _done.Clear();
            foreach (var b in Breaths.Values) if (b.ended >= 0 && now - b.ended > 1.0 || now - b.start > 120.0) _done.Add(b.id);
            foreach (var id in _done) Breaths.Remove(id);
            _done.Clear();
            foreach (var h in Hooks.Values) if (h.ended >= 0 && now - h.ended > 1.0 || now - h.start > 30.0) _done.Add(h.id);
            foreach (var id in _done) Hooks.Remove(id);
            _done.Clear();
        }

        /// <summary>A practice chamber was reset: towers and rock swarms in it go (without breaking anything new).</summary>
        void ClearMoreIn(float minX, float maxX)
        {
            bool In(Vector3 p) => p.x >= minX && p.x < maxX;
            _done.Clear();
            foreach (var t in Towers.Values) if (In(t.foot)) { if (t.body) Destroy(t.body.gameObject); _done.Add(t.id); }
            foreach (var id in _done) Towers.Remove(id);
            _done.Clear();
            foreach (var s in Swarms.Values)
                if (In(s.center))
                {
                    foreach (var rk in s.rocks) if (rk.body) Destroy(rk.body.gameObject);
                    _done.Add(s.id);
                }
            foreach (var id in _done) Swarms.Remove(id);
            _done.Clear();
        }

        void ClearMore()
        {
            foreach (var w in Wheels.Values) if (w.view) Destroy(w.view.gameObject);
            foreach (var d in Dice.Values) if (d.view) Destroy(d.view.gameObject);
            foreach (var j in Justices.Values) if (j.view) Destroy(j.view.gameObject);
            foreach (var d in DeathAuras.Values) if (d.view) Destroy(d.view.gameObject);
            foreach (var t in Towers.Values) if (t.body) Destroy(t.body.gameObject);
            foreach (var b in Breaths.Values) if (b.view) Destroy(b.view.gameObject);
            foreach (var h in Hooks.Values) if (h.view) Destroy(h.view.gameObject);
            Wheels.Clear(); Dice.Clear(); Justices.Clear(); DeathAuras.Clear(); Towers.Clear(); Breaths.Clear(); Hooks.Clear();
        }
    }
}
