using System;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Steam lobby-visibility detection used to harden public lobbies: remote push-to-talk is
    /// rejected by default when the lobby is public, protecting the host's API budget and
    /// keeping strangers' audio away from the speech service.
    ///
    /// Lethal Company's "joinable" lobby-data key is a boolean ("true"/"false") that means the
    /// host is accepting late joins, not the lobby's privacy, and Steam's real privacy (the
    /// numeric ELobbyType) is not exposed by the Facepunch wrapper. The game's own reliable
    /// signal is HostSettings.isLobbyPublic, so that drives detection: anything that is not
    /// public is a closed lobby Buddy already knows everyone in.
    /// </summary>
    internal static class LobbySafety
    {
        private static bool _warned;
        private static LobbyVisibility _lastKnownVisibility = LobbyVisibility.Unknown;
        private static float _nextCheckAt;

        internal static LobbyVisibility GetVisibility()
        {
            try
            {
                float now = Time.unscaledTime;
                if (now < _nextCheckAt) return _lastKnownVisibility;
                _nextCheckAt = now + 5f;

                var gm = GameNetworkManager.Instance;
                if (gm == null)
                {
                    _lastKnownVisibility = LobbyVisibility.Unknown;
                    return _lastKnownVisibility;
                }
                if (gm.disableSteam)
                {
                    // LAN games have no Steam lobby and no strangers: everyone present was invited,
                    // so remote voice stays available without a lobby-visibility check to make.
                    _lastKnownVisibility = LobbyVisibility.Friends;
                    return _lastKnownVisibility;
                }
                if (!gm.currentLobby.HasValue)
                {
                    _lastKnownVisibility = LobbyVisibility.Unknown;
                    return _lastKnownVisibility;
                }

                // Only the host keeps an authoritative isLobbyPublic; clients join without creating
                // HostSettings and correctly read non-public. The gate itself always runs on the
                // host, which is exactly where this value is accurate.
                if (gm.lobbyHostSettings != null && gm.lobbyHostSettings.isLobbyPublic)
                    _lastKnownVisibility = LobbyVisibility.Public;
                else
                    _lastKnownVisibility = LobbyVisibility.Friends;
                return _lastKnownVisibility;
            }
            catch (Exception ex)
            {
                _lastKnownVisibility = LobbyVisibility.Unknown;
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log?.LogWarning("Lobby visibility could not be detected; restricted remote features will remain disabled. " + ex.Message);
                }
                return _lastKnownVisibility;
            }
        }

        internal static bool IsPublicLobby() => GetVisibility() == LobbyVisibility.Public;

        internal static bool AllowsRestrictedRemoteFeaturesByDefault() =>
            LobbyVisibilityPolicy.AllowsRestrictedRemoteFeatures(GetVisibility());

        internal static void ResetSession()
        {
            _lastKnownVisibility = LobbyVisibility.Unknown;
            _nextCheckAt = 0f;
            _warned = false;
        }
    }
}
