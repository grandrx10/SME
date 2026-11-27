using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// What a practice dummy (K, U picks which) does: nothing; holds the arcane shield out in front of it (it doesn't
    /// drain, only breaks, and is back whole once neither it nor the dummy has been hurt for 2 s); or throws a Ball of
    /// Flame straight ahead every 5 s.
    /// </summary>
    public partial class TestDummy
    {
        public enum DummyKind : byte { Plain, Defense, Attack }
        public const int DummyKinds = 3;

        readonly NetworkVariable<DummyKind> _kind = new();
        /// <summary>When it last began a fireball (for the gesture).</summary>
        readonly NetworkVariable<double> _castAt = new(-100);
        double _hurtAt = -100, _nextShot;

        /// <summary>Server: what this one does, from now on.</summary>
        public void ServerSetKind(DummyKind kind)
        {
            _kind.Value = kind;
            _nextShot = WorldNet.Now + SpellPlans.AttackDummyEvery;
            _hurtAt = -100;
            if (kind == DummyKind.Defense) ServerRefillShield(false);
        }

        /// <summary>On its feet, with nothing holding it: able to shield or throw.</summary>
        protected override bool CanHoldShield => base.CanHoldShield && !_ragdoll && !_carried && !IsUndead && !_corpse.Value;

        /// <summary>Server: can finish (or start) its fireball.</summary>
        public bool CanThrow => CanHoldShield;

        const float CastGesture = 0.95f;

        public override SpellPose ViewPose
        {
            get
            {
                if (!IsAlive || (ViewFlags & NetState.Ragdoll) != 0) return SpellPose.None;
                if (Shield.Value.Raised) return SpellPose.ArcaneShield;
                return WorldNet.Now - _castAt.Value < CastGesture ? SpellPose.FlameCharge : SpellPose.None;
            }
        }

        public override float ViewPoseTime => Shield.Value.Raised ? 1f : (float)(WorldNet.Now - _castAt.Value);

        protected override void OnServerDamaged(float amount) => _hurtAt = WorldNet.Now;

        public override float ServerAbsorb(float dmg, Combatant by)
        {
            _hurtAt = WorldNet.Now;
            return base.ServerAbsorb(dmg, by);
        }

        public override void ServerTickShield(double now)
        {
            base.ServerTickShield(now);
            if (_kind.Value == DummyKind.Defense) Defend(now);
            else if (_kind.Value == DummyKind.Attack) Attack(now);
        }

        void Defend(double now)
        {
            if (!CanHoldShield || now < _hurtAt + SpellPlans.DefenseDummyBack) return;
            var s = Shield.Value;
            if (s.Raised && s.Still && s.Value >= SpellPlans.ShieldHealth) return;
            s.Still = true;
            s.Value = SpellPlans.ShieldHealth;
            s.Time = now;
            s.Raised = true;
            s.Broken = false;
            Shield.Value = s;
        }

        void Attack(double now)
        {
            if (now < _nextShot) return;
            if (!CanThrow) return; // (as soon as it's back up)
            _nextShot = now + SpellPlans.AttackDummyEvery;
            _castAt.Value = now;
            SpellServer.DummyFireball(this);
        }
    }
}
