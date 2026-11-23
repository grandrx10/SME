using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The op stream's part for Rachmaninoff, Sebastian and Undying Grasp. The server decides; every peer applies the same ops in order.</summary>
    public partial class WorldNet
    {
        /// <summary>One pellet, flying straight from <see cref="origin"/> along <see cref="dir"/> since <see cref="t"/>.</summary>
        public class PelletBit
        {
            public Vector3 origin, dir, end; public double t, ended = -1;
            public bool Alive => ended < 0;
        }
        /// <summary>One blast of Rachmaninoff.</summary>
        public class PelletsFx { public int id; public double start; public PelletBit[] bits; public PelletsView view; }

        public readonly Dictionary<int, PelletsFx> Pellets = new();

        /// <summary>Where a pellet is.</summary>
        public static Vector3 PelletPosition(PelletBit b, double now) => b.Alive
            ? b.origin + b.dir * Mathf.Min(SpellPlans.RachSpeed * Mathf.Max(0f, (float)(now - b.t)), SpellPlans.RachRange)
            : b.end;

        enum GunOp : byte { Pellets = 114, PelletTurn = 115, PelletEnd = 116, Sebastian = 117, Grasp = 118 }

        static FastBufferWriter NewOp(GunOp op, int size) => NewOp((Op)(byte)op, size);

        // ------------------------------------------------------------------ server API

        public static int ServerPellets(Vector3 origin, Vector3 dir, Vector3[] dirs)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(GunOp.Pellets, 64 + dirs.Length * 12);
            w.WriteValueSafe(id); w.WriteValueSafe(origin); w.WriteValueSafe(dir); w.WriteValueSafe(Now);
            w.WriteValueSafe((byte)dirs.Length);
            foreach (var d in dirs) w.WriteValueSafe(d);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>Pellet <paramref name="k"/> bounced off something: from here along <paramref name="dir"/>.</summary>
        public static void ServerPelletTurn(int id, int k, Vector3 origin, Vector3 dir)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(GunOp.PelletTurn, 48);
            w.WriteValueSafe(id); w.WriteValueSafe((byte)k); w.WriteValueSafe(origin); w.WriteValueSafe(dir); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Pellet <paramref name="k"/> is gone at <paramref name="point"/> (<paramref name="burst"/>: it hit something), knocking out <paramref name="cell"/> if it <paramref name="breaks"/>.</summary>
        public static void ServerPelletEnd(int id, int k, Vector3 point, Vector3Int cell, bool breaks, bool burst)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(GunOp.PelletEnd, 48);
            w.WriteValueSafe(id); w.WriteValueSafe((byte)k); w.WriteValueSafe(point); WriteCell(ref w, cell); w.WriteValueSafe(breaks); w.WriteValueSafe(burst);
            w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Sebastian's shot along <paramref name="points"/> (more than two: it bounced), vaporising these cells.</summary>
        public static void ServerSebastian(List<Vector3> points, List<Vector3Int> cells)
        {
            var i = Instance;
            if (i == null || !IsServer || points.Count < 2) return;
            var w = NewOp(GunOp.Sebastian, 32 + points.Count * 12 + cells.Count * 6);
            w.WriteValueSafe((byte)points.Count);
            foreach (var p in points) w.WriteValueSafe(p);
            WriteCells(ref w, cells);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Undying Grasp's hands shoot up out of the ground on these spots, now.</summary>
        public static void ServerGrasp(List<SpellPlans.GraspSlot> slots, Vector3 dir)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(GunOp.Grasp, 32 + slots.Count * 12);
            w.WriteValueSafe(dir); w.WriteValueSafe(Now);
            w.WriteValueSafe((byte)slots.Count);
            foreach (var s in slots) w.WriteValueSafe(s.ground);
            i.Broadcast(Finish(ref w));
        }

        // ------------------------------------------------------------------ apply (every peer)

        /// <summary>Applies one of these ops. False: not one of them.</summary>
        bool ApplyGuns(Op opRaw, ref FastBufferReader r, VoxelWorld world, List<VoxelWorld.RemovedCell> removed, List<List<VoxelWorld.RemovedCell>> islands)
        {
            switch ((GunOp)(byte)opRaw)
            {
                case GunOp.Pellets:
                {
                    var fx = new PelletsFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out Vector3 origin); r.ReadValueSafe(out Vector3 dir);
                    r.ReadValueSafe(out fx.start);
                    r.ReadValueSafe(out byte n);
                    fx.bits = new PelletBit[n];
                    for (int k = 0; k < n; k++)
                    {
                        r.ReadValueSafe(out Vector3 d);
                        fx.bits[k] = new PelletBit { origin = origin, dir = d, t = fx.start };
                    }
                    Pellets[fx.id] = fx;
                    if (!Debris.Suppressed)
                    {
                        fx.view = PelletsView.Create(fx);
                        MuzzleFlash.Create(origin, dir, 1f);
                        CameraRig.ShakeFrom(origin, 0.7f, 0.3f);
                    }
                    return true;
                }
                case GunOp.PelletTurn:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out byte k); r.ReadValueSafe(out Vector3 origin); r.ReadValueSafe(out Vector3 dir);
                    r.ReadValueSafe(out double t);
                    if (Pellets.TryGetValue(id, out var fx) && k < fx.bits.Length)
                    {
                        var b = fx.bits[k];
                        b.origin = origin; b.dir = dir; b.t = t;
                    }
                    return true;
                }
                case GunOp.PelletEnd:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out byte k); r.ReadValueSafe(out Vector3 point);
                    var cell = ReadCell(ref r);
                    r.ReadValueSafe(out bool breaks); r.ReadValueSafe(out bool burst); r.ReadValueSafe(out double t);
                    Vector3 dir = Vector3.forward;
                    if (Pellets.TryGetValue(id, out var fx) && k < fx.bits.Length)
                    {
                        dir = fx.bits[k].dir;
                        fx.bits[k].ended = t;
                        fx.bits[k].end = point;
                    }
                    if (breaks)
                    {
                        world.RemoveCells(new List<Vector3Int> { cell }, removed, true, islands);
                        Debris.Spawn(world, removed, point, 3f, -dir * 2f + Vector3.up * 2f, 1);
                    }
                    if (burst && !Debris.Suppressed) PelletsView.Spark(point);
                    return true;
                }
                case GunOp.Sebastian:
                {
                    r.ReadValueSafe(out byte n);
                    var points = new List<Vector3>(n);
                    for (int k = 0; k < n; k++) { r.ReadValueSafe(out Vector3 p); points.Add(p); }
                    var cells = ReadCells(ref r);
                    world.RemoveCells(cells, removed, true, islands);
                    // erased, not broken (as Arcane Judgment), with a golden rim
                    if (removed.Count > 0 && !Debris.Suppressed) VoidRise.Spawn(world, removed, SpellPlans.SebGold);
                    if (!Debris.Suppressed) SebastianBeam.Create(points);
                    return true;
                }
                case GunOp.Grasp:
                {
                    r.ReadValueSafe(out Vector3 dir); r.ReadValueSafe(out double start);
                    r.ReadValueSafe(out byte n);
                    var spots = new List<Vector3>(n);
                    for (int k = 0; k < n; k++) { r.ReadValueSafe(out Vector3 p); spots.Add(p); }
                    if (!Debris.Suppressed) GraspView.Create(spots, dir, start);
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ clean-up (every peer)

        void AnimateGuns(double now)
        {
            _done.Clear();
            foreach (var p in Pellets.Values)
            {
                bool any = false;
                foreach (var b in p.bits) any |= b.Alive || now - b.ended < 0.5;
                if (!any || now - p.start > 20.0) _done.Add(p.id);
            }
            foreach (var id in _done) Pellets.Remove(id);
            _done.Clear();
        }

        void ClearGuns()
        {
            foreach (var p in Pellets.Values) if (p.view) Destroy(p.view.gameObject);
            Pellets.Clear();
        }
    }
}
