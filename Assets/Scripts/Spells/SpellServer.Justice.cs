using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for Justice (and what damage dealt does for The Moon).
    ///  - Down on one knee by a corpse within 2 m for 2 s (knocked down meanwhile: nothing). Its killer is then outlined
    ///    through walls on the caster's screen, until one of the two dies. Each time the caster hurts them: 0.5 more mana a
    ///    second for 3 s (not more at once, it only starts over). Kills them: 10 mana over 1 s.
    ///  - The caster's own kill, or nobody's kill: 10 mana over 1 s at once. The killer already dead: the mana comes back.
    ///  - Each player investigates a body once (that death's ragdoll): not again, even after it's been done.
    /// </summary>
    public static partial class SpellServer
    {
        class JusticeCast { public PlayerController caster, killer; public Combatant corpse; public double at; public int cost; }

        static readonly List<JusticeCast> JusticesS = new();
        /// <summary>Who has already investigated which body (that death's ragdoll; a new death is a new body).</summary>
        static readonly HashSet<(PlayerController, GameObject)> Investigated = new();

        /// <summary>A new round: every body is new.</summary>
        static void ResetJustice() { JusticesS.Clear(); Investigated.Clear(); }

        static bool CastJustice(PlayerController caster, ulong targetId, double now)
        {
            var c = WorldNet.FindCombatant(targetId);
            if (c == null || !c.IsSpawned || !c.Dead.Value || c == caster || c.LastRagdoll == null) return false;
            if (!SpellPlans.CorpseInReach(CorpsePoint(c), caster.transform.position, 1.5f)) return false;
            if (Investigated.Contains((caster, c.LastRagdoll))) return false; // (already looked into this one)
            JusticesS.RemoveAll(j => j.caster == caster);
            JusticesS.Add(new JusticeCast { caster = caster, corpse = c, killer = c.Killer, at = now + SpellPlans.JusticeCast });
            WorldNet.ServerNecroFx(6, caster.NetworkObjectId, c.NetworkObjectId, CorpsePoint(c), now);
            return true;
        }

        /// <summary>The cast went through, for this much (it comes back if there's no killer to go after).</summary>
        public static void JusticeBegan(PlayerController caster, int cost)
        {
            var j = JusticesS.Find(x => x.caster == caster);
            if (j != null) j.cost = cost;
        }

        static void TickJustice(double now)
        {
            for (int i = JusticesS.Count - 1; i >= 0; i--)
            {
                var j = JusticesS[i];
                var me = j.caster;
                if (me == null || !me.IsAlive) { JusticesS.RemoveAt(i); continue; }
                if (j.corpse == null || !j.corpse.IsSpawned || !j.corpse.Dead.Value || j.corpse.LastRagdoll == null || me.Status.Value.Taken(now))
                {
                    // got up, or someone else had it
                    JusticesS.RemoveAt(i);
                    me.ServerEndCast();
                    WorldNet.ServerFizzle(me.NetworkObjectId);
                    continue;
                }
                if (now < j.at) continue;
                JusticesS.RemoveAt(i);
                Investigated.Add((me, j.corpse.LastRagdoll));
                if (j.killer == me || j.killer == null) Surge(me, now); // their own kill, or nobody's: the mana at once
                else if (j.killer != null && j.killer.IsAlive)
                {
                    var st = me.Status.Value;
                    st.JusticeOn = j.killer.NetworkObjectId;
                    me.Status.Value = st;
                }
                else if (j.cost > 0) me.Mana.Value = me.Mana.Value.With(now, Mathf.Min(me.Mana.Value.Limit, me.ManaNow + j.cost));
                WorldNet.ServerNecroFx(7, me.NetworkObjectId, j.corpse.NetworkObjectId, CorpsePoint(j.corpse), now);
            }
        }

        static void Surge(PlayerController pc, double now)
        {
            var m = pc.Mana.Value.With(now, pc.ManaNow);
            m.SurgeUntil = now + SpellPlans.JusticeSurgeTime;
            pc.Mana.Value = m;
        }

        /// <summary>Every hit that landed: Justice's killer hurt by its caster, The Moon filling.</summary>
        static void OnDamaged(Combatant victim, ulong attacker, float dmg)
        {
            if (dmg <= 0f) return;
            var by = PlayerController.ServerFind(attacker);
            if (by == null || by == victim || !by.IsAlive) return;
            double now = WorldNet.Now;
            if (by.Status.Value.JusticeOn != 0 && by.Status.Value.JusticeOn == victim.NetworkObjectId)
            {
                var m = by.Mana.Value.With(now, by.ManaNow);
                m.GainUntil = now + SpellPlans.JusticeGainTime;
                by.Mana.Value = m;
            }
            MoonFills(by, victim, dmg, now);
        }

        /// <summary>A body died: whoever was after it for Justice isn't any more (and has their mana, if they did it).</summary>
        static void JusticeDone(Combatant victim, ulong attacker)
        {
            JusticesS.RemoveAll(j => j.caster == victim);
            double now = WorldNet.Now;
            foreach (var pc in PlayerController.All)
            {
                if (pc == null || !pc.IsSpawned || pc.Status.Value.JusticeOn != victim.NetworkObjectId) continue;
                var st = pc.Status.Value;
                st.JusticeOn = 0;
                pc.Status.Value = st;
                if (pc.IsAlive && pc.OwnerClientId == attacker) Surge(pc, now);
            }
        }
    }
}
