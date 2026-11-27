using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for the Necromancy cards.
    ///  - Undead Servitude: crouched over a corpse within 2 m for 1 s (sickly green symbols from the hands and the body).
    ///    Knocked down meanwhile: nothing. Then the body gets up as one of the Undead, on the caster's side: green, 30
    ///    health, 5 mana at most, only Infectious Bite. The Undead and their master can't hurt or throw each other.
    ///  - Infectious Bite: crouched 0.2 s, then a head-first lunge (ragdolled) at the aim. The first enemy it meets takes 15
    ///    and the biter stays limp until it lands; until then it bites, even if something else knocks the biter about.
    ///    Bitten to death: the body turns green and gets up as one of the Undead 1 s later, on the biter's master's side (an Undead just dies).
    ///  - Summon Horde: two small mindless Undead (15 health) climb out of the ground either side of the caster (no more than
    ///    six at once: with six up, it can't be cast), shamble
    ///    at the nearest enemy and bite every 3 s.
    ///  - Undying (passive): killed by anything but a fall or lava, its owner gets up 1 s later as one of the Undead, on
    ///    their own side. They still count as standing meanwhile. The Undead don't have it.
    ///  - Skinwalker: down on one knee by a corpse within 2 m for 2 s (grey symbols from both; knocked down meanwhile: nothing). Then
    ///    the caster wears its skin (colour, face, name) and has its moves, Skinwalker itself staying in its slot; full health,
    ///    10 mana, and the corpse is gone. Until the next round, or until it's held 1 s away from any corpse: the skin's shed.
    /// </summary>
    public static partial class SpellServer
    {
        class ReviveState { public PlayerController caster; public Combatant corpse; public double at; }
        class BiteState { public Combatant biter; public double from, until; }
        class InfectState { public Combatant victim; public ulong master; public double at; }
        class UndyingState { public PlayerController who; public double at; }
        class SkinCast { public PlayerController caster; public Combatant corpse; public int slot; public double at; }

        static readonly List<ReviveState> RevivesS = new();
        static readonly List<BiteState> BitesS = new();
        static readonly List<InfectState> InfectsS = new();
        static readonly List<UndyingState> UndyingsS = new();
        static readonly List<SkinCast> SkinsS = new();

        static void ResetNecro()
        {
            UndyingsS.Clear();
            SkinsS.Clear();
            RevivesS.Clear();
            BitesS.Clear();
            InfectsS.Clear();
        }

        /// <summary>Where a dead body lies (its ragdoll's hips if there is one here, else where it fell).</summary>
        public static Vector3 CorpsePoint(Combatant c)
        {
            if (c == null) return Vector3.zero;
            if (c.LastRagdoll != null)
            {
                var pelvis = RagdollFactory.Pelvis(c.LastRagdoll);
                if (pelvis != null) return pelvis.position;
            }
            return c.transform.position;
        }

        /// <summary>The ground under a corpse (or a little under where it is, in the air).</summary>
        static Vector3 FeetUnder(Vector3 p)
        {
            if (Physics.Raycast(p + Vector3.up * 0.6f, Vector3.down, out var hit, 30f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                return hit.point;
            return p + Vector3.down * 0.4f;
        }

        static bool CastNecro(PlayerController caster, CardId card, Vector3 target, float yaw, ulong targetId, VoxelWorld world, double now)
        {
            switch (card)
            {
                case CardId.UndeadServitude:
                {
                    var c = WorldNet.FindCombatant(targetId);
                    if (c == null || !c.IsSpawned || !c.Dead.Value || c == caster) return false;
                    if (!SpellPlans.CorpseInReach(CorpsePoint(c), caster.transform.position, 1.5f)) return false;
                    if (RevivesS.Exists(r => r.corpse == c) || SkinsS.Exists(s => s.corpse == c) || c.LastRagdoll == null) return false;
                    RevivesS.Add(new ReviveState { caster = caster, corpse = c, at = now + SpellPlans.ReviveTime });
                    WorldNet.ServerNecroFx(0, caster.NetworkObjectId, c.NetworkObjectId, CorpsePoint(c), now);
                    return true;
                }
                case CardId.Skinwalker:
                {
                    var c = WorldNet.FindCombatant(targetId);
                    if (c == null || !c.IsSpawned || !c.Dead.Value || c == caster || c.LastRagdoll == null || caster.IsUndead) return false;
                    if (!SpellPlans.CorpseInReach(CorpsePoint(c), caster.transform.position, 1.5f)) return false;
                    if (RevivesS.Exists(r => r.corpse == c) || SkinsS.Exists(s => s.corpse == c)) return false;
                    SkinsS.RemoveAll(s => s.caster == caster);
                    SkinsS.Add(new SkinCast { caster = caster, corpse = c, slot = -1, at = now + SpellPlans.SkinTime });
                    if (c is TestDummy d) d.ServerHoldCorpse();
                    WorldNet.ServerNecroFx(4, caster.NetworkObjectId, c.NetworkObjectId, CorpsePoint(c), now);
                    return true;
                }
                case CardId.InfectiousBite:
                    BitesS.RemoveAll(b => b.biter == caster);
                    BitesS.Add(new BiteState { biter = caster, from = now + SpellPlans.BiteCrouch, until = now + SpellPlans.BiteCrouch + SpellPlans.BiteMaxTime });
                    return true;
                case CardId.SummonHorde:
                    return CastHorde(caster, world, now);
            }
            return false;
        }

        static void TickNecro(double now)
        {
            TickRevives(now);
            TickBites(now);
            TickInfects(now);
            TickUndyings(now);
            TickSkins(now);
        }

        /// <summary>Knocked down crouching: the corpse stays dead; the lunge that hadn't started never does.</summary>
        static bool FizzleNecro(PlayerController pc, double now)
        {
            bool shown = RevivesS.RemoveAll(r => r.caster == pc) > 0;
            shown |= SkinsS.RemoveAll(s => s.caster == pc) > 0;
            BitesS.RemoveAll(b => b.biter == pc && now < b.from);
            return shown;
        }

        // ------------------------------------------------------------------ Undead Servitude

        static void TickRevives(double now)
        {
            for (int i = RevivesS.Count - 1; i >= 0; i--)
            {
                var r = RevivesS[i];
                if (now < r.at) continue;
                RevivesS.RemoveAt(i);
                if (r.caster == null || !r.caster.IsAlive || r.corpse == null || !r.corpse.IsSpawned || !r.corpse.Dead.Value) continue;
                RaiseUndead(r.corpse, r.caster.Team, r.caster.ViewYaw, r.caster);
            }
        }

        /// <summary>
        /// The body gets up where it lies, as one of the Undead on <paramref name="master"/>'s side; if it lies somewhere that
        /// kills (lava, a kill barrier: the map killed it), beside <paramref name="beside"/> instead.
        /// </summary>
        static void RaiseUndead(Combatant c, ulong master, float yaw, Combatant beside = null)
        {
            Vector3 feet = FeetUnder(CorpsePoint(c));
            if (Deadly(feet) && beside != null) feet = FeetUnder(beside.transform.position + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * 1.2f);
            if (Deadly(feet) && beside != null) feet = beside.FeetPosition;
            if (c is PlayerController p) p.ServerReviveUndead(feet, yaw, master);
            else if (c is TestDummy d) d.ServerMakeUndead(feet, yaw, master, false, WorldNet.Now);
        }

        // ------------------------------------------------------------------ Infectious Bite

        /// <summary>Server: a body (a player's own cast, or a zombie lunging) is biting from now until it's back on its feet.</summary>
        public static void StartBite(Combatant biter)
        {
            if (biter == null) return;
            double now = WorldNet.Now;
            BitesS.RemoveAll(b => b.biter == biter);
            BitesS.Add(new BiteState { biter = biter, from = now, until = now + SpellPlans.BiteMaxTime });
        }

        static bool Biting(Combatant c) => c switch
        {
            PlayerController p => p.IsRagdolled,
            TestDummy d => d.Lunging,
            _ => false,
        };

        static void TickBites(double now)
        {
            for (int i = BitesS.Count - 1; i >= 0; i--)
            {
                var b = BitesS[i];
                var me = b.biter;
                if (me == null || !me.IsAlive || now > b.until) { BitesS.RemoveAt(i); continue; }
                if (now < b.from) continue;
                // back on its feet without biting anyone: over (after a moment for the owner's lunge to show up here)
                if (now > b.from + 0.25 && !Biting(me)) { BitesS.RemoveAt(i); continue; }
                Vector3 v = me.ViewVelocity;
                Vector3 head = me.transform.position + (v.sqrMagnitude > 1f ? v.normalized * 0.4f : Vector3.zero);
                Combatant victim = null;
                float best = SpellPlans.BiteReach * me.transform.localScale.y + 0.35f;
                foreach (var o in Combatant.Registry)
                {
                    if (o == null || o == me || !o.IsAlive || Combatant.Allies(me, o) || Combatant.RaisedAgainst(me, o) || Folded(o)) continue;
                    Vector3 feet = o.FeetPosition, ab = Vector3.up * 1.9f * o.transform.localScale.y;
                    float u = Mathf.Clamp01(Vector3.Dot(head - feet, ab) / ab.sqrMagnitude);
                    float d = (head - (feet + ab * u)).magnitude;
                    if (d < best) { best = d; victim = o; }
                }
                if (victim == null) continue;
                BitesS.RemoveAt(i);
                // the kill is the necromancer's (a player's own bite: theirs)
                var credit = me as PlayerController ?? WorldNet.FindCaster(me.Team);
                Vector3 push = v.sqrMagnitude > 0.01f ? new Vector3(v.x, 0f, v.z).normalized : me.transform.forward;
                bool wasUndead = victim.IsUndead;
                DamageFrom = me.FeetPosition; // (a zombie's: from where it is, not where its master is)
                Hit(victim, credit, SpellPlans.BiteDamage, victim.transform.position + Vector3.up * 0.4f, push * 3f, 0f, from: head - push * 0.5f, striker: me);
                DamageFrom = null;
                WorldNet.ServerNecroFx(3, me.NetworkObjectId, victim.NetworkObjectId, victim.transform.position + Vector3.up * 0.5f, now);
                if (!victim.IsAlive && victim.IsSpawned && !wasUndead && !UndyingPending(victim)) // (Undying: they get up on their own side instead; an Undead just dies)
                {
                    // bitten to death: it turns green and gets up on the biter's side
                    var st = victim.Status.Value;
                    st.InfectFrom = now;
                    victim.Status.Value = st;
                    InfectsS.RemoveAll(x => x.victim == victim);
                    InfectsS.Add(new InfectState { victim = victim, master = me.Team, at = now + SpellPlans.InfectTime });
                }
            }
        }

        static void TickInfects(double now)
        {
            for (int i = InfectsS.Count - 1; i >= 0; i--)
            {
                var f = InfectsS[i];
                if (now < f.at) continue;
                InfectsS.RemoveAt(i);
                if (f.victim == null || !f.victim.IsSpawned || !f.victim.Dead.Value) continue;
                var st = f.victim.Status.Value;
                st.InfectFrom = 0;
                f.victim.Status.Value = st;
                var master = WorldNet.FindCombatant(f.master);
                RaiseUndead(f.victim, f.master, master != null ? master.ViewYaw : 0f);
            }
        }

        /// <summary>A body died: whatever was being done to raise it (or by it) stops.</summary>
        static void OnKilled(Combatant victim, ulong attacker, DeathCause cause)
        {
            BitesS.RemoveAll(b => b.biter == victim);
            RevivesS.RemoveAll(r => r.caster == victim);
            SkinsS.RemoveAll(s => s.caster == victim);
            JusticeDone(victim, attacker);
            ForgetDeath(victim);
            ForgetStars(victim);
            // Undying: up again in a moment (not out of a fall or lava; the Undead don't have it)
            if (cause != DeathCause.Spell || victim is not PlayerController pc || pc.IsUndead || Cards.CountOf(pc.Hand, CardId.Undying) == 0) return;
            double now = WorldNet.Now;
            UndyingsS.RemoveAll(u => u.who == pc);
            UndyingsS.Add(new UndyingState { who = pc, at = now + SpellPlans.UndyingTime });
            var st = pc.Status.Value;
            st.InfectFrom = now; // the body turns green as it lies there
            pc.Status.Value = st;
        }

        // ------------------------------------------------------------------ Undying

        /// <summary>Dead, but about to get up again (Undying): still counts as standing.</summary>
        public static bool UndyingPending(Combatant c)
        {
            foreach (var u in UndyingsS) if (u.who == c) return true;
            return false;
        }

        static void TickUndyings(double now)
        {
            for (int i = UndyingsS.Count - 1; i >= 0; i--)
            {
                var u = UndyingsS[i];
                var me = u.who;
                bool gone = me == null || !me.IsSpawned || !me.Dead.Value;
                if (!gone && now < u.at) continue;
                UndyingsS.RemoveAt(i);
                if (gone) continue;
                var st = me.Status.Value;
                st.InfectFrom = 0;
                me.Status.Value = st;
                // only while there's a round to get up into (or in practice)
                if (!me.InPractice && MatchManager.Running && !MatchManager.Fighting && MatchManager.Current.phase != MatchPhase.RoundOver) continue;
                // where the body lies, or (it went over an edge, or into lava) where it was killed
                Vector3 feet = FeetUnder(CorpsePoint(me));
                if (Deadly(feet)) feet = FeetUnder(me.transform.position);
                if (Deadly(feet)) continue;
                me.ServerReviveUndead(feet, me.ViewYaw, me.NetworkObjectId);
            }
        }

        /// <summary>Standing here would kill at once (lava, a kill barrier, under the arena).</summary>
        static bool Deadly(Vector3 feet)
        {
            Vector3 hips = feet + Vector3.up * 0.95f;
            if (WorldNet.InsideWorld(hips)) return false;
            return hips.y < Practice.KillYAt(hips) || KillVolume.AnyContains(hips) || KillVolume.AnyContains(feet + Vector3.up * 0.2f);
        }

        // ------------------------------------------------------------------ Skinwalker

        /// <summary>The cast went through, from this slot (Skinwalker stays in it).</summary>
        public static void SkinwalkBegan(PlayerController caster, int slot)
        {
            var s = SkinsS.Find(x => x.caster == caster);
            if (s != null) s.slot = slot;
        }

        static void TickSkins(double now)
        {
            for (int i = SkinsS.Count - 1; i >= 0; i--)
            {
                var s = SkinsS[i];
                var me = s.caster;
                if (me == null || !me.IsAlive) { SkinsS.RemoveAt(i); continue; }
                if (s.corpse == null || !s.corpse.IsSpawned || !s.corpse.Dead.Value || s.corpse.LastRagdoll == null || me.Status.Value.Taken(now))
                {
                    // got up, or someone else had it: there's no skin to take
                    SkinsS.RemoveAt(i);
                    me.ServerEndCast();
                    WorldNet.ServerFizzle(me.NetworkObjectId);
                    continue;
                }
                if (now < s.at) continue;
                SkinsS.RemoveAt(i);
                if (s.slot >= 0) TakeSkin(me, s.corpse, s.slot, now);
            }
        }

        static void TakeSkin(PlayerController me, Combatant corpse, int slot, double now)
        {
            var skin = new SkinState { On = true, Kind = corpse.IsUndead ? SkinState.Undead : corpse is TestDummy ? SkinState.Dummy : SkinState.Player, Color = 255 };
            EquippedCards hand = SpellPlans.BaseKit;
            if (corpse is PlayerController p)
            {
                if (p.Skin.Value.On) skin = p.Skin.Value; // (wearing someone's skin already: that one)
                else
                {
                    skin.Color = p.BodyColor;
                    skin.Face = p.NetworkObjectId;
                    skin.SetName(p.DisplayName);
                }
                if (p.IsUndead) skin.Kind = SkinState.Undead;
                hand = p.Hand;
            }
            else skin.SetName(corpse.FeedName);
            if (corpse.IsUndead)
            {
                hand = default;
                hand[0] = CardId.InfectiousBite;
            }
            skin.Hand = SpellPlans.SkinHand(hand, slot, me.Hand[slot]);
            // what their old moves still have running ends (they may have no way to end it any more)
            CancelCard(me, CardId.Death);
            CancelCard(me, CardId.GrappleHook);
            CancelCard(me, CardId.TheHangedMan);
            CancelLovers(me);
            me.ServerTakeSkin(skin);
            WorldNet.ServerNecroFx(5, me.NetworkObjectId, corpse.NetworkObjectId, CorpsePoint(corpse), now);
        }

        /// <summary>The skin comes off: their old look and moves.</summary>
        public static void ShedSkin(PlayerController me)
        {
            // what the skin's moves still have running ends (they may have no way to end it any more)
            CancelCard(me, CardId.Death);
            CancelCard(me, CardId.GrappleHook);
            CancelCard(me, CardId.TheHangedMan);
            CancelLovers(me);
            me.ServerShedSkin();
            WorldNet.ServerNecroFx(5, me.NetworkObjectId, 0, me.transform.position, WorldNet.Now);
        }

        // ------------------------------------------------------------------ Summon Horde

        static readonly List<Vector3> _hordeSpots = new();

        /// <summary>How many of this necromancer's horde zombies are up (at most <see cref="SpellPlans.HordeMax"/>).</summary>
        public static int HordeCount(Combatant caster)
        {
            int n = 0;
            if (caster == null) return 0;
            foreach (var c in Combatant.Registry)
                if (c is TestDummy d && d.IsAlive && d.Status.Value.Horde && d.Status.Value.Master == caster.NetworkObjectId) n++;
            return n;
        }

        static bool CastHorde(PlayerController caster, VoxelWorld world, double now)
        {
            // six up already: no more (with five, only one more comes)
            int room = SpellPlans.HordeMax - HordeCount(caster);
            if (room <= 0) return false;
            if (!PlanHorde(world, caster.FeetPosition, caster.ViewYaw, _hordeSpots)) return false;
            float yaw = caster.ViewYaw;
            foreach (var spot in _hordeSpots)
            {
                if (room-- <= 0) break;
                var d = TestDummy.ServerSpawnZombie(spot, yaw, caster.NetworkObjectId, caster.PracticeSlot.Value, now + SpellPlans.HordeCrouch * 0.5);
                if (d == null) return false;
                WorldNet.ServerNecroFx(1, caster.NetworkObjectId, d.NetworkObjectId, spot, now);
            }
            return true;
        }

        /// <summary>
        /// The two spots either side of the caster: ground deep enough for a zombie to climb out of (so none of it ever
        /// shows below a thin floor). Tried a little closer in when the first spot won't do. False without two.
        /// </summary>
        public static bool PlanHorde(VoxelWorld w, Vector3 feet, float yaw, List<Vector3> spots)
        {
            spots.Clear();
            if (w == null) return false;
            Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            int depth = Mathf.CeilToInt(1.95f * SpellPlans.HordeScale / VoxelWorld.Size) + 1;
            foreach (float side in new[] { 1f, -1f })
            {
                foreach (float reach in new[] { SpellPlans.HordeSide, SpellPlans.HordeSide * 0.65f, SpellPlans.HordeSide * 1.35f })
                {
                    Vector3 p = feet + right * (side * reach);
                    var c = w.CellOf(p + Vector3.down * 0.1f);
                    int top = w.TopSolid(c.x, c.z, c.y - 4, c.y + 3);
                    if (top == int.MinValue) continue;
                    bool deep = true;
                    for (int y = top - depth; y <= top && deep; y++) deep = w.IsSolid(c.x, y, c.z);
                    if (!deep) continue;
                    // and room to stand up on it
                    if (w.IsSolid(c.x, top + 1, c.z) || w.IsSolid(c.x, top + 4, c.z)) continue;
                    spots.Add(new Vector3(p.x, w.Origin.y + (top + 1) * VoxelWorld.Size, p.z));
                    break;
                }
            }
            return spots.Count == 2;
        }
    }
}
