using System;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>Routes all generated speech through Buddy's single native Realtime voice.</summary>
    public static class BuddyTts
    {
        internal static void ResetSession()
        {
            OpenAiRealtimeVoiceClient.ResetSession();
        }

        internal static void DropQueuedSpeech()
        {
            OpenAiRealtimeVoiceClient.BeginPushToTalk();
        }

        public static void Speak(string text, Vector3 worldPos)
        {
            try
            {
                if (Plugin.TtsEnabled?.Value != true || string.IsNullOrWhiteSpace(text) || !OpenAiSecrets.HasKey) return;
                string cleaned = text.Trim();
                if (cleaned.StartsWith("[shout] ", StringComparison.OrdinalIgnoreCase))
                    cleaned = cleaned.Substring(8).TrimStart();
                OpenAiRealtimeVoiceClient.EnqueueExactSpeech(cleaned);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("Buddy Realtime speech: " + ex.Message);
            }
        }
    }
}
