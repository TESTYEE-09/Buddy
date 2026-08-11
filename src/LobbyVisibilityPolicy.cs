using System;

namespace LethalAICrewmate
{
    internal enum LobbyVisibility
    {
        Unknown,
        Public,
        Friends,
        InviteOnly
    }

    internal static class LobbyVisibilityPolicy
    {
        internal static LobbyVisibility Parse(string value)
        {
            string normalized = value?.Trim() ?? "";
            if (string.Equals(normalized, "public", StringComparison.OrdinalIgnoreCase))
                return LobbyVisibility.Public;
            if (string.Equals(normalized, "friends", StringComparison.OrdinalIgnoreCase))
                return LobbyVisibility.Friends;
            if (string.Equals(normalized, "inviteOnly", StringComparison.OrdinalIgnoreCase))
                return LobbyVisibility.InviteOnly;
            return LobbyVisibility.Unknown;
        }

        internal static bool AllowsRestrictedRemoteFeatures(LobbyVisibility visibility) =>
            visibility == LobbyVisibility.Friends || visibility == LobbyVisibility.InviteOnly;

        internal static bool AllowsRemoteVoice(LobbyVisibility visibility, bool enabled, bool publicOptIn)
        {
            if (!enabled) return false;
            if (AllowsRestrictedRemoteFeatures(visibility)) return true;
            return visibility == LobbyVisibility.Public && publicOptIn;
        }
    }
}
