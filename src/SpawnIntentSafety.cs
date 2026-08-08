using System;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Buddy is physically a Masked NetworkObject. Its vanilla network spawn can reach a client a
    /// moment before the custom Buddy-ID message. Pre-announcing the expected spawn position lets
    /// compatible clients temporarily suppress hostile Masked logic in that tiny identification gap.
    /// </summary>
    public static class SpawnIntentSafety
    {
        private const string MessageName = "LethalAICrewmate_SpawnIntent";
        private const float IntentLifetime = 3.5f;
        private const float MatchRadius = 6f;

        private static NetworkManager _registeredOn;
        private static Vector3 _expectedPosition;
        private static float _expiresAt;

        public static void Tick()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.CustomMessagingManager == null)
                {
                    _registeredOn = null;
                    _expiresAt = 0f;
                    return;
                }

                if (_registeredOn != nm)
                {
                    try { nm.CustomMessagingManager.UnregisterNamedMessageHandler(MessageName); } catch { /* ignore */ }
                    nm.CustomMessagingManager.RegisterNamedMessageHandler(MessageName, OnSpawnIntent);
                    _registeredOn = nm;
                }

                if (Time.unscaledTime > _expiresAt)
                    _expiresAt = 0f;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"SpawnIntentSafety.Tick: {ex.Message}");
            }
        }

        public static void BroadcastExpectedBuddyPosition()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || !nm.IsListening || nm.CustomMessagingManager == null)
                    return;
                if (!NetMessenger.IsHostSessionReadyForBuddy())
                    return;
                if (CrewmateRegistry.GetPrimary() != null)
                    return;

                Vector3 pos = ExpectedSpawnPosition();
                using (var writer = new FastBufferWriter(32, Allocator.Temp))
                {
                    writer.WriteValueSafe(pos);
                    nm.CustomMessagingManager.SendNamedMessageToAll(MessageName, writer, NetworkDelivery.Reliable);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Spawn intent broadcast: {ex.Message}");
            }
        }

        private static Vector3 ExpectedSpawnPosition()
        {
            try
            {
                var local = StartOfRound.Instance?.localPlayerController;
                if (local != null)
                {
                    return local.transform.position
                           + local.transform.right * 1.15f
                           + local.transform.forward * 0.35f
                           + Vector3.up * 0.05f;
                }
                if (StartOfRound.Instance?.middleOfShipNode != null)
                    return StartOfRound.Instance.middleOfShipNode.position;
            }
            catch { /* fallback */ }
            return Vector3.zero;
        }

        private static void OnSpawnIntent(ulong senderId, FastBufferReader reader)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.IsServer || !nm.IsClient || senderId != NetworkManager.ServerClientId)
                    return;
                if (!string.IsNullOrEmpty(NetMessenger.CompatibilityWarning))
                    return;

                reader.ReadValueSafe(out Vector3 pos);
                _expectedPosition = pos;
                _expiresAt = Time.unscaledTime + IntentLifetime;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Spawn intent receive: {ex.Message}");
            }
        }

        public static bool IsPendingBuddy(MaskedPlayerEnemy enemy)
        {
            try
            {
                if (enemy == null || Time.unscaledTime > _expiresAt)
                    return false;
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.IsServer || !nm.IsClient)
                    return false;
                if (CrewmateRegistry.IsCrewmate(enemy))
                    return false;
                return Vector3.Distance(enemy.transform.position, _expectedPosition) <= MatchRadius;
            }
            catch
            {
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(CrewmateSpawner), nameof(CrewmateSpawner.SpawnCrewmateIfNeeded))]
    internal static class Patch_CrewmateSpawner_PreannounceBuddy
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            if (CrewmateSpawner.IsHost())
                SpawnIntentSafety.BroadcastExpectedBuddyPosition();
        }
    }

    [HarmonyPatch(typeof(CrewmateSpawner), "TrySpawnOnce")]
    internal static class Patch_CrewmateSpawner_RefreshSpawnIntent
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            if (CrewmateSpawner.IsHost())
                SpawnIntentSafety.BroadcastExpectedBuddyPosition();
        }
    }

    // High-priority guards run before the normal Buddy-specific patches. They only suppress a Masked
    // for a few seconds near the host-announced Buddy spawn point until the real network ID arrives.
    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.DoAIInterval))]
    internal static class Patch_PendingBuddy_DoAIInterval
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool Prefix(MaskedPlayerEnemy __instance) => !SpawnIntentSafety.IsPendingBuddy(__instance);
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.Update))]
    internal static class Patch_PendingBuddy_Update
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool Prefix(MaskedPlayerEnemy __instance) => !SpawnIntentSafety.IsPendingBuddy(__instance);
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.OnCollideWithPlayer))]
    internal static class Patch_PendingBuddy_Collision
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool Prefix(MaskedPlayerEnemy __instance) => !SpawnIntentSafety.IsPendingBuddy(__instance);
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.KillPlayerAnimationServerRpc))]
    internal static class Patch_PendingBuddy_KillServer
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool Prefix(MaskedPlayerEnemy __instance) => !SpawnIntentSafety.IsPendingBuddy(__instance);
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.KillPlayerAnimationClientRpc))]
    internal static class Patch_PendingBuddy_KillClient
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool Prefix(MaskedPlayerEnemy __instance) => !SpawnIntentSafety.IsPendingBuddy(__instance);
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.FinishKillAnimation))]
    internal static class Patch_PendingBuddy_FinishKill
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool Prefix(MaskedPlayerEnemy __instance) => !SpawnIntentSafety.IsPendingBuddy(__instance);
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.DetectNoise))]
    internal static class Patch_PendingBuddy_DetectNoise
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool Prefix(MaskedPlayerEnemy __instance) => !SpawnIntentSafety.IsPendingBuddy(__instance);
    }
}
