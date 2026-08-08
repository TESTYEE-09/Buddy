using System;
using HarmonyLib;
using Unity.Netcode;

namespace LethalAICrewmate
{
    /// <summary>
    /// ShipLeave covers normal round transitions, but a lobby disconnect can destroy network objects
    /// without taking that path. Clear static Buddy state when the active NetworkManager stops
    /// listening so the next lobby starts clean.
    /// </summary>
    [HarmonyPatch(typeof(NetMessenger), nameof(NetMessenger.Tick))]
    internal static class Patch_NetMessenger_DisconnectCleanup
    {
        private static NetworkManager _lastManager;
        private static bool _wasListening;

        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                bool listening = nm != null && nm.IsListening;

                bool managerChanged = _lastManager != null && nm != _lastManager;
                bool disconnected = _wasListening && !listening;
                if (managerChanged || disconnected)
                {
                    CrewmateSpawner.DespawnAll();
                    Plugin.Log?.LogInfo("Cleared LethalAICrewmate session state after network disconnect/change.");
                }

                _lastManager = nm;
                _wasListening = listening;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Disconnect cleanup: {ex.Message}");
            }
        }
    }
}
