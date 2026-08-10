using System;
using System.Text;

namespace LethalAICrewmate
{
    internal static class PromptSafety
    {
        internal static string SanitizePlayerName(string value)
        {
            string clean = SanitizeSingleLine(value, 32);
            return string.IsNullOrWhiteSpace(clean) ? "Player" : clean;
        }

        internal static string SanitizeSingleLine(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || maxChars <= 0) return "";
            var sb = new StringBuilder(Math.Min(value.Length, maxChars));
            foreach (char ch in value)
            {
                if (sb.Length >= maxChars) break;
                if (char.IsControl(ch)) { sb.Append(' '); continue; }
                if (ch == '<') { sb.Append('‹'); continue; }
                if (ch == '>') { sb.Append('›'); continue; }
                sb.Append(ch);
            }
            return sb.ToString().Trim();
        }

        internal static string SanitizeChatText(string value) => SanitizeSingleLine(value, 512);

        internal static string SanitizeItemName(string value)
        {
            string clean = SanitizeSingleLine(value, 40);
            return string.IsNullOrWhiteSpace(clean) ? "scrap" : clean;
        }
    }
}
