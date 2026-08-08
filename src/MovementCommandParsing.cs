using System;
using System.Text.RegularExpressions;

namespace LethalAICrewmate
{
    internal enum MovementCommandKind
    {
        None,
        Follow,
        Stay,
        ReturnToShip,
        FetchScrap,
        ScoutAhead
    }

    internal readonly struct MovementCommand
    {
        internal MovementCommand(MovementCommandKind kind, float scoutDistance = 0f)
        {
            Kind = kind;
            ScoutDistance = scoutDistance;
        }

        internal MovementCommandKind Kind { get; }
        internal float ScoutDistance { get; }
    }

    internal static class MovementCommandParsing
    {
        internal const float DefaultScoutDistance = 10f;
        internal const float MinScoutDistance = 4f;
        internal const float MaxScoutDistance = 18f;

        internal static MovementCommand Parse(string value)
        {
            string lower = Normalize(value);
            if (string.IsNullOrEmpty(lower)) return default;

            if (lower == "ship" || lower == "home" ||
                ContainsAny(lower, "go to ship", "go to the ship", "return to ship", "back to ship", "go home", "head home"))
                return new MovementCommand(MovementCommandKind.ReturnToShip);

            if (lower == "fetch" || lower == "loot" || lower == "scrap" ||
                ContainsAny(lower, "fetch scrap", "collect scrap", "get scrap", "grab scrap", "find scrap", "bring scrap", "fetch loot", "collect loot"))
                return new MovementCommand(MovementCommandKind.FetchScrap);

            if (lower == "stay" || lower == "stay here" || lower == "wait" || lower == "wait here" ||
                lower == "stop" || lower == "stop moving" || lower == "stop there" || lower == "wait there" || lower == "hold" || lower == "hold here" || lower == "hold position" ||
                ContainsAny(lower, "stop following", "stop follow", "dont follow", "do not follow"))
                return new MovementCommand(MovementCommandKind.Stay);

            if (ContainsAny(lower, "go forward", "go forwards", "move forward", "walk forward", "go ahead", "go on ahead", "move ahead", "move up",
                    "check ahead", "check in front", "check whats ahead", "check what is ahead", "scout ahead", "scout forward", "get in front", "lead the way", "take point"))
                return new MovementCommand(MovementCommandKind.ScoutAhead, ParseScoutDistance(lower));

            if (lower == "follow" || lower == "follow me" || lower == "come" || lower == "come here" ||
                lower == "come on" || lower == "here" || lower == "on me" || lower == "with me" ||
                lower.StartsWith("follow me ", StringComparison.Ordinal) || lower.StartsWith("come here ", StringComparison.Ordinal) ||
                lower.StartsWith("can you follow me", StringComparison.Ordinal) || lower.StartsWith("could you follow me", StringComparison.Ordinal) ||
                lower.StartsWith("please follow me", StringComparison.Ordinal) || ContainsAny(lower, "follow us", "come with us", "come with me"))
                return new MovementCommand(MovementCommandKind.Follow);

            return default;
        }

        internal static bool IsDirectDirective(string value)
        {
            string lower = Normalize(value);
            return lower.StartsWith("follow", StringComparison.Ordinal) || lower.StartsWith("come", StringComparison.Ordinal) ||
                   lower.StartsWith("stay", StringComparison.Ordinal) || lower.StartsWith("wait", StringComparison.Ordinal) ||
                   lower.StartsWith("stop", StringComparison.Ordinal) || lower.StartsWith("hold", StringComparison.Ordinal) ||
                   lower.StartsWith("go ", StringComparison.Ordinal) || lower.StartsWith("move ", StringComparison.Ordinal) ||
                   lower.StartsWith("check ", StringComparison.Ordinal) || lower.StartsWith("scout ", StringComparison.Ordinal) ||
                   lower.StartsWith("lead ", StringComparison.Ordinal) || lower.StartsWith("take point", StringComparison.Ordinal) ||
                   lower.StartsWith("fetch ", StringComparison.Ordinal) || lower.StartsWith("collect ", StringComparison.Ordinal) ||
                   lower.StartsWith("get scrap", StringComparison.Ordinal) || lower.StartsWith("grab scrap", StringComparison.Ordinal) ||
                   lower.StartsWith("return ", StringComparison.Ordinal) || lower == "here" || lower == "on me";
        }

        private static float ParseScoutDistance(string lower)
        {
            var match = Regex.Match(lower, @"\b(\d{1,2})(?:\s*(?:m|metre|metres|meter|meters))?\b", RegexOptions.IgnoreCase);
            if (!match.Success || !float.TryParse(match.Groups[1].Value, out float distance))
                return DefaultScoutDistance;
            return Math.Max(MinScoutDistance, Math.Min(MaxScoutDistance, distance));
        }

        private static string Normalize(string value)
        {
            string lower = value?.Trim().ToLowerInvariant() ?? "";
            lower = lower.TrimEnd('.', '!', '?');
            lower = lower.Replace("don't", "dont");
            return Regex.Replace(lower, @"\s+", " ");
        }

        private static bool ContainsAny(string value, params string[] phrases)
        {
            foreach (string phrase in phrases)
                if (value.Contains(phrase)) return true;
            return false;
        }
    }
}
