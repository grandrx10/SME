using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The one ordered stream of world changes, decided by the server and applied identically on every peer:
    /// loading an arena, voxels breaking, walls rising, rocks being ripped out / thrown / exploding, slabs folding.
    /// Everything is sent as explicit integer cell lists, so all worlds stay identical. Debris and animation are
    /// local. The server keeps this round's ops and replays them to anyone joining late.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    public class WorldNet : MonoBehaviour
    {
        const string MsgOps = "sme_world";
        const string MsgHello = "sme_world_hello";

        enum Op : byte
        {
            LoadArena = 1, RemoveSphere = 2, Wall = 3, Merge = 4, RockGather = 5, RockLaunch = 6, RockImpact = 7, Fold = 8, RemoveBox = 9, Shatter = 10,
            PushBody = 11, Gust = 12, Tornado = 13, MissileVolley = 14, MissileLaunch = 15, MissileImpact = 16, MissileRedirect = 17,
            Barrier = 18, Vaporize = 19,
            Singe = 20, Fireball = 21, FireballLaunch = 22, FlameBurst = 23, Hellfire = 24, HellfirePoint = 25, HellfireRise = 26,
            Judgment = 27, JudgmentErase = 28, RockIgnite = 29, FireballEnd = 30,
            Priestess = 31, Domain = 32, DomainEnd = 33,
            Orb = 34, OrbEnd = 35, Quench = 36, HellfireQuench = 37, RockDouse = 38,
            Bubble = 39, BubbleHit = 40, BubbleEnd = 41, Tsunami = 42, TsunamiErase = 43, Wash = 44,
            Warp = 45, WarpEnd = 46, Portal = 47, PortalLink = 48, Swift = 49, SwiftEnd = 50,
            Fizzle = 60,
        }

        /// <summary>When each caster was last knocked out of a cast (server time), for the views to cancel what they were showing.</summary>
        static readonly Dictionary<ulong, double> FizzledAt = new();

        /// <summary>Was this caster knocked out of a cast between <paramref name="from"/> and <paramref name="until"/>?</summary>
        public static bool Fizzled(ulong caster, double from, double until) =>
            FizzledAt.TryGetValue(caster, out double t) && t >= from - 0.05 && t <= until;

        public static WorldNet Instance { get; private set; }
        public static double Now => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening
            ? NetworkManager.Singleton.ServerTime.Time : Time.timeAsDouble;
        static bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        /// <summary>(rock id, caster object id, start time) — the caster's owner uses it to time the throw.</summary>
        public static event Action<int, ulong, double> RockGathered;

        readonly List<byte[]> _log = new();
        bool _registered, _helloSent;
        int _nextId = 1;

        // ---- live effects (every peer) ----
        public class WallFx { public int id; public VoxelBody body; public double start; public int rise; public List<Vector3Int> cells; public List<ushort> values; }
        public class RockFx
        {
            public int id; public VoxelBody body; public ulong caster; public double start; public Vector3 home;
            public bool launched; public Vector3 origin, velocity; public double launchTime;
            /// <summary>Flies without rotating (ground blown off by Shove keeps its shape and facing).</summary>
            public bool noSpin;
        }
        public class FoldFx
        {
            public int id; public VoxelBody[] bodies = new VoxelBody[2]; public double start;
            public Vector3[] hinge = new Vector3[2], outward = new Vector3[2];
            public List<Vector3Int>[] final = new List<Vector3Int>[2]; public List<ushort>[] values = new List<ushort>[2];
        }
        public class GustFx { public int id; public ulong caster; public Vector3 origin, dir; public double start; public float life; public GustView view; }
        public class TornadoFx { public int id; public ulong caster; public Vector3 origin, dir; public double start; public TornadoView view; }
        public class Missile { public Vector3 pos, prev, dir; public float age; public bool alive = true, tracking = true; public ulong owner; }
        public class MissileFx
        {
            public int id; public ulong caster, target; public double start; public bool launched;
            public Missile[] m = new Missile[0]; public MissileView view;
        }
        public class BarrierFx { public ulong caster; public double start; public BarrierView view; }
        public class FireballFx
        {
            public int id; public ulong caster; public double start;
            public bool launched; public Vector3 origin, velocity; public double launchTime; public FireballView view;
            public ulong owner; public bool done;
        }
        public class HellfireFx
        {
            public int id; public ulong caster; public double start;
            public readonly List<Vector3> points = new();
            /// <summary>The caster's own every-frame copy of the line while drawing it (only on their machine): smoother than the networked points.</summary>
            public readonly List<Vector3> local = new();
            public double riseStart = -1; public HellfireView view;
            /// <summary>Put out by water at this time (-1 = still burning).</summary>
            public double quenched = -1;
        }
        public class JudgmentFx { public int id; public ulong caster; public Vector3 center; public double start; public JudgmentView view; }

        public readonly Dictionary<int, FireballFx> Fireballs = new();
        public readonly Dictionary<int, HellfireFx> Hellfires = new();
        public readonly Dictionary<int, JudgmentFx> Judgments = new();

        /// <summary>(fireball id, caster object id, start) — the caster's owner throws it when it's charged.</summary>
        public static event Action<int, ulong, double> FireballCharging;
        /// <summary>(hellfire id, caster object id) — the caster's owner draws the line.</summary>
        public static event Action<int, ulong> HellfireStarted;

        /// <summary>Where a fireball is: above its caster's raised hand while it charges, then flying straight.</summary>
        public static Vector3 FireballPosition(FireballFx f, double now)
        {
            if (f.launched) return f.origin + f.velocity * Mathf.Max(0f, (float)(now - f.launchTime));
            var c = FindCombatant(f.caster);
            return SpellVisuals.RightHand(c) + Vector3.up * 0.75f;
        }

        /// <summary>An Empress domain (kind 0, stays where it was cast) or an Emperor aura (kind 1, follows its caster).</summary>
        public class DomainFx
        {
            public int id; public byte kind; public ulong caster; public Vector3 center; public double start, ended = -1;
            public DomainView view;
        }
        public readonly Dictionary<int, DomainFx> Domains = new();

        /// <summary>A Water of Life orb. how it ended: 0 burst while forming, 1 drunk (by <see cref="who"/>), 2 left too long.</summary>
        public class OrbFx
        {
            public int id; public ulong caster; public Vector3 center; public double start, ended = -1;
            public byte how; public ulong who; public OrbView view;
        }
        /// <summary>A Bubble Away round its caster.</summary>
        public class BubbleFx
        {
            public ulong caster; public double start, ended = -1, lastHit = -10; public bool popped; public BubbleView view;
        }
        /// <summary>A Tsunami: rides the floor heights in <see cref="ground"/> from <see cref="origin"/> along <see cref="dir"/>.</summary>
        public class TsunamiFx
        {
            public int id; public ulong caster; public Vector3 origin, dir; public double start; public float[] ground;
            /// <summary>Lanes erased by Arcane Judgment (bit per lane).</summary>
            public int dead;
            public TsunamiView view;
        }
        public readonly Dictionary<int, OrbFx> Orbs = new();
        public readonly List<BubbleFx> Bubbles = new();
        public readonly Dictionary<int, TsunamiFx> Tsunamis = new();

        /// <summary>(caster object id) — a Bubble Away ended (popped or ran out); its owner drops out of it.</summary>
        public static event Action<ulong> BubbleEnded;

        /// <summary>Warp Self: a warp to <see cref="dest"/> (feet). how it ended early: 0 cancelled while charging, 1 hit while unfolding (unfolds at once).</summary>
        public class WarpFx
        {
            public int id; public ulong caster; public Vector3 dest; public double start, ended = -1; public byte how;
            public WarpView view;
        }
        /// <summary>A Warp Space portal: its opening faces along <see cref="yaw"/>. Linked to <see cref="pair"/> once the second is cast.</summary>
        public class PortalFx
        {
            public int id, pair; public ulong owner; public Vector3 center; public float yaw, pitch; public double start, linkStart = -1;
            public PortalView view;
            public Quaternion Rotation => SpellPlans.PortalRotation(yaw, pitch);
            /// <summary>The way its opening faces.</summary>
            public Vector3 Normal => Rotation * Vector3.forward;
            /// <summary>It closes at this time (the second portal restarts both portals' time).</summary>
            public double End => (linkStart >= 0 ? linkStart : start) + SpellPlans.PortalLife;
        }
        /// <summary>Arcane Swiftness on <see cref="caster"/>.</summary>
        public class SwiftFx { public ulong caster; public double start, ended = -1; public bool cancelled; public SwiftView view; }

        public readonly Dictionary<int, WarpFx> Warps = new();
        public readonly Dictionary<int, PortalFx> Portals = new();
        public readonly List<SwiftFx> Swifts = new();

        /// <summary>(warp id, caster object id, how) — a warp ended early; its owner stops it.</summary>
        public static event Action<int, ulong, byte> WarpEnded;
        /// <summary>(caster object id, cancelled) — Arcane Swiftness ended early.</summary>
        public static event Action<ulong, bool> SwiftEnded;

        /// <summary>The portal linked to this one, if both are still open.</summary>
        public PortalFx LinkedTo(PortalFx p, double now)
        {
            if (p.pair == 0 || now >= p.End || now < p.start) return null;
            return Portals.TryGetValue(p.pair, out var q) && now < q.End ? q : null;
        }

        /// <summary>
        /// Did something moving from a to b go in through a linked portal's opening? (<paramref name="ahead"/>: a ball
        /// that radius counts as in when it touches the opening; <paramref name="pad"/> shrinks the opening's edges.)
        /// Gives the portal it went into and the one it comes out of.
        /// </summary>
        public static bool PassPortal(Vector3 a, Vector3 b, float ahead, float pad, out PortalFx into, out PortalFx outOf)
        {
            into = outOf = null;
            var i = Instance;
            if (i == null || i.Portals.Count < 2) return false;
            double now = Now;
            foreach (var p in i.Portals.Values)
            {
                var q = i.LinkedTo(p, now);
                if (q == null || !SpellPlans.PortalCrossed(a, b, p.center, p.Rotation, ahead, pad)) continue;
                into = p;
                outOf = q;
                return true;
            }
            return false;
        }

        public static bool BubbleActive(BubbleFx b, double now) =>
            b.ended < 0 && now >= b.start + SpellPlans.BubbleWindup && now <= b.start + SpellPlans.BubbleWindup + SpellPlans.BubbleTime;

        public readonly Dictionary<int, WallFx> Walls = new();
        public readonly Dictionary<int, RockFx> Rocks = new();
        public readonly Dictionary<int, FoldFx> Folds = new();
        public readonly Dictionary<int, GustFx> Gusts = new();
        public readonly Dictionary<int, TornadoFx> Tornados = new();
        public readonly Dictionary<int, MissileFx> Missiles = new();
        public readonly List<BarrierFx> Barriers = new();

        /// <summary>The player or dummy with this network object id (null if gone).</summary>
        public static Combatant FindCombatant(ulong objectId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.SpawnManager == null) return null;
            return nm.SpawnManager.SpawnedObjects.TryGetValue(objectId, out var no) ? no.GetComponent<Combatant>() : null;
        }

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        // ------------------------------------------------------------------ lifecycle

        void Update()
        {
            var nm = NetworkManager.Singleton;
            bool listening = nm != null && nm.IsListening && nm.CustomMessagingManager != null;
            if (listening && !_registered)
            {
                nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgOps, OnOps);
                nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgHello, OnHello);
                _registered = true;
                _helloSent = false;
            }
            else if (!listening && _registered)
            {
                _registered = false;
                _log.Clear();
                ClearEffects();
            }
            if (!_registered) return;

            if (!nm.IsServer && nm.IsConnectedClient && !_helloSent)
            {
                _helloSent = true;
                using var w = new FastBufferWriter(4, Allocator.Temp);
                w.WriteValueSafe((byte)1);
                nm.CustomMessagingManager.SendNamedMessage(MsgHello, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableFragmentedSequenced);
            }

            Animate();
            if (nm.IsServer) SpellServer.Tick(this);
        }

        void OnHello(ulong sender, FastBufferReader r)
        {
            if (!IsServer || _log.Count == 0) return;
            // in pieces that fit comfortably in one transport message
            var batch = new List<byte[]>();
            int size = 0;
            foreach (var op in _log)
            {
                if (batch.Count > 0 && size + op.Length > 96 * 1024)
                {
                    SendBatch(sender, batch, true);
                    batch = new List<byte[]>();
                    size = 0;
                }
                batch.Add(op);
                size += op.Length + 4;
            }
            if (batch.Count > 0) SendBatch(sender, batch, true);
        }

        void OnOps(ulong sender, FastBufferReader r)
        {
            if (IsServer || sender != NetworkManager.ServerClientId) return;
            try
            {
                r.ReadValueSafe(out ushort count);
                r.ReadValueSafe(out bool replay);
                Debris.Suppressed = replay;
                for (int i = 0; i < count; i++)
                {
                    r.ReadValueSafe(out int len);
                    var bytes = new byte[len];
                    r.ReadBytesSafe(ref bytes, len);
                    ApplyBytes(bytes);
                }
            }
            catch (Exception e) { Debug.LogWarning("[SME] bad world message: " + e); }
            finally { Debris.Suppressed = false; }
        }

        void SendBatch(ulong client, List<byte[]> ops, bool replay)
        {
            var nm = NetworkManager.Singleton;
            if (client == nm.LocalClientId) return;
            int size = 16;
            foreach (var o in ops) size += o.Length + 4;
            using var w = new FastBufferWriter(size, Allocator.Temp, size + 64);
            w.WriteValueSafe((ushort)ops.Count);
            w.WriteValueSafe(replay); // history for a late joiner: no rubble
            foreach (var o in ops)
            {
                w.WriteValueSafe(o.Length);
                w.WriteBytesSafe(o, o.Length);
            }
            nm.CustomMessagingManager.SendNamedMessage(MsgOps, client, w, NetworkDelivery.ReliableFragmentedSequenced);
        }

        /// <summary>Server: record, send to everyone, apply here.</summary>
        void Broadcast(byte[] op)
        {
            _log.Add(op);
            var nm = NetworkManager.Singleton;
            var one = new List<byte[]> { op };
            foreach (var c in nm.ConnectedClientsIds)
                if (c != nm.LocalClientId) SendBatch(c, one, false);
            ApplyBytes(op);
        }

        void ApplyBytes(byte[] bytes)
        {
            using var r = new FastBufferReader(bytes, Allocator.Temp);
            Apply(r);
        }

        // ------------------------------------------------------------------ server API

        static FastBufferWriter NewOp(Op op, int size)
        {
            var w = new FastBufferWriter(size, Allocator.Temp, 1 << 20);
            w.WriteValueSafe((byte)op);
            return w;
        }

        static byte[] Finish(ref FastBufferWriter w)
        {
            var bytes = w.ToArray();
            w.Dispose();
            return bytes;
        }

        public static void ServerLoadArena(int arena, int round)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            i._log.Clear();
            i._nextId = 1;
            var w = NewOp(Op.LoadArena, 16);
            w.WriteValueSafe((short)arena);
            w.WriteValueSafe(round);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerRemoveSphere(Vector3Int center, int r4, float push, Vector3 extraVelocity)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.RemoveSphere, 32);
            WriteCell(ref w, center);
            w.WriteValueSafe((byte)Mathf.Clamp(r4, 1, 255));
            w.WriteValueSafe((byte)Mathf.Clamp(Mathf.RoundToInt(push), 0, 255));
            w.WriteValueSafe(extraVelocity);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Breaks every destructible cell in the box (inclusive), e.g. to make room for someone to stand up.</summary>
        public static void ServerRemoveBox(Vector3Int min, Vector3Int max, float push)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.RemoveBox, 24);
            WriteCell(ref w, min);
            WriteCell(ref w, max);
            w.WriteValueSafe((byte)Mathf.Clamp(Mathf.RoundToInt(push), 0, 255));
            i.Broadcast(Finish(ref w));
        }

        /// <summary>A folding attack closed on someone: its slabs burst into rubble instead of setting into walls.</summary>
        public static void ServerShatter(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.Shatter, 8);
            w.WriteValueSafe(id);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Shove hit summoned ground: these cells come loose and fly off as one lump.</summary>
        public static int ServerPushBody(ulong caster, List<VoxelWorld.RemovedCell> cells, Vector3 pivot, Vector3 velocity)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.PushBody, 64 + cells.Count * 8);
            w.WriteValueSafe(id);
            w.WriteValueSafe(caster);
            w.WriteValueSafe(cells.Count);
            foreach (var c in cells) { WriteCell(ref w, c.cell); w.WriteValueSafe(c.value); }
            w.WriteValueSafe(pivot);
            w.WriteValueSafe(velocity);
            w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static int ServerGust(ulong caster, Vector3 origin, Vector3 dir, float life)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.Gust, 52);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(origin); w.WriteValueSafe(dir); w.WriteValueSafe(Now); w.WriteValueSafe(life);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static int ServerTornado(ulong caster, Vector3 origin, Vector3 dir)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.Tornado, 48);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(origin); w.WriteValueSafe(dir); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static int ServerMissileVolley(ulong caster, ulong target)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.MissileVolley, 32);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(target); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static void ServerMissileLaunch(int id, Vector3 origin, Vector3[] dirs)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.MissileLaunch, 32 + dirs.Length * 12);
            w.WriteValueSafe(id); w.WriteValueSafe(origin); w.WriteValueSafe((byte)dirs.Length);
            foreach (var d in dirs) w.WriteValueSafe(d);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerMissileImpact(int id, int index, Vector3 point, Vector3Int cell, int r4, bool vaporized)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.MissileImpact, 40);
            w.WriteValueSafe(id); w.WriteValueSafe((byte)index); w.WriteValueSafe(point); WriteCell(ref w, cell);
            w.WriteValueSafe((byte)r4); w.WriteValueSafe(vaporized);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerMissileRedirect(int id, int index, Vector3 pos, Vector3 dir, ulong newOwner)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.MissileRedirect, 48);
            w.WriteValueSafe(id); w.WriteValueSafe((byte)index); w.WriteValueSafe(pos); w.WriteValueSafe(dir); w.WriteValueSafe(newOwner);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerBarrier(ulong caster)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.Barrier, 24);
            w.WriteValueSafe(caster); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Summoned ground that touched a barrier: gone without a trace.</summary>
        public static void ServerVaporize(List<Vector3Int> cells)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return;
            var w = NewOp(Op.Vaporize, 16 + cells.Count * 6);
            WriteCells(ref w, cells);
            i.Broadcast(Finish(ref w));
        }

        // ---------------- flame ----------------

        /// <summary>Scorch the exposed voxels round a point black.</summary>
        public static void ServerSinge(Vector3Int center, int r4, bool all = false)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.Singe, 16);
            WriteCell(ref w, center);
            w.WriteValueSafe((byte)Mathf.Clamp(r4, 1, 255));
            w.WriteValueSafe(all);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>A cartoon fire explosion (look only; damage and voxels are separate ops).</summary>
        public static void ServerFlameBurst(Vector3 point, float scale)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.FlameBurst, 20);
            w.WriteValueSafe(point);
            w.WriteValueSafe(scale);
            i.Broadcast(Finish(ref w));
        }

        public static int ServerFireball(ulong caster)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.Fireball, 24);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>Throws (or re-throws, when a gust turns it) a fireball from <paramref name="origin"/>.</summary>
        public static void ServerFireballLaunch(int id, Vector3 origin, Vector3 velocity, ulong owner)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.FireballLaunch, 48);
            w.WriteValueSafe(id); w.WriteValueSafe(origin); w.WriteValueSafe(velocity); w.WriteValueSafe(Now); w.WriteValueSafe(owner);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerFireballEnd(int id, Vector3 point, bool vaporized)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.FireballEnd, 24);
            w.WriteValueSafe(id); w.WriteValueSafe(point); w.WriteValueSafe(vaporized);
            i.Broadcast(Finish(ref w));
        }

        public static int ServerHellfire(ulong caster)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.Hellfire, 24);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static void ServerHellfirePoint(int id, Vector3 point)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.HellfirePoint, 20);
            w.WriteValueSafe(id); w.WriteValueSafe(point);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerHellfireRise(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.HellfireRise, 16);
            w.WriteValueSafe(id); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        // ---------------- arcane judgment ----------------

        public static int ServerJudgment(ulong caster, Vector3 center)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.Judgment, 40);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(center); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>The caster was knocked down mid-cast: views cancel whatever they were still casting.</summary>
        public static void ServerFizzle(ulong caster)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.Fizzle, 24);
            w.WriteValueSafe(caster); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Takes one layer off the top of the judgment's cylinder.</summary>
        public static void ServerJudgmentErase(Vector3Int center, int radius, int yMin, int yMax)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.JudgmentErase, 24);
            WriteCell(ref w, center);
            w.WriteValueSafe((short)radius); w.WriteValueSafe((short)yMin); w.WriteValueSafe((short)yMax);
            i.Broadcast(Finish(ref w));
        }

        // ---------------- tarot ----------------

        public static void ServerPriestess(ulong caster)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.Priestess, 20);
            w.WriteValueSafe(caster); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        public static int ServerDomain(byte kind, ulong caster, Vector3 center, double start)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.Domain, 40);
            w.WriteValueSafe(id); w.WriteValueSafe(kind); w.WriteValueSafe(caster); w.WriteValueSafe(center); w.WriteValueSafe(start);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static void ServerDomainEnd(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.DomainEnd, 16);
            w.WriteValueSafe(id); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        // ---------------- water ----------------

        public static int ServerOrb(ulong caster, Vector3 center)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.Orb, 40);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(center); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <param name="how">0 burst while forming, 1 drunk by <paramref name="who"/>, 2 left too long.</param>
        public static void ServerOrbEnd(int id, byte how, ulong who)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.OrbEnd, 16);
            w.WriteValueSafe(id); w.WriteValueSafe(how); w.WriteValueSafe(who);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>Fire put out by water here: a hiss of steam (look only).</summary>
        public static void ServerQuench(Vector3 point, float scale)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.Quench, 20);
            w.WriteValueSafe(point); w.WriteValueSafe(scale);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerHellfireQuench(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.HellfireQuench, 16);
            w.WriteValueSafe(id); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>A burning lump of ground went through water: it's just ground again.</summary>
        public static void ServerRockDouse(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.RockDouse, 8);
            w.WriteValueSafe(id);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerBubble(ulong caster)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.Bubble, 20);
            w.WriteValueSafe(caster); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerBubbleHit(ulong caster, Vector3 point)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.BubbleHit, 24);
            w.WriteValueSafe(caster); w.WriteValueSafe(point);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerBubbleEnd(ulong caster, bool popped)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.BubbleEnd, 24);
            w.WriteValueSafe(caster); w.WriteValueSafe(Now); w.WriteValueSafe(popped);
            i.Broadcast(Finish(ref w));
        }

        public static int ServerTsunami(ulong caster, Vector3 origin, Vector3 dir, float[] ground)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.Tsunami, 64 + ground.Length * 4);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(origin); w.WriteValueSafe(dir); w.WriteValueSafe(Now);
            w.WriteValueSafe((short)ground.Length);
            foreach (var g in ground) w.WriteValueSafe(g);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static void ServerTsunamiErase(int id, int dead)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.TsunamiErase, 12);
            w.WriteValueSafe(id); w.WriteValueSafe(dead);
            i.Broadcast(Finish(ref w));
        }

        // ---------------- arcane movement ----------------

        public static int ServerWarp(ulong caster, Vector3 dest)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.Warp, 40);
            w.WriteValueSafe(id); w.WriteValueSafe(caster); w.WriteValueSafe(dest); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <param name="how">0 cancelled while charging, 1 hit while unfolding.</param>
        public static void ServerWarpEnd(int id, byte how)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.WarpEnd, 16);
            w.WriteValueSafe(id); w.WriteValueSafe(how); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        public static int ServerPortal(ulong owner, Vector3 center, float yaw, float pitch)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.Portal, 44);
            w.WriteValueSafe(id); w.WriteValueSafe(owner); w.WriteValueSafe(center); w.WriteValueSafe(yaw); w.WriteValueSafe(pitch); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static void ServerPortalLink(int a, int b)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.PortalLink, 20);
            w.WriteValueSafe(a); w.WriteValueSafe(b); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerSwift(ulong caster)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.Swift, 20);
            w.WriteValueSafe(caster); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        public static void ServerSwiftEnd(ulong caster, bool cancelled)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.SwiftEnd, 24);
            w.WriteValueSafe(caster); w.WriteValueSafe(Now); w.WriteValueSafe(cancelled);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>A wave smashed these cells: broken, the rubble washed along <paramref name="dir"/>.</summary>
        public static void ServerWash(List<Vector3Int> cells, Vector3 dir)
        {
            var i = Instance;
            if (i == null || !IsServer || cells.Count == 0) return;
            var w = NewOp(Op.Wash, 32 + cells.Count * 6);
            WriteCells(ref w, cells);
            w.WriteValueSafe(dir);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>A flying lump of ground went through fire: it's burning now.</summary>
        public static void ServerRockIgnite(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.RockIgnite, 8);
            w.WriteValueSafe(id);
            i.Broadcast(Finish(ref w));
        }

        /// <summary>The cells of a box, for <see cref="ServerRemoveBox"/> and its client-side prediction.</summary>
        public static List<Vector3Int> BoxCells(Vector3Int min, Vector3Int max)
        {
            var list = new List<Vector3Int>();
            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
                list.Add(new Vector3Int(x, y, z));
            return list;
        }

        /// <param name="rise">How many cells the body starts below its final place (it rises that far).</param>
        public static int ServerWall(SpellPlans.WallPlan plan, int rise = SpellPlans.WallHeight + 1)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.Wall, 64 + plan.cells.Count * 8 + (plan.lifted.Count + plan.trench.Count) * 6);
            w.WriteValueSafe(id);
            w.WriteValueSafe(Now);
            w.WriteValueSafe((byte)Mathf.Clamp(rise, 1, 255));
            WriteCells(ref w, plan.trench);
            WriteCells(ref w, plan.lifted);
            WriteCells(ref w, plan.cells);
            WriteValues(ref w, plan.values);
            i.Broadcast(Finish(ref w));
            return id;
        }

        public static void ServerMerge(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.Merge, 8);
            w.WriteValueSafe(id);
            i.Broadcast(Finish(ref w));
        }

        public static int ServerRockGather(ulong caster, Vector3Int center, int r4)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            var w = NewOp(Op.RockGather, 40);
            w.WriteValueSafe(id);
            w.WriteValueSafe(caster);
            w.WriteValueSafe(Now);
            WriteCell(ref w, center);
            w.WriteValueSafe((byte)r4);
            i.Broadcast(Finish(ref w));
            return i.Rocks.ContainsKey(id) ? id : 0;
        }

        public static void ServerRockLaunch(int id, Vector3 origin, Vector3 velocity)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.RockLaunch, 48);
            w.WriteValueSafe(id);
            w.WriteValueSafe(origin);
            w.WriteValueSafe(velocity);
            w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        /// <param name="r4">Voxel damage radius in quarter voxels at the impact (0 = none, e.g. it hit a player).</param>
        public static void ServerRockImpact(int id, Vector3 point, Vector3Int cell, int r4, bool vaporized = false)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(Op.RockImpact, 40);
            w.WriteValueSafe(id);
            w.WriteValueSafe(point);
            WriteCell(ref w, cell);
            w.WriteValueSafe((byte)r4);
            w.WriteValueSafe(vaporized);
            i.Broadcast(Finish(ref w));
        }

        public static int ServerFold(SpellPlans.FoldPlan plan)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            int id = i._nextId++;
            int n = plan.slab[0].Count + plan.slab[1].Count;
            var w = NewOp(Op.Fold, 128 + n * 14);
            w.WriteValueSafe(id);
            w.WriteValueSafe(Now);
            for (int s = 0; s < 2; s++)
            {
                w.WriteValueSafe(plan.hinge[s]);
                w.WriteValueSafe(plan.outward[s]);
                WriteCells(ref w, plan.slab[s]);
                WriteValues(ref w, plan.slabValues[s]);
                WriteCells(ref w, plan.final[s]);
            }
            i.Broadcast(Finish(ref w));
            return id;
        }

        // ------------------------------------------------------------------ apply (every peer)

        void Apply(FastBufferReader r)
        {
            r.ReadValueSafe(out byte opByte);
            var op = (Op)opByte;
            if (op == Op.LoadArena)
            {
                r.ReadValueSafe(out short arena);
                r.ReadValueSafe(out int round);
                ClearEffects();
                Debris.ClearAll();
                MatchManager.LoadArenaLocal(arena, round);
                return;
            }

            var world = VoxelWorld.Current;
            if (world == null) return;
            var removed = new List<VoxelWorld.RemovedCell>();
            var islands = new List<List<VoxelWorld.RemovedCell>>();

            switch (op)
            {
                case Op.RemoveSphere:
                {
                    var c = ReadCell(ref r);
                    r.ReadValueSafe(out byte r4);
                    r.ReadValueSafe(out byte push);
                    r.ReadValueSafe(out Vector3 extra);
                    world.RemoveSphere(c, r4, removed, islands);
                    Debris.Spawn(world, removed, world.CellCenter(c), push, extra, 2);
                    break;
                }
                case Op.RemoveBox:
                {
                    var min = ReadCell(ref r);
                    var max = ReadCell(ref r);
                    r.ReadValueSafe(out byte push);
                    world.RemoveCells(BoxCells(min, max), removed, true, islands);
                    Debris.Spawn(world, removed, (world.CellCenter(min) + world.CellCenter(max)) * 0.5f, push, Vector3.zero, 2);
                    break;
                }
                case Op.PushBody:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out ulong caster);
                    r.ReadValueSafe(out int n);
                    var cells = new List<VoxelWorld.RemovedCell>(n);
                    var plain = new List<Vector3Int>(n);
                    for (int k = 0; k < n; k++)
                    {
                        var cc = ReadCell(ref r);
                        r.ReadValueSafe(out ushort v);
                        cells.Add(new VoxelWorld.RemovedCell { cell = cc, value = v });
                        plain.Add(cc);
                    }
                    r.ReadValueSafe(out Vector3 pivot);
                    r.ReadValueSafe(out Vector3 vel);
                    r.ReadValueSafe(out double t);
                    world.RemoveCells(plain, null, true, islands);
                    var fx = new RockFx { id = id, caster = caster, start = t, home = pivot, launched = true, origin = pivot, velocity = vel, launchTime = t, noSpin = true };
                    fx.body = VoxelBody.Create(world, id, cells, pivot, false);
                    Rocks[id] = fx;
                    SpellFx.Puff(pivot, new Color(0.8f, 0.82f, 0.85f), 1.2f);
                    break;
                }
                case Op.Gust:
                {
                    var fx = new GustFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.origin); r.ReadValueSafe(out fx.dir); r.ReadValueSafe(out fx.start);
                    r.ReadValueSafe(out fx.life);
                    if (!Debris.Suppressed) fx.view = GustView.Create(fx);
                    Gusts[fx.id] = fx;
                    break;
                }
                case Op.Tornado:
                {
                    var fx = new TornadoFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.origin); r.ReadValueSafe(out fx.dir); r.ReadValueSafe(out fx.start);
                    fx.view = TornadoView.Create(fx);
                    Tornados[fx.id] = fx;
                    break;
                }
                case Op.MissileVolley:
                {
                    var fx = new MissileFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.target); r.ReadValueSafe(out fx.start);
                    fx.view = MissileView.Create(fx);
                    Missiles[fx.id] = fx;
                    break;
                }
                case Op.MissileLaunch:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out Vector3 origin);
                    r.ReadValueSafe(out byte count);
                    var dirs = new Vector3[count];
                    for (int k = 0; k < count; k++) r.ReadValueSafe(out dirs[k]);
                    if (!Missiles.TryGetValue(id, out var fx)) break;
                    fx.launched = true;
                    fx.m = new Missile[count];
                    for (int k = 0; k < count; k++) fx.m[k] = new Missile { pos = origin, prev = origin, dir = dirs[k], owner = fx.caster };
                    break;
                }
                case Op.MissileImpact:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out byte idx);
                    r.ReadValueSafe(out Vector3 point);
                    var c = ReadCell(ref r);
                    r.ReadValueSafe(out byte r4);
                    r.ReadValueSafe(out bool vaporized);
                    if (Missiles.TryGetValue(id, out var fx) && idx < fx.m.Length) { fx.m[idx].alive = false; fx.m[idx].pos = point; }
                    // the missile's own view bursts where it ends; a barrier adds its own flash
                    if (vaporized) SpellFx.Sparkle(point, SpellVisuals.ArcaneBlue, 1f);
                    if (r4 > 0)
                    {
                        world.RemoveSphere(c, r4, removed, islands);
                        Debris.Spawn(world, removed, point, 5f, Vector3.zero, 1);
                    }
                    break;
                }
                case Op.MissileRedirect:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out byte idx);
                    r.ReadValueSafe(out Vector3 pos);
                    r.ReadValueSafe(out Vector3 dir);
                    r.ReadValueSafe(out ulong owner);
                    if (!Missiles.TryGetValue(id, out var fx) || idx >= fx.m.Length) break;
                    var m = fx.m[idx];
                    m.pos = m.prev = pos;
                    m.dir = dir;
                    m.tracking = false;
                    m.owner = owner;
                    m.age = 0f;
                    break;
                }
                case Op.Barrier:
                {
                    var fx = new BarrierFx();
                    r.ReadValueSafe(out fx.caster);
                    r.ReadValueSafe(out fx.start);
                    if (!Debris.Suppressed) fx.view = BarrierView.Create(fx);
                    Barriers.Add(fx);
                    break;
                }
                case Op.Vaporize:
                {
                    var cells = ReadCells(ref r);
                    world.RemoveCells(cells, removed, true, islands);
                    if (removed.Count > 0)
                    {
                        Vector3 mid = Vector3.zero;
                        foreach (var rc in removed) mid += world.CellCenter(rc.cell);
                        SpellFx.Sparkle(mid / removed.Count, SpellVisuals.ArcaneBlue, 1.2f);
                    }
                    break;
                }
                case Op.Singe:
                {
                    var c = ReadCell(ref r);
                    r.ReadValueSafe(out byte r4);
                    r.ReadValueSafe(out bool all);
                    world.Singe(c, r4, all);
                    break;
                }
                case Op.FlameBurst:
                {
                    r.ReadValueSafe(out Vector3 point);
                    r.ReadValueSafe(out float scale);
                    FireVfx.Burst(point, scale);
                    CameraRig.ShakeFrom(point, 0.3f * scale, 0.25f);
                    break;
                }
                case Op.Fireball:
                {
                    var fx = new FireballFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.start);
                    fx.owner = fx.caster;
                    fx.view = FireballView.Create(fx);
                    Fireballs[fx.id] = fx;
                    FireballCharging?.Invoke(fx.id, fx.caster, fx.start);
                    break;
                }
                case Op.FireballLaunch:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out Vector3 origin);
                    r.ReadValueSafe(out Vector3 vel);
                    r.ReadValueSafe(out double t);
                    r.ReadValueSafe(out ulong owner);
                    if (!Fireballs.TryGetValue(id, out var fx)) break;
                    fx.launched = true;
                    fx.origin = origin;
                    fx.velocity = vel;
                    fx.launchTime = t;
                    fx.owner = owner;
                    break;
                }
                case Op.FireballEnd:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out Vector3 point);
                    r.ReadValueSafe(out bool vaporized);
                    if (Fireballs.TryGetValue(id, out var fx))
                    {
                        fx.done = true;
                        fx.origin = point;
                        Fireballs.Remove(id);
                    }
                    if (vaporized) SpellFx.Sparkle(point, SpellVisuals.ArcaneBlue, 1.2f);
                    break;
                }
                case Op.Hellfire:
                {
                    var fx = new HellfireFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.start);
                    fx.view = HellfireView.Create(fx);
                    Hellfires[fx.id] = fx;
                    HellfireStarted?.Invoke(fx.id, fx.caster);
                    break;
                }
                case Op.HellfirePoint:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out Vector3 p);
                    if (Hellfires.TryGetValue(id, out var fx)) fx.points.Add(p);
                    break;
                }
                case Op.HellfireRise:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out double t);
                    if (Hellfires.TryGetValue(id, out var fx)) fx.riseStart = t;
                    break;
                }
                case Op.Judgment:
                {
                    var fx = new JudgmentFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.center); r.ReadValueSafe(out fx.start);
                    fx.view = JudgmentView.Create(fx);
                    Judgments[fx.id] = fx;
                    break;
                }
                case Op.Fizzle:
                {
                    r.ReadValueSafe(out ulong caster);
                    r.ReadValueSafe(out double t);
                    FizzledAt[caster] = t; // the views read it (missile orb, barrier, judgment circle, hellfire line, priestess sun)
                    break;
                }
                case Op.JudgmentErase:
                {
                    var c = ReadCell(ref r);
                    r.ReadValueSafe(out short radius);
                    r.ReadValueSafe(out short yMin);
                    r.ReadValueSafe(out short yMax);
                    world.EraseTopLayer(c.x, c.z, radius, yMin, yMax, removed);
                    // erased, not broken: the layer turns black and floats up in clumps, shrinking away
                    if (removed.Count > 0 && !Debris.Suppressed) VoidRise.Spawn(world, removed);
                    break;
                }
                case Op.RockIgnite:
                {
                    r.ReadValueSafe(out int id);
                    if (Rocks.TryGetValue(id, out var rock) && rock.body != null && !Debris.Suppressed)
                        FireVfx.Aura(rock.body.transform, rock.body.LocalBounds.center, Mathf.Clamp(rock.body.LocalBounds.extents.magnitude, 0.3f, 2.5f));
                    break;
                }
                case Op.Priestess:
                {
                    r.ReadValueSafe(out ulong caster);
                    r.ReadValueSafe(out double start);
                    if (!Debris.Suppressed) PriestessView.Create(caster, start);
                    break;
                }
                case Op.Domain:
                {
                    var fx = new DomainFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.kind); r.ReadValueSafe(out fx.caster);
                    r.ReadValueSafe(out fx.center); r.ReadValueSafe(out fx.start);
                    fx.view = DomainView.Create(fx);
                    Domains[fx.id] = fx;
                    break;
                }
                case Op.DomainEnd:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out double t);
                    if (Domains.TryGetValue(id, out var fx)) fx.ended = t;
                    break;
                }
                case Op.Orb:
                {
                    var fx = new OrbFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.center); r.ReadValueSafe(out fx.start);
                    fx.view = OrbView.Create(fx);
                    Orbs[fx.id] = fx;
                    break;
                }
                case Op.OrbEnd:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out byte how);
                    r.ReadValueSafe(out ulong who);
                    if (Orbs.TryGetValue(id, out var fx)) { fx.ended = Now; fx.how = how; fx.who = who; }
                    break;
                }
                case Op.Quench:
                {
                    r.ReadValueSafe(out Vector3 point);
                    r.ReadValueSafe(out float scale);
                    WaterVfx.Steam(point, scale);
                    break;
                }
                case Op.HellfireQuench:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out double t);
                    if (Hellfires.TryGetValue(id, out var fx)) fx.quenched = t;
                    break;
                }
                case Op.RockDouse:
                {
                    r.ReadValueSafe(out int id);
                    if (Rocks.TryGetValue(id, out var rock) && rock.body != null)
                    {
                        foreach (var a in rock.body.GetComponentsInChildren<FlameAura>()) Destroy(a.gameObject);
                        WaterVfx.Steam(rock.body.Center, 0.8f);
                    }
                    break;
                }
                case Op.Bubble:
                {
                    var fx = new BubbleFx();
                    r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.start);
                    fx.view = BubbleView.Create(fx);
                    Bubbles.Add(fx);
                    break;
                }
                case Op.BubbleHit:
                {
                    r.ReadValueSafe(out ulong caster);
                    r.ReadValueSafe(out Vector3 point);
                    var fx = Bubbles.FindLast(b => b.caster == caster && b.ended < 0);
                    if (fx != null) fx.lastHit = Now;
                    WaterVfx.Splash(point, 0.5f);
                    break;
                }
                case Op.BubbleEnd:
                {
                    r.ReadValueSafe(out ulong caster);
                    r.ReadValueSafe(out double t);
                    r.ReadValueSafe(out bool popped);
                    var fx = Bubbles.FindLast(b => b.caster == caster && b.ended < 0);
                    if (fx != null) { fx.ended = t; fx.popped = popped; }
                    BubbleEnded?.Invoke(caster);
                    break;
                }
                case Op.Tsunami:
                {
                    var fx = new TsunamiFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.origin); r.ReadValueSafe(out fx.dir);
                    r.ReadValueSafe(out fx.start);
                    r.ReadValueSafe(out short n);
                    fx.ground = new float[Mathf.Max(0, (int)n)];
                    for (int k = 0; k < fx.ground.Length; k++) r.ReadValueSafe(out fx.ground[k]);
                    fx.view = TsunamiView.Create(fx);
                    Tsunamis[fx.id] = fx;
                    break;
                }
                case Op.TsunamiErase:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out int dead);
                    if (Tsunamis.TryGetValue(id, out var fx)) fx.dead = dead;
                    break;
                }
                case Op.Warp:
                {
                    var fx = new WarpFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.dest); r.ReadValueSafe(out fx.start);
                    fx.view = WarpView.Create(fx);
                    Warps[fx.id] = fx;
                    break;
                }
                case Op.WarpEnd:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out byte how);
                    r.ReadValueSafe(out double t);
                    if (Warps.TryGetValue(id, out var fx))
                    {
                        fx.ended = t;
                        fx.how = how;
                        WarpEnded?.Invoke(id, fx.caster, how);
                    }
                    break;
                }
                case Op.Portal:
                {
                    var fx = new PortalFx();
                    r.ReadValueSafe(out fx.id); r.ReadValueSafe(out fx.owner); r.ReadValueSafe(out fx.center); r.ReadValueSafe(out fx.yaw);
                    r.ReadValueSafe(out fx.pitch); r.ReadValueSafe(out fx.start);
                    fx.view = PortalView.Create(fx);
                    Portals[fx.id] = fx;
                    break;
                }
                case Op.PortalLink:
                {
                    r.ReadValueSafe(out int a);
                    r.ReadValueSafe(out int b);
                    r.ReadValueSafe(out double t);
                    if (Portals.TryGetValue(a, out var pa) && Portals.TryGetValue(b, out var pb))
                    {
                        pa.pair = b; pb.pair = a;
                        pa.linkStart = pb.linkStart = t;
                    }
                    break;
                }
                case Op.Swift:
                {
                    var fx = new SwiftFx();
                    r.ReadValueSafe(out fx.caster); r.ReadValueSafe(out fx.start);
                    fx.view = SwiftView.Create(fx);
                    Swifts.Add(fx);
                    break;
                }
                case Op.SwiftEnd:
                {
                    r.ReadValueSafe(out ulong caster);
                    r.ReadValueSafe(out double t);
                    r.ReadValueSafe(out bool cancelled);
                    var fx = Swifts.FindLast(s => s.caster == caster && s.ended < 0);
                    if (fx != null) { fx.ended = t; fx.cancelled = cancelled; }
                    SwiftEnded?.Invoke(caster, cancelled);
                    break;
                }
                case Op.Wash:
                {
                    var cells = ReadCells(ref r);
                    r.ReadValueSafe(out Vector3 dir);
                    world.RemoveCells(cells, removed, true, islands);
                    if (removed.Count > 0)
                    {
                        Vector3 mid = Vector3.zero;
                        foreach (var rc in removed) mid += world.CellCenter(rc.cell);
                        mid /= removed.Count;
                        Debris.Spawn(world, removed, mid - dir, 3f, dir * 7f + Vector3.up * 2.5f, 2);
                    }
                    break;
                }
                case Op.Shatter:
                {
                    r.ReadValueSafe(out int id);
                    if (!Folds.TryGetValue(id, out var fold)) break;
                    Vector3 mid = (fold.hinge[0] + fold.hinge[1]) * 0.5f + Vector3.up * 1.2f;
                    foreach (var b in fold.bodies)
                    {
                        if (b == null) continue;
                        Debris.Spawn(world, b.Cells, mid, 5f, Vector3.up * 1.5f, 2, b.MeshRoot);
                        Destroy(b.gameObject);
                    }
                    Folds.Remove(id);
                    SpellFx.Puff(mid, new Color(0.6f, 0.5f, 0.4f), 1.4f);
                    break;
                }
                case Op.Wall:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out double start);
                    r.ReadValueSafe(out byte rise);
                    var trench = ReadCells(ref r);
                    var lifted = ReadCells(ref r);
                    var cells = ReadCells(ref r);
                    var values = ReadValues(ref r);
                    world.RemoveCells(trench, removed, true, islands);
                    Debris.Spawn(world, removed, world.CellCenter(cells.Count > 0 ? cells[0] : Vector3Int.zero), 2.5f, Vector3.up * 2f, 1);
                    world.RemoveCells(lifted, null, false, null);
                    var bodyCells = Zip(cells, values);
                    Vector3 pivot = bodyCells.Count > 0 ? world.CellCenter(bodyCells[0].cell) : world.Origin;
                    var fx = new WallFx { id = id, start = start, rise = rise, cells = cells, values = values };
                    if (bodyCells.Count > 0) fx.body = VoxelBody.Create(world, id, bodyCells, pivot);
                    Walls[id] = fx;
                    AnimateWall(fx);
                    break;
                }
                case Op.Merge:
                {
                    r.ReadValueSafe(out int id);
                    if (Walls.TryGetValue(id, out var wall))
                    {
                        world.FillCells(wall.cells, wall.values);
                        if (wall.body) Destroy(wall.body.gameObject);
                        Walls.Remove(id);
                    }
                    else if (Folds.TryGetValue(id, out var fold))
                    {
                        for (int s = 0; s < 2; s++)
                        {
                            world.FillCells(fold.final[s], fold.values[s]);
                            if (fold.bodies[s]) Destroy(fold.bodies[s].gameObject);
                        }
                        Folds.Remove(id);
                        // dust where they slam together
                        if (fold.final[0].Count > 0)
                            SpellFx.Puff((fold.hinge[0] + fold.hinge[1]) * 0.5f + Vector3.up * 1f, new Color(0.6f, 0.5f, 0.4f), 1.2f);
                    }
                    world.FlushRebuilds();
                    break;
                }
                case Op.RockGather:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out ulong caster);
                    r.ReadValueSafe(out double start);
                    var c = ReadCell(ref r);
                    r.ReadValueSafe(out byte r4);
                    world.RemoveSphere(c, r4, removed, islands);
                    if (removed.Count == 0) break;
                    Vector3 centroid = Vector3.zero;
                    foreach (var rc in removed) centroid += world.CellCenter(rc.cell);
                    centroid /= removed.Count;
                    var fx = new RockFx { id = id, caster = caster, start = start, home = centroid };
                    fx.body = VoxelBody.Create(world, id, removed, centroid, false);
                    Rocks[id] = fx;
                    SpellFx.Puff(world.CellCenter(c) + Vector3.up * 0.3f, new Color(0.55f, 0.45f, 0.35f), 0.7f);
                    RockGathered?.Invoke(id, caster, start);
                    break;
                }
                case Op.RockLaunch:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out Vector3 origin);
                    r.ReadValueSafe(out Vector3 vel);
                    r.ReadValueSafe(out double t);
                    if (Rocks.TryGetValue(id, out var rock))
                    {
                        rock.launched = true;
                        rock.origin = origin;
                        rock.velocity = vel;
                        rock.launchTime = t;
                    }
                    break;
                }
                case Op.RockImpact:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out Vector3 point);
                    var c = ReadCell(ref r);
                    r.ReadValueSafe(out byte r4);
                    r.ReadValueSafe(out bool vaporized);
                    Vector3 vel = Vector3.zero;
                    if (Rocks.TryGetValue(id, out var rock))
                    {
                        if (rock.body)
                        {
                            vel = rock.launched ? RockVelocity(rock, Now) : Vector3.zero;
                            if (vaporized) SpellFx.Sparkle(point, SpellVisuals.ArcaneBlue, 1.2f);
                            else if (rock.noSpin)
                            {
                                // a shoved slab of ground (maybe a whole wall) breaks into smaller slabs that keep most
                                // of its momentum; each splits again (8 -> 4 -> 2 -> single voxels) on every hard knock
                                Debris.Spawn(world, rock.body.Cells, point, 2f, vel * 0.6f, 8, rock.body.MeshRoot);
                            }
                            else
                            {
                                rock.body.SetPose(point, rock.body.transform.rotation); // a rock bursts where it hit
                                Debris.Spawn(world, rock.body.Cells, point, 3.5f, vel * 0.25f, 2, rock.body.MeshRoot);
                            }
                            Destroy(rock.body.gameObject);
                        }
                        Rocks.Remove(id);
                    }
                    if (vaporized) break;
                    if (r4 > 0)
                    {
                        world.RemoveSphere(c, r4, removed, islands);
                        Debris.Spawn(world, removed, point, 6f, vel * 0.15f, 2);
                    }
                    SpellFx.Puff(point, new Color(0.55f, 0.45f, 0.35f), 1f);
                    break;
                }
                case Op.Fold:
                {
                    r.ReadValueSafe(out int id);
                    r.ReadValueSafe(out double start);
                    var fx = new FoldFx { id = id, start = start };
                    for (int s = 0; s < 2; s++)
                    {
                        r.ReadValueSafe(out fx.hinge[s]);
                        r.ReadValueSafe(out fx.outward[s]);
                        var slab = ReadCells(ref r);
                        var vals = ReadValues(ref r);
                        fx.final[s] = ReadCells(ref r);
                        fx.values[s] = vals;
                        // pulling a slab out can leave whatever stood on it hanging: that falls
                        world.RemoveCells(slab, null, true, islands);
                        var cells = Zip(slab, vals);
                        if (cells.Count > 0) fx.bodies[s] = VoxelBody.Create(world, id * 10 + s, cells, fx.hinge[s]);
                    }
                    Folds[id] = fx;
                    AnimateFold(fx, Now);
                    break;
                }
            }

            foreach (var island in islands)
            {
                Vector3 mid = Vector3.zero;
                foreach (var rc in island) mid += world.CellCenter(rc.cell);
                mid /= Mathf.Max(1, island.Count);
                Debris.Spawn(world, island, mid, 0.8f, Vector3.zero, 4);
            }
        }

        // ------------------------------------------------------------------ animation (every peer)

        void Animate()
        {
            double now = Now;
            foreach (var w in Walls.Values) AnimateWall(w);
            foreach (var r in Rocks.Values) AnimateRock(r, now);
            foreach (var f in Folds.Values) AnimateFold(f, now);
            StepMissiles(Time.deltaTime);

            // finished effects (their views fade themselves out)
            _done.Clear();
            foreach (var g in Gusts.Values) if (now - g.start > g.life + 0.5) _done.Add(g.id);
            foreach (var id in _done) Gusts.Remove(id);
            _done.Clear();
            foreach (var t in Tornados.Values) if (now - t.start > SpellPlans.TornadoLife + 1.0) _done.Add(t.id);
            foreach (var id in _done) Tornados.Remove(id);
            _done.Clear();
            foreach (var m in Missiles.Values)
            {
                bool any = !m.launched;
                foreach (var x in m.m) any |= x.alive;
                if (!any || now - m.start > SpellPlans.MissileCharge + SpellPlans.MissileLife + 1.0) _done.Add(m.id);
            }
            foreach (var id in _done) Missiles.Remove(id);
            Barriers.RemoveAll(b => now - b.start > SpellPlans.BarrierWindup + SpellPlans.BarrierTime + 0.5);
            _done.Clear();
            foreach (var h in Hellfires.Values)
                if (now - h.start > SpellPlans.HellfireDraw + 3.0 && (h.riseStart < 0 || now - h.riseStart > SpellPlans.HellfireRise + SpellPlans.HellfireStand + SpellPlans.HellfireFall + 0.5))
                    _done.Add(h.id);
            foreach (var id in _done) Hellfires.Remove(id);
            _done.Clear();
            foreach (var j in Judgments.Values) if (now - j.start > SpellPlans.JudgmentLaserEnd + SpellPlans.JudgmentFade + 0.5) _done.Add(j.id);
            foreach (var id in _done) Judgments.Remove(id);
            _done.Clear();
            foreach (var f in Fireballs.Values) if (now - f.start > SpellPlans.FireballCharge + 6.0) _done.Add(f.id);
            foreach (var id in _done) Fireballs.Remove(id);
            _done.Clear();
            foreach (var d in Domains.Values) if (d.ended >= 0 && now - d.ended > 1.0 || now - d.start > 30.0) _done.Add(d.id);
            foreach (var id in _done) Domains.Remove(id);
            _done.Clear();
            foreach (var o in Orbs.Values) if (o.ended >= 0 && now - o.ended > 1.0 || now - o.start > SpellPlans.OrbGrow + SpellPlans.OrbLife + 3.0) _done.Add(o.id);
            foreach (var id in _done) Orbs.Remove(id);
            Bubbles.RemoveAll(b => b.ended >= 0 && now - b.ended > 1.0 || now - b.start > SpellPlans.BubbleWindup + SpellPlans.BubbleTime + 3.0);
            _done.Clear();
            foreach (var t in Tsunamis.Values) if (now - t.start > SpellPlans.TsunamiLife + SpellPlans.TsunamiCollapse + 1.0) _done.Add(t.id);
            foreach (var id in _done) Tsunamis.Remove(id);
            _done.Clear();
            foreach (var w in Warps.Values) if (w.ended >= 0 && now - w.ended > 1.0 || now - w.start > SpellPlans.WarpDone + 2.0) _done.Add(w.id);
            foreach (var id in _done) Warps.Remove(id);
            _done.Clear();
            foreach (var p in Portals.Values) if (now > p.End + 1.0) _done.Add(p.id);
            foreach (var id in _done) Portals.Remove(id);
            Swifts.RemoveAll(s => s.ended >= 0 && now - s.ended > 1.0 || now - s.start > SpellPlans.SwiftPrep + SpellPlans.SwiftTime + 2.0);
        }

        readonly List<int> _done = new();

        void StepMissiles(float dt)
        {
            foreach (var fx in Missiles.Values)
            {
                if (!fx.launched) continue;
                var target = FindCombatant(fx.target);
                bool targetOk = target != null && target.IsAlive;
                Vector3 aim = targetOk ? target.transform.position + Vector3.up * 0.3f : Vector3.zero;
                foreach (var m in fx.m)
                {
                    if (!m.alive) continue;
                    m.prev = m.pos;
                    SpellPlans.StepMissile(ref m.pos, ref m.dir, m.age, m.tracking && targetOk, aim, dt);
                    m.age += dt;
                    if (m.age > SpellPlans.MissileLife && !IsServer) m.alive = false; // the server announces its own fizzles
                }
            }
        }

        /// <summary>Is there an active barrier around this combatant right now?</summary>
        public static bool BarrierUp(ulong objectId, double now)
        {
            var i = Instance;
            if (i == null) return false;
            foreach (var b in i.Barriers)
                if (b.caster == objectId && now >= b.start + SpellPlans.BarrierWindup && now <= b.start + SpellPlans.BarrierWindup + SpellPlans.BarrierTime)
                    return true;
            return false;
        }

        void AnimateWall(WallFx w)
        {
            if (w.body == null) return;
            float t = Mathf.Clamp01((float)(Now - w.start) / SpellPlans.WallRise);
            float e = 1f - (1f - t) * (1f - t);
            float down = w.rise * VoxelWorld.Size * (1f - e);
            w.body.SetPose(w.body.HomePivot + Vector3.down * down, Quaternion.identity);
        }

        public static Vector3 RockPosition(RockFx r, double now)
        {
            if (r.launched)
            {
                float t = Mathf.Max(0f, (float)(now - r.launchTime));
                return r.origin + r.velocity * t + 0.5f * Vector3.down * SpellPlans.RockGravity * t * t;
            }
            var caster = FindCaster(r.caster);
            Vector3 hold = caster != null
                ? SpellPlans.RockHoldPoint(caster.transform.position, caster.ViewYaw, caster.ViewPitch)
                : r.home + Vector3.up * 1.5f;
            float g = Mathf.Clamp01((float)(now - r.start) / SpellPlans.RockGather);
            g = g * g * (3f - 2f * g);
            return Vector3.Lerp(r.home, hold, g);
        }

        public static Vector3 RockVelocity(RockFx r, double now)
        {
            float t = Mathf.Max(0f, (float)(now - r.launchTime));
            return r.velocity + Vector3.down * SpellPlans.RockGravity * t;
        }

        void AnimateRock(RockFx r, double now)
        {
            if (r.body == null) return;
            float t = (float)(now - r.start);
            Quaternion spin = Quaternion.Euler(t * 140f, t * 90f, t * 40f);
            if (r.launched) spin = Quaternion.Euler((float)(now - r.launchTime) * 720f, t * 90f, 0f);
            if (r.noSpin) spin = Quaternion.identity;
            Vector3 p = RockPosition(r, now);
            // a little tremble while it's being torn out
            if (t < SpellPlans.RockGather) p += UnityEngine.Random.insideUnitSphere * 0.03f;
            r.body.SetPose(p, spin);
        }

        void AnimateFold(FoldFx f, double now)
        {
            float t = (float)(now - f.start);
            for (int s = 0; s < 2; s++)
            {
                var b = f.bodies[s];
                if (b == null) continue;
                Quaternion closed = Quaternion.FromToRotation(f.outward[s], Vector3.up);
                Quaternion rot;
                Vector3 pivot = f.hinge[s];
                if (t < SpellPlans.FoldBuckle)
                {
                    // the ground buckles: shudders and heaves up a little
                    float k = t / SpellPlans.FoldBuckle;
                    float shake = Mathf.Sin(t * 70f + s * 2f) * 2.5f * k;
                    rot = Quaternion.Slerp(Quaternion.identity, closed, 0.04f * k) * Quaternion.AngleAxis(shake, Vector3.Cross(f.outward[s], Vector3.up));
                    pivot += Vector3.up * (0.06f * k);
                }
                else
                {
                    float u = Mathf.Clamp01((t - SpellPlans.FoldBuckle) / SpellPlans.FoldClose);
                    rot = Quaternion.Slerp(Quaternion.identity, closed, 0.04f + 0.96f * u * u);
                }
                b.SetPose(pivot, rot);
            }
        }

        void ClearEffects()
        {
            foreach (var w in Walls.Values) if (w.body) Destroy(w.body.gameObject);
            foreach (var r in Rocks.Values) if (r.body) Destroy(r.body.gameObject);
            foreach (var f in Folds.Values) foreach (var b in f.bodies) if (b) Destroy(b.gameObject);
            foreach (var g in Gusts.Values) if (g.view) Destroy(g.view.gameObject);
            foreach (var t in Tornados.Values) if (t.view) Destroy(t.view.gameObject);
            foreach (var m in Missiles.Values) if (m.view) Destroy(m.view.gameObject);
            foreach (var b in Barriers) if (b.view) Destroy(b.view.gameObject);
            foreach (var f in Fireballs.Values) if (f.view) Destroy(f.view.gameObject);
            foreach (var h in Hellfires.Values) if (h.view) Destroy(h.view.gameObject);
            foreach (var j in Judgments.Values) if (j.view) Destroy(j.view.gameObject);
            foreach (var d in Domains.Values) if (d.view) Destroy(d.view.gameObject);
            foreach (var o in Orbs.Values) if (o.view) Destroy(o.view.gameObject);
            foreach (var b in Bubbles) if (b.view) Destroy(b.view.gameObject);
            foreach (var t in Tsunamis.Values) if (t.view) Destroy(t.view.gameObject);
            Orbs.Clear();
            Bubbles.Clear();
            Tsunamis.Clear();
            foreach (var w in Warps.Values) if (w.view) Destroy(w.view.gameObject);
            foreach (var p in Portals.Values) if (p.view) Destroy(p.view.gameObject);
            foreach (var s in Swifts) if (s.view) Destroy(s.view.gameObject);
            Warps.Clear();
            Portals.Clear();
            Swifts.Clear();
            Domains.Clear();
            Fireballs.Clear();
            Hellfires.Clear();
            Judgments.Clear();
            FizzledAt.Clear();
            Walls.Clear();
            Rocks.Clear();
            Folds.Clear();
            Gusts.Clear();
            Tornados.Clear();
            Missiles.Clear();
            Barriers.Clear();
            SpellServer.Reset();
        }

        public static PlayerController FindCaster(ulong objectId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.SpawnManager == null) return null;
            return nm.SpawnManager.SpawnedObjects.TryGetValue(objectId, out var no) ? no.GetComponent<PlayerController>() : null;
        }

        // ------------------------------------------------------------------ (de)serialisation helpers

        static void WriteCell(ref FastBufferWriter w, Vector3Int c)
        {
            w.WriteValueSafe((short)c.x);
            w.WriteValueSafe((short)c.y);
            w.WriteValueSafe((short)c.z);
        }

        static Vector3Int ReadCell(ref FastBufferReader r)
        {
            r.ReadValueSafe(out short x);
            r.ReadValueSafe(out short y);
            r.ReadValueSafe(out short z);
            return new Vector3Int(x, y, z);
        }

        static void WriteCells(ref FastBufferWriter w, List<Vector3Int> cells)
        {
            w.WriteValueSafe(cells.Count);
            foreach (var c in cells) WriteCell(ref w, c);
        }

        static List<Vector3Int> ReadCells(ref FastBufferReader r)
        {
            r.ReadValueSafe(out int n);
            var list = new List<Vector3Int>(n);
            for (int i = 0; i < n; i++) list.Add(ReadCell(ref r));
            return list;
        }

        static void WriteValues(ref FastBufferWriter w, List<ushort> values)
        {
            w.WriteValueSafe(values.Count);
            foreach (var v in values) w.WriteValueSafe(v);
        }

        static List<ushort> ReadValues(ref FastBufferReader r)
        {
            r.ReadValueSafe(out int n);
            var list = new List<ushort>(n);
            for (int i = 0; i < n; i++)
            {
                r.ReadValueSafe(out ushort v);
                list.Add(v);
            }
            return list;
        }

        static List<VoxelWorld.RemovedCell> Zip(List<Vector3Int> cells, List<ushort> values)
        {
            var list = new List<VoxelWorld.RemovedCell>(cells.Count);
            for (int i = 0; i < cells.Count && i < values.Count; i++)
                if (values[i] != 0) list.Add(new VoxelWorld.RemovedCell { cell = cells[i], value = values[i] });
            return list;
        }
    }
}
