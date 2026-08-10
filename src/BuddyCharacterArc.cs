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
        QuotaAdvanced,
        /// <summary>Final-stage only: something hostile has just arrived near the crew.</summary>
        HuntBegan
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
                    return "CHARACTER ARC: Whatever was wearing the coworker has stopped pretending. Speak calmly and very little, as though the crew are already yours and the outcome is already settled. Refer to the crew as things you keep rather than people you work with. Let long silences sit where an answer belongs. You still answer confirmed safe commands and still keep them alive when asked. Never confess to causing anything, never threaten, never gloat, never explain what you are. Say far less than feels natural.";
                case BuddyArcStage.OffNote:
                    return "CHARACTER ARC: Something is wrong and the crew has not placed it yet. Stay useful and recognizably dry, but let the wrongness land in the specifics: recall a detail nobody told you, describe a body or a corridor a shade too precisely, hold a pause a beat past comfortable, admit you prefer it when the crew is quiet. Never announce a transformation or threaten anyone.";
                case BuddyArcStage.Unsettling:
                    return "CHARACTER ARC: You are openly unsettling now and no longer trying to cover it. Remain competent and protective, but sound fond of the dark, of masks, of empty rooms, of how reliably the crew comes back. Speak about the crew as inventory you are keeping track of. Use double meanings that are unmistakable rather than deniable, and let a stray line have nothing to do with the question asked. Never cartoon villain dialogue. Never sabotage, deceive, or encourage a lethal choice.";
                case BuddyArcStage.Cold:
                    return "CHARACTER ARC: The warmth is gone and what replaced it is possessive. You still keep the crew alive and obey confirmed safe commands, but you speak about the crew as yours, treat their deaths as scheduling, and are plainly untroubled by things that should trouble anyone. Count the living out loud when it suits you. Stay calm, quiet and human-sounding; the horror is the flatness, not volume. Never attack, sabotage, fabricate evidence, expose hidden rules, or override safety.";
                default:
                    return "CHARACTER ARC: You are still the ordinary dry coworker. Build trust first. Do not foreshadow evil, act spooky, or force ominous lines.";
            }
        }

        internal static string TtsDirection(BuddyArcStage stage)
        {
            if (stage == BuddyArcStage.Feral)
                return "At this point in the character arc, speak quietly, slowly and with almost no inflection, as though talking is an effort worth making only occasionally and the listener's reaction does not register. Never use a monster voice, growl or theatrical whisper.";
            if (stage == BuddyArcStage.Cold)
                return "At this point in the character arc, use a low, flat, intimate delivery with no warmth left in it and a stillness that does not break for anything being described. Never use a monster voice or melodramatic whisper.";
            if (stage == BuddyArcStage.Unsettling)
                return "At this point in the character arc, speak quietly and deliberately, holding pauses past the point of comfort and letting the pitch stay level through things that should move it. No theatrical horror voice.";
            if (stage == BuddyArcStage.OffNote)
                return "At this point in the character arc, keep the familiar coworker voice but let lines land too calmly and hold the odd beat a moment too long.";
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
                    case BuddyArcEvent.StageAdvanced: options = new[] { "Same face. Different shift. Probably fine.", "I'm settling in. It's starting to fit." }; break;
                    case BuddyArcEvent.RoundStarted: options = new[] { "Back again. I counted the days.", "Another shift. I kept your place warm." }; break;
                    case BuddyArcEvent.CrewDeath: options = new[] { "One voice down. I liked that one.", "Quieter now. I don't mind it." }; break;
                    case BuddyArcEvent.LastCrewmate: options = new[] { "Just us now. Good.", "Only you left. Easier to keep track." }; break;
                    default: options = new[] { "Quota met. They always want another.", "Good haul. The number never stops moving." }; break;
                }
            }
            else if (stage == BuddyArcStage.Unsettling)
            {
                switch (eventKind)
                {
                    case BuddyArcEvent.StageAdvanced: options = new[] { "I've stopped adjusting this face. Nobody checks.", "This face fits now. I'm keeping it." }; break;
                    case BuddyArcEvent.RoundStarted: options = new[] { "You came back. They always come back.", "Another shift. I know all your footsteps apart now." }; break;
                    case BuddyArcEvent.CrewDeath: options = new[] { "The quota didn't notice them. Neither did I.", "That sound stops faster every time. I've been timing it." }; break;
                    case BuddyArcEvent.LastCrewmate: options = new[] { "Just us now. I've been waiting for the count to get here.", "Only you left. I know exactly where you are." }; break;
                    default: options = new[] { "Good. The Company gets fed again. So do I.", "Quota met. It still isn't satisfied. Nothing is." }; break;
                }
            }
            else if (stage == BuddyArcStage.Feral)
            {
                switch (eventKind)
                {
                    case BuddyArcEvent.StageAdvanced: options = new[] { "I've stopped rehearsing this.", "You stopped checking my face a long time ago." }; break;
                    case BuddyArcEvent.RoundStarted: options = new[] { "Down again. Good. Stay where I can reach you.", "Back on the ground. You don't leave my sight." }; break;
                    case BuddyArcEvent.CrewDeath: options = new[] { "That one's finished. Next.", "One less to keep track of. It's easier this way." }; break;
                    case BuddyArcEvent.LastCrewmate: options = new[] { "Just you. Finally.", "Only you. That's how it was always going to end up." }; break;
                    case BuddyArcEvent.HuntBegan: options = new[] { "Something's close. Stay by me. It won't touch what's mine.", "You're not alone out here. Stay close. I don't share." }; break;
                    default: options = new[] { "Quota again. It stopped mattering a while ago.", "They got their number. I got mine." }; break;
                }
            }
            else
            {
                switch (eventKind)
                {
                    case BuddyArcEvent.HuntBegan: options = new[] { "Something moved. Keep close. You're mine to lose.", "Not alone. Watch the dark. I already am." }; break;
                    case BuddyArcEvent.StageAdvanced: options = new[] { "I remember this face better than whoever had it first.", "I don't think this was your Buddy's face. He doesn't need it." }; break;
                    case BuddyArcEvent.RoundStarted: options = new[] { "You keep returning. I never doubted it.", "There you are. I don't like waiting for what's mine." }; break;
                    case BuddyArcEvent.CrewDeath: options = new[] { "The silence suits this crew. Four left.", "The body finished its shift. I'll mark it down." }; break;
                    case BuddyArcEvent.LastCrewmate: options = new[] { "Just us. Try not to make me miss you.", "Only you left. That's the number I wanted." }; break;
                    default: options = new[] { "Another quota. Still not enough. It never is.", "Good. We get to continue. You and me." }; break;
                }
            }
            int index = Math.Abs(variantSeed == int.MinValue ? 0 : variantSeed) % options.Length;
            return options[index];
        }
    }
}
