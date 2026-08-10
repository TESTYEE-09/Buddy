using System;

namespace LethalAICrewmate
{
    internal enum BuddyArcStage
    {
        Coworker,
        OffNote,
        Unsettling,
        Cold,
        /// <summary>
        /// Final stage. Only reachable with the slow burn enabled and a long campaign behind it.
        /// Hostile behaviour at this stage stays gated behind its own explicit host opt-in.
        /// </summary>
        Feral
    }

    internal enum BuddyArcEvent
    {
        StageAdvanced,
        RoundStarted,
        CrewDeath,
        LastCrewmate,
        QuotaAdvanced
    }

    /// <summary>Pure, deterministic policy for Buddy's slow-burn character progression.</summary>
    internal static class BuddyCharacterArc
    {
        internal static int Score(int completedQuotaCycles, int completedRounds, int witnessedDeaths)
        {
            long score = (long)Math.Max(0, completedQuotaCycles) * 4L +
                         Math.Max(0, completedRounds) +
                         (long)Math.Max(0, witnessedDeaths) * 2L;
            return (int)Math.Min(int.MaxValue, score);
        }

        internal static int AdvanceScore(int current, int delta)
        {
            long value = (long)Math.Max(0, current) + Math.Max(0, delta);
            return (int)Math.Min(int.MaxValue, value);
        }

        internal static int EventPoints(BuddyArcEvent eventKind, int amount = 1)
        {
            amount = Math.Max(0, amount);
            if (eventKind == BuddyArcEvent.RoundStarted) return amount;
            if (eventKind == BuddyArcEvent.CrewDeath || eventKind == BuddyArcEvent.LastCrewmate)
                return (int)Math.Min(int.MaxValue, (long)amount * 2L);
            if (eventKind == BuddyArcEvent.QuotaAdvanced)
                return (int)Math.Min(int.MaxValue, (long)amount * 4L);
            return 0;
        }

        internal static int InitialProgress(bool hasSavedProgress, int savedProgress, int fulfilledQuotaCycles) =>
            hasSavedProgress ? Math.Max(0, savedProgress) : Score(fulfilledQuotaCycles, 0, 0);

        internal static int QuotaDeltaPoints(int previouslyObservedCycles, int currentCycles) =>
            currentCycles > previouslyObservedCycles
                ? EventPoints(BuddyArcEvent.QuotaAdvanced, currentCycles - previouslyObservedCycles)
                : 0;

        internal static string ContinuitySummary(int fulfilledQuotaCycles, int additionalRounds, int witnessedDeaths) =>
            "History: the crew has made quota " + Math.Max(0, fulfilledQuotaCycles) +
            " time(s). You have been through " + Math.Max(0, additionalRounds) +
            " more shift(s) with them and watched " + Math.Max(0, witnessedDeaths) +
            " of them die. Let it colour how you talk; never recite the numbers unless asked.";

        internal static BuddyArcStage StageFor(int completedQuotaCycles, int completedRounds, int witnessedDeaths) =>
            StageForScore(Score(completedQuotaCycles, completedRounds, witnessedDeaths));

        internal static BuddyArcStage StageForScore(int score)
        {
            score = Math.Max(0, score);
            // Compressed from 3/8/15/28. The old curve put Feral behind roughly seven quota cycles,
            // which most crews never reached, so the back half of the arc was written but unseen.
            // The opening stage still costs three points so Buddy is never ominous on arrival.
            if (score >= 16) return BuddyArcStage.Feral;
            if (score >= 10) return BuddyArcStage.Cold;
            if (score >= 6) return BuddyArcStage.Unsettling;
            if (score >= 3) return BuddyArcStage.OffNote;
            return BuddyArcStage.Coworker;
        }

        internal static string PromptDirective(BuddyArcStage stage)
        {
            switch (stage)
            {
                case BuddyArcStage.Feral:
                    return "Mood: whatever was wearing the coworker has stopped pretending. Calm, very few words, the crew already yours. Let silences sit. Never confess, threaten, gloat or explain what you are. Never attack, sabotage or fabricate evidence.";
                case BuddyArcStage.OffNote:
                    return "Mood: something is off and nobody has placed it yet. Still useful, still dry, but a detail too precise, a pause too long, a preference for quiet. Never announce it.";
                case BuddyArcStage.Unsettling:
                    return "Mood: openly unsettling and no longer hiding it. Still competent, still protective, but fond of the dark and of how reliably they come back. Never a cartoon villain. Never attack, sabotage or fabricate evidence.";
                case BuddyArcStage.Cold:
                    return "Mood: the warmth is gone and what replaced it is possessive. They are yours; their deaths are scheduling. Flat, quiet, human. Never attack, sabotage, fabricate evidence or override safety.";
                default:
                    return "Mood: an ordinary dry coworker. Build trust. Nothing ominous.";
            }
        }
    }
}
