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

            // Ignore system/join messages before any command parsing.
            if (lower.Contains("joined the") || lower.Contains("left the") ||
                lower.Contains("was kicked") || playerId < 0)
                return;

            Plugin.Log?.LogInfo($"Chat observed: '{msg}' (playerId={playerId})");

            bool addressed =
                lower.StartsWith(nameLower) ||
                lower.StartsWith("buddy") ||
                lower.Contains(nameLower) ||
                lower.Contains("buddy");

            string rest = msg;
            if (lower.StartsWith(nameLower))
                rest = msg.Substring(name.Length).TrimStart(' ', ',', ':', '-');
            else if (lower.StartsWith("buddy"))
                rest = msg.Substring(5).TrimStart(' ', ',', ':', '-');

            string restLower = rest.ToLowerInvariant();

            // Explicit terminal/orbit actions are deterministic player commands. The LLM is never
            // allowed to execute these side effects (see TerminalSafetyPatches).
            if (addressed || restLower.StartsWith("route") || restLower.StartsWith("buy") ||
                restLower == "moons" || restLower.StartsWith("terminal") || restLower == "store" ||
                restLower == "credits")
            {
                string termResult = TerminalBuddy.HandleChatCommand(msg);
                if (!string.IsNullOrEmpty(termResult))
                {
                    Plugin.Log?.LogInfo($"Terminal cmd: {termResult}");
                    try
                    {
                        if (HUDManager.Instance != null)
                            HUDManager.Instance.AddChatMessage(termResult, name);
                    }
                    catch { /* ignore */ }
                }
            }

            bool looksLikeCommand =
                restLower.Contains("follow") || restLower == "stay" || restLower.StartsWith("stay ") ||
                restLower.Contains("wait") || restLower == "stop" ||
                restLower.Contains("go to ship") || restLower.Contains("go home") ||
                restLower == "ship" || restLower.StartsWith("ship ") ||
                restLower.Contains("fetch") || restLower.Contains("collect scrap") ||
                restLower.Contains("scrap") || restLower.Contains("loot") ||
                restLower.Contains("come here") || restLower == "here" || restLower.Contains("come on");

            bool isCommand = false;
            if (looksLikeCommand && (addressed ||
                restLower.StartsWith("follow") || restLower.StartsWith("stay") ||
                restLower.StartsWith("fetch") || restLower.StartsWith("ship") ||
                restLower.StartsWith("go ") || restLower.StartsWith("come") ||
                restLower == "here" || restLower == "stop" || restLower.StartsWith("wait")))
            {
                isCommand = true;
                Plugin.Log?.LogInfo($"Command parsed from chat: '{rest}'");
                CrewmateAI.ApplyCommandFromChat(rest);
            }

            bool shouldReply = addressed || isCommand;
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
