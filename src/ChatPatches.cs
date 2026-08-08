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
            // allowed to execute these side effects.
            if (addressed || restLower.StartsWith("route") || restLower.StartsWith("buy") ||
                restLower == "moons" || restLower.StartsWith("terminal") || restLower == "store" ||
                restLower == "credits")
            {
                string termResult = TerminalBuddy.HandleChatCommand(msg);
                if (!string.IsNullOrEmpty(termResult))
                {
                    Plugin.Log?.LogInfo($"Terminal cmd: {termResult}");
                    // Replicate deterministic ship/terminal feedback to every matching player and
                    // speak it once. Do not ask the LLM to paraphrase or repeat a side effect.
                    LlmClient.PublishLocalReply(termResult);
                    return;
                }
            }

            MovementCommand movement = MovementCommandParsing.Parse(restLower);
            bool isCommand = false;
            string deterministicCommand = null;
            if (movement.Kind != MovementCommandKind.None && (addressed || MovementCommandParsing.IsDirectDirective(restLower)))
            {
                isCommand = true;
                Plugin.Log?.LogInfo($"Command parsed from chat: '{rest}'");
                if (CrewmateAI.ApplyCommandFromChat(rest, playerId, out string failure))
                    deterministicCommand = CommandName(movement.Kind);
                else if (!string.IsNullOrWhiteSpace(failure))
                {
                    LlmClient.PublishLocalReply(failure);
                    return;
                }
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
                if (CrewmateRegistry.GetPrimary() == null && !string.IsNullOrWhiteSpace(NetMessenger.HostCompatibilityWarning))
                {
                    LlmClient.PublishLocalReply(NetMessenger.HostCompatibilityWarning);
                    return;
                }
                if (!string.IsNullOrEmpty(deterministicCommand))
                {
                    LlmClient.PublishLocalReply(BuildCommandAcknowledgement(deterministicCommand));
                    return;
                }
                string playerName = GetPlayerName(playerId);
                LlmClient.EnqueuePlayerMessage(playerName, msg, isCommand);
            }
        }

        private static string CommandName(MovementCommandKind command)
        {
            if (command == MovementCommandKind.Follow) return "follow";
            if (command == MovementCommandKind.Stay) return "stay";
            if (command == MovementCommandKind.ReturnToShip) return "ship";
            if (command == MovementCommandKind.FetchScrap) return "fetch";
            if (command == MovementCommandKind.ScoutAhead) return "scout";
            return null;
        }

        private static string BuildCommandAcknowledgement(string command)
        {
            string[][] lines =
            {
                new[] { "On you.", "Following.", "Right behind you." },
                new[] { "I'll hold here.", "Staying put.", "I'll wait here." },
                new[] { "Heading back to the ship.", "Back to the ship, got it.", "Returning to the ship." },
                new[] { "I'll look for scrap.", "Going for scrap.", "I'll grab what I can." },
                new[] { "I'll check ahead.", "Taking point.", "I'll scout forward and report back." }
            };
            int group = command == "follow" ? 0 : command == "stay" ? 1 : command == "ship" ? 2 : command == "fetch" ? 3 : 4;
            var choices = lines[group];
            return choices[UnityEngine.Random.Range(0, choices.Length)];
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
