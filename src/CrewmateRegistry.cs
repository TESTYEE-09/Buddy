using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using Unity.Netcode;

namespace LethalAICrewmate
{
    public enum CrewmateState
    {
        FollowOwner,
        Stay,
        ReturnToShip,
        FetchScrap,
        ScoutAhead
    }

    public class CrewmateData
    {
        public ulong NetworkObjectId;
        public MaskedPlayerEnemy Enemy;
        public CrewmateState State = CrewmateState.FollowOwner;
        public PlayerControllerB Owner;
        public GrabbableObject HeldItem;
        public GrabbableObject FetchTarget;
        public Vector3 StayPosition;
        public bool Neutralized;
        public float NextObservationAt;
        /// <summary>Scrap already counted via CollectNewScrapForThisRound this session.</summary>
        public readonly HashSet<int> ScrapCountedInstanceIds = new HashSet<int>();
        /// <summary>Last requested NavMesh destination, used by the movement watchdog.</summary>
        public Vector3 ManualDestination;
        /// <summary>Cooldown for a failed or completed facility/outside recovery.</summary>
        public float NextAreaTeleportAt;
        /// <summary>Spaced path rebuild attempts made during a persistent area mismatch.</summary>
        public int AreaPathRebuildAttempts;
        public float NextAreaPathRebuildAt;
        /// <summary>Stable shoulder preference so following does not visibly zig-zag.</summary>
        public float FollowSideOffset;
        public Vector3 ScoutDestination;
        public float ScoutStartedAt;
        public float ScoutArrivedAt;
        public bool ScoutReportSent;
        /// <summary>How long Buddy has been waiting for a legitimate area transition to resolve.</summary>
        public float AreaMismatchStartedAt;
        /// <summary>Natural idle timing; presentation only.</summary>
        public float NextIdleLookAt;
        public float NextIntentionalPauseAt;
        public float IntentionalPauseUntil;
        /// <summary>Follow-target death reaction state. Never populated from global death counts.</summary>
        public float FollowTargetDiedAt;
        public Vector3 FollowTargetDeathPosition;
        public bool FollowTargetDeathWitnessed;
        public string FollowTargetDeathName;
        public bool DeathReportPending;
        public float NextFollowAcquireAt;
        public bool DeliverFetchToOwner;
        public float NextDoorCheckAt;
        public float DoorWaitUntil;
        public float NextDoorWaitAllowedAt;
    }

    public static class CrewmateRegistry
    {
        private const int MaxKnownRemoteIds = 4;
        private static readonly Dictionary<ulong, CrewmateData> ById = new Dictionary<ulong, CrewmateData>();
        private static readonly HashSet<int> InstanceIds = new HashSet<int>();
        /// <summary>Network IDs known as crewmates on clients (and host). Survives until unregister/leave.</summary>
        private static readonly HashSet<ulong> KnownCrewmateNetIds = new HashSet<ulong>();

        public static IEnumerable<CrewmateData> All => ById.Values;

        public static bool IsCrewmate(EnemyAI enemy)
        {
            if (enemy == null) return false;
            try
            {
                if (enemy.IsSpawned)
                {
                    if (ById.ContainsKey(enemy.NetworkObjectId))
                        return true;
                    if (KnownCrewmateNetIds.Contains(enemy.NetworkObjectId))
                        return true;
                }
            }
            catch
            {
                // NetworkObject may not be ready
            }
            return InstanceIds.Contains(enemy.GetInstanceID());
        }

        public static bool IsCrewmate(MaskedPlayerEnemy enemy) => IsCrewmate((EnemyAI)enemy);

        public static bool TryGet(EnemyAI enemy, out CrewmateData data)
        {
            data = null;
            if (enemy == null) return false;
            try
            {
                if (enemy.IsSpawned && ById.TryGetValue(enemy.NetworkObjectId, out data))
                    return true;
            }
            catch
            {
                // ignore
            }

            var id = enemy.GetInstanceID();
            foreach (var kv in ById)
            {
                if (kv.Value.Enemy != null && kv.Value.Enemy.GetInstanceID() == id)
                {
                    data = kv.Value;
                    return true;
                }
            }
            return false;
        }

        public static CrewmateData Register(MaskedPlayerEnemy enemy, PlayerControllerB owner)
        {
            if (enemy == null) return null;

            var data = new CrewmateData
            {
                Enemy = enemy,
                Owner = owner,
                State = CrewmateState.FollowOwner,
                StayPosition = enemy.transform.position,
                Neutralized = false,
                NextObservationAt = Time.time + 30f,
                FollowSideOffset = Random.Range(-0.75f, 0.75f),
                NextIdleLookAt = Time.time + Random.Range(7f, 15f),
                NextIntentionalPauseAt = Time.time + Random.Range(45f, 80f)
            };

            InstanceIds.Add(enemy.GetInstanceID());

            try
            {
                if (enemy.IsSpawned)
                {
                    data.NetworkObjectId = enemy.NetworkObjectId;
                    ById[data.NetworkObjectId] = data;
                    KnownCrewmateNetIds.Add(data.NetworkObjectId);
                }
                else
                {
                    // Will be re-keyed once spawned; keep via instance id lookup
                    ById[unchecked((ulong)(uint)enemy.GetInstanceID())] = data;
                    data.NetworkObjectId = unchecked((ulong)(uint)enemy.GetInstanceID());
                }
            }
            catch
            {
                ById[unchecked((ulong)(uint)enemy.GetInstanceID())] = data;
                data.NetworkObjectId = unchecked((ulong)(uint)enemy.GetInstanceID());
            }

            Plugin.Log?.LogInfo($"Registered crewmate id={data.NetworkObjectId}");
            return data;
        }

        /// <summary>
        /// Client-side (or late-join) registration: mark NetworkObjectId as crewmate and neutralize if found.
        /// </summary>
        public static void RegisterRemote(ulong networkObjectId)
        {
            if (networkObjectId == 0) return;
            if (!KnownCrewmateNetIds.Contains(networkObjectId) && KnownCrewmateNetIds.Count >= MaxKnownRemoteIds)
            {
                Plugin.Log?.LogWarning("Rejected excess remote Buddy identity.");
                return;
            }
            KnownCrewmateNetIds.Add(networkObjectId);

            try
            {
                if (ById.ContainsKey(networkObjectId))
                    return;

                MaskedPlayerEnemy found = null;
                var nm = NetworkManager.Singleton;
                if (nm?.SpawnManager != null &&
                    nm.SpawnManager.SpawnedObjects.TryGetValue(networkObjectId, out var netObj) && netObj != null)
                    found = netObj.GetComponent<MaskedPlayerEnemy>();

                if (found == null)
                {
                    Plugin.Log?.LogInfo($"Remote crewmate id={networkObjectId} noted (body not found yet).");
                    return;
                }

                if (IsCrewmate(found) && TryGet(found, out _))
                    return;

                var data = Register(found, null);
                EnsureNetworkKey(data);
                MaskedNeutralizePatches.Neutralize(found, data);
                BuddyNameTag.Attach(found, Plugin.CrewmateName?.Value ?? "Buddy");
                Plugin.Log?.LogInfo($"Remote crewmate id={networkObjectId} registered and neutralized.");
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"RegisterRemote: {ex.Message}");
            }
        }

        public static void UnregisterRemote(ulong networkObjectId)
        {
            KnownCrewmateNetIds.Remove(networkObjectId);
            Unregister(networkObjectId);
        }

        public static void EnsureNetworkKey(CrewmateData data)
        {
            if (data?.Enemy == null) return;
            try
            {
                if (!data.Enemy.IsSpawned) return;
                var realId = data.Enemy.NetworkObjectId;
                if (data.NetworkObjectId == realId && ById.ContainsKey(realId))
                {
                    KnownCrewmateNetIds.Add(realId);
                    return;
                }

                if (ById.ContainsKey(data.NetworkObjectId) && data.NetworkObjectId != realId)
                    ById.Remove(data.NetworkObjectId);

                data.NetworkObjectId = realId;
                ById[realId] = data;
                KnownCrewmateNetIds.Add(realId);
            }
            catch
            {
                // ignore
            }
        }

        public static void Unregister(ulong networkObjectId)
        {
            KnownCrewmateNetIds.Remove(networkObjectId);
            if (ById.TryGetValue(networkObjectId, out var data))
            {
                if (data.Enemy != null)
                    InstanceIds.Remove(data.Enemy.GetInstanceID());
                ById.Remove(networkObjectId);
            }
        }

        public static void UnregisterAll()
        {
            ById.Clear();
            InstanceIds.Clear();
            KnownCrewmateNetIds.Clear();
        }

        public static CrewmateData GetPrimary()
        {
            foreach (var d in ById.Values)
            {
                if (d?.Enemy != null && !d.Enemy.isEnemyDead)
                    return d;
            }
            return null;
        }

        public static void SetState(CrewmateData data, CrewmateState state)
        {
            if (data == null) return;
            data.State = state;
            if (state == CrewmateState.Stay && data.Enemy != null)
                data.StayPosition = data.Enemy.transform.position;
            if (state != CrewmateState.FetchScrap)
            {
                data.FetchTarget = null;
                data.DeliverFetchToOwner = false;
            }
            if (state != CrewmateState.ScoutAhead)
            {
                data.ScoutDestination = Vector3.zero;
                data.ScoutStartedAt = 0f;
                data.ScoutArrivedAt = 0f;
                data.ScoutReportSent = false;
            }
            Plugin.Log?.LogInfo($"Crewmate state -> {state}");
        }

        /// <summary>If a masked just spawned that matches a known remote id, register it.</summary>
        public static void TryBindKnown(MaskedPlayerEnemy enemy)
        {
            if (enemy == null || !enemy.IsSpawned) return;
            try
            {
                var id = enemy.NetworkObjectId;
                if (!KnownCrewmateNetIds.Contains(id)) return;
                if (TryGet(enemy, out var existing) && existing != null) return;
                var data = Register(enemy, null);
                EnsureNetworkKey(data);
                MaskedNeutralizePatches.Neutralize(enemy, data);
                BuddyNameTag.Attach(enemy, Plugin.CrewmateName?.Value ?? "Buddy");
                Plugin.Log?.LogInfo($"Late-bound remote crewmate id={id}.");
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"TryBindKnown: {ex.Message}");
            }
        }
    }
}
