using System;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace LethalAICrewmate
{
    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.AddTextToChatOnServer))]
    internal static class Patch_AddTextToChatOnServer
    {
        [HarmonyPostfix]
        private static void Postfix(string chatMessage, int playerId)
        {
            try
            {
                ChatObserver.OnServerChat(chatMessage, playerId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"AddTextToChatOnServer patch: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.AddPlayerChatMessageServerRpc))]
    internal static class Patch_AddPlayerChatMessageServerRpc
    {
        [HarmonyPostfix]
        private static void Postfix(string chatMessage, int playerId)
        {
            try
            {
                // Host receives ServerRpc; observe here as a belt-and-suspenders path.
                if (CrewmateSpawner.IsHost())
                    ChatObserver.OnServerChat(chatMessage, playerId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"AddPlayerChatMessageServerRpc patch: {ex}");
            }
        }
    }

    public static class ChatObserver
    {
        private static string _lastMessage;
        private static int _lastPlayerId = int.MinValue;
        private static float _lastMessageTime;

        public static void OnServerChat(string chatMessage, int playerId)
        {
            if (!CrewmateSpawner.IsHost()) return;
            if (string.IsNullOrWhiteSpace(chatMessage)) return;
            if (Plugin.Enabled != null && !Plugin.Enabled.Value) return;
            if (!CrewmateSpawner.CanTalkToBuddy) return;

            // Both Harmony hooks can see the same server chat event. De-dupe only the same
            // player's same message, so two people saying "buddy follow" together are not merged.
            if (playerId == _lastPlayerId && chatMessage == _lastMessage && Time.time - _lastMessageTime < 0.25f)
                return;
            _lastPlayerId = playerId;
            _lastMessage = chatMessage;
            _lastMessageTime = Time.time;

            var name = Plugin.CrewmateName?.Value ?? "Buddy";
            var msg = chatMessage.Trim();
            var lower = msg.ToLowerInvariant();
            var nameLower = name.ToLowerInvariant();

            // Ignore system/join messages before conversational handling.
            if (lower.Contains("joined the") || lower.Contains("left the") ||
                lower.Contains("was kicked") || playerId < 0)
                return;

            // Any real player chat means the crew is already talking; optional Buddy chatter waits.
            LlmClient.NotePlayerInteraction();
            Plugin.Log?.LogDebug($"Chat observed (playerId={playerId}, chars={msg.Length}).");

            bool addressed =
                lower.StartsWith(nameLower) ||
                lower.StartsWith("buddy") ||
                lower.Contains(nameLower) ||
                lower.Contains("buddy");

            // Conversational bookkeeping. The speaker is resolved from the host's own player list,
            // never from anything the message itself claims.
            string speakerName = GetPlayerName(playerId);
            BuddySocialIntelligence.NoteSpeech(playerId, speakerName, addressed);
            if (addressed) BuddyRelationships.NoteAddressing(speakerName);

            // Natural-language action selection belongs to gpt-realtime-2.1-mini. The host exposes
            // typed in-game tools and returns their real results; no phrase parser runs here.
            bool shouldReply = addressed;
            if (!shouldReply && msg.TrimEnd().EndsWith("?"))
            {
                var data = CrewmateRegistry.GetPrimary();
                var player = GetPlayerById(playerId);
                if (data?.Enemy != null && player != null)
                {
                    float range = Plugin.ChatTriggerRange?.Value ?? 25f;
                    float dist = Vector3.Distance(data.Enemy.transform.position, player.transform.position);
                    if (range <= 0f || dist <= range)
                        shouldReply = true;
                }
            }

            if (shouldReply)
            {
                if (CrewmateRegistry.GetPrimary() == null && !string.IsNullOrWhiteSpace(NetMessenger.HostCompatibilityWarning))
                {
                    long compatibilityJournalId = ResponseJournal.NoteInput("chat", GetPlayerName(playerId), msg);
                    LlmClient.PublishLocalReply(NetMessenger.HostCompatibilityWarning, compatibilityJournalId);
                    return;
                }
                string playerName = GetPlayerName(playerId);
                long journalId = ResponseJournal.NoteInput("chat", playerName, msg);
                if (!LlmClient.EnqueuePlayerMessage(playerName, playerId, msg, journalId))
                    ResponseJournal.Discard(journalId);
            }
        }

        private static PlayerControllerB GetPlayerById(int playerId)
        {
            try
            {
                var scripts = StartOfRound.Instance?.allPlayerScripts;
                if (scripts == null) return null;
                foreach (var player in scripts)
                    if (player != null && (int)player.playerClientId == playerId)
                        return player;
                if (playerId >= 0 && playerId < scripts.Length) return scripts[playerId];
            }
            catch { /* ignore */ }
            return null;
        }

        private static string GetPlayerName(int playerId)
        {
            var p = GetPlayerById(playerId);
            if (p != null && !string.IsNullOrEmpty(p.playerUsername))
                return p.playerUsername;
            return $"Player{playerId}";
        }
    }
}
