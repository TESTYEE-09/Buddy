using System;

namespace LethalAICrewmate
{
    /// <summary>
    /// Single source of truth for Buddy's two deliberately separate AI pipelines.
    /// Model IDs are not user-facing tuning knobs: changing one requires a release and tests.
    /// </summary>
    internal static class BuddyAiArchitecture
    {
        internal const string OpenAiProvider = "OpenAI";
        internal const string GroqProvider = "Groq";

        internal const string OpenAiRealtimeModel = "gpt-realtime-2.1-mini";
        internal const string OpenAiTranscriptionModel = "gpt-live-transcribe";

        internal const string GroqChatModel = "qwen/qwen3.6-27b";
        internal const string GroqTranscriptionModel = "whisper-large-v3-turbo";
        internal const string GroqSpeechModel = "canopylabs/orpheus-v1-english";

        internal static string NormalizeProvider(string value) =>
            string.Equals(value?.Trim(), GroqProvider, StringComparison.OrdinalIgnoreCase)
                ? GroqProvider
                : OpenAiProvider;

        internal static bool IsOpenAi(string value) =>
            string.Equals(NormalizeProvider(value), OpenAiProvider, StringComparison.Ordinal);

    }
}
