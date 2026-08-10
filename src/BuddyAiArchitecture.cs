using System;

namespace LethalAICrewmate
{
    /// <summary>
    /// Single source of truth for Buddy's OpenAI Realtime brain.
    /// The model ID is release-pinned rather than exposed as a user tuning knob.
    /// </summary>
    internal static class BuddyAiArchitecture
    {
        internal const string OpenAiRealtimeModel = "gpt-realtime-2.1-mini";

        /// <summary>Voices OpenAI Realtime accepts for this model. Release-owned list; the
        /// selected voice is a user setting, but anything outside this set is rejected.</summary>
        internal static readonly string[] RealtimeVoices =
        {
            "alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse"
        };

        internal const string DefaultRealtimeVoice = "ash";

        /// <summary>
        /// Reasoning effort accepted for this model, cheapest and fastest first. Higher settings buy
        /// better tool decisions with noticeably longer silence before Buddy answers, and reasoning
        /// shares the response token budget with his speech, so this is a real speed/quality dial.
        /// </summary>
        internal static readonly string[] ReasoningEfforts =
        {
            "minimal", "low", "medium", "high"
        };

        internal const string DefaultReasoningEffort = "low";

        /// <summary>Maps a config value to a valid reasoning effort, falling back to low.</summary>
        internal static string SanitizeReasoningEffort(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DefaultReasoningEffort;
            string candidate = value.Trim().ToLowerInvariant();
            foreach (string allowed in ReasoningEfforts)
                if (string.Equals(allowed, candidate, StringComparison.Ordinal)) return allowed;
            return DefaultReasoningEffort;
        }

        /// <summary>Maps a config value to a valid Realtime voice, falling back to Ash.</summary>
        internal static string SanitizeRealtimeVoice(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DefaultRealtimeVoice;
            string candidate = value.Trim().ToLowerInvariant();
            foreach (string allowed in RealtimeVoices)
                if (string.Equals(allowed, candidate, StringComparison.Ordinal)) return allowed;
            return DefaultRealtimeVoice;
        }
    }
}
