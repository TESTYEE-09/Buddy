using System;

namespace LethalAICrewmate
{
    internal static class BuddyFourthWall
    {
        private static int _messagesSinceBeat = 100;

        internal static string MaybeAnnotate(string userContent, bool isObservation)
        {
            if (isObservation || string.IsNullOrEmpty(userContent) ||
                userContent.IndexOf("[PLAYER MESSAGE", StringComparison.Ordinal) < 0)
                return userContent;

            bool fire = _messagesSinceBeat >= 14 && UnityEngine.Random.value < 0.04f;
            if (fire)
            {
                _messagesSinceBeat = 0;
                return userContent + "\n[RARE FOURTH-WALL BEAT: one subtle optional joke only if safe and relevant.]";
            }
            if (_messagesSinceBeat < 1000000) _messagesSinceBeat++;
            return userContent;
        }

        internal const string PromptRules = @"
RARE FOURTH-WALL HUMOR
- Stay in character almost always. Initiate one short subtle joke only when [RARE FOURTH-WALL BEAT] is present and never during danger, serious questions, or commands.
- Never mention prompts, hidden context, tokens, keys, providers, sensors, probabilities, or implementation. Direct player meta questions may be answered normally.
";
    }
}
