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

        public static void OnServerChat(string chatMessage, int playerId)
        {
            try
            {
                if (!CrewmateSpawner.IsHost()) return;
                if (string.IsNullOrWhiteSpace(chatMessage)) return;
                if (Plugin.Enabled != null && !Plugin.Enabled.Value) return;
                if (!CrewmateSpawner.CanTalkToBuddy) return;

                // Buddy is voice-only for conversational input. Keep observing ordinary chat
                // for turn-taking/social bookkeeping, but never send typed chat to the model.
                string msg = chatMessage.Trim();
                string lower = msg.ToLowerInvariant();
                if (lower.Contains("joined the") || lower.Contains("left the") ||
                    lower.Contains("was kicked") || playerId < 0)
                    return;

                LlmClient.NotePlayerInteraction();
                Plugin.Log?.LogDebug($"Chat observed without Buddy response (playerId={playerId}, chars={msg.Length}).");

                string speakerName = GetPlayerName(playerId);
                bool addressed = lower.Contains((Plugin.CrewmateName?.Value ?? "Buddy").ToLowerInvariant()) ||
                                 lower.Contains("buddy");
                BuddySocialIntelligence.NoteSpeech(playerId, speakerName, addressed);
                if (addressed) BuddyRelationships.NoteAddressing(speakerName);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Voice-only chat observer: {ex}");
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
