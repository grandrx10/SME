using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for Necromatic Blast's landing and the Hex cards.
    ///  - A hex: 0.8 s pointing at someone within 6 m and in sight (knocked down meanwhile: nothing). Then a ball of symbols
    ///    goes to them whatever they do, and the hex is on them for 10 s. Several can be on one body.
    ///  - Shatter: 50 damage taken sets it off: lifted and frozen in the air a moment, then 30 more, the ground round them
    ///    blown out, thrown hard away from the attack that did it. The kill is the hex caster's, and the body's in pieces.
    ///  - Stasis: 40 damage taken sets it off: frozen where they are, in the pose they're in, for 4 s or until the next hit
    ///    (anything that moves people counts). Not stopped by a combo break or anything that can't be knocked about. Burning doesn't thaw them.
    ///  - Mind: fills while its caster is near (2 s close by, 5 s at 8 m). Then they move the way the caster moves (in the
    ///    world, whichever way they face) and can't cast, for 6 s, or until 12 m apart. One at a time per body.
    /// </summary>
    public static partial class SpellServer
    {
        class HexCastState { public int id; public PlayerController caster; public Combatant target; public byte kind; public double start; }
        class HexFlyState { public PlayerController caster; public ulong by; public Combatant target; public byte kind; public double arrive; }
        class ShatterState { public Combatant victim; public ulong by; public Vector3 dir, from; public double at; }

        static readonly List<HexCastState> HexCasts = new();
        static readonly List<HexFlyState> HexFlies = new();
        static readonly List<ShatterState> Shatters = new();
        static double _mindNext, _mindCredit;

        /// <summary>Server: the damage being dealt right now is a burn's (it doesn't thaw anyone out of Stasis).</summary>
        public static bool BurnTicking;

        static void ResetHex()
        {
            HexCasts.Clear();
            HexFlies.Clear();
            Shatters.Clear();
            _mindNext = _mindCredit = 0;
        }

        // ------------------------------------------------------------------ casting

        static bool CastHex(PlayerController caster, CardId card, ulong targetId, double now)
        {
            byte kind = SpellPlans.HexKind(card);
            var t = WorldNet.FindCombatant(targetId);
            if (t == null || t == caster || !t.IsAlive || Combatant.Allies(caster, t) || Folded(t) || Room(t) != Room(caster)) return false;
            if ((t.transform.position - caster.transform.position).magnitude > SpellPlans.HexRange + 1.5f) return false; // (some slack for lag)
            if (!SpellPlans.HexSight(caster.transform.position, t)) return false;
            if (kind == SpellPlans.MindHex && MindHexed(t)) return false;
            HexCasts.RemoveAll(h => h.caster == caster);
            int id = WorldNet.ServerHexCast(caster.NetworkObjectId, t.NetworkObjectId, kind);
            HexCasts.Add(new HexCastState { id = id, caster = caster, target = t, kind = kind, start = now });
            return true;
        }

        /// <summary>Someone's Mind Hex is on this body, or has hold of it.</summary>
        public static bool MindHexed(Combatant c) =>
            c != null && c.IsSpawned && (c.MindHeld || c.Hexes.Value.Find(SpellPlans.MindHex, WorldNet.Now, out _));

        /// <summary>Knocked down while pointing: no hex.</summary>
        static bool FizzleHex(PlayerController pc) => HexCasts.RemoveAll(h => h.caster == pc) > 0;

        /// <summary>A practice chamber is being reset.</summary>
        static void ClearHex(PlayerController pc, System.Func<Vector3, bool> inside)
        {
            HexCasts.RemoveAll(h => h.caster == pc);
            HexFlies.RemoveAll(h => h.caster == pc);
            Shatters.RemoveAll(s => s.victim == null || s.victim == pc || inside(s.from));
            foreach (var c in Combatant.Registry)
            {
                if (c == null || !c.IsSpawned || c != pc && !inside(c.transform.position)) continue;
                if (c.Hexes.Value.Any) c.Hexes.Value = default;
                EndStasis(c);
                EndShatterHold(c);
                EndMind(c);
            }
        }

        // ------------------------------------------------------------------ every frame

        static void TickHex(double now)
        {
            for (int i = HexCasts.Count - 1; i >= 0; i--)
            {
                var h = HexCasts[i];
                if (h.caster == null || !h.caster.IsAlive) { HexCasts.RemoveAt(i); continue; }
                if (now < h.start + SpellPlans.HexCast) continue;
                HexCasts.RemoveAt(i);
                if (h.target == null || !h.target.IsAlive) continue;
                WorldNet.ServerHexFly(h.id);
                HexFlies.Add(new HexFlyState { caster = h.caster, by = h.caster.NetworkObjectId, target = h.target, kind = h.kind, arrive = now + SpellPlans.HexFlight });
            }
            for (int i = HexFlies.Count - 1; i >= 0; i--)
            {
                var f = HexFlies[i];
                if (now < f.arrive) continue;
                HexFlies.RemoveAt(i);
                if (f.target == null || !f.target.IsAlive) continue;
                if (f.kind == SpellPlans.MindHex && MindHexed(f.target)) continue; // someone else's got there first
                var marks = f.target.Hexes.Value;
                marks.Add(new HexMark { Kind = f.kind, By = f.by, From = now, Until = now + SpellPlans.HexTime }, now);
                f.target.Hexes.Value = marks;
            }
            if (Shatters.Count > 0)
            {
                var due = Shatters.FindAll(s => now >= s.at);
                Shatters.RemoveAll(s => now >= s.at);
                foreach (var s in due) Shatter(s);
            }
            ChainFrozen(now);
            TickMarks(now);
        }

        static void TickMarks(double now)
        {
            bool fill = now >= _mindNext, credit = now >= _mindCredit;
            if (fill) _mindNext = now + SpellPlans.MindStep;
            if (credit) _mindCredit = now + 0.25;
            foreach (var c in Combatant.Registry.ToArray())
            {
                if (c == null || !c.IsAlive) continue;
                // under someone's control
                if (c.MindHeld)
                {
                    var master = WorldNet.FindCaster(c.Status.Value.MindBy);
                    if (master == null || !master.IsAlive || (master.transform.position - c.transform.position).magnitude > SpellPlans.MindBreak) EndMind(c);
                    else if (credit) c.ServerCredit(master); // walked off a ledge: their kill
                }
                var marks = c.Hexes.Value;
                if (!marks.Any) continue;
                bool changed = false;
                PlayerController takenBy = null;
                for (int i = 0; i < HexMarks.Max; i++)
                {
                    var m = marks[i];
                    if (m.Kind == 0) continue;
                    if (!m.Live(now)) { marks[i] = default; changed = true; continue; }
                    if (m.Kind != SpellPlans.MindHex) continue;
                    var by = WorldNet.FindCaster(m.By);
                    if (by == null || !by.IsAlive) { marks[i] = default; changed = true; continue; }
                    if (!fill) continue;
                    float takes = SpellPlans.MindFillTime((by.transform.position - c.transform.position).magnitude);
                    if (takes <= 0f) continue;
                    m.Fill = Mathf.Min(1f, m.Fill + SpellPlans.MindStep / takes);
                    if (m.Fill >= 0.9999f) { takenBy = by; marks[i] = default; }
                    else marks[i] = m;
                    changed = true;
                }
                if (changed) c.Hexes.Value = marks;
                if (takenBy != null) BeginMind(c, takenBy, now);
            }
        }

        // ------------------------------------------------------------------ damage fills Shatter and Stasis

        /// <summary>Server: a hit landed on a (still living) body: fills its hexes, sets them off, and thaws it out of Stasis.</summary>
        public static void HexDamaged(Combatant victim, ulong attacker, float landed, Vector3 dir)
        {
            double now = WorldNet.Now;
            if (!BurnTicking) StasisHolds(victim);
            var marks = victim.Hexes.Value;
            if (!marks.Any || landed <= 0f) return;
            bool changed = false, stasis = false;
            for (int i = 0; i < HexMarks.Max; i++)
            {
                var m = marks[i];
                if (!m.Live(now) || m.Kind == SpellPlans.MindHex) continue;
                changed = true;
                m.Fill += landed / (m.Kind == SpellPlans.StasisHex ? SpellPlans.StasisNeeds : SpellPlans.ShatterNeeds);
                if (m.Fill < 0.9999f) { marks[i] = m; continue; }
                marks[i] = default;
                if (m.Kind == SpellPlans.StasisHex) stasis = true;
                else
                {
                    // (two at once go off together)
                    var first = Shatters.Find(s => s.victim == victim);
                    Shatters.Add(new ShatterState
                    {
                        victim = victim, by = m.By, dir = AwayFrom(victim, attacker, dir),
                        from = first != null ? first.from : victim.transform.position, at = first != null ? first.at : now + SpellPlans.ShatterHold,
                    });
                    if (first == null) BeginShatterHold(victim, now);
                }
            }
            if (changed) victim.Hexes.Value = marks;
            if (stasis) BeginStasis(victim, now);
        }

        /// <summary>Level, away from the attack: the way it knocks, or else straight away from whoever did it.</summary>
        static Vector3 AwayFrom(Combatant victim, ulong attacker, Vector3 dir)
        {
            dir.y = 0f;
            if (dir.magnitude > 0.3f) return dir.normalized;
            var source = PlayerController.ServerFind(attacker);
            if (source != null && source != victim)
            {
                Vector3 d = victim.transform.position - source.transform.position;
                d.y = 0f;
                if (d.sqrMagnitude > 0.01f) return d.normalized;
            }
            return Quaternion.Euler(0f, victim.ViewYaw, 0f) * Vector3.back;
        }

        // ------------------------------------------------------------------ Shatter

        /// <summary>Set off: lifted and held in the air, cased in glass, until it goes off.</summary>
        static void BeginShatterHold(Combatant c, double now)
        {
            var st = c.Status.Value;
            st.ShatterFrom = now;
            st.ShatterUntil = now + SpellPlans.ShatterHold + 0.5; // (ended by the server as it goes off)
            c.Status.Value = st;
            LetGo(c);
        }

        static void EndShatterHold(Combatant c)
        {
            if (c == null || !c.IsSpawned || c.Status.Value.ShatterUntil <= 0) return;
            var st = c.Status.Value;
            st.ShatterUntil = 0;
            c.Status.Value = st;
        }

        static void Shatter(ShatterState s)
        {
            var v = s.victim;
            if (v == null || !v.IsSpawned) return;
            EndShatterHold(v);
            if (!v.IsAlive) return;
            EndStasis(v); // (set off by the same hit: this is the next one)
            var by = WorldNet.FindCaster(s.by);
            Vector3 p = v.transform.position;
            WorldNet.ServerHexShatter(p, s.dir);
            var w = VoxelWorld.Current;
            // (the ground round where it lifted them from)
            if (w != null) WorldNet.ServerRemoveSphere(w.CellOf(p + Vector3.down * SpellPlans.ShatterLift), SpellPlans.R4(SpellPlans.ShatterRadius), 12f, s.dir * 5f);
            Vector3 fling = s.dir * SpellPlans.ShatterKnock + Vector3.up * SpellPlans.ShatterKnockUp;
            v.ServerCredit(by);
            v.ServerApplyDamage(SpellPlans.ShatterDamage, by != null ? by.OwnerClientId : Combatant.NoAttacker, p, fling.normalized, -1, fling.magnitude, DeathCause.Spell, shatter: true);
            if (v.IsAlive) v.ServerKnock(fling, SpellPlans.ShatterRagdoll);
        }

        // ------------------------------------------------------------------ Stasis

        static void BeginStasis(Combatant c, double now)
        {
            var st = c.Status.Value;
            st.StasisFrom = now;
            st.StasisUntil = now + SpellPlans.StasisTime;
            c.Status.Value = st;
            LetGo(c);
        }

        /// <summary>Frozen: whatever had hold of them lets go, whatever they were casting stops.</summary>
        static void LetGo(Combatant c)
        {
            ReleaseFromCarry(c);
            foreach (var d in ChariotsS)
                if (d.victim == c) { d.victim = null; c.PinnedBy.Value = 0; }
            FreeGrasp(c);
            PopBubble(c);
            if (c is PlayerController pc) OnInterrupted(pc, true);
        }

        static void PopBubble(Combatant c)
        {
            foreach (var b in BubblesS.ToArray()) if (b.caster == c) EndBubble(b, true);
        }

        /// <summary>
        /// Server: a hit (damage, a knock, a knockdown) on someone who may be frozen. True: the rest of the attack that froze
        /// them, which doesn't move them (nor does anything while they're about to shatter). Anything later thaws them out
        /// (false: it goes ahead as usual).
        /// </summary>
        public static bool StasisHolds(Combatant c)
        {
            if (c == null) return false;
            if (c.Shattering) return true;
            if (!c.InStasis) return false;
            if (WorldNet.Now <= c.Status.Value.StasisFrom + SpellPlans.StasisGrace) return true;
            EndStasis(c);
            return false;
        }

        /// <summary>Server: something is taking this body along without a hit (a chariot, the Empress's chains): it isn't frozen any more.</summary>
        static void FreeToMove(Combatant c)
        {
            EndStasis(c);
            EndShatterHold(c);
        }

        /// <summary>Frozen outside the Empress's domain that holds them: her chains still haul them back in.</summary>
        static void ChainFrozen(double now)
        {
            if (Domains.Count == 0) return;
            foreach (var c in Combatant.Registry)
            {
                if (c == null || !c.InStasis || now < c.Status.Value.StasisFrom + SpellPlans.StasisGrace || !TrapCenter(c, out var center)) continue;
                Vector3 o = c.FeetPosition - center;
                if (new Vector2(o.x, o.z).magnitude > SpellPlans.EmpressRadius + 0.3f || o.y > 4.3f) FreeToMove(c);
            }
        }

        static void EndStasis(Combatant c)
        {
            if (c == null || !c.IsSpawned || c.Status.Value.StasisUntil <= 0) return;
            var st = c.Status.Value;
            st.StasisUntil = 0;
            c.Status.Value = st;
        }

        // ------------------------------------------------------------------ Mind

        static void BeginMind(Combatant c, PlayerController by, double now)
        {
            var st = c.Status.Value;
            st.MindFrom = now;
            st.MindUntil = now + SpellPlans.MindTime;
            st.MindBy = by.NetworkObjectId;
            c.Status.Value = st;
            c.ServerCredit(by);
            PopBubble(c);
            if (c is PlayerController pc) OnInterrupted(pc, true);
            WorldNet.ServerHexProc(SpellPlans.MindHex, c.NetworkObjectId);
        }

        static void EndMind(Combatant c)
        {
            if (c == null || !c.IsSpawned || c.Status.Value.MindUntil <= 0) return;
            var st = c.Status.Value;
            st.MindUntil = 0;
            c.Status.Value = st;
        }

        /// <summary>Whoever has control of this body right now (null: nobody).</summary>
        public static PlayerController MindMaster(Combatant c) => c != null && c.MindHeld ? WorldNet.FindCaster(c.Status.Value.MindBy) : null;

        // ------------------------------------------------------------------ Necromatic Blast lands

        /// <summary>Necromatic Blast struck someone (no splash), or something solid: the voxels round there go a sickly green.</summary>
        static void LandNecro(FireballState f, WorldNet.FireballFx fx, Vector3 point, Combatant victim)
        {
            var owner = WorldNet.FindCaster(fx.owner);
            if (victim != null && victim.IsAlive)
            {
                bool enemy = !Combatant.Allies(owner, victim) && !Shielded(victim);
                Vector3 dir = fx.velocity.sqrMagnitude > 0.01f ? fx.velocity.normalized : Vector3.forward;
                Hit(victim, owner, SpellPlans.NecroDamage, point, dir * SpellPlans.NecroKnock + Vector3.up * SpellPlans.NecroKnockUp, SpellPlans.NecroRagdoll);
                if (enemy && owner != null && owner.IsAlive)
                {
                    owner.ServerHeal(SpellPlans.NecroHeal, SpellPlans.NecroHealTime);
                    WorldNet.ServerHexProc(0, owner.NetworkObjectId);
                }
            }
            else
            {
                var w = VoxelWorld.Current;
                if (w != null) WorldNet.ServerBlight(w.CellOf(point), SpellPlans.R4(SpellPlans.NecroBlight));
            }
            WorldNet.ServerFireballEnd(f.id, point, false);
        }
    }
}
