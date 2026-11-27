using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for Compressed Shot, Ephemerate, Cloud Step, Submerge and Bubble Bomb.
    ///  - Compressed Shot: held (the owner's own: half speed, falling gently, the right hand out at the aim); let go, a bolt
    ///    of air goes at whatever the centre dot is on: 15 to 25 (x1.5 on the head), thrown and ragdolled harder the longer it
    ///    was held, breaking through 1 to 5 layers of ground on the way.
    ///  - Ephemerate: gone for 0.75 s, flying where they aim, through anything (not out of the arena), untouchable. Back half
    ///    inside the ground: 20, and thrown up out of it (the ground in the way breaks; not counted for a combo break); less
    ///    than that: the ground they're in just breaks. Not again for 1 s.
    ///  - Cloud Step: the owner's own jump, three times as high (twice in the air, then land). Nothing here but the cost.
    ///  - Submerge: 0.5 s diving in (hurt or knocked meanwhile: nothing), then a pool on the ground (a ball of water in the
    ///    air): up to 2 s, untouchable, 1.5x as fast (the owner's motor), healing 5 a second. Pressed again, or out of mana: out of the ground spinning,
    ///    20 and thrown hard to anyone close. Not again for 1 s.
    ///  - Bubble Bomb: a red bubble glides 1 m forward and floats there (bumped off ground, it drifts away). Live after
    ///    0.6 s: an enemy within 2 m sets it off: 15, thrown and ragdolled, ground blown out, anyone burning put out, fire
    ///    in it quenched. Not set off in 5 s: it pops, harmlessly.
    /// </summary>
    public static partial class SpellServer
    {
        class EphemState { public PlayerController caster; public double until; }
        class SubState { public PlayerController caster; public double start, nextHeal; public bool under, leave; }
        class MineState { public int id; public PlayerController caster; public Vector3 p0, v, lastPos; public float damp; public double t0, start, bumpedAt; }
        class SplashState { public Vector3 center; public float radius; public double until; }

        static readonly List<EphemState> EphemsS = new();
        static readonly List<SubState> SubsS = new();
        static readonly List<MineState> MinesS = new();
        static readonly List<SplashState> Splashes = new();
        static readonly Dictionary<PlayerController, double> EphemEnded = new(), SubEnded = new();

        /// <summary>Server: the next knock doesn't count towards a combo break.</summary>
        public static bool QuietKnock;

        static void ResetGale()
        {
            EphemsS.Clear(); SubsS.Clear(); MinesS.Clear(); Splashes.Clear(); EphemEnded.Clear(); SubEnded.Clear();
        }

        static bool CastGale(PlayerController caster, CardId card, Vector3 target, float yaw, ulong targetId, double now)
        {
            switch (card)
            {
                case CardId.CompressedShot:
                    FireShot(caster, SpellPlans.ShotCharge(Mathf.Clamp(targetId / 1000f, 0f, SpellPlans.ShotChargeTime)), target);
                    return true;
                case CardId.Ephemerate:
                {
                    if (EphemsS.Exists(e => e.caster == caster)) return false;
                    if (EphemEnded.TryGetValue(caster, out double ended) && now < ended + SpellPlans.EphemRest - 0.15) return false;
                    EphemsS.Add(new EphemState { caster = caster, until = now + SpellPlans.EphemTime });
                    var st = caster.Status.Value;
                    st.EphemFrom = now;
                    st.EphemUntil = now + SpellPlans.EphemTime;
                    caster.Status.Value = st;
                    return true;
                }
                case CardId.CloudStep:
                    return true; // (the owner jumped)
                case CardId.Submerge:
                {
                    if (SubsS.Exists(s => s.caster == caster)) return false;
                    if (SubEnded.TryGetValue(caster, out double ended) && now < ended + SpellPlans.SubRest - 0.15) return false;
                    SubsS.Add(new SubState { caster = caster, start = now, nextHeal = now + SpellPlans.SubDive + 1.0 });
                    var st = caster.Status.Value;
                    st.SubFrom = now + SpellPlans.SubDive;
                    st.SubUntil = now + 3600.0; // (until it comes out)
                    caster.Status.Value = st;
                    RefreshFlow(caster); // no mana regeneration until it's out
                    return true;
                }
                case CardId.BubbleBomb:
                {
                    Vector3 at = SpellPlans.MineSpot(caster.transform.position, yaw);
                    if (Physics.CheckSphere(at, SpellPlans.MineRadius, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) at = caster.transform.position + Vector3.up * SpellPlans.MineUp;
                    Vector3 v = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * (SpellPlans.MineGlide * SpellPlans.MineDamp);
                    int id = WorldNet.ServerMine(caster.NetworkObjectId, at, v, SpellPlans.MineDamp);
                    MinesS.Add(new MineState { id = id, caster = caster, p0 = at, v = v, damp = SpellPlans.MineDamp, t0 = now, start = now, lastPos = at, bumpedAt = -1 });
                    return true;
                }
            }
            return false;
        }

        static void TickGale(double now)
        {
            TickEphems(now);
            TickSubs(now);
            TickMines(now);
            Splashes.RemoveAll(s => now > s.until);
        }

        /// <summary>Hurt or knocked while diving in: no pool.</summary>
        static void InterruptGale(PlayerController pc, bool knocked)
        {
            foreach (var s in SubsS.ToArray())
                if (s.caster == pc && !s.under) { SubsS.Remove(s); EndSubStatus(pc, WorldNet.Now); }
        }

        /// <summary>A practice chamber is being reset.</summary>
        static void ClearGale(PlayerController pc, System.Func<Vector3, bool> inside)
        {
            foreach (var m in MinesS.ToArray())
                if (m.caster == pc || inside(m.lastPos)) { MinesS.Remove(m); WorldNet.ServerMineEnd(m.id, m.lastPos, false); }
            foreach (var s in SubsS.ToArray()) if (s.caster == pc) { SubsS.Remove(s); EndSubStatus(pc, WorldNet.Now); }
            EphemsS.RemoveAll(e => e.caster == pc);
        }

        /// <summary>Under (or diving into) Submerge's pool: no other card, no shield.</summary>
        public static bool Submerged(PlayerController pc) => SubsS.Exists(s => s.caster == pc);

        // ------------------------------------------------------------------ Compressed Shot

        static readonly RaycastHit[] _shotHits = new RaycastHit[32];

        static void FireShot(PlayerController me, float charge, Vector3 aimed)
        {
            var w = VoxelWorld.Current;
            Vector3 from = SpellVisuals.RightHand(me);
            if ((from - me.transform.position).magnitude > 2.5f) from = me.transform.position + Vector3.up * 0.4f;
            // at whatever the centre dot was on, on the caster's own screen (from their camera); failing that, worked out here
            Vector3 dir = aimed.sqrMagnitude > 0.01f && (aimed - from).sqrMagnitude > 0.04f && !float.IsNaN(aimed.x)
                ? (aimed - from).normalized : SpellPlans.FlowerAim(me, from);
            int layers = Mathf.RoundToInt(Mathf.Lerp(SpellPlans.ShotLayers0, SpellPlans.ShotLayers1, charge));
            // through the ground: so many cells along the middle, then it's stopped
            float stop = SpellPlans.ShotRange;
            var cells = new HashSet<Vector3Int>();
            if (w != null)
            {
                var seen = new HashSet<Vector3Int>();
                int through = 0;
                for (float d = 0f; d < SpellPlans.ShotRange; d += VoxelWorld.Size * 0.4f)
                {
                    Vector3 p = from + dir * d;
                    var c = w.CellOf(p);
                    ushort v = w.Get(c);
                    if (v == 0 || !seen.Add(c)) continue;
                    if (w.IsIndestructible(v) || ++through > layers) { stop = d; break; }
                    // the bolt's width of it
                    int r = Mathf.CeilToInt(SpellPlans.ShotRadius / VoxelWorld.Size);
                    for (int x = -r; x <= r; x++)
                    for (int y = -r; y <= r; y++)
                    for (int z = -r; z <= r; z++)
                    {
                        var q = new Vector3Int(c.x + x, c.y + y, c.z + z);
                        if ((w.CellCenter(q) - p).magnitude > SpellPlans.ShotRadius + VoxelWorld.Size * 0.5f) continue;
                        ushort qv = w.Get(q);
                        if (qv != 0 && !w.IsIndestructible(qv)) cells.Add(q);
                    }
                }
            }
            // the first body it meets before that
            Combatant victim = null;
            Hitbox where = null;
            float best = stop;
            int n = Physics.SphereCastNonAlloc(from, SpellPlans.ShotRadius, dir, _shotHits, stop, 1 << Layers.Hitbox, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var hb = _shotHits[i].collider != null ? _shotHits[i].collider.GetComponent<Hitbox>() : null;
                var c = hb != null ? hb.Owner : null;
                if (c == null || c == me || !c.IsAlive || Folded(c) || Combatant.Allies(me, c)) continue;
                float d = _shotHits[i].distance;
                if (d < best || victim == c && hb.Region == HitRegion.Head) { best = Mathf.Min(best, d); victim = c; where = hb; }
            }
            // (a head among the same body's hits counts as a headshot)
            if (victim != null)
                for (int i = 0; i < n; i++)
                {
                    var hb = _shotHits[i].collider != null ? _shotHits[i].collider.GetComponent<Hitbox>() : null;
                    if (hb != null && hb.Owner == victim && hb.Region == HitRegion.Head && _shotHits[i].distance <= best + 0.6f) where = hb;
                }
            Vector3 end = from + dir * best;
            if (victim != null) cells.RemoveWhere(c => w != null && Vector3.Dot(w.CellCenter(c) - from, dir) > best);
            WorldNet.ServerWindShot(from, end, charge, new List<Vector3Int>(cells));
            if (victim == null) return;
            bool head = where != null && where.Region == HitRegion.Head;
            float dmg = Mathf.Lerp(SpellPlans.ShotDamage0, SpellPlans.ShotDamage1, charge) * (head ? SpellPlans.ShotHeadshot : 1f);
            Vector3 knock = dir * Mathf.Lerp(SpellPlans.ShotKnock0, SpellPlans.ShotKnock1, charge) + Vector3.up * Mathf.Lerp(SpellPlans.ShotKnockUp0, SpellPlans.ShotKnockUp1, charge);
            Hit(victim, me, dmg, end, knock, Mathf.Lerp(SpellPlans.ShotRagdoll0, SpellPlans.ShotRagdoll1, charge), from: from);
        }

        // ------------------------------------------------------------------ Ephemerate

        static void TickEphems(double now)
        {
            for (int i = EphemsS.Count - 1; i >= 0; i--)
            {
                var e = EphemsS[i];
                if (e.caster == null || !e.caster.IsAlive) { EphemsS.RemoveAt(i); continue; }
                // (a moment for where the owner came back to reach us)
                if (now < e.until + 0.15) continue;
                EphemsS.RemoveAt(i);
                EphemEnded[e.caster] = now;
                ComeBack(e.caster);
            }
        }

        /// <summary>Back: inside the ground half or more: hurt and thrown up out of it; less: the ground they're in breaks.</summary>
        static void ComeBack(PlayerController c)
        {
            var w = VoxelWorld.Current;
            if (w == null) return;
            Vector3 feet = c.FeetPosition;
            float S = VoxelWorld.Size, radius = 0.35f, height = 1.9f;
            int total = 0, solid = 0;
            var lo = w.CellOf(feet + new Vector3(-radius, 0f, -radius));
            var hi = w.CellOf(feet + new Vector3(radius, height, radius));
            for (int x = lo.x; x <= hi.x; x++)
            for (int y = lo.y; y <= hi.y; y++)
            for (int z = lo.z; z <= hi.z; z++)
            {
                Vector3 p = w.CellCenter(new Vector3Int(x, y, z));
                if (new Vector2(p.x - feet.x, p.z - feet.z).magnitude > radius + S * 0.5f || p.y < feet.y || p.y > feet.y + height) continue;
                total++;
                if (w.Get(x, y, z) != 0) solid++;
            }
            if (solid == 0) return;
            if (total > 0 && solid / (float)total >= SpellPlans.EphemStuck)
            {
                // up through whatever's above, until there's room for the whole body
                float top = feet.y;
                for (float y = feet.y; y < feet.y + 40f; y += S)
                {
                    top = y;
                    if (!Physics.CheckCapsule(new Vector3(feet.x, y + radius + 0.05f, feet.z), new Vector3(feet.x, y + height - radius, feet.z), radius, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) break;
                }
                WorldNet.ServerRemoveBox(w.CellOf(feet + new Vector3(-0.5f, 0f, -0.5f)), w.CellOf(new Vector3(feet.x + 0.5f, top + height + 0.5f, feet.z + 0.5f)), 4f);
                c.ServerApplyDamage(SpellPlans.EphemDamage, c.OwnerClientId, c.transform.position, Vector3.up, -1, 0f, DeathCause.Spell);
                if (!c.IsAlive) return;
                QuietKnock = true;
                try { c.ServerKnock(Vector3.up * SpellPlans.EphemFling, SpellPlans.EphemRagdoll); }
                finally { QuietKnock = false; }
            }
            else WorldNet.ServerRemoveBox(lo, hi, 2f);
        }

        // ------------------------------------------------------------------ Submerge

        static void TickSubs(double now)
        {
            for (int i = SubsS.Count - 1; i >= 0; i--)
            {
                if (i >= SubsS.Count) continue;
                var s = SubsS[i];
                var me = s.caster;
                if (me == null || !me.IsAlive) { SubsS.RemoveAt(i); if (me != null) { EndSubStatus(me, now); RefreshFlow(me); } continue; }
                if (!s.under)
                {
                    if (now < s.start + SpellPlans.SubDive) continue;
                    s.under = true;
                    if (s.leave) Emerge(s, now); // (let go of while still diving in: straight back out)
                    continue;
                }
                if (now >= s.start + SpellPlans.SubDive + SpellPlans.SubMax) { Emerge(s, now); continue; } // held as long as it can be
                if (now >= s.nextHeal)
                {
                    s.nextHeal = now + 1.0;
                    me.ServerHeal(SpellPlans.SubHeal, 1f);
                }
            }
        }

        static void EndSubStatus(PlayerController pc, double now)
        {
            if (pc == null || !pc.IsSpawned) return;
            var st = pc.Status.Value;
            if (st.SubUntil <= now) return;
            st.SubUntil = now;
            pc.Status.Value = st;
        }

        /// <summary>Out of the ground, spinning: everyone close is hurt and thrown hard.</summary>
        static void Emerge(SubState s, double now)
        {
            if (!SubsS.Remove(s)) return;
            var me = s.caster;
            EndSubStatus(me, now);
            SubEnded[me] = now;
            RefreshFlow(me);
            Vector3 feet = me.FeetPosition;
            foreach (var o in Combatant.Registry.ToArray())
            {
                if (o == null || o == me || !o.IsAlive || Folded(o) || Combatant.Allies(me, o)) continue;
                Vector3 d = o.FeetPosition - feet;
                if (new Vector2(d.x, d.z).magnitude > SpellPlans.SubEmergeReach || d.y < -1.5f || d.y > 2.5f) continue;
                Vector3 away = new Vector3(d.x, 0f, d.z);
                away = away.sqrMagnitude > 0.01f ? away.normalized : Quaternion.Euler(0f, me.ViewYaw, 0f) * Vector3.forward;
                Hit(o, me, SpellPlans.SubEmergeDamage, o.transform.position, away * SpellPlans.SubKnock + Vector3.up * SpellPlans.SubKnockUp, SpellPlans.SubRagdoll, from: feet);
            }
            me.ServerEmerged();
        }

        /// <summary>Let go of: out it comes (still diving in: out as soon as it's in).</summary>
        static void CancelSubmerge(PlayerController pc)
        {
            var s = SubsS.Find(x => x.caster == pc);
            if (s == null) return;
            if (s.under) Emerge(s, WorldNet.Now);
            else s.leave = true;
        }

        // ------------------------------------------------------------------ Bubble Bomb

        static void TickMines(double now)
        {
            for (int i = MinesS.Count - 1; i >= 0; i--)
            {
                if (i >= MinesS.Count) continue;
                var m = MinesS[i];
                Vector3 pos = SpellPlans.MineDrift(m.p0, m.v, m.damp, (float)(now - m.t0));
                if (now >= m.start + SpellPlans.MineLife) { MinesS.RemoveAt(i); WorldNet.ServerMineEnd(m.id, pos, false); continue; }
                // bumped into ground: it floats off the other way
                if (now > m.bumpedAt + 0.15 && Physics.CheckSphere(pos, SpellPlans.MineRadius, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                {
                    Vector3 vNow = m.v * Mathf.Exp(-m.damp * (float)(now - m.t0));
                    Vector3 step = pos - m.lastPos, n = -vNow.normalized;
                    if (step.sqrMagnitude > 1e-6f && Physics.SphereCast(m.lastPos, SpellPlans.MineRadius * 0.9f, step.normalized, out var hit, step.magnitude + 0.1f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                        n = hit.normal;
                    if (n.sqrMagnitude < 1e-4f) n = Vector3.up;
                    Vector3 v = Vector3.Reflect(vNow, n);
                    v = (v.sqrMagnitude > 1e-4f ? v.normalized : n) * Mathf.Max(v.magnitude, SpellPlans.MineBumpSpeed);
                    m.p0 = m.lastPos; m.v = v; m.damp = SpellPlans.MineBumpDamp; m.t0 = now; m.bumpedAt = now;
                    WorldNet.ServerMineMove(m.id, m.p0, m.v, m.damp);
                    pos = m.p0;
                }
                m.lastPos = pos;
                if (now < m.start + SpellPlans.MineArm) continue;
                foreach (var o in Combatant.Registry)
                {
                    if (o == null || o == m.caster || !o.IsAlive || Folded(o) || Combatant.Allies(m.caster, o)) continue;
                    if (SpellPlans.BodyGap(pos, o) > SpellPlans.MineTrigger) continue;
                    MinesS.RemoveAt(i);
                    BurstMine(m, pos, now);
                    break;
                }
            }
        }

        static void BurstMine(MineState m, Vector3 pos, double now)
        {
            WorldNet.ServerMineEnd(m.id, pos, true);
            var w = VoxelWorld.Current;
            if (w != null) WorldNet.ServerRemoveSphere(w.CellOf(pos), SpellPlans.R4(SpellPlans.MineCrater), 7f, Vector3.up * 2f);
            // fire in it is put out (fireballs, walls, bombs: they ask InWater), and anyone burning in it
            Splashes.Add(new SplashState { center = pos, radius = SpellPlans.MineBlast, until = now + 0.3 });
            foreach (var o in Combatant.Registry.ToArray())
            {
                if (o == null || !o.IsAlive || SpellPlans.BodyGap(pos, o) > SpellPlans.MineBlast) continue;
                if (o.BurnStacks.Value > 0) { o.ServerExtinguish(); WorldNet.ServerQuench(o.transform.position, 0.7f); }
                if (o == m.caster || Combatant.Allies(m.caster, o) || Folded(o)) continue;
                Vector3 away = o.transform.position - pos;
                away.y = 0f;
                away = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward;
                Hit(o, m.caster, SpellPlans.MineDamage, o.transform.position, away * SpellPlans.MineKnock + Vector3.up * SpellPlans.MineKnockUp, SpellPlans.MineRagdoll, from: pos);
            }
        }

        /// <summary>A Bubble Bomb's burst, still splashing: water for anything on fire.</summary>
        static bool InSplash(Vector3 p, float pad)
        {
            foreach (var s in Splashes) if ((p - s.center).magnitude < s.radius + pad) return true;
            return false;
        }
    }
}
