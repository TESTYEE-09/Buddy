namespace LethalAICrewmate
{
    /// <summary>
    /// Explicit corrections for stable Realtime speech spellings of vanilla moon names.
    /// Edit-distance matching is intentionally avoided so a fuzzy resolver cannot choose the
    /// wrong paid moon merely because two names sound or look similar.
    /// </summary>
    internal static class MoonSpeechAliasPolicy
    {
        internal static string Resolve(string query)
        {
            switch (DeterministicNameMatchPolicy.Normalize(query))
            {
                case "assurence":
                    return "Assurance";
                default:
                    return query;
            }
        }
    }
}
