using System;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>Deterministic emergency warning; it never waits for the LLM or player input.</summary>
    internal static class BuddyDangerCallout
    {
        private const float DangerDistance = 7.5f;
        private const float ScanInterval = 0.25f;
        private const float CooldownSeconds = 25f;

        private static float _nextScanAt;
        private static float _nextCalloutAt;
        private static int _lastThreatId;

        internal static void Tick()
        {
            try
            {
                if (!CrewmateSpawner.IsHost() || Time.unscaledTime < _nextScanAt) return;
                _nextScanAt = Time.unscaledTime + ScanInterval;
                if (StartOfRound.Instance == null || StartOfRound.Instance.inShipPhase) return;
                if (CrewmateRegistry.GetPrimary()?.Enemy == null) return;

                EnemyAI threat = FindImmediateThreat();
                if (threat == null)
                {
                    _lastThreatId = 0;
                    return;
                }

                int id = threat.GetInstanceID();
                if (Time.unscaledTime < _nextCalloutAt || id == _lastThreatId) return;
                _lastThreatId = id;
                _nextCalloutAt = Time.unscaledTime + CooldownSeconds;

                string enemyName = threat.enemyType?.enemyName;
                if (string.IsNullOrWhiteSpace(enemyName)) enemyName = "monster";
                string display = "RUN! " + enemyName + " is right on us!";
                Vector3 position = ResolveBuddyPosition();
                ulong netId = CrewmateRegistry.GetPrimary()?.NetworkObjectId ?? 0;
                string buddyName = Plugin.CrewmateName?.Value ?? "Buddy";

                ProximityChat.TryShowLocal(buddyName, display, position);
                NetMessenger.BroadcastCrewmateChat(buddyName, display, position, netId);
                BuddyTts.Speak("[shout] " + display, position);
                Plugin.Log?.LogWarning($"Buddy emergency callout: {enemyName} within {DangerDistance:F1}m.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy danger callout: {ex.Message}");
            }
        }

        private static EnemyAI FindImmediateThreat()
        {
            EnemyAI nearest = null;
            float nearestDistance = DangerDistance;
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            foreach (var enemy in UnityEngine.Object.FindObjectsOfType<EnemyAI>())
            {
                if (enemy == null || enemy.isEnemyDead || CrewmateRegistry.IsCrewmate(enemy)) continue;
                string name = (enemy.enemyType?.enemyName ?? enemy.GetType().Name).ToLowerInvariant();
                if (name.Contains("manticoil") || name.Contains("roaming locust")) continue;

                if (players == null) continue;
                foreach (var player in players)
                {
                    if (player == null || !player.isPlayerControlled || player.isPlayerDead) continue;
                    float distance = Vector3.Distance(enemy.transform.position, player.transform.position);
                    if (distance <= nearestDistance)
                    {
                        nearestDistance = distance;
                        nearest = enemy;
                    }
                }
            }
            return nearest;
        }

        private static Vector3 ResolveBuddyPosition()
        {
            var buddy = CrewmateRegistry.GetPrimary();
            return buddy?.Enemy != null ? buddy.Enemy.transform.position + Vector3.up * 1.6f : Vector3.zero;
        }
    }
}
