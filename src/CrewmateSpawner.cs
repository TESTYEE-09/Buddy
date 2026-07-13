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
        private static float _lastPollLog;
        private static int _pollAttempts;

        /// <summary>Called from land patches and periodic poll.</summary>
        public static void SpawnCrewmateIfNeeded(string reason = "unknown")
        {
            try
            {
                if (Plugin.Enabled == null || !Plugin.Enabled.Value)
                {
                    LogOnce($"skip spawn ({reason}): Disabled");
                    return;
                }
                if (!IsHost())
                {
                    LogOnce($"skip spawn ({reason}): not host");
                    return;
                }
                if (_spawnedThisLanding)
                    return;
                if (CrewmateRegistry.GetPrimary() != null)
                {
                    _spawnedThisLanding = true;
                    return;
                }

                var sor = StartOfRound.Instance;
                if (sor == null)
                {
                    LogOnce($"skip spawn ({reason}): StartOfRound null");
                    return;
                }

                // Prefer landed state; allow land-event reasons to proceed even if flags lag one frame
                bool looksLanded = sor.shipHasLanded && !sor.inShipPhase;
                bool forceFromEvent = reason.StartsWith("event:", StringComparison.Ordinal);
                if (!looksLanded && !forceFromEvent)
                {
                    return; // quiet — poll will retry
                }

                if (Plugin.Host == null)
                {
                    Plugin.Log?.LogWarning($"Host MB missing; immediate spawn ({reason})");
                    TrySpawnOnce(reason);
                    return;
                }

                if (_spawnAttemptInProgress) return;

                if (_spawnRoutine != null)
                {
                    try { Plugin.Host.StopCoroutine(_spawnRoutine); } catch { /* ignore */ }
                }
                Plugin.Log?.LogInfo($"Crewmate spawn requested ({reason}); starting retries…");
                _spawnRoutine = Plugin.Host.StartCoroutine(SpawnWithRetries(reason));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"SpawnCrewmateIfNeeded: {ex}");
            }
        }

        /// <summary>1–2 Hz poll from PluginHost while on a moon.</summary>
        public static void PollSpawn()
        {
            try
            {
                if (_spawnedThisLanding || _spawnAttemptInProgress) return;
                if (Plugin.Enabled == null || !Plugin.Enabled.Value) return;
                if (!IsHost()) return;

                var sor = StartOfRound.Instance;
                if (sor == null) return;
                if (!sor.shipHasLanded || sor.inShipPhase) return;
                if (CrewmateRegistry.GetPrimary() != null)
                {
                    _spawnedThisLanding = true;
                    return;
                }

                _pollAttempts++;
                SpawnCrewmateIfNeeded($"poll#{_pollAttempts}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"PollSpawn: {ex}");
            }
        }

        private static void LogOnce(string msg)
        {
            if (Time.unscaledTime - _lastPollLog < 8f) return;
            _lastPollLog = Time.unscaledTime;
            Plugin.Log?.LogInfo(msg);
        }

        private static IEnumerator SpawnWithRetries(string reason)
        {
            _spawnAttemptInProgress = true;
            float[] delays = { 0.05f, 0.5f, 1.0f, 2.0f, 4.0f, 7.0f };
            for (int i = 0; i < delays.Length; i++)
            {
                if (_spawnedThisLanding || CrewmateRegistry.GetPrimary() != null)
                    break;

                var sor = StartOfRound.Instance;
                if (!IsHost() || sor == null)
                    break;
                // Abort only if clearly back in orbit
                if (sor.inShipPhase && !sor.shipHasLanded)
                    break;

                yield return new WaitForSeconds(delays[i]);

                if (TrySpawnOnce($"{reason} try{i + 1}"))
                    break;

                Plugin.Log?.LogWarning($"Crewmate spawn attempt {i + 1}/{delays.Length} failed; retrying…");
            }

            if (!_spawnedThisLanding && CrewmateRegistry.GetPrimary() == null)
                Plugin.Log?.LogError("Crewmate failed to spawn after all retries. Check FindMaskedEnemyType / RoundManager logs.");

            _spawnAttemptInProgress = false;
            _spawnRoutine = null;
        }

        private static bool TrySpawnOnce(string reason)
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
                    Plugin.Log?.LogWarning($"[{reason}] Could not find MaskedPlayerEnemy EnemyType; crewmate not spawned.");
                    DumpEnemyTypeHints();
                    return false;
                }

                if (RoundManager.Instance == null)
                {
                    Plugin.Log?.LogWarning($"[{reason}] RoundManager missing; cannot spawn crewmate.");
                    return false;
                }

                var spawnPos = GetSpawnPosition();
                spawnPos = SnapToNavMesh(spawnPos, 15f);
                var yRot = 0f;

                Plugin.Log?.LogInfo($"[{reason}] Spawning crewmate at {spawnPos} using EnemyType '{enemyType.enemyName}' prefab='{enemyType.enemyPrefab?.name}'");

                MaskedPlayerEnemy masked = null;

                try
                {
                    NetworkObjectReference netRef =
                        RoundManager.Instance.SpawnEnemyGameObject(spawnPos, yRot, -1, enemyType);

                    if (!netRef.TryGet(out NetworkObject netObj) || netObj == null)
                    {
                        if (NetworkManager.Singleton != null)
                            netRef.TryGet(out netObj, NetworkManager.Singleton);
                    }

                    if (netObj != null)
                        masked = netObj.GetComponent<MaskedPlayerEnemy>();
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"SpawnEnemyGameObject threw: {ex.Message}");
                }

                if (masked == null)
                {
                    // Fallback: nearest newly spawned masked near spawn pos
                    var all = UnityEngine.Object.FindObjectsOfType<MaskedPlayerEnemy>();
                    float best = 30f;
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

                // Last resort: instantiate prefab + network spawn (host)
                if (masked == null && enemyType.enemyPrefab != null)
                {
                    try
                    {
                        Plugin.Log?.LogWarning($"[{reason}] Falling back to Instantiate+Spawn of Masked prefab");
                        var go = UnityEngine.Object.Instantiate(enemyType.enemyPrefab, spawnPos, Quaternion.identity);
                        var net = go.GetComponent<NetworkObject>();
                        masked = go.GetComponent<MaskedPlayerEnemy>();
                        if (net != null && !net.IsSpawned)
                            net.Spawn(true);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogError($"Instantiate spawn fallback failed: {ex}");
                    }
                }

                if (masked == null)
                {
                    Plugin.Log?.LogWarning($"[{reason}] Spawn did not yield a MaskedPlayerEnemy.");
                    return false;
                }

                try
                {
                    var snapped = SnapToNavMesh(masked.transform.position, 15f);
                    masked.transform.position = snapped;
                    if (masked.agent != null)
                    {
                        masked.agent.enabled = true;
                        if (NavMesh.SamplePosition(snapped, out var hit, 15f, NavMesh.AllAreas))
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

                if (data != null && data.NetworkObjectId != 0)
                    NetMessenger.BroadcastCrewmateSync(data.NetworkObjectId, active: true);

                _spawnedThisLanding = true;
                Plugin.Log?.LogInfo($"Crewmate '{Plugin.CrewmateName.Value}' spawned successfully (netId={data?.NetworkObjectId}, reason={reason}).");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"TrySpawnOnce: {ex}");
                return false;
            }
        }

        private static void DumpEnemyTypeHints()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<EnemyType>();
                int n = 0;
                foreach (var et in all)
                {
                    if (et == null) continue;
                    string nm = et.enemyName ?? et.name ?? "?";
                    if (n < 40)
                        Plugin.Log?.LogInfo($"  EnemyType candidate: '{nm}' prefab={(et.enemyPrefab != null ? et.enemyPrefab.name : "null")}");
                    n++;
                }
                Plugin.Log?.LogInfo($"EnemyType scan total: {n}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"DumpEnemyTypeHints: {ex.Message}");
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
                    _pollAttempts = 0;
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
                _pollAttempts = 0;
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
                    // Prefer near the local/host player so Buddy is visible immediately
                    var owner = FindPreferredOwner();
                    if (owner != null)
                    {
                        var near = owner.transform.position + owner.transform.forward * 2.5f + Vector3.up * 0.2f;
                        return SnapToNavMesh(near, 10f);
                    }

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
                var level = RoundManager.Instance != null
                    ? RoundManager.Instance.currentLevel
                    : StartOfRound.Instance?.currentLevel;

                var found = SearchLevelForMasked(level);
                if (found != null) return found;

                if (StartOfRound.Instance?.levels != null)
                {
                    foreach (var lvl in StartOfRound.Instance.levels)
                    {
                        found = SearchLevelForMasked(lvl);
                        if (found != null) return found;
                    }
                }

                var qmm = UnityEngine.Object.FindObjectOfType<QuickMenuManager>();
                if (qmm != null && qmm.testAllEnemiesLevel != null)
                {
                    found = SearchLevelForMasked(qmm.testAllEnemiesLevel);
                    if (found != null) return found;
                }

                var allTypes = Resources.FindObjectsOfTypeAll<EnemyType>();
                EnemyType byName = null;
                EnemyType byPrefab = null;
                foreach (var et in allTypes)
                {
                    if (et == null) continue;
                    if (et.enemyPrefab != null && et.enemyPrefab.GetComponent<MaskedPlayerEnemy>() != null)
                    {
                        byPrefab = et;
                        break;
                    }
                    if (IsMaskedType(et) && byName == null)
                        byName = et;
                }
                if (byPrefab != null) return byPrefab;
                if (byName != null) return byName;
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
                try
                {
                    if (et.enemyPrefab.GetComponent<MaskedPlayerEnemy>() != null)
                        return true;
                }
                catch { /* ignore */ }
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
                Plugin.Log?.LogInfo("Hook: OnShipLandedMiscEvents");
                NetMessenger.TryRegisterHandlers();
                CrewmateSpawner.SpawnCrewmateIfNeeded("event:OnShipLandedMiscEvents");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"OnShipLandedMiscEvents patch: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.OpenShipDoors))]
    internal static class Patch_OpenShipDoors
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                Plugin.Log?.LogInfo("Hook: OpenShipDoors");
                NetMessenger.TryRegisterHandlers();
                CrewmateSpawner.SpawnCrewmateIfNeeded("event:OpenShipDoors");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"OpenShipDoors patch: {ex}");
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
