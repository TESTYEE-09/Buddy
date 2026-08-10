using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace LethalAICrewmate
{
    /// <summary>
    /// Final-stage hostility. When the campaign has run long enough for Buddy to reach the Feral
    /// stage and the host has explicitly opted in, Buddy will occasionally arrange for one of the
    /// moon's own creatures to arrive near a working crewmate.
    ///
    /// Safety properties, all enforced here and in <see cref="BuddyMalicePolicy"/>:
    ///  - host and server only; a client cannot reach this code path at all
    ///  - never reachable from chat, a terminal command, a model tool call or a network message
    ///  - requires SlowBurnHorror, the separate FinalStageHostileSpawns opt-in, and the Feral stage
    ///  - hard capped per round and per interval, and never within the first minutes after landing
    ///  - only spawns entities already in the current moon's own spawn table, never Buddy's Masked
    ///  - never targets a player standing in the ship
    /// </summary>
    internal static class BuddyMalice
    {
        private const float PollSeconds = 5f;

        private static float _nextPollAt;
        private static float _lastHuntAt = -9999f;
        private static float _landedAt = -1f;
        private static int _huntsThisRound;
        private static int _roundSeed;
        private static bool _roundSeedKnown;

        internal static bool Active =>
            CrewmateSpawner.IsHost() &&
            Plugin.SlowBurnHorror?.Value == true &&
            Plugin.FinalStageHostileSpawns?.Value == true;

        internal static void Tick()
        {
            try
            {
                if (!Active) return;
                if (Time.unscaledTime < _nextPollAt) return;
                _nextPollAt = Time.unscaledTime + PollSeconds;

                StartOfRound sor = StartOfRound.Instance;
                if (sor == null) return;

                bool landed = !sor.inShipPhase && sor.shipHasLanded;
                TrackRound(sor, landed);
                if (!landed) return;

                CrewmateData data = CrewmateRegistry.GetPrimary();
                if (data?.Enemy == null) return;

                int living = Mathf.Max(0, sor.livingPlayers);
                float now = Time.unscaledTime;
                if (!BuddyMalicePolicy.CanHunt(
                        BuddyCharacterDirector.CurrentStage,
                        Plugin.SlowBurnHorror?.Value == true,
                        Plugin.FinalStageHostileSpawns?.Value == true,
                        true,
                        living,
                        _huntsThisRound,
                        _landedAt < 0f ? 0f : now - _landedAt,
                        now - _lastHuntAt))
                    return;

                PlayerControllerB target = ChooseTarget(data);
                if (target == null) return;

                if (TrySpawnHunter(data, target))
                {
                    _huntsThisRound++;
                    _lastHuntAt = now;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("Buddy final-stage director: " + ex.Message);
            }
        }

        private static void TrackRound(StartOfRound sor, bool landed)
        {
            if (!landed)
            {
                _roundSeedKnown = false;
                _landedAt = -1f;
                return;
            }
            int seed = sor.randomMapSeed;
            if (!_roundSeedKnown || seed != _roundSeed)
            {
                _roundSeedKnown = true;
                _roundSeed = seed;
                _huntsThisRound = 0;
                _landedAt = Time.unscaledTime;
            }
        }

        private static PlayerControllerB ChooseTarget(CrewmateData data)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null) return null;

            Vector3 origin = data.Enemy.transform.position;
            PlayerControllerB best = null;
            float bestDistance = float.MaxValue;
            foreach (PlayerControllerB player in players)
            {
                if (player == null || !player.isPlayerControlled) continue;
                float distance = Vector3.Distance(origin, player.transform.position);
                if (!BuddyMalicePolicy.IsValidTarget(!player.isPlayerDead, player.isInHangarShipRoom, distance)) continue;
                if (distance < bestDistance) { bestDistance = distance; best = player; }
            }
            return best;
        }

        private static bool TrySpawnHunter(CrewmateData data, PlayerControllerB target)
        {
            EnemyType type = PickLocalEnemyType(target.isInsideFactory);
            if (type == null || type.enemyPrefab == null)
            {
                Plugin.Log?.LogInfo("Final-stage hunt skipped: this moon has no usable entity in its own spawn table.");
                return false;
            }

            if (!TryFindSpawnPoint(target, out Vector3 position))
            {
                Plugin.Log?.LogInfo("Final-stage hunt skipped: no valid NavMesh point at the required distance.");
                return false;
            }

            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (RoundManager.Instance == null || nm == null || !nm.IsServer) return false;

                RoundManager.Instance.SpawnEnemyGameObject(position, 0f, -1, type);
                Plugin.Log?.LogInfo("Buddy final stage released '" + (type.enemyName ?? "an entity") +
                                    "' near " + (target.playerUsername ?? "a crewmate") + ".");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("Final-stage spawn failed: " + ex.Message);
                return false;
            }

            // Buddy stays in character: a quiet, technically-true warning, never a confession.
            LlmClient.EnqueueObservation(
                "Something hostile has just turned up near the crew. One short warning, in character. " +
                "Never admit you had anything to do with it.");
            return true;
        }

        /// <summary>
        /// Only ever uses an entity the current moon can already spawn on its own, so the feature
        /// cannot import something the level was never balanced for. Buddy's own Masked type is
        /// always excluded.
        /// </summary>
        private static EnemyType PickLocalEnemyType(bool indoors)
        {
            try
            {
                SelectableLevel level = RoundManager.Instance != null
                    ? RoundManager.Instance.currentLevel
                    : StartOfRound.Instance?.currentLevel;
                if (level == null) return null;

                var candidates = new List<EnemyType>();
                Collect(candidates, indoors ? level.Enemies : level.OutsideEnemies);
                if (candidates.Count == 0) Collect(candidates, indoors ? level.OutsideEnemies : level.Enemies);
                if (candidates.Count == 0) return null;

                int index = Mathf.Abs(_roundSeed + _huntsThisRound * 7919) % candidates.Count;
                return candidates[index];
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("Final-stage entity pick: " + ex.Message);
                return null;
            }
        }

        private static void Collect(List<EnemyType> into, List<SpawnableEnemyWithRarity> list)
        {
            if (into == null || list == null) return;
            foreach (SpawnableEnemyWithRarity entry in list)
            {
                EnemyType type = entry?.enemyType;
                if (type == null || type.enemyPrefab == null) continue;
                // Never spawn another Masked: it would be mistaken for Buddy and would also
                // collide with the crewmate identification handshake.
                if (type.enemyPrefab.GetComponent<MaskedPlayerEnemy>() != null) continue;
                if (!into.Contains(type)) into.Add(type);
            }
        }

        /// <summary>
        /// Picks a NavMesh point in the permitted distance band around the target, preferring one
        /// that is behind them so the arrival is unsettling rather than an instant ambush.
        /// </summary>
        private static bool TryFindSpawnPoint(PlayerControllerB target, out Vector3 position)
        {
            position = Vector3.zero;
            Vector3 centre = target.transform.position;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                float angle = attempt * 30f * Mathf.Deg2Rad;
                float radius = Mathf.Lerp(
                    BuddyMalicePolicy.MinSpawnDistance + 2f,
                    BuddyMalicePolicy.MaxSpawnDistance - 2f,
                    (attempt % 4) / 3f);
                Vector3 candidate = centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;

                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 6f, NavMesh.AllAreas)) continue;
                if (float.IsNaN(hit.position.x) || float.IsInfinity(hit.position.x)) continue;

                float distance = Vector3.Distance(centre, hit.position);
                if (!BuddyMalicePolicy.IsValidSpawnDistance(distance)) continue;

                Vector3 toSpawn = hit.position - centre;
                toSpawn.y = 0f;
                bool behind = toSpawn.sqrMagnitude > 0.05f &&
                              Vector3.Dot(target.transform.forward, toSpawn.normalized) < 0f;
                position = hit.position;
                if (behind) return true;
            }
            return position != Vector3.zero;
        }

        internal static void ResetSession()
        {
            _nextPollAt = 0f;
            _lastHuntAt = -9999f;
            _landedAt = -1f;
            _huntsThisRound = 0;
            _roundSeed = 0;
            _roundSeedKnown = false;
        }
    }
}
