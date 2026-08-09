using System;

namespace LethalAICrewmate
{
    /// <summary>Why Buddy might want to say something right now.</summary>
    internal enum BuddySpeechReason
    {
        /// <summary>Somebody said his name or asked him directly.</summary>
        DirectlyAddressed,
        /// <summary>An open question near him that nobody has answered.</summary>
        OpenQuestion,
        /// <summary>Unprompted colour: observations, environment, character beats.</summary>
        Unprompted,
        /// <summary>Confirmed immediate danger. Always allowed to cut in.</summary>
        Danger
    }

    /// <summary>
    /// Pure conversational-floor policy for a lobby with several talking humans. It decides when
    /// Buddy waits his turn, when he may cut in, and whom he should answer or follow.
    /// It grants no authority and performs no game action.
    /// </summary>
    internal static class BuddySocialPolicy
    {
        /// <summary>How long a human keeps the floor after speaking, in seconds.</summary>
        internal const float FloorHoldSeconds = 4.5f;

        /// <summary>How long a direct address stays "the current question" for.</summary>
        internal const float AddressWindowSeconds = 25f;

        /// <summary>Hard cap on remembered speakers. Turn-taking never needs more than this.</summary>
        internal const int MaxTrackedSpeakers = 4;

        /// <summary>
        /// Turn-taking. Danger always cuts in; a direct address waits only for a very short beat
        /// so Buddy does not step on the end of the sentence that summoned him.
        /// </summary>
        internal static bool ShouldWaitForTurn(
            BuddySpeechReason reason,
            float secondsSinceHumanSpoke,
            int humansTalkingRecently)
        {
            if (reason == BuddySpeechReason.Danger) return false;
            if (secondsSinceHumanSpoke < 0f) return false;

            if (reason == BuddySpeechReason.DirectlyAddressed)
                return secondsSinceHumanSpoke < 0.8f;

            // A busy conversation belongs to the humans in it.
            float hold = FloorHoldSeconds + Math.Max(0, humansTalkingRecently - 1) * 2.5f;
            if (reason == BuddySpeechReason.Unprompted) hold += 6f;
            return secondsSinceHumanSpoke < hold;
        }

        /// <summary>
        /// Scores a candidate for "who should Buddy answer or stay near". Higher wins.
        /// All inputs are real measurements; ties fall back to plain proximity.
        /// </summary>
        internal static int AttentionScore(
            bool addressedBuddyRecently,
            float secondsSinceTheySpoke,
            float distanceMetres,
            int relationshipAffinity,
            bool isInDanger)
        {
            int score = 0;
            if (addressedBuddyRecently) score += 400;
            if (isInDanger) score += 300;

            if (secondsSinceTheySpoke >= 0f && secondsSinceTheySpoke < 30f)
                score += (int)(120f * (1f - secondsSinceTheySpoke / 30f));

            score += Math.Max(0, 120 - (int)Math.Min(120f, Math.Max(0f, distanceMetres) * 2f));
            score += Math.Max(-60, Math.Min(60, relationshipAffinity / 3));
            return score;
        }

        /// <summary>Prompt guidance for a lobby with more than one live speaker.</summary>
        internal static string GroupDirective(int liveSpeakers, string mostRecentAsker)
        {
            if (liveSpeakers <= 1) return null;
            var line = "SOCIAL: " + liveSpeakers + " crewmates are talking near you. Answer one person, not the room. ";
            if (!string.IsNullOrWhiteSpace(mostRecentAsker))
            {
                string who = mostRecentAsker.Trim();
                if (who.Length > 32) who = who.Substring(0, 32);
                line += "The last person to actually address you was " + who + ". ";
            }
            line += "Do not repeat what a human just said, do not talk over an ongoing exchange, and stay quiet if nothing you have adds anything.";
            return line;
        }
    }
}
