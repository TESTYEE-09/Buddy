using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Host-only conversational awareness for multiplayer lobbies: who spoke, who addressed Buddy,
    /// how busy the floor is, and whom he should answer or stay near.
    ///
    /// Speaker identity always comes from the host's own resolved player list. Nothing here reads
    /// a client-supplied identifier or grants any authority, so it cannot widen the remote-action
    /// trust boundary. At most four speakers are remembered, in memory only.
    /// </summary>
    internal static class BuddySocialIntelligence
    {
        private sealed class SpeakerRecord
        {
            internal int PlayerId;
            internal string Name;
            internal float LastSpokeAt;
            internal float LastAddressedBuddyAt;
        }

        private static readonly List<SpeakerRecord> Speakers = new List<SpeakerRecord>();
        private static float _lastAnyHumanSpokeAt = -999f;
        private static string _lastAskerName;

        internal static bool Active => Plugin.SocialAwareness?.Value == true && CrewmateSpawner.IsHost();

        /// <summary>Records a real, host-observed utterance. Name comes from the host player list.</summary>
        internal static void NoteSpeech(int playerId, string playerName, bool addressedBuddy)
        {
            if (!Active) return;
            try
            {
                float now = Time.unscaledTime;
                _lastAnyHumanSpokeAt = now;

                SpeakerRecord record = Find(playerId);
                if (record == null)
                {
                    record = new SpeakerRecord { PlayerId = playerId };
                    if (Speakers.Count >= BuddySocialPolicy.MaxTrackedSpeakers)
                        Speakers.RemoveAt(OldestIndex());
                    Speakers.Add(record);
                }

                record.Name = string.IsNullOrWhiteSpace(playerName) ? record.Name : playerName;
                record.LastSpokeAt = now;
                if (addressedBuddy)
                {
                    record.LastAddressedBuddyAt = now;
                    _lastAskerName = record.Name;
                    BuddyRelationships.Note(record.Name, BuddyRelationEvent.Conversation);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("Buddy social note: " + ex.Message);
            }
        }

        /// <summary>Turn-taking gate. Returns true when Buddy should hold this line back for now.</summary>
        internal static bool ShouldWaitForTurn(BuddySpeechReason reason)
        {
            if (!Active) return false;
            float since = Time.unscaledTime - _lastAnyHumanSpokeAt;
            return BuddySocialPolicy.ShouldWaitForTurn(reason, since, RecentSpeakerCount());
        }

        internal static string PromptLine()
        {
            if (!Active) return null;
            return BuddySocialPolicy.GroupDirective(RecentSpeakerCount(), _lastAskerName);
        }

        private static int RecentSpeakerCount()
        {
            int count = 0;
            float now = Time.unscaledTime;
            foreach (SpeakerRecord record in Speakers)
                if (now - record.LastSpokeAt <= 30f) count++;
            return count;
        }

        private static SpeakerRecord Find(int playerId)
        {
            foreach (SpeakerRecord record in Speakers)
                if (record.PlayerId == playerId) return record;
            return null;
        }

        private static int OldestIndex()
        {
            int oldest = 0;
            for (int i = 1; i < Speakers.Count; i++)
                if (Speakers[i].LastSpokeAt < Speakers[oldest].LastSpokeAt) oldest = i;
            return oldest;
        }

        /// <summary>
        /// Chooses whom Buddy should stay near. Falls back to the caller's nearest-player result
        /// whenever social awareness is off or nothing scores meaningfully better.
        /// </summary>
        internal static PlayerControllerB ChooseAttentionTarget(CrewmateData data, PlayerControllerB fallback)
        {
            try
            {
                if (!Active || data?.Enemy == null) return fallback;
                PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
                if (players == null) return fallback;

                Vector3 origin = data.Enemy.transform.position;
                float now = Time.unscaledTime;
                PlayerControllerB best = fallback;
                int bestScore = int.MinValue;

                foreach (PlayerControllerB player in players)
                {
                    if (player == null || !player.isPlayerControlled || player.isPlayerDead) continue;

                    float distance = Vector3.Distance(origin, player.transform.position);
                    SpeakerRecord record = Find((int)player.playerClientId);
                    float sinceSpoke = record == null ? -1f : now - record.LastSpokeAt;
                    bool addressed = record != null &&
                                     record.LastAddressedBuddyAt > 0f &&
                                     now - record.LastAddressedBuddyAt <= BuddySocialPolicy.AddressWindowSeconds;

                    int score = BuddySocialPolicy.AttentionScore(
                        addressed,
                        sinceSpoke,
                        distance,
                        BuddyRelationships.AffinityFor(player.playerUsername),
                        player.health < 40);

                    if (score > bestScore) { bestScore = score; best = player; }
                }

                return best ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        internal static void ResetSession()
        {
            Speakers.Clear();
            _lastAnyHumanSpokeAt = -999f;
            _lastAskerName = null;
        }
    }
}
