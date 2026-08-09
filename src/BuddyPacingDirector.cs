using System;
using GameNetcodeStuff;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Host-only pacing director. It reads confirmed live state, scores tension, and coordinates
    /// silence, positioning, staged watching beats and dialogue density through one plan so the
    /// horror lands as a rhythm instead of unrelated tics.
    ///
    /// Presentation only. It never spawns anything, never grants authority, never suppresses a
    /// danger callout, and never touches the remote-action trust boundary.
    /// </summary>
    internal static class BuddyPacingDirector
    {
        private static float _nextPollAt;
        private static float _watchUntil;
        private static float _nextWatchAllowedAt;
        private static Vector3 _watchTarget;
        private static BuddyPacingPlan _plan = new BuddyPacingPlan
        {
            ExtraSilenceSeconds = 0f,
            FollowDistanceScale = 1f,
            DialogueDensity = 2,
            Presence = BuddyPresence.Normal
        };

        internal static int CurrentTension { get; private set; }
        internal static BuddyPacingPlan Plan => _plan;

        private static bool Active =>
            CrewmateSpawner.IsHost() &&
            Plugin.SlowBurnHorror?.Value == true &&
            Plugin.DynamicPacing?.Value == true;

        /// <summary>Extra quiet the autonomy layer must respect before starting unprompted chatter.</summary>
        internal static float ExtraSilenceSeconds => Active ? Math.Max(0f, _plan.ExtraSilenceSeconds) : 0f;

        /// <summary>True while unprompted small talk should be dropped entirely.</summary>
        internal static bool SuppressSmallTalk => Active && _plan.DialogueDensity <= 0;

        internal static string PromptDirective() => Active ? BuddyPacingPolicy.PromptDirective(_plan) : null;

        internal static float FollowSpacing(float baseSpacing)
        {
            if (!Active) return baseSpacing;
            float scale = Mathf.Clamp(_plan.FollowDistanceScale, 0.55f, 1f);
            return Mathf.Max(1.4f, baseSpacing * scale);
        }

        internal static void Tick()
        {
            try
            {
                if (!Active)
                {
                    if (CurrentTension != 0 || _plan.Presence != BuddyPresence.Normal) Reset();
                    return;
                }
                if (Time.unscaledTime < _nextPollAt) return;
                _nextPollAt = Time.unscaledTime + 1f;

                CrewmateData data = CrewmateRegistry.GetPrimary();
                if (data?.Enemy == null) return;

                CurrentTension = MeasureTension(data);
                _plan = BuddyPacingPolicy.Plan(
                    BuddyCharacterDirector.CurrentStage,
                    CurrentTension,
                    Mathf.Max(0f, Time.unscaledTime - LlmClient.LastBuddyLineAt),
                    Mathf.Max(0f, Time.unscaledTime - LlmClient.LastPlayerInteractionAt));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("Buddy pacing director: " + ex.Message);
            }
        }

        private static int MeasureTension(CrewmateData data)
        {
            Vector3 origin = data.Enemy.transform.position;
            int hostiles = 0;
            try
            {
                foreach (EnemyAI enemy in UnityEngine.Object.FindObjectsOfType<EnemyAI>())
                {
                    if (enemy == null || enemy.isEnemyDead) continue;
                    if (CrewmateRegistry.IsCrewmate(enemy)) continue;
                    if (Vector3.Distance(origin, enemy.transform.position) <= 22f) hostiles++;
                }
            }
            catch { /* ignore */ }

            bool inside = false;
            float lowestHealth = 1f;
            bool separated = false;
            try
            {
                PlayerControllerB owner = data.Owner;
                if (owner != null)
                {
                    inside = owner.isInsideFactory;
                    separated = Vector3.Distance(origin, owner.transform.position) >= 30f;
                }
                PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
                if (players != null)
                {
                    foreach (PlayerControllerB player in players)
                    {
                        if (player == null || !player.isPlayerControlled || player.isPlayerDead) continue;
                        if (Vector3.Distance(origin, player.transform.position) > 25f) continue;
                        lowestHealth = Mathf.Min(lowestHealth, Mathf.Clamp01(player.health / 100f));
                    }
                }
            }
            catch { /* ignore */ }

            bool late = false;
            int daysLeft = 3;
            try
            {
                if (TimeOfDay.Instance != null)
                {
                    late = TimeOfDay.Instance.hour >= 12;
                    daysLeft = Mathf.Max(0, TimeOfDay.Instance.daysUntilDeadline);
                }
            }
            catch { /* ignore */ }

            return BuddyPacingPolicy.Tension(hostiles, inside, late, lowestHealth, separated, daysLeft);
        }

        /// <summary>
        /// Staged "holds still and looks at you" beat. Returns true when the follow tick should
        /// stop for this frame. Bounded in length and separated by a long cooldown.
        /// </summary>
        internal static bool TryHoldAndWatch(CrewmateData data, PlayerControllerB target, float distance)
        {
            try
            {
                if (!Active || data?.Enemy == null || target == null) return false;
                float now = Time.unscaledTime;

                if (now < _watchUntil)
                {
                    FaceWatchTarget(data);
                    return true;
                }

                if (_plan.Presence != BuddyPresence.Watching) return false;
                if (distance < 5f || distance > 16f) return false;
                if (now < _nextWatchAllowedAt) return false;
                if (CurrentTension >= BuddyPacingPolicy.UneaseTension) return false;

                BuddyArcStage stage = BuddyCharacterDirector.CurrentStage;
                _watchUntil = now + BuddyPacingPolicy.WatchSeconds(stage);
                _nextWatchAllowedAt = _watchUntil + BuddyPacingPolicy.WatchCooldownSeconds(stage);
                _watchTarget = target.transform.position;
                FaceWatchTarget(data);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void FaceWatchTarget(CrewmateData data)
        {
            try
            {
                MaskedPlayerEnemy enemy = data.Enemy;
                if (enemy == null) return;
                if (enemy.agent != null && enemy.agent.isOnNavMesh)
                {
                    enemy.agent.isStopped = true;
                    enemy.agent.ResetPath();
                }
                enemy.moveTowardsDestination = false;
                enemy.movingTowardsTargetPlayer = false;

                Vector3 look = _watchTarget - enemy.transform.position;
                look.y = 0f;
                if (look.sqrMagnitude > 0.05f)
                    enemy.transform.rotation = Quaternion.Slerp(
                        enemy.transform.rotation, Quaternion.LookRotation(look.normalized), Time.deltaTime * 2.2f);
            }
            catch { /* presentation only */ }
        }

        internal static void ResetSession() => Reset();

        private static void Reset()
        {
            CurrentTension = 0;
            _nextPollAt = 0f;
            _watchUntil = 0f;
            _nextWatchAllowedAt = 0f;
            _watchTarget = Vector3.zero;
            _plan = new BuddyPacingPlan
            {
                ExtraSilenceSeconds = 0f,
                FollowDistanceScale = 1f,
                DialogueDensity = 2,
                Presence = BuddyPresence.Normal
            };
        }
    }
}
