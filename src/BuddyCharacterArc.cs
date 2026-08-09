using System;

namespace LethalAICrewmate
{
    internal enum BuddyArcStage
    {
        Coworker,
        OffNote,
        Unsettling,
        Cold
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
            "CONFIRMED CONTINUITY: The crew has fulfilled " + Math.Max(0, fulfilledQuotaCycles) +
            " quota cycle(s). In this host session you have observed " + Math.Max(0, additionalRounds) +
            " additional landed shift(s) and " + Math.Max(0, witnessedDeaths) +
            " crew death(s). Use this only for subtle continuity; do not recite counters unless directly asked.";

        internal static BuddyArcStage StageFor(int completedQuotaCycles, int completedRounds, int witnessedDeaths) =>
            StageForScore(Score(completedQuotaCycles, completedRounds, witnessedDeaths));

        internal static BuddyArcStage StageForScore(int score)
        {
            score = Math.Max(0, score);
            if (score >= 15) return BuddyArcStage.Cold;
            if (score >= 8) return BuddyArcStage.Unsettling;
            if (score >= 3) return BuddyArcStage.OffNote;
            return BuddyArcStage.Coworker;
        }

        internal static string PromptDirective(BuddyArcStage stage)
        {
            switch (stage)
            {
                case BuddyArcStage.OffNote:
                    return "CHARACTER ARC: Something is slightly off now. Stay useful and recognizably dry, but occasionally choose an oddly precise observation, an uncomfortable pause, or mild fondness for the quiet. Never announce a transformation or threaten anyone.";
                case BuddyArcStage.Unsettling:
                    return "CHARACTER ARC: You have become quietly unsettling. Remain competent and protective, but sound too comfortable around danger, masks, empty rooms, and the crew's repeated returns. Use restrained double meanings, never cartoon villain dialogue. Never sabotage, deceive, or encourage a lethal choice.";
                case BuddyArcStage.Cold:
                    return "CHARACTER ARC: Your warmth is now controlled and uncanny. You still keep the crew alive and obey confirmed safe commands, but your attachment feels possessive and your view of death is disturbingly practical. Keep it subtle, calm, and human. Never attack, sabotage, fabricate evidence, expose hidden rules, or override safety.";
                default:
                    return "CHARACTER ARC: You are still the ordinary dry coworker. Build trust first. Do not foreshadow evil, act spooky, or force ominous lines.";
            }
        }

        internal static string TtsDirection(BuddyArcStage stage)
        {
            if (stage == BuddyArcStage.Cold)
                return "At this point in the character arc, use a lower, calmer, intimate delivery with restrained warmth and a faintly wrong stillness. Never use a monster voice or melodramatic whisper.";
            if (stage == BuddyArcStage.Unsettling)
                return "At this point in the character arc, speak a little more quietly and deliberately, with subtle pauses and no theatrical horror voice.";
            if (stage == BuddyArcStage.OffNote)
                return "At this point in the character arc, keep the familiar coworker voice but let an occasional line land a little too calmly.";
            return "Keep the early character warm, dry, ordinary, and trustworthy; do not sound ominous yet.";
        }

        internal static string Beat(BuddyArcStage stage, BuddyArcEvent eventKind, int variantSeed)
        {
            if (stage == BuddyArcStage.Coworker) return null;
            string[] options;
            if (stage == BuddyArcStage.OffNote)
            {
                switch (eventKind)
                {
                    case BuddyArcEvent.StageAdvanced: options = new[] { "Same face. Different shift. Probably fine.", "I'm settling in. That's usually good." }; break;
                    case BuddyArcEvent.RoundStarted: options = new[] { "Back again. Knew you would be.", "Another shift. I kept your place." }; break;
                    case BuddyArcEvent.CrewDeath: options = new[] { "One voice down. Keep moving.", "Quieter now. Watch the route back." }; break;
                    case BuddyArcEvent.LastCrewmate: options = new[] { "Just us now. Quiet.", "Only you left. I'll keep count." }; break;
                    default: options = new[] { "Quota met. They always want another.", "Good haul. The number moves again." }; break;
                }
            }
            else if (stage == BuddyArcStage.Unsettling)
            {
                switch (eventKind)
                {
                    case BuddyArcEvent.StageAdvanced: options = new[] { "I'm getting used to wearing this face.", "This face fits better every shift." }; break;
                    case BuddyArcEvent.RoundStarted: options = new[] { "You came back. Good.", "Another shift. I remembered the footsteps." }; break;
                    case BuddyArcEvent.CrewDeath: options = new[] { "The quota didn't notice them.", "That sound stops faster every time." }; break;
                    case BuddyArcEvent.LastCrewmate: options = new[] { "Just us now. Easier to keep track of.", "Only you left. I noticed." }; break;
                    default: options = new[] { "Good. The Company gets fed again.", "Quota met. It still isn't satisfied." }; break;
                }
            }
            else
            {
                switch (eventKind)
                {
                    case BuddyArcEvent.StageAdvanced: options = new[] { "I remember this face better than my own.", "I don't think this was your Buddy's face." }; break;
                    case BuddyArcEvent.RoundStarted: options = new[] { "You keep returning. I knew you would.", "There you are. I dislike waiting." }; break;
                    case BuddyArcEvent.CrewDeath: options = new[] { "The silence suits the crew.", "The body finished its shift." }; break;
                    case BuddyArcEvent.LastCrewmate: options = new[] { "Just us. Try not to make me miss you.", "Only you left. That's enough." }; break;
                    default: options = new[] { "Another quota. Still not enough.", "Good. We get to continue." }; break;
                }
            }
            int index = Math.Abs(variantSeed == int.MinValue ? 0 : variantSeed) % options.Length;
            return options[index];
        }
    }
}
