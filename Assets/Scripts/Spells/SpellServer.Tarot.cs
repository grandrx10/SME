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
        }

        const byte Empress = 0, Emperor = 1;
        static readonly List<DomainState> Domains = new();

        static void ResetTarot()
        {
            foreach (var d in Domains) Release(d);
            Domains.Clear();
            Combatant.DamageScale = ScaleDamage;
        }

        /// <summary>The Fool: any card except The Fool itself.</summary>
        public static CardId RollFoolCard()
        {
            var pool = new List<CardId>();
            foreach (var c in Cards.All) if (c.id != CardId.TheFool && c.id != CardId.None) pool.Add(c.id);
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
                    caster.Mana.Value = new ManaState { Value = m.At(now), Time = now, FastUntil = now + SpellPlans.MagicianTime };
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
                    foreach (var old in Domains) if (old.caster == caster && !old.formed) return false;
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
            for (int i = Domains.Count - 1; i >= 0; i--)
            {
                var d = Domains[i];
                if (d.caster == null || !d.caster.IsAlive || now >= d.endAt) { End(d); continue; }
                if (now < d.formAt) continue;
                if (!d.formed && d.kind == Emperor)
                {
                    // one Emperor aura at a time per caster: a new one replaces the old
                    foreach (var old in Domains.ToArray())
                        if (old != d && old.kind == Emperor && old.caster == d.caster) End(old);
                    i = Domains.IndexOf(d);
                }
                d.formed = true;
                if (d.kind == Emperor) { d.center = d.caster.FeetPosition; continue; }

                // Empress: ends when its caster leaves it
                // (against its full size, so stepping about while it's still growing doesn't end it)
                if (!Inside(d, d.caster.FeetPosition, double.MaxValue, 0f)) { End(d); continue; }
                foreach (var p in Combatant.Registry)
                {
                    if (p == null || p == d.caster || !p.IsAlive) continue;
                    bool full = ManaOf(p, now) >= ManaState.Max - 0.001f;
                    if (d.trapped.Contains(p))
                    {
                        // held until their mana is full
                        if (full) { d.trapped.Remove(p); RefreshTrapped(p); }
                        continue;
                    }
                    // anyone in it (or walking in) without full mana is caught
                    if (!full && Inside(d, p.FeetPosition, now, 0.3f))
                    {
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

        /// <summary>Empress: her damage to people in her domain ×1.25. Emperor: damage from people inside his aura ×0.5.</summary>
        static float ScaleDamage(Combatant victim, ulong attacker, float dmg)
        {
            if (Domains.Count == 0) return dmg;
            var source = PlayerController.ServerFind(attacker);
            if (source == null || source == victim) return dmg;
            double now = WorldNet.Now;
            foreach (var d in Domains)
            {
                if (!d.formed) continue;
                if (d.kind == Empress && d.caster == source && Inside(d, victim.FeetPosition, now, 0.3f)) dmg *= SpellPlans.EmpressDamage;
                else if (d.kind == Emperor && d.caster == victim && Inside(d, source.FeetPosition, now, 0.3f)) dmg *= SpellPlans.EmperorDamage;
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

        /// <summary>The Magician ends early (ragdolled, trapped).</summary>
        static void DispelMagician(PlayerController p)
        {
            if (p == null) return;
            double now = WorldNet.Now;
            var m = p.Mana.Value;
            if (m.Fast(now)) p.Mana.Value = new ManaState { Value = m.At(now), Time = now };
        }
    }
}
