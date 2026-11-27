using Unity.Netcode;

namespace SuddenMageEncounter
{
    /// <summary>A player's arcane shield (right click): raising and lowering it. Its health: Combatant.Shield.</summary>
    public partial class PlayerController
    {
        protected override bool CanHoldShield => base.CanHoldShield && MatchManager.CastingAllowedFor(this);

        /// <summary>Owner: the shield goes up, or down.</summary>
        public void RequestShield(bool up) => ShieldRpc(up);

        [Rpc(SendTo.Server)]
        void ShieldRpc(bool up)
        {
            if (!up) { ServerLowerShield(false); return; }
            double now = WorldNet.Now;
            var s = Shield.Value;
            if (s.Raised) return;
            if (!CanHoldShield || now < _serverCastingUntil - 0.15 || SpellServer.BusyChanneling(this) || SpellServer.InBubble(this) || !s.Usable(now, 0.5f))
            {
                s.Forced++; // no: their end lets go again
                Shield.Value = s;
                return;
            }
            ServerRaiseShield();
        }
    }
}
