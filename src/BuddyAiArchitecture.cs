namespace LethalAICrewmate
{
    /// <summary>
    /// Single source of truth for Buddy's OpenAI Realtime brain.
    /// The model ID is release-pinned rather than exposed as a user tuning knob.
    /// </summary>
    internal static class BuddyAiArchitecture
    {
        internal const string OpenAiRealtimeModel = "gpt-realtime-2.1-mini";
    }
}
