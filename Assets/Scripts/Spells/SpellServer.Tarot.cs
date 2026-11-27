using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>Server rules for the Tarot cards.</summary>
    public static partial class SpellServer
    {
        class DomainState
        {
            public int id; public byte kind; public PlayerController caster; public Vector3 center;
            public double formAt, endAt; public bool formed;
            public readonly HashSet<Combatant> trapped = new();
            public readonly Dictionary<Combatant, double> caughtAt = new(); // Empress: when each was caught (held a fixed time, once)
        }

        const byte Empress = 0, Emperor = 1;
        static readonly List<DomainState> Domains = new();
        /// <summary>When each caster's last domain of each kind ended (another has to wait <see cref="SpellPlans.DomainRest"/>).</summary>
        static readonly Dictionary<(PlayerController, byte), double> DomainRested = new();
        /// <summary>Where the damage being dealt right now comes from, when that isn't where whoever deals it stands (The Emperor asks).</summary>
        static Vector3? DamageFrom;

        static void ResetTarot()
        {
            foreach (var d in Domains) Release(d);
            Domains.Clear();
            DomainRested.Clear();
            DamageFrom = null;
            Combatant.DamageScale = ScaleDamage;
        }

        /// <summary>The Fool: any card except The Fool itself.</summary>
        public static CardId RollFoolCard()
        {
            var pool = new List<CardId>();
            foreach (var c in Cards.All)
                if (c.id != CardId.TheFool && c.id != CardId.None && c.costType != CostType.Passive && c.rarity != Rarity.Secret && !c.companion) pool.Add(c.id); // (a passive card does nothing on its own; a secret stays one)
            return pool[Random.Range(0, pool.Count)];
        }

        static bool CastTarot(PlayerController caster, CardId card, double now)
        {
            Combatant.DamageScale ??= ScaleDamage;
            switch (card)
            {
                case CardId.TheMagician:
                {
                    // can't be cast again while it runs (refused: no cost, no cooldown)
                    var m = caster.Mana.Value;
                    if (m.Fast(now)) return false;
                    m = m.With(now, m.At(now));
                    m.FastUntil = now + SpellPlans.MagicianTime;
                    caster.Mana.Value = m;
                    return true;
                }
                case CardId.TheHighPriestess:
                    WorldNet.ServerPriestess(caster.NetworkObjectId);
                    return true;
                case CardId.TheEmpress:
                case CardId.TheEmperor:
                {
                    byte kind = card == CardId.TheEmpress ? Empress : Emperor;
                    // still clasping hands for one already: no
                    foreach (var old in Domains) if (old.caster == caster && (!old.formed || old.kind == kind)) return false; // (nor while one of these is up...)
                    if (DomainRested.TryGetValue((caster, kind), out double rested) && now < rested + SpellPlans.DomainRest - 0.15) return false; // (...nor just after)
                    var d = new DomainState
                    {
                        kind = kind, caster = caster, center = caster.FeetPosition,
                        formAt = now + SpellPlans.DomainDelay,
                    };
                    d.endAt = d.formAt + (kind == Empress ? SpellPlans.EmpressTime : SpellPlans.EmperorTime);
                    d.id = WorldNet.ServerDomain(kind, caster.NetworkObjectId, d.center, d.formAt);
                    Domains.Add(d);
                    return true;
                }
            }
            return false;
        }

        static void TickTarot(double now)
        {
            // (a ragdoll the server didn't cause: slipping on ice, a bite's own lunge)
            foreach (var pc in PlayerController.All)
                if (pc != null && pc.IsSpawned && pc.IsRagdolled && pc.Mana.Value.Fast(now)) DispelMagician(pc);
            for (int i = Domains.Count - 1; i >= 0; i--)
            {
                var d = Domains[i];
                if (d.caster == null || !d.caster.IsAlive || now >= d.endAt) { End(d); continue; }
                if (now < d.formAt) continue;
                d.formed = true;
                if (d.kind == Emperor) { d.center = d.caster.FeetPosition; continue; }

                // Empress: ends when its caster leaves it
                // (against its full size, so stepping about while it's still growing doesn't end it)
                if (!Inside(d, d.caster.FeetPosition, double.MaxValue, 0f)) { End(d); continue; }
                foreach (var p in Combatant.Registry)
                {
                    if (p == null || p == d.caster || !p.IsAlive || Invulnerable(p)) continue;
                    if (d.trapped.Contains(p))
                    {
                        // held for a fixed time
                        if (now >= d.caughtAt[p] + SpellPlans.EmpressTrapTime) { d.trapped.Remove(p); RefreshTrapped(p); }
                        continue;
                    }
                    // anyone in it (or walking in) is caught, once per domain
                    if (!d.caughtAt.ContainsKey(p) && Inside(d, p.FeetPosition, now, 0.3f))
                    {
                        d.caughtAt[p] = now;
                        if (ParryCatch(p, d.caster, p.transform.position)) continue; // Parry: the chains never take hold
                        d.trapped.Add(p);
                        RefreshTrapped(p);
                    }
                }
            }
        }

        static void End(DomainState d)
        {
            Domains.Remove(d);
            Release(d);
            if (d.formed && d.caster != null) DomainRested[(d.caster, d.kind)] = WorldNet.Now;
            WorldNet.ServerDomainEnd(d.id);
        }

        static void Release(DomainState d)
        {
            var was = new List<Combatant>(d.trapped);
            d.trapped.Clear();
            foreach (var p in was) RefreshTrapped(p);
        }

        /// <summary>A body's mana (a test dummy sits at 5, so it stays caught for the whole domain).</summary>
        static float ManaOf(Combatant c, double now) => c switch
        {
            PlayerController p => p.Mana.Value.At(now),
            TestDummy d => d.ManaNow,
            _ => ManaState.Max,
        };

        /// <summary>Server: where the Empress domain holding a body is (for the chains on server-simulated bodies).</summary>
        public static bool TrapCenter(Combatant c, out Vector3 center)
        {
            center = default;
            if (c == null || !c.Trapped) return false;
            foreach (var d in Domains)
                if (d.id == c.TrappedBy.Value && d.kind == Empress) { center = d.center; return true; }
            return false;
        }

        /// <summary>Trapped (and chained to its caster) while any Empress domain is holding them.</summary>
        static void RefreshTrapped(Combatant p)
        {
            if (p == null) return;
            int by = 0;
            foreach (var d in Domains) if (d.trapped.Contains(p)) { by = d.id; break; }
            if (p.TrappedBy.Value != by) p.TrappedBy.Value = by;
        }

        /// <summary>Within the circle (and roughly at its height), counting how far it has grown.</summary>
        static bool Inside(DomainState d, Vector3 feet, double now, float pad)
        {
            float grow = Mathf.Clamp01((float)(now - d.formAt) / SpellPlans.DomainGrow);
            Vector3 c = d.kind == Emperor && d.caster != null ? d.caster.FeetPosition : d.center;
            Vector3 o = feet - c;
            if (o.y < -2f || o.y > 5f) return false;
            return new Vector2(o.x, o.z).magnitude <= SpellPlans.DomainRadius(d.kind) * grow + pad;
        }

        /// <summary>Inside a formed Empress circle that isn't their own: they can't hold up their arcane shield.</summary>
        public static bool InEnemyEmpress(Combatant c)
        {
            if (c == null || Domains.Count == 0) return false;
            double now = WorldNet.Now;
            foreach (var d in Domains)
                if (d.kind == Empress && d.formed && d.caster != c && Inside(d, c.FeetPosition, now, 0.3f)) return true;
            return false;
        }

        /// <summary>
        /// Every hit passes through here. Bubble Away: someone in their bubble takes nothing (the bubble does).
        /// Empress: her damage to people in her domain ×1.25. Emperor: anything from outside his circle ×0.3.
        /// </summary>
        static float ScaleDamage(Combatant victim, ulong attacker, float dmg)
        {
            if (Invulnerable(victim)) return 0f;          // mid-warp, or Arcane Swiftness
            var source = PlayerController.ServerFind(attacker);
            if (Combatant.Allies(source, victim)) return 0f; // a necromancer and their Undead
            if (source != null && source != victim && source.Status.Value.Strong(WorldNet.Now)) dmg *= SpellPlans.StrengthDamage; // Strength
            if (AbsorbInBubble(victim, dmg)) return 0f; // Bubble Away: the bubble takes it
            if (Domains.Count == 0) return dmg;
            if (source == victim) return dmg;
            double now = WorldNet.Now;
            Vector3? from = DamageFrom ?? (source != null ? source.FeetPosition : (Vector3?)null);
            foreach (var d in Domains)
            {
                if (!d.formed) continue;
                if (d.kind == Empress && source != null && d.caster == source && Inside(d, victim.FeetPosition, now, 0.3f)) dmg *= SpellPlans.EmpressDamage;
                else if (d.kind == Emperor && d.caster == victim && from.HasValue && !Inside(d, from.Value, now, 0.3f)) dmg *= SpellPlans.EmperorDamage;
            }
            return dmg;
        }

        /// <summary>Hurt or thrown while still clasping hands: the domain never opens (the mana stays spent).</summary>
        static void CancelDomainStartup(PlayerController p)
        {
            if (p == null) return;
            foreach (var d in Domains.ToArray())
                if (d.caster == p && !d.formed) End(d);
        }

        /// <summary>The Magician ends early (hurt, or ragdolled by anything at all).</summary>
        public static void DispelMagician(PlayerController p)
        {
            if (p == null || !p.IsSpawned) return;
            double now = WorldNet.Now;
            var m = p.Mana.Value;
            if (!m.Fast(now)) return;
            m = m.With(now, m.At(now));
            m.FastUntil = 0;
            p.Mana.Value = m;
        }
    }
}
