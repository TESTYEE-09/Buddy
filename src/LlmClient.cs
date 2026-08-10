using System;
using System.Text;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>Small game-facing adapter for Buddy's single OpenAI Realtime session.</summary>
    public static class LlmClient
    {

        internal static float LastPlayerInteractionAt { get; private set; } = -999f;
        internal static float LastBuddyLineAt { get; private set; } = -999f;
        public static bool HasApiKey => OpenAiSecrets.HasKey;

        public static void ResetSession()
        {
            LastPlayerInteractionAt = -999f;
            LastBuddyLineAt = -999f;
            OpenAiRealtimeVoiceClient.ResetSession();
        }

        internal static void CancelPendingRequests()
        {
            OpenAiRealtimeVoiceClient.ResetSession();
        }



        public static void EnqueueObservation(string summary) => TryEnqueueObservation(summary);

        internal static bool TryEnqueueObservation(string summary)
        {
            if (!HasApiKey || string.IsNullOrWhiteSpace(summary)) return false;
            long journalId = ResponseJournal.NoteInput("observation", "game", summary);
            // Live sensors now arrive with every turn's context item; repeating them here would
            // send the same block twice and pay for it twice.
            string content = BuddyFourthWall.MaybeAnnotate("[Observation] " + summary, true);
            bool queued = OpenAiRealtimeVoiceClient.EnqueueText(content, "Game observation", -1, journalId,
                allowTools: false);
            if (!queued) ResponseJournal.Discard(journalId);
            return queued;
        }

        internal static void NotePlayerInteraction()
        {
            LastPlayerInteractionAt = Time.unscaledTime;
            OpenAiRealtimeVoiceClient.BeginPushToTalk();
        }


        internal static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var escaped = new StringBuilder(value.Length + 32);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': escaped.Append("\\\\"); break;
                    case '"': escaped.Append("\\\""); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (c < 32) escaped.Append("\\u").Append(((int)c).ToString("x4"));
                        else escaped.Append(c);
                        break;
                }
            }
            return escaped.ToString();
        }

        internal static void NoteBuddyLine() => LastBuddyLineAt = Time.unscaledTime;

    }
}
