using System;

namespace LethalAICrewmate
{
    /// <summary>
    /// Pure authorization policy for state-changing Buddy commands.
    /// A player id carried inside vanilla chat is not an authenticated network identity, so
    /// restricted lobbies only trust an explicit internal host request, a verified private lobby,
    /// or the host's deliberate public-lobby opt-in.
    /// </summary>
    internal static class RemoteActionAuthorization
    {
        internal static bool AllowsStateChangingRequest(
            LobbyVisibility visibility,
            bool publicLobbyOptIn,
            bool trustedInternalHostRequest)
        {
            if (trustedInternalHostRequest || publicLobbyOptIn)
                return true;
            return LobbyVisibilityPolicy.AllowsRestrictedRemoteFeatures(visibility);
        }

        internal static string NormalizeTerminalCommand(string message, string buddyName)
        {
            string lower = (message ?? "").Trim().ToLowerInvariant();
            string name = string.IsNullOrWhiteSpace(buddyName) ? "buddy" : buddyName.Trim().ToLowerInvariant();

            if (lower.StartsWith(name, StringComparison.Ordinal))
                lower = lower.Substring(name.Length).TrimStart(' ', ',', ':', '-');
            if (lower.StartsWith("buddy", StringComparison.Ordinal))
                lower = lower.Substring(5).TrimStart(' ', ',', ':', '-');
            return lower;
        }

        internal static bool ShouldBlockUnauthenticatedTextRequest(
            string normalizedCommand,
            LobbyVisibility visibility,
            bool publicLobbyOptIn)
        {
            return ShipCommandParsing.IsStateChangingRequest(normalizedCommand) &&
                   !AllowsStateChangingRequest(visibility, publicLobbyOptIn, trustedInternalHostRequest: false);
        }
    }
}
