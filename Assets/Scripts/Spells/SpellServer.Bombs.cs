using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for the flame bombs.
    ///  - Remote Flame Bomb: 0.3 s of the right hand (knocked down meanwhile: no bomb), then a bomb sticks where the caster was
    ///    looking, within 4 m: ground, walls, ceilings, or a body.
    ///  - Flame Bomb Toss: 0.3 s of both hands, then four fly out at the cursor, falling, scattered; each sticks to the first thing it
    ///    touches (never its thrower).
    ///  - Remote Detonation: 0.3 s of the left hand, then every bomb the caster owns is set off.
    ///  - A bomb is set off by that, by any projectile touching it, by flames (the Flame Thrower, a hellfire wall, being stuck to
    ///    someone burning), or by a flame explosion reaching it (another bomb's too). It
    ///    flashes for 0.3 s, then everything within 2 m is blown out and people there take 10 and are thrown, ragdolled (the caster too); everything
    ///    within 4 m is blackened and people there catch fire once. Water that reaches it first destroys it.
    ///  - What it's stuck to breaks away (or the body is gone): it falls, and sticks to whatever it lands on.
    /// </summary>
    public static partial class SpellServer
    {
        class BombState
        {
            public int id; public PlayerController owner;
            public ulong thrower;                 // never sticks to this body while it flies
            public Vector3 at; public Quaternion rot = Quaternion.identity; // as of the last tick
            public Vector3 lastPos;               // flying: swept up to here
            public double nextCheck, trigger = -1;
            public bool hostDied;
            public int lastGust;
        }
        class BombCast { public PlayerController caster; public double at; public Vector3 target; public float yaw; public ulong where; }

        static readonly List<BombState> BombsS = new();
        static readonly List<BombCast> PlacesS = new(), TossesS = new(), DetonatesS = new();

        static void ResetBombs()
        {
            BombsS.Clear();
            PlacesS.Clear();
            TossesS.Clear();
            DetonatesS.Clear();
        }

        static bool CastBombs(PlayerController caster, CardId card, Vector3 target, float yaw, ulong where, double now)
        {
            var c = new BombCast { caster = caster, target = target, yaw = yaw, where = where };
            switch (card)
            {
                case CardId.RemoteFlameBomb:
                    if (float.IsNaN(target.x + target.y + target.z + yaw) || !BombSpot(c, out var spot)) return false;
                    if (SpellPlans.BombDistance(caster, spot) > SpellPlans.BombReach + 1.5f) return false; // (some slack for lag)
                    c.at = now + SpellPlans.BombPlace;
                    PlacesS.Add(c);
                    return true;
                case CardId.FlameBombToss:
                    c.at = now + SpellPlans.TossTime;
                    TossesS.Add(c);
                    return true;
                case CardId.RemoteDetonation:
                    if (!BombsS.Exists(b => b.owner == caster && b.trigger < 0)) return false;
                    c.at = now + SpellPlans.DetonateTime;
                    DetonatesS.Add(c);
                    return true;
            }
            return false;
        }

        /// <summary>Where a placed bomb would touch. False: that body is gone.</summary>
        static bool BombSpot(BombCast c, out Vector3 spot)
        {
            spot = c.target;
            SpellPlans.UnpackBomb(c.where, out bool onBody, out int bone, out ulong id, out _);
            if (!onBody) return true;
            var body = WorldNet.FindCombatant(id);
            if (body == null || body == c.caster || !body.IsAlive) return false;
            var part = SpellPlans.BombBone(body, bone);
            if (part == null) return false;
            spot = part.TransformPoint(c.target);
            return (spot - body.transform.position).magnitude < 5f;
        }

        /// <summary>Knocked down: no bomb placed or thrown, none set off.</summary>
        static void FizzleBombs(PlayerController pc)
        {
            PlacesS.RemoveAll(c => c.caster == pc);
            TossesS.RemoveAll(c => c.caster == pc);
            DetonatesS.RemoveAll(c => c.caster == pc);
        }

        /// <summary>A practice chamber is being reset: its bombs (and its player's) are dropped.</summary>
        static void ClearBombs(PlayerController pc, System.Func<Vector3, bool> inside)
        {
            if (pc != null) FizzleBombs(pc);
            foreach (var b in BombsS.ToArray())
                if (pc != null && b.owner == pc || inside(b.at)) EndBomb(b, WorldNet.BombGone);
        }

        static void EndBomb(BombState b, byte how)
        {
            if (BombsS.Remove(b)) WorldNet.ServerBombEnd(b.id, b.at, how);
        }

        // ------------------------------------------------------------------ tick

        /// <summary>Runs before the projectiles move this frame (it needs where they were).</summary>
        static void TickBombs(WorldNet net, double now)
        {
            TickBombCasts(now);
            for (int i = BombsS.Count - 1; i >= 0; i--)
            {
                if (i >= BombsS.Count) continue;
                var b = BombsS[i];
                if (!net.Bombs.TryGetValue(b.id, out var fx)) { BombsS.RemoveAt(i); continue; }
                if (fx.mode == WorldNet.BombFx.Flying ? !FlyBomb(b, fx, now) : !HoldBomb(b, fx, now)) continue;
                fx.Pose(now, out b.at, out b.rot);
                if (BombInWater(b, now)) { EndBomb(b, WorldNet.BombDoused); continue; }
                if (b.trigger < 0 && BombInFlames(b, fx, now)) SetOff(b);
                if (b.trigger >= 0 && now >= b.trigger + SpellPlans.BombFuse) ExplodeBomb(b);
            }
            HitBombs(net, now);
        }

        static void TickBombCasts(double now)
        {
            for (int i = PlacesS.Count - 1; i >= 0; i--)
            {
                var c = PlacesS[i];
                if (now < c.at) continue;
                PlacesS.RemoveAt(i);
                if (c.caster != null && c.caster.IsAlive) PlaceBomb(c);
            }
            for (int i = TossesS.Count - 1; i >= 0; i--)
            {
                var c = TossesS[i];
                if (c.caster == null || !c.caster.IsAlive) { TossesS.RemoveAt(i); continue; }
                if (now < c.at + 0.3) continue;
                // the owner never said where (lag): they go where they're facing
                TossesS.RemoveAt(i);
                TossBombs(c.caster, c.caster.transform.position + ViewDir(c.caster) * 60f);
            }
            for (int i = DetonatesS.Count - 1; i >= 0; i--)
            {
                var c = DetonatesS[i];
                if (now < c.at) continue;
                DetonatesS.RemoveAt(i);
                if (c.caster == null || !c.caster.IsAlive) continue;
                foreach (var b in BombsS) if (b.owner == c.caster) SetOff(b);
            }
        }

        static void PlaceBomb(BombCast c)
        {
            if (!BombSpot(c, out var spot)) return;
            SpellPlans.UnpackBomb(c.where, out bool onBody, out int bone, out ulong body, out var n);
            ulong me = c.caster.NetworkObjectId;
            int id;
            if (onBody)
            {
                var part = SpellPlans.BombBone(WorldNet.FindCombatant(body), bone);
                var rot = SpellPlans.BombRotation(part.rotation * n, c.yaw);
                id = WorldNet.ServerBombOnBody(0, me, body, bone, c.target, Quaternion.Inverse(part.rotation) * rot);
            }
            else
            {
                Vector3 at = spot + n * (SpellPlans.BombThick * 0.5f);
                if (!Supported(at, n)) return; // it broke away meanwhile
                id = WorldNet.ServerBombStick(0, me, at, SpellPlans.BombRotation(n, c.yaw));
            }
            AddBomb(id, c.caster, spot);
        }

        static void AddBomb(int id, PlayerController owner, Vector3 at)
        {
            if (id != 0) BombsS.Add(new BombState { id = id, owner = owner, thrower = owner.NetworkObjectId, at = at, lastPos = at });
        }

        /// <summary>The caster's owner throws: at <paramref name="point"/>, where their cursor is.</summary>
        public static void ReleaseToss(PlayerController caster, Vector3 point)
        {
            var c = TossesS.Find(x => x.caster == caster);
            if (c == null || WorldNet.Now < c.at - 0.15 || float.IsNaN(point.x + point.y + point.z)) return;
            TossesS.Remove(c);
            TossBombs(caster, point);
        }

        static void TossBombs(PlayerController me, Vector3 point)
        {
            Vector3 chest = me.transform.position + Vector3.up * 0.45f;
            Vector3 aim = point - chest;
            bool close = aim.magnitude < 0.8f;
            aim = close ? ViewDir(me) : aim.normalized;
            Vector3 from = chest + aim * 0.55f;
            if (Physics.Linecast(chest, from, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) from = chest;
            Vector3 dir = close ? aim : SpellPlans.TossDir(from, point);
            for (int k = 0; k < SpellPlans.TossCount; k++)
            {
                Vector2 off = Random.insideUnitCircle * SpellPlans.TossSpread;
                Vector3 d = Quaternion.LookRotation(dir) * Quaternion.Euler(off.y, off.x, 0f) * Vector3.forward;
                float speed = SpellPlans.TossSpeed * (1f + Random.Range(-SpellPlans.TossSpeedVary, SpellPlans.TossSpeedVary));
                AddBomb(WorldNet.ServerBombFly(0, me.NetworkObjectId, from, d * speed), me, from);
            }
        }

        // ------------------------------------------------------------------ flying, stuck

        /// <summary>One frame of a flying bomb. False: it's gone.</summary>
        static bool FlyBomb(BombState b, WorldNet.BombFx fx, double now)
        {
            fx.Pose(now, out var pos, out _);
            const float R = SpellPlans.BombFlyRadius;
            if (pos.y < Practice.KillYAt(pos) && !WorldNet.InsideWorld(pos) || now - fx.t > SpellPlans.BombMaxFlight) { EndBomb(b, WorldNet.BombGone); return false; }
            if (WorldNet.PassPortal(b.lastPos, pos, R + 0.02f, R * 0.5f, out var pa, out var pb))
            {
                Vector3 np = SpellPlans.ThroughPoint(pos, pa.center, pa.Rotation, pb.center, pb.Rotation, true);
                WorldNet.ServerBombFly(b.id, fx.owner, np, SpellPlans.ThroughDir(fx.Velocity(now), pa.Rotation, pb.Rotation));
                b.lastPos = np;
                return true;
            }
            if (BarrierBlocks(b.thrower, pos, R)) { b.at = pos; EndBomb(b, WorldNet.BombVaporized); return false; }
            Vector3 step = pos - b.lastPos;
            float dist = step.magnitude;
            if (dist > 1e-4f && SweepBall(b.thrower, b.lastPos, step / dist, dist, R, out var hit, out var victim))
            {
                Vector3 n = hit.normal.sqrMagnitude > 0.01f ? hit.normal : -step / dist;
                if (victim == null)
                {
                    WorldNet.ServerBombStick(b.id, fx.owner, hit.point + n * (SpellPlans.BombThick * 0.5f), SpellPlans.BombRotation(n, fx.id * 53f));
                    return true;
                }
                var hb = hit.collider.GetComponent<Hitbox>();
                int bone = hb != null && hb.Start != null ? hb.SegmentIndex : SpellPlans.BombWholeBody;
                var part = SpellPlans.BombBone(victim, bone);
                if (part == null) { b.lastPos = pos; return true; }
                var rot = SpellPlans.BombRotation(n, fx.id * 53f);
                WorldNet.ServerBombOnBody(b.id, fx.owner, victim.NetworkObjectId, bone, part.InverseTransformPoint(hit.point), Quaternion.Inverse(part.rotation) * rot);
                b.hostDied = false;
                return true;
            }
            b.lastPos = pos;
            return true;
        }

        /// <summary>A stuck bomb: still held up? (False: it's about to fall.)</summary>
        static bool HoldBomb(BombState b, WorldNet.BombFx fx, double now)
        {
            if (fx.mode == WorldNet.BombFx.OnWorld)
            {
                if (now < b.nextCheck) return true;
                b.nextCheck = now + 0.12;
                if (Supported(fx.pos, fx.rot * Vector3.up)) return true;
                DropBomb(b, fx);
                return false;
            }
            var body = WorldNet.FindCombatant(fx.body);
            bool gone = body == null || !body.IsSpawned || SpellPlans.BombBone(body, fx.bone) == null;
            if (!gone && body.Dead.Value) b.hostDied = true;
            else if (!gone && b.hostDied)
            {
                // alive again: raised where it lay, it's still on them; back at a spawn, it stays behind
                b.hostDied = false;
                gone = !body.IsUndead;
            }
            if (!gone) return true;
            DropBomb(b, fx);
            return false;
        }

        static void DropBomb(BombState b, WorldNet.BombFx fx)
        {
            b.thrower = 0; // (sticks to whoever it lands on)
            b.lastPos = b.at;
            WorldNet.ServerBombFly(b.id, fx.owner, b.at, Vector3.zero);
        }

        /// <summary>Is there still something behind a bomb (its middle at <paramref name="at"/>, lying on a surface with normal <paramref name="n"/>)?</summary>
        static bool Supported(Vector3 at, Vector3 n)
        {
            var w = VoxelWorld.Current;
            if (w != null && w.IsSolid(w.CellOf(at - n * (SpellPlans.BombThick * 0.5f + 0.08f)))) return true;
            return Physics.Raycast(at + n * 0.1f, -n, 0.1f + SpellPlans.BombThick * 0.5f + 0.15f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        // ------------------------------------------------------------------ water

        static bool BombInWater(BombState b, double now)
        {
            if (InWater(b.at, SpellPlans.BombFlyRadius)) return true;
            foreach (var s in IceSpikesS)
                if (IceRising(s, now) && SpellPlans.IceDistance(s.b, s.line.dir, s.height, b.at) <= 0.2f) return true;
            return false;
        }

        /// <summary>Icy Breath: the bombs in it (not behind something) are put out.</summary>
        static void FreezeBombs(Vector3 origin, Vector3 dir)
        {
            foreach (var b in BombsS.ToArray())
            {
                if (!SpellPlans.InCone(origin, dir, SpellPlans.IceBreathReach, SpellPlans.IceBreathAngle, b.at, SpellPlans.BombFlyRadius)) continue;
                if (!WallBetween(origin, b.at)) EndBomb(b, WorldNet.BombDoused);
            }
        }

        static bool WallBetween(Vector3 from, Vector3 to) =>
            Physics.Linecast(from, to, out var hit, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) && (hit.point - to).magnitude > SpellPlans.BombSize;

        // ------------------------------------------------------------------ flames

        /// <summary>In a risen hellfire wall, or stuck to someone who's burning. (The Flame Thrower: see <see cref="FlamesSetOff"/>.)</summary>
        static bool BombInFlames(BombState b, WorldNet.BombFx fx, double now)
        {
            if (fx.mode == WorldNet.BombFx.OnBody)
            {
                var body = WorldNet.FindCombatant(fx.body);
                if (body != null && body.IsAlive && body.BurnStacks.Value > 0) return true;
            }
            foreach (var h in HellfiresS)
                if (h.riseAt >= 0 && InWall(h, b.at, Mathf.Clamp01((float)(now - h.riseAt) / SpellPlans.HellfireRise), SpellPlans.BombSize * 0.5f)) return true;
            return false;
        }

        /// <summary>The Flame Thrower: every bomb in its flames (not behind something) is set off, the caster's own too.</summary>
        static void FlamesSetOff(Vector3 origin, Vector3 dir)
        {
            foreach (var b in BombsS)
                if (b.trigger < 0 && SpellPlans.InCone(origin, dir, SpellPlans.FlameReach, SpellPlans.FlameAngle, b.at, SpellPlans.BombFlyRadius) && !WallBetween(origin, b.at)) SetOff(b);
        }

        // ------------------------------------------------------------------ set off

        static void SetOff(BombState b)
        {
            if (b.trigger >= 0) return;
            b.trigger = WorldNet.Now;
            WorldNet.ServerBombTrigger(b.id);
        }

        /// <summary>A flame explosion at <paramref name="point"/>: every bomb it reaches is set off.</summary>
        static void FlameSetsOff(Vector3 point, float radius)
        {
            foreach (var b in BombsS) if ((b.at - point).magnitude <= radius + SpellPlans.BombSize * 0.5f) SetOff(b);
        }

        /// <summary>Something of radius <paramref name="r"/> flies from a to b: every bomb it touches on the way is set off.</summary>
        static void ShotSetsOff(Vector3 a, Vector3 b, float r, bool throughWalls = false)
        {
            foreach (var s in BombsS)
            {
                if (s.trigger >= 0 || !SpellPlans.BombTouched(s.at, s.rot, a, b, r)) continue;
                if (throughWalls || !WallBetween(a, s.at)) SetOff(s);
            }
        }

        /// <summary>Every projectile in flight, against every bomb, over the stretch it covers this frame.</summary>
        static void HitBombs(WorldNet net, double now)
        {
            if (!BombsS.Exists(b => b.trigger < 0)) return;
            float dt = Mathf.Max(Time.deltaTime, 1f / 120f);
            foreach (var r in Rocks)
                if (r.launched && net.Rocks.TryGetValue(r.id, out var fx)) ShotSetsOff(r.lastPos, WorldNet.RockPosition(fx, now), r.radius);
            foreach (var f in FireballsS)
                if (f.launched && net.Fireballs.TryGetValue(f.id, out var fx)) ShotSetsOff(f.lastPos, WorldNet.FireballPosition(fx, now), SpellPlans.FireballRadius);
            foreach (var v in Volleys)
            {
                if (!v.launched || !net.Missiles.TryGetValue(v.id, out var mfx)) continue;
                foreach (var m in mfx.m) if (m.alive) ShotSetsOff(m.prev, m.pos, SpellPlans.MissileRadius);
            }
            foreach (var s in PelletsS)
            {
                if (!net.Pellets.TryGetValue(s.id, out var fx)) continue;
                for (int k = 0; k < fx.bits.Length; k++)
                    if (fx.bits[k].Alive) ShotSetsOff(s.lastPos[k], WorldNet.PelletPosition(fx.bits[k], now), SpellPlans.PelletRadius);
            }
            foreach (var s in StarsS)
            {
                if (!net.Stars.TryGetValue(s.id, out var fx)) continue;
                for (int k = 0; k < fx.stars.Length; k++)
                    if (fx.stars[k].launched && fx.stars[k].Alive) ShotSetsOff(s.lastPos[k], WorldNet.StarPosition(fx, k, now), SpellPlans.StarRadius);
            }

            foreach (var f in FusesS)
                if (f.launched) ShotSetsOff(f.origin + f.dir * f.swept, f.origin + f.dir * Mathf.Min(SpellPlans.FuseSpeed * (float)(now - f.launchTime), SpellPlans.FuseRange), SpellPlans.FuseRadius1, true);
            foreach (var f in ForcesS) // Repulsion's ball (Attraction's is water)
                if (f.out_ && !f.water) ShotSetsOff(f.pos - f.vel * dt, f.pos, SpellPlans.ForceRadius(false, (float)(now - f.start) - SpellPlans.OrbRaise));
            foreach (var h in HooksS)
                if (h.attachedAt < 0 && now >= h.start)
                    ShotSetsOff(h.lastTip, h.origin + h.dir * Mathf.Min(SpellPlans.HookSpeed * (float)(now - h.start), SpellPlans.HookRange), 0.12f);
            foreach (var w in CutsS) // Severance's flying cut
            {
                if (now < w.start) continue;
                float front = Mathf.Min(SpellPlans.SeveranceSpeed * (float)(now - w.start), SpellPlans.SeveranceRange);
                float back = Mathf.Max(0f, front - SpellPlans.SeveranceSpeed * dt);
                foreach (var b in BombsS)
                    if (b.trigger < 0 && SpellPlans.WaveTouches(w.origin, w.rot, back, front, b.at, SpellPlans.BombSize * 0.5f) && !WallBetween(w.origin, b.at)) SetOff(b);
            }
        }

        /// <summary>A gust turns the bombs still in the air, like any other projectile.</summary>
        static void RedirectBombs(GustState g, WorldNet net, double now, System.Func<Vector3, float, bool> inside)
        {
            foreach (var b in BombsS)
            {
                if (b.lastGust == g.id || !net.Bombs.TryGetValue(b.id, out var fx) || fx.mode != WorldNet.BombFx.Flying) continue;
                fx.Pose(now, out var pos, out _);
                if (!inside(pos, SpellPlans.BombFlyRadius)) continue;
                b.lastGust = g.id;
                b.thrower = g.caster.NetworkObjectId;
                b.lastPos = pos;
                WorldNet.ServerBombFly(b.id, fx.owner, pos, g.dir * SpellPlans.TossSpeed + Vector3.up * 2f);
            }
        }

        /// <summary>The Hierophant's wall sends thrown bombs back.</summary>
        static void ReflectBombs(HierophantState h, ulong me, WorldNet net, double now)
        {
            foreach (var b in BombsS)
            {
                if (b.thrower == me || !net.Bombs.TryGetValue(b.id, out var fx) || fx.mode != WorldNet.BombFx.Flying) continue;
                fx.Pose(now, out var pos, out _);
                if (!SpellPlans.HierophantTouch(b.lastPos, pos, SpellPlans.BombFlyRadius, h.center, h.rot, out var at, out var n)) continue;
                Vector3 v = fx.Velocity(now);
                if (Vector3.Dot(v, n) >= 0f) continue;
                b.thrower = me;
                b.lastPos = at + n * 0.05f;
                WorldNet.ServerBombFly(b.id, fx.owner, b.lastPos, SpellPlans.Reflect(v, n));
                WorldNet.ServerHierophantReflect(h.id, at);
            }
        }

        // ------------------------------------------------------------------ the blast

        static void ExplodeBomb(BombState b)
        {
            Vector3 p = b.at;
            EndBomb(b, WorldNet.BombExploded);
            FlameSetsOff(p, SpellPlans.BombBlast);
            foreach (var c in Combatant.Registry.ToArray())
            {
                // (its owner too; not their Undead, as with everything else of theirs)
                if (c == null || !c.IsAlive || Shielded(c) || Folded(c) || Combatant.Allies(b.owner, c) || !Touching(c, p, SpellPlans.BombScorch)) continue;
                c.ServerCredit(b.owner);
                Burn(c, b.owner, p);
                if (!Touching(c, p, SpellPlans.BombBlast)) continue;
                Vector3 away = c.transform.position - p;
                away = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.up;
                Hit(c, b.owner, SpellPlans.BombDamage, p, away * SpellPlans.BombKnock + Vector3.up * SpellPlans.BombKnockUp, SpellPlans.BombRagdoll, from: p);
            }
            Scorch(p, SpellPlans.R4(SpellPlans.BombBlast), SpellPlans.R4(SpellPlans.BombScorch), 10f);
        }
    }
}
