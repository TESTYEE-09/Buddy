using System;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Steam lobby-visibility detection used to harden public lobbies: remote push-to-talk is
    /// rejected by default when the lobby is public, protecting the host's API budget and
    /// keeping strangers' audio away from the speech service. Lethal Company stores the
    /// visibility under the lobby data key "joinable" ("public"/"friends"/"inviteOnly").
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
                if (gm == null || !gm.currentLobby.HasValue)
                {
                    _lastKnownVisibility = LobbyVisibility.Unknown;
                    return _lastKnownVisibility;
                }

                string joinable = gm.currentLobby.Value.GetData("joinable");
                _lastKnownVisibility = LobbyVisibilityPolicy.Parse(joinable);
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
