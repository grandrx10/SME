using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for The Star, The Moon, The Sun and The World. (Judgement: see TickJudgments.)
    ///  - The Star: the right hand up 0.3 s (knocked down meanwhile: none), then four stars over the caster's head, one more
    ///    for every earlier cast that hit someone (once per cast, up to 12; back to four when they die). Every 0.1 s
    ///    one flies at the caster's aim, 25 m at most, whatever happens to them meanwhile (short of dying). Whoever one
    ///    hits takes 5 and is outlined through walls for 3 s.
    ///  - The Moon: hands clasped 0.3 s, then a moon over the caster for 15 s (ragdolled or not) that fills as they hurt enemies.
    ///    Full after 30 damage: it heals them 15. Not full in time: it just goes.
    ///  - The Sun: 1.5 s after the cast the caster's colours are inverted for 3 s; anyone with them on their screen (nothing
    ///    in the way) takes 12 a second. Ragdolled: over.
    ///  - The World: hands clasped 0.5 s (knocked down meanwhile: nothing), then caster and target are crushed away, and a
    ///    sphere of radius 8.5 m swells 1.5 m above where the caster stood (sunk into the ground; moved to the nearest place with
    ///    room, if something unbreakable is in the way), throwing anyone else out: ground below, a glass dome
    ///    above, its shell unbreakable. The caster is at full health again. The two come back inside on opposite sides with no mana, and stay there till one is
    ///    dead or 12 s are up. Then the dome shatters. Not inside another World.
    /// </summary>
    public static partial class SpellServer
    {
        class StarState
        {
            public int id; public PlayerController caster; public double start; public int next, count;
            public bool scored; // one of them hit someone (the next cast gets one more)
            public readonly Vector3[] lastPos = new Vector3[SpellPlans.StarMax];
            public readonly ulong[] owner = new ulong[SpellPlans.StarMax]; // whose each one is (The Hierophant sends them back as its own)
        }
        class PendingStars { public PlayerController caster; public double at; }

        class WorldState
        {
            public int id, phase; public PlayerController caster; public Combatant target; public double castAt, crushAt, growAt, readyAt;
            public Vector3Int cell; public Vector3 center, spotA, spotB; public float yawA, yawB; public ushort body, top;
            public readonly Dictionary<Combatant, double> pushed = new();
            public readonly HashSet<Combatant> putOut = new();
            public bool Fighter(Combatant c) => c != null && (c == caster || c == target);
        }

        static readonly List<PendingStars> PendingStarsS = new();
        static readonly List<StarState> StarsS = new();
        /// <summary>Extra stars each caster's next cast gets (one per earlier cast that hit; until they die).</summary>
        static readonly Dictionary<PlayerController, int> StarBonus = new();

        static readonly List<WorldState> WorldsS = new();
        /// <summary>(who's looking, whose Sun) as their owners report it.</summary>
        static readonly HashSet<(PlayerController looker, ulong sun)> Gazes = new();
        static readonly Dictionary<(Combatant, Combatant), float> _sunOwed = new();

        const int WorldClasping = 0, WorldCrushing = 1, WorldGrowing = 2, WorldFight = 3;

        static void ResetTarot3()
        {
            PendingStarsS.Clear();
            StarsS.Clear();
            StarBonus.Clear();
            WorldsS.Clear();
            Gazes.Clear();
            _sunOwed.Clear();
        }

        static bool CastTarot3(PlayerController caster, CardId card, ulong targetId, double now)
        {
            switch (card)
            {
                case CardId.TheStar:
                    PendingStarsS.RemoveAll(p => p.caster == caster);
                    PendingStarsS.Add(new PendingStars { caster = caster, at = now + SpellPlans.StarRaise });
                    WorldNet.ServerGather(caster.NetworkObjectId, true, now, SpellPlans.StarRaise);
                    return true;
                case CardId.TheMoon:
                {
                    var st = caster.Status.Value;
                    if (st.MoonAt > now) return false; // one's still filling
                    st.MoonFrom = now + SpellPlans.MoonClasp;
                    st.MoonAt = st.MoonFrom + SpellPlans.MoonTime;
                    st.MoonFill = 0f;
                    caster.Status.Value = st;
                    WorldNet.ServerGather(caster.NetworkObjectId, false, now, SpellPlans.MoonClasp);
                    return true;
                }
                case CardId.TheSun:
                {
                    var st = caster.Status.Value;
                    if (st.SunUntil > now) return false; // still going
                    st.SunCast = now;
                    st.SunFrom = now + SpellPlans.SunDelay;
                    st.SunUntil = st.SunFrom + SpellPlans.SunTime;
                    caster.Status.Value = st;
                    return true;
                }
                case CardId.TheWorld:
                {
                    var t = WorldNet.FindCombatant(targetId);
                    if (t == null || t == caster || !t.IsAlive || Combatant.Allies(caster, t) || Room(t) != Room(caster)) return false;
                    if ((t.transform.position - caster.transform.position).magnitude > SpellPlans.WorldReach + 2f) return false; // a little slack for lag
                    if (!WorldNet.RoomForWorld(caster.transform.position)) return false; // in (or up against) another World
                    foreach (var w in WorldsS) // ...or one that's just coming
                    {
                        if (w.Fighter(caster) || w.Fighter(t)) return false;
                        if (w.phase < WorldFight && w.caster != null && (w.caster.transform.position - caster.transform.position).magnitude < SpellPlans.WorldRadius * 2f + 1f) return false;
                    }
                    WorldsS.Add(new WorldState { caster = caster, target = t, castAt = now });
                    WorldNet.ServerGather(caster.NetworkObjectId, false, now, SpellPlans.WorldClasp, true);
                    return true;
                }
            }
            return false;
        }

        static void TickTarot3(WorldNet net, double now, float dt)
        {
            TickStars(net, now);
            TickMoons(now);
            TickSuns(now, dt);
            TickWorlds(now);
        }

        /// <summary>Knocked down mid-cast: no stars, no World.</summary>
        static void FizzleTarot3(PlayerController pc, double now)
        {
            PendingStarsS.RemoveAll(p => p.caster == pc);
            WorldsS.RemoveAll(w => w.caster == pc && w.phase == WorldClasping);
        }

        /// <summary>Knocked down at any point: the sun is over.</summary>
        static void InterruptTarot3(PlayerController pc, bool knocked)
        {
            if (!knocked || !pc.IsSpawned) return;
            var st = pc.Status.Value;
            if (st.SunUntil == 0) return;
            st.SunUntil = 0;
            pc.Status.Value = st;
        }

        /// <summary>A practice chamber is being reset: what's going on in it (or by its player) is dropped.</summary>
        /// <summary>A player died: their next Star is back to four.</summary>
        static void ForgetStars(Combatant c)
        {
            if (c is PlayerController pc) StarBonus.Remove(pc);
        }

        static void ClearTarot3(PlayerController pc, System.Func<Vector3, bool> inside)
        {
            PendingStarsS.RemoveAll(p => p.caster == pc);
            if (pc != null) StarBonus.Remove(pc);

            foreach (var s in StarsS.ToArray()) if (s.caster == pc) EndStars(s, true);
            WorldsS.RemoveAll(w => w.Fighter(pc) || inside(w.center));
        }

        // ------------------------------------------------------------------ The Star

        static Vector3 ViewDir(Combatant c) => Quaternion.Euler(c.ViewPitch, c.ViewYaw, 0f) * Vector3.forward;

        /// <summary>The caster's owner sends their next star along <paramref name="dir"/>.</summary>
        public static void LaunchStar(PlayerController caster, int id, int index, Vector3 dir)
        {
            var s = StarsS.Find(x => x.id == id);
            if (s == null || s.caster != caster || index != s.next || !caster.IsAlive) return;
            if (WorldNet.Now < s.start + SpellPlans.StarEvery * (index + 1) - 0.1) return;
            SendStar(s, dir);
        }

        static void SendStar(StarState s, Vector3 dir)
        {
            var net = WorldNet.Instance;
            if (net == null || s.next >= s.count || !net.Stars.TryGetValue(s.id, out var fx)) return;
            if (dir.sqrMagnitude < 1e-4f) dir = ViewDir(s.caster);
            int k = s.next++;
            Vector3 from = WorldNet.StarPosition(fx, k, WorldNet.Now);
            s.lastPos[k] = from;
            s.owner[k] = s.caster.NetworkObjectId;
            WorldNet.ServerStarLaunch(s.id, k, from, dir.normalized);
        }

        /// <summary>The stars still over the caster's head go (they died); with <paramref name="flying"/>, those in the air too.</summary>
        static void EndStars(StarState s, bool flying)
        {
            var net = WorldNet.Instance;
            if (net != null && net.Stars.TryGetValue(s.id, out var fx))
                for (int k = flying ? 0 : s.next; k < s.count; k++)
                    if (fx.stars[k].Alive) WorldNet.ServerStarEnd(s.id, k, WorldNet.StarPosition(fx, k, WorldNet.Now), false);
            if (flying) StarsS.Remove(s);
            s.next = s.count;
        }

        static void TickStars(WorldNet net, double now)
        {
            for (int i = PendingStarsS.Count - 1; i >= 0; i--)
            {
                var p = PendingStarsS[i];
                if (now < p.at) continue;
                PendingStarsS.RemoveAt(i);
                if (p.caster == null || !p.caster.IsAlive) continue;
                StarBonus.TryGetValue(p.caster, out int extra);
                int count = Mathf.Min(SpellPlans.StarCount + extra, SpellPlans.StarMax);
                int id = WorldNet.ServerStars(p.caster.NetworkObjectId, now, count);
                if (id != 0) StarsS.Add(new StarState { id = id, caster = p.caster, start = now, count = count });
            }
            for (int i = StarsS.Count - 1; i >= 0; i--)
            {
                var s = StarsS[i];
                if (!net.Stars.TryGetValue(s.id, out var fx)) { StarsS.RemoveAt(i); continue; }
                bool gone = s.caster == null || !s.caster.IsAlive;
                if (s.next < s.count)
                {
                    if (gone) EndStars(s, false);
                    // the owner never said where (lag): it goes where they're facing
                    else if (now > s.start + SpellPlans.StarEvery * (s.next + 1) + 0.3) SendStar(s, ViewDir(s.caster));
                }
                bool any = s.next < s.count;
                for (int k = 0; k < s.count; k++)
                {
                    var b = fx.stars[k];
                    if (!b.launched || !b.Alive) continue;
                    any |= FlyStar(s, fx, k, now);
                }
                if (!any) StarsS.Remove(s);
            }
        }

        /// <summary>One flying star's frame. False: it's gone.</summary>
        static bool FlyStar(StarState s, WorldNet.StarFx fx, int k, double now)
        {
            var b = fx.stars[k];
            Vector3 pos = WorldNet.StarPosition(fx, k, now), last = s.lastPos[k], step = pos - last;
            float dist = step.magnitude;
            var by = WorldNet.FindCaster(s.owner[k]);
            if (BarrierBlocks(s.owner[k], pos, SpellPlans.StarRadius)) { WorldNet.ServerStarEnd(s.id, k, pos, true); return false; }
            var bubble = BubbleBlocking(s.owner[k], pos, SpellPlans.StarRadius);
            if (bubble != null)
            {
                HurtBubble(bubble, SpellPlans.StarDamage, pos);
                WorldNet.ServerStarEnd(s.id, k, pos, true);
                return false;
            }
            if (dist > 1e-4f && SweepBall(s.owner[k], last, step / dist, dist, SpellPlans.StarRadius, out var hit, out var victim) && !(victim != null && Combatant.Allies(by, victim)))
            {
                if (victim != null)
                {
                    bool touched = !Invulnerable(victim);
                    Hit(victim, by, SpellPlans.StarDamage, hit.point, Vector3.zero, 0f, from: hit.point - step.normalized);
                    // the caster's own star hit an enemy: their next cast gets one more (once per cast)
                    if (touched && !s.scored && s.caster != null && s.owner[k] == s.caster.NetworkObjectId && !Combatant.Allies(s.caster, victim))
                    {
                        s.scored = true;
                        StarBonus.TryGetValue(s.caster, out int extra);
                        StarBonus[s.caster] = Mathf.Min(extra + 1, SpellPlans.StarMax - SpellPlans.StarCount);
                    }
                    if (touched && victim.IsAlive)
                    {
                        // seen through everything for a while (another star starts it over)
                        var st = victim.Status.Value;
                        st.LitUntil = now + SpellPlans.StarLight;
                        victim.Status.Value = st;
                    }
                }
                WorldNet.ServerStarEnd(s.id, k, hit.point, true);
                return false;
            }
            s.lastPos[k] = pos;
            if (SpellPlans.StarSpeed * (now - b.t) >= SpellPlans.StarRange) { WorldNet.ServerStarEnd(s.id, k, pos, false); return false; }
            return true;
        }

        /// <summary>The Hierophant's wall sends stars back (as its caster's own, a full flight again).</summary>
        static void ReflectStars(HierophantState h, ulong me, WorldNet net, double now)
        {
            foreach (var s in StarsS)
            {
                if (!net.Stars.TryGetValue(s.id, out var fx)) continue;
                for (int k = 0; k < fx.stars.Length; k++)
                {
                    var b = fx.stars[k];
                    if (!b.launched || !b.Alive || s.owner[k] == me) continue;
                    if (!SpellPlans.HierophantTouch(s.lastPos[k], WorldNet.StarPosition(fx, k, now), SpellPlans.StarRadius, h.center, h.rot, out var at, out var n)) continue;
                    if (Vector3.Dot(b.dir, n) >= 0f) continue;
                    Vector3 from = at + n * 0.05f;
                    s.owner[k] = me;
                    s.lastPos[k] = from;
                    WorldNet.ServerStarLaunch(s.id, k, from, SpellPlans.Reflect(b.dir, n));
                    WorldNet.ServerHierophantReflect(h.id, at);
                }
            }
        }

        // ------------------------------------------------------------------ The Moon

        /// <summary>Time's up before it was full: it goes.</summary>
        static void TickMoons(double now)
        {
            foreach (var pc in PlayerController.All)
            {
                if (pc == null || !pc.IsSpawned) continue;
                var st = pc.Status.Value;
                if (st.MoonAt <= 0 || now < st.MoonAt) continue;
                st.MoonAt = 0;
                pc.Status.Value = st;
            }
        }

        /// <summary>A player hurt an enemy: their moon fills; full, they're healed.</summary>
        static void MoonFills(PlayerController by, Combatant victim, float dmg, double now)
        {
            var st = by.Status.Value;
            if (!st.Moon(now) || Combatant.Allies(by, victim) || Combatant.Teammates(by, victim)) return;
            st.MoonFill = Mathf.Min(1f, st.MoonFill + dmg / SpellPlans.MoonNeeds);
            bool full = st.MoonFill >= 0.9999f;
            if (full) st.MoonAt = 0;
            by.Status.Value = st;
            if (!full) return;
            by.ServerHeal(SpellPlans.MoonHeal, SpellPlans.MoonHealTime);
            WorldNet.ServerMoonFull(by.NetworkObjectId);
        }

        // ------------------------------------------------------------------ The Sun

        /// <summary>A player's owner says this caster's Sun is on their screen now (or not any more).</summary>
        public static void SunGaze(PlayerController looker, ulong sun, bool on)
        {
            if (looker == null) return;
            if (on) Gazes.Add((looker, sun));
            else Gazes.Remove((looker, sun));
        }

        static readonly List<Combatant> _suns = new();

        static void TickSuns(double now, float dt)
        {
            _suns.Clear();
            foreach (var c in Combatant.Registry) if (c != null && c.IsAlive && c.Status.Value.Sun(now)) _suns.Add(c);
            if (_suns.Count == 0)
            {
                if (_sunOwed.Count > 0) _sunOwed.Clear();
                if (Gazes.Count > 0) Gazes.RemoveWhere(g => WorldNet.FindCombatant(g.sun) is not { } s || !s.Status.Value.Sun(now));
                return;
            }
            foreach (var sun in _suns)
            foreach (var o in Combatant.Registry.ToArray())
            {
                if (o == null || o == sun || !o.IsAlive || Room(o) != Room(sun) || Combatant.Allies(sun, o)) continue;
                bool looking = o is PlayerController p ? Gazes.Contains((p, sun.NetworkObjectId)) : DummyLooks(o, sun);
                if (!looking) continue;
                _sunOwed.TryGetValue((sun, o), out float owed);
                owed += SpellPlans.SunDps * dt;
                if (owed >= SpellPlans.SunDps * SpellPlans.SunBite)
                {
                    o.ServerApplyDamage(owed, sun is PlayerController me ? me.OwnerClientId : Combatant.NoAttacker, o.transform.position, Vector3.up, -1, 0f, DeathCause.Spell);
                    owed = 0f;
                }
                _sunOwed[(sun, o)] = owed;
            }
        }

        /// <summary>A dummy (or zombie) facing the sun, with nothing in between.</summary>
        static bool DummyLooks(Combatant o, Combatant sun)
        {
            Vector3 eye = o.transform.position + Vector3.up * 0.75f, to = sun.transform.position + Vector3.up * 0.3f;
            Vector3 flat = new(to.x - eye.x, 0f, to.z - eye.z);
            if (Vector3.Angle(o.transform.forward, flat) > SpellPlans.SunDummyAngle) return false;
            return !Physics.Linecast(eye, to, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        // ------------------------------------------------------------------ The World

        static void TickWorlds(double now)
        {
            for (int i = WorldsS.Count - 1; i >= 0; i--)
            {
                var w = WorldsS[i];
                bool both = w.caster != null && w.caster.IsAlive && w.target != null && w.target.IsAlive;
                switch (w.phase)
                {
                    case WorldClasping:
                        if (!both) { WorldsS.RemoveAt(i); break; }
                        if (now < w.castAt + SpellPlans.WorldClasp) break;
                        if (!StartWorld(w, now)) WorldsS.RemoveAt(i);
                        break;
                    case WorldCrushing:
                        if (!both) { ReturnFromWorld(w); WorldsS.RemoveAt(i); break; } // one of them left the game
                        if (now < w.growAt) break;
                        w.phase = WorldGrowing;
                        w.id = WorldNet.ServerWorld(w.caster.NetworkObjectId, w.target.NetworkObjectId, w.cell, w.body, w.top, now);
                        break;
                    case WorldGrowing:
                        PushFromWorld(w, now);
                        if (!both) { ReturnFromWorld(w); EndWorld(w); break; }
                        if (now >= w.readyAt) FightInWorld(w, now);
                        break;
                    default:
                        // one of them dead, or time's up: the dome shatters
                        if (!both || now >= w.readyAt + SpellPlans.WorldLife) { EndWorld(w); break; }
                        KeepInWorld(w, now);
                        break;
                }
            }
        }

        /// <summary>The clasp is over: both are freed from whatever held them and crushed away.</summary>
        static bool StartWorld(WorldState w, double now)
        {
            var world = VoxelWorld.Current;
            Vector3 feet = w.caster.FeetPosition;
            if (world == null || !WorldNet.RoomForWorld(w.caster.transform.position)) return false;
            // what the ground the caster stood on is made of (or the nearest below; any ground at all, over a drop)
            var under = world.CellOf(feet + Vector3.down * 0.1f);
            int top = world.TopSolid(under.x, under.z, under.y - 40, under.y + 1);
            int ys = top == int.MinValue ? under.y : top;
            w.body = SpellPlans.GroundValue(world, under.x, ys, under.z, false);
            w.top = SpellPlans.GroundValue(world, under.x, ys, under.z, true);
            // the sphere half sunk into the ground there (or the nearest place it fits)
            w.cell = WorldSpot(world, world.CellOf(feet + Vector3.up * SpellPlans.WorldLift));
            w.center = world.CellCenter(w.cell);
            w.caster.Health.Value = w.caster.MaxHealth;
            // opposite sides, facing each other
            Vector3 dir = w.target.transform.position - w.caster.transform.position;
            dir.y = 0f;
            dir = dir.sqrMagnitude > 0.01f ? dir.normalized : Quaternion.Euler(0f, w.caster.ViewYaw, 0f) * Vector3.forward;
            Vector3 floor = new(w.center.x, world.CellMin(w.cell).y + VoxelWorld.Size, w.center.z);
            float reach = SpellPlans.WorldRadius * SpellPlans.WorldSpotShare;
            w.spotA = floor - dir * reach + Vector3.up * 1f;
            w.spotB = floor + dir * reach + Vector3.up * 1f;
            w.yawA = Quaternion.LookRotation(dir).eulerAngles.y;
            w.yawB = w.yawA + 180f;
            w.crushAt = now;
            w.growAt = now + SpellPlans.WorldCrush;
            w.readyAt = now + SpellPlans.WorldAway;
            w.phase = WorldCrushing;
            Take(w.caster, w.caster, w.spotA, w.yawA, now, w.readyAt);
            Take(w.caster, w.target, w.spotB, w.yawB, now, w.readyAt);
            return true;
        }

        // ---------------- somewhere it fits

        /// <summary>The cell nearest <paramref name="want"/> the sphere can be raised round: nothing unbreakable in its upper half, in the arena, clear of any other World. (Nowhere: there, and what won't break stays in it.)</summary>
        static Vector3Int WorldSpot(VoxelWorld world, Vector3Int want)
        {
            if (WorldFits(world, want)) return want;
            float S = VoxelWorld.Size;
            var spots = new List<(float far, Vector3Int cell)>();
            for (float across = 0f; across <= SpellPlans.WorldSearch; across += 2.5f)
            {
                int turns = across <= 0f ? 1 : Mathf.Clamp(Mathf.CeilToInt(across * 2f), 6, 32);
                for (int k = 0; k < turns; k++)
                for (float up = 0f; up <= SpellPlans.WorldSearchUp; up += 2f)
                {
                    float a = k * Mathf.PI * 2f / turns;
                    spots.Add((across + up * 1.5f, want + new Vector3Int(Mathf.RoundToInt(Mathf.Cos(a) * across / S), Mathf.RoundToInt(up / S), Mathf.RoundToInt(Mathf.Sin(a) * across / S))));
                }
            }
            spots.Sort((x, y) => x.far.CompareTo(y.far));
            foreach (var s in spots) if (WorldFits(world, s.cell)) return s.cell;
            return want;
        }

        static readonly Collider[] _worldIn = new Collider[64];

        static bool WorldFits(VoxelWorld world, Vector3Int c)
        {
            Vector3 center = world.CellCenter(c);
            if (ArenaLimits.Outside(center, ArenaLimits.Margin + 2f, false) || WorldNet.WorldAround(center, SpellPlans.WorldRadius + 1f) != null) return false;
            // structures that aren't voxels at all
            int n = Physics.OverlapSphereNonAlloc(center + Vector3.up * 1f, SpellPlans.WorldRadius - 1f, _worldIn, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var s = _worldIn[i];
                // (not ground on the move, rubble, or a portal)
                if (s == null || VoxelWorld.IsVoxelCollider(s) || s.attachedRigidbody != null || s.bounds.max.y <= center.y) continue;
                if (s.GetComponentInParent<PortalView>() == null && s.GetComponentInParent<VoxelBody>() == null && s.GetComponentInParent<TrainCar>() == null) return false;
            }
            // coarsely first (most places that won't do show it at once), then every cell
            return ClearAbove(world, c, 3) && ClearAbove(world, c, 1);
        }

        static bool ClearAbove(VoxelWorld world, Vector3Int c, int step)
        {
            int r = SpellPlans.WorldCells;
            for (int y = 1; y <= r; y += step)
            for (int x = -r; x <= r; x += step)
            for (int z = -r; z <= r; z += step)
            {
                if (x * x + y * y + z * z > r * r) continue;
                if (world.IsIndestructible(world.Get(c.x + x, c.y + y, c.z + z))) return false;
            }
            return true;
        }

        static void Take(Combatant by, Combatant c, Vector3 spot, float yaw, double now, double until)
        {
            ReleaseFromCarry(c);
            foreach (var d in Domains) if (d.trapped.Remove(c)) RefreshTrapped(c);
            foreach (var ch in ChariotsS.ToArray())
                if (ch.victim == c) { ch.victim = null; c.PinnedBy.Value = 0; }
            foreach (var h in HooksS.ToArray()) if (h.victim == c || h.caster == c) EndHook(h);
            EndHang(c);
            if (c is PlayerController pc)
            {
                OnInterrupted(pc, true); // whatever they were doing with their body stops
                foreach (var b in BubblesS.ToArray()) if (b.caster == pc) EndBubble(b, true);
                pc.ServerEndCast();
            }
            var st = c.Status.Value;
            st.WorldFrom = now;
            st.WorldUntil = until;
            st.WorldSpot = spot;
            st.WorldYaw = yaw;
            st.WorldBy = by.NetworkObjectId;
            c.Status.Value = st;
        }

        /// <summary>It came to nothing (someone left the game): whoever's still here unfolds where they stood.</summary>
        static void ReturnFromWorld(WorldState w)
        {
            foreach (var c in new[] { w.caster, w.target })
            {
                if (c == null || !c.IsSpawned) continue;
                var st = c.Status.Value;
                st.WorldUntil = WorldNet.Now;
                c.Status.Value = st;
            }
        }

        /// <summary>Anyone else the swelling sphere reaches is thrown out ahead of it; anyone it still caught at the end is put outside.</summary>
        static void PushFromWorld(WorldState w, double now)
        {
            float r = SpellPlans.WorldRadiusAt((float)(now - w.growAt));
            bool done = r >= SpellPlans.WorldRadius;
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || !c.IsAlive || w.Fighter(c)) continue;
                Vector3 d = c.transform.position - w.center;
                if (d.magnitude > r + 0.6f) continue;
                if (done) { if (w.putOut.Add(c)) OutOfWorld(w, c); continue; }
                if (w.pushed.TryGetValue(c, out double at) && now < at + 0.25) continue;
                w.pushed[c] = now;
                Vector3 away = new Vector3(d.x, 0f, d.z);
                away = away.sqrMagnitude > 0.01f ? away.normalized : Random.onUnitSphere;
                away.y = 0f;
                Hit(c, w.caster, 0f, c.transform.position, away.normalized * SpellPlans.WorldPush + Vector3.up * SpellPlans.WorldPushUp, SpellPlans.WorldPushRagdoll, from: w.center, card: CardId.TheWorld);
            }
        }

        /// <summary>Puts a body just outside the sphere, the way it's nearest to, on whatever ground there is.</summary>
        static void OutOfWorld(WorldState w, Combatant c)
        {
            Vector3 d = c.transform.position - w.center;
            Vector3 away = new(d.x, 0f, d.z);
            away = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward;
            Vector3 p = w.center + away * (SpellPlans.WorldRadius + 1.5f);
            Vector3 from = new(p.x, w.center.y + SpellPlans.WorldRadius + 5f, p.z);
            if (Physics.Raycast(from, Vector3.down, out var hit, SpellPlans.WorldRadius * 2f + 20f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                p.y = hit.point.y + 1f;
            else p.y = w.center.y + 2f;
            c.ServerPlace(p, c.ViewYaw);
        }

        /// <summary>The sphere stands and both are in it: no mana, and it's on.</summary>
        static void FightInWorld(WorldState w, double now)
        {
            w.phase = WorldFight;
            VoxelWorld.Current?.FinishLate();
            foreach (var c in new[] { (Combatant)w.caster, w.target })
            {
                var other = c == w.caster ? w.target : w.caster;
                c.ServerCredit(other as PlayerController); // (dying to the World itself is the other's kill)
                if (c is PlayerController pc)
                {
                    pc.Mana.Value = pc.Mana.Value.With(now, 0f);
                    RefreshFlow(pc);
                }
                else if (c is TestDummy d) d.ServerDrainMana(99f);
            }
        }

        /// <summary>Neither may leave (put back if something carried them out), and nobody else may come in.</summary>
        static void KeepInWorld(WorldState w, double now)
        {
            if (now < w.readyAt + 0.5) return; // (the server may still see them where they were)
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || !c.IsAlive || Room(c) != Room(w.caster)) continue;
                float d = (c.transform.position - w.center).magnitude;
                if (w.pushed.TryGetValue(c, out double at) && now < at + 0.5) continue; // (being moved already)
                if (w.Fighter(c))
                {
                    if (d < SpellPlans.WorldRadius) continue;
                    w.pushed[c] = now;
                    c.ServerPlace(c == w.caster ? w.spotA : w.spotB, c == w.caster ? w.yawA : w.yawB);
                }
                else if (d < SpellPlans.WorldRadius - 1f && c.Team != w.caster.Team && c.Team != w.target.Team)
                {
                    w.pushed[c] = now;
                    OutOfWorld(w, c);
                }
            }
        }



        static void EndWorld(WorldState w)
        {
            WorldsS.Remove(w);
            if (w.id != 0) WorldNet.ServerWorldBreak(w.id);
        }
    }
}
