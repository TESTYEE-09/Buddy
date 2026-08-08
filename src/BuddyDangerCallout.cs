using System;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>Deterministic emergency warning; it never waits for the LLM or player input.</summary>
    internal static class BuddyDangerCallout
    {
        private const float WarningDistance = 12.5f;
        private const float DangerDistance = 7.5f;
        private const float ScanInterval = 0.25f;
        private const float WarningCooldownSeconds = 10f;
        private const float DangerCooldownSeconds = 12f;

        private static float _nextScanAt;
        private static float _nextCalloutAt;
        private static int _lastThreatId;
        private static bool _warningSent;
        private static bool _dangerSent;

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
                    _warningSent = false;
                    _dangerSent = false;
                    return;
                }

                int id = threat.GetInstanceID();
                if (id != _lastThreatId)
                {
                    _lastThreatId = id;
                    _warningSent = false;
                    _dangerSent = false;
                }

                float distance = ResolveNearestPlayerDistance(threat);
                if (distance <= DangerDistance)
                {
                    if (_dangerSent || Time.unscaledTime < _nextCalloutAt) return;
                    _dangerSent = true;
                    _nextCalloutAt = Time.unscaledTime + DangerCooldownSeconds;
                }
                else
                {
                    if (_warningSent || Time.unscaledTime < _nextCalloutAt) return;
                    _warningSent = true;
                    _nextCalloutAt = Time.unscaledTime + WarningCooldownSeconds;
                }

                string enemyName = threat.enemyType?.enemyName;
                if (string.IsNullOrWhiteSpace(enemyName)) enemyName = "monster";
                string display = distance <= DangerDistance
                    ? "RUN! " + enemyName + " is right on us!"
                    : "Wait—did you see that? " + enemyName + " is close. Keep moving.";
                Vector3 position = ResolveBuddyPosition();
                ulong netId = CrewmateRegistry.GetPrimary()?.NetworkObjectId ?? 0;
                string buddyName = Plugin.CrewmateName?.Value ?? "Buddy";

                ProximityChat.TryShowLocal(buddyName, display, position);
                NetMessenger.BroadcastCrewmateChat(buddyName, display, position, netId);
                BuddyTts.Speak(distance <= DangerDistance ? "[shout] " + display : display, position);
                ResponseJournal.RecordDirect("callout", "system", "deterministic danger callout", display, enemyName + " within " + distance.ToString("F1") + "m");
                Plugin.Log?.LogWarning($"Buddy danger callout: {enemyName} within {distance:F1}m.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy danger callout: {ex.Message}");
            }
        }

        private static EnemyAI FindImmediateThreat()
        {
            EnemyAI nearest = null;
            float nearestDistance = WarningDistance;
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

        private static float ResolveNearestPlayerDistance(EnemyAI threat)
        {
            float nearest = float.MaxValue;
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || threat == null) return nearest;
            foreach (var player in players)
            {
                if (player == null || !player.isPlayerControlled || player.isPlayerDead) continue;
                nearest = Mathf.Min(nearest, Vector3.Distance(threat.transform.position, player.transform.position));
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
