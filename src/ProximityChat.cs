using System;
using GameNetcodeStuff;
using UnityEngine;

namespace LethalAICrewmate
{
    public static class ProximityChat
    {
        public static void TryShowLocal(string crewmateName, string text, Vector3 crewmatePosition)
        {
            TryShowLocal(crewmateName, text, crewmatePosition, out _);
        }

        public static bool TryShowLocal(string crewmateName, string text, Vector3 crewmatePosition, out string result)
        {
            result = "unknown";
            try
            {
                if (string.IsNullOrEmpty(text)) { result = "empty text"; return false; }
                if (HUDManager.Instance == null) { result = "HUD unavailable"; return false; }

                if (!ShouldHear(crewmatePosition))
                {
                    result = "outside configured hearing range";
                    return false;
                }

                string name = string.IsNullOrEmpty(crewmateName)
                    ? (Plugin.CrewmateName?.Value ?? "Buddy")
                    : crewmateName;

                HUDManager.Instance.AddChatMessage(text, name);
                result = "displayed";
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"TryShowLocal: {ex}");
                result = "HUD exception: " + ex.Message;
                return false;
            }
        }

        private static bool ShouldHear(Vector3 crewmatePosition)
        {
            try
            {
                var local = GetLocalPlayer();
                if (local == null) return true; // fail open if we can't resolve player

                // Dead players always hear
                if (local.isPlayerDead)
                    return true;

                float range = Plugin.ChatHearRange?.Value ?? 25f;
                if (range <= 0f)
                    return true; // 0 = everyone

                float dist = Vector3.Distance(local.transform.position, crewmatePosition);
                return dist <= range;
            }
            catch
            {
                return true;
            }
        }

        private static PlayerControllerB GetLocalPlayer()
        {
            try
            {
                if (StartOfRound.Instance?.localPlayerController != null)
                    return StartOfRound.Instance.localPlayerController;
                if (GameNetworkManager.Instance?.localPlayerController != null)
                    return GameNetworkManager.Instance.localPlayerController;
            }
            catch
            {
                // ignore
            }
            return null;
        }
    }
}
