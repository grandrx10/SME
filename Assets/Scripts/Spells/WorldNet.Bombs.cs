using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The op stream's part for the flame bombs. The server decides; every peer applies the same ops in order.</summary>
    public partial class WorldNet
    {
        /// <summary>A flame bomb: flying, stuck to the world, or stuck to a body part.</summary>
        public class BombFx
        {
            public const byte Flying = 0, OnWorld = 1, OnBody = 2;
            public int id; public ulong owner; public byte mode;
            public Vector3 origin, velocity; public double t;                // flying, since t
            public Vector3 pos; public Quaternion rot = Quaternion.identity; // stuck to the world (its middle)
            public ulong body; public int bone; public Vector3 localPos; public Quaternion localRot = Quaternion.identity; // stuck to a body (where it touches)
            public double triggered = -1;                                    // flashing since
            public BombView view;
            Vector3 _lastPos;
            Quaternion _lastRot = Quaternion.identity;

            /// <summary>Where it is: its middle, and which way it lies (its own up points away from the surface).</summary>
            public void Pose(double now, out Vector3 p, out Quaternion q)
            {
                if (mode == Flying)
                {
                    float dt = Mathf.Max(0f, (float)(now - t));
                    p = origin + velocity * dt + Vector3.down * (0.5f * SpellPlans.BombGravity * dt * dt);
                    q = Quaternion.Euler(dt * 420f + id * 77f, id * 131f, dt * 300f);
                }
                else if (mode == OnWorld) { p = pos; q = rot; }
                else
                {
                    var part = SpellPlans.BombBone(FindCombatant(body), bone);
                    if (part == null) { p = _lastPos; q = _lastRot; return; }
                    q = part.rotation * localRot;
                    p = part.TransformPoint(localPos) + q * Vector3.up * (SpellPlans.BombThick * 0.5f);
                }
                _lastPos = p;
                _lastRot = q;
            }

            public Vector3 Velocity(double now) => velocity + Vector3.down * (SpellPlans.BombGravity * Mathf.Max(0f, (float)(now - t)));
        }

        public readonly Dictionary<int, BombFx> Bombs = new();

        /// <summary>Does this player have a flame bomb out (that hasn't been set off)?</summary>
        public bool HasBomb(ulong owner)
        {
            foreach (var b in Bombs.Values) if (b.owner == owner && b.triggered < 0) return true;
            return false;
        }

        enum BombOp : byte { Fly = 119, Stick = 120, OnBody = 121, Trigger = 122, End = 123 }

        static FastBufferWriter NewOp(BombOp op, int size) => NewOp((Op)(byte)op, size);

        // ------------------------------------------------------------------ server API

        static FastBufferWriter BombHead(BombOp op, int id, ulong owner)
        {
            var w = NewOp(op, 96);
            w.WriteValueSafe(id); w.WriteValueSafe(owner); w.WriteValueSafe(Now);
            return w;
        }

        /// <summary>A bomb (a new one, with <paramref name="id"/> 0) flies from here. Returns its id.</summary>
        public static int ServerBombFly(int id, ulong owner, Vector3 origin, Vector3 velocity)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            if (id == 0) id = i._nextId++;
            var w = BombHead(BombOp.Fly, id, owner);
            w.WriteValueSafe(origin); w.WriteValueSafe(velocity);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>A bomb (a new one, with <paramref name="id"/> 0) sticks to the world: its middle at <paramref name="pos"/>. Returns its id.</summary>
        public static int ServerBombStick(int id, ulong owner, Vector3 pos, Quaternion rot)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            if (id == 0) id = i._nextId++;
            var w = BombHead(BombOp.Stick, id, owner);
            w.WriteValueSafe(pos); w.WriteValueSafe(rot);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>A bomb (a new one, with <paramref name="id"/> 0) sticks to part of a body, touching it at this point of the part. Returns its id.</summary>
        public static int ServerBombOnBody(int id, ulong owner, ulong body, int bone, Vector3 localPos, Quaternion localRot)
        {
            var i = Instance;
            if (i == null || !IsServer) return 0;
            if (id == 0) id = i._nextId++;
            var w = BombHead(BombOp.OnBody, id, owner);
            w.WriteValueSafe(body); w.WriteValueSafe((byte)bone); w.WriteValueSafe(localPos); w.WriteValueSafe(localRot);
            i.Broadcast(Finish(ref w));
            return id;
        }

        /// <summary>It's been set off: it starts flashing.</summary>
        public static void ServerBombTrigger(int id)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(BombOp.Trigger, 16);
            w.WriteValueSafe(id); w.WriteValueSafe(Now);
            i.Broadcast(Finish(ref w));
        }

        public const byte BombGone = 0, BombExploded = 1, BombDoused = 2, BombVaporized = 3;

        /// <summary>It's gone: exploded, put out by water, vaporised by a barrier, or just gone.</summary>
        public static void ServerBombEnd(int id, Vector3 point, byte how)
        {
            var i = Instance;
            if (i == null || !IsServer) return;
            var w = NewOp(BombOp.End, 24);
            w.WriteValueSafe(id); w.WriteValueSafe(point); w.WriteValueSafe(how);
            i.Broadcast(Finish(ref w));
        }

        // ------------------------------------------------------------------ apply (every peer)

        /// <summary>Applies one of these ops. False: not one of them.</summary>
        bool ApplyBombs(Op opRaw, ref FastBufferReader r)
        {
            var op = (BombOp)(byte)opRaw;
            switch (op)
            {
                case BombOp.Fly:
                case BombOp.Stick:
                case BombOp.OnBody:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out ulong owner); r.ReadValueSafe(out double t);
                    bool made = !Bombs.TryGetValue(id, out var fx);
                    if (made) Bombs[id] = fx = new BombFx { id = id, owner = owner };
                    if (op == BombOp.Fly)
                    {
                        fx.mode = BombFx.Flying;
                        fx.t = t;
                        r.ReadValueSafe(out fx.origin); r.ReadValueSafe(out fx.velocity);
                    }
                    else if (op == BombOp.Stick)
                    {
                        fx.mode = BombFx.OnWorld;
                        r.ReadValueSafe(out fx.pos); r.ReadValueSafe(out fx.rot);
                    }
                    else
                    {
                        fx.mode = BombFx.OnBody;
                        r.ReadValueSafe(out fx.body); r.ReadValueSafe(out byte bone); r.ReadValueSafe(out fx.localPos); r.ReadValueSafe(out fx.localRot);
                        fx.bone = bone;
                    }
                    if (made) fx.view = BombView.Create(fx);
                    else if (fx.view) fx.view.Moved();
                    return true;
                }
                case BombOp.Trigger:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out double t);
                    if (Bombs.TryGetValue(id, out var fx)) fx.triggered = t;
                    return true;
                }
                case BombOp.End:
                {
                    r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 point); r.ReadValueSafe(out byte how);
                    if (Bombs.TryGetValue(id, out var fx))
                    {
                        Bombs.Remove(id);
                        if (fx.view) fx.view.End();
                    }
                    if (Debris.Suppressed) return true;
                    if (how == BombExploded) BombVfx.Blast(point);
                    else if (how == BombDoused) WaterVfx.Steam(point, 0.8f);
                    else if (how == BombVaporized) SpellFx.Sparkle(point, SpellVisuals.ArcaneBlue, 0.8f);
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ clean-up (every peer)

        /// <summary>A practice chamber was reset: the bombs in it go.</summary>
        void ClearBombsIn(float minX, float maxX)
        {
            _done.Clear();
            foreach (var b in Bombs.Values)
            {
                b.Pose(Now, out var p, out _);
                if (p.x < minX || p.x >= maxX) continue;
                if (b.view) Destroy(b.view.gameObject);
                _done.Add(b.id);
            }
            foreach (var id in _done) Bombs.Remove(id);
            _done.Clear();
        }

        void ClearBombs()
        {
            foreach (var b in Bombs.Values) if (b.view) Destroy(b.view.gameObject);
            Bombs.Clear();
        }
    }
}
