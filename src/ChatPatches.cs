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
                // Host receives ServerRpc; also observe here as a belt-and-suspenders
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
        private static float _lastMessageTime;

        public static void OnServerChat(string chatMessage, int playerId)
        {
            if (!CrewmateSpawner.IsHost()) return;
            if (string.IsNullOrWhiteSpace(chatMessage)) return;
            if (Plugin.Enabled != null && !Plugin.Enabled.Value) return;

            // De-dupe if both patches fire for the same message
            if (chatMessage == _lastMessage && Time.time - _lastMessageTime < 0.25f)
                return;
            _lastMessage = chatMessage;
            _lastMessageTime = Time.time;

            var name = Plugin.CrewmateName?.Value ?? "Buddy";
            var msg = chatMessage.Trim();
            var lower = msg.ToLowerInvariant();
            var nameLower = name.ToLowerInvariant();

            bool addressed =
                lower.StartsWith(nameLower) ||
                lower.StartsWith("buddy") ||
                lower.Contains(nameLower) ||
                lower.Contains("buddy");

            bool isCommand = false;
            if (addressed || lower.StartsWith(nameLower) || lower.StartsWith("buddy"))
            {
                // Strip name prefix for command parse
                string rest = msg;
                if (lower.StartsWith(nameLower))
                    rest = msg.Substring(name.Length).TrimStart(' ', ',', ':', '-');
                else if (lower.StartsWith("buddy"))
                    rest = msg.Substring(5).TrimStart(' ', ',', ':', '-');

                string restLower = rest.ToLowerInvariant();
                if (restLower.Contains("follow") || restLower.Contains("stay") ||
                    restLower.Contains("wait") || restLower.Contains("ship") ||
                    restLower.Contains("fetch") || restLower.Contains("collect") ||
                    restLower.Contains("scrap") || restLower.Contains("go to ship") ||
                    restLower.Contains("go home"))
                {
                    isCommand = true;
                    CrewmateAI.ApplyCommandFromChat(rest);
                }
            }

            bool shouldReply = false;
            if (addressed)
                shouldReply = true;
            else if (msg.TrimEnd().EndsWith("?"))
            {
                // Question within ChatTriggerRange of crewmate
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

            if (shouldReply || isCommand)
            {
                string playerName = GetPlayerName(playerId);
                LlmClient.EnqueuePlayerMessage(playerName, msg, isCommand);
            }
        }

        private static PlayerControllerB GetPlayerById(int playerId)
        {
            try
            {
                var scripts = StartOfRound.Instance?.allPlayerScripts;
                if (scripts == null) return null;
                if (playerId >= 0 && playerId < scripts.Length)
                    return scripts[playerId];
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
