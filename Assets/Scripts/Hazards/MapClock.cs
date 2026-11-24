namespace SuddenMageEncounter
{
    /// <summary>
    /// The clock an arena's hazards run on, the same on every machine, so anything that only depends on time (the water's
    /// height, when a train comes) needs no messages at all. In a match: seconds since FIGHT. In a practice chamber: off,
    /// until its player sets the hazards off (<see cref="Arena.practiceHazards"/>); then they all start there and then,
    /// with no waiting.
    /// </summary>
    public static class MapClock
    {
        static bool On => MatchManager.Current.fightStart > 0 &&
                          MatchManager.Current.phase is MatchPhase.Countdown or MatchPhase.Playing or MatchPhase.RoundOver or MatchPhase.Intermission;

        const double Off = -1e6;

        /// <summary>Set off by hand in a practice chamber: skip every hazard's wait.</summary>
        public static bool ByHand(Arena a) => a != null && a.practiceHazards > 0;

        static bool InMatch(Arena a) => a != null && Arena.Current == a && On;

        /// <summary>Are this arena's hazards on a clock at all?</summary>
        public static bool Live(Arena a) => ByHand(a) || InMatch(a);

        /// <summary>Server time the clock started (FIGHT, or when they were set off; 0 = not).</summary>
        public static double StartOf(Arena a) => ByHand(a) ? a.practiceHazards : InMatch(a) ? MatchManager.Current.fightStart : 0;

        /// <summary>Seconds since a hazard that waits <paramref name="wait"/> set off, stopping for good once there's a winner (negative: not yet).</summary>
        public static float Since(Arena a, float wait)
        {
            if (ByHand(a)) return (float)(WorldNet.Now - a.practiceHazards);
            if (!InMatch(a)) return (float)Off;
            var s = MatchManager.Current;
            return (float)((s.fightEnd > 0 ? s.fightEnd : WorldNet.Now) - s.fightStart) - wait;
        }

        /// <summary>Seconds on the clock, carrying on after the round's been won (what's already under way finishes).</summary>
        public static double RunTime(Arena a) => Live(a) ? WorldNet.Now - StartOf(a) : Off;

        /// <summary>When (on this clock) the round was won: nothing new starts after it.</summary>
        public static double EndTime(Arena a) =>
            !ByHand(a) && MatchManager.Current.fightEnd > 0 ? MatchManager.Current.fightEnd - MatchManager.Current.fightStart : double.MaxValue;

        /// <summary>Hazards may start things now: the round is on with no winner yet (or they were set off by hand).</summary>
        public static bool Fighting(Arena a) =>
            ByHand(a) || InMatch(a) && MatchManager.Current.fightEnd <= 0 && WorldNet.Now >= MatchManager.Current.fightStart;
    }
}
