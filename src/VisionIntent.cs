using System;

namespace LethalAICrewmate
{
    internal static class VisionIntent
    {
        private static readonly string[] Phrases =
        {
            "what am i looking at", "what i'm looking at", "what am i staring at",
            "what i'm staring at", "what can you see", "what do you see",
            "what is on my screen", "what's on my screen", "look at my screen",
            "look at this", "can you see this", "can you see my screen", "screenshot",
            "in front of me", "what is this thing", "what's this thing", "identify this"
        };

        internal static bool IsVisualQuestion(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;
            string text = message.Trim().ToLowerInvariant();
            for (int i = 0; i < Phrases.Length; i++)
                if (text.Contains(Phrases[i])) return true;
            return false;
        }
    }
}
