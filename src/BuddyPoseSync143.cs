using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// v1.4.3 continuous server-authoritative pose replication for Buddy.
    /// The vanilla Masked prefab is not reliable enough for our custom AI movement/teleports on
    /// remote peers, so the host publishes a lightweight position/yaw/outside snapshot at 8 Hz.
    /// Clients only consume snapshots from the server and never author Buddy movement.
    /// </summary>
    internal static class BuddyPoseSync143
    {
        private const string MsgPose = "LethalAICrewmate_Pose143";
        private const float SendInterval = 0.125f;
        private const float SnapDistance = 7f;
        private const float PoseExpirySeconds = 3f;

        private sealed class RemotePose
        {
            public Vector3 Position;
            public float Yaw;
            public bool Outside;
            public bool Moving;
            public uint Sequence;
            public float ReceivedAt;
            public bool Applied;
            public bool LastAppliedOutside;
            public float LastDiagnosticAt;
        }

        private static readonly Dictionary<ulong, RemotePose> RemotePoses = new Dictionary<ulong, RemotePose>();
        private static bool _registered;
        private static NetworkManager _registeredOn;
        private static NetworkManager _sessionManager;
        private static float _nextSendAt;
        private static uint _sequence;

        internal static void Tick()
        {
            try
            {
                RegisterHandler();

                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsListening)
                {
                    ResetSession(nm);
                    return;
                }

                if (_sessionManager != nm)
                    ResetSession(nm);

                if (nm.IsServer)
                {
                    if (Time.unscaledTime >= _nextSendAt)
                    {
                        _nextSendAt = Time.unscaledTime + SendInterval;
                        SendAllPoses(nm);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy pose sync tick: {ex.Message}");
            }
        }

        internal static void LateTick()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsListening && nm.IsClient && !nm.IsServer)
                    ApplyRemotePoses(nm);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy late pose apply: {ex.Message}");
            }
        }

        internal static void SendImmediate(CrewmateData data)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || !nm.IsListening || data?.Enemy == null)
                    return;
                SendPose(nm, data);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy immediate pose sync: {ex.Message}");
            }
        }

        private static void ResetSession(NetworkManager manager)
        {
            _sessionManager = manager;
            _nextSendAt = 0f;
            _sequence = 0;
            RemotePoses.Clear();
        }

        private static void RegisterHandler()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null)
                return;
            if (_registered && _registeredOn == nm)
                return;

            try
            {
                if (_registeredOn != null && _registeredOn.CustomMessagingManager != null)
                {
                    try { _registeredOn.CustomMessagingManager.UnregisterNamedMessageHandler(MsgPose); } catch { }
                }
            }
            catch { }

            _registered = false;
            _registeredOn = nm;
            try { nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgPose); } catch { }
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgPose, OnPose);
            _registered = true;
            Plugin.Log?.LogInfo("Registered v1.4.3 Buddy pose-sync handler.");
        }

        private static void SendAllPoses(NetworkManager nm)
        {
            if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
                return;
            if (!NetMessenger.IsHostSessionReadyForBuddy())
                return;

            foreach (var data in CrewmateRegistry.All)
            {
                if (data?.Enemy == null || data.Enemy.isEnemyDead || data.NetworkObjectId == 0)
                    continue;
                SendPose(nm, data);
            }
        }

        private static void SendPose(NetworkManager nm, CrewmateData data)
        {
            if (nm == null || data?.Enemy == null || data.NetworkObjectId == 0)
                return;

            _sequence++;
            if (_sequence == 0) _sequence = 1;

            var enemy = data.Enemy;
            Vector3 position = enemy.transform.position;
            float yaw = enemy.transform.eulerAngles.y;
            byte outside = enemy.isOutside ? (byte)1 : (byte)0;
            byte moving = enemy.moveTowardsDestination ? (byte)1 : (byte)0;

            foreach (ulong clientId in NetMessenger.CompatibleClientIds())
            {
                using (var writer = new FastBufferWriter(80, Allocator.Temp))
                {
                    writer.WriteValueSafe(data.NetworkObjectId);
                    writer.WriteValueSafe(_sequence);
                    writer.WriteValueSafe(position);
                    writer.WriteValueSafe(yaw);
                    writer.WriteValueSafe(outside);
                    writer.WriteValueSafe(moving);
                    nm.CustomMessagingManager.SendNamedMessage(
                        MsgPose,
                        clientId,
                        writer,
                        NetworkDelivery.UnreliableSequenced);
                }
            }
        }

        private static void OnPose(ulong senderId, FastBufferReader reader)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsClient || nm.IsServer || !NetMessenger.CanAcceptServerStateMessage(senderId))
                    return;

                reader.ReadValueSafe(out ulong netId);
                reader.ReadValueSafe(out uint sequence);
                reader.ReadValueSafe(out Vector3 position);
                reader.ReadValueSafe(out float yaw);
                reader.ReadValueSafe(out byte outside);
                reader.ReadValueSafe(out byte moving);

                if (netId == 0 || !IsFinite(position) || float.IsNaN(yaw) || float.IsInfinity(yaw))
                    return;

                if (RemotePoses.TryGetValue(netId, out var previous) && previous != null &&
                    sequence != 0 && previous.Sequence != 0 && sequence <= previous.Sequence &&
                    previous.Sequence - sequence < uint.MaxValue / 2)
                    return;

                RemotePoses[netId] = new RemotePose
                {
                    Position = position,
                    Yaw = yaw,
                    Outside = outside != 0,
                    Moving = moving != 0,
                    Sequence = sequence,
                    ReceivedAt = Time.unscaledTime,
                    Applied = previous?.Applied ?? false,
                    LastAppliedOutside = previous?.LastAppliedOutside ?? (outside != 0),
                    LastDiagnosticAt = previous?.LastDiagnosticAt ?? 0f
                };

                // This also makes the ID safe before the Masked body finishes spawning locally.
                CrewmateRegistry.RegisterRemote(netId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy pose receive: {ex.Message}");
            }
        }

        private static void ApplyRemotePoses(NetworkManager nm)
        {
            if (nm?.SpawnManager == null || RemotePoses.Count == 0)
                return;

            var stale = new List<ulong>();
            foreach (var kv in RemotePoses)
            {
                ulong netId = kv.Key;
                RemotePose pose = kv.Value;
                if (pose == null || Time.unscaledTime - pose.ReceivedAt > PoseExpirySeconds)
                {
                    stale.Add(netId);
                    continue;
                }

                if (!nm.SpawnManager.SpawnedObjects.TryGetValue(netId, out var netObj) || netObj == null)
                {
                    if (Time.unscaledTime - pose.LastDiagnosticAt >= 10f)
                    {
                        pose.LastDiagnosticAt = Time.unscaledTime;
                        Plugin.Log?.LogInfo($"Buddy pose waiting for body binding netId={netId} packetAge={Time.unscaledTime - pose.ReceivedAt:F2}s.");
                    }
                    continue;
                }

                var enemy = netObj.GetComponent<MaskedPlayerEnemy>();
                if (enemy == null)
                    continue;

                CrewmateRegistry.TryBindKnown(enemy);

                try
                {
                    enemy.targetPlayer = null;
                    enemy.movingTowardsTargetPlayer = false;
                    enemy.moveTowardsDestination = false;
                    enemy.inKillAnimation = false;
                    if (!pose.Applied || enemy.isOutside != pose.Outside)
                    {
                        try { enemy.SetEnemyOutside(pose.Outside); } catch { }
                    }
                    enemy.isOutside = pose.Outside;
                }
                catch { }

                Vector3 current = enemy.transform.position;
                float distance = Vector3.Distance(current, pose.Position);
                bool areaChanged = pose.Applied && pose.LastAppliedOutside != pose.Outside;
                bool hardSnap = !pose.Applied || areaChanged || distance >= SnapDistance;
                Vector3 next = hardSnap
                    ? pose.Position
                    : Vector3.Lerp(current, pose.Position, Mathf.Clamp01(Time.unscaledDeltaTime * 12f));

                try
                {
                    if (enemy.agent != null)
                    {
                        if (enemy.agent.enabled && enemy.agent.isOnNavMesh)
                            enemy.agent.isStopped = true;
                        enemy.agent.enabled = false;
                    }
                }
                catch { }

                enemy.transform.position = next;
                Quaternion targetRot = Quaternion.Euler(0f, pose.Yaw, 0f);
                enemy.transform.rotation = hardSnap
                    ? targetRot
                    : Quaternion.Slerp(enemy.transform.rotation, targetRot, Mathf.Clamp01(Time.unscaledDeltaTime * 10f));

                if (hardSnap)
                {
                    string reason = !pose.Applied ? "first authoritative pose" : areaChanged ? "inside/outside transition" : $"drift {distance:F1}m";
                    Plugin.Log?.LogInfo($"Buddy pose hard-snap netId={netId} reason={reason} packetAge={Time.unscaledTime - pose.ReceivedAt:F2}s position={pose.Position}.");
                }
                pose.Applied = true;
                pose.LastAppliedOutside = pose.Outside;
            }

            foreach (ulong id in stale)
                RemotePoses.Remove(id);
        }

        private static bool IsFinite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsInfinity(v.x) ||
                     float.IsNaN(v.y) || float.IsInfinity(v.y) ||
                     float.IsNaN(v.z) || float.IsInfinity(v.z));
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(PluginHost), "Update")]
    internal static class Patch_PluginHost_BuddyPose143
    {
        [HarmonyLib.HarmonyPostfix]
        private static void Postfix()
        {
            BuddyPoseSync143.Tick();
        }
    }
}
