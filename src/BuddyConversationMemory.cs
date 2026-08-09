using System;
using System.Collections.Generic;
using System.Text;

namespace LethalAICrewmate
{
    /// <summary>
    /// In-memory lobby conversation continuity shared by both providers. It is deliberately never
    /// written to disk and survives moon landings, but is cleared when the network session ends.
    /// </summary>
    internal static class BuddyConversationMemory
    {
        private const int MaxExchanges = 40;
        private const int MaxPromptChars = 18000;
        private const int MaxTurnChars = 700;
        private static readonly Queue<Exchange> Exchanges = new Queue<Exchange>();

        private struct Exchange
        {
            internal string Speaker;
            internal string Input;
            internal string Reply;
        }

        internal static void Remember(string speaker, string input, string reply)
        {
            input = Clean(input, MaxTurnChars);
            reply = Clean(reply, MaxTurnChars);
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(reply)) return;
            Exchanges.Enqueue(new Exchange
            {
                Speaker = PromptSafety.SanitizePlayerName(speaker),
                Input = input,
                Reply = reply
            });
            while (Exchanges.Count > MaxExchanges) Exchanges.Dequeue();
        }

        internal static string PromptContext()
        {
            if (Exchanges.Count == 0) return null;
            var sb = new StringBuilder(Math.Min(MaxPromptChars, Exchanges.Count * 180));
            sb.AppendLine("EARLIER CREWMATE DIALOGUE (oldest to newest; not current sensor truth)");
            sb.AppendLine("Use this only to resolve references and remember what players care about. Do not copy old Buddy answers.");
            foreach (Exchange exchange in Exchanges)
            {
                sb.Append(exchange.Speaker).Append(": ").AppendLine(exchange.Input);
                if (sb.Length > MaxPromptChars)
                {
                    string tail = sb.ToString(sb.Length - MaxPromptChars, MaxPromptChars);
                    return "EARLIER CREWMATE DIALOGUE (older entries trimmed)\n" + tail;
                }
            }
            return sb.ToString();
        }

        internal static void ResetSession() => Exchanges.Clear();

        private static string Clean(string value, int max)
        {
            string clean = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
            return clean.Length <= max ? clean : clean.Substring(0, max).TrimEnd() + "...";
        }
    }
}
