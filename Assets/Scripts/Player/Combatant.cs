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
        /// <summary>The Empress domain (id) holding this body, 0 = free: can't move or cast (spells can still push it about, the chains pull it back in).</summary>
        public readonly NetworkVariable<int> TrappedBy = new();
        /// <summary>Held by an Empress domain (chained to whoever opened it).</summary>
        public bool Trapped => IsSpawned && TrappedBy.Value != 0;
        public virtual float MaxHealth => maxHealth;

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

        Renderer[] _renderers;

        protected virtual void Awake()
        {
            Animator = GetComponent<ProceduralAnimator>();
            if (GetComponent<BurnFx>() == null) gameObject.AddComponent<BurnFx>();
            if (GetComponent<TarotBodyFx>() == null) gameObject.AddComponent<TarotBodyFx>();
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

        public void ServerApplyDamage(float dmg, ulong attacker, Vector3 point, Vector3 dir, int segment, float impulse, DeathCause cause)
        {
            if (!IsServer || Dead.Value || dmg <= 0f) return;
            if (DamageScale != null) dmg = DamageScale(this, attacker, dmg); // Empress / Emperor
            if (dmg <= 0f) return;
            var source = PlayerController.ServerFind(attacker);
            if (source != null && source != this) _lastAttacker = attacker;
            float before = Health.Value;
            Health.Value = Mathf.Max(0f, Health.Value - dmg);
            OnServerDamaged(dmg);
            // tell whoever did it (damage number + hit marker on their screen)
            if (source != null && source != this)
                source.ServerHitConfirm(transform.position + Vector3.up * 1.15f, before - Health.Value, Health.Value <= 0f);
            if (Health.Value <= 0f)
            {
                // spells credit the caster; falls and barriers credit whoever hurt you last
                ulong credit = cause == DeathCause.Spell && source != null && source != this ? attacker : _lastAttacker;
                ServerDie(credit, point, dir * impulse, segment, cause);
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
            if (!IsServer || Dead.Value) return;
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

        /// <summary>Server: knock this body away (and ragdoll it for a while). Players override this.</summary>
        public virtual void ServerKnock(Vector3 velocity, float ragdollSeconds) { }

        /// <summary>Server: caught by a tornado — spun along with it (see SpellPlans.TornadoCarry) until thrown out by ServerKnock.</summary>
        public virtual void ServerCarry(Vector3 origin, Vector3 dir, double start, double captured, float phase) { }

        /// <summary>"Nobody" as an attacker.</summary>
        public const ulong NoAttacker = ulong.MaxValue;
        ulong _lastAttacker = NoAttacker;

        void ServerDie(ulong attacker, Vector3 point, Vector3 impulse, int segment, DeathCause cause)
        {
            Dead.Value = true;
            ClearBurns();
            OnServerKilled(attacker);
            ServerBroadcastKill(attacker, cause);
            DieRpc(point, impulse, (byte)Mathf.Clamp(segment, 0, 255));
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
        }

        // ---------------- kill feed ----------------

        /// <summary>(killer name, killer colour, victim name, victim colour, self-kill, cause). Fired on every client.</summary>
        public static event System.Action<string, int, string, int, bool, DeathCause> KillFeed;

        public virtual string FeedName => name;
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
        void DieRpc(Vector3 point, Vector3 impulse, byte segment)
        {
            if (LastRagdoll) Destroy(LastRagdoll);
            SetOutline(null); // the ragdoll copies the body's materials
            // already flopping (knocked down): that ragdoll just goes limp for good
            var live = TakeLiveRagdoll();
            LastRagdoll = live != null ? live : RagdollFactory.Spawn(this, ViewVelocity, segment, point, impulse);
            if (live != null) RagdollFactory.Push(live, impulse);
            SetVisible(false);
            OnDiedLocal();
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
            SetModelHidden(false);
            OnLiveRagdollChanged();
        }

        protected virtual void OnLiveRagdollChanged() { }

        /// <summary>Hands over a knocked-down ragdoll that is currently standing in for the body (null if none).</summary>
        GameObject TakeLiveRagdoll()
        {
            var r = _liveRagdoll;
            _liveRagdoll = null;
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
            foreach (var r in _renderers) if (r) r.enabled = on && !_modelHidden;
            Hitboxes?.SetEnabled(on);
        }

        bool _modelHidden;

        /// <summary>Hide only the meshes (hitboxes stay) while a live ragdoll stands in for the body.</summary>
        public void SetModelHidden(bool hidden)
        {
            _modelHidden = hidden;
            foreach (var r in _renderers) if (r) r.enabled = Visible && !hidden;
        }

        // ---------------- outline ----------------

        /// <summary>Every spawned combatant (players and dummies).</summary>
        public static readonly List<Combatant> Registry = new();
        Material[][] _baseMaterials;
        Material _outline;

        /// <summary>Adds (or with null removes) an outline material on the body's renderers.</summary>
        public void SetOutline(Material m)
        {
            if (m == _outline || _renderers == null) return;
            if (_baseMaterials == null)
            {
                _baseMaterials = new Material[_renderers.Length][];
                for (int i = 0; i < _renderers.Length; i++) _baseMaterials[i] = _renderers[i] ? _renderers[i].sharedMaterials : null;
            }
            _outline = m;
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                var baseMats = _baseMaterials[i];
                if (!r || baseMats == null) continue;
                if (m == null) { r.sharedMaterials = baseMats; continue; }
                var mats = new Material[baseMats.Length + 1];
                baseMats.CopyTo(mats, 0);
                mats[^1] = m;
                r.sharedMaterials = mats;
            }
        }
    }
}
