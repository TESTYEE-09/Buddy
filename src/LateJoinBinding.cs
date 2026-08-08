using System;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Custom state messages and NetworkObject spawn callbacks are separate streams. A late join can
    /// therefore learn Buddy's ID just after Masked.Start already ran. Retry binding at low frequency
    /// so either message ordering converges to the same friendly client state.
    /// </summary>
    [HarmonyPatch(typeof(NetMessenger), nameof(NetMessenger.Tick))]
    internal static class Patch_NetMessenger_RetryLateJoinBinding
    {
        private static float _nextBindAt;

        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsClient || nm.IsServer || !nm.IsListening)
                    return;
                if (Time.unscaledTime < _nextBindAt)
                    return;

                _nextBindAt = Time.unscaledTime + 0.5f;
                foreach (var masked in UnityEngine.Object.FindObjectsOfType<MaskedPlayerEnemy>())
                {
                    if (masked != null && masked.IsSpawned)
                        CrewmateRegistry.TryBindKnown(masked);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Late-join Buddy binding retry: {ex.Message}");
            }
        }
    }
}
