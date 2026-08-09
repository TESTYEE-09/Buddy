namespace LethalAICrewmate
{
    /// <summary>Pure, conservative choices for ordinary crewmate routines.</summary>
    internal static class BuddyCrewmateRoutinePolicy
    {
        internal const float HandoffDistance = 3.4f;
        internal const float DoorWaitSeconds = 1.6f;
        internal const float DoorRetrySeconds = 8f;

        internal static float ScrapScore(int value, float distance)
        {
            float safeDistance = distance < 0f ? 0f : distance;
            int safeValue = value < 0 ? 0 : value;
            return safeValue * 1.25f - safeDistance * 2.0f;
        }

        internal static bool ShouldWaitAtDoor(float ownerDoorDistance) => ownerDoorDistance <= 5.5f;
    }
}
