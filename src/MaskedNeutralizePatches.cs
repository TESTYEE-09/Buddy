using System;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace LethalAICrewmate
{
    public static class MaskedNeutralizePatches
    {
        public static void Neutralize(MaskedPlayerEnemy masked, CrewmateData data)
        {
            if (masked == null) return;
            try
            {
                // Hide comedy/tragedy masks
                if (masked.maskTypes != null)
                {
                    foreach (var go in masked.maskTypes)
                    {
                        if (go != null) go.SetActive(false);
                    }
                }

                try { masked.SetMaskGlow(false); } catch { /* optional */ }

                // Look like a normal crewmate (suit 0 = default orange if available)
                int suitId = 0;
                try
                {
                    if (data?.Owner != null)
                        suitId = data.Owner.currentSuitID;
                }
                catch { /* use 0 */ }

                try { masked.SetSuit(suitId); } catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"SetSuit failed: {ex.Message}");
                }

                // Don't mimic a dead player
                masked.mimickingPlayer = null;

                // Soften combat stats so it doesn't tank hits for sport
                if (masked.enemyHP < 5) masked.enemyHP = 5;

                // Stop any chase targeting
                masked.targetPlayer = null;
                masked.movingTowardsTargetPlayer = false;
                masked.inKillAnimation = false;

                if (data != null) data.Neutralized = true;
                Plugin.Log?.LogInfo("Neutralized masked crewmate (mask hidden, suit set).");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Neutralize: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.DoAIInterval))]
    internal static class Patch_Masked_DoAIInterval
    {
        [HarmonyPrefix]
        private static bool Prefix(MaskedPlayerEnemy __instance)
        {
            try
            {
                if (CrewmateRegistry.IsCrewmate(__instance))
                {
                    CrewmateAI.DoAIInterval(__instance);
                    return false; // skip hostile AI
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Masked.DoAIInterval patch: {ex}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.Update))]
    internal static class Patch_Masked_Update
    {
        [HarmonyPrefix]
        private static bool Prefix(MaskedPlayerEnemy __instance)
        {
            try
            {
                if (CrewmateRegistry.IsCrewmate(__instance))
                {
                    // Skip vanilla masked Update (chase/kill), but keep agent alive via our AI
                    CrewmateAI.CrewmateUpdate(__instance);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Masked.Update patch: {ex}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.OnCollideWithPlayer))]
    internal static class Patch_Masked_OnCollideWithPlayer
    {
        [HarmonyPrefix]
        private static bool Prefix(MaskedPlayerEnemy __instance)
        {
            try
            {
                if (CrewmateRegistry.IsCrewmate(__instance))
                    return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"OnCollideWithPlayer patch: {ex}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.KillPlayerAnimationServerRpc))]
    internal static class Patch_Masked_KillPlayerAnimationServerRpc
    {
        [HarmonyPrefix]
        private static bool Prefix(MaskedPlayerEnemy __instance)
        {
            try
            {
                if (CrewmateRegistry.IsCrewmate(__instance))
                    return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"KillPlayerAnimationServerRpc patch: {ex}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.KillPlayerAnimationClientRpc))]
    internal static class Patch_Masked_KillPlayerAnimationClientRpc
    {
        [HarmonyPrefix]
        private static bool Prefix(MaskedPlayerEnemy __instance)
        {
            try
            {
                if (CrewmateRegistry.IsCrewmate(__instance))
                    return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"KillPlayerAnimationClientRpc patch: {ex}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.FinishKillAnimation))]
    internal static class Patch_Masked_FinishKillAnimation
    {
        [HarmonyPrefix]
        private static bool Prefix(MaskedPlayerEnemy __instance)
        {
            try
            {
                if (CrewmateRegistry.IsCrewmate(__instance))
                    return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"FinishKillAnimation patch: {ex}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.HitEnemy))]
    internal static class Patch_EnemyAI_HitEnemy
    {
        // Let the crewmate take damage, but never retaliate via masked kill paths.
        // No Prefix skip — just ensure after hit it doesn't start chasing.
        [HarmonyPostfix]
        private static void Postfix(EnemyAI __instance)
        {
            try
            {
                if (CrewmateRegistry.IsCrewmate(__instance))
                {
                    __instance.targetPlayer = null;
                    __instance.movingTowardsTargetPlayer = false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"HitEnemy post patch: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.Start))]
    internal static class Patch_Masked_Start
    {
        [HarmonyPostfix]
        private static void Postfix(MaskedPlayerEnemy __instance)
        {
            try
            {
                if (CrewmateRegistry.TryGet(__instance, out var data))
                {
                    CrewmateRegistry.EnsureNetworkKey(data);
                    if (!data.Neutralized)
                        MaskedNeutralizePatches.Neutralize(__instance, data);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Masked.Start patch: {ex}");
            }
        }
    }
}
