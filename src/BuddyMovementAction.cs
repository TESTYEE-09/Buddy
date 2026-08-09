namespace LethalAICrewmate
{
    internal enum BuddyMovementActionKind
    {
        None,
        Follow,
        Stay,
        ReturnToShip,
        FetchScrap,
        ScoutAhead
    }

    internal readonly struct BuddyMovementAction
    {
        internal BuddyMovementAction(BuddyMovementActionKind kind, float scoutDistance = 0f, bool deliverToRequester = false)
        {
            Kind = kind;
            ScoutDistance = scoutDistance;
            DeliverToRequester = deliverToRequester;
        }

        internal BuddyMovementActionKind Kind { get; }
        internal float ScoutDistance { get; }
        internal bool DeliverToRequester { get; }
    }
}
