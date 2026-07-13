using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    public enum CrewmateState
    {
        FollowOwner,
        Stay,
        ReturnToShip,
        FetchScrap
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
    }

    public static class CrewmateRegistry
    {
        private static readonly Dictionary<ulong, CrewmateData> ById = new Dictionary<ulong, CrewmateData>();
        private static readonly HashSet<int> InstanceIds = new HashSet<int>();

        public static IEnumerable<CrewmateData> All => ById.Values;

        public static bool IsCrewmate(EnemyAI enemy)
        {
            if (enemy == null) return false;
            try
            {
                if (enemy.IsSpawned && ById.ContainsKey(enemy.NetworkObjectId))
                    return true;
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
                NextObservationAt = Time.time + 30f
            };

            InstanceIds.Add(enemy.GetInstanceID());

            try
            {
                if (enemy.IsSpawned)
                {
                    data.NetworkObjectId = enemy.NetworkObjectId;
                    ById[data.NetworkObjectId] = data;
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

        public static void EnsureNetworkKey(CrewmateData data)
        {
            if (data?.Enemy == null) return;
            try
            {
                if (!data.Enemy.IsSpawned) return;
                var realId = data.Enemy.NetworkObjectId;
                if (data.NetworkObjectId == realId && ById.ContainsKey(realId)) return;

                if (ById.ContainsKey(data.NetworkObjectId) && data.NetworkObjectId != realId)
                    ById.Remove(data.NetworkObjectId);

                data.NetworkObjectId = realId;
                ById[realId] = data;
            }
            catch
            {
                // ignore
            }
        }

        public static void Unregister(ulong networkObjectId)
        {
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
            }
            Plugin.Log?.LogInfo($"Crewmate state -> {state}");
        }
    }
}
