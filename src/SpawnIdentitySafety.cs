using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// CrewmateSpawner has a useful fallback that locates the newly spawned Masked when LC does not
    /// return a resolvable NetworkObjectReference. Without an identity guard, that fallback could
    /// select a real pre-existing Masked near the spawn point. Snapshot instances before the attempt
    /// and refuse registration of anything that existed beforehand.
    /// </summary>
    internal static class SpawnIdentitySafety
    {
        private static readonly HashSet<int> PreexistingMasked = new HashSet<int>();
        public static bool SpawnAttemptActive { get; private set; }

        public static void Begin()
        {
            SpawnAttemptActive = true;
            PreexistingMasked.Clear();
            try
            {
                foreach (var masked in UnityEngine.Object.FindObjectsOfType<MaskedPlayerEnemy>())
                {
                    if (masked != null)
                        PreexistingMasked.Add(masked.GetInstanceID());
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Masked spawn identity snapshot: {ex.Message}");
            }
        }

        public static void End()
        {
            SpawnAttemptActive = false;
            PreexistingMasked.Clear();
        }

        public static bool WasPresentBeforeAttempt(MaskedPlayerEnemy enemy)
        {
            return enemy != null && SpawnAttemptActive && PreexistingMasked.Contains(enemy.GetInstanceID());
        }
    }

    [HarmonyPatch(typeof(CrewmateSpawner), "TrySpawnOnce")]
    internal static class Patch_CrewmateSpawner_TrackSpawnIdentity
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Prefix() => SpawnIdentitySafety.Begin();

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Postfix() => SpawnIdentitySafety.End();
    }

    [HarmonyPatch(typeof(CrewmateRegistry), nameof(CrewmateRegistry.Register))]
    internal static class Patch_CrewmateRegistry_RejectExistingMasked
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Prefix(MaskedPlayerEnemy enemy)
        {
            if (!CrewmateSpawner.IsHost()) return;
            if (!SpawnIdentitySafety.WasPresentBeforeAttempt(enemy)) return;

            throw new InvalidOperationException(
                "Refusing to register a pre-existing Masked as Buddy during spawn fallback.");
        }
    }
}
