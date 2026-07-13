using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace LethalAICrewmate
{
    public static class CrewmateSpawner
    {
        private static bool _spawnedThisLanding;
        private static bool _spawnAttemptInProgress;
        private static Coroutine _spawnRoutine;

        public static void SpawnCrewmateIfNeeded()
        {
            try
            {
                if (Plugin.Enabled == null || !Plugin.Enabled.Value) return;
                if (!IsHost()) return;
                if (_spawnedThisLanding) return;
                if (StartOfRound.Instance == null || !StartOfRound.Instance.shipHasLanded) return;
                if (StartOfRound.Instance.inShipPhase) return;

                if (CrewmateRegistry.GetPrimary() != null)
                {
                    _spawnedThisLanding = true;
                    return;
                }

                if (Plugin.Host == null)
                {
                    // Fallback: try once immediately if host MB missing
                    TrySpawnOnce();
                    return;
                }

                if (_spawnAttemptInProgress) return;
                if (_spawnRoutine != null)
                {
                    try { Plugin.Host.StopCoroutine(_spawnRoutine); } catch { /* ignore */ }
                }
                _spawnRoutine = Plugin.Host.StartCoroutine(SpawnWithRetries());
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"SpawnCrewmateIfNeeded: {ex}");
            }
        }

        private static IEnumerator SpawnWithRetries()
        {
            _spawnAttemptInProgress = true;
            // Short delays: NavMesh / RoundManager sometimes not ready on the landing frame
            float[] delays = { 0.15f, 0.75f, 1.5f, 3f };
            for (int i = 0; i < delays.Length; i++)
            {
                if (_spawnedThisLanding || CrewmateRegistry.GetPrimary() != null)
                    break;
                if (!IsHost() || StartOfRound.Instance == null || !StartOfRound.Instance.shipHasLanded)
                    break;
                if (StartOfRound.Instance.inShipPhase)
                    break;

                yield return new WaitForSeconds(delays[i]);

                if (TrySpawnOnce())
                    break;

                Plugin.Log?.LogWarning($"Crewmate spawn attempt {i + 1}/{delays.Length} failed; retrying…");
            }
            _spawnAttemptInProgress = false;
            _spawnRoutine = null;
        }

        private static bool TrySpawnOnce()
        {
            try
            {
                if (_spawnedThisLanding) return true;
                if (CrewmateRegistry.GetPrimary() != null)
                {
                    _spawnedThisLanding = true;
                    return true;
                }

                var enemyType = FindMaskedEnemyType();
                if (enemyType == null || enemyType.enemyPrefab == null)
                {
                    Plugin.Log?.LogWarning("Could not find MaskedPlayerEnemy EnemyType; crewmate not spawned.");
                    return false;
                }

                if (RoundManager.Instance == null)
                {
                    Plugin.Log?.LogWarning("RoundManager missing; cannot spawn crewmate.");
                    return false;
                }

                var spawnPos = GetSpawnPosition();
                spawnPos = SnapToNavMesh(spawnPos, 12f);
                var yRot = 0f;

                Plugin.Log?.LogInfo($"Spawning crewmate at {spawnPos} using EnemyType '{enemyType.enemyName}'");

                NetworkObjectReference netRef =
                    RoundManager.Instance.SpawnEnemyGameObject(spawnPos, yRot, -1, enemyType);

                if (!netRef.TryGet(out NetworkObject netObj) || netObj == null)
                {
                    if (NetworkManager.Singleton != null)
                        netRef.TryGet(out netObj, NetworkManager.Singleton);
                }

                MaskedPlayerEnemy masked = null;
                if (netObj != null)
                    masked = netObj.GetComponent<MaskedPlayerEnemy>();

                if (masked == null)
                {
                    // Fallback: find nearest newly spawned masked near spawn pos
                    var all = UnityEngine.Object.FindObjectsOfType<MaskedPlayerEnemy>();
                    float best = 25f;
                    foreach (var m in all)
                    {
                        if (m == null) continue;
                        float d = Vector3.Distance(m.transform.position, spawnPos);
                        if (d < best && !CrewmateRegistry.IsCrewmate(m))
                        {
                            best = d;
                            masked = m;
                        }
                    }
                }

                if (masked == null)
                {
                    Plugin.Log?.LogWarning("SpawnEnemyGameObject did not yield a MaskedPlayerEnemy.");
                    return false;
                }

                // Snap body to navmesh after spawn
                try
                {
                    var snapped = SnapToNavMesh(masked.transform.position, 15f);
                    masked.transform.position = snapped;
                    if (masked.agent != null)
                    {
                        masked.agent.enabled = true;
                        if (masked.agent.isOnNavMesh)
                            masked.agent.Warp(snapped);
                        else if (NavMesh.SamplePosition(snapped, out var hit, 15f, NavMesh.AllAreas))
                            masked.agent.Warp(hit.position);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"Post-spawn NavMesh warp: {ex.Message}");
                }

                var owner = FindPreferredOwner();
                var data = CrewmateRegistry.Register(masked, owner);
                CrewmateRegistry.EnsureNetworkKey(data);
                MaskedNeutralizePatches.Neutralize(masked, data);

                // Clients need the net id so hostility patches apply for them too
                if (data != null && data.NetworkObjectId != 0)
                    NetMessenger.BroadcastCrewmateSync(data.NetworkObjectId, active: true);

                _spawnedThisLanding = true;
                Plugin.Log?.LogInfo($"Crewmate '{Plugin.CrewmateName.Value}' spawned successfully (netId={data?.NetworkObjectId}).");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"TrySpawnOnce: {ex}");
                return false;
            }
        }

        public static void DespawnAll()
        {
            try
            {
                if (!IsHost())
                {
                    CrewmateRegistry.UnregisterAll();
                    _spawnedThisLanding = false;
                    _spawnAttemptInProgress = false;
                    return;
                }

                var snapshot = new List<CrewmateData>(CrewmateRegistry.All);
                foreach (var data in snapshot)
                {
                    try
                    {
                        if (data.HeldItem != null)
                            CrewmateAI.DropHeldItem(data, data.Enemy != null ? data.Enemy.transform.position : Vector3.zero);

                        if (data.NetworkObjectId != 0)
                            NetMessenger.BroadcastCrewmateSync(data.NetworkObjectId, active: false);

                        if (data.Enemy != null && data.Enemy.IsSpawned && data.Enemy.NetworkObject != null)
                        {
                            RoundManager.Instance?.DespawnEnemyGameObject(data.Enemy.NetworkObject);
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning($"Despawn crewmate failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"DespawnAll: {ex}");
            }
            finally
            {
                CrewmateRegistry.UnregisterAll();
                LlmClient.ResetSession();
                _spawnedThisLanding = false;
                _spawnAttemptInProgress = false;
                _spawnRoutine = null;
            }
        }

        internal static bool IsHost()
        {
            try
            {
                if (NetworkManager.Singleton != null)
                    return NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost;
                if (GameNetworkManager.Instance != null)
                    return GameNetworkManager.Instance.isHostingGame;
            }
            catch
            {
                // ignore
            }
            return false;
        }

        private static Vector3 SnapToNavMesh(Vector3 pos, float maxDistance)
        {
            try
            {
                if (NavMesh.SamplePosition(pos, out var hit, maxDistance, NavMesh.AllAreas))
                    return hit.position;
            }
            catch
            {
                // ignore
            }
            return pos;
        }

        private static Vector3 GetSpawnPosition()
        {
            try
            {
                var sor = StartOfRound.Instance;
                if (sor != null)
                {
                    if (sor.outsideShipSpawnPosition != null)
                        return sor.outsideShipSpawnPosition.position + Vector3.forward * 2f + Vector3.up * 0.5f;
                    if (sor.middleOfShipNode != null)
                        return sor.middleOfShipNode.position + Vector3.forward * 3f;
                }

                if (RoundManager.Instance != null)
                {
                    var pos = RoundManager.Instance.GetNavMeshPosition(
                        sor != null && sor.outsideShipSpawnPosition != null
                            ? sor.outsideShipSpawnPosition.position
                            : Vector3.zero,
                        default, 5f, -1);
                    if (pos != Vector3.zero) return pos + Vector3.up * 0.2f;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"GetSpawnPosition: {ex.Message}");
            }
            return Vector3.zero;
        }

        private static PlayerControllerB FindPreferredOwner()
        {
            try
            {
                var sor = StartOfRound.Instance;
                if (sor?.allPlayerScripts == null) return null;

                // Prefer host local player, else first living player
                PlayerControllerB firstLiving = null;
                foreach (var p in sor.allPlayerScripts)
                {
                    if (p == null || p.isPlayerDead) continue;
                    if (firstLiving == null) firstLiving = p;
                    if (p.isHostPlayerObject || p.IsOwner)
                        return p;
                }
                return firstLiving ?? sor.localPlayerController;
            }
            catch
            {
                return null;
            }
        }

        public static EnemyType FindMaskedEnemyType()
        {
            try
            {
                // 1) Current level enemy lists
                var level = RoundManager.Instance != null
                    ? RoundManager.Instance.currentLevel
                    : StartOfRound.Instance?.currentLevel;

                var found = SearchLevelForMasked(level);
                if (found != null) return found;

                // 2) All StartOfRound levels
                if (StartOfRound.Instance?.levels != null)
                {
                    foreach (var lvl in StartOfRound.Instance.levels)
                    {
                        found = SearchLevelForMasked(lvl);
                        if (found != null) return found;
                    }
                }

                // 3) QuickMenuManager testAllEnemiesLevel
                var qmm = UnityEngine.Object.FindObjectOfType<QuickMenuManager>();
                if (qmm != null && qmm.testAllEnemiesLevel != null)
                {
                    found = SearchLevelForMasked(qmm.testAllEnemiesLevel);
                    if (found != null) return found;
                }

                // 4) Resources scan for EnemyType with Masked prefab
                var allTypes = Resources.FindObjectsOfTypeAll<EnemyType>();
                foreach (var et in allTypes)
                {
                    if (et == null) continue;
                    if (IsMaskedType(et))
                        return et;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"FindMaskedEnemyType: {ex}");
            }
            return null;
        }

        private static EnemyType SearchLevelForMasked(SelectableLevel level)
        {
            if (level == null) return null;
            EnemyType found = SearchEnemyList(level.Enemies);
            if (found != null) return found;
            found = SearchEnemyList(level.OutsideEnemies);
            if (found != null) return found;
            found = SearchEnemyList(level.DaytimeEnemies);
            return found;
        }

        private static EnemyType SearchEnemyList(List<SpawnableEnemyWithRarity> list)
        {
            if (list == null) return null;
            foreach (var entry in list)
            {
                if (entry?.enemyType != null && IsMaskedType(entry.enemyType))
                    return entry.enemyType;
            }
            return null;
        }

        private static bool IsMaskedType(EnemyType et)
        {
            if (et == null) return false;
            if (!string.IsNullOrEmpty(et.enemyName))
            {
                var n = et.enemyName.ToLowerInvariant();
                if (n.Contains("masked") || n.Contains("mimic"))
                    return true;
            }
            if (et.enemyPrefab != null)
            {
                if (et.enemyPrefab.GetComponent<MaskedPlayerEnemy>() != null)
                    return true;
                if (et.enemyPrefab.name.ToLowerInvariant().Contains("masked"))
                    return true;
            }
            if (!string.IsNullOrEmpty(et.name) && et.name.ToLowerInvariant().Contains("masked"))
                return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.OnShipLandedMiscEvents))]
    internal static class Patch_OnShipLanded
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                NetMessenger.TryRegisterHandlers();
                CrewmateSpawner.SpawnCrewmateIfNeeded();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"OnShipLandedMiscEvents patch: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ShipLeave))]
    internal static class Patch_ShipLeave
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            try
            {
                CrewmateSpawner.DespawnAll();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"ShipLeave patch: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.Start))]
    internal static class Patch_StartOfRound_Start
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                NetMessenger.TryRegisterHandlers();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"StartOfRound.Start patch: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.Start))]
    internal static class Patch_GameNetworkManager_Start
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                NetMessenger.TryRegisterHandlers();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"GameNetworkManager.Start patch: {ex}");
            }
        }
    }
}
