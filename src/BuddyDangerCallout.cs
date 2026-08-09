using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>Deterministic emergency warning; it never waits for the LLM or player input.</summary>
    internal static class BuddyDangerCallout
    {
        private enum ThreatSeverity { Low = 1, Moderate = 2, High = 3, Lethal = 4 }

        private const float WarningDistance = 12.5f;
        private const float DangerDistance = 7.5f;
        private const float ScanInterval = 0.25f;
        private const float WarningCooldownSeconds = 18f;
        private const float DangerCooldownSeconds = 18f;
        private const float SameMonsterCooldownSeconds = 120f;

        private static float _nextScanAt;
        private static float _nextCalloutAt;
        private static int _lastThreatId;
        private static bool _warningSent;
        private static bool _dangerSent;
        private static readonly Dictionary<int, float> LastCalloutByMonster = new Dictionary<int, float>();

        internal static void ResetSession()
        {
            LastCalloutByMonster.Clear();
            _nextScanAt = 0f;
            _nextCalloutAt = 0f;
            _lastThreatId = 0;
            _warningSent = false;
            _dangerSent = false;
        }

        internal static void Tick()
        {
            try
            {
                if (!CrewmateSpawner.IsHost() || Time.unscaledTime < _nextScanAt) return;
                _nextScanAt = Time.unscaledTime + ScanInterval;
                if (StartOfRound.Instance == null || StartOfRound.Instance.inShipPhase) return;
                if (CrewmateRegistry.GetPrimary()?.Enemy == null) return;

                EnemyAI threat = FindImmediateThreat(out ThreatSeverity severity);
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
                if (LastCalloutByMonster.TryGetValue(id, out float lastForMonster) &&
                    Time.unscaledTime - lastForMonster < SameMonsterCooldownSeconds)
                    return;

                bool immediate = distance <= DangerDistance;
                if (immediate)
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
                bool activelyThreatening = threat.targetPlayer != null || threat.movingTowardsTargetPlayer;
                string display = NaturalCallout(enemyName, severity, immediate, activelyThreatening);
                LastCalloutByMonster[id] = Time.unscaledTime;
                Vector3 position = ResolveBuddyPosition();
                ulong netId = CrewmateRegistry.GetPrimary()?.NetworkObjectId ?? 0;
                string buddyName = Plugin.CrewmateName?.Value ?? "Buddy";

                ProximityChat.TryShowLocal(buddyName, display, position);
                NetMessenger.BroadcastCrewmateChat(buddyName, display, position, netId);
                BuddyTts.Speak(immediate && severity >= ThreatSeverity.High ? "[shout] " + display : display, position);
                ResponseJournal.RecordDirect("callout", "system", "deterministic danger callout", display,
                    enemyName + " severity=" + severity + " within " + distance.ToString("F1") + "m");
                Plugin.Log?.LogWarning($"Buddy danger callout: {enemyName} severity={severity} within {distance:F1}m.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy danger callout: {ex.Message}");
            }
        }

        private static EnemyAI FindImmediateThreat(out ThreatSeverity selectedSeverity)
        {
            EnemyAI selected = null;
            selectedSeverity = ThreatSeverity.Low;
            float bestScore = float.MinValue;
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            foreach (EnemyAI enemy in UnityEngine.Object.FindObjectsOfType<EnemyAI>())
            {
                if (enemy == null || enemy.isEnemyDead || CrewmateRegistry.IsCrewmate(enemy)) continue;
                string name = (enemy.enemyType?.enemyName ?? enemy.GetType().Name).ToLowerInvariant();
                if (name.Contains("manticoil") || name.Contains("locust") || name.Contains("circuit bee")) continue;
                ThreatSeverity severity = ClassifyThreat(enemy, name);

                if (players == null) continue;
                foreach (PlayerControllerB player in players)
                {
                    if (player == null || !player.isPlayerControlled || player.isPlayerDead) continue;
                    float distance = Vector3.Distance(enemy.transform.position, player.transform.position);
                    if (distance > WarningDistance) continue;
                    float score = (int)severity * 20f - distance;
                    if (enemy.targetPlayer != null || enemy.movingTowardsTargetPlayer) score += 12f;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    selected = enemy;
                    selectedSeverity = severity;
                }
            }
            return selected;
        }

        private static ThreatSeverity ClassifyThreat(EnemyAI enemy, string name)
        {
            if (enemy != null && (enemy.targetPlayer != null || enemy.movingTowardsTargetPlayer)) return ThreatSeverity.Lethal;
            if (name.Contains("jester") || name.Contains("coil-head") || name.Contains("coilhead") ||
                name.Contains("bracken") || name.Contains("ghost girl") || name.Contains("forest giant") ||
                name.Contains("eyeless dog") || name.Contains("earth leviathan") || name.Contains("old bird") ||
                name.Contains("radmech")) return ThreatSeverity.Lethal;
            if (name.Contains("thumper") || name.Contains("nutcracker") || name.Contains("butler") ||
                name.Contains("bunker spider") || name.Contains("masked") || name.Contains("baboon hawk") ||
                name.Contains("kidnapper fox") || name.Contains("maneater")) return ThreatSeverity.High;
            if (name.Contains("hoarding bug") || name.Contains("snare flea") || name.Contains("spore lizard") ||
                name.Contains("slime") || name.Contains("tulip snake")) return ThreatSeverity.Low;
            return ThreatSeverity.Moderate;
        }

        private static string NaturalCallout(string enemyName, ThreatSeverity severity, bool immediate, bool activelyThreatening)
        {
            if (severity == ThreatSeverity.Lethal && (immediate || activelyThreatening))
            {
                string[] terrified =
                {
                    "Shit - " + enemyName + ", right there!",
                    enemyName + "! Move, move, move!",
                    "Oh shit, " + enemyName + " - run!",
                    "I'm actually scared. " + enemyName + "! Run!"
                };
                return terrified[UnityEngine.Random.Range(0, terrified.Length)];
            }
            if (severity >= ThreatSeverity.High && immediate)
            {
                string[] urgent =
                {
                    enemyName + " close - back up!",
                    "Watch it, " + enemyName + " right there!",
                    enemyName + "! Don't let it get close."
                };
                return urgent[UnityEngine.Random.Range(0, urgent.Length)];
            }
            string[] warning =
            {
                enemyName + " nearby. Keep moving.",
                "Careful - " + enemyName + " close.",
                "I saw a " + enemyName + "."
            };
            return warning[UnityEngine.Random.Range(0, warning.Length)];
        }

        private static float ResolveNearestPlayerDistance(EnemyAI threat)
        {
            float nearest = float.MaxValue;
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || threat == null) return nearest;
            foreach (PlayerControllerB player in players)
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
