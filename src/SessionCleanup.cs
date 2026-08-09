using System;
using Unity.Netcode;

namespace LethalAICrewmate
{
    /// <summary>
    /// ShipLeave covers normal round transitions, but a lobby disconnect can destroy network objects
    /// without taking that path. Clear static Buddy state when the active NetworkManager stops
    /// listening so the next lobby starts clean.
    /// </summary>
    internal static class SessionCleanup
    {
        private static NetworkManager _lastManager;
        private static bool _wasListening;

        internal static void Tick()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                bool listening = nm != null && nm.IsListening;

                bool managerChanged = _lastManager != null && nm != _lastManager;
                bool disconnected = _wasListening && !listening;
                if (managerChanged || disconnected)
                {
                    OpenAiRealtimeVoiceClient.ResetSession();
                    BuddyTts.ResetSession();
                    LlmClient.ResetSession();
                    ResponseJournal.ResetSession();
                    LobbySafety.ResetSession();
                    BuddyCharacterDirector.ResetSession();
                    BuddyAutonomy.ResetSession();
                    BuddyPacingDirector.ResetSession();
                    BuddyRelationships.ResetSession();
                    BuddyEnvironmentSensors.ResetSession();
                    BuddySocialIntelligence.ResetSession();
                    BuddyMalice.ResetSession();
                    VoiceCoexistence.ResetSession();
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
