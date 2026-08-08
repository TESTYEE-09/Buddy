using HarmonyLib;

namespace LethalAICrewmate
{
    /// <summary>
    /// Prevent Buddy from entering a multiplayer round until every connected remote player has
    /// proven they run the exact same LethalAICrewmate wire protocol/version.
    /// This protects unmodded or mismatched clients from treating Buddy as a hostile vanilla Masked.
    /// </summary>
    [HarmonyPatch(typeof(CrewmateSpawner), nameof(CrewmateSpawner.SpawnCrewmateIfNeeded))]
    internal static class Patch_CrewmateSpawner_SpawnCompatibilityGate
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (NetMessenger.IsHostSessionReadyForBuddy())
                return true;

            Plugin.Log?.LogInfo("Buddy spawn delayed until every multiplayer client is compatible.");
            return false;
        }
    }

    [HarmonyPatch(typeof(CrewmateSpawner), "TrySpawnOnce")]
    internal static class Patch_CrewmateSpawner_TrySpawnCompatibilityGate
    {
        [HarmonyPrefix]
        private static bool Prefix(ref bool __result)
        {
            if (NetMessenger.IsHostSessionReadyForBuddy())
                return true;

            __result = false;
            return false;
        }
    }
}
