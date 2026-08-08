using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;

namespace LethalAICrewmate
{
    /// <summary>
    /// Recovers Buddy from long-session NavMesh stalls. A moving Buddy that makes no measurable
    /// progress gets a path rebuild first; repeated stalls while following fall back to the same
    /// safe beside-player teleport used by the normal area-transition logic.
    /// </summary>
    internal static class BuddyMovementWatchdog
    {
        private const float SampleInterval = 0.75f;
        private const float MinProgress = 0.35f;
        private const float PathRecoveryAfter = 3.0f;
        private const float TeleportRecoveryAfter = 6.5f;
        private const float RecoveryCooldown = 1.75f;

        private sealed class Track
        {
            public Vector3 LastPosition;
            public float LastProgressAt;
            public float LastRecoveryAt;
            public int Recoveries;
        }

        private static readonly Dictionary<ulong, Track> Tracks = new Dictionary<ulong, Track>();
        private static float _nextSampleAt;

        internal static void Tick()
        {
            if (!CrewmateSpawner.IsHost())
                return;
            if (Time.unscaledTime < _nextSampleAt)
                return;
            _nextSampleAt = Time.unscaledTime + SampleInterval;

            try
            {
                var live = new HashSet<ulong>();
                foreach (var data in CrewmateRegistry.All)
                {
                    if (data?.Enemy == null || data.Enemy.isEnemyDead || data.NetworkObjectId == 0)
                        continue;

                    live.Add(data.NetworkObjectId);
                    Check(data);
                }

                var stale = new List<ulong>();
                foreach (var id in Tracks.Keys)
                    if (!live.Contains(id)) stale.Add(id);
                foreach (ulong id in stale)
                    Tracks.Remove(id);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy movement watchdog: {ex.Message}");
            }
        }

        private static void Check(CrewmateData data)
        {
            var enemy = data.Enemy;
            Vector3 position = enemy.transform.position;
            float now = Time.unscaledTime;

            if (!Tracks.TryGetValue(data.NetworkObjectId, out var track) || track == null)
            {
                track = new Track
                {
                    LastPosition = position,
                    LastProgressAt = now,
                    LastRecoveryAt = -999f,
                    Recoveries = 0
                };
                Tracks[data.NetworkObjectId] = track;
                return;
            }

            bool shouldMove = enemy.moveTowardsDestination && data.ManualDestination != Vector3.zero;
            float destinationDistance = shouldMove
                ? Vector3.Distance(position, data.ManualDestination)
                : 0f;

            if (!shouldMove || destinationDistance <= 3.0f)
            {
                track.LastPosition = position;
                track.LastProgressAt = now;
                track.Recoveries = 0;
                return;
            }

            float moved = Vector3.Distance(position, track.LastPosition);
            track.LastPosition = position;
            if (moved >= MinProgress)
            {
                track.LastProgressAt = now;
                track.Recoveries = 0;
                return;
            }

            float stalledFor = now - track.LastProgressAt;
            if (stalledFor < PathRecoveryAfter || now - track.LastRecoveryAt < RecoveryCooldown)
                return;

            if (stalledFor >= TeleportRecoveryAfter)
            {
                try
                {
                    if (CrewmateAI.RecoverStalled(data))
                    {
                        track.LastPosition = enemy.transform.position;
                        track.LastProgressAt = now;
                        track.LastRecoveryAt = now;
                        track.Recoveries = 0;
                        Plugin.Log?.LogWarning($"Buddy movement watchdog: safe recovery after {stalledFor:F1}s without progress in state {data.State}.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"Buddy watchdog teleport recovery failed: {ex.Message}");
                }
            }

            RebuildPath(data);
            track.LastRecoveryAt = now;
            track.Recoveries++;
            Plugin.Log?.LogWarning($"Buddy movement watchdog: rebuilt path after {stalledFor:F1}s without progress (attempt {track.Recoveries}).");
        }

        private static void RebuildPath(CrewmateData data)
        {
            var enemy = data.Enemy;
            if (enemy == null) return;

            Vector3 destination = data.ManualDestination;
            if (NavMesh.SamplePosition(destination, out var destHit, 10f, NavMesh.AllAreas))
                destination = destHit.position;

            try
            {
                enemy.moveTowardsDestination = true;
                enemy.movingTowardsTargetPlayer = false;
                enemy.targetPlayer = null;
                enemy.SetDestinationToPosition(destination, checkForPath: false);
            }
            catch { }

            try
            {
                if (enemy.agent == null)
                    return;

                if (!enemy.agent.enabled)
                    enemy.agent.enabled = true;

                if (!enemy.agent.isOnNavMesh &&
                    NavMesh.SamplePosition(enemy.transform.position, out var here, 12f, NavMesh.AllAreas))
                {
                    enemy.agent.Warp(here.position);
                }

                if (enemy.agent.isOnNavMesh)
                {
                    enemy.agent.isStopped = true;
                    enemy.agent.ResetPath();
                    enemy.agent.speed = 5.0f;
                    enemy.agent.stoppingDistance = 2.5f;
                    enemy.agent.isStopped = false;
                    enemy.agent.SetDestination(destination);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy watchdog path rebuild: {ex.Message}");
            }
        }

        private static PlayerControllerB ResolveOwner(CrewmateData data)
        {
            if (data?.Owner != null && !data.Owner.isPlayerDead)
                return data.Owner;

            try
            {
                var players = StartOfRound.Instance?.allPlayerScripts;
                if (players == null || data?.Enemy == null)
                    return null;

                PlayerControllerB best = null;
                float bestDistance = float.MaxValue;
                foreach (var player in players)
                {
                    if (player == null || player.isPlayerDead)
                        continue;
                    float d = Vector3.Distance(data.Enemy.transform.position, player.transform.position);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = player;
                    }
                }
                return best;
            }
            catch
            {
                return data?.Owner;
            }
        }
    }

}
