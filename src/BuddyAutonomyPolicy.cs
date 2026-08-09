namespace LethalAICrewmate
{
    internal enum BuddyContextEvent
    {
        EnteredFacility,
        LeftFacility,
        ReturnedToShip,
        LongTravel,
        QuietDowntime,
        Separated,
        ValuableScrap,
        WitnessedDeathReport,
        HazardNearby,
        WeatherTurn,
        UnusualEnemy
    }

    internal static class BuddyAutonomyPolicy
    {
        internal const float GlobalCooldown = 55f;
        internal const float PlayerPriorityWindow = 12f;

        internal static int Importance(BuddyContextEvent kind)
        {
            if (kind == BuddyContextEvent.WitnessedDeathReport) return 100;
            if (kind == BuddyContextEvent.UnusualEnemy) return 85;
            if (kind == BuddyContextEvent.HazardNearby) return 75;
            if (kind == BuddyContextEvent.Separated) return 70;
            if (kind == BuddyContextEvent.WeatherTurn) return 50;
            if (kind == BuddyContextEvent.EnteredFacility || kind == BuddyContextEvent.LeftFacility) return 55;
            if (kind == BuddyContextEvent.ReturnedToShip || kind == BuddyContextEvent.ValuableScrap) return 45;
            return 20;
        }

        internal static float RepeatCooldown(BuddyContextEvent kind)
        {
            if (kind == BuddyContextEvent.WitnessedDeathReport) return 30f;
            if (kind == BuddyContextEvent.UnusualEnemy) return 90f;
            if (kind == BuddyContextEvent.Separated) return 120f;
            // Environmental colour is the easiest way to make Buddy feel noisy, so it stays rare.
            if (kind == BuddyContextEvent.HazardNearby) return 150f;
            if (kind == BuddyContextEvent.WeatherTurn) return 300f;
            return 180f;
        }

        internal static bool CanSpeak(float now, float lastSpokeAt, float lastPlayerAt, float lastSameEventAt, BuddyContextEvent kind)
        {
            if (now - lastSameEventAt < RepeatCooldown(kind)) return false;
            if (kind != BuddyContextEvent.WitnessedDeathReport && now - lastPlayerAt < PlayerPriorityWindow) return false;
            float requiredGlobal = kind == BuddyContextEvent.WitnessedDeathReport ? 10f : GlobalCooldown;
            return now - lastSpokeAt >= requiredGlobal;
        }
    }
}
