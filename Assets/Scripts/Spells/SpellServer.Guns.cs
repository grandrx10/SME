using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for the gun cards and Undying Grasp.
    ///  - Rachmaninoff: 0.3 s to pull the shotgun out (knocked down meanwhile: no shot), then 12 pellets in a 15 degree cone
    ///    at the caster's aim, 3 damage each, each breaking the voxel it hits. The first to hit someone throws and ragdolls
    ///    them; the rest only hurt. The caster is thrown back the other way, ragdolled (it counts for combo breaks).
    ///  - Sebastian: paid for when the charge begins. Let go before 1 s: refunded. Interrupted (ragdolled, silenced): gone.
    ///    Let go charged: 0.3 s more (ragdolled meanwhile: no shot), then a level 25 m cylinder of vaporised ground: 50 to
    ///    everyone in it. Goes through everything that breaks; stopped by indestructible ground, portals, barriers and
    ///    bubbles; sent back by The Hierophant's wall.
    ///  - Undying Grasp: 0.5 s on one knee (knocked down meanwhile: nothing), then hands shoot up out of the ground along
    ///    15 m. Whoever they reach as they come up is held in place and silenced for 2.5 s, or until ragdolled.
    /// </summary>
    public static partial class SpellServer
    {
        class ShotState { public PlayerController caster; public double at; }
        class PelletsState
        {
            public int id; public PlayerController caster;
            public readonly Vector3[] lastPos = new Vector3[SpellPlans.RachPellets];
            public readonly ulong[] owner = new ulong[SpellPlans.RachPellets]; // whose each one is (The Hierophant sends them back as its own)
            public readonly HashSet<Combatant> thrown = new();                  // knocked down by this blast already
        }
        class RifleState { public PlayerController caster; public int slot, cost; public bool free; public double start, released = -1; }
        class GraspCastState { public PlayerController caster; public double at; }
        class GraspState { public PlayerController caster; public double start; public Vector3 dir; public List<SpellPlans.GraspSlot> slots; public readonly HashSet<Combatant> grabbed = new(); }
        class HeldState { public Combatant victim; public double from, until; public Vector3 spot; public bool settled; public float taken; }

        static readonly List<ShotState> ShotsS = new();
        static readonly List<PelletsState> PelletsS = new();
        static readonly List<RifleState> RiflesS = new();
        static readonly List<GraspCastState> GraspCastsS = new();
        static readonly List<GraspState> GraspsS = new();
        static readonly List<HeldState> HeldsS = new();

        static void ResetGuns()
        {
            ShotsS.Clear();
            PelletsS.Clear();
            RiflesS.Clear();
            GraspCastsS.Clear();
            GraspsS.Clear();
            HeldsS.Clear();
        }

        static bool CastGuns(PlayerController caster, CardId card, Vector3 target, double now)
        {
            switch (card)
            {
                case CardId.Rachmaninoff:
                    ShotsS.RemoveAll(s => s.caster == caster);
                    ShotsS.Add(new ShotState { caster = caster, at = now + SpellPlans.RachDelay });
                    return true;
                case CardId.Sebastian:
                    return true; // the charge begins (see SebastianBegan)
                case CardId.UndyingGrasp:
                    GraspCastsS.RemoveAll(g => g.caster == caster);
                    GraspCastsS.Add(new GraspCastState { caster = caster, at = now + SpellPlans.GraspCast });
                    return true;
            }
            return false;
        }

        static void TickGuns(WorldNet net, double now)
        {
            // the owner never said where (lag): it goes where they're facing
            foreach (var s in ShotsS.ToArray())
            {
                if (s.caster == null || !s.caster.IsAlive) { ShotsS.Remove(s); continue; }
                if (now > s.at + 0.3) Blast(s, SpellVisuals.RightHand(s.caster) + ViewDir(s.caster) * 0.4f, ViewDir(s.caster));
            }
            foreach (var g in GraspCastsS.ToArray())
            {
                if (g.caster == null || !g.caster.IsAlive) { GraspCastsS.Remove(g); continue; }
                if (now > g.at + 0.3) RaiseGrasp(g, g.caster.ViewYaw);
            }
            TickRifles(now);
            TickPellets(net, now);
            TickGrasps(now);
            TickHeld(now);
        }

        /// <summary>Knocked down mid-cast: no shot, no hands.</summary>
        static void FizzleGuns(PlayerController pc)
        {
            ShotsS.RemoveAll(s => s.caster == pc);
            LoseRifle(pc);
            GraspCastsS.RemoveAll(g => g.caster == pc);
        }

        /// <summary>A practice chamber is being reset: what's going on in it (or by its player) is dropped.</summary>
        static void ClearGuns(PlayerController pc, System.Func<Vector3, bool> inside)
        {
            if (pc != null)
            {
                FizzleGuns(pc);
                PelletsS.RemoveAll(p => p.caster == pc);
                GraspsS.RemoveAll(g => g.caster == pc);
            }
            GraspsS.RemoveAll(g => g.slots.Count > 0 && inside(g.slots[0].ground));
            foreach (var h in HeldsS.ToArray()) if (h.victim == null || h.victim == pc || inside(h.spot)) FreeGrasp(h.victim);
        }

        // ------------------------------------------------------------------ Rachmaninoff

        /// <summary>The caster's owner fires: from the muzzle at <paramref name="origin"/>, along <paramref name="dir"/>.</summary>
        public static void FireShotgun(PlayerController caster, Vector3 origin, Vector3 dir)
        {
            var s = ShotsS.Find(x => x.caster == caster);
            if (s == null || WorldNet.Now < s.at - 0.15) return;
            if (float.IsNaN(origin.x + origin.y + origin.z + dir.x + dir.y + dir.z) || dir.sqrMagnitude < 0.5f) return;
            Blast(s, origin, dir.normalized);
        }

        static void Blast(ShotState s, Vector3 origin, Vector3 dir)
        {
            ShotsS.Remove(s);
            var me = s.caster;
            Vector3 chest = me.transform.position + Vector3.up * 0.45f;
            // believable, and not poked through a wall
            if ((origin - chest).magnitude > 3f * SpellPlans.BodyGrow(me)) origin = chest + dir * 0.6f;
            if (Physics.Linecast(chest, origin, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) origin = chest;
            var dirs = new Vector3[SpellPlans.RachPellets];
            float turn = Random.Range(0f, 360f);
            for (int k = 0; k < dirs.Length; k++) dirs[k] = SpellPlans.PelletDir(dir, k, turn);
            int id = WorldNet.ServerPellets(origin, dir, dirs);
            if (id != 0)
            {
                var p = new PelletsState { id = id, caster = me };
                for (int k = 0; k < dirs.Length; k++)
                {
                    p.lastPos[k] = chest; // (someone between the body and the muzzle is hit too)
                    p.owner[k] = me.NetworkObjectId;
                }
                PelletsS.Add(p);
            }
            // the kick: straight back from the blast
            if (!Unmovable(me)) me.ServerRecoil(-dir * SpellPlans.RecoilBack + Vector3.up * SpellPlans.RecoilUp, SpellPlans.RecoilRagdoll);
        }

        static void TickPellets(WorldNet net, double now)
        {
            for (int i = PelletsS.Count - 1; i >= 0; i--)
            {
                var s = PelletsS[i];
                bool any = false;
                if (net.Pellets.TryGetValue(s.id, out var fx))
                    for (int k = 0; k < fx.bits.Length; k++)
                        if (fx.bits[k].Alive) any |= FlyPellet(s, fx, k, now);
                if (!any) PelletsS.RemoveAt(i);
            }
        }

        /// <summary>One pellet's frame. False: it's gone.</summary>
        static bool FlyPellet(PelletsState s, WorldNet.PelletsFx fx, int k, double now)
        {
            var b = fx.bits[k];
            Vector3 pos = WorldNet.PelletPosition(b, now), last = s.lastPos[k], step = pos - last;
            float dist = step.magnitude;
            var by = WorldNet.FindCaster(s.owner[k]);
            if (BarrierBlocks(s.owner[k], pos, SpellPlans.PelletRadius)) { WorldNet.ServerPelletEnd(s.id, k, pos, default, false, true); return false; }
            var bubble = BubbleBlocking(s.owner[k], pos, SpellPlans.PelletRadius);
            if (bubble != null)
            {
                HurtBubble(bubble, SpellPlans.RachDamage, pos);
                WorldNet.ServerPelletEnd(s.id, k, pos, default, false, true);
                return false;
            }
            if (dist > 1e-4f && SweepBall(s.owner[k], last, step / dist, dist, SpellPlans.PelletRadius, out var hit, out var victim) && !(victim != null && Combatant.Allies(by, victim)))
            {
                var w = VoxelWorld.Current;
                bool breaks = false;
                Vector3Int cell = default;
                if (victim != null)
                {
                    // the first one to reach someone throws them; the others of the same blast only hurt
                    bool first = !Shielded(victim) && s.thrown.Add(victim);
                    Vector3 flat = new(b.dir.x, 0f, b.dir.z);
                    Vector3 knock = first ? (flat.sqrMagnitude > 0.01f ? flat.normalized : b.dir) * SpellPlans.RachKnock + Vector3.up * SpellPlans.RachKnockUp : Vector3.zero;
                    Hit(victim, by, SpellPlans.RachDamage, hit.point, knock, first ? SpellPlans.RachRagdoll : 0f, from: hit.point - b.dir);
                }
                else if (w != null && VoxelWorld.IsVoxelCollider(hit.collider))
                {
                    cell = w.CellOf(hit.point - hit.normal * 0.1f);
                    breaks = w.IsSolid(cell) && !w.IsIndestructible(w.Get(cell));
                }
                WorldNet.ServerPelletEnd(s.id, k, hit.point, cell, breaks, true);
                return false;
            }
            s.lastPos[k] = pos;
            if (SpellPlans.RachSpeed * (now - b.t) >= SpellPlans.RachRange) { WorldNet.ServerPelletEnd(s.id, k, pos, default, false, false); return false; }
            return true;
        }

        /// <summary>The Hierophant's wall sends pellets back (as its caster's own, a full flight again).</summary>
        static void ReflectPellets(HierophantState h, ulong me, WorldNet net, double now)
        {
            foreach (var s in PelletsS)
            {
                if (!net.Pellets.TryGetValue(s.id, out var fx)) continue;
                for (int k = 0; k < fx.bits.Length; k++)
                {
                    var b = fx.bits[k];
                    if (!b.Alive || s.owner[k] == me) continue;
                    if (!SpellPlans.HierophantTouch(s.lastPos[k], WorldNet.PelletPosition(b, now), SpellPlans.PelletRadius, h.center, h.rot, out var at, out var n)) continue;
                    if (Vector3.Dot(b.dir, n) >= 0f) continue;
                    Vector3 from = at + n * 0.05f;
                    s.owner[k] = me;
                    s.lastPos[k] = from;
                    WorldNet.ServerPelletTurn(s.id, k, from, SpellPlans.Reflect(b.dir, n));
                    WorldNet.ServerHierophantReflect(h.id, at);
                }
            }
        }

        // ------------------------------------------------------------------ Sebastian

        static readonly HashSet<Vector3Int> _sebCells = new();
        static readonly Collider[] _sebBodies = new Collider[256];

        /// <summary>Charging Sebastian, or about to fire it: no other card meanwhile.</summary>
        static bool RifleOut(PlayerController pc) => RiflesS.Exists(r => r.caster == pc);

        /// <summary>The cast went through (already paid for): the charge begins.</summary>
        public static void SebastianBegan(PlayerController caster, int slot, int cost, bool free) =>
            RiflesS.Add(new RifleState { caster = caster, slot = slot, cost = cost, free = free, start = WorldNet.Now });

        /// <summary>The charge (or the shot) is lost: nothing comes back, and a card rolled by The Fool is spent.</summary>
        static void LoseRifle(PlayerController pc)
        {
            foreach (var r in RiflesS) if (r.caster == pc) SpendRifle(r);
            RiflesS.RemoveAll(r => r.caster == pc);
        }

        static void SpendRifle(RifleState r)
        {
            if (r.free && r.caster != null) r.caster.ServerDropFoolCard(r.slot, CardId.Sebastian);
        }

        /// <summary>The caster's owner was interrupted (silenced, taken...).</summary>
        public static void SebastianLost(PlayerController caster) => LoseRifle(caster);

        /// <summary>The caster's owner let go: charged, it fires in a moment; not yet, the mana comes back.</summary>
        public static void SebastianReleased(PlayerController caster, bool charged)
        {
            var r = RiflesS.Find(x => x.caster == caster);
            if (r == null || r.released >= 0) return;
            double now = WorldNet.Now;
            if (charged && now >= r.start + SpellPlans.SebCharge - 0.15) { r.released = now; return; }
            RiflesS.Remove(r);
            if (!caster.IsAlive) return;
            caster.ServerClearCooldown(r.slot);
            // (the card itself, if The Fool rolled it, was never used up)
            if (!r.free && r.cost > 0) caster.Mana.Value = caster.Mana.Value.With(now, Mathf.Min(caster.Mana.Value.Limit, caster.ManaNow + r.cost));
        }

        /// <summary>The caster's owner: the gun has charged up, it fires now, aimed this way.</summary>
        public static void SebastianShot(PlayerController caster, float yaw, float pitch)
        {
            var r = RiflesS.Find(x => x.caster == caster);
            if (r == null || r.released < 0 || WorldNet.Now < r.released + SpellPlans.SebWindup - 0.15 || float.IsNaN(yaw) || float.IsNaN(pitch)) return;
            ShootRifle(r, yaw, pitch);
        }

        static void ShootRifle(RifleState r, float yaw, float pitch)
        {
            RiflesS.Remove(r);
            SpendRifle(r);
            r.caster.ServerRestartCooldown(CardId.Sebastian);
            FireSebastian(r.caster, yaw, Mathf.Clamp(pitch, -90f, 90f));
        }

        static void TickRifles(double now)
        {
            foreach (var r in RiflesS.ToArray())
            {
                var me = r.caster;
                if (me == null || !me.IsSpawned) { RiflesS.Remove(r); continue; }
                bool lost = !me.IsAlive || me.Pinned || me.Status.Value.Taken(now);
                if (r.released < 0) lost |= me.Trapped || me.Grasped || me.Hexed; // silenced while charging
                if (lost) { LoseRifle(me); continue; }
                // the owner never said (lag): it goes where they're facing
                if (r.released >= 0 && now > r.released + SpellPlans.SebWindup + 0.3) ShootRifle(r, me.ViewYaw, me.ViewPitch);
            }
        }

        static void FireSebastian(PlayerController caster, float yaw, float pitch)
        {
            var world = VoxelWorld.Current;
            SpellPlans.SebastianRay(caster, yaw, pitch, out var from, out var dir);
            var points = new List<Vector3> { from };
            var hurt = new HashSet<Combatant>();
            _sebCells.Clear();
            PlayerController by = caster;
            float left = SpellPlans.SebLength;
            double now = WorldNet.Now;
            int jumps = 0; // (bit i: points i to i+1 is a jump across Skyland, not drawn)

            for (int leg = 0; leg < 8 && left > 0.1f; leg++)
            {
                ulong owner = by.NetworkObjectId;
                float len = SpellPlans.SebastianReach(world, from, dir, left, leg == 0 ? 0.9f : 0f);

                // the nearest standing Hierophant wall on the way: it bounces off
                HierophantState mirror = null;
                Vector3 bounce = default, normal = default;
                foreach (var h in HierophantsS)
                {
                    if (h.caster == null || h.caster == by || now < h.Up || now > h.End) continue;
                    if (!SpellPlans.HierophantTouch(from, from + dir * len, 0f, h.center, h.rot, out var at, out var n) || Vector3.Dot(dir, n) >= 0f) continue;
                    float d = Vector3.Dot(at - from, dir);
                    if (d < 0f || d > len) continue;
                    len = d; mirror = h; bounce = at; normal = n;
                }
                // Skyland: out through a side, the top or the bottom, it carries on in from the opposite one
                bool wrap = SkyWrap.ShotExit(from, dir, len, out float exit, out var enter) && exit < len;
                if (wrap) { len = exit; mirror = null; }
                // someone's barrier or bubble stops it
                for (float d = 0f; d < len; d += 0.2f)
                {
                    Vector3 p = from + dir * d;
                    var bubble = BubbleBlocking(owner, p, 0f);
                    if (bubble == null && !BarrierBlocks(owner, p, 0f)) continue;
                    if (bubble != null) HurtBubble(bubble, SpellPlans.SebDamage, p);
                    len = d;
                    mirror = null;
                    wrap = false;
                    break;
                }

                Vector3 to = from + dir * len;
                points.Add(to);
                HurtAlong(by, from, to, dir, hurt);
                ShotSetsOff(from, to, SpellPlans.SebRadius, true); // (it goes through the ground it breaks)
                SpellPlans.SebastianCells(world, from, to, _sebCells);
                left -= len;
                if (wrap)
                {
                    points.Add(enter);
                    jumps |= 1 << (points.Count - 2);
                    from = enter;
                    continue;
                }
                if (mirror == null) break;
                WorldNet.ServerHierophantReflect(mirror.id, bounce);
                by = mirror.caster;
                dir = SpellPlans.Reflect(dir, normal).normalized;
                from = bounce + normal * 0.05f;
                points[points.Count - 1] = from;
            }
            WorldNet.ServerSebastian(points, jumps, new List<Vector3Int>(_sebCells));
        }

        /// <summary>Everyone in one stretch of the shot (a capsule, as it's drawn), once per shot.</summary>
        static void HurtAlong(PlayerController by, Vector3 from, Vector3 to, Vector3 dir, HashSet<Combatant> hurt)
        {
            float r = SpellPlans.SebRadius, len = (to - from).magnitude;
            Vector3 end = len > r ? to - dir * r : from;
            int n = Physics.OverlapCapsuleNonAlloc(from, end, r, _sebBodies, 1 << Layers.Hitbox, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var hb = _sebBodies[i] != null ? _sebBodies[i].GetComponent<Hitbox>() : null;
                var c = hb != null ? hb.Owner : null;
                if (c == null || c == by || !c.IsAlive || Folded(c) || Shielded(c) || Combatant.Allies(by, c) || !hurt.Add(c)) continue;
                // on the shot's axis, so a kill leaves the hole where it went through
                Vector3 on = from + dir * Mathf.Clamp(Vector3.Dot(c.transform.position - from, dir), 0f, len);
                Hit(c, by, SpellPlans.SebDamage, on, dir * SpellPlans.SebKnock + Vector3.up * SpellPlans.SebKnockUp, SpellPlans.SebRagdoll, default, dir * r);
            }
        }

        // ------------------------------------------------------------------ Undying Grasp

        /// <summary>The caster's owner says the cast is done, facing <paramref name="yaw"/>.</summary>
        public static void ReleaseGrasp(PlayerController caster, float yaw)
        {
            var g = GraspCastsS.Find(x => x.caster == caster);
            if (g == null || WorldNet.Now < g.at - 0.15 || float.IsNaN(yaw)) return;
            RaiseGrasp(g, yaw);
        }

        static void RaiseGrasp(GraspCastState g, float yaw)
        {
            GraspCastsS.Remove(g);
            Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            var slots = SpellPlans.GraspGround(VoxelWorld.Current, g.caster.FeetPosition, dir);
            if (slots.Count == 0) return; // no ground, no hands
            WorldNet.ServerGrasp(slots, dir);
            GraspsS.Add(new GraspState { caster = g.caster, start = WorldNet.Now, dir = dir, slots = slots });
        }

        static void TickGrasps(double now)
        {
            for (int i = GraspsS.Count - 1; i >= 0; i--)
            {
                var g = GraspsS[i];
                if (g.caster == null || now > g.start + SpellPlans.GraspRise) { GraspsS.RemoveAt(i); continue; }
                foreach (var c in Combatant.Registry)
                {
                    if (c == null || c == g.caster || g.grabbed.Contains(c) || !Graspable(g.caster, c)) continue;
                    foreach (var slot in g.slots)
                    {
                        if (!SpellPlans.GraspTouches(slot.middle, g.dir, c.FeetPosition)) continue;
                        g.grabbed.Add(c);
                        if (!ShieldStops(c, g.caster, c.transform.position - g.dir * 2f, CardId.UndyingGrasp) && !ParryCatch(c, g.caster, c.FeetPosition)) Grasp(c, g.caster, now);
                        break;
                    }
                }
            }
        }

        static bool Graspable(PlayerController by, Combatant c) =>
            c.IsAlive && !Combatant.Allies(by, c) && !Folded(c) && !Shielded(c) && !Unmovable(c) && !Hanging(c) && !c.Pinned
            && (c.ViewFlags & NetState.Ragdoll) == 0;

        static void Grasp(Combatant c, PlayerController by, double now)
        {
            var st = c.Status.Value;
            st.GraspFrom = now;
            st.GraspUntil = now + SpellPlans.GraspHold;
            c.Status.Value = st;
            c.ServerCredit(by);
            HeldsS.RemoveAll(h => h.victim == c);
            HeldsS.Add(new HeldState { victim = c, from = now, until = st.GraspUntil, spot = c.transform.position });
        }

        static void TickHeld(double now)
        {
            for (int i = HeldsS.Count - 1; i >= 0; i--)
            {
                if (i >= HeldsS.Count) continue;
                var h = HeldsS[i];
                var c = h.victim;
                if (c == null || !c.IsSpawned || now > h.until) { HeldsS.RemoveAt(i); continue; }
                // (a moment for their own machine to hear they're held, before where they are counts)
                if (!h.settled && now > h.from + 0.35) { h.settled = true; h.spot = c.transform.position; }
                Vector3 off = c.transform.position - h.spot;
                off.y = 0f;
                bool ragdolled = (c.ViewFlags & NetState.Ragdoll) != 0;
                if (!c.IsAlive || ragdolled || c.Pinned || c.Status.Value.Taken(now) || h.settled && off.magnitude > 1.5f) FreeGrasp(c);
            }
        }

        /// <summary>Held by the hands and hurt: past <see cref="SpellPlans.GraspBreak"/> taken, they let go.</summary>
        public static void GraspDamaged(Combatant c, float landed)
        {
            if (landed <= 0f) return;
            var h = HeldsS.Find(x => x.victim == c);
            if (h == null) return;
            h.taken += landed;
            if (h.taken >= SpellPlans.GraspBreak) FreeGrasp(c);
        }

        /// <summary>The hands let go (ragdolled, dead, carried off, hurt enough): free to move and cast again.</summary>
        public static void FreeGrasp(Combatant c)
        {
            HeldsS.RemoveAll(h => h.victim == null || h.victim == c);
            if (c == null || !c.IsSpawned) return;
            var st = c.Status.Value;
            if (st.GraspUntil <= 0) return;
            st.GraspUntil = 0;
            c.Status.Value = st;
        }
    }
}
