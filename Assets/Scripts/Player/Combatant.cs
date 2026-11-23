using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>What eliminated someone (picks the kill feed icon).</summary>
    public enum DeathCause : byte { Spell = 0, Fall = 1, Barrier = 2 }

    /// <summary>
    /// Anything with the player model that can be hurt: health, hitboxes, death, ragdoll.
    /// Players (PlayerController) and test dummies (TestDummy) derive from this.
    /// </summary>
    public abstract class Combatant : NetworkBehaviour
    {
        [Header("References")]
        public Transform model;

        [Header("Health")]
        public float maxHealth = 100f;

        public readonly NetworkVariable<float> Health = new(100f);
        public readonly NetworkVariable<bool> Dead = new(false);
        /// <summary>How many burns are on this body right now (each: 15 damage over 5 s; they stack). Drives the flames on it.</summary>
        public readonly NetworkVariable<byte> BurnStacks = new(0);
        /// <summary>The Empress domain (id) holding this body, 0 = free: can't cast, and can't leave the domain (the chains pull it back in).</summary>
        public readonly NetworkVariable<int> TrappedBy = new();
        /// <summary>Held by an Empress domain (chained to whoever opened it).</summary>
        public bool Trapped => IsSpawned && TrappedBy.Value != 0;
        /// <summary>The Chariot driver (object id) this body is stuck on the front of, 0 = free: carried along, can't act.</summary>
        public readonly NetworkVariable<ulong> PinnedBy = new();
        /// <summary>Stuck on the front of someone's Chariot.</summary>
        public bool Pinned => IsSpawned && PinnedBy.Value != 0;

        /// <summary>The driver this body is stuck on the front of (null if free, or they're gone).</summary>
        public Combatant PinnedTo => Pinned ? WorldNet.FindCombatant(PinnedBy.Value) : null;
        public virtual float MaxHealth => maxHealth;

        /// <summary>Hanging, Wheel of Fortune's results, chilled, Undead (see <see cref="BodyStatus"/>). Server writes.</summary>
        public readonly NetworkVariable<BodyStatus> Status = new();
        /// <summary>One of the Undead (raised by Undead Servitude, an Infectious Bite, or Summon Horde).</summary>
        public bool IsUndead => IsSpawned && Status.Value.Undead;
        /// <summary>Held by the legs by Undying Grasp: can't move or cast (until it's ragdolled).</summary>
        public bool Grasped => IsSpawned && Status.Value.Grasped(WorldNet.Now);
        /// <summary>Whose side this body is on: its own, or (one of the Undead) its master's.</summary>
        public ulong Team => IsSpawned && Status.Value.Undead ? Status.Value.Master : NetworkObjectId;

        /// <summary>Two different bodies on the same side (a necromancer and their Undead): they can't hurt or throw each other.</summary>
        public static bool Allies(Combatant a, Combatant b) => a != null && b != null && a != b && a.IsSpawned && b.IsSpawned && a.Team == b.Team;

        /// <summary>Server: (victim, attacker client id, damage that landed) after every hit.</summary>
        public static System.Action<Combatant, ulong, float> Damaged;
        /// <summary>Server: (who, amount, over seconds) whenever healing is given.</summary>
        public static System.Action<Combatant, float, float> Healed;
        /// <summary>Server: (victim, attacker client id, what did it) when a body dies.</summary>
        public static System.Action<Combatant, ulong, DeathCause> Killed;

        public ProceduralAnimator Animator { get; private set; }
        public HitboxSet Hitboxes { get; private set; }
        public Dictionary<string, Transform> Bones { get; private set; }
        public GameObject LastRagdoll { get; protected set; }
        public bool Visible { get; private set; } = true;
        public bool IsAlive => IsSpawned && !Dead.Value;
        /// <summary>The body's root sits at the hips; this is where the feet touch the ground.</summary>
        public Vector3 FeetPosition => transform.position + Vector3.down * 0.95f;

        // view state used by the procedural animator
        public abstract Vector3 ViewVelocity { get; }
        public abstract float ViewYaw { get; }
        public abstract float ViewPitch { get; }
        public abstract byte ViewFlags { get; }
        /// <summary>Spell pose being played (ProceduralAnimator) and how long it has been playing.</summary>
        public virtual SpellPose ViewPose => SpellPose.None;
        public virtual float ViewPoseTime => 0f;
        /// <summary>Lunging teeth first with Infectious Bite right now (its ragdoll flies head first).</summary>
        public virtual bool LungingView => false;

        Renderer[] _renderers;

        protected virtual void Awake()
        {
            Animator = GetComponent<ProceduralAnimator>();
            if (GetComponent<BurnFx>() == null) gameObject.AddComponent<BurnFx>();
            if (GetComponent<TarotBodyFx>() == null) gameObject.AddComponent<TarotBodyFx>();
            if (GetComponent<StatusBodyFx>() == null) gameObject.AddComponent<StatusBodyFx>();
            if (GetComponent<Tarot3BodyFx>() == null) gameObject.AddComponent<Tarot3BodyFx>();
            if (GetComponent<GraspBodyFx>() == null) gameObject.AddComponent<GraspBodyFx>();
            if (model == null) model = transform.Find("PlayerModel");
            Bones = Rig.MapBones(model);
            _renderers = model.GetComponentsInChildren<Renderer>(true);
            foreach (var r in _renderers)
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
        }

        public override void OnNetworkSpawn()
        {
            Hitboxes ??= new HitboxSet(this, transform, Bones);
            Registry.Add(this);
            Dead.OnValueChanged += OnDeadChanged;
            if (IsServer) Health.Value = MaxHealth;
        }

        public override void OnNetworkDespawn()
        {
            Dead.OnValueChanged -= OnDeadChanged;
            Registry.Remove(this);
            if (LastRagdoll) Destroy(LastRagdoll);
            if (_liveRagdoll) Destroy(_liveRagdoll);
        }

        // ---------------- server ----------------

        /// <param name="cutNormal">Non-zero: if this hit kills, the body is cut in half along the plane through <paramref name="point"/> with this normal (Blade).</param>
        /// <param name="bore">Non-zero: if this hit kills, a round tunnel (radius: its length) along this axis through <paramref name="point"/> is missing from the body (the fused ball).</param>
        public void ServerApplyDamage(float dmg, ulong attacker, Vector3 point, Vector3 dir, int segment, float impulse, DeathCause cause, Vector3 cutNormal = default, Vector3 bore = default)
        {
            if (!IsServer || Dead.Value || dmg <= 0f) return;
            if (DamageScale != null) dmg = DamageScale(this, attacker, dmg); // Empress / Emperor
            if (dmg <= 0f) return;
            var source = PlayerController.ServerFind(attacker);
            if (source != null && source != this) _lastAttacker = attacker;
            float before = Health.Value;
            Health.Value = Mathf.Max(0f, Health.Value - dmg);
            OnServerDamaged(dmg);
            Damaged?.Invoke(this, attacker, before - Health.Value); // Justice: shared with whoever's tethered
            // tell whoever did it (damage number + hit marker on their screen): the hit's own damage, not just the
            // health that happened to be left (a 15-damage rock finishing someone on 1 HP showed "1")
            if (source != null && source != this)
                source.ServerHitConfirm(NetworkObjectId, transform.position + Vector3.up * 1.15f, Health.Value <= 0f ? dmg : before - Health.Value, Health.Value <= 0f);
            if (Health.Value <= 0f)
            {
                // spells credit the caster; falls and barriers credit whoever hurt you last
                ulong credit = cause == DeathCause.Spell && source != null && source != this ? attacker : _lastAttacker;
                ServerDie(credit, point, dir * impulse, segment, cause, cutNormal, bore);
            }
        }

        /// <summary>Server: instant elimination from the level (kill barrier, falling out of the arena).</summary>
        public void ServerKillByHazard(DeathCause cause, Vector3 point)
        {
            if (!IsServer || Dead.Value) return;
            Health.Value = 0f;
            ServerDie(_lastAttacker, point, Vector3.zero, -1, cause);
        }

        /// <summary>Server: scales damage before it lands (victim, attacker client id, damage) → damage.</summary>
        public static System.Func<Combatant, ulong, float, float> DamageScale;

        // ---------------- burn ----------------

        readonly List<(double end, ulong attacker)> _burns = new();
        float _burnOwed;

        /// <summary>Server: set alight — another 15 damage over 5 seconds, on top of any burns already going.</summary>
        public void ServerBurn(ulong attacker)
        {
            if (!IsServer || Dead.Value || SpellServer.Invulnerable(this)) return;
            _burns.Add((WorldNet.Now + SpellPlans.BurnTime, attacker));
            BurnStacks.Value = (byte)Mathf.Min(255, _burns.Count);
        }

        /// <summary>Server, every frame: burns tick their damage (in small bites) and run out.</summary>
        public void ServerTickBurn(double now, float dt)
        {
            if (_burns.Count == 0) return;
            if (Dead.Value) { ClearBurns(); return; }
            ulong lastAttacker = NoAttacker;
            int live = 0;
            for (int i = _burns.Count - 1; i >= 0; i--)
            {
                if (now >= _burns[i].end) { _burns.RemoveAt(i); continue; }
                live++;
                lastAttacker = _burns[i].attacker;
            }
            if ((byte)live != BurnStacks.Value) BurnStacks.Value = (byte)live;
            _burnOwed += live * (SpellPlans.BurnDamage / SpellPlans.BurnTime) * dt;
            if (_burnOwed >= 1.5f)
            {
                float bite = _burnOwed;
                _burnOwed = 0f;
                ServerApplyDamage(bite, lastAttacker, transform.position, Vector3.up, -1, 0f, DeathCause.Spell);
            }
        }

        void ClearBurns()
        {
            _burns.Clear();
            _burnOwed = 0f;
            if (IsServer && BurnStacks.Value != 0) BurnStacks.Value = 0;
        }

        /// <summary>Server: water put the flames out.</summary>
        public void ServerExtinguish() => ClearBurns();

        // ---------------- healing ----------------

        readonly List<(double end, float rate)> _heals = new();
        float _healOwed;

        /// <summary>Server: restore <paramref name="amount"/> health spread over <paramref name="seconds"/> (never above max).</summary>
        public void ServerHeal(float amount, float seconds)
        {
            if (!IsServer || Dead.Value || amount <= 0f) return;
            seconds = Mathf.Max(0.01f, seconds);
            _heals.Add((WorldNet.Now + seconds, amount / seconds));
            Healed?.Invoke(this, amount, seconds); // Justice: passed on to whoever's tethered to this body
        }

        /// <summary>Server, every frame: healing trickles in (in small steps) until it runs out.</summary>
        public void ServerTickHeal(double now, float dt)
        {
            if (_heals.Count == 0) return;
            if (Dead.Value) { _heals.Clear(); _healOwed = 0f; return; }
            for (int i = _heals.Count - 1; i >= 0; i--)
            {
                // only the part of this frame before it ran out
                float share = Mathf.Clamp((float)(_heals[i].end - (now - dt)), 0f, dt);
                _healOwed += _heals[i].rate * share;
                if (now >= _heals[i].end) _heals.RemoveAt(i);
            }
            if (_healOwed >= 1f || _heals.Count == 0)
            {
                Health.Value = Mathf.Min(MaxHealth, Health.Value + _healOwed);
                _healOwed = 0f;
            }
        }

        /// <summary>Server: knock this body away (and ragdoll it for a while). Players override this.</summary>
        public virtual void ServerKnock(Vector3 velocity, float ragdollSeconds) { }

        /// <summary>Server: put this body somewhere else, on its feet (The World). Players and dummies override this.</summary>
        public virtual void ServerPlace(Vector3 hips, float yaw) { }

        /// <summary>Server: caught by a tornado — spun along with it (see SpellPlans.TornadoCarry) until thrown out by ServerKnock.</summary>
        public virtual void ServerCarry(Vector3 origin, Vector3 dir, double start, double captured, float phase) { }

        /// <summary>"Nobody" as an attacker.</summary>
        public const ulong NoAttacker = ulong.MaxValue;
        ulong _lastAttacker = NoAttacker;

        /// <summary>
        /// Server: <paramref name="by"/> just shoved, pulled or flung this body (even without damage), so a fall or lava
        /// death from here is their kill.
        /// </summary>
        public void ServerCredit(PlayerController by)
        {
            if (IsServer && by != null && by != this && !Dead.Value) _lastAttacker = by.OwnerClientId;
        }

        void ServerDie(ulong attacker, Vector3 point, Vector3 impulse, int segment, DeathCause cause, Vector3 cutNormal = default, Vector3 bore = default)
        {
            Dead.Value = true;
            ClearBurns();
            OnServerKilled(attacker);
            Killed?.Invoke(this, attacker, cause);
            if (!InPracticeChamber) ServerBroadcastKill(attacker, cause); // practice deaths stay out of the kill feed
            DieRpc(point, impulse, (byte)Mathf.Clamp(segment, 0, 255), cutNormal, bore);
            _lastAttacker = NoAttacker;
        }

        /// <summary>Server: bring back to life with full health (round start).</summary>
        public void ServerRevive()
        {
            if (!IsServer || !IsSpawned) return;
            Health.Value = MaxHealth;
            Dead.Value = false;
            _lastAttacker = NoAttacker;
            ClearBurns();
            _heals.Clear();
            _healOwed = 0f;
        }

        // ---------------- kill feed ----------------

        /// <summary>(killer name, killer colour, victim name, victim colour, self-kill, cause). Fired on every client.</summary>
        public static event System.Action<string, int, string, int, bool, DeathCause> KillFeed;

        public virtual string FeedName => name;
        /// <summary>Lives in a practice chamber (a practising player, or one of a chamber's dummies).</summary>
        public virtual bool InPracticeChamber => false;
        public virtual int FeedColor => -1;

        void ServerBroadcastKill(ulong attacker, DeathCause cause)
        {
            PlayerController killer = attacker != NoAttacker ? PlayerController.ServerFind(attacker) : null;
            bool self = killer != null && killer == this;
            var kName = new FixedString32Bytes();
            kName.CopyFromTruncated(killer != null ? killer.FeedName : "");
            var vName = new FixedString32Bytes();
            vName.CopyFromTruncated(FeedName);
            KillFeedRpc(kName, (sbyte)(killer != null ? killer.FeedColor : -1), vName, (sbyte)FeedColor, self, (byte)cause);
        }

        [Rpc(SendTo.Everyone)]
        void KillFeedRpc(FixedString32Bytes killer, sbyte killerColor, FixedString32Bytes victim, sbyte victimColor, bool self, byte cause)
        {
            KillFeed?.Invoke(killer.ToString(), killerColor, victim.ToString(), victimColor, self, (DeathCause)cause);
        }

        protected virtual void OnServerDamaged(float amount) { }
        protected virtual void OnServerKilled(ulong attackerClientId) { }

        // ---------------- everyone ----------------

        [Rpc(SendTo.Everyone)]
        void DieRpc(Vector3 point, Vector3 impulse, byte segment, Vector3 cutNormal, Vector3 bore)
        {
            if (LastRagdoll) Destroy(LastRagdoll);
            SetOutline(null); // the ragdoll copies the body's materials
            // already flopping (knocked down): that ragdoll just goes limp for good
            var live = TakeLiveRagdoll();
            LastRagdoll = live != null ? live : RagdollFactory.Spawn(this, ViewVelocity, segment, point, impulse);
            if (live != null) RagdollFactory.Push(live, impulse);
            // a Blade kill: cut in two where the slash landed
            if (cutNormal.sqrMagnitude > 0.01f && LastRagdoll != null)
            {
                GameObject halves = null;
                try { halves = BodySlicer.Slice(LastRagdoll, point, cutNormal); }
                catch (System.Exception e) { Debug.LogWarning("[SME] Slice failed, using ragdoll: " + e.Message); }
                if (halves != null) LastRagdoll = halves;
            }
            // killed by the fused ball: a round hole right through where it passed
            if (bore.sqrMagnitude > 1e-4f && LastRagdoll != null)
            {
                try { BodyBorer.Bore(LastRagdoll, point, bore, bore.magnitude); }
                catch (System.Exception e) { Debug.LogWarning("[SME] Bore failed, using ragdoll: " + e.Message); }
            }
            if (LastRagdoll != null) SetUpCorpse(LastRagdoll);
            SetVisible(false);
            OnDiedLocal();
        }

        /// <summary>How long a dead body stays before shrinking away; 0 = until it's cleared (players: the next round).</summary>
        protected virtual float CorpseSeconds => RagdollFactory.Lifetime;

        void SetUpCorpse(GameObject body)
        {
            var ttl = body.GetComponent<RagdollLifetime>();
            float seconds = CorpseSeconds;
            if (seconds > 0f)
            {
                if (ttl == null) ttl = body.AddComponent<RagdollLifetime>();
                ttl.dieAt = Time.time + seconds;
            }
            else if (ttl != null) Destroy(ttl);
            if (body.GetComponent<LavaFloat>() == null) body.AddComponent<LavaFloat>();
        }

        /// <summary>Skinwalker: a corpse whose skin is being taken stays at least this much longer.</summary>
        public void HoldCorpse(float seconds)
        {
            var ttl = LastRagdoll != null ? LastRagdoll.GetComponent<RagdollLifetime>() : null;
            if (ttl != null) ttl.dieAt = Mathf.Max(ttl.dieAt, Time.time + seconds);
        }

        /// <summary>Skinwalker: its skin's been taken: the corpse shrinks away, and is nobody's any more.</summary>
        public void ConsumeCorpse()
        {
            if (!Dead.Value || LastRagdoll == null) return;
            var body = LastRagdoll;
            LastRagdoll = null;
            var ttl = body.GetComponent<RagdollLifetime>();
            if (ttl == null) ttl = body.AddComponent<RagdollLifetime>();
            ttl.dieAt = Time.time;
        }

        // ---------------- live ragdoll (knocked down, still alive) ----------------

        GameObject _liveRagdoll;
        public bool HasLiveRagdoll => _liveRagdoll != null;
        public GameObject LiveRagdollObject => _liveRagdoll;
        /// <summary>Where a camera should look while the body is a ragdoll (its pelvis), else null.</summary>
        public Transform RagdollFocus => _liveRagdoll != null ? RagdollFactory.Pelvis(_liveRagdoll)?.transform : null;

        /// <summary>Call every frame: shows a flopping ragdoll (steered onto the real body) while <paramref name="want"/> is true.</summary>
        protected void UpdateLiveRagdoll(bool want)
        {
            want &= !Dead.Value && Visible;
            if (want && _liveRagdoll == null)
            {
                _liveRagdoll = RagdollFactory.Spawn(this, ViewVelocity, -1, transform.position, Vector3.zero, 0f);
                _liveRagdoll.AddComponent<LiveRagdoll>().target = this;
                SetModelHidden(true);
                WatchRagdoll(_liveRagdoll);
                OnLiveRagdollChanged();
            }
            else if (!want && _liveRagdoll != null) ClearLiveRagdoll(!Dead.Value && Visible);
        }

        /// <param name="getUp">Play the get-up animation from wherever the ragdoll ended up.</param>
        protected void ClearLiveRagdoll(bool getUp = false)
        {
            if (_liveRagdoll == null) return;
            if (getUp && Animator != null) Animator.BeginGetUp(_liveRagdoll.transform);
            Destroy(_liveRagdoll);
            _liveRagdoll = null;
            WatchRagdoll(null);
            SetModelHidden(false);
            OnLiveRagdollChanged();
        }

        protected virtual void OnLiveRagdollChanged() { }

        /// <summary>Hands over a knocked-down ragdoll that is currently standing in for the body (null if none).</summary>
        GameObject TakeLiveRagdoll()
        {
            var r = _liveRagdoll;
            _liveRagdoll = null;
            WatchRagdoll(null);
            if (r != null)
            {
                Destroy(r.GetComponent<LiveRagdoll>());
                r.AddComponent<RagdollLifetime>().dieAt = Time.time + RagdollFactory.Lifetime;
                SetModelHidden(false);
                OnLiveRagdollChanged();
            }
            return r;
        }

        protected virtual void OnDiedLocal() { }

        protected virtual void OnDeadChanged(bool prev, bool dead)
        {
            SetVisible(!dead);
        }

        public virtual void SetVisible(bool on)
        {
            Visible = on;
            foreach (var r in _renderers) if (r) r.enabled = on && !_modelHidden && !Balled;
            Hitboxes?.SetEnabled(on && !Balled);
            if (_ballBox != null) _ballBox.SetActive(on && Balled);
        }

        bool _modelHidden;

        /// <summary>Hide only the meshes (hitboxes stay) while a live ragdoll stands in for the body.</summary>
        public void SetModelHidden(bool hidden)
        {
            _modelHidden = hidden;
            foreach (var r in _renderers) if (r) r.enabled = Visible && !hidden && !Balled;
        }

        // ---------------- The Chariot: rolled up into a big ball ----------------

        GameObject _ballBox;

        /// <summary>Charging with The Chariot: the body is a big ball (<see cref="SpellPlans.ChariotRadius"/>) instead of a person.</summary>
        public bool Balled { get; private set; }

        /// <summary>
        /// Every peer: turn the body into the chariot's ball, or back. The model is hidden and one hitbox the size of
        /// the ball stands in for the body's, so what's hit is exactly what's seen.
        /// </summary>
        public void SetBalled(bool on)
        {
            if (Balled == on) return;
            Balled = on;
            if (on && _ballBox == null)
            {
                _ballBox = new GameObject("HB_Ball") { layer = Layers.Hitbox };
                _ballBox.transform.SetParent(transform, false);
                _ballBox.transform.localPosition = SpellPlans.ChariotBallOffset;
                var col = _ballBox.AddComponent<SphereCollider>();
                col.isTrigger = true;
                col.radius = SpellPlans.ChariotRadius;
                _ballBox.AddComponent<Hitbox>().Owner = this;
            }
            SetVisible(Visible);
            OnLiveRagdollChanged(); // (the face goes with the model)
        }

        // ---------------- outline ----------------

        /// <summary>Every spawned combatant (players and dummies).</summary>
        public static readonly List<Combatant> Registry = new();
        Material[][] _baseMaterials;
        Material _outA, _outB, _outC;
        // the live ragdoll standing in for the body wears the same outlines (it was made with whatever the body had on)
        Renderer[] _ragdollRenderers;
        Material[][] _ragdollBase;

        /// <summary>Sets the materials drawn over the body as well as its own (an outline, seen through walls, inverted...); null = none.</summary>
        public void SetOutline(Material a, Material b = null, Material c = null)
        {
            if (a == _outA && b == _outB && c == _outC || _renderers == null) return;
            if (_baseMaterials == null)
            {
                _baseMaterials = new Material[_renderers.Length][];
                for (int i = 0; i < _renderers.Length; i++) _baseMaterials[i] = _renderers[i] ? _renderers[i].sharedMaterials : null;
            }
            _outA = a;
            _outB = b;
            _outC = c;
            Dress(_renderers, _baseMaterials);
            if (_ragdollRenderers != null) Dress(_ragdollRenderers, _ragdollBase);
        }

        void Dress(Renderer[] renderers, Material[][] bases)
        {
            int extra = (_outA != null ? 1 : 0) + (_outB != null ? 1 : 0) + (_outC != null ? 1 : 0);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                var baseMats = bases[i];
                if (!r || baseMats == null) continue;
                if (extra == 0) { r.sharedMaterials = baseMats; continue; }
                var mats = new Material[baseMats.Length + extra];
                baseMats.CopyTo(mats, 0);
                int k = baseMats.Length;
                if (_outA != null) mats[k++] = _outA;
                if (_outB != null) mats[k++] = _outB;
                if (_outC != null) mats[k] = _outC;
                r.sharedMaterials = mats;
            }
        }

        /// <summary>A live ragdoll was just made from the body (or it's gone): remember its own materials, without the outlines it copied.</summary>
        void WatchRagdoll(GameObject ragdoll)
        {
            _ragdollRenderers = null;
            _ragdollBase = null;
            if (ragdoll == null) return;
            _ragdollRenderers = ragdoll.GetComponentsInChildren<Renderer>(true);
            _ragdollBase = new Material[_ragdollRenderers.Length][];
            for (int i = 0; i < _ragdollRenderers.Length; i++)
                _ragdollBase[i] = _ragdollRenderers[i].name == FaceDecal.ObjectName
                    ? null
                    : System.Array.FindAll(_ragdollRenderers[i].sharedMaterials, m => m == null || m != _outA && m != _outB && m != _outC);
        }
    }
}
