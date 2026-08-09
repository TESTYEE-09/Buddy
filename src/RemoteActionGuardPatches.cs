using HarmonyLib;

namespace LethalAICrewmate
{
    /// <summary>
    /// Vanilla Lethal Company chat includes a caller-supplied playerId. That value is useful for
    /// presentation, but it is not an authenticated Netcode sender identity. Never use it to grant
    /// the host exemption for restricted state-changing Buddy actions.
    /// </summary>
    [HarmonyPatch(typeof(TerminalBuddy), nameof(TerminalBuddy.HandleChatCommand))]
    internal static class Patch_TerminalBuddy_HandleChatCommand_Authorization
    {
        [HarmonyPrefix]
        private static bool Prefix(string message, int requestingPlayerId, ref string __result)
        {
            // Negative ids are reserved for Buddy's own internal host-side calls. Normal observed
            // player chat always carries a non-negative id and therefore receives no host trust.
            if (requestingPlayerId < 0)
                return true;

            bool publicOptIn = Plugin.RemoteGameActionsInPublicLobbies?.Value == true;
            LobbyVisibility visibility = LobbySafety.GetVisibility();
            string command = RemoteActionAuthorization.NormalizeTerminalCommand(
                message,
                Plugin.CrewmateName?.Value ?? "Buddy");

            if (!RemoteActionAuthorization.ShouldBlockUnauthenticatedTextRequest(
                    command,
                    visibility,
                    publicOptIn))
                return true;

            __result = "Remote ship and terminal actions are disabled unless this is a verified friends/invite-only lobby.";
            Plugin.Log?.LogWarning(
                $"Blocked restricted Buddy text action in {visibility} lobby; vanilla chat playerId={requestingPlayerId} is not treated as authenticated host identity.");
            return false;
        }
    }
}
