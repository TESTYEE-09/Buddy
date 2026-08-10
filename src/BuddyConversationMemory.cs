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
        // This block is re-sent, uncached, on every single turn. At 40 exchanges and an 18,000
        // character ceiling it was by far the largest thing in a request - and almost entirely
        // redundant, because the Realtime session already carries the conversation as its own
        // items and truncates them on its own policy. What this needs to cover is the gap that
        // policy cannot: a dropped socket, where the session restarts with no history at all.
        // A short recent window does that for a fraction of the cost.
        private const int MaxExchanges = 8;
        private const int MaxPromptChars = 1600;
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
            string name = Plugin.CrewmateName?.Value ?? "Buddy";
            var sb = new StringBuilder(Math.Min(MaxPromptChars, Exchanges.Count * 260));
            sb.AppendLine("Earlier in this shift (oldest first, and no longer true of right now):");
            foreach (Exchange exchange in Exchanges)
            {
                sb.Append(exchange.Speaker).Append(": ").AppendLine(exchange.Input);
                sb.Append(name).Append(": ").AppendLine(exchange.Reply);
                if (sb.Length > MaxPromptChars)
                {
                    string tail = sb.ToString(sb.Length - MaxPromptChars, MaxPromptChars);
                    return "Earlier in this shift (start trimmed):\n" + tail;
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
