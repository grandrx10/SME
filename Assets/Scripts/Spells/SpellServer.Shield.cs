using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The arcane shield: whatever comes at someone from the side they're holding it out to hurts it instead of them.
    /// Damage past what it has left goes on to them, and with it the knockback. What only throws or grabs costs it eight
    /// times the spell's mana. Anyone close whose spell it stopped is thrown back.
    /// Not stopped: auras and gazes, map hazards and falls, judgment beams, a tornado's lift (its damage is), hexes, burns already going, The Chariot (running into them or carrying them off).
    /// Death's charge is stopped, but takes the whole shield.
    /// </summary>
    public static partial class SpellServer
    {
        static readonly List<(Combatant by, Combatant victim)> _rebukes = new();
        static readonly Dictionary<(Combatant, Combatant), double> _rebuked = new();

        static void ResetShields()
        {
            _rebukes.Clear();
            _rebuked.Clear();
        }

        /// <summary>Is <paramref name="victim"/> holding their shield out towards something coming from <paramref name="from"/> (not their own doing)?</summary>
        static bool Guards(Combatant victim, Combatant by, Vector3 from)
        {
            if (victim == null || by == victim || !victim.ShieldHeld || Shielded(victim)) return false;
            if (Combatant.Allies(by, victim) || Combatant.RaisedAgainst(by, victim)) return false; // (can't hurt them anyway)
            Vector3 chest = victim.transform.position + Vector3.up * 0.35f;
            Vector3 d = from - chest;
            if (d.sqrMagnitude < 0.01f && by != null) d = by.transform.position - victim.transform.position;
            return d.sqrMagnitude < 0.01f || Vector3.Angle(ViewDir(victim), d) <= SpellPlans.ShieldAngle;
        }

        /// <summary>Where a hit came from, if nothing said: against its knock, or else from whoever did it.</summary>
        static Vector3 SourceOf(Combatant victim, Combatant by, Vector3 point, Vector3 knock)
        {
            Vector3 flat = new(knock.x, 0f, knock.z);
            if (flat.magnitude > 0.5f) return victim.transform.position + Vector3.up * 0.35f - flat.normalized * 3f;
            return by != null ? by.transform.position : point;
        }

        /// <summary>
        /// Something that only throws or grabs (no damage): does the shield stop it? It costs it 8 x the card's mana; more
        /// than it has left breaks it and gets through. The throw that comes with damage the shield just took is free.
        /// </summary>
        static bool ShieldStops(Combatant victim, Combatant by, Vector3 from, CardId card)
        {
            if (card == CardId.TheChariot) return false; // (the user's wish: nothing holds it off)
            if (victim != null && victim.ServerAbsorbedNow(by)) return true;
            if (!Guards(victim, by, from)) return false;
            var info = Cards.Get(card);
            if (victim.ServerAbsorb(SpellPlans.ShieldPushCost * (info != null ? info.cost : 4), by) > 0f) return false;
            Rebuke(by, victim);
            return true;
        }

        /// <summary>The shield's part of a burn (taken all at once): how much of it is left for the body, 0..1.</summary>
        static float ShieldBurn(Combatant c, PlayerController attacker, Combatant by, Vector3 from)
        {
            if (!Guards(c, by, from)) return 1f;
            float dmg = SpellPlans.BurnDamage;
            if (Combatant.Teammates(attacker, c)) dmg *= SpellPlans.TeamDamage;
            if (Combatant.DamageScale != null) dmg = Combatant.DamageScale(c, attacker != null ? attacker.OwnerClientId : Combatant.NoAttacker, dmg);
            if (dmg <= 0f) return 1f;
            float left = c.ServerAbsorb(dmg, by);
            if (left <= 0f) Rebuke(by, c);
            return left / dmg;
        }

        /// <summary>The shield stopped <paramref name="by"/>'s spell: thrown back, if they're close (at the end of the tick: it interrupts what they're doing).</summary>
        static void Rebuke(Combatant by, Combatant victim)
        {
            if (by == null || by == victim || victim == null) return;
            if ((by.transform.position - victim.transform.position).magnitude <= SpellPlans.RebukeRange) _rebukes.Add((by, victim));
        }

        static void TickShields(double now)
        {
            foreach (var c in Combatant.Registry.ToArray())
                if (c != null && c.IsSpawned) c.ServerTickShield(now);

            foreach (var (by, victim) in _rebukes.ToArray())
            {
                if (by == null || !by.IsAlive || victim == null) continue;
                if (_rebuked.TryGetValue((by, victim), out double at) && now < at + SpellPlans.RebukeEvery) continue;
                _rebuked[(by, victim)] = now;
                if (Charging(by)) continue; // (a shield doesn't stop The Chariot, whatever of theirs it blocked)
                Vector3 away = by.transform.position - victim.transform.position;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = Quaternion.Euler(0f, victim.ViewYaw, 0f) * Vector3.forward;
                by.ServerCredit(victim as PlayerController);
                by.ServerKnock(away.normalized * SpellPlans.RebukeKnock + Vector3.up * SpellPlans.RebukeUp, SpellPlans.RebukeRagdoll);
            }
            _rebukes.Clear();
            if (_rebuked.Count > 64) _rebuked.Clear();
        }
    }
}
