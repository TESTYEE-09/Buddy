using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Emergency threat detection. The decision to warn is deterministic and never waits on the
    /// model: scanning, severity and cooldowns are all local. The wording is not - the confirmed
    /// fact is handed to the model so the warning sounds like Buddy rather than like one of three
    /// canned strings. A warning that cannot be handed over keeps its cooldown and retries.
    /// </summary>
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
                if (immediate && (_dangerSent || Time.unscaledTime < _nextCalloutAt)) return;
                if (!immediate && (_warningSent || Time.unscaledTime < _nextCalloutAt)) return;

                string enemyName = threat.enemyType?.enemyName;
                if (string.IsNullOrWhiteSpace(enemyName)) enemyName = "monster";
                bool activelyThreatening = threat.targetPlayer != null || threat.movingTowardsTargetPlayer;

                // The words are the model's, never ours. This used to pick from a hardcoded list
                // of canned strings, which is why every warning sounded the same and sounded
                // canned. The detection stays exactly as deterministic as before - what changed is
                // that we hand Buddy the confirmed fact and let him say it however he says things.
                string fact = enemyName + " " + Mathf.RoundToInt(distance) + " metres away" +
                              (activelyThreatening ? ", coming at the crew" : "") +
                              (immediate && severity >= ThreatSeverity.High
                                  ? ". Lethal and far too close - say so fast and scared."
                                  : severity >= ThreatSeverity.High
                                      ? ". Serious - warn them now, urgently."
                                      : ". Worth one short warning.");

                // Only burn the cooldowns if the warning was actually accepted. Handing the line to
                // the model made this failable in a way a hardcoded string never was: the queue can
                // be full, or a player can start talking and clear it. Marking the monster spoken-for
                // before knowing that would silence this threat for two full minutes over a warning
                // nobody ever heard - the one failure mode that actually gets somebody killed.
                if (!LlmClient.TryEnqueueObservation(fact))
                {
                    // Journal the miss as well as logging it. "Buddy never warned me about the
                    // thing that killed me" is the single hardest report to diagnose after the
                    // fact, and a warning that was detected but never handed over leaves no other
                    // trace: the observation turn that would normally record it never existed.
                    ResponseJournal.RecordDirect("callout", "system", fact,
                        "[not spoken - Buddy was busy; will retry]",
                        enemyName + " severity=" + severity + " within " + distance.ToString("F1") + "m");
                    Plugin.Log?.LogWarning($"Buddy danger callout dropped (busy); will retry: {enemyName} within {distance:F1}m.");
                    return;
                }

                LastCalloutByMonster[id] = Time.unscaledTime;
                if (immediate)
                {
                    _dangerSent = true;
                    _nextCalloutAt = Time.unscaledTime + DangerCooldownSeconds;
                }
                else
                {
                    _warningSent = true;
                    _nextCalloutAt = Time.unscaledTime + WarningCooldownSeconds;
                }
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
