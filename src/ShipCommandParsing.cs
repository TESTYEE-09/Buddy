using System;
using System.Text.RegularExpressions;

namespace LethalAICrewmate
{
    internal static class ShipCommandParsing
    {
        internal static bool IsStatusRequest(string value)
        {
            string lower = value?.Trim().ToLowerInvariant() ?? "";
            if (string.IsNullOrEmpty(lower)) return false;
            return lower == "status" || lower == "ship status" || lower == "report" ||
                   lower.Contains("what time") || lower.Contains("current time") || lower == "time" ||
                   lower.Contains("how late") || lower.Contains("credits") || lower.Contains("credit balance") ||
                   lower.Contains("quota") || lower.Contains("deadline") || lower.Contains("days left") ||
                   lower.Contains("weather") || lower.Contains("ship scrap") || lower.Contains("scrap value") ||
                   lower.Contains("crew status") || lower.Contains("how many alive") ||
                   lower.Contains("where are we") || lower.Contains("current moon");
        }

        internal static void ParsePurchase(string value, out string item, out int quantity)
        {
            item = value?.Trim().ToLowerInvariant() ?? "";
            quantity = 1;
            var leading = Regex.Match(item, @"^(\d{1,2})\s+(.+)$");
            if (leading.Success)
            {
                int.TryParse(leading.Groups[1].Value, out quantity);
                item = leading.Groups[2].Value.Trim();
            }
            else
            {
                var trailing = Regex.Match(item, @"^(.+?)\s+x(\d{1,2})$", RegexOptions.IgnoreCase);
                if (trailing.Success)
                {
                    item = trailing.Groups[1].Value.Trim();
                    int.TryParse(trailing.Groups[2].Value, out quantity);
                }
            }
            quantity = Math.Max(1, Math.Min(12, quantity));
        }

        internal static bool TryParseFacilityAction(string value, out string code, out bool enable)
        {
            string lower = value?.Trim().ToLowerInvariant() ?? "";
            code = null;
            enable = false;
            if (string.IsNullOrEmpty(lower)) return false;
            var match = Regex.Match(lower, @"\b([a-z]\d)\b", RegexOptions.IgnoreCase);
            if (!match.Success) return false;

            bool turnOff = lower.Contains("disable") || lower.Contains("deactivate") || lower.Contains("turn off") ||
                           lower.Contains("close") || lower.Contains("shut");
            bool turnOn = lower.Contains("enable") || lower.Contains("activate") || lower.Contains("turn on") ||
                          lower.Contains("open");
            if (!turnOff && !turnOn && !lower.StartsWith("terminal ") && !lower.StartsWith("code "))
                return false;

            code = match.Groups[1].Value;
            enable = turnOn && !turnOff;
            return true;
        }
    }
}
