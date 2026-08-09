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
            // allowed to execute these side effects, and they only run when Buddy is addressed
            // (or when the message is an explicitly pleaded spawn request, which stays sandboxed
            // by plea + item validation + quantity caps). Plain chat like "buy 3 flashlights" or
            // "route titan" must never spend credits or move the ship by itself.
            if (addressed || restLower.Contains("spawn"))
            {
                string termResult = TerminalBuddy.HandleChatCommand(msg, playerId);
                if (!string.IsNullOrEmpty(termResult))
                {
                    Plugin.Log?.LogInfo($"Terminal cmd: {termResult}");
                    // Replicate deterministic ship/terminal feedback to every matching player and
                    // speak it once. Do not ask the LLM to paraphrase or repeat a side effect.
                    long journalId = ResponseJournal.NoteInput("command", GetPlayerName(playerId), msg);
                    LlmClient.PublishLocalReply(termResult, journalId);
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
                    long journalId = ResponseJournal.NoteInput("command", GetPlayerName(playerId), msg);
                    LlmClient.PublishLocalReply(failure, journalId);
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
                    long compatibilityJournalId = ResponseJournal.NoteInput("command", GetPlayerName(playerId), msg);
                    LlmClient.PublishLocalReply(NetMessenger.HostCompatibilityWarning, compatibilityJournalId);
                    return;
                }
                if (!string.IsNullOrEmpty(deterministicCommand))
                {
                    long acknowledgementJournalId = ResponseJournal.NoteInput("command", GetPlayerName(playerId), msg);
                    LlmClient.PublishLocalReply(BuildCommandAcknowledgement(deterministicCommand), acknowledgementJournalId);
                    return;
                }
                string playerName = GetPlayerName(playerId);
                long journalId = ResponseJournal.NoteInput("chat", playerName, msg);
                if (!LlmClient.EnqueuePlayerMessage(playerName, msg, isCommand, journalId))
                    ResponseJournal.Discard(journalId);
            }
        }

        internal static string ExecuteDeterministicOnly(string command, int playerId)
        {
            if (!CrewmateSpawner.IsHost()) return "Rejected: only the host can execute Buddy commands.";
            if (string.IsNullOrWhiteSpace(command)) return "Rejected: empty command.";

            string message = command.Trim();
            string buddyName = Plugin.CrewmateName?.Value ?? "Buddy";
            if (message.IndexOf("buddy", StringComparison.OrdinalIgnoreCase) < 0 &&
                message.IndexOf(buddyName, StringComparison.OrdinalIgnoreCase) < 0)
                message = buddyName + " " + message;

            string terminal = TerminalBuddy.HandleChatCommand(message, playerId);
            if (!string.IsNullOrWhiteSpace(terminal)) return terminal;

            string rest = message;
            if (rest.StartsWith(buddyName, StringComparison.OrdinalIgnoreCase))
                rest = rest.Substring(buddyName.Length).TrimStart(' ', ',', ':', '-');
            else if (rest.StartsWith("buddy", StringComparison.OrdinalIgnoreCase))
                rest = rest.Substring(5).TrimStart(' ', ',', ':', '-');

            MovementCommand movement = MovementCommandParsing.Parse(rest.ToLowerInvariant());
            if (movement.Kind != MovementCommandKind.None)
            {
                if (CrewmateAI.ApplyCommandFromChat(rest, playerId, out string failure))
                    return "Confirmed: " + CommandName(movement.Kind) + " command started.";
                return string.IsNullOrWhiteSpace(failure) ? "Command could not be executed." : failure;
            }
            return "No supported deterministic game command matched. Do not claim an action occurred; answer conversationally.";
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
                new[] { "On you. Personal space revoked.", "Following. Try walking professionally.", "Right behind you, workplace hazard." },
                new[] { "Parked. Try not to miss me.", "Staying put. Thrilling assignment.", "Holding here like expensive furniture." },
                new[] { "Back to the ship. Cowardice, but organised.", "Heading home. Excellent survival instinct.", "Shipward bound, dignity optional." },
                new[] { "Scrap run. Capitalism needs me.", "Looking for scrap and poor decisions.", "I'll fetch loot. Guard my imaginary pension." },
                new[] { "Taking point. Terrible promotion.", "Checking ahead. Screaming counts as intel.", "Scouting forward. Prepare my tiny memorial." }
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
