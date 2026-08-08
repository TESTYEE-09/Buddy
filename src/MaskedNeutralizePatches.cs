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

                // Look like a normal crewmate (match owner suit when available)
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

                // Buddy is intentionally invincible. Damage entry points are also blocked below,
                // but restoring these fields makes a partial resync self-healing.
                masked.enemyHP = int.MaxValue;
                masked.isEnemyDead = false;

                // Stop any chase targeting
                masked.targetPlayer = null;
                masked.movingTowardsTargetPlayer = false;
                masked.inKillAnimation = false;

                // Disable hands-out / crouch attack presentation if fields exist
                try { masked.creatureAnimator?.SetBool("IsRunning", false); } catch { /* ignore */ }

                if (data != null) data.Neutralized = true;
                Plugin.Log?.LogInfo("Neutralized masked crewmate (mask hidden, suit set).");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Neutralize: {ex}");
            }
        }

        private static bool Guard(MaskedPlayerEnemy instance)
        {
            try
            {
                return CrewmateRegistry.IsCrewmate(instance);
            }
            catch
            {
                return false;
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
                    // Skip vanilla masked Update (chase/kill), keep agent + hold visuals alive
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

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.LateUpdate))]
    internal static class Patch_Masked_LateUpdate
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
                Plugin.Log?.LogError($"Masked.LateUpdate patch: {ex}");
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

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.HitEnemy))]
    internal static class Patch_Masked_HitEnemy
    {
        [HarmonyPrefix]
        private static bool Prefix(MaskedPlayerEnemy __instance)
        {
            try
            {
                if (CrewmateRegistry.IsCrewmate(__instance)) return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Masked.HitEnemy guard: {ex}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MaskedPlayerEnemy), nameof(MaskedPlayerEnemy.DetectNoise))]
    internal static class Patch_Masked_DetectNoise
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
                Plugin.Log?.LogError($"DetectNoise patch: {ex}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.HitEnemy))]
    internal static class Patch_EnemyAI_HitEnemy
    {
        [HarmonyPrefix]
        private static bool Prefix(EnemyAI __instance)
        {
            try
            {
                if (CrewmateRegistry.IsCrewmate(__instance)) return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"HitEnemy guard patch: {ex}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.KillEnemy))]
    internal static class Patch_EnemyAI_KillEnemy
    {
        [HarmonyPrefix]
        private static bool Prefix(EnemyAI __instance)
        {
            return !CrewmateRegistry.IsCrewmate(__instance);
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
                // Host path: already registered before Start, re-neutralize
                if (CrewmateRegistry.TryGet(__instance, out var data))
                {
                    CrewmateRegistry.EnsureNetworkKey(data);
                    if (!data.Neutralized)
                        MaskedNeutralizePatches.Neutralize(__instance, data);
                    return;
                }

                // Client path: bind if host already announced this net id
                CrewmateRegistry.TryBindKnown(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Masked.Start patch: {ex}");
            }
        }
    }
}
