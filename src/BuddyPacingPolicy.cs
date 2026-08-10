using System;

namespace LethalAICrewmate
{
    /// <summary>What Buddy is doing with his body while the pacing plan is active.</summary>
    internal enum BuddyPresence
    {
        /// <summary>Ordinary follow behaviour; the director asks for nothing.</summary>
        Normal,
        /// <summary>Stays noticeably closer than usual without blocking or touching.</summary>
        Close,
        /// <summary>Holds still and faces the crewmate for a short beat.</summary>
        Watching
    }

    internal struct BuddyPacingPlan
    {
        /// <summary>Extra quiet enforced on top of the normal autonomy cooldown, in seconds.</summary>
        internal float ExtraSilenceSeconds;
        /// <summary>Multiplier applied to Buddy's usual follow distance. 1 = unchanged.</summary>
        internal float FollowDistanceScale;
        /// <summary>0 = say nothing unprompted, 3 = ordinary chatty coworker.</summary>
        internal int DialogueDensity;
        internal BuddyPresence Presence;
    }

    /// <summary>
    /// Pure pacing policy. It decides how loud, how close and how still Buddy is, so silence,
    /// staring, positioning and dialogue move together instead of firing independently.
    /// It never grants authority, never changes safety and never triggers a game action.
    /// </summary>
    internal static class BuddyPacingPolicy
    {
        internal const int MaxTension = 100;

        /// <summary>Below this, a beat is not worth spending; above it, danger owns the moment.</summary>
        internal const int DangerTension = 65;
        internal const int UneaseTension = 35;

        internal static int ClampTension(int tension) => tension < 0 ? 0 : tension > MaxTension ? MaxTension : tension;

        /// <summary>
        /// Builds a tension score from signals the host can actually confirm. Every argument is a
        /// real measurement; nothing here is randomised or invented.
        /// </summary>
        internal static int Tension(
            int confirmedHostilesNearby,
            bool insideFacility,
            bool nightOrLate,
            float lowestNearbyHealthFraction,
            bool crewSeparated,
            int daysUntilDeadline)
        {
            int score = 0;
            score += Math.Min(3, Math.Max(0, confirmedHostilesNearby)) * 20;
            if (insideFacility) score += 10;
            if (nightOrLate) score += 10;
            if (lowestNearbyHealthFraction < 0.5f) score += 15;
            if (lowestNearbyHealthFraction < 0.25f) score += 10;
            if (crewSeparated) score += 12;
            if (daysUntilDeadline <= 0) score += 8;
            return ClampTension(score);
        }

        internal static BuddyPacingPlan Plan(
            BuddyArcStage stage,
            int tension,
            float secondsSinceLastLine,
            float secondsSincePlayerSpoke)
        {
            tension = ClampTension(tension);
            var plan = new BuddyPacingPlan
            {
                ExtraSilenceSeconds = 0f,
                FollowDistanceScale = 1f,
                DialogueDensity = 2,
                Presence = BuddyPresence.Normal
            };

            // Confirmed danger always outranks the horror director. Deterministic callouts own
            // this moment, so Buddy simply gets terse and stops any staged behaviour.
            if (tension >= DangerTension)
            {
                plan.DialogueDensity = 1;
                plan.ExtraSilenceSeconds = 0f;
                plan.Presence = BuddyPresence.Normal;
                return plan;
            }

            if (stage == BuddyArcStage.Coworker)
            {
                plan.DialogueDensity = tension >= UneaseTension ? 2 : 3;
                return plan;
            }

            // Rising unease: quieter and closer as the arc advances, and quieter still while the
            // crew is already talking. Staring is reserved for genuinely calm, long silences.
            int stageWeight = stage == BuddyArcStage.OffNote ? 1
                            : stage == BuddyArcStage.Unsettling ? 2
                            : stage == BuddyArcStage.Cold ? 3 : 4;

            plan.ExtraSilenceSeconds = stageWeight * 12f;
            plan.DialogueDensity = Math.Max(0, 3 - stageWeight);
            plan.FollowDistanceScale = 1f - 0.1f * stageWeight;

            bool calm = tension < UneaseTension;
            bool longQuiet = secondsSinceLastLine >= 60f && secondsSincePlayerSpoke >= 25f;
            if (calm && longQuiet && stageWeight >= 2)
                plan.Presence = BuddyPresence.Watching;
            else if (stageWeight >= 2)
                plan.Presence = BuddyPresence.Close;

            return plan;
        }

        /// <summary>How long a single watching beat may last before Buddy returns to normal work.</summary>
        internal static float WatchSeconds(BuddyArcStage stage) =>
            stage == BuddyArcStage.Feral ? 4.5f : stage == BuddyArcStage.Cold ? 3.5f : 2.5f;

        /// <summary>Minimum gap between staged watching beats so the trick never becomes a tic.</summary>
        internal static float WatchCooldownSeconds(BuddyArcStage stage) =>
            stage == BuddyArcStage.Feral ? 100f : stage == BuddyArcStage.Cold ? 150f : 240f;

        internal static string PromptDirective(BuddyPacingPlan plan)
        {
            if (plan.DialogueDensity <= 0)
                return "Pace: hold back. Nothing unless you are asked or it really matters. One short line at most.";
            if (plan.DialogueDensity == 1)
                return "Pace: terse. Answer, add nothing.";
            if (plan.DialogueDensity >= 3)
                return "Pace: normal shift talk is fine when there is a reason for it.";
            return "Pace: economical. Volunteer only what helps.";
        }
    }
}
