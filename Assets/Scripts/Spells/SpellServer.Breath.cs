using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for the Flame Thrower, Icy Breath and the Grapple Hook.
    ///  - Flame Thrower: held. Hands clasped straight out (rooted), fire spews in a cone: ground in it blackens, and 0.1 s
    ///    later burns away, layer by layer; anyone in it gets a burn every half second they stay. 2 mana a second; ends when let go, knocked
    ///    down, or out of mana. No knockback.
    ///  - Icy Breath: 0.3 s leaning in, then 3 s of frost breathed along the aim (moving is fine): surfaces it touches turn
    ///    to ice (walking on ice knocks you down), anyone in it is slowed to half speed and frosted over (until 2 s after the last of it), and it puts fire out as water does.
    ///    Knocked down: it stops.
    ///  - Grapple Hook: thrown from the left hand. Into ground: a few voxels break where it bites, and the caster is reeled in until they reach it (pressing it
    ///    again lets go, free). Into an enemy: 15, and they're pulled to the caster over 1 s, ragdolled. Anything that isn't
    ///    voxels (an ignored surface): it glances off. Knocked down before it lands: it's gone; after: it still reels in.
    /// </summary>
    public static partial class SpellServer
    {
        const byte FlameKind = 0, IceKind = 1;

        class BreathState
        {
            public int id; public PlayerController caster; public byte kind; public double start, end, nextCheck;
            public readonly Dictionary<Combatant, double> lastBurn = new();
        }
        class HookState
        {
            public int id; public PlayerController caster; public Vector3 origin, dir, lastTip, anchor; public double start, attachedAt = -1;
            public Combatant victim;
        }

        static readonly List<BreathState> BreathsS = new();
        static readonly List<HookState> HooksS = new();
        /// <summary>Cells the Flame Thrower has blackened, and when (they burn away half a second later).</summary>
        static readonly Dictionary<Vector3Int, double> Singed = new();

        static void ResetBreath()
        {
            BreathsS.Clear();
            HooksS.Clear();
            Singed.Clear();
        }

        static bool CastBreath(PlayerController caster, CardId card, Vector3 target, double now)
        {
            switch (card)
            {
                case CardId.FlameThrower:
                {
                    if (BreathsS.Exists(b => b.caster == caster && b.kind == FlameKind)) return false;
                    var b = new BreathState { caster = caster, kind = FlameKind, start = now, end = double.MaxValue };
                    b.id = WorldNet.ServerBreath(caster.NetworkObjectId, FlameKind, now);
                    BreathsS.Add(b);
                    RefreshFlow(caster);
                    return true;
                }
                case CardId.IcyBreath:
                {
                    if (BreathsS.Exists(b => b.caster == caster && b.kind == IceKind)) return false;
                    double from = now + SpellPlans.BreathLean;
                    var b = new BreathState { caster = caster, kind = IceKind, start = from, end = from + SpellPlans.IceBreathTime };
                    b.id = WorldNet.ServerBreath(caster.NetworkObjectId, IceKind, from);
                    BreathsS.Add(b);
                    return true;
                }
                case CardId.GrappleHook:
                {
                    if (HooksS.Exists(h => h.caster == caster)) return false;
                    Vector3 dir = target.sqrMagnitude > 1e-4f ? target.normalized : Quaternion.Euler(caster.ViewPitch, caster.ViewYaw, 0f) * Vector3.forward;
                    double start = now + SpellPlans.HookCast;
                    Vector3 origin = SpellVisuals.LeftHand(caster);
                    var h = new HookState { caster = caster, origin = origin, dir = dir, start = start, lastTip = origin };
                    h.id = WorldNet.ServerHook(caster.NetworkObjectId, origin, dir, start);
                    HooksS.Add(h);
                    return true;
                }
            }
            return false;
        }

        /// <summary>The owner let go of the Flame Thrower's button.</summary>
        public static void StopBreath(PlayerController pc)
        {
            foreach (var b in BreathsS.ToArray()) if (b.caster == pc && b.kind == FlameKind) EndBreath(b);
        }

        static void EndBreath(BreathState b)
        {
            if (!BreathsS.Remove(b)) return;
            WorldNet.ServerBreathEnd(b.id);
            if (b.kind == FlameKind) RefreshFlow(b.caster);
        }

        /// <summary>Knocked down: breaths stop; a hook still flying is gone (one already stuck keeps reeling in).</summary>
        static void InterruptBreath(PlayerController pc, bool knocked)
        {
            if (!knocked) return;
            foreach (var b in BreathsS.ToArray()) if (b.caster == pc) EndBreath(b);
            foreach (var h in HooksS.ToArray()) if (h.caster == pc && h.attachedAt < 0) EndHook(h);
        }

        static void TickBreath(double now)
        {
            for (int i = BreathsS.Count - 1; i >= 0; i--)
            {
                if (i >= BreathsS.Count) continue;
                var b = BreathsS[i];
                var me = b.caster;
                if (me == null || !me.IsAlive || now >= b.end || b.kind == FlameKind && me.ManaNow <= 0.001f) { EndBreath(b); continue; }
                if (now < b.start || now < b.nextCheck) continue;
                b.nextCheck = now + SpellPlans.FlameCheck;
                SpellPlans.BreathFrom(me, out var origin, out var dir);
                if (b.kind == FlameKind) FlameTick(b, origin, dir, now);
                else IceTick(b, origin, dir, now);
            }
            TickHooks(now);
        }

        static readonly List<Vector3Int> _singe = new(), _burn = new(), _ice = new();
        static readonly HashSet<Vector3Int> _seen = new();

        static Vector3 ConeRay(Vector3 dir, float angle)
        {
            Vector2 r = Random.insideUnitCircle * Mathf.Tan(angle * Mathf.Deg2Rad);
            Vector3 side = Vector3.Cross(dir, Vector3.up);
            if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
            side.Normalize();
            Vector3 up = Vector3.Cross(side, dir);
            return (dir + side * r.x + up * r.y).normalized;
        }

        static void FlameTick(BreathState b, Vector3 origin, Vector3 dir, double now)
        {
            var w = VoxelWorld.Current;
            _singe.Clear(); _burn.Clear(); _seen.Clear();
            int env = Layers.EnvironmentMask;
            if (w != null)
                for (int k = 0; k < SpellPlans.FlameRays; k++)
                {
                    Vector3 d = k == 0 ? dir : ConeRay(dir, SpellPlans.FlameAngle);
                    if (!Physics.Raycast(origin, d, out var hit, SpellPlans.FlameReach, env, QueryTriggerInteraction.Ignore)) continue;
                    if (!VoxelWorld.IsVoxelCollider(hit.collider)) continue;
                    var cell = w.CellOf(hit.point - hit.normal * 0.1f);
                    if (!_seen.Add(cell)) continue;
                    ushort v = w.Get(cell);
                    if (v == 0 || w.IsIndestructible(v)) continue;
                    if (!Singed.TryGetValue(cell, out double at))
                    {
                        Singed[cell] = now;
                        _singe.Add(cell);
                        MeltIceNear(hit.point, 0.6f);
                        continue;
                    }
                    if (now - at < SpellPlans.FlameSinge) continue;
                    // burnt through: it and the blackened ground round it go
                    Singed.Remove(cell);
                    _burn.Add(cell);
                    foreach (var n in Neighbours)
                    {
                        var p = cell + n;
                        if (Singed.TryGetValue(p, out double t2) && now - t2 >= SpellPlans.FlameSinge * 0.6f && !w.IsIndestructible(w.Get(p)))
                        {
                            Singed.Remove(p);
                            _burn.Add(p);
                        }
                    }
                }
            if (_singe.Count > 0) WorldNet.ServerCellsSinge(new List<Vector3Int>(_singe));
            if (_burn.Count > 0) WorldNet.ServerCellsBurn(new List<Vector3Int>(_burn), dir);
            if (Singed.Count > 4000) Singed.Clear(); // (long-forgotten scorch marks)

            FlamesSetOff(origin, dir);
            // anyone in the cone: a burn a second while they stay (no knockback)
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || c == b.caster || !c.IsAlive || Combatant.Allies(b.caster, c) || Shielded(c)) continue;
                if (!InBreath(origin, dir, SpellPlans.FlameReach, SpellPlans.FlameAngle, c)) continue;
                if (b.lastBurn.TryGetValue(c, out double last) && now - last < SpellPlans.FlameBurnEvery) continue;
                b.lastBurn[c] = now;
                Burn(c, b.caster);
                c.ServerCredit(b.caster);
            }
        }

        static readonly Vector3Int[] Neighbours =
        {
            Vector3Int.up, Vector3Int.down, Vector3Int.left, Vector3Int.right, new(0, 0, 1), new(0, 0, -1),
        };

        static bool InBreath(Vector3 origin, Vector3 dir, float reach, float angle, Combatant c)
        {
            Vector3 chest = c.transform.position + Vector3.up * 0.3f;
            bool inside = SpellPlans.InCone(origin, dir, reach, angle, chest, 0.4f)
                || SpellPlans.InCone(origin, dir, reach, angle, c.FeetPosition + Vector3.up * 0.3f, 0.4f);
            return inside && !Physics.Linecast(origin, chest, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        static void IceTick(BreathState b, Vector3 origin, Vector3 dir, double now)
        {
            var w = VoxelWorld.Current;
            _ice.Clear(); _seen.Clear();
            if (w != null)
                for (int k = 0; k < SpellPlans.FlameRays; k++)
                {
                    Vector3 d = k == 0 ? dir : ConeRay(dir, SpellPlans.IceBreathAngle);
                    if (!Physics.Raycast(origin, d, out var hit, SpellPlans.IceBreathReach, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                    if (!VoxelWorld.IsVoxelCollider(hit.collider)) continue;
                    var c0 = w.CellOf(hit.point - hit.normal * 0.1f);
                    // a little frosted patch round where it touches
                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx * dx + dy * dy + dz * dz > 2) continue;
                        var p = new Vector3Int(c0.x + dx, c0.y + dy, c0.z + dz);
                        if (!_seen.Add(p)) continue;
                        ushort v = w.Get(p);
                        if (v == 0 || w.IsIndestructible(v) || w.IsIce(v)) continue;
                        _ice.Add(p);
                    }
                }
            if (_ice.Count > 0) WorldNet.ServerIceCoat(new List<Vector3Int>(_ice));

            // anyone in it: slowed (and put out)
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || c == b.caster || !c.IsAlive || Combatant.Allies(b.caster, c) || Shielded(c)) continue;
                if (!InBreath(origin, dir, SpellPlans.IceBreathReach, SpellPlans.IceBreathAngle, c)) continue;
                var st = c.Status.Value;
                if (st.SlowUntil < now + SpellPlans.SlowTime - 0.15)
                {
                    st.SlowUntil = now + SpellPlans.SlowTime;
                    c.Status.Value = st;
                }
                c.ServerCredit(b.caster);
                if (c.BurnStacks.Value > 0)
                {
                    c.ServerExtinguish();
                    WorldNet.ServerQuench(c.transform.position, 0.7f);
                }
            }
            FreezeBombs(origin, dir);
            // it counters fire as water does: fireballs, hellfire walls, flame throwers caught in it go out
            var net = WorldNet.Instance;
            if (net != null)
                for (int i = FireballsS.Count - 1; i >= 0; i--)
                {
                    var f = FireballsS[i];
                    if (!net.Fireballs.TryGetValue(f.id, out var fx)) continue;
                    Vector3 p = WorldNet.FireballPosition(fx, now);
                    if (!SpellPlans.InCone(origin, dir, SpellPlans.IceBreathReach, SpellPlans.IceBreathAngle, p, SpellPlans.FireballRadius)) continue;
                    WorldNet.ServerFireballEnd(f.id, p, false);
                    WorldNet.ServerQuench(p, 1f);
                    FireballsS.RemoveAt(i);
                }
            for (int i = HellfiresS.Count - 1; i >= 0; i--)
            {
                var h = HellfiresS[i];
                if (h.riseAt < 0) continue;
                bool touched = false;
                foreach (var p in h.points)
                    if (SpellPlans.InCone(origin, dir, SpellPlans.IceBreathReach, SpellPlans.IceBreathAngle, p + Vector3.up * 0.5f, 0.6f)) { touched = true; break; }
                if (!touched) continue;
                HellfiresS.RemoveAt(i);
                WorldNet.ServerHellfireQuench(h.id);
            }
            foreach (var f in BreathsS.ToArray())
            {
                if (f.kind != FlameKind || f.caster == null || f.caster == b.caster) continue;
                SpellPlans.BreathFrom(f.caster, out var fo, out _);
                if (!SpellPlans.InCone(origin, dir, SpellPlans.IceBreathReach, SpellPlans.IceBreathAngle, fo, 0.5f)) continue;
                EndBreath(f);
                WorldNet.ServerQuench(fo, 0.8f);
            }
        }

        // ------------------------------------------------------------------ Grapple Hook

        static void TickHooks(double now)
        {
            int mask = Layers.EnvironmentMask | (1 << Layers.Hitbox);
            for (int i = HooksS.Count - 1; i >= 0; i--)
            {
                if (i >= HooksS.Count) continue;
                var h = HooksS[i];
                var me = h.caster;
                if (me == null || !me.IsAlive) { EndHook(h); continue; }
                if (now < h.start) continue;
                if (h.attachedAt >= 0)
                {
                    float held = (float)(now - h.attachedAt);
                    if (h.victim != null) { if (held >= SpellPlans.HookReelTime) EndHook(h); continue; }
                    // reeled all the way in (the server sees the owner a little late: some slack), or it's been too long
                    if ((me.transform.position - h.anchor).magnitude <= SpellPlans.HookArrive + 1f || held > SpellPlans.HookMaxTime) EndHook(h);
                    continue;
                }
                float travelled = SpellPlans.HookSpeed * (float)(now - h.start);
                Vector3 tip = h.origin + h.dir * Mathf.Min(travelled, SpellPlans.HookRange);
                Vector3 step = tip - h.lastTip;
                float dist = step.magnitude;
                if (dist > 1e-4f)
                {
                    int n = Physics.SphereCastNonAlloc(h.lastTip, 0.12f, step / dist, Hits, dist, mask, QueryTriggerInteraction.Collide);
                    float best = float.MaxValue;
                    RaycastHit hit = default;
                    Combatant who = null;
                    for (int k = 0; k < n; k++)
                    {
                        var c = Hits[k];
                        if (c.collider == null) continue;
                        Combatant owner = null;
                        if (c.collider.isTrigger)
                        {
                            var hb = c.collider.GetComponent<Hitbox>();
                            if (hb == null || hb.Owner == null || hb.Owner == me || !hb.Owner.IsAlive || Combatant.Allies(me, hb.Owner)) continue;
                            owner = hb.Owner;
                        }
                        if (c.distance < best) { best = c.distance; hit = c; who = owner; }
                    }
                    if (best < float.MaxValue)
                    {
                        Vector3 point = hit.distance <= 0f && hit.point == Vector3.zero ? h.lastTip : hit.point;
                        if (who != null) HookPerson(h, who, point, now);
                        else if (VoxelWorld.IsVoxelCollider(hit.collider) || hit.collider.GetComponentInParent<VoxelBody>() != null)
                        {
                            h.anchor = point;
                            h.attachedAt = now;
                            WorldNet.ServerHookAttach(h.id, point, 0);
                            // it bites in: a few voxels break where it hits
                            var w = VoxelWorld.Current;
                            if (w != null && VoxelWorld.IsVoxelCollider(hit.collider))
                                WorldNet.ServerRemoveSphere(w.CellOf(point - hit.normal * 0.1f), SpellPlans.HookBreakR4, 1.5f, -h.dir * 2f);
                        }
                        else EndHook(h); // not voxels: it glances off
                        continue;
                    }
                }
                h.lastTip = tip;
                if (travelled >= SpellPlans.HookRange) EndHook(h); // reached nothing
            }
        }

        /// <summary>The hook caught someone: 15, and they're dragged to the caster over a second (ragdolled).</summary>
        static void HookPerson(HookState h, Combatant who, Vector3 point, double now)
        {
            var me = h.caster;
            h.victim = who;
            h.attachedAt = now;
            WorldNet.ServerHookAttach(h.id, point, who.NetworkObjectId);
            // a throw that lands at the caster's feet (just in front of them) a second from now
            Vector3 to = me.transform.position;
            Vector3 flat = who.transform.position - to;
            flat.y = 0f;
            to += (flat.sqrMagnitude > 0.01f ? flat.normalized : me.transform.forward) * 1.2f;
            float T = SpellPlans.HookReelTime;
            Vector3 d = to - who.transform.position;
            Vector3 v = new Vector3(d.x, 0f, d.z) / T + Vector3.up * (d.y / T + 0.5f * 20f * T);
            Hit(who, me, SpellPlans.HookDamage, point, v, T + 0.3f);
        }

        static void EndHook(HookState h)
        {
            if (!HooksS.Remove(h)) return;
            WorldNet.ServerHookEnd(h.id);
        }
    }
}
