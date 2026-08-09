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
        private static float _nextSpawnAllowedAt;
        private static float _landedObservedAt = -1f;
        private const float LandingSettleSeconds = 1.25f;

        internal static bool IsBuddyPresent => CrewmateRegistry.GetPrimary()?.Enemy != null;
        internal static bool CanTalkToBuddy
        {
            get
            {
                if (IsBuddyPresent) return true;
                try { return StartOfRound.Instance?.inShipPhase == true; }
                catch { return false; }
            }
        }

        internal static bool IsLandingSettled()
        {
            try
            {
                var sor = StartOfRound.Instance;
                if (sor == null || sor.inShipPhase || !sor.shipHasLanded || sor.shipIsLeaving)
                {
                    _landedObservedAt = -1f;
                    return false;
                }
                if (_landedObservedAt < 0f) _landedObservedAt = Time.unscaledTime;
                return Time.unscaledTime - _landedObservedAt >= LandingSettleSeconds;
            }
            catch { return false; }
        }

        /// <summary>Called from lifecycle patches and the periodic host poll.</summary>
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
                if (!IsLandingSettled())
                {
                    LogOnce($"skip spawn ({reason}): ship is not fully landed and settled");
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

                // ShipLeave performs vanilla enemy cleanup; wait briefly before recreating Buddy in orbit.
                if (Time.unscaledTime < _nextSpawnAllowedAt) return;

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

        /// <summary>Periodic host poll in orbit and during moon visits.</summary>
        public static void PollSpawn()
        {
            try
            {
                if (_spawnedThisLanding && CrewmateRegistry.GetPrimary() == null)
                    _spawnedThisLanding = false;
                if (_spawnedThisLanding || _spawnAttemptInProgress) return;
                if (Plugin.Enabled == null || !Plugin.Enabled.Value) return;
                if (!IsHost()) return;
                if (Time.unscaledTime < _nextSpawnAllowedAt) return;
                if (!IsLandingSettled()) return;

                var sor = StartOfRound.Instance;
                if (sor == null) return;
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

                var spawnPos = GetSpawnPosition();
                if (!TrySnapToNavMesh(spawnPos, 15f, out spawnPos))
                {
                    Plugin.Log?.LogWarning($"[{reason}] No valid NavMesh spawn point is ready; waiting instead of spawning a floating Buddy.");
                    return false;
                }
                var yRot = 0f;
                var preExistingMasked = new HashSet<int>();
                foreach (var existing in UnityEngine.Object.FindObjectsOfType<MaskedPlayerEnemy>())
                    if (existing != null) preExistingMasked.Add(existing.GetInstanceID());

                Plugin.Log?.LogInfo($"[{reason}] Spawning crewmate at {spawnPos} using EnemyType '{enemyType.enemyName}' prefab='{enemyType.enemyPrefab?.name}'");

                MaskedPlayerEnemy masked = null;

                if (RoundManager.Instance != null) try
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
                    // Fallback: nearest newly spawned masked near spawn pos. Never adopt an
                    // enemy that is alive, engaged or mid-attack — that is a hostile Masked,
                    // not Buddy. The registry registration guard additionally refuses anything
                    // that existed before the spawn attempt.
                    var all = UnityEngine.Object.FindObjectsOfType<MaskedPlayerEnemy>();
                    float best = 30f;
                    foreach (var m in all)
                    {
                        if (m == null || m.isEnemyDead || preExistingMasked.Contains(m.GetInstanceID())) continue;
                        if (m.targetPlayer != null || m.movingTowardsTargetPlayer || m.inKillAnimation) continue;
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

                var owner = FindPreferredOwner();

                // Spawn APIs may reposition a Masked. Re-assert the validated exterior anchor.
                try
                {
                    Vector3 exterior = spawnPos;
                    masked.transform.position = exterior;
                    try { masked.SetEnemyOutside(true); } catch { /* optional */ }
                    masked.isOutside = true;

                    if (masked.agent != null)
                    {
                        masked.agent.enabled = true;
                        masked.agent.Warp(exterior);
                        masked.transform.position = exterior;
                    }

                    // Face the player
                    if (owner != null)
                    {
                        var look = owner.transform.position - masked.transform.position;
                        look.y = 0f;
                        if (look.sqrMagnitude > 0.01f)
                            masked.transform.rotation = Quaternion.LookRotation(look.normalized);
                    }

                    Plugin.Log?.LogInfo($"Post-spawn anchored Buddy outside the ship at {masked.transform.position}");
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"Post-spawn exterior placement: {ex.Message}");
                }

                var data = CrewmateRegistry.Register(masked, owner);
                CrewmateRegistry.EnsureNetworkKey(data);
                MaskedNeutralizePatches.Neutralize(masked, data);
                BuddyNameTag.Attach(masked, Plugin.CrewmateName?.Value ?? "Buddy");

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
                            if (RoundManager.Instance != null)
                                RoundManager.Instance.DespawnEnemyGameObject(data.Enemy.NetworkObject);
                            else
                                data.Enemy.NetworkObject.Despawn(true);
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
                LlmClient.CancelPendingRequests();
                _spawnedThisLanding = false;
                _spawnAttemptInProgress = false;
                _spawnRoutine = null;
                _pollAttempts = 0;
                _nextSpawnAllowedAt = Time.unscaledTime + 4f;
                _landedObservedAt = -1f;
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

        private static bool TrySnapToNavMesh(Vector3 pos, float maxDistance, out Vector3 snapped)
        {
            snapped = pos;
            try
            {
                if (!IsFinite(pos)) return false;
                if (!NavMesh.SamplePosition(pos, out var hit, maxDistance, NavMesh.AllAreas)) return false;
                snapped = hit.position;
                return IsFinite(snapped);
            }
            catch { return false; }
        }

        private static bool IsFinite(Vector3 value) =>
            !(float.IsNaN(value.x) || float.IsInfinity(value.x) ||
              float.IsNaN(value.y) || float.IsInfinity(value.y) ||
              float.IsNaN(value.z) || float.IsInfinity(value.z));

        /// <summary>
        /// Spawn next to the host player inside the ship (not outside on the moon).
        /// </summary>
        private static Vector3 GetSpawnPosition()
        {
            try
            {
                var sor = StartOfRound.Instance;

                // Buddy must appear on the moon, never inside or on top of the descending ship.
                // Exterior AI nodes are already baked onto the correct outside NavMesh.
                if (RoundManager.Instance != null)
                {
                    Vector3 ship = sor?.shipBounds != null
                        ? sor.shipBounds.bounds.center
                        : sor?.middleOfShipNode != null ? sor.middleOfShipNode.position : Vector3.zero;
                    GameObject bestExterior = null;
                    float bestDistance = float.MaxValue;
                    GameObject[] nodes = RoundManager.Instance.outsideAINodes;
                    if (nodes != null)
                    {
                        foreach (GameObject node in nodes)
                        {
                            if (node == null) continue;
                            Vector3 position = node.transform.position;
                            if (sor?.shipInnerRoomBounds != null && sor.shipInnerRoomBounds.bounds.Contains(position)) continue;
                            if (sor?.shipBounds != null && sor.shipBounds.bounds.Contains(position)) continue;
                            float distance = Vector3.Distance(ship, position);
                            if (distance < bestDistance)
                            {
                                bestDistance = distance;
                                bestExterior = node;
                            }
                        }
                    }
                    if (bestExterior != null && TrySnapToNavMesh(bestExterior.transform.position, 8f, out Vector3 exterior))
                    {
                        Plugin.Log?.LogInfo($"Spawn at exterior AI node {bestExterior.name}, {bestDistance:F1}m from ship");
                        return exterior;
                    }
                    Plugin.Log?.LogWarning("No exterior AI node is ready; Buddy will wait instead of spawning in the ship.");
                    return new Vector3(float.NaN, float.NaN, float.NaN);
                }

                // 1) Beside the host/local player (inside ship after land)
                var owner = FindPreferredOwner();
                if (owner != null)
                {
                    // Stand at player's right shoulder — close, visible, less door-clipping
                    Vector3 beside = owner.transform.position
                                     + owner.transform.right * 1.15f
                                     + owner.transform.forward * 0.35f
                                     + Vector3.up * 0.05f;
                    Vector3 snapped = SnapToNavMesh(beside, 6f);
                    Plugin.Log?.LogInfo($"Spawn beside player '{owner.playerUsername}' at {snapped}");
                    return snapped;
                }

                // 2) Ship interior anchors
                if (sor != null)
                {
                    if (sor.middleOfShipNode != null)
                    {
                        var mid = SnapToNavMesh(sor.middleOfShipNode.position, 8f);
                        Plugin.Log?.LogInfo($"Spawn at middleOfShipNode {mid}");
                        return mid;
                    }

                    if (sor.insideShipPositions != null && sor.insideShipPositions.Length > 0)
                    {
                        foreach (var t in sor.insideShipPositions)
                        {
                            if (t == null) continue;
                            var p = SnapToNavMesh(t.position, 6f);
                            Plugin.Log?.LogInfo($"Spawn at insideShipPosition {p}");
                            return p;
                        }
                    }

                    if (sor.shipInnerRoomBounds != null)
                    {
                        var c = sor.shipInnerRoomBounds.bounds.center;
                        c.y = sor.shipInnerRoomBounds.bounds.min.y + 0.1f;
                        var p = SnapToNavMesh(c, 8f);
                        Plugin.Log?.LogInfo($"Spawn at shipInnerRoomBounds {p}");
                        return p;
                    }
                }

                // 3) Last resort (still prefer ship-side over moon exterior)
                if (RoundManager.Instance != null && sor?.middleOfShipNode != null)
                {
                    var pos = RoundManager.Instance.GetNavMeshPosition(
                        sor.middleOfShipNode.position, default, 8f, -1);
                    if (pos != Vector3.zero) return pos;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"GetSpawnPosition: {ex.Message}");
            }
            return new Vector3(float.NaN, float.NaN, float.NaN);
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
                NetMessenger.TryRegisterHandlers();
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
