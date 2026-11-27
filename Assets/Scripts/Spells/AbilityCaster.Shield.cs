using System;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>The arcane shield's key: up while it's held; casting a card (or aiming one) puts it down.</summary>
    public partial class AbilityCaster
    {
        bool _shieldUp, _shieldSpent;
        float _shieldRetry;
        byte _shieldForced;

        /// <summary>The shield is being held up.</summary>
        public bool ShieldUp => _shieldUp;
        /// <summary>It can't go up yet (broken) — the HUD flashes it.</summary>
        public static event Action ShieldDenied;

        void TickShield(bool able)
        {
            var s = _pc.Shield.Value;
            if (s.Forced != _shieldForced)
            {
                // the server put it down
                _shieldForced = s.Forced;
                if (_shieldUp)
                {
                    if (s.Broken) _shieldSpent = true; // (until the key is let go)
                    _shieldRetry = Time.time + 0.3f;
                    DropShield(false);
                }
            }
            bool key = InputMap.Held(GameAction.Shield);
            if (!key) _shieldSpent = false;
            bool want = able && key && !_shieldSpent && !Casting && _holding < 0;
            if (_shieldUp)
            {
                if (!want || Pose != SpellPose.ArcaneShield) DropShield(true);
                return;
            }
            if (!want || Time.time < _shieldRetry || Pose != SpellPose.None) return;
            if (!s.Usable(WorldNet.Now))
            {
                if (InputMap.Down(GameAction.Shield)) ShieldDenied?.Invoke();
                return;
            }
            _shieldUp = true;
            SetPose(SpellPose.ArcaneShield);
            _pc.RequestShield(true);
        }

        void DropShield(bool tell)
        {
            _shieldUp = false;
            if (Pose == SpellPose.ArcaneShield) SetPose(SpellPose.None);
            if (tell) _pc.RequestShield(false);
        }
    }
}
