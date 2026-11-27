using Unity.Netcode;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The arcane shield's health (everyone's; a defense dummy's too). Who raises it: PlayerController.Shield, TestDummy.Kinds. What it stops: SpellServer.Shield.</summary>
    public abstract partial class Combatant
    {
        public readonly NetworkVariable<ShieldState> Shield = new(ShieldState.Full);

        /// <summary>Server: up, and nothing has them down or in its grip.</summary>
        public bool ShieldHeld => Shield.Value.Raised && Shield.Value.At(WorldNet.Now) > 0f && CanHoldShield;

        protected virtual bool CanHoldShield => IsAlive && (ViewFlags & NetState.Ragdoll) == 0 && !Trapped && !Grasped && !Hexed && !Pinned
            && !Status.Value.Taken(WorldNet.Now) && !SpellServer.InEnemyEmpress(this); // (no shield in someone else's Empress circle)

        /// <summary>Server: up it goes, with what it has.</summary>
        protected void ServerRaiseShield()
        {
            var s = Shield.Value;
            double now = WorldNet.Now;
            s.Value = s.At(now);
            s.Time = now;
            s.RegenAt = 0;
            s.Raised = true;
            s.Broken = false;
            Shield.Value = s;
        }

        /// <summary>Server: put it down (<paramref name="forced"/>: not by its owner's choice).</summary>
        public void ServerLowerShield(bool forced)
        {
            var s = Shield.Value;
            if (!IsServer || !s.Raised) return;
            double now = WorldNet.Now;
            s.Value = s.At(now);
            s.Time = now;
            s.RegenAt = now + SpellPlans.ShieldRegenDelay;
            s.Raised = false;
            if (forced) s.Forced++;
            Shield.Value = s;
        }

        /// <summary>Server: as good as new (a new round, a kill). <paramref name="lower"/>: and put down, if it was up.</summary>
        public void ServerRefillShield(bool lower)
        {
            if (!IsServer) return;
            var s = Shield.Value;
            s.Value = s.Max;
            s.Time = WorldNet.Now;
            s.RegenAt = 0;
            s.Broken = false;
            if (s.Raised && lower)
            {
                s.Raised = false;
                s.Forced++;
            }
            Shield.Value = s;
        }

        int _absorbFrame = -1;
        Combatant _absorbBy;

        /// <summary>Server: the shield has just (this frame) taken the whole of a hit of theirs.</summary>
        public bool ServerAbsorbedNow(Combatant by) => _absorbFrame == Time.frameCount && _absorbBy == by;

        /// <summary>Server: the arcane shield takes what it can of this; what's left for the body.</summary>
        public virtual float ServerAbsorb(float dmg, Combatant by)
        {
            if (!IsServer || !Shield.Value.Raised) return dmg;
            double now = WorldNet.Now;
            float hp = Shield.Value.At(now), took = Mathf.Min(hp, dmg), left = dmg - took;
            if (left < 0.01f) left = 0f;
            _absorbFrame = left <= 0f ? Time.frameCount : -1;
            _absorbBy = by;
            SetShield(hp - took, now);
            ShieldHitRpc(!Shield.Value.Raised, Mathf.Clamp01(took / Shield.Value.Max));
            return left;
        }

        void SetShield(float hp, double now)
        {
            var s = Shield.Value;
            s.Value = Mathf.Max(0f, hp);
            s.Time = now;
            s.RegenAt = now + SpellPlans.ShieldRegenDelay;
            if (hp < 0.01f)
            {
                s.Value = 0f;
                s.Raised = false;
                s.Broken = true;
                s.Forced++;
            }
            Shield.Value = s;
        }

        /// <summary>Server, every frame: it runs out; or whatever has hold of them puts it down.</summary>
        public virtual void ServerTickShield(double now)
        {
            if (!Shield.Value.Raised) return;
            if (Shield.Value.At(now) <= 0f)
            {
                SetShield(0f, now);
                ShieldHitRpc(true, 0f);
            }
            else if (!CanHoldShield) ServerLowerShield(true);
        }

        [Rpc(SendTo.Everyone)]
        void ShieldHitRpc(bool broke, float strength)
        {
            var view = GetComponent<ShieldView>();
            if (view != null) view.Struck(broke, strength);
        }
    }
}
