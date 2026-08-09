using System;

namespace LethalAICrewmate
{
    /// <summary>
    /// Pure gate for the final arc stage, where Buddy stops merely being unsettling and starts
    /// arranging for something to find the crew.
    ///
    /// This is a horror feature for the host's own campaign, so it is deliberately hard to reach
    /// and hard to abuse: it needs the slow burn enabled, its own explicit opt-in, the final
    /// stage, and it still obeys strict per-round and per-interval caps. Nothing outside the
    /// host's own director can request a hunt — no chat command, no model tool call and no
    /// network message reaches this policy.
    /// </summary>
    internal static class BuddyMalicePolicy
    {
        internal const int MaxHuntsPerRound = 2;
        internal const float MinSecondsBetweenHunts = 420f;
        internal const float MinSecondsAfterLanding = 180f;

        /// <summary>Never materialise on top of anyone, and never so far away it is pointless.</summary>
        internal const float MinSpawnDistance = 16f;
        internal const float MaxSpawnDistance = 30f;

        internal static bool StageAllowsHunting(BuddyArcStage stage) => stage == BuddyArcStage.Feral;

        internal static bool CanHunt(
            BuddyArcStage stage,
            bool slowBurnEnabled,
            bool hostileSpawnsOptIn,
            bool landedAndPlayable,
            int livingPlayers,
            int huntsThisRound,
            float secondsSinceLanding,
            float secondsSinceLastHunt)
        {
            if (!slowBurnEnabled || !hostileSpawnsOptIn) return false;
            if (!StageAllowsHunting(stage)) return false;
            if (!landedAndPlayable) return false;
            if (livingPlayers < 1) return false;
            if (huntsThisRound >= MaxHuntsPerRound) return false;
            if (secondsSinceLanding < MinSecondsAfterLanding) return false;
            if (secondsSinceLastHunt < MinSecondsBetweenHunts) return false;
            return true;
        }

        /// <summary>
        /// A candidate target must be a live player who is actually out working. Players sitting
        /// in the ship are left alone so the feature can never camp a safe respawn point.
        /// </summary>
        internal static bool IsValidTarget(bool alive, bool inShip, float distanceFromBuddy) =>
            alive && !inShip && distanceFromBuddy <= 60f;

        internal static bool IsValidSpawnDistance(float distance) =>
            distance >= MinSpawnDistance && distance <= MaxSpawnDistance;
    }
}
