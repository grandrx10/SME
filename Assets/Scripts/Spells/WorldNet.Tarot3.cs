using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The op stream's part for The Star, The Moon and The World. The server decides; every peer applies the same ops in order.</summary>
    public partial class WorldNet
    {
        // ------------------------------------------------------------------ live effects (every peer)

        /// <summary>One star: over its caster's head until launched from <see cref="origin"/> along <see cref="dir"/> at <see cref="t"/>.</summary>
        public class StarBit
        {
            public bool launched; public Vector3 origin, dir, end; public double t; public double ended = -1;
            public bool Alive => ended < 0;
        }
        /// <summary>The Star: stars over <see cref="caster"/>'s head from <see cref="start"/>, sent off one by one.</summary>
        public class StarFx
        {
            public int id; public ulong caster; public double start;
            public StarBit[] stars;
            public StarsView view;
        }
        /// <summary>The World: its sphere swelling round <see cref="cell"/> from <see cref="start"/>; over once <see cref="broken"/>.</summary>
        public class WorldFx
        {
            public int id; public ulong caster, target; public Vector3Int cell; public Vector3 center; public double start, broken = -1;
            public WorldView view;
        }

        public readonly Dictionary<int, StarFx> Stars = new();
        public readonly Dictionary<int, WorldFx> Worlds = new();

        /// <summary>Where star <paramref name="k"/> is: in its place over the head, or flying straight.</summary>
        public static Vector3 StarPosition(StarFx fx, int k, double now)
        {
            var s = fx.stars[k];
            if (!s.Alive) return s.end;
            if (s.launched) return s.origin + s.dir * Mathf.Min(SpellPlans.StarSpeed * Mathf.Max(0f, (float)(now - s.t)), SpellPlans.StarRange);
            var c = FindCombatant(fx.caster);
            return c != null ? SpellPlans.StarHome(TarotVfx.Head(c), c.ViewYaw, k, fx.stars.Length, (float)(now - fx.start)) : s.end;
        }

        /// <summary>The unbroken World a point is inside of, if any.</summary>
        public static WorldFx WorldAround(Vector3 p, float pad = 0f)
        {
            var i = Instance;
            if (i == null || i.Worlds.Count == 0) return null;
            float r = SpellPlans.WorldRadius + pad;
            foreach (var w in i.Worlds.Values)
                if (w.broken < 0 && (p - w.center).sqrMagnitude <= r * r) return w;
            return null;
        }

        /// <summary>The unbroken World this body is fighting in, if any.</summary>
        public static WorldFx WorldOf(Combatant c)
        {
            var i = Instance;
            if (i == null || c == null) return null;
            foreach (var w in i.Worlds.Values)
                if (w.broken < 0 && (w.caster == c.NetworkObjectId || w.target == c.NetworkObjectId)) return w;
            return null;
        }

        /// <summary>Is this point inside an unbroken World? (Nothing its sphere swallowed, lava or a drop, can kill there.)</summary>
        public static bool InsideWorld(Vector3 p) => WorldAround(p, 0.3f) != null;

        /// <summary>Could a World be raised over someone here? (Not from inside one, nor so close that the two would run into each other.)</summary>
        public static bool RoomForWorld(Vector3 p) =>
            WorldAround(p) == null && WorldAround(p + Vector3.up * SpellPlans.WorldLift, SpellPlans.WorldRadius + 1f) == null;

        enum Tarot3Op : byte { Stars = 106, StarLaunch = 107, StarEnd = 108, MoonFull = 109, World = 110, WorldBreak = 111, Gather = 112 }

        static FastBufferWriter NewOp(Tarot3Op op, int size) => NewOp((Op)(byte)op, size);

        // ------------------------------------------------------------------ server API

        public static int ServerStars(ulong caster, double start, int count)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Tarot3Op.Stars, 33);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(start); w.WriteValueSafe((byte)count);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>Star <paramref name="k"/> is off (or bounced off something), from here along <paramref name="dir"/>.</summary>
        public static void ServerStarLaunch(int id, int k, Vector3 origin, Vector3 dir)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Tarot3Op.StarLaunch, 48);
            w.WriteValueSafe(id); w.WriteValueSafe((byte)k); w.WriteValueSafe(origin); w.WriteValueSafe(dir); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Star <paramref name="k"/> is gone at <paramref name="point"/> (<paramref name="burst"/>: it hit something).</summary>
        public static void ServerStarEnd(int id, int k, Vector3 point, bool burst)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Tarot3Op.StarEnd, 40);
            w.WriteValueSafe(id); w.WriteValueSafe((byte)k); w.WriteValueSafe(point); w.WriteValueSafe(burst); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }


        /// <summary>Power gathers in the caster's clasped hands (or raised right one) for <paramref name="seconds"/>, unless they're knocked out of it.</summary>
        public static void ServerGather(ulong caster, bool rightHand, double start, float seconds, bool black = false)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Tarot3Op.Gather, 32);
            w.WriteValueSafe(caster); w.WriteValueSafe(rightHand); w.WriteValueSafe(start); w.WriteValueSafe(seconds); w.WriteValueSafe(black);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>The moon is full: its caster is healed.</summary>
        public static void ServerMoonFull(ulong caster)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Tarot3Op.MoonFull, 12);
            w.WriteValueSafe(caster);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>The World's sphere rises round <paramref name="cell"/> (the ground cell at its middle), made of these materials.</summary>
        public static int ServerWorld(ulong caster, ulong target, Vector3Int cell, ushort body, ushort top, double start)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Tarot3Op.World, 48);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(target); WriteCell(ref w, cell);
            w.WriteValueSafe(body); w.WriteValueSafe(top); w.WriteValueSafe(start);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>Someone in it died: the dome shatters, the rest is ordinary ground.</summary>
        public static void ServerWorldBreak(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Tarot3Op.WorldBreak, 16);
            w.WriteValueSafe(id); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        // ------------------------------------------------------------------ apply (every peer)

        /// <summary>Applies one of these ops. False: not one of them.</summary>
        bool ApplyTarot3(Op opRaw, ref FastBufferReader r, VoxelWorld world, List<VoxelWorld.RemovedCell> removed, List<List<VoxelWorld.RemovedCell>> islands)
        {
            switch ((Tarot3Op)(byte)opRaw)
            {
                case Tarot3Op.Stars:
                {
                    var fx = new StarFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.start); r.ReadValueSafe(out byte count);
                    fx.stars = new StarBit[Mathf.Clamp(count, 1, SpellPlans.StarMax)];
                    for (int k = 0; k < fx.stars.Length; k++) fx.stars[k] = new StarBit();
                    Stars[fx.id] = fx;
                    if (!Debris.Suppressed) fx.view = StarsView.Create(fx);
                    return true;
                }
                case Tarot3Op.StarLaunch:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out byte k); r.ReadValueSafe(out Vector3 origin); r.ReadValueSafe(out Vector3 dir);
                    r.ReadValueSafe(out double t);
                    if (Stars.TryGetValue(id, out var fx) && k < fx.stars.Length)
                    {
                        var s = fx.stars[k];
                        s.launched = true; s.origin = origin; s.dir = dir; s.t = t;
                    }
                    return true;
                }
                case Tarot3Op.StarEnd:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out byte k); r.ReadValueSafe(out Vector3 point); r.ReadValueSafe(out bool burst);
                    r.ReadValueSafe(out double t);
                    if (Stars.TryGetValue(id, out var fx) && k < fx.stars.Length)
                    {
                        fx.stars[k].ended = t;
                        fx.stars[k].end = point;
                        if (burst && !Debris.Suppressed) StarsView.Burst(point);
                    }
                    return true;
                }

                case Tarot3Op.Gather:
                {
                    r.ReadValueSafe(out ulong caster); r.ReadValueSafe(out bool rightHand); r.ReadValueSafe(out double start); r.ReadValueSafe(out float seconds);
                    r.ReadValueSafe(out bool black);
                    if (!Debris.Suppressed) TarotGather.Create(caster, rightHand, start, seconds, black);
                    return true;
                }
                case Tarot3Op.MoonFull:
                {
                    r.ReadValueSafe(out ulong caster);
                    if (!Debris.Suppressed) MoonDisc.Full(caster);
                    return true;
                }
                case Tarot3Op.World:
                {
                    var fx = new WorldFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.target);
                    fx.cell = ReadCell(ref r);
                    r.ReadValueSafe(out ushort body); r.ReadValueSafe(out ushort top); r.ReadValueSafe(out fx.start);
                    fx.center = world.CellCenter(fx.cell);
                    world.RaiseWorldSphere(fx.cell, SpellPlans.WorldCells, SpellPlans.WorldShell, body, top, Debris.Suppressed ? 0f : SpellPlans.WorldGrow);
                    if (!Debris.Suppressed) fx.view = WorldView.Create(fx);
                    Worlds[fx.id] = fx;
                    return true;
                }
                case Tarot3Op.WorldBreak:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out double t);
                    if (!Worlds.TryGetValue(id, out var fx) || fx.broken >= 0) return true;
                    fx.broken = t;
                    world.BreakWorldSphere(fx.cell, SpellPlans.WorldCells, Debris.Suppressed ? 0f : SpellPlans.WorldShatter, removed, islands);
                    Debris.Spawn(world, removed, fx.center, 4f, Vector3.down * 2f, 4, null, 90);
                    if (!Debris.Suppressed) WorldView.Shatter(fx.center);
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ clean-up (every peer)

        void AnimateTarot3(double now)
        {
            _done.Clear();
            foreach (var s in Stars.Values)
            {
                bool any = false;
                foreach (var b in s.stars) any |= b.Alive || now - b.ended < 1.0;
                if (!any || now - s.start > 60.0) _done.Add(s.id);
            }
            foreach (var id in _done) Stars.Remove(id);
            _done.Clear();
            foreach (var w in Worlds.Values) if (w.broken >= 0 && now - w.broken > 3.0) _done.Add(w.id);
            foreach (var id in _done) Worlds.Remove(id);
            _done.Clear();
        }

        /// <summary>A practice chamber was reset: a World in it goes with its ground.</summary>
        void ClearTarot3In(float minX, float maxX)
        {
            _done.Clear();
            foreach (var w in Worlds.Values)
                if (w.center.x >= minX && w.center.x < maxX)
                {
                    if (w.view) Destroy(w.view.gameObject);
                    _done.Add(w.id);
                }
            foreach (var id in _done) Worlds.Remove(id);
            _done.Clear();
        }

        void ClearTarot3()
        {
            foreach (var s in Stars.Values) if (s.view) Destroy(s.view.gameObject);
            foreach (var w in Worlds.Values) if (w.view) Destroy(w.view.gameObject);
            Stars.Clear();
            Worlds.Clear();
        }
    }
}
