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
                return userContent + "\n[RARE CHARACTER ASIDE: one subtle optional aside only if safe, relevant, and consistent with the current character arc.]";
            }
            if (_messagesSinceBeat < 1000000) _messagesSinceBeat++;
            return userContent;
        }

    }
}
