using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LethalAICrewmate
{
    /// <summary>Exact-first, ambiguity-safe matching for model-supplied game names.</summary>
    internal static class DeterministicNameMatchPolicy
    {
        internal const int Missing = -1;
        internal const int Ambiguous = -2;

        internal static int Resolve(string query, IReadOnlyList<string> candidateNames)
        {
            string wanted = Normalize(query);
            if (string.IsNullOrEmpty(wanted) || candidateNames == null) return Missing;

            int exact = Missing;
            int partial = Missing;
            for (int i = 0; i < candidateNames.Count; i++)
            {
                string candidate = Normalize(candidateNames[i]);
                if (string.IsNullOrEmpty(candidate)) continue;
                if (candidate == wanted)
                {
                    exact = exact == Missing ? i : Ambiguous;
                    continue;
                }
                if (candidate.Length < 3 || wanted.Length < 3 ||
                    (!candidate.Contains(wanted) && !wanted.Contains(candidate))) continue;
                partial = partial == Missing ? i : Ambiguous;
            }
            return exact != Missing ? exact : partial;
        }

        internal static string Normalize(string value)
        {
            string clean = (value ?? "").ToLowerInvariant().Trim();
            clean = Regex.Replace(clean, @"^(?:an?|the|some)\s+", "", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\b(?:item|object|thing)\b", "", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"[^a-z0-9]+", "");
            if (clean.Length > 3 && clean.EndsWith("s", StringComparison.Ordinal))
                clean = clean.Substring(0, clean.Length - 1);
            return clean;
        }
    }
}
