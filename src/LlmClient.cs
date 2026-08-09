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

        internal static void Tick() { }


        public static void EnqueueObservation(string summary) => TryEnqueueObservation(summary);

        internal static bool TryEnqueueObservation(string summary)
        {
            if (!HasApiKey || string.IsNullOrWhiteSpace(summary)) return false;
            string sensors = GameSensors.BuildLiveContext();
            long journalId = ResponseJournal.NoteInput("observation", "game", summary);
            ResponseJournal.RecordContext(journalId, sensors);
            string content = BuddyFourthWall.MaybeAnnotate(sensors + "\n[Observation] " + summary, true);
            bool queued = OpenAiRealtimeVoiceClient.EnqueueText(content, "Game observation", -1, journalId,
                includeScreenshot: false, allowTools: false);
            if (!queued) ResponseJournal.Discard(journalId);
            return queued;
        }

        internal static void NotePlayerInteraction()
        {
            LastPlayerInteractionAt = Time.unscaledTime;
            BuddyTts.DropQueuedSpeech();
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

        internal static void PublishLocalReply(string display, long journalId = 0)
        {
            if (string.IsNullOrWhiteSpace(display)) return;
            Publish(display, journalId, null);
        }

        internal static void PublishCharacterBeat(string display, string evidence)
        {
            if (string.IsNullOrWhiteSpace(display)) return;
            Publish(display, 0, evidence);
        }

        private static void Publish(string display, long journalId, string evidence)
        {
            var primary = CrewmateRegistry.GetPrimary();
            Vector3 position = primary?.Enemy != null ? primary.Enemy.transform.position : Vector3.zero;
            ulong networkId = primary?.NetworkObjectId ?? 0;
            string name = Plugin.CrewmateName?.Value ?? "Buddy";
            NetMessenger.BroadcastCrewmateChat(name, display, position, networkId);
            ProximityChat.TryShowLocal(name, display, position);
            BuddyTts.Speak(display, position + Vector3.up * 1.6f);
            NoteBuddyLine();
            if (evidence == null) ResponseJournal.RecordReply(journalId, display);
            else ResponseJournal.RecordDirect("character", "system", evidence, display);
        }
    }
}
