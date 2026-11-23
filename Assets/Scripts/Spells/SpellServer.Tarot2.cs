using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for The Hierophant, The Lovers and The Chariot.
    ///  - The Hierophant: 0.5 s after the cast a 4 x 4 m mahogany pane stands where it was aimed (3 m away at most, tilted to face the
    ///    caster's aim) for 6 s. Enemy projectiles that touch it bounce off and become the caster's.
    ///  - The Lovers: a beam is thrown at a target (heavily homing, 10 m at most). Blocked or short: it fails. Caught:
    ///    the two are tethered and both heal 5/s while the caster's mana drains 1/s (no regeneration). It snaps past
    ///    20 m, without line of sight, out of mana, or when cancelled (pressing the card again). Then: cooldown.
    ///  - The Chariot: the caster charges, faster and faster, the way they faced (the mouse steers it up to 25 degrees
    ///    either way; no other cards, no mana regeneration), smashing through anything under 6 voxels high (the chunks
    ///    hit for 10) and stopping at anything taller. Nothing knocks back or ragdolls the driver until it ends. People don't stop it: the first one run into is picked up (limp,
    ///    stuck on the front) and carried until they hit a wall, taking 50 (at a crash with nobody on the front, the
    ///    driver takes it). Anyone else is bowled aside. Two chariots meeting: both thrown back, 25 each.
    /// </summary>
    public static partial class SpellServer
    {
        class HierophantState
        {
            public int id; public PlayerController caster; public Vector3 center; public Quaternion rot; public double start;
            public double Up => start + SpellPlans.HierophantDelay;
            public double End => Up + SpellPlans.HierophantTime;
        }
        class LoversState
        {
            public int id; public PlayerController caster; public Combatant target;
            public Vector3 pos, dir; public float travelled; public bool tethered; public float healOwed;
        }
        class ChariotState
        {
            public PlayerController caster; public Vector3 dir; public float startYaw; public double start;
            public Combatant victim;
            public double nextChunks; // flying lumps are thrown at most this often (the rest of each smash is rubble)
            public readonly HashSet<Combatant> bowled = new();
        }

        static readonly List<HierophantState> HierophantsS = new();
        static readonly List<LoversState> LoversS = new();
        static readonly List<ChariotState> ChariotsS = new();

        static void ResetTarot2()
        {
            HierophantsS.Clear();
            LoversS.Clear();
            foreach (var c in ChariotsS) if (c.victim != null && c.victim.IsSpawned) c.victim.PinnedBy.Value = 0;
            ChariotsS.Clear();
        }

        static bool CastTarot2(PlayerController caster, CardId card, Vector3 target, float yaw, ulong extra, double now)
        {
            switch (card)
            {
                case CardId.TheHierophant:
                {
                    Vector3 chest = caster.transform.position + Vector3.up * 0.4f;
                    if ((target - chest).magnitude > SpellPlans.HierophantRange + SpellPlans.HierophantHeight * 0.5f + 1.5f) return false; // (lifted out of the ground) + lag
                    float pitch = SpellPlans.UnpackAngle(extra);
                    if (float.IsNaN(pitch)) pitch = 0f;
                    pitch = Mathf.Clamp(pitch, -90f, 90f);
                    var h = new HierophantState { caster = caster, center = target, rot = SpellPlans.PortalRotation(yaw, pitch), start = now };
                    h.id = WorldNet.ServerHierophant(caster.NetworkObjectId, target, yaw, pitch, h.Up);
                    HierophantsS.Add(h);
                    return true;
                }
                case CardId.TheLovers:
                {
                    // one beam at a time (pressing the card again cancels it instead; see CancelLovers)
                    if (LoversS.Exists(l => l.caster == caster)) return false;
                    var t = WorldNet.FindCombatant(extra);
                    if (t == null || t == caster || !t.IsAlive) return false;
                    if ((t.transform.position - caster.transform.position).magnitude > SpellPlans.LoversTargetRange + 4f) return false;
                    Vector3 dir = target.sqrMagnitude > 1e-4f ? target.normalized : Quaternion.Euler(caster.ViewPitch, caster.ViewYaw, 0f) * Vector3.forward;
                    Vector3 origin = SpellVisuals.RightHand(caster) + caster.transform.forward * 0.15f;
                    int id = WorldNet.ServerLovers(caster.NetworkObjectId, t.NetworkObjectId, origin, dir, now);
                    LoversS.Add(new LoversState { id = id, caster = caster, target = t, pos = origin, dir = dir });
                    return true;
                }
                case CardId.TheChariot:
                {
                    if (ChariotsS.Exists(c => c.caster == caster)) return false;
                    Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                    ChariotsS.Add(new ChariotState { caster = caster, dir = dir, startYaw = yaw, start = now });
                    // no mana comes back while charging (the cost is taken from this after the cast)
                    caster.Mana.Value = caster.Mana.Value.WithFlow(now, ManaState.Held);
                    WorldNet.ServerChariot(caster.NetworkObjectId, dir, now);
                    return true;
                }
            }
            return false;
        }

        static void TickTarot2(double now, float dt)
        {
            TickLovers(now, dt);
            TickChariots(now);
        }

        // ================================================================== The Hierophant

        /// <summary>Bounces enemy projectiles off the standing walls. Runs before the projectiles move this frame.</summary>
        static void TickHierophants(WorldNet net, double now)
        {
            for (int i = HierophantsS.Count - 1; i >= 0; i--)
            {
                var h = HierophantsS[i];
                if (now > h.End) { HierophantsS.RemoveAt(i); continue; }
                if (now < h.Up || h.caster == null) continue;
                ulong me = h.caster.NetworkObjectId;

                // thrown rocks, flying lumps
                foreach (var rock in Rocks)
                {
                    if (!rock.launched || rock.caster == h.caster || !net.Rocks.TryGetValue(rock.id, out var fx)) continue;
                    Vector3 pos = WorldNet.RockPosition(fx, now);
                    if (!SpellPlans.HierophantTouch(rock.lastPos, pos, rock.radius, h.center, h.rot, out var at, out var n)) continue;
                    Vector3 v = WorldNet.RockVelocity(fx, now);
                    if (Vector3.Dot(v, n) >= 0f) continue;
                    Vector3 np = at + n * 0.05f;
                    WorldNet.ServerRockLaunch(rock.id, np, SpellPlans.Reflect(v, n));
                    rock.caster = h.caster;
                    rock.ignore = null;
                    rock.lastPos = np;
                    rock.lastTime = now;
                    WorldNet.ServerHierophantReflect(h.id, at);
                }
                // fireballs
                foreach (var f in FireballsS)
                {
                    if (!f.launched || !net.Fireballs.TryGetValue(f.id, out var fx) || fx.owner == me) continue;
                    Vector3 pos = WorldNet.FireballPosition(fx, now);
                    if (!SpellPlans.HierophantTouch(f.lastPos, pos, SpellPlans.FireballRadius, h.center, h.rot, out var at, out var n)) continue;
                    if (Vector3.Dot(fx.velocity, n) >= 0f) continue;
                    Vector3 np = at + n * 0.05f;
                    WorldNet.ServerFireballLaunch(f.id, np, SpellPlans.Reflect(fx.velocity, n), me);
                    f.lastPos = np;
                    WorldNet.ServerHierophantReflect(h.id, at);
                }
                ReflectStars(h, me, net, now);
                ReflectPellets(h, me, net, now);
                ReflectBombs(h, me, net, now);
                // mana missiles (they stop tracking and fly straight back out)
                foreach (var v in Volleys)
                {
                    if (!v.launched || !net.Missiles.TryGetValue(v.id, out var mfx)) continue;
                    for (int m = 0; m < mfx.m.Length; m++)
                    {
                        var ms = mfx.m[m];
                        if (!ms.alive || ms.owner == me) continue;
                        if (!SpellPlans.HierophantTouch(ms.prev, ms.pos, SpellPlans.MissileRadius, h.center, h.rot, out var at, out var n)) continue;
                        if (Vector3.Dot(ms.dir, n) >= 0f) continue;
                        WorldNet.ServerMissileRedirect(v.id, m, at + n * 0.05f, SpellPlans.Reflect(ms.dir, n), me);
                        WorldNet.ServerHierophantReflect(h.id, at);
                    }
                }
                // Severance's flying cut: a new cut, of the caster's, goes back the other way
                double dt = Mathf.Max(Time.deltaTime, 1f / 120f);
                for (int k = CutsS.Count - 1; k >= 0; k--)
                {
                    var w = CutsS[k];
                    if (w.caster == h.caster || now < w.start) continue;
                    float front = Mathf.Min(SpellPlans.SeveranceSpeed * (float)(now - w.start), SpellPlans.SeveranceRange);
                    float before = Mathf.Max(0f, front - SpellPlans.SeveranceSpeed * (float)dt);
                    if (front >= SpellPlans.SeveranceRange && before >= SpellPlans.SeveranceRange) continue;
                    Vector3 a = w.origin + w.dir * before, b = w.origin + w.dir * front;
                    if (!SpellPlans.HierophantTouch(a, b, SpellPlans.SeveranceHalfHeight, h.center, h.rot, out var at, out var n)) continue;
                    if (Vector3.Dot(w.dir, n) >= 0f) continue;
                    EndCut(k, w);
                    Vector3 nd = SpellPlans.Reflect(w.dir, n);
                    Vector3 origin = at + n * 0.1f;
                    float tilt = Random.Range(-SpellPlans.SeveranceTilt, SpellPlans.SeveranceTilt);
                    int id = WorldNet.ServerWave(me, origin, nd, tilt, now);
                    CutsS.Add(new CutState { id = id, caster = h.caster, origin = origin, dir = nd, rot = SpellPlans.WaveRotation(nd, tilt), start = now });
                    WorldNet.ServerHierophantReflect(h.id, at);
                }
                // Attraction + Repulsion's fused ball: sent back the way it came, as the caster's
                foreach (var f in FusesS)
                {
                    if (!f.launched || f.caster == h.caster) continue;
                    float travelled = Mathf.Min(SpellPlans.FuseSpeed * (float)(now - f.launchTime), SpellPlans.FuseRange);
                    Vector3 a = f.origin + f.dir * f.swept, b = f.origin + f.dir * travelled;
                    if (!SpellPlans.HierophantTouch(a, b, SpellPlans.FuseRadius1, h.center, h.rot, out var at, out var n)) continue;
                    if (Vector3.Dot(f.dir, n) >= 0f) continue;
                    f.caster = h.caster;
                    f.origin = at + n * 0.05f;
                    f.dir = SpellPlans.Reflect(f.dir, n);
                    f.launchTime = now;
                    f.erased = f.swept = 0f;
                    f.hit.Clear();
                    WorldNet.ServerFuseLaunch(f.id, f.origin, f.dir);
                    WorldNet.ServerHierophantReflect(h.id, at);
                }
            }
        }

        // ================================================================== The Lovers

        /// <summary>The caster's owner pressed the card again: the beam (thrown or tethered) ends.</summary>
        public static void CancelLovers(PlayerController pc)
        {
            int i = LoversS.FindIndex(l => l.caster == pc);
            if (i >= 0) EndLovers(i, LoversS[i], LoversS[i].pos);
        }

        static void TickLovers(double now, float dt)
        {
            int env = Layers.EnvironmentMask;
            for (int i = LoversS.Count - 1; i >= 0; i--)
            {
                var l = LoversS[i];
                var me = l.caster;
                if (me == null || !me.IsAlive) { EndLovers(i, l, l.pos); continue; }
                if (!l.tethered)
                {
                    // the thrown beam, homing in
                    if (l.target == null || !l.target.IsAlive) { EndLovers(i, l, l.pos); continue; }
                    Vector3 aim = SpellPlans.LoversChest(l.target);
                    Vector3 prev = l.pos;
                    SpellPlans.StepLovers(ref l.pos, ref l.dir, aim, dt);
                    Vector3 step = l.pos - prev;
                    l.travelled += step.magnitude;
                    // caught: the closest it came this frame
                    float u = step.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(aim - prev, step) / step.sqrMagnitude) : 0f;
                    Vector3 closest = prev + step * u;
                    bool caught = (closest - aim).magnitude <= SpellPlans.LoversCatch;
                    // blocked (by terrain, a barrier or a bubble) before it got there: it fails
                    Vector3 reach = caught ? closest : l.pos;
                    Vector3 seg = reach - prev;
                    if (seg.sqrMagnitude > 1e-6f && Physics.SphereCast(prev, 0.08f, seg.normalized, out var hit, seg.magnitude, env, QueryTriggerInteraction.Ignore))
                    { l.pos = hit.point; EndLovers(i, l, hit.point); continue; }
                    if (BarrierBlocks(me.NetworkObjectId, reach, 0.1f) || BubbleBlocking(me.NetworkObjectId, reach, 0.1f) != null)
                    { l.pos = reach; EndLovers(i, l, reach); continue; }
                    if (caught)
                    {
                        l.tethered = true;
                        l.pos = aim;
                        me.Mana.Value = me.Mana.Value.WithFlow(now, ManaState.Draining);
                        WorldNet.ServerLoversTether(l.id);
                        continue;
                    }
                    if (l.travelled >= SpellPlans.LoversReach) EndLovers(i, l, l.pos); // never reached anyone
                    continue;
                }

                // tethered: snaps when stretched too far, out of sight, out of mana, or either of them is gone
                var t = l.target;
                if (t == null || !t.IsAlive) { EndLovers(i, l, l.pos); continue; }
                Vector3 a = SpellPlans.LoversChest(me), b = SpellPlans.LoversChest(t);
                l.pos = b;
                bool snapped = (b - a).magnitude > SpellPlans.LoversBreak
                    || Physics.Linecast(a, b, env, QueryTriggerInteraction.Ignore)
                    || me.ManaNow <= 0.001f;
                if (snapped) { EndLovers(i, l, Vector3.Lerp(a, b, 0.5f)); continue; }
                // both ends heal
                l.healOwed += SpellPlans.LoversHeal * dt;
                if (l.healOwed >= 1f)
                {
                    me.ServerHeal(l.healOwed, 0.05f);
                    t.ServerHeal(l.healOwed, 0.05f);
                    l.healOwed = 0f;
                }
            }
        }

        static void EndLovers(int index, LoversState l, Vector3 at)
        {
            LoversS.RemoveAt(index);
            var me = l.caster;
            if (me != null)
            {
                if (l.tethered) me.Mana.Value = me.Mana.Value.WithFlow(WorldNet.Now, ManaState.Refill);
                me.ServerRestartCooldown(CardId.TheLovers); // its cooldown starts when it breaks
            }
            WorldNet.ServerLoversEnd(l.id, at);
        }

        // ================================================================== The Chariot

        /// <summary>Is this player charging with The Chariot right now?</summary>
        public static bool Charging(Combatant c) => c != null && ChariotsS.Exists(x => x.caster == c);

        static void TickChariots(double now)
        {
            for (int i = ChariotsS.Count - 1; i >= 0; i--)
            {
                if (i >= ChariotsS.Count) continue; // a clash took out two at once
                var c = ChariotsS[i];
                var me = c.caster;
                if (me == null || !me.IsAlive) { EndChariot(c, false, c.caster != null ? c.caster.transform.position : Vector3.zero); continue; }
                if (now - c.start > SpellPlans.ChariotMaxTime) { EndChariot(c, false, me.transform.position); continue; }
                c.dir = SpellPlans.ChariotDir(me.ViewYaw); // steered by its driver (turning at a fixed rate)
                // whoever was stuck on the front got free (thrown off, died)
                if (c.victim != null && (!c.victim.IsAlive || c.victim.PinnedBy.Value != me.NetworkObjectId)) c.victim = null;

                // the ball runs into whoever it touches (another ball: the two balls touching)
                Vector3 ball = SpellPlans.ChariotBallCenter(me.transform.position);
                Vector3 right = new(c.dir.z, 0f, -c.dir.x);
                ChariotState clash = null;
                foreach (var o in Combatant.Registry.ToArray())
                {
                    if (o == null || o == me || o == c.victim || !o.IsAlive || c.bowled.Contains(o) || Folded(o) || Invulnerable(o) || Hanging(o)) continue;
                    var other = ChariotsS.Find(x => x.caster == o);
                    if (other != null)
                    {
                        if ((SpellPlans.ChariotBallCenter(o.transform.position) - ball).magnitude > SpellPlans.ChariotRadius * 2f) continue;
                        clash = other;
                        break;
                    }
                    if (!SpellPlans.ChariotTouches(ball, o.transform.position)) continue;
                    Vector3 rel = o.transform.position - ball;
                    // met with the front of the ball: stuck on it (the first one only)
                    if (c.victim == null && Vector3.Dot(rel, c.dir) > 0f && !o.Pinned && !Shielded(o)) { Pin(c, o); continue; }
                    // anyone else in the way is bowled aside
                    c.bowled.Add(o);
                    Vector3 side = Vector3.Dot(rel, right) >= 0f ? right : -right;
                    Hit(o, me, 0f, o.transform.position, side * 9f + c.dir * 6f + Vector3.up * 5f, 1f);
                }
                if (clash != null) Clash(c, clash);
            }
        }

        static void Pin(ChariotState c, Combatant o)
        {
            // they're stopped in whatever they were doing (before being pinned, which a knock would undo)
            if (o is PlayerController pc) OnInterrupted(pc, true);
            o.ServerCredit(c.caster); // carried off a cliff or into lava: their kill
            o.PinnedBy.Value = c.caster.NetworkObjectId;
            c.victim = o;
        }

        /// <summary>Two chariots run into each other: both stop, thrown back hard, 25 each.</summary>
        static void Clash(ChariotState a, ChariotState b)
        {
            var pa = a.caster;
            var pb = b.caster;
            Vector3 mid = (pa.transform.position + pb.transform.position) * 0.5f;
            EndChariot(a, false, mid);
            EndChariot(b, false, mid);
            Vector3 ab = pb.transform.position - pa.transform.position;
            ab.y = 0f;
            ab = ab.sqrMagnitude > 1e-4f ? ab.normalized : a.dir;
            Hit(pa, pb, SpellPlans.ChariotClashDamage, pa.transform.position, -ab * 16f + Vector3.up * 8f, 1.2f);
            Hit(pb, pa, SpellPlans.ChariotClashDamage, pb.transform.position, ab * 16f + Vector3.up * 8f, 1.2f);
            WorldNet.ServerHierophantReflect(0, mid + Vector3.up * 0.4f); // a mahogany burst where they met
        }

        /// <summary>The charge is over. <paramref name="crashed"/>: it hit something tall (the crash is shown).</summary>
        static void EndChariot(ChariotState c, bool crashed, Vector3 at)
        {
            if (!ChariotsS.Remove(c)) return;
            var me = c.caster;
            if (c.victim != null)
            {
                var v = c.victim;
                c.victim = null;
                if (v.IsSpawned) v.PinnedBy.Value = 0;
                // stopped some other way than the crash: they tumble off the front
                if (!crashed && v.IsAlive) { v.ServerCredit(me); v.ServerKnock(c.dir * 6f + Vector3.up * 4f, 0.5f); }
            }
            if (me != null)
            {
                me.Mana.Value = me.Mana.Value.WithFlow(WorldNet.Now, ManaState.Refill);
                me.ServerEndCast();
                WorldNet.ServerChariotEnd(me.NetworkObjectId, crashed, at);
            }
        }

        /// <summary>
        /// Is a smash / crash the driver's owner reports believable (near where the server sees them)? The server sees
        /// a remote driver a little in the past, and at 24 m/s that's metres behind, so the slack grows with the speed.
        /// (Refusing a real one leaves the driver's world with a tunnel nobody else has, and the charge never ending.)
        /// </summary>
        static bool ChariotReportNear(ChariotState c, PlayerController pc, Vector3 feet)
        {
            float speed = SpellPlans.ChariotSpeed((float)(WorldNet.Now - c.start));
            return (feet - pc.FeetPosition).magnitude <= 3f + speed * SpellPlans.ChariotReportLag;
        }

        /// <summary>The driver's owner ran into terrain under 6 voxels: it's knocked away (they already broke it on their screen).</summary>
        public static void ChariotSmash(PlayerController pc, Vector3 feet, Vector3 dir)
        {
            var c = ChariotsS.Find(x => x.caster == pc);
            if (c == null || !pc.IsAlive || !ChariotReportNear(c, pc, feet)) return;
            Smash(c, feet, dir);
        }

        /// <summary>The driver's owner ran into terrain 6 voxels high (or more): the crash.</summary>
        public static void ChariotCrash(PlayerController pc, Vector3 feet, Vector3 dir)
        {
            var c = ChariotsS.Find(x => x.caster == pc);
            if (c == null || !pc.IsAlive || !ChariotReportNear(c, pc, feet)) return;
            Smash(c, feet, dir);
            Vector3 point = feet + Vector3.up * SpellPlans.ChariotRadius + c.dir * SpellPlans.ChariotRadius; // the front of the ball
            var victim = c.victim;
            if (victim != null)
            {
                // the one on the front takes it, and stays down a moment longer
                c.victim = null;
                if (victim.IsSpawned) victim.PinnedBy.Value = 0;
                Hit(victim, pc, SpellPlans.ChariotDamage, victim.transform.position, -c.dir * 3f + Vector3.up * 3f, SpellPlans.ChariotStun);
                EndChariot(c, true, point);
            }
            else
            {
                // nobody on the front: the driver takes the crash, thrown back off the wall and ragdolled
                EndChariot(c, true, point);
                Hit(pc, pc, SpellPlans.ChariotDamage, pc.transform.position, -c.dir * SpellPlans.ChariotCrashKnock + Vector3.up * 6f, SpellPlans.ChariotCrashRagdoll);
            }
        }

        /// <summary>
        /// The terrain in the lane ahead is knocked away violently: a few big lumps fly on ahead (10 to anyone they hit),
        /// the rest breaks into rubble.
        /// </summary>
        static void Smash(ChariotState c, Vector3 feet, Vector3 dir)
        {
            var w = VoxelWorld.Current;
            if (w == null) return;
            dir.y = 0f;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : c.dir;
            var cells = SpellPlans.ChariotLane(w, feet, dir, out _);
            if (cells.Count == 0) return;
            float speed = SpellPlans.ChariotSpeed((float)(WorldNet.Now - c.start));
            Vector3 right = new(dir.z, 0f, -dir.x);

            // into lumps a few voxels across
            int n = SpellPlans.ChariotChunkCells;
            var lumps = new Dictionary<Vector3Int, List<VoxelWorld.RemovedCell>>();
            foreach (var rc in cells)
            {
                var key = new Vector3Int(Mathf.FloorToInt(rc.cell.x / (float)n), Mathf.FloorToInt(rc.cell.y / (float)n), Mathf.FloorToInt(rc.cell.z / (float)n));
                if (!lumps.TryGetValue(key, out var list)) lumps[key] = list = new List<VoxelWorld.RemovedCell>();
                list.Add(rc);
            }
            var order = new List<List<VoxelWorld.RemovedCell>>(lumps.Values);
            double now = WorldNet.Now;
            bool chunks = now >= c.nextChunks;
            if (chunks) c.nextChunks = now + SpellPlans.ChariotChunkEvery;
            order.Sort((x, y) => y.Count.CompareTo(x.Count));
            var rubble = new List<Vector3Int>();
            int flying = 0;
            foreach (var piece in order)
            {
                if (!chunks || flying >= SpellPlans.ChariotMaxChunks || piece.Count < 4)
                {
                    foreach (var rc in piece) rubble.Add(rc.cell);
                    continue;
                }
                Vector3 pivot = Vector3.zero;
                foreach (var rc in piece) pivot += w.CellCenter(rc.cell);
                pivot /= piece.Count;
                Vector3 vel = dir * (speed + Random.Range(2f, 7f)) + right * Random.Range(-5f, 5f) + Vector3.up * Random.Range(3f, 7f);
                int id = WorldNet.ServerPushBody(c.caster.NetworkObjectId, piece, pivot, vel);
                if (id == 0) { foreach (var rc in piece) rubble.Add(rc.cell); continue; }
                flying++;
                float size = Mathf.Pow(piece.Count, 1f / 3f) * VoxelWorld.Size;
                Rocks.Add(new RockState
                {
                    id = id, caster = c.caster, start = WorldNet.Now, launched = true, lastPos = pivot, lastTime = WorldNet.Now,
                    damage = SpellPlans.ChariotChunkDamage, radius = Mathf.Clamp(size * 0.45f, 0.25f, 0.8f), envR4 = 5,
                    ignore = c.victim, // the one stuck on the front is carried through it, not hit by it
                });
            }
            if (rubble.Count > 0) WorldNet.ServerWash(rubble, dir);
        }

        /// <summary>Knocked off the front of a chariot: free. (The driver can't be knocked down: see <see cref="Unmovable"/>. If anything gets past that, the charge stops.)</summary>
        static void InterruptTarot2(PlayerController pc, bool knocked)
        {
            if (!knocked) return;
            var c = ChariotsS.Find(x => x.caster == pc);
            if (c != null) EndChariot(c, false, pc.transform.position);
            foreach (var d in ChariotsS)
                if (d.victim == pc) { d.victim = null; pc.PinnedBy.Value = 0; }
        }
    }
}
