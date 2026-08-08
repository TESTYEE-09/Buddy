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
        private static bool _lastKnownPublic;
        private static float _nextCheckAt;

        internal static bool IsPublicLobby()
        {
            try
            {
                float now = Time.unscaledTime;
                if (now < _nextCheckAt) return _lastKnownPublic;
                _nextCheckAt = now + 5f;

                var gm = GameNetworkManager.Instance;
                if (gm == null || !gm.currentLobby.HasValue)
                {
                    _lastKnownPublic = false;
                    return false;
                }

                string joinable = gm.currentLobby.Value.GetData("joinable");
                _lastKnownPublic = string.Equals(joinable, "public", StringComparison.OrdinalIgnoreCase);
                return _lastKnownPublic;
            }
            catch (Exception ex)
            {
                // Detection problems must not break friends lobbies: treat the lobby as private
                // (remote voice allowed) and say so loudly so the host can decide.
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log?.LogWarning("Lobby visibility could not be detected; treating lobby as private. " + ex.Message);
                }
                return false;
            }
        }
    }
}
