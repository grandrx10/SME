using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server rules for the Botany cards: a pot phases into both hands and a plant grows up out of it in 0.3 s. No other card
    /// (or shield) while holding it. Ragdolled (or dead) while holding it: it's over.
    ///  - Sunflower: glows brighter for 3 s, then a beam goes where the caster aims for 3 s: 20 a second to anyone in it,
    ///    and it vaporises what it touches a layer every 0.2 s. Knocked down: the pot and flower are gone.
    ///  - Cactus: held 3 s, running 1.5x as fast: any enemy touching it or the caster takes 15, thrown and ragdolled (each at
    ///    most every 2 s). Then (or knocked down) it's put down where it is, and still pricks for 3 s.
    ///  - Fly Trap: held up to 5 s. The first enemy touching it or the caster is eaten: 15, and inside it until the trap is over
    ///    (a combo break frees them), then spat out. Pressing it again throws the pot, and whoever is in it: it can still eat in the
    ///    air, and lasts 2 s once thrown. Knocked down: gone, and whoever is in it spat out.
    /// </summary>
    public static partial class SpellServer
    {
        class PotState
        {
            public int id; public PlayerController caster; public byte kind; public double start, until, nextLayer; public int phase;
            public Vector3 lastPos;
            public Rigidbody body; public double nextMove; // thrown: the server's physics body
            public bool ate; public Combatant caught; public double caughtUntil;
            public readonly Dictionary<Combatant, double> pricked = new();
            public readonly Dictionary<Combatant, float> owed = new();
            public readonly Dictionary<Combatant, double> refused = new();
        }
        class SpitState { public Combatant who; public PlayerController by; public Vector3 knock; public double at; }

        static readonly List<PotState> PotsS = new();
        static readonly List<SpitState> Spits = new();

        static void ResetBotany()
        {
            foreach (var p in PotsS) if (p.body != null) Object.Destroy(p.body.gameObject);
            PotsS.Clear();
            Spits.Clear();
        }

        static bool CastBotany(PlayerController caster, CardId card, double now)
        {
            if (PotsS.Exists(p => p.caster == caster && p.phase == WorldNet.PotHeld)) return false;
            byte kind = SpellPlans.PotKind(card);
            int id = WorldNet.ServerPot(caster.NetworkObjectId, kind, now);
            if (id == 0) return false;
            PotsS.Add(new PotState { id = id, caster = caster, kind = kind, start = now, until = now + SpellPlans.PotHeldFor(kind), nextLayer = now + SpellPlans.FlowerLaserStart });
            return true;
        }

        /// <summary>Still holding a pot: no other card, no shield.</summary>
        public static bool HoldingPot(PlayerController pc) => PotsS.Exists(p => p.caster == pc && p.phase == WorldNet.PotHeld);

        /// <summary>Knocked down: a held cactus is put down; any other pot is gone.</summary>
        static void InterruptBotany(PlayerController pc, bool knocked)
        {
            if (!knocked) return;
            foreach (var p in PotsS.ToArray())
                if (p.caster == pc && p.phase == WorldNet.PotHeld) LetGoOfPot(p, WorldNet.Now);
        }

        /// <summary>A practice chamber is being reset.</summary>
        static void ClearBotany(PlayerController pc, System.Func<Vector3, bool> inside)
        {
            var net = WorldNet.Instance;
            foreach (var p in PotsS.ToArray())
                if (p.caster == pc || net != null && net.Pots.TryGetValue(p.id, out var fx) && inside(WorldNet.PotPosition(fx, WorldNet.Now))) EndPot(p);
        }

        /// <summary>The owner pressed the Fly Trap again: the pot is thrown (with whoever is in it).</summary>
        static void ThrowPot(PlayerController pc)
        {
            var p = PotsS.Find(x => x.caster == pc && x.phase == WorldNet.PotHeld && x.kind == SpellPlans.PotFlyTrap);
            var net = WorldNet.Instance;
            if (p == null || net == null || !net.Pots.TryGetValue(p.id, out var fx)) return;
            double now = WorldNet.Now;
            // thrown along the aim, on top of how the caster was already moving; a little end-over-end turn
            Vector3 vel = ViewDir(pc) * SpellPlans.TrapThrowSpeed + Vector3.up * SpellPlans.TrapThrowUp + pc.ViewVelocity;
            Vector3 right = Quaternion.Euler(0f, pc.ViewYaw, 0f) * Vector3.right;
            StartBody(p, fx, now, vel, right * 6f + Random.insideUnitSphere * 1.5f);
            p.until = now + SpellPlans.TrapFlight;
            if (p.caught != null && p.caught.IsSpawned)
            {
                // whoever is in it stays in it till it's over
                p.caughtUntil = p.until;
                var st = p.caught.Status.Value;
                st.EatenUntil = p.until;
                p.caught.Status.Value = st;
            }
        }

        /// <summary>
        /// From here on the pot is a real physics body (the server's: it falls, bounces and rolls), told to everyone 20
        /// times a second (they draw it a moment behind, between what they've been told, so it moves smoothly).
        /// </summary>
        static void StartBody(PotState p, WorldNet.PotFx fx, double now, Vector3 vel, Vector3 spin)
        {
            Vector3 from = WorldNet.PotPosition(fx, now);
            Quaternion rot = WorldNet.PotRotation(fx, now);
            WorldNet.ServerPotThrow(p.id, from, vel, rot.eulerAngles.y);
            p.phase = WorldNet.PotFlying;
            p.lastPos = from;
            var go = new GameObject("PotBody") { layer = Layers.Fx };
            go.transform.SetPositionAndRotation(from, rot);
            var box = go.AddComponent<BoxCollider>();
            var b = PotModel.Bounds(p.kind);
            box.center = b.center;
            box.size = b.size;
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 3f;
            rb.linearDamping = 0.05f;
            rb.angularDamping = 0.4f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.linearVelocity = vel;
            rb.maxAngularVelocity = 20f;
            rb.angularVelocity = spin;
            // the game's gravity (everything else falls at this), not Unity's gentler default
            go.AddComponent<ConstantForce>().force = Vector3.down * (rb.mass * Mathf.Max(0f, SpellPlans.PotGravity - Physics.gravity.magnitude));
            if (p.caster != null) foreach (var c in p.caster.GetComponentsInChildren<Collider>()) Physics.IgnoreCollision(box, c);
            if (p.caught != null) foreach (var c in p.caught.GetComponentsInChildren<Collider>()) Physics.IgnoreCollision(box, c);
            p.body = rb;
            p.nextMove = now;
        }

        /// <summary>The plant's head right now (a thrown or dropped pot: where its physics body really is).</summary>
        static Vector3 HeadOf(PotState p, WorldNet.PotFx fx, double now) =>
            p.body != null ? p.body.position + p.body.rotation * PotModel.Head(p.kind) : WorldNet.PotHead(fx, now);

        static void TickBotany(double now, float dt)
        {
            var net = WorldNet.Instance;
            if (net == null) return;
            for (int i = PotsS.Count - 1; i >= 0; i--)
            {
                if (i >= PotsS.Count) continue;
                var p = PotsS[i];
                if (!net.Pots.TryGetValue(p.id, out var fx)) { PotsS.RemoveAt(i); continue; }
                var me = p.caster;
                float t = (float)(now - p.start);
                if (p.phase == WorldNet.PotHeld && (me == null || !me.IsAlive)) { LetGoOfPot(p, now); continue; }
                if (now >= p.until)
                {
                    if (p.phase == WorldNet.PotHeld && p.kind == SpellPlans.PotCactus) DropPot(p, fx, now, SpellPlans.CactusGround);
                    else EndPot(p);
                    continue;
                }
                if (p.body != null && now >= p.nextMove)
                {
                    p.nextMove = now + 1.0 / 30.0;
                    var tr = p.body.transform;
                    WorldNet.ServerPotMove(p.id, tr.position, tr.rotation, p.body.linearVelocity, p.body.angularVelocity);
                }
                if (t < SpellPlans.PotGrow) continue;
                switch (p.kind)
                {
                    case SpellPlans.PotSunflower: FlowerTick(p, fx, now, dt, t); break;
                    case SpellPlans.PotCactus: CactusTick(p, fx, now); break;
                    default: TrapTick(p, fx, now); break;
                }
            }
            for (int i = Spits.Count - 1; i >= 0; i--)
            {
                var s = Spits[i];
                if (now < s.at) continue;
                Spits.RemoveAt(i);
                if (s.who == null || !s.who.IsAlive) continue;
                s.who.ServerCredit(s.by);
                s.who.ServerKnock(s.knock, SpellPlans.TrapSpitRagdoll);
            }
        }

        /// <summary>Ragdolled or dead while holding it: a cactus is put down; the others are gone.</summary>
        static void LetGoOfPot(PotState p, double now)
        {
            var net = WorldNet.Instance;
            if (p.kind == SpellPlans.PotCactus && now - p.start >= SpellPlans.PotGrow && net != null && net.Pots.TryGetValue(p.id, out var fx))
                DropPot(p, fx, now, SpellPlans.CactusGround);
            else EndPot(p);
        }

        /// <summary>Let go of (the cactus): it falls out of the hands carrying on the way the caster was going, and rolls, for <paramref name="lasts"/> more.</summary>
        static void DropPot(PotState p, WorldNet.PotFx fx, double now, float lasts)
        {
            Vector3 v = p.caster != null ? p.caster.ViewVelocity : Vector3.zero;
            Vector3 side = Vector3.Cross(Vector3.up, new Vector3(v.x, 0f, v.z));
            StartBody(p, fx, now, v, side * 1.5f);
            p.until = now + lasts;
        }

        static void EndPot(PotState p)
        {
            if (!PotsS.Remove(p)) return;
            if (p.body != null) Object.Destroy(p.body.gameObject);
            if (p.caught != null) Spit(p, WorldNet.Now);
            WorldNet.ServerPotEnd(p.id);
        }

        // ------------------------------------------------------------------ who it touches

        /// <summary>Enemies touching the plant (within <paramref name="reach"/> of its head) or, while it's held, its holder.</summary>
        static void Touching(PotState p, WorldNet.PotFx fx, double now, float reach, List<Combatant> into)
        {
            into.Clear();
            var me = p.caster;
            Vector3 head = HeadOf(p, fx, now);
            bool held = p.phase == WorldNet.PotHeld && me != null;
            foreach (var o in Combatant.Registry)
            {
                if (o == null || o == me || !o.IsAlive || Folded(o) || Combatant.Allies(me, o) || o.Status.Value.Eaten(now)) continue;
                Vector3 feet = o.FeetPosition, top = feet + Vector3.up * 1.9f * o.transform.localScale.y;
                // the plant against the body's middle line
                Vector3 ab = top - feet;
                float u = Mathf.Clamp01(Vector3.Dot(head - feet, ab) / ab.sqrMagnitude);
                bool touch = (head - (feet + ab * u)).magnitude <= reach + 0.35f;
                if (!touch && held)
                {
                    Vector3 d = o.transform.position - me.transform.position;
                    touch = new Vector2(d.x, d.z).magnitude <= SpellPlans.BodyTouch && Mathf.Abs(d.y) < 1.6f;
                }
                if (touch) into.Add(o);
            }
        }

        static readonly List<Combatant> _touching = new();

        // ------------------------------------------------------------------ Sunflower

        static void FlowerTick(PotState p, WorldNet.PotFx fx, double now, float dt, float t)
        {
            var me = p.caster;
            if (p.phase != WorldNet.PotHeld || me == null || t < SpellPlans.FlowerLaserStart) return;
            Vector3 from = WorldNet.PotHead(fx, now), dir = SpellPlans.FlowerAim(me, from);
            float reach = SpellPlans.FlowerReach(from, dir);
            // anyone in it: 20 a second (no ragdoll)
            foreach (var o in Combatant.Registry.ToArray())
            {
                if (o == null || o == me || !o.IsAlive || Combatant.Allies(me, o) || Folded(o)) continue;
                Vector3 feet = o.FeetPosition, top = feet + Vector3.up * 1.9f * o.transform.localScale.y;
                if (SegmentGap(from, from + dir * reach, feet, top) > SpellPlans.FlowerBeamRadius + 0.35f) continue;
                p.owed.TryGetValue(o, out float owed);
                owed += SpellPlans.FlowerDps * dt;
                if (owed >= 4f)
                {
                    Hit(o, me, owed, o.transform.position, Vector3.zero, 0f, from: from);
                    owed = 0f;
                }
                p.owed[o] = owed;
            }
            // what it touches is vaporised, a layer at a time
            var w = VoxelWorld.Current;
            if (w == null || now < p.nextLayer) return;
            p.nextLayer = now + SpellPlans.FlowerLayerEvery;
            var cells = new HashSet<Vector3Int>();
            var rot = Quaternion.LookRotation(dir);
            for (int k = 0; k < 9; k++)
            {
                // the middle, and eight round the edge of the beam
                Vector3 off = k == 0 ? Vector3.zero : rot * (Quaternion.Euler(0f, 0f, k * 45f) * Vector3.right * SpellPlans.FlowerBeamRadius * 0.85f);
                if (!Physics.Raycast(from + off, dir, out var hit, SpellPlans.FlowerRange, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                if (!VoxelWorld.IsVoxelCollider(hit.collider)) continue;
                var c = w.CellOf(hit.point - hit.normal * 0.1f);
                ushort v = w.Get(c);
                if (v != 0 && !w.IsIndestructible(v)) cells.Add(c);
            }
            if (cells.Count > 0) WorldNet.ServerFlowerErase(new List<Vector3Int>(cells));
        }

        /// <summary>The shortest distance between two segments.</summary>
        static float SegmentGap(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1)
        {
            Vector3 d1 = a1 - a0, d2 = b1 - b0, r = a0 - b0;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;
            if (a < 1e-6f) { s = 0f; t = Mathf.Clamp01(f / Mathf.Max(e, 1e-6f)); }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e < 1e-6f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else
                {
                    float b = Vector3.Dot(d1, d2), den = a * e - b * b;
                    s = den > 1e-6f ? Mathf.Clamp01((b * f - c * e) / den) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            return ((a0 + d1 * s) - (b0 + d2 * t)).magnitude;
        }

        // ------------------------------------------------------------------ Cactus

        static void CactusTick(PotState p, WorldNet.PotFx fx, double now)
        {
            Touching(p, fx, now, SpellPlans.CactusReach, _touching);
            Vector3 head = HeadOf(p, fx, now);
            foreach (var o in _touching)
            {
                if (p.pricked.TryGetValue(o, out double at) && now < at + SpellPlans.CactusEvery) continue;
                p.pricked[o] = now;
                Vector3 away = o.transform.position - head;
                away.y = 0f;
                away = away.sqrMagnitude > 0.01f ? away.normalized : Quaternion.Euler(0f, WorldNet.PotYaw(fx), 0f) * Vector3.forward;
                Hit(o, p.caster, SpellPlans.CactusDamage, o.transform.position, away * SpellPlans.CactusKnock + Vector3.up * SpellPlans.CactusKnockUp, SpellPlans.CactusRagdoll, from: head);
            }
        }

        /// <summary>Holding a cactus: this much faster (every peer works it out from the pots).</summary>
        public static float PotSpeed(PlayerController pc)
        {
            var net = WorldNet.Instance;
            if (pc == null || net == null) return 1f;
            var fx = net.HeldPot(pc.NetworkObjectId);
            return fx != null && fx.kind == SpellPlans.PotCactus ? SpellPlans.CactusSpeed : 1f;
        }

        // ------------------------------------------------------------------ Fly Trap

        static void TrapTick(PotState p, WorldNet.PotFx fx, double now)
        {
            // whoever's inside: spat out after 2 s (or gone)
            if (p.caught != null)
            {
                if (!p.caught.IsAlive || !p.caught.IsSpawned) p.caught = null;
                else if (now >= p.caughtUntil) Spit(p, now);
                return;
            }
            if (p.ate) return;
            Touching(p, fx, now, SpellPlans.TrapReach, _touching);
            Vector3 mouth = HeadOf(p, fx, now);
            foreach (var o in _touching)
            {
                if (p.refused.TryGetValue(o, out double r) && now < r + 1.0) continue;
                if (Shielded(o) || Invulnerable(o) || Unmovable(o) || Hanging(o)) continue;
                if (o is PlayerController opc ? opc.ServerUnstoppable : o is TestDummy od && od.ServerUnstoppable) continue; // fresh from a combo break
                // an arcane shield held towards it: it bites the shield instead
                if (ShieldStops(o, p.caster, mouth, CardId.FlyTrap)) { p.refused[o] = now; continue; }
                if (ParryCatch(o, p.caster, mouth)) { p.refused[o] = now; continue; } // Parry: not eaten
                Eat(p, o, now, mouth);
                return;
            }
        }

        static void Eat(PotState p, Combatant o, double now, Vector3 mouth)
        {
            Hit(o, p.caster, SpellPlans.TrapDamage, o.transform.position, Vector3.zero, 0f, from: mouth);
            if (!o.IsAlive) return;
            // whatever had hold of them lets go; whatever they were doing stops
            ReleaseFromCarry(o);
            foreach (var d in ChariotsS) if (d.victim == o) { d.victim = null; o.PinnedBy.Value = 0; }
            FreeGrasp(o);
            PopBubble(o);
            EndHang(o);
            if (o is PlayerController pc) OnInterrupted(pc, true);
            var st = o.Status.Value;
            st.EatenBy = p.id;
            st.EatenFrom = now;
            st.EatenUntil = p.until; // (held until the trap is over, or a combo break gets them out)
            o.Status.Value = st;
            o.ServerCredit(p.caster);
            p.ate = true;
            p.caught = o;
            p.caughtUntil = st.EatenUntil;
            // limp in its jaws the whole time (it counts towards a combo break, which gets them out at once: FreeFromTrap)
            o.ServerKnock(Vector3.zero, (float)(p.until - now) + 0.2f);
        }

        /// <summary>A combo break: whoever's inside a fly trap is out of it at once (where its mouth is, on their feet).</summary>
        public static void FreeFromTrap(Combatant c)
        {
            var p = PotsS.Find(x => x.caught == c);
            if (p == null) return;
            p.caught = null;
            if (c == null || !c.IsSpawned) return;
            var st = c.Status.Value;
            st.EatenUntil = 0;
            c.Status.Value = st;
            var net = WorldNet.Instance;
            // (out in front of the trap, not standing in its pot)
            if (c.IsAlive && net != null && net.Pots.TryGetValue(p.id, out var fx))
                c.ServerPlace(HeadOf(p, fx, WorldNet.Now) + Quaternion.Euler(0f, WorldNet.PotYaw(fx), 0f) * Vector3.forward * 0.9f + Vector3.up * 0.3f, c.ViewYaw);
        }

        /// <summary>Let go of: out of the trap where its mouth is, then (a moment later, once their own machine knows) thrown out of it.</summary>
        static void Spit(PotState p, double now)
        {
            var o = p.caught;
            p.caught = null;
            if (o == null || !o.IsSpawned) return;
            var st = o.Status.Value;
            st.EatenUntil = 0;
            o.Status.Value = st;
            if (!o.IsAlive) return;
            var net = WorldNet.Instance;
            Vector3 fwd = Quaternion.Euler(0f, p.caster != null ? p.caster.ViewYaw : 0f, 0f) * Vector3.forward;
            Vector3 vel = Vector3.zero;
            if (net != null && net.Pots.TryGetValue(p.id, out var fx))
            {
                fwd = Quaternion.Euler(0f, WorldNet.PotYaw(fx), 0f) * Vector3.forward;
                if (p.body != null) vel = p.body.linearVelocity;
                o.ServerPlace(HeadOf(p, fx, now) + Vector3.up * 0.2f, o.ViewYaw);
            }
            Vector3 knock = vel.sqrMagnitude > 1f ? new Vector3(vel.x, Mathf.Max(vel.y, 2f), vel.z) : fwd * SpellPlans.TrapSpit + Vector3.up * SpellPlans.TrapSpitUp;
            Spits.Add(new SpitState { who = o, by = p.caster, knock = knock, at = now + 0.12 });
        }
    }
}
