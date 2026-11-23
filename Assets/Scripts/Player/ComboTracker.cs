using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Server: counts how often (and how long) someone has been knocked down lately, for the combo break.
    /// Knocked down <see cref="SpellPlans.ComboKnockdowns"/> times, or lying ragdolled for
    /// <see cref="SpellPlans.ComboDownTime"/> s, within <see cref="SpellPlans.ComboWindow"/> s breaks the combo:
    /// they get up and can't be knocked down for <see cref="SpellPlans.ComboBreakTime"/> s.
    /// Shared by players and test dummies.
    /// </summary>
    public class ComboTracker
    {
        readonly List<float> _knockdowns = new();
        readonly List<Vector2> _downSpans = new(); // (start, end) of each stretch lying ragdolled
        bool _wasDown;
        float _unstoppableUntil;

        /// <summary>Just broke a combo: can't be knocked down (ragdolled, carried) until this runs out.</summary>
        public bool Unstoppable => Time.time < _unstoppableUntil;

        public void Reset()
        {
            _knockdowns.Clear();
            _downSpans.Clear();
            _wasDown = false;
            _unstoppableUntil = 0f;
        }

        /// <summary>Starts the unstoppable stretch (and forgets the knockdowns that led to it).</summary>
        public void Break()
        {
            Reset();
            _unstoppableUntil = Time.time + SpellPlans.ComboBreakTime;
        }

        /// <summary>A hit is about to knock them down. True when this one breaks the combo
        /// (a few hits close together count as one).</summary>
        public bool NoteKnockdown()
        {
            float now = Time.time;
            if (_knockdowns.Count > 0 && now - _knockdowns[^1] < SpellPlans.ComboHitMerge) return false;
            _knockdowns.Add(now);
            _knockdowns.RemoveAll(t => now - t > SpellPlans.ComboWindow);
            return _knockdowns.Count >= SpellPlans.ComboKnockdowns;
        }

        /// <summary>Every frame. True when they've lain ragdolled long enough within the window to break the combo.</summary>
        public bool Tick(bool alive, bool ragdolled)
        {
            if (!alive) { if (_downSpans.Count > 0 || _knockdowns.Count > 0) Reset(); return false; }
            float now = Time.time;
            bool down = ragdolled && !Unstoppable;
            if (down)
            {
                if (_wasDown && _downSpans.Count > 0) _downSpans[^1] = new Vector2(_downSpans[^1].x, now);
                else _downSpans.Add(new Vector2(now, now));
            }
            _wasDown = down;
            float from = now - SpellPlans.ComboWindow, total = 0f;
            for (int i = _downSpans.Count - 1; i >= 0; i--)
            {
                var s = _downSpans[i];
                if (s.y < from) { _downSpans.RemoveAt(i); continue; }
                total += s.y - Mathf.Max(s.x, from);
            }
            return total >= SpellPlans.ComboDownTime;
        }
    }
}
