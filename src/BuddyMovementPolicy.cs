namespace LethalAICrewmate
{
    /// <summary>Pure movement thresholds shared by runtime code and release checks.</summary>
    internal static class BuddyMovementPolicy
    {
        internal const float FollowStopDistance = 4.0f;
        internal const float FollowResumeDistance = 5.8f;
        internal const float EmergencySeparation = 70f;
        internal const float AreaRecoveryDelay = 20f;
        internal const float PathRebuildDelay = 3.5f;
        internal const float EmergencyStallDelay = 20f;
        internal const int RebuildsBeforeEmergency = 3;

        internal static float FollowSpeed(float distance)
        {
            if (distance >= 28f) return 6.2f;
            if (distance >= 14f) return 5.4f;
            return 4.35f;
        }

        internal static bool ShouldEmergencyRecover(float stalledSeconds, int rebuilds, float separation, float areaMismatchSeconds)
        {
            if (stalledSeconds < EmergencyStallDelay || rebuilds < RebuildsBeforeEmergency) return false;
            return separation >= EmergencySeparation || areaMismatchSeconds >= AreaRecoveryDelay;
        }

        internal static float DeathReactionDelay(ulong networkObjectId) => 8f + (networkObjectId % 5u);

        internal static bool CouldWitnessDeath(float distance, bool sameArea, bool hasLineOfSight) =>
            sameArea && hasLineOfSight && distance <= 20f;
    }
}
